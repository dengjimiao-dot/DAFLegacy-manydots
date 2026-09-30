using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// Mailbox senders (07 §4.7): mailbox snapshot and the new-mail alarm.
/// </summary>
public sealed partial class ChannelSessionSenders
{
    public async Task<CharacterMailRecord[]> SendMailboxStateAsync(
        ConnectionContext context,
        bool notifyNewMail,
        CancellationToken cancellationToken)
    {
        if (context.State.Character.Active is null)
        {
            return [];
        }

        var mails = await store.GetCharacterMailsAsync(
            context.State.Character.Active.Id,
            cancellationToken);
        context.State.Mailbox.KnownIds.Clear();
        context.State.Mailbox.KnownIds.UnionWith(mails.Select(mail => mail.Id));
        var mailboxPackets = MailboxPacketBuilders.CreateMailboxSnapshot(
            mails.Select(builders.ToGameMailEntry).ToArray());
        foreach (var packet in mailboxPackets)
        {
            await context.Queue.SendAsync(packet, "MAILBOX_STATE", cancellationToken);
        }

        if (notifyNewMail)
        {
            await SendNewMailAlarmAsync(context, mails, cancellationToken);
        }

        return mails;
    }

    public async Task SendNewMailAlarmAsync(
        ConnectionContext context,
        IReadOnlyCollection<CharacterMailRecord>? knownMails = null,
        CancellationToken cancellationToken = default)
    {
        if (context.State.Character.Active is null)
        {
            return;
        }

        var mails = knownMails
            ?? await store.GetCharacterMailsAsync(context.State.Character.Active.Id, cancellationToken);
        var newMailCount = mails.Count(mail => mail.IsNew);
        if (newMailCount == 0)
        {
            return;
        }

        var alarm = MailboxPacketBuilders.CreateMailboxAlarm(
            checked((short)Math.Min(newMailCount, short.MaxValue)));
        await context.Queue.SendAsync(alarm, "MAILBOX_ALARM", cancellationToken);
        await store.AcknowledgeCharacterMailNotificationsAsync(
            context.State.Character.Active.Id,
            cancellationToken);
    }
}
