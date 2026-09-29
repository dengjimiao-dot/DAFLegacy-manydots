namespace DFLegacy.Server;

public sealed record QuestAcceptancePlan(
    bool Success,
    Dictionary<ushort, CharacterItemRecord> Inventory,
    IReadOnlyList<QuestInsertedReward> InsertedItems,
    InventoryPlacementFailure Failure,
    string? Error)
{
    public static QuestAcceptancePlan Failed(
        IReadOnlyDictionary<ushort, CharacterItemRecord> inventory,
        InventoryPlacementFailure failure,
        string error) => new(
            false,
            inventory.ToDictionary(pair => pair.Key, pair => pair.Value),
            [],
            failure,
            error);
}

public static class QuestAcceptancePlanner
{
    public static QuestAcceptancePlan Create(
        IReadOnlyDictionary<ushort, CharacterItemRecord> inventory,
        QuestDefinition quest,
        ItemCatalog itemCatalog,
        uint weightLimit)
    {
        var plannedInventory = inventory.ToDictionary(
            pair => pair.Key,
            pair => pair.Value);
        var changedSlots = new HashSet<ushort>();

        foreach (var dependItem in quest.DependGiveItems)
        {
            var attachment = CharacterItemIdentity.Ensure(
                new CharacterMailAttachmentRecord(
                    dependItem.ItemId,
                    dependItem.Count,
                    Durability: itemCatalog.GetInitialDurability(dependItem.ItemId),
                    SealState: CharacterItemSealing.GetInitialSealState(
                        itemCatalog,
                        dependItem.ItemId)),
                itemCatalog);
            if (!CharacterInventoryPlanner.TryPlace(
                    plannedInventory,
                    attachment,
                    itemCatalog,
                    weightLimit,
                    out var placement,
                    out var failure))
            {
                return QuestAcceptancePlan.Failed(
                    inventory,
                    failure,
                    $"Could not grant quest dependency item {dependItem.ItemId}.");
            }

            plannedInventory = placement.Inventory;
            changedSlots.UnionWith(placement.ChangedSlots);
        }

        var insertedItems = new List<QuestInsertedReward>();
        foreach (var slot in changedSlots.Order())
        {
            var inserted = plannedInventory[slot];
            var previousCount = inventory.TryGetValue(slot, out var previous)
                && previous.ItemId == inserted.ItemId
                    ? previous.CountOrValue
                    : 0;
            if (inserted.CountOrValue <= previousCount)
            {
                continue;
            }

            insertedItems.Add(new QuestInsertedReward(
                slot,
                inserted.ItemId,
                inserted.CountOrValue - previousCount));
        }

        return new QuestAcceptancePlan(
            true,
            plannedInventory,
            insertedItems,
            InventoryPlacementFailure.None,
            null);
    }
}
