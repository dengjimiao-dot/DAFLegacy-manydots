using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// CHANGE_LETTER_STAT (CMD 144): deletes a mail or moves it between read/unread, synchronised with the session's known-id cache (07 §5 group 6).
/// </summary>
public sealed class MailboxStateHandler(JsonGameStore store) : IGameCommandHandler
{
    public ushort ProtocolId => (byte)CmdPacketType.CHANGE_LETTER_STAT;

    public string Name => "MAILBOXSTATE";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        if (session.Character.Active is null
            || !MailboxParsers.TryReadMailboxState(
                request.Body,
                out var mailId,
                out var mailState))
        {
            var error = ConnectionPacketBuilders.CreateCommandError(
                request,
                errorCode: 10);
            await context.Outbound.SendAsync(error, Name, cancellationToken);
            return;
        }

        var isKnownMail = session.Mailbox.KnownIds.Contains(mailId);
        if (mailState == 0)
        {
            var deleted = await store.DeleteCharacterMailAsync(
                session.Character.Active.Id,
                mailId,
                cancellationToken);
            if (!deleted && !isKnownMail)
            {
                var error = ConnectionPacketBuilders.CreateCommandError(
                    request,
                    errorCode: 10);
                await context.Outbound.SendAsync(error, Name, cancellationToken);
                return;
            }

            session.Mailbox.KnownIds.Remove(mailId);
        }
        else if (!isKnownMail
            || !await store.SetCharacterMailStateAsync(
                session.Character.Active.Id,
                mailId,
                mailState,
                cancellationToken))
        {
            var error = ConnectionPacketBuilders.CreateCommandError(
                request,
                errorCode: 10);
            await context.Outbound.SendAsync(error, Name, cancellationToken);
            return;
        }

        var stateReply = MailboxPacketBuilders.CreateMailboxStateReply(
            mailId,
            mailState);
        await context.Outbound.SendAsync(stateReply, Name, cancellationToken);
    }
}
