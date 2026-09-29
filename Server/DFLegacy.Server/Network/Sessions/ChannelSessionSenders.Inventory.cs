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
        int exclusiveUpperBound = 256)
    {
        for (var slot = (int)firstSlot; slot < exclusiveUpperBound; slot++)
        {
            if (!inventory.ContainsKey((ushort)slot))
            {
                return (ushort)slot;
            }
        }

        return ushort.MaxValue;
    }

    // ---- Inventory-space validators and slot search helpers (migrated
    // verbatim; chain branches call them through delegating wrappers).

    internal static bool TryGetInventorySpace(
        byte listType,
        Dictionary<ushort, CharacterItemRecord> mainInventory,
        Dictionary<ushort, CharacterItemRecord> avatarInventory,
        Dictionary<ushort, CharacterItemRecord> equippedInventory,
        Dictionary<ushort, CharacterItemRecord> warehouseInventory,
        out Dictionary<ushort, CharacterItemRecord> space)
    {
        if (listType == 0)
        {
            space = mainInventory;
            return true;
        }

        if (listType == 3)
        {
            space = equippedInventory;
            return true;
        }

        if (listType == 1)
        {
            space = avatarInventory;
            return true;
        }

        if (listType == 2)
        {
            space = warehouseInventory;
            return true;
        }

        space = null!;
        return false;
    }

    internal static bool IsInventorySlotValid(
        byte listType,
        ushort slot,
        ushort warehouseCapacity) => listType switch
    {
        0 => CharacterInventoryLayout.IsMainItemSlot(slot),
        1 => CharacterAvatarInventoryLayout.IsBagSlot(slot),
        2 => slot < warehouseCapacity,
        3 => slot < 23,
        _ => false
    };

    internal bool IsCompatibleMainInventorySlot(
        CharacterItemRecord item,
        ushort slot) =>
        itemCatalog.TryGetDefinition(item.ItemId, out var definition)
        && definition.InventoryCategory is not (
            ItemInventoryCategory.Avatar or ItemInventoryCategory.Creature)
        && CharacterInventoryLayout.IsCompatibleSlot(definition, slot);

    internal bool IsCompatibleInventorySlot(
        byte listType,
        CharacterItemRecord item,
        ushort slot,
        ushort warehouseCapacity)
    {
        if (!itemCatalog.TryGetDefinition(item.ItemId, out var definition))
        {
            return false;
        }

        return listType switch
        {
            0 => definition.InventoryCategory is not (
                    ItemInventoryCategory.Avatar or ItemInventoryCategory.Creature)
                && CharacterInventoryLayout.IsCompatibleSlot(definition, slot),
            1 => definition.InventoryCategory == ItemInventoryCategory.Avatar
                && CharacterAvatarInventoryLayout.IsBagSlot(slot),
            2 => definition.InventoryCategory != ItemInventoryCategory.Avatar
                && slot < warehouseCapacity,
            3 when CharacterAvatarInventoryLayout.IsWornSlot(slot) =>
                CharacterAvatarInventoryLayout.IsCompatibleWornSlot(definition, slot),
            3 => slot < 23
                && definition.InventoryCategory == ItemInventoryCategory.Equipment,
            _ => false
        };
    }

    internal ushort FindFreeCompatibleMainInventorySlot(
        IReadOnlyDictionary<ushort, CharacterItemRecord> inventory,
        CharacterItemRecord item)
    {
        if (!itemCatalog.TryGetDefinition(item.ItemId, out var definition))
        {
            return ushort.MaxValue;
        }

        if (CharacterInventoryLayout.TryGetCategoryRange(
                definition.InventoryCategory,
                out var start,
                out var end))
        {
            var categorySlot = FindFreeInventorySlot(inventory, start, end);
            if (categorySlot != ushort.MaxValue)
            {
                return categorySlot;
            }
        }

        return FindFreeInventorySlot(
            inventory,
            CharacterInventoryLayout.QuickSlotStart,
            CharacterInventoryLayout.QuickSlotEnd);
    }

    internal ushort FindPartialStackSlot(
        IReadOnlyDictionary<ushort, CharacterItemRecord> inventory,
        CharacterItemRecord item,
        byte listType,
        ushort? excludedSlot)
    {
        if (listType is not (0 or 2)
            || !itemCatalog.TryGetDefinition(item.ItemId, out var definition)
            || !definition.IsStackable
            || listType == 2
                && definition.InventoryCategory == ItemInventoryCategory.Avatar)
        {
            return ushort.MaxValue;
        }

        var stackLimit = CharacterInventoryPlanner.GetStackLimit(definition);
        return inventory.Values
            .Where(candidate => candidate.Slot != excludedSlot
                && candidate.ItemId == item.ItemId
                && candidate.State == item.State
                && candidate.Durability == item.Durability
                && candidate.SealState == item.SealState
                && candidate.CountOrValue < stackLimit
                && (listType != 0
                    || CharacterInventoryLayout.IsCompatibleSlot(
                        definition,
                        candidate.Slot)))
            .OrderBy(candidate => candidate.Slot)
            .Select(candidate => candidate.Slot)
            .DefaultIfEmpty(ushort.MaxValue)
            .First();
    }
}
