namespace DFLegacy.Server;

public static class EquipmentQuality
{
    public const ushort KaleidoBoxItemId = 15;
    public const uint MiddleQualitySeed = 0;
    public const uint MaximumRandomSeed = 999_999_997;
    public const uint TopQualitySeed = 999_999_998;
    private static readonly TimeSpan NpcShopSeedResetTime = TimeSpan.FromHours(6);
    private const int MaximumDistinctRollAttempts = 8;

    public static uint GetSeed(CharacterItemRecord item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.EquipmentQualitySeed ?? item.CountOrValue;
    }

    public static uint RollSeed(IDropRandomSource random)
    {
        ArgumentNullException.ThrowIfNull(random);

        // This is the client's 32-bit equipment-quality seed, not the seed of
        // a managed PRNG. Production callers pass GameRandomSource.Shared
        // (the pure managed server-arbitration random).
        return checked(1u + (uint)random.Next(
            checked((int)MaximumRandomSeed)));
    }

    public static uint GetNpcShopSeed(DateTimeOffset localNow)
    {
        var shopDate = localNow.TimeOfDay < NpcShopSeedResetTime
            ? localNow.Date.AddDays(-1)
            : localNow.Date;

        // The original shop seed uses the C tm fields directly. This is a
        // deterministic daily quality seed, not a gameplay-random roll.
        var tmYear = shopDate.Year - 1900;
        var tmMonth = shopDate.Month - 1;
        return checked((uint)(365 * tmYear + 30 * tmMonth + shopDate.Day));
    }

    public static uint RollDifferentSeed(
        uint currentSeed,
        IDropRandomSource random)
    {
        ArgumentNullException.ThrowIfNull(random);
        for (var attempt = 0; attempt < MaximumDistinctRollAttempts; attempt++)
        {
            var candidate = RollSeed(random);
            if (candidate != currentSeed)
            {
                return candidate;
            }
        }

        return currentSeed > 1 ? currentSeed - 1 : 2;
    }
}

public enum ItemAttributeModificationOperation : ushort
{
    Reseal = 1,
    AdjustQuality = 2
}

public enum ItemAttributeModificationFailure : byte
{
    None = 0,
    InvalidTarget = 4,
    ResealLimit = 13,
    InvalidMaterial = 17,
    AlreadySealed = 18,
    UnsupportedItem = 19,
    InsufficientMaterial = 22
}

public sealed record ItemAttributeModificationPlan(
    IReadOnlyDictionary<ushort, CharacterItemRecord> MainInventory,
    IReadOnlyDictionary<ushort, CharacterItemRecord> Equipment,
    ushort TargetSlot,
    byte TargetListType,
    ushort TargetItemId,
    ushort MaterialSlot,
    ushort MaterialItemId,
    uint MaterialConsumed,
    uint MaterialRemaining,
    ItemAttributeModificationOperation Operation,
    uint? PreviousQualitySeed = null,
    uint? NewQualitySeed = null,
    byte? PreviousResealCount = null,
    byte? NewResealCount = null);

public static class ItemAttributeModificationPlanner
{
    public static bool TryCreate(
        IReadOnlyDictionary<ushort, CharacterItemRecord> mainInventory,
        IReadOnlyDictionary<ushort, CharacterItemRecord> equipment,
        ushort requestedTargetSlot,
        ushort expectedTargetItemId,
        ushort materialSlot,
        ItemCatalog itemCatalog,
        ResealCatalog resealCatalog,
        IDropRandomSource random,
        out ItemAttributeModificationPlan plan,
        out ItemAttributeModificationFailure failure)
    {
        ArgumentNullException.ThrowIfNull(mainInventory);
        ArgumentNullException.ThrowIfNull(equipment);
        ArgumentNullException.ThrowIfNull(itemCatalog);
        ArgumentNullException.ThrowIfNull(resealCatalog);
        ArgumentNullException.ThrowIfNull(random);
        plan = null!;
        failure = ItemAttributeModificationFailure.InvalidTarget;

        if (expectedTargetItemId == 0
            || !TryResolveTarget(
                mainInventory,
                equipment,
                requestedTargetSlot,
                expectedTargetItemId,
                out var targetSlot,
                out var targetListType,
                out var target)
            || targetListType == 0 && targetSlot == materialSlot)
        {
            return false;
        }

        if (!mainInventory.TryGetValue(materialSlot, out var material))
        {
            failure = ItemAttributeModificationFailure.InvalidMaterial;
            return false;
        }

        return material.ItemId switch
        {
            CharacterItemSealing.GoldenWaxItemId => TryCreateReseal(
                mainInventory,
                equipment,
                targetSlot,
                targetListType,
                target,
                materialSlot,
                material,
                itemCatalog,
                resealCatalog,
                out plan,
                out failure),
            EquipmentQuality.KaleidoBoxItemId => TryCreateQualityAdjustment(
                mainInventory,
                equipment,
                targetSlot,
                targetListType,
                target,
                materialSlot,
                material,
                itemCatalog,
                random,
                out plan,
                out failure),
            _ => Fail(
                ItemAttributeModificationFailure.InvalidMaterial,
                out plan,
                out failure)
        };
    }

