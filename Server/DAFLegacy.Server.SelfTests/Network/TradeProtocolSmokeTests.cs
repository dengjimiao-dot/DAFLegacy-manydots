using System.Net;
using DAFLegacy.Protocol;
using DAFLegacy.Server;

/// <summary>
/// 交易协议层自测（11 号 P3 第一步；docs/design/12 §3.3）：NOTI 15-18 下行形状、
/// CMD 26 状态机转移（确认切换/取消拆除/双锁条件/幂等）与断线清理。
/// </summary>
internal static class TradeProtocolSmokeTests
{
    public static void Run(Action<bool, string> check)
    {
        // NOTI 15：五元组线性。
        var itemUpdate = TradePacketBuilders.CreateTradeItemUpdate(9, 4, 0x11223344, 3, 77);
        check(itemUpdate.ProtocolId == (byte)NotiPacketType.CHANGE_ITEMTRADE_ITEM
            && itemUpdate.Payload.Length == 11
            && System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(itemUpdate.Payload.AsSpan(0, 2)) == 9
            && System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(itemUpdate.Payload.AsSpan(2, 2)) == 4
            && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(itemUpdate.Payload.AsSpan(4, 4)) == 0x11223344
            && itemUpdate.Payload[8] == 3
            && System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(itemUpdate.Payload.AsSpan(9, 2)) == 77,
            "NOTI 15 carries the five-field slot update in wire order");

        // NOTI 16：count + 条目。
        var offer = TradePacketBuilders.CreateTradeOfferList(9,
            [new TradeOfferEntry(3, 2, 500), new TradeOfferEntry(8, 1, 600)]);
        check(offer.ProtocolId == (byte)NotiPacketType.CANCEL_ITEMTRADE
            && offer.Payload.Length == 2 + 2 + 2 * 5
            && System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(offer.Payload.AsSpan(0, 2)) == 9
            && System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(offer.Payload.AsSpan(2, 2)) == 2,
            "NOTI 16 carries the counted offer list");

        // NOTI 17 / 18。
        var state = TradePacketBuilders.CreateTradeState(9, 3);
        check(state.ProtocolId == (byte)NotiPacketType.STATE_ITEMTRADE
            && state.Payload.Length == 3
            && state.Payload[2] == 3,
            "NOTI 17 carries peer uid plus state byte");
        var finish = TradePacketBuilders.CreateTradeFinish(1, 2, 3);
        check(finish.ProtocolId == (byte)NotiPacketType.FINISH_ITEMTRADE
            && finish.Payload.Length == 6,
            "NOTI 18 carries the three u16 finish fields");

        // 状态机：无会话对时一切转移无效。
        var registry = new TradeSessionRegistry();
        var sessionA = Guid.NewGuid();
        var sessionB = Guid.NewGuid();
        check(registry.ApplyStateByte(sessionA, 1) is null,
            "state transitions without a trade pair are rejected");
        check(!registry.BeginTrade(sessionA, sessionA),
            "self-pairing is rejected");
        check(registry.BeginTrade(sessionA, sessionB), "the trade pair is established");

        // 确认切换：{1} → Confirmed；同值幂等。
        var confirmA = registry.ApplyStateByte(sessionA, 1);
        check(confirmA is { State: TradeState.Confirmed, InitiatorSessionId: var initiator }
            && initiator == sessionA,
            "state byte 1 marks the sender confirmed");
        check(registry.ApplyStateByte(sessionA, 1) is null,
            "repeating the same confirm is idempotent");

        // 双锁条件：对方未确认时 {3} 无效。
        check(registry.ApplyStateByte(sessionA, 3) is null,
            "lock is rejected until both sides confirmed");
        var confirmB = registry.ApplyStateByte(sessionB, 1);
        check(confirmB is { State: TradeState.Confirmed, PartnerSessionId: var partner }
            && partner == sessionA,
            "the partner confirms against the sender");
        var lockA = registry.ApplyStateByte(sessionA, 3);
        check(lockA is { State: TradeState.Locked },
            "the lock lands once both sides confirmed");
        check(!registry.TryGetPartner(sessionA, out _) && !registry.TryGetPartner(sessionB, out _),
            "the lock tears the pair down (the finish flow follows)");

        // 取消：{2} 拆除会话对。
        check(registry.BeginTrade(sessionA, sessionB), "a fresh pair is established");
        var cancel = registry.ApplyStateByte(sessionB, 2);
        check(cancel is { State: TradeState.Cancelled, InitiatorSessionId: var canceller }
            && canceller == sessionB,
            "state byte 2 cancels from the sender side");
        check(!registry.TryGetPartner(sessionA, out _),
            "the cancel clears both ends of the pair");

        // 断线清理。
        check(registry.BeginTrade(sessionA, sessionB), "the pair reforms for the disconnect case");
        check(registry.EndTradeForSession(sessionA) == sessionB,
            "ending for one session reports the other end");
        check(!registry.TryGetPartner(sessionB, out _),
            "the surviving end is released too");
    }
}
