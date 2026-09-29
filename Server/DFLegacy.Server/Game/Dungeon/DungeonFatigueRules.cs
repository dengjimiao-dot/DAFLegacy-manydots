namespace DFLegacy.Server;

public static class DungeonFatigueRules
{
    public static bool CanEnterNewDungeon(
        ushort usedFatigue,
        ushort maximumFatigue) =>
        usedFatigue < maximumFatigue;
}