    private static bool TryCreateQualityAdjustment(
        IReadOnlyDictionary<ushort, CharacterItemRecord> mainInventory,
        IReadOnlyDictionary<ushort, CharacterItemRecord> equipment,
        ushort targetSlot,
        byte targetListType,
        CharacterItemRecord target,
        ushort materialSlot,
        CharacterItemRecord material,
        ItemCatalog itemCatalog,
        IDropRandomSource random,
        out ItemAttributeModificationPlan plan,
        out ItemAttributeModificationFailure failure)
    {
        if (!IsSupportedEquipment(target, itemCatalog, out _))
        {
            return Fail(
                ItemAttributeModificationFailure.UnsupportedItem,
                out plan,
                out failure);
        }

        if (material.CountOrValue < 1)
        {
            return Fail(
                ItemAttributeModificationFailure.InsufficientMaterial,
                out plan,
                out failure);
        }

        var plannedMain = mainInventory.ToDictionary(
            pair => pair.Key,
            pair => pair.Value);
        var plannedEquipment = equipment.ToDictionary(
            pair => pair.Key,
            pair => pair.Value);
        var previousSeed = EquipmentQuality.GetSeed(target);
        var newSeed = EquipmentQuality.RollDifferentSeed(previousSeed, random);
        var targetSpace = targetListType == 3 ? plannedEquipment : plannedMain;
        targetSpace[targetSlot] = target with { EquipmentQualitySeed = newSeed };

        var materialRemaining = ConsumeMaterial(
            plannedMain,
            materialSlot,
            material,
            1);

        plan = new ItemAttributeModificationPlan(
            plannedMain,
            plannedEquipment,
            targetSlot,
            targetListType,
            target.ItemId,
            materialSlot,
            material.ItemId,
            1,
            materialRemaining,
            ItemAttributeModificationOperation.AdjustQuality,
            PreviousQualitySeed: previousSeed,
            NewQualitySeed: newSeed);
        failure = ItemAttributeModificationFailure.None;
        return true;
    }

