using System.Text;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// CHECK_CONNECTION (CMD 0): the client's channel keep-alive. Answers with
/// the CHANNELINFO notification (NOTI 1) carrying the channel display name,
/// ordinal, seed and datagram endpoints — the same bytes the legacy inline
/// branch produced (EntranceService fall-through, 07 §5 group 1).
/// </summary>
public sealed class CheckConnectionHandler(
    ServerOptions options,
    EntranceChannelDirectory channelDirectory) : IGameCommandHandler
{
    private static readonly Encoding ClientEncoding = PvfEncodings.Cp936Lossy();

    public ushort ProtocolId => (byte)CmdPacketType.CHECK_CONNECTION;

    public string Name => "CHECK_CONNECTION";

    // Zero-state keep-alive reachable before login; the smallest dispatch
    // gate sample (07 §4.8.1).
    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(
        ConnectionContext context,
        PacketFrame frame,
        CancellationToken cancellationToken)
    {
        var reply = ConnectionPacketBuilders.CreateChannelInfo(
            ClientEncoding.GetBytes(channelDirectory.Session.DisplayName),
            channelType: 1,
            result: 1,
            serverId: 1,
            channelNumber: checked((byte)channelDirectory.Session.Number),
            seed: checked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            [Encoding.ASCII.GetBytes(options.GameplayDatagram.Host)],
            port1: checked((uint)options.GameplayDatagram.PrimaryPort),
            port2: checked((uint)options.GameplayDatagram.SecondaryPort));
        await context.Queue.SendAsync(reply, Name, cancellationToken);
    }
}
