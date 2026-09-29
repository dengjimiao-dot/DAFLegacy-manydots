namespace DFLegacy.Server;

public static class CharacterInventoryLayout
{
    public const ushort GoldSlot = 0;
    public const ushort RevivalCoinSlot = 1;
    public const ushort MedalSlot = 2;
    public const ushort RevivalCoinItemId = 1;
    public const ushort MedalItemId = 2;
    public const ushort QuickSlotStart = 3;
    public const ushort QuickSlotEnd = 9;
    public const ushort EquipmentSlotStart = 9;
    public const ushort EquipmentSlotEnd = 41;
    public const ushort ConsumableSlotStart = 41;
    public const ushort ConsumableSlotEnd = 73;
    public const ushort MaterialSlotStart = 73;
    public const ushort MaterialSlotEnd = 105;
    public const ushort QuestSlotStart = 105;
    public const ushort QuestSlotEnd = 137;

    public static bool IsQuickSlot(ushort slot) =>
        slot is >= QuickSlotStart and < QuickSlotEnd;

    public static bool IsMainItemSlot(ushort slot) =>
        slot is >= QuickSlotStart and < QuestSlotEnd;

    public static bool IsRevivalCoin(CharacterItemRecord item) =>
        item.ItemId == RevivalCoinItemId;

    public static bool IsCompatibleSlot(ItemDefinition definition, ushort slot)
    {
        if (IsQuickSlot(slot))
        {
            return true;
        }

        return TryGetCategoryRange(
                definition.InventoryCategory,
                out var start,
                out var end)
            && slot >= start
            && slot < end;
    }

    public static bool TryGetCategoryRange(
        ItemInventoryCategory category,
        out ushort start,
        out ushort end)
    {
        (start, end) = category switch
        {
            ItemInventoryCategory.Equipment =>
                (EquipmentSlotStart, EquipmentSlotEnd),
            ItemInventoryCategory.Consumable =>
                (ConsumableSlotStart, ConsumableSlotEnd),
            ItemInventoryCategory.Material =>
                (MaterialSlotStart, MaterialSlotEnd),
            ItemInventoryCategory.Quest =>
                (QuestSlotStart, QuestSlotEnd),
            _ => ((ushort)0, (ushort)0)
        };
        return end > start;
    }
}

public enum InventoryPlacementFailure
{
    None,
    InvalidItem,
    UnsupportedCategory,
    Full,
    Overweight
}

public sealed record CharacterInventoryPlacementPlan(
    ushort DestinationSlot,
    Dictionary<ushort, CharacterItemRecord> Inventory,
    IReadOnlyList<ushort> ChangedSlots,
    ulong TotalWeight);

public sealed record CharacterInventoryNormalizationPlan(
    Dictionary<ushort, CharacterItemRecord> Inventory,
    IReadOnlyList<CharacterMailAttachmentRecord> OverflowItems,
    bool Changed);

public sealed record CharacterWarehouseNormalizationPlan(
    Dictionary<ushort, CharacterItemRecord> Warehouse,
    IReadOnlyList<CharacterMailAttachmentRecord> OverflowItems,
    bool Changed);

public static class CharacterInventoryPlanner
{
    // USERINFO stores the character inventory limit at x10 of the item-script
    // [weight]/[inventory limit] unit used by the client weight manager.
    public const uint CombatStatInventoryLimitScale = 10;

    public static bool TryConsumeRevivalCoin(
        IReadOnlyDictionary<ushort, CharacterItemRecord> inventory,
        out Dictionary<ushort, CharacterItemRecord> plannedInventory)
    {
        plannedInventory = inventory.ToDictionary(pair => pair.Key, pair => pair.Value);
        if (!plannedInventory.TryGetValue(
                CharacterInventoryLayout.RevivalCoinSlot,
                out var revivalCoins)
            || !CharacterInventoryLayout.IsRevivalCoin(revivalCoins)
            || revivalCoins.CountOrValue == 0)
        {
            return false;
        }

        if (revivalCoins.CountOrValue == 1)
        {
            plannedInventory.Remove(CharacterInventoryLayout.RevivalCoinSlot);
        }
        else
        {
            plannedInventory[CharacterInventoryLayout.RevivalCoinSlot] = revivalCoins with
            {
                CountOrValue = revivalCoins.CountOrValue - 1
            };
        }

        return true;
    }

