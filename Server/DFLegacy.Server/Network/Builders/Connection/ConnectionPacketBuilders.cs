using System.Buffers.Binary;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// Connection packet builders (07 §4.8.3 三层镜像: Builders/Connection).
/// Moved verbatim from GameProtocolEngine (R7); byte output unchanged.
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
            GameProtocolEngine.CommandPacketType,
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
            GameProtocolEngine.CommandPacketType,
            request.ProtocolId,
            [0, errorCode]);
    }
    public static GameServerPacket CreateEnterGameWorldComplete() =>
        new(GameProtocolEngine.NotificationPacketType, GameProtocolEngine.EnterGameWorldCompleteNotification, []);
    public static GameServerPacket CreateCheckConnection(ReadOnlySpan<byte> challenge)
    {
        if (challenge.Length != 16)
        {
            throw new ArgumentException("The check-connection challenge must be exactly 16 bytes.", nameof(challenge));
        }

        return new GameServerPacket(
            GameProtocolEngine.NotificationPacketType,
            GameProtocolEngine.CheckConnectionNotification,
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
            throw new ArgumentException("The DFLegacy channel display name must be shorter than 64 bytes.", nameof(displayName));
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
                throw new ArgumentException("A DFLegacy channel host must be shorter than 260 bytes.", nameof(hosts));
            }

            PacketBodyWriter.WriteLengthPrefixedBytes(writer, host);
        }

        writer.Write(port1);
        writer.Write(port2);
        return new GameServerPacket(
            GameProtocolEngine.NotificationPacketType,
            GameProtocolEngine.ChannelInfoNotification,
            payload.ToArray());
    }
}
