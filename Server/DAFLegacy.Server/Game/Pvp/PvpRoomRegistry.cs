namespace DAFLegacy.Server;

/// <summary>PVP 房间状态（11 号 P6 骨架；docs/design/12 §3.6 零持久化）。</summary>
public enum PvpRoomState : byte
{
    Waiting = 0,
    Started = 1,
    Settled = 2,
}

/// <summary>PVP 座位（8 座制；队伍 1/2/观察，A12 线索对齐）。</summary>
public sealed class PvpSeat
{
    public bool Occupied { get; set; }
    public Guid SessionId { get; set; }
    public ushort UserId { get; set; }
    public byte Team { get; set; }
    public bool Ready { get; set; }
    public ushort Kills { get; set; }
    public ushort Deaths { get; set; }
}

/// <summary>
/// PVP 房间注册表骨架（11 号 P6 第一步）：建房/进房/座位/准备/开战校验/击杀计数/
/// 清房。房间运行态纯内存（与组队同构）；下行构建器与结算公式按 P-36 对拍后接线。
/// </summary>
public sealed class PvpRoomRegistry
{
    public const int SeatsPerRoom = 8;

    private readonly object _gate = new();
    private readonly Dictionary<int, PvpRoom> _rooms = new();
    private readonly Dictionary<Guid, int> _roomBySession = new();
    private int _nextRoomId = 1;

    public sealed class PvpRoom
    {
        public required int RoomId { get; init; }
        public required Guid HostSessionId { get; set; }
        public ushort MapIndex { get; set; }
        public PvpRoomState State { get; set; } = PvpRoomState.Waiting;
        public PvpSeat[] Seats { get; } = CreateSeats();

        public IEnumerable<PvpSeat> OccupiedSeats => Seats.Where(seat => seat.Occupied);

        private static PvpSeat[] CreateSeats()
        {
            var seats = new PvpSeat[SeatsPerRoom];
            for (var index = 0; index < seats.Length; index++)
            {
                seats[index] = new PvpSeat { Team = (byte)(index < 4 ? 1 : 2) };
            }

            return seats;
        }
    }

    /// <summary>CMD 53：建房（房主占 0 号位）。返回房间或 null（已在房间）。</summary>
    public PvpRoom? Create(Guid hostSessionId, ushort hostUserId)
    {
        lock (_gate)
        {
            if (_roomBySession.ContainsKey(hostSessionId))
            {
                return null;
            }

            var room = new PvpRoom { RoomId = _nextRoomId++, HostSessionId = hostSessionId };
            room.Seats[0].Occupied = true;
            room.Seats[0].SessionId = hostSessionId;
            room.Seats[0].UserId = hostUserId;
            _rooms[room.RoomId] = room;
            _roomBySession[hostSessionId] = room.RoomId;
            return room;
        }
    }

    /// <summary>CMD 54：进房（首个空位）。</summary>
    public PvpRoom? Join(Guid sessionId, ushort userId)
    {
        lock (_gate)
        {
            if (_roomBySession.ContainsKey(sessionId))
            {
                return null;
            }

            var room = _rooms.Values.FirstOrDefault(candidate =>
                candidate.State == PvpRoomState.Waiting
                && candidate.OccupiedSeats.Count() < SeatsPerRoom);
            if (room is null)
            {
                return null;
            }

            var seat = room.Seats.First(seat => !seat.Occupied);
            seat.Occupied = true;
            seat.SessionId = sessionId;
            seat.UserId = userId;
            _roomBySession[sessionId] = room.RoomId;
            return room;
        }
    }

    /// <summary>CMD 55：座位/队伍选择。</summary>
    public bool SetSeat(Guid sessionId, byte seatIndex, byte team)
    {
        lock (_gate)
        {
            if (!_roomBySession.TryGetValue(sessionId, out var roomId)
                || !_rooms.TryGetValue(roomId, out var room)
                || seatIndex >= SeatsPerRoom
                || room.Seats[seatIndex].Occupied)
            {
                return false;
            }

            var current = room.Seats.First(seat => seat.SessionId == sessionId);
            // 挪位必须彻底清空原座位（含 SessionId/UserId/Ready），否则陈旧
            // SessionId 会污染后续所有按会话的座位查找。
            current.Occupied = false;
            current.SessionId = Guid.Empty;
            current.UserId = 0;
            current.Ready = false;
            var target = room.Seats[seatIndex];
            target.Occupied = true;
            target.SessionId = sessionId;
            target.UserId = current.UserId;
            target.Team = team;
            return true;
        }
    }

