using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DFLegacy.Server;

public readonly record struct CompoundItemAmount(
    ushort ItemId,
    uint Count);

public readonly record struct CompoundSkillRequirement(
    byte SkillId,
    byte Level);

public sealed record CompoundRecipeDefinition(
    ushort RecipeItemId,
    byte RecipeType,
    IReadOnlyList<CompoundItemAmount> Ingredients,
    uint GoldCost,
    IReadOnlyList<CompoundItemAmount> Results,
    IReadOnlyList<CompoundSkillRequirement> RequiredSkills);

public sealed class CompoundCatalog
{
    private const string StackableListPath = "stackable/stackable.lst";
    private const int MaximumRowCount = 64;

    private static readonly Encoding ScriptEncoding = CreateScriptEncoding();
    private static readonly Regex ListEntryPattern = new(
        @"^\s*(?<id>\d+)\s+`(?<path>[^`]+\.stk)`",
        RegexOptions.Compiled | RegexOptions.CultureInvariant |
        RegexOptions.IgnoreCase);
    private static readonly Regex RecipeTypePattern = new(
        @"^\s*\[stackable type\]\s*`\[recipe\]`\s*(?<value>\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant |
        RegexOptions.IgnoreCase | RegexOptions.Multiline);
    private static readonly Regex IntDataPattern = new(
        @"\[int data\]\s*(?<value>.*?)\s*\[/int data\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant |
        RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex IntegerPattern = new(
        @"[+-]?\d+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly string[] SupportedRecipeTags =
    [
        "`[craftmanship]`",
        "`[weaving]`",
        "`[machinary]`",
        "`[chemistry]`"
    ];

    private readonly ScriptFileSystem _scripts;
    private readonly ILogger<CompoundCatalog> _logger;
    private readonly Lazy<CatalogState> _state;

    public CompoundCatalog(
        ScriptFileSystem scripts,
        ILogger<CompoundCatalog> logger)
    {
        _scripts = scripts;
        _logger = logger;
        _state = new Lazy<CatalogState>(
            Load,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public int Count => _state.Value.Recipes.Count;

    public bool IsAvailable => Count != 0;

    public IEnumerable<ushort> RecipeItemIds =>
        _state.Value.Recipes.Keys;

    public void Initialize() => _ = _state.Value;

    public bool TryGetDefinition(
        ushort recipeItemId,
        out CompoundRecipeDefinition definition) =>
        _state.Value.Recipes.TryGetValue(recipeItemId, out definition!);

    private CatalogState Load()
    {
        if (!_scripts.FileExists(StackableListPath))
        {
            _logger.LogWarning(
                "Compound recipe list {ListPath} was not found in {Source}; item production is disabled.",
                StackableListPath,
                _scripts.SourceDescription);
            return CatalogState.Empty;
        }

        var recipes = new Dictionary<ushort, CompoundRecipeDefinition>();
        var missingScripts = 0;
        var invalidRecipes = 0;
        foreach (var sourceLine in _scripts.ReadLines(
                     StackableListPath,
                     ScriptEncoding))
        {
            var match = ListEntryPattern.Match(sourceLine);
            if (!match.Success)
            {
                continue;
            }

            if (!ushort.TryParse(
                    match.Groups["id"].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var itemId))
            {
                throw new InvalidDataException(
                    $"Recipe item id '{match.Groups["id"].Value}' in {StackableListPath} is outside the u16 range.");
            }

            var relativePath = match.Groups["path"].Value
                .Replace('\\', '/')
                .TrimStart('/');
            var scriptPath = relativePath.StartsWith(
                "stackable/",
                StringComparison.OrdinalIgnoreCase)
                    ? relativePath
                    : $"stackable/{relativePath}";
            if (!_scripts.FileExists(scriptPath))
            {
                missingScripts++;
                continue;
            }

            var text = _scripts
                .ReadAllTextUncached(scriptPath, ScriptEncoding)
                .Replace("\0", string.Empty, StringComparison.Ordinal);
            if (TryParseRecipe(itemId, text, out var recipe))
            {
                if (!recipes.TryAdd(itemId, recipe))
                {
                    throw new InvalidDataException(
                        $"Recipe item id {itemId} is duplicated in {StackableListPath}.");
                }
            }
            else if (RecipeTypePattern.IsMatch(text))
            {
                invalidRecipes++;
            }
        }

        _logger.LogInformation(
            "Cached DFLegacy compound recipes from {Source}: recipes={RecipeCount}, invalidRecipes={InvalidRecipeCount}, missingScripts={MissingScriptCount}.",
            _scripts.SourceDescription,
            recipes.Count,
            invalidRecipes,
            missingScripts);
        return new CatalogState(
            new ReadOnlyDictionary<ushort, CompoundRecipeDefinition>(recipes));
    }

    private static bool TryParseRecipe(
        ushort recipeItemId,
        string text,
        out CompoundRecipeDefinition definition)
    {
        definition = null!;
        if (recipeItemId == 0
            || string.IsNullOrWhiteSpace(text)
            || !SupportedRecipeTags.Any(tag =>
                text.Contains(tag, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var recipeTypeMatch = RecipeTypePattern.Match(text);
        if (!recipeTypeMatch.Success
            || !byte.TryParse(
                recipeTypeMatch.Groups["value"].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var recipeType))
        {
            return false;
        }

        var intDataMatch = IntDataPattern.Match(text);
        if (!intDataMatch.Success)
        {
            return false;
        }

        var values = IntegerPattern
            .Matches(StripComments(intDataMatch.Groups["value"].Value))
            .Select(match => long.TryParse(
                    match.Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var value)
                ? value
                : long.MinValue)
            .ToArray();
        if (values.Length < 5 || values.Contains(long.MinValue))
        {
            return false;
        }

        var offset = 0;
        if (!TryReadRowCount(values, ref offset, allowZero: false, out var inputRows)
            || offset + inputRows * 2 > values.Length)
        {
            return false;
        }

        var ingredients = new List<CompoundItemAmount>(inputRows);
        ulong goldCost = 0;
        for (var row = 0; row < inputRows; row++)
        {
            var itemId = values[offset++];
            var count = values[offset++];
            if (itemId is < ushort.MinValue or > ushort.MaxValue
                || count is <= 0 or > uint.MaxValue)
            {
                return false;
            }

            if (itemId == 0)
            {
                goldCost += checked((ulong)count);
                if (goldCost > uint.MaxValue)
                {
                    return false;
                }
            }
            else
            {
                ingredients.Add(new CompoundItemAmount(
                    checked((ushort)itemId),
                    checked((uint)count)));
            }
        }

        if (!TryReadRowCount(values, ref offset, allowZero: false, out var outputRows)
            || offset + outputRows * 2 > values.Length)
        {
            return false;
        }

        var results = new List<CompoundItemAmount>(outputRows);
        for (var row = 0; row < outputRows; row++)
        {
            var itemId = values[offset++];
            var count = values[offset++];
            if (itemId is <= 0 or > ushort.MaxValue
                || count is <= 0 or > uint.MaxValue)
            {
                return false;
            }

            results.Add(new CompoundItemAmount(
                checked((ushort)itemId),
                checked((uint)count)));
        }

        var requiredSkills = new List<CompoundSkillRequirement>();
        if (offset < values.Length)
        {
            if (!TryReadRowCount(
                    values,
                    ref offset,
                    allowZero: true,
                    out var skillRows)
                || offset + skillRows * 2 > values.Length)
            {
                return false;
            }

            requiredSkills.Capacity = skillRows;
            for (var row = 0; row < skillRows; row++)
            {
                var skillId = values[offset++];
                var level = values[offset++];
                if (skillId is <= 0 or > byte.MaxValue
                    || level is <= 0 or > byte.MaxValue)
                {
                    return false;
                }

                requiredSkills.Add(new CompoundSkillRequirement(
                    checked((byte)skillId),
                    checked((byte)level)));
            }
        }

        // DF2008 recipes may append one grade value after the skill rows.
        if (offset < values.Length)
        {
            offset++;
        }

        if (offset != values.Length)
        {
            return false;
        }

        definition = new CompoundRecipeDefinition(
            recipeItemId,
            recipeType,
            ingredients.AsReadOnly(),
            checked((uint)goldCost),
            results.AsReadOnly(),
            requiredSkills.AsReadOnly());
        return true;
    }

    private static bool TryReadRowCount(
        IReadOnlyList<long> values,
        ref int offset,
        bool allowZero,
        out int rowCount)
    {
        rowCount = 0;
        if (offset >= values.Count)
        {
            return false;
        }

        var value = values[offset++];
        if (value < (allowZero ? 0 : 1) || value > MaximumRowCount)
        {
            return false;
        }

        rowCount = checked((int)value);
        return true;
    }

    private static string StripComments(string text) =>
        string.Join(
            '\n',
            (text ?? string.Empty)
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split("//", 2, StringSplitOptions.None)[0]));

    private static Encoding CreateScriptEncoding()
    {
        return PvfEncodings.Cp936Lossy();
    }

    private sealed record CatalogState(
        IReadOnlyDictionary<ushort, CompoundRecipeDefinition> Recipes)
    {
        public static CatalogState Empty { get; } = new(
            new ReadOnlyDictionary<ushort, CompoundRecipeDefinition>(
                new Dictionary<ushort, CompoundRecipeDefinition>()));
    }
}
