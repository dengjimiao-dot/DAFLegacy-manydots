namespace DFLegacy.Server;

public sealed record HellDropProbabilityBand(
    byte MinimumLevel,
    byte MaximumLevel,
    int[] DifficultyProbabilities);

public sealed record HellDropLevelRange(
    byte Level,
    byte MinusLevel,
    byte PlusLevel);

public sealed class HellDropCatalog
{
    private const string ScriptPath = "etc/itemdropinfo_monster_hell.etc";
    private readonly MonsterDropCatalog _monsterDrops;
    private readonly ILogger<HellDropCatalog> _logger;
    private readonly Lazy<CatalogState> _state;

    public HellDropCatalog(
        ScriptFileSystem scripts,
        MonsterDropCatalog monsterDrops,
        ILogger<HellDropCatalog> logger)
    {
        _monsterDrops = monsterDrops;
        _logger = logger;
        _state = new Lazy<CatalogState>(
            () => Load(scripts, logger),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public int ProbabilityBandCount => _state.Value.ProbabilityBands.Length;

    public int LevelRangeCount => _state.Value.LevelRanges.Count;

    public void Initialize() => _ = _state.Value;

    public bool TryGetProbabilityBand(
        byte level,
        out HellDropProbabilityBand band)
    {
        band = _state.Value.ProbabilityBands.FirstOrDefault(candidate =>
            level >= candidate.MinimumLevel && level <= candidate.MaximumLevel)!;
        return band is not null;
    }

    public bool TryGetLevelRange(byte level, out HellDropLevelRange range) =>
        _state.Value.LevelRanges.TryGetValue(level, out range!);

    public int GetDifficultyProbability(
        byte level,
        byte dungeonDifficulty,
        byte hellPartyMode)
    {
        if (!TryGetProbabilityBand(level, out var band))
        {
            return 0;
        }

        var column = ResolveDifficultyColumn(dungeonDifficulty, hellPartyMode);
        return column < band.DifficultyProbabilities.Length
            ? band.DifficultyProbabilities[column]
            : 0;
    }

    public byte RollRarity(byte dungeonDifficulty, IDropRandomSource random)
    {
        // DF2008 has one PVF rarity table, not a separate table per difficulty.
        var thresholds = _state.Value.RarityThresholds;
        var roll = random.Next(10_000) + 1;
        for (var rarity = thresholds.Length - 1; rarity >= 0; rarity--)
        {
            if (roll >= thresholds[rarity])
            {
                return checked((byte)rarity);
            }
        }

        return 0;
    }

    public bool TryRollEquipment(
        byte dungeonLevel,
        byte dungeonDifficulty,
        byte hellPartyMode,
        IDropRandomSource random,
        out ushort itemId,
        double rateMultiplier = 1,
        bool forceDrop = false)
    {
        itemId = 0;
        if (hellPartyMode is not (1 or 2)
            || !double.IsFinite(rateMultiplier) || rateMultiplier < 0)
        {
            return false;
        }

        var level = (byte)Math.Clamp((int)dungeonLevel, 1, 200);
        if (!TryGetProbabilityBand(level, out _))
        {
            _logger.LogWarning("Hell drop has no probability band for level {Level}.", level);
            return false;
        }

        var column = ResolveDifficultyColumn(dungeonDifficulty, hellPartyMode);
        var weight = GetDifficultyProbability(level, dungeonDifficulty, hellPartyMode);
        var threshold = Math.Floor(weight * rateMultiplier);
        // 13339 generateSpecificItem (0x085357AA..0x085357D3) compares
        // inclusive CMTRand [0,1000] <= adjusted PVF weight. Not a 1/10000 gate.
        var gateRoll = random.Next(1_001);
        var hit = forceDrop || (rateMultiplier > 0 && weight >= 0 && gateRoll <= threshold);
        _logger.LogInformation(
            "Hell drop gate: level={Level}, difficulty={Difficulty}, mode={Mode}, column={Column}, weight={Weight}, multiplier={Multiplier}, roll={Roll}, hit={Hit}, forced={Forced}.",
            level, dungeonDifficulty, hellPartyMode, column, weight, rateMultiplier, gateRoll, hit, forceDrop);
        var range = _state.Value.LevelRanges.GetValueOrDefault(level)
            ?? new HellDropLevelRange(level, 3, 3);
        // DFLegacy's local economy guarantees one product per cosmofiend.
        // A missed dedicated roll selects common equipment instead of nothing.
        var rarity = hit ? RollRarity(dungeonDifficulty, random) : (byte)0;
        if (_monsterDrops.TryChooseItem(
                MonsterDropKind.Equipment,
                level,
                rarity,
                range.MinusLevel,
                range.PlusLevel,
                random,
                out itemId))
        {
            _logger.LogInformation("Hell equipment selected: level={Level}, rarity={Rarity}, item={ItemId}.", level, rarity, itemId);
            return true;
        }

        var fallback = rarity != 0
            && _monsterDrops.TryChooseItem(
                MonsterDropKind.Equipment,
                level,
                (byte)0,
                range.MinusLevel,
                range.PlusLevel,
                random,
                out itemId);
        _logger.LogInformation(
            "Hell equipment pool miss: level={Level}, rarity={Rarity}, commonFallback={Fallback}, item={ItemId}.",
            level, rarity, fallback, itemId);
        return fallback;
    }

    public static int ResolveDifficultyColumn(
        byte dungeonDifficulty,
        byte hellPartyMode)
    {
        var difficulty = Math.Clamp((int)dungeonDifficulty, 0, 3);
        return hellPartyMode >= 2
            ? difficulty
            : Math.Min(3, difficulty + 2);
    }

    public static int[] GetRarityCounts(byte dungeonDifficulty)
    {
        var difficulty = Math.Clamp((int)dungeonDifficulty, 0, 3);
        return
        [
            7_999 - difficulty * 1_000,
            1_000 + difficulty * 360,
            500 + difficulty * 270,
            500 + difficulty * 270,
            1 + difficulty * 100
        ];
    }

    public static int[] BuildRarityThresholds(byte dungeonDifficulty)
    {
        var counts = GetRarityCounts(dungeonDifficulty);
        var thresholds = new int[5];
        var cumulative = 0;
        for (var rarity = 1; rarity < thresholds.Length; rarity++)
        {
            cumulative += counts[rarity - 1];
            thresholds[rarity] = cumulative + 1;
        }

        return thresholds;
    }

    private static CatalogState Load(
        ScriptFileSystem scripts,
        ILogger logger)
    {
        var probabilityBands = DropScriptParser.ReadSection(
                scripts,
                ScriptPath,
                "dungeon difficulty drop prob",
                logger)
            .Select(ParseProbabilityBand)
            .Where(band => band is not null)
            .Cast<HellDropProbabilityBand>()
            .ToArray();
        var rarityValues = DropScriptParser.ReadSection(
                scripts,
                ScriptPath,
                "basis of rarity dicision",
                logger)
            .SelectMany(row => row)
            .Select(value => int.TryParse(value, out var parsed) ? parsed : 0)
            .Take(5)
            .ToArray();
        var rarityThresholds = rarityValues.Length == 5
            ? rarityValues
            : BuildRarityThresholds(0);
        var levelRanges = DropScriptParser.ReadSection(
                scripts,
                ScriptPath,
                "item drop ref table",
                logger)
            .Select(ParseLevelRange)
            .Where(range => range is not null)
            .Cast<HellDropLevelRange>()
            .ToDictionary(range => range.Level);

        logger.LogInformation(
            "Loaded {BandCount} hell-drop probability bands and {LevelCount} level ranges from {Path}.",
            probabilityBands.Length,
            levelRanges.Count,
            ScriptPath);
        return new CatalogState(probabilityBands, rarityThresholds, levelRanges);
    }

    private static HellDropProbabilityBand? ParseProbabilityBand(string[] columns)
    {
        if (columns.Length < 6
            || !byte.TryParse(columns[0], out var minimumLevel)
            || !byte.TryParse(columns[1], out var maximumLevel))
        {
            return null;
        }

        var probabilities = new int[4];
        for (var index = 0; index < probabilities.Length; index++)
        {
            if (!int.TryParse(columns[index + 2], out probabilities[index]))
            {
                return null;
            }
        }

        return new HellDropProbabilityBand(
            minimumLevel,
            maximumLevel,
            probabilities);
    }

    private static HellDropLevelRange? ParseLevelRange(string[] columns) =>
        columns.Length >= 3
        && byte.TryParse(columns[0], out var level)
        && byte.TryParse(columns[1], out var minusLevel)
        && byte.TryParse(columns[2], out var plusLevel)
            ? new HellDropLevelRange(level, minusLevel, plusLevel)
            : null;

    private sealed record CatalogState(
        HellDropProbabilityBand[] ProbabilityBands,
        int[] RarityThresholds,
        IReadOnlyDictionary<byte, HellDropLevelRange> LevelRanges);
}
