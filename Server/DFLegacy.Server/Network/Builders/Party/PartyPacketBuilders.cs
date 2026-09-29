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
            GameProtocolEngine.UdpPeerInfoNotification,
            payload.ToArray());
    }
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
            GameProtocolEngine.PartyInfoNotification,
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
            GameProtocolEngine.PeerRequestNotification,
            payload.ToArray());
    }
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
            GameProtocolEngine.PeerResponseNotification,
            payload.ToArray());
    }
    public static GameServerPacket CreateWalkout(ushort userId, byte reason)
    {
        // The wire uid is u8 here (reader 0x41C42F); larger ids truncate for
        // the matching lookup, which only affects the departure chat line.
        return new GameServerPacket(
            GameProtocolEngine.NotificationPacketType,
            GameProtocolEngine.WalkoutNotification,
            [(byte)userId, reason]);
    }
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
    public static GameServerPacket CreatePartyCommandError(
        PacketFrame request,
        byte errorCode) =>
        new(
            GameProtocolEngine.CommandPacketType,
            request.ProtocolId,
            [0, errorCode, 0, 0]);
    public static GameServerPacket CreatePeerCommandError(
        PacketFrame request,
        byte errorCode) =>
        new(
            GameProtocolEngine.CommandPacketType,
            request.ProtocolId,
            [0, 0, errorCode, 0]);
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
            GameProtocolEngine.UdpHostNotification,
            [partyIndex]);
    }
}
