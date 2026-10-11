using System.Linq;
using DAFLegacy.Server;
using DAFLegacy.Server.SelfTests.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

internal static class QuestBulkCompletionSmokeTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), $"daflegacy-quest-bulk-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "quest"));
        try
        {
            File.WriteAllText(Path.Combine(root, "quest", "quest.lst"),
                "100 `normal.qst`\n"
                + "101 `already.qst`\n"
                + "110 `highlevel.qst`\n"
                + "120 `swordman.qst`\n"
                + "130 `growfinal.qst`\n"
                + "140 `awakenfinal.qst`\n"
                + "150 `jobchange1step.qst`\n"
                + "160 `jobchange2step.qst`\n"
                + "170 `growtype2normal.qst`\n"
                + "180 `expired.qst`\n"
                + "190 `selectable.qst`\n"
                + "200 `goldreward.qst`\n"
                + "1016 `tutorial.qst`\n");
            WriteQuest(Path.Combine(root, "quest", "normal.qst"), "[level] 10 99\n[job] `all`\n");
            WriteQuest(Path.Combine(root, "quest", "already.qst"), "[level] 10 99\n[job] `all`\n");
            WriteQuest(Path.Combine(root, "quest", "highlevel.qst"), "[level] 61 99\n[job] `all`\n");
            WriteQuest(Path.Combine(root, "quest", "swordman.qst"), "[level] 10 99\n[job] `swordman`\n");
            WriteQuest(Path.Combine(root, "quest", "growfinal.qst"),
                "[level] 18 99\n[job] `all`\n[reward type] `grow type`\n[reward int data]\n1\n[/reward int data]\n");
            WriteQuest(Path.Combine(root, "quest", "awakenfinal.qst"),
                "[level] 48 99\n[job] `all`\n[reward type] `awakening type`\n[reward int data]\n1\n[/reward int data]\n");
            WriteQuest(Path.Combine(root, "quest", "jobchange1step.qst"),
                "[level] 17 99\n[job] `all`\n[job change quest] 1\n");
            WriteQuest(Path.Combine(root, "quest", "jobchange2step.qst"),
                "[level] 47 99\n[job] `all`\n[job change quest] 2\n");
            WriteQuest(Path.Combine(root, "quest", "growtype2normal.qst"),
                "[level] 20 99\n[job] `all`\n[grow type] 2\n");
            WriteQuest(Path.Combine(root, "quest", "expired.qst"), "[level] 10 30\n[job] `all`\n");
            WriteQuest(Path.Combine(root, "quest", "selectable.qst"),
                "[level] 10 99\n[job] `all`\n[reward selection int data]\n501 1\n[/reward selection int data]\n");
            WriteQuest(Path.Combine(root, "quest", "goldreward.qst"),
                "[level] 10 99\n[job] `all`\n[reward int data]\n0 5000\n[/reward int data]\n");
            WriteQuest(Path.Combine(root, "quest", "tutorial.qst"),
                "[type] `meet npc`\n[npc index] 2\n[complete npc index] 2\n");
            var options = new ServerOptions
            {
                DatabasePath = Path.Combine(root, "state.db"),
                ScriptPvfPath = "",
                SkillScriptPath = root
            };
            var scripts = new ScriptFileSystem(options, NullLogger<ScriptFileSystem>.Instance);
            var quests = new QuestCatalog(scripts, NullLogger<QuestCatalog>.Instance);
            var items = new ItemCatalog(scripts, NullLogger<ItemCatalog>.Instance);
            var experienceCatalog = new CharacterExperienceCatalog(
                scripts, NullLogger<CharacterExperienceCatalog>.Instance);
            var spCatalog = new CharacterSpCatalog(scripts, NullLogger<CharacterSpCatalog>.Instance);
            check(quests.Definitions.Count == 13,
                "bulk completion fixture loads all thirteen quest definitions");

            var advanced = new CharacterRecord(
                Guid.NewGuid(), "Advanced", Job: 2, GrowType: 1, Level: 60, Sp: 0, Cash: 0)
            {
                CompletedQuestIds = [101]
            };
            check(QuestBulkCompletionPlanner.SelectLevelEligibleQuestIds(advanced, quests)
                    is [100, 130, 150, 190, 200],
                "an advanced character completes quests whose level range covers the level");
            var midLevel = new CharacterRecord(
                Guid.NewGuid(), "MidLevel", Job: 2, GrowType: 1, Level: 25, Sp: 0, Cash: 0)
            {
                CompletedQuestIds = [101]
            };
            check(QuestBulkCompletionPlanner.SelectLevelEligibleQuestIds(midLevel, quests)
                    is [100, 130, 150, 180, 190, 200],
                "a mid-level character also completes the quest range it still fits");
            var novice = new CharacterRecord(
                Guid.NewGuid(), "Novice", Job: 2, GrowType: 0, Level: 60, Sp: 0, Cash: 0)
            {
                CompletedQuestIds = [101]
            };
            check(QuestBulkCompletionPlanner.SelectLevelEligibleQuestIds(novice, quests)
                    is [100, 190, 200],
                "an unadvanced character keeps the whole job-change and awakening chain");
            var awakened = new CharacterRecord(
                Guid.NewGuid(), "Awakened", Job: 2, GrowType: 1, Level: 60, Sp: 0, Cash: 0)
            {
                AwakeningType = 1,
                CompletedQuestIds = [101]
            };
            check(QuestBulkCompletionPlanner.SelectLevelEligibleQuestIds(awakened, quests)
                    is [100, 130, 140, 150, 160, 190, 200],
                "an awakened character also records the awakening chain as history");
            var lowLevel = new CharacterRecord(
                Guid.NewGuid(), "LowLevel", Job: 2, GrowType: 1, Level: 9, Sp: 0, Cash: 0);
            check(QuestBulkCompletionPlanner.SelectLevelEligibleQuestIds(lowLevel, quests).Count == 0,
                "quests whose range starts above the character level are not completed");

            var settlement = QuestBulkCompletionPlanner.PlanRewardSettlement(
                midLevel,
                QuestBulkCompletionPlanner.SelectLevelEligibleQuestIds(midLevel, quests),
                quests,
                items,
                experienceCatalog,
                spCatalog);
            check(settlement.SkippedRewardQuestIds is [190]
                    && settlement.Gold == 5000
                    && settlement.Experience > 0
                    && settlement.Level == midLevel.Level
                    && settlement.Mails.Count == 0
                    && settlement.RewardedQuestIds.Count == 5,
                "reward settlement grants gold and experience and skips selectable-reward quests");

            TestGameStore.SeedDefaultTestAccount(options);
            var store = TestGameStore.Create(options);
            await store.InitializeAsync();
            var character = (await store.GetCharactersAsync("test"))[0];
            character = (await store.SaveCharacterQuestsAsync(
                "test", character.Id, [new CharacterQuestRecord(100, 7)], [42]))!;
            var stored = QuestBulkCompletionPlanner.PlanRewardSettlement(
                character,
                [100, 116, 200],
                quests,
                items,
                experienceCatalog,
                spCatalog);
            var result = await store.CompleteCharacterQuestsByLevelAsync(
                character.Id,
                [100, 116, 200],
                [9],
                stored.Level,
                stored.Experience,
                stored.SkillPoints,
                stored.Gold,
                stored.Inventory,
                stored.WarehouseCapacity,
                stored.Mails);
            check(result is not null
                    && result.CompletedQuestIds is [100, 116, 200]
                    && result.Character.Quests!.Count == 0
                    && result.Character.CompletedQuestIds is [42, 100, 116, 200]
                    && result.Character.RewardedQuestIds!.Contains(100)
                    && result.Character.RewardedQuestIds!.Contains(200)
                    && result.Character.Gold == stored.Gold
                    && result.Character.Experience == stored.Experience
                    && result.Character.Level == stored.Level
                    && result.Character.UnlockedDungeonIds!.Contains(9)
                    && DungeonDifficultyProgression.GetMaximumDifficulty(
                        result.Character.DungeonProgress, 9) == 0
                    && result.AppendedMailCount == 0,
                "bulk completion settles rewards, merges completed and rewarded sets and unlocks dungeons");
            var reloaded = TestGameStore.Create(options);
            await reloaded.InitializeAsync();
            var persisted = (await reloaded.GetCharactersAsync("test"))[0];
            check(persisted.Quests!.Count == 0
                    && persisted.CompletedQuestIds is [42, 100, 116, 200]
                    && persisted.RewardedQuestIds!.Contains(100)
                    && persisted.UnlockedDungeonIds!.Contains(9)
                    && persisted.Gold == stored.Gold,
                "bulk completion survives a store reload");

            var cleared = await reloaded.ClearCharacterMailboxAsync(persisted.Id);
            check(cleared is not null,
                "mailbox clear reports the cleared character");
            var mailboxAfterClear = (await reloaded.GetCharactersAsync("test"))[0].Mailbox;
            check(mailboxAfterClear is { Count: 0 },
                "mailbox clear empties the mailbox");
            check(await reloaded.ClearCharacterMailboxAsync(Guid.NewGuid()) is null,
                "mailbox clear rejects unknown characters");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void WriteQuest(string path, string body) =>
        File.WriteAllText(path, body);
}
