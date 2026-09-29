using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// Dungeon reward flows (07 §4.7): clear-card selection and settlement,
/// tutorial hand-off and the passive-object drop registry.
/// </summary>
public sealed partial class ChannelSessionSenders
{
    public async Task SendCardSelectionSnapshotAsync(
    ConnectionContext context,
    bool inventoryChanged,
    CancellationToken cancellationToken = default)
    {
    if (context.State.Dungeon.ClearReward is null)
    {
        return;
    }

    var selectCardReply = DungeonPacketBuilders.CreateSelectCardReply(
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
            GameProtocolConstants.ClearRewardCardColumnCount,
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
    var enableClear = DungeonPacketBuilders.CreateEnableClearDungeon();
    await context.Outbound.SendAsync(enableClear, "DUNGEON", cancellationToken);
    await CompleteClearQuestTriggersAsync(context, 
        "empty boss-room settlement");
    logger.LogInformation(
        "Dungeon {DungeonId} empty boss room ({RoomX},{RoomY}) enabled settlement.",
        context.State.Dungeon.CurrentDungeonId,
        context.State.Dungeon.RoomX,
        context.State.Dungeon.RoomY);
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
        var acceptQuest = QuestPacketBuilders.CreateAcceptQuestReply(
            QuestCatalog.TutorialCompletionQuestId,
            trigger: 1);
        await context.Outbound.SendAsync(acceptQuest, "TUTORIAL", cancellationToken);

        var readyQuest = QuestPacketBuilders.CreateSetQuestTriggerReply(
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

    public async Task<(bool Saved, bool InventoryChanged, ushort? RewardItemId)>
    GrantFreeClearRewardAsync(
        ConnectionContext context,
        byte selectedCardIndex,
        CancellationToken cancellationToken = default)
    {
    if (context.State.Dungeon.ClearReward is null
        || selectedCardIndex >= GameProtocolConstants.ClearRewardCardColumnCount)
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
}
