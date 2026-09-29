using System.Buffers.Binary;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// EPLP (CMD 75): end-of-play result handshake (07 §5 group 13).
/// </summary>
public sealed class EplpHandler(    ILogger<EplpHandler> logger,
    ChannelSessionSenders senders) : IGameCommandHandler
{
    public ushort ProtocolId => (byte)CmdPacketType.EPLP_COMMAND;

    public string Name => "EPLP";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        if (session.Dungeon.TutorialActive)
        {
            logger.LogInformation(
                "Ignored normal EPLP settlement command during the tutorial.");
            return;
        }

        var state = request.Body.Length >= 3
            ? request.Body[2]
            : (byte)0;
        var option = request.Body.Length >= 4
            ? request.Body[3]
            : (byte)0;

        if (state == 1
            && session.Dungeon.CardRightsSent
            && session.Dungeon.ClearReward is not null
            && !session.Dungeon.FreeCardIndex.HasValue)
        {
            if (!await senders.TryAutomaticallySelectFreeCardAsync(context, 
                "eplp-fallback"))
            {
                logger.LogWarning(
                    "Deferred dungeon {DungeonId} EPLP completion because automatic free-card settlement failed for user {UserId}",
                    session.Dungeon.CurrentDungeonId,
                    session.Character.LocalUserId);
                return;
            }
        }

        var eplpReply =
            DungeonPacketBuilders.CreateEplpReply(state, option);
        await context.Outbound.SendAsync(eplpReply, Name, cancellationToken);

        if (state == 1)
        {
            var freeCardWasSelected = session.Dungeon.FreeCardIndex.HasValue;
            await senders.ReturnToTownFromDungeonAsync(context, 
                freeCardWasSelected
                    ? "settlement-after-card"
                    : "settlement-without-card");
        }
    }
}
