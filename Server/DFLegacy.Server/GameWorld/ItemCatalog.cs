using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DFLegacy.Server;

public enum ItemScriptKind
{
    Equipment,
    Stackable
}

public enum ItemInventoryCategory
{
    Equipment,
    Consumable,
    Material,
    Quest,
    Avatar,
    Creature
}

public enum ItemAttachType
{
    Unknown,
    Free,
    Trade,
    TradeDelete,
    Sealing,
    SealingTrade,
    Account
}

public enum IncreaseStatusType : byte
{
    SkillPoints = 0,
    Experience = 1,
    MaximumHp = 2,
    MaximumMp = 3,
    Strength = 4,
    Vitality = 5,
    Intelligence = 6,
    Spirit = 7,
    MovementSpeed = 8,
    AllElementResistance = 9,
    SkillReset = 10
}

public sealed record IncreaseStatusEffect(
    IncreaseStatusType Type,
    int Amount);

public sealed record LotteryRewardDefinition(
    ushort ItemId,
    uint CountOrValue,
    int Weight);

public sealed record LotteryItemDefinition(
    LotteryRewardDefinition FallbackReward,
    IReadOnlyList<LotteryRewardDefinition> WeightedRewards)
{
    public const int RollUpperBound = 100_000;

    public LotteryRewardDefinition Select(int roll)
    {
        if (roll is < 0 or >= RollUpperBound)
        {
            throw new ArgumentOutOfRangeException(nameof(roll));
        }

        long cumulativeWeight = 0;
        foreach (var reward in WeightedRewards)
        {
            cumulativeWeight += reward.Weight;
            if (cumulativeWeight > roll)
            {
                return reward;
            }
        }

        return FallbackReward;
    }
}

public enum CeraBoosterRewardKind
{
    Avatar,
    SpecialAvatar,
    Cera,
    Creature,
    Equipment,
    Stackable,
    Etc,
    SpecialCreature,
    Emblem
}

public sealed record CeraBoosterRewardDefinition(
    CeraBoosterRewardKind Kind,
    ushort ItemId,
    uint Count,
    int Weight,
    int AvatarPeriodDays = 0,
    ushort AvatarAbilityIndex = 0);

public sealed record CeraBoosterRewardGroup(
    CeraBoosterRewardKind Kind,
    int DrawCount,
    IReadOnlyList<CeraBoosterRewardDefinition> Rewards,
    int TotalWeight)
{
    public CeraBoosterRewardDefinition Select(IDropRandomSource random)
    {
        ArgumentNullException.ThrowIfNull(random);
        if (Rewards.Count == 0 || TotalWeight <= 0)
        {
            throw new InvalidOperationException("A Cera booster reward group has no selectable reward.");
        }

        var roll = random.Next(TotalWeight);
        var cumulative = 0;
        foreach (var reward in Rewards)
        {
            cumulative = checked(cumulative + reward.Weight);
            if (roll < cumulative)
            {
                return reward;
            }
        }

        throw new InvalidOperationException("A Cera booster weighted selection was not resolved.");
    }
}

public sealed record CeraBoosterDefinition(
    IReadOnlyList<CeraBoosterRewardGroup> Groups)
{
    public IReadOnlyList<CeraBoosterRewardDefinition> SelectAll(
        IDropRandomSource random)
    {
        ArgumentNullException.ThrowIfNull(random);
        var selected = new List<CeraBoosterRewardDefinition>();
        foreach (var group in Groups)
        {
            for (var draw = 0; draw < group.DrawCount; draw++)
            {
                selected.Add(group.Select(random));
            }
        }

        return selected;
    }
}

public sealed record CeraPackageRewardDefinition(
    ushort ItemId,
    uint Count);

public sealed record CeraPackageDefinition(
    IReadOnlyList<CeraPackageRewardDefinition> Rewards);

public sealed record ItemDefinition(
    ushort Id,
    string ScriptPath,
    string Name,
    ItemScriptKind ScriptKind,
    ItemInventoryCategory InventoryCategory,
    string TypeTag,
    ItemAttachType AttachType,
    int? Grade,
    int? Rarity,
    int? Weight,
    int? MinimumLevel,
    int? MaximumLevel,
    int? CreationRate,
    int? StackLimit,
    int? MaximumHavingCount,
    int? Cash,
    int? Price,
    int? Value,
    int? Durability,
    int? CreatureSubType = null,
    int? CreatureOutputIndex = null,
    int? CreatureSpecies = null,
    LotteryItemDefinition? Lottery = null,
    int? LotteryUseCost = null,
    IncreaseStatusEffect? IncreaseStatus = null,
    int? CreatureMinimumLevel = null,
    decimal? CreatureExperienceAmountRate = null,
    CeraBoosterDefinition? CeraBooster = null,
    CeraPackageDefinition? CeraPackage = null,
    int? InventoryLimit = null)
{
    public bool IsEquipment => ScriptKind == ItemScriptKind.Equipment;

    public bool IsTitle =>
        IsEquipment && TypeTag is "title name" or "title";

    public bool IsStackable => ScriptKind == ItemScriptKind.Stackable;

    public bool CanDropOnGround => AttachType is
        ItemAttachType.Free or ItemAttachType.Sealing;
}

