using System.Buffers.Binary;
using DFLegacy.Protocol;
using DFLegacy.Server;
using Microsoft.Extensions.Logging.Abstractions;

internal static class QuestMapSmokeTests
{
    public static void Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), $"dflegacy-quest-map-{Guid.NewGuid():N}");
        try
        {
            foreach (var directory in new[] { "quest", "dungeon", "map" })
            {
                Directory.CreateDirectory(Path.Combine(root, directory));
            }
            File.WriteAllText(Path.Combine(root, "quest/quest.lst"), "10 `rescue.qst`\n");
            File.WriteAllText(Path.Combine(root, "quest/rescue.qst"),
                "[type] `[clear map]`\n[delete npc index] 10\n[complete npc index] 10\n"
                + "[appear map] 14 -1 2224 100\n[int data]\n2224\n[/int data]\n");
            File.WriteAllText(Path.Combine(root, "dungeon/dungeon.lst"), "14 `test.dgn`\n");
            File.WriteAllText(Path.Combine(root, "dungeon/test.dgn"),
                "[minimum level] 1\n[basis level] 1\n[maze info]\n[size] 3 1\n"
                + "[greed]\n`PPP`\n[start map]\n0 0\n[/start map]\n"
                + "[boss map]\n2 0\n1 0\n[/boss map]\n");
            File.WriteAllText(Path.Combine(root, "map/map.lst"),
                "1 `start.map`\n2 `normal.map`\n3 `boss.map`\n2224 `rescue.map`\n");
            foreach (var name in new[] { "start", "normal", "boss", "rescue" })
            {
                var dungeonTag = name == "rescue" ? "" : "[dungeon]\n14\n[/dungeon]\n";
                File.WriteAllText(Path.Combine(root, $"map/{name}.map"),
                    dungeonTag + $"[type] `[{(name == "boss" ? "boss" : "normal")}]`\n"
                    + "[greed]\n`P`\n[monster]\n1 1 1 100 100 0 1 1 `[fixed]` `[normal]`\n[/monster]\n");
            }

            var options = new ServerOptions { ScriptPvfPath = "", SkillScriptPath = root };
            var scripts = new ScriptFileSystem(options, NullLogger<ScriptFileSystem>.Instance);
            var quests = new QuestCatalog(scripts, NullLogger<QuestCatalog>.Instance);
            var catalog = new DungeonCatalog(scripts,
                new ObjectDropGenerator(options,
                    new ObjectDropCatalog(scripts, NullLogger<ObjectDropCatalog>.Instance),
                    new GoldDropCatalog(scripts, NullLogger<GoldDropCatalog>.Instance)),
                NullLogger<DungeonCatalog>.Instance);
            var random = new ZeroDropRandomSource();
            check(quests.TryGetDefinition(10, out var quest)
                    && quest.DeleteNpcIndex == 10 && quest.InitialTrigger == 1
                    && QuestMapRequirement.GetTargetMapId(quest) == 2224,
                "rescue quest parses hidden NPC, nonzero pending trigger and target map");
            DungeonLayout NewLayout()
            {
                if (!catalog.TryCreateLayout(14, out var layout, random: random))
                {
                    throw new InvalidOperationException("rescue fixture layout is missing");
                }
                return layout;
            }

            var plain = NewLayout();
            check(!catalog.TryApplyQuestMap(plain, [], out _, random)
                    && catalog.TryCreateRoom(plain, 1, 0, out var normal, random: random)
                    && normal.MapId == 2,
                "without unfinished quests a rescue map cannot enter the normal pool");
            var layout = NewLayout();
            check(catalog.TryApplyQuestMap(layout, [quest], out var selected, random)
                    && selected == 2224
                    && catalog.TryCreateRoom(layout, 2, 0, out var boss, random: random)
                    && boss.MapType == DungeonMapType.Boss,
                "rescue map occupies an unused boss candidate without replacing the boss");
            check(!catalog.TryApplyQuestMap(layout, [quest], out _, random),
                "resolved rescue coordinate is not rerolled");
            check(!catalog.TryApplyQuestMap(NewLayout(),
                    [quest with { AppearMap = [9, -1, 2224, 100] }], out _, random)
                    && !catalog.TryApplyQuestMap(NewLayout(),
                    [quest with { AppearMap = [14, -1, 2224, 0] }], out _, random)
                    && !catalog.TryApplyQuestMap(NewLayout(),
                    [quest with { AppearMap = [14, -1, 65535, 100] }], out _, random),
                "wrong dungeon, zero chance and missing rescue maps do not substitute rooms");

            var run = new DungeonRunState(catalog);
            check(run.TryStart(layout, 0, 0, out _, random)
                    && run.TryMoveRoom(1, 0, out var rescue, random)
                    && rescue.Room.MapId == 2224,
                "entering the selected rescue room loads its actual map and monster rows");
            check(!QuestMapRequirement.TryApplyClientCompletion(quest, 0, run, out _),
                "clear-map completion is rejected while its monsters remain alive");
            foreach (var monster in run.CurrentRoom!.Monsters)
            {
                run.MarkMonsterDefeated(monster.UniqueId);
            }
            check(QuestMapRequirement.TryApplyClientCompletion(quest, 0, run, out var trigger)
                    && trigger == 0
                    && !QuestMapRequirement.TryApplyClientCompletion(quest, 0x10, run, out _)
                    && !QuestMapRequirement.TryApplyClientCompletion(
                        quest with { ConditionRows = [[999]] }, 0, run, out _),
                "CMD35 action zero completes only a genuinely cleared target map");
            check(run.TryMoveRoom(0, 0, out _, random)
                    && run.HasClearedMap(2224)
                    && run.TryMoveRoom(1, 0, out var revisit, random)
                    && revisit.Revisit && revisit.Room.MapId == 2224
                    && revisit.Room.Monsters.Length == 0,
                "room revisit retains rescue completion and does not respawn its monsters");
            run.Stop();
            check(!run.HasClearedMap(2224), "clear-map evidence does not leak into the next dungeon run");

            var accept = GameProtocolEngine.CreateAcceptQuestReply(10, quest.InitialTrigger);
            var complete = GameProtocolEngine.CreateSetQuestTriggerReply(10, 0);
            check(accept.ProtocolId == 33 && accept.Payload.SequenceEqual(new byte[] { 1, 10, 0, 1, 0, 0, 0, 0 })
                    && complete.ProtocolId == 35
                    && complete.Payload.SequenceEqual(new byte[] { 1, 10, 0, 0, 0, 0, 0 }),
                "native quest replies hide the NPC with pending trigger and restore it with zero");
            var start = GameProtocolEngine.CreateStartMap(1, 0, 2224, []);
            check(start.ProtocolId == 29
                    && BinaryPrimitives.ReadUInt16LittleEndian(start.Payload.AsSpan(7, 2)) == 2224,
                "START_MAP transmits the rescue map ID in the existing DF2008 field");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
