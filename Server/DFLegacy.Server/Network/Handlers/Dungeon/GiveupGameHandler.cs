using System.Buffers.Binary;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// GIVEUP_GAME (CMD 45): gives up the dungeon run and returns to town (07 §5 group 13).
/// </summary>
public sealed class GiveupGameHandler(
    ChannelSessionSenders senders) : IGameCommandHandler
{
    public ushort ProtocolId => (byte)CmdPacketType.GIVEUP_GAME;

    public string Name => "GIVEUP_GAME";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        if (session.Character.Active is { TutorialStarted: false })
        {
            var tutorialError = ConnectionPacketBuilders.CreateCommandError(
                request,
                errorCode: 4);
            await context.Queue.SendAsync(tutorialError, Name, cancellationToken);
            RuntimeLogger.Info(
                "Rejected tutorial give-up for character {CharacterId}.",
                session.Character.Active.Id);
            return;
        }

        // The in-dungeon Return to Town button uses command
        // 45 in DNF 1.0.1.9. It does not use EPLP (75),
        // which is reserved for the post-settlement screen.
        await senders.ReturnToTownFromDungeonAsync(context, 
            "giveup-game",
            forcedDungeonExit: true);
    }
}
