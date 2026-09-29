using System.Text;
using DFLegacy.Protocol;
using DFLegacy.Server;

internal static class MegaphoneSmokeTests
{
    public static void Run(Action<bool, string> check)
    {
        // 实测抓包：服务器喇叭（物品 36，主背包槽 8）通过 CMD 17 type=13
        // 发送 "hello"，而非 USE_STACKABLE。见
        // docs/protocol/new-packet-formats-60cn.md §1.1。
        byte[] capturedBody =
        [
            0x0E, 0x00, // seq
            0x0D, // message type 13 = server megaphone
            0x00, 0x00, // target/item-space
            0x08, 0x00, 0x00, 0x00, // speaker slot u16 + reserved u16
            0x05, 0x00, 0x00, 0x00, // text length
            0x68, 0x65, 0x6C, 0x6C, 0x6F, // "hello"
        ];

        check(EntranceService.TryReadSendMessage(capturedBody, out var request),
            "captured megaphone CMD 17 body parses");
        check(request.MessageType == 13, "megaphone request keeps speaker type 13");
        check(request.SlotOrReserved == 8, "megaphone request exposes speaker slot 8");
        check(Encoding.ASCII.GetString(request.MessageBytes) == "hello",
            "megaphone request keeps the typed text");

        // NOTI 130 载荷：type u8 + flag u8 + target u16 + dstr 名字 + dstr 消息
        // （客户端 case 130 @0x427414；dstr = u32 长度 + 内容，名字 cap 0x1E、
        // 消息 cap 0x200，越界会导致客户端游标错位）。
        var clientEncoding = PvfEncodings.Cp936Lossy();
        var name = clientEncoding.GetBytes("频道");
        check(name.Length == 4, "CP936 encodes the two-character speaker name as 4 bytes");
        var packet = GameProtocolEngine.CreateMegaphoneNotification(
            (GameMessageType)14,
            ownerFlag: 0x42,
            targetAreaUserId: 0x1042,
            senderName: name,
            messageBytes: "hello"u8.ToArray());
        check(packet.Type == 0 && packet.ProtocolId == 130,
            "megaphone notification frames NOTI 130");
        byte[] expectedPayload =
        [
            0x0E, // type 14 (channel-family speaker)
            0x42, // owner flag
            0x42, 0x10, // target 0x1042
            0x04, 0x00, 0x00, 0x00, ..name,
            0x05, 0x00, 0x00, 0x00, 0x68, 0x65, 0x6C, 0x6C, 0x6F,
        ];
        check(packet.Payload.AsSpan().SequenceEqual(expectedPayload),
            "megaphone payload matches the client read order");

        check(packet.TotalLength == 6 + expectedPayload.Length,
            "megaphone frame length equals 6 + payload");

        // 越界名字/空消息必须抛出：客户端对这些长度不推进游标，会错位。
        var nameTooLong = new byte[0x1E];
        var threw = false;
        try
        {
            GameProtocolEngine.CreateMegaphoneNotification(
                (GameMessageType)13,
                0,
                0,
                nameTooLong,
                "hello"u8.ToArray());
        }
        catch (ArgumentOutOfRangeException)
        {
            threw = true;
        }

        check(threw, "megaphone builder rejects names at the 0x1E capacity");

        threw = false;
        try
        {
            GameProtocolEngine.CreateMegaphoneNotification(
                (GameMessageType)13,
                0,
                0,
                name,
                []);
        }
        catch (ArgumentOutOfRangeException)
        {
            threw = true;
        }

        check(threw, "megaphone builder rejects empty messages");
    }
}
