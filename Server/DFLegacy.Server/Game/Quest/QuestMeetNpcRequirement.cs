namespace DFLegacy.Server;

public static class QuestMeetNpcRequirement
{
    public const byte CompleteTriggerAction = 0;

    public static bool IsMeetNpc(QuestDefinition? quest) =>
        quest is not null
        && string.Equals(quest.Type, "meet npc", StringComparison.OrdinalIgnoreCase);

    public static bool TryApplyClientCompletion(
        QuestDefinition? quest,
        uint currentTrigger,
        byte action,
        out uint updatedTrigger)
    {
        updatedTrigger = currentTrigger;
        if (!IsMeetNpc(quest) || action != CompleteTriggerAction)
        {
            return false;
        }

        // DF2008 validates the configured completion NPC locally and reports
        // the completed meet-NPC condition as CMD 35 action 0.
        updatedTrigger = 0;
        return true;
    }
}
