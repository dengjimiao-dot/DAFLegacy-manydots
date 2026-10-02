using System.Net;
using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

/// <summary>城镇在场记录（原 PartyCoordinator.PresenceEntry，批次 3 迁入中枢）。</summary>
public sealed record TownPresenceEntry(
    Guid SessionId,
    Guid CharacterId,
    uint AccountUid,
    ushort UserId,
    string Name,
    byte Level,
    byte Job,
    byte TownId,
    byte AreaId,
    short X,
    short Y,
    byte Direction,
    int ChannelNumber,
    GameServerPacket Appearance);

public enum TownPresenceTransitionKind
{
    Entered,
    Left,
}

public sealed record TownPresenceTransition(
    TownPresenceTransitionKind Kind,
    TownPresenceEntry Entry);

/// <summary>连接层端点快照（认领算法的输入；docs/design/10 §4.2）。</summary>
public sealed record TownConnectionView(
    Guid SessionId,
    IPAddress TcpAddress,
    GameNatReport? SelfReport);

/// <summary>
/// 城镇广播中枢（docs/design/10 §4.1 G4，批次 3）：频道会话连接层（端点三字段
/// 自报/认领/解析与频道人口统计的唯一归属）与城镇在场层（区域实体交换、三级广播、
/// 查找服务、状态翻转钩子）的唯一归属。组队域（PartyCoordinator）与 UDP 数据面
/// （GameplayDatagramService）都只作为消费方。
/// 背压由每连接出站的有界 ConnectionQueue 承担（07 架构），中枢只补广播计数、
/// 不另设队列；状态翻转钩子（好友/工会上下线通知，11 号 P2/P5）按翻转现查的纪律
/// 由订阅方自行决定是否消费，中枢不做常驻订阅登记。
/// </summary>
public sealed class TownPresenceService(CharacterSessionRegistry characterSessions)
{
    private readonly object _gate = new();

    // ---- 连接层：频道会话建立即登记，断开回收；端点三字段挂连接态 ----
    private sealed class ConnectionEntry
    {
        public required Guid SessionId { get; init; }
        public required int ChannelNumber { get; init; }
        public required IPAddress TcpAddress { get; init; }
        public required RuntimeSession Runtime { get; init; }
        public GameNatReport? SelfReport { get; set; }
        public IPEndPoint? ClaimedEndpoint { get; set; }
        public IPEndPoint? ResolvedEndpoint { get; set; }
        public uint NextSequence { get; set; }
    }

    private readonly Dictionary<Guid, ConnectionEntry> _connections = new();
    private readonly Dictionary<int, int> _populationByChannel = new();

    /// <summary>解析端点反查（中继归属）：端点键 → 会话。随 SetResolvedEndpoint 单点维护。</summary>
    private readonly Dictionary<string, Guid> _sessionsByEndpoint = new();

    // ---- 城镇层：进/离城登记与区域交付（原 PartyCoordinator 在场表）----
    private readonly Dictionary<Guid, TownPresenceEntry> _presenceBySession = new();
    private readonly List<Action<TownPresenceTransition>> _transitionHooks = [];

    // ---- 中枢计数（docs/design/10 §4.4）----
    private long _broadcastRecipients;
    private long _presenceTransitions;

    /// <summary>状态翻转钩子注册（翻转时同步调用；订阅方异常不得影响在场状态）。</summary>
    public void RegisterTransitionHook(Action<TownPresenceTransition> hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        lock (_gate)
        {
            _transitionHooks.Add(hook);
        }
    }

    // ------------------------------------------------------------------
    // 连接层（端点三字段 + 频道人口）
    // ------------------------------------------------------------------

