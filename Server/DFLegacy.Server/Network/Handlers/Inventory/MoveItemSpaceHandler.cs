using System.Buffers.Binary;
using System.Text;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// MOVE_ITEM_SPACE (CMD 19): moves stacks between main/avatar/warehouse/creature spaces, including creature-space swaps and the compound equip flows (07 §5 group 9).
/// </summary>
public sealed partial class MoveItemSpaceHandler(JsonGameStore store,
    ItemCatalog itemCatalog,
    ILogger<MoveItemSpaceHandler> logger,
    CharacterPacketBuilders builders,
    ChannelSessionSenders senders,
    PremiumBenefitCatalog premiumBenefitCatalog) : IGameCommandHandler
{
    private static readonly Encoding ClientEncoding = PvfEncodings.Cp936Lossy();

    private const ushort EquippedTitleSlot = 10;

    public ushort ProtocolId => (byte)CmdPacketType.MOVE_ITEMSPACE;

    public string Name => "MOVE_ITEM_SPACE";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        var parsedMove = InventoryParsers.TryReadInventoryMove(
            request.Body,
            out var sourceListType,
            out var sourceSlot,
            out var moveCount,
            out var destinationListType,
            out var destinationSlot);
        if (await TryHandleCreatureSpaceMoveAsync(
                context,
                parsedMove,
                sourceListType,
                sourceSlot,
                moveCount,
                destinationListType,
                destinationSlot,
                cancellationToken))
        {
            return;
        }

        if (session.Character.Active is null
            || !parsedMove
            || !ChannelSessionSenders.IsInventorySlotValid(
                sourceListType,
                sourceSlot,
                session.Inventory.WarehouseCapacity)
            || (destinationSlot != ushort.MaxValue
                && !ChannelSessionSenders.IsInventorySlotValid(
                    destinationListType,
                    destinationSlot,
                    session.Inventory.WarehouseCapacity))
            || !ChannelSessionSenders.TryGetInventorySpace(
                sourceListType,
                session.Inventory.Main,
                session.Inventory.Avatar,
                session.Inventory.Equipped,
                session.Inventory.Warehouse,
                out var sourceSpace)
            || !ChannelSessionSenders.TryGetInventorySpace(
                destinationListType,
                session.Inventory.Main,
                session.Inventory.Avatar,
                session.Inventory.Equipped,
                session.Inventory.Warehouse,
                out var destinationSpace)
            || !sourceSpace.TryGetValue(sourceSlot, out var sourceItem))
        {
            // DFLegacy encodes a move into an empty main-list
            // slot in reverse: the empty target is sent as
            // the source and the occupied origin as the
            // destination. This is used by both quick-slot
            // and bag empty-position moves.
            if (session.Character.Active is not null
                && parsedMove
                && sourceListType == 0
                && destinationListType == 0
                && sourceSlot >= 3
                && destinationSlot >= 3
                && !session.Inventory.Main.ContainsKey(sourceSlot)
                && session.Inventory.Main.TryGetValue(
                    destinationSlot,
                    out var itemToMoveIntoEmptySlot)
                && senders.IsCompatibleMainInventorySlot(
                    itemToMoveIntoEmptySlot,
                    sourceSlot))
            {
                session.Inventory.Main.Remove(destinationSlot);
                session.Inventory.Main[sourceSlot] = itemToMoveIntoEmptySlot with
                {
                    Slot = sourceSlot
                };
                session.Character.Active = await store.SaveCharacterInventoryAsync(
                    session.Login.AccountName,
                    session.Character.Active.Id,
                    session.Currency.Gold,
                    session.Inventory.Main.Values,
                    session.Inventory.Equipped.Values,
                    cancellationToken) ?? session.Character.Active;

                var reverseMoveReply =
                    InventoryPacketBuilders.CreateMoveItemSpaceReply(
                        sourceListType,
                        sourceSlot,
                        itemToMoveIntoEmptySlot.CountOrValue,
                        destinationListType,
                        destinationSlot);
                await context.Outbound.SendAsync(reverseMoveReply, Name, cancellationToken);

                if (EntranceService.ShouldUseIncrementalMainMoveRefresh(
                        sourceListType,
                        sourceSlot,
                        destinationListType,
                        destinationSlot))
                {
                    // A complete list-zero rebuild makes the old
                    // client select the category of the last bag
                    // item. Quick slots have no category, so moving
                    // an item there used to jump to the material tab.
                    await senders.SendMainInventoryMoveDeltaAsync(context, cancellationToken,
                        destinationSlot,
                        sourceSlot);
                }
                else
                {
                    var refreshMain = builders.CreateMainInventoryPacket(
                        session.Currency.Gold,
                        session.Currency.VictoryPoints,
                        session.Inventory.Main.Values,
                        preferredLastSlot: sourceSlot);
                    await context.Outbound.SendAsync(refreshMain, Name, cancellationToken);
                }

                logger.LogInformation(
                    "Moved item {ItemId} from empty-target encoded 0:{DestinationSlot} to 0:{SourceSlot}",
                    itemToMoveIntoEmptySlot.ItemId,
                    destinationSlot,
                    sourceSlot);
                return;
            }

            // Avatar-bag empty targets are encoded in the
            // same reverse form as main-bag empty targets.
            if (session.Character.Active is not null
                && parsedMove
                && sourceListType == 1
                && destinationListType == 1
                && !session.Inventory.Avatar.ContainsKey(sourceSlot)
                && session.Inventory.Avatar.TryGetValue(
                    destinationSlot,
                    out var avatarToMove)
                && senders.IsCompatibleInventorySlot(
                    1,
                    avatarToMove,
                    sourceSlot,
                    session.Inventory.WarehouseCapacity))
            {
                session.Inventory.Avatar.Remove(destinationSlot);
                session.Inventory.Avatar[sourceSlot] = avatarToMove with
                {
                    Slot = sourceSlot
                };
                session.Character.Active = await store.SaveCharacterItemSpacesAsync(
                    session.Login.AccountName,
                    session.Character.Active.Id,
                    session.Currency.Gold,
                    session.Inventory.Main.Values,
                    session.Inventory.Equipped.Values,
                    session.Inventory.Warehouse.Values,
                    cancellationToken,
                    avatarInventory: session.Inventory.Avatar.Values)
                    ?? session.Character.Active;

                var reverseAvatarReply =
                    InventoryPacketBuilders.CreateMoveItemSpaceReply(
                        sourceListType,
                        sourceSlot,
                        avatarToMove.CountOrValue,
                        destinationListType,
                        destinationSlot);
                await context.Outbound.SendAsync(reverseAvatarReply, Name, cancellationToken);

                var refreshAvatar = builders.CreateAvatarInventoryPacket(
                    session.Inventory.Avatar.Values);
                await context.Outbound.SendAsync(refreshAvatar, Name, cancellationToken);
                return;
            }

            // Unequipping an Avatar also arrives as list 1
            // empty source -> list 3 occupied destination.
            if (session.Character.Active is not null
                && parsedMove
                && sourceListType == 1
                && destinationListType == 3
                && !session.Inventory.Avatar.ContainsKey(sourceSlot)
                && session.Inventory.Equipped.TryGetValue(
                    destinationSlot,
                    out var avatarToUnequip)
                && senders.IsCompatibleInventorySlot(
                    1,
                    avatarToUnequip,
                    sourceSlot,
                    session.Inventory.WarehouseCapacity)
                && itemCatalog.TryGetDefinition(
                    avatarToUnequip.ItemId,
                    out var avatarDefinition)
                && CharacterAvatarInventoryLayout.IsCompatibleWornSlot(
                    avatarDefinition,
                    destinationSlot))
            {
                session.Inventory.Equipped.Remove(destinationSlot);
                var unsealedAvatar =
                    CharacterItemSealing.UnsealWhenEquipped(
                        avatarToUnequip,
                        avatarDefinition);
                session.Inventory.Avatar[sourceSlot] = unsealedAvatar with
                {
                    Slot = sourceSlot,
                    State = 0
                };
                session.Character.Active = await store.SaveCharacterItemSpacesAsync(
                    session.Login.AccountName,
                    session.Character.Active.Id,
                    session.Currency.Gold,
                    session.Inventory.Main.Values,
                    session.Inventory.Equipped.Values,
                    session.Inventory.Warehouse.Values,
                    cancellationToken,
                    avatarInventory: session.Inventory.Avatar.Values)
                    ?? session.Character.Active;

                var avatarUnequipReply =
                    InventoryPacketBuilders.CreateMoveItemSpaceReply(
                        sourceListType,
                        sourceSlot,
                        avatarToUnequip.CountOrValue,
                        destinationListType,
                        destinationSlot);
                await context.Outbound.SendAsync(avatarUnequipReply, Name, cancellationToken);

                var refreshAvatar = builders.CreateAvatarInventoryPacket(
                    session.Inventory.Avatar.Values);
                await context.Outbound.SendAsync(refreshAvatar, Name, cancellationToken);

                await Task.Delay(
                    TimeSpan.FromMilliseconds(100),
                    cancellationToken);
                if (session.Dungeon.CurrentDungeonId == 0)
                {
                    await senders.SendTownAppearanceAsync(context, cancellationToken: cancellationToken);
                }
                else
                {
                    await senders.SendCurrentEquipmentStateAsync(context, cancellationToken: cancellationToken);
                }

                logger.LogInformation(
                    "Unequipped Avatar item {ItemId} from 3:{WornSlot} to 1:{BagSlot}.",
                    avatarToUnequip.ItemId,
                    destinationSlot,
                    sourceSlot);
                return;
            }

            // Dropping a cargo item onto an empty bag slot is
            // encoded in reverse by DFLegacy, just like an
            // unequip: the empty main slot is the source and
            // the occupied cargo slot is the destination.
            if (session.Character.Active is not null
                && parsedMove
                && sourceListType == 0
                && destinationListType == 2
                && sourceSlot >= 3
                && destinationSlot < session.Inventory.WarehouseCapacity
                && !session.Inventory.Main.ContainsKey(sourceSlot)
                && session.Inventory.Warehouse.TryGetValue(
                    destinationSlot,
                    out var itemToWithdraw)
                && itemCatalog.TryGetDefinition(
                    itemToWithdraw.ItemId,
                    out _))
            {
                var withdrawnCount = moveCount <= 0
                    ? itemToWithdraw.CountOrValue
                    : Math.Min(itemToWithdraw.CountOrValue, (uint)moveCount);
                var withdrawal = new CharacterMailAttachmentRecord(
                    itemToWithdraw.ItemId,
                    withdrawnCount,
                    itemToWithdraw.State,
                    itemToWithdraw.Durability,
                    itemToWithdraw.SealState,
                    EquipmentQualitySeed:
                        itemToWithdraw.EquipmentQualitySeed,
                    InstanceId: itemToWithdraw.InstanceId);
                if (!CharacterInventoryPlanner.TryPlace(
                        session.Inventory.Main,
                        withdrawal,
                        itemCatalog,
                        senders.GetInventoryWeightLimit(context, session.Character.Active),
                        sourceSlot,
                        out var withdrawalPlan,
                        out var withdrawalFailure))
                {
                    await senders.SendInventoryMoveNoOpAsync(context, 
                        sourceListType,
                        sourceSlot,
                        destinationListType,
                        destinationSlot,
                        $"warehouse withdrawal failed: {withdrawalFailure}",
                        cancellationToken);
                    return;
                }

                if (withdrawnCount < itemToWithdraw.CountOrValue)
                {
                    session.Inventory.Warehouse[destinationSlot] = itemToWithdraw with
                    {
                        CountOrValue = itemToWithdraw.CountOrValue - withdrawnCount
                    };
                }
                else
                {
                    session.Inventory.Warehouse.Remove(destinationSlot);
                }

                var savedWithdrawal = await store.SaveCharacterItemSpacesAsync(
                    session.Login.AccountName,
                    session.Character.Active.Id,
                    session.Currency.Gold,
                    withdrawalPlan.Inventory.Values,
                    session.Inventory.Equipped.Values,
                    session.Inventory.Warehouse.Values,
                    cancellationToken);
                if (savedWithdrawal is null)
                {
                    session.Inventory.Warehouse[destinationSlot] = itemToWithdraw;

                    await senders.SendInventoryMoveNoOpAsync(context, 
                        sourceListType,
                        sourceSlot,
                        destinationListType,
                        destinationSlot,
                        "warehouse withdrawal persistence failed");
                    return;
                }

                session.Character.Active = savedWithdrawal;
                session.Inventory.Main = withdrawalPlan.Inventory;

                var withdrawReply =
                    InventoryPacketBuilders.CreateMoveItemSpaceReply(
                        sourceListType,
                        sourceSlot,
                        withdrawnCount,
                        destinationListType,
                        destinationSlot);
                await context.Outbound.SendAsync(withdrawReply, Name, cancellationToken);

                foreach (var refresh in new[]
                         {
                              builders.CreateMainInventoryPacket(
                                  session.Currency.Gold,
                                  session.Currency.VictoryPoints,
                                  session.Inventory.Main.Values,
                                  withdrawalPlan.DestinationSlot),
                             builders.CreateWarehousePacket(
                                 session.Inventory.Warehouse.Values,
                                 session.Inventory.WarehouseCapacity)
                         })
                {
                    await context.Outbound.SendAsync(refresh, Name, cancellationToken);
                }

                logger.LogInformation(
                    "Withdrew item {ItemId} from reverse-encoded cargo slot {CargoSlot}; main destination starts at {MainSlot}.",
                    itemToWithdraw.ItemId,
                    destinationSlot,
                    withdrawalPlan.DestinationSlot);
                return;
            }

            // This client unequips by selecting an empty
            // bag slot as the source and the occupied worn
            // slot as the destination. Treat that request
            // as a reverse swap into the empty bag slot.
            if (session.Character.Active is not null
                && parsedMove
                && sourceListType == 0
                && sourceSlot >= 3
                && destinationListType == 3
                && session.Inventory.Equipped.TryGetValue(
                    destinationSlot,
                    out var itemToUnequip)
                && senders.IsCompatibleMainInventorySlot(
                    itemToUnequip,
                    sourceSlot))
            {
                session.Inventory.Equipped.Remove(destinationSlot);
                var unsealedItem =
                    CharacterItemSealing.UnsealWhenEquipped(
                        itemToUnequip,
                        itemCatalog);
                session.Inventory.Main[sourceSlot] = CharacterItemIdentity.MoveToSlot(
                    unsealedItem,
                    sourceSlot,
                    itemCatalog);
                session.Character.Active = await store.SaveCharacterInventoryAsync(
                    session.Login.AccountName,
                    session.Character.Active.Id,
                    session.Currency.Gold,
                    session.Inventory.Main.Values,
                    session.Inventory.Equipped.Values,
                    cancellationToken) ?? session.Character.Active;

                var unequipReply =
                    InventoryPacketBuilders.CreateMoveItemSpaceReply(
                        sourceListType,
                        sourceSlot,
                        itemToUnequip.CountOrValue,
                        destinationListType,
                        destinationSlot);
                await context.Outbound.SendAsync(unequipReply, Name, cancellationToken);

                // Protocol 14 treats every added record as
                // newly acquired and displays a loot notice.
                // Protocol 13 refreshes the same bag state
                // silently and is already used after sales.
                var refreshBag = builders.CreateMainInventoryPacket(
                    session.Currency.Gold,
                    session.Currency.VictoryPoints,
                    session.Inventory.Main.Values,
                    preferredLastSlot: sourceSlot);
                await context.Outbound.SendAsync(refreshBag, Name, cancellationToken);

                // Do not free the worn object in the same UI
                // tick. Once the drag controller releases
                // its pointer, the normal list-3 removal is
                // safe and updates the equipment panel.
                await Task.Delay(
                    TimeSpan.FromMilliseconds(100),
                    cancellationToken);

                if (session.Dungeon.CurrentDungeonId == 0
                    && destinationSlot is 9 or EquippedTitleSlot)
                {
                    await senders.SendTownAppearanceAsync(context, cancellationToken: cancellationToken);
                }
                else
                {
                    // Repeated protocol-14 removals leave
                    // this client with a stale worn-object
                    // pointer and crash on the second
                    // unequip. Subtype one owns the full
                    // equipment projection and safely
                    // removes every omitted worn slot.
                    await senders.SendCurrentEquipmentStateAsync(context, cancellationToken: cancellationToken);
                }

                logger.LogInformation(
                    "Unequipped item {ItemId} from 3:{SourceSlot} to 0:{DestinationSlot}",
                    itemToUnequip.ItemId,
                    destinationSlot,
                    sourceSlot);

                return;
            }

            if (session.Character.Active is not null && parsedMove)
            {
                await senders.SendInventoryMoveNoOpAsync(context, 
                    sourceListType,
                    sourceSlot,
                    destinationListType,
                    destinationSlot,
                    "invalid or unavailable source/destination");
            }
            else
            {
                var error = ConnectionPacketBuilders.CreateCommandError(request, 4);
                await context.Outbound.SendAsync(error, Name, cancellationToken);
            }

            if (session.Character.Active is not null
                && parsedMove
                && sourceListType == 0
                && destinationListType == 0)
            {
                var refreshMain = builders.CreateMainInventoryPacket(
                    session.Currency.Gold,
                    session.Currency.VictoryPoints,
                    session.Inventory.Main.Values);
                await context.Outbound.SendAsync(refreshMain, Name, cancellationToken);
            }

            return;
        }

        if (await TryHandleForwardWarehouseWithdrawalAsync(
                context,
                sourceListType,
                sourceSlot,
                moveCount,
                destinationListType,
                destinationSlot,
                sourceItem,
                cancellationToken))
        {
            return;
        }

        if (destinationSlot == ushort.MaxValue)
        {
            destinationSlot = senders.FindPartialStackSlot(
                destinationSpace,
                sourceItem,
                destinationListType,
                sourceSpace == destinationSpace ? sourceSlot : null);
            if (destinationSlot == ushort.MaxValue)
            {
                destinationSlot = destinationListType == 0
                    ? senders.FindFreeCompatibleMainInventorySlot(
                        destinationSpace,
                        sourceItem)
                    : ChannelSessionSenders.FindFreeInventorySlot(
                        destinationSpace,
                        0,
                        destinationListType switch
                        {
                            1 => CharacterAvatarInventoryLayout.Capacity,
                            2 => session.Inventory.WarehouseCapacity,
                            3 => 23,
                            _ => 0
                        });
            }
        }

        if (destinationSlot == ushort.MaxValue
            || !ChannelSessionSenders.IsInventorySlotValid(
                destinationListType,
                destinationSlot,
                session.Inventory.WarehouseCapacity)
            || !senders.IsCompatibleInventorySlot(
                destinationListType,
                sourceItem,
                destinationSlot,
                session.Inventory.WarehouseCapacity))
        {
            await senders.SendInventoryMoveNoOpAsync(context, 
                sourceListType,
                sourceSlot,
                destinationListType,
                destinationSlot,
                "destination slot is invalid or incompatible");
            return;
        }

        if (destinationListType == 2
            && itemCatalog.TryGetDefinition(
                sourceItem.ItemId,
                out var warehouseDefinition)
            && warehouseDefinition.InventoryCategory
                == ItemInventoryCategory.Avatar)
        {
            await senders.SendInventoryMoveNoOpAsync(context, 
                sourceListType,
                sourceSlot,
                destinationListType,
                destinationSlot,
                "avatar items cannot enter character storage");
            return;
        }

        if (destinationListType == 3
            && itemCatalog.TryGetDefinition(
                sourceItem.ItemId,
                out var equipmentDefinition)
            && !premiumBenefitCatalog.CanEquipByLevel(
                session.Character.Active.Level,
                senders.GetActivePremiumServiceTypes(context),
                equipmentDefinition))
        {
            await senders.SendInventoryMoveNoOpAsync(context, 
                sourceListType,
                sourceSlot,
                destinationListType,
                destinationSlot,
                $"equipment level {equipmentDefinition.MinimumLevel.GetValueOrDefault()} exceeds the Premium-effective character level");
            return;
        }

        var movedWithinMainList = sourceListType == 0
            && destinationListType == 0;
        if (sourceListType == destinationListType
            && sourceSlot == destinationSlot)
        {
            var noOpReply = InventoryPacketBuilders.CreateMoveItemSpaceReply(
                sourceListType,
                sourceSlot,
                sourceItem.CountOrValue,
                destinationListType,
                destinationSlot);
            await context.Outbound.SendAsync(noOpReply, Name, cancellationToken);
            return;
        }

        destinationSpace.TryGetValue(destinationSlot, out var displacedItem);
        if (displacedItem is not null
            && !senders.IsCompatibleInventorySlot(
                sourceListType,
                displacedItem,
                sourceSlot,
                session.Inventory.WarehouseCapacity))
        {
            await senders.SendInventoryMoveNoOpAsync(context, 
                sourceListType,
                sourceSlot,
                destinationListType,
                destinationSlot,
                "displaced item is incompatible with the source slot");
            return;
        }

        if (sourceListType == 3 || destinationListType == 3)
        {
            sourceItem = CharacterItemSealing.UnsealWhenEquipped(
                sourceItem,
                itemCatalog);
            if (displacedItem is not null)
            {
                displacedItem = CharacterItemSealing.UnsealWhenEquipped(
                    displacedItem,
                    itemCatalog);
            }
        }

        var requestedCount = moveCount <= 0
            ? sourceItem.CountOrValue
            : Math.Min(sourceItem.CountOrValue, (uint)moveCount);
        var canStack = displacedItem is not null
            && sourceListType != 3
            && destinationListType != 3
            && !itemCatalog.IsEquipment(sourceItem.ItemId)
            && sourceItem.ItemId == displacedItem.ItemId
            && sourceItem.State == displacedItem.State
            && sourceItem.Durability == displacedItem.Durability
            && sourceItem.SealState == displacedItem.SealState;
        var canSplit = sourceListType != 3
            && destinationListType != 3
            && displacedItem is null
            && requestedCount < sourceItem.CountOrValue;

        if (canStack)
        {
            var stackLimit = itemCatalog.TryGetDefinition(
                    sourceItem.ItemId,
                    out var stackDefinition)
                ? CharacterInventoryPlanner.GetStackLimit(stackDefinition)
                : CharacterInventoryPlanner.DefaultStackLimit;
            var available = stackLimit > displacedItem!.CountOrValue
                ? stackLimit - displacedItem.CountOrValue
                : 0;
            if (available == 0)
            {
                await senders.SendInventoryMoveNoOpAsync(context, 
                    sourceListType,
                    sourceSlot,
                    destinationListType,
                    destinationSlot,
                    "destination stack is full");
                return;
            }

            requestedCount = Math.Min(requestedCount, available);
            var stackedCount = checked(
                displacedItem!.CountOrValue + requestedCount);
            destinationSpace[destinationSlot] = displacedItem with
            {
                CountOrValue = stackedCount
            };
            if (requestedCount < sourceItem.CountOrValue)
            {
                sourceSpace[sourceSlot] = sourceItem with
                {
                    CountOrValue = sourceItem.CountOrValue - requestedCount
                };
            }
            else
            {
                sourceSpace.Remove(sourceSlot);
            }
        }
        else if (canSplit)
        {
            sourceSpace[sourceSlot] = sourceItem with
            {
                CountOrValue = sourceItem.CountOrValue - requestedCount
            };
            destinationSpace[destinationSlot] = sourceItem with
            {
                Slot = destinationSlot,
                CountOrValue = requestedCount
            };
        }
        else
        {
            sourceSpace.Remove(sourceSlot);
            destinationSpace[destinationSlot] = CharacterItemIdentity.MoveToSlot(
                sourceItem, destinationSlot, itemCatalog);

            if (displacedItem is not null)
            {
                destinationSpace.Remove(destinationSlot);
                destinationSpace[destinationSlot] = CharacterItemIdentity.MoveToSlot(
                    sourceItem, destinationSlot, itemCatalog);
                sourceSpace[sourceSlot] = CharacterItemIdentity.MoveToSlot(
                    displacedItem, sourceSlot, itemCatalog);
            }
        }

        await SendMoveCompletionAsync(
            context,
            sourceListType,
            sourceSlot,
            requestedCount,
            destinationListType,
            destinationSlot,
            sourceItem,
            movedWithinMainList,
            displacedItem,
            cancellationToken);
    }
}
