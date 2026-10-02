using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

/// <summary>
/// CHANGE_HOST (CMD 131): thin shell delegating to PartyHandlerCore (07 §4.6
/// 族接管). 60A1 has no reply case for this command (P-35): the transfer is
/// observed through the NOTI 26 host-slot broadcast and the NOTI 9 roster.
/// </summary>
public sealed class TransferLeaderHandler(PartyHandlerCore core) : IGameCommandHandler
{
    public ushort ProtocolId => (byte)CmdPacketType.CHANGE_HOST;

    public string Name => "ChangeHost";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken) =>
        core.HandleAsync(context, request, cancellationToken);
}
