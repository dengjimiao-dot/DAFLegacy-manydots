using System.Buffers.Binary;

namespace DAFLegacy.Server;

/// <summary>
/// Inventory request parsing (07 §4.7): SORT_ITEM (CMD 20), MOVE_ITEM_SPACE
/// (CMD 19) and DECREASE_DURABILITY (CMD 51) bodies.
/// </summary>
internal static class InventoryParsers
{
    internal readonly record struct ConsumeItemsRequestEntry(
        ushort Operation,
        ushort Slot,
        ushort ItemId,
        uint RequestedCount);


    internal static bool TryReadSortItem(byte[] body, out byte itemSpace)
    {
        itemSpace = 0;
        if (body.Length < 3)
        {
            return false;
        }

        itemSpace = body[2];
        return true;
    }

    internal static bool TryReadDecreaseDurability(
        byte[] body,
        out byte slot)
    {
        slot = 0;
        if (body.Length < 3)
        {
            return false;
        }

        slot = body[2];
        return true;
    }

    internal static bool TryReadInventoryMove(
        byte[] body,
        out byte sourceListType,
        out ushort sourceSlot,
        out int moveCount,
        out byte destinationListType,
        out ushort destinationSlot)
    {
        sourceListType = 0;
        sourceSlot = 0;
        moveCount = 0;
        destinationListType = 0;
        destinationSlot = 0;
        if (body.Length < 20)
        {
            return false;
        }

        sourceListType = body[2];
        var signedSourceSlot = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(3, 2));
        moveCount = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(7, 4));
        destinationListType = body[11];
        var signedDestinationSlot = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(12, 2));
        if (signedSourceSlot < 0 || moveCount < 0)
        {
            return false;
        }

        sourceSlot = (ushort)signedSourceSlot;
        destinationSlot = signedDestinationSlot < 0
            ? ushort.MaxValue
            : (ushort)signedDestinationSlot;
        return true;
    }

    internal static bool TryReadConsumeItems(
        byte[] body,
        out byte listType,
        out ConsumeItemsRequestEntry[] entries)
    {
        listType = byte.MaxValue;
        entries = [];
        if (body.Length < 4)
        {
            return false;
        }

        // sequence:u16, list-type:u8, count:u8, followed by:
        // operation:u16, slot:u16, item-id:u16, requested-count:u32.
        // The client appends a four-byte command token after all entries.
        listType = body[2];
        var count = body[3];
        const int entrySize = sizeof(ushort) * 3 + sizeof(uint);
        if (count == 0 || count > 32 || body.Length < 4 + count * entrySize)
        {
            return false;
        }

        var parsed = new ConsumeItemsRequestEntry[count];
        var offset = 4;
        for (var index = 0; index < count; index++)
        {
            parsed[index] = new ConsumeItemsRequestEntry(
                BinaryPrimitives.ReadUInt16LittleEndian(
                    body.AsSpan(offset, sizeof(ushort))),
                BinaryPrimitives.ReadUInt16LittleEndian(
                    body.AsSpan(offset + 2, sizeof(ushort))),
                BinaryPrimitives.ReadUInt16LittleEndian(
                    body.AsSpan(offset + 4, sizeof(ushort))),
                BinaryPrimitives.ReadUInt32LittleEndian(
                    body.AsSpan(offset + 6, sizeof(uint))));
            offset += entrySize;
        }

        entries = parsed;
        return true;
    }

    internal static bool TryReadStackableItemUse(
        byte[] body,
        out ushort slot,
        out byte listType)
    {
        slot = ushort.MaxValue;
        listType = byte.MaxValue;
        if (body.Length < 5)
        {
            return false;
        }

        // DNF 1.0.1.9 USE_STACKABLE is sequence:u16,
        // slot:u16, list-type:u8.
        slot = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(2, 2));
        listType = body[4];
        return slot is >= 3 and < ushort.MaxValue;
    }
}
