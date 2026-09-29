using System.Buffers.Binary;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// Inventory packet builders (07 §4.8.3 三层镜像: Builders/Inventory).
/// Moved verbatim from the R7 engine split; byte output unchanged.
/// </summary>
internal static class InventoryPacketBuilders
{
    public static GameServerPacket CreateDeleteItemReply(
        byte listType,
        IReadOnlyList<GameItemConsumptionEntry> entries)
    {
        if (entries.Count > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(entries));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write(listType);
        writer.Write((byte)entries.Count);
        foreach (var entry in entries)
        {
            writer.Write(entry.Slot);
            writer.Write(entry.ConsumedCount);
        }

        return new GameServerPacket(
            GameProtocolConstants.CommandPacketType,
            (byte)CmdPacketType.DELETE_ITEM,
            payload.ToArray());
    }
    public static GameServerPacket CreateDeleteItemError(
        byte listType,
        byte errorCode) =>
        new(
            GameProtocolConstants.CommandPacketType,
            (byte)CmdPacketType.DELETE_ITEM,
            [0, errorCode, listType]);
    public static GameServerPacket CreateUseStackableItemReply(
        ushort slot,
        byte listType) =>
        new(
            GameProtocolConstants.CommandPacketType,
            (byte)CmdPacketType.USE_STACKABLE,
            [
                1,
                (byte)slot,
                (byte)(slot >> 8),
                listType
            ]);
    public static GameServerPacket CreateUseStackableItemError(
        byte errorCode,
        byte listType) =>
        new(
            GameProtocolConstants.CommandPacketType,
            (byte)CmdPacketType.USE_STACKABLE,
            [0, errorCode, listType]);
    public static GameServerPacket CreateItemList(
        byte listType,
        IReadOnlyList<GameInventoryEntry> items,
        ushort listParameter = 0)
    {
        if (items.Count > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(items));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(listType);
        if (listType == 2)
        {
            writer.Write(listParameter);
        }

        writer.Write((ushort)items.Count);
        foreach (var item in items)
        {
            writer.Write(item.Slot);
            writer.Write(item.ItemId);
            writer.Write(item.CountOrValue);
            writer.Write(item.State);
            writer.Write(item.Durability);
            writer.Write(item.SealState);
        }

        return new GameServerPacket(
            GameProtocolConstants.NotificationPacketType,
            (byte)NotiPacketType.ITEM_LIST,
            payload.ToArray());
    }
    public static GameServerPacket CreateMainItemList(
        uint gold,
        IReadOnlyList<GameInventoryEntry> items)
    {
        return InventoryPacketBuilders.CreateMainItemList(gold, victoryPoints: 0, items);
    }
    public static GameServerPacket CreateUpdateItemList(
        byte listType,
        IReadOnlyList<GameInventoryEntry> items)
    {
        if (items.Count > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(items));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(listType);
        writer.Write((ushort)items.Count);
        foreach (var item in items)
        {
            writer.Write(item.Slot);
            writer.Write(item.ItemId);
            writer.Write(item.CountOrValue);
            writer.Write(item.State);
            writer.Write(item.Durability);
            writer.Write(item.SealState);
        }

        return new GameServerPacket(
            GameProtocolConstants.NotificationPacketType,
            (byte)NotiPacketType.UPDATE_ITEM_LIST,
            payload.ToArray());
    }
    public static GameServerPacket CreateMoveItemSpaceReply(
        byte sourceListType,
        ushort sourceSlot,
        uint movedCount,
        byte destinationListType,
        ushort destinationSlot)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write(sourceListType);
        writer.Write(sourceSlot);
        writer.Write(movedCount);
        writer.Write(destinationListType);
        writer.Write(destinationSlot);

        return new GameServerPacket(
            GameProtocolConstants.CommandPacketType,
            (byte)CmdPacketType.MOVE_ITEMSPACE,
            payload.ToArray());
    }
    public static GameServerPacket CreateSortItemReply(byte itemSpace) =>
        new(GameProtocolConstants.CommandPacketType, (byte)CmdPacketType.SORT_ITEM, [1, itemSpace]);
    public static GameServerPacket CreateSortItemError(
        byte itemSpace,
        byte errorCode = 1) =>
        new(GameProtocolConstants.CommandPacketType, (byte)CmdPacketType.SORT_ITEM, [0, errorCode, itemSpace]);
    public static GameServerPacket CreateDecreaseDurabilityReply(byte slot) =>
        new(
            GameProtocolConstants.CommandPacketType,
            (byte)CmdPacketType.DECREASE_DURABILITY,
            [1, slot]);

    public static GameServerPacket CreateMainItemList(
        uint gold,
        uint victoryPoints,
        IReadOnlyList<GameInventoryEntry> items)
    {
        if (items.Count > ushort.MaxValue - 2)
        {
            throw new ArgumentOutOfRangeException(nameof(items));
        }

        // DFLegacy keeps three virtual currency objects in main-list slots 0..2:
        // gold (slot 0/item 0), revival coins (slot 1/item 1), and victory
        // points (slot 2/item 2). ITEM_LIST has no separate wallet header.
        // Revival coins remain inventory-backed, while the two account/character
        // numeric balances are synthesized here on every full main-list update.
        var entries = new GameInventoryEntry[items.Count + 2];
        entries[0] = new GameInventoryEntry(
            Slot: 0,
            ItemId: 0,
            CountOrValue: gold);
        entries[1] = new GameInventoryEntry(
            Slot: 2,
            ItemId: 2,
            CountOrValue: victoryPoints);
        for (var index = 0; index < items.Count; index++)
        {
            entries[index + 2] = items[index];
        }

        return InventoryPacketBuilders.CreateItemList(0, entries);
    }
}
