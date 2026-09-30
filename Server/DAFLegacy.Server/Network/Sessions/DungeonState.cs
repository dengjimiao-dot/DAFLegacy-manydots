using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

/// <summary>
/// Dungeon run state: selection countdown, monster session, clear-card and
/// result bookkeeping (07 §4.3). Field defaults mirror the former closure
/// initializers one to one.
/// </summary>
public sealed class DungeonState
{
    public ushort CurrentDungeonId;

    public bool InSelection;

    public WorldMapDefinition? SelectionWorldMap;

    public DateTimeOffset? SelectionDeadline;

    public byte RoomX;

    public byte RoomY;

    public byte Difficulty;

    public DungeonLayout? Layout;

    public DungeonRoomDefinition? Room;

    public HashSet<ushort> AliveMonsters = [];

    public HashSet<ushort> ReportedBossDeaths = [];

    public HashSet<ushort> BossCascadeDeaths = [];

    public HashSet<byte> DestroyedPassiveObjects = [];

    public DungeonGroundItemState GroundItems = new();

    /// <summary>Assigned by the pump; the run recorder needs its catalog.</summary>
    public DungeonRunState Run = null!;

    public bool TutorialActive;

    public bool CharacterAlive = true;

    public long DeathGeneration;

    public (long Generation, DateTimeOffset Deadline)? DeathTimeout;

    public bool ClearEnabled;

    public bool ResultSent;

    public bool CardLayoutSent;

    public bool CardRightsSent;

    public DateTimeOffset? CardSelectionDeadline;

    public DungeonClearRewardRoll? ClearReward;

    public byte? FreeCardIndex;

    public byte? GoldCardIndex;

    public uint MonsterExperienceGained;

    public GameDungeonClearExperienceBreakdown? ClearExperience;

    public uint ConsumedFatigue;

    public long? CreatureStomachCheckpointSeconds;

    public bool LeveledUp;

    public int NormalMonsterKills;

    public int ChampionMonsterKills;

    public int BossMonsterKills;

    // 13338 displays at most eleven Champion/SuperChampion reward
    // animations before the clear-card screen. This ordinal is per
    // dungeon run and is independent from the kill-count weighting.
    public int ChampionRewardOrdinal;

    /// <summary>Return-to-town anchor snapshots of the pre-dungeon position.</summary>
    public byte ReturnTownId;

    public byte ReturnAreaId;

    public short ReturnX;

    public short ReturnY;

    public byte ReturnDirection;
}
