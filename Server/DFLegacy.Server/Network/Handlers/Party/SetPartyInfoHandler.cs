using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// SET_PARTY_INFO (CMD 12): thin shell delegating to PartyHandlerCore (07 §4.6 族接管).
/// </summary>
public sealed class SetPartyInfoHandler(PartyHandlerCore core) : IGameCommandHandler
{
    public ushort ProtocolId => (byte)CmdPacketType.SET_PARTY_INFO;

    public string Name => "SetPartyInfo";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken) =>
        core.HandleAsync(context, request, cancellationToken);
}