    public const uint DefaultStackLimit = int.MaxValue;

    public static bool TryPlace(
        IReadOnlyDictionary<ushort, CharacterItemRecord> inventory,
        CharacterMailAttachmentRecord item,
        ItemCatalog catalog,
        uint weightLimit,
        out CharacterInventoryPlacementPlan plan,
        out InventoryPlacementFailure failure) =>
        TryPlace(
            inventory,
            item,
            catalog,
            weightLimit,
            preferredEmptySlot: null,
            out plan,
            out failure);

    public static bool TryPlace(
        IReadOnlyDictionary<ushort, CharacterItemRecord> inventory,
        CharacterMailAttachmentRecord item,
        ItemCatalog catalog,
        uint weightLimit,
        ushort? preferredEmptySlot,
        out CharacterInventoryPlacementPlan plan,
        out InventoryPlacementFailure failure)
    {
        plan = null!;
        failure = InventoryPlacementFailure.None;
        if (item.ItemId == 0
            || item.CountOrValue == 0
            || !catalog.TryGetDefinition(item.ItemId, out var definition))
        {
            failure = InventoryPlacementFailure.InvalidItem;
            return false;
        }

        if (item.ItemId == CharacterInventoryLayout.RevivalCoinItemId)
        {
            return TryPlaceRevivalCoins(
                inventory,
                item,
                catalog,
                out plan,
                out failure);
        }

        var candidateSlots = EnumerateCandidateSlots(definition).ToArray();
        if (candidateSlots.Length == 0)
        {
            failure = InventoryPlacementFailure.UnsupportedCategory;
            return false;
        }

        var currentWeight = CalculateWeight(inventory.Values, catalog);
        var addedWeight = MultiplyWeight(definition, item.CountOrValue);
        var totalWeight = AddWeight(currentWeight, addedWeight);
        if (totalWeight > weightLimit)
        {
            failure = InventoryPlacementFailure.Overweight;
            return false;
        }

        var planned = inventory.ToDictionary(pair => pair.Key, pair => pair.Value);
        var changedSlots = new List<ushort>();
        var remaining = item.CountOrValue;
        var emptySlotCandidates = preferredEmptySlot.HasValue
            && candidateSlots.Contains(preferredEmptySlot.Value)
                ? candidateSlots
                    .Prepend(preferredEmptySlot.Value)
                    .Distinct()
                : candidateSlots;
        if (definition.IsStackable)
        {
            var stackLimit = GetStackLimit(definition);
            foreach (var slot in candidateSlots)
            {
                if (remaining == 0)
                {
                    break;
                }

                if (!planned.TryGetValue(slot, out var existing)
                    || !CanStack(existing, item)
                    || existing.CountOrValue >= stackLimit)
                {
                    continue;
                }

                var added = Math.Min(remaining, stackLimit - existing.CountOrValue);
                planned[slot] = existing with
                {
                    CountOrValue = existing.CountOrValue + added
                };
                changedSlots.Add(slot);
                remaining -= added;
            }

            foreach (var slot in emptySlotCandidates)
            {
                if (remaining == 0)
                {
                    break;
                }

                if (planned.ContainsKey(slot))
                {
                    continue;
                }

                var added = Math.Min(remaining, stackLimit);
                if (!planned.TryAdd(
                        slot,
                        new CharacterItemRecord(
                            slot,
                            item.ItemId,
                            added,
                            item.State,
                            item.Durability,
                            item.SealState)))
                {
                    failure = InventoryPlacementFailure.Full;
                    return false;
                }

                changedSlots.Add(slot);
                remaining -= added;
            }
        }
        else
        {
            var firstUnit = true;
            foreach (var slot in emptySlotCandidates)
            {
                if (remaining == 0)
                {
                    break;
                }

                if (planned.ContainsKey(slot))
                {
                    continue;
                }

                if (!planned.TryAdd(
                        slot,
                        new CharacterItemRecord(
                            slot,
                            item.ItemId,
                            1,
                            item.State,
                            catalog.NormalizeDurability(item.ItemId, item.Durability),
                            item.SealState,
                            EquipmentQualitySeed: item.EquipmentQualitySeed,
                            InstanceId: CharacterItemIdentity.GetUnitInstanceId(
                                definition,
                                item.InstanceId,
                                firstUnit))))
                {
                    failure = InventoryPlacementFailure.Full;
                    return false;
                }

                changedSlots.Add(slot);
                remaining--;
                firstUnit = false;
            }
        }

        if (remaining != 0 || changedSlots.Count == 0)
        {
            failure = InventoryPlacementFailure.Full;
            return false;
        }

        plan = new CharacterInventoryPlacementPlan(
            changedSlots[0],
            planned,
            changedSlots,
            totalWeight);
        return true;
    }

