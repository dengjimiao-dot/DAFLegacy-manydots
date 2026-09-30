using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// SET_UDP_IP_PORT (CMD 2): the client reports its NAT-observed datagram
/// endpoint. The fresh endpoint is registered for the datagram services and
/// republished to party members immediately, or their client keeps the
/// "连接中" connecting status.
/// </summary>
public sealed class SetUdpEndpointHandler(
    GameplayDatagramService gameplayDatagram,
    PartyCoordinator partyCoordinator) : IGameCommandHandler
{
    public ushort ProtocolId => (byte)CmdPacketType.SET_UDP_IP_PORT;

    public string Name => "SET_UDP_IP_PORT";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(
        ConnectionContext context,
        PacketFrame frame,
        CancellationToken cancellationToken)
    {
        var parsed = GameDatagramProtocol.TryParseNatReport(
            frame.Body,
            out var natReport);
        if (parsed)
        {
            gameplayDatagram.SetNatReport(
                context.Runtime.Id,
                natReport);
            partyCoordinator.RefreshPartyEndpoints(context.Runtime.Id);
        }

        var endpointReply = parsed
            ? ConnectionPacketBuilders.CreateCommandReply(frame, success: true)
            : ConnectionPacketBuilders.CreateCommandError(frame, errorCode: 2);
        await context.Queue.SendAsync(endpointReply, Name, cancellationToken);
    }
}
