using System.Buffers.Binary;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// SCORE_SCROLL_STATE (CMD 72): dungeon score-scroll display state (07 §5 group 13).
/// </summary>
public sealed class ScoreScrollStateHandler(
    ChannelSessionSenders senders) : IGameCommandHandler
{
    public ushort ProtocolId => (byte)CmdPacketType.SCORE_SCROLL_STATE;

    public string Name => "SCORE_SCROLL_STATE";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        if (session.Dungeon.TutorialActive)
        {
            if (session.Dungeon.ClearEnabled && session.Dungeon.AliveMonsters.Count == 0)
            {
                await senders.CompleteTutorialAndReturnAsync(context,
                    "tutorial-score-scroll-fallback");
            }
            else
            {
                RuntimeLogger.Info(
                    "Ignored tutorial score-scroll command before the tutorial room was cleared.");
            }
            return;
        }

        if (session.Dungeon.ResultSent && !session.Dungeon.CardLayoutSent)
        {
            session.Dungeon.CardLayoutSent = true;
            var scoreScrollReply =
                DungeonPacketBuilders.CreateScoreScrollStateReply();
            await context.Queue.SendAsync(scoreScrollReply, Name, cancellationToken);
            RuntimeLogger.Info(
                "Dungeon {DungeonId} score scroll acknowledged for user {UserId}",
                session.Dungeon.CurrentDungeonId,
                session.Character.LocalUserId);
        }
    }
}
