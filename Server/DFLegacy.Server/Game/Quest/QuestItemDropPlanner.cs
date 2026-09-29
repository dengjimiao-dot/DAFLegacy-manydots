namespace DFLegacy.Server;

public sealed record QuestItemDrop(
    ushort ItemId,
    uint Count);

public static class QuestItemDropPlanner
{
    public static IReadOnlyList<QuestItemDrop> PlanClear(
        QuestDefinition? quest,
        int dungeonId,
        int difficulty,
        IEnumerable<CharacterItemRecord>? inventory,
        Func<int, bool>? chanceRoll)
    {
        if (quest is null)
        {
            return [];
        }

        var drops = new List<QuestItemDrop>();
        var counts = CountItems(inventory);
        foreach (var row in quest.ClearRewardItems)
        {
            if ((row.DungeonId < 0 || row.DungeonId == dungeonId)
                && (row.Difficulty < 0 || row.Difficulty == difficulty))
            {
                AddCappedDrop(
                    drops,
                    counts,
                    row.ItemId,
                    row.Count,
                    row.MaximumCount,
                    row.ChancePercent,
                    chanceRoll);
            }
        }

        return drops;
    }

    public static IReadOnlyList<QuestItemDrop> PlanMonster(
        QuestDefinition? quest,
        int dungeonId,
        int difficulty,
        int monsterIndex,
        IEnumerable<CharacterItemRecord>? inventory,
        Func<int, bool>? chanceRoll)
    {
        if (quest is null)
        {
            return [];
        }

        var drops = new List<QuestItemDrop>();
        var counts = CountItems(inventory);
        foreach (var row in quest.MonsterRewardItems)
        {
            if ((row.DungeonId < 0 || row.DungeonId == dungeonId)
                && (row.MonsterIndex < 0 || row.MonsterIndex == monsterIndex)
                && (row.Difficulty < 0 || row.Difficulty == difficulty))
            {
                AddCappedDrop(
                    drops,
                    counts,
                    row.ItemId,
                    row.Count,
                    row.MaximumCount,
                    row.ChancePercent,
                    chanceRoll);
            }
        }

        return drops;
    }

    private static Dictionary<ushort, ulong> CountItems(
        IEnumerable<CharacterItemRecord>? inventory) =>
        (inventory ?? [])
        .GroupBy(item => item.ItemId)
        .ToDictionary(
            group => group.Key,
            group => group.Aggregate(
                0UL,
                (total, item) => total > ulong.MaxValue - item.CountOrValue
                    ? ulong.MaxValue
                    : total + item.CountOrValue));

    private static bool ShouldDrop(int chancePercent, Func<int, bool>? chanceRoll) =>
        chancePercent >= 100
        || chancePercent > 0 && chanceRoll is not null && chanceRoll(chancePercent);

    private static void AddCappedDrop(
        ICollection<QuestItemDrop> drops,
        IDictionary<ushort, ulong> counts,
        ushort itemId,
        uint count,
        uint maximumCount,
        int chancePercent,
        Func<int, bool>? chanceRoll)
    {
        if (itemId == 0 || count == 0 || maximumCount == 0)
        {
            return;
        }

        var current = counts.TryGetValue(itemId, out var existingCount)
            ? existingCount
            : 0UL;
        if (current >= maximumCount)
        {
            return;
        }

        if (!ShouldDrop(chancePercent, chanceRoll))
        {
            return;
        }

        var granted = checked((uint)Math.Min(count, (ulong)maximumCount - current));
        if (granted == 0)
        {
            return;
        }

        drops.Add(new QuestItemDrop(itemId, granted));
        counts[itemId] = current + granted;
    }
}
