using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// LEAVE_PARTY (CMD 13): thin shell delegating to PartyHandlerCore (07 §4.6 族接管).
/// </summary>
public sealed class LeavePartyHandler(PartyHandlerCore core) : IGameCommandHandler
{
    public ushort ProtocolId => GameProtocolEngine.LeavePartyCommand;

    public string Name => "LeaveParty";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken) =>
        core.HandleAsync(context, request, cancellationToken);
}
