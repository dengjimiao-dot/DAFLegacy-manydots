using System.Buffers.Binary;
using System.Text;
using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

/// <summary>
/// 社交系统封包构建器（11 号 P2；布局依据 PENDING P-35 的 60A1 case 体取证）。
/// 名字字段一律客户端文本编码（GBK，与 ItemTradeStateHandler 同源）；dstr 写法
/// 遵守 sub_8B3250 的硬性要求：长度 1..cap−1（0 或 ≥cap 时客户端游标不前进，
/// 参照喇叭构建器的同类注释纪律）。NOTI 56 记录与应答条目的字段语义为候选映射，
/// 结构已由 case 体证实，值域待真机对拍（P-36）。
/// </summary>
internal static class SocialPacketBuilders
{
    private static readonly Encoding ClientEncoding = PvfEncodings.Cp936Lossy();

    /// <summary>dstr 名字上限（NOTI 54/55/56 与应答条目的 0x1F 容量）。</summary>
    private const int NameCapacity = 31;

    /// <summary>NOTI 56 的固定记录宽度（sub_8F5180 一次拷贝 0x2A 字节）。</summary>
    private const int BuddyRecordSize = 42;

    /// <summary>NOTI 56 的单条好友记录（语义候选映射，见类注释）。</summary>
    public sealed record FriendListEntry(
        ushort UserId,
        string Name,
        bool Online,
        byte Level,
        byte Job,
        byte Grow,
        uint AccountUid);

    private static byte[] EncodeName(string name)
    {
        var bytes = ClientEncoding.GetBytes(name);
        if (bytes.Length is < 1 or > NameCapacity - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(name));
        }

        return bytes;
    }

    /// <summary>NOTI 56：{u8 count; count × 42B 记录}，名字内联 @[3..]、uid u16@[36]、accId u32@[38..41]。</summary>
    public static GameServerPacket CreateBuddyConnList(IReadOnlyList<FriendListEntry> friends)
    {
        ArgumentNullException.ThrowIfNull(friends);
        if (friends.Count > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(friends));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)friends.Count);
        foreach (var friend in friends)
        {
            var name = EncodeName(friend.Name);
            var record = new byte[BuddyRecordSize];
            record[0] = friend.Online ? (byte)1 : (byte)0;
            record[1] = friend.Level;
            record[2] = friend.Job;
            Array.Copy(name, 0, record, 3, name.Length);
            record[35] = friend.Grow;
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(36, 2), friend.UserId);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(38, 4), friend.AccountUid);
            writer.Write(record);
        }

        return new GameServerPacket(
            GameProtocolConstants.NotificationPacketType,
            (byte)NotiPacketType.BUDDY_CONN_LIST,
            payload.ToArray());
    }

    /// <summary>NOTI 54/55：{u8 0, dstr 名字, u8 在线标志}——上线/下线提示（串表 0x298/0x29A）。</summary>
    public static GameServerPacket CreateBuddyIn(string name) =>
        CreateBuddyTransition(NotiPacketType.BUDDY_IN, name);

    public static GameServerPacket CreateBuddyOut(string name) =>
        CreateBuddyTransition(NotiPacketType.BUDDY_OUT, name);

    private static GameServerPacket CreateBuddyTransition(NotiPacketType type, string name)
    {
        var nameBytes = EncodeName(name);
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)0);
        writer.Write((uint)nameBytes.Length);
        writer.Write(nameBytes);
        writer.Write(type == NotiPacketType.BUDDY_IN ? (byte)1 : (byte)0);
        return new GameServerPacket(
            GameProtocolConstants.NotificationPacketType,
            (byte)type,
            payload.ToArray());
    }

    /// <summary>CMD 63 应答（成功）：{dstr 名字, u8×3, u32 accId, u8 在线, u8 0}——新好友完整条目。</summary>
    public static GameServerPacket CreateAddFriendReply(
        PacketFrame request,
        string name,
        byte level,
        byte job,
        byte grow,
        uint accountUid,
        bool online)
    {
        var nameBytes = EncodeName(name);
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write((uint)nameBytes.Length);
        writer.Write(nameBytes);
        writer.Write(level);
        writer.Write(job);
        writer.Write(grow);
        writer.Write(accountUid);
        writer.Write(online ? (byte)1 : (byte)0);
        writer.Write((byte)0);
        return new GameServerPacket(
            GameProtocolConstants.CommandPacketType,
            request.ProtocolId,
            payload.ToArray());
    }

    /// <summary>CMD 64 应答（成功）：{dstr 名字, u8 1}。</summary>
    public static GameServerPacket CreateRemoveFriendReply(PacketFrame request, string name)
    {
        var nameBytes = EncodeName(name);
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write((uint)nameBytes.Length);
        writer.Write(nameBytes);
        writer.Write((byte)1);
        return new GameServerPacket(
            GameProtocolConstants.CommandPacketType,
            request.ProtocolId,
            payload.ToArray());
    }

    /// <summary>CMD 128 应答（成功）：{u8 1}（单字节，语义候选=生效标志）。</summary>
    public static GameServerPacket CreateBlacklistAddReply(PacketFrame request) =>
        new(GameProtocolConstants.CommandPacketType, request.ProtocolId, [1, 1]);

    /// <summary>CMD 129 应答（成功）：{dstr 名字, u8 1}（与 CMD 64 同形）。</summary>
    public static GameServerPacket CreateBlacklistRemoveReply(PacketFrame request, string name)
    {
        var nameBytes = EncodeName(name);
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write((uint)nameBytes.Length);
        writer.Write(nameBytes);
        writer.Write((byte)1);
        return new GameServerPacket(
            GameProtocolConstants.CommandPacketType,
            request.ProtocolId,
            payload.ToArray());
    }

    public sealed record BlacklistEntry(
        string Name,
        byte Day,
        byte MonthMinusOne,
        byte YearMinus1900);

    /// <summary>CMD 130 应答：{u8 count; count × {dstr 名字, u8 日, u8 月−1, u8 年−1900}}（黑名单全量）。</summary>
    public static GameServerPacket CreateBlacklistListReply(
        PacketFrame request,
        IReadOnlyList<BlacklistEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(entries));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write((byte)entries.Count);
        foreach (var entry in entries)
        {
            var nameBytes = EncodeName(entry.Name);
            writer.Write((uint)nameBytes.Length);
            writer.Write(nameBytes);
            writer.Write(entry.Day);
            writer.Write(entry.MonthMinusOne);
            writer.Write(entry.YearMinus1900);
        }

        return new GameServerPacket(
            GameProtocolConstants.CommandPacketType,
            request.ProtocolId,
            payload.ToArray());
    }
}

