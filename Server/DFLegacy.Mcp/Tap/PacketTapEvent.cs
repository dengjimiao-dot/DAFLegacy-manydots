using DFLegacy.Server;

namespace DFLegacy.Mcp;

/// <summary>
/// G1 · 协议包实时监听的富事件模型（docs/design/09-mcp-packet-tap.md §4.2）。
/// MCP 面向智能体的数据形状——字段演进不动 Server。
/// </summary>
/// <param name="Seq">跨全部服务的全局单调事件序号，packet_watch 的续读游标。</param>
/// <param name="Timestamp">事件时间（服务器本地时区）。</param>
/// <param name="SessionId">产生事件的网络会话。</param>
/// <param name="Service">会话所属监听服务，如 entrance:2311 / channel:7001 / character-datagram:2311。</param>
/// <param name="Direction">RX / TX / RX-UDP / TX-UDP / RX-UDP-NAT / RX-UDP-NAT-KEEPALIVE 等。</param>
/// <param name="Type">0=NOTI 或 entrance 协议帧、1=CMD、2=UDP 数据报。</param>
/// <param name="ProtocolId">包号（报文名对应的数字编号）；UDP 无报文名时为 255。</param>
/// <param name="PacketName">PacketNames.TryGet 结果（60CN 客户端逆向枚举名），未知为 null。</param>
/// <param name="TotalLength">线路总长（含帧头；UDP 为数据报长度）。</param>
/// <param name="Crc32">帧头声明的 CRC32（UDP/无 CRC 路径为 0）。</param>
/// <param name="CrcValid">CRC 是否通过校验。</param>
/// <param name="Body">解码后 body（截断到 <see cref="PacketTapHub.MaxBodyBytes"/>）。</param>
/// <param name="BodyTruncated">body 是否被截断。</param>
/// <param name="Wire">线路原始帧，仅 RX TCP 帧路径有（含帧头与客户端密文）。</param>
/// <param name="WireTruncated">wire 是否被截断。</param>
/// <param name="PlainText">PacketPlainText 字段级解码文本，未知/失败为 null。</param>
public sealed record PacketTapEvent(
    long Seq,
    DateTimeOffset Timestamp,
    Guid SessionId,
    string Service,
    string Direction,
    byte Type,
    byte ProtocolId,
    string? PacketName,
    int TotalLength,
    uint Crc32,
    bool CrcValid,
    byte[] Body,
    bool BodyTruncated,
    byte[]? Wire,
    bool WireTruncated,
    string? PlainText);
