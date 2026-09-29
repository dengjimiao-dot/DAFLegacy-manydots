using System.Text;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// CREATE_CHARACTER (CMD 5): creates the character and answers with the
/// command's generic one-byte result — a successful result closes the
/// creation panel and makes this client refresh USERINFO, so a fresh roster
/// follows the acknowledgement.
/// </summary>
public sealed class CreateCharacterHandler(
    JsonGameStore store,
    CharacterPacketBuilders builders) : IGameCommandHandler
{
    private static readonly Encoding ClientEncoding = PvfEncodings.Cp936Lossy();

    public ushort ProtocolId => GameProtocolEngine.CreateCharacterCommand;

    public string Name => "CREATE_CHARACTER";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(
        ConnectionContext context,
        PacketFrame frame,
        CancellationToken cancellationToken)
    {
        var created = false;
        if (CharacterParsers.TryReadCreateCharacter(frame.Body, out var job, out var nameBytes))
        {
            var create = await store.CreateCharacterAsync(
                context.State.Login.AccountUid,
                new CreateCharacterRequest(ClientEncoding.GetString(nameBytes), job, 0, 1),
                cancellationToken);
            created = create.Created;
        }

        var createReply = created
            ? ConnectionPacketBuilders.CreateCommandReply(frame, success: true)
            : ConnectionPacketBuilders.CreateCommandError(frame, errorCode: 2);
        await context.Outbound.SendAsync(createReply, Name, cancellationToken);

        if (created)
        {
            var userInfo = await builders.CreateUserInfoAsync(
                context.State.Login.AccountUid,
                cancellationToken);
            await context.Outbound.SendAsync(userInfo, Name, cancellationToken);
        }
    }
}