    public static CharacterInventoryNormalizationPlan Normalize(
        IEnumerable<CharacterItemRecord> inventory,
        ItemCatalog catalog)
    {
        var source = inventory
            .Where(item => item.ItemId != 0 && item.CountOrValue != 0)
            .OrderBy(item => item.Slot)
            .ToArray();
        var normalized = new Dictionary<ushort, CharacterItemRecord>();
        var pending = new List<CharacterMailAttachmentRecord>();
        var overflow = new List<CharacterMailAttachmentRecord>();

        var revivalCoinCount = source
            .Where(CharacterInventoryLayout.IsRevivalCoin)
            .Aggregate(0UL, (total, item) =>
                Math.Min(uint.MaxValue, total + item.CountOrValue));
        if (revivalCoinCount != 0)
        {
            normalized.Add(
                CharacterInventoryLayout.RevivalCoinSlot,
                new CharacterItemRecord(
                    CharacterInventoryLayout.RevivalCoinSlot,
                    CharacterInventoryLayout.RevivalCoinItemId,
                    checked((uint)revivalCoinCount)));
        }

        foreach (var item in source.Where(item =>
                     !CharacterInventoryLayout.IsRevivalCoin(item)))
        {
            if (!catalog.TryGetDefinition(item.ItemId, out var definition))
            {
                if (CharacterInventoryLayout.IsMainItemSlot(item.Slot)
                    && normalized.TryAdd(item.Slot, item))
                {
                    continue;
                }

                overflow.Add(ToAttachment(item));
                continue;
            }

            var identifiedItem = CharacterItemIdentity.Ensure(item, definition);

            var perSlotLimit = definition.IsStackable
                ? GetStackLimit(definition)
                : 1u;
            var preservedCount = Math.Min(item.CountOrValue, perSlotLimit);
            if (CharacterInventoryLayout.IsCompatibleSlot(definition, item.Slot)
                && normalized.TryAdd(
                    item.Slot,
                    identifiedItem with
                    {
                        CountOrValue = preservedCount,
                        Durability = catalog.NormalizeDurability(
                            item.ItemId,
                            item.Durability)
                    }))
            {
                if (preservedCount < item.CountOrValue)
                {
                    pending.Add(ToAttachment(
                        identifiedItem with
                        {
                            CountOrValue = item.CountOrValue - preservedCount,
                            InstanceId = Guid.Empty
                        }));
                }

                continue;
            }

            pending.Add(ToAttachment(identifiedItem));
        }

        foreach (var item in pending)
        {
            if (TryPlace(
                    normalized,
                    item,
                    catalog,
                    uint.MaxValue,
                    out var placement,
                    out _))
            {
                normalized = placement.Inventory;
                continue;
            }

                overflow.AddRange(CreateMailAttachments(item, catalog));
        }

        var result = normalized.Values.OrderBy(item => item.Slot).ToArray();
        var changed = overflow.Count != 0
            || source.Length != result.Length
            || !source.SequenceEqual(result);
        return new CharacterInventoryNormalizationPlan(normalized, overflow, changed);
    }

