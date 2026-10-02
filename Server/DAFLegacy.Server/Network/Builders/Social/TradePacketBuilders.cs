using System.Buffers.Binary;
using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

/// <summary>
/// 交易下行构建器（11 号 P3；布局依据 PENDING P-35 的 60A1 case 体取证）。
/// 字段语义为候选映射（结构由读序证实），值域待真机对拍（P-36）。
/// </summary>
internal static class TradePacketBuilders
{
    /// <summary>NOTI 15（槽位更新五元组）：{u16, u16, u32, u8, u16}——候选：对端 uid、槽位、itemId、数量、尾字段。</summary>
    public static GameServerPacket CreateTradeItemUpdate(
        ushort peerUserId,
        ushort slot,
        uint itemId,
        byte count,
        ushort tail)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(peerUserId);
        writer.Write(slot);
        writer.Write(itemId);
        writer.Write(count);
        writer.Write(tail);
        return TradeNotification(NotiPacketType.CHANGE_ITEMTRADE_ITEM, payload);
    }

    /// <summary>NOTI 16（报价清单）：{u16 count; count × {u16 slot, u8 count, u16 itemId}}——候选：对端交易格全量。</summary>
    public static GameServerPacket CreateTradeOfferList(
        ushort peerUserId,
        IReadOnlyList<TradeOfferEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(entries));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(peerUserId);
        writer.Write(checked((ushort)entries.Count));
        foreach (var entry in entries)
        {
            writer.Write(entry.Slot);
            writer.Write(entry.Count);
            writer.Write(entry.ItemId);
        }

        return TradeNotification(NotiPacketType.CANCEL_ITEMTRADE, payload);
    }

    /// <summary>NOTI 17（确认状态）：{u16 对端 uid, u8 状态}。</summary>
    public static GameServerPacket CreateTradeState(ushort peerUserId, byte state)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(peerUserId);
        writer.Write(state);
        return TradeNotification(NotiPacketType.STATE_ITEMTRADE, payload);
    }

    /// <summary>NOTI 18（完成）：{u16 ×3}——三字段落盘后客户端拆除交易态。</summary>
    public static GameServerPacket CreateTradeFinish(ushort fieldA, ushort fieldB, ushort fieldC)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(fieldA);
        writer.Write(fieldB);
        writer.Write(fieldC);
        return TradeNotification(NotiPacketType.FINISH_ITEMTRADE, payload);
    }

    private static GameServerPacket TradeNotification(NotiPacketType type, MemoryStream payload) =>
        new(
            GameProtocolConstants.NotificationPacketType,
            (byte)type,
            payload.ToArray());
}

/// <summary>NOTI 16 的单条报价条目（候选：交易格槽位/数量/物品）。</summary>
public sealed record TradeOfferEntry(ushort Slot, byte Count, ushort ItemId);
