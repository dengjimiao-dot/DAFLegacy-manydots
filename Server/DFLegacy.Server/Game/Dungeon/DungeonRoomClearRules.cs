using DFLegacy.Protocol;

namespace DFLegacy.Server;

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

        var bossIds = room.Monsters
            .Where(monster => monster.Type == GameDungeonMonsterTypes.Boss)
            .Select(monster => monster.UniqueId)
            .ToArray();
        if (bossIds.Length == 0)
        {
            return aliveMonsterIds.Count == 0
                ? new DungeonRoomClearDecision(
                    DungeonRoomClearReason.AllMonstersDefeated,
                    [])
                : DungeonRoomClearDecision.None;
        }

        if (defeatedMonster.Type == GameDungeonMonsterTypes.Boss
            && !bossIds.Any(aliveMonsterIds.Contains))
        {
            return new DungeonRoomClearDecision(
                DungeonRoomClearReason.LastBossDefeated,
                aliveMonsterIds.OrderBy(id => id).ToArray());
        }

        if (aliveMonsterIds.Count == 0)
        {
            return new DungeonRoomClearDecision(
                DungeonRoomClearReason.AllMonstersDefeated,
                []);
        }

        return DungeonRoomClearDecision.None;
    }
}
