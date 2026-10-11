using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

public sealed record DungeonRoomTransition(
    DungeonRoomDefinition Room,
    bool FirstVisit,
    bool Revisit,
    bool CompletedHellRoomOnExit,
    bool ReuseClientRoomState);

public sealed class DungeonRunState(DungeonCatalog catalog)
{
    private readonly Dictionary<DungeonRoomPosition, RoomState> _rooms = [];
    private DungeonLayout? _layout;
    private RoomState? _current;
    private DungeonBossEntranceCondition _bossEntranceCondition =
        DungeonBossEntranceCondition.Unrestricted;
    private readonly Dictionary<DungeonRoomPosition, List<ushort>> _bossMonsterAssignments = [];
    private readonly Dictionary<ushort, int> _bossEntranceKills = [];
    private readonly HashSet<DungeonRoomPosition> _clearedRooms = [];

    public bool IsActive => _layout is not null && _current is not null;

    public bool IsCleared { get; private set; }

    public bool IsBossClearPending { get; private set; }

    public bool IsCurrentTerminalRoom => IsActive
        && RoomX == _layout!.BossX && RoomY == _layout!.BossY
        && _current!.EncounterRoom.MapType == DungeonMapType.Boss;

    public ushort DungeonId => _layout?.DungeonId ?? 0;

    public byte Difficulty { get; private set; }

    public byte RoomX { get; private set; }

    public byte RoomY { get; private set; }

    public bool HellMode { get; private set; }

    public byte HellPartyMode { get; private set; }

    public byte RoomStateFlag => IsCurrentHellRoom ? (byte)2 : (byte)1;

    public byte CurrentHellPartyMode =>
        HellMode && !IsCurrentHellRoomCleared ? HellPartyMode : (byte)0;

    public bool IsBossRoomEntranceOpen => _bossEntranceCondition.Kind switch
    {
        DungeonBossEntranceConditionKind.HuntMonster => _bossEntranceCondition.Monsters.All(requirement =>
            GetBossEntranceKillCount(requirement.MonsterId) >= requirement.RequiredKills),
        DungeonBossEntranceConditionKind.ClearRooms => _bossEntranceCondition.Rooms.All(_clearedRooms.Contains),
        _ => true
    };

    public int GetBossEntranceKillCount(ushort monsterId) => _bossEntranceKills.GetValueOrDefault(monsterId);

    public bool HasClearedRoom(byte x, byte y) => _clearedRooms.Contains(new(x, y));

    public bool IsBossRoomEntranceBlocked(byte x, byte y) => _layout is not null
        && x == _layout.BossX && y == _layout.BossY && !IsBossRoomEntranceOpen;

    public bool IsCurrentHellRoom =>
        HellMode
        && catalog.TryGetHellRoom(DungeonId, out _, out var hellX, out var hellY)
        && RoomX == hellX
        && RoomY == hellY;

    public bool IsCurrentHellRoomCleared =>
        IsCurrentHellRoom && _current?.HellEncounterCleared == true;

    public DungeonRoomDefinition? CurrentRoom => _current is null
        ? null
        : ProjectRoom(_current);

    public bool TryStart(
        DungeonLayout layout,
        byte difficulty,
        byte dungeonOption,
        out DungeonRoomTransition transition,
        IDropRandomSource? random = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        transition = null!;
        Stop();
        _layout = layout;
        Difficulty = difficulty;
        HellMode = dungeonOption != 0
            && catalog.TryGetHellRoom(layout.DungeonId, out _, out _, out _);
        HellPartyMode = HellMode
            ? dungeonOption >= 2 ? (byte)2 : (byte)1
            : (byte)0;
        random ??= GameRandomSource.Shared;
        if (!TryInitializeBossEntrance(random))
        {
            Stop();
            return false;
        }
        if (!TryEnterRoom(
                layout.StartX,
                layout.StartY,
                out transition,
                random,
                validateAdjacent: false))
        {
            Stop();
            return false;
        }

        return true;
    }

    public bool TryMoveRoom(
        byte roomX,
        byte roomY,
        out DungeonRoomTransition transition,
        IDropRandomSource? random = null)
    {
        transition = null!;
        if (!IsActive || IsCleared || IsBossClearPending
            || roomX == RoomX && roomY == RoomY
            || IsBossRoomEntranceBlocked(roomX, roomY))
        {
            return false;
        }

        return TryEnterRoom(
            roomX,
            roomY,
            out transition,
            random,
            validateAdjacent: true);
    }

