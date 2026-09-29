using System.Buffers.Binary;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// Persistence, reply and refresh tail of <see cref="MoveItemSpaceHandler"/>
/// (07 §4.7: split by sub-flow to keep each file small).
/// </summary>
public sealed partial class MoveItemSpaceHandler
{
    private async Task SendMoveCompletionAsync(
        ConnectionContext context,
        byte sourceListType,
        ushort sourceSlot,
        uint requestedCount,
        byte destinationListType,
        ushort destinationSlot,
        CharacterItemRecord sourceItem,
        bool movedWithinMainList,
        CharacterItemRecord? displacedItem,
        CancellationToken cancellationToken)
    {
        var session = context.State;
        session.Character.Active = await store.SaveCharacterItemSpacesAsync(
            session.Login.AccountName,
            session.Character.Active.Id,
            session.Currency.Gold,
            session.Inventory.Main.Values,
            session.Inventory.Equipped.Values,
            session.Inventory.Warehouse.Values,
            cancellationToken,
            avatarInventory: session.Inventory.Avatar.Values) ?? session.Character.Active;

        var moveReply = GameProtocolEngine.CreateMoveItemSpaceReply(
            sourceListType,
            sourceSlot,
            requestedCount,
            destinationListType,
            destinationSlot);
        await context.Outbound.SendAsync(moveReply, Name, cancellationToken);

        if (sourceListType == 2 || destinationListType == 2)
        {
            if (sourceListType == 2 && destinationListType == 0)
            {
                // CMD 19 already removes a deposited item from the
                // main inventory. A redundant full list-zero snapshot
                // makes the old client select the category of the last
                // reconstructed item, unexpectedly changing the active
                // bag tab. Withdrawals still need the snapshot because
                // automatic stacking can update more than one bag slot.
                var refreshMain = builders.CreateMainInventoryPacket(
                    session.Currency.Gold,
                    session.Currency.VictoryPoints,
                    session.Inventory.Main.Values,
                    preferredLastSlot: destinationSlot);
                await context.Outbound.SendAsync(refreshMain, Name, cancellationToken);
            }

            var refreshWarehouse = builders.CreateWarehousePacket(
                session.Inventory.Warehouse.Values,
                session.Inventory.WarehouseCapacity);
            await context.Outbound.SendAsync(refreshWarehouse, Name, cancellationToken);

            logger.LogInformation(
                "Moved character cargo item {ItemId} from {SourceList}:{SourceSlot} to {DestinationList}:{DestinationSlot}.",
                sourceItem.ItemId,
                sourceListType,
                sourceSlot,
                destinationListType,
                destinationSlot);
            return;
        }

        if (sourceListType == 0)
        {
            // Protocol 19's internal removal is guarded by
            // a client-side pending-operation vector. Some
            // equipment paths leave that vector empty. A
            // move into an occupied worn slot swaps the old
            // equipment back into this source slot, so the
            // authoritative update must preserve that item
            // instead of always deleting the source.
            if (EntranceService.ShouldUseIncrementalMainMoveRefresh(
                    sourceListType,
                    sourceSlot,
                    destinationListType,
                    destinationSlot))
            {
                // CMD 19 completes the client transaction; NOTI 14
                // only reconciles the two changed slots and therefore
                // does not reconstruct every inventory category.
                await senders.SendMainInventoryMoveDeltaAsync(context, cancellationToken,
                    sourceSlot,
                    destinationSlot);
            }
            else if (movedWithinMainList
                || (destinationListType == 3 && displacedItem is not null))
            {
                // DFLegacy does not reliably replace existing
                // objects across the quick-slot (3..8) and
                // bag (9+) regions, nor during an occupied
                // equipment swap, when protocol 14 carries
                // only one slot. Refresh the complete main
                // list so both endpoints are authoritative.
                var refreshBag = builders.CreateMainInventoryPacket(
                    session.Currency.Gold,
                    session.Currency.VictoryPoints,
                    session.Inventory.Main.Values,
                    preferredLastSlot: movedWithinMainList
                        ? destinationSlot
                        : sourceSlot);
                await context.Outbound.SendAsync(refreshBag, Name, cancellationToken);
            }
            else
            {
                var sourceUpdateEntry = session.Inventory.Main.TryGetValue(
                    sourceSlot,
                    out var sourceReplacement)
                        ? builders.ToGameInventoryEntry(sourceReplacement)
                        : new GameInventoryEntry(
                            sourceSlot,
                            ushort.MaxValue,
                            0);
                var updateSourceItem =
                    GameProtocolEngine.CreateUpdateItemList(
                        0,
                        [sourceUpdateEntry]);
                await context.Outbound.SendAsync(updateSourceItem, Name, cancellationToken);
            }
        }

        if (sourceListType == 1 || destinationListType == 1)
        {
            var refreshAvatar = builders.CreateAvatarInventoryPacket(
                session.Inventory.Avatar.Values);
            await context.Outbound.SendAsync(refreshAvatar, Name, cancellationToken);
        }

        if (sourceListType == 3 || destinationListType == 3)
        {
            if (destinationListType == 3)
            {
                if (displacedItem is null)
                {
                    var addWornItem =
                        GameProtocolEngine.CreateUpdateItemList(
                            3,
                            [builders.ToGameInventoryEntry(
                                session.Inventory.Equipped[destinationSlot])]);
                    await context.Outbound.SendAsync(addWornItem, Name, cancellationToken);
                }
            }

            // The launcher enables the client's existing
            // notification-14 construction path for empty
            // worn slots. Keep that incremental path for
            // additions; removals are synchronized below
            // through the subtype-one snapshot.
        }

        var replacedWornItem = destinationListType == 3
            && displacedItem is not null;
        if (replacedWornItem)
        {
            // The drag controller still owns the old worn
            // object while the command reply is processed.
            await Task.Delay(
                TimeSpan.FromMilliseconds(100),
                cancellationToken);
        }

        if (session.Dungeon.CurrentDungeonId == 0
            && ((sourceListType == 3
                    && sourceSlot <= EquippedTitleSlot)
                || (destinationListType == 3
                    && destinationSlot <= EquippedTitleSlot)))
        {
            await senders.SendTownAppearanceAsync(context, cancellationToken: cancellationToken);
        }
        else if (sourceListType == 3 || replacedWornItem)
        {
            await senders.SendCurrentEquipmentStateAsync(context, cancellationToken: cancellationToken);
        }

        logger.LogInformation(
            "Moved item {ItemId} from {SourceList}:{SourceSlot} to {DestinationList}:{DestinationSlot}",
            sourceItem.ItemId,
            sourceListType,
            sourceSlot,
            destinationListType,
            destinationSlot);
    }
}
