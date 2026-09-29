using System.Collections.Concurrent;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

public sealed record SessionSnapshot(
    Guid Id,
    string Service,
    string RemoteEndpoint,
    DateTimeOffset ConnectedAt,
    long ReceivedPackets,
    long SentPackets,
    byte? LastProtocolId);

public sealed record PacketTraceSnapshot(
    DateTimeOffset Timestamp,
    Guid SessionId,
    string Service,
    string Direction,
    byte Type,
    byte ProtocolId,
    int TotalLength,
    byte RecordCount,
    uint DeclaredCrc32,
    bool HasValidCrc32,
    string BodyHex,
    bool BodyTruncated,
    string? PacketName = null);

public sealed class RuntimeSession
{
    public required Guid Id { get; init; }
    public required string Service { get; init; }
    public required string RemoteEndpoint { get; init; }
    public DateTimeOffset ConnectedAt { get; init; } = DateTimeOffset.Now;
    public long ReceivedPackets;
    public long SentPackets;
    public byte? LastProtocolId;

    public SessionSnapshot Snapshot() => new(
        Id, Service, RemoteEndpoint, ConnectedAt,
        Interlocked.Read(ref ReceivedPackets),
        Interlocked.Read(ref SentPackets),
        LastProtocolId);
}

public sealed class RuntimeState
{
    private readonly ConcurrentDictionary<Guid, RuntimeSession> _sessions = new();
    private readonly ConcurrentQueue<PacketTraceSnapshot> _recentPackets = new();
    private long _totalConnections;
    private long _totalPackets;
    private const int MaximumRecentPackets = 400;

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;
    public long TotalConnections => Interlocked.Read(ref _totalConnections);
    public long TotalPackets => Interlocked.Read(ref _totalPackets);

    // 包事件唯一漏斗（docs/design/09-mcp-packet-tap.md §5.1）：Tap 面向智能体等
    // 外部消费者，快照/日志继续服务管理界面，两条通道互不依赖。
    // TracingEnabled 由组合根启动时置位（= EnablePacketTracing）。
    public bool TracingEnabled { get; set; }

    /// <summary>MCP 模块激活时由模块宿主注入；默认 null，零开销。</summary>
    public IPacketTapSink? TapSink { get; set; }

    public RuntimeSession Add(string service, string remoteEndpoint)
    {
        var session = new RuntimeSession
        {
            Id = Guid.NewGuid(),
            Service = service,
            RemoteEndpoint = remoteEndpoint
        };
        _sessions[session.Id] = session;
        Interlocked.Increment(ref _totalConnections);
        return session;
    }

    public void CountPacket(RuntimeSession session)
    {
        Interlocked.Increment(ref session.ReceivedPackets);
        Interlocked.Increment(ref _totalPackets);
    }

    public void TracePacket(
        RuntimeSession session,
        string direction,
        PacketFrame frame,
        int dumpLimit,
        PacketFrame? rawFrame = null)
    {
        var tapSink = TapSink;
        if (tapSink is { Active: true })
        {
            tapSink.Publish(
                session.Id,
                session.Service,
                direction,
                frame.Type,
                frame.ProtocolId,
                frame.TotalLength,
                frame.DeclaredCrc32,
                frame.HasValidCrc32,
                TapDecodeMode.ClientCmd,
                frame.Body,
                wire: rawFrame?.Encode() ?? default);
        }

        if (!TracingEnabled)
        {
            return;
        }

        CaptureFrameSnapshot(session, direction, frame, dumpLimit);
    }

    /// <summary>
    /// 管理快照专用：无论 tracing 开关与否都入 400 条快照环，但不进 Tap——
    /// 这些调用点与 LogPacket 的 RX 追踪是同一帧，Tap 发布已由后者承担。
    /// 供"少量关键指令在关闭高频追踪时仍可在管理界面看到"的既有调用点使用。
    /// </summary>
    public void TracePacketAlways(
        RuntimeSession session,
        string direction,
        PacketFrame frame,
        int dumpLimit)
    {
        CaptureFrameSnapshot(session, direction, frame, dumpLimit);
    }

