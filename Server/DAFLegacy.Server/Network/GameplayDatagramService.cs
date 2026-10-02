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
    private UdpClient? _primary;
    private UdpClient? _secondary;
    // 数据面遥测（docs/design/10 §4.4）：Interlocked 计数 + 5 秒聚合汇报，
    // 逐包 Info 日志同步降为 Debug；tap 捕获（TraceDatagram）通道不受影响。
    private NatProbeRateLimiter? _rateLimiter;
    private long _rxNatProbe;
    private long _rxKeepalive;
    private long _rxGameplay;
    private long _rxRelayed;
    private long _rxNoPeerEndpoint;
    private long _rxUnattributed;
    private long _rxRateLimited;
    private long _rxMalformed;
    private long _rxClaimSkipped;
    private int _malformedWarned;
    private int _claimSkippedWarned;

    /// <summary>
    /// Registers the resolver that maps a datagram sender's session to the
    /// peer sessions its gameplay datagrams must be relayed to. Party
    /// real-time traffic flows through this server relay because direct
    /// client-to-client UDP is routinely dropped by client firewalls; the
    /// party endpoint table (NOTI 11) advertises this server's listener for
    /// every peer, so clients only ever talk to it.
    /// </summary>
    public void SetRelayResolver(Func<Guid, IReadOnlyList<Guid>?> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _relayResolver = resolver;
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
                        "UDP dataplane aggregate: probe={Probe} keepalive={Keepalive} gameplay={Gameplay} relayed={Relayed} noPeerEndpoint={NoPeerEndpoint} unattributed={Unattributed} rateLimited={RateLimited} malformed={Malformed} claimSkipped={ClaimSkipped}",
                        current[0], current[1], current[2], current[3],
                        current[4], current[5], current[6], current[7], current[8]);
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
        Interlocked.Read(ref _rxNoPeerEndpoint),
        Interlocked.Read(ref _rxUnattributed),
        Interlocked.Read(ref _rxRateLimited),
        Interlocked.Read(ref _rxMalformed),
        Interlocked.Read(ref _rxClaimSkipped),
    ];

    private async Task ReceiveLoopAsync(
        UdpClient listener,
        string listenerName,
        CancellationToken cancellationToken)
    {
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
            RelayDatagram(listener, remote, datagram, cancellationToken);
            LogMtUdpBlocks(remote, datagram);
        }
    }

    /// <summary>
    /// Forwards one client's gameplay datagram to its relay peers' observed
    /// UDP endpoints through the same listener, so replies hairpin through
    /// the server exactly like the advertised endpoint table expects.
    /// </summary>
    private void RelayDatagram(
        UdpClient listener,
        IPEndPoint remote,
        byte[] datagram,
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

            _ = listener.SendAsync(datagram, endpoint, cancellationToken);
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

    private void LogMtUdpBlocks(IPEndPoint remote, ReadOnlySpan<byte> datagram)
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
                Interlocked.Increment(ref _rxMalformed);
                if (Interlocked.Exchange(ref _malformedWarned, 1) == 0)
                {
                    RuntimeLogger.Warn(
                        "Ignored malformed gameplay UDP datagram from {Remote} at offset {Offset}; length={Length} (first occurrence; further occurrences are counted in the aggregate)",
                        remote,
                        offset,
                        datagram.Length);
                }

                return;
            }

            offset += consumed;
            if (frame is null)
            {
                continue;
            }

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
