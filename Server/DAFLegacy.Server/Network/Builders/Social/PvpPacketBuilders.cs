using System.Buffers.Binary;
using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

/// <summary>
/// PVP 下行构建器（11 号 P6；仅收录读序完全证实的三包：44/46/45 头部骨架。
/// NOTI 41/43/47 的复合条目布局留实施期）。
/// </summary>
internal static class PvpPacketBuilders
{
    /// <summary>NOTI 44（准备状态广播）：{u8 座位, u8 准备态}（case 0x423AD9 全量）。</summary>
    public static GameServerPacket CreatePvpReady(byte seatIndex, bool ready) =>
        Simple((byte)NotiPacketType.PVP_READY_STATE, seatIndex, ready ? (byte)1 : (byte)0);

    /// <summary>NOTI 46（击杀广播）：{u8 阵亡座位, u8 击杀者座位}（case 0x4246E2 全量）。</summary>
    public static GameServerPacket CreatePvpKill(byte victimSeatIndex, byte killerSeatIndex) =>
        Simple((byte)NotiPacketType.DIE_PVP_CHARACTER, victimSeatIndex, killerSeatIndex);

    /// <summary>NOTI 45（开战）头部：{u8 地图, u8 模式, …}——尾部字段留实施期补齐。</summary>
    public static GameServerPacket CreatePvpStartHead(byte mapIndex, byte mode) =>
        Simple((byte)NotiPacketType.START_PVP, mapIndex, mode);

    private static GameServerPacket Simple(byte protocolId, byte first, byte second) =>
        new(GameProtocolConstants.NotificationPacketType, protocolId, [first, second]);
}
