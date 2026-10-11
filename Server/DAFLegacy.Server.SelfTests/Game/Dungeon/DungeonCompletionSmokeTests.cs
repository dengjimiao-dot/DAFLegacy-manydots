using DAFLegacy.Protocol;
using DAFLegacy.Server;
using DAFLegacy.Server.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

internal static class DungeonCompletionSmokeTests
{
    public static void Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), $"daflegacy-dungeon-completion-{Guid.NewGuid():N}");
        try
        {
            foreach (var bossMapId in new ushort[] { 5802, 5803 })
            {
                var bossFirst = bossMapId == 5802;
                var catalog = CreateCatalog(Path.Combine(root, bossMapId.ToString()), bossMapId,
                    bossFirst
                        ? "1030 1 0 268 807 0 20 1 `[fixed]` `[boss]`\n"
                            + "1034 0 20 1189 431 0 1 1 `[fixed]` `[normal]`\n"
                        : "1034 0 20 1189 431 0 1 1 `[fixed]` `[normal]`\n"
                            + "1030 1 0 268 807 0 20 1 `[fixed]` `[boss]`\n",
                    bossFirst ? [100, 0] : [0, 100]);
                var random = new ZeroDropRandomSource();
                if (!catalog.TryCreateLayout(41, out var layout, random: random))
                {
                    throw new InvalidOperationException("ColdBoy completion fixture has no layout.");
                }
                var run = new DungeonRunState(catalog);
                DungeonRoomTransition revisit = null!;
                check(run.TryStart(layout, 1, 0, out _, random)
                        && !run.IsCleared && !run.IsBossClearPending
                        && !run.BeginBossClear() && !run.TryEnableEmptyBossClear(),
                    $"ColdBoy {bossMapId} does not clear or lock movement in its empty start room");
                check(run.TryMoveRoom(0, 0, out _, random)
                        && run.TryMoveRoom(1, 0, out _, random)
                        && run.TryMoveRoom(0, 0, out revisit, random) && revisit.Revisit,
                    $"ColdBoy {bossMapId} permits ordinary revisits before boss completion");
                var boss = run.CurrentRoom!.Monsters.Single(monster =>
                    monster.Type == GameDungeonMonsterTypes.Boss);
                var heart = run.CurrentRoom.Monsters.Single(monster => monster.MonsterIndex == 1034);
                var seed = run.CurrentRoom.ClientRandomSeed;
                check(boss.UniqueId == (bossFirst ? 0 : 1) && heart.Level == 20
                        && boss.Team == 100 && heart.Team == 0,
                    $"ColdBoy {bossMapId} keeps both PVF actor orderings and the protected heart");
                check(DungeonCatalog.PromoteChampions(revisit.Room.Monsters,
                            byte.MaxValue, random).Single(monster => monster.MonsterIndex == 1034)
                        .Type == GameDungeonMonsterTypes.Normal,
                    $"ColdBoy {bossMapId} cannot promote its allied heart to champion");
                check(run.BeginBossClear() && !run.BeginBossClear()
                        && run.IsBossClearPending && !run.IsCleared
                        && !run.TryMoveRoom(1, 0, out _, random)
                        && run.RoomX == 0 && run.CurrentRoom.ClientRandomSeed == seed,
                    $"ColdBoy {bossMapId} rejects MOVE_MAP during the final boss death animation");
                check(run.MarkMonsterDefeated(boss.UniqueId) && run.IsCleared
                        && !run.IsBossClearPending && run.HasClearedMap(bossMapId)
                        && run.CurrentRoom.Monsters.Single().UniqueId == heart.UniqueId,
                    $"ColdBoy {bossMapId} clears with the protected heart still alive");
                check(DungeonPacketBuilders.CreateStartMap(0, 0, bossMapId, revisit.Room.Monsters)
                        .Payload.Length == 30,
                    $"ColdBoy {bossMapId} team metadata does not alter DF2008 START_MAP fields");
                check(!run.MarkMonsterDefeated(boss.UniqueId)
                        && !run.TryMoveRoom(1, 0, out _, random)
                        && !run.TryEnableEmptyBossClear() && run.IsCleared,
                    $"ColdBoy {bossMapId} cannot return to an old room or reopen clear after completion");
                check(run.MarkMonsterDefeated(heart.UniqueId) && run.IsCleared
                        && !run.TryMoveRoom(1, 0, out _, random),
                    $"ColdBoy {bossMapId} late cascade cleanup cannot reset the completed run");
                run.Stop();
                check(!run.IsCleared && !run.IsBossClearPending && !run.HasClearedMap(bossMapId)
                        && run.TryStart(layout, 1, 0, out _, random)
                        && run.TryMoveRoom(0, 0, out _, random)
                        && run.MarkMonsterDefeated(boss.UniqueId) && run.IsCleared,
                    $"ColdBoy {bossMapId} resets only for a new run and supports CMD42 without CMD127");
            }

            var multiBoss = CreateCatalog(Path.Combine(root, "multi-boss"), 5802,
                "1030 1 0 268 807 0 20 1 `[fixed]` `[boss]`\n"
                + "1031 1 0 368 807 0 20 1 `[fixed]` `[boss]`\n"
                + "1034 0 20 1189 431 0 1 1 `[fixed]` `[normal]`\n", [100, 100, 0]);
            var multiRun = StartBossRoom(multiBoss);
            check(multiRun.MarkMonsterDefeated(0) && !multiRun.IsCleared
                    && multiRun.MarkMonsterDefeated(1) && multiRun.IsCleared
                    && multiRun.CurrentRoom!.Monsters.Single().MonsterIndex == 1034,
                "a terminal room with multiple real bosses waits for the last boss, not its protected actor");

            var enemyCascade = CreateCatalog(Path.Combine(root, "enemy-cascade"), 5802,
                "1030 1 0 268 807 0 20 1 `[fixed]` `[boss]`\n"
                + "1032 1 0 368 807 0 20 1 `[fixed]` `[normal]`\n"
                + "1034 0 20 1189 431 0 1 1 `[fixed]` `[normal]`\n", [100, 100, 0]);
            var cascadeRun = StartBossRoom(enemyCascade);
            var cascadeRoom = cascadeRun.CurrentRoom!;
            var cascadeDecision = DungeonRoomClearRules.Evaluate(
                cascadeRoom, cascadeRoom.Monsters[0], new HashSet<ushort> { 1, 2 });
            check(!cascadeDecision.ShouldEnableClear
                    && cascadeDecision.ForcedMonsterIds.SequenceEqual(new ushort[] { 1 })
                    && cascadeRun.MarkMonsterDefeated(0) && !cascadeRun.IsCleared
                    && cascadeRun.IsBossClearPending && !cascadeRun.TryMoveRoom(1, 0, out _)
                    && cascadeRun.MarkMonsterDefeated(1) && cascadeRun.IsCleared
                    && cascadeRun.CurrentRoom!.Monsters.Single().UniqueId == 2,
                "only surviving team-100 enemies delay clear or enter the native boss cascade");

            var alliesOnly = CreateCatalog(Path.Combine(root, "allies-only"), 5802,
                "1034 0 20 1189 431 0 1 1 `[fixed]` `[normal]`\n"
                + "1035 0 20 1189 441 0 1 1 `[fixed]` `[normal]`\n"
                + "1036 0 20 1189 451 0 1 1 `[fixed]` `[boss]`\n", [0, 1, 99]);
            var alliesRun = StartBossRoom(alliesOnly);
            check(!alliesRun.BeginBossClear() && alliesRun.TryEnableEmptyBossClear()
                    && alliesRun.IsCleared && alliesRun.CurrentRoom!.Monsters.Length == 3
                    && alliesRun.HasClearedMap(5802),
                "non-100 teams, including an allied boss actor, do not block a terminal room");

            var noBoss = CreateCatalog(Path.Combine(root, "no-boss"), 5802,
                "1032 1 0 268 807 0 20 1 `[fixed]` `[normal]`\n"
                + "1033 1 0 368 807 0 20 1 `[fixed]` `[normal]`\n");
            var noBossRun = StartBossRoom(noBoss);
            check(noBossRun.CurrentRoom!.Monsters.All(monster => monster.Team == 100)
                    && !noBossRun.BeginBossClear() && !noBossRun.TryEnableEmptyBossClear()
                    && noBossRun.MarkMonsterDefeated(0) && !noBossRun.IsCleared
                    && noBossRun.MarkMonsterDefeated(1) && noBossRun.IsCleared,
                "a terminal room without a real boss still requires all static monsters to die");

            var empty = CreateCatalog(Path.Combine(root, "empty"), 5802, "");
            var emptyRun = StartBossRoom(empty);
            check(emptyRun.TryEnableEmptyBossClear() && emptyRun.IsCleared
                    && !emptyRun.TryEnableEmptyBossClear()
                    && !emptyRun.TryMoveRoom(1, 0, out _),
                "empty terminal settlement is one-shot and also locks MOVE_MAP");

            var rejectedMove = ConnectionPacketBuilders.CreateCommandError(
                new PacketFrame(1, 48, 0, [1, 0, 1, 0]),
                GameProtocolConstants.MoveMapInvalidStateError);
            check(rejectedMove.Type == 1 && rejectedMove.ProtocolId == 48
                    && rejectedMove.Payload.SequenceEqual(new byte[] { 0, 0x13 })
                    && DungeonPacketBuilders.CreateEnableClearDungeon().ProtocolId == 31
                    && DungeonPacketBuilders.CreateEnableClearDungeon().Payload.Length == 0,
                "completion uses the existing empty NOTI31 and CMD48 invalid-state response");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static DungeonRunState StartBossRoom(DungeonCatalog catalog)
    {
        var random = new ZeroDropRandomSource();
        if (!catalog.TryCreateLayout(41, out var layout, random: random))
        {
            throw new InvalidOperationException("Completion fixture has no layout.");
        }
        var run = new DungeonRunState(catalog);
        if (!run.TryStart(layout, 0, 0, out _, random)
            || !run.TryMoveRoom(0, 0, out _, random))
        {
            throw new InvalidOperationException("Completion fixture could not enter its boss room.");
        }
        return run;
    }

    private static DungeonCatalog CreateCatalog(
        string root, ushort bossMapId, string monsters, int[]? teams = null)
    {
        Directory.CreateDirectory(Path.Combine(root, "dungeon"));
        Directory.CreateDirectory(Path.Combine(root, "map"));
        File.WriteAllText(Path.Combine(root, "dungeon/dungeon.lst"), "41 `coldboy.dgn`\n");
        File.WriteAllText(Path.Combine(root, "dungeon/coldboy.dgn"),
            "[minimum required level] 33\n[basis level] 38\n[maze info]\n[size] 2 1\n"
            + "[greed]\n`BBEE`\n[start map]\n1 0\n[/start map]\n[boss map]\n0 0\n[/boss map]\n");
        File.WriteAllText(Path.Combine(root, "map/map.lst"),
            $"5801 `s5801.map`\n{bossMapId} `b{bossMapId}.map`\n");
        File.WriteAllText(Path.Combine(root, "map/s5801.map"),
            "[dungeon]\n41\n[/dungeon]\n[type] `[normal]`\n[greed]\n`EE EE`\n");
        File.WriteAllText(Path.Combine(root, $"map/b{bossMapId}.map"),
            "[dungeon]\n41\n[/dungeon]\n[type] `[boss]`\n[greed]\n`BB BB`\n"
            + $"[monster]\n{monsters}[/monster]\n"
            + (teams is null ? "" : $"[monster team]\n{string.Join("\n", teams)}\n[/monster team]\n"));
        var options = new ServerOptions { ScriptPvfPath = "", SkillScriptPath = root };
        var scripts = new ScriptFileSystem(options,
            NullLogger<ScriptFileSystem>.Instance);
        return new DungeonCatalog(scripts,
            new ObjectDropGenerator(new ServerSettings(),
                new ObjectDropCatalog(scripts, NullLogger<ObjectDropCatalog>.Instance),
                new GoldDropCatalog(scripts, NullLogger<GoldDropCatalog>.Instance)),
            NullLogger<DungeonCatalog>.Instance);
    }
}
