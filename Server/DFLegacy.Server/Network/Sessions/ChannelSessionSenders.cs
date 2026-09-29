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
    CharacterStatCatalog characterStatCatalog,
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

    public async Task SendWeaknessRecoveryNotificationSafeAsync(
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

    public async Task SendDungeonPermissionsSafeAsync(ConnectionContext context)
    {
        try
        {
            var permissions = GameProtocolEngine.CreateDungeonPermissions(
                GetDungeonPermissions(context));
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

    public uint GetInventoryWeightLimit(ConnectionContext context, CharacterRecord character) =>
        CharacterInventoryPlanner.CalculateMainInventoryWeightAllowance(
            characterStatCatalog.Get(character).InventoryLimit,
            context.State.Inventory.Equipped.Values,
            context.State.Inventory.Creature.Values,
            itemCatalog);

    /// <summary>
    /// Adopts a saved warehouse-upgrade state after a store round-trip; the
    /// former AdoptSavedWarehouseUpgradeState local function. Returns true
    /// when the capacity changed.
    /// </summary>
    public bool AdoptSavedWarehouseUpgradeState(ConnectionContext context, CharacterRecord savedCharacter)
    {
        var previousCapacity = context.State.Inventory.WarehouseCapacity;
        context.State.Character.Active = savedCharacter;
        context.State.Inventory.Main = (savedCharacter.Inventory ?? [])
            .ToDictionary(item => item.Slot);
        context.State.Inventory.Warehouse = (savedCharacter.Warehouse ?? [])
            .ToDictionary(item => item.Slot);
        context.State.Inventory.WarehouseCapacity = CharacterWarehouseProgression.Normalize(
            savedCharacter.WarehouseCapacity);
        return context.State.Inventory.WarehouseCapacity != previousCapacity;
    }

    public async Task SendWarehouseStateAsync(ConnectionContext context, CancellationToken cancellationToken)
    {
        var warehouseRefresh = builders.CreateWarehousePacket(
            context.State.Inventory.Warehouse.Values,
            context.State.Inventory.WarehouseCapacity);
        await context.Outbound.SendAsync(warehouseRefresh, "WAREHOUSE_STATE", cancellationToken);
    }

    // ---- Quest-flow helpers (migrated verbatim from the former local
    // functions; the dungeon branches keep delegating wrappers).

    public uint GetAuthoritativeQuestTrigger(
    ConnectionContext context,
    QuestDefinition definition,
    IEnumerable<CharacterItemRecord>? inventory,
    int gold,
    uint victoryPoints) => QuestPvpRankRequirement.IsPvpRank(definition)
        ? QuestPvpRankRequirement.GetTrigger(definition,
            pvpExperienceCatalog.GetGradeForPoints(context.State.Character.Active?.PvpPoints ?? 0))
        : QuestItemRequirementPlanner.GetInitialTrigger(
            definition, inventory, gold, victoryPoints);

    public List<CharacterQuestRecord> RefreshSeekingQuestTriggers(
    ConnectionContext context,
    
    IEnumerable<CharacterQuestRecord> quests,
    IEnumerable<CharacterItemRecord> inventory,
    int gold,
    uint victoryPoints,
    out List<(ushort QuestId, uint OldTrigger, uint NewTrigger)> changes)
{
    changes = [];
    var refreshed = new List<CharacterQuestRecord>();
    foreach (var quest in quests)
    {
        if (!questCatalog.TryGetDefinition(quest.QuestId, out var definition)
            || !QuestItemRequirementPlanner.IsSeeking(definition))
        {
            refreshed.Add(quest);
            continue;
        }

        var trigger = QuestItemRequirementPlanner.GetInitialTrigger(
            definition,
            inventory,
            gold,
            victoryPoints);
        refreshed.Add(quest with { Trigger = trigger });
        if (trigger != quest.Trigger)
        {
            changes.Add((quest.QuestId, quest.Trigger, trigger));
        }
    }

    return refreshed;
}

    public async Task GrantQuestItemDropsAsync(
        ConnectionContext context,
        Func<QuestDefinition, IEnumerable<CharacterItemRecord>, IReadOnlyList<QuestItemDrop>> planDrops,
        string source,
    CancellationToken cancellationToken = default)
{
    if (context.State.Character.Active is null || context.State.Dungeon.CurrentDungeonId == 0)
    {
        return;
    }

    var plannedInventory = context.State.Inventory.Main.ToDictionary(
        pair => pair.Key,
        pair => pair.Value);
    var plannedWarehouseCapacity = context.State.Inventory.WarehouseCapacity;
    var changedSlots = new HashSet<ushort>();
    var grantedDrops = new List<(ushort QuestId, QuestItemDrop Drop)>();
    foreach (var activeQuest in context.State.Quest.Current.ToArray())
    {
        if (!questCatalog.TryGetDefinition(
                activeQuest.QuestId,
                out var definition))
        {
            continue;
        }

        foreach (var drop in planDrops(definition, plannedInventory.Values))
        {
            if (CharacterWarehouseProgression.TryGetItemTargetCapacity(
                    drop.ItemId,
                    out var targetCapacity))
            {
                plannedWarehouseCapacity = Math.Max(
                    plannedWarehouseCapacity,
                    targetCapacity);
                grantedDrops.Add((activeQuest.QuestId, drop));
                continue;
            }

            var taskItem = CharacterItemIdentity.Ensure(
                new CharacterMailAttachmentRecord(
                drop.ItemId,
                drop.Count,
                Durability: itemCatalog.GetInitialDurability(drop.ItemId),
                SealState: CharacterItemSealing.GetInitialSealState(
                    itemCatalog,
                    drop.ItemId)),
                itemCatalog);
            if (!CharacterInventoryPlanner.TryPlace(
                    plannedInventory,
                    taskItem,
                    itemCatalog,
                    GetInventoryWeightLimit(context, context.State.Character.Active),
                    out var placement,
                    out var failure))
            {
                logger.LogWarning(
                    "Could not grant quest item {ItemId} x{Count} for quest {QuestId} from {Source}: {Failure}.",
                    drop.ItemId,
                    drop.Count,
                    activeQuest.QuestId,
                    source,
                    failure);
                continue;
            }

            plannedInventory = placement.Inventory;
            changedSlots.UnionWith(placement.ChangedSlots);
            grantedDrops.Add((activeQuest.QuestId, drop));
        }
    }

    var plannedQuests = RefreshSeekingQuestTriggers(context, 
        context.State.Quest.Current,
        plannedInventory.Values,
        context.State.Currency.Gold,
        context.State.Currency.VictoryPoints,
        out var triggerChanges);
    if (changedSlots.Count == 0
        && triggerChanges.Count == 0
        && plannedWarehouseCapacity == context.State.Inventory.WarehouseCapacity)
    {
        return;
    }

    var saved = await store.SaveCharacterQuestProgressAsync(
        context.State.Login.AccountName,
        context.State.Character.Active.Id,
        plannedInventory.Values,
        plannedQuests,
        context.State.Quest.CompletedIds,
        cancellationToken,
        warehouseCapacity: plannedWarehouseCapacity,
        expectedWarehouseCapacity: context.State.Inventory.WarehouseCapacity);
    if (saved is null)
    {
        logger.LogWarning(
            "Could not persist quest item grants from {Source}; inventory and quest progress were left unchanged.",
            source);
        return;
    }

    var warehouseCapacityChanged =
        AdoptSavedWarehouseUpgradeState(context, saved);
    context.State.Quest.Current = plannedQuests;
    if (changedSlots.Count > 0)
    {
        var itemUpdate = GameProtocolEngine.CreateUpdateItemList(
            0,
            changedSlots
                .OrderBy(slot => slot)
                .Select(slot => builders.ToGameInventoryEntry(context.State.Inventory.Main[slot]))
                .ToArray());
        await context.Outbound.SendAsync(itemUpdate, "QUEST_ITEMS", cancellationToken);
    }

    if (warehouseCapacityChanged)
    {
        await SendWarehouseStateAsync(context, cancellationToken);
    }

    foreach (var change in triggerChanges)
    {
        var triggerUpdate = GameProtocolEngine.CreateSetQuestTriggerReply(
            change.QuestId,
            change.NewTrigger);
        await context.Outbound.SendAsync(triggerUpdate, "QUEST_TRIGGER", cancellationToken);
    }

    foreach (var grant in grantedDrops)
    {
        logger.LogInformation(
            "Granted quest item from {Source}: quest={QuestId}, item={ItemId}, count={Count}.",
            source,
            grant.QuestId,
            grant.Drop.ItemId,
            grant.Drop.Count);
    }

    }

    public async Task SendAcceptableQuestListAsync(ConnectionContext context, CancellationToken cancellationToken)
{
    var acceptable = GameProtocolEngine.CreateAcceptableQuestList(
        GetAcceptableQuestIds(context));
    await context.Outbound.SendAsync(acceptable, "QUEST_LIST", cancellationToken);
}

    public async Task SendCurrentSkillInfoAsync(ConnectionContext context, CancellationToken cancellationToken)
{
    var skillInfo = GameProtocolEngine.CreateSkillInfo(
        checked((ushort)Math.Min(context.State.Skill.CurrentSp, ushort.MaxValue)),
        GetCurrentGameSkills(context));
    await context.Outbound.SendAsync(skillInfo, "SKILL_INFO", cancellationToken);
    }

    // ---- Town-appearance / character-detail senders (migrated verbatim
    // from the former local functions; the legacy chain keeps its copies
    // until groups 12/14 migrate).

    public async Task SendCurrentEquipmentStateAsync(
    ConnectionContext context,
    GameCharacterCombatStats? validatedStats = null,
    CancellationToken cancellationToken = default)
{
    if (context.State.Character.Active is null)
    {
        return;
    }

    var projectedStats = validatedStats
        ?? characterStatCatalog.Get(context.State.Character.Active);
    var equipment = GameProtocolEngine.CreateCurrentCharacterDetails(
        context.State.Character.LocalUserId,
        experience: context.State.Character.Active.Experience,
        learnedSkills: GetCurrentGameSkills(context),
        combatStats: projectedStats,
        equippedItems: builders.CreateWornEquipmentSnapshot(
            context.State.Inventory.Equipped.Values,
            context.State.Inventory.Creature),
        equippedCreatureLevel: CharacterPacketBuilders.GetEquippedCreatureLevel(
            context.State.Inventory.Creature),
        staminaRecoveryPercentage: GetProjectedStaminaRecovery(context));
    await context.Outbound.SendAsync(equipment, "CHARACTER_DETAILS", cancellationToken);
    context.State.Character.LastProjectedCombatStats = projectedStats;
}

public async Task SendEquippedCreatureDeathAsync(
    ConnectionContext context,
    bool force = false,
    CancellationToken cancellationToken = default)
{
    if (context.State.Character.Active is null
        || !context.State.Inventory.Creature.TryGetValue(
            CharacterCreatureInventoryLayout.EquippedCreatureSlot,
            out var equippedCreature)
        || equippedCreature.CountOrValue == 0)
    {
        return;
    }

    var creatureUid = equippedCreature.CountOrValue;
    var stomach = Math.Clamp(
        (int)(equippedCreature.CreatureStomach ?? 100),
        0,
        CreatureHungerPlanner.MaximumStomach);
    if (stomach != 0)
    {
        context.State.Weakness.CreatureDeathNotificationsSent.Remove(creatureUid);
        return;
    }

    if (!force
        && context.State.Weakness.CreatureDeathNotificationsSent.Contains(creatureUid))
    {
        return;
    }

    var died = GameProtocolEngine.CreateDiedCreature(context.State.Character.LocalUserId);
    await context.Outbound.SendAsync(died, "CREATURE_DIED", cancellationToken);
    context.State.Weakness.CreatureDeathNotificationsSent.Add(creatureUid);
    logger.LogInformation(
        "Sent DIED_CREATURE for Creature uid {CreatureUid}, area user {UserId}.",
        creatureUid,
        context.State.Character.LocalUserId);
}

public async Task SendEquippedCreatureHungerAsync(
    ConnectionContext context,
    bool forceDeathNotification = false,
    CancellationToken cancellationToken = default)
{
    if (context.State.Character.Active is null
        || !context.State.Inventory.Creature.TryGetValue(
            CharacterCreatureInventoryLayout.EquippedCreatureSlot,
            out var equippedCreature)
        || equippedCreature.CountOrValue == 0)
    {
        return;
    }

    var creatureState = GameProtocolEngine.CreateCreatureState(
        equippedCreature.CountOrValue,
        (uint)Math.Clamp(
            (int)(equippedCreature.CreatureStomach ?? 100),
            0,
            CreatureHungerPlanner.MaximumStomach));
    await context.Outbound.SendAsync(creatureState, "CREATURE_STATE", cancellationToken);
    await SendEquippedCreatureDeathAsync(context, forceDeathNotification, cancellationToken);
}

public async Task SendEquippedCreatureResponseAsync(
    ConnectionContext context,
    CancellationToken cancellationToken = default)
{
    if (context.State.Character.Active is null
        || !context.State.Inventory.Creature.ContainsKey(
            CharacterCreatureInventoryLayout.EquippedCreatureSlot))
    {
        return;
    }

    var creatureResponse =
        GameProtocolEngine.CreateCreatureResponse(context.State.Character.LocalUserId);
    await context.Outbound.SendAsync(creatureResponse, "CREATURE_RESPONSE", cancellationToken);
}

public async Task SendTownAppearanceAsync(
    ConnectionContext context,
    bool waitForTownActor = false,
    CancellationToken cancellationToken = default)
{
    if (context.State.Character.Active is null || context.State.Dungeon.CurrentDungeonId != 0)
    {
        return;
    }

    if (waitForTownActor)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
    }

    // The original server equips the Creature and establishes
    // its town state before broadcasting the mode-zero basic
    // user record containing item id, name and alive state.
    await SendEquippedCreatureHungerAsync(
        context,
        // Only AREA_USERS reconstruction needs to replay a
        // persisted death marker. Ordinary equipment updates
        // keep the transition de-duplicated.
        forceDeathNotification: waitForTownActor,
        cancellationToken);

    var appearance = builders.CreateTownAppearancePacket(
        context.State.Character.Active,
        context.State.Character.LocalUserId,
        context.State.Inventory.Equipped.Values,
        context.State.Inventory.Creature.Values,
        HasActivePremiumService(context, GameProtocolEngine.BlackDiamondServiceType));
    await context.Outbound.SendAsync(appearance, "TOWN_APPEARANCE", cancellationToken);

    // Mode zero is the compact town-actor appearance
    // projection: Avatar slots 0..8, weapon slot 9, plus the
    // dedicated title and Creature fields. It drops other worn
    // objects omitted from its compact list, so immediately
    // rebuild the authoritative equipment projection with
    // subtype one.
    await SendCurrentEquipmentStateAsync(context, cancellationToken: cancellationToken);
    await SendInitialStaminaStateAsync(context, cancellationToken);
}

public async Task ValidateAndCorrectCharacterStatsAsync(
    ConnectionContext context,
    string reason,
    CancellationToken cancellationToken = default)
{
    if (context.State.Character.Active is null)
    {
        return;
    }

    var validation = characterStatCatalog.Validate(
        context.State.Character.Active,
        context.State.Character.LastProjectedCombatStats);
    if (context.State.Dungeon.CurrentDungeonId == 0)
    {
        // Mode zero first updates level/growtype on the town
        // actor. Mode one then overwrites the unequipped stat
        // block with the PVF-derived authoritative snapshot.
        await SendTownAppearanceAsync(context, cancellationToken: cancellationToken);
    }
    else
    {
        await SendCurrentEquipmentStateAsync(context, validation.Expected, cancellationToken);
    }

    logger.LogInformation(
        "Validated PVF character attributes after {Reason} for {CharacterId}; level={Level}, job={Job}, growType={GrowType}, awakening={AwakeningType}, correctionRequired={CorrectionRequired}, mismatches={Mismatches}.",
        reason,
        context.State.Character.Active.Id,
        context.State.Character.Active.Level,
        context.State.Character.Active.Job,
        context.State.Character.Active.GrowType & 0x0F,
        Math.Max(context.State.Character.Active.AwakeningType, context.State.Character.Active.GrowType >> 4),
        !validation.IsCorrect,
        validation.MismatchedFields.Count == 0
            ? "none"
            : string.Join(',', validation.MismatchedFields));
}

public async Task SendInitialStaminaStateAsync(
    ConnectionContext context,
    CancellationToken cancellationToken = default)
{
    if (context.State.Weakness.StaminaStateInitialized || context.State.Character.Active is null)
    {
        return;
    }

    // DFLegacy NOTI 4 owns the weakness percentage. Persisted
    // weakness is restored after the authoritative combat-stat
    // projection; active black diamond always projects full stamina.
    var stamina = GameProtocolEngine.CreateStamina(
        GetProjectedStaminaRecovery(context));
    await context.Outbound.SendAsync(stamina, "STAMINA_NOTI", cancellationToken);
    context.State.Weakness.StaminaStateInitialized = true;
}

    // ---- Inventory-move senders and the Creature-move planner cluster
    // (migrated verbatim from EntranceService; the dungeon branches keep
    // delegating wrappers until group 13 migrates).

public async Task<bool> SettleDungeonCreatureHungerAsync(
    ConnectionContext context,
    bool notifyClient,
    CancellationToken cancellationToken)
{
    if (context.State.Dungeon.CreatureStomachCheckpointSeconds is not long checkpoint)
    {
        return true;
    }

    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var elapsedSeconds = checked((uint)Math.Min(
        uint.MaxValue,
        Math.Max(0L, now - checkpoint)));
    if (elapsedSeconds == 0)
    {
        return true;
    }

    if (context.State.Character.Active is null
        || !CreatureHungerPlanner.TrySettle(
            context.State.Inventory.Creature,
            elapsedSeconds,
            itemCatalog,
            out var hungerPlan))
    {
        context.State.Dungeon.CreatureStomachCheckpointSeconds = now;
        return true;
    }

    if (!hungerPlan.Changed)
    {
        context.State.Dungeon.CreatureStomachCheckpointSeconds = now;
        return true;
    }

    var saved = await store.SaveCharacterItemSpacesAsync(
        context.State.Login.AccountName,
        context.State.Character.Active.Id,
        context.State.Currency.Gold,
        context.State.Inventory.Main.Values,
        context.State.Inventory.Equipped.Values,
        context.State.Inventory.Warehouse.Values,
        cancellationToken,
        avatarInventory: context.State.Inventory.Avatar.Values,
        creatureInventory: hungerPlan.Inventory.Values);
    if (saved is null)
    {
        logger.LogWarning(
            "Could not persist Creature stomach settlement for character {CharacterId} in dungeon {DungeonId}.",
            context.State.Character.Active.Id,
            context.State.Dungeon.CurrentDungeonId);
        return false;
    }

    context.State.Inventory.Creature = hungerPlan.Inventory;
    context.State.Character.Active = context.State.Character.Active with
    {
        CreatureInventory = hungerPlan.Inventory.Values
            .OrderBy(item => item.Slot)
            .ToList()
    };
    context.State.Dungeon.CreatureStomachCheckpointSeconds = now;

    if (hungerPlan.BecameDead)
    {
        // The 13339 timer path emits NOTI 100 exactly when the
        // effective stomach crosses below one.  Do this even
        // for a silent settlement so the client cannot keep a
        // live map object after a transactional refresh.
        await SendEquippedCreatureDeathAsync(context, cancellationToken: cancellationToken);
    }

    if (notifyClient && hungerPlan.StomachChanged)
    {
        var stomachState = GameProtocolEngine.CreateCreatureState(
            hungerPlan.CreatureUid,
            hungerPlan.Stomach);
        await context.Outbound.SendAsync(stomachState, "CREATURE_STATE", cancellationToken);
    }

    if (notifyClient && hungerPlan.ConsumedFoodCount != 0)
    {
        var creatureItems = builders.CreateCreatureInventoryItemListPacket(
            context.State.Inventory.Creature.Values);
        await context.Outbound.SendAsync(creatureItems, "CREATURE_STATE", cancellationToken);
    }

    logger.LogInformation(
        "Dungeon {DungeonId} settled {ElapsedSeconds}s of Creature hunger for uid {CreatureUid}: stomach {PreviousStomach}->{Stomach}, remainder {PreviousRemainder}->{Remainder}s, auto-consumed food item 24 x{ConsumedFoodCount}.",
        context.State.Dungeon.CurrentDungeonId,
        hungerPlan.ElapsedSeconds,
        hungerPlan.CreatureUid,
        hungerPlan.PreviousStomach,
        hungerPlan.Stomach,
        hungerPlan.PreviousRemainderSeconds,
        hungerPlan.RemainderSeconds,
        hungerPlan.ConsumedFoodCount);
    return true;
}

public async Task SendInventoryMoveNoOpAsync(
    ConnectionContext context,
    byte sourceListType,
    ushort sourceSlot,
    byte destinationListType,
    ushort destinationSlot,
    string reason,
    CancellationToken cancellationToken = default)
{
    var noOpReply = GameProtocolEngine.CreateMoveItemSpaceReply(
        sourceListType,
        sourceSlot,
        movedCount: 0,
        destinationListType,
        destinationSlot);
    await context.Outbound.SendAsync(noOpReply, "INVENTORY_DELTA", cancellationToken);

    if (sourceListType == 0 || destinationListType == 0)
    {
        var mainRefresh = builders.CreateMainInventoryPacket(
            context.State.Currency.Gold,
            context.State.Currency.VictoryPoints,
            context.State.Inventory.Main.Values);
        await context.Outbound.SendAsync(mainRefresh, "INVENTORY_DELTA", cancellationToken);
    }

    if (sourceListType == 2 || destinationListType == 2)
    {
        var warehouseRefresh = builders.CreateWarehousePacket(
            context.State.Inventory.Warehouse.Values,
            context.State.Inventory.WarehouseCapacity);
        await context.Outbound.SendAsync(warehouseRefresh, "INVENTORY_DELTA", cancellationToken);
    }

    if (sourceListType == 1 || destinationListType == 1)
    {
        var avatarRefresh = builders.CreateAvatarInventoryPacket(
            context.State.Inventory.Avatar.Values);
        await context.Outbound.SendAsync(avatarRefresh, "INVENTORY_DELTA", cancellationToken);
    }

    if (sourceListType == 7 || destinationListType == 7)
    {
        foreach (var creatureRefresh in builders.CreateCreatureInventoryPackets(
                     context.State.Inventory.Creature.Values))
        {
            await context.Outbound.SendAsync(creatureRefresh, "INVENTORY_DELTA", cancellationToken);
        }
    }

    if ((sourceListType == 3 || destinationListType == 3)
        && sourceListType != 7
        && destinationListType != 7)
    {
        await SendCurrentEquipmentStateAsync(context, cancellationToken: cancellationToken);
    }

    logger.LogInformation(
        "Completed rejected inventory move as a no-op from {SourceList}:{SourceSlot} to {DestinationList}:{DestinationSlot}: {Reason}.",
        sourceListType,
        sourceSlot,
        destinationListType,
        destinationSlot,
        reason);
}

public async Task SendMainInventoryMoveDeltaAsync(
    ConnectionContext context,
    CancellationToken cancellationToken,
    params ushort[] slots)
{
    foreach (var slot in slots
                 .Where(CharacterInventoryLayout.IsMainItemSlot)
                 .Distinct())
    {
        var entry = context.State.Inventory.Main.TryGetValue(slot, out var item)
            ? builders.ToGameInventoryEntry(item)
            : new GameInventoryEntry(slot, ushort.MaxValue, 0);
        var update = GameProtocolEngine.CreateUpdateItemList(0, [entry]);
        await context.Outbound.SendAsync(update, "INVENTORY_DELTA", cancellationToken);
    }
}

    internal bool TryPlanCreatureInventoryMove(
        IReadOnlyDictionary<ushort, CharacterItemRecord> warehouse,
        ushort warehouseCapacity,
        IReadOnlyDictionary<ushort, CharacterItemRecord> creatureInventory,
        byte sourceListType,
        ushort sourceSlot,
        int moveCount,
        byte destinationListType,
        ushort destinationSlot,
        out CreatureInventoryMovePlan plan,
        out string failure)
    {
        plan = null!;
        failure = string.Empty;
        if (sourceListType is not (2 or 3 or 7)
            || destinationListType is not (2 or 3 or 7)
            || sourceListType != 7 && destinationListType != 7)
        {
            failure = "Creature space only supports its native equipment slots and character storage";
            return false;
        }

        if (!IsCreatureMoveSlotValid(sourceListType, sourceSlot, warehouseCapacity)
            || destinationSlot != ushort.MaxValue
                && !IsCreatureMoveSlotValid(
                    destinationListType,
                    destinationSlot,
                    warehouseCapacity))
        {
            failure = "Creature move contains an invalid slot";
            return false;
        }

        var plannedWarehouse = warehouse.ToDictionary(pair => pair.Key, pair => pair.Value);
        var plannedCreature = creatureInventory.ToDictionary(
            pair => pair.Key,
            pair => pair.Value);
        var actualSourceList = sourceListType;
        var actualSourceClientSlot = sourceSlot;
        var actualDestinationList = destinationListType;
        var actualDestinationClientSlot = destinationSlot;
        var actualSourceStorageSlot = MapCreatureEndpointSlot(
            actualSourceList,
            actualSourceClientSlot);
        var actualDestinationStorageSlot = actualDestinationClientSlot == ushort.MaxValue
            ? ushort.MaxValue
            : MapCreatureEndpointSlot(
                actualDestinationList,
                actualDestinationClientSlot);
        var sourceSpace = GetCreatureMoveSpace(
            actualSourceList,
            plannedWarehouse,
            plannedCreature);
        var destinationSpace = GetCreatureMoveSpace(
            actualDestinationList,
            plannedWarehouse,
            plannedCreature);
        if (!sourceSpace.TryGetValue(actualSourceStorageSlot, out var sourceItem))
        {
            if (actualDestinationStorageSlot == ushort.MaxValue
                || !destinationSpace.TryGetValue(
                    actualDestinationStorageSlot,
                    out sourceItem))
            {
                failure = "Creature move source is empty";
                return false;
            }

            (actualSourceList, actualDestinationList) =
                (actualDestinationList, actualSourceList);
            (actualSourceClientSlot, actualDestinationClientSlot) =
                (actualDestinationClientSlot, actualSourceClientSlot);
            (actualSourceStorageSlot, actualDestinationStorageSlot) =
                (actualDestinationStorageSlot, actualSourceStorageSlot);
            sourceSpace = GetCreatureMoveSpace(
                actualSourceList,
                plannedWarehouse,
                plannedCreature);
            destinationSpace = GetCreatureMoveSpace(
                actualDestinationList,
                plannedWarehouse,
                plannedCreature);
        }

        if (!itemCatalog.TryGetDefinition(sourceItem.ItemId, out var sourceDefinition))
        {
            failure = "Creature move item is unknown";
            return false;
        }

        if (actualSourceList == 3 || actualDestinationList == 3)
        {
            sourceItem = CharacterItemSealing.UnsealWhenEquipped(
                sourceItem,
                sourceDefinition);
        }

        if (actualDestinationClientSlot == ushort.MaxValue)
        {
            actualDestinationClientSlot = FindCreatureMoveDestination(
                destinationSpace,
                actualDestinationList,
                sourceItem,
                sourceDefinition,
                actualSourceList == actualDestinationList
                    ? actualSourceClientSlot
                    : null,
                warehouseCapacity);
            actualDestinationStorageSlot = actualDestinationClientSlot == ushort.MaxValue
                ? ushort.MaxValue
                : MapCreatureEndpointSlot(
                    actualDestinationList,
                    actualDestinationClientSlot);
        }

        if (actualDestinationClientSlot == ushort.MaxValue
            || !IsCreatureMoveSlotValid(
                actualDestinationList,
                actualDestinationClientSlot,
                warehouseCapacity)
            || !IsCompatibleCreatureMoveSlot(
                actualDestinationList,
                sourceDefinition,
                actualDestinationClientSlot,
                warehouseCapacity))
        {
            failure = "Creature move destination is full or incompatible";
            return false;
        }

        if (actualSourceList == actualDestinationList
            && actualSourceStorageSlot == actualDestinationStorageSlot)
        {
            plan = new CreatureInventoryMovePlan(
                plannedWarehouse,
                plannedCreature,
                sourceDefinition.IsStackable ? sourceItem.CountOrValue : 1,
                destinationSlot == ushort.MaxValue
                    ? actualDestinationClientSlot
                    : destinationSlot,
                sourceItem.ItemId,
                EquippedSlotsChanged: false,
                EquippedCreatureChanged: false);
            return true;
        }

        destinationSpace.TryGetValue(actualDestinationStorageSlot, out var displacedItem);
        if (displacedItem is not null
            && (!itemCatalog.TryGetDefinition(
                    displacedItem.ItemId,
                    out var displacedDefinition)
                || !IsCompatibleCreatureMoveSlot(
                    actualSourceList,
                    displacedDefinition,
                    actualSourceClientSlot,
                    warehouseCapacity)))
        {
            failure = "Creature move would displace an incompatible item";
            return false;
        }

        if (displacedItem is not null
            && (actualSourceList == 3 || actualDestinationList == 3))
        {
            displacedItem = CharacterItemSealing.UnsealWhenEquipped(
                displacedItem,
                itemCatalog);
        }

        var availableQuantity = sourceDefinition.IsStackable
            ? sourceItem.CountOrValue
            : 1u;
        var requestedQuantity = moveCount <= 0
            ? availableQuantity
            : Math.Min(availableQuantity, checked((uint)moveCount));
        if (requestedQuantity == 0)
        {
            failure = "Creature move count is zero";
            return false;
        }

        var canStack = sourceDefinition.IsStackable
            && displacedItem is not null
            && displacedItem.ItemId == sourceItem.ItemId
            && displacedItem.State == sourceItem.State
            && displacedItem.Durability == sourceItem.Durability
            && displacedItem.SealState == sourceItem.SealState;
        if (canStack)
        {
            var stackLimit = CharacterInventoryPlanner.GetStackLimit(sourceDefinition);
            var available = stackLimit > displacedItem!.CountOrValue
                ? stackLimit - displacedItem.CountOrValue
                : 0;
            if (available == 0)
            {
                failure = "Creature destination stack is full";
                return false;
            }

            requestedQuantity = Math.Min(requestedQuantity, available);
            destinationSpace[actualDestinationStorageSlot] = displacedItem with
            {
                CountOrValue = displacedItem.CountOrValue + requestedQuantity
            };
            if (requestedQuantity == sourceItem.CountOrValue)
            {
                sourceSpace.Remove(actualSourceStorageSlot);
            }
            else
            {
                sourceSpace[actualSourceStorageSlot] = sourceItem with
                {
                    CountOrValue = sourceItem.CountOrValue - requestedQuantity
                };
            }
        }
        else if (sourceDefinition.IsStackable
            && displacedItem is null
            && requestedQuantity < sourceItem.CountOrValue)
        {
            sourceSpace[actualSourceStorageSlot] = sourceItem with
            {
                CountOrValue = sourceItem.CountOrValue - requestedQuantity
            };
            destinationSpace[actualDestinationStorageSlot] = sourceItem with
            {
                Slot = actualDestinationStorageSlot,
                CountOrValue = requestedQuantity
            };
        }
        else
        {
            sourceSpace.Remove(actualSourceStorageSlot);
            destinationSpace[actualDestinationStorageSlot] = sourceItem with
            {
                Slot = actualDestinationStorageSlot
            };
            if (displacedItem is not null)
            {
                sourceSpace[actualSourceStorageSlot] = displacedItem with
                {
                    Slot = actualSourceStorageSlot
                };
            }
        }

        var normalizedCreature = CharacterCreatureInventoryPlanner.Normalize(
            plannedCreature.Values,
            itemCatalog);
        if (normalizedCreature.OverflowItems.Count != 0)
        {
            failure = "Creature move normalization overflowed";
            return false;
        }

        var equippedChanged = Enumerable.Range(
                CharacterCreatureInventoryLayout.EquippedCreatureSlot,
                CharacterCreatureInventoryLayout.Capacity
                    - CharacterCreatureInventoryLayout.EquippedCreatureSlot)
            .Select(checkedSlot => checked((ushort)checkedSlot))
            .Any(slot => !Equals(
                creatureInventory.GetValueOrDefault(slot),
                normalizedCreature.Inventory.GetValueOrDefault(slot)));
        var equippedCreatureChanged = !Equals(
            creatureInventory.GetValueOrDefault(
                CharacterCreatureInventoryLayout.EquippedCreatureSlot),
            normalizedCreature.Inventory.GetValueOrDefault(
                CharacterCreatureInventoryLayout.EquippedCreatureSlot));
        plan = new CreatureInventoryMovePlan(
            plannedWarehouse,
            normalizedCreature.Inventory,
            requestedQuantity,
            destinationSlot == ushort.MaxValue
                ? actualDestinationClientSlot
                : destinationSlot,
            sourceItem.ItemId,
            equippedChanged,
            equippedCreatureChanged);
        return true;
    }

    internal ushort FindCreatureMoveDestination(
        IReadOnlyDictionary<ushort, CharacterItemRecord> destination,
        byte destinationListType,
        CharacterItemRecord item,
        ItemDefinition definition,
        ushort? excludedSlot,
        ushort warehouseCapacity)
    {
        if (destinationListType == 3)
        {
            if (!CharacterCreatureInventoryLayout.TryGetEquippedSlot(
                    definition,
                    out var equippedStorageSlot)
                || destination.ContainsKey(equippedStorageSlot))
            {
                return ushort.MaxValue;
            }

            return MapCreatureStorageSlotToClient(equippedStorageSlot);
        }

        if (definition.IsStackable)
        {
            var stackLimit = CharacterInventoryPlanner.GetStackLimit(definition);
            var partial = destination.Values
                .Where(candidate => candidate.Slot != excludedSlot
                    && candidate.ItemId == item.ItemId
                    && candidate.State == item.State
                    && candidate.Durability == item.Durability
                    && candidate.SealState == item.SealState
                    && candidate.CountOrValue < stackLimit
                    && IsCompatibleCreatureMoveSlot(
                        destinationListType,
                        definition,
                        candidate.Slot,
                        warehouseCapacity))
                .OrderBy(candidate => candidate.Slot)
                .Select(candidate => candidate.Slot)
                .DefaultIfEmpty(ushort.MaxValue)
                .First();
            if (partial != ushort.MaxValue)
            {
                return partial;
            }
        }

        if (destinationListType == 2)
        {
            return FindFreeInventorySlot(
                destination,
                0,
                warehouseCapacity);
        }

        if (!CharacterCreatureInventoryLayout.TryGetBagRange(
                definition,
                out var start,
                out var end))
        {
            return ushort.MaxValue;
        }

        return FindFreeInventorySlot(destination, start, end);
    }

    internal bool IsCompatibleCreatureMoveSlot(
        byte listType,
        ItemDefinition definition,
        ushort slot,
        ushort warehouseCapacity) => listType switch
    {
        2 => definition.InventoryCategory != ItemInventoryCategory.Avatar
            && slot < warehouseCapacity,
        3 => definition.InventoryCategory == ItemInventoryCategory.Creature
            && CharacterCreatureInventoryLayout.TryGetEquippedSlot(
                definition,
                out var equippedSlot)
            && equippedSlot == MapCreatureEndpointSlot(3, slot),
        7 => definition.InventoryCategory == ItemInventoryCategory.Creature
            && CharacterCreatureInventoryLayout.IsCompatibleSlot(definition, slot),
        _ => false
    };

    internal static bool IsCreatureMoveSlotValid(
        byte listType,
        ushort slot,
        ushort warehouseCapacity) =>
        listType switch
        {
            2 => slot < warehouseCapacity,
            3 => slot is >= CharacterCreatureInventoryLayout.ClientEquippedCreatureSlot
                and <= CharacterCreatureInventoryLayout.ClientEquippedArtifactGreenSlot,
            7 => CharacterCreatureInventoryLayout.IsInventorySlot(slot),
            _ => false
        };

    internal static Dictionary<ushort, CharacterItemRecord> GetCreatureMoveSpace(
        byte listType,
        Dictionary<ushort, CharacterItemRecord> warehouse,
        Dictionary<ushort, CharacterItemRecord> creatureInventory) =>
        listType is 3 or 7 ? creatureInventory : warehouse;

    internal static ushort MapCreatureEndpointSlot(byte listType, ushort slot) =>
        listType == 3
            ? slot switch
            {
                CharacterCreatureInventoryLayout.ClientEquippedCreatureSlot =>
                    CharacterCreatureInventoryLayout.EquippedCreatureSlot,
                CharacterCreatureInventoryLayout.ClientEquippedArtifactRedSlot =>
                    CharacterCreatureInventoryLayout.EquippedArtifactRedSlot,
                CharacterCreatureInventoryLayout.ClientEquippedArtifactBlueSlot =>
                    CharacterCreatureInventoryLayout.EquippedArtifactBlueSlot,
                CharacterCreatureInventoryLayout.ClientEquippedArtifactGreenSlot =>
                    CharacterCreatureInventoryLayout.EquippedArtifactGreenSlot,
                _ => ushort.MaxValue
            }
            : slot;

    internal static ushort MapCreatureStorageSlotToClient(ushort slot) => slot switch
    {
        CharacterCreatureInventoryLayout.EquippedCreatureSlot =>
            CharacterCreatureInventoryLayout.ClientEquippedCreatureSlot,
        CharacterCreatureInventoryLayout.EquippedArtifactRedSlot =>
            CharacterCreatureInventoryLayout.ClientEquippedArtifactRedSlot,
        CharacterCreatureInventoryLayout.EquippedArtifactBlueSlot =>
            CharacterCreatureInventoryLayout.ClientEquippedArtifactBlueSlot,
        CharacterCreatureInventoryLayout.EquippedArtifactGreenSlot =>
            CharacterCreatureInventoryLayout.ClientEquippedArtifactGreenSlot,
        _ => ushort.MaxValue
    };

    internal static ushort FindFreeInventorySlot(
        IReadOnlyDictionary<ushort, CharacterItemRecord> inventory,
        ushort firstSlot,
        int exclusiveUpperBound = 256)
    {
        for (var slot = (int)firstSlot; slot < exclusiveUpperBound; slot++)
        {
            if (!inventory.ContainsKey((ushort)slot))
            {
                return (ushort)slot;
            }
        }

        return ushort.MaxValue;
    }

    // ---- Inventory-space validators and slot search helpers (migrated
    // verbatim; chain branches call them through delegating wrappers).

    internal static bool TryGetInventorySpace(
        byte listType,
        Dictionary<ushort, CharacterItemRecord> mainInventory,
        Dictionary<ushort, CharacterItemRecord> avatarInventory,
        Dictionary<ushort, CharacterItemRecord> equippedInventory,
        Dictionary<ushort, CharacterItemRecord> warehouseInventory,
        out Dictionary<ushort, CharacterItemRecord> space)
    {
        if (listType == 0)
        {
            space = mainInventory;
            return true;
        }

        if (listType == 3)
        {
            space = equippedInventory;
            return true;
        }

        if (listType == 1)
        {
            space = avatarInventory;
            return true;
        }

        if (listType == 2)
        {
            space = warehouseInventory;
            return true;
        }

        space = null!;
        return false;
    }

    internal static bool IsInventorySlotValid(
        byte listType,
        ushort slot,
        ushort warehouseCapacity) => listType switch
    {
        0 => CharacterInventoryLayout.IsMainItemSlot(slot),
        1 => CharacterAvatarInventoryLayout.IsBagSlot(slot),
        2 => slot < warehouseCapacity,
        3 => slot < 23,
        _ => false
    };

    internal bool IsCompatibleMainInventorySlot(
        CharacterItemRecord item,
        ushort slot) =>
        itemCatalog.TryGetDefinition(item.ItemId, out var definition)
        && definition.InventoryCategory is not (
            ItemInventoryCategory.Avatar or ItemInventoryCategory.Creature)
        && CharacterInventoryLayout.IsCompatibleSlot(definition, slot);

    internal bool IsCompatibleInventorySlot(
        byte listType,
        CharacterItemRecord item,
        ushort slot,
        ushort warehouseCapacity)
    {
        if (!itemCatalog.TryGetDefinition(item.ItemId, out var definition))
        {
            return false;
        }

        return listType switch
        {
            0 => definition.InventoryCategory is not (
                    ItemInventoryCategory.Avatar or ItemInventoryCategory.Creature)
                && CharacterInventoryLayout.IsCompatibleSlot(definition, slot),
            1 => definition.InventoryCategory == ItemInventoryCategory.Avatar
                && CharacterAvatarInventoryLayout.IsBagSlot(slot),
            2 => definition.InventoryCategory != ItemInventoryCategory.Avatar
                && slot < warehouseCapacity,
            3 when CharacterAvatarInventoryLayout.IsWornSlot(slot) =>
                CharacterAvatarInventoryLayout.IsCompatibleWornSlot(definition, slot),
            3 => slot < 23
                && definition.InventoryCategory == ItemInventoryCategory.Equipment,
            _ => false
        };
    }

    internal ushort FindFreeCompatibleMainInventorySlot(
        IReadOnlyDictionary<ushort, CharacterItemRecord> inventory,
        CharacterItemRecord item)
    {
        if (!itemCatalog.TryGetDefinition(item.ItemId, out var definition))
        {
            return ushort.MaxValue;
        }

        if (CharacterInventoryLayout.TryGetCategoryRange(
                definition.InventoryCategory,
                out var start,
                out var end))
        {
            var categorySlot = FindFreeInventorySlot(inventory, start, end);
            if (categorySlot != ushort.MaxValue)
            {
                return categorySlot;
            }
        }

        return FindFreeInventorySlot(
            inventory,
            CharacterInventoryLayout.QuickSlotStart,
            CharacterInventoryLayout.QuickSlotEnd);
    }

    internal ushort FindPartialStackSlot(
        IReadOnlyDictionary<ushort, CharacterItemRecord> inventory,
        CharacterItemRecord item,
        byte listType,
        ushort? excludedSlot)
    {
        if (listType is not (0 or 2)
            || !itemCatalog.TryGetDefinition(item.ItemId, out var definition)
            || !definition.IsStackable
            || listType == 2
                && definition.InventoryCategory == ItemInventoryCategory.Avatar)
        {
            return ushort.MaxValue;
        }

        var stackLimit = CharacterInventoryPlanner.GetStackLimit(definition);
        return inventory.Values
            .Where(candidate => candidate.Slot != excludedSlot
                && candidate.ItemId == item.ItemId
                && candidate.State == item.State
                && candidate.Durability == item.Durability
                && candidate.SealState == item.SealState
                && candidate.CountOrValue < stackLimit
                && (listType != 0
                    || CharacterInventoryLayout.IsCompatibleSlot(
                        definition,
                        candidate.Slot)))
            .OrderBy(candidate => candidate.Slot)
            .Select(candidate => candidate.Slot)
            .DefaultIfEmpty(ushort.MaxValue)
            .First();
    }

}