    public bool MarkMonsterDefeated(ushort uniqueId)
    {
        if (_current is null)
        {
            return false;
        }
        var defeatedMonster = _current.EncounterRoom.Monsters.FirstOrDefault(
            monster => monster.UniqueId == uniqueId);
        if (defeatedMonster is null)
        {
            return false;
        }

        var first = _current.DefeatedMonsterIds.Add(uniqueId);
        if (first)
        {
            if (defeatedMonster.IsEnemy
                && _bossEntranceCondition.Monsters.Any(requirement => requirement.MonsterId == defeatedMonster.MonsterIndex))
            {
                _bossEntranceKills[defeatedMonster.MonsterIndex] = Math.Min(int.MaxValue - 1,
                    GetBossEntranceKillCount(defeatedMonster.MonsterIndex)) + 1;
            }
            MarkCurrentRoomCleared();
            TryCompleteCurrentHellRoom();
            if (IsCurrentTerminalRoom)
            {
                var aliveMonsterIds = _current.EncounterRoom.Monsters
                    .Where(monster => !_current.DefeatedMonsterIds.Contains(monster.UniqueId))
                    .Select(monster => monster.UniqueId)
                    .ToHashSet();
                var decision = DungeonRoomClearRules.Evaluate(
                    _current.EncounterRoom, defeatedMonster, aliveMonsterIds);
                if (decision.ShouldEnableClear)
                {
                    IsCleared = true;
                    IsBossClearPending = false;
                }
                else if (decision.Reason == DungeonRoomClearReason.LastBossDefeated)
                {
                    IsBossClearPending = true;
                }
            }
        }

        return first;
    }

    public bool HasClearedMap(ushort mapId) => mapId != 0
        && _rooms.Values.Any(room => room.EncounterRoom.MapId == mapId
            && (room == _current && IsCleared
                || room.EncounterRoom.Monsters.Where(monster => monster.IsEnemy).All(monster =>
                    room.DefeatedMonsterIds.Contains(monster.UniqueId))));

    public bool BeginBossClear()
    {
        if (!IsCurrentTerminalRoom || IsCleared || IsBossClearPending
            || !_current!.EncounterRoom.Monsters.Any(monster =>
                monster.IsEnemy && monster.Type == GameDungeonMonsterTypes.Boss))
        {
            return false;
        }

        // A valid final BOSS_DIE_CHECK starts the death-animation window.
        // Do not let MOVE_MAP discard it before the later CMD 42 confirmation.
        IsBossClearPending = true;
        return true;
    }

    public bool TryEnableEmptyBossClear()
    {
        if (!IsCurrentTerminalRoom || IsCleared
            || _current!.EncounterRoom.Monsters.Any(monster => monster.IsEnemy))
        {
            return false;
        }

        IsCleared = true;
        IsBossClearPending = false;
        return true;
    }

    public bool MarkPassiveObjectDestroyed(byte objectIndex)
    {
        if (_current is null
            || !_current.EncounterRoom.PassiveObjects.Any(passiveObject =>
                passiveObject.ObjectIndex == objectIndex))
        {
            return false;
        }

        return _current.DestroyedPassiveObjectIds.Add(objectIndex);
    }

    public bool TryMarkPassiveObjectDestroyed(
        ushort objectCode,
        out byte objectIndex)
    {
        objectIndex = byte.MaxValue;
        if (_current is null)
        {
            return false;
        }

        var passiveObject = _current.EncounterRoom.PassiveObjects
            .Where(candidate =>
                candidate.PassiveObjectId == objectCode
                && !_current.DestroyedPassiveObjectIds.Contains(candidate.ObjectIndex))
            .OrderBy(candidate => candidate.ObjectIndex)
            .FirstOrDefault();
        if (passiveObject is null && objectCode <= byte.MaxValue)
        {
            passiveObject = _current.EncounterRoom.PassiveObjects.FirstOrDefault(candidate =>
                candidate.ObjectIndex == objectCode
                && !_current.DestroyedPassiveObjectIds.Contains(candidate.ObjectIndex));
        }

        if (passiveObject is null)
        {
            return false;
        }

        objectIndex = passiveObject.ObjectIndex;
        return _current.DestroyedPassiveObjectIds.Add(objectIndex);
    }

    public bool IsHellFiend(ushort uniqueId) =>
        IsCurrentHellRoom
        && _current?.EncounterRoom.Monsters.Any(monster =>
            monster.UniqueId == uniqueId && monster.IsHellHidden) == true;

    public bool CompleteCurrentHellRoomOnExit()
    {
        if (!IsCurrentHellRoom || _current is null || _current.HellEncounterCleared)
        {
            return false;
        }

        _current.DefeatedMonsterIds.UnionWith(
            _current.EncounterRoom.Monsters.Select(monster => monster.UniqueId));
        _current.DestroyedPassiveObjectIds.UnionWith(
            _current.EncounterRoom.PassiveObjects.Select(passiveObject =>
                passiveObject.ObjectIndex));
        _current.HellEncounterCleared = true;
        return true;
    }

