using System.Text;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// Inventory and equipment builders (07 §4.7): main/avatar/warehouse item
/// lists, worn-equipment snapshots and the inventory-refresh packet.
/// </summary>
public sealed partial class CharacterPacketBuilders
{
    internal GameServerPacket CreateMainInventoryPacket(
        int gold,
        uint victoryPoints,
        IEnumerable<CharacterItemRecord> inventory,
        ushort? preferredLastSlot = null)
    {
        var entries = inventory
            .Where(item => item.ItemId != 0
                && !CharacterWarehouseProgression.TryGetItemTargetCapacity(
                    item.ItemId,
                    out _)
                && (item.Slot >= CharacterInventoryLayout.QuickSlotStart
                    || (item.Slot == CharacterInventoryLayout.RevivalCoinSlot
                        && item.ItemId
                            == CharacterInventoryLayout.RevivalCoinItemId)))
            .OrderBy(item => preferredLastSlot.HasValue
                && item.Slot == preferredLastSlot.Value
                    ? 1
                    : 0)
            .ThenBy(item => item.Slot)
            .Select(ToGameInventoryEntry)
            .ToArray();
        return GameProtocolEngine.CreateMainItemList(
            checked((uint)Math.Max(0, gold)),
            victoryPoints,
            entries);
    }

    internal GameServerPacket CreateWarehousePacket(
        IEnumerable<CharacterItemRecord> warehouse,
        ushort warehouseCapacity)
    {
        var entries = warehouse
            .Where(item => item.Slot < warehouseCapacity
                && item.ItemId != 0
                && !CharacterWarehouseProgression.TryGetItemTargetCapacity(
                    item.ItemId,
                    out _))
            .OrderBy(item => item.Slot)
            .Select(ToGameInventoryEntry)
            .ToArray();
        return GameProtocolEngine.CreateItemList(
            2,
            entries,
            warehouseCapacity);
    }

    internal GameServerPacket CreateAvatarInventoryPacket(
        IEnumerable<CharacterItemRecord> avatarInventory)
    {
        var entries = avatarInventory
            .Where(item => CharacterAvatarInventoryLayout.IsBagSlot(item.Slot)
                && item.ItemId != 0)
            .OrderBy(item => item.Slot)
            .Select(ToGameInventoryEntry)
            .ToArray();
        return GameProtocolEngine.CreateItemList(1, entries);
    }

    internal IEnumerable<GameServerPacket> CreateInventoryRefreshPackets(
        int gold,
        uint victoryPoints,
        IEnumerable<CharacterItemRecord> inventory,
        IEnumerable<CharacterItemRecord> equipment)
    {
        yield return CreateMainInventoryPacket(gold, victoryPoints, inventory);
    }

    internal GameInventoryEntry[] CreateWornEquipmentSnapshot(
        IEnumerable<CharacterItemRecord> equipment,
        IReadOnlyDictionary<ushort, CharacterItemRecord> creatureInventory)
    {
        var snapshot = equipment
            .Where(item => item.Slot is < CharacterCreatureInventoryLayout.ClientEquippedCreatureSlot
                or > CharacterCreatureInventoryLayout.ClientEquippedArtifactGreenSlot)
            .Select(ToGameInventoryEntry)
            .ToList();
        var creatureSlots = new (ushort StorageSlot, ushort ClientSlot)[]
        {
            (CharacterCreatureInventoryLayout.EquippedCreatureSlot,
                CharacterCreatureInventoryLayout.ClientEquippedCreatureSlot),
            (CharacterCreatureInventoryLayout.EquippedArtifactRedSlot,
                CharacterCreatureInventoryLayout.ClientEquippedArtifactRedSlot),
            (CharacterCreatureInventoryLayout.EquippedArtifactBlueSlot,
                CharacterCreatureInventoryLayout.ClientEquippedArtifactBlueSlot),
            (CharacterCreatureInventoryLayout.EquippedArtifactGreenSlot,
                CharacterCreatureInventoryLayout.ClientEquippedArtifactGreenSlot)
        };
        foreach (var (storageSlot, clientSlot) in creatureSlots)
        {
            if (creatureInventory.TryGetValue(storageSlot, out var item))
            {
                snapshot.Add(ToGameInventoryEntry(item with { Slot = clientSlot }));
            }
        }

        return snapshot.OrderBy(item => item.Slot).ToArray();
    }

    internal static CharacterItemRecord NormalizeAvatarInstance(
        CharacterItemRecord item) =>
        item with
        {
            CountOrValue = 1,
            Durability = 0,
            AvatarRemainingSeconds = item.AvatarRemainingSeconds ?? 0,
            AvatarAbilityIndex = item.AvatarAbilityIndex ?? 0
        };
}
