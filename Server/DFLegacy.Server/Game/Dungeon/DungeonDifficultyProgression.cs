namespace DFLegacy.Server;

public sealed record CharacterDungeonProgressRecord(
    ushort DungeonId,
    byte MaximumDifficulty);

public static class DungeonDifficultyProgression
{
    public const byte HighestDifficulty = 3;

    public static bool TryGetNextDifficulty(
        byte clearedDifficulty,
        byte resultCode,
        out byte nextDifficulty)
    {
        nextDifficulty = clearedDifficulty;
        switch (clearedDifficulty)
        {
            case 0:
                nextDifficulty = 1;
                return true;
            case 1 when resultCode >= 55:
                nextDifficulty = 2;
                return true;
            case 2 when resultCode >= 85:
                nextDifficulty = 3;
                return true;
            default:
                return false;
        }
    }

    public static byte GetMaximumDifficulty(
        IEnumerable<CharacterDungeonProgressRecord>? progress,
        ushort dungeonId) =>
        progress?
            .Where(entry => entry.DungeonId == dungeonId)
            .Select(entry => NormalizeDifficulty(entry.MaximumDifficulty))
            .DefaultIfEmpty()
            .Max()
        ?? 0;

    public static CharacterDungeonProgressRecord[] Normalize(
        IEnumerable<CharacterDungeonProgressRecord>? progress) =>
        (progress ?? [])
            .Where(entry => entry.DungeonId != 0)
            .GroupBy(entry => entry.DungeonId)
            .Select(group => new CharacterDungeonProgressRecord(
                group.Key,
                group.Max(entry => NormalizeDifficulty(entry.MaximumDifficulty))))
            .OrderBy(entry => entry.DungeonId)
            .ToArray();

    public static byte NormalizeDifficulty(int difficulty) =>
        checked((byte)Math.Clamp(difficulty, 0, HighestDifficulty));
}
