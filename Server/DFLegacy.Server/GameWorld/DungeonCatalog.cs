using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

public enum DungeonMapType : byte
{
    Normal,
    Boss,
    Dummy
}

public sealed record DungeonDefinition(
    ushort DungeonId,
    byte MinimumLevel,
    byte BasisLevel,
    byte Width,
    byte Height,
    byte StartX,
    byte StartY,
    byte BossX,
    byte BossY,
    string RoomTopology)
{
    public DungeonPassiveItemGroup[] PassiveItemGroups { get; init; } = [];

    public bool GoldCardUse { get; init; } = true;

    public double ExperienceMultiplier { get; init; } = 1;

    public DungeonMazeDefinition[] Mazes { get; init; } = [];

    public bool IsHellDungeon { get; init; }

    public ushort SealDoorMapId { get; init; }

    public DungeonRoomPosition SealDoorPosition { get; init; }

    public int DummyAppearCount { get; init; }

    public ushort WarRoomMapId { get; init; }

    public ushort[] HellTicketItemIds { get; init; } = [];

    // DF2008 stores one value for each of its four dungeon difficulties. The
    // selected value is an expected maze-wide amount, not an exact quota.
    public int[] ChampionCounts { get; init; } = new int[4];
}

public readonly record struct DungeonRoomPosition(byte X, byte Y);

public sealed record DungeonMazeDefinition(
    byte MazeIndex,
    byte Width,
    byte Height,
    string RoomTopology,
    DungeonRoomPosition[] StartPositions,
    DungeonRoomPosition[] BossPositions,
    IReadOnlyDictionary<DungeonRoomPosition, ushort[]> MapSpecifications);

public sealed class DungeonLayout
{
    private readonly Dictionary<DungeonRoomPosition, ushort> _resolvedMaps = new();

    internal DungeonLayout(
        ushort dungeonId,
        DungeonMazeDefinition maze,
        DungeonRoomPosition start,
        DungeonRoomPosition boss)
    {
        DungeonId = dungeonId;
        Maze = maze;
        StartX = start.X;
        StartY = start.Y;
        BossX = boss.X;
        BossY = boss.Y;
    }

    public ushort DungeonId { get; }

    public DungeonMazeDefinition Maze { get; }

    public byte MazeIndex => Maze.MazeIndex;

    public byte Width => Maze.Width;

    public byte Height => Maze.Height;

    public byte StartX { get; }

    public byte StartY { get; }

    public byte BossX { get; }

    public byte BossY { get; }

    internal bool TryGetResolvedMap(DungeonRoomPosition position, out ushort mapId) =>
        _resolvedMaps.TryGetValue(position, out mapId);

    internal void SetResolvedMap(DungeonRoomPosition position, ushort mapId) =>
        _resolvedMaps.TryAdd(position, mapId);
}

public sealed record DungeonPassiveWeightedItem(ushort ItemId, int Weight);

public sealed record DungeonPassiveItemGroup(
    byte Index,
    short LevelOverride,
    DungeonPassiveWeightedItem[] Items);

public sealed record DungeonPassiveObject(
    byte ObjectIndex,
    ushort PassiveObjectId,
    byte? ItemAssociationSlot)
{
    public ushort X { get; init; }

    public ushort Y { get; init; }
}

public sealed record DungeonPassiveItemSpawn(
    byte PassiveObjectIndex,
    byte AssociationSlot,
    DungeonGeneratedDrop Drop)
{
    public ushort X { get; init; }

    public ushort Y { get; init; }
}

public sealed record DungeonRoomDefinition(
    ushort MapId,
    DungeonMapType MapType,
    GameDungeonMonster[] Monsters,
    char Topology,
    bool HasScriptBossActor,
    DungeonPassiveObject[] PassiveObjects,
    DungeonPassiveItemSpawn[] PassiveItemSpawns)
{
    public bool IsHellRoom { get; init; }

    // The old client treats this MAP-level value as a fixed number of normal
    // monsters to promote before applying the DGN per-room chance.
    public int BaseChampionCount { get; init; }

    // START_MAP stores this value in the client's current-map state. Keep it
    // stable for the lifetime of a generated room, including revisits.
    public uint ClientRandomSeed { get; init; }
}

