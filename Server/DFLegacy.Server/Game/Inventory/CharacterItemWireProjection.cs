namespace DFLegacy.Server;

public readonly record struct CharacterItemWireValues(
    uint AddInfo,
    byte ItemAttr,
    ushort Durability,
    byte SealState);

public static class CharacterItemWireProjection
{
    public static CharacterItemWireValues Project(
        CharacterMailAttachmentRecord item,
        ItemCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Project(
            new CharacterItemRecord(
                0,
                item.ItemId,
                item.CountOrValue,
                item.State,
                item.Durability,
                item.SealState,
                item.AvatarRemainingSeconds,
                item.AvatarAbilityIndex,
                EquipmentQualitySeed: item.EquipmentQualitySeed,
                InstanceId: item.InstanceId),
            catalog);
    }

    public static CharacterItemWireValues Project(
        CharacterItemRecord item,
        ItemCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(catalog);

        var addInfo = item.AvatarRemainingSeconds ?? item.CountOrValue;
        var itemAttr = item.State;
        if (catalog.TryGetDefinition(item.ItemId, out var definition)
            && definition.IsEquipment
            && definition.InventoryCategory == ItemInventoryCategory.Equipment)
        {
            // Title equipment has no variable quality. A zero add_info keeps
            // the target client on its fixed middle-grade display (string
            // index 1752), regardless of stale persisted quality data.
            addInfo = definition.IsTitle
                ? EquipmentQuality.MiddleQualitySeed
                : item.EquipmentQualitySeed ?? item.CountOrValue;
        }

        return new CharacterItemWireValues(
            addInfo,
            itemAttr,
            item.AvatarAbilityIndex ?? item.Durability,
            item.SealState);
    }
}
