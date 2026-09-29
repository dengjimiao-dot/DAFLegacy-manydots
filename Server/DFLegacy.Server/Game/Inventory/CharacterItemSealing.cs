namespace DFLegacy.Server;

public static class CharacterItemSealing
{
    public const ushort GoldenWaxItemId = 14;
    // DF2008 serializes this byte on each item instance. A non-zero value means
    // the [sealing] item is still sealed; the first successful equip binds it.
    public const byte Unsealed = 0;
    public const byte Sealed = 1;
    public const byte MaximumResealCount = 7;
    public const byte ReinforcementMask = 0x1F;
    private const int ResealCountShift = 5;

    public static byte GetResealCount(CharacterItemRecord item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return checked((byte)(item.State >> ResealCountShift));
    }

    public static CharacterItemRecord SetResealCount(
        CharacterItemRecord item,
        byte resealCount)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (resealCount > MaximumResealCount)
        {
            throw new ArgumentOutOfRangeException(nameof(resealCount));
        }

        return item with
        {
            State = checked((byte)(
                (item.State & ReinforcementMask)
                | (resealCount << ResealCountShift)))
        };
    }

    public static byte GetInitialSealState(ItemCatalog catalog, ushort itemId)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.TryGetDefinition(itemId, out var definition)
            ? GetInitialSealState(definition)
            : Unsealed;
    }

    public static byte GetInitialSealState(ItemDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.IsEquipment
            && definition.AttachType == ItemAttachType.Sealing
            ? Sealed
            : Unsealed;
    }

    public static CharacterItemRecord UnsealWhenEquipped(
        CharacterItemRecord item,
        ItemCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(catalog);

        return catalog.TryGetDefinition(item.ItemId, out var definition)
            ? UnsealWhenEquipped(item, definition)
            : item;
    }

    public static CharacterItemRecord UnsealWhenEquipped(
        CharacterItemRecord item,
        ItemDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(definition);

        return definition.AttachType == ItemAttachType.Sealing
            && item.SealState != Unsealed
                ? item with { SealState = Unsealed }
                : item;
    }

    public static bool CanTrade(
        CharacterItemRecord item,
        ItemDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(definition);

        return definition.AttachType == ItemAttachType.Free
            || definition.AttachType == ItemAttachType.Sealing
                && item.SealState != Unsealed;
    }

    public static bool CanTrade(
        CharacterMailAttachmentRecord item,
        ItemDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(definition);

        return definition.AttachType == ItemAttachType.Free
            || definition.AttachType == ItemAttachType.Sealing
                && item.SealState != Unsealed;
    }
}
