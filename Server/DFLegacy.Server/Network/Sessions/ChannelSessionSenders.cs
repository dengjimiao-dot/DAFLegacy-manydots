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
    ScriptFileSystem scripts,
    ServerOptions options,
    CharacterStatCatalog characterStatCatalog,
    CharacterSpCatalog spCatalog,
    CreatureExperienceCatalog creatureExperienceCatalog,
    CharacterSessionRegistry characterSessions,
    PartyCoordinator partyCoordinator,
    QuestCatalog questCatalog,
    DungeonCatalog dungeonCatalog,
    PvpExperienceCatalog pvpExperienceCatalog,
    CharacterPacketBuilders builders,
    ILogger<ChannelSessionSenders> logger)
{
    // Entrance content cache (migrated with LoadContent; single instance
    // shared with the pump's entrance-port path).
    private readonly object _contentGate = new();
    private EntranceContent? _content;
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
            || HasActivePremiumService(context,
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

public async Task SendPremiumServiceInfoAsync(
    ConnectionContext context,
    byte serviceType,
    uint remainSeconds,
    CancellationToken cancellationToken = default)
{
    if (remainSeconds == 0)
    {
        context.State.Premium.ExpiryUnix.TryRemove(serviceType, out _);
    }
    else
    {
        var expiryUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            + (long)remainSeconds;
        context.State.Premium.ExpiryUnix[serviceType] = expiryUnix;
    }

    var premiumNotification =
        GameProtocolEngine.CreatePremiumServiceNotification(
            action: GameProtocolEngine.PremiumServiceUpdateAction,
            serviceType,
            remainSeconds);
    await context.Outbound.SendAsync(premiumNotification, "PREMIUM_INFO", cancellationToken);

    if (serviceType == GameProtocolEngine.BlackDiamondServiceType
        && context.State.Character.Active is not null
        && context.State.Dungeon.CurrentDungeonId == 0)
    {
        var appearance = builders.CreateTownAppearancePacket(
            context.State.Character.Active,
            context.State.Character.LocalUserId,
            context.State.Inventory.Equipped.Values,
            context.State.Inventory.Creature.Values,
            HasActivePremiumService(context,
                GameProtocolEngine.BlackDiamondServiceType));
        await context.Outbound.SendAsync(appearance, "PREMIUM_INFO", cancellationToken);
        await SendTownFatigueStateAsync(context, cancellationToken);
    }
}

public async Task SendTownFatigueStateAsync(
    ConnectionContext context,
    CancellationToken cancellationToken = default)
{
    // AREA_USERS constructs the town HUD. EVENT_INFO then selects
    // and initializes its normal/premium fatigue-bar resources.
    var eventInfo = GameProtocolEngine.CreateEventInfo();
    await context.Outbound.SendAsync(eventInfo, "TOWN_STAMINA", cancellationToken);

    var premiumFatigue = HasActivePremiumService(
            context,
            GameProtocolEngine.BlackDiamondServiceType)
        ? GameProtocolEngine.BlackDiamondBonusFatigue
        : (ushort)0;
    var maximumFatigue = checked((ushort)(
        GameProtocolEngine.DefaultMaximumFatigue + premiumFatigue));
    var fatigue = GameProtocolEngine.CreateFatigue(
        usedFatigue: Math.Min(
            context.State.Character.Active?.UsedFatigue ?? 0,
            maximumFatigue),
        maximumFatigue: maximumFatigue,
        premiumFatigue: premiumFatigue);
    await context.Outbound.SendAsync(fatigue, "TOWN_STAMINA", cancellationToken);
}

    // ---- Town presence and scene-transition senders (migrated verbatim
    // for the R4 Town group).

public void PublishTownPresence(ConnectionContext context)
{
    if (context.State.Character.Active is null)
    {
        return;
    }

    var appearance = builders.CreateTownAppearancePacket(
        context.State.Character.Active,
        context.State.Character.LocalUserId,
        context.State.Inventory.Equipped.Values,
        context.State.Inventory.Creature.Values,
        HasActivePremiumService(context, GameProtocolEngine.BlackDiamondServiceType));
    partyCoordinator.PublishTownPresence(
        new PartyPresenceUpdate(
            context.Runtime.Id,
            context.State.Character.Active.Id,
            context.State.Login.AccountUid,
            context.State.Character.LocalUserId,
            context.State.Character.Active.Name,
            (byte)Math.Clamp(context.State.Character.Active.Level, (byte)1, byte.MaxValue),
            (byte)Math.Clamp(context.State.Character.Active.Job, byte.MinValue, byte.MaxValue),
            context.State.Character.TownId,
            context.State.Character.AreaId,
            context.State.Character.X,
            context.State.Character.Y,
            context.State.Character.Direction),
        appearance);
}

public async Task SendEnterGameWorldCompleteAsync(
    ConnectionContext context,
    CancellationToken cancellationToken = default)
{
    // USERINFO locks keyboard and mouse commands at priority
    // two. DFLegacy notification 136 clears both locks after the
    // local actor has been constructed by AREA_USERS.
    var gameWorldComplete =
        GameProtocolEngine.CreateEnterGameWorldComplete();
    await context.Outbound.SendAsync(gameWorldComplete, "TOWN_NOTI", cancellationToken);
}

public async Task SendSilentTownStaminaStateAsync(
    ConnectionContext context,
    CancellationToken cancellationToken = default)
{
    if (context.State.Character.Active is null || context.State.Dungeon.CurrentDungeonId != 0)
    {
        return;
    }

    // AREA_USERS recreates the town actor with empty stamina.
    // NOTI 33 restores a normal town actor without a recovery
    // sound, but its client handler also fills current HP/MP.
    // It must therefore never be sent while inside a dungeon.
    var stamina = context.State.Character.Active.IsWeakened
        && !HasActivePremiumService(
            context,
            GameProtocolEngine.BlackDiamondServiceType)
            ? GameProtocolEngine.CreateStamina(
                context.State.Character.Active.WeaknessRecovery)
            : GameProtocolEngine.CreateFailClearDungeon(
                DungeonWeaknessPolicy.FullStamina);
    await context.Outbound.SendAsync(stamina, "TOWN_NOTI", cancellationToken);
}

    // ---- Dungeon flow helpers (migrated verbatim for R5: return-to-town,
    // selection eligibility, room fatigue, creature experience, clear-reward
    // cards and quest-item grants).

public async Task ReturnToTownFromDungeonAsync(
    ConnectionContext context,
    string reason,
    bool forcedDungeonExit = false,
    CancellationToken cancellationToken = default)
{
    if (context.State.Dungeon.CurrentDungeonId == 0 && !context.State.Dungeon.InSelection)
    {
        logger.LogWarning(
            "Ignored {Reason} return-to-town request because the user is not in a dungeon or dungeon selection.",
            reason);
        return;
    }

    if (context.State.Dungeon.CurrentDungeonId != 0 && !context.State.Dungeon.TutorialActive)
    {
        await SettleDungeonCreatureHungerAsync(context, 
            notifyClient: true,
            cancellationToken);
    }

    var leavingDungeonId = context.State.Dungeon.CurrentDungeonId;
    var leftDungeonSelection = context.State.Dungeon.InSelection;
    var gainedExperience = context.State.Dungeon.MonsterExperienceGained;
    var leveledUp = context.State.Dungeon.LeveledUp;
    if (context.State.Character.Active is not null && gainedExperience > 0)
    {
        var savedProgression = await store.SaveCharacterProgressionAsync(
            context.State.Login.AccountName,
            context.State.Character.Active.Id,
            context.State.Character.Active.Level,
            context.State.Character.Active.Experience,
            context.State.Skill.CurrentSp,
            cancellationToken);
        if (savedProgression is not null)
        {
            context.State.Character.Active = context.State.Character.Active with
            {
                Level = savedProgression.Level,
                Experience = savedProgression.Experience,
                Sp = savedProgression.Sp
            };
        }
    }

    var hasBlackDiamond = false;
    if (context.State.Character.Active is not null)
    {
        var blackDiamondState = await store.GetPremiumServiceAsync(
            context.State.Login.AccountName,
            cancellationToken);
        hasBlackDiamond = blackDiamondState.Active;
        if (blackDiamondState.Active
            && blackDiamondState.ExpiresAt is { } blackDiamondExpiry)
        {
            context.State.Premium.ExpiryUnix[
                GameProtocolEngine.BlackDiamondServiceType] =
                blackDiamondExpiry;
        }
        else
        {
            context.State.Premium.ExpiryUnix.TryRemove(
                GameProtocolEngine.BlackDiamondServiceType,
                out _);
        }
    }

    var enteredWeakness = false;
    if (context.State.Character.Active is not null
        && !context.State.Character.Active.IsWeakened
        && DungeonWeaknessPolicy.ShouldApply(
            context.State.Character.Active.Level,
            forcedDungeonExit,
            dungeonActive: leavingDungeonId != 0,
            dungeonCleared: context.State.Dungeon.ClearEnabled,
            hasBlackDiamond))
    {
        var weakenedCharacter = await store.SetCharacterWeaknessRecoveryAsync(
            context.State.Character.Active.Id,
            DungeonWeaknessPolicy.InitialRecovery,
            cancellationToken);
        if (weakenedCharacter is not null)
        {
            context.State.Character.Active = weakenedCharacter;
            context.State.Currency.Gold = context.State.Character.Active.Gold;
            enteredWeakness = true;
        }
        else
        {
            logger.LogError(
                "Could not persist weakness for character {CharacterId} while returning from dungeon {DungeonId}; the client will retain normal stamina.",
                context.State.Character.Active.Id,
                leavingDungeonId);
        }
    }

    context.State.Dungeon.CurrentDungeonId = 0;
    context.State.Dungeon.Layout = null;
    context.State.Dungeon.Room = null;
    context.State.Dungeon.InSelection = false;
    context.State.Dungeon.SelectionDeadline = null;
    context.State.Dungeon.SelectionWorldMap = null;
    context.State.Dungeon.AliveMonsters.Clear();
    context.State.Dungeon.ReportedBossDeaths.Clear();
    context.State.Dungeon.BossCascadeDeaths.Clear();
    context.State.Dungeon.DestroyedPassiveObjects.Clear();
    context.State.Dungeon.GroundItems.ResetDungeon();
    context.State.Dungeon.Run.Stop();
    context.State.Dungeon.TutorialActive = false;
    context.State.Dungeon.CharacterAlive = true;
    context.State.Dungeon.DeathGeneration++;
    context.State.Dungeon.DeathTimeout = null;
    context.State.Dungeon.Difficulty = 0;
    context.State.Dungeon.ClearEnabled = false;
    context.State.Dungeon.ResultSent = false;
    context.State.Dungeon.CardLayoutSent = false;
    context.State.Dungeon.CardRightsSent = false;
    context.State.Dungeon.CardSelectionDeadline = null;
    context.State.Dungeon.ClearReward = null;
    context.State.Dungeon.FreeCardIndex = null;
    context.State.Dungeon.GoldCardIndex = null;
    context.State.Dungeon.MonsterExperienceGained = 0;
    context.State.Dungeon.ClearExperience = null;
    context.State.Dungeon.ConsumedFatigue = 0;
    context.State.Dungeon.CreatureStomachCheckpointSeconds = null;
    context.State.Dungeon.LeveledUp = false;
    context.State.Dungeon.NormalMonsterKills = 0;
    context.State.Dungeon.ChampionMonsterKills = 0;
    context.State.Dungeon.BossMonsterKills = 0;
    context.State.Dungeon.ChampionRewardOrdinal = 0;
    context.State.Character.TownId = context.State.Dungeon.ReturnTownId;
    context.State.Character.AreaId = context.State.Dungeon.ReturnAreaId;
    context.State.Character.X = context.State.Dungeon.ReturnX;
    context.State.Character.Y = context.State.Dungeon.ReturnY;
    context.State.Character.Direction = context.State.Dungeon.ReturnDirection;
    context.State.Character.MovementValue = 100;
    await SessionLocationPersistence.SaveCurrentLocationAsync(store, context.State, cancellationToken);

    var townState = GameProtocolEngine.CreateUserState(
        context.State.Character.LocalUserId,
        state: 0);
    await context.Outbound.SendAsync(townState, "DUNGEON", cancellationToken);

    var townUser = new GameTownUser(
        context.State.Character.LocalUserId,
        context.State.Character.X,
        context.State.Character.Y,
        context.State.Character.Direction);
    var userArea = GameProtocolEngine.CreateUserArea(
        context.State.Character.TownId,
        context.State.Character.AreaId,
        townUser);
    await context.Outbound.SendAsync(userArea, "DUNGEON", cancellationToken);

    var areaUsers = GameProtocolEngine.CreateAreaUsers(
        context.State.Character.TownId,
        context.State.Character.AreaId,
        [townUser]);
    await context.Outbound.SendAsync(areaUsers, "DUNGEON", cancellationToken);
    context.State.Character.AreaRosterInitialized = true;
    await SendTownFatigueStateAsync(context, cancellationToken);
    await SendTownAppearanceAsync(context, waitForTownActor: true, cancellationToken);
    await SendSilentTownStaminaStateAsync(context, cancellationToken);
    await SendEnterGameWorldCompleteAsync(context, cancellationToken);
    // The town return bypasses SET_USER_AREA, so refresh the
    // town presence (and any same-area residents' view) here.
    PublishTownPresence(context);
    if (leveledUp)
    {
        await SendCurrentSkillInfoAsync(context, cancellationToken);
        await SendAcceptableQuestListAsync(context, cancellationToken);
    }

    if (leftDungeonSelection && leavingDungeonId == 0)
    {
        logger.LogInformation(
            "Dungeon selection returned user {UserId} to town through {Reason}.",
            context.State.Character.LocalUserId,
            reason);
    }
    else
    {
        logger.LogInformation(
            "Dungeon {DungeonId} returned user {UserId} to town through {Reason}; exp={Experience}, weakened={IsWeakened}, enteredWeakness={EnteredWeakness}, blackDiamond={HasBlackDiamond}.",
            leavingDungeonId,
            context.State.Character.LocalUserId,
            reason,
            gainedExperience,
            context.State.Character.Active?.IsWeakened ?? false,
            enteredWeakness,
            hasBlackDiamond);
    }
}

public async Task SendDungeonSelectionEligibilityAsync(
    ConnectionContext context,
    CancellationToken cancellationToken = default)
{
    var questsMet = context.State.Dungeon.SelectionWorldMap?.MeetsHellQuests(context.State.Quest.CompletedIds) == true;
    var missingItems = context.State.Dungeon.SelectionWorldMap is { HasHellDungeon: true }
        && !context.State.Dungeon.SelectionWorldMap.HasHellItems(context.State.Inventory.Main.Values);
    var packet = GameProtocolEngine.CreateEnterSelectDungeon(
        questsMet, missingItems ? [(ushort)0] : []);
    await context.Outbound.SendAsync(packet, "DUNGEON", cancellationToken);
    logger.LogInformation(
        "World map {WorldMapId} eligibility for {CharacterId}: hellQuests={QuestsMet}, missingHellItems={MissingItems}.",
        context.State.Dungeon.SelectionWorldMap?.Id, context.State.Character.Active?.Id, questsMet, missingItems);
}

public async Task ConsumeDungeonRoomFatigueAsync(
    ConnectionContext context,
    byte roomX,
    byte roomY,
    CancellationToken cancellationToken = default)
{
    if (context.State.Character.Active is null || context.State.Dungeon.CurrentDungeonId == 0)
    {
        return;
    }

    var premiumFatigue = HasActivePremiumService(context, GameProtocolEngine.BlackDiamondServiceType)
        ? GameProtocolEngine.BlackDiamondBonusFatigue
        : (ushort)0;
    var maximumFatigue = checked((ushort)(
        GameProtocolEngine.DefaultMaximumFatigue + premiumFatigue));
    var consumption =
        await store.ConsumeDungeonRoomFatigueAsync(
            context.State.Character.Active.Id,
            maximumFatigue,
            cancellationToken);
    if (consumption.Character is null)
    {
        logger.LogWarning(
            "Could not consume dungeon-room fatigue because character {CharacterId} was not found.",
            context.State.Character.Active.Id);
        return;
    }

    context.State.Character.Active = consumption.Character;
    if (!consumption.Consumed)
    {
        logger.LogInformation(
            "Dungeon {DungeonId} room ({RoomX},{RoomY}) consumed no fatigue because user {UserId} is already at {UsedFatigue}/{MaximumFatigue}.",
            context.State.Dungeon.CurrentDungeonId,
            roomX,
            roomY,
            context.State.Character.LocalUserId,
            consumption.UsedFatigue,
            maximumFatigue);
        return;
    }

    context.State.Dungeon.ConsumedFatigue++;

    var fatigue = GameProtocolEngine.CreateFatigue(
        consumption.UsedFatigue,
        maximumFatigue,
        premiumFatigue);
    await context.Outbound.SendAsync(fatigue, "DUNGEON", cancellationToken);
    logger.LogInformation(
        "Dungeon {DungeonId} room ({RoomX},{RoomY}) consumed one fatigue for user {UserId}: used {PreviousUsedFatigue}->{UsedFatigue}, remaining={RemainingFatigue}/{MaximumFatigue}.",
        context.State.Dungeon.CurrentDungeonId,
        roomX,
        roomY,
        context.State.Character.LocalUserId,
        consumption.PreviousUsedFatigue,
        consumption.UsedFatigue,
        maximumFatigue - consumption.UsedFatigue,
        maximumFatigue);
}

public async Task GrantDungeonCreatureExperienceAsync(
    ConnectionContext context,
    CancellationToken cancellationToken = default)
{
    if (context.State.Dungeon.ConsumedFatigue == 0
        || context.State.Character.Active is null
        || !context.State.Inventory.Creature.TryGetValue(
            CharacterCreatureInventoryLayout.EquippedCreatureSlot,
            out var creature)
        || creature.CountOrValue == 0
        || (creature.CreatureStomach ?? 100) == 0
        || !itemCatalog.TryGetDefinition(
            creature.ItemId,
            out var creatureDefinition)
        || !CharacterCreatureInventoryLayout.IsCreature(
            creatureDefinition)
        || creatureDefinition.CreatureSubType == 1)
    {
        context.State.Dungeon.ConsumedFatigue = 0;
        return;
    }

    var previousLevel = creature.CreatureLevel ?? 1;
    var bonusRate =
        GetEquippedCreatureExperienceBonusRate(context, previousLevel);
    var grant = creatureExperienceCatalog.Grant(
        creature.CreatureExperience ?? 0,
        previousLevel,
        creatureDefinition.CreatureSpecies,
        context.State.Dungeon.ConsumedFatigue,
        bonusRate);
    if (grant.GrantedExperience == 0)
    {
        context.State.Dungeon.ConsumedFatigue = 0;
        return;
    }

    var plannedCreatureInventory = context.State.Inventory.Creature.ToDictionary();
    plannedCreatureInventory[
        CharacterCreatureInventoryLayout.EquippedCreatureSlot] =
        creature with
        {
            CreatureExperience = grant.Experience,
            CreatureLevel = grant.Level
        };
    var saved = await store.SaveCharacterItemSpacesAsync(
        context.State.Login.AccountName,
        context.State.Character.Active.Id,
        context.State.Currency.Gold,
        context.State.Inventory.Main.Values,
        context.State.Inventory.Equipped.Values,
        context.State.Inventory.Warehouse.Values,
        cancellationToken,
        avatarInventory: context.State.Inventory.Avatar.Values,
        creatureInventory: plannedCreatureInventory.Values);
    if (saved is null)
    {
        logger.LogWarning(
            "Could not persist Creature experience for character {CharacterId} after clearing dungeon {DungeonId}.",
            context.State.Character.Active.Id,
            context.State.Dungeon.CurrentDungeonId);
        return;
    }

    context.State.Inventory.Creature = plannedCreatureInventory;
    context.State.Character.Active = context.State.Character.Active with
    {
        CreatureInventory = plannedCreatureInventory.Values
            .OrderBy(item => item.Slot)
            .ToList()
    };
    context.State.Dungeon.ConsumedFatigue = 0;
    var experienceNotification =
        GameProtocolEngine.CreateCreatureGainExperience(
            grant.Level,
            grant.Experience);
    await context.Outbound.SendAsync(experienceNotification, "DUNGEON", cancellationToken);
    logger.LogInformation(
        "Dungeon {DungeonId} granted Creature uid {CreatureUid} {GrantedExperience} experience from {ConsumedFatigue} fatigue and {BonusExperience} artifact bonus: exp {PreviousExperience}->{Experience}, level {PreviousLevel}->{Level}.",
        context.State.Dungeon.CurrentDungeonId,
        creature.CountOrValue,
        grant.GrantedExperience,
        grant.BaseExperience,
        grant.BonusExperience,
        grant.PreviousExperience,
        grant.Experience,
        grant.PreviousLevel,
        grant.Level);
}

public async Task SendCardSelectionSnapshotAsync(
    ConnectionContext context,
    bool inventoryChanged,
    CancellationToken cancellationToken = default)
{
    if (context.State.Dungeon.ClearReward is null)
    {
        return;
    }

    var selectCardReply = GameProtocolEngine.CreateSelectCardReply(
        context.State.Dungeon.FreeCardIndex,
        context.State.Dungeon.GoldCardIndex,
        goldRewardRevealed: context.State.Dungeon.GoldCardIndex.HasValue,
        goldRewardItemId: context.State.Dungeon.ClearReward.GoldItemId);
    await context.Outbound.SendAsync(selectCardReply, "DUNGEON", cancellationToken);
    if (!inventoryChanged)
    {
        return;
    }

    var inventoryRefresh = builders.CreateMainInventoryPacket(
        context.State.Currency.Gold,
        context.State.Currency.VictoryPoints,
        context.State.Inventory.Main.Values);
    await context.Outbound.SendAsync(inventoryRefresh, "DUNGEON", cancellationToken);
}

public async Task<bool> TryAutomaticallySelectFreeCardAsync(
    ConnectionContext context,
    string source,
    CancellationToken cancellationToken = default)
{
    if (context.State.Dungeon.ClearReward is null || context.State.Dungeon.FreeCardIndex.HasValue)
    {
        return true;
    }

    var automaticCardIndex =
        ClearRewardCardPlanner.FindLowestAvailableFreeCardIndex(
            GameProtocolEngine.ClearRewardCardColumnCount,
            []);
    if (!automaticCardIndex.HasValue)
    {
        return true;
    }

    var automaticGrant = await GrantFreeClearRewardAsync(
        context,
        automaticCardIndex.Value);
    if (!automaticGrant.Saved)
    {
        logger.LogWarning(
            "Could not automatically settle dungeon {DungeonId} free card for user {UserId} through {Source}",
            context.State.Dungeon.CurrentDungeonId,
            context.State.Character.LocalUserId,
            source);
        return false;
    }

    // DNF 1.0.1.9 consumes the same CMD 74 ownership snapshot
    // for manual and TimerCardSelect assignments.
    await SendCardSelectionSnapshotAsync(
        context,
        automaticGrant.InventoryChanged,
        cancellationToken);
    logger.LogInformation(
        "Dungeon {DungeonId} automatically selected free card {CardIndex} for user {UserId} through {Source}; reward={RewardItemId}",
        context.State.Dungeon.CurrentDungeonId,
        automaticCardIndex.Value,
        context.State.Character.LocalUserId,
        source,
        automaticGrant.RewardItemId);
    return true;
}

public async Task<(bool Saved, bool[] Granted)> SaveClearRewardEquipmentAsync(
    ConnectionContext context,
    int targetGold,
    IReadOnlyList<ushort?> itemIds,
    CancellationToken cancellationToken = default)
{
    var granted = new bool[itemIds.Count];
    if (context.State.Character.Active is null)
    {
        return (false, granted);
    }

    var plannedInventory = context.State.Inventory.Main.ToDictionary(
        pair => pair.Key,
        pair => pair.Value);
    var plannedWarehouseCapacity = context.State.Inventory.WarehouseCapacity;
    var overflowMails = new List<CreateCharacterMailRequest>();
    for (var index = 0; index < itemIds.Count; index++)
    {
        if (!itemIds[index].HasValue)
        {
            continue;
        }

        if (CharacterWarehouseProgression.TryGetItemTargetCapacity(
                itemIds[index]!.Value,
                out var targetCapacity))
        {
            plannedWarehouseCapacity = Math.Max(
                plannedWarehouseCapacity,
                targetCapacity);
            granted[index] = true;
            continue;
        }

        var rewardItem = CharacterItemIdentity.Ensure(
            new CharacterMailAttachmentRecord(
            itemIds[index]!.Value,
            1,
            Durability: itemCatalog.GetInitialDurability(
                itemIds[index]!.Value),
            SealState: CharacterItemSealing.GetInitialSealState(
                itemCatalog,
                itemIds[index]!.Value)),
            itemCatalog);
        if (CharacterInventoryPlanner.TryPlace(
                plannedInventory,
                rewardItem,
                itemCatalog,
                GetInventoryWeightLimit(context, context.State.Character.Active),
                out var plan,
                out _))
        {
            plannedInventory = plan.Inventory;
            granted[index] = true;
            continue;
        }

        if (!itemCatalog.TryGetDefinition(
                rewardItem.ItemId,
                out _))
        {
            continue;
        }

        // Card rewards must never become ground drops. Preserve
        // the generated instance in the same inventory/mail transaction.
        overflowMails.AddRange(CharacterPacketBuilders.CreateOverflowMailRequests(
            [rewardItem],
            "Dungeon clear reward"));

        granted[index] = true;
    }

    var normalizedGold = Math.Max(0, targetGold);
    if (normalizedGold == context.State.Currency.Gold
        && !granted.Any(value => value)
        && overflowMails.Count == 0)
    {
        return (true, granted);
    }

    var saved = await store.SaveCharacterInventoryAsync(
        context.State.Login.AccountName,
        context.State.Character.Active.Id,
        normalizedGold,
        plannedInventory.Values,
        context.State.Inventory.Equipped.Values,
        cancellationToken,
        overflowMails,
        warehouseCapacity: plannedWarehouseCapacity,
        expectedWarehouseCapacity: context.State.Inventory.WarehouseCapacity);
    if (saved is null)
    {
        return (false, new bool[itemIds.Count]);
    }

    var warehouseCapacityChanged =
        AdoptSavedWarehouseUpgradeState(context, saved);
    context.State.Currency.Gold = normalizedGold;
    if (overflowMails.Count > 0)
    {
        characterSessions.NotifyMailReceived(context.State.Character.Active.CharacterNo);
    }

    if (warehouseCapacityChanged)
    {
        await SendWarehouseStateAsync(context, cancellationToken);
    }

    return (true, granted);
}

public async Task CompleteClearQuestTriggersAsync(
    ConnectionContext context,
    string source,
    CancellationToken cancellationToken = default)
{
    if (context.State.Character.Active is null || context.State.Dungeon.CurrentDungeonId == 0)
    {
        return;
    }

    var completedSimpleClearQuestIds = context.State.Quest.Current
        .Where(quest => quest.Trigger != 0)
        .Where(quest =>
            questCatalog.TryGetDefinition(
                quest.QuestId,
                out var definition)
            && ((context.State.Dungeon.ClearEnabled
                    && QuestDungeonClearRequirement.IsSimpleClear(definition)
                    && QuestDungeonClearRequirement.Matches(
                        definition, context.State.Dungeon.CurrentDungeonId, context.State.Dungeon.Difficulty))
                || context.State.Dungeon.Run.HasClearedMap(
                    QuestMapRequirement.GetTargetMapId(definition))))
        .Select(quest => quest.QuestId)
        .ToHashSet();
    if (completedSimpleClearQuestIds.Count == 0)
    {
        return;
    }

    var plannedQuests = context.State.Quest.Current
        .Select(quest => completedSimpleClearQuestIds.Contains(quest.QuestId)
            ? quest with { Trigger = 0 }
            : quest)
        .ToList();
    var savedQuestProgress = await store.SaveCharacterQuestsAsync(
        context.State.Login.AccountName,
        context.State.Character.Active.Id,
        plannedQuests,
        context.State.Quest.CompletedIds,
        cancellationToken: cancellationToken);
    if (savedQuestProgress is null)
    {
        logger.LogWarning(
            "Could not persist clear quest progress after {Source} in dungeon {DungeonId}.",
            source,
            context.State.Dungeon.CurrentDungeonId);
        return;
    }

    context.State.Quest.Current = plannedQuests;
    context.State.Character.Active = context.State.Character.Active with
    {
        Quests = savedQuestProgress.Quests,
        CompletedQuestIds = savedQuestProgress.CompletedQuestIds
    };
    foreach (var questId in completedSimpleClearQuestIds.Order())
    {
        var triggerUpdate =
            GameProtocolEngine.CreateSetQuestTriggerReply(
                questId,
                trigger: 0);
        await context.Outbound.SendAsync(triggerUpdate, "DUNGEON", cancellationToken);
        logger.LogInformation(
            "Completed clear quest {QuestId} after {Source} in dungeon {DungeonId} difficulty {Difficulty}.",
            questId,
            source,
            context.State.Dungeon.CurrentDungeonId,
            context.State.Dungeon.Difficulty);
    }
}

public async Task EnableEmptyBossRoomSettlementAsync(
    ConnectionContext context,
    DungeonRoomDefinition room,
    CancellationToken cancellationToken = default)
{
    if (room.Monsters.Length != 0
        || context.State.Dungeon.ClearEnabled
        || room.MapType != DungeonMapType.Boss)
    {
        return;
    }

    // War-room scripts (2000-2002) use timed dynamic spawns
    // rather than static .map monster rows. Until that mode is
    // simulated, an empty terminal room must remain settleable.
    context.State.Dungeon.ClearEnabled = true;
    var enableClear = GameProtocolEngine.CreateEnableClearDungeon();
    await context.Outbound.SendAsync(enableClear, "DUNGEON", cancellationToken);
    await CompleteClearQuestTriggersAsync(context, 
        "empty boss-room settlement");
    logger.LogInformation(
        "Dungeon {DungeonId} empty boss room ({RoomX},{RoomY}) enabled settlement.",
        context.State.Dungeon.CurrentDungeonId,
        context.State.Dungeon.RoomX,
        context.State.Dungeon.RoomY);
}

public Task GrantMonsterQuestItemsAsync(
    ConnectionContext context,
    int monsterIndex,
    CancellationToken cancellationToken = default) =>
    GrantQuestItemDropsAsync(context, 
        (quest, inventory) => QuestItemDropPlanner.PlanMonster(
            quest,
            context.State.Dungeon.CurrentDungeonId,
            context.State.Dungeon.Difficulty,
            monsterIndex,
            inventory,
            chance => GameRandomSource.Shared.Next(100) < chance),
        $"monster {monsterIndex}");

public Task GrantClearQuestItemsAsync(
    ConnectionContext context,
    CancellationToken cancellationToken = default) =>
    GrantQuestItemDropsAsync(context, 
        (quest, inventory) => QuestItemDropPlanner.PlanClear(
            quest,
            context.State.Dungeon.CurrentDungeonId,
            context.State.Dungeon.Difficulty,
            inventory,
        chance => GameRandomSource.Shared.Next(100) < chance),
        $"dungeon {context.State.Dungeon.CurrentDungeonId} clear");

public decimal GetEquippedCreatureExperienceBonusRate(
    ConnectionContext context,
    byte creatureLevel)
{
    decimal bonusRate = 0;
    foreach (var artifactSlot in new[]
             {
                 CharacterCreatureInventoryLayout.EquippedArtifactRedSlot,
                 CharacterCreatureInventoryLayout.EquippedArtifactBlueSlot,
                 CharacterCreatureInventoryLayout.EquippedArtifactGreenSlot
             })
    {
        if (!context.State.Inventory.Creature.TryGetValue(artifactSlot, out var artifact)
            || !itemCatalog.TryGetDefinition(
                artifact.ItemId,
                out var definition)
            || !CharacterCreatureInventoryLayout.TryGetEquippedSlot(
                definition,
                out var expectedSlot)
            || expectedSlot != artifactSlot
            || definition.CreatureExperienceAmountRate is not decimal rate
            || definition.CreatureMinimumLevel is int minimumLevel
                && minimumLevel > 0
                && creatureLevel < minimumLevel)
        {
            continue;
        }

        bonusRate += rate;
    }

    return bonusRate;
}

public async Task<(bool Saved, bool InventoryChanged, ushort? RewardItemId)>
    GrantFreeClearRewardAsync(
        ConnectionContext context,
        byte selectedCardIndex,
        CancellationToken cancellationToken = default)
{
    if (context.State.Dungeon.ClearReward is null
        || selectedCardIndex >= GameProtocolEngine.ClearRewardCardColumnCount)
    {
        return (false, false, null);
    }

    if (context.State.Dungeon.FreeCardIndex.HasValue)
    {
        return (
            context.State.Dungeon.FreeCardIndex == selectedCardIndex,
            false,
            context.State.Dungeon.ClearReward.FreeItemId
                ?? context.State.Dungeon.ClearReward.PremiumItemId);
    }

    var goldBeforeGrant = context.State.Currency.Gold;
    var settledGold = (int)Math.Min(
        int.MaxValue,
        (long)context.State.Currency.Gold + context.State.Dungeon.ClearReward.FreeGoldAmount);
    var freeGrant = await SaveClearRewardEquipmentAsync(context, 
        settledGold,
        [
            context.State.Dungeon.ClearReward.FreeItemId,
            context.State.Dungeon.ClearReward.PremiumItemId
        ]);
    if (!freeGrant.Saved)
    {
        return (false, false, null);
    }

    context.State.Dungeon.FreeCardIndex = selectedCardIndex;
    context.State.Dungeon.CardSelectionDeadline = null;
    var rewardItemId = freeGrant.Granted[0]
        ? context.State.Dungeon.ClearReward.FreeItemId
        : freeGrant.Granted[1]
            ? context.State.Dungeon.ClearReward.PremiumItemId
            : null;
    return (
        true,
        context.State.Currency.Gold != goldBeforeGrant
            || freeGrant.Granted.Any(granted => granted),
        rewardItemId);
}

public async Task CompleteTutorialAndReturnAsync(
    ConnectionContext context,
    string reason,
    CancellationToken cancellationToken = default)
{
    if (context.State.Character.Active is null
        || !context.State.Dungeon.TutorialActive
        || !context.State.Dungeon.ClearEnabled
        || context.State.Dungeon.AliveMonsters.Count != 0)
    {
        logger.LogWarning(
            "Ignored tutorial completion because its room is not cleared for user {UserId}.",
            context.State.Character.LocalUserId);
        return;
    }

    CharacterQuestRecord? tutorialQuest = null;
    var tutorialQuestWasActive = context.State.Quest.Current.Any(quest =>
        quest.QuestId == QuestCatalog.TutorialCompletionQuestId);
    if (!tutorialQuestWasActive
        && !context.State.Quest.CompletedIds.Contains(QuestCatalog.TutorialCompletionQuestId))
    {
        if (context.State.Quest.Current.Count >= 3)
        {
            logger.LogWarning(
                "Tutorial completion quest {QuestId} was not activated because the active quest list is full.",
                QuestCatalog.TutorialCompletionQuestId);
        }
        else if (!questCatalog.TryGetDefinition(
                     QuestCatalog.TutorialCompletionQuestId,
                     out var definition)
                 || !QuestCatalog.IsTutorialCompletionQuestDefinition(definition))
        {
            logger.LogWarning(
                "Tutorial completion quest {QuestId} is missing or does not match the target client definition.",
                QuestCatalog.TutorialCompletionQuestId);
        }
        else
        {
            tutorialQuest = new CharacterQuestRecord(
                QuestCatalog.TutorialCompletionQuestId,
                Trigger: 0);
        }
    }

    var saved = await store.CompleteCharacterTutorialAsync(
        context.State.Login.AccountName,
        context.State.Character.Active.Id,
        tutorialQuest: tutorialQuest,
        level: context.State.Character.Active.Level,
        experience: context.State.Character.Active.Experience,
        skillPoints: context.State.Skill.CurrentSp,
        cancellationToken: cancellationToken);
    if (saved is null)
    {
        logger.LogWarning(
            "Could not persist tutorial completion for character {CharacterId}.",
            context.State.Character.Active.Id);
        return;
    }

    context.State.Character.Active = context.State.Character.Active with
    {
        TutorialStarted = saved.TutorialStarted,
        Quests = saved.Quests,
        CompletedQuestIds = saved.CompletedQuestIds,
        Level = saved.Level,
        Experience = saved.Experience,
        Sp = saved.Sp
    };
    context.State.Quest.Current = (saved.Quests ?? [])
        .Where(quest => quest.QuestId != ushort.MaxValue)
        .Take(3)
        .ToList();
    context.State.Quest.CompletedIds = (saved.CompletedQuestIds ?? []).ToHashSet();
    var tutorialQuestActivated = !tutorialQuestWasActive
        && context.State.Quest.Current.Any(quest =>
            quest.QuestId == QuestCatalog.TutorialCompletionQuestId);
    if (tutorialQuestActivated)
    {
        var acceptQuest = GameProtocolEngine.CreateAcceptQuestReply(
            QuestCatalog.TutorialCompletionQuestId,
            trigger: 1);
        await context.Outbound.SendAsync(acceptQuest, "TUTORIAL", cancellationToken);

        var readyQuest = GameProtocolEngine.CreateSetQuestTriggerReply(
            QuestCatalog.TutorialCompletionQuestId,
            trigger: 0);
        await context.Outbound.SendAsync(readyQuest, "TUTORIAL", cancellationToken);
        await SendDungeonPermissionsAsync(context, cancellationToken);
        await SendAcceptableQuestListAsync(context, cancellationToken);
        logger.LogInformation(
            "Activated tutorial completion quest {QuestId} for character {CharacterId}.",
            QuestCatalog.TutorialCompletionQuestId,
            context.State.Character.Active.Id);
    }

    await ReturnToTownFromDungeonAsync(context, reason);
}

    // ---- Dungeon passive-object drop registry (migrated verbatim).

    internal GamePassiveObjectDrop[] RegisterPassiveObjectDrops(
        DungeonRoomDefinition room,
        DungeonGroundItemState groundItems,
        ushort ownerUserId,
        byte roomX,
        byte roomY)
    {
        var result = new List<GamePassiveObjectDrop>(room.PassiveItemSpawns.Length);
        foreach (var spawn in room.PassiveItemSpawns)
        {
            var groundItem = groundItems.AddPassive(
                spawn.Drop,
                ownerUserId,
                spawn.PassiveObjectIndex,
                spawn.AssociationSlot,
                roomX,
                roomY,
                x: unchecked((short)spawn.X),
                y: unchecked((short)spawn.Y),
                catalog: itemCatalog);
            result.Add(new GamePassiveObjectDrop(
                spawn.AssociationSlot,
                groundItem.GroundId,
                groundItem.ItemId,
                groundItem.GetClientAddInfo(itemCatalog),
                groundItem.OwnerUserId));
        }

        return result.ToArray();
    }

    internal GamePassiveObjectDrop[] GetRegisteredPassiveObjectDrops(
        DungeonGroundItemState groundItems,
        byte roomX,
        byte roomY) =>
        groundItems.GetPassiveObjectItems(roomX, roomY)
            .Select(item => new GamePassiveObjectDrop(
                item.PassiveItemSlot,
                item.GroundId,
                item.ItemId,
                item.GetClientAddInfo(itemCatalog),
                item.OwnerUserId))
            .ToArray();

    internal EntranceContent LoadContent()
    {
        if (_content is not null)
        {
            return _content;
        }

        lock (_contentGate)
        {
            if (_content is not null)
            {
                return _content;
            }

            byte[]? channelScript = null;
            const string virtualPath = "etc/channel_info.etc";
            if (scripts.FileExists(virtualPath))
            {
                channelScript = scripts.ReadAllBytes(virtualPath);
            }
            else
            {
                logger.LogWarning(
                    "{VirtualPath} is missing from {Source}; protocol 9 download is disabled.",
                    virtualPath,
                    scripts.SourceDescription);
            }

            var channel = options.Channel;
            var metadata = EntranceMetadata.BuildSingleLocalChannel(new EntranceChannel(
                channel.Name,
                channel.Host,
                channel.Port,
                channel.ChannelNumber,
                channel.MaximumUsers,
                channel.CurrentUsers));
            _content = new EntranceContent(channelScript, metadata);
            return _content;
        }
    }

public int ApplyLevelUpSkillPoints(ConnectionContext context, CharacterExperienceGrant progression)
{
    var granted = spCatalog.CalculateLevelUpReward(
        progression.PreviousLevel,
        progression.Level);
    if (granted == 0)
    {
        return 0;
    }

    context.State.Skill.CurrentSp = checked(context.State.Skill.CurrentSp + granted);
    return granted;
}
}
