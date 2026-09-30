using System.Text;
using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

/// <summary>
/// Town-presence builders (07 §4.7): the compact town-actor appearance
/// projection.
/// </summary>
public sealed partial class CharacterPacketBuilders
{
    internal GameServerPacket CreateTownAppearancePacket(
        CharacterRecord character,
        ushort userId,
        IEnumerable<CharacterItemRecord> equipment,
        IEnumerable<CharacterItemRecord> creatureInventory,
        bool hasBlackDiamond)
    {
        var equipmentSnapshot = equipment.ToArray();

        return CharactersPacketBuilders.CreateCurrentCharacterInfo(
            new GameCharacterSummary(
                0,
                EncodeCharacterName(character.Name),
                (byte)Math.Clamp(character.Job, byte.MinValue, byte.MaxValue),
                (byte)Math.Clamp(character.GrowType, 0, 15),
                (byte)Math.Clamp(character.Level, 1, byte.MaxValue),
                AwakeningType: (byte)Math.Clamp(
                    character.AwakeningType,
                    byte.MinValue,
                    byte.MaxValue),
                PvpGrade: pvpExperienceCatalog.GetGradeForPoints(
                    character.PvpPoints)),
            userId,
            equipmentSnapshot
                .Where(item => item.Slot <= EquippedTitleSlot)
                .Select(ToGameInventoryEntry)
                .ToArray(),
            CreateEquippedCreatureAppearance(creatureInventory),
            hasBlackDiamond);
    }
}
