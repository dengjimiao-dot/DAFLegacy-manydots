using System.Buffers.Binary;
using System.Text;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// REPAIR_EQUIPMENT (CMD 25): repair quote and settlement for worn equipment (07 §5 group 10).
/// </summary>
public sealed class RepairEquipmentHandler(IGameStore store,
    ItemCatalog itemCatalog
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

        var plannedMain = session.Inventory.Main.ToDictionary(
            pair => pair.Key,
            pair => pair.Value);
        var plannedEquipment = session.Inventory.Equipped.ToDictionary(
            pair => pair.Key,
            pair => pair.Value);
        var repairTargets = new List<(
            Dictionary<ushort, CharacterItemRecord> Space,
            CharacterItemRecord Item,
            ushort MaximumDurability,
            int Price)>();
        var inDungeonRepair = session.Dungeon.CurrentDungeonId != 0;

        void AddRepairTarget(
            Dictionary<ushort, CharacterItemRecord> space,
            CharacterItemRecord item)
        {
            if (!itemCatalog.TryGetMaximumDurability(
                    item.ItemId,
                    out var maximumDurability)
                || item.Durability >= maximumDurability
                || !itemCatalog.TryCalculateRepairPrice(
                    item.ItemId,
                    item.Durability,
                    inDungeonRepair,
                    out var repairPrice))
            {
                return;
            }

            repairTargets.Add((
                space,
                item,
                maximumDurability,
                repairPrice));
        }

        if (repairSlot == ushort.MaxValue)
        {
            foreach (var item in plannedEquipment.Values)
            {
                AddRepairTarget(plannedEquipment, item);
            }

            foreach (var item in plannedMain.Values)
            {
                AddRepairTarget(plannedMain, item);
            }
        }
        else
        {
            var selectedSpace = itemSpace == 0
                ? plannedMain
                : plannedEquipment;
            if (!selectedSpace.TryGetValue(
                    repairSlot,
                    out var selectedItem)
                || !itemCatalog.TryGetMaximumDurability(
                    selectedItem.ItemId,
                    out var maximumDurability)
                || selectedItem.Durability >= maximumDurability
                || !itemCatalog.TryCalculateRepairPrice(
                    selectedItem.ItemId,
                    selectedItem.Durability,
                    inDungeonRepair,
                    out var repairPrice))
            {
                var error = ConnectionPacketBuilders.CreateCommandError(
                    request,
                    0x11);
                await context.Queue.SendAsync(error, Name, cancellationToken);
                return;
            }

            repairTargets.Add((
                selectedSpace,
                selectedItem,
                maximumDurability,
                repairPrice));
        }

        var totalRepairPrice = repairTargets.Sum(
            target => (long)target.Price);
        if (totalRepairPrice > session.Currency.Gold)
        {
            var error = ConnectionPacketBuilders.CreateCommandError(
                request,
                10);
            await context.Queue.SendAsync(error, Name, cancellationToken);
            return;
        }

        foreach (var target in repairTargets)
        {
            target.Space[target.Item.Slot] = target.Item with
            {
                Durability = target.MaximumDurability
            };
        }

        var nextGold = checked(session.Currency.Gold - (int)totalRepairPrice);
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
        RuntimeLogger.Info(
            "Repaired {RepairCount} equipment item(s) at {ItemSpace}:{Slot}; cost={Cost}, gold={Gold}, dungeonRate={DungeonRate}",
            repairTargets.Count,
            itemSpace,
            repairSlot,
            totalRepairPrice,
            session.Currency.Gold,
            inDungeonRepair);
    }
}
