using System.Buffers.Binary;
using System.Text;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// BUY_ITEM (CMD 21): NPC shop purchases with the shared inventory/warehouse overflow path (07 §5 group 10).
/// </summary>
public sealed class BuyItemHandler(JsonGameStore store,
    ItemCatalog itemCatalog,
    CharacterSessionRegistry characterSessions,
    ILogger<BuyItemHandler> logger,
    CharacterPacketBuilders builders,
    ChannelSessionSenders senders) : IGameCommandHandler
{

    private static readonly Encoding ClientEncoding = PvfEncodings.Cp936Lossy();

    public ushort ProtocolId => GameProtocolEngine.BuyItemCommand;

    public string Name => "BuyItem";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        if (session.Character.Active is null
            || !ShopParsers.TryReadBuyItem(
                request.Body,
                out var itemId,
                out var countOrQualitySeed))
        {
            var error = ConnectionPacketBuilders.CreateCommandError(request, 4);
            await context.Outbound.SendAsync(error, Name, cancellationToken);
            return;
        }

        if (!itemCatalog.TryGetDefinition(itemId, out var itemDefinition))
        {
            var error = ConnectionPacketBuilders.CreateCommandError(request, 4);
            await context.Outbound.SendAsync(error, Name, cancellationToken);
            return;
        }

        var count = itemDefinition.IsEquipment
            ? 1u
            : (uint)Math.Clamp(countOrQualitySeed, 1, 999);
        if (!itemCatalog.TryGetPurchasePrice(itemId, out var unitPrice))
        {
            var error = ConnectionPacketBuilders.CreateCommandError(request, 4);
            await context.Outbound.SendAsync(error, Name, cancellationToken);
            logger.LogWarning(
                "Rejected NPC-shop purchase of item {ItemId}: its cached item definition disappeared during request processing.",
                itemId);
            return;
        }

        var totalPrice = checked(unitPrice * (int)count);
        if (session.Currency.Gold < totalPrice)
        {
            var error = ConnectionPacketBuilders.CreateCommandError(request, 4);
            await context.Outbound.SendAsync(error, Name, cancellationToken);
            return;
        }

        var purchasedItem = CharacterItemIdentity.Ensure(
            new CharacterMailAttachmentRecord(
            itemId,
            count,
            Durability: itemDefinition.InventoryCategory
                == ItemInventoryCategory.Equipment
                    ? itemCatalog.GetInitialDurability(itemId)
                    : itemDefinition.IsEquipment
                        ? (ushort)1_000
                        : (ushort)0,
            SealState: CharacterItemSealing.GetInitialSealState(
                itemDefinition),
            EquipmentQualitySeed:
                itemDefinition.InventoryCategory
                    == ItemInventoryCategory.Equipment
                && !itemDefinition.IsTitle
                    ? EquipmentQuality.GetNpcShopSeed(
                        DateTimeOffset.Now)
                    : null),
            itemDefinition);
        var plannedInventory = session.Inventory.Main.ToDictionary(
            pair => pair.Key,
            pair => pair.Value);
        var plannedAvatarInventory = session.Inventory.Avatar.ToDictionary(
            pair => pair.Key,
            pair => pair.Value);
        var plannedCreatureInventory = session.Inventory.Creature.ToDictionary(
            pair => pair.Key,
            pair => pair.Value);
        var isWarehouseUpgradeItem =
            CharacterWarehouseProgression.TryGetItemTargetCapacity(
                itemId,
                out var warehouseUpgradeTarget);
        var plannedWarehouseCapacity = isWarehouseUpgradeItem
            ? Math.Max(session.Inventory.WarehouseCapacity, warehouseUpgradeTarget)
            : session.Inventory.WarehouseCapacity;
        var overflowMails = Array.Empty<CreateCharacterMailRequest>();
        ushort? destinationSlot = null;
        var destinationListType = itemDefinition.InventoryCategory switch
        {
            ItemInventoryCategory.Avatar => (byte)1,
            ItemInventoryCategory.Creature => (byte)7,
            _ => (byte)0
        };
        var placementFailure = InventoryPlacementFailure.None;
        if (isWarehouseUpgradeItem)
        {
            destinationSlot = null;
        }
        else if (itemDefinition.InventoryCategory
                     == ItemInventoryCategory.Avatar
                 && CharacterAvatarInventoryPlanner.TryPlace(
                     plannedAvatarInventory,
                     purchasedItem,
                     itemCatalog,
                     preferredEmptySlot: null,
                     out var avatarPurchasePlan,
                     out placementFailure))
        {
            plannedAvatarInventory = avatarPurchasePlan.Inventory;
            destinationSlot = avatarPurchasePlan.DestinationSlot;
        }
        else if (itemDefinition.InventoryCategory
                     == ItemInventoryCategory.Creature
                 && CharacterCreatureInventoryPlanner.TryPlace(
                     plannedCreatureInventory,
                     new CharacterItemRecord(
                         ushort.MaxValue,
                         purchasedItem.ItemId,
                         purchasedItem.CountOrValue,
                         purchasedItem.State,
                         purchasedItem.Durability,
                         purchasedItem.SealState,
                         InstanceId: purchasedItem.InstanceId),
                     itemCatalog,
                     preferredEmptySlot: null,
                     out var creaturePurchasePlan,
                     out placementFailure))
        {
            plannedCreatureInventory = creaturePurchasePlan.Inventory;
            destinationSlot = creaturePurchasePlan.DestinationSlot;
        }
        else if (itemDefinition.InventoryCategory
                     is not (ItemInventoryCategory.Avatar
                         or ItemInventoryCategory.Creature)
                 && CharacterInventoryPlanner.TryPlace(
                 plannedInventory,
                 purchasedItem,
                 itemCatalog,
                 senders.GetInventoryWeightLimit(context, session.Character.Active),
                 out var purchasePlan,
                 out placementFailure))
        {
            plannedInventory = purchasePlan.Inventory;
            destinationSlot = purchasePlan.DestinationSlot;
        }
        else if (placementFailure is InventoryPlacementFailure.InvalidItem
                 or InventoryPlacementFailure.UnsupportedCategory)
        {
            var error = ConnectionPacketBuilders.CreateCommandError(request, 4);
            await context.Outbound.SendAsync(error, Name, cancellationToken);
            return;
        }
        else
        {
            overflowMails = CharacterPacketBuilders.CreateOverflowMailRequests(
                CharacterInventoryPlanner.CreateMailAttachments(
                    purchasedItem,
                    itemCatalog),
                "Town shop purchase");
        }

        var nextGold = session.Currency.Gold - totalPrice;
        var savedCharacter = await store.SaveCharacterItemSpacesAsync(
            session.Login.AccountName,
            session.Character.Active.Id,
            nextGold,
            plannedInventory.Values,
            session.Inventory.Equipped.Values,
            session.Inventory.Warehouse.Values,
            cancellationToken,
            overflowMails,
            avatarInventory: plannedAvatarInventory.Values,
            creatureInventory: plannedCreatureInventory.Values,
            warehouseCapacity: plannedWarehouseCapacity,
            expectedWarehouseCapacity: session.Inventory.WarehouseCapacity);
        if (savedCharacter is null)
        {
            var error = ConnectionPacketBuilders.CreateCommandError(request, 4);
            await context.Outbound.SendAsync(error, Name, cancellationToken);
            return;
        }

        var warehouseCapacityChanged =
            senders.AdoptSavedWarehouseUpgradeState(context, savedCharacter);
        session.Character.Active = savedCharacter;
        session.Inventory.Main = (savedCharacter.Inventory ?? [])
            .ToDictionary(item => item.Slot);
        session.Inventory.Avatar = (savedCharacter.AvatarInventory ?? [])
            .ToDictionary(item => item.Slot);
        session.Inventory.Creature = (savedCharacter.CreatureInventory ?? [])
            .ToDictionary(item => item.Slot);
        session.Currency.Gold = nextGold;
        if (overflowMails.Length > 0)
        {
            characterSessions.NotifyMailReceived(session.Character.Active.CharacterNo);
        }

        var buyReply = ShopPacketBuilders.CreateBuyItemReply(
            updatedGold: (uint)session.Currency.Gold,
            updatedVictoryPoints: session.Currency.VictoryPoints,
            auxiliaryItemSpaceValue: 0,
            updatedCera: (uint)session.Currency.Cash,
            slot: destinationSlot ?? ushort.MaxValue,
            itemId: purchasedItem.ItemId,
            countOrValue: purchasedItem.CountOrValue);
        await context.Outbound.SendAsync(buyReply, Name, cancellationToken);
        var mainRefresh = builders.CreateMainInventoryPacket(
            session.Currency.Gold,
            session.Currency.VictoryPoints,
            session.Inventory.Main.Values,
            preferredLastSlot: destinationSlot);
        await context.Outbound.SendAsync(mainRefresh, Name, cancellationToken);
        if (destinationListType == 1)
        {
            var avatarRefresh = builders.CreateAvatarInventoryPacket(
                session.Inventory.Avatar.Values);
            await context.Outbound.SendAsync(avatarRefresh, Name, cancellationToken);
        }
        else if (destinationListType == 7)
        {
            foreach (var creatureRefresh in builders.CreateCreatureInventoryPackets(
                         session.Inventory.Creature.Values))
            {
                await context.Outbound.SendAsync(creatureRefresh, Name, cancellationToken);
            }
        }
        if (warehouseCapacityChanged || isWarehouseUpgradeItem)
        {
            await senders.SendWarehouseStateAsync(context, cancellationToken);
        }
        logger.LogInformation(
            "Bought item {ItemId} x{Count} for {UnitPrice} each ({TotalPrice} total); listType={ListType}, slot={Slot}, overflowMail={OverflowMail}, gold={Gold}",
            itemId,
            count,
            unitPrice,
            totalPrice,
            destinationListType,
            destinationSlot,
            overflowMails.Length,
            session.Currency.Gold);
    }
}
