using System.Buffers.Binary;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// TUTORIAL_COMPLETION (CMD 153): tutorial dungeon completion hand-off (07 §5 group 13).
/// </summary>
public sealed class TutorialCompletionHandler(
    ChannelSessionSenders senders) : IGameCommandHandler
{
    public ushort ProtocolId => (byte)CmdPacketType.CHANGE_TUTORIAL_FLAG;

    public string Name => "TUTORIAL_COMPLETION";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        var tutorialModuleReply = ConnectionPacketBuilders.CreateCommandReply(
            request,
            success: true);
        await context.Outbound.SendAsync(tutorialModuleReply, Name, cancellationToken);

        if (session.Dungeon.TutorialActive
            && session.Dungeon.ClearEnabled
            && session.Dungeon.AliveMonsters.Count == 0
            && DungeonPacketBuilders.IsTutorialCompletionModule(request.Body))
        {
            await senders.CompleteTutorialAndReturnAsync(context, 
                "tutorial-final-module");
        }
    }
}
