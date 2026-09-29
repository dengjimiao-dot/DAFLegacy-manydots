namespace DFLegacy.Server;

public static class QuestDungeonClearRequirement
{
    public const int SimpleClearSubType = 6;
    public const byte CompleteTriggerAction = 0;

    public static bool IsDungeonClearCondition(QuestDefinition? quest) =>
        quest is not null
        && quest.SubType is >= 0 and <= 7
        && string.Equals(
            quest.Type,
            "condition under clear",
            StringComparison.OrdinalIgnoreCase);

    public static bool IsSimpleClear(QuestDefinition? quest) =>
        IsDungeonClearCondition(quest)
        && quest!.SubType == SimpleClearSubType;

    public static bool Matches(
        QuestDefinition? quest,
        ushort dungeonId,
        byte difficulty)
    {
        if (!IsDungeonClearCondition(quest))
        {
            return false;
        }

        // DF2008 checks configured difficulty as a minimum. A value of -1
        // accepts every difficulty.
        return quest!.ConditionRows.Any(row =>
            row.Length >= 1
            && row[0] == dungeonId
            && (row.Length < 2 || row[1] < 0 || difficulty >= row[1]));
    }

    public static bool TryApplyClientCompletion(
        QuestDefinition? quest,
        byte action,
        out uint updatedTrigger)
    {
        updatedTrigger = 0;
        return IsDungeonClearCondition(quest)
            && action == CompleteTriggerAction;
    }
}
