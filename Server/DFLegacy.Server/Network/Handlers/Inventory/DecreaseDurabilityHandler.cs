using System.Buffers.Binary;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// DECREASE_DURABILITY (CMD 51): applies the client-reported durability decay (07 §5 group 9).
/// </summary>
public sealed class DecreaseDurabilityHandler(JsonGameStore store,
    ItemCatalog itemCatalog
    ) : IGameCommandHandler
{

    public ushort ProtocolId => (byte)CmdPacketType.DECREASE_DURABILITY;

    public string Name => "DecreaseDurability";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        if (session.Character.Active is null
            || session.Dungeon.CurrentDungeonId == 0
            || !InventoryParsers.TryReadDecreaseDurability(
                request.Body,
                out var equipmentSlot)
            || equipmentSlot is < 9 or >= 23
            || !session.Inventory.Equipped.TryGetValue(
                equipmentSlot,
                out var usedEquipment)
            || !itemCatalog.TryGetMaximumDurability(
                usedEquipment.ItemId,
                out _))
        {
            var error = ConnectionPacketBuilders.CreateCommandError(
                request,
                17);
            await context.Queue.SendAsync(error, Name, cancellationToken);
            return;
        }

        if (usedEquipment.Durability == 0)
        {
            var error = ConnectionPacketBuilders.CreateCommandError(
                request,
                22);
            await context.Queue.SendAsync(error, Name, cancellationToken);
            return;
        }

        var plannedEquipment = session.Inventory.Equipped.ToDictionary(
            pair => pair.Key,
            pair => pair.Value);
        plannedEquipment[equipmentSlot] = usedEquipment with
        {
            Durability = checked((ushort)(
                usedEquipment.Durability - 1))
        };
        var savedDurability =
            await store.SaveCharacterItemSpacesAsync(
                session.Login.AccountName,
                session.Character.Active.Id,
                session.Currency.Gold,
                session.Inventory.Main.Values,
                plannedEquipment.Values,
                session.Inventory.Warehouse.Values,
                cancellationToken,
                avatarInventory: session.Inventory.Avatar.Values,
                creatureInventory: session.Inventory.Creature.Values);
        if (savedDurability is null)
        {
            var error = ConnectionPacketBuilders.CreateCommandError(
                request,
                4);
            await context.Queue.SendAsync(error, Name, cancellationToken);
            return;
        }

        session.Character.Active = savedDurability;
        session.Inventory.Equipped = plannedEquipment;
        var durabilityReply =
            InventoryPacketBuilders.CreateDecreaseDurabilityReply(
                equipmentSlot);
        await context.Queue.SendAsync(durabilityReply, Name, cancellationToken);
        RuntimeLogger.Debug(
            "Decreased equipped item {ItemId} durability at slot {Slot}: {OldDurability}->{NewDurability}",
            usedEquipment.ItemId,
            equipmentSlot,
            usedEquipment.Durability,
            usedEquipment.Durability - 1);
    }
}
