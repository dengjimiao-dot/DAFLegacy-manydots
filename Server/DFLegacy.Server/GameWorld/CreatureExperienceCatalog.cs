using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace DFLegacy.Server;

public sealed record CreatureExperienceGrant(
    byte PreviousLevel,
    uint PreviousExperience,
    byte Level,
    uint Experience,
    uint BaseExperience,
    uint BonusExperience,
    uint GrantedExperience)
{
    public bool LeveledUp => Level > PreviousLevel;
}

public sealed class CreatureExperienceCatalog
{
    private const string ExperienceTablePath = "creature/exptable.tbl";
    private const string CreatureListPath = "creature/creature.lst";

    private static readonly Regex TableValuePattern = new(
        @"^\s*(?<value>\d+)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ListEntryPattern = new(
        @"^\s*(?<id>\d+)\s+`(?<path>[^`]+)`",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MaximumLevelPattern = new(
        @"^\s*\[max level\]\s+(?<value>\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant |
        RegexOptions.Multiline | RegexOptions.IgnoreCase);

    private readonly ScriptFileSystem _scripts;
    private readonly ILogger<CreatureExperienceCatalog> _logger;
    private readonly Lazy<CatalogState> _state;

    public CreatureExperienceCatalog(
        ScriptFileSystem scripts,
        ILogger<CreatureExperienceCatalog> logger)
    {
        _scripts = scripts;
        _logger = logger;
        _state = new Lazy<CatalogState>(
            Load,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public int ThresholdCount => _state.Value.Thresholds.Length;

    public int SpeciesCount => _state.Value.MaximumLevels.Count;

    public void Initialize() => _ = _state.Value;

    public byte GetLevel(uint cumulativeExperience, int? creatureSpecies)
    {
        var state = _state.Value;
        var maximumLevel = GetMaximumLevel(state, creatureSpecies);
        var level = 1;
        foreach (var threshold in state.Thresholds)
        {
            if (level >= maximumLevel || cumulativeExperience < threshold)
            {
                break;
            }

            level++;
        }

        return checked((byte)level);
    }

    public int GetMaximumLevel(int? creatureSpecies) =>
        GetMaximumLevel(_state.Value, creatureSpecies);

    public CreatureExperienceGrant Grant(
        uint currentExperience,
        byte currentLevel,
        int? creatureSpecies,
        uint baseExperience,
        decimal bonusRatePercent)
    {
        var maximumLevel = GetMaximumLevel(creatureSpecies);
        var previousLevel = checked((byte)Math.Clamp(
            (int)currentLevel,
            1,
            maximumLevel));
        if (baseExperience == 0 || previousLevel >= maximumLevel)
        {
            return new CreatureExperienceGrant(
                previousLevel,
                currentExperience,
                previousLevel,
                currentExperience,
                0,
                0,
                0);
        }

        var signedBonus = decimal.Truncate(
            baseExperience * bonusRatePercent / 100m);
        var requestedExperience = Math.Clamp(
            (decimal)baseExperience + signedBonus,
            0m,
            uint.MaxValue);
        var experience = checked((uint)Math.Min(
            uint.MaxValue,
            (ulong)currentExperience + (uint)requestedExperience));
        var grantedExperience = experience - currentExperience;
        var effectiveBaseExperience = Math.Min(baseExperience, grantedExperience);
        var bonusExperience = grantedExperience - effectiveBaseExperience;
        var calculatedLevel = GetLevel(experience, creatureSpecies);
        var level = Math.Max(previousLevel, calculatedLevel);

        return new CreatureExperienceGrant(
            previousLevel,
            currentExperience,
            level,
            experience,
            effectiveBaseExperience,
            bonusExperience,
            grantedExperience);
    }

    private CatalogState Load()
    {
        // R9：与其它 catalog 一致——缺数据不终止进程；记录 Error 并禁用
        // 生物经验（空阈值表使等级停留在 1、不再发放经验）。
        if (!_scripts.FileExists(ExperienceTablePath))
        {
            _logger.LogError(
                "Creature experience table {ExperienceTablePath} was not found in {Source}; creature experience is disabled.",
                ExperienceTablePath,
                _scripts.SourceDescription);
            return new CatalogState(
                Array.Empty<uint>(),
                new ReadOnlyDictionary<int, int>(new Dictionary<int, int>()));
        }

        var thresholds = LoadThresholds();
        var maximumLevels = LoadMaximumLevels(
            Math.Min(byte.MaxValue, thresholds.Length + 1));
        _logger.LogInformation(
            "Cached {ThresholdCount} DFLegacy Creature experience thresholds and {SpeciesCount} species maximum levels from {Source}.",
            thresholds.Length,
            maximumLevels.Count,
            _scripts.SourceDescription);
        return new CatalogState(
            thresholds,
            new ReadOnlyDictionary<int, int>(maximumLevels));
    }

    private uint[] LoadThresholds()
    {
        if (!_scripts.FileExists(ExperienceTablePath))
        {
            throw new FileNotFoundException(
                $"Creature experience table '{ExperienceTablePath}' was not found in {_scripts.SourceDescription}.",
                ExperienceTablePath);
        }

        var thresholds = new List<uint>();
        foreach (var rawLine in _scripts.ReadLines(ExperienceTablePath))
        {
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var match = TableValuePattern.Match(line);
            if (!match.Success
                || !uint.TryParse(match.Groups["value"].Value, out var threshold)
                || threshold == 0
                || thresholds.Count > 0 && threshold <= thresholds[^1])
            {
                throw new InvalidDataException(
                    $"{ExperienceTablePath} contains an invalid or non-increasing threshold: '{rawLine}'.");
            }

            thresholds.Add(threshold);
        }

        if (thresholds.Count == 0)
        {
            throw new InvalidDataException(
                $"{ExperienceTablePath} does not contain any experience thresholds.");
        }

        return thresholds.ToArray();
    }

    private Dictionary<int, int> LoadMaximumLevels(int tableMaximumLevel)
    {
        var maximumLevels = new Dictionary<int, int>();
        if (!_scripts.FileExists(CreatureListPath))
        {
            _logger.LogWarning(
                "Creature list {CreatureListPath} was not found; Creature levels will use the experience-table maximum {MaximumLevel}.",
                CreatureListPath,
                tableMaximumLevel);
            return maximumLevels;
        }

        foreach (var rawLine in _scripts.ReadLines(CreatureListPath))
        {
            var match = ListEntryPattern.Match(StripComment(rawLine));
            if (!match.Success)
            {
                continue;
            }

            if (!int.TryParse(match.Groups["id"].Value, out var species)
                || species <= 0)
            {
                throw new InvalidDataException(
                    $"{CreatureListPath} contains an invalid species id: '{rawLine}'.");
            }

            if (maximumLevels.ContainsKey(species))
            {
                throw new InvalidDataException(
                    $"{CreatureListPath} contains duplicate species id {species}.");
            }

            var relativePath = match.Groups["path"].Value
                .Replace('\\', '/')
                .TrimStart('/');
            var scriptPath = relativePath.StartsWith(
                "creature/",
                StringComparison.OrdinalIgnoreCase)
                ? relativePath
                : $"creature/{relativePath}";
            if (!_scripts.FileExists(scriptPath))
            {
                _logger.LogWarning(
                    "Creature species {Species} script {ScriptPath} was not found; using the experience-table maximum {MaximumLevel}.",
                    species,
                    scriptPath,
                    tableMaximumLevel);
                maximumLevels.Add(species, tableMaximumLevel);
                continue;
            }

            var levelMatch = MaximumLevelPattern.Match(
                _scripts.ReadAllTextUncached(scriptPath));
            if (!levelMatch.Success
                || !int.TryParse(levelMatch.Groups["value"].Value, out var maximumLevel)
                || maximumLevel <= 0)
            {
                _logger.LogWarning(
                    "Creature species {Species} script {ScriptPath} has no valid [max level]; using the experience-table maximum {MaximumLevel}.",
                    species,
                    scriptPath,
                    tableMaximumLevel);
                maximumLevels.Add(species, tableMaximumLevel);
                continue;
            }

            maximumLevels.Add(
                species,
                Math.Clamp(maximumLevel, 1, Math.Min(byte.MaxValue, tableMaximumLevel)));
        }

        return maximumLevels;
    }

    private static int GetMaximumLevel(
        CatalogState state,
        int? creatureSpecies) =>
        creatureSpecies.HasValue
        && state.MaximumLevels.TryGetValue(creatureSpecies.Value, out var maximumLevel)
            ? maximumLevel
            : Math.Min(byte.MaxValue, state.Thresholds.Length + 1);

    private static string StripComment(string value)
    {
        var commentIndex = value.IndexOf("//", StringComparison.Ordinal);
        return commentIndex >= 0 ? value[..commentIndex] : value;
    }

    private sealed record CatalogState(
        uint[] Thresholds,
        IReadOnlyDictionary<int, int> MaximumLevels);
}
