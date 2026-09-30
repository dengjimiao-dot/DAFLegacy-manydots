using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// Quest-flow helpers (07 §4.7): triggers, seeking refresh, quest-item
/// grants and the acceptable-quest list.
/// </summary>
public sealed partial class ChannelSessionSenders
{
    public IReadOnlyList<ushort> GetAcceptableQuestIds(ConnectionContext context) => context.State.Character.Active is null
        ? []
        : questCatalog.GetAcceptableQuestIds(
            context.State.Character.Active,
            context.State.Quest.Current,
            context.State.Quest.CompletedIds,
            context.State.Character.TownId == 1 ? 2 : null,
            itemCatalog);

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
                RuntimeLogger.Warn(
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
        RuntimeLogger.Warn(
            "Could not persist quest item grants from {Source}; inventory and quest progress were left unchanged.",
            source);
        return;
    }

    var warehouseCapacityChanged =
        AdoptSavedWarehouseUpgradeState(context, saved);
    context.State.Quest.Current = plannedQuests;
    if (changedSlots.Count > 0)
    {
        var itemUpdate = InventoryPacketBuilders.CreateUpdateItemList(
            0,
            changedSlots
                .OrderBy(slot => slot)
                .Select(slot => builders.ToGameInventoryEntry(context.State.Inventory.Main[slot]))
                .ToArray());
        await context.Queue.SendAsync(itemUpdate, "QUEST_ITEMS", cancellationToken);
    }

    if (warehouseCapacityChanged)
    {
        await SendWarehouseStateAsync(context, cancellationToken);
    }

    foreach (var change in triggerChanges)
    {
        var triggerUpdate = QuestPacketBuilders.CreateSetQuestTriggerReply(
            change.QuestId,
            change.NewTrigger);
        await context.Queue.SendAsync(triggerUpdate, "QUEST_TRIGGER", cancellationToken);
    }

    foreach (var grant in grantedDrops)
    {
        RuntimeLogger.Info(
            "Granted quest item from {Source}: quest={QuestId}, item={ItemId}, count={Count}.",
            source,
            grant.QuestId,
            grant.Drop.ItemId,
            grant.Drop.Count);
    }

    }

    public async Task SendAcceptableQuestListAsync(ConnectionContext context, CancellationToken cancellationToken)
    {
    var acceptable = QuestPacketBuilders.CreateAcceptableQuestList(
        GetAcceptableQuestIds(context));
    await context.Queue.SendAsync(acceptable, "QUEST_LIST", cancellationToken);
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
        RuntimeLogger.Warn(
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
            QuestPacketBuilders.CreateSetQuestTriggerReply(
                questId,
                trigger: 0);
        await context.Queue.SendAsync(triggerUpdate, "DUNGEON", cancellationToken);
        RuntimeLogger.Info(
            "Completed clear quest {QuestId} after {Source} in dungeon {DungeonId} difficulty {Difficulty}.",
            questId,
            source,
            context.State.Dungeon.CurrentDungeonId,
            context.State.Dungeon.Difficulty);
    }
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
}
