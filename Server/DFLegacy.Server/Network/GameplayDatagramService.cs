using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

public sealed class GameplayDatagramService(
    ServerOptions options,
    RuntimeState runtime,
    ILogger<GameplayDatagramService> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, GameplaySession> _sessions = new();
    private readonly ConcurrentDictionary<string, IPEndPoint> _observedByAddress = new();
    private readonly ConcurrentDictionary<string, Guid> _sessionsByEndpoint = new();
    private Func<Guid, IReadOnlyList<Guid>?>? _relayResolver;
    private UdpClient? _primary;
    private UdpClient? _secondary;

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

    public void RegisterChannelSession(RuntimeSession runtimeSession, IPAddress tcpAddress)
    {
        ArgumentNullException.ThrowIfNull(runtimeSession);
        ArgumentNullException.ThrowIfNull(tcpAddress);
        _sessions[runtimeSession.Id] = new GameplaySession(
            runtimeSession,
            tcpAddress.MapToIPv4());
    }

    public void UnregisterChannelSession(Guid sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var session))
        {
            return;
        }

        lock (session.Gate)
        {
            if (session.DatagramEndpoint is not null)
            {
                _sessionsByEndpoint.TryRemove(
                    EndpointKey(session.DatagramEndpoint),
                    out _);
            }
        }
    }

    public void SetNatReport(Guid sessionId, GameNatReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            return;
        }

        lock (session.Gate)
        {
            session.NatReport = report;
            var endpoint = SelectDatagramEndpoint(session, report);
            SetDatagramEndpoint(session, endpoint);
        }

        logger.LogInformation(
            "UDP endpoint registered for channel session {SessionId}: nat={NatType} local={LocalAddress} public={PublicAddress}:{PublicPort} mtu={Mtu} target={Target}",
            sessionId,
            report.NatType,
            report.LocalAddress,
            report.PublicAddress,
            report.PublicPort,
            report.Mtu,
            session.DatagramEndpoint);
    }

    public bool TryGetPeerInfo(
        Guid sessionId,
        ushort userId,
        uint accountId,
        out GamePeerInfo peer)
    {
        peer = null!;
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            return false;
        }

        lock (session.Gate)
        {
            if (session.NatReport is not { } report)
            {
                return false;
            }

            var publicPort = report.PublicPort;
            if (publicPort == 0 && session.DatagramEndpoint is not null)
            {
                publicPort = checked((ushort)session.DatagramEndpoint.Port);
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
            || !_sessions.TryGetValue(sessionId, out var session))
        {
            return false;
        }

        IPEndPoint endpoint;
        uint sequence;
        lock (session.Gate)
        {
            if (session.DatagramEndpoint is null)
            {
                return false;
            }

            endpoint = session.DatagramEndpoint;
            sequence = session.NextSequence++;
        }

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
        Interlocked.Increment(ref session.Runtime.SentPackets);
        runtime.TraceDatagram(
            session.Runtime,
            "TX-UDP",
            datagram,
            options.HexDumpLimit,
            protocolId: (byte)GameDatagramProtocol.SetActiveObjectHpProtocol);

        logger.LogInformation(
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
            logger.LogWarning("Gameplay datagram listener is disabled.");
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
        logger.LogInformation(
            "Gameplay UDP primary/secondary listeners ready at {Primary} and {Secondary}",
            primaryEndpoint,
            secondaryEndpoint);

        try
        {
            await Task.WhenAll(
                ReceiveLoopAsync(_primary, "primary", stoppingToken),
                ReceiveLoopAsync(_secondary, "secondary", stoppingToken));
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
            _sessionsByEndpoint.Clear();
            _observedByAddress.Clear();
            _sessions.Clear();
        }
    }

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
                var firstContact = ObserveEndpoint(remote);
                await listener.SendAsync(response, remote, cancellationToken);
                TraceDatagram(remote, datagram, "RX-UDP-NAT");
                TraceDatagram(remote, response, "TX-UDP-NAT", countReceived: false);
                logger.LogInformation(
                    "Gameplay UDP NAT probe (type {ProbeType}) on {Listener} from {Remote}{FirstContact}",
                    datagram[0],
                    listenerName,
                    remote,
                    firstContact ? " [first contact: endpoint registered for relay]" : "");
                continue;
            }

            if (GameDatagramProtocol.IsNatKeepAlive(datagram))
            {
                ObserveEndpoint(remote);
                TraceDatagram(remote, datagram, "RX-UDP-NAT-KEEPALIVE");
                logger.LogDebug(
                    "Handled gameplay UDP NAT keepalive on {Listener} from {Remote}",
                    listenerName,
                    remote);
                continue;
            }

            TraceDatagram(remote, datagram, "RX-UDP");
            logger.LogInformation(
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
            || !_sessionsByEndpoint.TryGetValue(
                EndpointKey(remote),
                out var senderSessionId))
        {
            logger.LogInformation(
                "Gameplay UDP datagram from {Remote} could not be attributed to a session; not relaying.",
                remote);
            return;
        }

        var peers = _relayResolver(senderSessionId);
        if (peers is null)
        {
            logger.LogInformation(
                "Gameplay UDP datagram from session {SenderSessionId} has no relay peers (not in a party).",
                senderSessionId);
            return;
        }

        foreach (var peerSessionId in peers)
        {
            if (peerSessionId == senderSessionId
                || !_sessions.TryGetValue(peerSessionId, out var peer))
            {
                continue;
            }

            IPEndPoint endpoint;
            lock (peer.Gate)
            {
                if (peer.DatagramEndpoint is null)
                {
                    continue;
                }

                endpoint = peer.DatagramEndpoint;
            }

            _ = listener.SendAsync(datagram, endpoint, cancellationToken);
            Interlocked.Increment(ref peer.Runtime.SentPackets);
            logger.LogInformation(
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

        foreach (var session in _sessions.Values)
        {
            lock (session.Gate)
            {
                if (!string.Equals(
                        AddressKey(session.TcpAddress),
                        addressKey,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                SetDatagramEndpoint(session, observed);
            }
        }

        return firstContact;
    }

    private IPEndPoint? SelectDatagramEndpoint(
        GameplaySession session,
        GameNatReport report)
    {
        if (_observedByAddress.TryGetValue(
                AddressKey(session.TcpAddress),
                out var observed))
        {
            return observed;
        }

        if (report.PublicPort == 0
            || report.PublicAddress.Equals(IPAddress.Any)
            || report.PublicAddress.Equals(IPAddress.None))
        {
            return null;
        }

        return new IPEndPoint(
            report.PublicAddress.MapToIPv4(),
            report.PublicPort);
    }

    private void SetDatagramEndpoint(
        GameplaySession session,
        IPEndPoint? endpoint)
    {
        if (session.DatagramEndpoint is not null)
        {
            _sessionsByEndpoint.TryRemove(
                EndpointKey(session.DatagramEndpoint),
                out _);
        }

        session.DatagramEndpoint = endpoint;
        if (endpoint is not null)
        {
            _sessionsByEndpoint[EndpointKey(endpoint)] = session.Runtime.Id;
        }
    }

    private void TraceDatagram(
        IPEndPoint remote,
        ReadOnlySpan<byte> datagram,
        string direction,
        bool countReceived = true)
    {
        if (!_sessionsByEndpoint.TryGetValue(
                EndpointKey(remote),
                out var sessionId)
            || !_sessions.TryGetValue(sessionId, out var session))
        {
            return;
        }

        if (countReceived)
        {
            runtime.CountPacket(session.Runtime);
        }
        else
        {
            Interlocked.Increment(ref session.Runtime.SentPackets);
        }

        runtime.TraceDatagram(
            session.Runtime,
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
                logger.LogWarning(
                    "Ignored malformed gameplay UDP datagram from {Remote} at offset {Offset}; length={Length}",
                    remote,
                    offset,
                    datagram.Length);
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
                logger.LogDebug(
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
                logger.LogWarning(
                    "Gameplay UDP application from {Remote} failed CRC or cipher validation.",
                    remote);
            }
        }
    }

    private static string AddressKey(IPAddress address) =>
        address.MapToIPv4().ToString();

    private static string EndpointKey(IPEndPoint endpoint) =>
        $"{AddressKey(endpoint.Address)}:{endpoint.Port}";

    private sealed class GameplaySession(
        RuntimeSession runtimeSession,
        IPAddress tcpAddress)
    {
        public object Gate { get; } = new();
        public RuntimeSession Runtime { get; } = runtimeSession;
        public IPAddress TcpAddress { get; } = tcpAddress;
        public GameNatReport? NatReport { get; set; }
        public IPEndPoint? DatagramEndpoint { get; set; }
        public uint NextSequence { get; set; }
    }
}