    private void CaptureFrameSnapshot(
        RuntimeSession session,
        string direction,
        PacketFrame frame,
        int dumpLimit)
    {
        var dumpLength = Math.Min(frame.Body.Length, Math.Max(0, dumpLimit));
        PacketNames.TryGet(frame.Type, frame.ProtocolId, out var packetName);
        _recentPackets.Enqueue(new PacketTraceSnapshot(
            DateTimeOffset.Now,
            session.Id,
            session.Service,
            direction,
            frame.Type,
            frame.ProtocolId,
            frame.TotalLength,
            frame.RecordCount,
            frame.DeclaredCrc32,
            frame.HasValidCrc32,
            Convert.ToHexString(frame.Body.AsSpan(0, dumpLength)),
            frame.Body.Length > dumpLength,
            packetName));

        while (_recentPackets.Count > MaximumRecentPackets)
        {
            _recentPackets.TryDequeue(out _);
        }
    }

    public void TracePacket(RuntimeSession session, string direction, GameServerPacket packet, int dumpLimit)
    {
        var tapSink = TapSink;
        if (tapSink is { Active: true })
        {
            tapSink.Publish(
                session.Id,
                session.Service,
                direction,
                packet.Type,
                packet.ProtocolId,
                packet.TotalLength,
                crc32: 0,
                crcValid: true,
                TapDecodeMode.Server,
                packet.Payload);
        }

        if (!TracingEnabled)
        {
            return;
        }

        var dumpLength = Math.Min(packet.Payload.Length, Math.Max(0, dumpLimit));
        PacketNames.TryGet(packet.Type, packet.ProtocolId, out var packetName);
        _recentPackets.Enqueue(new PacketTraceSnapshot(
            DateTimeOffset.Now,
            session.Id,
            session.Service,
            direction,
            packet.Type,
            packet.ProtocolId,
            packet.TotalLength,
            packet.Payload.Length == 0 ? (byte)0 : packet.Payload[0],
            0,
            true,
            Convert.ToHexString(packet.Payload.AsSpan(0, dumpLength)),
            packet.Payload.Length > dumpLength,
            packetName));

        while (_recentPackets.Count > MaximumRecentPackets)
        {
            _recentPackets.TryDequeue(out _);
        }
    }

    public void TraceDatagram(
        RuntimeSession session,
        string direction,
        ReadOnlySpan<byte> datagram,
        int dumpLimit,
        byte protocolId = byte.MaxValue)
    {
        var tapSink = TapSink;
        if (tapSink is { Active: true })
        {
            tapSink.Publish(
                session.Id,
                session.Service,
                direction,
                type: 2,
                protocolId,
                datagram.Length,
                crc32: 0,
                crcValid: true,
                TapDecodeMode.None,
                datagram);
        }

        if (!TracingEnabled)
        {
            return;
        }

        var dumpLength = Math.Min(datagram.Length, Math.Max(0, dumpLimit));
        _recentPackets.Enqueue(new PacketTraceSnapshot(
            DateTimeOffset.Now,
            session.Id,
            session.Service,
            direction,
            2,
            protocolId,
            datagram.Length,
            0,
            0,
            true,
            Convert.ToHexString(datagram[..dumpLength]),
            datagram.Length > dumpLength));

        while (_recentPackets.Count > MaximumRecentPackets)
        {
            _recentPackets.TryDequeue(out _);
        }
    }

    public void Remove(Guid id) => _sessions.TryRemove(id, out _);
    public SessionSnapshot[] Sessions() => _sessions.Values.Select(x => x.Snapshot()).ToArray();
    public PacketTraceSnapshot[] RecentPackets() => _recentPackets.ToArray();
}
