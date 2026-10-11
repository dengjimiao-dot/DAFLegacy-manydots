namespace DAFLegacy.Server;

public enum DungeonBossEntranceConditionKind
{
    None,
    HuntMonster,
    ClearRooms
}

public sealed record DungeonBossEntranceMonsterRequirement(
    ushort MonsterId,
    int SpawnCount,
    int RequiredKills);

public sealed record DungeonBossEntranceCondition(
    DungeonBossEntranceConditionKind Kind,
    DungeonBossEntranceMonsterRequirement[] Monsters,
    DungeonRoomPosition[] Rooms)
{
    public static DungeonBossEntranceCondition Unrestricted { get; } =
        new(DungeonBossEntranceConditionKind.None, [], []);
}
