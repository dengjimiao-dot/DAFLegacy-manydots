using DFLegacy.Protocol;
using DFLegacy.Server;

internal static class QuestPvpRankSmokeTests
{
    public static void Run(Action<bool, string> check)
    {
        var quest = new QuestDefinition(1702, 0, 20, 99, "all", -1, 27, 27,
            "pvp rank", [], [], true, false, "item", [], [], 0)
        {
            ConditionRows = [[1]]
        };
        check(QuestPvpRankRequirement.GetTrigger(quest, 0) == 1
                && QuestPvpRankRequirement.GetTrigger(quest, 1) == 0
                && QuestPvpRankRequirement.GetTrigger(quest, 2) == 0
                && QuestPvpRankRequirement.GetTrigger(quest, 20) == 0,
            "PvP quest threshold 1 accepts 9th, 8th and higher ranks but rejects 10th");
        check(QuestPvpRankRequirement.GetTrigger(quest with { ConditionRows = [[20]] }, 19) == 1
                && QuestPvpRankRequirement.GetTrigger(quest with { ConditionRows = [[20]] }, 20) == 0,
            "PvP quest comparisons retain ascending internal grades past novice ranks");
        check(QuestPvpRankRequirement.GetTrigger(quest with { ConditionRows = [] }, 2) == 1
                && QuestPvpRankRequirement.GetTrigger(quest with { ConditionRows = [[]] }, 2) == 1
                && QuestPvpRankRequirement.GetTrigger(quest with { ConditionRows = [[-1]] }, 2) == 1
                && !QuestPvpRankRequirement.IsPvpRank(quest with { Type = "seeking" }),
            "malformed PvP conditions and unrelated quest types do not auto-complete");
        var trigger = QuestPvpRankRequirement.GetTrigger(quest, 2);
        var accepted = GameProtocolEngine.CreateAcceptQuestReply(1702, trigger);
        var updated = GameProtocolEngine.CreateSetQuestTriggerReply(1702, trigger);
        check(accepted.Payload.SequenceEqual(new byte[] { 1, 0xA6, 6, 0, 0, 0, 0, 0 })
                && updated.Payload.SequenceEqual(new byte[] { 1, 0xA6, 6, 0, 0, 0, 0 }),
            "already-qualified PvP quests encode complete triggers in CMD33 and CMD35");
    }
}