public sealed class ItemCatalog
{
    private const string EquipmentListPath = "equipment/equipment.lst";
    private const string EquipmentPriceTablePath = "equipment/pricetable.tbl";
    private const string StackableListPath = "stackable/stackable.lst";
    private const int DefaultSellPriceRatePerMille = 1_000;

    private static readonly Encoding ScriptEncoding = CreateScriptEncoding();

    private static readonly Regex ListEntryPattern = new(
        @"^\s*(?<id>\d+)\s+`(?<path>[^`]+)`",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex NamePattern = CreateStringTagPattern("name");
    private static readonly Regex AttachTypePattern = CreateTokenTagPattern("attach type");
    private static readonly Regex EquipmentTypePattern = CreateTokenTagPattern("equipment type");
    private static readonly Regex StackableTypePattern = CreateTokenTagPattern("stackable type");
    private static readonly Regex GradePattern = CreateIntegerTagPattern("grade");
    private static readonly Regex RarityPattern = CreateIntegerTagPattern("rarity");
    private static readonly Regex WeightPattern = CreateIntegerTagPattern("weight");
    private static readonly Regex MinimumLevelPattern = CreateIntegerTagPattern("minimum level");
    private static readonly Regex MaximumLevelPattern = CreateIntegerTagPattern("maximum level");
    private static readonly Regex CreationRatePattern = CreateIntegerTagPattern("creation rate");
    private static readonly Regex StackLimitPattern = CreateIntegerTagPattern("stack limit");
    private static readonly Regex MaximumHavingCountPattern =
        CreateIntegerTagPattern("max having count");
    private static readonly Regex CashPattern = CreateIntegerTagPattern("cash");
    private static readonly Regex PricePattern = CreateIntegerTagPattern("price");
    private static readonly Regex ValuePattern = CreateIntegerTagPattern("value");
    private static readonly Regex DurabilityPattern = CreateIntegerTagPattern("durability");
    private static readonly Regex InventoryLimitPattern =
        CreateIntegerTagPattern("inventory limit");
    private static readonly Regex CreatureSubTypePattern = CreateIntegerTagPattern("sub type");
    private static readonly Regex CreatureOutputIndexPattern =
        CreateIntegerTagPattern("output index");
    private static readonly Regex CreatureSpeciesPattern =
        CreateIntegerTagPattern("creature species");
    private static readonly Regex CreatureMinimumLevelPattern =
        CreateIntegerTagPattern("creature minimum level");
    private static readonly Regex CreatureExperienceAmountRatePattern =
        CreateDecimalTagPattern("creature experience amount rate");
    private static readonly Regex LotteryUseCostPattern =
        CreateIntegerTagPattern("lottery use cost");
    private static readonly Regex IntDataPattern = new(
        @"^\s*\[int data\]\s*(?<value>.*?)^\s*\[/int data\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline |
        RegexOptions.Singleline | RegexOptions.IgnoreCase);
    private static readonly Regex BoosterInfoPattern = new(
        @"^\s*\[booster info\]\s*(?<value>.*?)^\s*\[/booster info\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline |
        RegexOptions.Singleline | RegexOptions.IgnoreCase);
    private static readonly Regex PackageDataPattern = new(
        @"^\s*\[package data\]\s*(?<value>.*?)^\s*\[/package data\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline |
        RegexOptions.Singleline | RegexOptions.IgnoreCase);
    private static readonly Regex BoosterGroupTagPattern = new(
        @"^\[(?<closing>/)?(?<tag>avatar|special avatar|cera|creature|equipment|stackable|etc|special creature|emblem)\]$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex IntegerPattern = new(
        @"[+-]?\d+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ScriptFileSystem _scripts;
    private readonly ILogger<ItemCatalog> _logger;
    private readonly Lazy<CatalogState> _state;

    public ItemCatalog(
        ScriptFileSystem scripts,
        ILogger<ItemCatalog> logger)
    {
        _scripts = scripts;
        _logger = logger;
        _state = new Lazy<CatalogState>(
            Load,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public int Count => _state.Value.Definitions.Count;

    public int LotteryCount => _state.Value.LotteryCount;

    public int CeraBoosterCount => _state.Value.CeraBoosterCount;

    public int CeraPackageCount => _state.Value.CeraPackageCount;

    public int SkillPointBookCount => _state.Value.SkillPointBookCount;

    public int ExperienceBookCount => _state.Value.ExperienceBookCount;

    public int AttributeStoneCount => _state.Value.AttributeStoneCount;

    public int IncreaseStatusItemCount =>
        _state.Value.SkillPointBookCount
        + _state.Value.ExperienceBookCount
        + _state.Value.AttributeStoneCount;

    public IReadOnlyDictionary<ushort, ItemDefinition> Definitions =>
        _state.Value.Definitions;

    public void Initialize() => _ = _state.Value;

    public bool Contains(ushort itemId) => _state.Value.Definitions.ContainsKey(itemId);

    public bool TryGetDefinition(ushort itemId, out ItemDefinition definition) =>
        _state.Value.Definitions.TryGetValue(itemId, out definition!);

    public bool IsEquipment(ushort itemId) =>
        TryGetDefinition(itemId, out var definition) && definition.IsEquipment;

    public bool TryGetPurchasePrice(ushort itemId, out int price)
    {
        price = 0;
        if (!TryGetDefinition(itemId, out var definition))
        {
            return false;
        }

        // NPC shops display and charge the item's raw PVF [price]. The
        // equipment/pricetable.tbl rate is only applied when selling an item
        // back to an NPC and must not be reused for purchases. Some scripts
        // intentionally omit [price]; the original shop semantics treat those
        // entries as free rather than rejecting the transaction.
        price = Math.Max(0, definition.Price ?? 0);
        return true;
    }

    public bool TryGetSellPrice(ushort itemId, out int price)
    {
        price = 0;
        if (!TryGetDefinition(itemId, out var definition)
            || definition.Price is not int cachedPrice
            || cachedPrice < 0)
        {
            return false;
        }

        price = checked((int)((long)cachedPrice
            * _state.Value.PriceRates.NormalSellRatePerMille
            / 1_000));
        return true;
    }

    public bool TryCalculateDisjointSellValue(ushort itemId, out int value)
    {
        value = 0;
        if (!TryGetDefinition(itemId, out var definition)
            || definition.Price is not int configuredPrice
            || configuredPrice < 0)
        {
            return false;
        }

        // DisJoint::GetResult raises the configured price by ten percent before
        // applying the normal equipment price-table rate. Preserve that order
        // because both divisions truncate in the original server.
        var adjustedPrice = checked((long)configuredPrice * 11 / 10);
        value = checked((int)(adjustedPrice
            * _state.Value.PriceRates.NormalSellRatePerMille
            / 1_000));
        return true;
    }

    public ushort GetInitialDurability(ushort itemId) =>
        TryGetDefinition(itemId, out var definition)
            && TryGetMaximumDurability(definition, out var maximumDurability)
                ? maximumDurability
                : (ushort)0;

    public ushort NormalizeDurability(ushort itemId, ushort durability) =>
        TryGetDefinition(itemId, out var definition)
            ? NormalizeDurability(definition, durability)
            : durability;

    public bool TryGetMaximumDurability(
        ushort itemId,
        out ushort durability)
    {
        durability = 0;
        return TryGetDefinition(itemId, out var definition)
            && TryGetMaximumDurability(definition, out durability);
    }

    public bool TryCalculateRepairPrice(
        ushort itemId,
        ushort durability,
        bool inDungeon,
        out int price)
    {
        price = 0;
        if (!TryGetDefinition(itemId, out var definition)
            || definition.Price is not int configuredPrice
            || configuredPrice < 0
            || definition.Grade is not int grade
            || grade < 0
            || !TryGetMaximumDurability(definition, out var maximumDurability))
        {
            return false;
        }

        var currentDurability = Math.Min(durability, maximumDurability);
        var missingDurability = maximumDurability - currentDurability;
        var gradeAdjustedPrice = checked((long)configuredPrice * (grade + 5) / 10);
        var repairRatePercent = inDungeon ? 11L : 10L;
        price = checked((int)(gradeAdjustedPrice
            * repairRatePercent
            * missingDurability
            / (100L * maximumDurability)));
        return true;
    }

    public bool TryCalculateSellPrice(
        ushort itemId,
        ushort durability,
        out int price)
    {
        if (!TryGetSellPrice(itemId, out var fullDurabilitySellPrice)
            || !TryGetDefinition(itemId, out var definition))
        {
            price = 0;
            return false;
        }

        if (!TryGetMaximumDurability(definition, out var maximumDurability))
        {
            price = fullDurabilitySellPrice;
            return true;
        }

        var currentDurability = Math.Min(durability, maximumDurability);
        // Match sub_45D230 in the 2008 client: apply the price-table rate first,
        // then scale that truncated value by current durability.
        price = checked((int)((long)fullDurabilitySellPrice
            * currentDurability
            / maximumDurability));
        return true;
    }

    private static ushort NormalizeDurability(
        ItemDefinition definition,
        ushort durability)
    {
        if (!TryGetMaximumDurability(definition, out var maximumDurability))
        {
            return durability;
        }

        // Zero is a real broken-equipment state. Legacy placeholder values such
        // as 1000 are above the PVF maximum and are clamped to that maximum.
        return durability > maximumDurability
            ? maximumDurability
            : durability;
    }

    private static bool TryGetMaximumDurability(
        ItemDefinition definition,
        out ushort durability)
    {
        durability = 0;
        if (definition.InventoryCategory != ItemInventoryCategory.Equipment
            || definition.Durability is not int configuredDurability
            || configuredDurability <= 0
            || configuredDurability > ushort.MaxValue)
        {
            return false;
        }

        durability = checked((ushort)configuredDurability);
        return true;
    }

    private CatalogState Load()
    {
        var definitions = new Dictionary<ushort, ItemDefinition>();
        var missingScripts = 0;
        LoadList(
            EquipmentListPath,
            "equipment",
            ItemScriptKind.Equipment,
            definitions,
            ref missingScripts);
        LoadList(
            StackableListPath,
            "stackable",
            ItemScriptKind.Stackable,
            definitions,
            ref missingScripts);
        var priceRates = LoadPriceRates();

        var categories = definitions.Values
            .GroupBy(definition => definition.InventoryCategory)
            .ToDictionary(group => group.Key, group => group.Count());
        _logger.LogInformation(
            "Cached {ItemCount} DFLegacy item definitions from {Source}: equipment={EquipmentCount}, consumable={ConsumableCount}, material={MaterialCount}, quest={QuestCount}, avatar={AvatarCount}, creature={CreatureCount}, lottery={LotteryCount}, ceraBoosters={CeraBoosterCount}, ceraPackages={CeraPackageCount}, spBooks={SkillPointBookCount}, experienceBooks={ExperienceBookCount}, attributeStones={AttributeStoneCount}, sellRate={SellRate}/1000, dungeonSellRate={DungeonSellRate}/1000, missingScripts={MissingScriptCount}.",
            definitions.Count,
            _scripts.SourceDescription,
            GetCategoryCount(categories, ItemInventoryCategory.Equipment),
            GetCategoryCount(categories, ItemInventoryCategory.Consumable),
            GetCategoryCount(categories, ItemInventoryCategory.Material),
            GetCategoryCount(categories, ItemInventoryCategory.Quest),
            GetCategoryCount(categories, ItemInventoryCategory.Avatar),
            GetCategoryCount(categories, ItemInventoryCategory.Creature),
            definitions.Values.Count(definition => definition.Lottery is not null),
            definitions.Values.Count(definition => definition.CeraBooster is not null),
            definitions.Values.Count(definition => definition.CeraPackage is not null),
            definitions.Values.Count(definition =>
                definition.IncreaseStatus?.Type == IncreaseStatusType.SkillPoints),
            definitions.Values.Count(definition =>
                definition.IncreaseStatus?.Type == IncreaseStatusType.Experience),
            definitions.Values.Count(definition =>
                definition.IncreaseStatus is
                {
                    Type: not IncreaseStatusType.SkillPoints
                        and not IncreaseStatusType.Experience
                }),
            priceRates.NormalSellRatePerMille,
            priceRates.DungeonSellRatePerMille,
            missingScripts);

        return new CatalogState(
            new ReadOnlyDictionary<ushort, ItemDefinition>(definitions),
            definitions.Values.Count(definition => definition.Lottery is not null),
            definitions.Values.Count(definition => definition.CeraBooster is not null),
            definitions.Values.Count(definition => definition.CeraPackage is not null),
            definitions.Values.Count(definition =>
                definition.IncreaseStatus?.Type == IncreaseStatusType.SkillPoints),
            definitions.Values.Count(definition =>
                definition.IncreaseStatus?.Type == IncreaseStatusType.Experience),
            definitions.Values.Count(definition =>
                definition.IncreaseStatus is
                {
                    Type: not IncreaseStatusType.SkillPoints
                        and not IncreaseStatusType.Experience
                }),
            priceRates);
    }

    private PriceRates LoadPriceRates()
    {
        if (_scripts.FileExists(EquipmentPriceTablePath))
        {
            foreach (var sourceLine in _scripts.ReadLines(EquipmentPriceTablePath))
            {
                var line = sourceLine.Split("//", 2, StringSplitOptions.None)[0].Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                if (!line.StartsWith("[", StringComparison.Ordinal))
                {
                    continue;
                }

                var values = IntegerPattern.Matches(line)
                    .Select(match => int.Parse(match.Value))
                    .ToArray();
                if (values.Length >= 3
                    && values[0] >= 0
                    && values[1] >= 0
                    && values[2] >= 0)
                {
                    return new PriceRates(values[0], values[1], values[2]);
                }
            }
        }

        _logger.LogWarning(
            "Item price rates were not found in {PriceTablePath}; using the compatibility rate {DefaultRate}/1000.",
            EquipmentPriceTablePath,
            DefaultSellPriceRatePerMille);
        return new PriceRates(
            DefaultSellPriceRatePerMille,
            DefaultSellPriceRatePerMille,
            DefaultSellPriceRatePerMille);
    }

    private void LoadList(
        string listPath,
        string scriptRoot,
        ItemScriptKind scriptKind,
        Dictionary<ushort, ItemDefinition> definitions,
        ref int missingScripts)
    {
        if (!_scripts.FileExists(listPath))
        {
            _logger.LogWarning(
                "Item list {ListPath} was not found in {Source}.",
                listPath,
                _scripts.SourceDescription);
            return;
        }

        foreach (var line in _scripts.ReadLines(listPath, ScriptEncoding))
        {
            var match = ListEntryPattern.Match(line);
            if (!match.Success)
            {
                continue;
            }

            if (!ushort.TryParse(match.Groups["id"].Value, out var itemId))
            {
                throw new InvalidDataException(
                    $"Item id '{match.Groups["id"].Value}' in {listPath} is outside the DFLegacy u16 range.");
            }

            var relativePath = match.Groups["path"].Value.Replace('\\', '/').TrimStart('/');
            var scriptPath = relativePath.StartsWith(
                $"{scriptRoot}/",
                StringComparison.OrdinalIgnoreCase)
                ? relativePath
                : $"{scriptRoot}/{relativePath}";
            if (definitions.ContainsKey(itemId))
            {
                throw new InvalidDataException(
                    $"Item id {itemId} is duplicated by '{scriptPath}' in {listPath}.");
            }

            string text;
            if (_scripts.FileExists(scriptPath))
            {
                text = _scripts.ReadAllTextUncached(scriptPath, ScriptEncoding);
            }
            else
            {
                text = string.Empty;
                missingScripts++;
            }

            var typeTag = ReadToken(
                scriptKind == ItemScriptKind.Equipment
                    ? EquipmentTypePattern
                    : StackableTypePattern,
                text);
            definitions.Add(
                itemId,
                new ItemDefinition(
                    itemId,
                    scriptPath,
                    ReadString(NamePattern, text),
                    scriptKind,
                    ClassifyInventory(scriptKind, typeTag),
                    typeTag,
                    ParseAttachType(ReadToken(AttachTypePattern, text)),
                    ReadInteger(GradePattern, text),
                    ReadInteger(RarityPattern, text),
                    ReadInteger(WeightPattern, text),
                    ReadInteger(MinimumLevelPattern, text),
                    ReadInteger(MaximumLevelPattern, text),
                    ReadInteger(CreationRatePattern, text),
                    ReadInteger(StackLimitPattern, text),
                    ReadInteger(MaximumHavingCountPattern, text),
                    ReadInteger(CashPattern, text),
                     ReadInteger(PricePattern, text),
                     ReadInteger(ValuePattern, text),
                     ReadInteger(DurabilityPattern, text),
                     ReadInteger(CreatureSubTypePattern, text),
                     ReadInteger(CreatureOutputIndexPattern, text),
                     ReadInteger(CreatureSpeciesPattern, text),
                     ReadLottery(typeTag, text),
                     ReadInteger(LotteryUseCostPattern, text),
                     ReadIncreaseStatusEffect(scriptPath),
                     ReadInteger(CreatureMinimumLevelPattern, text),
                     ReadDecimal(CreatureExperienceAmountRatePattern, text),
                     ReadCeraBooster(typeTag, text, scriptPath),
                     ReadCeraPackage(typeTag, text, scriptPath),
                     ReadInteger(InventoryLimitPattern, text)));
        }
    }

    private static CeraPackageDefinition? ReadCeraPackage(
        string typeTag,
        string text,
        string scriptPath)
    {
        if (typeTag != "cera package")
        {
            return null;
        }

        var dataMatch = PackageDataPattern.Match(text);
        if (!dataMatch.Success)
        {
            throw new InvalidDataException(
                $"Cera package '{scriptPath}' has no [package data] block.");
        }

        var data = string.Join(
            '\n',
            dataMatch.Groups["value"].Value
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split("//", 2, StringSplitOptions.None)[0]));
        var values = IntegerPattern.Matches(data)
            .Select(match => long.Parse(match.Value, CultureInfo.InvariantCulture))
            .ToArray();
        if (values.Length == 0 || values.Length % 2 != 0)
        {
            throw new InvalidDataException(
                $"Cera package '{scriptPath}' has an invalid [package data] block.");
        }

        var rewards = new List<CeraPackageRewardDefinition>(values.Length / 2);
        for (var index = 0; index < values.Length; index += 2)
        {
            if (values[index] is <= 0 or > ushort.MaxValue
                || values[index + 1] is <= 0 or > uint.MaxValue)
            {
                throw new InvalidDataException(
                    $"Cera package '{scriptPath}' has an invalid reward pair "
                    + $"'{values[index]} {values[index + 1]}'.");
            }

            rewards.Add(new CeraPackageRewardDefinition(
                checked((ushort)values[index]),
                checked((uint)values[index + 1])));
        }

        return new CeraPackageDefinition(rewards.AsReadOnly());
    }

    private static CeraBoosterDefinition? ReadCeraBooster(
        string typeTag,
        string text,
        string scriptPath)
    {
        if (typeTag != "cera booster")
        {
            return null;
        }

        var infoMatch = BoosterInfoPattern.Match(text);
        if (!infoMatch.Success)
        {
            throw new InvalidDataException(
                $"Cera booster '{scriptPath}' has no [booster info] block.");
        }

        var lines = infoMatch.Groups["value"].Value
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split("//", 2, StringSplitOptions.None)[0].Trim())
            .Where(line => line.Length != 0)
            .ToArray();
        var groups = new List<CeraBoosterRewardGroup>();
        for (var index = 0; index < lines.Length; index++)
        {
            var open = BoosterGroupTagPattern.Match(lines[index]);
            if (!open.Success || open.Groups["closing"].Success)
            {
                throw new InvalidDataException(
                    $"Cera booster '{scriptPath}' has an invalid group tag '{lines[index]}'.");
            }

            var tag = open.Groups["tag"].Value.ToLowerInvariant();
            var kind = ParseCeraBoosterRewardKind(tag);
            if (++index >= lines.Length
                || !int.TryParse(lines[index], out var drawCount)
                || drawCount <= 0)
            {
                throw new InvalidDataException(
                    $"Cera booster '{scriptPath}' group [{tag}] has an invalid draw count.");
            }

            var rewards = new List<CeraBoosterRewardDefinition>();
            var totalWeight = 0;
            var closed = false;
            while (++index < lines.Length)
            {
                var close = BoosterGroupTagPattern.Match(lines[index]);
                if (close.Success && close.Groups["closing"].Success)
                {
                    if (!string.Equals(
                            close.Groups["tag"].Value,
                            tag,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            $"Cera booster '{scriptPath}' closes [{tag}] with '{lines[index]}'.");
                    }

                    closed = true;
                    break;
                }

                var values = IntegerPattern.Matches(lines[index])
                    .Select(match => long.Parse(match.Value, CultureInfo.InvariantCulture))
                    .ToArray();
                var isAvatar = kind is CeraBoosterRewardKind.Avatar
                    or CeraBoosterRewardKind.SpecialAvatar;
                if (values.Length < (isAvatar ? 5 : 3)
                    || values[0] is <= 0 or > ushort.MaxValue
                    || values[1] is <= 0 or > int.MaxValue
                    || values[2] is <= 0 or > uint.MaxValue
                    || isAvatar && (values[3] is < 0 or > int.MaxValue
                        || values[4] is < 0 or > ushort.MaxValue))
                {
                    throw new InvalidDataException(
                        $"Cera booster '{scriptPath}' has an invalid [{tag}] reward row '{lines[index]}'.");
                }

                var weight = checked((int)values[1]);
                totalWeight = checked(totalWeight + weight);
                rewards.Add(new CeraBoosterRewardDefinition(
                    kind,
                    checked((ushort)values[0]),
                    checked((uint)values[2]),
                    weight,
                    isAvatar ? checked((int)values[3]) : 0,
                    isAvatar ? checked((ushort)values[4]) : (ushort)0));
            }

            if (!closed || rewards.Count == 0)
            {
                throw new InvalidDataException(
                    $"Cera booster '{scriptPath}' group [{tag}] is empty or not closed.");
            }

            groups.Add(new CeraBoosterRewardGroup(
                kind,
                drawCount,
                rewards.AsReadOnly(),
                totalWeight));
        }

        if (groups.Count == 0)
        {
            throw new InvalidDataException(
                $"Cera booster '{scriptPath}' contains no reward groups.");
        }

        return new CeraBoosterDefinition(groups.AsReadOnly());
    }

    private static CeraBoosterRewardKind ParseCeraBoosterRewardKind(
        string tag) => tag switch
    {
        "avatar" => CeraBoosterRewardKind.Avatar,
        "special avatar" => CeraBoosterRewardKind.SpecialAvatar,
        "cera" => CeraBoosterRewardKind.Cera,
        "creature" => CeraBoosterRewardKind.Creature,
        "equipment" => CeraBoosterRewardKind.Equipment,
        "stackable" => CeraBoosterRewardKind.Stackable,
        "etc" => CeraBoosterRewardKind.Etc,
        "special creature" => CeraBoosterRewardKind.SpecialCreature,
        "emblem" => CeraBoosterRewardKind.Emblem,
        _ => throw new InvalidDataException($"Unsupported Cera booster reward tag '{tag}'.")
    };

    private static IncreaseStatusEffect? ReadIncreaseStatusEffect(string scriptPath) =>
        scriptPath.Replace('\\', '/') switch
        {
            var path when path.Equals(
                "stackable/book_exp1.stk",
                StringComparison.OrdinalIgnoreCase) => new(
                    IncreaseStatusType.Experience,
                    100),
            var path when path.Equals(
                "stackable/book_exp2.stk",
                StringComparison.OrdinalIgnoreCase) => new(
                    IncreaseStatusType.Experience,
                    1_000),
            var path when path.Equals(
                "stackable/book_exp3.stk",
                StringComparison.OrdinalIgnoreCase) => new(
                    IncreaseStatusType.Experience,
                    10_000),
            var path when path.Equals(
                "stackable/book_exp4.stk",
                StringComparison.OrdinalIgnoreCase) => new(
                    IncreaseStatusType.Experience,
                    100_000),
            var path when path.Equals(
                "stackable/book_skill1.stk",
                StringComparison.OrdinalIgnoreCase) => new(
                    IncreaseStatusType.SkillPoints,
                    5),
            var path when path.Equals(
                "stackable/book_skill2.stk",
                StringComparison.OrdinalIgnoreCase) => new(
                    IncreaseStatusType.SkillPoints,
                    20),
            var path when path.Equals(
                "stackable/stone_hp.stk",
                StringComparison.OrdinalIgnoreCase) => new(
                    IncreaseStatusType.MaximumHp,
                    25),
            var path when path.Equals(
                "stackable/stone_mp.stk",
                StringComparison.OrdinalIgnoreCase) => new(
                    IncreaseStatusType.MaximumMp,
                    25),
            var path when path.Equals(
                "stackable/stone_str.stk",
                StringComparison.OrdinalIgnoreCase) => new(
                    IncreaseStatusType.Strength,
                    5),
            var path when path.Equals(
                "stackable/stone_helth.stk",
                StringComparison.OrdinalIgnoreCase) => new(
                    IncreaseStatusType.Vitality,
                    5),
            var path when path.Equals(
                "stackable/stone_int.stk",
                StringComparison.OrdinalIgnoreCase) => new(
                    IncreaseStatusType.Intelligence,
                    5),
            var path when path.Equals(
                "stackable/stone_mind.stk",
                StringComparison.OrdinalIgnoreCase) => new(
                    IncreaseStatusType.Spirit,
                    5),
            var path when path.Equals(
                "stackable/stone_speed.stk",
                StringComparison.OrdinalIgnoreCase) => new(
                    IncreaseStatusType.MovementSpeed,
                    1),
            var path when path.Equals(
                "stackable/stone_allr.stk",
                StringComparison.OrdinalIgnoreCase) => new(
                    IncreaseStatusType.AllElementResistance,
                    1),
            _ => null
        };

    private static LotteryItemDefinition? ReadLottery(string typeTag, string text)
    {
        if (typeTag is not ("upgradable legacy" or "legacy"))
        {
            return null;
        }

        var match = IntDataPattern.Match(text);
        if (!match.Success)
        {
            return null;
        }

        var payload = Regex.Replace(
            match.Groups["value"].Value,
            @"//.*$",
            string.Empty,
            RegexOptions.Multiline);
        var values = IntegerPattern.Matches(payload)
            .Select(value => long.Parse(value.Value))
            .ToArray();
        var valueStride = typeTag == "upgradable legacy" ? 3 : 2;
        var headerLength = typeTag == "upgradable legacy" ? 2 : 1;
        if (values.Length < headerLength
            || (values.Length - headerLength) % valueStride != 0
            || !TryCreateLotteryReward(
                values[0],
                typeTag == "upgradable legacy" ? values[1] : 1,
                weight: 0,
                out var fallback))
        {
            return null;
        }

        var rewards = new List<LotteryRewardDefinition>();
        for (var index = headerLength; index < values.Length; index += valueStride)
        {
            var count = typeTag == "upgradable legacy" ? values[index + 2] : 1;
            if (!TryCreateLotteryReward(
                    values[index],
                    count,
                    values[index + 1],
                    out var reward)
                || reward.Weight <= 0)
            {
                return null;
            }

            rewards.Add(reward);
        }

        return new LotteryItemDefinition(fallback, rewards.AsReadOnly());
    }

    private static bool TryCreateLotteryReward(
        long itemId,
        long countOrValue,
        long weight,
        out LotteryRewardDefinition reward)
    {
        reward = null!;
        if (itemId is < ushort.MinValue or > ushort.MaxValue
            || countOrValue is <= 0 or > uint.MaxValue
            || weight is < 0 or > int.MaxValue)
        {
            return false;
        }

        reward = new LotteryRewardDefinition(
            checked((ushort)itemId),
            checked((uint)countOrValue),
            checked((int)weight));
        return true;
    }

    private static ItemInventoryCategory ClassifyInventory(
        ItemScriptKind scriptKind,
        string typeTag)
    {
        if (scriptKind == ItemScriptKind.Equipment)
        {
            if (typeTag.EndsWith(" avatar", StringComparison.Ordinal)
                || typeTag == "aurora avatar")
            {
                return ItemInventoryCategory.Avatar;
            }

            if (typeTag == "creature" || typeTag.StartsWith("artifact ", StringComparison.Ordinal))
            {
                return ItemInventoryCategory.Creature;
            }

            return ItemInventoryCategory.Equipment;
        }

        return typeTag switch
        {
            "material" or "material expert job" => ItemInventoryCategory.Material,
            "quest" or "quest receive" => ItemInventoryCategory.Quest,
            "creature" or "feed" => ItemInventoryCategory.Creature,
            _ => ItemInventoryCategory.Consumable
        };
    }

    private static ItemAttachType ParseAttachType(string value) => value switch
    {
        "free" => ItemAttachType.Free,
        "trade" => ItemAttachType.Trade,
        "trade delete" => ItemAttachType.TradeDelete,
        "sealing" => ItemAttachType.Sealing,
        "sealing trade" => ItemAttachType.SealingTrade,
        "account" => ItemAttachType.Account,
        _ => ItemAttachType.Unknown
    };

    private static Encoding CreateScriptEncoding()
    {
        return PvfEncodings.Cp936Lossy();
    }

    private static int GetCategoryCount(
        IReadOnlyDictionary<ItemInventoryCategory, int> categories,
        ItemInventoryCategory category) =>
        categories.GetValueOrDefault(category);

    private static int? ReadInteger(Regex pattern, string text) =>
        int.TryParse(pattern.Match(text).Groups["value"].Value, out var value)
            ? value
            : null;

    private static decimal? ReadDecimal(Regex pattern, string text) =>
        decimal.TryParse(
            pattern.Match(text).Groups["value"].Value,
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var value)
                ? value
                : null;

    private static string ReadString(Regex pattern, string text)
    {
        var match = pattern.Match(text);
        return match.Success ? match.Groups["value"].Value.Trim() : string.Empty;
    }

    private static string ReadToken(Regex pattern, string text)
    {
        var match = pattern.Match(text);
        return match.Success
            ? match.Groups["value"].Value.Trim().ToLowerInvariant()
            : string.Empty;
    }

    private static Regex CreateIntegerTagPattern(string tag) => new(
        $@"^\s*\[{Regex.Escape(tag)}\]\s+(?<value>[+-]?\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline |
        RegexOptions.IgnoreCase);

    private static Regex CreateDecimalTagPattern(string tag) => new(
        $@"^\s*\[{Regex.Escape(tag)}\]\s+(?<value>[+-]?(?:\d+(?:\.\d+)?|\.\d+))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline |
        RegexOptions.IgnoreCase);

    private static Regex CreateStringTagPattern(string tag) => new(
        $@"^\s*\[{Regex.Escape(tag)}\]\s+`(?<value>[^`]*)`",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline |
        RegexOptions.IgnoreCase);

    private static Regex CreateTokenTagPattern(string tag) => new(
        $@"^\s*\[{Regex.Escape(tag)}\]\s+`?\[(?<value>[^\]]+)\]`?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline |
        RegexOptions.IgnoreCase);

    private sealed record CatalogState(
        IReadOnlyDictionary<ushort, ItemDefinition> Definitions,
        int LotteryCount,
        int CeraBoosterCount,
        int CeraPackageCount,
        int SkillPointBookCount,
        int ExperienceBookCount,
        int AttributeStoneCount,
        PriceRates PriceRates);

    private readonly record struct PriceRates(
        int NormalSellRatePerMille,
        int DungeonSellRatePerMille,
        int RepairRatePerMille);
}
