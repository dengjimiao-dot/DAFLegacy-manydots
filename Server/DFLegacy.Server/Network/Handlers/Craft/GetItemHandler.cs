using System.Buffers.Binary;
using System.Text;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// GET_ITEM (CMD 46): world-drop pickup (07 §5 group 11).
/// </summary>
public sealed class GetItemHandler(JsonGameStore store,
    ItemCatalog itemCatalog,
    CharacterStatCatalog characterStatCatalog,
    ILogger<GetItemHandler> logger,
    CharacterPacketBuilders builders,
    ChannelSessionSenders senders) : IGameCommandHandler
{
    private static readonly Encoding ClientEncoding = PvfEncodings.Cp936Lossy();

    public ushort ProtocolId => GameProtocolEngine.GetItemCommand;

    public string Name => "GetItem";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        if (request.Body.Length < 4
            || session.Dungeon.CurrentDungeonId == 0
            || session.Character.Active is null)
        {
            return;
        }

        var groundId = BinaryPrimitives.ReadUInt16LittleEndian(
            request.Body.AsSpan(2, sizeof(ushort)));
        if (!session.Dungeon.GroundItems.TryGet(
                groundId,
                session.Dungeon.RoomX,
                session.Dungeon.RoomY,
                out var groundItem)
            || groundItem.OwnerUserId != session.Character.LocalUserId)
        {
            logger.LogWarning(
                "Ignored invalid or duplicate pickup of ground item {GroundId} for user {UserId}.",
                groundId,
                session.Character.LocalUserId);
            return;
        }

        if (groundItem.IsGold)
        {
            int updatedGold;
            try
            {
                updatedGold = checked(
                    session.Currency.Gold + checked((int)groundItem.CountOrValue));
            }
            catch (OverflowException)
            {
                logger.LogWarning(
                    "Gold pickup {GroundId} would overflow the character balance.",
                    groundId);
                return;
            }

            var saved = await store.SaveCharacterInventoryAsync(
                session.Login.AccountName,
                session.Character.Active.Id,
                updatedGold,
                session.Inventory.Main.Values,
                session.Inventory.Equipped.Values,
                cancellationToken);
            if (saved is null)
            {
                logger.LogWarning(
                    "Gold pickup {GroundId} was not persisted; the ground item was retained.",
                    groundId);
                return;
            }

            session.Character.Active = saved;
            session.Currency.Gold = updatedGold;
            session.Dungeon.GroundItems.Remove(groundId);
            var pickup = DungeonPacketBuilders.CreateGoldItemPickup(
                groundId,
                session.Character.LocalUserId,
                groundItem.CountOrValue);
            await context.Outbound.SendAsync(pickup, Name, cancellationToken);

            var refresh = builders.CreateMainInventoryPacket(
                session.Currency.Gold,
                session.Currency.VictoryPoints,
                session.Inventory.Main.Values);
            await context.Outbound.SendAsync(refresh, Name, cancellationToken);
            return;
        }

        if (CharacterWarehouseProgression.TryGetItemTargetCapacity(
                groundItem.ItemId,
                out var groundUpgradeTarget))
        {
            var plannedCapacity = Math.Max(
                session.Inventory.WarehouseCapacity,
                groundUpgradeTarget);
            var savedUpgrade = await store.SaveCharacterInventoryAsync(
                session.Login.AccountName,
                session.Character.Active.Id,
                session.Currency.Gold,
                session.Inventory.Main.Values,
                session.Inventory.Equipped.Values,
                cancellationToken,
                warehouseCapacity: plannedCapacity,
                expectedWarehouseCapacity: session.Inventory.WarehouseCapacity);
            if (savedUpgrade is null)
            {
                logger.LogWarning(
                    "Warehouse-upgrade pickup {GroundId} was not persisted; the ground item was retained.",
                    groundId);
                return;
            }

            _ = senders.AdoptSavedWarehouseUpgradeState(context, savedUpgrade);
            session.Dungeon.GroundItems.Remove(groundId);
            var upgradePickup =
                DungeonPacketBuilders.CreateInventoryItemPickup(
                    groundId,
                    session.Character.LocalUserId,
                    ushort.MaxValue);
            await context.Outbound.SendAsync(upgradePickup, Name, cancellationToken);

            var inventoryRefresh = builders.CreateMainInventoryPacket(
                session.Currency.Gold,
                session.Currency.VictoryPoints,
                session.Inventory.Main.Values);
            await context.Outbound.SendAsync(inventoryRefresh, Name, cancellationToken);
            await senders.SendWarehouseStateAsync(context, cancellationToken);
            return;
        }

        if (!CharacterInventoryPlanner.CanCarryAdditionalItem(
                session.Inventory.Main.Values,
                session.Inventory.Equipped.Values,
                session.Inventory.Creature.Values,
                groundItem.ItemId,
                groundItem.CountOrValue,
                characterStatCatalog.Get(session.Character.Active).InventoryLimit,
                itemCatalog,
                out var carriedWeight,
                out var pickupWeight,
                out var carriedWeightLimit))
        {
            // Keep the authoritative ground record and its ID. The client has
            // already hidden the item for its optimistic pickup animation;
            // CMD 46 error 0x15 rolls that state back and drops it visually.
            // NOTI 39 would falsely confirm pickup, while NOTI 40 would create
            // a second ground object instead of releasing the existing one.
            var overweightReply =
                CraftPacketBuilders.CreateGetItemSilentRejection(request);
            await context.Outbound.SendAsync(overweightReply, Name, cancellationToken);

            var combinedWeight = carriedWeight > ulong.MaxValue - pickupWeight
                ? ulong.MaxValue
                : carriedWeight + pickupWeight;
            var excessWeight = combinedWeight - carriedWeightLimit;
            var overweightMessage = FormattableString.Invariant(
                $"将超出物品栏负重上限 {excessWeight / 1000d:F3}kg.");
            var notice = ChatPacketBuilders.CreateMessageNotification(
                GameMessageType.Type16Brown,
                // A zero target suppresses the local-character prefix.
                0,
                ClientEncoding.GetBytes(overweightMessage));
            await context.Outbound.SendAsync(notice, Name, cancellationToken);
            logger.LogInformation(
                "Ground item {GroundId} pickup rejected as overweight; current={CurrentWeight}, incoming={IncomingWeight}, limit={WeightLimit}, excess={ExcessWeight}; authoritative ground state was retained for the client's pickup rollback.",
                groundId,
                carriedWeight,
                pickupWeight,
                carriedWeightLimit,
                excessWeight);
            return;
        }

        if (itemCatalog.TryGetDefinition(
                groundItem.ItemId,
                out var groundDefinition)
            && groundDefinition.InventoryCategory
                == ItemInventoryCategory.Creature)
        {
            var creatureGroundItem = groundItem.PreservedItem is
                { } preservedCreatureItem
                ? preservedCreatureItem with
                {
                    Slot = ushort.MaxValue,
                    CountOrValue = groundItem.CountOrValue
                }
                : new CharacterItemRecord(
                    ushort.MaxValue,
                    groundItem.ItemId,
                    groundItem.CountOrValue,
                    Durability: groundDefinition.IsEquipment
                        ? (ushort)1_000
                        : (ushort)0,
                    InstanceId: groundItem.PreservedItem?.InstanceId
                        ?? Guid.Empty);
            if (!CharacterCreatureInventoryPlanner.TryPlace(
                    session.Inventory.Creature,
                    creatureGroundItem,
                    itemCatalog,
                    preferredEmptySlot: null,
                    out var creaturePickupPlan,
                    out var creaturePickupFailure))
            {
                logger.LogInformation(
                    "Ground Creature item {GroundId} cannot enter Creature inventory ({Failure}); it remains in the room.",
                    groundId,
                    creaturePickupFailure);
                return;
            }

            var savedCreaturePickup =
                await store.SaveCharacterItemSpacesAsync(
                    session.Login.AccountName,
                    session.Character.Active.Id,
                    session.Currency.Gold,
                    session.Inventory.Main.Values,
                    session.Inventory.Equipped.Values,
                    session.Inventory.Warehouse.Values,
                    cancellationToken,
                    avatarInventory: session.Inventory.Avatar.Values,
                    creatureInventory:
                        creaturePickupPlan.Inventory.Values);
            if (savedCreaturePickup is null)
            {
                logger.LogWarning(
                    "Creature item pickup {GroundId} was not persisted; the ground item was retained.",
                    groundId);
                return;
            }

            session.Character.Active = savedCreaturePickup;
            session.Inventory.Creature = creaturePickupPlan.Inventory;
            session.Dungeon.GroundItems.Remove(groundId);
            var creaturePickup =
                DungeonPacketBuilders.CreateInventoryItemPickup(
                    groundId,
                    session.Character.LocalUserId,
                    creaturePickupPlan.DestinationSlot);
            await context.Outbound.SendAsync(creaturePickup, Name, cancellationToken);

            foreach (var creatureRefresh in
                     builders.CreateCreatureInventoryPackets(
                         session.Inventory.Creature.Values))
            {
                await context.Outbound.SendAsync(creatureRefresh, Name, cancellationToken);
            }

            return;
        }

        if (itemCatalog.TryGetDefinition(
                groundItem.ItemId,
                out groundDefinition)
            && groundDefinition.InventoryCategory
                == ItemInventoryCategory.Avatar)
        {
            var preservedAvatarItem = groundItem.PreservedItem;
            var avatarGroundItem = new CharacterMailAttachmentRecord(
                groundItem.ItemId,
                groundItem.CountOrValue,
                State: preservedAvatarItem?.State ?? 0,
                Durability: preservedAvatarItem?.Durability ?? 1_000,
                SealState: preservedAvatarItem?.SealState ?? 0,
                AvatarRemainingSeconds:
                    preservedAvatarItem?.AvatarRemainingSeconds,
                AvatarAbilityIndex:
                    preservedAvatarItem?.AvatarAbilityIndex,
                InstanceId: preservedAvatarItem?.InstanceId
                    ?? Guid.Empty);
            if (!CharacterAvatarInventoryPlanner.TryPlace(
                    session.Inventory.Avatar,
                    avatarGroundItem,
                    itemCatalog,
                    out var avatarPickupPlan,
                    out var avatarPickupFailure))
            {
                logger.LogInformation(
                    "Ground Avatar item {GroundId} cannot enter Avatar inventory ({Failure}); it remains in the room.",
                    groundId,
                    avatarPickupFailure);
                return;
            }

            var savedAvatarPickup =
                await store.SaveCharacterItemSpacesAsync(
                    session.Login.AccountName,
                    session.Character.Active.Id,
                    session.Currency.Gold,
                    session.Inventory.Main.Values,
                    session.Inventory.Equipped.Values,
                    session.Inventory.Warehouse.Values,
                    cancellationToken,
                    avatarInventory: avatarPickupPlan.Inventory.Values);
            if (savedAvatarPickup is null)
            {
                logger.LogWarning(
                    "Avatar item pickup {GroundId} was not persisted; the ground item was retained.",
                    groundId);
                return;
            }

            session.Character.Active = savedAvatarPickup;
            session.Inventory.Avatar = avatarPickupPlan.Inventory;
            session.Dungeon.GroundItems.Remove(groundId);
            var avatarPickup =
                DungeonPacketBuilders.CreateInventoryItemPickup(
                    groundId,
                    session.Character.LocalUserId,
                    avatarPickupPlan.DestinationSlot);
            await context.Outbound.SendAsync(avatarPickup, Name, cancellationToken);

            var avatarRefresh = builders.CreateAvatarInventoryPacket(
                session.Inventory.Avatar.Values);
            await context.Outbound.SendAsync(avatarRefresh, Name, cancellationToken);
            return;
        }

        if (!DungeonPickupPlanner.TryPlanItem(
                session.Inventory.Main,
                groundItem,
                itemCatalog,
                senders.GetInventoryWeightLimit(context, session.Character.Active),
                out var pickupPlan,
                out var pickupFailure))
        {
            logger.LogInformation(
                "Ground item {GroundId} cannot enter the inventory ({Failure}); it remains in the room.",
                groundId,
                pickupFailure);
            return;
        }

        var savedInventory = await store.SaveCharacterInventoryAsync(
            session.Login.AccountName,
            session.Character.Active.Id,
            session.Currency.Gold,
            pickupPlan.Inventory.Values,
            session.Inventory.Equipped.Values,
            cancellationToken);
        if (savedInventory is null)
        {
            logger.LogWarning(
                "Item pickup {GroundId} was not persisted; the ground item was retained.",
                groundId);
            return;
        }

        session.Character.Active = savedInventory;
        session.Inventory.Main = pickupPlan.Inventory;
        session.Dungeon.GroundItems.Remove(groundId);
        var itemPickup = DungeonPacketBuilders.CreateInventoryItemPickup(
            groundId,
            session.Character.LocalUserId,
            pickupPlan.DestinationSlot);
        await context.Outbound.SendAsync(itemPickup, Name, cancellationToken);

        var itemRefresh = builders.CreateMainInventoryPacket(
            session.Currency.Gold,
            session.Currency.VictoryPoints,
            session.Inventory.Main.Values,
            pickupPlan.DestinationSlot);
        await context.Outbound.SendAsync(itemRefresh, Name, cancellationToken);
    }
}
