using System.Globalization;

namespace DFLegacy.Mcp;

/// <summary>
/// packet_watch / packet_wait 共用的过滤条件（docs/design/09-mcp-packet-tap.md
/// §6.2）。全部条件 AND；数组内 OR；缺省 = 不过滤。
/// </summary>
public sealed class PacketTapFilter
{
    /// <summary>包类型："noti"（含 entrance 协议帧）、"cmd"、"udp"。</summary>
    public string[]? Kinds { get; set; }

    /// <summary>服务名包含匹配（大小写不敏感），如 "channel" 命中 "channel:7001"。</summary>
    public string[]? Services { get; set; }

    /// <summary>方向前缀匹配，如 "RX" 命中 "RX-UDP" 系列。</summary>
    public string[]? Directions { get; set; }

    /// <summary>精确包号。</summary>
    public int? ProtocolId { get; set; }

    /// <summary>报文名包含匹配（大小写不敏感）。</summary>
    public string? NameContains { get; set; }

    /// <summary>精确会话 Id。</summary>
    public string? SessionId { get; set; }

    /// <summary>body 最小长度。</summary>
    public int? MinBodyLength { get; set; }

    /// <summary>编译为热路径谓词；在 hub 入订阅通道之前执行。</summary>
    public Func<PacketTapEvent, bool> Compile()
    {
        var kinds = ParseKinds(Kinds);
        var services = NormalizeTerms(Services);
        var directions = NormalizeTerms(Directions);
        var protocolId = ProtocolId is >= 0 and <= byte.MaxValue ? (byte?)ProtocolId : null;
        var nameContains = string.IsNullOrWhiteSpace(NameContains) ? null : NameContains.Trim();
        var sessionId = Guid.TryParse(SessionId, out var parsed) ? parsed : (Guid?)null;
        var minBodyLength = MinBodyLength;

        if (kinds is null
            && services is null
            && directions is null
            && protocolId is null
            && nameContains is null
            && sessionId is null
            && minBodyLength is null)
        {
            return _ => true;
        }

        return @event =>
        {
            if (kinds is not null && !kinds.Contains(@event.Type))
            {
                return false;
            }

            if (services is not null
                && !services.Any(service => @event.Service.Contains(service, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (directions is not null
                && !directions.Any(direction => @event.Direction.StartsWith(direction, StringComparison.Ordinal)))
            {
                return false;
            }

            if (protocolId is not null && @event.ProtocolId != protocolId)
            {
                return false;
            }

            if (nameContains is not null
                && (@event.PacketName is null
                    || !@event.PacketName.Contains(nameContains, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (sessionId is not null && @event.SessionId != sessionId)
            {
                return false;
            }

            if (minBodyLength is not null && @event.Body.Length < minBodyLength)
            {
                return false;
            }

            return true;
        };
    }

    private static byte[]? ParseKinds(string[]? kinds)
    {
        if (kinds is null || kinds.Length == 0)
        {
            return null;
        }

        var parsed = new List<byte>(kinds.Length);
        foreach (var kind in kinds)
        {
            switch (kind?.Trim().ToLowerInvariant())
            {
                case "noti":
                    parsed.Add(0);
                    break;
                case "cmd":
                    parsed.Add(1);
                    break;
                case "udp":
                    parsed.Add(2);
                    break;
            }
        }

        return parsed.Count == 0 ? null : [.. parsed];
    }

    private static string[]? NormalizeTerms(string[]? terms)
    {
        if (terms is null)
        {
            return null;
        }

        var normalized = terms
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Select(term => term.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return normalized.Length == 0 ? null : normalized;
    }
}
