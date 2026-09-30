namespace DAFLegacy.Server;

/// <summary>Accepted quests and completed quest ids (07 §4.3).</summary>
public sealed class QuestState
{
    public List<CharacterQuestRecord> Current = [];

    public HashSet<ushort> CompletedIds = [];
}
