using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// WALKOUT_PARTY_MEMBER (CMD 14): thin shell delegating to PartyHandlerCore (07 §4.6 族接管).
/// </summary>
public sealed class WalkoutPartyMemberHandler(PartyHandlerCore core) : IGameCommandHandler
{
    public ushort ProtocolId => GameProtocolEngine.WalkoutPartyMemberCommand;

    public string Name => "WalkoutPartyMember";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken) =>
        core.HandleAsync(context, request, cancellationToken);
}
