using System.Globalization;

namespace DAFLegacy.Protocol;

/// <summary>
/// 单条频道目录条目，来自 PVF 内 channel_info.etc 的组 1 频道行。DisplayName 是
/// 经 etc/etc.chn.str 名称引用解析出的频道显示名（客户端字符串表引用本身不是显示名，
/// 不进元数据）。端点与容量不在 etc 格式内：全部频道统一使用 Channel 节的协议面身份
/// （Host/Port/MaximumUsers）与在线会话数动态值（EntranceMetadata.BuildChannels 落地）。
/// Type/Tag 承载 etc 的分类列，仅供后续按频道准入（回填方案 §4.3 M2）消费；
/// 60A1 元数据记录本身不携带分类字段。
/// </summary>
public sealed record ChannelCatalogEntry(
    int GroupIndex,
    int ChannelNumber,
    int Type,
    string? Tag,
    string DisplayName);

/// <summary>
/// 入口频道目录（参考项目 A21 Network/Protocol/ChannelProtocolHandler.ParseScriptChannels
/// 的逻辑平移，搬逻辑不搬格式）。频道目录唯一来源是 PVF 内 etc/channel_info.etc（显示名
/// 经 etc/etc.chn.str 解析），server.json 不提供频道目录配置；与参考项目一致只取
/// [server] 组 1：组 1 的频道行落到元数据组下标 0（cain），其余组由
/// EntranceMetadata.BuildChannels 回落单频道默认记录，满足 60A1 客户端
/// 「九组记录齐全」的硬性要求（60a4-handshake-protocol.md §11.3）。
/// </summary>
public static class ChannelCatalog
{
    /// <summary>
    /// PVF 内频道目录脚本路径；同时是协议 9 下发脚本的真源，目录解析与脚本下发
    /// 读同一份文件，保证客户端脚本与服务端元数据的频道号同源一致。
    /// </summary>
    public const string ChannelInfoEtcPath = "etc/channel_info.etc";

    /// <summary>PVF 内频道显示名表路径（名称引用 → 显示文本）。</summary>
    public const string ChannelStringTablePath = "etc/etc.chn.str";

    /// <summary>
    /// 解析 channel_info.etc 的 [server] 节组 1 频道行，显示名按名称引用查
    /// <paramref name="displayNames"/>（etc/etc.chn.str 解析结果）；引用缺失或查不到
    /// 的行按坏行丢弃。行形态兼容两种布局：频道号 + 反引号名称引用 + 类型 + 反引号
    /// 组名（A21 样本）与频道号 + 裸名称引用 + 类型 + 反引号组名（60CN 现网文件）。
    /// 组行 = 剥离 // 注释后仅剩单个整数的行（如「1 // 凯恩」）；仅组号 1 触发收集，
    /// 其余组号停止收集，与参考项目「只取组 1」一致。缺类型列或频道号越界
    /// （1..255 之外）的行同样丢弃，不中断解析。
    /// </summary>
    public static IReadOnlyList<ChannelCatalogEntry>? TryParseChannelInfoEtc(
        string? text,
        IReadOnlyDictionary<string, string>? displayNames)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var entries = new List<ChannelCatalogEntry>();
        var inServer = false;
        var collecting = false;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = StripLineComment(rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.Equals("[server]", StringComparison.OrdinalIgnoreCase))
            {
                inServer = true;
                collecting = false;
                continue;
            }

            if (line.Equals("[/server]", StringComparison.OrdinalIgnoreCase))
            {
                inServer = false;
                collecting = false;
                continue;
            }

            if (!inServer)
            {
                continue;
            }

            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 1)
            {
                collecting = TryParseInvariantInt32(tokens[0], out var groupNumber)
                             && groupNumber == 1;
                continue;
            }

            if (!collecting
                || tokens.Length < 3
                || !TryParseInvariantInt32(tokens[0], out var channelNumber)
                || channelNumber is < 1 or > 255
                || !TryFindChannelType(tokens, out var type)
                || !TryResolveDisplayName(tokens, displayNames, out var displayName))
            {
                continue;
            }

            entries.Add(new ChannelCatalogEntry(
                GroupIndex: 0,
                ChannelNumber: channelNumber,
                Type: type,
                Tag: FindChannelTag(tokens),
                DisplayName: displayName));
        }

        return entries.Count > 0 ? entries : null;
    }

    /// <summary>
    /// 解析 etc/etc.chn.str 的「名称引用&gt;显示文本」行表（CP936 文本由调用方解码后
    /// 传入）；键容忍 &lt;键&gt; 与裸键两种形态，重复键后行覆盖前行。
    /// </summary>
    public static IReadOnlyDictionary<string, string>? TryParseChannelStringTable(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            var separator = line.IndexOf('>');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            if (key.StartsWith('<'))
            {
                key = key[1..];
            }

            var value = line[(separator + 1)..].Trim();
            if (key.Length > 0 && value.Length > 0)
            {
                table[key] = value;
            }
        }

        return table.Count > 0 ? table : null;
    }

    private static bool TryResolveDisplayName(
        string[] tokens,
        IReadOnlyDictionary<string, string>? displayNames,
        out string displayName)
    {
        displayName = string.Empty;
        if (tokens.Length < 2 || displayNames is null)
        {
            return false;
        }

        var reference = tokens[1];
        if (reference.Length >= 2 && reference.StartsWith('`') && reference.EndsWith('`'))
        {
            reference = reference[1..^1];
        }

        if (reference.Length >= 2 && reference.StartsWith('<') && reference.EndsWith('>'))
        {
            reference = reference[1..^1];
        }

        var tablePrefix = reference.LastIndexOf("::", StringComparison.Ordinal);
        var key = tablePrefix >= 0 ? reference[(tablePrefix + 2)..] : reference;
        return key.Length > 0 && displayNames.TryGetValue(key, out displayName!);
    }

    private static string StripLineComment(string line)
    {
        var commentStart = line.IndexOf("//", StringComparison.Ordinal);
        return commentStart >= 0 ? line[..commentStart] : line;
    }

    private static bool TryFindChannelType(string[] tokens, out int type)
    {
        for (var index = 1; index < tokens.Length; index++)
        {
            if (tokens[index].StartsWith('`'))
            {
                continue;
            }

            if (TryParseInvariantInt32(tokens[index], out type))
            {
                return true;
            }
        }

        type = 0;
        return false;
    }

    private static string? FindChannelTag(string[] tokens)
    {
        foreach (var token in tokens)
        {
            if (token.Length < 3 || !token.StartsWith('`') || !token.EndsWith('`'))
            {
                continue;
            }

            var inner = token[1..^1];
            if (inner.StartsWith('['))
            {
                return inner;
            }
        }

        return null;
    }

    private static bool TryParseInvariantInt32(string token, out int value) =>
        int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
}
