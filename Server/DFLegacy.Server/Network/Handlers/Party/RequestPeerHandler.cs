using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// REQUEST_PEER (CMD 10): thin shell delegating to PartyHandlerCore (07 §4.6 族接管).
/// </summary>
public sealed class RequestPeerHandler(PartyHandlerCore core) : IGameCommandHandler
{
    public ushort ProtocolId => (byte)CmdPacketType.REQUEST_PEER;

    public string Name => "RequestPeer";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken) =>
        core.HandleAsync(context, request, cancellationToken);
}
