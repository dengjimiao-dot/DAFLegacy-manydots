using System.Text;
using System.Text.RegularExpressions;

namespace DFLegacy.Server;

public sealed record QuestDefinition(
    ushort QuestId,
    int Order,
    int MinimumLevel,
    int MaximumLevel,
    string Job,
    int GrowType,
    int NpcIndex,
    int CompleteNpcIndex,
    string Type,
    ushort[] PrerequisiteQuestIds,
    ushort[] CollisionQuestIds,
    bool CanGiveup,
    bool Repeatable,
    string RewardType,
    QuestRewardItemDefinition[] FixedRewardItems,
    QuestRewardItemDefinition[] SelectableRewardItems,
    int GoldReward,
    int CreatureKind = -1,
    int CreatureLevel = 0,
    int EvolutionCreatureKind = -1,
    uint InitialTrigger = 1,
    int AwakeningType = -1,
    string ScriptPath = "",
    int JobChangeQuest = 0,
    int GrowthReward = -1)
{
    private const int PackedTriggerBits = 9;
    private const uint PackedTriggerMask = (1u << PackedTriggerBits) - 1;

    public int[][] ConditionRows { get; init; } = [];

    public int SubType { get; init; } = -1;

    public ushort[][] PrerequisiteQuestGroups { get; init; } = [];

    public int[] AppearMap { get; init; } = [];

    public int DeleteNpcIndex { get; init; } = -1;

    public QuestClearRewardItemDefinition[] ClearRewardItems { get; init; } = [];

    public QuestMonsterRewardItemDefinition[] MonsterRewardItems { get; init; } = [];

    public QuestDependGiveItemDefinition[] DependGiveItems { get; init; } = [];

    public IReadOnlyList<QuestRewardItemDefinition> ResolveRewardItems(
        int characterJob,
        int characterGrowType,
        int selectionIndex)
    {
        if (RewardType.Length > 0 && RewardType != "item")
        {
            return [];
        }

        var rewards = FixedRewardItems
            .Where(reward => reward.Matches(characterJob, characterGrowType))
            .ToList();
        var selectable = SelectableRewardItems
            .Where(reward => reward.Matches(characterJob, characterGrowType))
            .ToArray();
        if (selectionIndex >= 0 && selectionIndex < selectable.Length)
        {
            rewards.Add(selectable[selectionIndex]);
        }

        return rewards;
    }

    public bool TryApplyTriggerAction(uint trigger, byte action, out uint updatedTrigger)
    {
        updatedTrigger = trigger;
        if (action == 1)
        {
            updatedTrigger = trigger == uint.MaxValue ? trigger : trigger + 1;
            return true;
        }

        var targetIndex = action switch
        {
            0x10 => 0,
            0x20 => 1,
            0x40 => 2,
            _ => -1
        };
        if (targetIndex < 0)
        {
            return false;
        }

        var shift = targetIndex * PackedTriggerBits;
        var current = (trigger >> shift) & PackedTriggerMask;
        var next = current == 0 ? 0 : current - 1;
        updatedTrigger = (trigger & ~(PackedTriggerMask << shift)) | (next << shift);
        return true;
    }

    public bool IsTriggerComplete(uint trigger) => trigger == 0;

    public bool TryGetGrowthReward(out byte chainType, out byte growNumber)
    {
        chainType = 0;
        growNumber = 0;
        if (RewardType is not "grow type" and not "awakening type")
        {
            return true;
        }

        var maximum = RewardType == "grow type" ? 4 : 15;
        if (GrowthReward is < 1 || GrowthReward > maximum)
        {
            return false;
        }

        chainType = RewardType == "grow type" ? (byte)1 : (byte)2;
        growNumber = checked((byte)GrowthReward);
        return true;
    }
}

public sealed record QuestRewardItemDefinition(
    ushort ItemId,
    uint Count,
    int Job = -1,
    int GrowType = -1)
{
    public bool Matches(int characterJob, int characterGrowType) =>
        (Job < 0 || Job == characterJob)
        && (GrowType < 0 || GrowType == (characterGrowType & 0x0F));
}

public sealed record QuestClearRewardItemDefinition(
    int DungeonId,
    int Difficulty,
    ushort ItemId,
    uint Count,
    int ChancePercent,
    uint MaximumCount);

public sealed record QuestMonsterRewardItemDefinition(
    int MonsterIndex,
    int DungeonId,
    int Difficulty,
    ushort ItemId,
    uint Count,
    int ChancePercent,
    uint MaximumCount);

