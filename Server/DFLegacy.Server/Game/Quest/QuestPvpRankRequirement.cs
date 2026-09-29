namespace DFLegacy.Server;

public static class QuestPvpRankRequirement
{
    public static bool IsPvpRank(QuestDefinition? quest) => quest is not null
        && string.Equals(quest.Type, "pvp rank", StringComparison.OrdinalIgnoreCase);

    public static uint GetTrigger(QuestDefinition quest, byte grade)
    {
        // DF2008 0x4EB200 compares the internal grade against the first
        // [int data] value: 0 = 10th, 1 = 9th, 2 = 8th, then ascending ranks.
        // [PVP RANK] is the acceptance prerequisite, not this condition.
        return IsPvpRank(quest)
            && quest.ConditionRows.Length > 0
            && quest.ConditionRows[0].Length > 0
            && quest.ConditionRows[0][0] >= 0
            && grade >= quest.ConditionRows[0][0] ? 0u : 1u;
    }
}
