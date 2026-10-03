using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

public sealed class GameplayDatagramService(
    ServerOptions options,
    RuntimeState runtime,
    TownPresenceService town
) : BackgroundService
{
    // 连接与端点状态的唯一归属是 TownPresenceService（docs/design/10 批次 3）；
    // 本服务保留认领/解析算法、观测缓存与收发循环，经中枢读写连接态。
    private readonly TownPresenceService _town = town;
    private readonly ConcurrentDictionary<Guid, RuntimeSession> _sessionRuntimes = new();
    private readonly ConcurrentDictionary<string, IPEndPoint> _observedByAddress = new();
    private Func<Guid, IReadOnlyList<Guid>?>? _relayResolver;
    private Func<Guid, byte?>? _senderSlotLookup;
    private UdpClient? _primary;
    private UdpClient? _secondary;
    // 数据面遥测（docs/design/10 §4.4）：Interlocked 计数 + 5 秒聚合汇报，
    // 逐包 Info 日志同步降为 Debug；tap 捕获（TraceDatagram）通道不受影响。
    private NatProbeRateLimiter? _rateLimiter;
    private long _rxNatProbe;
    private long _rxKeepalive;
    private long _rxGameplay;
    private long _rxRelayed;
    private long _txRelayFailures;
    private long _rxNoPeerEndpoint;
    private long _rxUnattributed;
    private long _rxRateLimited;
    private long _rxMalformed;
    private long _rxClaimSkipped;
    private long _rxSlotMismatch;
    private int _malformedWarned;
    private int _claimSkippedWarned;
    private int _slotMismatchWarned;

    /// <summary>
    /// Registers the resolver that maps a datagram sender's session to the
    /// peer sessions its gameplay datagrams must be relayed to. Party
    /// real-time traffic flows through this server relay because direct
    /// client-to-client UDP is routinely dropped by client firewalls; the
    /// party endpoint table (NOTI 11) advertises this server's listener for
    /// every peer, so clients only ever talk to it.
    /// </summary>
    public GameplayDatagramService SetRelayResolver(Func<Guid, IReadOnlyList<Guid>?> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _relayResolver = resolver;
        return this;
    }

    /// <summary>
    /// Registers the lookup that maps an attributed datagram sender to its
    /// party roster slot. Frames declaring a different slot are counted and
    /// logged, still relayed: the receiver attributes frames by this slot
    /// byte (party 分册「数据面归属」第 1 条), so a mismatch is impersonation
    /// evidence, but the send-side anchor that every legitimate frame carries
    /// the sender's own slot is not yet confirmed on real machines — the
    /// counter decides a later tightening, per docs/design/10 §4.5.
    /// </summary>
    public GameplayDatagramService SetSenderSlotLookup(Func<Guid, byte?> senderSlotLookup)
    {
        ArgumentNullException.ThrowIfNull(senderSlotLookup);
        _senderSlotLookup = senderSlotLookup;
        return this;
    }

    public void RegisterChannelSession(RuntimeSession runtimeSession, int channelNumber, IPAddress tcpAddress)
    {
        ArgumentNullException.ThrowIfNull(runtimeSession);
        ArgumentNullException.ThrowIfNull(tcpAddress);
        _sessionRuntimes[runtimeSession.Id] = runtimeSession;
        _town.AcquireConnection(runtimeSession.Id, channelNumber, runtimeSession, tcpAddress);
    }

    public void UnregisterChannelSession(Guid sessionId)
    {
        _sessionRuntimes.TryRemove(sessionId, out _);
        _town.ReleaseConnection(sessionId);
    }

    public void SetNatReport(Guid sessionId, GameNatReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!_sessionRuntimes.TryGetValue(sessionId, out _)
            || !_town.TryGetTcpAddress(sessionId, out var tcpAddress))
        {
            return;
        }

        _town.SetSelfReport(sessionId, report);
        var endpoint = ResolveEndpoint(
            _town.GetClaimedEndpoint(sessionId),
            tcpAddress,
            _town.IsSharedTcpIp(tcpAddress),
            _observedByAddress,
            report);
        _town.SetResolvedEndpoint(sessionId, endpoint);

        RuntimeLogger.Info(
            "UDP endpoint registered for channel session {SessionId}: nat={NatType} local={LocalAddress} public={PublicAddress}:{PublicPort} mtu={Mtu} target={Target}",
            sessionId,
            report.NatType,
            report.LocalAddress,
            report.PublicAddress,
            report.PublicPort,
            report.Mtu,
            endpoint);
    }

    public bool TryGetPeerInfo(
        Guid sessionId,
        ushort userId,
        uint accountId,
        out GamePeerInfo peer)
    {
        peer = null!;
        var report = _town.GetSelfReport(sessionId);
        if (report is null)
        {
            return false;
        }

        var publicPort = report.PublicPort;
        var resolved = _town.GetResolvedEndpoint(sessionId);
        if (publicPort == 0 && resolved is not null)
        {
            publicPort = checked((ushort)resolved.Port);
        }

        peer = new GamePeerInfo(
            userId,
            report.LocalAddress.MapToIPv4(),
            report.PublicAddress.MapToIPv4(),
            publicPort,
            accountId,
            report.NatType,
            report.Mtu);
        return true;
    }

    public async ValueTask<bool> SendSetActiveObjectHpAsync(
        Guid sessionId,
        ushort objectType,
        ushort objectId,
        uint hp,
        uint secondaryValue,
        byte senderPartyIndex,
        CancellationToken cancellationToken)
    {
        if (_primary is null
            || !_sessionRuntimes.TryGetValue(sessionId, out var sessionRuntime)
            || _town.GetResolvedEndpoint(sessionId) is not { } endpoint)
        {
            return false;
        }

        var sequence = _town.NextSequence(sessionId);

        var application = GameDatagramProtocol.CreateSetActiveObjectHpApplication(
            objectType,
            objectId,
            hp,
            secondaryValue);
        var datagram = GameDatagramProtocol.CreateUnreliableFrame(
            sequence,
            senderPartyIndex,
            flag: 0,
            application);
        await _primary.SendAsync(datagram, endpoint, cancellationToken);
        Interlocked.Increment(ref sessionRuntime.SentPackets);
        runtime.TraceDatagram(
            sessionRuntime,
            "TX-UDP",
            datagram,
            options.HexDumpLimit,
            protocolId: (byte)GameDatagramProtocol.SetActiveObjectHpProtocol);

        RuntimeLogger.Info(
            "UDP protocol 12 sent to {Endpoint}: sequence={Sequence} sender={Sender} object={ObjectType}:{ObjectId} hp={Hp} secondary={Secondary}",
            endpoint,
            sequence,
            senderPartyIndex,
            objectType,
            objectId,
            hp,
            secondaryValue);
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.GameplayDatagram.Enabled)
        {
            RuntimeLogger.Warn("Gameplay datagram listener is disabled.");
            return;
        }

        if (options.GameplayDatagram.PrimaryPort == options.GameplayDatagram.SecondaryPort)
        {
            throw new InvalidOperationException(
                "Gameplay datagram primary and secondary ports must differ.");
        }

        if (options.GameplayDatagram.SenderPartyIndex >= 8)
        {
            throw new InvalidOperationException(
                "Gameplay datagram sender party index must be between zero and seven.");
        }

        var address = IPAddress.Parse(options.GameplayDatagram.Host);
        var primaryEndpoint = new IPEndPoint(
            address,
            options.GameplayDatagram.PrimaryPort);
        var secondaryEndpoint = new IPEndPoint(
            address,
            options.GameplayDatagram.SecondaryPort);
        _primary = new UdpClient(primaryEndpoint);
        _secondary = new UdpClient(secondaryEndpoint);
        // 双端口共用同一限流器实例：客户端以同一 socket 交替探测两端口（docs/design/10 §4.3）。
        _rateLimiter = new NatProbeRateLimiter(options.GameplayDatagram.NatProbeRateLimit);
        RuntimeLogger.Info(
            "Gameplay UDP primary/secondary listeners ready at {Primary} and {Secondary} (nat probe rate limit {RateLimit}/s per IP)",
            primaryEndpoint,
            secondaryEndpoint,
            options.GameplayDatagram.NatProbeRateLimit);

        try
        {
            await Task.WhenAll(
                ReceiveLoopAsync(_primary, "primary", stoppingToken),
                ReceiveLoopAsync(_secondary, "secondary", stoppingToken),
                ReportLoopAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _primary.Dispose();
            _secondary.Dispose();
            _primary = null;
            _secondary = null;
            _rateLimiter = null;
            // 连接态与端点归属在中枢（连接级生命周期），不随本服务停机清理。
            _sessionRuntimes.Clear();
            _observedByAddress.Clear();
        }
    }

    /// <summary>每 5 秒一条聚合汇总，有活动才输出（形态对齐参考实现的周期流量报告）。</summary>
    private async Task ReportLoopAsync(CancellationToken cancellationToken)
    {
        var previous = SnapshotCounters();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var current = SnapshotCounters();
                var active = false;
                for (var index = 0; index < current.Length; index++)
                {
                    if (current[index] != previous[index])
                    {
                        active = true;
                        break;
                    }
                }

                if (active)
                {
                    RuntimeLogger.Info(
                        "UDP dataplane aggregate: probe={Probe} keepalive={Keepalive} gameplay={Gameplay} relayed={Relayed} relayFailed={RelayFailed} noPeerEndpoint={NoPeerEndpoint} unattributed={Unattributed} rateLimited={RateLimited} malformed={Malformed} claimSkipped={ClaimSkipped} slotMismatch={SlotMismatch}",
                        current[0], current[1], current[2], current[3],
                        current[4], current[5], current[6], current[7],
                        current[8], current[9], current[10]);
                }

                previous = current;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private long[] SnapshotCounters() =>
    [
        Interlocked.Read(ref _rxNatProbe),
        Interlocked.Read(ref _rxKeepalive),
        Interlocked.Read(ref _rxGameplay),
        Interlocked.Read(ref _rxRelayed),
        Interlocked.Read(ref _txRelayFailures),
        Interlocked.Read(ref _rxNoPeerEndpoint),
        Interlocked.Read(ref _rxUnattributed),
        Interlocked.Read(ref _rxRateLimited),
        Interlocked.Read(ref _rxMalformed),
        Interlocked.Read(ref _rxClaimSkipped),
        Interlocked.Read(ref _rxSlotMismatch),
    ];

    private async Task ReceiveLoopAsync(
        UdpClient listener,
        string listenerName,
        CancellationToken cancellationToken)
    {
        // 逐监听单线程消费：块解析结果复用同一清单，稳态零清单分配。
        var blockFrames = new List<GameMtUdpFrame>();
        while (!cancellationToken.IsCancellationRequested)
        {
            var received = await listener.ReceiveAsync(cancellationToken);
            var remote = received.RemoteEndPoint;
            var datagram = received.Buffer;

            if (GameDatagramProtocol.TryCreateNatProbeResponse(
                    datagram,
                    remote,
                    out var response))
            {
                // 超限静默丢弃：不回包、不学习端点，客户端按既有节奏重试（docs/design/10 §4.3）。
                if (!_rateLimiter!.TryAcquire(remote.Address))
                {
                    Interlocked.Increment(ref _rxRateLimited);
                    continue;
                }

                var firstContact = ObserveEndpoint(remote);
                Interlocked.Increment(ref _rxNatProbe);
                await listener.SendAsync(response, remote, cancellationToken);
                TraceDatagram(remote, datagram, "RX-UDP-NAT");
                TraceDatagram(remote, response, "TX-UDP-NAT", countReceived: false);
                RuntimeLogger.Info(
                    "Gameplay UDP NAT probe (type {ProbeType}) on {Listener} from {Remote}{FirstContact}",
                    datagram[0],
                    listenerName,
                    remote,
                    firstContact ? " [first contact: endpoint registered for relay]" : "");
                continue;
            }

            if (GameDatagramProtocol.IsNatKeepAlive(datagram))
            {
                Interlocked.Increment(ref _rxKeepalive);
                ObserveEndpoint(remote);
                TraceDatagram(remote, datagram, "RX-UDP-NAT-KEEPALIVE");
                RuntimeLogger.Debug(
                    "Handled gameplay UDP NAT keepalive on {Listener} from {Remote}",
                    listenerName,
                    remote);
                continue;
            }

            TraceDatagram(remote, datagram, "RX-UDP");
            Interlocked.Increment(ref _rxGameplay);
            RuntimeLogger.Debug(
                "Gameplay UDP datagram from {Remote}: {Length} bytes, head {Head}",
                remote,
                datagram.Length,
                Convert.ToHexString(datagram, 0, Math.Min(datagram.Length, 16)));
            // 转发前置结构门（docs/design/10 §4.5）：块结构解析失败的收包不再转发。
            // 客户端接收端对越界/无法归属的块本就整包丢弃（party 分册「数据面归属」
            // 第 1 条），转发畸形包只有跨成员放大效果；合法客户端按同一块布局组帧，
            // 该门不触及合法流量。CRC/密码学失败不设门——那是接收端的校验职责。
            blockFrames.Clear();
            if (!TryReadBlockSequence(datagram, blockFrames))
            {
                Interlocked.Increment(ref _rxMalformed);
                if (Interlocked.Exchange(ref _malformedWarned, 1) == 0)
                {
                    RuntimeLogger.Warn(
                        "Ignored malformed gameplay UDP datagram from {Remote}: length={Length}, not relayed (first occurrence; further occurrences are counted in the aggregate)",
                        remote,
                        datagram.Length);
                }

                continue;
            }

            LogBlockApplications(remote, blockFrames);
            RelayDatagram(listener, remote, datagram, blockFrames, cancellationToken);
        }
    }

    /// <summary>
    /// 转发门的结构遍历（docs/design/10 §4.5）：整包按 MT-UDP 块序列解析，任一
    /// 块越界或消费停滞即整包不合格。类型 1/2 块随遍历物化进 <paramref name="frames"/>
    /// （供槽位观测与逐块日志复用，不再二次解析）；类型 0 块不物化。空数据报
    /// 视为合格（与现行转发行为一致）。
    /// </summary>
    internal static bool TryReadBlockSequence(
        ReadOnlySpan<byte> datagram,
        List<GameMtUdpFrame> frames)
    {
        var offset = 0;
        while (offset < datagram.Length)
        {
            if (!GameDatagramProtocol.TryReadBlock(
                    datagram[offset..],
                    out var frame,
                    out var consumed)
                || consumed <= 0)
            {
                return false;
            }

            offset += consumed;
            if (frame is not null)
            {
                frames.Add(frame);
            }
        }

        return true;
    }

    /// <summary>
    /// Forwards one client's gameplay datagram to its relay peers' observed
    /// UDP endpoints through the same listener, so replies hairpin through
    /// the server exactly like the advertised endpoint table expects. The
    /// synchronous resolution (attribution, slot observation, peer endpoint
    /// lookup) runs on the receive loop; each peer send is dispatched as its
    /// own observed task (<see cref="SendRelayedDatagramAsync"/>), so a send
    /// never blocks the next receive. Concurrent SendToAsync calls on one
    /// listener are safe: the underlying Socket is thread-safe.
    /// </summary>
    private void RelayDatagram(
        UdpClient listener,
        IPEndPoint remote,
        byte[] datagram,
        IReadOnlyList<GameMtUdpFrame> frames,
        CancellationToken cancellationToken)
    {
        if (_relayResolver is null
            || !_town.TryGetSessionByEndpoint(
                EndpointKey(remote),
                out var senderSessionId))
        {
            Interlocked.Increment(ref _rxUnattributed);
            RuntimeLogger.Debug(
                "Gameplay UDP datagram from {Remote} could not be attributed to a session; not relaying.",
                remote);
            return;
        }

        CountSenderSlotMismatches(senderSessionId, frames);

        var peers = _relayResolver(senderSessionId);
        if (peers is null)
        {
            RuntimeLogger.Debug(
                "Gameplay UDP datagram from session {SenderSessionId} has no relay peers (not in a party).",
                senderSessionId);
            return;
        }

        foreach (var peerSessionId in peers)
        {
            if (peerSessionId == senderSessionId)
            {
                continue;
            }

            var endpoint = _town.GetResolvedEndpoint(peerSessionId);
            if (endpoint is null)
            {
                if (_sessionRuntimes.ContainsKey(peerSessionId))
                {
                    Interlocked.Increment(ref _rxNoPeerEndpoint);
                }

                continue;
            }

            _ = SendRelayedDatagramAsync(
                listener,
                datagram,
                endpoint,
                senderSessionId,
                peerSessionId,
                cancellationToken);
        }
    }

    /// <summary>
    /// Sends one relayed datagram to a single peer and observes the outcome:
    /// a socket failure counts as txRelayFailures and never as relayed, while
    /// shutdown races (cancellation, disposed listener) end silently. The
    /// receive loop deliberately does not await this task, so it must never
    /// leak an unobserved exception.
    /// </summary>
    private async Task SendRelayedDatagramAsync(
        UdpClient listener,
        byte[] datagram,
        IPEndPoint endpoint,
        Guid senderSessionId,
        Guid peerSessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            await listener.SendAsync(datagram, endpoint, cancellationToken);
        }
        catch (SocketException)
        {
            Interlocked.Increment(ref _txRelayFailures);
            return;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        Interlocked.Increment(ref _rxRelayed);
        if (_sessionRuntimes.TryGetValue(peerSessionId, out var peerRuntime))
        {
            Interlocked.Increment(ref peerRuntime.SentPackets);
        }

        RuntimeLogger.Debug(
            "Relayed a {Length}-byte gameplay datagram (head {Head}) from session {SenderSessionId} to {Endpoint}.",
            datagram.Length,
            Convert.ToHexString(datagram, 0, Math.Min(datagram.Length, 8)),
            senderSessionId,
            endpoint);
    }

    /// <summary>
    /// 槽位伪造观测（docs/design/10 §4.5，先计数不拦截）：帧内偏移 7 的发送槽位
    /// 与名册槽位不一致即计数并首见 Warn，数据报仍照常转发。客户端接收端按该
    /// 字节选对端对象，mismatch 即冒名证据；发送侧「合法帧恒写自身槽位」尚无
    /// 发送点锚定，拦截收紧以真机计数为零为前提，不在本批实施。
    /// </summary>
    private void CountSenderSlotMismatches(
        Guid senderSessionId,
        IReadOnlyList<GameMtUdpFrame> frames)
    {
        var expectedSlot = _senderSlotLookup?.Invoke(senderSessionId);
        if (expectedSlot is not { } slot)
        {
            return;
        }

        foreach (var frame in frames)
        {
            if (frame.SenderPartyIndex == slot)
            {
                continue;
            }

            Interlocked.Increment(ref _rxSlotMismatch);
            if (Interlocked.Exchange(ref _slotMismatchWarned, 1) == 0)
            {
                RuntimeLogger.Warn(
                    "Gameplay datagram frame from session {SenderSessionId} declares sender slot {DeclaredSlot} but the roster holds slot {ExpectedSlot}; relayed as-is (observation only, first occurrence)",
                    senderSessionId,
                    frame.SenderPartyIndex,
                    slot);
            }
        }
    }

    private bool ObserveEndpoint(IPEndPoint remote)
    {
        var addressKey = AddressKey(remote.Address);
        var observed = new IPEndPoint(remote.Address.MapToIPv4(), remote.Port);
        var firstContact = !_observedByAddress.ContainsKey(addressKey);
        _observedByAddress[addressKey] = observed;

        // 端点学习按会话认领（docs/design/10 §4.2）：观测端点精确命中唯一连接的自报
        // 公网端点 → 只认领该连接；无命中且该 IP 仅一个连接 → 沿用整体学习（现行语义，
        // 单会话等价性由本支保证）；多连接且无法区分 → 不学习、不覆盖任何连接。
        var candidates = _town.GetConnectionsOnTcpIp(remote.Address);
        var claim = FindUniqueClaimTarget(candidates, observed);
        if (claim is not null)
        {
            _town.SetClaimedEndpoint(claim.Value, observed);
            _town.SetResolvedEndpoint(claim.Value, observed);
        }
        else if (candidates.Count == 1)
        {
            _town.SetResolvedEndpoint(candidates[0].SessionId, observed);
        }
        else
        {
            Interlocked.Increment(ref _rxClaimSkipped);
            if (Interlocked.Exchange(ref _claimSkippedWarned, 1) == 0)
            {
                RuntimeLogger.Warn(
                    "Endpoint observation from {Remote} matched no self-reported endpoint across {Count} sessions on {Address}; skipping endpoint learning (first occurrence)",
                    remote,
                    candidates.Count,
                    addressKey);
            }
        }

        return firstContact;
    }

    /// <summary>
    /// 精确认领匹配：返回自报公网端点与观测端点精确相等的唯一连接；无命中或多连接
    /// 自报同端点（无法区分）时返回 null。纯函数，快照上判定。
    /// </summary>
    internal static Guid? FindUniqueClaimTarget(
        IReadOnlyList<TownConnectionView> candidates,
        IPEndPoint observed)
    {
        Guid? match = null;
        foreach (var candidate in candidates)
        {
            var report = candidate.SelfReport;
            if (report is null
                || report.PublicPort == 0
                || !report.PublicAddress.Equals(observed.Address)
                || report.PublicPort != observed.Port)
            {
                continue;
            }

            if (match is not null)
            {
                return null;
            }

            match = candidate.SessionId;
        }

        return match;
    }

    /// <summary>
    /// 解析总序（docs/design/10 §4.2）：认领端点 &gt; 非共享 IP 的观测端点（现行语义）
    /// &gt; 自报公网端点 &gt; 无。共享 IP 且无认领时不得采用按 IP 共享的观测值——
    /// 这是相对现行唯一收紧，单连接场景与现行严格等价。
    /// </summary>
    internal static IPEndPoint? ResolveEndpoint(
        IPEndPoint? claimedEndpoint,
        IPAddress tcpAddress,
        bool sharedIp,
        IReadOnlyDictionary<string, IPEndPoint> observedByAddress,
        GameNatReport report)
    {
        if (claimedEndpoint is not null)
        {
            return claimedEndpoint;
        }

        if (!sharedIp
            && observedByAddress.TryGetValue(AddressKey(tcpAddress), out var observed))
        {
            return observed;
        }

        if (report.PublicPort == 0
            || report.PublicAddress.Equals(IPAddress.Any)
            || report.PublicAddress.Equals(IPAddress.None))
        {
            return null;
        }

        return new IPEndPoint(report.PublicAddress.MapToIPv4(), report.PublicPort);
    }

    private void TraceDatagram(
        IPEndPoint remote,
        ReadOnlySpan<byte> datagram,
        string direction,
        bool countReceived = true)
    {
        if (!_town.TryGetSessionByEndpoint(
                EndpointKey(remote),
                out var sessionId)
            || !_sessionRuntimes.TryGetValue(sessionId, out var sessionRuntime))
        {
            return;
        }

        if (countReceived)
        {
            runtime.CountPacket(sessionRuntime);
        }
        else
        {
            Interlocked.Increment(ref sessionRuntime.SentPackets);
        }

        runtime.TraceDatagram(
            sessionRuntime,
            direction,
            datagram,
            options.HexDumpLimit);
    }

    /// <summary>逐块应用层解析日志（Debug）：CRC/密码学失败只 Warn 不设门——
    /// 接收端客户端对应用块自带校验，转发门只看块结构（docs/design/10 §4.5）。</summary>
    private void LogBlockApplications(
        IPEndPoint remote,
        IReadOnlyList<GameMtUdpFrame> frames)
    {
        foreach (var frame in frames)
        {
            if (GameDatagramProtocol.TryParseApplication(
                    frame.Application,
                    out var application))
            {
                RuntimeLogger.Debug(
                    "RX gameplay UDP from {Remote}: block={BlockType} sequence={Sequence} sender={Sender} flag={Flag} protocol={ProtocolId} payload={PayloadLength}",
                    remote,
                    frame.BlockType,
                    frame.Sequence,
                    frame.SenderPartyIndex,
                    frame.Flag,
                    application.ProtocolId,
                    application.Payload.Length);
            }
            else
            {
                RuntimeLogger.Warn(
                    "Gameplay UDP application from {Remote} failed CRC or cipher validation.",
                    remote);
            }
        }
    }

    private static string AddressKey(IPAddress address) =>
        address.MapToIPv4().ToString();

    private static string EndpointKey(IPEndPoint endpoint) =>
        $"{AddressKey(endpoint.Address)}:{endpoint.Port}";
}
