using System.Diagnostics.CodeAnalysis;
using DFLegacy.Server;

namespace DFLegacy.Protocol;

/// <summary>
/// Packet enum names recovered from the DNF.exe 60A1 client binary
/// (name tables at .rdata 0xB54170 NOTI / 0xB54430 CMD; see
/// docs/protocol/client-packet-enums-60cn.md). Regenerate with
/// tools/gen_packet_names.py after re-extracting from a new client build.
/// </summary>
public static class PacketNames
{
    public static bool TryGet(byte type, byte protocolId, [NotNullWhen(true)] out string? name)
    {
        // 名称反查单源到 PacketTypes 枚举（07 §4.8.3 规则 5）：枚举成员名
        // 即客户端 ENUM_* 名的去前缀形态，此处的字符串形态与旧表逐项一致。
        var (enumType, prefix) = type == GameProtocolEngine.NotificationPacketType
            ? (typeof(NotiPacketType), "ENUM_NOTIPACKET_")
            : type == GameProtocolEngine.CommandPacketType
                ? (typeof(CmdPacketType), "ENUM_CMDPACKET_")
                : (null, null);
        if (enumType is null
            || !Enum.IsDefined(enumType, (ushort)protocolId))
        {
            name = null;
            return false;
        }

        name = prefix + Enum.GetName(enumType, (ushort)protocolId);
        return true;
    }
}
