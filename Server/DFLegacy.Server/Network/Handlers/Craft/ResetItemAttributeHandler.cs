using System.Buffers.Binary;
using System.Text;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// RESET_ITEM_ATTRIBUTE (CMD 84): reseal/attribute reset for the equipment (07 §5 group 11).
/// </summary>
public sealed class ResetItemAttributeHandler(IGameStore store,
    ItemCatalog itemCatalog,
    ResealCatalog resealCatalog,
    CharacterPacketBuilders builders,
    ChannelSessionSenders senders) : IGameCommandHandler
{
    private static readonly Encoding ClientEncoding = PvfEncodings.Cp936Lossy();

    public ushort ProtocolId => (byte)CmdPacketType.RESET_ITEM_ATTR;

    public string Name => "ResetItemAttribute";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        if (session.Character.Active is null
            || session.Dungeon.CurrentDungeonId != 0
            || !CraftPacketBuilders.TryParseResetItemAttributeCommand(
                request.Body,
                out var resetCommand))
        {
            var error =
                CraftPacketBuilders.CreateResetItemAttributeFailure(
                    (byte)ItemAttributeModificationFailure.InvalidTarget);
            await context.Queue.SendAsync(error, Name, cancellationToken);
            return;
        }

        if (!ItemAttributeModificationPlanner.TryCreate(
                session.Inventory.Main,
                session.Inventory.Equipped,
                resetCommand.TargetSlot,
                resetCommand.TargetItemId,
                resetCommand.MaterialSlot,
                itemCatalog,
                resealCatalog,
                GameRandomSource.Shared,
                out var modificationPlan,
                out var modificationFailure))
        {
            var error =
                CraftPacketBuilders.CreateResetItemAttributeFailure(
                    (byte)modificationFailure);
            await context.Queue.SendAsync(error, Name, cancellationToken);
            RuntimeLogger.Warn(
                "Rejected item-attribute modification for item {ItemId} at slot {Slot} with material slot {MaterialSlot}: {Failure}.",
                resetCommand.TargetItemId,
                resetCommand.TargetSlot,
                resetCommand.MaterialSlot,
                modificationFailure);
            return;
        }

        var savedModification =
            await store.CommitItemAttributeModificationAsync(
                session.Login.AccountName,
                session.Character.Active.Id,
                session.Inventory.Main.Values,
                session.Inventory.Equipped.Values,
                modificationPlan.MainInventory.Values,
                modificationPlan.Equipment.Values,
                cancellationToken);
        if (savedModification is null)
        {
            var error =
                CraftPacketBuilders.CreateResetItemAttributeFailure(
                    (byte)ItemAttributeModificationFailure.InvalidTarget);
            await context.Queue.SendAsync(error, Name, cancellationToken);
            return;
        }

        var warehouseCapacityChanged =
            senders.AdoptSavedWarehouseUpgradeState(context, savedModification);
        session.Inventory.Equipped = (savedModification.Equipment ?? [])
            .ToDictionary(item => item.Slot);
        if (warehouseCapacityChanged)
        {
            await senders.SendWarehouseStateAsync(context, cancellationToken);
        }

        var modificationReply =
            CraftPacketBuilders.CreateResetItemAttributeReply(
                modificationPlan.MaterialSlot,
                modificationPlan.MaterialRemaining,
                (ushort)modificationPlan.Operation);
        await context.Queue.SendAsync(modificationReply, Name, cancellationToken);

        var adjustedItem = modificationPlan.TargetListType == 3
            ? session.Inventory.Equipped[modificationPlan.TargetSlot]
            : session.Inventory.Main[modificationPlan.TargetSlot];
        var targetUpdate =
            InventoryPacketBuilders.CreateUpdateItemList(
                modificationPlan.TargetListType,
                [builders.ToGameInventoryEntry(adjustedItem)]);
        await context.Queue.SendAsync(targetUpdate, Name, cancellationToken);

        RuntimeLogger.Info(
            "Completed item-attribute operation {Operation} for item {ItemId} at {ListType}:{Slot}: material={MaterialItemId}x{MaterialConsumed}, remaining={MaterialRemaining}, quality={PreviousSeed}->{NewSeed}, reseals={PreviousResealCount}->{NewResealCount}.",
            modificationPlan.Operation,
            modificationPlan.TargetItemId,
            modificationPlan.TargetListType,
            modificationPlan.TargetSlot,
            modificationPlan.MaterialItemId,
            modificationPlan.MaterialConsumed,
            modificationPlan.MaterialRemaining,
            modificationPlan.PreviousQualitySeed,
            modificationPlan.NewQualitySeed,
            modificationPlan.PreviousResealCount,
            modificationPlan.NewResealCount);
    }
}