    public static CharacterWarehouseNormalizationPlan NormalizeWarehouse(
        IEnumerable<CharacterItemRecord> warehouse,
        ItemCatalog catalog,
        ushort capacity)
    {
        var source = warehouse
            .Where(item => item.ItemId != 0 && item.CountOrValue != 0)
            .OrderBy(item => item.Slot)
            .ToArray();
        var normalized = new Dictionary<ushort, CharacterItemRecord>();
        var overflow = new List<CharacterMailAttachmentRecord>();
        foreach (var item in source)
        {
            if (!catalog.TryGetDefinition(item.ItemId, out var definition))
            {
                var unknownTarget = item.Slot < capacity
                    && !normalized.ContainsKey(item.Slot)
                        ? item.Slot
                        : FindFreeWarehouseSlot(normalized, capacity);
                if (unknownTarget == ushort.MaxValue)
                {
                    overflow.Add(ToAttachment(item));
                }
                else
                {
                    normalized.Add(unknownTarget, item with { Slot = unknownTarget });
                }

                continue;
            }

            if (definition.InventoryCategory == ItemInventoryCategory.Avatar)
            {
                overflow.AddRange(CreateMailAttachments(ToAttachment(item), catalog));
                continue;
            }

            var identifiedItem = CharacterItemIdentity.Ensure(item, definition);

            var remaining = item.CountOrValue;
            var perSlotLimit = definition.IsStackable
                ? GetStackLimit(definition)
                : 1u;
            var firstChunk = true;
            while (remaining > 0)
            {
                var target = firstChunk
                    && item.Slot < capacity
                    && !normalized.ContainsKey(item.Slot)
                        ? item.Slot
                        : FindFreeWarehouseSlot(normalized, capacity);
                var count = Math.Min(remaining, perSlotLimit);
                if (target == ushort.MaxValue)
                {
                    overflow.AddRange(CreateMailAttachments(
                        ToAttachment(identifiedItem with
                        {
                            CountOrValue = remaining,
                            InstanceId = firstChunk
                                ? identifiedItem.InstanceId
                                : Guid.Empty
                        }),
                        catalog));
                    break;
                }

                normalized.Add(
                    target,
                    identifiedItem with
                    {
                        Slot = target,
                        CountOrValue = count,
                        Durability = catalog.NormalizeDurability(
                            item.ItemId,
                            item.Durability),
                        InstanceId = CharacterItemIdentity.GetUnitInstanceId(
                            definition,
                            identifiedItem.InstanceId,
                            firstChunk)
                    });
                remaining -= count;
                firstChunk = false;
            }
        }

        var result = normalized.Values.OrderBy(item => item.Slot).ToArray();
        var changed = overflow.Count != 0
            || source.Length != result.Length
            || !source.SequenceEqual(result);
        return new CharacterWarehouseNormalizationPlan(normalized, overflow, changed);
    }

    public static ulong CalculateWeight(
        IEnumerable<CharacterItemRecord> inventory,
        ItemCatalog catalog)
    {
        ulong total = 0;
        foreach (var item in inventory)
        {
            if (item.ItemId == 0
                || item.CountOrValue == 0
                || CharacterInventoryLayout.IsRevivalCoin(item)
                || !catalog.TryGetDefinition(item.ItemId, out var definition))
            {
                continue;
            }

            total = AddWeight(total, MultiplyWeight(definition, item.CountOrValue));
        }

        return total;
    }

