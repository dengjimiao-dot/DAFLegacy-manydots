using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// Cross-session notification hooks of <see cref="SelectCharacterHandler"/>
/// (07 §4.7: split per sub-flow to keep each file small).
/// </summary>
public sealed partial class SelectCharacterHandler
{
    // ---- Cross-session notification hooks (the CharacterSessionRegistry
    // lease callbacks). Legacy semantics kept: failures are swallowed so a
    // cross-session push never breaks the pushing session; frames still go
    // through the target connection's single-writer pump (07 §4.4).

    private async Task SendTypedMessageNotificationSafeAsync(
        ConnectionContext context,
        GameMessageType messageType,
        ushort targetAreaUserId,
        byte[] messageBytes)
    {
        try
        {
            if (context.State.Character.Active is null)
            {
                return;
            }

            var messageNotification = GameProtocolEngine.CreateMessageNotification(
                messageType,
                targetAreaUserId,
                messageBytes);
            await context.Outbound.SendAsync(messageNotification, "MESSAGE_NOTI", context.Disconnecting);
        }
        catch (OperationCanceledException) when (context.Disconnecting.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Could not send message notification to character {CharacterId}.",
                context.State.Character.Active?.Id);
        }
    }

    private async Task SendMegaphoneNotificationSafeAsync(
        ConnectionContext context,
        GameMessageType messageType,
        byte ownerFlag,
        ushort targetAreaUserId,
        byte[] senderName,
        byte[] messageBytes)
    {
        try
        {
            if (context.State.Character.Active is null)
            {
                return;
            }

            var megaphoneNotification = GameProtocolEngine.CreateMegaphoneNotification(
                messageType,
                ownerFlag,
                targetAreaUserId,
                senderName,
                messageBytes);
            await context.Outbound.SendAsync(megaphoneNotification, "MEGAPHONE_NOTI", context.Disconnecting);
        }
        catch (OperationCanceledException) when (context.Disconnecting.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Could not send megaphone notification to character {CharacterId}.",
                context.State.Character.Active?.Id);
        }
    }

    private async Task SendPopupNotificationSafeAsync(
        ConnectionContext context,
        byte[] messageBytes)
    {
        try
        {
            if (context.State.Character.Active is null)
            {
                return;
            }

            var popupNotification = GameProtocolEngine.CreatePopupNotification(messageBytes);
            await context.Outbound.SendAsync(popupNotification, "POPUP_NOTI", context.Disconnecting);
        }
        catch (OperationCanceledException) when (context.Disconnecting.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Could not send popup notification to character {CharacterId}.",
                context.State.Character.Active?.Id);
        }
    }

    private async Task SendWeaknessRecoveryNotificationSafeAsync(
        ConnectionContext context,
        byte recovery)
    {
        try
        {
            if (context.State.Character.Active is null)
            {
                return;
            }

            var stamina = GameProtocolEngine.CreateStamina(recovery);
            await context.Outbound.SendAsync(stamina, "STAMINA_NOTI", context.Disconnecting);
        }
        catch (OperationCanceledException) when (context.Disconnecting.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Could not send weakness recovery notification to character {CharacterId}.",
                context.State.Character.Active?.Id);
        }
    }

    private async Task SendDungeonPermissionsSafeAsync(ConnectionContext context)
    {
        try
        {
            var permissions = GameProtocolEngine.CreateDungeonPermissions(
                senders.GetDungeonPermissions(context));
            await context.Outbound.SendAsync(permissions, "DUNGEON_PERMISSIONS", context.Disconnecting);
        }
        catch (OperationCanceledException) when (context.Disconnecting.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Could not send dungeon permissions to character {CharacterId}.",
                context.State.Character.Active?.Id);
        }
    }
}
