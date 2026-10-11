using System.Buffers.Binary;
using System.Text;
using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

/// <summary>
/// REPAIR_EQUIPMENT (CMD 25): repair quote and settlement for worn equipment (07 §5 group 10).
/// </summary>
public sealed class RepairEquipmentHandler(IGameStore store,
    ItemCatalog itemCatalog,
    CharacterPacketBuilders builders
    ) : IGameCommandHandler
{

    private static readonly Encoding ClientEncoding = PvfEncodings.Cp936Lossy();

    public ushort ProtocolId => (byte)CmdPacketType.REPAIR_EQUIPMENT;

    public string Name => "RepairEquipment";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        if (session.Character.Active is null
            || !ShopParsers.TryReadRepairEquipment(
                request.Body,
                out var itemSpace,
                out var repairSlot))
        {
            var error = ConnectionPacketBuilders.CreateCommandError(
                request,
                0x11);
            await context.Queue.SendAsync(error, Name, cancellationToken);
            return;
        }

        var inDungeonRepair = session.Dungeon.CurrentDungeonId != 0;
        if (!EquipmentRepairPlanner.TryCreate(
                session.Inventory.Main,
                session.Inventory.Equipped,
                session.Currency.Gold,
                itemSpace,
                repairSlot,
                inDungeonRepair,
                itemCatalog,
                out var repairPlan,
                out var repairFailure))
        {
            var error = ConnectionPacketBuilders.CreateCommandError(
                request,
                (byte)repairFailure);
            await context.Queue.SendAsync(error, Name, cancellationToken);
            return;
        }

        var plannedMain = repairPlan.MainInventory;
        var plannedEquipment = repairPlan.Equipment;
        var nextGold = repairPlan.RemainingGold;
        var savedRepair = await store.SaveCharacterItemSpacesAsync(
            session.Login.AccountName,
            session.Character.Active.Id,
            nextGold,
            plannedMain.Values,
            plannedEquipment.Values,
            session.Inventory.Warehouse.Values,
            cancellationToken,
            avatarInventory: session.Inventory.Avatar.Values,
            creatureInventory: session.Inventory.Creature.Values);
        if (savedRepair is null)
        {
            var error = ConnectionPacketBuilders.CreateCommandError(
                request,
                4);
            await context.Queue.SendAsync(error, Name, cancellationToken);
            return;
        }

        session.Character.Active = savedRepair;
        session.Currency.Gold = nextGold;
        session.Inventory.Main = plannedMain;
        session.Inventory.Equipped = plannedEquipment;
        var repairReply =
            ShopPacketBuilders.CreateRepairEquipmentReply(
                checked((uint)session.Currency.Gold),
                itemSpace,
                repairSlot);
        await context.Queue.SendAsync(repairReply, Name, cancellationToken);
        foreach (var group in repairPlan.SynchronizationItems.GroupBy(item => item.ItemSpace))
        {
            var durabilityUpdate = InventoryPacketBuilders.CreateUpdateItemList(group.Key,
                group.Select(item => builders.ToGameInventoryEntry(item.Item)).ToArray());
            await context.Queue.SendAsync(durabilityUpdate, Name, cancellationToken);
        }

        RuntimeLogger.Info(
            "Repaired {RepairCount} equipment item(s) at {ItemSpace}:{Slot}; cost={Cost}, gold={Gold}, dungeonRate={DungeonRate}",
            repairPlan.RepairedItems.Count,
            itemSpace,
            repairSlot,
            repairPlan.Price,
            session.Currency.Gold,
            inDungeonRepair);
    }
}
