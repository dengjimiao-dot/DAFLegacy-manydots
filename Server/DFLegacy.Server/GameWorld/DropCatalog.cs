using System.Globalization;
using System.Text;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

public sealed record WorldDropItem(ushort ItemId, int Weight);

public sealed record WorldDropLevel(
    byte Level,
    int TotalWeight,
    IReadOnlyList<WorldDropItem> Items);

public sealed class WorldDropCatalog
{
    private const string ScriptPath = "etc/worlddrop.etc";
    private readonly Lazy<IReadOnlyDictionary<byte, WorldDropLevel>> _levels;

    public WorldDropCatalog(
        ScriptFileSystem scripts,
        ILogger<WorldDropCatalog> logger)
    {
        _levels = new Lazy<IReadOnlyDictionary<byte, WorldDropLevel>>(
            () => Load(scripts, logger),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public int LevelCount => _levels.Value.Count;

    public bool TryGetLevel(byte level, out WorldDropLevel definition) =>
        _levels.Value.TryGetValue(level, out definition!);

    private static IReadOnlyDictionary<byte, WorldDropLevel> Load(
        ScriptFileSystem scripts,
        ILogger logger)
    {
        var levels = new Dictionary<byte, WorldDropLevel>();
        foreach (var columns in DropScriptParser.ReadSection(
                     scripts,
                     ScriptPath,
                     "world drop",
                     logger))
        {
            if (columns.Length < 3
                || !byte.TryParse(columns[0], out var level))
            {
                continue;
            }

            var items = new List<WorldDropItem>();
            var totalWeight = 0;
            for (var index = 2; index < columns.Length; index += 2)
            {
                if (!int.TryParse(
                        columns[index],
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var rawItemId)
                    || rawItemId == -1)
                {
                    break;
                }

                if (index + 1 >= columns.Length
                    || rawItemId is <= 0 or > ushort.MaxValue
                    || !int.TryParse(
                        columns[index + 1],
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var weight)
                    || weight <= 0)
                {
                    continue;
                }

                totalWeight = checked(totalWeight + weight);
                items.Add(new WorldDropItem(checked((ushort)rawItemId), weight));
            }

            levels[level] = new WorldDropLevel(level, totalWeight, items);
        }

        logger.LogInformation(
            "Loaded {LevelCount} world-drop levels from {Path}.",
            levels.Count,
            ScriptPath);
        return levels;
    }
}

public sealed record GoldDropLevel(byte Level, uint BaseAmount, byte SpreadPercent);

public sealed class GoldDropCatalog
{
    private const string ScriptPath = "etc/itemdropinfo_common.etc";
    private readonly Lazy<IReadOnlyDictionary<byte, GoldDropLevel>> _levels;

    public GoldDropCatalog(
        ScriptFileSystem scripts,
        ILogger<GoldDropCatalog> logger)
    {
        _levels = new Lazy<IReadOnlyDictionary<byte, GoldDropLevel>>(
            () => Load(scripts, logger),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public int LevelCount => _levels.Value.Count;

    public bool TryGetLevel(byte level, out GoldDropLevel definition) =>
        _levels.Value.TryGetValue(level, out definition!);

    private static IReadOnlyDictionary<byte, GoldDropLevel> Load(
        ScriptFileSystem scripts,
        ILogger logger)
    {
        var levels = new Dictionary<byte, GoldDropLevel>();
        foreach (var columns in DropScriptParser.ReadSection(
                     scripts,
                     ScriptPath,
                     "gold drop ref table",
                     logger))
        {
            if (columns.Length < 3
                || !byte.TryParse(columns[0], out var level)
                || !uint.TryParse(
                    columns[1],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var baseAmount)
                || !byte.TryParse(columns[2], out var spreadPercent))
            {
                continue;
            }

            levels[level] = new GoldDropLevel(level, baseAmount, spreadPercent);
        }

        logger.LogInformation(
            "Loaded {LevelCount} gold-drop amount levels from {Path}.",
            levels.Count,
            ScriptPath);
        return levels;
    }
}

public enum MonsterDropKind : byte
{
    Gold = 0,
    Consumable = 1,
    Equipment = 2,
    Recipe = 3,
    Artifact = 4
}

public sealed record MonsterDropProbabilityBand(
    byte MinimumLevel,
    byte MaximumLevel,
    int GoldProbability,
    int ConsumableProbability,
    int EquipmentProbability,
    int RecipeProbability,
    int ArtifactProbability)
{
    public int GetProbability(MonsterDropKind kind) => kind switch
    {
        MonsterDropKind.Gold => GoldProbability,
        MonsterDropKind.Consumable => ConsumableProbability,
        MonsterDropKind.Equipment => EquipmentProbability,
        MonsterDropKind.Recipe => RecipeProbability,
        MonsterDropKind.Artifact => ArtifactProbability,
        _ => 0
    };
}

public sealed record MonsterDropLevelRange(
    byte Level,
    byte MinusLevel,
    byte PlusLevel);

public sealed class MonsterDropCatalog
{
    private const string ScriptPath = "etc/itemdropinfo_monseter.etc";
    private static readonly MonsterDropKind[] ItemKinds =
    [
        MonsterDropKind.Consumable,
        MonsterDropKind.Equipment,
        MonsterDropKind.Recipe,
        MonsterDropKind.Artifact
    ];
    private readonly Lazy<CatalogState> _state;

    public MonsterDropCatalog(
        ScriptFileSystem scripts,
        ItemCatalog items,
        ILogger<MonsterDropCatalog> logger)
    {
        _state = new Lazy<CatalogState>(
            () => Load(scripts, items, logger),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public int ProbabilityBandCount => _state.Value.ProbabilityBands.Length;

    public int LevelRangeCount => _state.Value.LevelRanges.Count;

    public int EquipmentCandidateCount =>
        GetCandidateCount(MonsterDropKind.Equipment);

    public int WeightedTableCount =>
        GetWeightedTableCount(MonsterDropKind.Equipment);

    public void Initialize() => _ = _state.Value;

    public bool TryGetProbabilityBand(
        byte level,
        out MonsterDropProbabilityBand band)
    {
        band = _state.Value.ProbabilityBands.FirstOrDefault(candidate =>
            level >= candidate.MinimumLevel && level <= candidate.MaximumLevel)!;
        return band is not null;
    }

    public int GetProbability(byte level, MonsterDropKind kind) =>
        TryGetProbabilityBand(level, out var band)
            ? band.GetProbability(kind)
            : 0;

    public int GetGoldProbability(byte level) =>
        GetProbability(level, MonsterDropKind.Gold);

    public bool TryGetLevelRange(byte level, out MonsterDropLevelRange range) =>
        _state.Value.LevelRanges.TryGetValue(level, out range!);

    public IReadOnlyList<int> GetRarityThresholds(MonsterDropKind kind)
    {
        if (kind == MonsterDropKind.Gold)
        {
            return [];
        }

        return _state.Value.RarityThresholds[(int)kind - 1];
    }

    public int GetCandidateCount(MonsterDropKind kind) =>
        _state.Value.ItemPools.TryGetValue(kind, out var pool)
            ? pool.CandidateCount
            : 0;

    public int GetWeightedTableCount(MonsterDropKind kind) =>
        _state.Value.ItemPools.TryGetValue(kind, out var pool)
            ? pool.WeightedTables.Count
            : 0;

    public bool HasItemPool(MonsterDropKind kind) =>
        _state.Value.ItemPools.ContainsKey(kind);

    public bool IsItemCandidate(MonsterDropKind kind, ushort itemId) =>
        _state.Value.ItemPools.TryGetValue(kind, out var pool)
        && pool.CandidateIds.Contains(itemId);

    public double GetMultiplier(
        MonsterDropKind kind,
        int partyMemberCount,
        byte dungeonDifficulty,
        byte monsterType)
    {
        var state = _state.Value;
        var rowIndex = Math.Clamp((int)kind, 0, 4);
        var partyIndex = Math.Clamp(partyMemberCount, 1, 4) - 1;
        var difficultyIndex = Math.Clamp((int)dungeonDifficulty, 0, 3);
        var monsterTypeIndex = Math.Clamp((int)monsterType, 0, 3);
        return state.PartyMultipliers[rowIndex][partyIndex]
            * state.DifficultyMultipliers[rowIndex][difficultyIndex]
            * state.MonsterTypeMultipliers[rowIndex][monsterTypeIndex];
    }

    public double GetGoldMultiplier(
        int partyMemberCount,
        byte dungeonDifficulty,
        bool isBoss) =>
        GetMultiplier(
            MonsterDropKind.Gold,
            partyMemberCount,
            dungeonDifficulty,
            isBoss ? GameDungeonMonsterTypes.Boss : GameDungeonMonsterTypes.Normal);

    public byte RollRarity(MonsterDropKind kind, IDropRandomSource random)
    {
        if (kind == MonsterDropKind.Gold)
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        var thresholds = _state.Value.RarityThresholds[(int)kind - 1];
        var roll = random.Next(10_000) + 1;
        for (var rarity = 0; rarity < thresholds.Length; rarity++)
        {
            if (roll <= thresholds[rarity])
            {
                return checked((byte)rarity);
            }
        }

        return checked((byte)Math.Max(0, thresholds.Length - 1));
    }

    public bool TryGetWeightedTableStats(
        byte generationLevel,
        byte rarity,
        out int candidateCount,
        out int totalWeight) =>
        TryGetWeightedTableStats(
            MonsterDropKind.Equipment,
            generationLevel,
            rarity,
            out candidateCount,
            out totalWeight);

    public bool TryGetWeightedTableStats(
        MonsterDropKind kind,
        byte generationLevel,
        byte rarity,
        out int candidateCount,
        out int totalWeight)
    {
        if (_state.Value.ItemPools.TryGetValue(kind, out var pool)
            && pool.WeightedTables.TryGetValue(
                (generationLevel, rarity),
                out var table))
        {
            candidateCount = table.Entries.Length;
            totalWeight = table.TotalWeight;
            return true;
        }

        candidateCount = 0;
        totalWeight = 0;
        return false;
    }

    public bool TryChooseEquipment(
        byte generationLevel,
        byte rarity,
        IDropRandomSource random,
        out ushort itemId) =>
        TryChooseItem(
            MonsterDropKind.Equipment,
            generationLevel,
            rarity,
            random,
            out itemId);

    public bool TryChooseItem(
        MonsterDropKind kind,
        byte generationLevel,
        byte rarity,
        IDropRandomSource random,
        out ushort itemId)
    {
        if (!_state.Value.ItemPools.TryGetValue(kind, out var pool)
            || !pool.WeightedTables.TryGetValue(
                (generationLevel, rarity),
                out var table))
        {
            itemId = 0;
            return false;
        }

        return WeightedItemTableBuilder.TryChoose(table, random, out itemId);
    }

    public bool TryChooseItem(
        MonsterDropKind kind,
        byte generationLevel,
        byte rarity,
        byte minusLevel,
        byte plusLevel,
        IDropRandomSource random,
        out ushort itemId)
    {
        itemId = 0;
        if (!_state.Value.ItemPools.TryGetValue(kind, out var pool))
        {
            return false;
        }

        var minimumGrade = Math.Max(1, generationLevel - minusLevel);
        var maximumGradeExclusive = Math.Min(256, generationLevel + plusLevel);
        var candidates = pool.Candidates
            .Where(candidate => candidate.Rarity == rarity
                && candidate.Grade >= minimumGrade
                && candidate.Grade < maximumGradeExclusive)
            .ToArray();
        var totalWeight = candidates.Aggregate(
            0L,
            (total, candidate) => total + candidate.CreationRate);
        if (totalWeight <= 0)
        {
            return false;
        }

        var selection = totalWeight <= int.MaxValue
            ? random.Next(checked((int)totalWeight))
            : (long)((ulong)random.Next(int.MaxValue) * (ulong)totalWeight
                / int.MaxValue);
        var cumulative = 0L;
        foreach (var candidate in candidates)
        {
            cumulative += candidate.CreationRate;
            if (selection < cumulative)
            {
                itemId = candidate.ItemId;
                return true;
            }
        }

        return false;
    }

    private static CatalogState Load(
        ScriptFileSystem scripts,
        ItemCatalog items,
        ILogger logger)
    {
        var probabilityBands = DropScriptParser.ReadSection(
                scripts,
                ScriptPath,
                "drop prob",
                logger)
            .Select(ParseProbabilityBand)
            .Where(band => band is not null)
            .Cast<MonsterDropProbabilityBand>()
            .ToArray();
        var rarityThresholds = ReadRarityThresholds(scripts, logger);
        var partyMultipliers = ReadMultiplierTable(
            scripts,
            "party member drop bonusrate",
            logger);
        var difficultyMultipliers = ReadMultiplierTable(
            scripts,
            "dungeon difficulty drop bonusrate",
            logger);
        var monsterTypeMultipliers = ReadMultiplierTable(
            scripts,
            "monster type drop bonusrate",
            logger);
        var levelRanges = DropScriptParser.ReadSection(
                scripts,
                ScriptPath,
                "item drop ref table",
                logger)
            .Select(ParseLevelRange)
            .Where(range => range is not null && range.Level is >= 1 and <= 99)
            .Cast<MonsterDropLevelRange>()
            .ToDictionary(range => range.Level);
        var weightedLevelRanges = levelRanges.Values
            .Select(range => new WeightedItemLevelRange(
                range.Level,
                range.MinusLevel,
                range.PlusLevel))
            .ToArray();
        var itemPools = new Dictionary<MonsterDropKind, ItemPool>();
        foreach (var kind in ItemKinds)
        {
            var candidates = LoadItemCandidates(items, kind);
            itemPools.Add(
                kind,
                new ItemPool(
                    candidates.Length,
                    candidates.Select(candidate => candidate.ItemId).ToHashSet(),
                    candidates,
                    WeightedItemTableBuilder.Build(
                        weightedLevelRanges,
                        candidates,
                        maximumRarity: 4)));
        }

        logger.LogInformation(
            "Loaded {BandCount} monster-drop probability bands and {LevelCount} level ranges from {Path}; prebuilt weighted pools: consumable={ConsumableCandidateCount}/{ConsumableTableCount}, equipment={EquipmentCandidateCount}/{EquipmentTableCount}, recipe={RecipeCandidateCount}/{RecipeTableCount}, artifact={ArtifactCandidateCount}/{ArtifactTableCount} candidates/tables.",
            probabilityBands.Length,
            levelRanges.Count,
            ScriptPath,
            itemPools[MonsterDropKind.Consumable].CandidateCount,
            itemPools[MonsterDropKind.Consumable].WeightedTables.Count,
            itemPools[MonsterDropKind.Equipment].CandidateCount,
            itemPools[MonsterDropKind.Equipment].WeightedTables.Count,
            itemPools[MonsterDropKind.Recipe].CandidateCount,
            itemPools[MonsterDropKind.Recipe].WeightedTables.Count,
            itemPools[MonsterDropKind.Artifact].CandidateCount,
            itemPools[MonsterDropKind.Artifact].WeightedTables.Count);
        return new CatalogState(
            probabilityBands,
            rarityThresholds,
            partyMultipliers,
            difficultyMultipliers,
            monsterTypeMultipliers,
            levelRanges,
            itemPools);
    }

    private static MonsterDropProbabilityBand? ParseProbabilityBand(string[] columns)
    {
        if (columns.Length < 7
            || !byte.TryParse(columns[0], out var minimumLevel)
            || !byte.TryParse(columns[1], out var maximumLevel))
        {
            return null;
        }

        var probabilities = new int[5];
        for (var index = 0; index < probabilities.Length; index++)
        {
            if (!int.TryParse(
                    columns[index + 2],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out probabilities[index]))
            {
                return null;
            }
        }

        return new MonsterDropProbabilityBand(
            minimumLevel,
            maximumLevel,
            probabilities[0],
            probabilities[1],
            probabilities[2],
            probabilities[3],
            probabilities[4]);
    }

    private static MonsterDropLevelRange? ParseLevelRange(string[] columns) =>
        columns.Length >= 3
        && byte.TryParse(columns[0], out var level)
        && byte.TryParse(columns[1], out var minusLevel)
        && byte.TryParse(columns[2], out var plusLevel)
            ? new MonsterDropLevelRange(level, minusLevel, plusLevel)
            : null;

    private static int[][] ReadRarityThresholds(
        ScriptFileSystem scripts,
        ILogger logger)
    {
        var rows = DropScriptParser.ReadSection(
            scripts,
            ScriptPath,
            "basis of rarity dicision",
            logger);
        int[] fallback = [7_000, 9_449, 9_999, 10_000, 10_001];
        var result = new int[4][];
        for (var rowIndex = 0; rowIndex < result.Length; rowIndex++)
        {
            result[rowIndex] = new int[5];
            for (var columnIndex = 0; columnIndex < result[rowIndex].Length; columnIndex++)
            {
                result[rowIndex][columnIndex] = rowIndex < rows.Count
                    && columnIndex < rows[rowIndex].Length
                    && int.TryParse(
                        rows[rowIndex][columnIndex],
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var value)
                            ? value
                            : fallback[columnIndex];
            }
        }

        return result;
    }

    private static double[][] ReadMultiplierTable(
        ScriptFileSystem scripts,
        string section,
        ILogger logger)
    {
        var rows = DropScriptParser.ReadSection(
                scripts,
                ScriptPath,
                section,
                logger);
        var result = new double[5][];
        for (var rowIndex = 0; rowIndex < result.Length; rowIndex++)
        {
            result[rowIndex] = new double[4];
            for (var columnIndex = 0; columnIndex < result[rowIndex].Length; columnIndex++)
            {
                result[rowIndex][columnIndex] = rowIndex < rows.Count
                    && columnIndex < rows[rowIndex].Length
                    && double.TryParse(
                        rows[rowIndex][columnIndex],
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var value)
                            ? value
                            : 1;
            }
        }

        return result;
    }

    private static WeightedItemCandidate[] LoadItemCandidates(
        ItemCatalog items,
        MonsterDropKind kind)
    {
        var result = new List<WeightedItemCandidate>();
        foreach (var definition in items.Definitions.Values)
        {
            if (definition.Grade is not { } grade
                || grade is <= 0 or > byte.MaxValue
                || definition.Rarity is not { } rarity
                || rarity is < 0 or > 4
                || definition.CreationRate is not { } creationRate
                || creationRate <= 0
                || !MatchesItemKind(definition, kind))
            {
                continue;
            }

            result.Add(new WeightedItemCandidate(
                definition.Id,
                checked((byte)grade),
                checked((byte)rarity),
                creationRate));
        }

        return result.ToArray();
    }

    private static bool MatchesItemKind(
        ItemDefinition definition,
        MonsterDropKind kind) =>
        kind switch
        {
            MonsterDropKind.Consumable =>
                definition.ScriptKind == ItemScriptKind.Stackable
                && definition.TypeTag != "recipe",
            MonsterDropKind.Equipment =>
                definition.ScriptKind == ItemScriptKind.Equipment
                && definition.InventoryCategory == ItemInventoryCategory.Equipment,
            MonsterDropKind.Recipe =>
                definition.ScriptKind == ItemScriptKind.Stackable
                && definition.TypeTag == "recipe",
            MonsterDropKind.Artifact =>
                definition.ScriptKind == ItemScriptKind.Equipment
                && definition.TypeTag is "artifact red" or "artifact blue" or "artifact green",
            _ => false
        };

    private sealed record CatalogState(
        MonsterDropProbabilityBand[] ProbabilityBands,
        int[][] RarityThresholds,
        double[][] PartyMultipliers,
        double[][] DifficultyMultipliers,
        double[][] MonsterTypeMultipliers,
        IReadOnlyDictionary<byte, MonsterDropLevelRange> LevelRanges,
        IReadOnlyDictionary<MonsterDropKind, ItemPool> ItemPools);

    private sealed record ItemPool(
        int CandidateCount,
        IReadOnlySet<ushort> CandidateIds,
        WeightedItemCandidate[] Candidates,
        IReadOnlyDictionary<
            (byte Level, byte Rarity),
            WeightedItemTable> WeightedTables);
}

public sealed record DungeonGeneratedDrop(
    bool IsGold,
    ushort ItemId,
    uint CountOrValue);

public sealed class DungeonDropGenerator(
    ServerOptions options,
    WorldDropCatalog worldDrops,
    GoldDropCatalog goldDrops,
    MonsterDropCatalog monsterDrops)
{
    private static readonly MonsterDropKind[] ItemKinds =
    [
        MonsterDropKind.Consumable,
        MonsterDropKind.Equipment,
        MonsterDropKind.Recipe,
        MonsterDropKind.Artifact
    ];

    public bool Enabled => options.Drop.Enabled;

    public IReadOnlyList<DungeonGeneratedDrop> Generate(
        byte monsterLevel,
        bool isBoss,
        byte dungeonDifficulty = 0,
        int partyMemberCount = 1,
        IDropRandomSource? random = null) =>
        Generate(
            monsterLevel,
            isBoss ? GameDungeonMonsterTypes.Boss : GameDungeonMonsterTypes.Normal,
            dungeonDifficulty,
            partyMemberCount,
            random);

    public IReadOnlyList<DungeonGeneratedDrop> Generate(
        byte monsterLevel,
        byte monsterType,
        byte dungeonDifficulty = 0,
        int partyMemberCount = 1,
        IDropRandomSource? random = null)
    {
        if (!options.Drop.Enabled)
        {
            return [];
        }

        random ??= GameRandomSource.Shared;
        var result = new List<DungeonGeneratedDrop>(6);
        if (goldDrops.TryGetLevel(monsterLevel, out var gold)
            && ShouldDrop(
                MonsterDropKind.Gold,
                monsterLevel,
                monsterType,
                dungeonDifficulty,
                partyMemberCount,
                random))
        {
            var spread = gold.SpreadPercent;
            var spreadRoll = spread == 0
                ? 0
                : random.Next(checked(spread * 2 + 1)) - spread;
            var signedAmount = (long)gold.BaseAmount
                + (long)gold.BaseAmount * spreadRoll / 100;
            result.Add(new DungeonGeneratedDrop(
                IsGold: true,
                ItemId: 0,
                CountOrValue: checked((uint)Math.Max(1, signedAmount))));
        }

        foreach (var kind in ItemKinds)
        {
            if (!ShouldDrop(
                    kind,
                    monsterLevel,
                    monsterType,
                    dungeonDifficulty,
                    partyMemberCount,
                    random))
            {
                continue;
            }

            var rarity = monsterDrops.RollRarity(kind, random);
            if (!monsterDrops.TryChooseItem(
                    kind,
                    monsterLevel,
                    rarity,
                    random,
                    out var itemId)
                && (rarity == 0
                    || !monsterDrops.TryChooseItem(
                        kind,
                        monsterLevel,
                        rarity: 0,
                        random,
                        out itemId)))
            {
                continue;
            }

            result.Add(new DungeonGeneratedDrop(
                IsGold: false,
                itemId,
                CountOrValue: 1));
        }

        if (worldDrops.TryGetLevel(monsterLevel, out var world)
            && world.TotalWeight > 0
            && (options.Drop.ForceDrops
                || random.Next(100_000) < Math.Clamp(
                    (int)Math.Floor(
                        world.TotalWeight * options.Drop.RatePercent / 100.0),
                    0,
                    100_000)))
        {
            var selection = random.Next(world.TotalWeight);
            var cumulative = 0;
            foreach (var item in world.Items)
            {
                cumulative += item.Weight;
                if (selection < cumulative)
                {
                    result.Add(new DungeonGeneratedDrop(
                        IsGold: false,
                        item.ItemId,
                        CountOrValue: 1));
                    break;
                }
            }
        }

        return result;
    }

    private bool ShouldDrop(
        MonsterDropKind kind,
        byte monsterLevel,
        byte monsterType,
        byte dungeonDifficulty,
        int partyMemberCount,
        IDropRandomSource random)
    {
        var probability = monsterDrops.GetProbability(monsterLevel, kind);
        if (probability <= 0)
        {
            return false;
        }

        if (options.Drop.ForceDrops)
        {
            return true;
        }

        var multiplier = monsterDrops.GetMultiplier(
            kind,
            partyMemberCount,
            dungeonDifficulty,
            monsterType);
        var threshold = Math.Clamp(
            (int)Math.Floor(
                probability
                * multiplier
                * options.Drop.RatePercent
                / 100.0
                * options.Drop.EconomicRate),
            0,
            10_000);
        return random.Next(10_000) < threshold;
    }
}

internal static class DropScriptParser
{
    public static IReadOnlyList<string[]> ReadSection(
        ScriptFileSystem scripts,
        string path,
        string section,
        ILogger logger)
    {
        if (!scripts.FileExists(path))
        {
            logger.LogWarning(
                "Drop script {Path} was not found in {Source}.",
                path,
                scripts.SourceDescription);
            return [];
        }

        var rows = new List<string[]>();
        var inSection = false;
        foreach (var rawLine in scripts.ReadLines(path, Encoding.Latin1))
        {
            var line = rawLine.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inSection = string.Equals(
                    line[1..^1].Trim(),
                    section,
                    StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inSection)
            {
                continue;
            }

            var commentIndex = line.IndexOf("//", StringComparison.Ordinal);
            if (commentIndex >= 0)
            {
                line = line[..commentIndex];
            }

            var columns = line.Split(
                [' ', '\t'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (columns.Length > 0)
            {
                rows.Add(columns);
            }
        }

        return rows;
    }
}
