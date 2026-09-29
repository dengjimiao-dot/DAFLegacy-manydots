using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using DFLegacy.Protocol;
using DFLegacy.Server;
using ModelContextProtocol.Server;

namespace DFLegacy.Mcp;

/// <summary>
/// G1 · 协议包实时监听工具组（docs/design/09-mcp-packet-tap.md §6）。全部只读；
/// 返回 JSON 文本。hex 与 crc 等展示格式在这里格式化，ring 里只存字节。
/// </summary>
[McpServerToolType]
public sealed class PacketTapTools(PacketTapHub hub, RuntimeState runtime)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string ToJson(object payload) => JsonSerializer.Serialize(payload, JsonOptions);

    private static object EventJson(PacketTapEvent @event) => new
    {
        seq = @event.Seq,
        timestamp = @event.Timestamp,
        sessionId = @event.SessionId,
        service = @event.Service,
        direction = @event.Direction,
        kind = KindName(@event.Type),
        type = @event.Type,
        protocolId = @event.ProtocolId,
        packetName = @event.PacketName,
        totalLength = @event.TotalLength,
        crc32 = $"0x{@event.Crc32:X8}",
        crcValid = @event.CrcValid,
        bodyLength = @event.Body.Length,
        bodyHex = Convert.ToHexString(@event.Body),
        bodyTruncated = @event.BodyTruncated,
        wireHex = @event.Wire is null ? null : Convert.ToHexString(@event.Wire),
        wireTruncated = @event.WireTruncated,
        bodyLengthOnWire = @event.Wire?.Length,
        plainText = @event.PlainText,
    };

    private static string KindName(byte type) => type switch
    {
        0 => "noti",
        1 => "cmd",
        2 => "udp",
        _ => $"type{type}",
    };

    [McpServerTool(Name = "packet_watch", ReadOnly = true, Idempotent = true),
     Description("实时跟踪协议收发包（tail -f 语义）。循环调用：把上次返回的 nextSeq 作为 afterSeq 传回即可续读（nextSeq=下一个未读 seq，空调用时不变）；无匹配新包时挂起至多 waitMs 后返回空 events。")]
    public async Task<string> PacketWatch(
        [Description("续读游标（下一个未读 seq）：-1=从留存最早的事件开始回放；0=不回放、只看本次调用之后的新包；正数=返回 seq 从该值起的未读事件")]
        long afterSeq = -1,
        [Description("单次返回条数上限，1-200，默认 50")] int limit = 50,
        [Description("无匹配新包时的最长等待毫秒数，0=立即返回，默认 10000")] int waitMs = 10000,
        [Description("过滤条件对象 {kinds,services,directions,protocolId,nameContains,sessionId,minBodyLength}，全部 AND、数组内 OR、缺省不过滤")]
        PacketTapFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        var result = await hub.WatchAsync(afterSeq, limit, waitMs, filter, cancellationToken);
        return ToJson(new
        {
            events = result.Events.Select(EventJson),
            nextSeq = result.NextSeq,
            ringOldestSeq = result.RingOldestSeq,
            cursorLapsed = result.CursorLapsed,
            subscriberDropped = result.SubscriberDropped,
        });
    }

    [McpServerTool(Name = "packet_get", ReadOnly = true),
     Description("按 seq 取单个留存事件的完整详情（含完整 bodyHex 与 RX 密文 wireHex），供逆向拷字节。事件已被环形存档淘汰时 found=false。")]
    public string PacketGet([Description("要取的事件序号（packet_watch 返回的 seq）")] long seq)
    {
        var @event = hub.GetBySeq(seq);
        if (@event is null)
        {
            return ToJson(new
            {
                found = false,
                seq,
                ringOldestSeq = hub.OldestSeq,
                currentSeq = hub.CurrentSeq,
            });
        }

        return ToJson(new { found = true, @event = EventJson(@event) });
    }

    [McpServerTool(Name = "packet_wait", ReadOnly = true),
     Description("等待第一条匹配的事件（\"点了强化等回包\"式往返验证）：从本次调用时刻起挂起，匹配到即返回该事件，超时返回 {timedOut:true}。")]
    public async Task<string> PacketWait(
        [Description("过滤条件对象，见 packet_watch")] PacketTapFilter? filter = null,
        [Description("最长等待毫秒数，默认 15000")] int timeoutMs = 15000,
        CancellationToken cancellationToken = default)
    {
        var result = await hub.WatchAsync(afterSeq: 0, limit: 1, waitMs: timeoutMs, filter, cancellationToken);
        if (result.Events.Count == 0)
        {
            return ToJson(new { timedOut = true, timeoutMs });
        }

        return ToJson(new
        {
            timedOut = false,
            @event = EventJson(result.Events[0]),
        });
    }

    [McpServerTool(Name = "session_list", ReadOnly = true),
     Description("列出当前全部网络会话（登录入口/频道 TCP 与 UDP），用于找到要盯的 sessionId。")]
    public string SessionList()
    {
        return ToJson(new
        {
            sessions = runtime.Sessions().Select(session => new
            {
                id = session.Id,
                service = session.Service,
                remoteEndpoint = session.RemoteEndpoint,
                connectedAt = session.ConnectedAt,
                receivedPackets = session.ReceivedPackets,
                sentPackets = session.SentPackets,
                lastProtocolId = session.LastProtocolId,
            }),
        });
    }

    [McpServerTool(Name = "tap_status", ReadOnly = true),
     Description("抓包通道状态：当前 seq、环形存档占用/容量、订阅数。用于确认 packet_watch 通道活着。")]
    public string TapStatus()
    {
        return ToJson(new
        {
            enabled = true,
            currentSeq = hub.CurrentSeq,
            ringOldestSeq = hub.OldestSeq,
            ringCount = hub.RingCount,
            ringCapacity = PacketTapHub.RingCapacity,
            subscribers = hub.SubscriberCount,
            subscriberChannelCapacity = PacketTapHub.SubscriberChannelCapacity,
            maxBodyBytes = PacketTapHub.MaxBodyBytes,
            maxWireBytes = PacketTapHub.MaxWireBytes,
        });
    }

    [McpServerTool(Name = "packet_names_search", ReadOnly = true),
     Description("在报文名注册表（60CN 客户端逆向）中按名字或包号检索，先查\"包号↔名字\"再配 watch 过滤。")]
    public string PacketNamesSearch(
        [Description("检索词：报文名片段（大小写不敏感，如 ITEM）或包号数字")] string query,
        [Description("返回条数上限，1-100，默认 20")] int limit = 20)
    {
        limit = Math.Clamp(limit, 1, 100);
        var normalized = query?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            return ToJson(new { matches = Array.Empty<object>() });
        }

        var numeric = long.TryParse(normalized, out var parsed)
            && parsed is >= 0 and <= byte.MaxValue
            ? (byte?)parsed
            : null;
        var matches = new List<object>();
        for (byte type = 0; type <= 1; type++)
        {
            // 循环变量用 int：byte 到 255 时自增会回绕，`<= byte.MaxValue` 变死循环。
            for (var protocolId = 0; protocolId <= byte.MaxValue; protocolId++)
            {
                if (!PacketNames.TryGet(type, (byte)protocolId, out var name) || name is null)
                {
                    continue;
                }

                var byNumber = numeric is not null && protocolId == numeric;
                if (!byNumber && !name.Contains(normalized, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                matches.Add(new
                {
                    type = (int)type,
                    kind = KindName(type),
                    protocolId,
                    name,
                });
                if (matches.Count >= limit)
                {
                    return ToJson(new { query = normalized, matches });
                }
            }
        }

        return ToJson(new { query = normalized, matches });
    }

    [McpServerTool(Name = "protocol_decode", ReadOnly = true),
     Description("离线复用服务端字段级解码器：对提交的 bodyHex 做明文解码，返回解码文本或 null（未知包号）。")]
    public string ProtocolDecode(
        [Description("帧类型：0=NOTI（走服务端解码器）、1=CMD（走客户端指令解码器）")] byte type,
        [Description("包号")] byte protocolId,
        [Description("body 的十六进制字符串（可含空格）")] string bodyHex,
        [Description("true=按服务端下行解码器（DecodeServer）解读，默认按客户端上行解读")] bool fromServer = false)
    {
        byte[] body;
        try
        {
            body = Convert.FromHexString(bodyHex.Replace(" ", "").Replace("\n", "").Replace("\r", ""));
        }
        catch (FormatException exception)
        {
            return ToJson(new { decoded = (string?)null, error = $"bodyHex is not valid hex: {exception.Message}" });
        }

        string? decoded = null;
        string? error = null;
        try
        {
            decoded = fromServer
                ? PacketPlainText.DecodeServer(type, protocolId, body)
                : PacketPlainText.Decode(type, protocolId, body);
            if (decoded is null)
            {
                error = "No decoder for this type/protocolId; body returned as null.";
            }
        }
        catch (Exception exception)
        {
            error = $"Decode failed: {exception.Message}";
        }

        return ToJson(new
        {
            decoded,
            bodyLength = body.Length,
            bodyHex = Convert.ToHexString(body),
            error,
        });
    }
}
