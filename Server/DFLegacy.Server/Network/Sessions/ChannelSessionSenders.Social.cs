using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// Social senders (07 §4.7): PVP record sync and the fire-and-forget
/// cross-session chat notifications (whisper echo, megaphone, popup).
/// </summary>
public sealed partial class ChannelSessionSenders
{
    public async Task SendPvpRecordAsync(ConnectionContext context, CancellationToken cancellationToken)
    {
        if (context.State.Character.Active is null)
        {
            return;
        }

        // PvpPoints is the source of truth. Older state files may
        // have persisted the pre-boundary grade (for example,
        // 2,000 points as grade 1), so derive the wire rank from
        // the cumulative PVF thresholds on every full sync.
        var grade = pvpExperienceCatalog.GetGradeForPoints(
            context.State.Character.Active.PvpPoints);
        var pvpRecord = GameProtocolEngine.CreatePvpRecord(
            context.State.Character.Active.PvpWins,
            context.State.Character.Active.PvpLosses,
            context.State.Character.Active.PvpPoints,
            pvpExperienceCatalog.GetCurrentRankPoint(grade),
            pvpExperienceCatalog.GetNextRankPoint(grade),
            grade,
            context.State.Character.Active.PvpGradeExtension);
        await context.Outbound.SendAsync(pvpRecord, "PVP_RECORD", cancellationToken);

        var refreshedQuests = context.State.Quest.Current.Select(quest =>
            questCatalog.TryGetDefinition(quest.QuestId, out var definition)
                && QuestPvpRankRequirement.IsPvpRank(definition)
                ? quest with { Trigger = QuestPvpRankRequirement.GetTrigger(definition, grade) }
                : quest).ToList();
        var changedQuests = refreshedQuests.Where((quest, index) =>
            quest.Trigger != context.State.Quest.Current[index].Trigger).ToArray();
        if (changedQuests.Length == 0)
        {
            return;
        }

        var saved = await store.SaveCharacterQuestsAsync(
            context.State.Login.AccountName, context.State.Character.Active.Id, refreshedQuests, context.State.Quest.CompletedIds, cancellationToken);
        if (saved is null)
        {
            logger.LogWarning("Could not persist PvP-rank quest progress for {CharacterId}.", context.State.Character.Active.Id);
            return;
        }
        context.State.Quest.Current = refreshedQuests;
        context.State.Character.Active = context.State.Character.Active with { Quests = saved.Quests };
        foreach (var quest in changedQuests)
        {
            var update = GameProtocolEngine.CreateSetQuestTriggerReply(quest.QuestId, quest.Trigger);
            await context.Outbound.SendAsync(update, "QUEST_TRIGGER", cancellationToken);
        }
    }

    // ---- Fire-and-forget cross-session notification hooks. Legacy
    // semantics kept: failures are swallowed so a push never breaks the
    // pushing session; frames still go through the target connection's
    // single-writer pump (07 §4.4).

    public async Task SendTypedMessageNotificationSafeAsync(
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

    public async Task SendMegaphoneNotificationSafeAsync(
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

    public async Task SendPopupNotificationSafeAsync(
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
}
