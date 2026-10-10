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
    IGameStore store,
    ServerOptions options
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

        // 未注册账号：按策略用提交的账号/口令直接建号并继续登录（协议面不新增字段，
        // 见 ServerOptions.AccountOptions）。只有带口令的登录请求（登录表单形态）才
        // 建号；仅账号名的旧短包形态仍按原逻辑回登录失败。
        var accountCreated = false;
        if (authenticated is null
            && loginPassword is not null
            && options.Accounts.CreateMissingAccountsOnLogin)
        {
            authenticated = await store.TryCreateLoginAccountAsync(
                loginName,
                loginPassword,
                cancellationToken);
            accountCreated = authenticated is not null;
            if (accountCreated)
            {
                RuntimeLogger.Info(
                    "Created account {AccountName} on login (uid {AccountUid}).",
                    authenticated!.UserName,
                    authenticated.AccountUid);
            }
            else
            {
                RuntimeLogger.Warn(
                    "Could not create account {AccountName} on login; the name or password did not satisfy the login-time rules.",
                    loginName);
            }
        }

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
