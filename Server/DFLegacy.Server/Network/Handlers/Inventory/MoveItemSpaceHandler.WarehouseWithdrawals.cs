using System.Buffers.Binary;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// Forward cargo withdrawal sub-flow of <see cref="MoveItemSpaceHandler"/>
/// (07 §4.7: split by sub-flow to keep each file small). A forward cargo
/// withdrawal carries the occupied cargo source and an explicit empty bag
/// target: treat it as a newly entering bag item and fill every compatible
/// partial stack before allocating a slot.
/// </summary>
public sealed partial class MoveItemSpaceHandler
{
    private async Task<bool> TryHandleForwardWarehouseWithdrawalAsync(
        ConnectionContext context,
        byte sourceListType,
        ushort sourceSlot,
        int moveCount,
        byte destinationListType,
        ushort destinationSlot,
        CharacterItemRecord sourceItem,
        CancellationToken cancellationToken)
    {
        var session = context.State;
        if (session.Character.Active is null
            || sourceListType != 2
            || destinationListType != 0
            || session.Inventory.Main.ContainsKey(destinationSlot)
            || !senders.IsCompatibleMainInventorySlot(sourceItem, destinationSlot))
        {
            return false;
        }

            var withdrawnCount = moveCount <= 0
                ? sourceItem.CountOrValue
                : Math.Min(sourceItem.CountOrValue, (uint)moveCount);
            var withdrawal = new CharacterMailAttachmentRecord(
                sourceItem.ItemId,
                withdrawnCount,
                sourceItem.State,
                sourceItem.Durability,
                sourceItem.SealState,
                EquipmentQualitySeed:
                    sourceItem.EquipmentQualitySeed,
                InstanceId: sourceItem.InstanceId);
            if (!CharacterInventoryPlanner.TryPlace(
                    session.Inventory.Main,
                    withdrawal,
                    itemCatalog,
                    senders.GetInventoryWeightLimit(context, session.Character.Active!),
                    destinationSlot,
                    out var withdrawalPlan,
                    out var withdrawalFailure))
            {
                await senders.SendInventoryMoveNoOpAsync(context, 
                    sourceListType,
                    sourceSlot,
                    destinationListType,
                    destinationSlot,
                    $"forward warehouse withdrawal failed: {withdrawalFailure}");
                    return true;
            }

            var plannedWarehouse = session.Inventory.Warehouse.ToDictionary(
                pair => pair.Key,
                pair => pair.Value);
            if (withdrawnCount < sourceItem.CountOrValue)
            {
                plannedWarehouse[sourceSlot] = sourceItem with
                {
                    CountOrValue = sourceItem.CountOrValue - withdrawnCount
                };
            }
            else
            {
                plannedWarehouse.Remove(sourceSlot);
            }

            var savedWithdrawal = await store.SaveCharacterItemSpacesAsync(
                session.Login.AccountName,
                session.Character.Active.Id,
                session.Currency.Gold,
                withdrawalPlan.Inventory.Values,
                session.Inventory.Equipped.Values,
                plannedWarehouse.Values,
                cancellationToken);
            if (savedWithdrawal is null)
            {
                await senders.SendInventoryMoveNoOpAsync(context, 
                    sourceListType,
                    sourceSlot,
                    destinationListType,
                    destinationSlot,
                    "forward warehouse withdrawal persistence failed");
                    return true;
            }

            session.Character.Active = savedWithdrawal;
            session.Inventory.Main = withdrawalPlan.Inventory;
            session.Inventory.Warehouse = plannedWarehouse;

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
                "Withdrew item {ItemId} from forward cargo slot {CargoSlot}; automatic main destination starts at {MainSlot}.",
                sourceItem.ItemId,
                sourceSlot,
                withdrawalPlan.DestinationSlot);
                    return true;
    }
}
