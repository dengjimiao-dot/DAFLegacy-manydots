using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DFLegacy.Server;

public enum ClearRewardProbabilityCategory : byte
{
    Default = 0,
    GoldCard = 1,
    Event = 2,
    PremiumDefault = 3,
    PremiumGoldCard = 4,
    PremiumBonus = 5
}

public sealed record ClearRewardProbabilityBand(
    byte MinimumLevel,
    byte MaximumLevel,
    int Probability);

public sealed record ClearRewardLevelRange(
    byte Level,
    byte MinusLevel,
    byte PlusLevel);

public sealed record DungeonClearRewardRoll(
    uint GoldCardCost,
    bool GoldCardEnabled,
    uint FreeGoldAmount,
    ushort? FreeItemId,
    ushort? PremiumItemId,
    ushort? GoldItemId);

public sealed class ClearRewardCatalog
{
    private const string ScriptPath = "etc/itemdropinfo_clearreward.etc";
    private const string EquipmentListPath = "equipment/equipment.lst";
    private const string IntegerTagPattern = @"(?im)^\s*\[{0}\]\s+(-?\d+)";
    private static readonly Encoding ScriptEncoding = CreateScriptEncoding();
    private static readonly Regex ListEntryPattern = new(
        @"(?m)^\s*(\d+)\s+`([^`]+)`",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly CatalogState _state;

    public ClearRewardCatalog(
        ScriptFileSystem scripts,
        ILogger<ClearRewardCatalog> logger)
    {
        _state = Load(scripts, logger);
    }

    public int EquipmentCandidateCount => _state.EquipmentCandidateCount;

    public int WeightedTableCount => _state.WeightedTables.Count;

    public uint GetGoldCardCost(byte level) =>
        _state.GoldCardCosts.TryGetValue(level, out var cost)
            ? cost
            : 0;

    public double GetGoldMultiplier(
        byte dungeonDifficulty,
        int partyMemberCount)
    {
        var state = _state;
        return state.DifficultyGoldMultipliers[
                Math.Clamp((int)dungeonDifficulty, 0, 3)]
            * state.PartyMultipliers[
                Math.Clamp(partyMemberCount, 1, 4) - 1];
    }

    public bool TryGetWeightedTableStats(
        byte generationLevel,
        byte rarity,
        out int candidateCount,
        out int totalWeight)
    {
        if (_state.WeightedTables.TryGetValue(
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

    public ushort? GenerateEquipment(
        ClearRewardProbabilityCategory category,
        byte generationLevel,
        byte dungeonDifficulty,
        int partyMemberCount,
        IDropRandomSource random)
    {
        var state = _state;
        var band = state.ProbabilityBands[(int)category].FirstOrDefault(candidate =>
            generationLevel >= candidate.MinimumLevel
            && generationLevel <= candidate.MaximumLevel);
        if (band is null)
        {
            return null;
        }

        var partyMultiplier = state.PartyMultipliers[
            Math.Clamp(partyMemberCount, 1, 4) - 1];
        var difficultyBonus = state.DifficultyBonuses[
            Math.Clamp((int)dungeonDifficulty, 0, 3)];
        var probability = band.Probability * partyMultiplier + difficultyBonus;
        if (category is ClearRewardProbabilityCategory.GoldCard
            or ClearRewardProbabilityCategory.PremiumGoldCard)
        {
            probability *= state.GoldCardCreateRate;
        }

        if (random.Next(10_000) >= Math.Clamp((int)probability, 0, 10_000))
        {
            return null;
        }

        // DFLegacy can describe consumable, equipment, recipe and artifact
        // branches. This emulator intentionally exposes only the equipment
        // branch requested by the legacy client integration; every other roll
        // is a valid blank card.
        if (random.Next(10_000) >= state.EquipmentTypeProbability)
        {
            return null;
        }

        var rarityRoll = random.Next(10_000) + 1;
        byte rarity = 0;
        for (; rarity < state.RarityThresholds.Length; rarity++)
        {
            if (rarityRoll <= state.RarityThresholds[rarity])
            {
                break;
            }
        }

        rarity = checked((byte)Math.Min(
            rarity,
            state.RarityThresholds.Length - 1));
        if (!state.WeightedTables.TryGetValue(
                (generationLevel, rarity),
                out var weightedTable))
        {
            return null;
        }

        return WeightedItemTableBuilder.TryChoose(
            weightedTable,
            random,
            out var itemId)
                ? itemId
                : null;
    }

    private static CatalogState Load(
        ScriptFileSystem scripts,
        ILogger logger)
    {
        var probabilityBands = ReadProbabilityBands(scripts, logger);
        var equipmentTypeProbability = ReadIntegerRow(
                scripts,
                "drop item type prob",
                logger,
                [0, 10_000, 0, 0])
            .ElementAtOrDefault(1);
        var difficultyBonuses = ReadIntegerRow(
            scripts,
            "dungeon difficulty drop bonusrate",
            logger,
            [0, 0, 0, 0]);
        var rarityThresholds = ReadIntegerRow(
            scripts,
            "basis of rarity dicision",
            logger,
            [8_000, 9_799, 9_999, 10_000, 10_001]);
        var partyMultipliers = ReadDoubleRow(
            scripts,
            "party member drop bonusrate",
            logger,
            [1, 1, 1, 1]);
        var difficultyGoldMultipliers = ReadDoubleRow(
            scripts,
            "dungeon difficulty gold drop bonusrate",
            logger,
            [1, 1, 1, 1]);
        var goldCardCreateRate = ReadDoubleRow(
                scripts,
                "gold card create rate",
                logger,
                [1])
            .FirstOrDefault(1);
        var goldCardCosts = DropScriptParser.ReadSection(
                scripts,
                ScriptPath,
                "gold card cost table",
                logger)
            .Where(columns => columns.Length >= 2)
            .Select(columns =>
                byte.TryParse(columns[0], out var level)
                && uint.TryParse(
                    columns[1],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var cost)
                    ? new KeyValuePair<byte, uint>(level, cost)
                    : default)
            .Where(pair => pair.Key != 0)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        var levelRanges = DropScriptParser.ReadSection(
                scripts,
                ScriptPath,
                "item drop ref table",
                logger)
            .Where(columns => columns.Length >= 3)
            .Select(columns =>
                byte.TryParse(columns[0], out var level)
                && byte.TryParse(columns[1], out var minusLevel)
                && byte.TryParse(columns[2], out var plusLevel)
                    ? new ClearRewardLevelRange(level, minusLevel, plusLevel)
                    : null)
            .Where(range => range is not null)
            .Cast<ClearRewardLevelRange>()
            .ToDictionary(range => range.Level);
        var candidates = LoadEquipmentCandidates(scripts, logger);
        var weightedTables = WeightedItemTableBuilder.Build(
            levelRanges.Values.Select(range => new WeightedItemLevelRange(
                range.Level,
                range.MinusLevel,
                range.PlusLevel)),
            candidates,
            maximumRarity: 5);

        logger.LogInformation(
            "Loaded {BandCount} clear-reward probability bands and prebuilt {TableCount} level/rarity tables from {CandidateCount} weighted equipment candidates in {Path}.",
            probabilityBands.Sum(bands => bands.Length),
            weightedTables.Count,
            candidates.Length,
            ScriptPath);
        return new CatalogState(
            probabilityBands,
            Math.Clamp(equipmentTypeProbability, 0, 10_000),
            Pad(difficultyBonuses, 4, 0),
            rarityThresholds.Length == 0
                ? [8_000, 9_799, 9_999, 10_000, 10_001]
                : rarityThresholds,
            Pad(partyMultipliers, 4, 1),
            Pad(difficultyGoldMultipliers, 4, 1),
            Math.Max(0, goldCardCreateRate),
            goldCardCosts,
            candidates.Length,
            weightedTables);
    }

    private static ClearRewardProbabilityBand[][] ReadProbabilityBands(
        ScriptFileSystem scripts,
        ILogger logger)
    {
        var result = Enumerable.Range(0, 6)
            .Select(_ => new List<ClearRewardProbabilityBand>())
            .ToArray();
        var category = -1;
        foreach (var columns in DropScriptParser.ReadSection(
                     scripts,
                     ScriptPath,
                     "drop prob",
                     logger))
        {
            if (columns[0].StartsWith('`'))
            {
                category++;
                continue;
            }

            if (category is < 0 or >= 6
                || columns.Length < 3
                || !byte.TryParse(columns[0], out var minimumLevel)
                || !byte.TryParse(columns[1], out var maximumLevel)
                || !int.TryParse(
                    columns[2],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var probability))
            {
                continue;
            }

            result[category].Add(new ClearRewardProbabilityBand(
                minimumLevel,
                maximumLevel,
                probability));
        }

        return result.Select(bands => bands.ToArray()).ToArray();
    }

    private static int[] ReadIntegerRow(
        ScriptFileSystem scripts,
        string section,
        ILogger logger,
        int[] fallback)
    {
        var row = DropScriptParser.ReadSection(scripts, ScriptPath, section, logger)
            .FirstOrDefault();
        if (row is null)
        {
            return fallback;
        }

        return row
            .Select(value => int.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsed)
                    ? parsed
                    : 0)
            .ToArray();
    }

    private static double[] ReadDoubleRow(
        ScriptFileSystem scripts,
        string section,
        ILogger logger,
        double[] fallback)
    {
        var row = DropScriptParser.ReadSection(scripts, ScriptPath, section, logger)
            .FirstOrDefault();
        if (row is null)
        {
            return fallback;
        }

        return row
            .Select(value => double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed)
                    ? parsed
                    : 0)
            .ToArray();
    }

    private static WeightedItemCandidate[] LoadEquipmentCandidates(
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
        var result = new List<WeightedItemCandidate>();
        foreach (Match entry in ListEntryPattern.Matches(list))
        {
            if (!ushort.TryParse(entry.Groups[1].Value, out var itemId))
            {
                continue;
            }

            var relativePath = entry.Groups[2].Value
                .Replace('\\', '/')
                .TrimStart('/');
            if (!relativePath.EndsWith(".equ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var itemPath = $"equipment/{relativePath}";
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

            result.Add(new WeightedItemCandidate(
                itemId,
                checked((byte)grade),
                checked((byte)rarity),
                creationRate));
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

    private static T[] Pad<T>(T[] values, int count, T fallback)
    {
        var result = Enumerable.Repeat(fallback, count).ToArray();
        Array.Copy(values, result, Math.Min(values.Length, count));
        return result;
    }

    private static Encoding CreateScriptEncoding()
    {
        return PvfEncodings.Cp949();
    }

    private sealed record CatalogState(
        ClearRewardProbabilityBand[][] ProbabilityBands,
        int EquipmentTypeProbability,
        int[] DifficultyBonuses,
        int[] RarityThresholds,
        double[] PartyMultipliers,
        double[] DifficultyGoldMultipliers,
        double GoldCardCreateRate,
        IReadOnlyDictionary<byte, uint> GoldCardCosts,
        int EquipmentCandidateCount,
        IReadOnlyDictionary<
            (byte Level, byte Rarity),
            WeightedItemTable> WeightedTables);
}

public sealed class ClearRewardGenerator(
    ClearRewardCatalog catalog,
    GoldDropCatalog goldDrops)
{
    public DungeonClearRewardRoll Generate(
        byte generationLevel,
        byte dungeonDifficulty,
        bool isPremium,
        bool goldCardEnabled,
        int partyMemberCount = 1,
        int normalMonsterKills = 0,
        int championMonsterKills = 0,
        int bossMonsterKills = 0,
        IDropRandomSource? random = null)
    {
        random ??= GameRandomSource.Shared;
        var freeCategory = isPremium
            ? ClearRewardProbabilityCategory.PremiumDefault
            : ClearRewardProbabilityCategory.Default;
        var goldCategory = isPremium
            ? ClearRewardProbabilityCategory.PremiumGoldCard
            : ClearRewardProbabilityCategory.GoldCard;
        var freeItem = catalog.GenerateEquipment(
            freeCategory,
            generationLevel,
            dungeonDifficulty,
            partyMemberCount,
            random);
        var premiumItem = isPremium
            ? catalog.GenerateEquipment(
                ClearRewardProbabilityCategory.PremiumBonus,
                generationLevel,
                dungeonDifficulty,
                partyMemberCount,
                random)
            : null;
        var goldItem = goldCardEnabled
            ? catalog.GenerateEquipment(
                goldCategory,
                generationLevel,
                dungeonDifficulty,
                partyMemberCount,
                random)
            : null;
        var freeGoldAmount = GenerateClearGold(
            generationLevel,
            dungeonDifficulty,
            partyMemberCount,
            normalMonsterKills,
            championMonsterKills,
            bossMonsterKills,
            random);

        return new DungeonClearRewardRoll(
            goldCardEnabled ? catalog.GetGoldCardCost(generationLevel) : 0,
            goldCardEnabled,
            freeGoldAmount,
            freeItem,
            premiumItem,
            goldItem);
    }

    private uint GenerateClearGold(
        byte generationLevel,
        byte dungeonDifficulty,
        int partyMemberCount,
        int normalMonsterKills,
        int championMonsterKills,
        int bossMonsterKills,
        IDropRandomSource random)
    {
        if (!goldDrops.TryGetLevel(generationLevel, out var gold))
        {
            return 0;
        }

        // 13339 counts ordinary, Champion/SuperChampion and Boss kills as
        // 0.5, 1 and 2. The configured difficulty/party multiplier is applied
        // once to the per-member result, not once to a party pool and again to
        // each member. Applying it twice would over-reward a 2-4 member party.
        var weightedKillsTwice = Math.Max(0L, normalMonsterKills)
            + 2L * Math.Max(0, championMonsterKills)
            + 4L * Math.Max(0, bossMonsterKills);
        if (weightedKillsTwice == 0)
        {
            return 0;
        }

        var memberCount = Math.Clamp(partyMemberCount, 1, 4);
        var multiplier = catalog.GetGoldMultiplier(
            dungeonDifficulty,
            memberCount);
        var goldUnit = 175L * gold.BaseAmount / 1_000;
        var memberGold = Math.Floor(
            goldUnit * weightedKillsTwice / 4.0 / memberCount * multiplier);
        var spread = gold.SpreadPercent;
        var spreadRoll = spread == 0
            ? 0
            : random.Next(checked(spread * 2 + 1)) - spread;
        var amount = Math.Floor(memberGold * (100 + spreadRoll) / 100.0);
        return checked((uint)Math.Clamp(amount, 0, uint.MaxValue));
    }
}
