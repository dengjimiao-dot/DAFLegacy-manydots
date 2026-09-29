namespace DFLegacy.Server;

public sealed record DungeonItemPickupPlan(
    ushort DestinationSlot,
    Dictionary<ushort, CharacterItemRecord> Inventory);

public static class DungeonPickupPlanner
{
    public static bool TryPlanItem(
        IReadOnlyDictionary<ushort, CharacterItemRecord> inventory,
        DungeonGroundItem groundItem,
        ItemCatalog catalog,
        uint weightLimit,
        out DungeonItemPickupPlan plan,
        out InventoryPlacementFailure failure)
    {
        plan = null!;
        var preservedItem = groundItem.PreservedItem;
        if (!CharacterInventoryPlanner.TryPlace(
                inventory,
                new CharacterMailAttachmentRecord(
                    groundItem.ItemId,
                    groundItem.CountOrValue,
                    State: preservedItem?.State ?? 0,
                    Durability: preservedItem?.Durability
                        ?? catalog.GetInitialDurability(groundItem.ItemId),
                    SealState: preservedItem?.SealState
                        ?? CharacterItemSealing.GetInitialSealState(
                            catalog,
                            groundItem.ItemId),
                    AvatarRemainingSeconds:
                        preservedItem?.AvatarRemainingSeconds,
                    AvatarAbilityIndex: preservedItem?.AvatarAbilityIndex,
                    EquipmentQualitySeed:
                        preservedItem?.EquipmentQualitySeed,
                    InstanceId: preservedItem?.InstanceId ?? Guid.Empty),
                catalog,
                weightLimit,
                out var placement,
                out failure))
        {
            return false;
        }

        plan = new DungeonItemPickupPlan(
            placement.DestinationSlot,
            placement.Inventory);
        return true;
    }
}
