using System.Buffers.Binary;
using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

/// <summary>
/// GIVEUP_GAME (CMD 45): answers the in-dungeon Return to Town button — either
/// by giving up a dungeon run or, while the newbie tutorial is still pending,
/// by leaving that tutorial (07 §5 group 13).
/// </summary>
public sealed class GiveupGameHandler(
    IGameStore store,
    ChannelSessionSenders senders) : IGameCommandHandler
{
    public ushort ProtocolId => (byte)CmdPacketType.GIVEUP_GAME;

    public string Name => "GIVEUP_GAME";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;

        // 新手教程未完成时,CMD 45 是"菜单退出新手教程"而不是"放弃副本":参考服
        // 日志同形(「已返回城镇（菜单退出新手教程）…新手教程已退出并持久化」),
        // 客户端在教程副本里点"返回城镇"必须能回城。此前这里直接回错误码 4,该
        // 按钮因而毫无反应(2026-10-10 抓包 [0,4] + 参考服日志复核,见 PENDING P-46)。
        if (session.Character.Active is { TutorialStarted: false } tutorialPending)
        {
            var completedTutorial = await store.CompleteCharacterTutorialAsync(
                session.Login.AccountName,
                tutorialPending.Id,
                cancellationToken: cancellationToken);
            if (completedTutorial is not null)
            {
                session.Character.Active = completedTutorial;
                RuntimeLogger.Info(
                    "Exited the newbie tutorial on the return-to-town request for character {CharacterId}.",
                    completedTutorial.Id);
            }
        }

        // 参考服同形:先回 [1](客户端 case 45 成功分支据此 refresh 副本选择态,
        // 见 sub_8772D0),再推城镇初始化流。
        var acknowledged = ConnectionPacketBuilders.CreateCommandReply(
            request,
            success: true);
        await context.Queue.SendAsync(acknowledged, Name, cancellationToken);

        // The in-dungeon Return to Town button uses command
        // 45 in DNF 1.0.1.9. It does not use EPLP (75),
        // which is reserved for the post-settlement screen.
        await senders.ReturnToTownFromDungeonAsync(context,
            "giveup-game",
            forcedDungeonExit: true);
    }
}
