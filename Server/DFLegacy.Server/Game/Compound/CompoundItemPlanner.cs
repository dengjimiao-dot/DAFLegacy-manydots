using System.Collections.ObjectModel;

namespace DFLegacy.Server;

public enum CompoundItemFailure : byte
{
    None = 0,
    SequenceConflict = 2,
    InventoryFull = 4,
    InsufficientGold = 10,
    InvalidRecipe = 19,
    InsufficientMaterial = 22
}

public readonly record struct CompoundItemConsumption(
    byte ListType,
    ushort Slot,
    uint RemainingCount);

public readonly record struct CompoundItemCreation(
    ushort Slot,
    ushort ItemId,
    uint CountOrValue,
    byte State,
    ushort Durability);

public sealed record CompoundItemPlan(
    IReadOnlyDictionary<ushort, CharacterItemRecord> MainInventory,
    int RemainingGold,
    ushort RecipeItemId,
    byte RecipeType,
    byte CraftCount,
    uint GoldCost,
    IReadOnlyList<CompoundItemConsumption> ConsumedItems,
    IReadOnlyList<CompoundItemCreation> CreatedItems);

public static class CompoundItemPlanner
{
    private const byte MainInventoryListType = 0;

    public static bool TryCreate(
        IReadOnlyDictionary<ushort, CharacterItemRecord> mainInventory,
        int currentGold,
        IReadOnlyCollection<CharacterSkillRecord> skills,
        ushort sourceValue,
        bool sourceIsItemId,
        byte craftCount,
        uint weightLimit,
        CompoundCatalog compoundCatalog,
        ItemCatalog itemCatalog,
        out CompoundItemPlan plan,
        out CompoundItemFailure failure)
    {
        ArgumentNullException.ThrowIfNull(mainInventory);
        ArgumentNullException.ThrowIfNull(skills);
        ArgumentNullException.ThrowIfNull(compoundCatalog);
        ArgumentNullException.ThrowIfNull(itemCatalog);
        plan = null!;
        failure = CompoundItemFailure.InvalidRecipe;
        if (sourceValue == 0 || craftCount == 0 || currentGold < 0)
        {
            return false;
        }

        CharacterItemRecord? recipeItem = null;
        var recipeItemId = sourceValue;
        if (!sourceIsItemId)
        {
            failure = CompoundItemFailure.InsufficientMaterial;
            if (!mainInventory.TryGetValue(sourceValue, out recipeItem)
                || recipeItem.ItemId == 0
                || recipeItem.CountOrValue < craftCount)
            {
                return false;
            }

            recipeItemId = recipeItem.ItemId;
        }

        failure = CompoundItemFailure.InvalidRecipe;
        if (!compoundCatalog.TryGetDefinition(recipeItemId, out var recipe)
            || recipe.Results.Count == 0
            || recipe.RequiredSkills.Any(requirement =>
                !skills.Any(skill =>
                    skill.SkillId == requirement.SkillId
                    && skill.Level >= requirement.Level)))
        {
            return false;
        }

        var totalGold = (ulong)recipe.GoldCost * craftCount;
        if (totalGold > int.MaxValue || totalGold > (ulong)currentGold)
        {
            failure = CompoundItemFailure.InsufficientGold;
            return false;
        }

        var plannedInventory = mainInventory.ToDictionary(
            pair => pair.Key,
            pair => pair.Value);
        var consumedBySlot = new SortedDictionary<ushort, uint>();
        if (!sourceIsItemId)
        {
            ConsumeAtSlot(
                plannedInventory,
                recipeItem!,
                craftCount,
                consumedBySlot);
        }

        foreach (var ingredient in recipe.Ingredients)
        {
            var remaining = (ulong)ingredient.Count * craftCount;
            foreach (var item in plannedInventory.Values
                         .Where(item =>
                             item.ItemId == ingredient.ItemId
                             && item.CountOrValue != 0)
                         .OrderBy(item => item.Slot)
                         .ToArray())
            {
                if (remaining == 0)
                {
                    break;
                }

                var consumedCount = (uint)Math.Min(
                    (ulong)item.CountOrValue,
                    remaining);
                ConsumeAtSlot(
                    plannedInventory,
                    item,
                    consumedCount,
                    consumedBySlot);
                remaining -= consumedCount;
            }

            if (remaining != 0)
            {
                failure = CompoundItemFailure.InsufficientMaterial;
                return false;
            }
        }

        var createdBySlot = new SortedDictionary<ushort, CompoundItemCreation>();
        foreach (var result in recipe.Results)
        {
            var totalResult = (ulong)result.Count * craftCount;
            if (totalResult is 0 or > uint.MaxValue
                || !itemCatalog.TryGetDefinition(result.ItemId, out var resultDefinition))
            {
                failure = CompoundItemFailure.InvalidRecipe;
                return false;
            }

            var attachment = CharacterItemIdentity.Ensure(
                new CharacterMailAttachmentRecord(
                result.ItemId,
                checked((uint)totalResult),
                State: 0,
                Durability: resultDefinition.IsEquipment
                    ? itemCatalog.GetInitialDurability(result.ItemId)
                    : (ushort)0,
                SealState: 0),
                resultDefinition);
            if (!CharacterInventoryPlanner.TryPlace(
                    plannedInventory,
                    attachment,
                    itemCatalog,
                    weightLimit,
                    out var placement,
                    out var placementFailure))
            {
                failure = placementFailure is
                    InventoryPlacementFailure.Full or InventoryPlacementFailure.Overweight
                        ? CompoundItemFailure.InventoryFull
                        : CompoundItemFailure.InvalidRecipe;
                return false;
            }

            plannedInventory = placement.Inventory;
            foreach (var changedSlot in placement.ChangedSlots)
            {
                var created = plannedInventory[changedSlot];
                createdBySlot[changedSlot] = new CompoundItemCreation(
                    changedSlot,
                    created.ItemId,
                    created.CountOrValue,
                    created.State,
                    created.Durability);
            }
        }

        if (totalGold != 0)
        {
            consumedBySlot[CharacterInventoryLayout.GoldSlot] =
                checked((uint)(currentGold - (int)totalGold));
        }

        if (consumedBySlot.Count > byte.MaxValue
            || createdBySlot.Count == 0
            || createdBySlot.Count > byte.MaxValue)
        {
            failure = CompoundItemFailure.InventoryFull;
            return false;
        }

        var consumed = consumedBySlot
            .Select(pair => new CompoundItemConsumption(
                MainInventoryListType,
                pair.Key,
                pair.Value))
            .ToArray();
        var createdItems = createdBySlot.Values.ToArray();
        plan = new CompoundItemPlan(
            new ReadOnlyDictionary<ushort, CharacterItemRecord>(plannedInventory),
            checked(currentGold - (int)totalGold),
            recipeItemId,
            recipe.RecipeType,
            craftCount,
            checked((uint)totalGold),
            Array.AsReadOnly(consumed),
            Array.AsReadOnly(createdItems));
        failure = CompoundItemFailure.None;
        return true;
    }

    private static void ConsumeAtSlot(
        Dictionary<ushort, CharacterItemRecord> inventory,
        CharacterItemRecord item,
        uint count,
        IDictionary<ushort, uint> consumedBySlot)
    {
        var remaining = checked(item.CountOrValue - count);
        consumedBySlot[item.Slot] = remaining;
        if (remaining == 0)
        {
            inventory.Remove(item.Slot);
        }
        else
        {
            inventory[item.Slot] = item with { CountOrValue = remaining };
        }
    }
}
