namespace DFLegacy.Server;

public sealed record QuestConsumedItemChange(
    ushort Slot,
    uint RemainingCount);

public sealed record QuestItemConsumptionPlan(
    Dictionary<ushort, CharacterItemRecord> Inventory,
    IReadOnlyList<QuestConsumedItemChange> ChangedItems,
    int GoldCost,
    uint VictoryPointCost);

public static class QuestItemRequirementPlanner
{
    public static bool IsSeeking(QuestDefinition? quest) =>
        quest is not null
        && string.Equals(quest.Type, "seeking", StringComparison.OrdinalIgnoreCase);

    public static uint GetInitialTrigger(
        QuestDefinition quest,
        IEnumerable<CharacterItemRecord>? inventory,
        int gold,
        uint victoryPoints) =>
        IsSeeking(quest)
            ? IsSatisfied(quest, inventory, gold, victoryPoints) ? 0u : 1u
            : quest.InitialTrigger;

    public static bool IsSatisfied(
        QuestDefinition? quest,
        IEnumerable<CharacterItemRecord>? inventory,
        int gold,
        uint victoryPoints)
    {
        if (!TryBuildRequirements(quest, out var requirements))
        {
            return false;
        }

        if (gold < requirements.GoldCost
            || victoryPoints < requirements.VictoryPointCost)
        {
            return false;
        }

        var counts = (inventory ?? [])
            .GroupBy(item => item.ItemId)
            .ToDictionary(
                group => group.Key,
                group => group.Aggregate(
                    0UL,
                    (total, item) => total > ulong.MaxValue - item.CountOrValue
                        ? ulong.MaxValue
                        : total + item.CountOrValue));
        return requirements.Items.All(requirement =>
            counts.GetValueOrDefault(requirement.Key) >= requirement.Value);
    }

    public static bool TryConsume(
        QuestDefinition quest,
        IReadOnlyDictionary<ushort, CharacterItemRecord> inventory,
        int gold,
        uint victoryPoints,
        out QuestItemConsumptionPlan plan)
    {
        var plannedInventory = inventory.ToDictionary(pair => pair.Key, pair => pair.Value);
        if (!IsSeeking(quest))
        {
            plan = new QuestItemConsumptionPlan(plannedInventory, [], 0, 0);
            return true;
        }

        if (!TryBuildRequirements(quest, out var requirements)
            || !IsSatisfied(quest, inventory.Values, gold, victoryPoints))
        {
            plan = null!;
            return false;
        }

        var changedItems = new List<QuestConsumedItemChange>();
        foreach (var requirement in requirements.Items)
        {
            var remaining = requirement.Value;
            foreach (var item in plannedInventory.Values
                         .Where(item => item.ItemId == requirement.Key)
                         .OrderBy(item => item.CountOrValue)
                         .ThenBy(item => item.Slot)
                         .ToArray())
            {
                if (remaining == 0)
                {
                    break;
                }

                var consumed = Math.Min((ulong)item.CountOrValue, remaining);
                var remainingInSlot = checked((uint)(item.CountOrValue - consumed));
                if (remainingInSlot == 0)
                {
                    plannedInventory.Remove(item.Slot);
                }
                else
                {
                    plannedInventory[item.Slot] = item with
                    {
                        CountOrValue = remainingInSlot
                    };
                }

                changedItems.Add(new QuestConsumedItemChange(item.Slot, remainingInSlot));
                remaining -= consumed;
            }

            if (remaining != 0)
            {
                plan = null!;
                return false;
            }
        }

        plan = new QuestItemConsumptionPlan(
            plannedInventory,
            changedItems,
            requirements.GoldCost,
            requirements.VictoryPointCost);
        return true;
    }

    private static bool TryBuildRequirements(
        QuestDefinition? quest,
        out SeekingRequirements requirements)
    {
        requirements = null!;
        if (!IsSeeking(quest) || quest!.ConditionRows.Length == 0)
        {
            return false;
        }

        long goldCost = 0;
        ulong victoryPointCost = 0;
        var items = new Dictionary<ushort, ulong>();
        foreach (var row in quest.ConditionRows)
        {
            if (row.Length < 2 || row[1] < 0)
            {
                return false;
            }

            if (row[0] == 0)
            {
                goldCost += row[1];
                if (goldCost > int.MaxValue)
                {
                    return false;
                }
                continue;
            }

            if (row[0] == 2)
            {
                victoryPointCost += checked((uint)row[1]);
                if (victoryPointCost > uint.MaxValue)
                {
                    return false;
                }
                continue;
            }

            if (row[0] is < 1 or > ushort.MaxValue)
            {
                return false;
            }

            var itemId = checked((ushort)row[0]);
            var required = items.GetValueOrDefault(itemId) + checked((uint)row[1]);
            if (required > uint.MaxValue)
            {
                return false;
            }
            items[itemId] = required;
        }

        requirements = new SeekingRequirements(
            items,
            checked((int)goldCost),
            checked((uint)victoryPointCost));
        return true;
    }

    private sealed record SeekingRequirements(
        IReadOnlyDictionary<ushort, ulong> Items,
        int GoldCost,
        uint VictoryPointCost);
}
