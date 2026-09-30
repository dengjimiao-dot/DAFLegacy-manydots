using System.Buffers.Binary;
using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

/// <summary>
/// FINISH_LOADING (CMD 40): client finished loading the dungeon map (07 §5 group 13).
/// </summary>
public sealed class FinishLoadingHandler(
    ChannelSessionSenders senders) : IGameCommandHandler
{
    public ushort ProtocolId => (byte)CmdPacketType.FINISH_LOADING;

    public string Name => "FINISH_LOADING";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        var loadingReply = ConnectionPacketBuilders.CreateCommandReply(request, success: true);
        await context.Queue.SendAsync(loadingReply, Name, cancellationToken);

        // Command 40 tells the server that the local town
        // scene has finished loading. DNF 1.0.1.9 then
        // expects notification 30 to release its remaining
        // town-side managers. This build's handler consumes
        // no notification payload.
        var loadingNotification = DungeonPacketBuilders.CreateFinishLoading();
        await context.Queue.SendAsync(loadingNotification, Name, cancellationToken);
        // FinishLoading reconstructs the local map actor.
        // Replay a persisted zero-stomach death marker for
        // dungeon scenes even if this connection already
        // reported the same transition in an earlier map.
        await senders.SendEquippedCreatureHungerAsync(
            context,
            forceDeathNotification: session.Dungeon.CurrentDungeonId != 0);
    }
}
