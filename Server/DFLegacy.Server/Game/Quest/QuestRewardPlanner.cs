namespace DFLegacy.Server;

public sealed record QuestInsertedReward(
    ushort Slot,
    ushort ItemId,
    uint CountOrSeed);

public sealed record QuestRewardPlan(
    bool Success,
    int Gold,
    List<CharacterItemRecord> Inventory,
    IReadOnlyList<QuestRewardItemDefinition> GrantedItems,
    IReadOnlyList<CharacterMailAttachmentRecord> MailedItems,
    IReadOnlyList<QuestInsertedReward> InsertedItems,
    string? Error,
    ushort WarehouseCapacity)
{
    public static QuestRewardPlan Failed(string error, CharacterRecord character) =>
        new(
            false,
            character.Gold,
            character.Inventory?.ToList() ?? [],
            [],
            [],
            [],
            error,
            CharacterWarehouseProgression.Normalize(character.WarehouseCapacity));
}

public static class QuestRewardPlanner
{
    public static QuestRewardPlan Create(
        CharacterRecord character,
        QuestDefinition quest,
        int selectionIndex,
        ItemCatalog itemCatalog,
        uint weightLimit)
    {
        var selectable = quest.SelectableRewardItems
            .Where(reward => reward.Matches(character.Job, character.GrowType))
            .ToArray();
        if (selectable.Length > 0
            && (selectionIndex < 0 || selectionIndex >= selectable.Length))
        {
            return QuestRewardPlan.Failed("A valid selectable quest reward is required.", character);
        }

        var rewards = quest.ResolveRewardItems(
            character.Job,
            character.GrowType,
            selectionIndex);
        var normalizedInventory = CharacterInventoryPlanner.Normalize(
            character.Inventory ?? [],
            itemCatalog);
        var inventory = normalizedInventory.Inventory;
        var mailedItems = normalizedInventory.OverflowItems.ToList();
        var insertedItems = new List<QuestInsertedReward>();
        var warehouseCapacity = CharacterWarehouseProgression.Normalize(
            character.WarehouseCapacity);

        foreach (var reward in rewards)
        {
            if (CharacterWarehouseProgression.TryGetItemTargetCapacity(
                    reward.ItemId,
                    out var targetCapacity))
            {
                warehouseCapacity = Math.Max(warehouseCapacity, targetCapacity);
                continue;
            }

            var item = CharacterItemIdentity.Ensure(
                new CharacterMailAttachmentRecord(
                reward.ItemId,
                reward.Count,
                Durability: itemCatalog.GetInitialDurability(reward.ItemId),
                SealState: CharacterItemSealing.GetInitialSealState(
                    itemCatalog,
                    reward.ItemId)),
                itemCatalog);
            if (CharacterInventoryPlanner.TryPlace(
                    inventory,
                    item,
                    itemCatalog,
                    weightLimit,
                    out var placement,
                    out var failure))
            {
                foreach (var slot in placement.ChangedSlots)
                {
                    var inserted = placement.Inventory[slot];
                    var previousCount = inventory.TryGetValue(slot, out var previous)
                        && previous.ItemId == inserted.ItemId
                            ? previous.CountOrValue
                            : 0;
                    if (inserted.CountOrValue > previousCount)
                    {
                        insertedItems.Add(new QuestInsertedReward(
                            slot,
                            inserted.ItemId,
                            inserted.CountOrValue - previousCount));
                    }
                }

                inventory = placement.Inventory;
                continue;
            }

            if (failure == InventoryPlacementFailure.InvalidItem)
            {
                return QuestRewardPlan.Failed(
                    $"Quest reward item {reward.ItemId} is not present in the item catalog.",
                    character);
            }

            mailedItems.AddRange(
                CharacterInventoryPlanner.CreateMailAttachments(item, itemCatalog));
        }

        var rewardedGold = (long)Math.Max(0, character.Gold) + Math.Max(0, quest.GoldReward);
        if (rewardedGold > int.MaxValue)
        {
            return QuestRewardPlan.Failed("The character gold limit would be exceeded.", character);
        }

        return new QuestRewardPlan(
            true,
            (int)rewardedGold,
            inventory.Values.OrderBy(item => item.Slot).ToList(),
            rewards,
            mailedItems,
            insertedItems,
            null,
            warehouseCapacity);
    }
}
