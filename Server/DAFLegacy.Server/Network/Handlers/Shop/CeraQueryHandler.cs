using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

/// <summary>
/// CERA (CMD 66): the cash-shop "refresh cera" button. The request carries no
/// fields beyond the sequence prefix; the answer is the same NOTI 53 push the
/// server already sends when the balance changes — the client reads it as
/// [u8 flag][u32 cera]. Never a command-type reply (07 §5 group 10).
/// </summary>
public sealed class CeraQueryHandler : IGameCommandHandler
{
    public ushort ProtocolId => (byte)CmdPacketType.CERA;

    public string Name => "CeraQuery";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(
        ConnectionContext context,
        PacketFrame request,
        CancellationToken cancellationToken)
    {
        var session = context.State;
        if (session.Character.Active is null)
        {
            // The client only sends this from the in-game cash UI; without a
            // selected character there is no balance to report.
            RuntimeLogger.Debug("Ignored cera refresh before character selection.");
            return;
        }

        var cera = session.Currency.Cash;
        var ceraNotification = ShopPacketBuilders.CreateCeraUpdate(
            success: true,
            value: checked((uint)Math.Max(0, cera)));
        await context.Queue.SendAsync(ceraNotification, Name, cancellationToken);
    }
}
