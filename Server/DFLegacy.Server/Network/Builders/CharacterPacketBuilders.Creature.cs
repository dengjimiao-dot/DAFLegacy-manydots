using System.Text;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// Creature-space builders (07 §4.7): creature inventory packets, name
/// encoding and the equipped-creature appearance projection.
/// </summary>
public sealed partial class CharacterPacketBuilders
{
    internal IEnumerable<GameServerPacket> CreateCreatureInventoryPackets(
        IEnumerable<CharacterItemRecord> creatureInventory)
    {
        var items = creatureInventory
            .Where(item => CharacterCreatureInventoryLayout.IsInventorySlot(item.Slot)
                && item.ItemId != 0)
            .OrderBy(item => item.Slot)
            .ToArray();
        var creatures = items
            .Where(item => itemCatalog.TryGetDefinition(item.ItemId, out var definition)
                && CharacterCreatureInventoryLayout.IsCreature(definition))
            .GroupBy(item => item.CountOrValue)
            .Select(group => group.First())
            .Where(item => item.CountOrValue != 0)
            .Select(item => new GameCreatureInfoEntry(
                item.CountOrValue,
                item.CreatureStomach ?? 100,
                item.CreatureExperience ?? 0,
                item.CreatureLevel ?? 1,
                EncodeCreatureName(item.CreatureName, item.ItemId),
                item.CreatureNoCharge ?? false))
            .ToArray();
        yield return GameProtocolEngine.CreateCreatureInfo(creatures);
        yield return CreateCreatureInventoryItemListPacket(items);
    }

    internal GameServerPacket CreateCreatureInventoryItemListPacket(
        IEnumerable<CharacterItemRecord> creatureInventory)
    {
        var entries = creatureInventory
            .Where(item => CharacterCreatureInventoryLayout.IsInventorySlot(item.Slot)
                && item.ItemId != 0)
            .OrderBy(item => item.Slot)
            .Select(ToGameInventoryEntry)
            .ToArray();
        return GameProtocolEngine.CreateItemList(7, entries);
    }

    internal byte[] EncodeCreatureName(string? name, ushort itemId)
    {
        if (string.IsNullOrWhiteSpace(name)
            && itemCatalog.TryGetDefinition(itemId, out var definition))
        {
            name = definition.Name;
        }

        name = string.IsNullOrWhiteSpace(name) ? "Creature" : name;
        var output = new byte[29];
        ClientEncoding.GetEncoder().Convert(
            name.AsSpan(),
            output.AsSpan(),
            flush: true,
            out _,
            out var bytesUsed,
            out _);
        return output.AsSpan(0, bytesUsed).ToArray();
    }

    internal static byte GetEquippedCreatureLevel(
        IReadOnlyDictionary<ushort, CharacterItemRecord> creatureInventory) =>
        creatureInventory.TryGetValue(
                CharacterCreatureInventoryLayout.EquippedCreatureSlot,
                out var creature)
            ? creature.CreatureLevel ?? 1
            : (byte)0;

    internal GameCreatureAppearanceEntry? CreateEquippedCreatureAppearance(
        IEnumerable<CharacterItemRecord> creatureInventory)
    {
        var creature = creatureInventory.FirstOrDefault(item =>
            item.Slot == CharacterCreatureInventoryLayout.EquippedCreatureSlot
            && item.ItemId != 0);
        return creature is null
            ? null
            : new GameCreatureAppearanceEntry(
                creature.ItemId,
                EncodeCreatureName(creature.CreatureName, creature.ItemId),
                State: 1);
    }

    // ---- Mail attachment planning (migrated verbatim).
}
