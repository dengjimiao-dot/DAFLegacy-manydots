namespace DAFLegacy.Server;

/// <summary>
/// 十六进制格式化工具(docs/design/07 §11 日志统一的共享拆分,
/// 逐包日志本体现居同目录 PacketTraceLogger):只做字节到文本的排版,
/// 不涉及任何加密/解密或协议语义,供统一日志与逐包捕获共用。
/// </summary>
public static class HexDump
{
    /// <summary>空格分隔的大写十六进制;空输入返回空串。</summary>
    public static string ToSpacedHex(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        return string.Create(bytes.Length * 3 - 1, bytes.ToArray(), (span, buffer) =>
        {
            const string HexChars = "0123456789ABCDEF";
            for (var i = 0; i < buffer.Length; i++)
            {
                span[i * 3] = HexChars[buffer[i] >> 4];
                span[i * 3 + 1] = HexChars[buffer[i] & 0xF];
                if (i + 1 < buffer.Length)
                {
                    span[i * 3 + 2] = ' ';
                }
            }
        });
    }
}
