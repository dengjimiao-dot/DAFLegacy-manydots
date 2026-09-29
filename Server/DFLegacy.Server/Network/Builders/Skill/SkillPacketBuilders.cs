using System.Buffers.Binary;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// Skill packet builders (07 §4.8.3 三层镜像: Builders/Skill).
/// Moved verbatim from the R7 engine split; byte output unchanged.
/// </summary>
internal static class SkillPacketBuilders
{
    internal const byte GrowthSkillClassCount = 5;
    public static GameServerPacket CreateIncreaseStatusReply(
        ushort sourceSlot,
        byte statusType,
        int amount,
        ushort firstAuxiliaryValue = 0,
        ushort secondAuxiliaryValue = 0)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write(sourceSlot);
        writer.Write(statusType);
        writer.Write(amount);
        writer.Write(firstAuxiliaryValue);
        writer.Write(secondAuxiliaryValue);
        return new GameServerPacket(
            GameProtocolConstants.CommandPacketType,
            (byte)CmdPacketType.INCREASE_STATUS,
            payload.ToArray());
    }
    public static GameServerPacket CreateChangeSkillSlotReply(
        byte sourceSlot,
        byte destinationSlot) =>
        new(
            GameProtocolConstants.CommandPacketType,
            (byte)CmdPacketType.CHANGE_SKILLSLOT,
            [1, sourceSlot, destinationSlot]);
    public static GameServerPacket CreateBuySkillReply(
        ushort remainingSkillPoints,
        byte slot,
        byte skillId,
        byte level)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write(remainingSkillPoints);
        writer.Write(slot);
        writer.Write(skillId);
        writer.Write(level);
        return new GameServerPacket(GameProtocolConstants.CommandPacketType, (byte)CmdPacketType.BUY_SKILL, payload.ToArray());
    }
    public static GameServerPacket CreateSkillInfo(
        ushort skillPoints,
        IReadOnlyList<GameSkillEntry> skills)
    {
        if (skills.Count > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(skills));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(skillPoints);
        writer.Write((byte)skills.Count);
        for (var index = 0; index < skills.Count; index++)
        {
            var skill = skills[index];
            // DNF 1.0.1.9 protocol 19 records are slot/id/level triples.
            // When no stable UI slot is persisted yet, assign compact slots in
            // packet order. The learned-skill list in USERINFO remains id/level.
            writer.Write(skill.Slot ?? checked((byte)index));
            writer.Write(skill.SkillId);
            writer.Write(skill.Level);
        }

        writer.Write((byte)0); // no per-skill auxiliary values
        return new GameServerPacket(
            GameProtocolConstants.NotificationPacketType,
            (byte)NotiPacketType.SKILLINFO,
            payload.ToArray());
    }
}
