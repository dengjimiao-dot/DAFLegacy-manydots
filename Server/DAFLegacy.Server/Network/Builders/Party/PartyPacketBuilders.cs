using System.Buffers.Binary;
using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

/// <summary>
/// Party packet builders (07 §4.8.3 三层镜像: Builders/Party).
/// Moved verbatim from the R7 engine split; byte output unchanged.
/// </summary>
internal static class PartyPacketBuilders
{
    private const ushort PartyEmptySlotUserId = 0xFFFF;
    public static GameServerPacket CreateUdpPeerInfo(
        IReadOnlyList<GamePeerInfo> peers)
    {
        ArgumentNullException.ThrowIfNull(peers);
        if (peers.Count > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(peers));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)peers.Count);
        foreach (var peer in peers)
        {
            writer.Write(peer.UserId);
            PacketBodyWriter.WriteIpv4Address(writer, peer.LocalAddress);
            PacketBodyWriter.WriteIpv4Address(writer, peer.PublicAddress);
            writer.Write(peer.PublicPort);
            writer.Write(peer.AccountId);
            writer.Write(peer.NatType);
            writer.Write(peer.Mtu);
        }

        return new GameServerPacket(
            GameProtocolConstants.NotificationPacketType,
            (byte)NotiPacketType.USER_UDP_IP_PORT,
            payload.ToArray());
    }
    /// <summary>
    /// PARTY_INFO (NOTI 9) uses a block list in this build: u16 block count,
    /// then per block u16 party id and u8 type. Type 0/1 carry the settings
    /// section the client reads at 0x41B8F9: u8 title kind, then — only when
    /// the kind is 0 — the uplink dstr shape (u32 length + CP936 text, read
    /// by sub_8B3250 with cap 0x40 and the local string 0x272 as the fallback
    /// for zero/over-cap lengths), then u8 user max. Kind 1..6 skips the dstr
    /// and goes straight to the user-max byte. Type 0/2 additionally carry
    /// four roster slots of u16 uid + u8 state plus the leader slot tail byte
    /// (reader at 0x41B9CD, leader store at 0x41BAD7); type 3 is the
    /// five-byte clear header only. There is no trailing has-extra byte in
    /// this build.
    /// </summary>
    public static GameServerPacket CreatePartyInfo(
        IReadOnlyList<PartyInfoWireBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        if (blocks.Count > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(blocks));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((ushort)blocks.Count);
        foreach (var block in blocks)
        {
            writer.Write((ushort)block.PartyId);
            writer.Write(block.BlockType);
            if (block.BlockType is 0 or 1)
            {
                PartyPacketBuilders.WritePartySettings(writer, block);
            }

            if (block.BlockType is 0 or 2)
            {
                if (block.Members.Count > 4)
                {
                    throw new ArgumentOutOfRangeException(nameof(blocks));
                }

                for (var slot = 0; slot < 4; slot++)
                {
                    if (slot < block.Members.Count)
                    {
                        writer.Write(block.Members[slot].UserId);
                        writer.Write(block.Members[slot].State);
                    }
                    else
                    {
                        // The client renders 0xFFFF slots as empty (cleared
                        // slots are written as 0xFFFF at 0x41BB1C).
                        writer.Write(PartyEmptySlotUserId);
                        writer.Write((byte)0);
                    }
                }

                writer.Write(block.LeaderSlot);
            }
        }

        return new GameServerPacket(
            GameProtocolConstants.NotificationPacketType,
            (byte)NotiPacketType.PARTY_INFO,
            payload.ToArray());
    }

    /// <summary>
    /// Exclusive cap of the kind-0 title length: the client's dstr reader
    /// (sub_8B3250, called with cap 0x40 at 0x41B955) displays lengths below
    /// the cap, falls back to its local string 0x272 at or beyond it (without
    /// consuming the payload), and the uplink parser rejects them equally.
    /// </summary>
    public const int CustomTitleLengthCap = 0x40;

    private static void WritePartySettings(
        BinaryWriter writer,
        PartyInfoWireBlock block)
    {
        // Downstream settings section (reader 0x41B8F9): the kind byte always
        // comes first. Kind 0 continues with the uplink dstr shape — the
        // reader unconditionally consumes a u32 length there (sub_8B3250) and
        // the text bytes when 0 < length < cap, so emitting the bare two-byte
        // pair desynchronises it: the name renders as the local fallback
        // string 0x272 and every later block of the packet misaligns. A zero
        // length is the honest "no custom title" form and renders the same
        // fallback. Kind 1..6 skips the dstr entirely.
        writer.Write(block.TitleKind);
        if (block.TitleKind == 0)
        {
            var titleBytes = block.TitleBytes;
            if (titleBytes is { Count: > 0 })
            {
                if (titleBytes.Count >= CustomTitleLengthCap)
                {
                    throw new ArgumentOutOfRangeException(nameof(block));
                }

                writer.Write((uint)titleBytes.Count);
                foreach (var value in titleBytes)
                {
                    writer.Write(value);
                }
            }
            else
            {
                writer.Write(0u);
            }
        }

        writer.Write(block.UserMax);
    }
    /// <summary>
    /// NOTI 7 invite popup. The reader at 0x41B4C8 consumes u16 uid, u8 type,
    /// u32 peer id and, for type 0, two more u16 state words.
    /// </summary>
    public static GameServerPacket CreatePeerRequest(
        ushort fromUserId,
        byte requestType,
        uint peerId,
        ushort fromUserState = 0,
        ushort fromUserSecondaryState = 0)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(fromUserId);
        writer.Write(requestType);
        writer.Write(peerId);
        if (requestType == 0)
        {
            writer.Write(fromUserState);
            writer.Write(fromUserSecondaryState);
        }

        return new GameServerPacket(
            GameProtocolConstants.NotificationPacketType,
            (byte)NotiPacketType.REQUEST_PEER,
            payload.ToArray());
    }
    /// <summary>
    /// NOTI 8 peer response ack (reader at 0x41B5F1): u16 uid, u8 type,
    /// u32 value. The type-0 zero-value form is the party accept the inviter
    /// needs to claim the party before the PARTY_INFO formation arrives.
    /// </summary>
    public static GameServerPacket CreatePeerResponse(
        ushort fromUserId,
        byte responseType,
        uint value)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(fromUserId);
        writer.Write(responseType);
        writer.Write(value);
        return new GameServerPacket(
            GameProtocolConstants.NotificationPacketType,
            (byte)NotiPacketType.RESPONSE_PEER,
            payload.ToArray());
    }
    /// <summary>
    /// NOTI 10 departure notice (reader at 0x41C41A): u8 uid, u8 reason
    /// 0..9. Reason 0/1 render the left/kicked party chat lines.
    /// </summary>
    public static GameServerPacket CreateWalkout(ushort userId, byte reason)
    {
        // The wire uid is u8 here (reader 0x41C42F); larger ids truncate for
        // the matching lookup, which only affects the departure chat line.
        return new GameServerPacket(
            GameProtocolConstants.NotificationPacketType,
            (byte)NotiPacketType.WALKOUT,
            [(byte)userId, reason]);
    }
    /// <summary>
    /// Parses SET_PARTY_INFO (CMD 12, writer at 0x7D5F86): after the two-byte
    /// sequence number the client writes u8 title kind, a u32-prefixed custom
    /// title when the kind is zero, and u8 user max (radio index + 2).
    /// </summary>
    public static bool TryParseSetPartyInfo(
        ReadOnlySpan<byte> body,
        out PartySettingsRequest request)
    {
        request = new PartySettingsRequest(0, [], 0);
        // The block runs for every party command (10-14); short bodies such
        // as WALKOUT's three bytes must fall through to the dispatch switch
        // instead of throwing here.
        if (body.Length < 3)
        {
            return false;
        }

        var titleKind = body[2];
        if (titleKind != 0)
        {
            if (body.Length < 4)
            {
                return false;
            }

            request = new PartySettingsRequest(
                titleKind,
                [],
                body[3]);
            return true;
        }

        if (body.Length < 8)
        {
            return false;
        }

        var titleLength = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(3, 4));
        if (titleLength >= 0x40 || body.Length < 8 + titleLength)
        {
            return false;
        }

        var titleBytes = body.Slice(7, (int)titleLength).ToArray();
        request = new PartySettingsRequest(
            titleKind,
            titleBytes,
            body[7 + (int)titleLength]);
        return true;
    }
    /// <summary>Parses REQUEST_PEER (CMD 10): u16 target uid, u8 type, u32 id.</summary>
    public static bool TryParseRequestPeer(
        ReadOnlySpan<byte> body,
        out PeerRequestFrame request)
    {
        request = new PeerRequestFrame(0, 0, 0);
        if (body.Length < 9)
        {
            return false;
        }

        request = new PeerRequestFrame(
            BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(2, 2)),
            body[4],
            BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(5, 4)));
        return true;
    }
    /// <summary>
    /// Parses RESPONSE_PEER (CMD 11): u16 peer uid, u8 type, u32 value. The
    /// client accept path writes exactly seven payload bytes with a zero type
    /// and value (0x4FEBC0); refusals set a non-zero type or value.
    /// </summary>
    public static bool TryParseResponsePeer(
        ReadOnlySpan<byte> body,
        out PeerResponseFrame response)
    {
        response = new PeerResponseFrame(0, 0, 0);
        if (body.Length < 9)
        {
            return false;
        }

        response = new PeerResponseFrame(
            BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(2, 2)),
            body[4],
            BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(5, 4)));
        return true;
    }
    public static bool IsAcceptedPeerResponse(PeerResponseFrame response) =>
        response.ResponseType == 0 && response.Value == 0;
    /// <summary>
    /// RESPONSE_PEER (CMD 11) success reply. The 60A1 reply case (0x4104FF)
    /// reads the result byte and then, on success, **unconditionally** reads
    /// u16 + u8 (0x41050A..0x410533: sub_8F5140 then sub_8F5120) plus one more
    /// u8 when that type byte is 1 (0x41054D, the trade completion branch), so
    /// the body must echo the request's peer uid and type — the same shape the
    /// reference writes for this command (`U8(1); U16(peer); U8(type)`).
    /// Replying with a bare `[1]` makes the client read past the payload into
    /// whatever the receive buffer still holds; a leftover 1 byte there also
    /// routes the accept into the trade-completion branch with a garbage uid.
    /// 2026-10-08 IDA 复核(case 0x4104FF 反编)结论。
    /// </summary>
    public static GameServerPacket CreatePeerResponseAck(PeerResponseFrame response) =>
        new(
            GameProtocolConstants.CommandPacketType,
            (byte)CmdPacketType.RESPONSE_PEER,
            [
                (byte)1,
                (byte)response.PeerUserId,
                (byte)(response.PeerUserId >> 8),
                response.ResponseType,
            ]);
    /// <summary>
    /// Party command failure replies are not the generic [0, code] form: the
    /// reply dispatcher reads a result byte first (zero = failure) and then
    /// one error byte at payload[1] for SET_PARTY_INFO (code 8 pops a party
    /// message, case 0x410799), LEAVE_PARTY (code 0x12/0x13, case 0x4107D8) and
    /// WALKOUT (case 0x410834). The trailing zero keeps the global read cursor
    /// inside the payload.
    /// </summary>
    public static GameServerPacket CreatePartyCommandError(
        PacketFrame request,
        byte errorCode) =>
        new(
            GameProtocolConstants.CommandPacketType,
            request.ProtocolId,
            [0, errorCode, 0, 0]);
    /// <summary>The peer commands read their failure bytes differently: REQUEST_PEER
    /// (case 0x4102F6) reads u8 + u8 and pops on the second byte == 5, while
    /// RESPONSE_PEER (case 0x4104FF) reads u8 + u8 where the first byte is the
    /// A12-style error class (3/4/0x11/0x12/0x13 → strings 0x2C3..0x2CC) and
    /// the second a sub-code. 2026-10-08 IDA 复核;本服 RESPONSE_PEER 的失败
    /// 应答仍按现行 [0,0,code,0] 发送(类码 0 不弹提示),类码映射待确认各串
    /// 文案后再接(PENDING P-37)。</summary>
    public static GameServerPacket CreatePeerCommandError(
        PacketFrame request,
        byte errorCode) =>
        new(
            GameProtocolConstants.CommandPacketType,
            request.ProtocolId,
            [0, 0, errorCode, 0]);
    /// <summary>Parses WALKOUT_PARTY_MEMBER (CMD 14, writer at 0x8520F3):
    /// one byte holding the selected member's roster slot index.</summary>
    public static bool TryParseWalkoutPartyMember(
        ReadOnlySpan<byte> body,
        out byte targetSlot)
    {
        targetSlot = 0;
        if (body.Length < 3)
        {
            return false;
        }

        targetSlot = body[2];
        return targetSlot < 4;
    }

    public static GameServerPacket CreateUdpHost(byte partyIndex = 0)
    {
        if (partyIndex >= 8)
        {
            throw new ArgumentOutOfRangeException(nameof(partyIndex));
        }

        return new GameServerPacket(
            GameProtocolConstants.NotificationPacketType,
            (byte)NotiPacketType.UDP_HOST,
            [partyIndex]);
    }
}
