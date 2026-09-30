using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

/// <summary>
/// GET_USERINFO (CMD 8): pushes the account's character roster. The select
/// screen requests it after login, after create/delete and after returning
/// from the town menu.
/// </summary>
public sealed class GetUserInfoHandler(CharacterPacketBuilders builders) : IGameCommandHandler
{
    public ushort ProtocolId => (byte)CmdPacketType.GET_USERINFO;

    public string Name => "GET_USERINFO";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(
        ConnectionContext context,
        PacketFrame frame,
        CancellationToken cancellationToken)
    {
        var userInfo = await builders.CreateUserInfoAsync(
            context.State.Login.AccountUid,
            cancellationToken);
        await context.Queue.SendAsync(userInfo, Name, cancellationToken);
    }
}
