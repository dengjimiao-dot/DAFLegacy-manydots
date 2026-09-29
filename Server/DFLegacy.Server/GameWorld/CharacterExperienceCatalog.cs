using System.Text.RegularExpressions;

namespace DFLegacy.Server;

public sealed record CharacterExperienceGrant(
    int PreviousLevel,
    uint PreviousExperience,
    int Level,
    uint Experience,
    uint GrantedExperience)
{
    public bool LeveledUp => Level > PreviousLevel;
}

public sealed class CharacterExperienceCatalog
{
    public const int MaximumLevel = 60;

    private static readonly Regex IntegerPattern = new(
        @"\d+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly ScriptFileSystem _scripts;
    private readonly ILogger<CharacterExperienceCatalog> _logger;
    private readonly Lazy<uint[]> _thresholds;

    public CharacterExperienceCatalog(
        ScriptFileSystem scripts,
        ILogger<CharacterExperienceCatalog> logger)
    {
        _scripts = scripts;
        _logger = logger;
        _thresholds = new Lazy<uint[]>(
            LoadThresholds,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public uint CalculateQuestExperience(CharacterRecord character, QuestDefinition quest)
    {
        if (quest.Repeatable || character.Level >= MaximumLevel)
        {
            return 0;
        }

        var questLevel = Math.Clamp(quest.MinimumLevel, 1, MaximumLevel - 1);
        var baseReward = Math.Max(1u, GetExperienceForLevel(questLevel) / 10u);
        var levelDifference = character.Level - questLevel;
        var percentage = levelDifference switch
        {
            <= 6 => 100u,
            <= 11 => 80u,
            _ => 30u
        };
        return Math.Max(1u, checked(baseReward * percentage / 100u));
    }

    public CharacterExperienceGrant Grant(CharacterRecord character, uint amount)
    {
        var previousLevel = Math.Clamp(character.Level, 1, MaximumLevel);
        var previousExperience = character.Experience;
        if (amount == 0 || previousLevel >= MaximumLevel)
        {
            return new CharacterExperienceGrant(
                previousLevel,
                previousExperience,
                previousLevel,
                previousExperience,
                0);
        }

        var experience = (uint)Math.Min(
            uint.MaxValue,
            (ulong)previousExperience + amount);
        var level = previousLevel;
        var thresholds = _thresholds.Value;
        while (level < MaximumLevel
               && level - 1 < thresholds.Length
               && experience >= thresholds[level - 1])
        {
            level++;
        }

        return new CharacterExperienceGrant(
            previousLevel,
            previousExperience,
            level,
            experience,
            amount);
    }

    public uint GetExperienceForLevel(int level)
    {
        var clampedLevel = Math.Clamp(level, 1, MaximumLevel - 1);
        var thresholds = _thresholds.Value;
        if (thresholds.Length < clampedLevel)
        {
            return 1;
        }

        var previousThreshold = clampedLevel == 1 ? 0u : thresholds[clampedLevel - 2];
        return Math.Max(1u, thresholds[clampedLevel - 1] - previousThreshold);
    }

    public uint GetMinimumCumulativeExperience(int level)
    {
        var clampedLevel = Math.Clamp(level, 1, MaximumLevel);
        if (clampedLevel <= 1)
        {
            return 0;
        }

        var thresholds = _thresholds.Value;
        var thresholdIndex = clampedLevel - 2;
        return thresholdIndex < thresholds.Length
            ? thresholds[thresholdIndex]
            : 0;
    }

    public uint NormalizeCumulativeExperience(int level, uint experience)
    {
        var clampedLevel = Math.Clamp(level, 1, MaximumLevel);
        var minimum = GetMinimumCumulativeExperience(clampedLevel);
        if (experience >= minimum || clampedLevel <= 1)
        {
            return experience;
        }

        // A high-level character created with the preceding level's threshold
        // should retain experience earned since that incorrect baseline.
        var precedingMinimum = GetMinimumCumulativeExperience(clampedLevel - 1);
        var carriedProgress = experience > precedingMinimum
            ? experience - precedingMinimum
            : 0;
        var normalized = (ulong)minimum + carriedProgress;
        if (clampedLevel < MaximumLevel)
        {
            var nextMinimum = GetMinimumCumulativeExperience(clampedLevel + 1);
            if (nextMinimum > minimum)
            {
                normalized = Math.Min(normalized, (ulong)nextMinimum - 1);
            }
        }

        return (uint)Math.Min(normalized, uint.MaxValue);
    }

    private uint[] LoadThresholds()
    {
        const string path = "character/exptable.tbl";
        if (!_scripts.FileExists(path))
        {
            _logger.LogWarning(
                "Character experience table was not found in {Source}.",
                _scripts.SourceDescription);
            return [];
        }

        var thresholds = _scripts.ReadLines(path)
            .Select(line => IntegerPattern.Match(line))
            .Where(match => match.Success)
            .Select(match => uint.TryParse(match.Value, out var threshold) ? threshold : 0)
            .Where(threshold => threshold > 0)
            .Take(MaximumLevel - 1)
            .ToArray();
        _logger.LogInformation(
            "Loaded {Count} DFLegacy character experience thresholds from {Path}.",
            thresholds.Length,
            _scripts.SourceDescription);
        return thresholds;
    }
}
