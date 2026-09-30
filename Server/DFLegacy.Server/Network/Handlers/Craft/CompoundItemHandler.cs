using System.Buffers.Binary;
using System.Text;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// COMPOUND_ITEM (CMD 27): item compounding with the request-replay cache (07 §5 group 11).
/// </summary>
public sealed class CompoundItemHandler(IGameStore store,
    ItemCatalog itemCatalog,
    CompoundCatalog compoundCatalog,
    CharacterPacketBuilders builders,
    ChannelSessionSenders senders) : IGameCommandHandler
{
    private const int CompoundReplayCapacity = 256;

    private static readonly Encoding ClientEncoding = PvfEncodings.Cp936Lossy();

    public ushort ProtocolId => (byte)CmdPacketType.COMPOUND_ITEM;

    public string Name => "CompoundItem";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        if (session.Character.Active is null
            || session.Dungeon.CurrentDungeonId != 0
            || !CraftPacketBuilders.TryParseCompoundItemCommand(
                request.Body,
                out var compoundItemCommand))
        {
            var error = CraftPacketBuilders.CreateCompoundItemFailure(
                (byte)CompoundItemFailure.InvalidRecipe);
            await context.Queue.SendAsync(error, Name, cancellationToken);
            return;
        }

        if (session.Inventory.CompoundReplay.TryGetValue(
                compoundItemCommand.Sequence,
                out var replay))
        {
            if (!request.Body.AsSpan().SequenceEqual(replay.RequestBody))
            {
                var conflict =
                    CraftPacketBuilders.CreateCompoundItemFailure(
                        (byte)CompoundItemFailure.SequenceConflict);
                await context.Queue.SendAsync(conflict, Name, cancellationToken);
                RuntimeLogger.Warn(
                    "Rejected compound sequence {Sequence} reused by a different request.",
                    compoundItemCommand.Sequence);
                return;
            }

            await context.Queue.SendAsync(replay.Reply, Name, cancellationToken);
            var replayRefresh = builders.CreateMainInventoryPacket(
                session.Currency.Gold,
                session.Currency.VictoryPoints,
                session.Inventory.Main.Values);
            await context.Queue.SendAsync(replayRefresh, Name, cancellationToken);
            if (replay.RefreshWarehouse)
            {
                await senders.SendWarehouseStateAsync(context, cancellationToken);
            }

            RuntimeLogger.Info(
                "Replayed compound acknowledgement for duplicate sequence {Sequence}.",
                compoundItemCommand.Sequence);
            return;
        }

        var expectedGold = session.Currency.Gold;
        var expectedInventory = session.Inventory.Main.Values
            .OrderBy(item => item.Slot)
            .ToArray();
        if (!CompoundItemPlanner.TryCreate(
                session.Inventory.Main,
                session.Currency.Gold,
                session.Skill.Current,
                compoundItemCommand.SourceValue,
                compoundItemCommand.SourceIsItemId,
                compoundItemCommand.CraftCount,
                senders.GetInventoryWeightLimit(context, session.Character.Active),
                compoundCatalog,
                itemCatalog,
                out var compoundItemPlan,
                out var compoundItemFailure))
        {
            var error = CraftPacketBuilders.CreateCompoundItemFailure(
                (byte)compoundItemFailure);
            await context.Queue.SendAsync(error, Name, cancellationToken);
            RuntimeLogger.Warn(
                "Rejected compound recipe source={SourceType}:{SourceValue}, count={CraftCount}: {Failure}.",
                compoundItemCommand.SourceIsItemId ? "item" : "slot",
                compoundItemCommand.SourceValue,
                compoundItemCommand.CraftCount,
                compoundItemFailure);
            return;
        }

        var savedCompoundItem = await store.CommitCompoundItemAsync(
            session.Login.AccountName,
            session.Character.Active.Id,
            expectedGold,
            expectedInventory,
            compoundItemPlan.RemainingGold,
            compoundItemPlan.MainInventory.Values,
            cancellationToken);
        if (savedCompoundItem is null)
        {
            var error = CraftPacketBuilders.CreateCompoundItemFailure(
                (byte)CompoundItemFailure.InvalidRecipe);
            await context.Queue.SendAsync(error, Name, cancellationToken);
            RuntimeLogger.Warn(
                "Rejected stale compound commit for sequence {Sequence}, recipe {RecipeItemId}.",
                compoundItemCommand.Sequence,
                compoundItemPlan.RecipeItemId);
            return;
        }

        var warehouseCapacityChanged =
            senders.AdoptSavedWarehouseUpgradeState(context, savedCompoundItem);
        session.Currency.Gold = savedCompoundItem.Gold;
        var hasWarehouseUpgradeResult =
            compoundItemPlan.CreatedItems.Any(item =>
                CharacterWarehouseProgression
                    .TryGetItemTargetCapacity(item.ItemId, out _));
        var refreshWarehouse = warehouseCapacityChanged
            || hasWarehouseUpgradeResult;
        var compoundItemReply =
            CraftPacketBuilders.CreateCompoundItemReply(
                compoundItemPlan.ConsumedItems.Select(item =>
                    new GameCompoundItemConsumption(
                        item.ListType,
                        item.Slot,
                        item.RemainingCount)).ToArray(),
                compoundItemPlan.CreatedItems.Select(item =>
                    new GameCompoundItemResult(
                        item.Slot,
                        item.ItemId,
                        item.CountOrValue,
                        item.State,
                        item.Durability)).ToArray());
        while (session.Inventory.CompoundReplay.Count >= CompoundReplayCapacity
            && session.Inventory.CompoundReplayOrder.TryDequeue(out var oldestSequence))
        {
            session.Inventory.CompoundReplay.Remove(oldestSequence);
        }

        session.Inventory.CompoundReplay.Add(
            compoundItemCommand.Sequence,
            new CompoundItemReplayEntry(
                request.Body.ToArray(),
                compoundItemReply,
                refreshWarehouse));
        session.Inventory.CompoundReplayOrder.Enqueue(compoundItemCommand.Sequence);
        await context.Queue.SendAsync(compoundItemReply, Name, cancellationToken);
        // DF2008 does not reliably apply the CMD27 delta when
        // a consumed recipe slot is immediately reused by the
        // crafted result. Always follow the acknowledgement
        // with the authoritative main-inventory snapshot.
        var inventoryRefresh = builders.CreateMainInventoryPacket(
            session.Currency.Gold,
            session.Currency.VictoryPoints,
            session.Inventory.Main.Values);
        await context.Queue.SendAsync(inventoryRefresh, Name, cancellationToken);

        if (refreshWarehouse)
        {
            await senders.SendWarehouseStateAsync(context, cancellationToken);
        }

        RuntimeLogger.Info(
            "Compounded recipe {RecipeItemId} from {SourceType}:{SourceValue}: count={CraftCount}, recipeType={RecipeType}, goldCost={GoldCost}, consumedSlots={ConsumedCount}, createdSlots={CreatedCount}, gold={Gold}.",
            compoundItemPlan.RecipeItemId,
            compoundItemCommand.SourceIsItemId ? "item" : "slot",
            compoundItemCommand.SourceValue,
            compoundItemPlan.CraftCount,
            compoundItemPlan.RecipeType,
            compoundItemPlan.GoldCost,
            compoundItemPlan.ConsumedItems.Count,
            compoundItemPlan.CreatedItems.Count,
            session.Currency.Gold);
    }
}
