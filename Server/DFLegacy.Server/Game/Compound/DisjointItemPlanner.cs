using DFLegacy.Protocol;

namespace DFLegacy.Server;

public enum DisjointItemFailure
{
    None,
    InvalidRequest,
    InvalidItem,
    InventoryFull
}

public sealed record DisjointItemPlan(
    ushort SourceSlot,
    byte ItemSpace,
    ushort SourceItemId,
    Dictionary<ushort, CharacterItemRecord> MainInventory,
    IReadOnlyList<GameDisjointRewardEntry> ResponseRewards);

public static class DisjointItemPlanner
{
    public static bool TryCreate(
        IReadOnlyDictionary<ushort, CharacterItemRecord> mainInventory,
        ushort sourceSlot,
        byte itemSpace,
        uint weightLimit,
        int additionalItemRoll,
        int jackpotRoll,
        ItemCatalog itemCatalog,
        DisjointCatalog disjointCatalog,
        out DisjointItemPlan plan,
        out DisjointItemFailure failure)
    {
        plan = null!;
        failure = DisjointItemFailure.None;
        if (itemSpace != 0
            || sourceSlot is < CharacterInventoryLayout.EquipmentSlotStart
                or >= CharacterInventoryLayout.EquipmentSlotEnd)
        {
            failure = DisjointItemFailure.InvalidRequest;
            return false;
        }

        if (!mainInventory.TryGetValue(sourceSlot, out var sourceItem)
            || sourceItem.ItemId == 0
            || sourceItem.CountOrValue != 1
            || !itemCatalog.TryGetDefinition(sourceItem.ItemId, out var sourceDefinition)
            || sourceDefinition.ScriptKind != ItemScriptKind.Equipment
            || sourceDefinition.InventoryCategory != ItemInventoryCategory.Equipment
            || sourceDefinition.AttachType == ItemAttachType.TradeDelete
            || !disjointCatalog.TryGenerate(
                sourceDefinition,
                additionalItemRoll,
                jackpotRoll,
                itemCatalog,
                out var generatedRewards))
        {
            failure = DisjointItemFailure.InvalidItem;
            return false;
        }

        var plannedInventory = mainInventory.ToDictionary(
            pair => pair.Key,
            pair => pair.Value);
        plannedInventory.Remove(sourceSlot);
        var responseRewards = new List<GameDisjointRewardEntry>();
        foreach (var reward in generatedRewards)
        {
            if (CharacterWarehouseProgression.TryGetItemTargetCapacity(
                    reward.ItemId,
                    out _))
            {
                responseRewards.Add(new GameDisjointRewardEntry(
                    ushort.MaxValue,
                    reward.ItemId,
                    reward.Count));
                continue;
            }

            var beforePlacement = plannedInventory;
            if (!CharacterInventoryPlanner.TryPlace(
                    beforePlacement,
                    new CharacterMailAttachmentRecord(reward.ItemId, reward.Count),
                    itemCatalog,
                    weightLimit,
                    out var placement,
                    out _))
            {
                failure = DisjointItemFailure.InventoryFull;
                return false;
            }

            foreach (var changedSlot in placement.ChangedSlots)
            {
                var placedItem = placement.Inventory[changedSlot];
                var previousCount = beforePlacement.TryGetValue(changedSlot, out var previous)
                    && previous.ItemId == placedItem.ItemId
                        ? previous.CountOrValue
                        : 0;
                var addedCount = checked(placedItem.CountOrValue - previousCount);
                if (addedCount != 0)
                {
                    responseRewards.Add(new GameDisjointRewardEntry(
                        changedSlot,
                        placedItem.ItemId,
                        addedCount));
                }
            }

            plannedInventory = placement.Inventory;
        }

        if (responseRewards.Count == 0 || responseRewards.Count > byte.MaxValue)
        {
            failure = DisjointItemFailure.InventoryFull;
            return false;
        }

        plan = new DisjointItemPlan(
            sourceSlot,
            itemSpace,
            sourceItem.ItemId,
            plannedInventory,
            responseRewards.AsReadOnly());
        return true;
    }
}
