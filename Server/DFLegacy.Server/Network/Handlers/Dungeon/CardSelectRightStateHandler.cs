using System.Buffers.Binary;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// CARD_SELECT_RIGHT_STATE (CMD 73): clear-card ownership snapshot (07 §5 group 13).
/// </summary>
public sealed class CardSelectRightStateHandler(    ILogger<CardSelectRightStateHandler> logger) : IGameCommandHandler
{
    private static readonly TimeSpan ClearRewardCardSelectionTimeout =
        TimeSpan.FromSeconds(4);

    public ushort ProtocolId => GameProtocolEngine.CardSelectRightStateCommand;

    public string Name => "CARD_SELECT_RIGHT_STATE";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        if (session.Dungeon.CardLayoutSent && !session.Dungeon.CardRightsSent)
        {
            session.Dungeon.CardRightsSent = true;
            session.Dungeon.CardSelectionDeadline =
                DateTimeOffset.UtcNow
                + ClearRewardCardSelectionTimeout;
            var cardRights =
                DungeonPacketBuilders.CreateCardSelectRightState();
            await context.Outbound.SendAsync(cardRights, Name, cancellationToken);
            logger.LogInformation(
                "Dungeon {DungeonId} card selection rights sent for user {UserId}; automatic free-card selection in {TimeoutSeconds}s",
                session.Dungeon.CurrentDungeonId,
                session.Character.LocalUserId,
                ClearRewardCardSelectionTimeout.TotalSeconds);
        }
    }
}
