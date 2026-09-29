using System.Globalization;
using System.Text.RegularExpressions;

namespace DFLegacy.Server;

public sealed class ResealCatalog
{
    private const string ScriptPath = "etc/reseal.etc";

    private static readonly Regex IntegerPattern = new(
        @"[+-]?\d+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ScriptFileSystem _scripts;
    private readonly ILogger<ResealCatalog> _logger;
    private readonly Lazy<CatalogState> _state;

    public ResealCatalog(
        ScriptFileSystem scripts,
        ILogger<ResealCatalog> logger)
    {
        _scripts = scripts;
        _logger = logger;
        _state = new Lazy<CatalogState>(
            Load,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public bool IsAvailable => _state.Value.IsAvailable;

    public int GradeBandCount => _state.Value.GradeBands.Count;

    public int RarityCount => _state.Value.RarityWeights.Count;

    public void Initialize() => _ = _state.Value;

    public bool TryGetCost(
        ItemDefinition equipment,
        byte currentResealCount,
        out uint cost)
    {
        ArgumentNullException.ThrowIfNull(equipment);
        cost = 0;
        var state = _state.Value;
        if (!state.IsAvailable
            || equipment.ScriptKind != ItemScriptKind.Equipment
            || equipment.InventoryCategory != ItemInventoryCategory.Equipment
            || equipment.Grade is not int grade
            || grade is < 1 or > 99
            || equipment.Rarity is not int rarity
            || rarity < 0
            || rarity >= state.RarityWeights.Count
            || rarity >= state.ExtendedRarityWeights.Count
            || currentResealCount >= CharacterItemSealing.MaximumResealCount)
        {
            return false;
        }

        var gradeBandIndex = Math.Min(
            (grade - 1) / 10,
            state.GradeBands.Count - 1);
        var calculated = checked(
            (long)state.GradeBands[gradeBandIndex]
                * state.RarityWeights[rarity]
            + (long)state.ExtendedRarityWeights[rarity]
                * currentResealCount);
        if (calculated <= 0 || calculated > uint.MaxValue)
        {
            return false;
        }

        cost = checked((uint)calculated);
        return true;
    }

    private CatalogState Load()
    {
        if (!_scripts.FileExists(ScriptPath))
        {
            _logger.LogWarning(
                "Reseal script {ScriptPath} was not found in {Source}; equipment resealing is disabled.",
                ScriptPath,
                _scripts.SourceDescription);
            return CatalogState.Empty;
        }

        var sections = ReadSections(_scripts.ReadLines(ScriptPath));
        var gradeBands = ReadPositiveValues(sections, "grade");
        var rarityWeights = ReadPositiveValues(sections, "rarity weight");
        var extendedRarityWeights = ReadNonNegativeValues(
            sections,
            "extended rarity weight");
        if (gradeBands.Count == 0
            || rarityWeights.Count == 0
            || rarityWeights.Count != extendedRarityWeights.Count)
        {
            throw new InvalidDataException(
                $"{ScriptPath} does not contain matching grade and rarity tables.");
        }

        _logger.LogInformation(
            "Cached DFLegacy reseal rules from {Source}: gradeBands={GradeBandCount}, rarityRows={RarityCount}.",
            _scripts.SourceDescription,
            gradeBands.Count,
            rarityWeights.Count);
        return new CatalogState(
            gradeBands,
            rarityWeights,
            extendedRarityWeights);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ReadSections(
        IEnumerable<string> sourceLines)
    {
        var sections = new Dictionary<string, List<string>>(
            StringComparer.OrdinalIgnoreCase);
        string? currentSection = null;
        foreach (var sourceLine in sourceLines)
        {
            var line = sourceLine.Split("//", 2, StringSplitOptions.None)[0].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("[", StringComparison.Ordinal)
                && line.IndexOf(']') is int end
                && end > 1)
            {
                var tag = line[1..end].Trim();
                if (tag.StartsWith("/", StringComparison.Ordinal))
                {
                    currentSection = null;
                    continue;
                }

                currentSection = tag;
                if (!sections.ContainsKey(tag))
                {
                    sections[tag] = [];
                }

                var remainder = line[(end + 1)..].Trim();
                if (remainder.Length != 0)
                {
                    sections[tag].Add(remainder);
                }

                continue;
            }

            if (currentSection is not null)
            {
                sections[currentSection].Add(line);
            }
        }

        return sections.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value.AsReadOnly(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<int> ReadPositiveValues(
        IReadOnlyDictionary<string, IReadOnlyList<string>> sections,
        string section) =>
        ReadValues(sections, section, allowZero: false);

    private static IReadOnlyList<int> ReadNonNegativeValues(
        IReadOnlyDictionary<string, IReadOnlyList<string>> sections,
        string section) =>
        ReadValues(sections, section, allowZero: true);

    private static IReadOnlyList<int> ReadValues(
        IReadOnlyDictionary<string, IReadOnlyList<string>> sections,
        string section,
        bool allowZero)
    {
        if (!sections.TryGetValue(section, out var rows))
        {
            return [];
        }

        var values = new List<int>();
        foreach (var row in rows)
        {
            var match = IntegerPattern.Match(row);
            if (!match.Success
                || !int.TryParse(
                    match.Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var value)
                || value < (allowZero ? 0 : 1))
            {
                throw new InvalidDataException(
                    $"{ScriptPath} has an invalid [{section}] row '{row}'.");
            }

            values.Add(value);
        }

        return values.AsReadOnly();
    }

    private sealed record CatalogState(
        IReadOnlyList<int> GradeBands,
        IReadOnlyList<int> RarityWeights,
        IReadOnlyList<int> ExtendedRarityWeights)
    {
        public static CatalogState Empty { get; } = new([], [], []);

        public bool IsAvailable => GradeBands.Count != 0
            && RarityWeights.Count != 0
            && RarityWeights.Count == ExtendedRarityWeights.Count;
    }
}
