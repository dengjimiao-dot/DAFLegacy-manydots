using System.Buffers.Binary;
using System.Text;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// DISJOINT_ITEM (CMD 28): item disassembly into the disjoint table (07 §5 group 11).
/// </summary>
public sealed class DisjointItemHandler(JsonGameStore store,
    ItemCatalog itemCatalog,
    DisjointCatalog disjointCatalog,
    CharacterPacketBuilders builders,
    ChannelSessionSenders senders) : IGameCommandHandler
{
    private static readonly Encoding ClientEncoding = PvfEncodings.Cp936Lossy();

    public ushort ProtocolId => (byte)CmdPacketType.DISJOINT_ITEM;

    public string Name => "DisjointItem";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        if (session.Character.Active is null || request.Body.Length < 5)
        {
            var error = ConnectionPacketBuilders.CreateCommandError(
                request,
                errorCode: 17);
            await context.Queue.SendAsync(error, Name, cancellationToken);
            return;
        }

        var sourceSlot = BinaryPrimitives.ReadUInt16LittleEndian(
            request.Body.AsSpan(2, sizeof(ushort)));
        var itemSpace = request.Body[4];
        var additionalItemRoll = GameRandomSource.Shared.Next(int.MaxValue);
        var jackpotRoll = GameRandomSource.Shared.Next(
            DisjointCatalog.JackpotRollUpperBound);
        if (!DisjointItemPlanner.TryCreate(
                session.Inventory.Main,
                sourceSlot,
                itemSpace,
                senders.GetInventoryWeightLimit(context, session.Character.Active),
                additionalItemRoll,
                jackpotRoll,
                itemCatalog,
                disjointCatalog,
                out var disjointPlan,
                out var disjointFailure))
        {
            var errorCode = disjointFailure
                == DisjointItemFailure.InventoryFull
                    ? (byte)4
                    : (byte)17;
            var error = ConnectionPacketBuilders.CreateCommandError(
                request,
                errorCode);
            await context.Queue.SendAsync(error, Name, cancellationToken);
            RuntimeLogger.Warn(
                "Rejected equipment disjoint at slot {SourceSlot}, itemSpace={ItemSpace}: {Failure}.",
                sourceSlot,
                itemSpace,
                disjointFailure);
            return;
        }

        var disjointWarehouseSettlement =
            senders.PlanWarehouseUpgradeSettlement(context,
                disjointPlan.MainInventory.Values,
                session.Inventory.Warehouse.Values,
                disjointPlan.ResponseRewards.Select(reward =>
                    reward.ItemId));
        var hasDisjointWarehouseUpgrade =
            disjointPlan.ResponseRewards.Any(reward =>
                CharacterWarehouseProgression
                    .TryGetItemTargetCapacity(reward.ItemId, out _));
        var savedDisjoint = await store.SaveCharacterItemSpacesAsync(
            session.Login.AccountName,
            session.Character.Active.Id,
            session.Currency.Gold,
            disjointWarehouseSettlement.Inventory,
            session.Inventory.Equipped.Values,
            disjointWarehouseSettlement.Warehouse,
            cancellationToken,
            avatarInventory: session.Inventory.Avatar.Values,
            creatureInventory: session.Inventory.Creature.Values,
            warehouseCapacity:
                disjointWarehouseSettlement.Capacity,
            expectedWarehouseCapacity: session.Inventory.WarehouseCapacity);
        if (savedDisjoint is null)
        {
            var error = ConnectionPacketBuilders.CreateCommandError(
                request,
                errorCode: 17);
            await context.Queue.SendAsync(error, Name, cancellationToken);
            return;
        }

        var warehouseCapacityChanged =
            senders.AdoptSavedWarehouseUpgradeState(context, savedDisjoint);
        var disjointReply = CraftPacketBuilders.CreateDisjointItemReply(
            disjointPlan.SourceSlot,
            disjointPlan.ItemSpace,
            disjointPlan.ResponseRewards);
        await context.Queue.SendAsync(disjointReply, Name, cancellationToken);
        if (warehouseCapacityChanged || hasDisjointWarehouseUpgrade)
        {
            var inventoryRefresh = builders.CreateMainInventoryPacket(
                session.Currency.Gold,
                session.Currency.VictoryPoints,
                session.Inventory.Main.Values);
            await context.Queue.SendAsync(inventoryRefresh, Name, cancellationToken);
            await senders.SendWarehouseStateAsync(context, cancellationToken);
        }
        RuntimeLogger.Info(
            "Disjointed item {SourceItemId} at slot {SourceSlot}; additionalRoll={AdditionalRoll}, jackpotRoll={JackpotRoll}, rewards={Rewards}.",
            disjointPlan.SourceItemId,
            sourceSlot,
            additionalItemRoll,
            jackpotRoll,
            string.Join(
                ", ",
                disjointPlan.ResponseRewards.Select(reward =>
                    $"{reward.ItemId}x{reward.Count}@{reward.DestinationSlot}")));
    }
}
