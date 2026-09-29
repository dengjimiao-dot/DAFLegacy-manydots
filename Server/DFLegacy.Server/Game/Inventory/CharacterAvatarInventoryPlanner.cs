namespace DFLegacy.Server;

public static class CharacterAvatarInventoryLayout
{
    public const ushort Capacity = 105;
    public const ushort WornSlotCount = 9;

    public static bool IsBagSlot(ushort slot) => slot < Capacity;

    public static bool IsWornSlot(ushort slot) => slot < WornSlotCount;

    public static bool TryGetWornSlot(ItemDefinition definition, out ushort slot)
    {
        slot = definition.TypeTag switch
        {
            "hat avatar" => 0,
            "hair avatar" => 1,
            "face avatar" => 2,
            "coat avatar" => 3,
            "pants avatar" => 4,
            "shoes avatar" => 5,
            "breast avatar" => 6,
            "waist avatar" => 7,
            "skin avatar" => 8,
            _ => ushort.MaxValue
        };
        return slot != ushort.MaxValue;
    }

    public static bool IsCompatibleWornSlot(ItemDefinition definition, ushort slot) =>
        definition.InventoryCategory == ItemInventoryCategory.Avatar
        && TryGetWornSlot(definition, out var expectedSlot)
        && slot == expectedSlot;
}

public sealed record CharacterAvatarInventoryPlacementPlan(
    ushort DestinationSlot,
    Dictionary<ushort, CharacterItemRecord> Inventory,
    IReadOnlyList<ushort> ChangedSlots);

public sealed record CharacterAvatarInventoryNormalizationPlan(
    Dictionary<ushort, CharacterItemRecord> Inventory,
    IReadOnlyList<CharacterMailAttachmentRecord> OverflowItems,
    bool Changed);

public static class CharacterAvatarInventoryPlanner
{
    public static bool TryPlace(
        IReadOnlyDictionary<ushort, CharacterItemRecord> inventory,
        CharacterMailAttachmentRecord item,
        ItemCatalog catalog,
        out CharacterAvatarInventoryPlacementPlan plan,
        out InventoryPlacementFailure failure) =>
        TryPlace(inventory, item, catalog, null, out plan, out failure);

    public static bool TryPlace(
        IReadOnlyDictionary<ushort, CharacterItemRecord> inventory,
        CharacterMailAttachmentRecord item,
        ItemCatalog catalog,
        ushort? preferredEmptySlot,
        out CharacterAvatarInventoryPlacementPlan plan,
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

        if (definition.InventoryCategory != ItemInventoryCategory.Avatar)
        {
            failure = InventoryPlacementFailure.UnsupportedCategory;
            return false;
        }

        var planned = inventory.ToDictionary(pair => pair.Key, pair => pair.Value);
        var changedSlots = new List<ushort>();
        var candidateSlots = EnumerateSlots(preferredEmptySlot);
        var remaining = item.CountOrValue;
        var firstUnit = true;
        foreach (var slot in candidateSlots)
        {
            if (remaining == 0)
            {
                break;
            }

            if (planned.ContainsKey(slot))
            {
                continue;
            }

            planned.Add(
                slot,
                new CharacterItemRecord(
                    slot,
                    item.ItemId,
                    1,
                    item.State,
                    Durability: 0,
                    SealState: item.SealState,
                    AvatarRemainingSeconds: item.AvatarRemainingSeconds ?? 0,
                    AvatarAbilityIndex: item.AvatarAbilityIndex ?? 0,
                    InstanceId: CharacterItemIdentity.GetUnitInstanceId(
                        definition,
                        item.InstanceId,
                        firstUnit)));
            changedSlots.Add(slot);
            remaining--;
            firstUnit = false;
        }

        if (remaining != 0 || changedSlots.Count == 0)
        {
            failure = InventoryPlacementFailure.Full;
            return false;
        }

        plan = new CharacterAvatarInventoryPlacementPlan(
            changedSlots[0],
            planned,
            changedSlots);
        return true;
    }

    public static CharacterAvatarInventoryNormalizationPlan Normalize(
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

        foreach (var item in source)
        {
            if (!catalog.TryGetDefinition(item.ItemId, out var definition)
                || definition.InventoryCategory != ItemInventoryCategory.Avatar)
            {
                overflow.Add(ToAttachment(item));
                continue;
            }

            var identifiedItem = CharacterItemIdentity.Ensure(item, definition);

            if (CharacterAvatarInventoryLayout.IsBagSlot(item.Slot)
                && normalized.TryAdd(
                    item.Slot,
                    identifiedItem with
                    {
                        CountOrValue = 1,
                        Durability = 0,
                        AvatarRemainingSeconds = item.AvatarRemainingSeconds ?? 0,
                        AvatarAbilityIndex = item.AvatarAbilityIndex ?? 0
                    }))
            {
                if (item.CountOrValue > 1)
                {
                    pending.Add(ToAttachment(
                        identifiedItem with
                        {
                            CountOrValue = item.CountOrValue - 1,
                            InstanceId = Guid.Empty
                        }));
                }

                continue;
            }

            pending.Add(ToAttachment(identifiedItem));
        }

        foreach (var item in pending)
        {
            if (TryPlace(normalized, item, catalog, out var placement, out _))
            {
                normalized = placement.Inventory;
                continue;
            }

            overflow.AddRange(CharacterInventoryPlanner.CreateMailAttachments(item, catalog));
        }

        var result = normalized.Values.OrderBy(item => item.Slot).ToArray();
        var changed = overflow.Count != 0
            || source.Length != result.Length
            || !source.SequenceEqual(result);
        return new CharacterAvatarInventoryNormalizationPlan(normalized, overflow, changed);
    }

    private static IEnumerable<ushort> EnumerateSlots(ushort? preferredEmptySlot)
    {
        if (preferredEmptySlot.HasValue
            && CharacterAvatarInventoryLayout.IsBagSlot(preferredEmptySlot.Value))
        {
            yield return preferredEmptySlot.Value;
        }

        for (ushort slot = 0; slot < CharacterAvatarInventoryLayout.Capacity; slot++)
        {
            if (slot != preferredEmptySlot)
            {
                yield return slot;
            }
        }
    }

    private static CharacterMailAttachmentRecord ToAttachment(CharacterItemRecord item) =>
        new(
            item.ItemId,
            item.CountOrValue,
            item.State,
            item.Durability,
            item.SealState,
            item.AvatarRemainingSeconds,
            item.AvatarAbilityIndex,
            InstanceId: item.InstanceId);
}
