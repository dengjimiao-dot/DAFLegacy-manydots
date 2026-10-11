using System.Text;
using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

/// <summary>
/// CREATE_CHARACTER (CMD 5): creates the character and answers with the
/// command's generic one-byte result — a successful result closes the
/// creation panel and makes this client refresh USERINFO, so a fresh roster
/// follows the acknowledgement.
/// </summary>
public sealed class CreateCharacterHandler(
    IGameStore store,
    CharacterPacketBuilders builders) : IGameCommandHandler
{
    private static readonly Encoding ClientEncoding = PvfEncodings.Cp936Lossy();

    public ushort ProtocolId => (byte)CmdPacketType.CREATE_CHARACTER;

    public string Name => "CREATE_CHARACTER";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(
        ConnectionContext context,
        PacketFrame frame,
        CancellationToken cancellationToken)
    {
        // 客户端 CMD 5 失败分支只对错误码 4 与 0x12 走可见的消息通道
        // (sub_799EF0);2/0x13/0x17/0x18/0x5F 都经 .data 的弹窗槽 0xB55570,
        // 该槽在本客户端构建里初始化为空桩 retn,故这些码不会弹任何提示
        // (2026-10-10 IDA + 抓包复核,见 packet-detail-login-character-60cn.md
        // 与 PENDING P-45)。失败码由存储层按原因判定:重名/保留名 → 0x12
        // (串 683「含有屏蔽字或已存在的角色名」),名额已满 → 4(串 682
        // 「角色数量已达上限」),其余未知失败保持 2。
        var create = new CreateCharacterResult(false, null,
            Failure: CharacterCreationFailure.InvalidRequest);
        if (CharacterParsers.TryReadCreateCharacter(frame.Body, out var job, out var nameBytes))
        {
            create = await store.CreateCharacterAsync(
                context.State.Login.AccountUid,
                new CreateCharacterRequest(ClientEncoding.GetString(nameBytes), job, 0, 1),
                cancellationToken);
        }

        var createReply = CharacterCreationReply.Create(frame, create);
        await context.Queue.SendAsync(createReply, Name, cancellationToken);

        if (create.Created)
        {
            var userInfo = await builders.CreateUserInfoAsync(
                context.State.Login.AccountUid,
                cancellationToken);
            await context.Queue.SendAsync(userInfo, Name, cancellationToken);
        }
        else
        {
            RuntimeLogger.Info(
                "Rejected character creation for account {AccountUid}: failure={Failure}, reason={Reason}.",
                context.State.Login.AccountUid,
                create.Failure,
                create.Error);
        }
    }
}
