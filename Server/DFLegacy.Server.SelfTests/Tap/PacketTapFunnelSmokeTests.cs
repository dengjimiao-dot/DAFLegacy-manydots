using DFLegacy.Protocol;
using DFLegacy.Server;

internal static class PacketTapFunnelSmokeTests
{
    public static void Run(Action<bool, string> check)
    {
        // 漏斗是包事件唯一汇聚点（docs/design/09-mcp-packet-tap.md §5.1/§12）：
        // Tap 通道（TapSink）与快照/日志通道（TracingEnabled）互不依赖。
        var runtime = new RuntimeState();
        var session = runtime.Add("entrance:2311", "127.0.0.1:40000");
        var body = new byte[] { 1, 2, 3, 4 };
        var frame = PacketFrame.Create(protocolId: 49, payload: body, type: 1);
        var rawFrame = PacketFrame.Create(protocolId: 49, payload: new byte[] { 9, 9 }, type: 1);
        var badCrcFrame = new PacketFrame(1, 49, DeclaredCrc32: 0xDEADBEEF, Body: body);

        // 无 TapSink 且 tracing 关：快照队列保持空（/api/packets 回归）。
        runtime.TracePacket(session, "RX", frame, 64);
        check(runtime.RecentPackets().Length == 0,
            "funnel keeps the admin snapshot empty when tracing is off and no tap sink is attached");

        // 仅 Tap 激活：原始参数直传（含 RX 线路帧），快照仍为空。
        var publications = new List<TapPublication>();
        runtime.TapSink = new RecordingTapSink(active: true, publications);
        runtime.TracePacket(session, "RX", frame, 64, rawFrame);
        check(publications.Count == 1
                && publications[0].SessionId == session.Id
                && publications[0].Service == "entrance:2311"
                && publications[0].Direction == "RX"
                && publications[0].Type == 1
                && publications[0].ProtocolId == 49
                && publications[0].TotalLength == frame.TotalLength
                && publications[0].DecodeMode == TapDecodeMode.ClientCmd
                && publications[0].Body.SequenceEqual(frame.Body)
                && publications[0].Wire is not null
                && publications[0].Wire.SequenceEqual(rawFrame.Encode()),
            "an active tap sink receives the raw publish parameters including the RX wire frame");
        check(runtime.RecentPackets().Length == 0,
            "tap-only mode leaves the admin snapshot empty");

        // CRC 校验位随帧直传：坏 CRC 也能被智能体观测。
        runtime.TracePacket(session, "RX", badCrcFrame, 64);
        check(publications.Count == 2
                && publications[1].Crc32 == 0xDEADBEEF
                && !publications[1].CrcValid,
            "declared CRC32 and validity flow through the funnel unchanged");

        // 两者同开：双通道各自正确。
        runtime.TracingEnabled = true;
        runtime.TracePacket(session, "TX", frame, 64);
        check(publications.Count == 3 && runtime.RecentPackets().Length == 1,
            "tracing and tap channels record independently when both are enabled");
        var snapshot = runtime.RecentPackets().Single();
        check(snapshot.Direction == "TX" && snapshot.ProtocolId == 49,
            "the admin snapshot content is unchanged by the tap sink");
        check(snapshot.BodyHex == Convert.ToHexString(frame.Body)
                && snapshot.PacketName == "ENUM_CMDPACKET_SET_PLAY_RESULT",
            "the admin snapshot keeps the packet name lookup behavior");

        // 服务端包路径：decodeMode=Server、无线路帧（TX 线路帧可由 body+帧头推导）。
        var serverPacket = new GameServerPacket(0, 44, [5, 6, 7]);
        runtime.TracePacket(session, "TX", serverPacket, 64);
        check(publications.Count == 4
                && publications[3].Type == 0
                && publications[3].ProtocolId == 44
                && publications[3].DecodeMode == TapDecodeMode.Server
                && publications[3].Body.SequenceEqual(serverPacket.Payload)
                && publications[3].Wire is null,
            "server packet publishes use the server decode mode without a wire frame");

        // UDP 数据报路径：decodeMode=None、type=2、无线路帧。
        runtime.TraceDatagram(session, "RX-UDP", [10, 11, 12], 64);
        check(publications.Count == 5
                && publications[4].Type == 2
                && publications[4].ProtocolId == byte.MaxValue
                && publications[4].DecodeMode == TapDecodeMode.None
                && publications[4].Body.SequenceEqual(new byte[] { 10, 11, 12 })
                && publications[4].Wire is null,
            "datagram publishes use decode mode none with protocol 255 by default");

        runtime.TraceDatagram(session, "TX-UDP", [13, 14], 64, protocolId: 12);
        check(publications.Count == 6 && publications[5].ProtocolId == 12,
            "datagram publishes carry the explicit protocol id");

        // 非激活 TapSink：漏斗布尔判断短路，零发布。
        var idlePublications = new List<TapPublication>();
        runtime.TapSink = new RecordingTapSink(active: false, idlePublications);
        runtime.TracePacket(session, "RX", frame, 64);
        check(idlePublications.Count == 0,
            "an inactive tap sink is never invoked");

        // 少量关键指令（RX-CERA 等）：tracing 关也入管理快照，且不重复进 Tap。
        var ceraPublications = new List<TapPublication>();
        runtime.TapSink = new RecordingTapSink(active: true, ceraPublications);
        var snapshotsBeforeAlways = runtime.RecentPackets().Length;
        runtime.TracingEnabled = false;
        var commandFrame = PacketFrame.Create(protocolId: 49, payload: body, type: 1);
        runtime.TracePacketAlways(session, "RX-CERA", commandFrame, 64);
        check(ceraPublications.Count == 0
                && runtime.RecentPackets().Length == snapshotsBeforeAlways + 1
                && runtime.RecentPackets()[^1].Direction == "RX-CERA",
            "TracePacketAlways feeds only the admin snapshot, tap publication stays with the RX funnel call");

        runtime.TapSink = null;
        runtime.TracingEnabled = false;
    }
}

/// <summary>一次 Publish 的原始参数快照（SelfTests 自实现接缝，§4.1）。</summary>
internal sealed record TapPublication(
    Guid SessionId,
    string Service,
    string Direction,
    byte Type,
    byte ProtocolId,
    int TotalLength,
    uint Crc32,
    bool CrcValid,
    TapDecodeMode DecodeMode,
    byte[] Body,
    byte[]? Wire);

internal sealed class RecordingTapSink : IPacketTapSink
{
    private readonly List<TapPublication> _publications;

    public RecordingTapSink(bool active, List<TapPublication> publications)
    {
        Active = active;
        _publications = publications;
    }

    public bool Active { get; }

    public void Publish(
        Guid sessionId,
        string service,
        string direction,
        byte type,
        byte protocolId,
        int totalLength,
        uint crc32,
        bool crcValid,
        TapDecodeMode decodeMode,
        ReadOnlySpan<byte> body,
        ReadOnlySpan<byte> wire = default)
    {
        _publications.Add(new TapPublication(
            sessionId,
            service,
            direction,
            type,
            protocolId,
            totalLength,
            crc32,
            crcValid,
            decodeMode,
            body.ToArray(),
            wire.IsEmpty ? null : wire.ToArray()));
    }
}