    public void Stop()
    {
        IsCleared = false;
        IsBossClearPending = false;
        _rooms.Clear();
        _bossMonsterAssignments.Clear();
        _bossEntranceKills.Clear();
        _clearedRooms.Clear();
        _bossEntranceCondition = DungeonBossEntranceCondition.Unrestricted;
        _layout = null;
        _current = null;
        Difficulty = 0;
        RoomX = 0;
        RoomY = 0;
        HellMode = false;
        HellPartyMode = 0;
    }

    private bool TryInitializeBossEntrance(IDropRandomSource random)
    {
        if (_layout is null || !catalog.TryGetDefinition(DungeonId, out var definition))
        {
            return false;
        }

        _bossEntranceCondition = definition.BossEntranceCondition;
        // 13339 SetGridPath (0x082FFB30) assigns each requested appearance to
        // an eligible non-start/non-boss grid, with replacement.
        foreach (var requirement in _bossEntranceCondition.Monsters)
        {
            if (requirement.SpawnCount == 0)
            {
                continue;
            }

            var candidates = catalog.GetBossEntranceMonsterCandidates(_layout, requirement.MonsterId, HellMode);
            if (candidates.Length == 0)
            {
                return false;
            }

            for (var index = 0; index < requirement.SpawnCount; index++)
            {
                var position = candidates[random.Next(candidates.Length)];
                if (!_bossMonsterAssignments.TryGetValue(position, out var assigned))
                {
                    assigned = [];
                    _bossMonsterAssignments.Add(position, assigned);
                }
                assigned.Add(requirement.MonsterId);
            }
        }

        return true;
    }

    private void MarkCurrentRoomCleared()
    {
        if (_current is not null && _current.EncounterRoom.Monsters.Where(monster => monster.IsEnemy)
                .All(monster => _current.DefeatedMonsterIds.Contains(monster.UniqueId)))
        {
            _clearedRooms.Add(new(RoomX, RoomY));
        }
    }

    private bool TryEnterRoom(
        byte roomX,
        byte roomY,
        out DungeonRoomTransition transition,
        IDropRandomSource? random,
        bool validateAdjacent)
    {
        transition = null!;
        if (_layout is null
            || validateAdjacent && !RoomsConnect(_layout, RoomX, RoomY, roomX, roomY))
        {
            return false;
        }

        var position = new DungeonRoomPosition(roomX, roomY);
        var firstVisit = !_rooms.TryGetValue(position, out var state);
        if (firstVisit)
        {
            random ??= GameRandomSource.Shared;
            if (!catalog.TryCreateRoom(
                    _layout,
                    roomX,
                    roomY,
                    out var baseRoom,
                    Difficulty,
                    random))
            {
                return false;
            }

            var encounterRoom = baseRoom;
            if (HellMode
                && catalog.TryGetHellRoom(DungeonId, out _, out var hellX, out var hellY)
                && roomX == hellX
                && roomY == hellY
                && catalog.TryCreateHellRoom(
                    _layout,
                    Difficulty,
                    out var hellRoom,
                    random))
            {
                encounterRoom = hellRoom;
            }

            if (HellMode
                && catalog.TryGetHellRoom(DungeonId, out _, out var expectedHellX, out var expectedHellY)
                && roomX == expectedHellX
                && roomY == expectedHellY
                && !encounterRoom.IsHellRoom)
            {
                return false;
            }

            if (_bossMonsterAssignments.TryGetValue(position, out var assignedMonsters))
            {
                var monsters = encounterRoom.Monsters.ToList();
                foreach (var monsterId in assignedMonsters)
                {
                    var template = encounterRoom.BossEntranceMonsterTemplates.FirstOrDefault(monster =>
                        monster.MonsterIndex == monsterId);
                    if (template is null || monsters.Count >= byte.MaxValue)
                    {
                        return false;
                    }
                    monsters.Add(template with { UniqueId = checked((ushort)monsters.Count) });
                }
                encounterRoom = encounterRoom with { Monsters = monsters.ToArray() };
            }

            var championTargetCount = catalog.GetChampionTargetCount(
                _layout,
                Difficulty,
                encounterRoom,
                random);
            var clientRandomSeed = CreateClientRandomSeed(random);
            baseRoom = baseRoom with
            {
                ClientRandomSeed = clientRandomSeed
            };
            encounterRoom = encounterRoom with
            {
                Monsters = DungeonCatalog.PromoteChampions(
                    encounterRoom.Monsters,
                    championTargetCount,
                    random),
                ClientRandomSeed = clientRandomSeed
            };

            state = new RoomState(baseRoom, encounterRoom);
            _rooms.Add(position, state);
        }

        var completedHellRoomOnExit = CompleteCurrentHellRoomOnExit();
        RoomX = roomX;
        RoomY = roomY;
        var enteredState = state!;
        _current = enteredState;
        MarkCurrentRoomCleared();
        var revisit = enteredState.VisitCount > 0;
        enteredState.VisitCount++;
        transition = new DungeonRoomTransition(
            ProjectRoom(enteredState),
            FirstVisit: !revisit,
            Revisit: revisit,
            completedHellRoomOnExit,
            // Ordinary revisits reuse the room snapshot that NOTI 29 built
            // on first entry. A cleared hell encounter is the exception: its
            // client scene must be rebuilt as the empty base-map room.
            ReuseClientRoomState: revisit
                && !(enteredState.HellEncounterCleared
                    && enteredState.EncounterRoom.IsHellRoom));
        return true;
    }

