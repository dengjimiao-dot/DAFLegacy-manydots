namespace DFLegacy.Server;

public sealed record CharacterWarehouseUpgradeItemSettlement(
    IReadOnlyList<CharacterItemRecord> Inventory,
    IReadOnlyList<CharacterItemRecord> Warehouse,
    ushort Capacity,
    IReadOnlyList<ushort> ConsumedInventorySlots,
    IReadOnlyList<ushort> ConsumedWarehouseSlots)
{
    public bool ConsumedItems =>
        ConsumedInventorySlots.Count != 0 || ConsumedWarehouseSlots.Count != 0;
}

public static class CharacterWarehouseProgression
{
    public const ushort InitialCapacity = 8;
    public const ushort MaximumCapacity = 120;
    public const ushort UpgradeSize = 16;

    private static readonly ushort[] CapacityStepsArray =
    [
        8,
        24,
        40,
        56,
        72,
        88,
        104,
        120
    ];

    public static IReadOnlyList<ushort> CapacitySteps { get; } =
        Array.AsReadOnly(CapacityStepsArray);

    public static ushort Normalize(ushort capacity)
    {
        var normalized = InitialCapacity;
        foreach (var step in CapacityStepsArray)
        {
            if (step > capacity)
            {
                break;
            }

            normalized = step;
        }

        return normalized;
    }

    public static bool TryGetNextCapacity(ushort currentCapacity, out ushort nextCapacity)
    {
        var index = Array.IndexOf(CapacityStepsArray, currentCapacity);
        if (index < 0 || index == CapacityStepsArray.Length - 1)
        {
            nextCapacity = currentCapacity;
            return false;
        }

        nextCapacity = CapacityStepsArray[index + 1];
        return true;
    }

    public static bool IsNextUpgrade(ushort currentCapacity, ushort targetCapacity) =>
        TryGetNextCapacity(currentCapacity, out var nextCapacity)
        && nextCapacity == targetCapacity;

    public static bool TryGetItemTargetCapacity(ushort itemId, out ushort capacity)
    {
        capacity = itemId switch
        {
            // The target client maps the legacy named grades to its current slot table.
            5 => 24,
            6 => 40,
            7 => 56,
            50 => 24,
            57 => 40,
            58 => 56,
            59 => 72,
            60 => 88,
            61 => 104,
            62 => 120,
            68 => 72,
            _ => 0
        };
        return capacity != 0;
    }

    public static CharacterWarehouseUpgradeItemSettlement SettleUpgradeItems(
        IEnumerable<CharacterItemRecord> inventory,
        IEnumerable<CharacterItemRecord> warehouse,
        ushort currentCapacity)
    {
        var settledCapacity = Normalize(currentCapacity);
        var settledInventory = new List<CharacterItemRecord>();
        var settledWarehouse = new List<CharacterItemRecord>();
        var consumedInventorySlots = new List<ushort>();
        var consumedWarehouseSlots = new List<ushort>();

        SettleSpace(
            inventory,
            settledInventory,
            consumedInventorySlots,
            ref settledCapacity);
        SettleSpace(
            warehouse,
            settledWarehouse,
            consumedWarehouseSlots,
            ref settledCapacity);

        return new(
            settledInventory,
            settledWarehouse,
            settledCapacity,
            consumedInventorySlots,
            consumedWarehouseSlots);
    }

    public static ushort ApplyItemTarget(ushort currentCapacity, ushort itemId) =>
        TryGetItemTargetCapacity(itemId, out var targetCapacity)
            ? Math.Max(Normalize(currentCapacity), targetCapacity)
            : Normalize(currentCapacity);

    private static void SettleSpace(
        IEnumerable<CharacterItemRecord> source,
        ICollection<CharacterItemRecord> settled,
        ICollection<ushort> consumedSlots,
        ref ushort capacity)
    {
        foreach (var item in source)
        {
            if (!TryGetItemTargetCapacity(item.ItemId, out var targetCapacity))
            {
                settled.Add(item);
                continue;
            }

            consumedSlots.Add(item.Slot);
            capacity = Math.Max(capacity, targetCapacity);
        }
    }
}
