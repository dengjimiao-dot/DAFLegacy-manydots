namespace DFLegacy.Server;

public sealed record PremiumBenefitDefinition(
    byte ServiceType,
    int OverSkillLevel,
    IReadOnlyDictionary<int, int> OverEquipableLevels);

public sealed class PremiumBenefitCatalog
{
    private const string PremiumListPath = "etc/premiumlist.etc";

    private readonly ScriptFileSystem _scripts;
    private readonly ILogger<PremiumBenefitCatalog> _logger;
    private readonly Lazy<IReadOnlyDictionary<byte, PremiumBenefitDefinition>> _definitions;

    public PremiumBenefitCatalog(
        ScriptFileSystem scripts,
        ILogger<PremiumBenefitCatalog> logger)
    {
        _scripts = scripts;
        _logger = logger;
        _definitions = new Lazy<IReadOnlyDictionary<byte, PremiumBenefitDefinition>>(
            LoadDefinitions,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public int Count => _definitions.Value.Count;

    public void Initialize() => _ = _definitions.Value;

    public int GetOverSkillLevel(IEnumerable<byte> activeServiceTypes)
    {
        var maximum = 0;
        foreach (var serviceType in activeServiceTypes.Distinct())
        {
            if (_definitions.Value.TryGetValue(serviceType, out var definition))
            {
                maximum = Math.Max(maximum, definition.OverSkillLevel);
            }
        }

        return maximum;
    }

    public int GetOverEquipableLevel(
        IEnumerable<byte> activeServiceTypes,
        int equipmentType)
    {
        if (equipmentType <= 0)
        {
            return 0;
        }

        var maximum = 0;
        foreach (var serviceType in activeServiceTypes.Distinct())
        {
            if (_definitions.Value.TryGetValue(serviceType, out var definition)
                && definition.OverEquipableLevels.TryGetValue(
                    equipmentType,
                    out var overLevel))
            {
                maximum = Math.Max(maximum, overLevel);
            }
        }

        return maximum;
    }

    public int GetEffectiveSkillLevel(
        int characterLevel,
        IEnumerable<byte> activeServiceTypes) =>
        SaturatingAdd(characterLevel, GetOverSkillLevel(activeServiceTypes));

    public int GetEffectiveEquipmentLevel(
        int characterLevel,
        IEnumerable<byte> activeServiceTypes,
        ItemDefinition definition) =>
        SaturatingAdd(
            characterLevel,
            GetOverEquipableLevel(
                activeServiceTypes,
                GetPremiumEquipmentType(definition)));

    public bool CanEquipByLevel(
        int characterLevel,
        IEnumerable<byte> activeServiceTypes,
        ItemDefinition definition) =>
        definition.MinimumLevel is null or <= 0
        || definition.MinimumLevel.Value <= GetEffectiveEquipmentLevel(
            characterLevel,
            activeServiceTypes,
            definition);

    public static int GetPremiumEquipmentType(ItemDefinition definition)
    {
        if (definition.InventoryCategory == ItemInventoryCategory.Avatar)
        {
            return 4;
        }

        if (definition.InventoryCategory != ItemInventoryCategory.Equipment)
        {
            return 0;
        }

        return definition.TypeTag switch
        {
            "weapon" => 1,
            "coat" or "pants" or "shoulder" or "waist" or "shoes" => 2,
            "amulet" or "wrist" or "ring" => 3,
            _ => 0
        };
    }

    private IReadOnlyDictionary<byte, PremiumBenefitDefinition> LoadDefinitions()
    {
        var definitions = new Dictionary<byte, PremiumBenefitDefinition>();
        if (!_scripts.FileExists(PremiumListPath))
        {
            _logger.LogWarning(
                "DFLegacy Premium benefit list was not found in {Source}.",
                _scripts.SourceDescription);
            return definitions;
        }

        var lines = _scripts.ReadLines(PremiumListPath)
            .Select(StripComment)
            .Select(line => line.Trim())
            .Where(line => line.Length != 0)
            .ToArray();
        byte? serviceType = null;
        var overSkillLevel = 0;
        var overEquipableLevels = new Dictionary<int, int>();

        void Flush()
        {
            if (!serviceType.HasValue)
            {
                return;
            }

            definitions[serviceType.Value] = new PremiumBenefitDefinition(
                serviceType.Value,
                overSkillLevel,
                new Dictionary<int, int>(overEquipableLevels));
        }

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (TryReadTaggedInteger(line, "type", out var parsedType))
            {
                Flush();
                serviceType = parsedType is >= byte.MinValue and <= byte.MaxValue
                    ? (byte)parsedType
                    : null;
                overSkillLevel = 0;
                overEquipableLevels.Clear();
                continue;
            }

            if (!serviceType.HasValue)
            {
                continue;
            }

            if (TryReadTaggedInteger(line, "over skill", out var parsedOverSkill))
            {
                overSkillLevel = Math.Max(0, parsedOverSkill);
                continue;
            }

            if (!string.Equals(line, "[over equipinfo]", StringComparison.OrdinalIgnoreCase)
                || index + 1 >= lines.Length
                || !int.TryParse(lines[++index], out var entryCount)
                || entryCount < 0)
            {
                continue;
            }

            for (var entryIndex = 0;
                 entryIndex < entryCount && index + 1 < lines.Length;
                 entryIndex++)
            {
                var fields = lines[++index].Split(
                    [' ', '\t'],
                    StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length >= 2
                    && int.TryParse(fields[0], out var equipmentType)
                    && int.TryParse(fields[1], out var overLevel)
                    && equipmentType > 0)
                {
                    overEquipableLevels[equipmentType] = Math.Max(0, overLevel);
                }
            }
        }

        Flush();
        _logger.LogInformation(
            "Loaded {Count} Premium benefit definitions from {Source}.",
            definitions.Count,
            _scripts.SourceDescription);
        return definitions;
    }

    private static bool TryReadTaggedInteger(
        string line,
        string tag,
        out int value)
    {
        value = 0;
        var prefix = $"[{tag}]";
        return line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(line[prefix.Length..].Trim(), out value);
    }

    private static string StripComment(string line)
    {
        var comment = line.IndexOf("//", StringComparison.Ordinal);
        return comment >= 0 ? line[..comment] : line;
    }

    private static int SaturatingAdd(int value, int addition) =>
        value > int.MaxValue - addition ? int.MaxValue : value + addition;
}
