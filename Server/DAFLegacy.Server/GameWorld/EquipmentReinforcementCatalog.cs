using System.Globalization;
using System.Text;

namespace DAFLegacy.Server;

public sealed record EquipmentReinforcementCost(
    ushort MaterialItemId,
    uint MaterialCount,
    int Gold,
    int FailureWeight,
    int PenaltyType,
    byte TargetLevel);

/// <summary>
/// etc/upgrade.etc 的可选 [disjoint] 段（强化毁坏补偿）。子键与取值语义循
/// 参考工程 UpgradeTableFile 的同名条目；补偿物品必须为本版存在的物品
/// （A21 参考值 3243 强烈之痕迹在 60CN 物品表中不存在，不可照搬）。
/// </summary>
public sealed record ReinforcementDisjointConfig(
    ushort BonusItemId,
    int EquipLevelConst,
    int BonusCountLevelBase,
    IReadOnlyDictionary<string, double> BonusWeightByRarity,
    IReadOnlyDictionary<string, int> CorrectionGradeByRarity)
{
    public static readonly string[] RarityNames =
        ["common", "uncommon", "rare", "unique", "epic"];
}

public sealed class EquipmentReinforcementCatalog
{
    private const string ScriptPath = "etc/upgrade.etc";

    public const byte MaximumLevel = 31;
    public const int FailureRollUpperBound = 100_000;

