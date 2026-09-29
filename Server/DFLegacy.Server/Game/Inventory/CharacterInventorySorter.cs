namespace DFLegacy.Server;

public static class CharacterInventorySorter
{
    private static readonly (ushort Start, ushort End, bool SortEquipment)[] MainSections =
    [
        (CharacterInventoryLayout.EquipmentSlotStart,
            CharacterInventoryLayout.EquipmentSlotEnd,
            true),
        (CharacterInventoryLayout.ConsumableSlotStart,
            CharacterInventoryLayout.ConsumableSlotEnd,
            false),
        (CharacterInventoryLayout.MaterialSlotStart,
            CharacterInventoryLayout.MaterialSlotEnd,
            false),
        (CharacterInventoryLayout.QuestSlotStart,
            CharacterInventoryLayout.QuestSlotEnd,
            false)
    ];

    public static Dictionary<ushort, CharacterItemRecord> SortMain(
        IReadOnlyDictionary<ushort, CharacterItemRecord> inventory,
        ItemCatalog catalog)
    {
        var arranged = inventory.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var section in MainSections)
        {
            var items = inventory
                .Where(pair => pair.Key >= section.Start && pair.Key < section.End)
                .Select(pair => (OldSlot: pair.Key, Item: pair.Value));
            var sorted = section.SortEquipment
                ? items.OrderBy(entry => GetRarity(entry.Item.ItemId, catalog))
                    .ThenBy(entry => entry.Item.ItemId)
                    .ThenBy(entry => entry.OldSlot)
                    .ToArray()
                : items.OrderBy(entry => entry.Item.ItemId)
                    .ThenBy(entry => entry.OldSlot)
                    .ToArray();
            for (var slot = (int)section.Start; slot < section.End; slot++)
            {
                arranged.Remove((ushort)slot);
            }

            for (var index = 0; index < sorted.Length; index++)
            {
                var targetSlot = checked((ushort)(section.Start + index));
                arranged[targetSlot] = sorted[index].Item with { Slot = targetSlot };
            }
        }

        return arranged;
    }

    public static Dictionary<ushort, CharacterItemRecord> SortWarehouse(
        IReadOnlyDictionary<ushort, CharacterItemRecord> warehouse,
        ItemCatalog catalog,
        ushort capacity)
    {
        var sorted = warehouse
            .Where(pair => pair.Key < capacity)
            .Select(pair => (OldSlot: pair.Key, Item: pair.Value))
            .OrderBy(entry => GetCategoryRank(entry.Item.ItemId, catalog))
            .ThenBy(entry => entry.Item.ItemId)
            .ThenBy(entry => entry.OldSlot)
            .ToArray();
        var arranged = new Dictionary<ushort, CharacterItemRecord>(sorted.Length);
        for (var index = 0; index < sorted.Length; index++)
        {
            var targetSlot = checked((ushort)index);
            arranged[targetSlot] = sorted[index].Item with { Slot = targetSlot };
        }

        return arranged;
    }

    private static int GetRarity(ushort itemId, ItemCatalog catalog) =>
        catalog.TryGetDefinition(itemId, out var definition)
            ? definition.Rarity ?? 9_999
            : 9_999;

    private static int GetCategoryRank(ushort itemId, ItemCatalog catalog) =>
        catalog.TryGetDefinition(itemId, out var definition)
            ? (int)definition.InventoryCategory
            : int.MaxValue;
}
