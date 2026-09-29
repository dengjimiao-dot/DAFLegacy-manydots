using System.Buffers.Binary;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// Quest packet builders (07 §4.8.3 三层镜像: Builders/Quest).
/// Moved verbatim from GameProtocolEngine (R7); byte output unchanged.
/// </summary>
internal static class QuestPacketBuilders
{
    public static GameServerPacket CreateAcceptableQuestList(
        IReadOnlyList<ushort> questIds)
    {
        if (questIds.Count > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(questIds));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)questIds.Count);
        foreach (var questId in questIds)
        {
            writer.Write(questId);
        }

        return new GameServerPacket(
            GameProtocolEngine.NotificationPacketType,
            GameProtocolEngine.AcceptableQuestListNotification,
            payload.ToArray());
    }
    public static GameServerPacket CreateEmptyAcceptableQuestList() =>
        QuestPacketBuilders.CreateAcceptableQuestList([]);
    public static GameServerPacket CreateAcceptQuestReply(
        ushort questId,
        uint trigger = 0,
        IReadOnlyList<GameQuestInsertedItem>? insertedItems = null)
    {
        // DF2008 0x4133D1 reads quest:u16, trigger:u32, grants:u8.
        // 0x4EC960 hides the script's [delete npc index] when trigger != 0;
        // CMD 35 trigger 0 restores it through 0x4EBB60. No extra NPC packet.
        var inserted = insertedItems ?? [];
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write(questId);
        writer.Write(trigger);
        writer.Write(checked((byte)Math.Min(byte.MaxValue, inserted.Count)));
        foreach (var item in inserted.Take(byte.MaxValue))
        {
            // DF2008 CMD 33 reads slot, item ID, then the inserted count/add_info.
            writer.Write(item.Slot);
            writer.Write(item.ItemId);
            writer.Write(item.CountOrSeed);
        }

        return new GameServerPacket(GameProtocolEngine.CommandPacketType, GameProtocolEngine.AcceptQuestCommand, payload.ToArray());
    }
    public static GameServerPacket CreateGiveupQuestReply(ushort questId)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write(questId);
        return new GameServerPacket(GameProtocolEngine.CommandPacketType, GameProtocolEngine.GiveupQuestCommand, payload.ToArray());
    }
    public static GameServerPacket CreateSetQuestTriggerReply(
        ushort questId,
        uint trigger)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write(questId);
        writer.Write(trigger);
        return new GameServerPacket(GameProtocolEngine.CommandPacketType, GameProtocolEngine.SetQuestTriggerCommand, payload.ToArray());
    }
    public static GameServerPacket CreateFinishQuestReply(
        ushort questId,
        IReadOnlyList<GameQuestConsumedItem>? consumedItems = null,
        IReadOnlyList<GameQuestInsertedItem>? insertedItems = null,
        byte chainType = 0,
        byte growNumber = 0,
        IReadOnlyList<GameQuestGrowthSkillChange>? growthSkillChanges = null)
    {
        if (chainType is not 0 and not 1 and not 2)
        {
            throw new ArgumentOutOfRangeException(nameof(chainType));
        }

        var consumed = consumedItems ?? [];
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write(questId);
        writer.Write((byte)0); // completion type
        writer.Write(checked((byte)Math.Min(byte.MaxValue, consumed.Count)));
        foreach (var item in consumed.Take(byte.MaxValue))
        {
            writer.Write((byte)0); // inventory update type
            writer.Write(item.Slot);
            writer.Write(item.RemainingCount);
        }

        writer.Write(chainType);
        if (chainType == 0)
        {
            var inserted = insertedItems ?? [];
            writer.Write(checked((byte)Math.Min(byte.MaxValue, inserted.Count)));
            foreach (var item in inserted.Take(byte.MaxValue))
            {
                writer.Write(item.Slot);
                writer.Write(item.ItemId);
                writer.Write(item.CountOrSeed);
            }
        }
        else
        {
            writer.Write(growNumber);
            var changes = (growthSkillChanges ?? [])
                .Where(change => change.SkillClass < SkillPacketBuilders.GrowthSkillClassCount)
                .Take(byte.MaxValue)
                .ToArray();
            writer.Write(checked((byte)changes.Length));
            foreach (var change in changes)
            {
                // DF2008 CMD 36 reads each change as class, skill ID, then level.
                writer.Write(change.SkillClass);
                writer.Write(change.SkillId);
                writer.Write(change.Level);
            }
        }

        return new GameServerPacket(GameProtocolEngine.CommandPacketType, GameProtocolEngine.FinishQuestCommand, payload.ToArray());
    }
}
