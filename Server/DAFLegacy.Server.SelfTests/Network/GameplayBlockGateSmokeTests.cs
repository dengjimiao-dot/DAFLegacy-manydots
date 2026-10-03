using System.Buffers.Binary;
using DAFLegacy.Protocol;
using DAFLegacy.Server;

/// <summary>
/// 转发门块序列解析自测（docs/design/10 §4.5）：合法帧/多块序列通过、块越界、
/// 声明长度超包、槽位越界、尾部垃圾与空包的判定。输入直接由协议核心构建器
/// 产出，保证门与组帧使用同一块布局。
/// </summary>
internal static class GameplayBlockGateSmokeTests
{
    public static void Run(Action<bool, string> check)
    {
        // 合法不可靠帧：整包通过，帧物化一次，字段保真。
        var application = GameDatagramProtocol.CreateApplication(
            mode: 1,
            protocolId: 12,
            plaintextPayload: [1, 2, 3, 4]);
        var frame = GameDatagramProtocol.CreateUnreliableFrame(
            sequence: 7,
            senderPartyIndex: 2,
            flag: 0,
            application);
        var frames = new List<GameMtUdpFrame>();
        check(GameplayDatagramService.TryReadBlockSequence(frame, frames),
            "a well-formed unreliable frame passes the gate");
        check(frames.Count == 1
                && frames[0].BlockType == GameDatagramProtocol.UnreliableBlockType
                && frames[0].Sequence == 7
                && frames[0].SenderPartyIndex == 2
                && frames[0].Application.AsSpan().SequenceEqual(application),
            "the passing frame materializes once with its fields intact");

        // 多块序列：可靠帧与不可靠帧串联，全部物化。
        var reliable = new byte[GameDatagramProtocol.MtUdpHeaderLength + application.Length];
        reliable[0] = GameDatagramProtocol.ReliableBlockType;
        BinaryPrimitives.WriteUInt32LittleEndian(reliable.AsSpan(1, 4), 9);
        BinaryPrimitives.WriteUInt16LittleEndian(
            reliable.AsSpan(5, 2),
            (ushort)application.Length);
        reliable[7] = 1;
        reliable[8] = 0;
        application.CopyTo(reliable, GameDatagramProtocol.MtUdpHeaderLength);
        var combined = new byte[frame.Length + reliable.Length];
        frame.CopyTo(combined, 0);
        reliable.CopyTo(combined, frame.Length);
        frames.Clear();
        check(GameplayDatagramService.TryReadBlockSequence(combined, frames),
            "a multi-block datagram passes the gate");
        check(frames.Count == 2
                && frames[0].BlockType == GameDatagramProtocol.UnreliableBlockType
                && frames[1].BlockType == GameDatagramProtocol.ReliableBlockType,
            "both blocks of a multi-block datagram materialize in order");

        // 声明应用长度超过包长：整包拒绝（越界读的最后一段）。
        var truncated = new byte[GameDatagramProtocol.MtUdpHeaderLength + 2];
        truncated[0] = GameDatagramProtocol.UnreliableBlockType;
        truncated[5] = 0xFF;
        truncated[6] = 0xFF;
        frames.Clear();
        check(!GameplayDatagramService.TryReadBlockSequence(truncated, frames),
            "a declared application length past the datagram end fails the gate");
        check(frames.Count == 0, "a failed gate materializes no frames");

        // 槽位越界（>=8）：客户端接收端同样整包丢弃，门判定一致。
        var badSlot = (byte[])frame.Clone();
        badSlot[7] = 8;
        check(!GameplayDatagramService.TryReadBlockSequence(badSlot, []),
            "an out-of-range sender slot fails the gate");

        // 合法块之后跟尾部垃圾（截断的块头）：整包拒绝（客户端解析游标同样会停滞）。
        var trailingGarbage = new byte[frame.Length + 3];
        frame.CopyTo(trailingGarbage, 0);
        trailingGarbage[frame.Length] = GameDatagramProtocol.UnreliableBlockType;
        check(!GameplayDatagramService.TryReadBlockSequence(trailingGarbage, []),
            "a well-formed block followed by garbage fails the gate");

        // 未知块类型：拒绝（门只放行类型 0/1/2，与客户端分派面一致）。
        var unknownType = (byte[])frame.Clone();
        unknownType[0] = 3;
        check(!GameplayDatagramService.TryReadBlockSequence(unknownType, []),
            "an unknown block type fails the gate");

        // 空数据报：合格且无物化（与现行转发行为一致）。
        frames.Clear();
        check(GameplayDatagramService.TryReadBlockSequence([], frames)
                && frames.Count == 0,
            "an empty datagram passes with no frames");

        // 保活形态（首字节 0、不足 8 字节）不构成绕过：在玩法数据报路径上拒绝。
        check(!GameDatagramProtocol.IsNatKeepAlive([0, 1, 2, 3]),
            "a short leading-zero datagram is not mistaken for a keepalive");
        check(!GameplayDatagramService.TryReadBlockSequence([0, 1, 2, 3], []),
            "a short leading-zero datagram fails the gate");
    }
}
