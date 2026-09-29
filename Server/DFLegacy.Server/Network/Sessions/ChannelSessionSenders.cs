using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// Session-state notification senders and coarse state readers shared across
/// handler groups (07 §4.7/§4.8.2: cross-domain flows keep one canonical
/// implementation). Migrated verbatim from the former HandleClientAsync
/// local functions; sends go through <see cref="IOutboundQueue"/> so every
/// frame passes the single-writer pump (07 §4.4). The per-group builder
/// split (Mailbox/Dungeon builders) happens when those groups migrate.
/// </summary>
public sealed class ChannelSessionSenders(
    JsonGameStore store,
    ItemCatalog itemCatalog,
    QuestCatalog questCatalog,
    DungeonCatalog dungeonCatalog,
    PvpExperienceCatalog pvpExperienceCatalog,
    CharacterPacketBuilders builders,
    ILogger<ChannelSessionSenders> logger)
{
    public GameSkillEntry[] GetCurrentGameSkills(ConnectionContext context) => context.State.Skill.Current
        .OrderBy(skill => skill.Slot)
        .Select(skill => new GameSkillEntry(skill.SkillId, skill.Level, skill.Slot))
        .ToArray();

    public bool HasActivePremiumService(ConnectionContext context, byte serviceType) =>
        context.State.Premium.ExpiryUnix.TryGetValue(serviceType, out var expiryUnix)
        && expiryUnix > DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public byte[] GetActivePremiumServiceTypes(ConnectionContext context)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return context.State.Premium.ExpiryUnix
            .Where(entry => entry.Value > now)
            .Select(entry => entry.Key)
            .ToArray();
    }

    public byte GetProjectedStaminaRecovery(ConnectionContext context)
    {
        if (context.State.Character.Active is null
            || !context.State.Character.Active.IsWeakened
            || HasActivePremiumService(
                context,
                GameProtocolEngine.BlackDiamondServiceType))
        {
            return DungeonWeaknessPolicy.FullStamina;
        }

        return (byte)Math.Clamp(
            context.State.Character.Active.WeaknessRecovery,
            DungeonWeaknessPolicy.MinimumRecovery,
            DungeonWeaknessPolicy.FullStamina);
    }

    public IReadOnlyList<ushort> GetAcceptableQuestIds(ConnectionContext context) => context.State.Character.Active is null
        ? []
        : questCatalog.GetAcceptableQuestIds(
            context.State.Character.Active,
            context.State.Quest.Current,
            context.State.Quest.CompletedIds,
            context.State.Character.TownId == 1 ? 2 : null,
            itemCatalog);

    public IReadOnlyList<ushort> GetVisibleDungeonIds(ConnectionContext context) =>
        HiddenDungeonUnlockCatalog.ResolveVisibleDungeonIds(
            dungeonCatalog.DungeonIds,
            context.State.Character.Active?.UnlockedDungeonIds)
        .Where(dungeonId => dungeonId != GameProtocolEngine.TutorialDungeonId)
        .ToArray();

    public IReadOnlyList<GameDungeonPermissionEntry> GetDungeonPermissions(ConnectionContext context)
    {
        var permissions = GetVisibleDungeonIds(context)
            .Select(dungeonId => new GameDungeonPermissionEntry(
                dungeonId,
                DungeonDifficultyProgression.GetMaximumDifficulty(
                    context.State.Character.Active?.DungeonProgress,
                dungeonId)))
            .ToList();
        if (context.State.Character.Active is { TutorialStarted: false }
            && permissions.All(permission =>
                permission.DungeonId != GameProtocolEngine.TutorialDungeonId))
        {
            permissions.Add(new GameDungeonPermissionEntry(
                GameProtocolEngine.TutorialDungeonId,
                ClearState: 0));
        }

        return permissions;
    }

    public async Task SendDungeonPermissionsAsync(ConnectionContext context, CancellationToken cancellationToken)
    {
        var permissions = GameProtocolEngine.CreateDungeonPermissions(
            GetDungeonPermissions(context));
        await context.Outbound.SendAsync(permissions, "DUNGEON_PERMISSIONS", cancellationToken);
    }

    /// <summary>
    /// Settles warehouse-capacity upgrades against the current session
    /// capacity; the former PlanWarehouseUpgradeSettlement local function.
    /// </summary>
    public CharacterWarehouseUpgradeItemSettlement PlanWarehouseUpgradeSettlement(
        ConnectionContext context,
        IEnumerable<CharacterItemRecord> inventory,
        IEnumerable<CharacterItemRecord> warehouse,
        IEnumerable<ushort>? acquiredItemIds = null)
    {
        var settlement = CharacterWarehouseProgression.SettleUpgradeItems(
            inventory,
            warehouse,
            context.State.Inventory.WarehouseCapacity);
        var targetCapacity = settlement.Capacity;
        foreach (var itemId in acquiredItemIds ?? [])
        {
            targetCapacity = CharacterWarehouseProgression.ApplyItemTarget(
                targetCapacity,
                itemId);
        }

        return targetCapacity == settlement.Capacity
            ? settlement
            : settlement with { Capacity = targetCapacity };
    }

    public async Task<CharacterMailRecord[]> SendMailboxStateAsync(
        ConnectionContext context,
        bool notifyNewMail,
        CancellationToken cancellationToken)
    {
        if (context.State.Character.Active is null)
        {
            return [];
        }

        var mails = await store.GetCharacterMailsAsync(
            context.State.Character.Active.Id,
            cancellationToken);
        context.State.Mailbox.KnownIds.Clear();
        context.State.Mailbox.KnownIds.UnionWith(mails.Select(mail => mail.Id));
        var mailboxPackets = GameProtocolEngine.CreateMailboxSnapshot(
            mails.Select(builders.ToGameMailEntry).ToArray());
        foreach (var packet in mailboxPackets)
        {
            await context.Outbound.SendAsync(packet, "MAILBOX_STATE", cancellationToken);
        }

        if (notifyNewMail)
        {
            await SendNewMailAlarmAsync(context, mails, cancellationToken);
        }

        return mails;
    }

    public async Task SendNewMailAlarmAsync(
        ConnectionContext context,
        IReadOnlyCollection<CharacterMailRecord>? knownMails = null,
        CancellationToken cancellationToken = default)
    {
        if (context.State.Character.Active is null)
        {
            return;
        }

        var mails = knownMails
            ?? await store.GetCharacterMailsAsync(context.State.Character.Active.Id, cancellationToken);
        var newMailCount = mails.Count(mail => mail.IsNew);
        if (newMailCount == 0)
        {
            return;
        }

        var alarm = GameProtocolEngine.CreateMailboxAlarm(
            checked((short)Math.Min(newMailCount, short.MaxValue)));
        await context.Outbound.SendAsync(alarm, "MAILBOX_ALARM", cancellationToken);
        await store.AcknowledgeCharacterMailNotificationsAsync(
            context.State.Character.Active.Id,
            cancellationToken);
    }

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
}
