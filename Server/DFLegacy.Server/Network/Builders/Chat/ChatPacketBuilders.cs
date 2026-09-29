using System.Buffers.Binary;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// Chat packet builders (07 §4.8.3 三层镜像: Builders/Chat).
/// Moved verbatim from GameProtocolEngine (R7); byte output unchanged.
/// </summary>
internal static class ChatPacketBuilders
{
    public static GameServerPacket CreateMessageNotification(
        GameMessageType messageType,
        ushort targetAreaUserId,
        ReadOnlySpan<byte> messageBytes) =>
        ChatPacketBuilders.CreateChatMessageNotification(
            GameProtocolEngine.MessageNotification,
            messageType,
            targetAreaUserId,
            messageBytes);
    public static GameServerPacket CreateCreatureScriptMessageNotification(
        GameMessageType messageType,
        ushort targetAreaUserId,
        ReadOnlySpan<byte> messageBytes) =>
        ChatPacketBuilders.CreateChatMessageNotification(
            GameProtocolEngine.CreatureScriptMessageNotification,
            messageType,
            targetAreaUserId,
            messageBytes);
    public static GameServerPacket CreateCreatureMessageNotification(
        GameMessageType messageType,
        ushort targetAreaUserId,
        ReadOnlySpan<byte> messageBytes) =>
        ChatPacketBuilders.CreateChatMessageNotification(
            GameProtocolEngine.CreatureMessageNotification,
            messageType,
            targetAreaUserId,
            messageBytes);
    public static GameServerPacket CreateMegaphoneNotification(
        GameMessageType messageType,
        byte ownerFlag,
        ushort targetAreaUserId,
        ReadOnlySpan<byte> senderName,
        ReadOnlySpan<byte> messageBytes)
    {
        // NOTI 130 is read by DNF.exe case 130 (sub_419420 @0x427414) with
        // the global-cursor dstr reader sub_8B3250: a length of zero or at
        // least the destination capacity leaves the cursor unadvanced and
        // shifts every later field, so the caps below are hard requirements.
        // Flag byte matching the local player routes the message to the chat
        // window; any other value renders the speaker banner (0xFF00F2FF).
        if (senderName.Length is < 1 or >= 0x1E)
        {
            throw new ArgumentOutOfRangeException(nameof(senderName));
        }

        if (messageBytes.Length is < 1 or >= 0x200)
        {
            throw new ArgumentOutOfRangeException(nameof(messageBytes));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)messageType);
        writer.Write(ownerFlag);
        writer.Write(targetAreaUserId);
        writer.Write((uint)senderName.Length);
        writer.Write(senderName);
        writer.Write((uint)messageBytes.Length);
        writer.Write(messageBytes);
        return new GameServerPacket(
            GameProtocolEngine.NotificationPacketType,
            GameProtocolEngine.MegaphoneMessageNotification,
            payload.ToArray());
    }
    private static GameServerPacket CreateChatMessageNotification(
        byte notificationId,
        GameMessageType messageType,
        ushort targetAreaUserId,
        ReadOnlySpan<byte> messageBytes)
    {
        // The 2008 client uses a fixed 0x100-byte destination buffer and only
        // accepts non-empty strings whose encoded length is below that size.
        // DNF.exe 0x41CA99 parses NOTI 126 and 131 through this same payload
        // layout; the notification id only selects the final display mode.
        if (messageBytes.Length is < 1 or >= 0x100)
        {
            throw new ArgumentOutOfRangeException(nameof(messageBytes));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)messageType);
        writer.Write(targetAreaUserId);
        writer.Write((uint)messageBytes.Length);
        writer.Write(messageBytes);
        return new GameServerPacket(
            GameProtocolEngine.NotificationPacketType,
            notificationId,
            payload.ToArray());
    }
    public static GameServerPacket CreatePopupNotification(
        ReadOnlySpan<byte> messageBytes)
    {
        // NOTI 126 is the legacy CREATURE_MESSAGE packet. Message type zero
        // formats the received text with client string 640 and opens popup 44.
        if (messageBytes.Length is < 1 or >= 0x100)
        {
            throw new ArgumentOutOfRangeException(nameof(messageBytes));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)0);
        writer.Write((ushort)0);
        writer.Write((uint)messageBytes.Length);
        writer.Write(messageBytes);
        return new GameServerPacket(
            GameProtocolEngine.NotificationPacketType,
            GameProtocolEngine.CreatureMessageNotification,
            payload.ToArray());
    }
}
