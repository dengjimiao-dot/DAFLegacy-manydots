namespace DAFLegacy.Server;

public enum EquipmentRepairFailure : byte
{
    None,
    InvalidTarget = 0x11,
    InsufficientGold = 0x0A
}

public sealed record EquipmentRepairUpdate(byte ItemSpace, CharacterItemRecord Item);

public sealed record EquipmentRepairPlan(
    Dictionary<ushort, CharacterItemRecord> MainInventory,
    Dictionary<ushort, CharacterItemRecord> Equipment,
    int RemainingGold,
    int Price,
    IReadOnlyList<EquipmentRepairUpdate> RepairedItems,
    IReadOnlyList<EquipmentRepairUpdate> SynchronizationItems);

public static class EquipmentRepairPlanner
{
    public static bool TryCreate(
        IReadOnlyDictionary<ushort, CharacterItemRecord> mainInventory,
        IReadOnlyDictionary<ushort, CharacterItemRecord> equipment,
        int currentGold,
        byte itemSpace,
        ushort slot,
        bool inDungeon,
        ItemCatalog catalog,
        out EquipmentRepairPlan plan,
        out EquipmentRepairFailure failure)
    {
        plan = null!;
        failure = EquipmentRepairFailure.InvalidTarget;
        if (currentGold < 0 || itemSpace is not (0 or 3))
        {
            return false;
        }

        var main = mainInventory.ToDictionary();
        var worn = equipment.ToDictionary();
        var targets = new List<(byte Space, CharacterItemRecord Item, ushort Maximum, int Price)>();

        bool AddTarget(byte space, CharacterItemRecord item, bool skipFull)
        {
            if (!catalog.TryGetMaximumDurability(item.ItemId, out var maximum)
                || skipFull && item.Durability >= maximum)
            {
                return false;
            }

            var price = 0;
            if (item.Durability < maximum
                && !catalog.TryCalculateRepairPrice(item.ItemId, item.Durability, inDungeon, out price))
            {
                return false;
            }

            targets.Add((space, item, maximum, price));
            return true;
        }

        try
        {
            if (slot == ushort.MaxValue)
            {
                foreach (var item in worn.Values.Where(item => item.Slot < 23).OrderBy(item => item.Slot))
                {
                    AddTarget(3, item, skipFull: true);
                }
            }
            else
            {
                if (itemSpace == 0 ? !CharacterInventoryLayout.IsMainItemSlot(slot) : slot >= 23)
                {
                    return false;
                }

                var selected = itemSpace == 0 ? main : worn;
                if (!selected.TryGetValue(slot, out var item) || !AddTarget(itemSpace, item, skipFull: false))
                {
                    return false;
                }
            }
        }
        catch (OverflowException)
        {
            return false;
        }

        var total = targets.Sum(target => (long)target.Price);
        if (total > currentGold)
        {
            failure = EquipmentRepairFailure.InsufficientGold;
            return false;
        }

        var repaired = new List<EquipmentRepairUpdate>();
        foreach (var target in targets)
        {
            var item = target.Item with { Durability = target.Maximum };
            (target.Space == 0 ? main : worn)[item.Slot] = item;
            repaired.Add(new(target.Space, item));
        }

        IReadOnlyList<EquipmentRepairUpdate> synchronization = repaired;
        if (slot == ushort.MaxValue)
        {
            // DF2008 0x00411C33..0x00411D06 fills worn AND all main-bag
            // equipment locally. Only worn items are charged/repaired here;
            // follow REPLY25 with authoritative rows to undo the bag updates.
            synchronization = main.Values
                .Where(item => catalog.TryGetMaximumDurability(item.ItemId, out _))
                .OrderBy(item => item.Slot).Select(item => new EquipmentRepairUpdate(0, item))
                .Concat(worn.Values.Where(item => item.Slot < 23
                        && catalog.TryGetMaximumDurability(item.ItemId, out _))
                    .OrderBy(item => item.Slot).Select(item => new EquipmentRepairUpdate(3, item)))
                .ToArray();
        }

        plan = new(main, worn, currentGold - (int)total, (int)total, repaired, synchronization);
        failure = EquipmentRepairFailure.None;
        return true;
    }
}
