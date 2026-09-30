using System.Buffers.Binary;
using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

/// <summary>
/// ALLDIE_MONSTER (CMD 148): clears all alive monsters of the room (07 §5 group 13).
/// </summary>
public sealed class AllDieMonsterHandler() : IGameCommandHandler
{
    public ushort ProtocolId => (byte)CmdPacketType.ALLDIE_MONSTER;

    public string Name => "ALLDIE_MONSTER";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        RuntimeLogger.Info(
            "Accepted DAFLegacy ALLDIE_MONSTER for dungeon {DungeonId}, room ({RoomX},{RoomY}); waiting for normal monster-death confirmations.",
            session.Dungeon.CurrentDungeonId,
            session.Dungeon.RoomX,
            session.Dungeon.RoomY);
    }
}
