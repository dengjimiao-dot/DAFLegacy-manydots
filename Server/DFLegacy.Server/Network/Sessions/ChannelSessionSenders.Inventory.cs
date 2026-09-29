using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// Inventory helpers (07 §4.7): warehouse settlement, weight limit and
/// the space validators / slot search used by the item handlers.
/// </summary>
public sealed partial class ChannelSessionSenders
{
    public CharacterWarehouseUpgradeItemSettlement PlanWarehouseUpgradeSettlement(
        ConnectionContext context,
        IEnumerable<CharacterItemRecord> inventory,
        IEnumerable<CharacterItemRecord> warehouse,
        IEnumerable<ushort>? acquiredItemIds = null)
    {
        var settlement = CharacterWarehouseProgression.SettleUpgradeItems(
            inventory,
            warehouse,
            context.State.Inventory.WarehouseCapacity);
        var targetCapacity = settlement.Capacity;
        foreach (var itemId in acquiredItemIds ?? [])
        {
            targetCapacity = CharacterWarehouseProgression.ApplyItemTarget(
                targetCapacity,
                itemId);
        }

        return targetCapacity == settlement.Capacity
            ? settlement
            : settlement with { Capacity = targetCapacity };
    }

    public uint GetInventoryWeightLimit(ConnectionContext context, CharacterRecord character) =>
        CharacterInventoryPlanner.CalculateMainInventoryWeightAllowance(
            characterStatCatalog.Get(character).InventoryLimit,
            context.State.Inventory.Equipped.Values,
            context.State.Inventory.Creature.Values,
            itemCatalog);

    /// <summary>
    /// Adopts a saved warehouse-upgrade state after a store round-trip; the
    /// former AdoptSavedWarehouseUpgradeState local function. Returns true
    /// when the capacity changed.
    /// </summary>

    public bool AdoptSavedWarehouseUpgradeState(ConnectionContext context, CharacterRecord savedCharacter)
    {
        var previousCapacity = context.State.Inventory.WarehouseCapacity;
        context.State.Character.Active = savedCharacter;
        context.State.Inventory.Main = (savedCharacter.Inventory ?? [])
            .ToDictionary(item => item.Slot);
        context.State.Inventory.Warehouse = (savedCharacter.Warehouse ?? [])
            .ToDictionary(item => item.Slot);
        context.State.Inventory.WarehouseCapacity = CharacterWarehouseProgression.Normalize(
            savedCharacter.WarehouseCapacity);
        return context.State.Inventory.WarehouseCapacity != previousCapacity;
    }

    public async Task SendWarehouseStateAsync(ConnectionContext context, CancellationToken cancellationToken)
    {
        var warehouseRefresh = builders.CreateWarehousePacket(
            context.State.Inventory.Warehouse.Values,
            context.State.Inventory.WarehouseCapacity);
        await context.Outbound.SendAsync(warehouseRefresh, "WAREHOUSE_STATE", cancellationToken);
    }

    // ---- Quest-flow helpers (migrated verbatim from the former local
    // functions; the dungeon branches keep delegating wrappers).

    internal static ushort FindFreeInventorySlot(
        IReadOnlyDictionary<ushort, CharacterItemRecord> inventory,
        ushort firstSlot,
        int exclusiveUpperBound = 256) =>
        CharacterInventorySpaceRules.FindFreeInventorySlot(inventory, firstSlot, exclusiveUpperBound);

    // ---- Inventory-space validators and slot search helpers (migrated
    // verbatim; chain branches call them through delegating wrappers).

    internal static bool TryGetInventorySpace(
        byte listType,
        Dictionary<ushort, CharacterItemRecord> mainInventory,
        Dictionary<ushort, CharacterItemRecord> avatarInventory,
        Dictionary<ushort, CharacterItemRecord> equippedInventory,
        Dictionary<ushort, CharacterItemRecord> warehouseInventory,
        out Dictionary<ushort, CharacterItemRecord> space) =>
        CharacterInventorySpaceRules.TryGetInventorySpace(
            listType, mainInventory, avatarInventory, equippedInventory, warehouseInventory, out space);

    internal static bool IsInventorySlotValid(
        byte listType,
        ushort slot,
        ushort warehouseCapacity) =>
        CharacterInventorySpaceRules.IsInventorySlotValid(listType, slot, warehouseCapacity);

    internal bool IsCompatibleMainInventorySlot(
        CharacterItemRecord item,
        ushort slot) =>
        CharacterInventorySpaceRules.IsCompatibleMainInventorySlot(itemCatalog, item, slot);

    internal bool IsCompatibleInventorySlot(
        byte listType,
        CharacterItemRecord item,
        ushort slot,
        ushort warehouseCapacity) =>
        CharacterInventorySpaceRules.IsCompatibleInventorySlot(
            itemCatalog, listType, item, slot, warehouseCapacity);

    internal ushort FindFreeCompatibleMainInventorySlot(
        IReadOnlyDictionary<ushort, CharacterItemRecord> inventory,
        CharacterItemRecord item) =>
        CharacterInventorySpaceRules.FindFreeCompatibleMainInventorySlot(itemCatalog, inventory, item);

    internal ushort FindPartialStackSlot(
        IReadOnlyDictionary<ushort, CharacterItemRecord> inventory,
        CharacterItemRecord item,
        byte listType,
        ushort? excludedSlot) =>
        CharacterInventorySpaceRules.FindPartialStackSlot(
            itemCatalog, inventory, item, listType, excludedSlot);
}
