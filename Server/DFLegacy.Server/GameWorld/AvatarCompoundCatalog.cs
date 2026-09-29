using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DFLegacy.Server;

public sealed record AvatarCompoundWeightedItem(
    ushort ItemId,
    int Weight);

public sealed record AvatarCompoundDefinition(
    byte Job,
    byte Grade,
    byte UpperGrade,
    ushort MaterialItemId,
    uint MaterialCount,
    IReadOnlyDictionary<byte, int> RareRates,
    IReadOnlyDictionary<byte, int> UpperRareRates,
    IReadOnlyDictionary<byte, IReadOnlyList<AvatarCompoundWeightedItem>> RareItems,
    IReadOnlyDictionary<byte, IReadOnlyList<AvatarCompoundWeightedItem>> NormalItems);

public readonly record struct AvatarCompoundItemMetadata(
    byte Job,
    byte Part,
    byte Grade,
    int OptionCount);

public readonly record struct AvatarCompoundSelection(
    ushort ItemId,
    int RareRate,
    bool IsRare);

public sealed class AvatarCompoundCatalog
{
    private const string MasterScriptPath = "etc/compoundavatar.etc";

    public const int RareRateUpperBound = 10_000;
    public const byte CompoundPartCount = 8;

    private static readonly Encoding ScriptEncoding = CreateScriptEncoding();
    private static readonly Regex MasterEntryPattern = new(
        @"^\s*(?<job>\d+)\s+`(?<path>[^`]+\.etc)`",
        RegexOptions.Compiled | RegexOptions.CultureInvariant |
        RegexOptions.IgnoreCase | RegexOptions.Multiline);
    private static readonly Regex IntegerPattern = new(
        @"[+-]?\d+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex RateEntryPattern = new(
        @"`(?<part>[^`]+)`\s+(?<rate>[+-]?\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AvatarSelectAbilityPattern = new(
        @"\[avatar select ability\]\s*(?<value>.*?)\s*\[/avatar select ability\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant |
        RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly IReadOnlyDictionary<string, byte> PartsByName =
        new ReadOnlyDictionary<string, byte>(
            new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase)
            {
                ["hat"] = 0,
                ["hair"] = 1,
                ["face"] = 2,
                ["coat"] = 3,
                ["pants"] = 4,
                ["shoes"] = 5,
                ["neck"] = 6,
                ["belt"] = 7
            });

    private readonly ScriptFileSystem _scripts;
    private readonly ItemCatalog _items;
    private readonly ILogger<AvatarCompoundCatalog> _logger;
    private readonly Lazy<CatalogState> _state;
    private readonly ConcurrentDictionary<ushort, AvatarMetadataCacheEntry>
        _avatarMetadata = new();

