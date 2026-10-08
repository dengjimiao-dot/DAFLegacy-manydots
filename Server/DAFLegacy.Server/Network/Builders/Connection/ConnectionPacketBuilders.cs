using System.Buffers.Binary;
using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

/// <summary>
/// Connection packet builders (07 §4.8.3 三层镜像: Builders/Connection).
/// Moved verbatim from the R7 engine split; byte output unchanged.
/// </summary>
internal static class ConnectionPacketBuilders
{
    public static GameServerPacket CreateCommandReply(PacketFrame request, bool success)
    {
        if (request.Body.Length < sizeof(ushort))
        {
            throw new ArgumentException("A game command must contain a two-byte sequence number.", nameof(request));
        }

        return new GameServerPacket(
            GameProtocolConstants.CommandPacketType,
            request.ProtocolId,
            [success ? (byte)1 : (byte)0]);
    }
    public static GameServerPacket CreateCommandError(PacketFrame request, byte errorCode)
    {
        if (request.Body.Length < sizeof(ushort))
        {
            throw new ArgumentException("A game command must contain a two-byte sequence number.", nameof(request));
        }

        return new GameServerPacket(
            GameProtocolConstants.CommandPacketType,
            request.ProtocolId,
            [0, errorCode]);
    }
    public static GameServerPacket CreateEnterGameWorldComplete() =>
        new(GameProtocolConstants.NotificationPacketType, (byte)NotiPacketType.ENTER_GAMEWORLD_COMPLETE, []);

    /// <summary>
    /// LOGIN (CMD 1) success reply. The 60A1 reply case (0x40F2E0) reads
    /// <c>u8 result + u8×4 + u32 account + dstr relay host + u32 relay port1 +
    /// u32 relay port2 + u8×2</c> (2026-10-08 IDA 复核) and hands the host and
    /// both ports to <c>sub_906070</c>, which stores them on the client's relay
    /// transport and sets its enable flag. The account lands in
    /// <c>dword_B55EE0</c>, the source account of the client's relay frames.
    /// <para>
    /// The four leading flags keep the previously verified zeroes (the third
    /// one is normalised to 4 by the client itself, which is why the bare
    /// five-byte form used to work); everything after them must be present or
    /// the client reads past the payload and configures the relay transport
    /// from stale buffer bytes. This server implements no relay, so the relay
    /// fields carry the inert form — empty host, zero ports — while the
    /// account stays truthful for the frame header's source field.
    /// </para>
    /// </summary>
    public static GameServerPacket CreateLoginOk(uint accountUid)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);      // result
        writer.Write((byte)0);      // account flag 1
        writer.Write((byte)0);      // account flag 2
        writer.Write((byte)0);      // client-state class (client normalises 0 to 4)
        writer.Write((byte)0);      // trailing account flag
        writer.Write(accountUid);   // relay frame source account (dword_B55EE0)
        writer.Write(0u);           // relay host: zero-length dstr = no relay endpoint
        writer.Write(0u);           // relay port 1
        writer.Write(0u);           // relay port 2
        writer.Write((byte)0);      // trailing flag 1
        writer.Write((byte)0);      // trailing flag 2
        return new GameServerPacket(
            GameProtocolConstants.CommandPacketType,
            (byte)CmdPacketType.LOGIN,
            payload.ToArray());
    }
    public static GameServerPacket CreateCheckConnection(ReadOnlySpan<byte> challenge)
    {
        if (challenge.Length != 16)
        {
            throw new ArgumentException("The check-connection challenge must be exactly 16 bytes.", nameof(challenge));
        }

        return new GameServerPacket(
            GameProtocolConstants.NotificationPacketType,
            (byte)NotiPacketType.CHECK_CONNECTION,
            challenge.ToArray());
    }
    public static GameServerPacket CreateChannelInfo(
        ReadOnlySpan<byte> displayName,
        uint channelType,
        uint result,
        byte serverId,
        byte channelNumber,
        uint seed,
        IReadOnlyList<byte[]> hosts,
        uint port1,
        uint port2)
    {
        if (displayName.Length >= 0x40)
        {
            throw new ArgumentException("The DAFLegacy channel display name must be shorter than 64 bytes.", nameof(displayName));
        }

        ArgumentNullException.ThrowIfNull(hosts);

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        PacketBodyWriter.WriteLengthPrefixedBytes(writer, displayName.ToArray());
        writer.Write(channelType);
        writer.Write(result);
        writer.Write(serverId);
        writer.Write(channelNumber);
        writer.Write(seed);
        writer.Write((uint)hosts.Count);
        foreach (var host in hosts)
        {
            ArgumentNullException.ThrowIfNull(host);
            if (host.Length >= 0x104)
            {
                throw new ArgumentException("A DAFLegacy channel host must be shorter than 260 bytes.", nameof(hosts));
            }

            PacketBodyWriter.WriteLengthPrefixedBytes(writer, host);
        }

        writer.Write(port1);
        writer.Write(port2);
        return new GameServerPacket(
            GameProtocolConstants.NotificationPacketType,
            (byte)NotiPacketType.CHANNELINFO,
            payload.ToArray());
    }
}
