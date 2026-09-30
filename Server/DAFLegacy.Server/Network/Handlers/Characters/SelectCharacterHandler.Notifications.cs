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
}
