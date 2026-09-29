using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// CmdPacketType.CALL_MEMER_LIST (CMD 82): the legacy fall-through answered this
/// polling command with the canned private-store reply (07 §5 group 14);
/// the handler keeps those exact bytes.
/// </summary>
public sealed class PrivateStorePollHandler : IGameCommandHandler
{
    public ushort ProtocolId => (byte)CmdPacketType.CALL_MEMER_LIST;

    public string Name => "PRIVATE_STORE_POLL";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var reply = TownPacketBuilders.CreatePrivateStorePollReply();
        await context.Outbound.SendAsync(reply, Name, cancellationToken);
    }
}
