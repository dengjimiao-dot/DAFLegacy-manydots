namespace DFLegacy.Server;

public enum AvatarCompoundFailure
{
    None,
    DefinitionUnavailable,
    InvalidMaterial,
    AvatarMismatch,
    PartMismatch,
    JobMismatch,
    GradeMismatch,
    ResultUnavailable,
    OptionOutOfRange
}

public sealed record AvatarCompoundPlan(
    Dictionary<ushort, CharacterItemRecord> MainInventory,
    Dictionary<ushort, CharacterItemRecord> AvatarInventory,
    ushort MaterialSlot,
    ushort MaterialItemId,
    uint MaterialConsumed,
    uint MaterialRemaining,
    ushort RequestedFirstSlot,
    ushort RequestedSecondSlot,
    ushort ResolvedFirstSlot,
    ushort ResolvedSecondSlot,
    ushort ResultItemId,
    byte ResultPart,
    byte SelectedOption,
    int RareRate,
    bool IsRare);

public static class AvatarCompoundPlanner
{
    public static bool TryCreate(
        IReadOnlyDictionary<ushort, CharacterItemRecord> mainInventory,
        IReadOnlyDictionary<ushort, CharacterItemRecord> avatarInventory,
        int characterJob,
        ushort materialSlot,
        ushort firstAvatarItemId,
        ushort secondAvatarItemId,
        byte selectedOption,
        ushort firstAvatarSlot,
        ushort secondAvatarSlot,
        AvatarCompoundCatalog catalog,
        IDropRandomSource random,
        out AvatarCompoundPlan plan,
        out AvatarCompoundFailure failure)
    {
        ArgumentNullException.ThrowIfNull(mainInventory);
        ArgumentNullException.ThrowIfNull(avatarInventory);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(random);
        plan = null!;
        failure = AvatarCompoundFailure.DefinitionUnavailable;
        if (characterJob is < byte.MinValue or > byte.MaxValue
            || !catalog.TryGetDefinition(checked((byte)characterJob), out var definition))
        {
            return false;
        }

        failure = AvatarCompoundFailure.InvalidMaterial;
        if (!mainInventory.TryGetValue(materialSlot, out var material)
            || material.ItemId != definition.MaterialItemId
            || material.CountOrValue < definition.MaterialCount)
        {
            return false;
        }

        failure = AvatarCompoundFailure.AvatarMismatch;
        if (!TryResolveSlots(
                avatarInventory,
                firstAvatarSlot,
                secondAvatarSlot,
                firstAvatarItemId,
                secondAvatarItemId,
                out var resolvedFirstSlot,
                out var resolvedSecondSlot,
                out var firstAvatar,
                out var secondAvatar))
        {
            return false;
        }

        if (!catalog.TryGetAvatarMetadata(firstAvatar.ItemId, out var firstMetadata)
            || !catalog.TryGetAvatarMetadata(secondAvatar.ItemId, out var secondMetadata)
            || firstMetadata.Part >= AvatarCompoundCatalog.CompoundPartCount
            || secondMetadata.Part != firstMetadata.Part)
        {
            failure = AvatarCompoundFailure.PartMismatch;
            return false;
        }

        if (firstMetadata.Job != characterJob || secondMetadata.Job != characterJob)
        {
            failure = AvatarCompoundFailure.JobMismatch;
            return false;
        }

        if (!IsAllowedGrade(firstMetadata.Grade, definition)
            || !IsAllowedGrade(secondMetadata.Grade, definition))
        {
            failure = AvatarCompoundFailure.GradeMismatch;
            return false;
        }

        if (!catalog.TrySelectResult(
                checked((byte)characterJob),
                firstMetadata.Part,
                firstMetadata.Grade,
                secondMetadata.Grade,
                random,
                out var selection)
            || !catalog.TryGetAvatarMetadata(selection.ItemId, out var resultMetadata)
            || resultMetadata.Job != characterJob
            || resultMetadata.Part != firstMetadata.Part)
        {
            failure = AvatarCompoundFailure.ResultUnavailable;
            return false;
        }

        if (resultMetadata.OptionCount <= 0
            || selectedOption >= resultMetadata.OptionCount)
        {
            failure = AvatarCompoundFailure.OptionOutOfRange;
            return false;
        }

        var plannedMain = mainInventory.ToDictionary(pair => pair.Key, pair => pair.Value);
        var materialRemaining = material.CountOrValue - definition.MaterialCount;
        if (materialRemaining == 0)
        {
            plannedMain.Remove(materialSlot);
        }
        else
        {
            plannedMain[materialSlot] = material with
            {
                CountOrValue = materialRemaining
            };
        }

        var plannedAvatars = avatarInventory.ToDictionary(
            pair => pair.Key,
            pair => pair.Value);
        plannedAvatars.Remove(resolvedFirstSlot);
        plannedAvatars.Remove(resolvedSecondSlot);
        plannedAvatars[resolvedFirstSlot] = new CharacterItemRecord(
            resolvedFirstSlot,
            selection.ItemId,
            1,
            State: 0,
            Durability: 0,
            SealState: 0,
            AvatarRemainingSeconds: 0,
            AvatarAbilityIndex: selectedOption,
            InstanceId: CharacterItemIdentity.CreateInstanceId());

        plan = new AvatarCompoundPlan(
            plannedMain,
            plannedAvatars,
            materialSlot,
            definition.MaterialItemId,
            definition.MaterialCount,
            materialRemaining,
            firstAvatarSlot,
            secondAvatarSlot,
            resolvedFirstSlot,
            resolvedSecondSlot,
            selection.ItemId,
            firstMetadata.Part,
            selectedOption,
            selection.RareRate,
            selection.IsRare);
        failure = AvatarCompoundFailure.None;
        return true;
    }

