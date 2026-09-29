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
}}
