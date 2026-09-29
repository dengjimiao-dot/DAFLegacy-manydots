namespace DFLegacy.Server;

public static class QuestMapRequirement
{
    public static bool IsClearMap(QuestDefinition? quest) =>
        quest is not null
        && string.Equals(quest.Type, "clear map", StringComparison.OrdinalIgnoreCase);

    public static ushort GetTargetMapId(QuestDefinition? quest) =>
        IsClearMap(quest)
        && quest!.ConditionRows.Length > 0
        && quest.ConditionRows[0].Length > 0
        && quest.ConditionRows[0][0] is > 0 and <= ushort.MaxValue
            ? (ushort)quest.ConditionRows[0][0]
            : (ushort)0;

    public static bool TryApplyClientCompletion(
        QuestDefinition quest,
        byte action,
        DungeonRunState run,
        out uint trigger)
    {
        trigger = 0;
        // DF2008 0x4EB670 matches the first [int data] map ID and calls
        // 0x4EAF40 with action 0, not a packed hunt-counter decrement.
        return action == 0 && run.HasClearedMap(GetTargetMapId(quest));
    }
}
