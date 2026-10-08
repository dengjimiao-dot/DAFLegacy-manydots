using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

/// <summary>
/// LOGIN (CMD 1): authenticates the account (password path or the launcher's
/// passwordless find path) and binds the account identity to the session.
/// The success acknowledgement is the canned frame the legacy fall-through
/// produced via <see cref="FallbackCommandReplies.TryCreateReply"/>: one general success
/// byte, three account flags and one trailing client-state flag, all zeroes,
/// which selects the normal character-list path in this client build.
/// </summary>
public sealed class LoginHandler(
    IGameStore store
    ) : IGameCommandHandler
{
    public ushort ProtocolId => (byte)CmdPacketType.LOGIN;

    public string Name => "LOGIN";

    // Reachable before authentication by definition; the channel client logs
    // in right after CHECK_CONNECTION (07 §4.8.1 registration).
    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(
        ConnectionContext context,
        PacketFrame frame,
        CancellationToken cancellationToken)
    {
        if (!LoginParsers.TryReadLoginCredentials(
                frame.Body,
                out var loginName,
                out var loginPassword))
        {
            var malformedLoginError = ConnectionPacketBuilders.CreateCommandError(
                frame,
                errorCode: 2);
            await context.Queue.SendAsync(malformedLoginError, Name, cancellationToken);
            RuntimeLogger.Warn(
                "Rejected malformed login command for channel session {SessionId}.",
                context.Runtime.Id);
            return;
        }

        var authenticated = loginPassword is not null
            ? await store.AuthenticateAsync(
                loginName,
                loginPassword,
                cancellationToken)
            : await store.FindAccountAsync(loginName, cancellationToken);
        if (authenticated is null)
        {
            var loginError = ConnectionPacketBuilders.CreateCommandError(
                frame,
                errorCode: 2);
            await context.Queue.SendAsync(loginError, Name, cancellationToken);
            return;
        }

        context.State.Login.AccountName = authenticated.UserName;
        context.State.Login.AccountUid = authenticated.AccountUid;

        // 应答按客户端 CMD 1 case(0x40F2E0)的读取序给全：结果 + 4 标志 + 账号 +
        // 中继主机 dstr + 两个端口 + 2 尾字节（2026-10-08 IDA 复核）；缺字段会让
        // 客户端越包尾取残留值去配置其 TCP 中继通道。本服无中继，中继字段给惰性值。
        var reply = ConnectionPacketBuilders.CreateLoginOk(context.State.Login.AccountUid);
        await context.Queue.SendAsync(reply, Name, cancellationToken);
    }
}
