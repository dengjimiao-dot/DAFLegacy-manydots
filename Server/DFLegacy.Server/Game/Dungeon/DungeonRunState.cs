using DFLegacy.Protocol;

namespace DFLegacy.Server;

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

    public bool IsActive => _layout is not null && _current is not null;

    public ushort DungeonId => _layout?.DungeonId ?? 0;

    public byte Difficulty { get; private set; }

    public byte RoomX { get; private set; }

    public byte RoomY { get; private set; }

    public bool HellMode { get; private set; }

    public byte HellPartyMode { get; private set; }

    public byte RoomStateFlag => IsCurrentHellRoom ? (byte)2 : (byte)1;

    public byte CurrentHellPartyMode =>
        HellMode && !IsCurrentHellRoomCleared ? HellPartyMode : (byte)0;

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
        if (!IsActive || roomX == RoomX && roomY == RoomY)
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
        if (_current is null
            || !_current.EncounterRoom.Monsters.Any(monster => monster.UniqueId == uniqueId))
        {
            return false;
        }

        var first = _current.DefeatedMonsterIds.Add(uniqueId);
        if (first)
        {
            TryCompleteCurrentHellRoom();
        }

        return first;
    }

    public bool HasClearedMap(ushort mapId) => mapId != 0
        && _rooms.Values.Any(room => room.EncounterRoom.MapId == mapId
            && room.EncounterRoom.Monsters.All(monster =>
                room.DefeatedMonsterIds.Contains(monster.UniqueId)));

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
        _rooms.Clear();
        _layout = null;
        _current = null;
        Difficulty = 0;
        RoomX = 0;
        RoomY = 0;
        HellMode = false;
        HellPartyMode = 0;
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

        if (_current.EncounterRoom.Monsters.All(monster =>
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
