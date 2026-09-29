using System.Text;
using System.Text.RegularExpressions;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

public sealed record SkillDefinition(
    byte SkillId,
    bool IsActive,
    bool IsGuildSkill,
    int RawGroup,
    int NumberOfGrowTypes,
    int MaximumLevel,
    int RequiredLevel,
    int RequiredLevelRange,
    int[] GrowTypeMaximumLevels,
    int[] SecondGrowTypeMaximumLevels,
    int[] PurchaseCosts,
    int[] Prerequisites,
    ushort ConsumedItemId,
    uint ConsumedItemCount)
{
    public int MaximumLevelFor(int growType, int awakeningType = 0)
    {
        if (growType < 0)
        {
            return 0;
        }

        var firstGrowMaximum = GrowTypeMaximumLevels.Length == 0
            ? MaximumLevel
            : growType < GrowTypeMaximumLevels.Length
            ? Math.Min(MaximumLevel, GrowTypeMaximumLevels[growType])
            : 0;

        // DF2008 stores two second-growth limits for each of its five
        // first-growth branches. As in the original server, a zero second-
        // growth limit falls back to the ordinary first-growth limit.
        const int secondGrowTypeCount = 2;
        if (awakeningType is > 0 and <= secondGrowTypeCount)
        {
            var index = growType * secondGrowTypeCount + awakeningType - 1;
            if (index >= 0 && index < SecondGrowTypeMaximumLevels.Length)
            {
                var secondGrowMaximum = SecondGrowTypeMaximumLevels[index];
                if (secondGrowMaximum > 0)
                {
                    return Math.Min(MaximumLevel, secondGrowMaximum);
                }
            }
        }

        return firstGrowMaximum;
    }

    public int CostFor(int currentLevel, int targetLevel)
    {
        if (targetLevel <= currentLevel || PurchaseCosts.Length == 0)
        {
            return 0;
        }

        var cost = 0;
        for (var level = currentLevel; level < targetLevel; level++)
        {
            cost += PurchaseCosts[Math.Min(level, PurchaseCosts.Length - 1)];
        }

        return cost;
    }

    public int RequiredCharacterLevelFor(int targetLevel) =>
        RequiredLevel + Math.Max(0, targetLevel - 1) * RequiredLevelRange;
}

public sealed record CharacterGrantedSkillChange(
    byte SkillId,
    byte Level,
    byte SkillClass);

public sealed record SkillLevelRefund(
    byte SkillId,
    byte PreviousLevel,
    byte CurrentLevel,
    int SkillPoints);

public sealed class SkillCatalog
{
    public const byte QuickSlotStart = 0;
    public const byte QuickSlotCount = 6;
    public const byte SkillClassCount = 5;
    public const byte SkillClassSlotCount = 42;
    public const byte SkillClassStart = QuickSlotStart + QuickSlotCount;
    public const byte GuildSkillStart = 204;
    public const byte SkillSlotEnd = 216;

