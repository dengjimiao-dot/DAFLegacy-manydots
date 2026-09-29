using System.Buffers.Binary;
using System.Text;

namespace DFLegacy.Server;

/// <summary>
/// Mailbox request parsing (07 §4.7): MAILBOX_EXTRACT (CMD 98), the
/// CHANGE_LETTER_STAT (CMD 144) body and MAILBOX_SEND (CMD 97).
/// </summary>
internal static class MailboxParsers
{
    private static readonly Encoding ClientEncoding = PvfEncodings.Cp936Lossy();

    internal static bool TryReadMailboxExtract(byte[] body, out uint mailId)
    {
        mailId = 0;
        if (body.Length < 2 + sizeof(uint))
        {
            return false;
        }

        mailId = BinaryPrimitives.ReadUInt32LittleEndian(
            body.AsSpan(2, sizeof(uint)));
        return mailId != 0;
    }

    internal static bool TryReadMailboxState(
        byte[] body,
        out uint mailId,
        out ushort state)
    {
        mailId = 0;
        state = 0;
        if (body.Length != sizeof(ushort) + sizeof(uint) + sizeof(ushort))
        {
            return false;
        }

        mailId = BinaryPrimitives.ReadUInt32LittleEndian(
            body.AsSpan(sizeof(ushort), sizeof(uint)));
        state = BinaryPrimitives.ReadUInt16LittleEndian(
            body.AsSpan(sizeof(ushort) + sizeof(uint), sizeof(ushort)));
        return mailId != 0 && state is 0 or 2 or 3;
    }

    internal static bool TryReadMailboxSend(
        byte[] body,
        out MailboxSendRequest request)
    {
        request = default;
        if (!TryReadLengthPrefixedBytes(body, 2, out var recipientBytes, out var offset)
            || recipientBytes.Length is 0 or >= 30
            || body.Length - offset
                < sizeof(uint) + sizeof(byte) + sizeof(ushort) + sizeof(ushort) + sizeof(uint))
        {
            return false;
        }

        var goldValue = BinaryPrimitives.ReadUInt32LittleEndian(
            body.AsSpan(offset, sizeof(uint)));
        offset += sizeof(uint);
        if (goldValue > int.MaxValue)
        {
            return false;
        }

        var listType = body[offset++];
        var slot = BinaryPrimitives.ReadUInt16LittleEndian(
            body.AsSpan(offset, sizeof(ushort)));
        offset += sizeof(ushort);
        var itemId = BinaryPrimitives.ReadUInt16LittleEndian(
            body.AsSpan(offset, sizeof(ushort)));
        offset += sizeof(ushort);
        var countOrValue = BinaryPrimitives.ReadUInt32LittleEndian(
            body.AsSpan(offset, sizeof(uint)));
        offset += sizeof(uint);
        if (!TryReadLengthPrefixedBytes(body, offset, out var textBytes, out var endOffset)
            || textBytes.Length >= 512
            || endOffset != body.Length)
        {
            return false;
        }

        var recipient = ClientEncoding.GetString(recipientBytes).Trim();
        if (string.IsNullOrWhiteSpace(recipient) || recipient.Length > 20)
        {
            return false;
        }

        request = new MailboxSendRequest(
            recipient,
            checked((int)goldValue),
            listType,
            slot,
            itemId,
            countOrValue,
            ClientEncoding.GetString(textBytes));
        return true;
    }

    internal static bool TryReadLengthPrefixedBytes(
        byte[] source,
        int offset,
        out byte[] value,
        out int nextOffset) =>
        ClientBodyParsers.TryReadLengthPrefixedBytes(source, offset, out value, out nextOffset);

    internal static Encoding CreateClientEncoding()
    {
        return PvfEncodings.Cp936Lossy();
    }

    internal readonly record struct MailboxSendRequest(
        string Recipient,
        int Gold,
        byte ListType,
        ushort Slot,
        ushort ItemId,
        uint CountOrValue,
        string Text);
}
