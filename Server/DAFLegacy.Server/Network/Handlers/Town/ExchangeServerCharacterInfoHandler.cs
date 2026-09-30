using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

/// <summary>
/// CmdPacketType.REQUEST_BLACKLIST (CMD 130): the legacy fall-through
/// answered with the canned exchange-server character-info reply (07 §5
/// group 14); the handler keeps those exact bytes.
/// </summary>
public sealed class ExchangeServerCharacterInfoHandler : IGameCommandHandler
{
    public ushort ProtocolId => (byte)CmdPacketType.REQUEST_BLACKLIST;

    public string Name => "EXCHANGE_SERVER_CHARACTER_INFO";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var reply = CharactersPacketBuilders.CreateExchangeServerCharacterInfoReply();
        await context.Queue.SendAsync(reply, Name, cancellationToken);
    }
}
