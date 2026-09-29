using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// ExchangeServerCharacterInfoCommand (CMD 130): the legacy fall-through
/// answered with the canned exchange-server character-info reply (07 §5
/// group 14); the handler keeps those exact bytes.
/// </summary>
public sealed class ExchangeServerCharacterInfoHandler : IGameCommandHandler
{
    public ushort ProtocolId => GameProtocolEngine.ExchangeServerCharacterInfoCommand;

    public string Name => "EXCHANGE_SERVER_CHARACTER_INFO";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var reply = CharactersPacketBuilders.CreateExchangeServerCharacterInfoReply();
        await context.Outbound.SendAsync(reply, Name, cancellationToken);
    }
}
