using System.Buffers.Binary;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// Party packet builders (07 §4.8.3 三层镜像: Builders/Party).
/// Moved verbatim from GameProtocolEngine (R7); byte output unchanged.
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
            GameProtocolEngine.NotificationPacketType,
            (byte)NotiPacketType.USER_UDP_IP_PORT,
            payload.ToArray());
    }
    /// <summary>
    /// PARTY_INFO (NOTI 9) uses a block list in this build: u16 block count,
    /// then per block u16 party id and u8 type. Type 0/1 carry the settings
    /// pair the client reads at 0x41B8F9 (u8 title kind, u8 user max — kind-0
    /// titles render from the client's local string 0x272, never from the
    /// wire); type 0/2 carry four roster slots of u16 uid + u8 state plus
    /// the leader slot tail byte (reader at 0x41B9CD, leader store at
    /// 0x41BAD7); type 3 is the five-byte clear header only. There is no
    /// trailing has-extra byte in this build.
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
                PartyPacketBuilders.WritePartySettings(writer, block.TitleKind, block.UserMax);
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
            GameProtocolEngine.NotificationPacketType,
            (byte)NotiPacketType.PARTY_INFO,
            payload.ToArray());
    }
    private static void WritePartySettings(BinaryWriter writer, byte titleKind, byte userMax)
    {
        // Downstream settings are the bare two-byte pair (reader 0x41B8F9):
        // the client loads its local default-title string 0x272 for kind 0
        // and leaves the title empty otherwise — the u32-prefixed dstr only
        // exists on the uplink SET_PARTY_INFO writer (0x7D5F86). Emitting it
        // here desynchronises the reader: userMax and every roster slot
        // shift by the four length bytes.
        writer.Write(titleKind);
        writer.Write(userMax);
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
            GameProtocolEngine.NotificationPacketType,
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
            GameProtocolEngine.NotificationPacketType,
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
            GameProtocolEngine.NotificationPacketType,
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
    /// Party command failure replies are not the generic [0, code] form: the
    /// reply dispatcher reads a result byte first (zero = failure) and then
    /// one error byte at payload[1] for SET_PARTY_INFO (code 8 pops a party
    /// message, case 0x410799), LEAVE_PARTY (code 0x12, case 0x4107D8) and
    /// WALKOUT (code 4, case 0x410834). The trailing zero keeps the global
    /// read cursor inside the payload.
    /// </summary>
    public static GameServerPacket CreatePartyCommandError(
        PacketFrame request,
        byte errorCode) =>
        new(
            GameProtocolEngine.CommandPacketType,
            request.ProtocolId,
            [0, errorCode, 0, 0]);
    /// <summary>The peer commands read their error byte at payload[2]
    /// instead: REQUEST_PEER pops a message on code 5 (case 0x4102F6) and
    /// RESPONSE_PEER on code 1 (case 0x4104FF).</summary>
    public static GameServerPacket CreatePeerCommandError(
        PacketFrame request,
        byte errorCode) =>
        new(
            GameProtocolEngine.CommandPacketType,
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
            GameProtocolEngine.NotificationPacketType,
            (byte)NotiPacketType.UDP_HOST,
            [partyIndex]);
    }
}