    /// <summary>CMD 56：准备状态切换（NOTI 44 值域：{u8 座位, u8 准备态}）。</summary>
    public bool? SetReady(Guid sessionId, bool ready)
    {
        lock (_gate)
        {
            if (!_roomBySession.TryGetValue(sessionId, out var roomId)
                || !_rooms.TryGetValue(roomId, out var room))
            {
                return null;
            }

            var seat = room.Seats.FirstOrDefault(seat =>
                seat.Occupied && seat.SessionId == sessionId);
            if (seat is null)
            {
                return null;
            }

            seat.Ready = ready;
            return ready;
        }
    }

    /// <summary>CMD 45 开战校验：全员就绪且双方至少各一人；通过即转 Started。</summary>
    public PvpRoom? Start(Guid hostSessionId)
    {
        lock (_gate)
        {
            if (!_roomBySession.TryGetValue(hostSessionId, out var roomId)
                || !_rooms.TryGetValue(roomId, out var room)
                || room.HostSessionId != hostSessionId
                || room.State != PvpRoomState.Waiting)
            {
                return null;
            }

            var occupied = room.OccupiedSeats.ToList();
            if (occupied.Count < 2
                || occupied.Any(seat => !seat.Ready)
                || occupied.Select(seat => seat.Team).Distinct().Count() < 2)
            {
                return null;
            }

            room.State = PvpRoomState.Started;
            return room;
        }
    }

    /// <summary>CMD 58：击杀上报（阵亡座位 + 击杀者座位；A12 线索同构）。</summary>
    public bool ReportKill(Guid reporterSessionId, byte victimSeatIndex, byte killerSeatIndex)
    {
        lock (_gate)
        {
            if (!_roomBySession.TryGetValue(reporterSessionId, out var roomId)
                || !_rooms.TryGetValue(roomId, out var room)
                || room.State != PvpRoomState.Started
                || victimSeatIndex >= SeatsPerRoom
                || killerSeatIndex >= SeatsPerRoom)
            {
                return false;
            }

            room.Seats[victimSeatIndex].Deaths++;
            room.Seats[killerSeatIndex].Kills++;
            return true;
        }
    }

    /// <summary>退房/断线清理。返回被退的房间（无则 null）。</summary>
    public PvpRoom? Leave(Guid sessionId)
    {
        lock (_gate)
        {
            if (!_roomBySession.TryGetValue(sessionId, out var roomId)
                || !_rooms.TryGetValue(roomId, out var room))
            {
                return null;
            }

            var seat = room.Seats.FirstOrDefault(seat =>
                seat.Occupied && seat.SessionId == sessionId);
            if (seat is not null)
            {
                seat.Occupied = false;
                seat.SessionId = Guid.Empty;
                seat.UserId = 0;
                seat.Ready = false;
            }

            _roomBySession.Remove(sessionId);
            if (room.HostSessionId == sessionId)
            {
                var successor = room.OccupiedSeats.FirstOrDefault();
                if (successor is not null)
                {
                    room.HostSessionId = successor.SessionId;
                }
                else
                {
                    _rooms.Remove(roomId);
                }
            }

            return room;
        }
    }

    public PvpRoom? FindBySession(Guid sessionId)
    {
        lock (_gate)
        {
            return _roomBySession.TryGetValue(sessionId, out var roomId)
                && _rooms.TryGetValue(roomId, out var room)
                ? room
                : null;
        }
    }

    public IReadOnlyList<PvpRoom> ListWaiting() =>
        ListByState(PvpRoomState.Waiting);

    private PvpRoom[] ListByState(PvpRoomState state)
    {
        lock (_gate)
        {
            return _rooms.Values.Where(room => room.State == state).ToArray();
        }
    }
}