    internal static bool AvatarItemsMatch(
        CharacterItemRecord firstSlotItem,
        CharacterItemRecord secondSlotItem,
        ushort firstExpectedItemId,
        ushort secondExpectedItemId) =>
        firstSlotItem.ItemId != 0
        && secondSlotItem.ItemId != 0
        && firstSlotItem.CountOrValue == 1
        && secondSlotItem.CountOrValue == 1
        && ((firstSlotItem.ItemId == firstExpectedItemId
                && secondSlotItem.ItemId == secondExpectedItemId)
            || (firstSlotItem.ItemId == secondExpectedItemId
                && secondSlotItem.ItemId == firstExpectedItemId));

    internal static bool TryResolveSlots(
        IReadOnlyDictionary<ushort, CharacterItemRecord> avatars,
        ushort firstRequestedSlot,
        ushort secondRequestedSlot,
        ushort firstExpectedItemId,
        ushort secondExpectedItemId,
        out ushort firstResolvedSlot,
        out ushort secondResolvedSlot,
        out CharacterItemRecord firstAvatar,
        out CharacterItemRecord secondAvatar)
    {
        firstResolvedSlot = 0;
        secondResolvedSlot = 0;
        firstAvatar = null!;
        secondAvatar = null!;
        if (firstRequestedSlot == secondRequestedSlot
            || !CharacterAvatarInventoryLayout.IsBagSlot(firstRequestedSlot)
            || !CharacterAvatarInventoryLayout.IsBagSlot(secondRequestedSlot)
            || firstExpectedItemId == 0
            || secondExpectedItemId == 0)
        {
            return false;
        }

        if (avatars.TryGetValue(firstRequestedSlot, out firstAvatar!)
            && avatars.TryGetValue(secondRequestedSlot, out secondAvatar!)
            && AvatarItemsMatch(
                firstAvatar,
                secondAvatar,
                firstExpectedItemId,
                secondExpectedItemId))
        {
            firstResolvedSlot = firstRequestedSlot;
            secondResolvedSlot = secondRequestedSlot;
            return true;
        }

        var fallbackFirst = avatars.Values
            .Where(item => CharacterAvatarInventoryLayout.IsBagSlot(item.Slot)
                && item.ItemId == firstExpectedItemId
                && item.CountOrValue == 1)
            .OrderBy(item => item.Slot)
            .FirstOrDefault();
        if (fallbackFirst is null)
        {
            firstAvatar = null!;
            secondAvatar = null!;
            return false;
        }

        var fallbackSecond = avatars.Values
            .Where(item => CharacterAvatarInventoryLayout.IsBagSlot(item.Slot)
                && item.Slot != fallbackFirst.Slot
                && item.ItemId == secondExpectedItemId
                && item.CountOrValue == 1)
            .OrderBy(item => item.Slot)
            .FirstOrDefault();
        if (fallbackSecond is null)
        {
            firstAvatar = null!;
            secondAvatar = null!;
            return false;
        }

        firstResolvedSlot = fallbackFirst.Slot;
        secondResolvedSlot = fallbackSecond.Slot;
        firstAvatar = fallbackFirst;
        secondAvatar = fallbackSecond;
        return true;
    }

    private static bool IsAllowedGrade(
        byte grade,
        AvatarCompoundDefinition definition) =>
        grade == definition.Grade || grade == definition.UpperGrade;
}
