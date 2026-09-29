namespace DFLegacy.Server;

/// <summary>
/// 明文富化提示：告诉 Tap 消费方按哪套现有解码器解读 body。取值镜像
/// <see cref="PacketPlainText"/> 在 LogPacket 两个重载里的选择逻辑。
/// </summary>
public enum TapDecodeMode
{
    /// <summary>不解码（UDP 数据报、无法归类的帧）。</summary>
    None,

    /// <summary>客户端上行 body：<see cref="PacketPlainText.Decode"/>，未知或非 CMD 为 null。</summary>
    ClientCmd,

    /// <summary>服务端下行 payload：<see cref="PacketPlainText.DecodeServer"/>。</summary>
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