    public static ulong CalculateCarriedWeight(
        IEnumerable<CharacterItemRecord> inventory,
        IEnumerable<CharacterItemRecord> equippedInventory,
        IEnumerable<CharacterItemRecord> creatureInventory,
        ItemCatalog catalog) =>
        AddWeight(
            AddWeight(
                CalculateWeight(inventory, catalog),
                CalculateWeight(equippedInventory, catalog)),
            CalculateWeight(creatureInventory, catalog));

    public static ulong CalculateCarriedWeightLimit(
        uint combatStatInventoryLimit,
        IEnumerable<CharacterItemRecord> equippedInventory,
        ItemCatalog catalog)
    {
        ulong limit = combatStatInventoryLimit / CombatStatInventoryLimitScale;
        foreach (var item in equippedInventory)
        {
            if (item.ItemId == 0
                || item.CountOrValue == 0
                || !catalog.TryGetDefinition(item.ItemId, out var definition)
                || definition.InventoryLimit is not int inventoryLimitBonus)
            {
                continue;
            }

            if (inventoryLimitBonus >= 0)
            {
                limit = AddWeight(limit, checked((uint)inventoryLimitBonus));
            }
            else
            {
                var reduction = (ulong)-(long)inventoryLimitBonus;
                limit = reduction >= limit ? 0 : limit - reduction;
            }
        }

        return limit;
    }

    public static uint CalculateMainInventoryWeightAllowance(
        uint combatStatInventoryLimit,
        IEnumerable<CharacterItemRecord> equippedInventory,
        IEnumerable<CharacterItemRecord> creatureInventory,
        ItemCatalog catalog)
    {
        var limit = CalculateCarriedWeightLimit(
            combatStatInventoryLimit,
            equippedInventory,
            catalog);
        var outsideMainInventoryWeight = AddWeight(
            CalculateWeight(equippedInventory, catalog),
            CalculateWeight(creatureInventory, catalog));
        if (outsideMainInventoryWeight >= limit)
        {
            return 0;
        }

        return (uint)Math.Min(uint.MaxValue, limit - outsideMainInventoryWeight);
    }

    public static bool CanCarryAdditionalItem(
        IEnumerable<CharacterItemRecord> inventory,
        IEnumerable<CharacterItemRecord> equippedInventory,
        IEnumerable<CharacterItemRecord> creatureInventory,
        ushort itemId,
        uint count,
        uint combatStatInventoryLimit,
        ItemCatalog catalog,
        out ulong currentWeight,
        out ulong addedWeight,
        out ulong weightLimit)
    {
        currentWeight = CalculateCarriedWeight(
            inventory,
            equippedInventory,
            creatureInventory,
            catalog);
        weightLimit = CalculateCarriedWeightLimit(
            combatStatInventoryLimit,
            equippedInventory,
            catalog);
        addedWeight = catalog.TryGetDefinition(itemId, out var definition)
            ? MultiplyWeight(definition, count)
            : 0;
        return AddWeight(currentWeight, addedWeight) <= weightLimit;
    }

    private static ulong MultiplyWeight(ItemDefinition definition, uint count)
    {
        var unitWeight = (ulong)Math.Max(0, definition.Weight ?? 0);
        return unitWeight != 0 && count > ulong.MaxValue / unitWeight
            ? ulong.MaxValue
            : unitWeight * count;
    }

    private static ulong AddWeight(ulong left, ulong right) =>
        left > ulong.MaxValue - right ? ulong.MaxValue : left + right;

