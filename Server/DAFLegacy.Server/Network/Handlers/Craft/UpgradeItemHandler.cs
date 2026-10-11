using System.Buffers.Binary;
using System.Text;
using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

/// <summary>
/// UPGRADE_ITEM (CMD 83): equipment reinforcement with the quality-adjustment policy (07 §5 group 11).
/// </summary>
public sealed class UpgradeItemHandler(IGameStore store,
    ItemCatalog itemCatalog,
    EquipmentReinforcementCatalog reinforcementCatalog,
    CharacterPacketBuilders builders,
    ChannelSessionSenders senders) : IGameCommandHandler
{
    private static readonly Encoding ClientEncoding = PvfEncodings.Cp936Lossy();

    public ushort ProtocolId => (byte)CmdPacketType.UPGRADE_ITEM;

    public string Name => "UpgradeItem";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        if (session.Character.Active is null
            || session.Dungeon.CurrentDungeonId != 0
            || !CraftPacketBuilders.TryParseUpgradeItemCommand(
                request.Body,
                out var upgradeCommand))
        {
            var error = ConnectionPacketBuilders.CreateCommandError(
                request,
                (byte)EquipmentReinforcementFailure.InvalidTarget);
            await context.Queue.SendAsync(error, Name, cancellationToken);
            return;
        }

        if (!EquipmentReinforcementPlanner.TryCreate(
                session.Inventory.Main,
                session.Inventory.Equipped,
                session.Currency.Gold,
                upgradeCommand.TargetSlot,
                upgradeCommand.TargetItemId,
                upgradeCommand.MaterialSlot,
                senders.GetInventoryWeightLimit(context, session.Character.Active),
                itemCatalog,
                reinforcementCatalog,
                GameRandomSource.Shared,
                out var reinforcementPlan,
                out var reinforcementFailure))
        {
            var error = ConnectionPacketBuilders.CreateCommandError(
                request,
                (byte)reinforcementFailure);
            await context.Queue.SendAsync(error, Name, cancellationToken);
            RuntimeLogger.Warn(
                "Rejected equipment reinforcement for item {ItemId} at slot {Slot} with material slot {MaterialSlot}: {Failure}.",
                upgradeCommand.TargetItemId,
                upgradeCommand.TargetSlot,
                upgradeCommand.MaterialSlot,
                reinforcementFailure);
            return;
        }

        var savedReinforcement =
            await store.SaveCharacterInventoryAsync(
                session.Login.AccountName,
                session.Character.Active.Id,
                reinforcementPlan.RemainingGold,
                reinforcementPlan.MainInventory.Values,
                reinforcementPlan.Equipment.Values,
                cancellationToken);
        if (savedReinforcement is null)
        {
            var error = ConnectionPacketBuilders.CreateCommandError(
                request,
                (byte)EquipmentReinforcementFailure.InvalidTarget);
            await context.Queue.SendAsync(error, Name, cancellationToken);
            return;
        }

        var warehouseCapacityChanged =
            senders.AdoptSavedWarehouseUpgradeState(context, savedReinforcement);
        session.Inventory.Equipped = (savedReinforcement.Equipment ?? [])
            .ToDictionary(item => item.Slot);
        session.Currency.Gold = savedReinforcement.Gold;
        if (warehouseCapacityChanged)
        {
            await senders.SendWarehouseStateAsync(context, cancellationToken);
        }

        var reinforcementReply =
            CraftPacketBuilders.CreateUpgradeItemReply(
                reinforcementPlan.MaterialSlot,
                reinforcementPlan.MaterialRemaining,
                reinforcementPlan.ResultCode,
                reinforcementPlan.NewLevel,
                reinforcementPlan.TargetSlot,
                reinforcementPlan.DestroyRewards
                    .Select(reward => new GameUpgradeItemDestroyReward(
                        reward.Slot,
                        reward.ItemId,
                        reward.Count))
                    .ToArray());
        if (reinforcementPlan.Destroyed)
        {
            // 60CN 客户端消费损坏应答时要求目标物品仍在槽上（sub_40F290
            // case 0x53 @0x41624a 查不到即报 1658「缺少物品」）：由应答自身
            // 移除目标、弹出损坏提示并写入补偿物品，快照随后对账金币与
            // 材料余量。
            await context.Queue.SendAsync(reinforcementReply, Name, cancellationToken);
            if (reinforcementPlan.TargetListType == 0)
            {
                var destroyedInventoryRefresh = builders.CreateMainInventoryPacket(
                    session.Currency.Gold,
                    session.Currency.VictoryPoints,
                    session.Inventory.Main.Values);
                await context.Queue.SendAsync(destroyedInventoryRefresh, Name, cancellationToken);
            }
        }
        else
        {
            // DF2008 refreshes a bag target before consuming the
            // CMD83 acknowledgement. Worn targets are updated by
            // the acknowledgement itself.
            if (reinforcementPlan.TargetListType == 0)
            {
                var inventoryRefresh = builders.CreateMainInventoryPacket(
                    session.Currency.Gold,
                    session.Currency.VictoryPoints,
                    session.Inventory.Main.Values,
                    preferredLastSlot: reinforcementPlan.TargetSlot);
                await context.Queue.SendAsync(inventoryRefresh, Name, cancellationToken);
            }

            await context.Queue.SendAsync(reinforcementReply, Name, cancellationToken);
        }
        RuntimeLogger.Info(
            "Reinforced item {ItemId} at {ListType}:{Slot}: level={PreviousLevel}->{NewLevel}, result={ResultCode}, destroyed={Destroyed}, material={MaterialItemId}x{MaterialCount}, goldCost={GoldCost}, gold={Gold}, roll={Roll}/{RollUpperBound}, destroyRewards={DestroyRewards}.",
            reinforcementPlan.TargetItemId,
            reinforcementPlan.TargetListType,
            reinforcementPlan.TargetSlot,
            reinforcementPlan.PreviousLevel,
            reinforcementPlan.NewLevel,
            reinforcementPlan.ResultCode,
            reinforcementPlan.Destroyed,
            reinforcementPlan.Cost.MaterialItemId,
            reinforcementPlan.Cost.MaterialCount,
            reinforcementPlan.Cost.Gold,
            session.Currency.Gold,
            reinforcementPlan.FailureRoll,
            EquipmentReinforcementCatalog.FailureRollUpperBound,
            reinforcementPlan.DestroyRewards.Count != 0
                ? string.Join(
                    "+",
                    reinforcementPlan.DestroyRewards.Select(reward =>
                        $"{reward.ItemId}x{reward.Count}@{reward.Slot}"))
                : "none");
    }
}
