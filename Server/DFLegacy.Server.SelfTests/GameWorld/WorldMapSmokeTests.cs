using DFLegacy.Protocol;
using DFLegacy.Server;
using Microsoft.Extensions.Logging.Abstractions;

internal static class WorldMapSmokeTests
{
    public static void Run(Action<bool, string> check)
    {
        check(GameProtocolEngine.CreateEnterSelectDungeon(false, []).Payload
            .SequenceEqual(new byte[] { 0, 0 }), "NOTI 27 blocks hell quests with string 5431");
        check(GameProtocolEngine.CreateEnterSelectDungeon(true, [0]).Payload
            .SequenceEqual(new byte[] { 1, 1, 0, 0 }), "NOTI 27 names missing-item party slot zero with string 5430");
        check(GameProtocolEngine.CreateEnterSelectDungeon(false, [0, 3]).Payload
            .SequenceEqual(new byte[] { 0, 2, 0, 0, 3, 0 }), "NOTI 27 carries independent quest and item restrictions");

        var root = Path.Combine(Path.GetTempPath(), $"dflegacy-worldmap-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "worldmap"));
            Directory.CreateDirectory(Path.Combine(root, "town"));
            File.WriteAllText(Path.Combine(root, "worldmap/worldmap.lst"), "3 `sky.wdm`\n4 `sea.wdm`\n");
            File.WriteAllText(Path.Combine(root, "town/town.lst"), "3 `coast.twn`\n");
            File.WriteAllText(Path.Combine(root, "town/coast.twn"), """
                [area]
                3 // gate area
                `town.map`
                `[minimap rect]`
                0 0 10 10
                `[/minimap rect]`
                `[dungeon gate]` // world map, not dungeon index
                3
                [/area]
                [area]
                4
                `[dungeon gate]`
                4
                [/area]
                """);
            File.WriteAllText(Path.Combine(root, "worldmap/sky.wdm"), """
                [dungeon]
                11 -1 // comment 999 must not become a requirement
                17 363
                [/dungeon]
                [hell dungeon] 1
                [hell quest]
                2601 2602
                [/hell quest]
                [item condition]
                4150 3
                4151 1
                [/item condition]
                """);
            File.WriteAllText(Path.Combine(root, "worldmap/sea.wdm"), "[dungeon]\n21 -1\n[/dungeon]\n[hell dungeon] 0\n");
            var scripts = new ScriptFileSystem(new ServerOptions
            {
                ScriptPvfPath = string.Empty,
                SkillScriptPath = root
            }, NullLogger<ScriptFileSystem>.Instance);
            var catalog = new WorldMapCatalog(scripts);
            check(catalog.TryGetForGate(3, 3, out var map) && map.Id == 3
                && map.DungeonIds.SequenceEqual(new ushort[] { 11, 17 }), "town gate resolves its own worldmap list");
            check(!catalog.TryGetForGate(3, 0, out _), "normal town area does not inherit another gate's hell permissions");
            check(catalog.TryGetForGate(3, 4, out var other) && !other.MeetsHellQuests(new HashSet<ushort>()),
                "worldmap without hell cannot activate hell mode");
            check(!map.MeetsHellQuests(new HashSet<ushort> { 2601 })
                && map.MeetsHellQuests(new HashSet<ushort> { 2601, 2602 }), "all hell quests must be completed, not merely accepted");
            var inventory = new[]
            {
                new CharacterItemRecord(9, 4150, 1),
                new CharacterItemRecord(10, 4150, 4),
                new CharacterItemRecord(11, 4151, 1)
            }.ToDictionary(item => item.Slot);
            check(map.TryConsumeHellItems(inventory, out var remaining)
                && !remaining.ContainsKey(9) && !remaining.ContainsKey(11)
                && remaining[10].CountOrValue == 2 && inventory[10].CountOrValue == 4,
                "hell item plan consumes exact configured counts across stacks without mutating original inventory");
            inventory.Remove(11);
            check(!map.TryConsumeHellItems(inventory, out remaining) && remaining.Count == inventory.Count
                && remaining[10].CountOrValue == 4, "missing one hell item causes no partial consumption");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
