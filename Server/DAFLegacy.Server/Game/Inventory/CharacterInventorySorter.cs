namespace DAFLegacy.Server;

public static class CharacterInventorySorter
{
    /// <summary>
    /// 整理范围：全部分区。用于客户端未随请求上送页签（旧三字节形态）或该
    /// 客户端无页签概念的场合；其余不在 0..3 的取值同样按全部分区处理。
    /// </summary>
    public const byte SortAllSections = 0xFF;

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
        ItemCatalog catalog,
        byte section = SortAllSections)
    {
        var arranged = inventory.ToDictionary(pair => pair.Key, pair => pair.Value);
        for (var sectionIndex = 0; sectionIndex < MainSections.Length; sectionIndex++)
        {
            if (section < MainSections.Length && section != sectionIndex)
            {
                continue;
            }

            var current = MainSections[sectionIndex];
            var items = inventory
                .Where(pair => pair.Key >= current.Start && pair.Key < current.End)
                .Select(pair => (OldSlot: pair.Key, Item: pair.Value));
            var sorted = current.SortEquipment
                ? items.OrderBy(entry => GetRarity(entry.Item.ItemId, catalog))
                    .ThenBy(entry => entry.Item.ItemId)
                    .ThenBy(entry => entry.OldSlot)
                    .ToArray()
                : items.OrderBy(entry => entry.Item.ItemId)
                    .ThenBy(entry => entry.OldSlot)
                    .ToArray();
            for (var slot = (int)current.Start; slot < current.End; slot++)
            {
                arranged.Remove((ushort)slot);
            }

            for (var index = 0; index < sorted.Length; index++)
            {
                var targetSlot = checked((ushort)(current.Start + index));
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
