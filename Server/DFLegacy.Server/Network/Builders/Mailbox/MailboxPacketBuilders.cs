using System.Buffers.Binary;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// Mailbox packet builders (07 §4.8.3 三层镜像: Builders/Mailbox).
/// Moved verbatim from GameProtocolEngine (R7); byte output unchanged.
/// </summary>
internal static class MailboxPacketBuilders
{
    public static GameServerPacket CreateMailboxOpenReply(ushort notLoadedCount = 0)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write(notLoadedCount);
        return new GameServerPacket(
            GameProtocolEngine.CommandPacketType,
            (byte)CmdPacketType.MAILBOX_OPEN,
            payload.ToArray());
    }
    public static GameServerPacket CreateMailboxExtractReply(uint mailId)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write(mailId);
        return new GameServerPacket(
            GameProtocolEngine.CommandPacketType,
            (byte)CmdPacketType.MAILBOX_EXTRACT_ITEM,
            payload.ToArray());
    }
    public static GameServerPacket CreateMailboxStateReply(uint mailId, ushort state)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write(mailId);
        writer.Write(state);
        return new GameServerPacket(
            GameProtocolEngine.CommandPacketType,
            (byte)CmdPacketType.CHANGE_LETTER_STAT,
            payload.ToArray());
    }
    public static GameServerPacket CreateMailboxList(
        IReadOnlyList<GameMailEntry> mails,
        short notLoadedCount = 0,
        bool reset = true,
        bool includeEmptyPackageCarriers = false)
    {
        ArgumentNullException.ThrowIfNull(mails);
        if (mails.Count > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(mails));
        }

        foreach (var mail in mails)
        {
            MailboxPacketBuilders.ValidateMailEntry(mail);
        }

        var packageMails = includeEmptyPackageCarriers
            ? mails.ToArray()
            : mails
                .Where(mail => mail.Gold != 0 || mail.Attachment is not null)
                .ToArray();

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        // The first section is a reward-package list, not a mail-summary list.
        // Text-only and archived mail belongs only to the body section below.
        writer.Write((byte)packageMails.Length);
        writer.Write(reset ? (byte)0 : (byte)1);
        foreach (var mail in packageMails)
        {
            var attachment = mail.Attachment;
            writer.Write(mail.MailId);
            PacketBodyWriter.WriteLengthPrefixedBytes(writer, mail.SenderBytes);
            writer.Write(mail.Gold);
            writer.Write(attachment?.ItemId ?? (ushort)0);

            // DFLegacy's mailbox item tuple predates ITEM_LIST and uses a
            // different byte order: seal, instance/value, durability, state.
            writer.Write(attachment?.SealState ?? (byte)0);
            writer.Write(attachment?.CountOrValue ?? 0u);
            writer.Write(attachment?.Durability ?? (ushort)0);
            writer.Write(attachment?.State ?? (byte)0);
            writer.Write(mail.SentAt);
            writer.Write(mail.MailId); // joins the package and text records
        }

        writer.Write(notLoadedCount);

        // Every visible mail has a body record. The state selects the client's
        // inbox (1/2) or archive (3); MailId correlates it with a package above.
        writer.Write((ushort)mails.Count);
        foreach (var mail in mails)
        {
            writer.Write(mail.MailId);
            writer.Write(mail.ExpiresAt);
            PacketBodyWriter.WriteLengthPrefixedBytes(writer, mail.SenderBytes);
            PacketBodyWriter.WriteLengthPrefixedBytes(writer, mail.TextBytes);
            writer.Write(mail.SentAt);
            writer.Write(mail.State);
        }

        return new GameServerPacket(
            GameProtocolEngine.NotificationPacketType,
            (byte)NotiPacketType.MAILBOX_MAIL_LIST,
            payload.ToArray());
    }
    public static IReadOnlyList<GameServerPacket> CreateMailboxSnapshot(
        IReadOnlyList<GameMailEntry> mails,
        short notLoadedCount = 0)
    {
        ArgumentNullException.ThrowIfNull(mails);

        var packets = new List<GameServerPacket>();
        var reset = true;

        // The old client carries one package-header id into every body in the
        // same NOTI 97. Restore archived bodies one at a time while the inbox
        // is empty so their empty carriers set that id without adding rows.
        foreach (var archivedMail in mails.Where(mail => mail.State == 3))
        {
            packets.Add(MailboxPacketBuilders.CreateMailboxList(
                [archivedMail],
                notLoadedCount,
                reset,
                includeEmptyPackageCarriers: true));
            reset = false;
        }

        var inboxMails = mails
            .Where(mail => mail.State != 3)
            .ToArray();
        if (inboxMails.Length != 0 || packets.Count == 0)
        {
            packets.Add(MailboxPacketBuilders.CreateMailboxList(
                inboxMails,
                notLoadedCount,
                reset));
        }

        return packets;
    }
    public static GameServerPacket CreateMailboxRemoveMail(
        IReadOnlyCollection<uint> mailIds)
    {
        ArgumentNullException.ThrowIfNull(mailIds);
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(checked((uint)mailIds.Count));
        foreach (var mailId in mailIds)
        {
            writer.Write(mailId);
        }

        return new GameServerPacket(
            GameProtocolEngine.NotificationPacketType,
            (byte)NotiPacketType.MAILBOX_REMOVE_MAIL,
            payload.ToArray());
    }
    public static GameServerPacket CreateMailboxAlarm(short newMailCount) =>
        new(
            GameProtocolEngine.NotificationPacketType,
            (byte)NotiPacketType.MAILBOX_ALARM,
            [(byte)newMailCount, (byte)(newMailCount >> 8)]);
    private static void ValidateMailEntry(GameMailEntry mail)
    {
        ArgumentNullException.ThrowIfNull(mail);
        if (mail.MailId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(mail), "Mail ids must be non-zero.");
        }

        if (mail.SenderBytes.Length is 0 or >= 30)
        {
            throw new ArgumentException(
                "DFLegacy mail senders must contain 1-29 encoded bytes.",
                nameof(mail));
        }

        if (mail.TextBytes.Length >= 512)
        {
            throw new ArgumentException(
                "DFLegacy mail text must contain at most 511 encoded bytes.",
                nameof(mail));
        }

        if (mail.Attachment is { ItemId: 0 })
        {
            throw new ArgumentException(
                "Mail attachments must have a non-zero item id.",
                nameof(mail));
        }

        if (mail.State is not (1 or 2 or 3))
        {
            throw new ArgumentOutOfRangeException(
                nameof(mail),
                "DFLegacy mail states must be received, read or archived.");
        }
    }
}
