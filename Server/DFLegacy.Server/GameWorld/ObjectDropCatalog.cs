using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DFLegacy.Server;

public enum ObjectDropItemType : byte
{
    Stackable = 1,
    Equipment = 2,
    Recipe = 3,
    Artifact = 4
}

public sealed record ObjectDropProbabilityBand(
    byte MinimumLevel,
    byte MaximumLevel,
    int GoldProbability,
    int StackableProbability,
    int EquipmentProbability,
    int RecipeProbability,
    int ArtifactProbability)
{
    public int GetItemProbability(ObjectDropItemType type) => type switch
    {
        ObjectDropItemType.Stackable => StackableProbability,
        ObjectDropItemType.Equipment => EquipmentProbability,
        ObjectDropItemType.Recipe => RecipeProbability,
        ObjectDropItemType.Artifact => ArtifactProbability,
        _ => 0
    };
}

public sealed record ObjectDropLevelRange(
    byte Level,
    byte MinusLevel,
    byte PlusLevel);

public sealed record ObjectDropItemCandidate(
    ushort ItemId,
    byte Grade,
    byte Rarity,
    int CreationRate,
    bool IsArtifact);

public sealed class ObjectDropCatalog
{
    private const string ScriptPath = "etc/itemdropinfo_object.etc";
    private const string EquipmentListPath = "equipment/equipment.lst";
    private static readonly Encoding ScriptEncoding = CreateScriptEncoding();
    private static readonly Regex ListEntryPattern = new(
        @"(?m)^\s*(\d+)\s+`([^`]+)`",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private const string IntegerTagPattern = @"(?im)^\s*\[{0}\]\s+(-?\d+)";

    private readonly Lazy<CatalogState> _state;

    public ObjectDropCatalog(
        ScriptFileSystem scripts,
        ILogger<ObjectDropCatalog> logger)
    {
        _state = new Lazy<CatalogState>(
            () => Load(scripts, logger),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public int EquipmentCandidateCount => _state.Value.EquipmentCandidates.Length;

    public bool TryGetProbabilityBand(byte level, out ObjectDropProbabilityBand band)
    {
        band = _state.Value.ProbabilityBands.FirstOrDefault(candidate =>
            level >= candidate.MinimumLevel && level <= candidate.MaximumLevel)!;
        return band is not null;
    }

    public bool TryGetLevelRange(byte level, out ObjectDropLevelRange range) =>
        _state.Value.LevelRanges.TryGetValue(level, out range!);

    public double GetDifficultyMultiplier(
        int row,
        byte dungeonDifficulty)
    {
        var state = _state.Value;
        return state.DifficultyMultipliers[
            Math.Clamp(row, 0, state.DifficultyMultipliers.Length - 1)][
            Math.Clamp((int)dungeonDifficulty, 0, 3)];
    }

    public double GetMonsterTypeMultiplier(int row, byte monsterType)
    {
        var state = _state.Value;
        return state.MonsterTypeMultipliers[
            Math.Clamp(row, 0, state.MonsterTypeMultipliers.Length - 1)][
            Math.Clamp((int)monsterType, 0, 3)];
    }

    public byte RollRarity(ObjectDropItemType type, IDropRandomSource random)
    {
        var row = _state.Value.RarityThresholds[(int)type - 1];
        var roll = random.Next(10_000) + 1;
        for (var rarity = 0; rarity < row.Length; rarity++)
        {
            if (roll <= row[rarity])
            {
                return checked((byte)rarity);
            }
        }

        return checked((byte)Math.Max(0, row.Length - 1));
    }

    public bool TryChooseEquipment(
        byte generationLevel,
        byte rarity,
        IDropRandomSource random,
        out ObjectDropItemCandidate candidate)
    {
        candidate = null!;
        if (!TryGetLevelRange(generationLevel, out var range))
        {
            return false;
        }

        var minimumGrade = Math.Max(1, generationLevel - range.MinusLevel);
        var maximumGradeExclusive = Math.Min(201, generationLevel + range.PlusLevel);
        var candidates = _state.Value.EquipmentCandidates
            .Where(item =>
                item.Rarity == rarity
                && item.Grade >= minimumGrade
                && item.Grade < maximumGradeExclusive)
            .ToArray();
        var totalWeight = candidates.Sum(item => (long)item.CreationRate);
        if (totalWeight <= 0 || totalWeight > int.MaxValue)
        {
            return false;
        }

        var roll = random.Next((int)totalWeight);
        var cumulative = 0;
        foreach (var item in candidates)
        {
            cumulative += item.CreationRate;
            if (roll < cumulative)
            {
                candidate = item;
                return true;
            }
        }

        return false;
    }

    private static CatalogState Load(
        ScriptFileSystem scripts,
        ILogger logger)
    {
        var probabilityBands = DropScriptParser.ReadSection(
                scripts,
                ScriptPath,
                "drop prob",
                logger)
            .Select(ParseProbabilityBand)
            .Where(band => band is not null)
            .Cast<ObjectDropProbabilityBand>()
            .ToArray();
        var rarityThresholds = ReadRarityThresholds(scripts, logger);
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
            .Where(range => range is not null)
            .Cast<ObjectDropLevelRange>()
            .ToDictionary(range => range.Level);
        var equipmentCandidates = LoadEquipmentCandidates(scripts, logger);

        logger.LogInformation(
            "Loaded {BandCount} object-drop bands, {LevelCount} level ranges and {CandidateCount} equipment candidates from {Path}.",
            probabilityBands.Length,
            levelRanges.Count,
            equipmentCandidates.Length,
            ScriptPath);
        return new CatalogState(
            probabilityBands,
            rarityThresholds,
            difficultyMultipliers,
            monsterTypeMultipliers,
            levelRanges,
            equipmentCandidates);
    }

    private static ObjectDropProbabilityBand? ParseProbabilityBand(string[] columns)
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

        return new ObjectDropProbabilityBand(
            minimumLevel,
            maximumLevel,
            probabilities[0],
            probabilities[1],
            probabilities[2],
            probabilities[3],
            probabilities[4]);
    }

    private static ObjectDropLevelRange? ParseLevelRange(string[] columns)
    {
        return columns.Length >= 3
            && byte.TryParse(columns[0], out var level)
            && byte.TryParse(columns[1], out var minusLevel)
            && byte.TryParse(columns[2], out var plusLevel)
                ? new ObjectDropLevelRange(level, minusLevel, plusLevel)
                : null;
    }

    private static int[][] ReadRarityThresholds(
        ScriptFileSystem scripts,
        ILogger logger)
    {
        var rows = DropScriptParser.ReadSection(
            scripts,
            ScriptPath,
            "basis of rarity dicision",
            logger);
        var result = new int[4][];
        for (var rowIndex = 0; rowIndex < result.Length; rowIndex++)
        {
            result[rowIndex] = rowIndex < rows.Count
                ? rows[rowIndex]
                    .Take(5)
                    .Select(value => int.TryParse(
                        value,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var parsed)
                            ? parsed
                            : 10_000)
                    .ToArray()
                : [7_000, 9_449, 9_999, 10_000, 10_001];
        }

        return result;
    }

    private static double[][] ReadMultiplierTable(
        ScriptFileSystem scripts,
        string section,
        ILogger logger)
    {
        var rows = DropScriptParser.ReadSection(scripts, ScriptPath, section, logger);
        var result = new double[5][];
        for (var rowIndex = 0; rowIndex < result.Length; rowIndex++)
        {
            result[rowIndex] = new double[4];
            for (var columnIndex = 0; columnIndex < 4; columnIndex++)
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

    private static ObjectDropItemCandidate[] LoadEquipmentCandidates(
        ScriptFileSystem scripts,
        ILogger logger)
    {
        if (!scripts.FileExists(EquipmentListPath))
        {
            logger.LogWarning(
                "Equipment list {Path} was not found in {Source}.",
                EquipmentListPath,
                scripts.SourceDescription);
            return [];
        }

        var list = scripts.ReadAllText(EquipmentListPath, ScriptEncoding);
        var result = new List<ObjectDropItemCandidate>();
        foreach (Match entry in ListEntryPattern.Matches(list))
        {
            if (!ushort.TryParse(entry.Groups[1].Value, out var itemId))
            {
                continue;
            }

            var itemPath = $"equipment/{entry.Groups[2].Value.Replace('\\', '/').TrimStart('/')}";
            if (!scripts.FileExists(itemPath))
            {
                continue;
            }

            var text = scripts.ReadAllText(itemPath, Encoding.Latin1);
            if (!TryParseIntegerTag(text, "grade", out var grade)
                || grade is <= 0 or > byte.MaxValue
                || !TryParseIntegerTag(text, "rarity", out var rarity)
                || rarity is < 0 or > 5
                || !TryParseIntegerTag(text, "creation rate", out var creationRate)
                || creationRate <= 0)
            {
                continue;
            }

            result.Add(new ObjectDropItemCandidate(
                itemId,
                checked((byte)grade),
                checked((byte)rarity),
                creationRate,
                text.Contains("[equipment type] `[artifact", StringComparison.OrdinalIgnoreCase)));
        }

        return result.ToArray();
    }

    private static bool TryParseIntegerTag(
        string text,
        string tag,
        out int value)
    {
        value = 0;
        var pattern = string.Format(
            CultureInfo.InvariantCulture,
            IntegerTagPattern,
            Regex.Escape(tag));
        var match = Regex.Match(
            text,
            pattern,
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);
        return match.Success
            && int.TryParse(
                match.Groups[1].Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out value);
    }

    private static Encoding CreateScriptEncoding()
    {
        return PvfEncodings.Cp949();
    }

    private sealed record CatalogState(
        ObjectDropProbabilityBand[] ProbabilityBands,
        int[][] RarityThresholds,
        double[][] DifficultyMultipliers,
        double[][] MonsterTypeMultipliers,
        IReadOnlyDictionary<byte, ObjectDropLevelRange> LevelRanges,
        ObjectDropItemCandidate[] EquipmentCandidates);
}

public sealed class ObjectDropGenerator(
    ServerOptions options,
    ObjectDropCatalog objectDrops,
    GoldDropCatalog goldDrops)
{
    public bool Enabled => options.Drop.Enabled;

    public IReadOnlyList<DungeonGeneratedDrop> Generate(
        byte generationLevel,
        byte dungeonDifficulty,
        IDropRandomSource? random = null)
    {
        if (!options.Drop.Enabled
            || !objectDrops.TryGetProbabilityBand(generationLevel, out var band))
        {
            return [];
        }

        random ??= GameRandomSource.Shared;
        var result = new List<DungeonGeneratedDrop>(2);
        var ratePercent = Math.Max(0, options.Drop.RatePercent);
        if (goldDrops.TryGetLevel(generationLevel, out var gold)
            && ShouldGenerate(
                band.GoldProbability,
                objectDrops.GetDifficultyMultiplier(0, dungeonDifficulty),
                objectDrops.GetMonsterTypeMultiplier(0, 0),
                ratePercent,
                random))
        {
            var spreadRoll = gold.SpreadPercent == 0
                ? 0
                : random.Next(checked(gold.SpreadPercent * 2 + 1)) - gold.SpreadPercent;
            var amount = (long)gold.BaseAmount
                + (long)gold.BaseAmount * spreadRoll / 100;
            result.Add(new DungeonGeneratedDrop(
                IsGold: true,
                ItemId: 0,
                CountOrValue: checked((uint)Math.Max(1, amount))));
        }

        const ObjectDropItemType equipmentType = ObjectDropItemType.Equipment;
        if (ShouldGenerate(
                band.GetItemProbability(equipmentType),
                objectDrops.GetDifficultyMultiplier((int)equipmentType, dungeonDifficulty),
                objectDrops.GetMonsterTypeMultiplier((int)equipmentType, 0),
                ratePercent,
                random))
        {
            var rarity = objectDrops.RollRarity(equipmentType, random);
            if (objectDrops.TryChooseEquipment(
                    generationLevel,
                    rarity,
                    random,
                    out var equipment))
            {
                result.Add(new DungeonGeneratedDrop(
                    IsGold: false,
                    equipment.ItemId,
                    CountOrValue: 1));
            }
        }

        return result;
    }

    private bool ShouldGenerate(
        int probability,
        double difficultyMultiplier,
        double monsterTypeMultiplier,
        double ratePercent,
        IDropRandomSource random)
    {
        if (probability <= 0)
        {
            return false;
        }

        if (options.Drop.ForceDrops)
        {
            return true;
        }

        var threshold = Math.Clamp(
            (int)Math.Floor(
                probability
                * difficultyMultiplier
                * monsterTypeMultiplier
                * ratePercent
                / 100.0),
            0,
            10_000);
        return random.Next(10_000) < threshold;
    }
}
