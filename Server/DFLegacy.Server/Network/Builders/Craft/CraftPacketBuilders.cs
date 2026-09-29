using System.Buffers.Binary;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// Craft packet builders (07 §4.8.3 三层镜像: Builders/Craft).
/// Moved verbatim from GameProtocolEngine (R7); byte output unchanged.
/// </summary>
internal static class CraftPacketBuilders
{
    public static GameServerPacket CreateGetItemSilentRejection(PacketFrame request)
    {
        if (request.ProtocolId != GameProtocolEngine.GetItemCommand)
        {
            throw new ArgumentException(
                "A silent item-pickup rejection requires a GET_ITEM request.",
                nameof(request));
        }

        return ConnectionPacketBuilders.CreateCommandError(request, GameProtocolEngine.GetItemSilentRejectionError);
    }
    public static GameServerPacket CreateDisjointItemReply(
        ushort sourceSlot,
        byte itemSpace,
        IReadOnlyList<GameDisjointRewardEntry> rewards)
    {
        if (rewards.Count > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(rewards));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write(sourceSlot);
        writer.Write(itemSpace);
        writer.Write((byte)rewards.Count);
        foreach (var reward in rewards)
        {
            writer.Write(reward.DestinationSlot);
            writer.Write(reward.ItemId);
            writer.Write(reward.Count);
        }

        return new GameServerPacket(
            GameProtocolEngine.CommandPacketType,
            GameProtocolEngine.DisjointItemCommand,
            payload.ToArray());
    }
    public static bool TryParseCompoundItemCommand(
        ReadOnlySpan<byte> body,
        out GameCompoundItemCommand command)
    {
        command = default;
        const int bodyLength = sizeof(ushort) // command sequence
            + sizeof(ushort)                 // recipe item id or inventory slot
            + sizeof(byte)                   // source is item id
            + sizeof(byte)                   // craft count
            + sizeof(byte)                   // reserved zero
            + sizeof(byte);                  // fixed one
        if (body.Length != bodyLength
            || body[4] > 1
            || body[5] == 0
            || body[6] != 0
            || body[7] != 1)
        {
            return false;
        }

        command = new GameCompoundItemCommand(
            BinaryPrimitives.ReadUInt16LittleEndian(body),
            BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(2, sizeof(ushort))),
            body[4] != 0,
            body[5]);
        return true;
    }
    public static GameServerPacket CreateCompoundItemReply(
        IReadOnlyList<GameCompoundItemConsumption> consumedItems,
        IReadOnlyList<GameCompoundItemResult> resultItems)
    {
        ArgumentNullException.ThrowIfNull(consumedItems);
        ArgumentNullException.ThrowIfNull(resultItems);
        if (consumedItems.Count > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(consumedItems));
        }

        if (resultItems.Count > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(resultItems));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write(checked((byte)consumedItems.Count));
        foreach (var item in consumedItems)
        {
            writer.Write(item.ListType);
            writer.Write(item.Slot);
            writer.Write(item.RemainingCount);
        }

        writer.Write(checked((byte)resultItems.Count));
        foreach (var item in resultItems)
        {
            writer.Write(item.Slot);
            writer.Write(item.ItemId);
            writer.Write(item.CountOrValue);
            writer.Write(item.State);
            writer.Write(item.Durability);
        }

        return new GameServerPacket(
            GameProtocolEngine.CommandPacketType,
            GameProtocolEngine.CompoundItemCommand,
            payload.ToArray());
    }
    public static GameServerPacket CreateCompoundItemFailure(byte errorCode) =>
        new(
            GameProtocolEngine.CommandPacketType,
            GameProtocolEngine.CompoundItemCommand,
            [0, errorCode]);
    public static bool TryParseUpgradeItemCommand(
        ReadOnlySpan<byte> body,
        out GameUpgradeItemCommand command)
    {
        command = default;
        const int bodyLength = sizeof(ushort) // command sequence
            + sizeof(ushort)                 // target slot
            + sizeof(ushort)                 // target item id
            + sizeof(ushort);                // material slot
        if (body.Length != bodyLength)
        {
            return false;
        }

        command = new GameUpgradeItemCommand(
            BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(2, sizeof(ushort))),
            BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(4, sizeof(ushort))),
            BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(6, sizeof(ushort))));
        return command.TargetItemId != 0
            && command.TargetSlot != command.MaterialSlot;
    }
    public static GameServerPacket CreateUpgradeItemReply(
        ushort materialSlot,
        uint materialRemaining,
        byte resultCode,
        byte newLevel,
        ushort targetSlot)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write(materialSlot);
        writer.Write(materialRemaining);
        writer.Write(resultCode);
        writer.Write(newLevel);
        writer.Write(targetSlot);
        if (resultCode > 2)
        {
            writer.Write((byte)0);
        }

        return new GameServerPacket(
            GameProtocolEngine.CommandPacketType,
            GameProtocolEngine.UpgradeItemCommand,
            payload.ToArray());
    }
    public static bool TryParseResetItemAttributeCommand(
        ReadOnlySpan<byte> body,
        out GameResetItemAttributeCommand command)
    {
        command = default;
        const int bodyLength = sizeof(ushort) // command sequence
            + sizeof(ushort)                 // target slot
            + sizeof(ushort)                 // target item id
            + sizeof(ushort);                // material slot
        if (body.Length != bodyLength)
        {
            return false;
        }

        command = new GameResetItemAttributeCommand(
            BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(2, sizeof(ushort))),
            BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(4, sizeof(ushort))),
            BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(6, sizeof(ushort))));
        return command.TargetItemId != 0
            && command.TargetSlot != command.MaterialSlot;
    }
    public static GameServerPacket CreateResetItemAttributeReply(
        ushort materialSlot,
        uint materialRemaining,
        ushort operationType)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write(materialSlot);
        writer.Write(materialRemaining);
        writer.Write(operationType);
        return new GameServerPacket(
            GameProtocolEngine.CommandPacketType,
            GameProtocolEngine.ResetItemAttributeCommand,
            payload.ToArray());
    }
    public static GameServerPacket CreateResetItemAttributeFailure(byte errorCode) =>
        new(
            GameProtocolEngine.CommandPacketType,
            GameProtocolEngine.ResetItemAttributeCommand,
            [0, errorCode]);
    public static bool TryParseCompoundAvatarCommand(
        ReadOnlySpan<byte> body,
        out GameCompoundAvatarCommand command)
    {
        command = default;
        const int bodyLength = sizeof(ushort) // command sequence
            + sizeof(ushort)                 // material slot
            + sizeof(ushort)                 // first item-list marker
            + sizeof(ushort)                 // first avatar item id
            + sizeof(ushort)                 // second item-list marker
            + sizeof(ushort)                 // second avatar item id
            + sizeof(ushort)                 // option-list marker
            + sizeof(byte)                   // selected option row
            + sizeof(byte)                   // first avatar slot
            + sizeof(byte);                  // second avatar slot
        if (body.Length != bodyLength)
        {
            return false;
        }

        command = new GameCompoundAvatarCommand(
            BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(2, sizeof(ushort))),
            BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(6, sizeof(ushort))),
            BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(10, sizeof(ushort))),
            body[14],
            body[15],
            body[16]);
        return true;
    }
    public static GameServerPacket CreateCompoundAvatarReply(
        IEnumerable<GameAvatarCompoundConsumption> consumedItems,
        ushort resultSlot,
        ushort resultItemId,
        uint remainingSeconds,
        ushort selectedOption)
    {
        var consumed = (consumedItems ?? [])
            .Take(byte.MaxValue)
            .ToArray();
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write(checked((byte)consumed.Length));
        foreach (var item in consumed)
        {
            writer.Write(item.ListType);
            writer.Write(item.Slot);
            writer.Write(item.RemainingCount);
        }

        writer.Write(resultSlot);
        writer.Write(resultItemId);
        writer.Write(remainingSeconds);
        writer.Write(selectedOption);
        return new GameServerPacket(
            GameProtocolEngine.CommandPacketType,
            GameProtocolEngine.CompoundAvatarCommand,
            payload.ToArray());
    }
    public static GameServerPacket CreateCompoundAvatarFailure(byte errorCode = 4) =>
        new(
            GameProtocolEngine.CommandPacketType,
            GameProtocolEngine.CompoundAvatarCommand,
            [0, errorCode]);
    public static GameServerPacket CreateUseLotteryItemReply(
        ushort sourceSlot,
        ushort destinationSlot,
        ushort itemId,
        uint countOrValue,
        ushort instanceValue = 0,
        byte itemAttribute = 0)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write(sourceSlot);
        writer.Write(destinationSlot);
        writer.Write(itemId);
        writer.Write(countOrValue);
        writer.Write(instanceValue);
        writer.Write(itemAttribute);
        return new GameServerPacket(
            GameProtocolEngine.CommandPacketType,
            GameProtocolEngine.UseLotteryItemCommand,
            payload.ToArray());
    }
    public static bool TryParseDropItemCommand(
        ReadOnlySpan<byte> body,
        out GameDropItemCommand command)
    {
        command = default;
        const int bodyLength = sizeof(ushort) // command sequence
            + sizeof(short)                  // map x
            + sizeof(short)                  // map y
            + sizeof(byte)                   // item space
            + sizeof(ushort)                 // source slot
            + sizeof(uint);                  // count
        if (body.Length != bodyLength)
        {
            return false;
        }

        command = new GameDropItemCommand(
            BinaryPrimitives.ReadInt16LittleEndian(body.Slice(2, sizeof(short))),
            BinaryPrimitives.ReadInt16LittleEndian(body.Slice(4, sizeof(short))),
            body[6],
            BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(7, sizeof(ushort))),
            BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(9, sizeof(uint))));
        return command.Count > 0;
    }
    public static GameServerPacket CreateDropItemReply(
        byte itemSpace,
        ushort slot,
        uint count)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write(itemSpace);
        writer.Write(slot);
        writer.Write(count);
        return new GameServerPacket(
            GameProtocolEngine.CommandPacketType,
            GameProtocolEngine.DropItemCommand,
            payload.ToArray());
    }
    public static GameServerPacket CreateDropItemErrorReply(
        byte errorCode,
        byte itemSpace) =>
        new(
            GameProtocolEngine.CommandPacketType,
            GameProtocolEngine.DropItemCommand,
            [0, errorCode, itemSpace]);
    public static GameServerPacket CreateDropItem(
        ushort userId,
        short x,
        short y,
        GameDungeonDrop drop)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(userId);
        writer.Write(x);
        writer.Write(y);
        writer.Write(drop.GroundId);
        writer.Write(drop.ItemId);
        // DFLegacy NOTI 40 does not reuse the five-field record embedded in
        // NOTI 38. The client reads item attr as a byte before add-info and
        // does not read an owner id at the end of this notification.
        writer.Write(drop.ItemAttr);
        writer.Write(drop.AddInfo);
        writer.Write(drop.Durability);
        return new GameServerPacket(
            GameProtocolEngine.NotificationPacketType,
            GameProtocolEngine.DropItemNotification,
            payload.ToArray());
    }
}
