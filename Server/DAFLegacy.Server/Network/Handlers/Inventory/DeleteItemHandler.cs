using System.Buffers.Binary;
using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

/// <summary>
/// DELETE_ITEM (CMD 18): removes an item stack and refreshes the affected spaces (07 §5 group 9).
/// </summary>
public sealed class DeleteItemHandler(
    ServerOptions options,
    RuntimeState runtime,
    IGameStore store,
    ItemCatalog itemCatalog
    ) : IGameCommandHandler
{

    public ushort ProtocolId => (byte)CmdPacketType.DELETE_ITEM;

    public string Name => "DeleteItem";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        if (!options.EnablePacketTracing)
        {
            // Snapshot-only (no tap): the RX funnel call in
            // LogPacket already owns tap publication for
            // this frame.
            runtime.TracePacketAlways(
                context.Runtime,
                "RX-CONSUME-ITEMS",
                request,
                options.HexDumpLimit);
        }

        if (session.Character.Active is null
            || !InventoryParsers.TryReadConsumeItems(
                request.Body,
                out var listType,
                out var requestedConsumptions)
            || listType != 0)
        {
            RuntimeLogger.Warn(
                "Rejected consume-items transaction session.Character.Active={HasCharacter}, body={Body}.",
                session.Character.Active is not null,
                Convert.ToHexString(request.Body));
            return;
        }

        var completionEntries =
            new List<GameItemConsumptionEntry>(
                requestedConsumptions.Length);
        var consumptionBySlot = new Dictionary<ushort, ulong>();
        var validTransaction = true;
        foreach (var requestedConsumption in requestedConsumptions)
        {
            if (!session.Inventory.Main.TryGetValue(
                    requestedConsumption.Slot,
                    out var inventoryItem)
                || requestedConsumption.RequestedCount == 0
                || requestedConsumption.ItemId != inventoryItem.ItemId
                || itemCatalog.IsEquipment(inventoryItem.ItemId))
            {
                RuntimeLogger.Warn(
                    "Consume-items transaction has an invalid entry operation={Operation}, slot={Slot}, requestedItem={RequestedItemId}, actualItem={ActualItemId}, requested={RequestedCount}.",
                    requestedConsumption.Operation,
                    requestedConsumption.Slot,
                    requestedConsumption.ItemId,
                    inventoryItem?.ItemId,
                    requestedConsumption.RequestedCount);
                validTransaction = false;
                break;
            }

            var accumulated = consumptionBySlot.GetValueOrDefault(
                requestedConsumption.Slot);
            accumulated += requestedConsumption.RequestedCount;
            if (accumulated > inventoryItem.CountOrValue)
            {
                RuntimeLogger.Warn(
                    "Consume-items transaction exceeds slot={Slot} stack item={ItemId}, requestedTotal={RequestedTotal}, current={CurrentCount}.",
                    requestedConsumption.Slot,
                    inventoryItem.ItemId,
                    accumulated,
                    inventoryItem.CountOrValue);
                validTransaction = false;
                break;
            }

            consumptionBySlot[requestedConsumption.Slot] =
                accumulated;
            completionEntries.Add(
                new GameItemConsumptionEntry(
                    requestedConsumption.Slot,
                    requestedConsumption.RequestedCount));
        }

        if (!validTransaction)
        {
            var deleteItemError =
                InventoryPacketBuilders.CreateDeleteItemError(
                    listType,
                    errorCode: 0x11);
            await context.Queue.SendAsync(deleteItemError, Name, cancellationToken);
            return;
        }

        foreach (var (slot, requestedCount) in consumptionBySlot)
        {
            var inventoryItem = session.Inventory.Main[slot];
            var remaining = inventoryItem.CountOrValue
                - checked((uint)requestedCount);
            if (remaining == 0)
            {
                session.Inventory.Main.Remove(slot);
            }
            else
            {
                session.Inventory.Main[slot] = inventoryItem with
                {
                    CountOrValue = remaining
                };
            }
        }

        session.Character.Active = await store.SaveCharacterItemSpacesAsync(
            session.Login.AccountName,
            session.Character.Active.Id,
            session.Currency.Gold,
            session.Inventory.Main.Values,
            session.Inventory.Equipped.Values,
            session.Inventory.Warehouse.Values,
            cancellationToken,
            avatarInventory: session.Inventory.Avatar.Values) ?? session.Character.Active;

        foreach (var requestedConsumption in requestedConsumptions)
        {
            RuntimeLogger.Info(
                "Consumed material listType={ListType}, operation={Operation}, slot={Slot}, item={ItemId}, consumed={ConsumedCount}, remaining={RemainingCount}.",
                listType,
                requestedConsumption.Operation,
                requestedConsumption.Slot,
                requestedConsumption.ItemId,
                requestedConsumption.RequestedCount,
                session.Inventory.Main.TryGetValue(
                    requestedConsumption.Slot,
                    out var remainingItem)
                    ? remainingItem.CountOrValue
                    : 0);
        }

        var deleteItemReply =
            InventoryPacketBuilders.CreateDeleteItemReply(
                listType,
                completionEntries);
        await context.Queue.SendAsync(deleteItemReply, Name, cancellationToken);
    }
}