    public AvatarCompoundCatalog(
        ScriptFileSystem scripts,
        ItemCatalog items,
        ILogger<AvatarCompoundCatalog> logger)
    {
        _scripts = scripts;
        _items = items;
        _logger = logger;
        _state = new Lazy<CatalogState>(
            Load,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public int Count => _state.Value.Definitions.Count;

    public bool IsAvailable => Count != 0;

    public void Initialize() => _ = _state.Value;

    public bool TryGetDefinition(
        byte job,
        out AvatarCompoundDefinition definition) =>
        _state.Value.Definitions.TryGetValue(job, out definition!);

    public bool TryGetAvatarMetadata(
        ushort itemId,
        out AvatarCompoundItemMetadata metadata)
    {
        var cached = _avatarMetadata.GetOrAdd(itemId, LoadAvatarMetadata);
        metadata = cached.Metadata;
        return cached.Found;
    }

    public bool TrySelectResult(
        byte job,
        byte part,
        byte firstGrade,
        byte secondGrade,
        IDropRandomSource random,
        out AvatarCompoundSelection selection)
    {
        ArgumentNullException.ThrowIfNull(random);
        selection = default;
        if (!_state.Value.Definitions.TryGetValue(job, out var definition)
            || part >= CompoundPartCount)
        {
            return false;
        }

        var rates = firstGrade == definition.Grade
            && secondGrade == definition.Grade
                ? definition.RareRates
                : definition.UpperRareRates;
        var rareRate = Math.Clamp(rates.GetValueOrDefault(part), 0, RareRateUpperBound);
        definition.RareItems.TryGetValue(part, out var rarePool);
        var isRare = rareRate > 0
            && rarePool is { Count: > 0 }
            && random.Next(RareRateUpperBound) < rareRate;
        IReadOnlyList<AvatarCompoundWeightedItem> selectedPool;
        if (isRare)
        {
            selectedPool = rarePool!;
        }
        else if (!definition.NormalItems.TryGetValue(part, out var normalPool)
            || normalPool.Count == 0)
        {
            return false;
        }
        else
        {
            selectedPool = normalPool;
        }

        if (!TrySelectWeighted(selectedPool, random, out var itemId))
        {
            return false;
        }

        selection = new AvatarCompoundSelection(itemId, rareRate, isRare);
        return true;
    }

    private CatalogState Load()
    {
        if (!_scripts.FileExists(MasterScriptPath))
        {
            _logger.LogWarning(
                "Avatar compound script {ScriptPath} was not found in {Source}; avatar compound is disabled.",
                MasterScriptPath,
                _scripts.SourceDescription);
            return CatalogState.Empty;
        }

        var definitions = new Dictionary<byte, AvatarCompoundDefinition>();
        var master = _scripts.ReadAllText(MasterScriptPath, ScriptEncoding);
        foreach (Match match in MasterEntryPattern.Matches(master))
        {
            if (!int.TryParse(
                    match.Groups["job"].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var oneBasedJob)
                || oneBasedJob is <= 0 or > byte.MaxValue + 1)
            {
                continue;
            }

            var job = checked((byte)(oneBasedJob - 1));
            var relativePath = match.Groups["path"].Value
                .Replace('\\', '/')
                .TrimStart('/');
            var scriptPath = relativePath.StartsWith(
                "etc/",
                StringComparison.OrdinalIgnoreCase)
                    ? relativePath
                    : $"etc/{relativePath}";
            if (!_scripts.FileExists(scriptPath))
            {
                _logger.LogWarning(
                    "Avatar compound job {Job} references missing script {ScriptPath}.",
                    job,
                    scriptPath);
                continue;
            }

            if (!TryParseDefinition(
                    job,
                    _scripts.ReadAllText(scriptPath, ScriptEncoding),
                    out var definition))
            {
                _logger.LogWarning(
                    "Avatar compound job {Job} has an invalid definition in {ScriptPath}.",
                    job,
                    scriptPath);
                continue;
            }

            definitions[job] = definition;
        }

        _logger.LogInformation(
            "Cached DFLegacy avatar compound rules from {Source}: jobs={JobCount}.",
            _scripts.SourceDescription,
            definitions.Count);
        return new CatalogState(
            new ReadOnlyDictionary<byte, AvatarCompoundDefinition>(definitions));
    }

    private AvatarMetadataCacheEntry LoadAvatarMetadata(ushort itemId)
    {
        if (!_items.TryGetDefinition(itemId, out var definition)
            || definition.InventoryCategory != ItemInventoryCategory.Avatar
            || definition.Grade is not int grade
            || grade is < byte.MinValue or >= byte.MaxValue
            || !TryGetAvatarJob(definition.ScriptPath, out var job)
            || !CharacterAvatarInventoryLayout.TryGetWornSlot(definition, out var part)
            || part >= CompoundPartCount)
        {
            return AvatarMetadataCacheEntry.NotFound;
        }

        var optionCount = 0;
        if (_scripts.FileExists(definition.ScriptPath))
        {
            optionCount = ReadAvatarOptionCount(
                _scripts.ReadAllText(definition.ScriptPath, ScriptEncoding));
        }

        return new AvatarMetadataCacheEntry(
            true,
            new AvatarCompoundItemMetadata(
                job,
                checked((byte)part),
                checked((byte)grade),
                optionCount));
    }

    private static bool TryParseDefinition(
        byte job,
        string text,
        out AvatarCompoundDefinition definition)
    {
        definition = null!;
        var source = StripComments(text);
        if (!TryReadInlineInteger(source, "grade", out var grade)
            || !TryReadInlineInteger(source, "upper grade", out var upperGrade)
            || grade is < byte.MinValue or > byte.MaxValue
            || upperGrade is < byte.MinValue or > byte.MaxValue)
        {
            return false;
        }

        var rareRates = ReadRates(source, "rare rate");
        var upperRareRates = ReadRates(source, "upper rare rate");
        var material = ReadSectionIntegers(source, "material");
        if (material.Length < 3
            || material[0] != 1
            || material[1] is <= 0 or > ushort.MaxValue
            || material[2] is <= 0 or > uint.MaxValue
            || rareRates.Count != CompoundPartCount
            || upperRareRates.Count != CompoundPartCount)
        {
            return false;
        }

        var rareItems = new Dictionary<byte, IReadOnlyList<AvatarCompoundWeightedItem>>();
        var normalItems = new Dictionary<byte, IReadOnlyList<AvatarCompoundWeightedItem>>();
        for (byte part = 0; part < CompoundPartCount; part++)
        {
            var partName = PartsByName.First(pair => pair.Value == part).Key;
            var values = ReadSectionIntegers(source, $"{partName} avatar");
            if (values.Length == 0
                || values[0] <= 0
                || values[0] > int.MaxValue / 2
                || values.Length < 1 + values[0] * 2)
            {
                return false;
            }

            var rareCount = checked((int)values[0]);
            var parsedRare = ReadWeightedItems(values, 1, rareCount);
            var normalOffset = checked(1 + rareCount * 2);
            var parsedNormal = ReadWeightedItems(
                values,
                normalOffset,
                (values.Length - normalOffset) / 2);
            if (parsedRare.Count == 0 || parsedNormal.Count == 0)
            {
                return false;
            }

            rareItems[part] = parsedRare;
            normalItems[part] = parsedNormal;
        }

        definition = new AvatarCompoundDefinition(
            job,
            checked((byte)grade),
            checked((byte)upperGrade),
            checked((ushort)material[1]),
            checked((uint)material[2]),
            new ReadOnlyDictionary<byte, int>(rareRates),
            new ReadOnlyDictionary<byte, int>(upperRareRates),
            new ReadOnlyDictionary<byte, IReadOnlyList<AvatarCompoundWeightedItem>>(
                rareItems),
            new ReadOnlyDictionary<byte, IReadOnlyList<AvatarCompoundWeightedItem>>(
                normalItems));
        return true;
    }

    private static IReadOnlyList<AvatarCompoundWeightedItem> ReadWeightedItems(
        long[] values,
        int offset,
        int count)
    {
        var result = new List<AvatarCompoundWeightedItem>(Math.Max(0, count));
        for (var index = 0; index < count; index++)
        {
            var pairOffset = checked(offset + index * 2);
            if (pairOffset < 0 || pairOffset > values.Length - 2)
            {
                break;
            }

            var itemId = values[pairOffset];
            var weight = values[pairOffset + 1];
            if (itemId is > 0 and <= ushort.MaxValue
                && weight is >= 0 and <= int.MaxValue)
            {
                result.Add(new AvatarCompoundWeightedItem(
                    checked((ushort)itemId),
                    checked((int)weight)));
            }
        }

        return result.AsReadOnly();
    }

    private static Dictionary<byte, int> ReadRates(string text, string sectionName)
    {
        var rates = new Dictionary<byte, int>();
        if (!TryReadSection(text, sectionName, out var section))
        {
            return rates;
        }

        foreach (Match match in RateEntryPattern.Matches(section))
        {
            if (PartsByName.TryGetValue(match.Groups["part"].Value.Trim(), out var part)
                && int.TryParse(
                    match.Groups["rate"].Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var rate))
            {
                rates[part] = rate;
            }
        }

        return rates;
    }

    private static long[] ReadSectionIntegers(string text, string sectionName)
    {
        if (!TryReadSection(text, sectionName, out var section))
        {
            return [];
        }

        return IntegerPattern.Matches(section)
            .Select(match => long.Parse(
                match.Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture))
            .ToArray();
    }

    private static bool TryReadInlineInteger(
        string text,
        string tag,
        out int value)
    {
        value = 0;
        var match = Regex.Match(
            text,
            $@"^\s*\[{Regex.Escape(tag)}\]\s*(?<value>[+-]?\d+)",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase |
            RegexOptions.Multiline);
        return match.Success
            && int.TryParse(
                match.Groups["value"].Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out value);
    }

    private static bool TryReadSection(
        string text,
        string sectionName,
        out string section)
    {
        var match = Regex.Match(
            text,
            $@"^\s*\[{Regex.Escape(sectionName)}\]\s*(?<value>.*?)^\s*\[/{Regex.Escape(sectionName)}\]",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase |
            RegexOptions.Multiline | RegexOptions.Singleline);
        section = match.Success ? match.Groups["value"].Value : string.Empty;
        return match.Success;
    }

    private static int ReadAvatarOptionCount(string text)
    {
        var match = AvatarSelectAbilityPattern.Match(text ?? string.Empty);
        if (!match.Success)
        {
            return 0;
        }

        return match.Groups["value"].Value
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => StripComment(line).Trim())
            .Count(line => line.StartsWith("`[", StringComparison.Ordinal));
    }

    private static bool TryGetAvatarJob(string scriptPath, out byte job)
    {
        var path = (scriptPath ?? string.Empty)
            .Replace('\\', '/')
            .ToLowerInvariant();
        job = path switch
        {
            _ when path.Contains("/swordman/avatar/", StringComparison.Ordinal) => 0,
            _ when path.Contains("/fighter/avatar/", StringComparison.Ordinal) => 1,
            _ when path.Contains("/gunner/avatar/", StringComparison.Ordinal) => 2,
            _ when path.Contains("/mage/avatar/", StringComparison.Ordinal) => 3,
            _ when path.Contains("/priest/avatar/", StringComparison.Ordinal) => 4,
            _ => byte.MaxValue
        };
        return job != byte.MaxValue;
    }

    private static bool TrySelectWeighted(
        IReadOnlyList<AvatarCompoundWeightedItem> pool,
        IDropRandomSource random,
        out ushort itemId)
    {
        itemId = 0;
        if (pool.Count == 0)
        {
            return false;
        }

        var totalWeight = pool.Aggregate(
            0L,
            (total, item) => checked(total + Math.Max(0, item.Weight)));
        if (totalWeight <= 0)
        {
            itemId = pool[0].ItemId;
            return itemId != 0;
        }

        var roll = totalWeight <= int.MaxValue
            ? random.Next(checked((int)totalWeight))
            : (long)((ulong)random.Next(int.MaxValue) * (ulong)totalWeight
                / int.MaxValue);
        var cumulative = 0L;
        foreach (var item in pool)
        {
            cumulative = checked(cumulative + Math.Max(0, item.Weight));
            if (roll < cumulative)
            {
                itemId = item.ItemId;
                return itemId != 0;
            }
        }

        return false;
    }

    private static string StripComments(string text) => string.Join(
        '\n',
        (text ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(StripComment));

    private static string StripComment(string line)
    {
        var commentIndex = line.IndexOf("//", StringComparison.Ordinal);
        return commentIndex >= 0 ? line[..commentIndex] : line;
    }

    private static Encoding CreateScriptEncoding()
    {
        return PvfEncodings.Cp949Lossy();
    }

    private readonly record struct AvatarMetadataCacheEntry(
        bool Found,
        AvatarCompoundItemMetadata Metadata)
    {
        public static AvatarMetadataCacheEntry NotFound { get; } = new(false, default);
    }

    private sealed record CatalogState(
        IReadOnlyDictionary<byte, AvatarCompoundDefinition> Definitions)
    {
        public static CatalogState Empty { get; } = new(
            new ReadOnlyDictionary<byte, AvatarCompoundDefinition>(
                new Dictionary<byte, AvatarCompoundDefinition>()));
    }
}
