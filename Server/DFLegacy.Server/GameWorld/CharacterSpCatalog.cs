using System.Text.RegularExpressions;

namespace DFLegacy.Server;

public sealed class CharacterSpCatalog
{
    private const string ScriptPath = "etc/sptable.etc";

    private static readonly Regex TableRowPattern = new(
        @"^\s*(\d+)\s+(\d+)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ScriptFileSystem _scripts;
    private readonly ILogger<CharacterSpCatalog> _logger;
    private readonly Lazy<uint[]> _rewards;

    public CharacterSpCatalog(
        ScriptFileSystem scripts,
        ILogger<CharacterSpCatalog> logger)
    {
        _scripts = scripts;
        _logger = logger;
        _rewards = new Lazy<uint[]>(
            LoadRewards,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public int Count => Math.Max(0, _rewards.Value.Length - 1);

    public void Initialize() => _ = _rewards.Value;

    public uint GetRewardForLevel(int level)
    {
        var rewards = _rewards.Value;
        return level > 0 && level < rewards.Length
            ? rewards[level]
            : 0;
    }

    public int CalculateLevelUpReward(int previousLevel, int level)
    {
        if (level <= previousLevel)
        {
            return 0;
        }

        long total = 0;
        for (var enteredLevel = previousLevel + 1; enteredLevel <= level; enteredLevel++)
        {
            total = checked(total + GetRewardForLevel(enteredLevel));
        }

        return checked((int)total);
    }

    public int CalculateTotalReward(int level) =>
        CalculateLevelUpReward(0, level);

    private uint[] LoadRewards()
    {
        if (!_scripts.FileExists(ScriptPath))
        {
            _logger.LogWarning(
                "Character SP table {ScriptPath} was not found in {Source}; level-ups will grant no SP.",
                ScriptPath,
                _scripts.SourceDescription);
            return [0];
        }

        var rewardsByLevel = new SortedDictionary<int, uint>();
        var inTable = false;
        foreach (var rawLine in _scripts.ReadLines(ScriptPath))
        {
            var line = StripComment(rawLine).Trim();
            if (line.Equals("[sp table]", StringComparison.OrdinalIgnoreCase))
            {
                inTable = true;
                continue;
            }

            if (line.Equals("[/sp table]", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (!inTable || line.Length == 0)
            {
                continue;
            }

            var match = TableRowPattern.Match(line);
            if (!match.Success
                || !int.TryParse(match.Groups[1].Value, out var level)
                || level <= 0
                || !uint.TryParse(match.Groups[2].Value, out var reward))
            {
                throw new InvalidDataException(
                    $"{ScriptPath} contains an invalid SP row: '{rawLine}'.");
            }

            if (!rewardsByLevel.TryAdd(level, reward))
            {
                throw new InvalidDataException(
                    $"{ScriptPath} contains duplicate level {level}.");
            }
        }

        if (rewardsByLevel.Count == 0)
        {
            throw new InvalidDataException(
                $"{ScriptPath} does not contain any [sp table] rows.");
        }

        var maximumLevel = rewardsByLevel.Keys.Max();
        var rewards = new uint[maximumLevel + 1];
        for (var level = 1; level <= maximumLevel; level++)
        {
            if (!rewardsByLevel.TryGetValue(level, out rewards[level]))
            {
                throw new InvalidDataException(
                    $"{ScriptPath} is missing level {level}.");
            }
        }

        _logger.LogInformation(
            "Cached {Count} DFLegacy level-up SP rewards from {Source}; supported levels=1-{MaximumLevel}.",
            maximumLevel,
            _scripts.SourceDescription,
            maximumLevel);
        return rewards;
    }

    private static string StripComment(string value)
    {
        var commentIndex = value.IndexOf("//", StringComparison.Ordinal);
        return commentIndex >= 0 ? value[..commentIndex] : value;
    }
}
