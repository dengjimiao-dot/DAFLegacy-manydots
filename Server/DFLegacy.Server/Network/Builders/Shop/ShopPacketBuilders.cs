using System.Buffers.Binary;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// Shop packet builders (07 §4.8.3 三层镜像: Builders/Shop).
/// Moved verbatim from GameProtocolEngine (R7); byte output unchanged.
/// </summary>
internal static class ShopPacketBuilders
{
    public static GameServerPacket CreateBuyItemReply(
        uint updatedGold,
        uint updatedVictoryPoints,
        uint auxiliaryItemSpaceValue,
        uint updatedCera,
        ushort slot,
        ushort itemId,
        uint countOrValue)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        // DF2008's command-21 success handler reads four complete wallet values
        // before the purchased item tuple. In particular, the fourth u32 is
        // copied into the Cera balance. A shorter reply makes the client read
        // its ASCII-'0' receive-buffer padding as 0x30303030 (808464432 Cera).
        writer.Write(updatedGold);
        writer.Write(updatedVictoryPoints);
        writer.Write(auxiliaryItemSpaceValue);
        writer.Write(updatedCera);
        writer.Write(slot);
        writer.Write(itemId);
        writer.Write(countOrValue);
        return new GameServerPacket(
            GameProtocolEngine.CommandPacketType,
            (byte)CmdPacketType.BUY_ITEM,
            payload.ToArray());
    }
    public static GameServerPacket CreateCeraShopBuyReply(
        int shopCategory,
        uint commodityNo,
        IReadOnlyList<GameCeraShopReward>? rewards = null)
    {
        rewards ??= [];
        if (rewards.Count > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rewards),
                "DF2008 command 67 supports at most 65535 result entries.");
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write((byte)0);
        writer.Write(Math.Clamp(shopCategory, 0, 3));
        writer.Write(commodityNo);
        // The DF2008 client keys its Cera result-item map by these values.
        // Keeping them equal also selects the normal purchase-completion path.
        writer.Write(commodityNo);
        writer.Write(commodityNo);
        writer.Write(ushort.MaxValue);
        writer.Write((ushort)rewards.Count);
        foreach (var reward in rewards)
        {
            writer.Write(reward.ItemId);
            writer.Write(reward.Count);
        }

        writer.Write(ushort.MaxValue);
        writer.Write(0u);
        return new GameServerPacket(
            GameProtocolEngine.CommandPacketType,
            (byte)CmdPacketType.BUY_CERASHOP_ITEM,
            payload.ToArray());
    }
    public static GameServerPacket CreateCeraShopBuyError(
        byte errorCode,
        int shopCategory = 0,
        uint commodityNo = uint.MaxValue)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)0);
        writer.Write(errorCode);
        writer.Write(Math.Clamp(shopCategory, 0, 3));
        writer.Write(commodityNo);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write((ushort)0);
        return new GameServerPacket(
            GameProtocolEngine.CommandPacketType,
            (byte)CmdPacketType.BUY_CERASHOP_ITEM,
            payload.ToArray());
    }
    public static GameServerPacket CreateCeraTicketReply(
        string billingKey,
        uint ticketNumber = 0)
    {
        var keyBytes = System.Text.Encoding.ASCII.GetBytes(billingKey);
        if (keyBytes.Length is 0 or >= 64)
        {
            throw new ArgumentOutOfRangeException(
                nameof(billingKey),
                "The DFLegacy Cera ticket must contain 1 to 63 ASCII bytes.");
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write((uint)keyBytes.Length);
        writer.Write(keyBytes);
        writer.Write(ticketNumber);
        return new GameServerPacket(
            GameProtocolEngine.CommandPacketType,
            (byte)CmdPacketType.GEN_CERATICKET,
            payload.ToArray());
    }
    public static GameServerPacket CreateCeraUpdate(bool success, uint value)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(success ? (byte)1 : (byte)0);
        writer.Write(value);
        return new GameServerPacket(
            GameProtocolEngine.NotificationPacketType,
            (byte)NotiPacketType.CERA,
            payload.ToArray());
    }
    public static GameServerPacket CreateSellItemReply(
        uint updatedGold,
        byte itemSpace,
        ushort slot,
        ushort soldCount)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write(updatedGold);
        writer.Write(itemSpace);
        writer.Write(slot);
        writer.Write(soldCount);
        return new GameServerPacket(
            GameProtocolEngine.CommandPacketType,
            (byte)CmdPacketType.SELL_ITEM,
            payload.ToArray());
    }
    public static GameServerPacket CreateRepairEquipmentReply(
        uint updatedGold,
        byte itemSpace,
        ushort slot)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write(updatedGold);
        writer.Write(itemSpace);
        writer.Write(slot);
        return new GameServerPacket(
            GameProtocolEngine.CommandPacketType,
            (byte)CmdPacketType.REPAIR_EQUIPMENT,
            payload.ToArray());
    }
    public static GameServerPacket CreateSetItemTradeStateReply(
        ushort itemId,
        byte listType)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write(itemId);
        writer.Write(listType);
        writer.Write((byte)0);
        return new GameServerPacket(
            GameProtocolEngine.CommandPacketType,
            (byte)CmdPacketType.SET_ITEMTRADE_STATE,
            payload.ToArray());
    }
}
