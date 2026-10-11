using System.Buffers.Binary;
using DAFLegacy.Protocol;
using DAFLegacy.Server;
using DAFLegacy.Server.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

internal static class DungeonBossEntranceSmokeTests
{
    private const string Hunt = "[boss room entrance condition]\n`[hunt monster]`\n4\n"
        + "50073 1 1 // appearance and kill count\n50074 1 1\n50075 1 1\n50076 1 1\n";

    public static void Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), $"daflegacy-boss-entrance-{Guid.NewGuid():N}");
        try
        {
            var catalog = CreateCatalog(Path.Combine(root, "hunt"), Hunt);
            var random = new ZeroDropRandomSource();
            check(catalog.TryGetDefinition(35, out var definition)
                    && definition.BossEntranceCondition.Kind == DungeonBossEntranceConditionKind.HuntMonster
                    && definition.BossEntranceCondition.Monsters.Select(requirement => requirement.MonsterId)
                        .SequenceEqual(new ushort[] { 50073, 50074, 50075, 50076 })
                    && definition.BossEntranceCondition.Monsters.All(requirement =>
                        requirement.SpawnCount == 1 && requirement.RequiredKills == 1),
                "boss entrance reads all four monster/appearance/kill triples rather than treating them as room counts");
            _ = catalog.TryCreateLayout(35, out var layout, random: random);
            var run = new DungeonRunState(catalog);
            check(run.TryStart(layout, 1, 0, out var start, random)
                    && !run.IsBossRoomEntranceOpen && start.Room.Monsters.Length == 0
                    && run.HasClearedRoom(0, 0)
                    && run.TryMoveRoom(1, 0, out var first, random)
                    && first.Room.Monsters.Count(monster => monster.IsBossEntranceMonster) == 4,
                "a run distributes condition appearances outside start and boss rooms and records empty start-room completion");
            var room = run.CurrentRoom!;
            var guardians = room.Monsters.Where(monster => monster.IsBossEntranceMonster).ToArray();
            var normal = room.Monsters.Single(monster => monster.MonsterIndex == 410);
            check(guardians.Select(monster => monster.MapListIndex).SequenceEqual(new byte[] { 1, 2, 3, 4 })
                    && guardians.Select(monster => monster.UniqueId).Distinct().Count() == 4
                    && guardians.All(monster => monster.Level == 46 && monster.IsEnemy && monster.IsBoxMonster == 0)
                    && DungeonCatalog.PromoteChampions(room.Monsters, 255, random)
                        .Where(monster => monster.IsBossEntranceMonster).All(monster => monster.Type == 0),
                "DF2008 condition actors use the appended local MAP row, +2 dungeon level and independent runtime IDs without champion promotion");
            var packet = DungeonPacketBuilders.CreateStartMap(1, 0, room.MapId, room.Monsters);
            var secondRecord = packet.Payload.AsSpan(10 + 9, 9);
            check(packet.ProtocolId == 29 && packet.Payload.Length == 12 + 9 * room.Monsters.Length
                    && secondRecord[0] == 1
                    && BinaryPrimitives.ReadUInt16LittleEndian(secondRecord.Slice(1, 2)) == guardians[0].UniqueId
                    && BinaryPrimitives.ReadUInt16LittleEndian(secondRecord.Slice(3, 2)) == 50_073,
                "boss entrance metadata retains the target client's nine-byte NOTI29 record rather than using the 13339 layout");
            check(run.MarkMonsterDefeated(normal.UniqueId)
                    && run.MarkMonsterDefeated(guardians[0].UniqueId)
                    && !run.MarkMonsterDefeated(guardians[0].UniqueId)
                    && !run.MarkMonsterDefeated(ushort.MaxValue)
                    && run.GetBossEntranceKillCount(50_073) == 1
                    && run.MarkMonsterDefeated(guardians[1].UniqueId)
                    && !run.IsBossRoomEntranceOpen,
                "only first validated deaths of matching monster IDs advance entrance progress");
            var seed = run.CurrentRoom!.ClientRandomSeed;
            check(run.TryMoveRoom(2, 0, out _, random)
                    && run.CurrentRoom!.Monsters.All(monster => !monster.IsBossEntranceMonster)
                    && run.IsBossRoomEntranceBlocked(3, 0)
                    && !run.TryMoveRoom(3, 0, out _, random)
                    && run.RoomX == 2,
                "unmet entrance conditions reject an adjacent boss transition without changing the current room");
            check(run.MarkMonsterDefeated(run.CurrentRoom!.Monsters[0].UniqueId)
                    && run.TryMoveRoom(1, 0, out var revisit, random) && revisit.Revisit
                    && revisit.ReuseClientRoomState && revisit.Room.ClientRandomSeed == seed
                    && revisit.Room.Monsters.Where(monster => monster.IsBossEntranceMonster)
                        .Select(monster => monster.UniqueId).SequenceEqual(guardians.Skip(2).Select(monster => monster.UniqueId))
                    && run.GetBossEntranceKillCount(50_073) == 1,
                "room revisits keep assigned actors, killed-state and entrance counters without spawning replacements");
            check(run.MarkMonsterDefeated(guardians[2].UniqueId) && !run.IsBossRoomEntranceOpen
                    && run.MarkMonsterDefeated(guardians[3].UniqueId) && run.IsBossRoomEntranceOpen
                    && run.HasClearedRoom(1, 0)
                    && run.TryMoveRoom(2, 0, out _, random)
                    && run.TryMoveRoom(3, 0, out _, random),
                "all four required kills unlock boss entry through the existing MOVE_MAP and START_MAP path");
            check(run.MarkMonsterDefeated(run.CurrentRoom!.Monsters[0].UniqueId)
                    && run.IsCleared && !run.TryMoveRoom(2, 0, out _, random),
                "entrance unlocking does not undo one-shot terminal-boss completion or allow a second clear");
            run.Stop();
            check(run.GetBossEntranceKillCount(50_073) == 0
                    && run.TryStart(layout, 1, 0, out _, random)
                    && !run.IsBossRoomEntranceOpen && run.TryMoveRoom(1, 0, out _, random)
                    && run.CurrentRoom!.Monsters.Count(monster => monster.IsBossEntranceMonster) == 4,
                "boss-entrance progress and appearances reset for a new dungeon run, not on a room transition");

            var ordinary = CreateCatalog(Path.Combine(root, "ordinary"), "");
            _ = ordinary.TryCreateLayout(35, out var ordinaryLayout, random: random);
            var ordinaryRun = new DungeonRunState(ordinary);
            check(ordinaryRun.TryStart(ordinaryLayout, 0, 0, out _, random)
                    && ordinaryRun.IsBossRoomEntranceOpen && ordinaryRun.TryMoveRoom(1, 0, out _, random)
                    && ordinaryRun.CurrentRoom!.Monsters.Length == 1,
                "a dungeon without entrance conditions does not spawn optional MAP actors or gain a new full-map-clear restriction");
            var rooms = CreateCatalog(Path.Combine(root, "rooms"),
                "[boss room entrance condition]\n`[seeking]`\n2\n1 0\n2 0\n");
            _ = rooms.TryCreateLayout(35, out var roomLayout, random: random);
            var roomRun = new DungeonRunState(rooms);
            check(roomRun.TryStart(roomLayout, 0, 0, out _, random)
                    && roomRun.TryMoveRoom(1, 0, out _, random) && roomRun.MarkMonsterDefeated(0)
                    && !roomRun.IsBossRoomEntranceOpen
                    && roomRun.TryMoveRoom(2, 0, out _, random) && !roomRun.IsBossRoomEntranceOpen
                    && roomRun.MarkMonsterDefeated(0) && roomRun.IsBossRoomEntranceOpen
                    && roomRun.TryMoveRoom(3, 0, out _, random),
                "configured non-boss room objectives retain room-clear state and do not complete merely on entry");
            var missing = CreateCatalog(Path.Combine(root, "missing"), Hunt, includeTemplates: false);
            _ = missing.TryCreateLayout(35, out var missingLayout, random: random);
            check(!new DungeonRunState(missing).TryStart(missingLayout, 0, 0, out _, random),
                "a hunt with no eligible condition templates fails entry instead of starting an unwinnable run");
            var reply = ConnectionPacketBuilders.CreateCommandError(
                new PacketFrame(1, 48, 0, [1, 0, 3, 0]),
                GameProtocolConstants.MoveMapBossEntranceConditionError);
            check(reply.ProtocolId == 48 && reply.Payload.SequenceEqual(new byte[] { 0, 7 }),
                "unmet boss entrance uses native error seven and DF2008's existing pending-move cleanup");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static DungeonCatalog CreateCatalog(string root, string condition, bool includeTemplates = true)
    {
        Directory.CreateDirectory(Path.Combine(root, "dungeon"));
        Directory.CreateDirectory(Path.Combine(root, "map"));
        File.WriteAllText(Path.Combine(root, "dungeon/dungeon.lst"), "35 `test.dgn`\n");
        File.WriteAllText(Path.Combine(root, "dungeon/test.dgn"),
            "[basis level] 44\n" + condition + "[maze info]\n[size] 4 1\n[greed]\n`BBFFFFEE`\n"
            + "[start map]\n0 0\n[/start map]\n[boss map]\n3 0\n[/boss map]\n"
            + "[map specification]\n1 0\n102\n[/map specification]\n"
            + "[map specification]\n2 0\n103\n[/map specification]\n");
        File.WriteAllText(Path.Combine(root, "map/map.lst"),
            "101 `s101.map`\n102 `n102.map`\n103 `n103.map`\n104 `b104.map`\n");
        File.WriteAllText(Path.Combine(root, "map/s101.map"),
            "[dungeon]\n35\n[/dungeon]\n[type] `[normal]`\n[greed]\n`BB BB`\n");
        foreach (var id in new[] { 102, 103 })
        {
            File.WriteAllText(Path.Combine(root, $"map/n{id}.map"),
                "[dungeon]\n35\n[/dungeon]\n[type] `[normal]`\n[greed]\n`FF FF`\n"
                + "[monster]\n410 1 0 120 200 0 1 1 `[fixed]` `[normal]`\n[/monster]\n"
                + (includeTemplates ? "[monster condition]\n"
                    + string.Join('\n', Enumerable.Range(50_073, 4).Select(monster =>
                        $"{monster} 1 2 320 200 0 1 1 `[fixed]` `[normal]`"))
                    + "\n[/monster condition]\n" : ""));
        }
        File.WriteAllText(Path.Combine(root, "map/b104.map"),
            "[dungeon]\n35\n[/dungeon]\n[type] `[boss]`\n[greed]\n`EE EE`\n"
            + "[monster]\n50077 1 0 320 200 0 1 1 `[fixed]` `[boss]`\n[/monster]\n");
        var options = new ServerOptions { ScriptPvfPath = "", SkillScriptPath = root };
        var scripts = new ScriptFileSystem(options, NullLogger<ScriptFileSystem>.Instance);
        return new DungeonCatalog(scripts,
            new ObjectDropGenerator(new ServerSettings(),
                new ObjectDropCatalog(scripts, NullLogger<ObjectDropCatalog>.Instance),
                new GoldDropCatalog(scripts, NullLogger<GoldDropCatalog>.Instance)),
            NullLogger<DungeonCatalog>.Instance);
    }
}
