namespace DFLegacy.Server;

public static class HiddenDungeonUnlockCatalog
{
    public static CharacterRecord ApplyUnlocks(
        CharacterRecord character,
        IEnumerable<ushort> dungeonIds)
    {
        var unlocked = (character.UnlockedDungeonIds ?? [])
            .Concat(dungeonIds).Where(id => id != 0).Distinct().Order().ToList();
        return character with
        {
            UnlockedDungeonIds = unlocked,
            DungeonProgress = DungeonDifficultyProgression.Normalize(
                (character.DungeonProgress ?? []).Concat(unlocked.Select(id =>
                    new CharacterDungeonProgressRecord(id, 0)))).ToList()
        };
    }
    private static readonly IReadOnlyDictionary<ushort, ushort> DungeonByQuest =
        new Dictionary<ushort, ushort>
        {
            [4] = 9,
            [116] = 9,
            [363] = 17,
            [66] = 51,
            [203] = 1000,
            [428] = 1500
        };

    private static readonly IReadOnlySet<ushort> HiddenDungeonIds =
        DungeonByQuest.Values.ToHashSet();

    public static bool TryGetDungeonId(ushort questId, out ushort dungeonId) =>
        DungeonByQuest.TryGetValue(questId, out dungeonId);

    public static IReadOnlyList<ushort> ResolveUnlockedDungeonIds(
        IEnumerable<ushort> questIds) =>
        questIds
            .Distinct()
            .Select(questId => DungeonByQuest.GetValueOrDefault(questId))
            .Where(dungeonId => dungeonId != 0)
            .Distinct()
            .OrderBy(dungeonId => dungeonId)
            .ToArray();

    public static IReadOnlyList<ushort> ResolveUnlockedDungeonIds(
        IEnumerable<ushort> questIds,
        QuestCatalog quests,
        DungeonCatalog dungeons) =>
        questIds
            .Distinct()
            .SelectMany(questId =>
            {
                quests.TryGetDefinition(questId, out var definition);
                return ResolveQuestDungeonUnlockIds(questId, definition, dungeons);
            })
            .Distinct()
            .OrderBy(dungeonId => dungeonId)
            .ToArray();

    public static IReadOnlyList<ushort> ResolveQuestDungeonUnlockIds(
        ushort questId,
        QuestDefinition? definition,
        DungeonCatalog dungeons)
    {
        var result = new HashSet<ushort>();
        if (TryGetDungeonId(questId, out var mappedDungeonId))
        {
            result.Add(mappedDungeonId);
        }

        if (definition is not null)
        {
            foreach (var row in definition.ConditionRows)
            {
                if (row.Length > 0
                    && row[0] is > 0 and <= ushort.MaxValue
                    && !HiddenDungeonIds.Contains((ushort)row[0])
                    && dungeons.TryGetDefinition(checked((ushort)row[0]), out _))
                {
                    result.Add(checked((ushort)row[0]));
                }
            }

            if (definition.AppearMap.Length > 0
                && definition.AppearMap[0] is > 0 and <= ushort.MaxValue
                && !HiddenDungeonIds.Contains((ushort)definition.AppearMap[0])
                && dungeons.TryGetDefinition(
                    checked((ushort)definition.AppearMap[0]),
                    out _))
            {
                result.Add(checked((ushort)definition.AppearMap[0]));
            }
        }

        return result.OrderBy(dungeonId => dungeonId).ToArray();
    }

    public static IReadOnlyList<ushort> ResolveVisibleDungeonIds(
        IEnumerable<ushort> catalogDungeonIds,
        IEnumerable<ushort>? unlockedDungeonIds)
    {
        var unlocked = (unlockedDungeonIds ?? []).ToHashSet();
        return catalogDungeonIds
            .Where(dungeonId => !HiddenDungeonIds.Contains(dungeonId)
                || unlocked.Contains(dungeonId))
            .Distinct()
            .OrderBy(dungeonId => dungeonId)
            .ToArray();
    }
}
