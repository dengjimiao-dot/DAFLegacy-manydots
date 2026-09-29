using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

public sealed record CharacterStatValidation(
    bool IsCorrect,
    GameCharacterCombatStats Expected,
    GameCharacterCombatStats? Actual,
    IReadOnlyList<string> MismatchedFields);

public sealed class CharacterStatCatalog
{
    private const string CharacterListPath = "character/character.lst";

    private static readonly Regex ListEntryPattern = new(
        @"(?m)^\s*(?<job>\d+)\s+`(?<path>[^`]+\.chr)`",
        RegexOptions.Compiled | RegexOptions.CultureInvariant |
        RegexOptions.IgnoreCase);
    private static readonly Regex InitialHeaderPattern = new(
        @"(?im)^\s*\[initial value\][^\r\n]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GrowTypeHeaderPattern = new(
        @"(?im)^\s*\[growtype\s+(?<growType>\d+)\][^\r\n]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GrowthValueBoundaryPattern = new(
        @"(?im)^\s*\[(?:skill|growtype\s+\d+|awakening(?:\s+name|\s+\d+)?)\][^\r\n]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex InitialValueBoundaryPattern = new(
        @"(?im)^\s*\[(?:skill|growtype\s+\d+)\][^\r\n]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AwakeningHeaderPattern = new(
        @"(?im)^\s*\[awakening\s+(?<awakeningType>\d+)\][^\r\n]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AwakeningValueBoundaryPattern = new(
        @"(?im)^\s*\[(?:awakening\s+skill|awakening\s+\d+|growtype\s+\d+)\][^\r\n]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TagValuePattern = new(
        @"(?im)^\s*\[(?<tag>[^\]\r\n]+)\]\s*(?<value>[+-]?\d+(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly IReadOnlyDictionary<int, string> DefaultJobPaths =
        new ReadOnlyDictionary<int, string>(new Dictionary<int, string>
        {
            [0] = "character/swordman/swordman.chr",
            [1] = "character/fighter/fighter.chr",
            [2] = "character/gunner/gunner.chr",
            [3] = "character/mage/mage.chr",
            [4] = "character/priest/priest.chr"
        });
    private static readonly IReadOnlyDictionary<int, JobGrowth> BuiltInJobs =
        CreateBuiltInJobs();

    private readonly ScriptFileSystem _scripts;
    private readonly ILogger<CharacterStatCatalog> _logger;
    private readonly Lazy<CatalogState> _state;

    public CharacterStatCatalog(
        ScriptFileSystem scripts,
        ILogger<CharacterStatCatalog> logger)
    {
        _scripts = scripts;
        _logger = logger;
        _state = new Lazy<CatalogState>(
            Load,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public int Count => _state.Value.Jobs.Count;

    public void Initialize() => _ = _state.Value;

    public bool HasAwakeningGrowth(
        int job,
        int growType,
        int awakeningType = 1)
    {
        if (!_state.Value.Jobs.TryGetValue(job, out var definition))
        {
            return false;
        }

        var scriptGrowType = NormalizeGrowType(growType) + 1;
        return definition.AwakeningGrowth.ContainsKey(
            (scriptGrowType, Math.Max(1, awakeningType)));
    }

    public GameCharacterCombatStats Get(int job, int growType, int level) =>
        Get(job, growType, awakeningType: 0, level);

    public GameCharacterCombatStats Get(
        int job,
        int growType,
        int awakeningType,
        int level)
    {
        var definition = GetJob(job);
        var growth = ResolveGrowth(
            definition,
            NormalizeGrowType(growType),
            Math.Max(0, awakeningType));
        var growthCount = Math.Max(
            0,
            Math.Clamp(level, 1, CharacterExperienceCatalog.MaximumLevel) - 1);
        return ToWire(definition.Initial.Add(growth, growthCount));
    }

    public GameCharacterCombatStats GetBase(CharacterRecord character)
    {
        ArgumentNullException.ThrowIfNull(character);
        var packedAwakeningType = Math.Max(0, character.GrowType >> 4);
        return Get(
            character.Job,
            character.GrowType & 0x0F,
            Math.Max(character.AwakeningType, packedAwakeningType),
            character.Level);
    }

    public GameCharacterCombatStats Get(CharacterRecord character)
    {
        ArgumentNullException.ThrowIfNull(character);
        var stats = GetBase(character);
        var allResistance = character.BonusAllElementResistance;
        return stats with
        {
            MaximumHp = AddUnsigned(stats.MaximumHp, character.BonusMaximumHp),
            MaximumMp = AddUnsigned(stats.MaximumMp, character.BonusMaximumMp),
            // These four old-client fields carry STR, VIT, INT and SPI.
            PhysicalAttack = AddSigned(stats.PhysicalAttack, character.BonusStrength),
            PhysicalDefense = AddSigned(stats.PhysicalDefense, character.BonusVitality),
            MagicalAttack = AddSigned(stats.MagicalAttack, character.BonusIntelligence),
            MagicalDefense = AddSigned(stats.MagicalDefense, character.BonusSpirit),
            FireResistance = AddSigned(stats.FireResistance, allResistance),
            WaterResistance = AddSigned(stats.WaterResistance, allResistance),
            DarkResistance = AddSigned(stats.DarkResistance, allResistance),
            LightResistance = AddSigned(stats.LightResistance, allResistance),
            MovementSpeed = AddUnsigned(
                stats.MovementSpeed,
                character.BonusMovementSpeed)
        };
    }

    public CharacterStatValidation Validate(
        CharacterRecord character,
        GameCharacterCombatStats? actual)
    {
        var expected = Get(character);
        if (actual is null)
        {
            return new CharacterStatValidation(
                false,
                expected,
                null,
                ["snapshot"]);
        }

        var mismatches = new List<string>();
        AddMismatch(mismatches, nameof(expected.MaximumHp), expected.MaximumHp, actual.MaximumHp);
        AddMismatch(mismatches, nameof(expected.MaximumMp), expected.MaximumMp, actual.MaximumMp);
        AddMismatch(mismatches, nameof(expected.PhysicalAttack), expected.PhysicalAttack, actual.PhysicalAttack);
        AddMismatch(mismatches, nameof(expected.PhysicalDefense), expected.PhysicalDefense, actual.PhysicalDefense);
        AddMismatch(mismatches, nameof(expected.MagicalAttack), expected.MagicalAttack, actual.MagicalAttack);
        AddMismatch(mismatches, nameof(expected.MagicalDefense), expected.MagicalDefense, actual.MagicalDefense);
        AddMismatch(mismatches, nameof(expected.FireResistance), expected.FireResistance, actual.FireResistance);
        AddMismatch(mismatches, nameof(expected.WaterResistance), expected.WaterResistance, actual.WaterResistance);
        AddMismatch(mismatches, nameof(expected.DarkResistance), expected.DarkResistance, actual.DarkResistance);
        AddMismatch(mismatches, nameof(expected.LightResistance), expected.LightResistance, actual.LightResistance);
        AddMismatch(mismatches, nameof(expected.InventoryLimit), expected.InventoryLimit, actual.InventoryLimit);
        AddMismatch(mismatches, nameof(expected.HpRegeneration), expected.HpRegeneration, actual.HpRegeneration);
        AddMismatch(mismatches, nameof(expected.MpRegeneration), expected.MpRegeneration, actual.MpRegeneration);
        AddMismatch(mismatches, nameof(expected.MovementSpeed), expected.MovementSpeed, actual.MovementSpeed);
        AddMismatch(mismatches, nameof(expected.AttackSpeed), expected.AttackSpeed, actual.AttackSpeed);
        AddMismatch(mismatches, nameof(expected.CastSpeed), expected.CastSpeed, actual.CastSpeed);
        AddMismatch(mismatches, nameof(expected.HitRecovery), expected.HitRecovery, actual.HitRecovery);
        AddMismatch(mismatches, nameof(expected.JumpPower), expected.JumpPower, actual.JumpPower);
        AddMismatch(mismatches, nameof(expected.Weight), expected.Weight, actual.Weight);
        return new CharacterStatValidation(
            mismatches.Count == 0,
            expected,
            actual,
            mismatches.ToArray());
    }

    internal static JobGrowth Parse(string text, string sourcePath = "character.chr")
    {
        text = (text ?? string.Empty).Replace("\0", string.Empty, StringComparison.Ordinal);
        var initialHeader = InitialHeaderPattern.Match(text);
        if (!initialHeader.Success)
        {
            throw new InvalidDataException(
                $"{sourcePath} does not contain [initial value].");
        }

        var initialTail = text[checked(initialHeader.Index + initialHeader.Length)..];
        var initialBoundary = InitialValueBoundaryPattern.Match(initialTail);
        var initialSection = initialBoundary.Success
            ? initialTail[..initialBoundary.Index]
            : initialTail;
        var initial = ParseValues(initialSection);
        if (initial.RecognizedTagCount < 6)
        {
            throw new InvalidDataException(
                $"{sourcePath} has an incomplete [initial value] section.");
        }

        var growth = new Dictionary<int, StatValues>();
        var awakeningGrowth = new Dictionary<(int GrowType, int AwakeningType), StatValues>();
        var growTypeHeaders = GrowTypeHeaderPattern.Matches(text).Cast<Match>().ToArray();
        foreach (var header in growTypeHeaders)
        {
            if (!int.TryParse(
                    header.Groups["growType"].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var scriptGrowType)
                || scriptGrowType <= 0)
            {
                continue;
            }

            var valueStart = checked(header.Index + header.Length);
            var tail = text[valueStart..];
            var valueBoundary = GrowthValueBoundaryPattern.Match(tail);
            var valueSection = valueBoundary.Success
                ? tail[..valueBoundary.Index]
                : tail;
            var parsedGrowth = ParseValues(valueSection);
            if (parsedGrowth.RecognizedTagCount > 0)
            {
                growth[scriptGrowType] = parsedGrowth.Values;
            }

            var nextGrowType = growTypeHeaders.FirstOrDefault(candidate =>
                candidate.Index > header.Index);
            var growTypeEnd = nextGrowType?.Index ?? text.Length;
            var growTypeSection = text[valueStart..growTypeEnd];
            foreach (Match awakeningHeader in AwakeningHeaderPattern.Matches(growTypeSection))
            {
                if (!int.TryParse(
                        awakeningHeader.Groups["awakeningType"].Value,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var awakeningType)
                    || awakeningType <= 0)
                {
                    continue;
                }

                var awakeningStart = checked(
                    awakeningHeader.Index + awakeningHeader.Length);
                var awakeningTail = growTypeSection[awakeningStart..];
                var awakeningBoundary = AwakeningValueBoundaryPattern.Match(
                    awakeningTail);
                var awakeningSection = awakeningBoundary.Success
                    ? awakeningTail[..awakeningBoundary.Index]
                    : awakeningTail;
                var parsedAwakening = ParseValues(awakeningSection);
                if (parsedAwakening.RecognizedTagCount > 0)
                {
                    awakeningGrowth[(scriptGrowType, awakeningType)] =
                        parsedAwakening.Values;
                }
            }
        }

        if (!growth.ContainsKey(1))
        {
            throw new InvalidDataException(
                $"{sourcePath} does not contain base [growtype 1] attributes.");
        }

        return new JobGrowth(
            initial.Values,
            new ReadOnlyDictionary<int, StatValues>(growth),
            new ReadOnlyDictionary<(int, int), StatValues>(awakeningGrowth));
    }

    private CatalogState Load()
    {
        var paths = LoadJobPaths();
        var jobs = new Dictionary<int, JobGrowth>();
        foreach (var entry in paths.OrderBy(entry => entry.Key))
        {
            if (!_scripts.FileExists(entry.Value))
            {
                _logger.LogWarning(
                    "Character script {ScriptPath} for job {Job} was not found in {Source}; built-in base attributes will be used.",
                    entry.Value,
                    entry.Key,
                    _scripts.SourceDescription);
                continue;
            }

            try
            {
                var text = _scripts.ReadAllText(entry.Value, Encoding.Latin1);
                jobs[entry.Key] = Parse(text, entry.Value);
            }
            catch (Exception exception) when (
                exception is InvalidDataException or FormatException or OverflowException)
            {
                _logger.LogWarning(
                    exception,
                    "Character script {ScriptPath} for job {Job} could not be parsed; built-in base attributes will be used.",
                    entry.Value,
                    entry.Key);
            }
        }

        _logger.LogInformation(
            "Cached PVF character-stat growth for {Count} jobs from {Source}.",
            jobs.Count,
            _scripts.SourceDescription);
        return new CatalogState(
            new ReadOnlyDictionary<int, JobGrowth>(jobs));
    }

    private IReadOnlyDictionary<int, string> LoadJobPaths()
    {
        var result = new Dictionary<int, string>();
        if (_scripts.FileExists(CharacterListPath))
        {
            var list = _scripts.ReadAllText(CharacterListPath, Encoding.Latin1);
            foreach (Match match in ListEntryPattern.Matches(list))
            {
                if (int.TryParse(
                        match.Groups["job"].Value,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var job))
                {
                    result[job] = NormalizeCharacterPath(
                        match.Groups["path"].Value);
                }
            }
        }

        foreach (var fallback in DefaultJobPaths)
        {
            result.TryAdd(fallback.Key, fallback.Value);
        }

        return new ReadOnlyDictionary<int, string>(result);
    }

    private JobGrowth GetJob(int job)
    {
        if (_state.Value.Jobs.TryGetValue(job, out var loaded))
        {
            return loaded;
        }

        var fallbackJob = BuiltInJobs.ContainsKey(job) ? job : 0;
        return BuiltInJobs[fallbackJob];
    }

    private static StatValues ResolveGrowth(
        JobGrowth definition,
        int growType,
        int awakeningType)
    {
        var scriptGrowType = growType + 1;
        if (awakeningType > 0)
        {
            if (definition.AwakeningGrowth.TryGetValue(
                    (scriptGrowType, awakeningType),
                    out var exactAwakening))
            {
                return exactAwakening;
            }

            if (definition.AwakeningGrowth.TryGetValue(
                    (scriptGrowType, 1),
                    out var firstAwakening))
            {
                return firstAwakening;
            }
        }

        if (definition.Growth.TryGetValue(scriptGrowType, out var selected))
        {
            return selected;
        }

        return definition.Growth.TryGetValue(1, out var baseGrowth)
            ? baseGrowth
            : StatValues.Empty;
    }

    private static ParsedValues ParseValues(string section)
    {
        var values = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in TagValuePattern.Matches(section ?? string.Empty))
        {
            if (decimal.TryParse(
                    match.Groups["value"].Value,
                    NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out var value))
            {
                values.TryAdd(match.Groups["tag"].Value.Trim(), value);
            }
        }

        var recognized = 0;
        decimal Read(string tag)
        {
            if (!values.TryGetValue(tag, out var value))
            {
                return 0m;
            }

            recognized++;
            return value;
        }

        return new ParsedValues(
            new StatValues(
                MaximumHp: Read("HP MAX"),
                MaximumMp: Read("MP MAX"),
                Strength: Read("physical attack"),
                Vitality: Read("physical defense"),
                Intelligence: Read("magical attack"),
                Spirit: Read("magical defense"),
                FireResistance: Read("fire resistance"),
                WaterResistance: Read("water resistance"),
                DarkResistance: Read("dark resistance"),
                LightResistance: Read("light resistance"),
                InventoryLimit: Read("inventory limit"),
                HpRegeneration: Read("HP regen speed"),
                MpRegeneration: Read("MP regen speed"),
                MovementSpeed: Read("move speed"),
                AttackSpeed: Read("attack speed"),
                CastSpeed: Read("cast speed"),
                HitRecovery: Read("hit recovery"),
                JumpPower: Read("jump power"),
                Weight: Read("weight")),
            recognized);
    }

    private static GameCharacterCombatStats ToWire(StatValues values) => new(
        MaximumHp: ToUInt32(values.MaximumHp),
        MaximumMp: ToUInt32(values.MaximumMp),
        PhysicalAttack: ToInt16(values.Strength),
        PhysicalDefense: ToInt16(values.Vitality),
        MagicalAttack: ToInt16(values.Intelligence),
        MagicalDefense: ToInt16(values.Spirit),
        FireResistance: ToInt16(values.FireResistance),
        WaterResistance: ToInt16(values.WaterResistance),
        DarkResistance: ToInt16(values.DarkResistance),
        LightResistance: ToInt16(values.LightResistance),
        InventoryLimit: ToUInt32(values.InventoryLimit),
        HpRegeneration: ToUInt16(values.HpRegeneration),
        MpRegeneration: ToUInt16(values.MpRegeneration),
        MovementSpeed: ToUInt16(values.MovementSpeed),
        AttackSpeed: ToUInt16(values.AttackSpeed),
        CastSpeed: ToUInt16(values.CastSpeed),
        HitRecovery: ToUInt16(values.HitRecovery),
        JumpPower: ToUInt16(values.JumpPower),
        Weight: ToUInt32(values.Weight));

    private static uint ToUInt32(decimal value)
    {
        var wireValue = Math.Round(
            value * 10m,
            MidpointRounding.AwayFromZero);
        return checked((uint)Math.Clamp(wireValue, 0m, uint.MaxValue));
    }

    private static ushort ToUInt16(decimal value)
    {
        var wireValue = Math.Round(
            value * 10m,
            MidpointRounding.AwayFromZero);
        return checked((ushort)Math.Clamp(wireValue, 0m, ushort.MaxValue));
    }

    private static short ToInt16(decimal value)
    {
        var wireValue = Math.Round(
            value * 10m,
            MidpointRounding.AwayFromZero);
        return checked((short)Math.Clamp(wireValue, short.MinValue, short.MaxValue));
    }

    private static uint AddUnsigned(uint value, int displayedBonus)
    {
        var bonus = Math.Max(0L, (long)displayedBonus * 10L);
        return checked((uint)Math.Min(uint.MaxValue, (ulong)value + (ulong)bonus));
    }

    private static ushort AddUnsigned(ushort value, int displayedBonus)
    {
        var bonus = Math.Max(0L, (long)displayedBonus * 10L);
        return checked((ushort)Math.Min(ushort.MaxValue, (long)value + bonus));
    }

    private static short AddSigned(short value, int displayedBonus)
    {
        var result = (long)value + (long)displayedBonus * 10L;
        return checked((short)Math.Clamp(result, short.MinValue, short.MaxValue));
    }

    private static int NormalizeGrowType(int growType) =>
        growType < 0 ? 0 : Math.Clamp(growType & 0x0F, 0, 4);

    private static string NormalizeCharacterPath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        return normalized.StartsWith("character/", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : $"character/{normalized}";
    }

    private static void AddMismatch<T>(
        ICollection<string> result,
        string name,
        T expected,
        T actual)
        where T : IEquatable<T>
    {
        if (!expected.Equals(actual))
        {
            result.Add(name);
        }
    }

    private static IReadOnlyDictionary<int, JobGrowth> CreateBuiltInJobs()
    {
        var result = new Dictionary<int, JobGrowth>
        {
            [0] = CreateBuiltInJob(
                new StatValues(
                    MaximumHp: 180m,
                    MaximumMp: 140m,
                    Strength: 7m,
                    Vitality: 7m,
                    Intelligence: 4m,
                    Spirit: 4m,
                    DarkResistance: 20m,
                    LightResistance: -20m,
                    InventoryLimit: 40_000m,
                    MpRegeneration: 50m,
                    MovementSpeed: 850m,
                    AttackSpeed: 850m,
                    CastSpeed: 700m,
                    HitRecovery: 600m,
                    JumpPower: 430m,
                    Weight: 68_000m),
                new StatValues(
                    MaximumHp: 45m,
                    MaximumMp: 25m,
                    Strength: 3.3m,
                    Vitality: 3.3m,
                    Intelligence: 2.7m,
                    Spirit: 2.7m,
                    InventoryLimit: 300m,
                    MpRegeneration: 2.5m,
                    HitRecovery: 1.5m)),
            [1] = CreateBuiltInJob(
                new StatValues(
                    MaximumHp: 200m,
                    MaximumMp: 120m,
                    Strength: 7m,
                    Vitality: 7m,
                    Intelligence: 4m,
                    Spirit: 4m,
                    DarkResistance: -20m,
                    LightResistance: 20m,
                    InventoryLimit: 36_000m,
                    MpRegeneration: 40m,
                    MovementSpeed: 910m,
                    AttackSpeed: 950m,
                    CastSpeed: 1_000m,
                    HitRecovery: 600m,
                    JumpPower: 470m,
                    Weight: 50_000m),
                new StatValues(
                    MaximumHp: 50m,
                    MaximumMp: 20m,
                    Strength: 3.5m,
                    Vitality: 3.5m,
                    Intelligence: 2.5m,
                    Spirit: 2.5m,
                    InventoryLimit: 270m,
                    MpRegeneration: 2m,
                    HitRecovery: 1.5m)),
            [2] = CreateBuiltInJob(
                new StatValues(
                    MaximumHp: 160m,
                    MaximumMp: 160m,
                    Strength: 6m,
                    Vitality: 6m,
                    Intelligence: 5m,
                    Spirit: 5m,
                    InventoryLimit: 33_000m,
                    MpRegeneration: 50m,
                    MovementSpeed: 820m,
                    AttackSpeed: 950m,
                    CastSpeed: 800m,
                    HitRecovery: 600m,
                    JumpPower: 490m,
                    Weight: 60_000m),
                new StatValues(
                    MaximumHp: 40m,
                    MaximumMp: 30m,
                    Strength: 3m,
                    Vitality: 3m,
                    Intelligence: 3m,
                    Spirit: 3m,
                    InventoryLimit: 300m,
                    MpRegeneration: 2.5m,
                    HitRecovery: 1m)),
            [3] = CreateBuiltInJob(
                new StatValues(
                    MaximumHp: 120m,
                    MaximumMp: 200m,
                    Strength: 4m,
                    Vitality: 4m,
                    Intelligence: 7m,
                    Spirit: 7m,
                    InventoryLimit: 28_000m,
                    MpRegeneration: 80m,
                    MovementSpeed: 800m,
                    AttackSpeed: 1_000m,
                    CastSpeed: 1_000m,
                    HitRecovery: 500m,
                    JumpPower: 350m,
                    Weight: 40_000m),
                new StatValues(
                    MaximumHp: 35m,
                    MaximumMp: 35m,
                    Strength: 2.5m,
                    Vitality: 2.5m,
                    Intelligence: 3.5m,
                    Spirit: 3.5m,
                    InventoryLimit: 250m,
                    MpRegeneration: 4m,
                    HitRecovery: 1m)),
            [4] = CreateBuiltInJob(
                new StatValues(
                    MaximumHp: 220m,
                    MaximumMp: 100m,
                    Strength: 6m,
                    Vitality: 6m,
                    Intelligence: 4m,
                    Spirit: 6m,
                    InventoryLimit: 40_000m,
                    MpRegeneration: 50m,
                    MovementSpeed: 750m,
                    AttackSpeed: 950m,
                    CastSpeed: 1_000m,
                    HitRecovery: 500m,
                    JumpPower: 500m,
                    Weight: 85_000m),
                new StatValues(
                    MaximumHp: 50m,
                    MaximumMp: 20m,
                    Strength: 3.2m,
                    Vitality: 3.3m,
                    Intelligence: 2.5m,
                    Spirit: 3m,
                    InventoryLimit: 300m,
                    MpRegeneration: 2.5m,
                    HitRecovery: 1m))
        };
        return new ReadOnlyDictionary<int, JobGrowth>(result);
    }

    private static JobGrowth CreateBuiltInJob(
        StatValues initial,
        StatValues growth) =>
        new(
            initial,
            new ReadOnlyDictionary<int, StatValues>(
                new Dictionary<int, StatValues> { [1] = growth }),
            new ReadOnlyDictionary<(int, int), StatValues>(
                new Dictionary<(int, int), StatValues>()));

    internal sealed record JobGrowth(
        StatValues Initial,
        IReadOnlyDictionary<int, StatValues> Growth,
        IReadOnlyDictionary<(int GrowType, int AwakeningType), StatValues>
            AwakeningGrowth);

    internal sealed record StatValues(
        decimal MaximumHp = 0m,
        decimal MaximumMp = 0m,
        decimal Strength = 0m,
        decimal Vitality = 0m,
        decimal Intelligence = 0m,
        decimal Spirit = 0m,
        decimal FireResistance = 0m,
        decimal WaterResistance = 0m,
        decimal DarkResistance = 0m,
        decimal LightResistance = 0m,
        decimal InventoryLimit = 0m,
        decimal HpRegeneration = 0m,
        decimal MpRegeneration = 0m,
        decimal MovementSpeed = 0m,
        decimal AttackSpeed = 0m,
        decimal CastSpeed = 0m,
        decimal HitRecovery = 0m,
        decimal JumpPower = 0m,
        decimal Weight = 0m)
    {
        public static StatValues Empty { get; } = new();

        public StatValues Add(StatValues growth, int count) => new(
            MaximumHp + growth.MaximumHp * count,
            MaximumMp + growth.MaximumMp * count,
            Strength + growth.Strength * count,
            Vitality + growth.Vitality * count,
            Intelligence + growth.Intelligence * count,
            Spirit + growth.Spirit * count,
            FireResistance + growth.FireResistance * count,
            WaterResistance + growth.WaterResistance * count,
            DarkResistance + growth.DarkResistance * count,
            LightResistance + growth.LightResistance * count,
            InventoryLimit + growth.InventoryLimit * count,
            HpRegeneration + growth.HpRegeneration * count,
            MpRegeneration + growth.MpRegeneration * count,
            MovementSpeed + growth.MovementSpeed * count,
            AttackSpeed + growth.AttackSpeed * count,
            CastSpeed + growth.CastSpeed * count,
            HitRecovery + growth.HitRecovery * count,
            JumpPower + growth.JumpPower * count,
            Weight + growth.Weight * count);
    }

    private sealed record ParsedValues(
        StatValues Values,
        int RecognizedTagCount);

    private sealed record CatalogState(
        IReadOnlyDictionary<int, JobGrowth> Jobs);
}
