using System.Buffers.Binary;
using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

/// <summary>
/// DIE_CHARACTER (CMD 43): dungeon death with the return-to-town countdown (07 §5 group 13).
/// </summary>
public sealed class DieCharacterHandler() : IGameCommandHandler
{
    public ushort ProtocolId => (byte)CmdPacketType.DIE_CHARACTER;

    public string Name => "DIE_CHARACTER";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        if (session.Dungeon.CurrentDungeonId == 0
            || session.Character.Active is null
            || !session.Dungeon.CharacterAlive)
        {
            var error = ConnectionPacketBuilders.CreateCommandError(
                request,
                errorCode: 19);
            await context.Queue.SendAsync(error, Name, cancellationToken);
            RuntimeLogger.Warn(
                "Rejected DIE_CHARACTER for user {UserId}: dungeon={DungeonId}, hasCharacter={HasCharacter}, alive={Alive}.",
                session.Character.LocalUserId,
                session.Dungeon.CurrentDungeonId,
                session.Character.Active is not null,
                session.Dungeon.CharacterAlive);
            return;
        }

        session.Dungeon.CharacterAlive = false;
        session.Dungeon.DeathGeneration++;
        session.Dungeon.DeathTimeout = (
            session.Dungeon.DeathGeneration,
            DateTimeOffset.UtcNow.AddSeconds(10));
        var dieState = DungeonPacketBuilders.CreateDieState(
            session.Character.LocalUserId,
            isAlive: false);
        await context.Queue.SendAsync(dieState, Name, cancellationToken);
        RuntimeLogger.Info(
            "User {UserId} entered authoritative dungeon death state in dungeon {DungeonId} room ({RoomX},{RoomY}).",
            session.Character.LocalUserId,
            session.Dungeon.CurrentDungeonId,
            session.Dungeon.RoomX,
            session.Dungeon.RoomY);
    }
}
