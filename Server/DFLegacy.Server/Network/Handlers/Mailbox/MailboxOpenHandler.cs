using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// MAILBOX_OPEN (CMD 99): answers the open acknowledgement and pushes the current mailbox snapshot (07 §5 group 6).
/// </summary>
public sealed class MailboxOpenHandler(ChannelSessionSenders senders) : IGameCommandHandler
{
    public ushort ProtocolId => (byte)CmdPacketType.MAILBOX_OPEN;

    public string Name => "MAILBOXOPEN";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        if (session.Character.Active is null)
        {
            var error = ConnectionPacketBuilders.CreateCommandError(
                request,
                errorCode: 22);
            await context.Outbound.SendAsync(error, Name, cancellationToken);
            return;
        }

        var openReply = MailboxPacketBuilders.CreateMailboxOpenReply();
        await context.Outbound.SendAsync(openReply, Name, cancellationToken);
        await senders.SendMailboxStateAsync(context, notifyNewMail: true, cancellationToken);
    }
}
