using System.Buffers.Binary;

namespace DAFLegacy.Protocol;

public sealed record GameCharacterSummary(
    ushort Slot,
    byte[] NameBytes,
    byte Job,
    byte GrowType,
    byte Level,
    IReadOnlyList<GameInventoryEntry>? AppearanceItems = null,
    uint CharacterNo = 0,
    byte AwakeningType = 0,
    byte PvpGrade = 0);

public sealed record GameInventoryEntry(
    ushort Slot,
    ushort ItemId,
    uint CountOrValue,
    byte State = 0,
    ushort Durability = 0,
    byte SealState = 0);
public sealed record GameCreatureInfoEntry(
    uint Uid,
    byte Stomach,
    uint Experience,
    byte Level,
    byte[] NameBytes,
    bool NoCharge = false);
public sealed record GameCreatureAppearanceEntry(
    ushort ItemId,
    byte[] NameBytes,
    byte State = 1);
public sealed record GameItemConsumptionEntry(
    ushort Slot,
    uint ConsumedCount);
public sealed record GameDisjointRewardEntry(
    ushort DestinationSlot,
    ushort ItemId,
    uint Count);
public readonly record struct GameCeraShopReward(ushort ItemId, uint Count);

public sealed record GameSkillEntry(byte SkillId, byte Level, byte? Slot = null);
public sealed record GameQuestEntry(ushort QuestId, uint Trigger = 0);
public sealed record GameQuestConsumedItem(ushort Slot, uint RemainingCount);
public sealed record GameQuestInsertedItem(ushort Slot, ushort ItemId, uint CountOrSeed);
public sealed record GameQuestGrowthSkillChange(byte SkillId, byte SkillClass, byte Level);
public sealed record GamePremiumServiceEntry(byte ServiceType, uint RemainSeconds);
public sealed record GameCharacterCombatStats(
    uint MaximumHp,
    uint MaximumMp,
    short PhysicalAttack,
    short PhysicalDefense,
    short MagicalAttack,
    short MagicalDefense,
    short FireResistance,
    short WaterResistance,
    short DarkResistance,
    short LightResistance,
    uint InventoryLimit,
    ushort HpRegeneration,
    ushort MpRegeneration,
    ushort MovementSpeed,
    ushort AttackSpeed,
    ushort CastSpeed,
    ushort HitRecovery,
    ushort JumpPower,
    uint Weight);
public sealed record GameDungeonPermissionEntry(ushort DungeonId, byte ClearState);
public sealed record GameTownUser(
    ushort UserId,
    short X,
    short Y,
    byte Direction);
public sealed record GameDungeonMonster(
    byte MapListIndex,
    ushort UniqueId,
    ushort MonsterIndex,
    byte Level,
    byte Type = 0,
    byte IsBoxMonster = 0,
    byte BoxIndex = 0,
    bool IsHellHidden = false,
    ushort X = 0,
    ushort Y = 0);

public static class GameDungeonMonsterTypes
{
    public const byte Normal = 0;
    public const byte Champion = 1;
    public const byte SuperChampion = 2;
    public const byte Boss = 3;
}

public sealed record GameDungeonClearExperienceBreakdown
{
    public static GameDungeonClearExperienceBreakdown Empty { get; } = new();

    public uint BaseExperience { get; init; }

    public uint RankBonus { get; init; }

    public uint PartyBonus { get; init; }

    public uint AvatarBonus { get; init; }

    public uint EventBonus { get; init; }

    public uint BlackDiamondBonus { get; init; }

    public uint ChannelBonus { get; init; }

    public uint MentorBonus { get; init; }

    public uint CreatureBonus { get; init; }

    public uint TotalExperience => (uint)Math.Min(
        uint.MaxValue,
        (ulong)BaseExperience
        + RankBonus
        + PartyBonus
        + AvatarBonus
        + EventBonus
        + BlackDiamondBonus
        + ChannelBonus
        + MentorBonus
        + CreatureBonus);
}

public sealed record GameDungeonDrop(
    ushort GroundId,
    ushort ItemId,
    uint AddInfo,
    ushort Durability = 0,
    ushort OwnerUserId = 0,
    byte ItemAttr = 0);
public sealed record GamePassiveObjectDrop(
    byte AssociationSlot,
    ushort GroundId,
    ushort ItemId,
    uint AddInfo,
    ushort OwnerUserId = 0);
public readonly record struct GameDieMonsterCommand(
    ushort EntityId,
    bool IsPassiveObject);

public readonly record struct GameUseCoinCommand(ushort TargetUserId);
public readonly record struct GameDropItemCommand(
    short X,
    short Y,
    byte ItemSpace,
    ushort Slot,
    uint Count);
public readonly record struct GameBossDieCheckCommand(
    ushort ParticipantId,
    ushort BossId,
    uint SkillChecksum);
public readonly record struct GameUpgradeItemCommand(
    ushort TargetSlot,
    ushort TargetItemId,
    ushort MaterialSlot);
public readonly record struct GameUpgradeItemDestroyReward(
    ushort Slot,
    ushort ItemId,
    uint Count);
public readonly record struct GameResetItemAttributeCommand(
    ushort TargetSlot,
    ushort TargetItemId,
    ushort MaterialSlot);
public readonly record struct GameCompoundItemCommand(
    ushort Sequence,
    ushort SourceValue,
    bool SourceIsItemId,
    byte CraftCount);
public readonly record struct GameCompoundItemConsumption(
    byte ListType,
    ushort Slot,
    uint RemainingCount);
public readonly record struct GameCompoundItemResult(
    ushort Slot,
    ushort ItemId,
    uint CountOrValue,
    byte State,
    ushort Durability);
public readonly record struct GameCompoundAvatarCommand(
    ushort MaterialSlot,
    ushort FirstAvatarItemId,
    ushort SecondAvatarItemId,
    byte SelectedOption,
    byte FirstAvatarSlot,
    byte SecondAvatarSlot);
public readonly record struct GameAvatarCompoundConsumption(
    byte ListType,
    ushort Slot,
    uint RemainingCount);

public sealed record GameMailAttachment(
    ushort ItemId,
    uint CountOrValue,
    byte State = 0,
    ushort Durability = 0,
    byte SealState = 0);

public sealed record GameMailEntry(
    uint MailId,
    byte[] SenderBytes,
    byte[] TextBytes,
    uint Gold,
    GameMailAttachment? Attachment,
    uint SentAt,
    uint ExpiresAt,
    ushort State = 1);

public enum GameMessageType : byte
{
    Type00DualRouteBrown = 0,
    Type01LightGreen = 1,
    Type02LightCyan = 2,
    Type03White = 3,
    Type04Brown = 4,
    Type05White = 5,
    Type06Magenta = 6,
    Type07LightGreen = 7,
    Type08Orange = 8,
    Type09White = 9,
    Type10White = 10,
    Type11LightCyan = 11,
    Type12Yellow = 12,
    Type13SpeakerYellow = 13,
    Type14SpeakerYellow = 14,
    Type15SpeakerYellow = 15,
    Type16Brown = 16,
    Type17ClientFormattedNotice = 17,
    Type18DirectOutput = 18
}

/// <summary>
/// Handles the initial command exchange used after the client selects a channel.
/// Game/channel packets share the ten-byte transport header with entrance packets,
/// but the body begins with a two-byte command sequence number. Command payload
/// serialization starts after that sequence number.
/// </summary>
