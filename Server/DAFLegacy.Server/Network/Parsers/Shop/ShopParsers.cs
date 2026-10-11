using System.Buffers.Binary;
using System.Text;

namespace DAFLegacy.Server;

/// <summary>
/// Shop and trade request parsing (07 §4.7): BUY_ITEM (CMD 21), SELL_ITEM
/// (CMD 24), the Cera-shop purchase body (CMD 67) and ITEMTRADE_STATE
/// (CMD 26).
/// </summary>
internal static class ShopParsers
{
    internal readonly record struct CeraShopPurchaseRequestItem(
        byte AvatarAbilityIndex,
        ushort PeriodCode,
        uint CommodityNo);


    private static readonly Encoding ClientEncoding = PvfEncodings.Cp936Lossy();

    internal static bool TryReadBuyItem(
        byte[] body,
        out ushort itemId,
        out int countOrQualitySeed)
    {
        itemId = 0;
        countOrQualitySeed = 1;
        if (body.Length < 4)
        {
            return false;
        }

        // Stackable purchases use the second value as the count. Equipment
        // purchases use it as the client's daily shop-quality seed; the
        // server remains authoritative and recomputes that seed at 06:00.
        // Later clients widened both values to i32, while this target build
        // also emits the compact u16/u16 form.
        if (body.Length >= 6)
        {
            var rawItemId = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(2, 4));
            if (rawItemId is > 0 and <= ushort.MaxValue)
            {
                itemId = (ushort)rawItemId;
                if (body.Length >= 10)
                {
                    countOrQualitySeed = BinaryPrimitives.ReadInt32LittleEndian(
                        body.AsSpan(6, 4));
                }

                return true;
            }
        }

        itemId = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(2, 2));
        if (body.Length >= 6)
        {
            countOrQualitySeed = BinaryPrimitives.ReadUInt16LittleEndian(
                body.AsSpan(4, 2));
        }
        else if (body.Length >= 5)
        {
            countOrQualitySeed = body[4];
        }

        return itemId != 0;
    }

    internal static bool TryReadCeraShopPurchase(
        byte[] body,
        out CeraShopPurchaseRequestItem[] products,
        out bool isGift)
    {
        products = [];
        isGift = false;
        if (body.Length < 4)
        {
            return false;
        }

        var offset = 2;
        var giftMode = body[offset++];
        if (giftMode > 1)
        {
            return false;
        }

        if (giftMode == 1)
        {
            isGift = true;
            if (body.Length < offset + sizeof(uint))
            {
                return false;
            }

            var recipientLength = BinaryPrimitives.ReadUInt32LittleEndian(
                body.AsSpan(offset, sizeof(uint)));
            offset += sizeof(uint);
            if (recipientLength is 0 or > 30
                || recipientLength > (uint)(body.Length - offset))
            {
                return false;
            }

            offset += checked((int)recipientLength);
        }

        if (offset >= body.Length)
        {
            return false;
        }

        var count = body[offset++];
        if (count == 0 || count > 32)
        {
            return false;
        }

        var requiredLength = offset + count * 7;
        if (body.Length != requiredLength)
        {
            return false;
        }

        var parsed = new CeraShopPurchaseRequestItem[count];
        for (var index = 0; index < count; index++)
        {
            var avatarAbilityIndex = body[offset];
            var periodCode = BinaryPrimitives.ReadUInt16LittleEndian(
                body.AsSpan(offset + 1, sizeof(ushort)));
            var commodityNo = BinaryPrimitives.ReadUInt32LittleEndian(
                body.AsSpan(offset + 3, sizeof(uint)));
            if (commodityNo == 0)
            {
                return false;
            }

            parsed[index] = new CeraShopPurchaseRequestItem(
                avatarAbilityIndex,
                periodCode,
                commodityNo);
            offset += 7;
        }

        products = parsed;
        return true;
    }

    internal static bool TryReadSellItem(
        byte[] body,
        out byte listType,
        out ushort slot,
        out int count)
    {
        listType = 0;
        slot = 0;
        count = 0;
        if (body.Length == 5 && body[2] is 0 or 3)
        {
            listType = body[2];
            slot = body[3];
            count = body[4];
            return count > 0;
        }

        if (body.Length >= 7 && body[2] is 0 or 3)
        {
            listType = body[2];
            var signedSlot = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(3, 2));
            count = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(5, 2));
            if (signedSlot < 0)
            {
                return false;
            }

            slot = (ushort)signedSlot;
            return count > 0;
        }

        if (body.Length < 6)
        {
            return false;
        }

        var legacySlot = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(2, 2));
        count = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(4, 2));
        if (legacySlot < 0)
        {
            return false;
        }

        slot = (ushort)legacySlot;
        return count > 0;
    }

    internal static bool TryReadSetItemTradeState(
        byte[] body,
        out byte listType,
        out ushort slot,
        out ushort state)
    {
        listType = 0;
        slot = 0;
        state = 0;
        if (body.Length < 7 || body[2] is not (0 or 3))
        {
            return false;
        }

        listType = body[2];
        slot = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(3, 2));
        state = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(5, 2));
        return true;
    }

    internal static bool TryReadRepairEquipment(
        byte[] body,
        out byte itemSpace,
        out ushort slot)
    {
        itemSpace = 0;
        slot = 0;
        if (body.Length < 5 || body[2] is not (0 or 3))
        {
            return false;
        }

        itemSpace = body[2];
        slot = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(3, 2));
        return slot == ushort.MaxValue
            || ChannelSessionSenders.IsInventorySlotValid(
                itemSpace,
                slot,
                CharacterWarehouseProgression.InitialCapacity);
    }
}
