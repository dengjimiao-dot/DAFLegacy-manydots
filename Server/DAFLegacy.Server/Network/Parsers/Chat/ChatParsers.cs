using System.Buffers.Binary;
using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

/// <summary>
/// Chat request parsing (07 §4.7): SEND_MESSAGE (CMD 17) and the creature
/// talk-alikes CMD 124/132 share the same body layout — type byte, reserved
/// u16, client u32 area-user id, u32 length-prefixed CP936 text, and for the
/// direct-message types a trailing partner-name dstr.
/// </summary>
internal static class ChatParsers
{
    internal readonly record struct SendMessageRequest(
        byte MessageType,
        ushort TargetOrItemSpace,
        uint SlotOrReserved,
        byte[] MessageBytes,
        byte[] TargetNameBytes);

    private static byte[] EncodeCharacterName(string name) =>
        CharacterPacketBuilders.EncodeCharacterName(name);

    internal static bool TryReadSendMessage(
        byte[] body,
        out SendMessageRequest request)
    {
        request = default;
        const int payloadOffset = sizeof(ushort);
        const int messageOffset = payloadOffset
            + sizeof(byte)
            + sizeof(ushort)
            + sizeof(uint)
            + sizeof(uint);
        if (body.Length < messageOffset)
        {
            return false;
        }

        var messageType = body[payloadOffset];
        var targetOrItemSpace = BinaryPrimitives.ReadUInt16LittleEndian(
            body.AsSpan(payloadOffset + sizeof(byte), sizeof(ushort)));
        var slotOrReserved = BinaryPrimitives.ReadUInt32LittleEndian(
            body.AsSpan(payloadOffset + sizeof(byte) + sizeof(ushort), sizeof(uint)));
        var messageLength = BinaryPrimitives.ReadUInt32LittleEndian(
            body.AsSpan(
                payloadOffset + sizeof(byte) + sizeof(ushort) + sizeof(uint),
                sizeof(uint)));
        if (messageLength is 0 or > 0xFF
            || messageLength > body.Length - messageOffset)
        {
            return false;
        }

        var messageBytes = body.AsSpan(messageOffset, (int)messageLength).ToArray();
        if (messageBytes.Contains((byte)0))
        {
            return false;
        }

        // Direct messages (type 18, and the type 1/7 chat-window whispers)
        // append a second CP936 dstr naming the chat partner: DNF.exe
        // sub_811C20 writes type 18 + zero u16 + zero u32 + text + partner
        // name (a 20-byte window buffer), and the shared sender sub_8495B0
        // appends the same name dstr for types 1 and 7. Every other type
        // ends at the text, so their bodies must not carry trailing bytes.
        var targetNameBytes = Array.Empty<byte>();
        if (messageType
            is (byte)GameMessageType.Type01LightGreen
                or (byte)GameMessageType.Type07LightGreen
                or (byte)GameMessageType.Type18DirectOutput)
        {
            var nameOffset = messageOffset + (int)messageLength;
            if (body.Length < nameOffset + sizeof(uint)
                || !TryReadTargetName(
                    body.AsSpan(nameOffset),
                    out targetNameBytes))
            {
                return false;
            }
        }
        else if (body.Length != messageOffset + (int)messageLength)
        {
            return false;
        }

        request = new SendMessageRequest(
            messageType,
            targetOrItemSpace,
            slotOrReserved,
            messageBytes,
            targetNameBytes);
        return true;
    }

    private static bool TryReadTargetName(
        ReadOnlySpan<byte> span,
        out byte[] nameBytes)
    {
        nameBytes = [];
        var nameLength = BinaryPrimitives.ReadUInt32LittleEndian(span);
        // sub_8495B0 rejects partner names above 0x1E bytes before sending.
        if (nameLength is 0 or > 0x1E
            || nameLength != span.Length - sizeof(uint))
        {
            return false;
        }

        nameBytes = span.Slice(sizeof(uint), (int)nameLength).ToArray();
        return !nameBytes.Contains((byte)0);
    }

}
