using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

public enum DungeonRoomClearReason : byte
{
    None,
    AllMonstersDefeated,
    LastBossDefeated
}

public readonly record struct DungeonRoomClearDecision(
    DungeonRoomClearReason Reason,
    ushort[] ForcedMonsterIds)
{
    public bool ShouldEnableClear =>
        Reason != DungeonRoomClearReason.None
        && ForcedMonsterIds.Length == 0;

    public static DungeonRoomClearDecision None { get; } = new(
        DungeonRoomClearReason.None,
        []);
}

public static class DungeonRoomClearRules
{
    public static DungeonRoomClearDecision Evaluate(
        DungeonRoomDefinition room,
        GameDungeonMonster defeatedMonster,
        IReadOnlySet<ushort> aliveMonsterIds)
    {
        if (room.MapType != DungeonMapType.Boss)
        {
            return DungeonRoomClearDecision.None;
        }

        // 13339 CBattle_Field::check_grid_clear (0x0830A0E8) blocks only
        // surviving team-100 actors. Team 0..99 allies are not clear targets.
        var enemyMonsters = room.Monsters.Where(monster => monster.IsEnemy).ToArray();
        var aliveEnemyIds = enemyMonsters
            .Where(monster => aliveMonsterIds.Contains(monster.UniqueId))
            .Select(monster => monster.UniqueId)
            .ToHashSet();
        var bossIds = enemyMonsters
            .Where(monster => monster.Type == GameDungeonMonsterTypes.Boss)
            .Select(monster => monster.UniqueId)
            .ToArray();
        if (bossIds.Length == 0)
        {
            return aliveEnemyIds.Count == 0
                ? new DungeonRoomClearDecision(
                    DungeonRoomClearReason.AllMonstersDefeated,
                    [])
                : DungeonRoomClearDecision.None;
        }

        if (defeatedMonster.IsEnemy && defeatedMonster.Type == GameDungeonMonsterTypes.Boss
            && !bossIds.Any(aliveEnemyIds.Contains))
        {
            return new DungeonRoomClearDecision(
                DungeonRoomClearReason.LastBossDefeated,
                aliveEnemyIds.OrderBy(id => id).ToArray());
        }

        if (aliveEnemyIds.Count == 0)
        {
            return new DungeonRoomClearDecision(
                DungeonRoomClearReason.AllMonstersDefeated,
                []);
        }

        return DungeonRoomClearDecision.None;
    }
}