    private static uint CreateClientRandomSeed(IDropRandomSource random)
    {
        var seed = checked((uint)random.Next(1 << 16)) << 16
            | checked((uint)random.Next(1 << 16));
        return seed == 0 ? 1u : seed;
    }

    private void TryCompleteCurrentHellRoom()
    {
        if (!IsCurrentHellRoom || _current is null || _current.HellEncounterCleared)
        {
            return;
        }

        if (_current.EncounterRoom.Monsters.Where(monster => monster.IsEnemy).All(monster =>
                _current.DefeatedMonsterIds.Contains(monster.UniqueId)))
        {
            _current.HellEncounterCleared = true;
            _current.DestroyedPassiveObjectIds.UnionWith(
                _current.EncounterRoom.PassiveObjects.Select(passiveObject =>
                    passiveObject.ObjectIndex));
        }
    }

    private static DungeonRoomDefinition ProjectRoom(RoomState state)
    {
        if (state.HellEncounterCleared && state.EncounterRoom.IsHellRoom)
        {
            return state.BaseRoom with
            {
                Monsters = [],
                PassiveObjects = [],
                PassiveItemSpawns = []
            };
        }

        return state.EncounterRoom with
        {
            Monsters = state.EncounterRoom.Monsters
                .Where(monster => !state.DefeatedMonsterIds.Contains(monster.UniqueId))
                .ToArray(),
            PassiveObjects = state.EncounterRoom.PassiveObjects
                .Where(passiveObject =>
                    !state.DestroyedPassiveObjectIds.Contains(passiveObject.ObjectIndex))
                .ToArray(),
            PassiveItemSpawns = state.EncounterRoom.PassiveItemSpawns
                .Where(spawn =>
                    !state.DestroyedPassiveObjectIds.Contains(spawn.PassiveObjectIndex))
                .ToArray()
        };
    }

    private static bool RoomsConnect(
        DungeonLayout layout,
        byte currentX,
        byte currentY,
        byte targetX,
        byte targetY)
    {
        var deltaX = targetX - currentX;
        var deltaY = targetY - currentY;
        if (Math.Abs(deltaX) + Math.Abs(deltaY) != 1
            || targetX >= layout.Width
            || targetY >= layout.Height)
        {
            return false;
        }

        var topology = layout.Maze.RoomTopology;
        if (topology.Length != layout.Width * layout.Height)
        {
            return true;
        }

        var currentMask = TopologyMask(topology[currentY * layout.Width + currentX]);
        var targetMask = TopologyMask(topology[targetY * layout.Width + targetX]);
        var currentDoor = deltaX > 0 ? 1 : deltaX < 0 ? 4 : deltaY < 0 ? 2 : 8;
        var targetDoor = deltaX > 0 ? 4 : deltaX < 0 ? 1 : deltaY < 0 ? 8 : 2;
        return (currentMask & currentDoor) != 0 && (targetMask & targetDoor) != 0;
    }

    private static int TopologyMask(char topology)
    {
        var normalized = char.ToUpperInvariant(topology);
        return normalized is >= 'A' and <= 'P' ? normalized - 'A' : 0;
    }

    private sealed class RoomState(
        DungeonRoomDefinition baseRoom,
        DungeonRoomDefinition encounterRoom)
    {
        public DungeonRoomDefinition BaseRoom { get; } = baseRoom;

        public DungeonRoomDefinition EncounterRoom { get; } = encounterRoom;

        public HashSet<ushort> DefeatedMonsterIds { get; } = [];

        public HashSet<byte> DestroyedPassiveObjectIds { get; } = [];

        public int VisitCount { get; set; }

        public bool HellEncounterCleared { get; set; }
    }
}