    private static readonly HashSet<string> SupportedSections =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "table",
            "cost",
            "cost weights by rarity",
            "type"
        };

    private readonly ScriptFileSystem _scripts;
    private readonly ServerOptions _options;
    private readonly ILogger<EquipmentReinforcementCatalog> _logger;
    private readonly Lazy<CatalogState> _state;

    public EquipmentReinforcementCatalog(
        ServerOptions options,
        ScriptFileSystem scripts,
        ILogger<EquipmentReinforcementCatalog> logger)
    {
        _options = options;
        _scripts = scripts;
        _logger = logger;
        _state = new Lazy<CatalogState>(
            Load,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public bool IsAvailable => _state.Value.Rows.Count != 0;

    public int RowCount => _state.Value.Rows.Count;

    public void Initialize() => _ = _state.Value;

    public bool TryGetCost(
        byte currentLevel,
        ItemDefinition equipment,
        out EquipmentReinforcementCost cost)
    {
        ArgumentNullException.ThrowIfNull(equipment);
        cost = null!;
        var state = _state.Value;
        var targetLevel = currentLevel + 1;
        if (currentLevel >= MaximumLevel
            || targetLevel > state.Rows.Count
            || equipment.ScriptKind != ItemScriptKind.Equipment
            || equipment.InventoryCategory != ItemInventoryCategory.Equipment)
        {
            return false;
        }

        var row = state.Rows[targetLevel - 1];
        cost = new EquipmentReinforcementCost(
            row.MaterialItemId,
            row.MaterialCount,
            CalculateGold(state, equipment),
            row.FailureWeight,
            row.PenaltyType,
            checked((byte)targetLevel));
        return true;
    }

    /// <summary>
    /// 按可选 [disjoint] 段计算强化毁坏补偿：品级修正后须达到 [equip level const]
    /// 门槛，目标强化等级须高于计数基准，补偿数量 = (目标等级 − 基准)² × 品级权重
    /// （参考工程 ItemUpgradeTableProvider.CalculateDestroyBonus 同式）。段缺失或
    /// 任一门槛不满足时不补偿。
    /// </summary>
    public bool TryGetDestroyBonus(
        ItemDefinition equipment,
        byte targetLevel,
        out ushort bonusItemId,
        out int bonusCount)
    {
        ArgumentNullException.ThrowIfNull(equipment);
        bonusItemId = 0;
        bonusCount = 0;
        var disjoint = _state.Value.Disjoint;
        if (disjoint is null)
        {
            return false;
        }

        var rarity = Math.Clamp(equipment.Rarity ?? 0, 0, ReinforcementDisjointConfig.RarityNames.Length - 1);
        var rarityName = ReinforcementDisjointConfig.RarityNames[rarity];
        var correctionGrade = disjoint.CorrectionGradeByRarity.TryGetValue(
            rarityName,
            out var correction)
            ? correction
            : 0;
        var correctedGrade = Math.Max(0, (equipment.Grade ?? 0) - correctionGrade);
        if (disjoint.EquipLevelConst > 0 && correctedGrade < disjoint.EquipLevelConst)
        {
            return false;
        }

        if (targetLevel <= disjoint.BonusCountLevelBase
            || !disjoint.BonusWeightByRarity.TryGetValue(rarityName, out var weight)
            || weight <= 0)
        {
            return false;
        }

        var levelsAboveBase = targetLevel - disjoint.BonusCountLevelBase;
        var count = (int)(levelsAboveBase * levelsAboveBase * weight);
        if (count <= 0)
        {
            return false;
        }

        bonusItemId = disjoint.BonusItemId;
        bonusCount = count;
        return true;
    }

    private CatalogState Load()
    {
        if (!_scripts.FileExists(ScriptPath))
        {
            _logger.LogWarning(
                "Reinforcement script {ScriptPath} was not found in {Source}; equipment reinforcement is disabled.",
                ScriptPath,
                _scripts.SourceDescription);
            return CatalogState.Empty;
        }

        var sourceLines = _scripts
            .ReadLines(ScriptPath, PvfEncodings.Cp936Lossy())
            .ToArray();
        var sections = ReadSections(sourceLines);
        var rows = ReadTableRows(GetSection(sections, "table"));
        if (rows.Count == 0)
        {
            throw new InvalidDataException(
                $"{ScriptPath} does not contain any valid reinforcement rows.");
        }

        var costsByLevel = ReadIntList(GetSection(sections, "cost"));
        var rarityWeights = ReadDoubleList(
            GetSection(sections, "cost weights by rarity"));
        var typeWeights = ReadDoubleList(GetSection(sections, "type"));
        ReinforcementDisjointConfig? disjoint;
        string disjointSource;
        if (!_options.Reinforcement.Enabled)
        {
            // 总开关关闭（docs/design/02 R5）：毁坏补偿硬关闭，PVF [disjoint]
            // 段与 server.json 覆写节点一并失效。
            disjoint = null;
            disjointSource = "disabled";
        }
        else
        {
            disjoint = ReadDisjoint(sourceLines);
            disjointSource = disjoint is not null ? "etc/upgrade.etc" : "none";
            var configuredBonus = _options.Reinforcement.DestroyBonus;
            if (configuredBonus.ItemId > 0)
            {
                // server.json 覆写为整节点语义（docs/design/02 R5）：启用即整体取代
                // PVF [disjoint] 段，不做逐字段合并。
                disjoint = new ReinforcementDisjointConfig(
                    checked((ushort)configuredBonus.ItemId),
                    configuredBonus.EquipLevelConst,
                    configuredBonus.BonusCountLevelBase,
                    NormalizeRarityValues(configuredBonus.BonusWeightByRarity),
                    NormalizeRarityValues(configuredBonus.CorrectionGradeByRarity));
                disjointSource = "server.json";
            }
        }

        _logger.LogInformation(
            "Cached DAFLegacy reinforcement rules from {Source}: rows={RowCount}, levelCosts={CostCount}, rarityWeights={RarityCount}, typeWeights={TypeCount}, destroyBonus={DestroyBonus} (source={DestroyBonusSource}).",
            _scripts.SourceDescription,
            rows.Count,
            costsByLevel.Count,
            rarityWeights.Count,
            typeWeights.Count,
            disjoint is null ? "none" : disjoint.BonusItemId.ToString(),
            disjointSource);
        return new CatalogState(rows, costsByLevel, rarityWeights, typeWeights, disjoint);
    }

    private static IReadOnlyDictionary<string, TValue> NormalizeRarityValues<TValue>(
        IDictionary<string, TValue> values) =>
        values.ToDictionary(
            pair => pair.Key.ToLowerInvariant(),
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);

    private static int CalculateGold(
        CatalogState state,
        ItemDefinition equipment)
    {
        var minimumLevel = Math.Max(1, equipment.MinimumLevel ?? 1);
        var rarity = Math.Clamp(equipment.Rarity ?? 0, 0, 4);
        var costIndex = checked(minimumLevel + Math.Max(0, rarity - 1) * 2);
        if (costIndex < 0
            || costIndex >= state.CostsByLevel.Count
            || state.CostsByLevel[costIndex] <= 0)
        {
            return 0;
        }

        var rarityWeight = rarity < state.RarityWeights.Count
            ? state.RarityWeights[rarity]
            : 1d;
        if (rarityWeight <= 0)
        {
            rarityWeight = 1d;
        }

        var typeIndex = GetTypeIndex(equipment.ScriptPath);
        var typeWeight = typeIndex < state.TypeWeights.Count
            ? state.TypeWeights[typeIndex]
            : 1d;
        if (typeWeight < 0)
        {
            typeWeight = 0;
        }

        var value = state.CostsByLevel[costIndex] * rarityWeight * typeWeight;
        if (double.IsNaN(value) || value <= 0)
        {
            return 0;
        }

        if (double.IsPositiveInfinity(value) || value > int.MaxValue)
        {
            throw new InvalidDataException(
                $"{ScriptPath} produced a reinforcement gold cost above Int32.MaxValue.");
        }

        return Math.Max(0, (int)Math.Round(value, MidpointRounding.AwayFromZero));
    }

    private static int GetTypeIndex(string path)
    {
        var value = (path ?? string.Empty).Replace('\\', '/').ToLowerInvariant();
        if (ContainsAny(
                value,
                "/weapon/",
                "/sword/",
                "/blunt/",
                "/spear/",
                "/claw/",
                "/gun/",
                "/bow/",
                "/staff/",
                "/rod/",
                "/pole/"))
        {
            return 0;
        }

        if (value.Contains("/title/", StringComparison.Ordinal))
        {
            return 1;
        }

        if (ContainsAny(value, "/jacket/", "/coat/"))
        {
            return 2;
        }

        if (value.Contains("/shoulder/", StringComparison.Ordinal))
        {
            return 3;
        }

        if (value.Contains("/pants/", StringComparison.Ordinal))
        {
            return 4;
        }

        if (value.Contains("/shoes/", StringComparison.Ordinal))
        {
            return 5;
        }

        if (value.Contains("/belt/", StringComparison.Ordinal))
        {
            return 6;
        }

        if (ContainsAny(value, "/amulet/", "/necklace/"))
        {
            return 7;
        }

        if (ContainsAny(value, "/wrist/", "/bracelet/"))
        {
            return 8;
        }

        return value.Contains("/ring/", StringComparison.Ordinal) ? 9 : 0;
    }

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(candidate => value.Contains(candidate, StringComparison.Ordinal));

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ReadSections(
        IEnumerable<string> sourceLines)
    {
        var sections = new Dictionary<string, List<string>>(
            StringComparer.OrdinalIgnoreCase);
        string? currentSection = null;
        foreach (var sourceLine in sourceLines)
        {
            var line = StripComment(sourceLine);
            if (line.Length == 0)
            {
                continue;
            }

            if (TryReadTag(line, out var tag, out var remainder))
            {
                if (tag.StartsWith("/", StringComparison.Ordinal))
                {
                    currentSection = null;
                    continue;
                }

                currentSection = SupportedSections.Contains(tag) ? tag : null;
                if (currentSection is null)
                {
                    continue;
                }

                if (!sections.TryGetValue(currentSection, out var rows))
                {
                    rows = [];
                    sections[currentSection] = rows;
                }

                if (remainder.Length != 0)
                {
                    rows.Add(remainder);
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

    private static IReadOnlyList<string> GetSection(
        IReadOnlyDictionary<string, IReadOnlyList<string>> sections,
        string name) =>
        sections.TryGetValue(name, out var rows) ? rows : [];

    private static IReadOnlyList<ReinforcementRow> ReadTableRows(
        IReadOnlyList<string> rows)
    {
        var result = new List<ReinforcementRow>();
        foreach (var row in rows)
        {
            var values = SplitValues(row);
            if (values.Length < 10
                || !TryRoundInt(values[5], out var failureWeight)
                || !TryRoundInt(values[6], out var penaltyType)
                || !TryRoundInt(values[8], out var materialItemId)
                || !TryRoundLong(values[9], out var materialCount))
            {
                continue;
            }

            if (materialItemId is <= 0 or > ushort.MaxValue
                || materialCount is <= 0 or > uint.MaxValue)
            {
                throw new InvalidDataException(
                    $"{ScriptPath} has an invalid material row '{row}'.");
            }

            result.Add(new ReinforcementRow(
                checked((ushort)materialItemId),
                checked((uint)materialCount),
                failureWeight,
                penaltyType));
        }

        return result.AsReadOnly();
    }

    private static IReadOnlyList<int> ReadIntList(IReadOnlyList<string> rows)
    {
        var result = new List<int>();
        foreach (var row in rows)
        {
            var values = SplitValues(row);
            if (values.Length != 0
                && int.TryParse(
                    values[0],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var value))
            {
                result.Add(value);
            }
        }

        return result.AsReadOnly();
    }

    private static IReadOnlyList<double> ReadDoubleList(IReadOnlyList<string> rows)
    {
        var result = new List<double>();
        foreach (var row in rows)
        {
            var values = SplitValues(row);
            if (values.Length != 0
                && double.TryParse(
                    values[0],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var value))
            {
                result.Add(value);
            }
        }

        return result.AsReadOnly();
    }

    private static string[] SplitValues(string value) =>
        value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static bool TryRoundInt(string value, out int result)
    {
        result = 0;
        if (!double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed)
            || double.IsNaN(parsed)
            || double.IsInfinity(parsed)
            || parsed < int.MinValue
            || parsed > int.MaxValue)
        {
            return false;
        }

        result = checked((int)Math.Round(parsed, MidpointRounding.AwayFromZero));
        return true;
    }

    private static bool TryRoundLong(string value, out long result)
    {
        result = 0;
        if (!double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed)
            || double.IsNaN(parsed)
            || double.IsInfinity(parsed)
            || parsed < long.MinValue
            || parsed > long.MaxValue)
        {
            return false;
        }

        result = checked((long)Math.Round(parsed, MidpointRounding.AwayFromZero));
        return true;
    }

    private static string StripComment(string line)
    {
        var commentIndex = line.IndexOf("//", StringComparison.Ordinal);
        return (commentIndex >= 0 ? line[..commentIndex] : line).Trim();
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

    private sealed record ReinforcementRow(
        ushort MaterialItemId,
        uint MaterialCount,
        int FailureWeight,
        int PenaltyType);

    private sealed record CatalogState(
        IReadOnlyList<ReinforcementRow> Rows,
        IReadOnlyList<int> CostsByLevel,
        IReadOnlyList<double> RarityWeights,
        IReadOnlyList<double> TypeWeights,
        ReinforcementDisjointConfig? Disjoint)
    {
        public static CatalogState Empty { get; } = new([], [], [], [], null);
    }

    /// <summary>
    /// 读取 [disjoint] 段（含一层嵌套子键）。段缺失、补偿物品无效或缺关键
    /// 子键时返回 null。名称与数值同行、以空白分隔，名称可带反引号。
    /// </summary>
    private ReinforcementDisjointConfig? ReadDisjoint(
        IReadOnlyList<string> sourceLines)
    {
        var blocks = ReadDisjointBlocks(sourceLines);
        if (blocks.Count == 0
            || !TryReadDisjointInt(blocks, "disjoint bonus item", out var bonusItemId)
            || bonusItemId is <= 0 or > ushort.MaxValue
            || !TryReadDisjointInt(blocks, "upgrade const for bonus item count", out var levelBase))
        {
            return null;
        }

        TryReadDisjointInt(blocks, "equip level const", out var equipLevelConst);
        var weights = ReadDisjointMap(
            blocks,
            "upgrade failed bonus weight by rarity",
            out var weightsPresent);
        var corrections = ReadDisjointMap(
            blocks,
            "correction grade by rarity",
            out var correctionsPresent);
        if (!weightsPresent || !correctionsPresent)
        {
            return null;
        }

        return new ReinforcementDisjointConfig(
            checked((ushort)bonusItemId),
            equipLevelConst,
            levelBase,
            weights,
            corrections.ToDictionary(
                pair => pair.Key,
                pair => checked((int)Math.Round(pair.Value, MidpointRounding.AwayFromZero)),
                StringComparer.OrdinalIgnoreCase));
    }

    private static Dictionary<string, List<string>> ReadDisjointBlocks(
        IReadOnlyList<string> sourceLines)
    {
        var blocks = new Dictionary<string, List<string>>(
            StringComparer.OrdinalIgnoreCase);
        var insideDisjoint = false;
        string? current = null;
        for (var index = 0; index < sourceLines.Count; index++)
        {
            var line = StripComment(sourceLines[index]);
            if (line.Length == 0)
            {
                continue;
            }

            if (TryReadTag(line, out var tag, out var remainder))
            {
                if (insideDisjoint && tag.StartsWith("/", StringComparison.Ordinal))
                {
                    if (tag[1..].Equals("disjoint", StringComparison.OrdinalIgnoreCase))
                    {
                        return blocks;
                    }

                    if (current is not null
                        && tag[1..].Equals(current, StringComparison.OrdinalIgnoreCase))
                    {
                        current = null;
                    }

                    continue;
                }

                if (!insideDisjoint)
                {
                    insideDisjoint = tag.Equals("disjoint", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                current = tag;
                if (!blocks.TryGetValue(tag, out var rows))
                {
                    rows = [];
                    blocks[tag] = rows;
                }

                if (remainder.Length != 0)
                {
                    rows.Add(remainder);
                }

                continue;
            }

            if (insideDisjoint && current is not null)
            {
                blocks[current].Add(line);
            }
        }

        return [];
    }

    private static bool TryReadDisjointInt(
        IReadOnlyDictionary<string, List<string>> blocks,
        string key,
        out int value)
    {
        value = 0;
        if (!blocks.TryGetValue(key, out var rows)
            || rows.Count == 0)
        {
            return false;
        }

        var tokens = SplitValues(rows[0]);
        return tokens.Length != 0
            && TryRoundInt(StripNameToken(tokens[0]), out value);
    }

    private static IReadOnlyDictionary<string, double> ReadDisjointMap(
        IReadOnlyDictionary<string, List<string>> blocks,
        string key,
        out bool present)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        present = false;
        if (!blocks.TryGetValue(key, out var rows))
        {
            return result;
        }

        foreach (var row in rows)
        {
            var tokens = SplitValues(row);
            if (tokens.Length < 2)
            {
                continue;
            }

            var name = StripNameToken(tokens[0]);
            if (!double.TryParse(
                    tokens[1],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var value))
            {
                continue;
            }

            result[name] = value;
            present = true;
        }

        return result;
    }

    private static string StripNameToken(string token) => token.Trim('`');
}