    private static bool TryCreateReseal(
        IReadOnlyDictionary<ushort, CharacterItemRecord> mainInventory,
        IReadOnlyDictionary<ushort, CharacterItemRecord> equipment,
        ushort targetSlot,
        byte targetListType,
        CharacterItemRecord target,
        ushort materialSlot,
        CharacterItemRecord material,
        ItemCatalog itemCatalog,
        ResealCatalog resealCatalog,
        out ItemAttributeModificationPlan plan,
        out ItemAttributeModificationFailure failure)
    {
        if (targetListType != 0
            || !IsSupportedEquipment(target, itemCatalog, out var definition)
            || definition.AttachType != ItemAttachType.Sealing)
        {
            return Fail(
                ItemAttributeModificationFailure.UnsupportedItem,
                out plan,
                out failure);
        }

        if (target.SealState != CharacterItemSealing.Unsealed)
        {
            return Fail(
                ItemAttributeModificationFailure.AlreadySealed,
                out plan,
                out failure);
        }

        var previousResealCount = CharacterItemSealing.GetResealCount(target);
        if (previousResealCount >= CharacterItemSealing.MaximumResealCount)
        {
            return Fail(
                ItemAttributeModificationFailure.ResealLimit,
                out plan,
                out failure);
        }

        if (!resealCatalog.TryGetCost(
                definition,
                previousResealCount,
                out var materialCost))
        {
            return Fail(
                ItemAttributeModificationFailure.UnsupportedItem,
                out plan,
                out failure);
        }

        if (material.CountOrValue < materialCost)
        {
            return Fail(
                ItemAttributeModificationFailure.InsufficientMaterial,
                out plan,
                out failure);
        }

        var newResealCount = checked((byte)(previousResealCount + 1));
        var resealed = CharacterItemSealing.SetResealCount(
            target with { SealState = CharacterItemSealing.Sealed },
            newResealCount);
        var plannedMain = mainInventory.ToDictionary(
            pair => pair.Key,
            pair => pair.Value);
        var plannedEquipment = equipment.ToDictionary(
            pair => pair.Key,
            pair => pair.Value);
        plannedMain[targetSlot] = resealed;
        var materialRemaining = ConsumeMaterial(
            plannedMain,
            materialSlot,
            material,
            materialCost);

        plan = new ItemAttributeModificationPlan(
            plannedMain,
            plannedEquipment,
            targetSlot,
            targetListType,
            target.ItemId,
            materialSlot,
            material.ItemId,
            materialCost,
            materialRemaining,
            ItemAttributeModificationOperation.Reseal,
            PreviousResealCount: previousResealCount,
            NewResealCount: newResealCount);
        failure = ItemAttributeModificationFailure.None;
        return true;
    }

    private static bool IsSupportedEquipment(
        CharacterItemRecord target,
        ItemCatalog itemCatalog,
        out ItemDefinition definition)
    {
        definition = null!;
        return itemCatalog.TryGetDefinition(target.ItemId, out definition)
            && definition.ScriptKind == ItemScriptKind.Equipment
            && definition.InventoryCategory == ItemInventoryCategory.Equipment
            && !definition.IsTitle;
    }

    private static uint ConsumeMaterial(
        IDictionary<ushort, CharacterItemRecord> inventory,
        ushort materialSlot,
        CharacterItemRecord material,
        uint count)
    {
        var remaining = material.CountOrValue - count;
        if (remaining == 0)
        {
            inventory.Remove(materialSlot);
        }
        else
        {
            inventory[materialSlot] = material with { CountOrValue = remaining };
        }

        return remaining;
    }

    private static bool Fail(
        ItemAttributeModificationFailure value,
        out ItemAttributeModificationPlan plan,
        out ItemAttributeModificationFailure failure)
    {
        plan = null!;
        failure = value;
        return false;
    }

    private static bool TryResolveTarget(
        IReadOnlyDictionary<ushort, CharacterItemRecord> mainInventory,
        IReadOnlyDictionary<ushort, CharacterItemRecord> equipment,
        ushort requestedSlot,
        ushort expectedItemId,
        out ushort resolvedSlot,
        out byte listType,
        out CharacterItemRecord target)
    {
        resolvedSlot = requestedSlot;
        listType = 0;
        target = null!;
        if (mainInventory.TryGetValue(requestedSlot, out var item)
            && IsExpectedTarget(item, expectedItemId))
        {
            target = item;
            return true;
        }

        if (equipment.TryGetValue(requestedSlot, out item)
            && IsExpectedTarget(item, expectedItemId))
        {
            listType = 3;
            target = item;
            return true;
        }

        if (requestedSlot < 10)
        {
            var mappedSlot = checked((ushort)(requestedSlot + 9));
            if (equipment.TryGetValue(mappedSlot, out item)
                && IsExpectedTarget(item, expectedItemId))
            {
                resolvedSlot = mappedSlot;
                listType = 3;
                target = item;
                return true;
            }
        }

        return false;
    }

    private static bool IsExpectedTarget(
        CharacterItemRecord item,
        ushort expectedItemId) =>
        item.ItemId != 0 && item.ItemId == expectedItemId;
}
