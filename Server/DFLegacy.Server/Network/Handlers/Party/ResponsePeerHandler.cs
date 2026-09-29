using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// RESPONSE_PEER (CMD 11): thin shell delegating to PartyHandlerCore (07 §4.6 族接管).
/// </summary>
public sealed class ResponsePeerHandler(PartyHandlerCore core) : IGameCommandHandler
{
    public ushort ProtocolId => GameProtocolEngine.ResponsePeerCommand;

    public string Name => "ResponsePeer";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken) =>
        core.HandleAsync(context, request, cancellationToken);
}