    public void AcquireConnection(Guid sessionId, int channelNumber, RuntimeSession runtime, IPAddress tcpAddress)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(tcpAddress);
        lock (_gate)
        {
            _connections[sessionId] = new ConnectionEntry
            {
                SessionId = sessionId,
                ChannelNumber = channelNumber,
                TcpAddress = tcpAddress.MapToIPv4(),
                Runtime = runtime,
            };
            _populationByChannel[channelNumber] = _populationByChannel.GetValueOrDefault(channelNumber) + 1;
        }
    }

    public void ReleaseConnection(Guid sessionId)
    {
        lock (_gate)
        {
            if (!_connections.Remove(sessionId, out var entry))
            {
                return;
            }

            var population = _populationByChannel.GetValueOrDefault(entry.ChannelNumber) - 1;
            if (population > 0)
            {
                _populationByChannel[entry.ChannelNumber] = population;
            }
            else
            {
                _populationByChannel.Remove(entry.ChannelNumber);
            }

            if (entry.ResolvedEndpoint is not null)
            {
                _sessionsByEndpoint.Remove(EndpointKey(entry.ResolvedEndpoint));
            }
        }
    }

    /// <summary>频道人口唯一真源：每频道连接注册数（元数据 CurrentUsers 字段）。</summary>
    public int GetChannelPopulation(int channelNumber)
    {
        lock (_gate)
        {
            return _populationByChannel.GetValueOrDefault(channelNumber);
        }
    }

    public GameNatReport? GetSelfReport(Guid sessionId)
    {
        lock (_gate)
        {
            return _connections.TryGetValue(sessionId, out var entry) ? entry.SelfReport : null;
        }
    }

    public void SetSelfReport(Guid sessionId, GameNatReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        lock (_gate)
        {
            if (_connections.TryGetValue(sessionId, out var entry))
            {
                entry.SelfReport = report;
            }
        }
    }

    public IPEndPoint? GetClaimedEndpoint(Guid sessionId)
    {
        lock (_gate)
        {
            return _connections.TryGetValue(sessionId, out var entry) ? entry.ClaimedEndpoint : null;
        }
    }

    public void SetClaimedEndpoint(Guid sessionId, IPEndPoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        lock (_gate)
        {
            if (_connections.TryGetValue(sessionId, out var entry))
            {
                entry.ClaimedEndpoint = endpoint;
            }
        }
    }

    public IPEndPoint? GetResolvedEndpoint(Guid sessionId)
    {
        lock (_gate)
        {
            return _connections.TryGetValue(sessionId, out var entry) ? entry.ResolvedEndpoint : null;
        }
    }

    /// <summary>解析端点写入；同时维护反查索引（旧值移除、新值登记），写入点单一。</summary>
    public void SetResolvedEndpoint(Guid sessionId, IPEndPoint? endpoint)
    {
        lock (_gate)
        {
            if (!_connections.TryGetValue(sessionId, out var entry))
            {
                return;
            }

            if (entry.ResolvedEndpoint is not null)
            {
                _sessionsByEndpoint.Remove(EndpointKey(entry.ResolvedEndpoint));
            }

            entry.ResolvedEndpoint = endpoint;
            if (endpoint is not null)
            {
                _sessionsByEndpoint[EndpointKey(endpoint)] = sessionId;
            }
        }
    }

    public bool TryGetSessionByEndpoint(string endpointKey, out Guid sessionId)
    {
        lock (_gate)
        {
            return _sessionsByEndpoint.TryGetValue(endpointKey, out sessionId!);
        }
    }

    /// <summary>认领算法输入：同一 TCP 地址下的连接快照（SelfReport 为不可变引用，读不加其锁）。</summary>
    public IReadOnlyList<TownConnectionView> GetConnectionsOnTcpIp(IPAddress address)
    {
        var key = AddressKey(address);
        List<TownConnectionView> matches = [];
        lock (_gate)
        {
            foreach (var entry in _connections.Values)
            {
                if (string.Equals(AddressKey(entry.TcpAddress), key, StringComparison.Ordinal))
                {
                    matches.Add(new TownConnectionView(entry.SessionId, entry.TcpAddress, entry.SelfReport));
                }
            }
        }

        return matches;
    }

    /// <summary>同一 TCP 地址下是否还有一个以上连接（解析总序第二级的共享判定）。</summary>
    public bool IsSharedTcpIp(IPAddress address)
    {
        var key = AddressKey(address);
        var count = 0;
        lock (_gate)
        {
            foreach (var entry in _connections.Values)
            {
                if (string.Equals(AddressKey(entry.TcpAddress), key, StringComparison.Ordinal)
                    && ++count > 1)
                {
                    return true;
                }
            }
        }

        return false;
    }

    public bool TryGetTcpAddress(Guid sessionId, out IPAddress tcpAddress)
    {
        lock (_gate)
        {
            if (_connections.TryGetValue(sessionId, out var entry))
            {
                tcpAddress = entry.TcpAddress;
                return true;
            }
        }

        tcpAddress = null!;
        return false;
    }

    /// <summary>UDP 可靠块（协议 12）的每连接序号源。</summary>
    public uint NextSequence(Guid sessionId)
    {
        lock (_gate)
        {
            if (_connections.TryGetValue(sessionId, out var entry))
            {
                return entry.NextSequence++;
            }
        }

        return 0;
    }

    public bool TryGetRuntime(Guid sessionId, out RuntimeSession runtime)
    {
        lock (_gate)
        {
            if (_connections.TryGetValue(sessionId, out var entry))
            {
                runtime = entry.Runtime;
                return true;
            }
        }

        runtime = null!;
        return false;
    }

    // ------------------------------------------------------------------
    // 城镇层（在场注册、区域交换、查找、广播）
    // ------------------------------------------------------------------

    /// <summary>
    /// Upserts the sender's presence and exchanges town entities with the
    /// other residents of the area: they learn the arrival through its
    /// appearance packet plus USER_AREA, and the arrival receives the cached
    /// appearance packets of everyone already present. Each session builds
    /// its own appearance packet because only the owning connection has the
    /// character's equipment state; the hub replays the cached bytes.
    /// </summary>
    public void PublishTownPresence(
        PartyPresenceUpdate update,
        GameServerPacket appearancePacket)
    {
        ArgumentNullException.ThrowIfNull(appearancePacket);
        TownPresenceEntry[] sameArea;
        TownPresenceEntry[] leftArea;
        var entry = new TownPresenceEntry(
            update.SessionId,
            update.CharacterId,
            update.AccountUid,
            update.UserId,
            update.Name,
            update.Level,
            update.Job,
            update.TownId,
            update.AreaId,
            update.X,
            update.Y,
            update.Direction,
            update.ChannelNumber,
            appearancePacket);
        lock (_gate)
        {
            leftArea = [];
            if (_presenceBySession.TryGetValue(update.SessionId, out var previous))
            {
                if (previous.TownId != update.TownId || previous.AreaId != update.AreaId)
                {
                    leftArea = SameAreaSnapshotLocked(previous.TownId, previous.AreaId, previous.ChannelNumber, update.SessionId);
                }
            }

            _presenceBySession[update.SessionId] = entry;
            sameArea = SameAreaSnapshotLocked(update.TownId, update.AreaId, update.ChannelNumber, update.SessionId);
        }

        FireTransition(TownPresenceTransitionKind.Entered, entry);
        var userArea = TownPacketBuilders.CreateUserArea(
            update.TownId,
            update.AreaId,
            new GameTownUser(update.UserId, update.X, update.Y, update.Direction));
        foreach (var other in leftArea)
        {
            SendToSession(other.SessionId, TownPacketBuilders.CreateUserLeave(update.UserId));
        }

        foreach (var other in sameArea)
        {
            // The resident learns the arrival...
            SendToSession(other.SessionId, appearancePacket);
            SendToSession(other.SessionId, userArea);
            // ...and the arrival learns the resident.
            SendToSession(update.SessionId, other.Appearance);
            SendToSession(update.SessionId, TownPacketBuilders.CreateUserArea(
                other.TownId,
                other.AreaId,
                new GameTownUser(other.UserId, other.X, other.Y, other.Direction)));
        }
    }

    /// <summary>Movement sync: updates the stored position and pushes
    /// USER_POSITION to the other residents of the area.</summary>
    public void UpdatePresencePosition(Guid sessionId, short x, short y, byte direction)
    {
        TownPresenceEntry? entry;
        TownPresenceEntry[] sameArea;
        lock (_gate)
        {
            if (!_presenceBySession.TryGetValue(sessionId, out entry))
            {
                return;
            }

            entry = entry with { X = x, Y = y, Direction = direction };
            _presenceBySession[sessionId] = entry;
            sameArea = SameAreaSnapshotLocked(entry.TownId, entry.AreaId, entry.ChannelNumber, sessionId);
        }

        var position = TownPacketBuilders.CreateUserPosition(
            new GameTownUser(entry.UserId, x, y, direction));
        foreach (var other in sameArea)
        {
            SendToSession(other.SessionId, position);
        }
    }

    /// <summary>
    /// Delivers a packet to the other residents of the sender's town area.
    /// Returns the number of recipients reached.
    /// </summary>
    public int SendToSameArea(Guid sessionId, GameServerPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        TownPresenceEntry[] sameArea;
        lock (_gate)
        {
            if (!_presenceBySession.TryGetValue(sessionId, out var entry))
            {
                return 0;
            }

            sameArea = SameAreaSnapshotLocked(entry.TownId, entry.AreaId, entry.ChannelNumber, sessionId);
        }

        foreach (var other in sameArea)
        {
            SendToSession(other.SessionId, packet);
        }

        return sameArea.Length;
    }

    /// <summary>
    /// Delivers a packet to the online character with the given name,
    /// resolved through the town presence table (the same table the ACT1
    /// client builds its chat target list from). False when nobody of that
    /// name currently has a town presence.
    /// </summary>
    public bool TrySendToName(string targetName, GameServerPacket packet)
    {
        return TrySendToName(targetName, packet, null, null);
    }

    /// <summary>
    /// TrySendToName with a whisper-context preface: NOTI 12 chat lines carry
    /// only the speaker's area-user id, and the receiver's client resolves
    /// the line's speaker name in its own town-user table. When the partner
    /// lives on another channel or area (FIX-08 presence isolation keeps
    /// their rosters separate), that lookup misses and the line renders
    /// without a speaker — so the sender's town appearance is delivered
    /// first, inserting the find-or-create record the line resolves against.
    /// </summary>
    public bool TrySendToName(
        string targetName,
        GameServerPacket packet,
        Guid? senderSessionId,
        GameServerPacket? senderAppearance)
    {
        ArgumentNullException.ThrowIfNull(targetName);
        ArgumentNullException.ThrowIfNull(packet);
        Guid sessionId;
        lock (_gate)
        {
            TownPresenceEntry? match = null;
            TownPresenceEntry? sender = null;
            if (senderSessionId.HasValue)
            {
                _presenceBySession.TryGetValue(senderSessionId.Value, out sender);
            }

            foreach (var entry in _presenceBySession.Values)
            {
                if (string.Equals(entry.Name, targetName, StringComparison.Ordinal))
                {
                    match = entry;
                    break;
                }
            }

            if (match is null)
            {
                return false;
            }

            sessionId = match.SessionId;
            if (sender is not null
                && senderAppearance is not null
                && (sender.TownId != match.TownId
                    || sender.AreaId != match.AreaId
                    || sender.ChannelNumber != match.ChannelNumber))
            {
                SendToSession(match.SessionId, senderAppearance);
            }
        }

        return SendToSession(sessionId, packet);
    }

    /// <summary>Area departure: drops the presence, tells the area the user
    /// left and fires the transition hook. Called on return-to-character-select.</summary>
    public void RemovePresence(Guid sessionId)
    {
        TownPresenceEntry[] sameArea;
        ushort userId;
        TownPresenceEntry? entry;
        lock (_gate)
        {
            if (!_presenceBySession.TryGetValue(sessionId, out entry))
            {
                return;
            }

            userId = entry.UserId;
            _presenceBySession.Remove(sessionId);
            sameArea = SameAreaSnapshotLocked(entry.TownId, entry.AreaId, entry.ChannelNumber, sessionId);
        }

        FireTransition(TownPresenceTransitionKind.Left, entry);
        var leave = TownPacketBuilders.CreateUserLeave(userId);
        foreach (var other in sameArea)
        {
            SendToSession(other.SessionId, leave);
        }
    }

    public TownPresenceEntry? FindBySession(Guid sessionId)
    {
        lock (_gate)
        {
            return _presenceBySession.TryGetValue(sessionId, out var entry) ? entry : null;
        }
    }

    public TownPresenceEntry? FindByCharacter(Guid characterId)
    {
        lock (_gate)
        {
            foreach (var entry in _presenceBySession.Values)
            {
                if (entry.CharacterId == characterId)
                {
                    return entry;
                }
            }
        }

        return null;
    }

    public TownPresenceEntry? FindByUserId(ushort userId)
    {
        lock (_gate)
        {
            foreach (var entry in _presenceBySession.Values)
            {
                if (entry.UserId == userId)
                {
                    return entry;
                }
            }
        }

        return null;
    }

    /// <summary>同城同区域且同频道的在册名册（M2：城镇可见性按频道隔离，
    /// 不同频道的居民互不下发外观/位置/离开通知）。</summary>
    private TownPresenceEntry[] SameAreaSnapshotLocked(
        byte townId,
        byte areaId,
        int channelNumber,
        Guid excludeSessionId)
    {
        var matches = new List<TownPresenceEntry>();
        foreach (var entry in _presenceBySession.Values)
        {
            if (entry.SessionId != excludeSessionId &&
                entry.TownId == townId &&
                entry.AreaId == areaId &&
                entry.ChannelNumber == channelNumber)
            {
                matches.Add(entry);
            }
        }

        return matches.ToArray();
    }

    private void FireTransition(TownPresenceTransitionKind kind, TownPresenceEntry entry)
    {
        Interlocked.Increment(ref _presenceTransitions);
        Action<TownPresenceTransition>[] hooks;
        lock (_gate)
        {
            hooks = [.. _transitionHooks];
        }

        var transition = new TownPresenceTransition(kind, entry);
        foreach (var hook in hooks)
        {
            try
            {
                hook(transition);
            }
            catch (Exception exception)
            {
                // 订阅方异常不得影响在场状态（docs/design/10 §4.1 翻转钩子纪律）。
                RuntimeLogger.Warn(
                    exception,
                    "A town presence transition hook failed for session {SessionId}.",
                    entry.SessionId);
            }
        }
    }

    private bool SendToSession(Guid sessionId, GameServerPacket packet)
    {
        var delivered = characterSessions.NotifyGamePacket(sessionId, packet);
        if (delivered)
        {
            Interlocked.Increment(ref _broadcastRecipients);
            return true;
        }

        RuntimeLogger.Warn(
            "Dropped a {ProtocolId} packet for session {SessionId}: the connection is gone.",
            packet.ProtocolId,
            sessionId);
        return false;
    }

    internal long BroadcastRecipientCount => Interlocked.Read(ref _broadcastRecipients);

    internal long PresenceTransitionCount => Interlocked.Read(ref _presenceTransitions);

    private static string AddressKey(IPAddress address) =>
        address.MapToIPv4().ToString();

    private static string EndpointKey(IPEndPoint endpoint) =>
        $"{AddressKey(endpoint.Address)}:{endpoint.Port}";
}
