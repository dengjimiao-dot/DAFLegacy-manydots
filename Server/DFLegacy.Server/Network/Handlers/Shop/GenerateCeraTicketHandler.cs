using System.Buffers.Binary;
using System.Text;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// GENERATE_CERA_TICKET (CMD 68): mints the Cera web ticket for the active character (07 §5 group 10).
/// </summary>
public sealed class GenerateCeraTicketHandler(ILogger<GenerateCeraTicketHandler> logger) : IGameCommandHandler
{

    private static readonly Encoding ClientEncoding = PvfEncodings.Cp936Lossy();

    public ushort ProtocolId => (byte)CmdPacketType.GEN_CERATICKET;

    public string Name => "GenerateCeraTicket";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        var ticket = Guid.NewGuid().ToString("N");
        var ticketReply = ShopPacketBuilders.CreateCeraTicketReply(
            ticket);
        await context.Outbound.SendAsync(ticketReply, Name, cancellationToken);
        logger.LogInformation(
            "Generated a DFLegacy Cera web ticket for character {CharacterId}.",
            session.Character.Active?.Id);
    }
}
