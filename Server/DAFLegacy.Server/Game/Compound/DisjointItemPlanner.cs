using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

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
    IReadOnlyList<GameDisjointRewardEntry> ResponseRewards)
{
    public Dictionary<ushort, CharacterItemRecord> CreatureInventory { get; init; } = [];
}

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
        out DisjointItemFailure failure) =>
        TryCreate(mainInventory, new Dictionary<ushort, CharacterItemRecord>(),
            sourceSlot, itemSpace, weightLimit, additionalItemRoll, jackpotRoll,
            itemCatalog, disjointCatalog, out plan, out failure);

    public static bool TryCreate(
        IReadOnlyDictionary<ushort, CharacterItemRecord> mainInventory,
        IReadOnlyDictionary<ushort, CharacterItemRecord> creatureInventory,
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
        var validSourceSlot = itemSpace switch
        {
            0 => sourceSlot is >= CharacterInventoryLayout.EquipmentSlotStart
                and < CharacterInventoryLayout.EquipmentSlotEnd,
            7 => sourceSlot is >= CharacterCreatureInventoryLayout.ArtifactBagStart
                and < CharacterCreatureInventoryLayout.ArtifactBagEnd,
            _ => false
        };
        if (!validSourceSlot)
        {
            failure = DisjointItemFailure.InvalidRequest;
            return false;
        }

        var sourceInventory = itemSpace == 7 ? creatureInventory : mainInventory;
        if (!sourceInventory.TryGetValue(sourceSlot, out var sourceItem)
            || sourceItem.ItemId == 0
            || sourceItem.CountOrValue != 1
            || !itemCatalog.TryGetDefinition(sourceItem.ItemId, out var sourceDefinition)
            || sourceDefinition.ScriptKind != ItemScriptKind.Equipment
            || itemSpace == 0
                && sourceDefinition.InventoryCategory != ItemInventoryCategory.Equipment
            || itemSpace == 7
                && !CharacterCreatureInventoryLayout.IsArtifact(sourceDefinition)
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
        var plannedCreatureInventory = creatureInventory.ToDictionary(
            pair => pair.Key,
            pair => pair.Value);
        if (itemSpace == 7)
        {
            plannedCreatureInventory.Remove(sourceSlot);
        }
        else
        {
            plannedInventory.Remove(sourceSlot);
        }
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
            responseRewards.AsReadOnly())
        {
            CreatureInventory = plannedCreatureInventory
        };
        return true;
    }
}
