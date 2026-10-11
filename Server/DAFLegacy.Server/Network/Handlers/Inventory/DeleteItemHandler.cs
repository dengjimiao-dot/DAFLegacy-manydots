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
                out var requestedConsumptions))
        {
            // DF2008 0x00410CEF reads the failed request's
            // list byte before releasing its pending operation.
            var parseError = InventoryPacketBuilders.CreateDeleteItemError(
                request.Body.Length > 2 ? request.Body[2] : (byte)0,
                errorCode: 0x11);
            await context.Queue.SendAsync(parseError, Name, cancellationToken);
            RuntimeLogger.Warn(
                "Rejected consume-items transaction session.Character.Active={HasCharacter}, body={Body}.",
                session.Character.Active is not null,
                Convert.ToHexString(request.Body));
            return;
        }

        var sourceInventory = listType == 7
            ? session.Inventory.Creature
            : session.Inventory.Main;
        if (!ItemDeletionPlanner.TryCreate(
                sourceInventory,
                listType,
                requestedConsumptions,
                itemCatalog,
                out var deletionPlan))
        {
            var deleteItemError = InventoryPacketBuilders.CreateDeleteItemError(
                listType,
                errorCode: 0x11);
            await context.Queue.SendAsync(deleteItemError, Name, cancellationToken);
            RuntimeLogger.Warn(
                "Rejected item deletion listType={ListType}, body={Body}.",
                listType,
                Convert.ToHexString(request.Body));
            return;
        }

        var plannedMainInventory = listType == 0
            ? deletionPlan.Inventory
            : session.Inventory.Main;
        var plannedCreatureInventory = listType == 7
            ? deletionPlan.Inventory
            : session.Inventory.Creature;
        var savedDeletion = await store.SaveCharacterItemSpacesAsync(
            session.Login.AccountName,
            session.Character.Active.Id,
            session.Currency.Gold,
            plannedMainInventory.Values,
            session.Inventory.Equipped.Values,
            session.Inventory.Warehouse.Values,
            cancellationToken,
            avatarInventory: session.Inventory.Avatar.Values,
            creatureInventory: plannedCreatureInventory.Values);
        if (savedDeletion is null)
        {
            var saveError = InventoryPacketBuilders.CreateDeleteItemError(
                listType,
                errorCode: 0x11);
            await context.Queue.SendAsync(saveError, Name, cancellationToken);
            return;
        }

        session.Character.Active = savedDeletion;
        session.Inventory.Main = plannedMainInventory;
        session.Inventory.Creature = plannedCreatureInventory;

        foreach (var requestedConsumption in requestedConsumptions)
        {
            RuntimeLogger.Info(
                "Deleted/consumed item listType={ListType}, operation={Operation}, slot={Slot}, item={ItemId}, consumed={ConsumedCount}, remaining={RemainingCount}.",
                listType,
                requestedConsumption.Operation,
                requestedConsumption.Slot,
                requestedConsumption.ItemId,
                requestedConsumption.RequestedCount,
                deletionPlan.Inventory.TryGetValue(
                    requestedConsumption.Slot,
                    out var remainingItem)
                    ? remainingItem.CountOrValue
                    : 0);
        }

        var deleteItemReply =
            InventoryPacketBuilders.CreateDeleteItemReply(
                listType,
                deletionPlan.CompletionEntries);
        await context.Queue.SendAsync(deleteItemReply, Name, cancellationToken);
    }
}
