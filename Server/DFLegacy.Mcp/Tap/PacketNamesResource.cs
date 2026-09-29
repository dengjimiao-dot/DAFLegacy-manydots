using System.ComponentModel;
using System.Text.Json;
using DFLegacy.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DFLegacy.Mcp;

/// <summary>
/// G1 Resources（docs/design/09-mcp-packet-tap.md §6.4）：整张报文名注册表
/// JSON（源自 60A1 客户端逆向，见 PacketNames.cs 头注释）。经
/// PacketNames.TryGet 枚举两张表，Server 侧零改动。
/// </summary>
[McpServerResourceType]
public sealed class PacketNamesResources
{
    private static string BuildTable(byte type)
    {
        var entries = new List<object>();
        // 循环变量用 int：byte 到 255 时自增会回绕，`<= byte.MaxValue` 变死循环。
        for (var protocolId = 0; protocolId <= byte.MaxValue; protocolId++)
        {
            if (PacketNames.TryGet(type, (byte)protocolId, out var name) && name is not null)
            {
                entries.Add(new { protocolId, name });
            }
        }

        return JsonSerializer.Serialize(new
        {
            type = (int)type,
            kind = type == 0 ? "noti" : "cmd",
            count = entries.Count,
            entries,
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    [McpServerResource(UriTemplate = "packet-names://noti", Name = "packet-names-noti", MimeType = "application/json"),
     Description("NOTI 包报文名注册表全文（protocolId → 60CN 客户端枚举名）。")]
    public ValueTask<ResourceContents> NotiNames()
        => ValueTask.FromResult<ResourceContents>(new TextResourceContents
        {
            Uri = "packet-names://noti",
            MimeType = "application/json",
            Text = BuildTable(0),
        });

    [McpServerResource(UriTemplate = "packet-names://cmd", Name = "packet-names-cmd", MimeType = "application/json"),
     Description("CMD 包报文名注册表全文（protocolId → 60CN 客户端枚举名）。")]
    public ValueTask<ResourceContents> CmdNames()
        => ValueTask.FromResult<ResourceContents>(new TextResourceContents
        {
            Uri = "packet-names://cmd",
            MimeType = "application/json",
            Text = BuildTable(1),
        });
}