public sealed class DungeonCatalog
{
    private static readonly Encoding ScriptEncoding = CreateScriptEncoding();
    private static readonly Regex ListEntryPattern = new(
        @"(?m)^\s*(\d+)\s+`([^`]+)`",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex IntegerPattern = new(
        @"-?\d+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CoordinatePattern = new(
        @"\((-?\d+)\s*[,\.]\s*(-?\d+)\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ILogger<DungeonCatalog> _logger;
    private readonly ObjectDropGenerator _objectDropGenerator;
    private readonly Lazy<CatalogState> _state;

    public DungeonCatalog(
        ScriptFileSystem scripts,
        ObjectDropGenerator objectDropGenerator,
        ILogger<DungeonCatalog> logger)
    {
        _logger = logger;
        _objectDropGenerator = objectDropGenerator;
        _state = new Lazy<CatalogState>(
            () => Load(scripts),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public IReadOnlyList<ushort> DungeonIds => _state.Value.Definitions.Keys
        .OrderBy(id => id)
        .ToArray();

    public IReadOnlyList<ushort> HellDungeonIds => _state.Value.Definitions.Values
        .Where(definition => definition.IsHellDungeon)
        .Select(definition => definition.DungeonId)
        .OrderBy(id => id)
        .ToArray();

    public bool TryGetDefinition(ushort dungeonId, out DungeonDefinition definition) =>
        _state.Value.Definitions.TryGetValue(dungeonId, out definition!);

    public bool IsHellDungeon(ushort dungeonId) =>
        TryGetDefinition(dungeonId, out var definition) && definition.IsHellDungeon;

    public bool TryGetHellRoom(
        ushort dungeonId,
        out ushort mapId,
        out byte roomX,
        out byte roomY)
    {
        mapId = 0;
        roomX = 0;
        roomY = 0;
        if (!TryGetDefinition(dungeonId, out var definition)
            || !definition.IsHellDungeon
            || definition.SealDoorMapId == 0)
        {
            return false;
        }

        mapId = definition.SealDoorMapId;
        roomX = definition.SealDoorPosition.X;
        roomY = definition.SealDoorPosition.Y;
        return true;
    }

    public bool TryGetMapType(ushort mapId, out DungeonMapType mapType)
    {
        mapType = DungeonMapType.Normal;
        if (!_state.Value.Maps.TryGetValue(mapId, out var map))
        {
            return false;
        }

        mapType = map.MapType;
        return true;
    }

    public bool IsBossRoom(ushort dungeonId, byte roomX, byte roomY) =>
        TryGetDefinition(dungeonId, out var definition)
        && definition.BossX == roomX
        && definition.BossY == roomY;

    public bool TryCreateLayout(
        ushort dungeonId,
        out DungeonLayout layout,
        int partyMemberCount = 1,
        IDropRandomSource? random = null)
    {
        layout = null!;
        var state = _state.Value;
        if (!state.Definitions.TryGetValue(dungeonId, out var definition))
        {
            return false;
        }

        random ??= GameRandomSource.Shared;
        var mazes = definition.Mazes.Length > 0
            ? definition.Mazes
            : [CreateLegacyMaze(definition, state.MapSpecifications.GetValueOrDefault(dungeonId))];
        var eligibleMazes = mazes
            .Where(maze => maze.StartPositions.Length > 0)
            .ToArray();
        if (eligibleMazes.Length == 0)
        {
            return false;
        }

        // DFLegacy scripts do not carry the later party-size maze bounds. Keep
        // the argument in the API so those bounds can be honored if they are
        // recovered, but select uniformly just like CDungeon::GetRandMaze.
        _ = partyMemberCount;
        var maze = eligibleMazes[random.Next(eligibleMazes.Length)];
        var start = maze.StartPositions[random.Next(maze.StartPositions.Length)];
        var bossCandidates = GetSelectableBossPositions(definition, maze, start);
        var boss = bossCandidates.Length > 0
            ? bossCandidates[random.Next(bossCandidates.Length)]
            : new DungeonRoomPosition(byte.MaxValue, byte.MaxValue);
        layout = new DungeonLayout(dungeonId, maze, start, boss);
        return true;
    }

    internal static DungeonRoomPosition[] GetSelectableBossPositions(
        DungeonDefinition definition,
        DungeonMazeDefinition maze,
        DungeonRoomPosition start)
    {
        var candidates = definition.DummyAppearCount > 0
            ? maze.BossPositions.Take(1).ToArray()
            : maze.BossPositions.Where(position => position != start).ToArray();
        return candidates.Length > 0 ? candidates : maze.BossPositions;
    }

    public int GetChampionTargetCount(
        DungeonLayout layout,
        byte difficulty,
        DungeonRoomDefinition room,
        IDropRandomSource? random = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(room);
        if (!_state.Value.Definitions.TryGetValue(layout.DungeonId, out var definition))
        {
            return 0;
        }

        var baseCount = Math.Max(0, room.BaseChampionCount);
        if (definition.ChampionCounts.Length == 0)
        {
            return baseCount;
        }

        var difficultyIndex = Math.Clamp(
            difficulty,
            byte.MinValue,
            checked((byte)(definition.ChampionCounts.Length - 1)));
        var chance = CalculateAdditionalChampionChance(
            definition.ChampionCounts[difficultyIndex],
            layout.Width,
            layout.Height);
        if (chance <= 0)
        {
            return baseCount;
        }

        random ??= GameRandomSource.Shared;
        return baseCount + (random.Next(100) < chance ? 1 : 0);
    }

    internal static int CalculateAdditionalChampionChance(
        int dungeonChampionValue,
        byte mazeWidth,
        byte mazeHeight)
    {
        var roomCount = Math.Max(1, mazeWidth * mazeHeight);
        return checked((int)Math.Clamp(
            Math.Max(0L, dungeonChampionValue) * 100L / roomCount,
            0L,
            100L));
    }

    internal static GameDungeonMonster[] PromoteChampions(
        IEnumerable<GameDungeonMonster>? monsters,
        int count,
        IDropRandomSource? random = null)
    {
        var promoted = (monsters ?? []).ToArray();
        if (count <= 0 || promoted.Length == 0)
        {
            return promoted;
        }

        random ??= GameRandomSource.Shared;
        var candidates = Enumerable.Range(0, promoted.Length)
            .Where(index => IsChampionCandidate(promoted[index]))
            .ToList();
        for (var promotedCount = 0;
             promotedCount < count && candidates.Count > 0;
             promotedCount++)
        {
            var selectedCandidate = random.Next(candidates.Count);
            var index = candidates[selectedCandidate];
            candidates[selectedCandidate] = candidates[^1];
            candidates.RemoveAt(candidates.Count - 1);
            promoted[index] = promoted[index] with
            {
                Type = GameDungeonMonsterTypes.Champion
            };
        }

        return promoted;
    }

    private static bool IsChampionCandidate(GameDungeonMonster monster) =>
        monster.Type == GameDungeonMonsterTypes.Normal
        && monster.IsBoxMonster == 0
        && !monster.IsHellHidden;

    public bool TryGetRoomTopology(
        ushort dungeonId,
        byte roomX,
        byte roomY,
        out char topology)
    {
        topology = '\0';
        return TryGetDefinition(dungeonId, out var definition)
            && TryGetRoomTopology(definition, roomX, roomY, out topology);
    }

    public bool MapMatchesRoomTopology(
        ushort dungeonId,
        byte roomX,
        byte roomY,
        ushort mapId)
    {
        var state = _state.Value;
        return state.Definitions.TryGetValue(dungeonId, out var definition)
            && state.Maps.TryGetValue(mapId, out var map)
            && (!TryGetRoomTopology(definition, roomX, roomY, out var topology)
                || MapSupportsTopology(map, topology));
    }

    public bool TryApplyQuestMap(
        DungeonLayout layout,
        IEnumerable<QuestDefinition> unfinishedQuests,
        out ushort questMapId,
        IDropRandomSource? random = null)
    {
        questMapId = 0;
        var state = _state.Value;
        var bosses = layout.Maze.BossPositions;
        var bossIndex = Array.FindIndex(bosses,
            point => point.X == layout.BossX && point.Y == layout.BossY);
        if (bossIndex < 0 || bosses.Length < 2)
        {
            return false;
        }

        // 13339 SetGridPath (0x82FFF70) uses the next boss candidate,
        // or the previous candidate when the selected boss is last.
        var position = bosses[bossIndex + 1 < bosses.Length ? bossIndex + 1 : bossIndex - 1];
        if (position.X == layout.StartX && position.Y == layout.StartY
            || position.X == layout.BossX && position.Y == layout.BossY
            || layout.TryGetResolvedMap(position, out _))
        {
            return false;
        }

        random ??= GameRandomSource.Shared;
        foreach (var quest in unfinishedQuests)
        {
            var appearance = quest.AppearMap;
            // Dungeon-wide rescue-map form: dungeon, -1, map, chance (%).
            if (appearance.Length < 4 || appearance[0] != layout.DungeonId
                || appearance[1] != -1 || appearance[2] is <= 0 or > ushort.MaxValue
                || appearance[3] <= 0
                || !state.Maps.TryGetValue((ushort)appearance[2], out var map)
                || map.MapType != DungeonMapType.Normal)
            {
                continue;
            }

            if (appearance[3] < 100 && random.Next(100) >= appearance[3])
            {
                continue;
            }

            // Rescue maps can omit [dungeon] and [greed]; never put them in
            // the general random pool. Persist this choice for room revisits.
            questMapId = map.MapId;
            layout.SetResolvedMap(position, questMapId);
            return true;
        }

        return false;
    }

    public bool TryCreateRoom(
        ushort dungeonId,
        byte roomX,
        byte roomY,
        out DungeonRoomDefinition room,
        byte dungeonDifficulty = 0,
        IDropRandomSource? random = null)
    {
        var state = _state.Value;
        if (!state.Definitions.TryGetValue(dungeonId, out var definition))
        {
            room = null!;
            return false;
        }

        var maze = definition.Mazes.FirstOrDefault()
            ?? CreateLegacyMaze(definition, state.MapSpecifications.GetValueOrDefault(dungeonId));
        var start = maze.StartPositions.FirstOrDefault(
            new DungeonRoomPosition(definition.StartX, definition.StartY));
        var boss = maze.BossPositions.FirstOrDefault(
            new DungeonRoomPosition(definition.BossX, definition.BossY));
        return TryCreateRoom(
            new DungeonLayout(dungeonId, maze, start, boss),
            roomX,
            roomY,
            out room,
            dungeonDifficulty,
            random);
    }

    public bool TryCreateRoom(
        DungeonLayout layout,
        byte roomX,
        byte roomY,
        out DungeonRoomDefinition room,
        byte dungeonDifficulty = 0,
        IDropRandomSource? random = null)
    {
        room = null!;
        var state = _state.Value;
        if (!state.Definitions.TryGetValue(layout.DungeonId, out var definition)
            || roomX >= layout.Width
            || roomY >= layout.Height)
        {
            return false;
        }

        var point = new DungeonRoomPosition(roomX, roomY);
        var isStartRoom = layout.StartX == roomX && layout.StartY == roomY;
        var isBossRoom = layout.BossX == roomX && layout.BossY == roomY;
        var isSpecifiedRoom = layout.Maze.MapSpecifications.ContainsKey(point);
        var hasTopology = TryGetRoomTopology(layout.Maze, roomX, roomY, out var parsedTopology);
        if (hasTopology
            && !IsOpenRoomTopology(parsedTopology)
            && !isStartRoom
            && !isBossRoom
            && !isSpecifiedRoom)
        {
            return false;
        }

        var topology = hasTopology
            ? parsedTopology
            : '\0';
        random ??= GameRandomSource.Shared;
        var mapId = ResolveMapId(state, definition, layout, roomX, roomY, random);
        if (mapId == 0 || !state.Maps.TryGetValue(mapId, out var map))
        {
            return false;
        }

        return TryBuildRoom(
            state,
            definition,
            map,
            topology,
            dungeonDifficulty,
            random,
            isHellRoom: false,
            out room);
    }

    public bool TryCreateHellRoom(
        DungeonLayout layout,
        byte dungeonDifficulty,
        out DungeonRoomDefinition room,
        IDropRandomSource? random = null)
    {
        room = null!;
        var state = _state.Value;
        if (!state.Definitions.TryGetValue(layout.DungeonId, out var definition)
            || !definition.IsHellDungeon
            || definition.SealDoorMapId == 0
            || !state.Maps.TryGetValue(definition.SealDoorMapId, out var map))
        {
            return false;
        }

        var position = definition.SealDoorPosition;
        var topology = TryGetRoomTopology(layout.Maze, position.X, position.Y, out var parsed)
            ? parsed
            : '\0';
        return TryBuildRoom(
            state,
            definition,
            map,
            topology,
            dungeonDifficulty,
            random ?? GameRandomSource.Shared,
            isHellRoom: true,
            out room);
    }

    private bool TryBuildRoom(
        CatalogState state,
        DungeonDefinition definition,
        MapDefinition map,
        char topology,
        byte dungeonDifficulty,
        IDropRandomSource random,
        bool isHellRoom,
        out DungeonRoomDefinition room)
    {
        var monsterRows = map.Monsters.Take(byte.MaxValue).ToArray();
        var mapMarksBoss = monsterRows.Any(monster =>
            monster.Type == GameDungeonMonsterTypes.Boss);
        var monsters = monsterRows
            .Select((monster, index) =>
            {
                var level = monster.UsesDungeonLevel
                    ? definition.BasisLevel + monster.LevelOffsetOrFixedLevel
                    : monster.LevelOffsetOrFixedLevel;
                if (level <= 0)
                {
                    level = definition.BasisLevel;
                }

                return new GameDungeonMonster(
                    MapListIndex: checked((byte)index),
                    UniqueId: checked((ushort)index),
                    MonsterIndex: monster.MonsterIndex,
                    Level: checked((byte)Math.Clamp(level, 1, byte.MaxValue)),
                    Type: monster.Type,
                    X: monster.X,
                    Y: monster.Y);
            })
            .ToList();
        foreach (var passiveObject in map.PassiveObjects)
        {
            foreach (var action in passiveObject.Actions
                         .Where(action => action.Type == PassiveObjectActionType.Monster))
            {
                if (monsters.Count >= byte.MaxValue
                    || action.Value1 is <= 0 or > ushort.MaxValue
                    || action.Value2 is <= 0 or > byte.MaxValue
                    || action.Value3 <= 0
                    || action.Value4 <= 0)
                {
                    continue;
                }

                var threshold = Math.Clamp(
                    100 * action.Value3 / action.Value4,
                    0,
                    100);
                if (random.Next(100) + 1 > threshold)
                {
                    continue;
                }

                var monsterId = checked((ushort)action.Value1);
                monsters.Add(new GameDungeonMonster(
                    MapListIndex: action.ActionIndex,
                    UniqueId: checked((ushort)monsters.Count),
                    MonsterIndex: monsterId,
                    Level: checked((byte)action.Value2),
                    Type: GameDungeonMonsterTypes.Normal,
                    IsBoxMonster: 1,
                    BoxIndex: passiveObject.ObjectIndex,
                    IsHellHidden: isHellRoom && state.HellDemonIds.Contains(monsterId),
                    X: passiveObject.X,
                    Y: passiveObject.Y));
            }
        }

        var passiveItemSpawns = new List<DungeonPassiveItemSpawn>();
        foreach (var passiveObject in map.PassiveObjects)
        {
            if (passiveObject.ItemAssociationSlot is not { } associationSlot)
            {
                continue;
            }

            foreach (var action in passiveObject.Actions
                         .Where(action => action.Type == PassiveObjectActionType.Item))
            {
                if (action.Value1 < 0
                    || action.Value1 >= definition.PassiveItemGroups.Length)
                {
                    continue;
                }

                var group = definition.PassiveItemGroups[action.Value1];
                var generationLevel = group.LevelOverride > 0
                    ? (byte)Math.Clamp((int)group.LevelOverride, 1, byte.MaxValue)
                    : definition.BasisLevel;
                if (!_objectDropGenerator.Enabled)
                {
                    continue;
                }

                for (var draw = 0;
                     draw < Math.Clamp(action.Value2, 0, byte.MaxValue)
                     && passiveItemSpawns.Count < byte.MaxValue;
                     draw++)
                {
                    if (TryGenerateSpecificPassiveItem(group, random, out var generated))
                    {
                        passiveItemSpawns.Add(new DungeonPassiveItemSpawn(
                            passiveObject.ObjectIndex,
                            associationSlot,
                            generated)
                        {
                            X = passiveObject.X,
                            Y = passiveObject.Y
                        });
                    }
                }

                for (var draw = 0;
                     draw < Math.Clamp(action.Value3, 0, byte.MaxValue)
                     && passiveItemSpawns.Count < byte.MaxValue;
                     draw++)
                {
                    foreach (var generated in _objectDropGenerator.Generate(
                                 generationLevel,
                                 dungeonDifficulty,
                                 random))
                    {
                        if (passiveItemSpawns.Count >= byte.MaxValue)
                        {
                            break;
                        }

                        passiveItemSpawns.Add(new DungeonPassiveItemSpawn(
                            passiveObject.ObjectIndex,
                            associationSlot,
                            generated)
                        {
                            X = passiveObject.X,
                            Y = passiveObject.Y
                        });
                    }
                }
            }
        }

        room = new DungeonRoomDefinition(
            map.MapId,
            map.MapType,
            monsters.ToArray(),
            topology,
            mapMarksBoss,
            map.PassiveObjects
                .Select(passiveObject => new DungeonPassiveObject(
                    passiveObject.ObjectIndex,
                    passiveObject.PassiveObjectId,
                    passiveObject.ItemAssociationSlot)
                {
                    X = passiveObject.X,
                    Y = passiveObject.Y
                })
                .ToArray(),
            passiveItemSpawns.ToArray())
        {
            IsHellRoom = isHellRoom,
            BaseChampionCount = map.BaseChampionCount
        };
        return true;
    }

    private static bool TryGenerateSpecificPassiveItem(
        DungeonPassiveItemGroup group,
        IDropRandomSource random,
        out DungeonGeneratedDrop generated)
    {
        generated = null!;
        var roll = random.Next(10_000);
        var cumulative = 0;
        foreach (var item in group.Items)
        {
            cumulative = Math.Min(10_000, cumulative + item.Weight);
            if (roll < cumulative)
            {
                generated = new DungeonGeneratedDrop(
                    IsGold: false,
                    item.ItemId,
                    CountOrValue: 1);
                return true;
            }
        }

        return false;
    }

    private CatalogState Load(ScriptFileSystem scripts)
    {
        const string dungeonListPath = "dungeon/dungeon.lst";
        const string mapListPath = "map/map.lst";
        if (!scripts.FileExists(dungeonListPath)
            || !scripts.FileExists(mapListPath))
        {
            _logger.LogWarning(
                "Dungeon or map list is missing in {Source}; using the built-in Lorien fallback.",
                scripts.SourceDescription);
            return CreateFallbackState();
        }

        try
        {
            var hellDemonIds = LoadHellDemonIds(scripts);
            var hellTicketItems = LoadHellTicketItems(scripts);
            var maps = LoadMaps(scripts, mapListPath);
            var definitions = new Dictionary<ushort, DungeonDefinition>();
            var mapSpecifications = new Dictionary<
                ushort,
                IReadOnlyDictionary<DungeonRoomPosition, ushort[]>>();
            var dungeonList = scripts.ReadAllText(dungeonListPath, ScriptEncoding);
            foreach (Match entry in ListEntryPattern.Matches(dungeonList))
            {
                if (!ushort.TryParse(entry.Groups[1].Value, out var dungeonId))
                {
                    continue;
                }

                var relativePath = NormalizeRelativePath(entry.Groups[2].Value);
                var dungeonPath = $"dungeon/{relativePath}";
                if (!scripts.FileExists(dungeonPath))
                {
                    _logger.LogWarning(
                        "Dungeon {DungeonId} script was not found at {Path}.",
                        dungeonId,
                        dungeonPath);
                    continue;
                }

                var text = scripts.ReadAllText(dungeonPath, ScriptEncoding);
                if (!TryParseDungeon(
                        dungeonId,
                        text,
                        hellTicketItems.GetValueOrDefault(dungeonId) ?? [],
                        out var definition,
                        out var specifications))
                {
                    _logger.LogWarning(
                        "Dungeon {DungeonId} has no usable first maze in {Path}.",
                        dungeonId,
                        dungeonPath);
                    continue;
                }

                definitions[dungeonId] = definition;
                mapSpecifications[dungeonId] = specifications;
            }

            if (definitions.Count == 0)
            {
                _logger.LogWarning(
                    "No DFLegacy dungeons could be parsed below {ScriptRoot}; using the Lorien fallback.",
                    scripts.SourceDescription);
                return CreateFallbackState();
            }

            _logger.LogInformation(
                "Loaded {DungeonCount} DFLegacy dungeons and {MapCount} map definitions from {ScriptRoot}.",
                definitions.Count,
                maps.Count,
                scripts.SourceDescription);
            return new CatalogState(definitions, maps, mapSpecifications, hellDemonIds);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Failed to load DFLegacy dungeon scripts; using the Lorien fallback.");
            return CreateFallbackState();
        }
    }

    private static IReadOnlySet<ushort> LoadHellDemonIds(ScriptFileSystem scripts)
    {
        const string monsterListPath = "monster/monster.lst";
        if (!scripts.FileExists(monsterListPath))
        {
            return new HashSet<ushort>();
        }

        var result = new HashSet<ushort>();
        var monsterList = scripts.ReadAllText(monsterListPath, ScriptEncoding);
        foreach (Match entry in ListEntryPattern.Matches(monsterList))
        {
            if (ushort.TryParse(entry.Groups[1].Value, out var monsterId)
                && NormalizeRelativePath(entry.Groups[2].Value)
                    .Contains("cosmofiend/", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(monsterId);
            }
        }

        return result;
    }

    private static IReadOnlyDictionary<ushort, ushort[]> LoadHellTicketItems(
        ScriptFileSystem scripts)
    {
        var result = new Dictionary<ushort, ushort[]>();
        foreach (var path in scripts.EnumeratePaths("worldmap/")
                     .Concat(scripts.EnumeratePaths("worldmap2/"))
                     .Where(path => path.EndsWith(".wdm", StringComparison.OrdinalIgnoreCase))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var text = scripts.ReadAllText(path, ScriptEncoding);
            if (ParseInlineIntegers(text, "hell dungeon").FirstOrDefault() == 0)
            {
                continue;
            }

            var ticketIds = ParseTagRows(text, "item condition")
                .Select(values => values.FirstOrDefault())
                .Where(value => value is > 0 and <= ushort.MaxValue)
                .Select(value => checked((ushort)value))
                .Distinct()
                .ToArray();
            if (ticketIds.Length == 0)
            {
                continue;
            }

            foreach (var dungeonId in ParseTagRows(text, "dungeon")
                         .Select(values => values.FirstOrDefault())
                         .Where(value => value is > 0 and <= ushort.MaxValue)
                         .Select(value => checked((ushort)value)))
            {
                result[dungeonId] = ticketIds;
            }
        }

        return result;
    }

    private static IEnumerable<int[]> ParseTagRows(string text, string tag)
    {
        var pattern = $@"(?ims)^\s*\[{Regex.Escape(tag)}\]\s*(.*?)(?=^\s*\[[^/\r\n][^\]]*\]|\z)";
        foreach (Match match in Regex.Matches(text, pattern, RegexOptions.CultureInvariant))
        {
            foreach (var line in ParseDataLines(match.Groups[1].Value))
            {
                var values = ParseIntegers(line);
                if (values.Length > 0)
                {
                    yield return values;
                }
            }
        }
    }

    private static Dictionary<ushort, MapDefinition> LoadMaps(
        ScriptFileSystem scripts,
        string mapListPath)
    {
        var maps = new Dictionary<ushort, MapDefinition>();
        var mapList = scripts.ReadAllText(mapListPath, ScriptEncoding);
        foreach (Match entry in ListEntryPattern.Matches(mapList))
        {
            if (!ushort.TryParse(entry.Groups[1].Value, out var mapId))
            {
                continue;
            }

            var relativePath = NormalizeRelativePath(entry.Groups[2].Value);
            var mapPath = $"map/{relativePath}";
            if (!scripts.FileExists(mapPath))
            {
                continue;
            }

            var text = scripts.ReadAllText(mapPath, ScriptEncoding);
            var dungeonIds = ParseAllBlockIntegers(text, "dungeon")
                .Where(value => value is >= ushort.MinValue and <= ushort.MaxValue)
                .Select(value => (ushort)value)
                .Distinct()
                .ToArray();
            var monsters = ParseMonsterRows(text);
            var specialPassiveObjects = ParseSpecialPassiveObjects(text);
            var passiveObjects = specialPassiveObjects
                .Concat(ParsePassiveObjectRows(text, specialPassiveObjects.Length))
                .Take(byte.MaxValue)
                .ToArray();
            var fileName = Path.GetFileNameWithoutExtension(relativePath);
            var coordinateMatch = CoordinatePattern.Match(fileName);
            int? coordinateX = coordinateMatch.Success
                ? int.Parse(coordinateMatch.Groups[1].Value)
                : null;
            int? coordinateY = coordinateMatch.Success
                ? int.Parse(coordinateMatch.Groups[2].Value)
                : null;
            maps[mapId] = new MapDefinition(
                mapId,
                dungeonIds,
                ClassifyMap(fileName),
                ParseMapType(text),
                coordinateX,
                coordinateY,
                ParseTopologySymbols(text),
                ParseStandaloneMapChampionCount(text),
                monsters,
                passiveObjects);
        }

        return maps;
    }

    private static bool TryParseDungeon(
        ushort dungeonId,
        string text,
        ushort[] hellTicketItemIds,
        out DungeonDefinition definition,
        out IReadOnlyDictionary<DungeonRoomPosition, ushort[]> specifications)
    {
        definition = null!;
        specifications = new Dictionary<DungeonRoomPosition, ushort[]>();
        var mazes = ParseMazes(text);
        if (mazes.Length == 0)
        {
            return false;
        }

        var firstMaze = mazes[0];
        var firstStart = firstMaze.StartPositions[0];
        var firstBoss = firstMaze.BossPositions.FirstOrDefault(
            new DungeonRoomPosition(byte.MaxValue, byte.MaxValue));
        var minimumLevel = ParseInlineIntegers(text, "minimum required level")
            .FirstOrDefault(1);
        var basisLevel = ParseInlineIntegers(text, "basis level")
            .FirstOrDefault(Math.Max(1, minimumLevel));
        var goldCardUse = !TryParseTaggedInteger(
                text,
                "gold card use",
                out var goldCardUseValue)
            || goldCardUseValue != 0;
        var isHellDungeon = ParseInlineIntegers(text, "hell dungeon")
            .FirstOrDefault() != 0;
        var sealDoorMap = ParseInlineIntegers(text, "seal door map index")
            .FirstOrDefault();
        var sealDoorPosition = ParseInlineIntegers(text, "seal door pos");
        var rawDummyAppearCount = ParseInlineIntegers(text, "dummy appear count")
            .FirstOrDefault();
        var dummyAppearCount = Math.Clamp(
            rawDummyAppearCount,
            0,
            Math.Max(0, firstMaze.BossPositions.Length - 1));
        var warRoomMap = ParseInlineIntegers(text, "warroom map index")
            .FirstOrDefault();
        definition = new DungeonDefinition(
            dungeonId,
            (byte)Math.Clamp(minimumLevel, 1, byte.MaxValue),
            (byte)Math.Clamp(basisLevel, 1, byte.MaxValue),
            firstMaze.Width,
            firstMaze.Height,
            firstStart.X,
            firstStart.Y,
            firstBoss.X,
            firstBoss.Y,
            firstMaze.RoomTopology)
        {
            PassiveItemGroups = ParsePassiveItemGroups(text),
            GoldCardUse = goldCardUse,
            ExperienceMultiplier = TryParseTaggedDouble(
                    text,
                    "experience increasing point",
                    out var experienceMultiplier)
                ? Math.Max(0, experienceMultiplier)
                : 1,
            Mazes = mazes,
            IsHellDungeon = isHellDungeon,
            SealDoorMapId = sealDoorMap is > 0 and <= ushort.MaxValue
                ? checked((ushort)sealDoorMap)
                : (ushort)0,
            SealDoorPosition = new DungeonRoomPosition(
                checked((byte)Math.Clamp(
                    sealDoorPosition.FirstOrDefault(),
                    byte.MinValue,
                    byte.MaxValue)),
                checked((byte)Math.Clamp(
                    sealDoorPosition.Skip(1).FirstOrDefault(),
                    byte.MinValue,
                    byte.MaxValue))),
            DummyAppearCount = dummyAppearCount,
            WarRoomMapId = warRoomMap is > 0 and <= ushort.MaxValue
                ? checked((ushort)warRoomMap)
                : (ushort)0,
            HellTicketItemIds = hellTicketItemIds,
            ChampionCounts = NormalizeChampionCounts(
                ParseInlineIntegers(text, "champion"))
        };
        specifications = firstMaze.MapSpecifications;
        return true;
    }

    private static DungeonMazeDefinition[] ParseMazes(string text)
    {
        var markers = Regex.Matches(
            text,
            @"(?im)^\s*\[maze info\]\s*$",
            RegexOptions.CultureInvariant);
        var result = new List<DungeonMazeDefinition>(markers.Count);
        for (var mazeIndex = 0;
             mazeIndex < markers.Count && mazeIndex <= byte.MaxValue;
             mazeIndex++)
        {
            var start = markers[mazeIndex].Index;
            var end = mazeIndex + 1 < markers.Count
                ? markers[mazeIndex + 1].Index
                : text.Length;
            var mazeText = text[start..end];
            var size = ParseInlineIntegers(mazeText, "size");
            if (size.Length < 2
                || size[0] is <= 0 or > byte.MaxValue
                || size[1] is <= 0 or > byte.MaxValue)
            {
                continue;
            }

            var width = checked((byte)size[0]);
            var height = checked((byte)size[1]);
            var startValues = ParseFirstBlockIntegers(mazeText, "start map");
            var starts = ParseRoomPositions(startValues, width, height);
            if (starts.Length == 0)
            {
                starts = [new DungeonRoomPosition(0, 0)];
            }

            var bossValues = ParseFirstBlockIntegers(mazeText, "boss map");
            var bosses = ParseRoomPositions(bossValues, width, height);
            if (bossValues.Length == 0)
            {
                bosses =
                [
                    new DungeonRoomPosition(
                        checked((byte)(width - 1)),
                        checked((byte)(height - 1)))
                ];
            }

            var topology = ParseDungeonRoomTopology(mazeText, width, height);
            result.Add(new DungeonMazeDefinition(
                checked((byte)mazeIndex),
                width,
                height,
                topology,
                starts,
                bosses,
                ParseMapSpecifications(mazeText, width, height)));
        }

        return result.ToArray();
    }

    private static DungeonRoomPosition[] ParseRoomPositions(
        IReadOnlyList<int> values,
        byte width,
        byte height)
    {
        var result = new List<DungeonRoomPosition>(values.Count / 2);
        for (var index = 0; index + 1 < values.Count; index += 2)
        {
            var x = values[index];
            var y = values[index + 1];
            if (x < 0 || y < 0 || x >= width || y >= height)
            {
                continue;
            }

            var position = new DungeonRoomPosition((byte)x, (byte)y);
            if (!result.Contains(position))
            {
                result.Add(position);
            }
        }

        return result.ToArray();
    }

    private static string ParseDungeonRoomTopology(
        string maze,
        byte width,
        byte height)
    {
        var symbols = ParseTopologySymbols(maze);
        var roomCount = checked(width * height);
        if (symbols.Length == roomCount)
        {
            return new string(symbols);
        }

        // DGN [greed] stores each room code twice (for example "BBNNMM"),
        // whereas a MAP [greed] lists the accepted two-letter room codes.
        if (symbols.Length == roomCount * 2)
        {
            return new string(Enumerable.Range(0, roomCount)
                .Select(index => symbols[index * 2])
                .ToArray());
        }

        return string.Empty;
    }

    private static IReadOnlyDictionary<DungeonRoomPosition, ushort[]> ParseMapSpecifications(
        string maze,
        byte width,
        byte height)
    {
        var result = new Dictionary<DungeonRoomPosition, ushort[]>();
        foreach (var values in ParseAllBlocks(maze, "map specification")
                     .Select(ParseIntegers))
        {
            if (values.Length < 3
                || values[0] < 0
                || values[1] < 0
                || values[0] >= width
                || values[1] >= height)
            {
                continue;
            }

            var mapIds = values[2..]
                .Where(value => value is > 0 and <= ushort.MaxValue)
                .Select(value => (ushort)value)
                .Distinct()
                .ToArray();
            if (mapIds.Length > 0)
            {
                result[new DungeonRoomPosition((byte)values[0], (byte)values[1])] = mapIds;
            }
        }

        return result;
    }

    private static ushort ResolveMapId(
        CatalogState state,
        DungeonDefinition dungeon,
        DungeonLayout layout,
        byte roomX,
        byte roomY,
        IDropRandomSource random)
    {
        var point = new DungeonRoomPosition(roomX, roomY);
        if (layout.TryGetResolvedMap(point, out var resolvedMapId))
        {
            return resolvedMapId;
        }

        var dungeonMaps = state.Maps.Values
            .Where(map => map.DungeonIds.Contains(dungeon.DungeonId))
            .OrderBy(map => map.MapId)
            .ToArray();
        var isStart = layout.StartX == roomX && layout.StartY == roomY;
        var isBoss = layout.BossX == roomX && layout.BossY == roomY;
        var topology = TryGetRoomTopology(layout.Maze, roomX, roomY, out var parsedTopology)
            ? parsedTopology
            : '\0';
        var role = isStart ? MapKind.Start : isBoss ? MapKind.Boss : MapKind.Normal;

        bool MatchesPreferredRole(MapDefinition map) => role switch
        {
            MapKind.Start => map.Kind == MapKind.Start
                && map.MapType == DungeonMapType.Normal,
            MapKind.Boss => map.MapType == DungeonMapType.Boss,
            _ => map.Kind == MapKind.Normal
                && map.MapType == DungeonMapType.Normal
        };
        var hasPreferredRolePool = dungeonMaps.Any(MatchesPreferredRole);

        bool MatchesRole(MapDefinition map) => role == MapKind.Start
                                               && !hasPreferredRolePool
            ? map.Kind != MapKind.Boss && map.MapType == DungeonMapType.Normal
            : MatchesPreferredRole(map);

        ushort SelectAndRemember(IReadOnlyList<MapDefinition> candidates)
        {
            if (candidates.Count == 0)
            {
                return 0;
            }

            var selected = candidates[random.Next(candidates.Count)].MapId;
            layout.SetResolvedMap(point, selected);
            return selected;
        }

        if (TryResolveSpecifiedMap(
                state,
                layout.Maze.MapSpecifications,
                point,
                topology,
                MatchesRole,
                random,
                out var specifiedMap))
        {
            layout.SetResolvedMap(point, specifiedMap);
            return specifiedMap;
        }

        var roleAtCoordinate = dungeonMaps
            .Where(map => MatchesRole(map)
                && map.CoordinateX == roomX
                && map.CoordinateY == roomY)
            .ToArray();
        var roleAtCoordinateWithTopology = roleAtCoordinate
            .Where(map => MapSupportsTopology(map, topology))
            .ToArray();
        if (roleAtCoordinateWithTopology.Length > 0)
        {
            return SelectAndRemember(roleAtCoordinateWithTopology);
        }

        var rolePool = dungeonMaps
            .Where(map => MatchesRole(map) && map.CoordinateX is null)
            .ToArray();
        var rolePoolWithTopology = rolePool
            .Where(map => MapSupportsTopology(map, topology))
            .ToArray();
        if (rolePoolWithTopology.Length > 0)
        {
            return SelectAndRemember(rolePoolWithTopology);
        }

        var roleWithTopology = dungeonMaps
            .Where(map => MatchesRole(map) && MapSupportsTopology(map, topology))
            .ToArray();
        if (roleWithTopology.Length > 0)
        {
            return SelectAndRemember(roleWithTopology);
        }

        // Compatibility fallback for a script whose map files omit [greed].
        if (roleAtCoordinate.Length > 0)
        {
            return SelectAndRemember(roleAtCoordinate);
        }

        if (rolePool.Length > 0)
        {
            return SelectAndRemember(rolePool);
        }

        // Never turn a selected boss coordinate into a normal/dummy map (or a
        // selected normal coordinate into a boss/dummy map). A missing role
        // pool is a bad dungeon definition and must fail visibly.
        return 0;
    }

    private static bool TryResolveSpecifiedMap(
        CatalogState state,
        IReadOnlyDictionary<DungeonRoomPosition, ushort[]> specifications,
        DungeonRoomPosition point,
        char topology,
        Func<MapDefinition, bool> matchesRole,
        IDropRandomSource random,
        out ushort mapId)
    {
        mapId = 0;
        if (!specifications.TryGetValue(point, out var candidates))
        {
            return false;
        }

        var matching = candidates
            .Select(candidate => state.Maps.GetValueOrDefault(candidate))
            .Where(map => map is not null
                && matchesRole(map)
                && MapSupportsTopology(map, topology))
            .Cast<MapDefinition>()
            .ToArray();
        if (matching.Length == 0)
        {
            matching = candidates
                .Select(candidate => state.Maps.GetValueOrDefault(candidate))
                .Where(map => map is not null && matchesRole(map))
                .Cast<MapDefinition>()
                .ToArray();
        }

        if (matching.Length == 0)
        {
            return false;
        }

        mapId = matching[random.Next(matching.Length)].MapId;
        return true;
    }

    private static MapMonsterDefinition[] ParseMonsterRows(string text)
    {
        var result = new List<MapMonsterDefinition>();
        foreach (var block in ParseAllBlocks(text, "monster"))
        {
            foreach (var rawLine in block.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.Split("//", 2, StringSplitOptions.None)[0].Trim();
                var values = ParseIntegers(line);
                if (values.Length >= 5
                    && values[0] is > 0 and <= ushort.MaxValue
                    && values[3] is >= 0 and <= ushort.MaxValue
                    && values[4] is >= 0 and <= ushort.MaxValue)
                {
                    result.Add(new MapMonsterDefinition(
                        checked((ushort)values[0]),
                        ParseMonsterType(line),
                        checked((ushort)values[3]),
                        checked((ushort)values[4]),
                        values[1] != 0,
                        values[2]));
                }
            }
        }

        return result.Take(byte.MaxValue).ToArray();
    }

    private static DungeonPassiveItemGroup[] ParsePassiveItemGroups(string text)
    {
        var result = new List<DungeonPassiveItemGroup>();
        foreach (var block in ParseAllBlocks(text, "special passive object item"))
        {
            var lines = ParseDataLines(block);
            for (var cursor = 0; cursor < lines.Length;)
            {
                var header = ParseIntegers(lines[cursor++]);
                if (header.Length < 3
                    || header[0] != result.Count
                    || header[0] is < 0 or >= byte.MaxValue
                    || header[1] is < short.MinValue or > short.MaxValue
                    || header[2] < 0)
                {
                    return [];
                }

                var items = new List<DungeonPassiveWeightedItem>();
                for (var itemIndex = 0; itemIndex < header[2] && cursor < lines.Length; itemIndex++)
                {
                    var values = ParseIntegers(lines[cursor++]);
                    if (values.Length >= 2
                        && values[0] is > 0 and <= ushort.MaxValue
                        && values[1] > 0)
                    {
                        items.Add(new DungeonPassiveWeightedItem(
                            checked((ushort)values[0]),
                            values[1]));
                    }
                }

                result.Add(new DungeonPassiveItemGroup(
                    checked((byte)header[0]),
                    checked((short)header[1]),
                    items.ToArray()));
            }
        }

        return result.ToArray();
    }

    private static MapSpecialPassiveObject[] ParsePassiveObjectRows(
        string text,
        int startingObjectIndex)
    {
        var result = new List<MapSpecialPassiveObject>();
        foreach (var block in ParseAllBlocks(text, "passive object"))
        {
            foreach (var rawLine in block.Split(
                         ['\r', '\n'],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                var values = ParseIntegers(
                    rawLine.Split("//", 2, StringSplitOptions.None)[0]);
                var objectIndex = startingObjectIndex + result.Count;
                if (objectIndex >= byte.MaxValue
                    || values.Length < 4
                    || values[0] is <= 0 or > ushort.MaxValue
                    || values[1] is < 0 or > ushort.MaxValue
                    || values[2] is < 0 or > ushort.MaxValue
                    || values[1] == 0 && values[2] == 0)
                {
                    continue;
                }

                result.Add(new MapSpecialPassiveObject(
                    checked((byte)objectIndex),
                    checked((ushort)values[0]),
                    checked((ushort)values[1]),
                    checked((ushort)values[2]),
                    ItemAssociationSlot: null,
                    Actions: []));
            }
        }

        return result.ToArray();
    }

    private static MapSpecialPassiveObject[] ParseSpecialPassiveObjects(string text)
    {
        var result = new List<MapSpecialPassiveObject>();
        var nextItemAssociationSlot = 0;
        foreach (var block in ParseAllBlocks(text, "special passive object"))
        {
            var lines = ParseDataLines(block);
            for (var cursor = 0; cursor < lines.Length && result.Count < byte.MaxValue;)
            {
                var objectValues = ParseIntegers(lines[cursor++]);
                if (objectValues.Length < 4
                    || objectValues[0] is <= 0 or > ushort.MaxValue)
                {
                    continue;
                }

                int actionCount;
                if (objectValues.Length >= 5)
                {
                    actionCount = objectValues[4];
                }
                else if (cursor < lines.Length)
                {
                    var countValues = ParseIntegers(lines[cursor++]);
                    actionCount = countValues.FirstOrDefault(-1);
                }
                else
                {
                    actionCount = 0;
                }

                actionCount = Math.Clamp(actionCount, 0, byte.MaxValue);
                var actions = new List<MapPassiveObjectAction>();
                for (var actionIndex = 0;
                     actionIndex < actionCount && cursor < lines.Length;
                     actionIndex++)
                {
                    var line = lines[cursor++];
                    var match = Regex.Match(
                        line,
                        @"^\s*`?\[(monster|trap|item|quest)\]`?\s*(.*)$",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    if (!match.Success)
                    {
                        continue;
                    }

                    var values = ParseIntegers(match.Groups[2].Value);
                    if (values.Length < 5)
                    {
                        continue;
                    }

                    var type = match.Groups[1].Value.ToLowerInvariant() switch
                    {
                        "monster" => PassiveObjectActionType.Monster,
                        "trap" => PassiveObjectActionType.Trap,
                        "item" => PassiveObjectActionType.Item,
                        "quest" => PassiveObjectActionType.Quest,
                        _ => PassiveObjectActionType.Unknown
                    };
                    actions.Add(new MapPassiveObjectAction(
                        checked((byte)actionIndex),
                        type,
                        values[0],
                        values[1],
                        values[2],
                        values[3],
                        values[4]));
                }

                byte? itemAssociationSlot = null;
                if (actions.Any(action => action.Type == PassiveObjectActionType.Item)
                    && nextItemAssociationSlot < byte.MaxValue)
                {
                    itemAssociationSlot = checked((byte)nextItemAssociationSlot++);
                }

                result.Add(new MapSpecialPassiveObject(
                    ObjectIndex: checked((byte)result.Count),
                    PassiveObjectId: checked((ushort)objectValues[0]),
                    X: checked((ushort)Math.Clamp(
                        objectValues.ElementAtOrDefault(1),
                        ushort.MinValue,
                        ushort.MaxValue)),
                    Y: checked((ushort)Math.Clamp(
                        objectValues.ElementAtOrDefault(2),
                        ushort.MinValue,
                        ushort.MaxValue)),
                    ItemAssociationSlot: itemAssociationSlot,
                    Actions: actions.ToArray()));
            }
        }

        return result.ToArray();
    }

    private static string[] ParseDataLines(string block) => block
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Split("//", 2, StringSplitOptions.None)[0].Trim())
        .Where(line => line.Length > 0)
        .ToArray();

    private static IEnumerable<int> ParseAllBlockIntegers(string text, string tag) =>
        ParseAllBlocks(text, tag).SelectMany(ParseIntegers);

    private static int[] ParseFirstBlockIntegers(string text, string tag) =>
        ParseAllBlocks(text, tag).Select(ParseIntegers).FirstOrDefault() ?? [];

    private static IEnumerable<string> ParseAllBlocks(string text, string tag)
    {
        var pattern = $@"\[{Regex.Escape(tag)}\]\s*(.*?)\s*\[/{Regex.Escape(tag)}\]";
        return Regex.Matches(
                text,
                pattern,
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)
            .Select(match => match.Groups[1].Value);
    }

    private static char[] ParseTopologySymbols(string text)
    {
        var match = Regex.Match(
            text,
            @"\[greed\]\s*(.*?)(?=\r?\n\s*\[[^/\r\n][^\]]*\]|\z)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        return match.Success
            ? match.Groups[1].Value
                .Where(character =>
                    !char.IsWhiteSpace(character)
                    && character != '`'
                    && character != ',')
                .ToArray()
            : [];
    }

    private static bool TryGetRoomTopology(
        DungeonDefinition dungeon,
        byte roomX,
        byte roomY,
        out char topology)
    {
        topology = '\0';
        if (roomX >= dungeon.Width
            || roomY >= dungeon.Height
            || dungeon.RoomTopology.Length != dungeon.Width * dungeon.Height)
        {
            return false;
        }

        topology = dungeon.RoomTopology[roomY * dungeon.Width + roomX];
        return true;
    }

    private static bool TryGetRoomTopology(
        DungeonMazeDefinition maze,
        byte roomX,
        byte roomY,
        out char topology)
    {
        topology = '\0';
        if (roomX >= maze.Width
            || roomY >= maze.Height
            || maze.RoomTopology.Length != maze.Width * maze.Height)
        {
            return false;
        }

        topology = maze.RoomTopology[roomY * maze.Width + roomX];
        return true;
    }

    private static DungeonMazeDefinition CreateLegacyMaze(
        DungeonDefinition definition,
        IReadOnlyDictionary<DungeonRoomPosition, ushort[]>? specifications)
    {
        var bossPositions = definition.BossX == byte.MaxValue
                            && definition.BossY == byte.MaxValue
            ? []
            : new[] { new DungeonRoomPosition(definition.BossX, definition.BossY) };
        return new DungeonMazeDefinition(
            MazeIndex: 0,
            definition.Width,
            definition.Height,
            definition.RoomTopology,
            [new DungeonRoomPosition(definition.StartX, definition.StartY)],
            bossPositions,
            specifications
            ?? new Dictionary<DungeonRoomPosition, ushort[]>());
    }

    private static bool IsOpenRoomTopology(char topology) =>
        topology is not ('\0' or '0' or '.' or 'x' or 'X' or '\u253C');

    private static bool MapSupportsTopology(MapDefinition map, char topology) =>
        topology == '\0' || map.TopologySymbols.Contains(topology);

    private static byte ParseMonsterType(string line)
    {
        if (line.Contains("[boss]", StringComparison.OrdinalIgnoreCase))
        {
            return GameDungeonMonsterTypes.Boss;
        }

        if (line.Contains("[super champion]", StringComparison.OrdinalIgnoreCase))
        {
            return GameDungeonMonsterTypes.SuperChampion;
        }

        if (line.Contains("[champion]", StringComparison.OrdinalIgnoreCase))
        {
            return GameDungeonMonsterTypes.Champion;
        }

        return GameDungeonMonsterTypes.Normal;
    }

    internal static int ParseStandaloneMapChampionCount(string text)
    {
        var match = Regex.Match(
            text,
            @"(?im)^\s*\[champion\]\s+(-?\d+)\s*(?://[^\r\n]*)?$",
            RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups[1].Value, out var value)
            ? Math.Max(0, value)
            : 0;
    }

    private static int[] NormalizeChampionCounts(IEnumerable<int>? values)
    {
        var result = new int[4];
        foreach (var entry in (values ?? []).Take(result.Length).Select(
                     (value, index) => (value, index)))
        {
            result[entry.index] = Math.Max(0, entry.value);
        }

        return result;
    }

    private static int[] ParseInlineIntegers(string text, string tag)
    {
        var match = Regex.Match(
            text,
            $@"\[{Regex.Escape(tag)}\]\s*([^\r\n]*)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? ParseIntegers(match.Groups[1].Value) : [];
    }

    private static bool TryParseTaggedInteger(
        string text,
        string tag,
        out int value)
    {
        value = 0;
        var match = Regex.Match(
            text,
            $@"\[{Regex.Escape(tag)}\]\s*(-?\d+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups[1].Value, out value);
    }

    private static bool TryParseTaggedDouble(
        string text,
        string tag,
        out double value)
    {
        value = 0;
        var match = Regex.Match(
            text,
            $@"\[{Regex.Escape(tag)}\]\s*([-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success
            && double.TryParse(
                match.Groups[1].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value)
            && double.IsFinite(value);
    }

    private static int[] ParseIntegers(string text) => IntegerPattern.Matches(text)
        .Select(match => int.TryParse(match.Value, out var value) ? value : 0)
        .ToArray();

    private static string NormalizeRelativePath(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private static Encoding CreateScriptEncoding()
    {
        return PvfEncodings.Cp949();
    }

    private static MapKind ClassifyMap(string fileName)
    {
        var normalized = CoordinatePattern.Replace(fileName, string.Empty)
            .Trim()
            .ToLowerInvariant();
        if (normalized.StartsWith("start", StringComparison.Ordinal)
            || (normalized.StartsWith('s')
                && normalized.Length > 1
                && char.IsDigit(normalized[1])))
        {
            return MapKind.Start;
        }

        if (normalized.StartsWith("boss", StringComparison.Ordinal)
            || (normalized.StartsWith('b')
                && normalized.Length > 1
                && char.IsDigit(normalized[1])))
        {
            return MapKind.Boss;
        }

        return MapKind.Normal;
    }

    private static DungeonMapType ParseMapType(string text)
    {
        var match = Regex.Match(
            text,
            @"(?im)^\s*\[type\]\s*`?\s*\[(?<type>[^\]]+)\]\s*`?",
            RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return DungeonMapType.Normal;
        }

        return match.Groups["type"].Value.Trim().ToLowerInvariant() switch
        {
            "boss" => DungeonMapType.Boss,
            "dummy" => DungeonMapType.Dummy,
            _ => DungeonMapType.Normal
        };
    }

    private static CatalogState CreateFallbackState()
    {
        var definitions = new Dictionary<ushort, DungeonDefinition>
        {
            [1] = new(1, 1, 1, 3, 2, 0, 0, 2, 1, string.Empty),
            [2] = new(2, 2, 2, 1, 1, 0, 0, 0, 0, string.Empty)
        };
        var rooms = new Dictionary<DungeonRoomPosition, ushort[]>
        {
            [new DungeonRoomPosition(0, 0)] = [1],
            [new DungeonRoomPosition(1, 0)] = [5],
            [new DungeonRoomPosition(2, 0)] = [6],
            [new DungeonRoomPosition(0, 1)] = [8],
            [new DungeonRoomPosition(1, 1)] = [7],
            [new DungeonRoomPosition(2, 1)] = [13]
        };
        var counts = new Dictionary<ushort, int>
        {
            [1] = 3,
            [5] = 5,
            [6] = 6,
            [7] = 5,
            [8] = 4,
            [13] = 7,
            [101] = 0
        };
        var maps = counts.ToDictionary(
            entry => entry.Key,
            entry => new MapDefinition(
                entry.Key,
                entry.Key == 101 ? [2] : [1],
                entry.Key == 1 || entry.Key == 101
                    ? MapKind.Start
                    : entry.Key == 13 ? MapKind.Boss : MapKind.Normal,
                entry.Key == 13 ? DungeonMapType.Boss : DungeonMapType.Normal,
                null,
                null,
                [],
                0,
                Enumerable.Range(0, entry.Value)
                    .Select(index => new MapMonsterDefinition(
                        1,
                        entry.Key == 13 && index == entry.Value - 1
                            ? GameDungeonMonsterTypes.Boss
                            : GameDungeonMonsterTypes.Normal))
                    .ToArray(),
                []));
        return new CatalogState(
            definitions,
            maps,
            new Dictionary<
                ushort,
                IReadOnlyDictionary<DungeonRoomPosition, ushort[]>>
            {
                [1] = rooms,
                [2] = new Dictionary<DungeonRoomPosition, ushort[]>
                {
                    [new DungeonRoomPosition(0, 0)] = [101]
                }
            },
            new HashSet<ushort>());
    }

    private sealed record CatalogState(
        IReadOnlyDictionary<ushort, DungeonDefinition> Definitions,
        IReadOnlyDictionary<ushort, MapDefinition> Maps,
        IReadOnlyDictionary<
            ushort,
            IReadOnlyDictionary<DungeonRoomPosition, ushort[]>> MapSpecifications,
        IReadOnlySet<ushort> HellDemonIds);

    private sealed record MapDefinition(
        ushort MapId,
        ushort[] DungeonIds,
        MapKind Kind,
        DungeonMapType MapType,
        int? CoordinateX,
        int? CoordinateY,
        char[] TopologySymbols,
        int BaseChampionCount,
        MapMonsterDefinition[] Monsters,
        MapSpecialPassiveObject[] PassiveObjects);

    private readonly record struct MapMonsterDefinition(
        ushort MonsterIndex,
        byte Type,
        ushort X = 0,
        ushort Y = 0,
        bool UsesDungeonLevel = true,
        int LevelOffsetOrFixedLevel = 0);

    private sealed record MapSpecialPassiveObject(
        byte ObjectIndex,
        ushort PassiveObjectId,
        ushort X,
        ushort Y,
        byte? ItemAssociationSlot,
        MapPassiveObjectAction[] Actions);

    private readonly record struct MapPassiveObjectAction(
        byte ActionIndex,
        PassiveObjectActionType Type,
        int Value1,
        int Value2,
        int Value3,
        int Value4,
        int Value5);

    private enum PassiveObjectActionType : byte
    {
        Unknown,
        Monster,
        Trap,
        Item,
        Quest
    }

    private enum MapKind : byte
    {
        Normal,
        Start,
        Boss
    }
}
