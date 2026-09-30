using System.Text;
using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

/// <summary>
/// Login initialization burst builder (07 §6·R1, the
/// InitPacketBuilderRegistry direction): the ordered packet sequence the
/// character-select flow streams after the character details.
/// </summary>
public sealed partial class CharacterPacketBuilders
{
    internal IEnumerable<GameServerPacket> CreateTownInitializationPackets(
        int skillPoints,
        IReadOnlyList<GameSkillEntry> panelSkills,
        int gold,
        uint victoryPoints,
        IEnumerable<CharacterItemRecord> inventory,
        IEnumerable<CharacterItemRecord> equipment,
        IEnumerable<CharacterItemRecord> avatarInventory,
        IEnumerable<CharacterItemRecord> warehouse,
        ushort warehouseCapacity,
        IEnumerable<CharacterItemRecord> creatureInventory,
        IReadOnlyList<ushort> acceptableQuestIds,
        IReadOnlyList<GameDungeonPermissionEntry> dungeonPermissions)
    {
        // Dungeon permissions are u16-counted (u16 id, u8 state) records, and
        // the acceptable-quest list is u8-counted. The stamina notification is
        // sent later, after AREA_USERS has created the local town actor.
        yield return DungeonPacketBuilders.CreateDungeonPermissions(dungeonPermissions);

        yield return CreateMainInventoryPacket(gold, victoryPoints, inventory);
        yield return CreateAvatarInventoryPacket(avatarInventory);
        yield return CreateWarehousePacket(warehouse, warehouseCapacity);
        foreach (var creaturePacket in CreateCreatureInventoryPackets(creatureInventory))
        {
            yield return creaturePacket;
        }
        yield return SkillPacketBuilders.CreateSkillInfo(
            (ushort)Math.Clamp(skillPoints, ushort.MinValue, ushort.MaxValue),
            panelSkills);
        yield return QuestPacketBuilders.CreateAcceptableQuestList(acceptableQuestIds);
    }
}
