using System.Globalization;
using System.Text.RegularExpressions;

namespace DFLegacy.Server;

public sealed record DisjointRewardDefinition(ushort ItemId, uint Count);

public sealed class DisjointCatalog
{
    private const string ScriptPath = "etc/disjoint.etc";
    public const int JackpotRollUpperBound = 10_000;

    private static readonly Regex NumberPattern = new(
        @"[+-]?(?:\d+(?:\.\d+)?|\.\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ScriptFileSystem _scripts;
    private readonly ILogger<DisjointCatalog> _logger;
    private readonly Lazy<CatalogState> _state;

    public DisjointCatalog(
        ScriptFileSystem scripts,
        ILogger<DisjointCatalog> logger)
    {
        _scripts = scripts;
        _logger = logger;
        _state = new Lazy<CatalogState>(
            Load,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public ushort PrimaryItemId => _state.Value.PrimaryItemId;

    public int CubeCreationConstant => _state.Value.CubeCreationConstant;

    public int RarityCount => _state.Value.Rarities.Count;

    public bool IsAvailable => _state.Value.IsAvailable;

    public void Initialize() => _ = _state.Value;

    public bool TryGenerate(
        ItemDefinition source,
        int additionalItemRoll,
        int jackpotRoll,
        ItemCatalog itemCatalog,
        out IReadOnlyList<DisjointRewardDefinition> rewards)
    {
        rewards = [];
        var state = _state.Value;
        if (!state.IsAvailable
            || source.ScriptKind != ItemScriptKind.Equipment
            || source.InventoryCategory != ItemInventoryCategory.Equipment
            || source.Rarity is not int rarity
            || rarity < 0
            || rarity >= state.Rarities.Count
            || source.Grade is not int grade
            || grade < 0
            || additionalItemRoll < 0
            || jackpotRoll is < 0 or >= JackpotRollUpperBound
            || !itemCatalog.TryCalculateDisjointSellValue(source.Id, out var sellValue))
        {
            return false;
        }

        var rarityInfo = state.Rarities[rarity];
        var scaledValue = checked((long)1_000 * sellValue / state.CubeCreationConstant);
        var primaryCountValue = decimal.Floor(
            scaledValue * rarityInfo.PrimaryMultiplier / 1_000m);
        var primaryCount = ToRewardCount(primaryCountValue);
        if (primaryCount == 0)
        {
            primaryCount = 1;
        }

        var generated = new List<DisjointRewardDefinition>(2)
        {
            new(state.PrimaryItemId, primaryCount)
        };
        if (rarityInfo.AdditionalItemIds.Count != 0
            && rarityInfo.Additional is { } additional)
        {
            var itemId = rarityInfo.AdditionalItemIds[
                additionalItemRoll % rarityInfo.AdditionalItemIds.Count];
            var isJackpot = jackpotRoll < additional.JackpotThreshold;
            var divisor = isJackpot
                ? additional.JackpotDivisor
                : additional.NormalDivisor;
            var count = ToRewardCount(decimal.Floor(grade / divisor));
            if (count == 0)
            {
                count = 1;
            }

            generated.Add(new DisjointRewardDefinition(itemId, count));
        }

        rewards = generated.AsReadOnly();
        return true;
    }

    private CatalogState Load()
    {
        if (!_scripts.FileExists(ScriptPath))
        {
            _logger.LogWarning(
                "Disjoint script {ScriptPath} was not found in {Source}; equipment disjoint is disabled.",
                ScriptPath,
                _scripts.SourceDescription);
            return CatalogState.Empty;
        }

        var lines = _scripts.ReadLines(ScriptPath)
            .Select(StripComment)
            .Where(line => line.Length != 0)
            .ToArray();
        var primaryItemId = ReadPrimaryItemId(lines);
        var creationValues = ReadHeaderValues(lines, "cube creation const");
        if (creationValues.Count < 2
            || !TryReadPositiveInt(creationValues[0], out var creationConstant))
        {
            throw new InvalidDataException(
                $"{ScriptPath} has an invalid [cube creation const] row.");
        }

        var multipliers = creationValues
            .Skip(1)
            .Select(ParsePositiveDecimal)
            .ToArray();
        var additionalItems = ReadAdditionalItems(lines);
        var additionalConstants = ReadAdditionalConstants(lines);
        if (multipliers.Length == 0
            || additionalItems.Count != multipliers.Length
            || additionalConstants.Count != multipliers.Length)
        {
            throw new InvalidDataException(
                $"{ScriptPath} rarity rows do not match [cube creation const].");
        }

        var rarities = new DisjointRarityInfo[multipliers.Length];
        for (var rarity = 0; rarity < rarities.Length; rarity++)
        {
            rarities[rarity] = new DisjointRarityInfo(
                multipliers[rarity],
                additionalItems[rarity],
                additionalConstants[rarity]);
        }

        _logger.LogInformation(
            "Cached DFLegacy disjoint rules from {Source}: primaryItem={PrimaryItemId}, creationConstant={CreationConstant}, rarityRows={RarityCount}, additionalRows={AdditionalCount}.",
            _scripts.SourceDescription,
            primaryItemId,
            creationConstant,
            rarities.Length,
            rarities.Count(rarity => rarity.AdditionalItemIds.Count != 0));
        return new CatalogState(
            primaryItemId,
            creationConstant,
            Array.AsReadOnly(rarities));
    }

    private static ushort ReadPrimaryItemId(IReadOnlyList<string> lines)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            if (!IsTag(lines[index], "cube index"))
            {
                continue;
            }

            index++;
            if (index >= lines.Count || !IsToken(lines[index], "no element"))
            {
                continue;
            }

            for (index++; index < lines.Count && !IsTag(lines[index], "/cube index"); index++)
            {
                var values = ReadNumbers(lines[index]);
                if (values.Count != 0
                    && ushort.TryParse(
                        values[0],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var itemId)
                    && itemId != 0)
                {
                    return itemId;
                }
            }
        }

        throw new InvalidDataException(
            $"{ScriptPath} does not define a [no element] cube item.");
    }

    private static IReadOnlyList<string> ReadHeaderValues(
        IReadOnlyList<string> lines,
        string tag)
    {
        foreach (var line in lines)
        {
            if (TryReadTag(line, out var currentTag, out var remainder)
                && string.Equals(currentTag, tag, StringComparison.OrdinalIgnoreCase))
            {
                return ReadNumbers(remainder);
            }
        }

        return [];
    }

    private static IReadOnlyList<IReadOnlyList<ushort>> ReadAdditionalItems(
        IReadOnlyList<string> lines)
    {
        var rows = ReadSectionRows(lines, "additional result");
        var result = new List<IReadOnlyList<ushort>>(rows.Count);
        foreach (var row in rows)
        {
            var values = ReadNumbers(row);
            if (values.Count == 0
                || !int.TryParse(values[0], out var count)
                || count < 0
                || values.Count != count + 1)
            {
                throw new InvalidDataException(
                    $"{ScriptPath} has an invalid [additional result] row '{row}'.");
            }

            var itemIds = new ushort[count];
            for (var index = 0; index < count; index++)
            {
                if (!ushort.TryParse(values[index + 1], out itemIds[index])
                    || itemIds[index] == 0)
                {
                    throw new InvalidDataException(
                        $"{ScriptPath} has an invalid additional item id in '{row}'.");
                }
            }

            result.Add(Array.AsReadOnly(itemIds));
        }

        return result.AsReadOnly();
    }

    private static IReadOnlyList<DisjointAdditionalInfo?> ReadAdditionalConstants(
        IReadOnlyList<string> lines)
    {
        var rows = ReadSectionRows(lines, "additional result const");
        var result = new List<DisjointAdditionalInfo?>(rows.Count);
        foreach (var row in rows)
        {
            var values = ReadNumbers(row);
            if (values.Count == 1 && ParseDecimal(values[0]) == 0)
            {
                result.Add(null);
                continue;
            }

            if (values.Count < 3)
            {
                throw new InvalidDataException(
                    $"{ScriptPath} has an invalid [additional result const] row '{row}'.");
            }

            var jackpotDivisor = ParsePositiveDecimal(values[0]);
            var normalDivisor = ParsePositiveDecimal(values[1]);
            var chancePercent = ParseDecimal(values[2]);
            if (chancePercent is < 0 or > 100)
            {
                throw new InvalidDataException(
                    $"{ScriptPath} has an invalid additional jackpot chance in '{row}'.");
            }

            result.Add(new DisjointAdditionalInfo(
                jackpotDivisor,
                normalDivisor,
                checked((int)decimal.Floor(chancePercent * 100m))));
        }

        return result.AsReadOnly();
    }

    private static IReadOnlyList<string> ReadSectionRows(
        IReadOnlyList<string> lines,
        string tag)
    {
        var rows = new List<string>();
        var found = false;
        foreach (var line in lines)
        {
            if (!found)
            {
                found = IsTag(line, tag);
                continue;
            }

            if (TryReadTag(line, out _, out _))
            {
                break;
            }

            rows.Add(line);
        }

        return rows.AsReadOnly();
    }

    private static string StripComment(string line)
    {
        var commentIndex = line.IndexOf("//", StringComparison.Ordinal);
        return (commentIndex >= 0 ? line[..commentIndex] : line).Trim();
    }

    private static bool IsTag(string line, string expected) =>
        TryReadTag(line, out var tag, out _)
        && string.Equals(tag, expected, StringComparison.OrdinalIgnoreCase);

    private static bool IsToken(string line, string expected)
    {
        var value = line.Trim().Trim('`');
        return value.Length > 2
            && value[0] == '['
            && value[^1] == ']'
            && string.Equals(
                value[1..^1].Trim(),
                expected,
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadTag(
        string line,
        out string tag,
        out string remainder)
    {
        tag = string.Empty;
        remainder = string.Empty;
        var value = line.Trim();
        if (value.Length == 0 || value[0] != '[')
        {
            return false;
        }

        var closing = value.IndexOf(']');
        if (closing <= 1)
        {
            return false;
        }

        tag = value[1..closing].Trim();
        remainder = value[(closing + 1)..].Trim();
        return true;
    }

    private static IReadOnlyList<string> ReadNumbers(string value) =>
        NumberPattern.Matches(value)
            .Select(match => match.Value)
            .ToArray();

    private static bool TryReadPositiveInt(string value, out int result) =>
        int.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out result)
        && result > 0;

    private static decimal ParsePositiveDecimal(string value)
    {
        var result = ParseDecimal(value);
        if (result <= 0)
        {
            throw new InvalidDataException(
                $"{ScriptPath} expected a positive number but found '{value}'.");
        }

        return result;
    }

    private static decimal ParseDecimal(string value) =>
        decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture);

    private static uint ToRewardCount(decimal value)
    {
        if (value <= 0)
        {
            return 0;
        }

        if (value > uint.MaxValue)
        {
            throw new InvalidDataException(
                $"{ScriptPath} generated a reward count above the protocol limit.");
        }

        return checked((uint)value);
    }

    private sealed record DisjointRarityInfo(
        decimal PrimaryMultiplier,
        IReadOnlyList<ushort> AdditionalItemIds,
        DisjointAdditionalInfo? Additional);

    private sealed record DisjointAdditionalInfo(
        decimal JackpotDivisor,
        decimal NormalDivisor,
        int JackpotThreshold);

    private sealed record CatalogState(
        ushort PrimaryItemId,
        int CubeCreationConstant,
        IReadOnlyList<DisjointRarityInfo> Rarities)
    {
        public static CatalogState Empty { get; } = new(0, 0, []);

        public bool IsAvailable =>
            PrimaryItemId != 0 && CubeCreationConstant > 0 && Rarities.Count != 0;
    }
}
