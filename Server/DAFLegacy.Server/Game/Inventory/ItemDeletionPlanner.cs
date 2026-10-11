using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

public readonly record struct ItemDeletionRequestEntry(
    ushort Operation,
    ushort Slot,
    ushort ItemId,
    uint RequestedCount);

public sealed record ItemDeletionPlan(
    Dictionary<ushort, CharacterItemRecord> Inventory,
    IReadOnlyList<GameItemConsumptionEntry> CompletionEntries);

public static class ItemDeletionPlanner
{
    public static bool TryCreate(
        IReadOnlyDictionary<ushort, CharacterItemRecord> inventory,
        byte itemSpace,
        IReadOnlyList<ItemDeletionRequestEntry> entries,
        ItemCatalog catalog,
        out ItemDeletionPlan plan)
    {
        plan = null!;
        if (itemSpace is not (0 or 7) || entries.Count is 0 or > 32)
        {
            return false;
        }

        var consumedBySlot = new Dictionary<ushort, ulong>();
        foreach (var entry in entries)
        {
            if (!inventory.TryGetValue(entry.Slot, out var item)
                || entry.ItemId != item.ItemId || entry.RequestedCount == 0
                || !catalog.TryGetDefinition(item.ItemId, out var definition))
            {
                return false;
            }

            if (itemSpace == 0 && definition.IsEquipment
                || itemSpace == 7
                    && (!CharacterCreatureInventoryLayout.IsArtifact(definition)
                        || entry.Slot < CharacterCreatureInventoryLayout.ArtifactBagStart
                        || entry.Slot >= CharacterCreatureInventoryLayout.ArtifactBagEnd
                        || item.CountOrValue != 1 || entry.RequestedCount != 1))
            {
                return false;
            }

            var consumed = consumedBySlot.GetValueOrDefault(entry.Slot) + entry.RequestedCount;
            if (consumed > item.CountOrValue)
            {
                return false;
            }

            consumedBySlot[entry.Slot] = consumed;
        }

        var planned = inventory.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var (slot, consumed) in consumedBySlot)
        {
            var item = planned[slot];
            var remaining = item.CountOrValue - checked((uint)consumed);
            if (remaining == 0)
            {
                planned.Remove(slot);
            }
            else
            {
                planned[slot] = item with { CountOrValue = remaining };
            }
        }

        plan = new ItemDeletionPlan(planned,
            entries.Select(entry => new GameItemConsumptionEntry(entry.Slot, entry.RequestedCount)).ToArray());
        return true;
    }
}
