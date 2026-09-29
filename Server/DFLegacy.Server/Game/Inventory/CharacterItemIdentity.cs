namespace DFLegacy.Server;

public static class CharacterItemIdentity
{
    public static bool RequiresInstanceId(ItemDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.IsEquipment && !definition.IsStackable;
    }

    public static Guid CreateInstanceId() => Guid.CreateVersion7();

    public static CharacterItemRecord MoveToSlot(
        CharacterItemRecord item,
        ushort slot,
        ItemCatalog catalog)
    {
        // Move the UUID-owned record, not just its item ID. State includes both
        // reinforcement and reseal count and must not be reset on unequip.
        return Ensure(item, catalog) with { Slot = slot };
    }

    public static CharacterItemRecord Ensure(
        CharacterItemRecord item,
        ItemCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.TryGetDefinition(item.ItemId, out var definition)
            ? Ensure(item, definition)
            : item;
    }

    public static CharacterItemRecord Ensure(
        CharacterItemRecord item,
        ItemDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(definition);
        return RequiresInstanceId(definition) && item.InstanceId == Guid.Empty
            ? item with { InstanceId = CreateInstanceId() }
            : item;
    }

    public static CharacterMailAttachmentRecord Ensure(
        CharacterMailAttachmentRecord item,
        ItemCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.TryGetDefinition(item.ItemId, out var definition)
            ? Ensure(item, definition)
            : item;
    }

    public static CharacterMailAttachmentRecord Ensure(
        CharacterMailAttachmentRecord item,
        ItemDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(definition);
        return RequiresInstanceId(definition) && item.InstanceId == Guid.Empty
            ? item with { InstanceId = CreateInstanceId() }
            : item;
    }

    public static Guid GetUnitInstanceId(
        ItemDefinition definition,
        Guid preservedInstanceId,
        bool firstUnit)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!RequiresInstanceId(definition))
        {
            return Guid.Empty;
        }

        return firstUnit && preservedInstanceId != Guid.Empty
            ? preservedInstanceId
            : CreateInstanceId();
    }
}
