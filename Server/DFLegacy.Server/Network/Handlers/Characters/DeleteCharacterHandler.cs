using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// DELETE_CHARACTER (CMD 6): deletes the character in the requested slot.
/// Success carries the deleted slot after the common result byte — the
/// client uses that slot to remove the matching character card — and a
/// fresh USERINFO roster follows.
/// </summary>
public sealed class DeleteCharacterHandler(
    JsonGameStore store,
    CharacterPacketBuilders builders) : IGameCommandHandler
{
    public ushort ProtocolId => (byte)CmdPacketType.DELETE_CHARACTER;

    public string Name => "DELETE_CHARACTER";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(
        ConnectionContext context,
        PacketFrame frame,
        CancellationToken cancellationToken)
    {
        var deleted = false;
        var slot = (byte)0;
        if (CharacterParsers.TryReadDeleteCharacter(frame.Body, out slot))
        {
            var delete = await store.DeleteCharacterAsync(
                context.State.Login.AccountUid,
                slot,
                cancellationToken);
            deleted = delete.Deleted;
        }

        var deleteReply = deleted
            ? CharactersPacketBuilders.CreateDeleteCharacterReply(slot)
            : ConnectionPacketBuilders.CreateCommandError(frame, errorCode: 2);
        await context.Queue.SendAsync(deleteReply, Name, cancellationToken);

        if (deleted)
        {
            var userInfo = await builders.CreateUserInfoAsync(
                context.State.Login.AccountUid,
                cancellationToken);
            await context.Queue.SendAsync(userInfo, Name, cancellationToken);
        }
    }
}
