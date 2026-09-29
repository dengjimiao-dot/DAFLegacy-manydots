using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

public sealed record MonsterChampionDropEntry(
    ushort ItemId,
    int Probability);

public sealed record MonsterChampionDropDefinition(
    ushort MonsterIndex,
    string SourcePath,
    MonsterChampionDropEntry[] ItemDrops,
    MonsterChampionDropEntry[] CommonDrops,
    MonsterChampionDropEntry[] SuperDrops);

public sealed class MonsterChampionDropCatalog
{
    private const string MonsterListPath = "monster/monster.lst";
    private const int ProbabilityDenominator = 10_000;
    private const string ItemDropTag = "item";
    private const string CommonDropTag = "common champion drop item";
    private const string SuperDropTag = "super champion drop item";

    private static readonly Encoding ScriptEncoding = CreateScriptEncoding();
    private static readonly Regex ListEntryPattern = new(
        @"(?m)^\s*(?<id>\d+)\s+`(?<path>[^`]+)`",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DropEntryPattern = new(
        @"^\s*(?<item>\d+)\s+(?<probability>\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ServerOptions _options;
    private readonly Lazy<CatalogState> _state;

    public MonsterChampionDropCatalog(
        ServerOptions options,
        ScriptFileSystem scripts,
        ILogger<MonsterChampionDropCatalog> logger)
    {
        _options = options;
        _state = new Lazy<CatalogState>(
            () => Load(scripts, logger),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public int DefinitionCount => _state.Value.Definitions.Count;

    public int ItemDropEntryCount => _state.Value.Definitions.Values
        .Sum(definition => definition.ItemDrops.Length);

    public int CommonDropEntryCount => _state.Value.Definitions.Values
        .Sum(definition => definition.CommonDrops.Length);

    public int SuperDropEntryCount => _state.Value.Definitions.Values
        .Sum(definition => definition.SuperDrops.Length);

    public void Initialize() => _ = _state.Value;

    public bool TryGetDefinition(
        ushort monsterIndex,
        out MonsterChampionDropDefinition definition) =>
        _state.Value.Definitions.TryGetValue(monsterIndex, out definition!);

    public IReadOnlyList<DungeonGeneratedDrop> Roll(
        ushort monsterIndex,
        byte monsterType,
        IDropRandomSource? random = null)
    {
        if (!_options.Drop.Enabled
            || !_state.Value.Definitions.TryGetValue(monsterIndex, out var definition))
        {
            return [];
        }

        random ??= GameRandomSource.Shared;
        var result = new List<DungeonGeneratedDrop>(
            definition.ItemDrops.Length
            + (UsesCommonChampionTable(monsterType)
                ? definition.CommonDrops.Length
                : 0)
            + (monsterType == GameDungeonMonsterTypes.SuperChampion
                ? definition.SuperDrops.Length
                : 0));
        // Every row in a MOB [item] block is an independent 1/10000 roll.
        // It is not a weighted choose-one table and is evaluated for every
        // naturally defeated monster type.
        RollTable(definition.ItemDrops, random, result);
        if (UsesCommonChampionTable(monsterType))
        {
            RollTable(definition.CommonDrops, random, result);
        }

        if (monsterType == GameDungeonMonsterTypes.SuperChampion)
        {
            RollTable(definition.SuperDrops, random, result);
        }

        return result;
    }

    private void RollTable(
        IEnumerable<MonsterChampionDropEntry> entries,
        IDropRandomSource random,
        ICollection<DungeonGeneratedDrop> result)
    {
        foreach (var entry in entries)
        {
            var probability = Math.Clamp(
                entry.Probability,
                0,
                ProbabilityDenominator);
            if (probability == 0
                || (!_options.Drop.ForceDrops
                    && random.Next(ProbabilityDenominator) >= probability))
            {
                continue;
            }

            result.Add(new DungeonGeneratedDrop(
                IsGold: false,
                entry.ItemId,
                CountOrValue: 1));
        }
    }

    private static bool UsesCommonChampionTable(byte monsterType) => monsterType is
        GameDungeonMonsterTypes.Boss
        or GameDungeonMonsterTypes.Champion
        or GameDungeonMonsterTypes.SuperChampion;

    private static CatalogState Load(
        ScriptFileSystem scripts,
        ILogger logger)
    {
        var definitions = new Dictionary<ushort, MonsterChampionDropDefinition>();
        if (!scripts.FileExists(MonsterListPath))
        {
            logger.LogWarning(
                "Monster list {Path} is unavailable; MOB-specific item drops are disabled.",
                MonsterListPath);
            return new CatalogState(definitions);
        }

        var list = scripts.ReadAllText(MonsterListPath, ScriptEncoding);
        foreach (Match entry in ListEntryPattern.Matches(list))
        {
            if (!ushort.TryParse(
                    entry.Groups["id"].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var monsterIndex)
                || monsterIndex == 0)
            {
                continue;
            }

            var relativePath = NormalizeRelativePath(entry.Groups["path"].Value);
            var sourcePath = relativePath.StartsWith(
                "monster/",
                StringComparison.OrdinalIgnoreCase)
                ? relativePath
                : $"monster/{relativePath}";
            if (!scripts.FileExists(sourcePath))
            {
                continue;
            }

            try
            {
                var text = scripts.ReadAllText(sourcePath, ScriptEncoding);
                var definition = new MonsterChampionDropDefinition(
                    monsterIndex,
                    sourcePath,
                    ParseDropBlock(text, ItemDropTag),
                    ParseDropBlock(text, CommonDropTag),
                    ParseDropBlock(text, SuperDropTag));
                if (definition.ItemDrops.Length > 0
                    || definition.CommonDrops.Length > 0
                    || definition.SuperDrops.Length > 0)
                {
                    definitions[monsterIndex] = definition;
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Could not parse MOB-specific drops for monster {MonsterIndex} from {Path}.",
                    monsterIndex,
                    sourcePath);
            }
        }

        logger.LogInformation(
            "Loaded MOB-specific drops for {DefinitionCount} monsters from {Source}: itemEntries={ItemEntryCount}, commonChampionEntries={CommonEntryCount}, superChampionEntries={SuperEntryCount}.",
            definitions.Count,
            scripts.SourceDescription,
            definitions.Values.Sum(definition => definition.ItemDrops.Length),
            definitions.Values.Sum(definition => definition.CommonDrops.Length),
            definitions.Values.Sum(definition => definition.SuperDrops.Length));
        return new CatalogState(definitions);
    }

    internal static MonsterChampionDropEntry[] ParseDropBlock(
        string text,
        string tag)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);

        var openingTag = $"[{tag}]";
        var closingTag = $"[/{tag}]";
        var result = new List<MonsterChampionDropEntry>();
        var inside = false;
        foreach (var rawLine in text.Split(
                     ["\r\n", "\n", "\r"],
                     StringSplitOptions.None))
        {
            var line = StripComment(rawLine).Trim();
            if (!inside)
            {
                if (line.StartsWith(openingTag, StringComparison.OrdinalIgnoreCase))
                {
                    inside = true;
                }

                continue;
            }

            if (line.StartsWith(closingTag, StringComparison.OrdinalIgnoreCase))
            {
                inside = false;
                continue;
            }

            var match = DropEntryPattern.Match(line);
            if (!match.Success
                || !ushort.TryParse(
                    match.Groups["item"].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var itemId)
                || itemId == 0
                || !int.TryParse(
                    match.Groups["probability"].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var probability)
                || probability <= 0)
            {
                continue;
            }

            result.Add(new MonsterChampionDropEntry(
                itemId,
                Math.Min(ProbabilityDenominator, probability)));
        }

        return result.ToArray();
    }

    private static string StripComment(string line)
    {
        var commentIndex = line.IndexOf("//", StringComparison.Ordinal);
        return commentIndex >= 0 ? line[..commentIndex] : line;
    }

    private static string NormalizeRelativePath(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private static Encoding CreateScriptEncoding()
    {
        return PvfEncodings.Cp949();
    }

    private sealed record CatalogState(
        IReadOnlyDictionary<ushort, MonsterChampionDropDefinition> Definitions);
}
