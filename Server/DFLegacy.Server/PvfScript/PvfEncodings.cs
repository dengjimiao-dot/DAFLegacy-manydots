using System.Text;

namespace DFLegacy.Server;

/// <summary>
/// 编码契约的唯一归属地（docs/design/02 · R4）。
/// CP949 用于 PVF 路径表与韩文脚本文本；CP936 用于客户端可见文本；
/// Latin1 用于无语义字节透传。组合根在读取任何配置与脚本之前注册代码页
/// 提供程序；本类型在被独立使用（自测、离线工具）时经
/// <see cref="EnsureRegistered"/> 自行补注册，使 PvfScript 模块自足。
/// </summary>
public static class PvfEncodings
{
    private static readonly object RegistrationGate = new();
    private static bool _registered;

    public static void EnsureRegistered()
    {
        if (_registered)
        {
            return;
        }

        lock (RegistrationGate)
        {
            if (_registered)
            {
                return;
            }

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _registered = true;
        }
    }

    /// <summary>PVF 路径表与 CP949 脚本文本（默认回退语义，与原 GetEncoding(949) 一致）。</summary>
    public static Encoding Cp949()
    {
        EnsureRegistered();
        return Encoding.GetEncoding(949);
    }

    /// <summary>CP949 + ReplacementFallback（Avatar 合成目录等按此语义构造）。</summary>
    public static Encoding Cp949Lossy()
    {
        EnsureRegistered();
        return Encoding.GetEncoding(
            949,
            EncoderFallback.ReplacementFallback,
            DecoderFallback.ReplacementFallback);
    }

    /// <summary>CP936 + ReplacementFallback（脚本节文本、聊天等宽松链路）。</summary>
    public static Encoding Cp936Lossy()
    {
        EnsureRegistered();
        return Encoding.GetEncoding(
            936,
            EncoderFallback.ReplacementFallback,
            DecoderFallback.ReplacementFallback);
    }

    /// <summary>CP936 + ExceptionFallback（客户端可见文本的严格模式，坏字节即抛）。</summary>
    public static Encoding Cp936Strict()
    {
        EnsureRegistered();
        return Encoding.GetEncoding(
            936,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
    }
}
