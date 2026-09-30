using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

/// <summary>
/// 逐包追踪日志（自 EntranceService.LogPacket 两个重载拆出，
/// docs/design/07 §11 日志统一的工具下沉）：先无条件写入 RuntimeState
/// 的 Tap 漏斗与管理快照，再按 tracing 开关输出控制台 hex 与原始字节。
/// 只做观测，不参与协议语义。
/// </summary>
public sealed class PacketTraceLogger(RuntimeState runtime, ServerOptions options)
{
    public void LogPacket(string direction, RuntimeSession session, PacketFrame frame, PacketFrame? rawFrame = null)
    {
        // 漏斗无条件收集（Tap 激活时供智能体抓包），tracing 门卫只挡控制台
        // hex 与日志（docs/design/09-mcp-packet-tap.md §5.2）。
        runtime.TracePacket(session, direction, frame, options.HexDumpLimit, rawFrame);
        if (!options.EnablePacketTracing)
        {
            return;
        }

        var bodyLimit = Math.Min(frame.Body.Length, options.HexDumpLimit);
        var body = HexDump.ToSpacedHex(frame.Body.AsSpan(0, bodyLimit));
        var wire = (rawFrame ?? frame).Encode();
        var rawLimit = Math.Min(wire.Length, options.HexDumpLimit + PacketFrame.HeaderLength);
        var raw = HexDump.ToSpacedHex(wire.AsSpan(0, rawLimit));
        var idLabel = frame.Type == GameProtocolConstants.NotificationPacketType ? "NOTI" : "CMD";
        // Received command bodies are the TryDecode product (the wire carries
        // the client's rolling-transform ciphertext); server-built frames are
        // plaintext, so only RX keeps the decode label.
        var bodyLabel = direction == "RX" ? "TryDecodeBody" : "body";
        RuntimeLogger.Info(
            "{IdLabel} [{SessionId}] Packet={Id}({IdHex}) len={Length} {BodyLabel}=({BodyLen}B) [{Body}]{BodySuffix} crc={Crc:X8} valid={Valid}\n  raw: {Raw}{RawSuffix}",
            idLabel, session.Id, frame.ProtocolId, $"0x{frame.ProtocolId:X2}",
            frame.TotalLength, bodyLabel, frame.Body.Length, body, frame.Body.Length > bodyLimit ? "..." : "",
            frame.DeclaredCrc32, frame.HasValidCrc32,
            raw, wire.Length > rawLimit ? "..." : "");
    }

    public void LogPacket(string direction, RuntimeSession session, GameServerPacket packet)
    {
        runtime.TracePacket(session, direction, packet, options.HexDumpLimit);
        if (!options.EnablePacketTracing)
        {
            return;
        }

        var payloadLimit = Math.Min(packet.Payload.Length, options.HexDumpLimit);
        var payloadDump = HexDump.ToSpacedHex(packet.Payload.AsSpan(0, payloadLimit));
        var wire = packet.Encode();
        var rawLimit = Math.Min(wire.Length, options.HexDumpLimit + GameServerPacket.HeaderLength);
        var raw = HexDump.ToSpacedHex(wire.AsSpan(0, rawLimit));
        var idLabel = packet.Type == GameProtocolConstants.NotificationPacketType ? "NOTI" : "CMD";
        RuntimeLogger.Info(
            "{IdLabel} [{SessionId}] Packet={Id}({IdHex}) len={Length} body=({BodyLen}B) [{Body}]{BodySuffix}\n  raw: {Raw}{RawSuffix}",
            idLabel, session.Id, packet.ProtocolId, $"0x{packet.ProtocolId:X2}",
            packet.TotalLength, packet.Payload.Length, payloadDump, packet.Payload.Length > payloadLimit ? "..." : "",
            raw, wire.Length > rawLimit ? "..." : "");
    }
}