    private static bool TryPlaceRevivalCoins(
        IReadOnlyDictionary<ushort, CharacterItemRecord> inventory,
        CharacterMailAttachmentRecord item,
        ItemCatalog catalog,
        out CharacterInventoryPlacementPlan plan,
        out InventoryPlacementFailure failure)
    {
        var existingCount = inventory.Values
            .Where(CharacterInventoryLayout.IsRevivalCoin)
            .Aggregate(0UL, (total, existing) =>
                Math.Min(uint.MaxValue, total + existing.CountOrValue));
        if (existingCount + item.CountOrValue > uint.MaxValue)
        {
            plan = null!;
            failure = InventoryPlacementFailure.Full;
            return false;
        }

        var planned = inventory
            .Where(pair => !CharacterInventoryLayout.IsRevivalCoin(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        if (planned.ContainsKey(CharacterInventoryLayout.RevivalCoinSlot))
        {
            plan = null!;
            failure = InventoryPlacementFailure.Full;
            return false;
        }

        planned.Add(
            CharacterInventoryLayout.RevivalCoinSlot,
            new CharacterItemRecord(
                CharacterInventoryLayout.RevivalCoinSlot,
                CharacterInventoryLayout.RevivalCoinItemId,
                checked((uint)(existingCount + item.CountOrValue))));
        var changedSlots = inventory.Values
            .Where(CharacterInventoryLayout.IsRevivalCoin)
            .Select(existing => existing.Slot)
            .Append(CharacterInventoryLayout.RevivalCoinSlot)
            .Distinct()
            .ToArray();
        plan = new CharacterInventoryPlacementPlan(
            CharacterInventoryLayout.RevivalCoinSlot,
            planned,
            changedSlots,
            CalculateWeight(planned.Values, catalog));
        failure = InventoryPlacementFailure.None;
        return true;
    }

    private static IEnumerable<ushort> EnumerateCandidateSlots(ItemDefinition definition)
    {
        if (!CharacterInventoryLayout.TryGetCategoryRange(
                definition.InventoryCategory,
                out var start,
                out var end))
        {
            yield break;
        }

        for (var slot = (int)start; slot < end; slot++)
        {
            yield return (ushort)slot;
        }

        for (var slot = (int)CharacterInventoryLayout.QuickSlotStart;
             slot < CharacterInventoryLayout.QuickSlotEnd;
             slot++)
        {
            yield return (ushort)slot;
        }
    }

    public static uint GetStackLimit(ItemDefinition definition) =>
        definition.StackLimit is > 0
            ? checked((uint)definition.StackLimit.Value)
            : DefaultStackLimit;

    private static bool CanStack(
        CharacterItemRecord existing,
        CharacterMailAttachmentRecord item) =>
        existing.ItemId == item.ItemId
        && existing.State == item.State
        && existing.Durability == item.Durability
        && existing.SealState == item.SealState;

    private static CharacterMailAttachmentRecord ToAttachment(CharacterItemRecord item) =>
        new(
            item.ItemId,
            item.CountOrValue,
            item.State,
            item.Durability,
            item.SealState,
            item.AvatarRemainingSeconds,
            item.AvatarAbilityIndex,
            item.EquipmentQualitySeed,
            item.InstanceId);

    public static IReadOnlyList<CharacterMailAttachmentRecord> CreateMailAttachments(
        CharacterMailAttachmentRecord item,
        ItemCatalog catalog)
    {
        var limit = catalog.TryGetDefinition(item.ItemId, out var definition)
            && definition.IsStackable
                ? GetStackLimit(definition)
                : 1u;
        var remaining = item.CountOrValue;
        var firstUnit = true;
        var attachments = new List<CharacterMailAttachmentRecord>();
        while (remaining > 0)
        {
            var count = Math.Min(remaining, limit);
            attachments.Add(item with
            {
                CountOrValue = count,
                InstanceId = definition is null
                    ? item.InstanceId
                    : CharacterItemIdentity.GetUnitInstanceId(
                        definition,
                        item.InstanceId,
                        firstUnit)
            });
            remaining -= count;
            firstUnit = false;
        }

        return attachments;
    }

    private static ushort FindFreeWarehouseSlot(
        IReadOnlyDictionary<ushort, CharacterItemRecord> warehouse,
        ushort capacity)
    {
        for (ushort slot = 0; slot < capacity; slot++)
        {
            if (!warehouse.ContainsKey(slot))
            {
                return slot;
            }
        }

        return ushort.MaxValue;
    }
}