public sealed record QuestDependGiveItemDefinition(
    ushort ItemId,
    uint Count);

public sealed class QuestCatalog
{
    public const ushort TutorialCompletionQuestId = 1016;

    private static readonly Regex ListEntryPattern = new(
        @"(?m)^\s*(\d+)\s+`([^`]+)`",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex IntegerPattern = new(
        @"-?\d+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SequencePathPattern = new(
        @"^(.*?)-(\d+)\.qst$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant |
        RegexOptions.IgnoreCase);
    private static readonly IReadOnlyDictionary<int, string> JobNames =
        new Dictionary<int, string>
        {
            [0] = "swordman",
            [1] = "fighter",
            [2] = "gunner",
            [3] = "mage",
            [4] = "priest"
        };

    private readonly ScriptFileSystem _scripts;
    private readonly ILogger<QuestCatalog> _logger;
    private readonly Lazy<IReadOnlyList<QuestDefinition>> _definitions;
    private readonly Lazy<IReadOnlyDictionary<ushort, int>> _completionMapping;

    public IReadOnlyDictionary<ushort, int> CompletionMapping => _completionMapping.Value;

    private IReadOnlyDictionary<ushort, int> LoadCompletionMapping()
    {
        const string path = "quest/questmappingtable.tbl";
        var result = new Dictionary<ushort, int>();
        if (!_scripts.FileExists(path))
        {
            _logger.LogWarning("Quest completion mapping is missing: {Path}.", path);
            return result;
        }
        var text = Regex.Replace(_scripts.ReadAllText(path, Encoding.Latin1), @"//[^\r\n]*", "");
        var usedIndices = new HashSet<int>();
        foreach (Match match in Regex.Matches(text, @"(?m)^\s*(\d+)\s+`[^`]*`\s+(-?\d+)"))
        {
            if (!ushort.TryParse(match.Groups[1].Value, out var questId)
                || !int.TryParse(match.Groups[2].Value, out var index)
                || index < 0 || index / 512 > 8 || index % 512 >= 256)
            {
                _logger.LogWarning("Unsupported DF2008 quest completion mapping row: {Row}.", match.Value);
                continue;
            }
            if (result.ContainsKey(questId) || !usedIndices.Add(index))
            {
                throw new InvalidDataException($"Duplicate quest completion mapping: {match.Value}");
            }
            result.Add(questId, index);
        }
        return result;
    }
    private readonly Lazy<IReadOnlyDictionary<ushort, ushort[]>>
        _implicitSequencePredecessors;

    public QuestCatalog(ScriptFileSystem scripts, ILogger<QuestCatalog> logger)
    {
        _scripts = scripts;
        _logger = logger;
        _completionMapping = new(LoadCompletionMapping, LazyThreadSafetyMode.ExecutionAndPublication);
        _definitions = new Lazy<IReadOnlyList<QuestDefinition>>(
            LoadDefinitions,
            LazyThreadSafetyMode.ExecutionAndPublication);
        _implicitSequencePredecessors =
            new Lazy<IReadOnlyDictionary<ushort, ushort[]>>(
                () => BuildImplicitSequencePredecessors(_definitions.Value),
                LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public IReadOnlyList<ushort> GetAcceptableQuestIds(
        CharacterRecord character,
        IReadOnlyCollection<CharacterQuestRecord> activeQuests,
        IReadOnlyCollection<ushort> completedQuestIds,
        int? preferredNpcIndex = null,
        ItemCatalog? itemCatalog = null)
    {
        var active = activeQuests.Select(quest => quest.QuestId).ToHashSet();
        var completed = completedQuestIds.ToHashSet();
        var eligible = _definitions.Value
            .Where(definition => IsAcceptable(
                definition,
                character,
                active,
                completed,
                itemCatalog))
            .OrderBy(definition =>
                preferredNpcIndex.HasValue && definition.NpcIndex == preferredNpcIndex.Value ? 0 : 1)
            .ThenBy(definition => definition.Order)
            .ToArray();

        if (eligible.Length > byte.MaxValue)
        {
            _logger.LogWarning(
                "Truncated acceptable quest list from {Count} to the DFLegacy limit of 255.",
                eligible.Length);
        }

        return eligible
            .Take(byte.MaxValue)
            .Select(definition => definition.QuestId)
            .ToArray();
    }

    public bool TryGetDefinition(ushort questId, out QuestDefinition definition)
    {
        definition = _definitions.Value.FirstOrDefault(entry => entry.QuestId == questId)!;
        return definition is not null;
    }

    public static bool IsTutorialCompletionQuestDefinition(QuestDefinition definition) =>
        definition.QuestId == TutorialCompletionQuestId
        && NormalizeTag(definition.Type) == "meet npc"
        && definition.NpcIndex == 2
        && definition.CompleteNpcIndex == 2;

    private bool IsAcceptable(
        QuestDefinition definition,
        CharacterRecord character,
        ISet<ushort> active,
        ISet<ushort> completed,
        ItemCatalog? itemCatalog)
    {
        if (IsTutorialCompletionQuestDefinition(definition)
            || active.Contains(definition.QuestId)
            || (!definition.Repeatable && completed.Contains(definition.QuestId))
            || character.Level < definition.MinimumLevel
            || character.Level > definition.MaximumLevel
            || !MatchesJob(definition.Job, character.Job)
            || (definition.GrowType >= 0
                && definition.GrowType != (character.GrowType & 0x0F)))
        {
            return false;
        }

        var primaryGrowType = character.GrowType & 0x0F;
        var awakeningType = Math.Max(
            character.AwakeningType,
            Math.Max(0, character.GrowType >> 4));
        if ((definition.JobChangeQuest == 1 && primaryGrowType != 0)
            || (definition.JobChangeQuest == 2
                && (primaryGrowType == 0 || awakeningType != 0)))
        {
            return false;
        }

        var prerequisiteGroups = definition.PrerequisiteQuestGroups
            .Where(group => group.Length > 0)
            .ToArray();
        if (prerequisiteGroups.Length == 0
            && definition.PrerequisiteQuestIds.Length > 0)
        {
            prerequisiteGroups = [definition.PrerequisiteQuestIds];
        }

        if (prerequisiteGroups.Length > 0
            && !prerequisiteGroups.Any(group =>
                group.All(completed.Contains)))
        {
            return false;
        }

        if (_implicitSequencePredecessors.Value.TryGetValue(
                definition.QuestId,
                out var predecessors)
            && predecessors.Length > 0
            && !predecessors.Any(completed.Contains))
        {
            return false;
        }

        if (definition.CreatureKind >= 0
            && itemCatalog is not null
            && !HasQualifyingCreature(definition, character, itemCatalog))
        {
            return false;
        }

        return !definition.CollisionQuestIds.Any(collision =>
            active.Contains(collision) || completed.Contains(collision));
    }

    public static bool HasQualifyingCreature(
        QuestDefinition definition,
        CharacterRecord character,
        ItemCatalog itemCatalog) =>
        definition.CreatureKind < 0
        || (character.CreatureInventory ?? []).Any(item =>
            item.CountOrValue != 0
            && (item.CreatureLevel ?? 1) >= Math.Max(1, definition.CreatureLevel)
            && itemCatalog.TryGetDefinition(item.ItemId, out var itemDefinition)
            && CharacterCreatureInventoryLayout.IsCreature(itemDefinition)
            && itemDefinition.CreatureSubType != 1
            && itemDefinition.CreatureSpecies == definition.CreatureKind);

    private static bool MatchesJob(string questJob, int characterJob)
    {
        var normalized = NormalizeTag(questJob);
        if (normalized.Length == 0 || normalized == "all")
        {
            return true;
        }

        return JobNames.TryGetValue(characterJob, out var jobName)
            && normalized == jobName;
    }

    private IReadOnlyList<QuestDefinition> LoadDefinitions()
    {
        const string questRoot = "quest";
        const string listPath = "quest/quest.lst";
        if (!_scripts.FileExists(listPath))
        {
            _logger.LogWarning(
                "Quest list was not found in {Source}.",
                _scripts.SourceDescription);
            return [];
        }

        var result = new List<QuestDefinition>();
        var listText = _scripts.ReadAllText(listPath, Encoding.Latin1);
        var order = 0;
        foreach (Match match in ListEntryPattern.Matches(listText))
        {
            if (!int.TryParse(match.Groups[1].Value, out var rawQuestId)
                || rawQuestId is < 0 or > ushort.MaxValue)
            {
                continue;
            }

            var relativePath = match.Groups[2].Value.Replace('\\', '/').TrimStart('/');
            var questPath = $"{questRoot}/{relativePath}";
            if (!_scripts.FileExists(questPath))
            {
                continue;
            }

            try
            {
                var lines = _scripts.ReadAllLines(questPath, Encoding.Latin1);
                var levels = ParseIntegers(ReadScalar(lines, "level"));
                var minimumLevel = levels.Length > 0 ? Math.Max(1, levels[0]) : 1;
                var maximumLevel = levels.Length > 1 ? Math.Max(minimumLevel, levels[1]) : 99;
                var grade = NormalizeTag(ReadScalar(lines, "grade"));
                var rewardType = NormalizeTag(ReadScalar(lines, "reward type"));
                var questType = NormalizeTag(ReadScalar(lines, "type"));
                var conditionIntegers = ParseIntegers(ReadBlock(lines, "int data"));
                var rewardIntegers = ParseIntegers(ReadBlock(lines, "reward int data"));
                var fixedRewards = ParseRewardItems(ReadBlock(lines, "reward int data"));
                var selectableRewards = ParseRewardItems(
                    ReadBlock(lines, "reward selection int data"));
                var prerequisiteGroups = ReadIntegerGroups(
                        lines,
                        "pre required quest")
                    .Select(group => group
                        .Where(questId => questId is > 0 and <= ushort.MaxValue)
                        .Select(questId => checked((ushort)questId))
                        .Distinct()
                        .ToArray())
                    .Where(group => group.Length > 0)
                    .ToArray();
                var collisionQuestIds = ReadIntegerGroups(
                        lines,
                        "collision quest")
                    .SelectMany(group => group)
                    .Where(questId => questId is > 0 and <= ushort.MaxValue)
                    .Select(questId => checked((ushort)questId))
                    .Distinct()
                    .ToArray();
                result.Add(new QuestDefinition(
                    (ushort)rawQuestId,
                    order++,
                    minimumLevel,
                    maximumLevel,
                    ReadScalar(lines, "job"),
                    ParseFirstInteger(ReadScalar(lines, "grow type"), -1),
                    ParseFirstInteger(ReadScalar(lines, "npc index"), -1),
                    ParseFirstInteger(ReadScalar(lines, "complete npc index"), -1),
                    questType,
                    prerequisiteGroups
                        .SelectMany(group => group)
                        .Distinct()
                        .ToArray(),
                    collisionQuestIds,
                    !HasTag(lines, "cant giveup"),
                    grade is "daily" or "normaly repeat" or "special daily",
                    rewardType,
                    fixedRewards.Where(reward => reward.ItemId != 0).ToArray(),
                    selectableRewards.Where(reward => reward.ItemId != 0).ToArray(),
                    fixedRewards
                        .Where(reward => reward.ItemId == 0)
                        .Aggregate(0, (gold, reward) => checked(gold + (int)reward.Count)),
                    ParseFirstInteger(ReadScalar(lines, "creature kind"), -1),
                    Math.Max(0, ParseFirstInteger(
                        ReadScalar(lines, "creature level"),
                        0)),
                    rewardType == "creature evolution" && rewardIntegers.Length > 0
                        ? rewardIntegers[0]
                        : -1,
                    questType == "hunt monster"
                        ? PackHuntTargetCounts(conditionIntegers)
                        : 1,
                    rewardType == "awakening type" && rewardIntegers.Length > 0
                        ? Math.Max(0, rewardIntegers[0])
                        : -1,
                    questPath,
                    ParseFirstInteger(ReadScalar(lines, "job change quest"), 0),
                    (rewardType is "grow type" or "awakening type")
                        && rewardIntegers.Length > 0
                            ? rewardIntegers[0]
                            : -1)
                {
                    ConditionRows = ReadIntegerRows(lines, "int data"),
                    SubType = ParseFirstInteger(ReadScalar(lines, "sub type"), -1),
                    PrerequisiteQuestGroups = prerequisiteGroups,
                    AppearMap = ParseIntegers(ReadScalar(lines, "appear map")),
                    DeleteNpcIndex = ParseFirstInteger(ReadScalar(lines, "delete npc index"), -1),
                    ClearRewardItems = ParseClearRewardItems(
                        ReadIntegerRows(lines, "clear reward item")),
                    MonsterRewardItems = ParseMonsterRewardItems(
                        ReadIntegerRows(lines, "monster reward item")),
                    DependGiveItems = ParseDependGiveItems(
                        ReadIntegerRows(lines, "depend give item"))
                });
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Could not parse quest {QuestId} at {Path}.",
                    rawQuestId,
                    questPath);
            }
        }

        _logger.LogInformation(
            "Loaded {Count} DFLegacy quest definitions from {Path}.",
            result.Count,
            _scripts.SourceDescription);
        return result;
    }

    private static IReadOnlyDictionary<ushort, ushort[]>
        BuildImplicitSequencePredecessors(
            IEnumerable<QuestDefinition> definitions)
    {
        var candidates = definitions
            .Where(definition => definition.JobChangeQuest == 2)
            .Select(definition =>
            {
                var match = SequencePathPattern.Match(definition.ScriptPath);
                return new
                {
                    Definition = definition,
                    Prefix = match.Success
                        ? match.Groups[1].Value.ToLowerInvariant()
                        : string.Empty,
                    Step = match.Success
                        && int.TryParse(match.Groups[2].Value, out var step)
                            ? step
                            : -1
                };
            })
            .Where(candidate => candidate.Step > 0 && candidate.Prefix.Length > 0)
            .GroupBy(candidate => new
            {
                candidate.Prefix,
                Job = NormalizeTag(candidate.Definition.Job),
                candidate.Definition.GrowType,
                candidate.Definition.JobChangeQuest
            });
        var result = new Dictionary<ushort, ushort[]>();
        foreach (var sequence in candidates)
        {
            var steps = sequence
                .GroupBy(candidate => candidate.Step)
                .OrderBy(group => group.Key)
                .ToArray();
            for (var index = 1; index < steps.Length; index++)
            {
                var predecessors = steps[index - 1]
                    .Select(candidate => candidate.Definition.QuestId)
                    .Distinct()
                    .OrderBy(questId => questId)
                    .ToArray();
                foreach (var candidate in steps[index])
                {
                    result[candidate.Definition.QuestId] = predecessors;
                }
            }
        }

        return result;
    }

    private static string ReadScalar(IReadOnlyList<string> lines, string tag)
    {
        var prefix = $"[{tag}]";
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimStart();
            if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return StripComment(line[prefix.Length..]).Trim();
        }

        return string.Empty;
    }

    private static string ReadBlock(IReadOnlyList<string> lines, string tag)
    {
        var start = $"[{tag}]";
        var end = $"[/{tag}]";
        var values = new StringBuilder();
        var reading = false;
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimStart();
            if (!reading)
            {
                if (!line.StartsWith(start, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                reading = true;
                values.Append(' ').Append(StripComment(line[start.Length..]));
                continue;
            }

            if (line.StartsWith(end, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (line.StartsWith("[", StringComparison.Ordinal))
            {
                break;
            }

            values.Append(' ').Append(StripComment(line));
        }

        return values.ToString();
    }

    private static int[][] ReadIntegerGroups(
        IReadOnlyList<string> lines,
        string tag)
    {
        var start = $"[{tag}]";
        var end = $"[/{tag}]";
        var groups = new List<int[]>();
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index].TrimStart();
            if (!line.StartsWith(start, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var values = new List<int>();
            values.AddRange(ParseIntegers(StripComment(line[start.Length..])));
            for (index++; index < lines.Count; index++)
            {
                line = lines[index].TrimStart();
                if (line.StartsWith(end, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                if (line.StartsWith("[", StringComparison.Ordinal))
                {
                    index--;
                    break;
                }

                values.AddRange(ParseIntegers(StripComment(line)));
            }

            if (values.Count > 0)
            {
                groups.Add(values.ToArray());
            }
        }

        return groups.ToArray();
    }

    private static int[][] ReadIntegerRows(
        IReadOnlyList<string> lines,
        string tag)
    {
        var start = $"[{tag}]";
        var end = $"[/{tag}]";
        var rows = new List<int[]>();
        var reading = false;
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimStart();
            if (!reading)
            {
                if (!line.StartsWith(start, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                reading = true;
                AddIntegerRow(StripComment(line[start.Length..]), rows);
                continue;
            }

            if (line.StartsWith(end, StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("[", StringComparison.Ordinal))
            {
                break;
            }

            AddIntegerRow(StripComment(line), rows);
        }

        return rows.ToArray();
    }

    private static void AddIntegerRow(string value, ICollection<int[]> rows)
    {
        var integers = ParseIntegers(value);
        if (integers.Length > 0)
        {
            rows.Add(integers);
        }
    }

    private static bool HasTag(IReadOnlyList<string> lines, string tag)
    {
        var prefix = $"[{tag}]";
        return lines.Any(line => line.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static string StripComment(string value)
    {
        var commentIndex = value.IndexOf("//", StringComparison.Ordinal);
        return commentIndex >= 0 ? value[..commentIndex] : value;
    }

    private static string NormalizeTag(string value) => value
        .Trim()
        .Trim('`')
        .Trim()
        .Trim('[', ']')
        .Trim()
        .ToLowerInvariant();

    private static int[] ParseIntegers(string value) => IntegerPattern
        .Matches(value)
        .Select(match => int.Parse(match.Value))
        .ToArray();

    private static int ParseFirstInteger(string value, int fallback)
    {
        var match = IntegerPattern.Match(value);
        return match.Success && int.TryParse(match.Value, out var parsed) ? parsed : fallback;
    }

    private static uint PackHuntTargetCounts(IReadOnlyList<int> values)
    {
        const int packedTriggerBits = 9;
        var trigger = 0u;
        var targetIndex = 0;
        for (var index = 3; index < values.Count && targetIndex < 3; index += 4)
        {
            var count = checked((uint)Math.Clamp(values[index], 0, 511));
            trigger |= count << (targetIndex * packedTriggerBits);
            targetIndex++;
        }

        return trigger;
    }

    private static ushort[] ParseQuestIds(string value) => ParseIntegers(value)
        .Where(questId => questId is > 0 and <= ushort.MaxValue)
        .Select(questId => (ushort)questId)
        .Distinct()
        .ToArray();

    private static QuestRewardItemDefinition[] ParseRewardItems(string value)
    {
        var tokens = value.Split(
            [' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries);
        var rewards = new List<QuestRewardItemDefinition>();
        var index = 0;
        while (index < tokens.Length)
        {
            if (!int.TryParse(tokens[index++], out var rawItemId))
            {
                continue;
            }

            var job = -1;
            var growType = -1;
            if (index < tokens.Length && NormalizeTag(tokens[index]) == "job")
            {
                index++;
                if (index < tokens.Length
                    && int.TryParse(tokens[index], out var parsedJob))
                {
                    job = parsedJob;
                }
                index++;

                if (index < tokens.Length
                    && int.TryParse(tokens[index], out var parsedGrowType))
                {
                    growType = parsedGrowType;
                }
                index++;
            }

            if (index >= tokens.Length || !long.TryParse(tokens[index++], out var rawCount))
            {
                continue;
            }

            if (rawItemId is < 0 or > ushort.MaxValue || rawCount is <= 0 or > uint.MaxValue)
            {
                continue;
            }

            rewards.Add(new QuestRewardItemDefinition(
                (ushort)rawItemId,
                (uint)rawCount,
                job,
                growType));
        }

        return rewards.ToArray();
    }

    private static QuestClearRewardItemDefinition[] ParseClearRewardItems(
        IEnumerable<int[]> rows) => rows
        .Where(row => row.Length >= 6
            && row[0] >= -1
            && row[2] is > 0 and <= ushort.MaxValue
            && row[3] > 0)
        .Select(row => new QuestClearRewardItemDefinition(
            row[0],
            row[1],
            checked((ushort)row[2]),
            checked((uint)row[3]),
            Math.Clamp(row[4], 0, 100),
            ParseMaximumCount(row[5])))
        .ToArray();

    private static QuestMonsterRewardItemDefinition[] ParseMonsterRewardItems(
        IEnumerable<int[]> rows) => rows
        .Where(row => row.Length >= 7
            && row[0] >= 0
            && row[1] >= -1
            && row[3] is > 0 and <= ushort.MaxValue
            && row[4] > 0)
        .Select(row => new QuestMonsterRewardItemDefinition(
            row[0],
            row[1],
            row[2],
            checked((ushort)row[3]),
            checked((uint)row[4]),
            Math.Clamp(row[5], 0, 100),
            ParseMaximumCount(row[6])))
        .ToArray();

    private static QuestDependGiveItemDefinition[] ParseDependGiveItems(
        IEnumerable<int[]> rows) => rows
        .Where(row => row.Length >= 2
            && row[0] is > 0 and <= ushort.MaxValue
            && row[1] > 0)
        .Select(row => new QuestDependGiveItemDefinition(
            checked((ushort)row[0]),
            checked((uint)row[1])))
        .ToArray();

    private static uint ParseMaximumCount(int value) =>
        value < 0 ? uint.MaxValue : checked((uint)value);
}
