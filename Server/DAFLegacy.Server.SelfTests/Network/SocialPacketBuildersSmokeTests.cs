using System.Buffers.Binary;
using DAFLegacy.Protocol;
using DAFLegacy.Server;

/// <summary>
/// 社交封包构建器自测（11 号 P2；布局依据 PENDING P-35）：NOTI 56 记录偏移、
/// 54/55 形状、五个应答的封套与载荷形状。字段值域为候选映射，真机窗口（P-36）对拍。
/// </summary>
internal static class SocialPacketBuildersSmokeTests
{
    public static void Run(Action<bool, string> check)
    {
        var request = new PacketFrame(1, 63, DeclaredCrc32: 0, Body: [0, 0]);

        // NOTI 56：count + 42B 记录；名字内联 @[3..]、在线 @[0]、uid @[36]、accId @[38..41]。
        var list = SocialPacketBuilders.CreateBuddyConnList(
        [
            new SocialPacketBuilders.FriendListEntry(UserId: 77, Name: "Alice", Online: true, Level: 24, Job: 1, Grow: 2, AccountUid: 0x11223344),
        ]);
        check(list.ProtocolId == (byte)NotiPacketType.BUDDY_CONN_LIST, "NOTI 56 keeps the buddy conn list protocol id");
        check(list.Payload[0] == 1 && list.Payload.Length == 1 + 42, "one 42-byte record follows the count byte");
        check(list.Payload[1] == 1 && list.Payload[2] == 24 && list.Payload[3] == 1,
            "record carries online flag, level and job at offsets 0..2");
        check(list.Payload[4] == (byte)'A' && list.Payload[8] == (byte)'e' && list.Payload[9] == 0,
            "the name is written inline from offset 3 and NUL-padded");
        check(list.Payload[36] == 2, "grow lands at record offset 35");
        check(BinaryPrimitives.ReadUInt16LittleEndian(list.Payload.AsSpan(37, 2)) == 77,
            "the area user id lands little-endian at offset 36");
        check(BinaryPrimitives.ReadUInt32LittleEndian(list.Payload.AsSpan(39, 4)) == 0x11223344,
            "the account uid lands little-endian at offset 38");
        check(list.Payload[42] == 0x11, "record offset 41 carries the account uid high byte");

        // 名字编码上界：GBK 超长名字必须被拒绝（dstr 1..30 规避游标坑）。
        var thrown = false;
        try
        {
            SocialPacketBuilders.CreateBuddyConnList(
                [new SocialPacketBuilders.FriendListEntry(1, new string('甲', 31), true, 1, 1, 0, 1)]);
        }
        catch (ArgumentOutOfRangeException)
        {
            thrown = true;
        }

        check(thrown, "names beyond the dstr capacity are rejected");

        // NOTI 54/55：{0, dstr 名字, 在线标志}。
        var buddyIn = SocialPacketBuilders.CreateBuddyIn("Bob");
        check(buddyIn.ProtocolId == (byte)NotiPacketType.BUDDY_IN
            && buddyIn.Payload[0] == 0
            && BinaryPrimitives.ReadUInt32LittleEndian(buddyIn.Payload.AsSpan(1, 4)) == 3
            && buddyIn.Payload[5] == (byte)'B'
            && buddyIn.Payload[8] == 1,
            "BUDDY_IN carries the name with the online tail flag");
        var buddyOut = SocialPacketBuilders.CreateBuddyOut("Bob");
        check(buddyOut.ProtocolId == (byte)NotiPacketType.BUDDY_OUT
            && buddyOut.Payload[8] == 0,
            "BUDDY_OUT flips the tail flag to offline");

        // CMD 63 应答：成功 + 完整条目。
        var addReply = SocialPacketBuilders.CreateAddFriendReply(request, "Cara", 30, 3, 1, 42, online: true);
        check(addReply.Type == GameProtocolConstants.CommandPacketType && addReply.ProtocolId == 63,
            "the add-friend reply mirrors the command envelope");
        check(addReply.Payload[0] == 1
            && BinaryPrimitives.ReadUInt32LittleEndian(addReply.Payload.AsSpan(1, 4)) == 4
            && addReply.Payload[5] == (byte)'C'
            && addReply.Payload[9] == 30 && addReply.Payload[10] == 3 && addReply.Payload[11] == 1
            && BinaryPrimitives.ReadUInt32LittleEndian(addReply.Payload.AsSpan(12, 4)) == 42
            && addReply.Payload[16] == 1 && addReply.Payload[17] == 0,
            "the add-friend reply carries the full friend entry");

        // CMD 64 / 129 应答：{1, dstr 名字, 1}。
        var removeReply = SocialPacketBuilders.CreateRemoveFriendReply(request, "Dan");
        check(removeReply.Payload[0] == 1 && removeReply.Payload[^1] == 1
            && BinaryPrimitives.ReadUInt32LittleEndian(removeReply.Payload.AsSpan(1, 4)) == 3,
            "the remove-friend reply echoes the removed name");
        var blacklistRemove = SocialPacketBuilders.CreateBlacklistRemoveReply(
            new PacketFrame(1, 129, DeclaredCrc32: 0, Body: [0, 0]), "Eve");
        check(blacklistRemove.ProtocolId == 129 && blacklistRemove.Payload[0] == 1,
            "the blacklist-remove reply mirrors the remove-friend shape");

        // CMD 128 应答：{1, 1}。
        var blacklistAdd = SocialPacketBuilders.CreateBlacklistAddReply(
            new PacketFrame(1, 128, DeclaredCrc32: 0, Body: [0, 0]));
        check(blacklistAdd.ProtocolId == 128
            && blacklistAdd.Payload.SequenceEqual(new byte[] { 1, 1 }),
            "the blacklist-add reply is the success byte plus the active flag");

        // CMD 130 应答：{1, count, ×{dstr 名字, 日, 月−1, 年−1900}}。
        var blacklistList = SocialPacketBuilders.CreateBlacklistListReply(
            new PacketFrame(1, 130, DeclaredCrc32: 0, Body: [0, 0]),
            [new SocialPacketBuilders.BlacklistEntry("Finn", Day: 7, MonthMinusOne: 9, YearMinus1900: 126)]);
        check(blacklistList.ProtocolId == 130
            && blacklistList.Payload[0] == 1
            && blacklistList.Payload[1] == 1
            && BinaryPrimitives.ReadUInt32LittleEndian(blacklistList.Payload.AsSpan(2, 4)) == 4
            && blacklistList.Payload[6] == (byte)'F'
            && blacklistList.Payload[10] == 7 && blacklistList.Payload[11] == 9 && blacklistList.Payload[12] == 126,
            "the blacklist list reply carries tm-style join dates");
        var emptyBlacklist = SocialPacketBuilders.CreateBlacklistListReply(
            new PacketFrame(1, 130, DeclaredCrc32: 0, Body: [0, 0]),
            []);
        check(emptyBlacklist.Payload.SequenceEqual(new byte[] { 1, 0 }),
            "an empty blacklist replies with a zero count");
    }
}
