namespace DAFLegacy.Server;

/// <summary>
/// body 口径分类：告诉 Tap 消费方这段原始字节来自哪一路，便于判读来源或后续
/// 接入字段级解码器。取值沿用原 LogPacket 两个重载的分类口径；当前服务端未接入
/// 解码器（<c>Network/Core/PacketPlainText.cs</c> 保留未用），本分类只作来源标识。
/// </summary>
public enum TapDecodeMode
{
    /// <summary>无口径（数据报、无法归类的帧）。</summary>
    None,

    /// <summary>客户端上行 body（客户端密文已解密的 CMD body）。</summary>
    ClientCmd,

    /// <summary>服务端下行 payload。</summary>
    Server,
}

/// <summary>
/// 服务端声明"我能对外提供协议流原始数据"的遥测端口（docs/design/09-mcp-packet-tap.md
/// §4.1）。不含任何 MCP 概念：MCP 模块、SelfTests、未来的 pcap 导出都实现它来消费漏斗。
/// 漏斗只在 <see cref="Active"/> 为 true 时调用 <see cref="Publish"/>，参数为原始值直传——
/// 不拷贝、不查名、不解码，由实现方自行拷贝截断。
/// </summary>
public interface IPacketTapSink
{
    /// <summary>漏斗热路径上唯一的读取项（一次布尔判断）。</summary>
    bool Active { get; }

    /// <summary>
    /// 发布一次协议收发事件。<paramref name="wire"/> 为空跨度表示未捕获线路帧
    /// （TX 帧、UDP 数据报）；RX TCP 帧传含帧头与客户端密文的原始线路字节。
    /// 实现方必须自行拷贝 span（漏斗返回后 span 即失效）。
    /// </summary>
    void Publish(
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
        ReadOnlySpan<byte> wire = default);
}
