using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

/// <summary>
/// Cross-session notification hooks of <see cref="SelectCharacterHandler"/>,
/// delegating to the shared safe senders on <see cref="ChannelSessionSenders"/>.
/// </summary>
public sealed partial class SelectCharacterHandler
{
    private Task SendTypedMessageNotificationSafeAsync(
        ConnectionContext context,
        GameMessageType messageType,
        ushort targetAreaUserId,
        byte[] messageBytes) =>
        senders.SendTypedMessageNotificationSafeAsync(context, messageType, targetAreaUserId, messageBytes);

    private Task SendMegaphoneNotificationSafeAsync(
        ConnectionContext context,
        GameMessageType messageType,
        byte ownerFlag,
        ushort targetAreaUserId,
        byte[] senderName,
        byte[] messageBytes) =>
        senders.SendMegaphoneNotificationSafeAsync(context, messageType, ownerFlag, targetAreaUserId, senderName, messageBytes);

    private Task SendPopupNotificationSafeAsync(
        ConnectionContext context,
        byte[] messageBytes) =>
        senders.SendPopupNotificationSafeAsync(context, messageBytes);

    private Task SendWeaknessRecoveryNotificationSafeAsync(
        ConnectionContext context,
        byte recovery) =>
        senders.SendWeaknessRecoveryNotificationSafeAsync(context, recovery);

    private Task SendDungeonPermissionsSafeAsync(ConnectionContext context) =>
        senders.SendDungeonPermissionsSafeAsync(context);

    private Task SendAcceptableQuestListSafeAsync(ConnectionContext context) =>
        senders.SendAcceptableQuestListSafeAsync(context);

    /// <summary>
    /// 后台批量完成（含奖励结算）采纳后的在线刷新：背包整包、仓库容量、
    /// 升级时的技能与权威属性、可接任务列表。跨会话推送，吞掉断线竞态。
    /// </summary>
    private async Task AdoptAdminCharacterProgressSafeAsync(
        ConnectionContext context,
        bool leveledUp,
        bool warehouseCapacityChanged)
    {
        try
        {
            var session = context.State;
            if (session.Character.Active is null)
            {
                return;
            }

            var inventoryRefresh = builders.CreateMainInventoryPacket(
                session.Currency.Gold,
                session.Currency.VictoryPoints,
                session.Inventory.Main.Values);
            await context.Queue.SendAsync(
                inventoryRefresh,
                "ADMIN_QUEST_REWARD",
                context.Disconnecting);
            if (warehouseCapacityChanged)
            {
                await senders.SendWarehouseStateAsync(context, context.Disconnecting);
            }

            if (leveledUp)
            {
                await senders.SendCurrentSkillInfoAsync(context, context.Disconnecting);
                await senders.ValidateAndCorrectCharacterStatsAsync(
                    context,
                    "admin quest reward settlement",
                    context.Disconnecting);
            }

            await senders.SendAcceptableQuestListAsync(context, context.Disconnecting);
        }
        catch (OperationCanceledException) when (context.Disconnecting.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            RuntimeLogger.Warn(
                "Could not push the admin quest reward settlement to the online character: {Error}",
                exception.Message);
        }
    }
}