/// <summary>NOTI 78 的单条摊位记录（候选映射：uid/在场标/区号，布局 {u16,u8,u8,u16,u16}）。</summary>
public sealed record PrivateStoreBroadcastEntry(
    ushort UserId,
    byte TownId,
    byte AreaId,
    ushort CharacterNoTail,
    ushort Tail);

public static class PrivateStorePacketBuilders
{
    /// <summary>NOTI 78（建摊区域广播）：{u16, u8, u8, u16, u16}（sub_4268E1 读序）。</summary>
    public static GameServerPacket CreatePrivateStoreCreated(
        PrivateStoreBroadcastEntry entry)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(entry.UserId);
        writer.Write(entry.TownId);
        writer.Write(entry.AreaId);
        writer.Write(entry.CharacterNoTail);
        writer.Write(entry.Tail);
        return new GameServerPacket(
            GameProtocolConstants.NotificationPacketType,
            (byte)NotiPacketType.CREATE_PRIVATE_STORE,
            payload.ToArray());
    }

    /// <summary>CMD 86 应答：{u8 成功, u16 店主 uid, u8 条目数, 条目 × {u16, u32, u16, u32, u8, u16}}
    /// （case 0x41669A 读序；条目字段候选 = 槽位/itemUid/数量尾/价格/数量/尾标）。</summary>
    public static GameServerPacket CreateEnterStoreReply(
        PacketFrame request,
        bool success,
        ushort ownerUserId,
        IReadOnlyList<PrivateStoreListEntry> entries)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(success ? (byte)1 : (byte)0);
        if (!success)
        {
            return new GameServerPacket(
                GameProtocolConstants.CommandPacketType,
                request.ProtocolId,
                payload.ToArray());
        }

        writer.Write(ownerUserId);
        writer.Write((byte)entries.Count);
        foreach (var entry in entries)
        {
            writer.Write(entry.Slot);
            writer.Write(entry.ItemUid);
            writer.Write(entry.TailA);
            writer.Write(entry.PriceGold);
            writer.Write(entry.Count);
            writer.Write(entry.TailB);
        }

        return new GameServerPacket(
            GameProtocolConstants.CommandPacketType,
            request.ProtocolId,
            payload.ToArray());
    }

    /// <summary>CMD 88/89/90 的 {u8 结果} 形应答。</summary>
    public static GameServerPacket CreateStoreByteReply(PacketFrame request, bool success) =>
        new(GameProtocolConstants.CommandPacketType, request.ProtocolId, [success ? (byte)1 : (byte)0]);

    public sealed record PrivateStoreListEntry(
        ushort Slot,
        uint ItemUid,
        ushort TailA,
        uint PriceGold,
        byte Count,
        ushort TailB);
}
