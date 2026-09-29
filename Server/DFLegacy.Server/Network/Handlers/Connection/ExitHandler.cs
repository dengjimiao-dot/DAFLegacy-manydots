using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// EXIT (CMD 3): the 1.0.1.9 client clears a possible old game session
/// before it begins the selected-channel login flow. The legacy chain
/// delegated this protocol to <see cref="FallbackCommandReplies.TryCreateReply"/>,
/// which answers a one-byte true result and requires the body to carry at
/// least the record-count byte plus one payload byte — shorter bodies fell
/// through to the "No channel handler" warning. Both behaviors are kept.
/// </summary>
public sealed class ExitHandler(ILogger<ExitHandler> logger) : IGameCommandHandler
{
    public ushort ProtocolId => (byte)CmdPacketType.EXIT;

    public string Name => "EXIT";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(
        ConnectionContext context,
        PacketFrame frame,
        CancellationToken cancellationToken)
    {
        if (frame.Body.Length < sizeof(ushort))
        {
            logger.LogWarning(
                "No channel handler for packet type {Type}, protocol {ProtocolId}; frame retained in logs.",
                frame.Type,
                frame.ProtocolId);
            return;
        }

        var reply = ConnectionPacketBuilders.CreateCommandReply(frame, success: true);
        await context.Outbound.SendAsync(reply, Name, cancellationToken);
    }
}