    private static readonly Regex ListEntryPattern = new(
        @"(?m)^\s*(\d+)\s+`([^`]+)`",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex IntegerPattern = new(
        @"-?\d+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SkillBlockPattern = new(
        @"\[skill\](?<body>.*?)\[/skill\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant |
        RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex AwakeningSkillBlockPattern = new(
        @"\[awakening\s+skill\](?<body>.*?)\[/awakening\s+skill\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant |
        RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex SkillRowPattern = new(
        @"(?m)^\s*(?<id>\d+)\s+(?<level>-?\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GrowTypePattern = new(
        @"(?im)^\s*\[growtype\s+(?<growType>\d+)\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AwakeningPattern = new(
        @"(?im)^\s*\[awakening\s+(?<awakeningType>\d+)\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ScriptFileSystem _scripts;
    private readonly ILogger<SkillCatalog> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<int, IReadOnlyDictionary<byte, SkillDefinition>> _jobs = [];
    private readonly Lazy<IReadOnlyDictionary<int, CharacterSkillGrantSet>> _grantedSkills;

    public SkillCatalog(ScriptFileSystem scripts, ILogger<SkillCatalog> logger)
    {
        _scripts = scripts;
        _logger = logger;
        _grantedSkills = new Lazy<IReadOnlyDictionary<int, CharacterSkillGrantSet>>(
            LoadGrantedSkills,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public bool TryGetDefinition(int job, byte skillId, out SkillDefinition definition) =>
        GetJob(job).TryGetValue(skillId, out definition!);

    public IReadOnlyList<CharacterGrantedSkillChange> GetGrowTypeSkillChanges(
        int job,
        int growType)
    {
        if (!_grantedSkills.Value.TryGetValue(job, out var grantSet)
            || !grantSet.GrowTypes.TryGetValue(
                (growType & 0x0F) + 1,
                out var growTypeGrant))
        {
            return [];
        }

        return CreateGrowthSkillChanges(job, growTypeGrant.Skills);
    }

    public IReadOnlyList<CharacterGrantedSkillChange> GetAwakeningSkillChanges(
        int job,
        int growType,
        int awakeningType)
    {
        if (!_grantedSkills.Value.TryGetValue(job, out var grantSet)
            || !grantSet.GrowTypes.TryGetValue(
                (growType & 0x0F) + 1,
                out var growTypeGrant)
            || !growTypeGrant.AwakeningSkills.TryGetValue(
                Math.Max(0, awakeningType),
                out var awakeningSkills))
        {
            return [];
        }

        return CreateGrowthSkillChanges(job, awakeningSkills);
    }

    public bool TryApplyGrowthSkillChanges(
        int job,
        IEnumerable<CharacterSkillRecord> currentSkills,
        IEnumerable<CharacterGrantedSkillChange> changes,
        out IReadOnlyList<CharacterSkillRecord> result)
    {
        var source = currentSkills
            .Where(skill => skill.Level > 0)
            .OrderBy(skill => skill.Slot)
            .ToArray();
        var working = source.ToList();
        var effectiveChanges = changes
            .GroupBy(change => change.SkillId)
            .Select(group => group.Last())
            .OrderBy(change => change.Level > 0 ? 1 : 0)
            .ToArray();
        foreach (var change in effectiveChanges)
        {
            if (change.Level == 0)
            {
                working.RemoveAll(skill => skill.SkillId == change.SkillId);
                continue;
            }

            var existingIndex = working.FindIndex(skill =>
                skill.SkillId == change.SkillId);
            if (existingIndex >= 0)
            {
                if (working[existingIndex].Level < change.Level)
                {
                    working[existingIndex] = working[existingIndex] with
                    {
                        Level = change.Level
                    };
                }

                continue;
            }

            var occupied = working.Select(skill => skill.Slot).ToHashSet();
            byte? slot = null;
            if (TryGetDefinition(job, change.SkillId, out var definition))
            {
                slot = AllocateSlot(definition, occupied);
            }

            slot ??= FirstFreeForClass(change.SkillClass, occupied);
            if (!slot.HasValue)
            {
                result = source;
                return false;
            }

            working.Add(new CharacterSkillRecord(
                slot.Value,
                change.SkillId,
                change.Level));
        }

        result = working.OrderBy(skill => skill.Slot).ToArray();
        return AreGrowthSkillChangesApplied(result, effectiveChanges);
    }

    public static bool AreGrowthSkillChangesApplied(
        IEnumerable<CharacterSkillRecord> skills,
        IEnumerable<CharacterGrantedSkillChange> changes)
    {
        var learned = skills.Where(skill => skill.Level > 0).ToArray();
        return changes
            .GroupBy(change => change.SkillId)
            .Select(group => group.Last())
            .All(change => change.Level == 0
                ? learned.All(skill => skill.SkillId != change.SkillId)
                : learned.Any(skill => skill.SkillId == change.SkillId
                    && skill.Level >= change.Level));
    }

    public IReadOnlyList<GameSkillEntry> GetGrantedSkills(
        int job,
        int growType,
        int awakeningType = 0)
    {
        if (!_grantedSkills.Value.TryGetValue(job, out var grantSet))
        {
            return [];
        }

        var result = grantSet.InitialSkills.ToList();
        // Character/quest state uses zero-based branches, while .chr files use
        // [growtype 1] for the unadvanced branch through [growtype 5].
        var scriptGrowType = (growType & 0x0F) + 1;
        if (grantSet.GrowTypes.TryGetValue(scriptGrowType, out var growTypeGrant))
        {
            MergeGrantedSkills(result, growTypeGrant.Skills);
            foreach (var awakeningSkills in growTypeGrant.AwakeningSkills
                         .Where(entry => entry.Key <= Math.Max(0, awakeningType))
                         .OrderBy(entry => entry.Key)
                         .Select(entry => entry.Value))
            {
                MergeGrantedSkills(result, awakeningSkills);
            }
        }

        return result
            .Where(skill => skill.Level > 0)
            .ToArray();
    }

    public IReadOnlyList<CharacterSkillRecord> CreateResetLayout(
        int job,
        int growType,
        IEnumerable<CharacterSkillRecord> currentSkills,
        IEnumerable<GameSkillEntry>? grantedSlotPreferences = null,
        int awakeningType = 0)
    {
        var preferredSlots = (grantedSlotPreferences ?? [])
            .Where(skill => skill.Slot.HasValue)
            .GroupBy(skill => skill.SkillId)
            .ToDictionary(group => group.Key, group => group.First().Slot!.Value);
        var granted = GetGrantedSkills(job, growType, awakeningType)
            .Select(skill => preferredSlots.TryGetValue(skill.SkillId, out var slot)
                ? skill with { Slot = slot }
                : skill);
        var guildSkills = currentSkills
            .Where(skill => skill.Level > 0
                && TryGetDefinition(job, skill.SkillId, out var definition)
                && definition.IsGuildSkill)
            .Select(skill => new GameSkillEntry(
                skill.SkillId,
                skill.Level,
                skill.Slot));

        return CreateInitialLayout(job, granted.Concat(guildSkills));
    }

    public IReadOnlyList<CharacterSkillRecord> CreateInitialLayout(
        int job,
        IEnumerable<GameSkillEntry> initialSkills)
    {
        var merged = initialSkills
            .GroupBy(skill => skill.SkillId)
            .Select(group =>
            {
                var highest = group.OrderByDescending(skill => skill.Level).First();
                var fixedSlot = group.FirstOrDefault(skill => skill.Slot.HasValue)?.Slot;
                return highest with { Slot = fixedSlot ?? highest.Slot };
            })
            .ToArray();
        var occupied = new HashSet<byte>();
        var result = new List<CharacterSkillRecord>(merged.Length);

        foreach (var skill in merged)
        {
            var hasDefinition = TryGetDefinition(job, skill.SkillId, out var definition);
            var slot = skill.Slot is { } requestedSlot
                && !occupied.Contains(requestedSlot)
                && (hasDefinition
                    ? CanOccupySlot(definition, requestedSlot)
                    : IsOrdinaryInventorySlot(requestedSlot))
                    ? requestedSlot
                    : (byte?)null;
            if (slot is null)
            {
                slot = hasDefinition
                    ? AllocateSlot(definition, occupied)
                    : FirstFree(occupied, SkillClassStart, GuildSkillStart);
            }

            if (!slot.HasValue)
            {
                _logger.LogWarning(
                    "No free DFLegacy skill slot for job {Job}, skill {SkillId}.",
                    job,
                    skill.SkillId);
                continue;
            }

            occupied.Add(slot.Value);
            result.Add(new CharacterSkillRecord(slot.Value, skill.SkillId, skill.Level));
        }

        return result.OrderBy(skill => skill.Slot).ToArray();
    }

    public IReadOnlyList<CharacterSkillRecord> NormalizeLayout(
        int job,
        int growType,
        int awakeningType,
        IEnumerable<CharacterSkillRecord> skills,
        out bool changed)
    {
        var source = skills
            .Where(skill => skill.Level > 0)
            .GroupBy(skill => skill.SkillId)
            .Select(group => group
                .OrderByDescending(skill => skill.Level)
                .ThenBy(skill => skill.Slot)
                .First())
            .OrderBy(skill => skill.Slot)
            .ToArray();
        var normalizedLevels = new List<(CharacterSkillRecord Skill, SkillDefinition? Definition)>();

        foreach (var skill in source)
        {
            if (!TryGetDefinition(job, skill.SkillId, out var definition))
            {
                normalizedLevels.Add((skill, null));
                continue;
            }

            var maximumLevel = definition.MaximumLevelFor(growType, awakeningType);
            if (maximumLevel <= 0)
            {
                continue;
            }

            var level = checked((byte)Math.Min(skill.Level, maximumLevel));
            var normalized = skill with { Level = level };
            normalizedLevels.Add((normalized, definition));
        }

        var result = NormalizeSlots(job, normalizedLevels, out var slotsNormalized);
        if (!slotsNormalized)
        {
            result = normalizedLevels
                .Select(entry => entry.Skill)
                .OrderBy(skill => skill.Slot)
                .ToArray();
        }

        changed = !source.SequenceEqual(result);
        return result;
    }

    public IReadOnlyList<CharacterSkillRecord> ReconcileOverLevelSkills(
        int job,
        int growType,
        int awakeningType,
        int effectiveCharacterLevel,
        IEnumerable<CharacterSkillRecord> skills,
        out int refundedSkillPoints,
        out IReadOnlyList<SkillLevelRefund> refunds)
    {
        var source = skills
            .Where(skill => skill.Level > 0)
            .GroupBy(skill => skill.SkillId)
            .Select(group => group
                .OrderByDescending(skill => skill.Level)
                .ThenBy(skill => skill.Slot)
                .First())
            .OrderBy(skill => skill.Slot)
            .ToArray();
        var grantedLevels = GetGrantedSkills(job, growType, awakeningType)
            .GroupBy(skill => skill.SkillId)
            .ToDictionary(
                group => group.Key,
                group => checked((int)group.Max(skill => skill.Level)));
        var retainedLevels = new Dictionary<byte, int>(source.Length);

        foreach (var skill in source)
        {
            if (!TryGetDefinition(job, skill.SkillId, out var definition)
                || definition.IsGuildSkill)
            {
                retainedLevels[skill.SkillId] = skill.Level;
                continue;
            }

            var maximumLevel = Math.Min(
                skill.Level,
                Math.Max(0, definition.MaximumLevelFor(growType, awakeningType)));
            var levelAllowedByCharacter = 0;
            for (var level = 1; level <= maximumLevel; level++)
            {
                if (definition.RequiredCharacterLevelFor(level)
                    > effectiveCharacterLevel)
                {
                    break;
                }

                levelAllowedByCharacter = level;
            }

            var grantedLevel = grantedLevels.GetValueOrDefault(skill.SkillId);
            retainedLevels[skill.SkillId] = Math.Min(
                skill.Level,
                Math.Max(levelAllowedByCharacter, grantedLevel));
        }

        // Match the server's skill-tree load pass: an over-level dependent skill
        // cannot survive after its prerequisite has itself been reduced. Granted
        // class/awakening levels remain the lower bound and never refund SP.
        bool changed;
        do
        {
            changed = false;
            foreach (var skill in source)
            {
                if (!TryGetDefinition(job, skill.SkillId, out var definition)
                    || definition.IsGuildSkill
                    || retainedLevels[skill.SkillId] == 0)
                {
                    continue;
                }

                var hasPrerequisites = true;
                for (var index = 0;
                     index + 1 < definition.Prerequisites.Length;
                     index += 2)
                {
                    var prerequisiteId = definition.Prerequisites[index];
                    var prerequisiteLevel = definition.Prerequisites[index + 1];
                    if (prerequisiteId is < byte.MinValue or > byte.MaxValue
                        || retainedLevels.GetValueOrDefault(
                            checked((byte)prerequisiteId)) < prerequisiteLevel)
                    {
                        hasPrerequisites = false;
                        break;
                    }
                }

                if (hasPrerequisites)
                {
                    continue;
                }

                var grantedLevel = grantedLevels.GetValueOrDefault(skill.SkillId);
                var nextLevel = Math.Min(skill.Level, grantedLevel);
                if (nextLevel != retainedLevels[skill.SkillId])
                {
                    retainedLevels[skill.SkillId] = nextLevel;
                    changed = true;
                }
            }
        }
        while (changed);

        var result = new List<CharacterSkillRecord>(source.Length);
        var refundEntries = new List<SkillLevelRefund>();
        long totalRefund = 0;
        foreach (var skill in source)
        {
            var retainedLevel = retainedLevels[skill.SkillId];
            if (retainedLevel > 0)
            {
                result.Add(skill with { Level = checked((byte)retainedLevel) });
            }

            if (retainedLevel >= skill.Level
                || !TryGetDefinition(job, skill.SkillId, out var definition)
                || definition.IsGuildSkill)
            {
                continue;
            }

            var skillPointRefund = Math.Max(
                0,
                definition.CostFor(retainedLevel, skill.Level));
            totalRefund = Math.Min(int.MaxValue, totalRefund + skillPointRefund);
            refundEntries.Add(new SkillLevelRefund(
                skill.SkillId,
                skill.Level,
                checked((byte)retainedLevel),
                skillPointRefund));
        }

        refundedSkillPoints = checked((int)totalRefund);
        refunds = refundEntries;
        return result.OrderBy(skill => skill.Slot).ToArray();
    }

    public bool TrySwapSlots(
        int job,
        IEnumerable<CharacterSkillRecord> skills,
        byte sourceSlot,
        byte destinationSlot,
        out IReadOnlyList<CharacterSkillRecord> result,
        out string rejectionReason)
    {
        var source = skills.OrderBy(skill => skill.Slot).ToArray();
        result = source;
        rejectionReason = string.Empty;

        if (sourceSlot >= SkillSlotEnd || destinationSlot >= SkillSlotEnd)
        {
            rejectionReason = "slot is outside the DFLegacy skill layout";
            return false;
        }

        var sourceSkill = source.FirstOrDefault(skill => skill.Slot == sourceSlot);
        var destinationSkill = source.FirstOrDefault(skill => skill.Slot == destinationSlot);
        if (sourceSkill is not null
            && !CanMoveSkillTo(job, sourceSkill, destinationSlot, out rejectionReason))
        {
            return false;
        }

        if (destinationSkill is not null
            && !CanMoveSkillTo(job, destinationSkill, sourceSlot, out rejectionReason))
        {
            return false;
        }

        result = SwapSlots(source, sourceSlot, destinationSlot, out _);
        return true;
    }

    public static IReadOnlyList<CharacterSkillRecord> SwapSlots(
        IEnumerable<CharacterSkillRecord> skills,
        byte sourceSlot,
        byte destinationSlot,
        out bool changed)
    {
        var result = skills.ToList();
        var sourceIndex = result.FindIndex(skill => skill.Slot == sourceSlot);
        var destinationIndex = result.FindIndex(skill => skill.Slot == destinationSlot);
        changed = sourceIndex >= 0 || destinationIndex >= 0;
        if (!changed || sourceSlot == destinationSlot)
        {
            changed = false;
            return result.OrderBy(skill => skill.Slot).ToArray();
        }

        if (sourceIndex >= 0)
        {
            result[sourceIndex] = result[sourceIndex] with { Slot = destinationSlot };
        }

        if (destinationIndex >= 0)
        {
            result[destinationIndex] = result[destinationIndex] with { Slot = sourceSlot };
        }

        return result.OrderBy(skill => skill.Slot).ToArray();
    }

    public byte? AllocateSlot(SkillDefinition definition, ISet<byte> occupied)
    {
        if (!TryGetClassSlotRange(definition, out var start, out var end))
        {
            return null;
        }

        return FirstFree(occupied, start, end);
    }

    public static bool CanOccupySlot(SkillDefinition definition, byte slot)
    {
        if (slot < QuickSlotCount)
        {
            return definition.IsActive && !definition.IsGuildSkill;
        }

        return TryGetClassSlotRange(definition, out var start, out var end)
            && slot >= start
            && slot < end;
    }

    private IReadOnlyList<CharacterSkillRecord> NormalizeSlots(
        int job,
        IReadOnlyList<(CharacterSkillRecord Skill, SkillDefinition? Definition)> entries,
        out bool succeeded)
    {
        succeeded = true;
        var result = new List<CharacterSkillRecord>(entries.Count);
        var pending = new List<(CharacterSkillRecord Skill, SkillDefinition? Definition)>();
        var occupied = new HashSet<byte>();

        // Preserve every unambiguous, already-valid player choice first. Only
        // misplaced skills and slot collisions are assigned a new position.
        foreach (var entry in entries.OrderBy(entry => entry.Skill.Slot))
        {
            var slotIsValid = entry.Definition is not null
                ? CanOccupySlot(entry.Definition, entry.Skill.Slot)
                : IsOrdinaryInventorySlot(entry.Skill.Slot);
            if (slotIsValid && occupied.Add(entry.Skill.Slot))
            {
                result.Add(entry.Skill);
            }
            else
            {
                pending.Add(entry);
            }
        }

        foreach (var entry in pending)
        {
            var slot = entry.Definition is not null
                ? AllocateSlot(entry.Definition, occupied)
                : FirstFree(occupied, SkillClassStart, GuildSkillStart);
            if (slot is null)
            {
                _logger.LogWarning(
                    "No valid DFLegacy skill slot remains for job {Job}, skill {SkillId}; leaving the complete persisted layout unchanged.",
                    job,
                    entry.Skill.SkillId);
                succeeded = false;
                return [];
            }

            occupied.Add(slot.Value);
            result.Add(entry.Skill with { Slot = slot.Value });
        }

        return result.OrderBy(skill => skill.Slot).ToArray();
    }

    private bool CanMoveSkillTo(
        int job,
        CharacterSkillRecord skill,
        byte destinationSlot,
        out string rejectionReason)
    {
        if (!TryGetDefinition(job, skill.SkillId, out var definition))
        {
            rejectionReason = $"skill {skill.SkillId} has no PVF metadata";
            return false;
        }

        if (CanOccupySlot(definition, destinationSlot))
        {
            rejectionReason = string.Empty;
            return true;
        }

        rejectionReason = definition.IsGuildSkill
            ? $"guild skill {skill.SkillId} must remain in slots {GuildSkillStart}-{SkillSlotEnd - 1}"
            : !definition.IsActive && destinationSlot < QuickSlotCount
                ? $"passive skill {skill.SkillId} cannot enter the quick slots"
                : $"skill {skill.SkillId} must remain in skill class {definition.RawGroup}";
        return false;
    }

    private static bool TryGetClassSlotRange(
        SkillDefinition definition,
        out byte start,
        out byte end)
    {
        if (definition.IsGuildSkill)
        {
            start = GuildSkillStart;
            end = SkillSlotEnd;
            return true;
        }

        if (definition.RawGroup is < 0 or >= SkillClassCount)
        {
            start = 0;
            end = 0;
            return false;
        }

        start = checked((byte)(SkillClassStart + definition.RawGroup * SkillClassSlotCount));
        end = definition.RawGroup == SkillClassCount - 1
            ? GuildSkillStart
            : checked((byte)(start + SkillClassSlotCount));
        return true;
    }

    private static bool IsOrdinaryInventorySlot(byte slot) =>
        slot >= SkillClassStart && slot < GuildSkillStart;

    private IReadOnlyList<CharacterGrantedSkillChange> CreateGrowthSkillChanges(
        int job,
        IEnumerable<GameSkillEntry> skills) => skills
        .Select(skill => new CharacterGrantedSkillChange(
            skill.SkillId,
            skill.Level,
            ResolveGrowthSkillClass(job, skill.SkillId)))
        .ToArray();

    private byte ResolveGrowthSkillClass(int job, byte skillId)
    {
        if (TryGetDefinition(job, skillId, out var definition)
            && definition.RawGroup is >= 0 and < SkillClassCount)
        {
            return checked((byte)definition.RawGroup);
        }

        return skillId < 170 ? (byte)0 : (byte)4;
    }

    private static byte? FirstFreeForClass(byte skillClass, ISet<byte> occupied)
    {
        if (skillClass >= SkillClassCount)
        {
            return null;
        }

        var start = SkillClassStart + skillClass * SkillClassSlotCount;
        var end = skillClass == SkillClassCount - 1
            ? GuildSkillStart
            : start + SkillClassSlotCount;
        return FirstFree(occupied, start, end);
    }

    private IReadOnlyDictionary<byte, SkillDefinition> GetJob(int job)
    {
        lock (_gate)
        {
            if (_jobs.TryGetValue(job, out var cached))
            {
                return cached;
            }

            var loaded = LoadJob(job);
            _jobs[job] = loaded;
            return loaded;
        }
    }

    private IReadOnlyDictionary<int, CharacterSkillGrantSet> LoadGrantedSkills()
    {
        const string characterListPath = "character/character.lst";
        var result = new Dictionary<int, CharacterSkillGrantSet>();
        if (!_scripts.FileExists(characterListPath))
        {
            _logger.LogWarning(
                "Character list is unavailable in {Source}; granted skills cannot be restored after a skill reset.",
                _scripts.SourceDescription);
            return result;
        }

        foreach (var (job, relativePath) in ParseList(characterListPath))
        {
            var path = $"character/{NormalizeRelativePath(relativePath)}";
            if (!_scripts.FileExists(path))
            {
                continue;
            }

            var text = _scripts.ReadAllText(path, Encoding.Latin1);
            var growTypeMatches = GrowTypePattern.Matches(text);
            var initialEnd = growTypeMatches.Count == 0
                ? text.Length
                : growTypeMatches[0].Index;
            var initialSkills = ParseGrantedSkillSection(text[..initialEnd]);
            var growTypes = new Dictionary<int, CharacterGrowTypeSkillGrantSet>();
            for (var index = 0; index < growTypeMatches.Count; index++)
            {
                var match = growTypeMatches[index];
                var sectionEnd = index + 1 < growTypeMatches.Count
                    ? growTypeMatches[index + 1].Index
                    : text.Length;
                var section = text[match.Index..sectionEnd];
                var awakeningMatches = AwakeningPattern.Matches(section);
                var growTypeSkillEnd = awakeningMatches.Count == 0
                    ? section.Length
                    : awakeningMatches[0].Index;
                var skills = ParseGrantedSkillSection(section[..growTypeSkillEnd]);
                var awakeningSkills = new Dictionary<int, IReadOnlyList<GameSkillEntry>>();
                for (var awakeningIndex = 0;
                     awakeningIndex < awakeningMatches.Count;
                     awakeningIndex++)
                {
                    var awakeningMatch = awakeningMatches[awakeningIndex];
                    var awakeningEnd = awakeningIndex + 1 < awakeningMatches.Count
                        ? awakeningMatches[awakeningIndex + 1].Index
                        : section.Length;
                    var parsed = ParseGrantedSkillSection(
                        section[awakeningMatch.Index..awakeningEnd],
                        AwakeningSkillBlockPattern);
                    if (parsed.Count > 0)
                    {
                        awakeningSkills[int.Parse(
                            awakeningMatch.Groups["awakeningType"].Value)] = parsed;
                    }
                }

                growTypes[int.Parse(match.Groups["growType"].Value)] =
                    new CharacterGrowTypeSkillGrantSet(skills, awakeningSkills);
            }

            result[job] = new CharacterSkillGrantSet(initialSkills, growTypes);
        }

        _logger.LogInformation(
            "Cached character-granted skill tables for {Count} jobs from {Source}.",
            result.Count,
            _scripts.SourceDescription);
        return result;
    }

    private static IReadOnlyList<GameSkillEntry> ParseGrantedSkillSection(
        string section,
        Regex? blockPattern = null)
    {
        var result = new List<GameSkillEntry>();
        foreach (Match block in (blockPattern ?? SkillBlockPattern).Matches(section))
        {
            foreach (Match row in SkillRowPattern.Matches(block.Groups["body"].Value))
            {
                var rawSkillId = int.Parse(row.Groups["id"].Value);
                var rawLevel = int.Parse(row.Groups["level"].Value);
                if (rawSkillId is < byte.MinValue or > byte.MaxValue
                    || rawLevel is < byte.MinValue or > byte.MaxValue)
                {
                    continue;
                }

                result.Add(new GameSkillEntry(
                    checked((byte)rawSkillId),
                    checked((byte)rawLevel)));
            }
        }

        return result;
    }

    private static void MergeGrantedSkills(
        List<GameSkillEntry> result,
        IEnumerable<GameSkillEntry> additions)
    {
        foreach (var skill in additions)
        {
            var existingIndex = result.FindIndex(candidate =>
                candidate.SkillId == skill.SkillId);
            if (skill.Level == 0)
            {
                if (existingIndex >= 0)
                {
                    result.RemoveAt(existingIndex);
                }

                continue;
            }

            if (existingIndex >= 0)
            {
                result[existingIndex] = skill;
            }
            else
            {
                result.Add(skill);
            }
        }
    }

    private IReadOnlyDictionary<byte, SkillDefinition> LoadJob(int job)
    {
        if (!_scripts.FileExists("skill/skilllist.lst"))
        {
            _logger.LogWarning(
                "Skill list is unavailable in {Source}; skill classification metadata is unavailable.",
                _scripts.SourceDescription);
            return new Dictionary<byte, SkillDefinition>();
        }

        try
        {
            const string skillRoot = "skill";
            var listIndex = ParseList($"{skillRoot}/skilllist.lst");
            if (!listIndex.TryGetValue(job, out var jobListRelativePath))
            {
                _logger.LogWarning("No skill list is configured for DFLegacy job {Job}.", job);
                return new Dictionary<byte, SkillDefinition>();
            }

            var definitions = new Dictionary<byte, SkillDefinition>();
            foreach (var (rawSkillId, relativePath) in ParseList(
                         $"{skillRoot}/{NormalizeRelativePath(jobListRelativePath)}"))
            {
                if (rawSkillId is < byte.MinValue or > byte.MaxValue)
                {
                    continue;
                }

                var fullPath = $"{skillRoot}/{NormalizeRelativePath(relativePath)}";
                if (!_scripts.FileExists(fullPath))
                {
                    continue;
                }

                var text = _scripts.ReadAllText(fullPath, Encoding.Latin1);
                var definition = ParseDefinition(checked((byte)rawSkillId), text);
                definitions[definition.SkillId] = definition;
            }

            _logger.LogInformation(
                "Loaded {Count} DFLegacy skill definitions for job {Job} from {Path}.",
                definitions.Count,
                job,
                _scripts.SourceDescription);
            return definitions;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Could not load DFLegacy skill metadata for job {Job}.",
                job);
            return new Dictionary<byte, SkillDefinition>();
        }
    }

    private Dictionary<int, string> ParseList(string path)
    {
        var entries = new Dictionary<int, string>();
        if (!_scripts.FileExists(path))
        {
            return entries;
        }

        foreach (Match match in ListEntryPattern.Matches(
                     _scripts.ReadAllText(path, Encoding.Latin1)))
        {
            entries[int.Parse(match.Groups[1].Value)] = match.Groups[2].Value.Trim();
        }

        return entries;
    }

    private static string NormalizeRelativePath(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private static SkillDefinition ParseDefinition(byte skillId, string text)
    {
        var type = ReadTag(text, "type");
        var maximumLevel = ReadFirstInteger(text, "maximum level", 1);
        var consumedItem = ReadIntegers(ReadTag(text, "consume item"));
        return new SkillDefinition(
            skillId,
            IsActive: Regex.IsMatch(
                type,
                @"\[\s*active\s*\]",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            IsGuildSkill: ReadFirstInteger(text, "purchase gsp", 0) > 0,
            RawGroup: ReadFirstInteger(text, "skill class", 0),
            NumberOfGrowTypes: ReadIntegers(ReadTag(text, "skill fitness growtype")).Length,
            MaximumLevel: Math.Max(1, maximumLevel),
            RequiredLevel: Math.Max(0, ReadFirstInteger(text, "required level", 0)),
            RequiredLevelRange: Math.Max(1, ReadFirstInteger(text, "required level range", 1)),
            GrowTypeMaximumLevels: ReadIntegers(ReadTag(text, "growtype maximum level")),
            SecondGrowTypeMaximumLevels: ReadIntegers(
                ReadTag(text, "second growtype maximum level")),
            PurchaseCosts: ReadIntegers(ReadTag(text, "purchase cost")),
            Prerequisites: ReadIntegers(ReadTag(text, "pre required skill")),
            ConsumedItemId: consumedItem.Length >= 2
                && consumedItem[0] is > 0 and <= ushort.MaxValue
                    ? checked((ushort)consumedItem[0])
                    : (ushort)0,
            ConsumedItemCount: consumedItem.Length >= 2
                && consumedItem[1] > 0
                    ? checked((uint)consumedItem[1])
                    : 0);
    }

    private static string ReadTag(string text, string tag)
    {
        var marker = $"[{tag}]";
        var start = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return string.Empty;
        }

        start += marker.Length;
        var close = text.IndexOf($"[/{tag}]", start, StringComparison.OrdinalIgnoreCase);
        var lineEnd = text.IndexOfAny(['\r', '\n'], start);
        int end;
        if (close >= 0 && (lineEnd < 0 || string.IsNullOrWhiteSpace(text[start..lineEnd])))
        {
            end = close;
        }
        else
        {
            end = lineEnd >= 0 ? lineEnd : text.Length;
        }

        var value = text[start..end];
        return Regex.Replace(value, @"//.*?(\r?\n|$)", " ");
    }

    private static int ReadFirstInteger(string text, string tag, int fallback)
    {
        var values = ReadIntegers(ReadTag(text, tag));
        return values.Length > 0 ? values[0] : fallback;
    }

    private static int[] ReadIntegers(string value) =>
        IntegerPattern.Matches(value)
            .Select(match => int.Parse(match.Value))
            .ToArray();

    private static byte? FirstFree(ISet<byte> occupied, int start, int end)
    {
        for (var slot = start; slot < end && slot <= byte.MaxValue; slot++)
        {
            var value = checked((byte)slot);
            if (!occupied.Contains(value))
            {
                return value;
            }
        }

        return null;
    }

    private sealed record CharacterSkillGrantSet(
        IReadOnlyList<GameSkillEntry> InitialSkills,
        IReadOnlyDictionary<int, CharacterGrowTypeSkillGrantSet> GrowTypes);

    private sealed record CharacterGrowTypeSkillGrantSet(
        IReadOnlyList<GameSkillEntry> Skills,
        IReadOnlyDictionary<int, IReadOnlyList<GameSkillEntry>> AwakeningSkills);
}
