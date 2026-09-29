using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

public sealed class DungeonExperienceCatalog
{
    private const string ServerParameterPath = "etc/serverparameter.etc";
    private static readonly Encoding ScriptEncoding = CreateScriptEncoding();
    private static readonly Regex NumberPattern = new(
        @"[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // DF2008 keeps this level-indexed table in the server database rather than
    // Script.pvf. These values mirror monster_reward_ref used by the reference
    // server; all configurable multipliers still come from the target PVF.
    private static readonly uint[] MonsterBaseRewards =
    [
        30, 40, 50, 60, 70, 80, 90, 100, 110, 120,
        130, 140, 150, 160, 170, 185, 201, 218, 235, 253,
        271, 290, 310, 330, 351, 372, 394, 417, 440, 464,
        488, 513, 539, 565, 592, 619, 647, 676, 705, 735,
        765, 796, 828, 860, 893, 926, 960, 995, 1030, 1066,
        1102, 1139, 1177, 1215, 1254, 1293, 1333, 1374, 1415, 1457,
        1499, 1542, 1586, 1630, 1675, 1720, 1766, 1813, 1860, 1908,
        1956, 2005, 2055, 2105, 2156, 2207, 2259, 2312, 2365, 2419,
        2473, 2528, 2584, 2640, 2697, 2754, 2812, 2871, 2930, 2990,
        3050, 3111, 3173, 3235, 3298, 3361, 3425, 3490, 3555, 3575
    ];

    private readonly ExperienceRateState _rates;
    private readonly double _operatorMonsterMultiplier;
    private readonly double _operatorClearMultiplier;
    private readonly double _blackDiamondClearBonusRate;
    private readonly double _eventBonusRate;
    private readonly DateTimeOffset? _eventStartsAt;
    private readonly DateTimeOffset? _eventEndsAt;
    private readonly IReadOnlyDictionary<ushort, double> _channelBonusRates;

    public DungeonExperienceCatalog(
        ServerOptions options,
        ScriptFileSystem scripts,
        ILogger<DungeonExperienceCatalog> logger)
    {
        _rates = LoadRates(scripts, logger);
        _operatorMonsterMultiplier = NormalizeRate(
            options.Experience?.MonsterMultiplier ?? 1.0,
            1.0);
        _operatorClearMultiplier = NormalizeRate(
            options.Experience?.ClearMultiplier ?? 1.0,
            1.0);
        _blackDiamondClearBonusRate = NormalizeRate(options.Experience?.BlackDiamondClearBonusRate ?? 0.05, 0);
        _eventBonusRate = NormalizeRate(options.Experience?.ClearEventBonusRate ?? 0, 0);
        _eventStartsAt = options.Experience?.ClearEventStartsAt;
        _eventEndsAt = options.Experience?.ClearEventEndsAt;
        _channelBonusRates = LoadChannelBonusRates(scripts, options.Channel.ServerNumber, options.Channel.ChannelNumber);

        logger.LogInformation(
            "Loaded dungeon EXP rates from {Source}: monster={MonsterRate}, clear={ClearRate}, party=[{PartyRates}], difficulty=[{DifficultyRates}], rank=[{RankRates}], kind=[{KindRates}], operatorMonster={OperatorMonster}, operatorClear={OperatorClear}.",
            scripts.SourceDescription,
            _rates.MonsterBonusRate,
            _rates.ClearBonusRate,
            string.Join(',', _rates.PartyRates),
            string.Join(',', _rates.DifficultyRates),
            string.Join(',', _rates.RankRates),
            string.Join(',', _rates.MonsterKindRates),
            _operatorMonsterMultiplier,
            _operatorClearMultiplier);
        logger.LogInformation(
            "Clear EXP bonus policy: blackDiamond={BlackDiamondRate}, event={EventRate}, eventStart={EventStart}, eventEnd={EventEnd}, server={ServerNumber}, channel={ChannelNumber}, matchingDungeons={DungeonCount}.",
            _blackDiamondClearBonusRate, _eventBonusRate, _eventStartsAt, _eventEndsAt,
            options.Channel.ServerNumber, options.Channel.ChannelNumber, _channelBonusRates.Count);
    }

    public int MonsterBaseRewardCount => MonsterBaseRewards.Length;

    public double MonsterBonusRate => _rates.MonsterBonusRate;

    public double ClearBonusRate => _rates.ClearBonusRate;

    public double GetChannelBonusRate(ushort dungeonId) => _channelBonusRates.GetValueOrDefault(dungeonId);

    public double GetEventBonusRate(DateTimeOffset now) =>
        (!_eventStartsAt.HasValue || now >= _eventStartsAt.Value)
        && (!_eventEndsAt.HasValue || now < _eventEndsAt.Value) ? _eventBonusRate : 0;

    public uint GetMonsterBaseReward(int monsterLevel) =>
        monsterLevel >= 1 && monsterLevel <= MonsterBaseRewards.Length
            ? MonsterBaseRewards[monsterLevel - 1]
            : 0;

    public double GetPartyRate(int partyMemberCount) => _rates.PartyRates[
        Math.Clamp(partyMemberCount, 1, _rates.PartyRates.Length) - 1];

    public double GetDifficultyRate(byte difficulty) => _rates.DifficultyRates[
        Math.Clamp((int)difficulty, 0, _rates.DifficultyRates.Length - 1)];

    public double GetMonsterKindRate(byte monsterType) =>
        monsterType < _rates.MonsterKindRates.Length
            ? _rates.MonsterKindRates[monsterType]
            : 1.0;

    public double GetRankRate(byte resultCode) => resultCode switch
    {
        >= 105 => _rates.RankRates[4],
        >= 95 => _rates.RankRates[3],
        >= 85 => _rates.RankRates[2],
        >= 70 => _rates.RankRates[1],
        >= 55 => _rates.RankRates[0],
        _ => 0
    };

    public uint CalculateMonsterExperience(
        int characterLevel,
        int monsterLevel,
        byte monsterType,
        ushort monsterIndex,
        DungeonDefinition dungeon,
        byte difficulty,
        int partyMemberCount = 1)
    {
        if (characterLevel >= CharacterExperienceCatalog.MaximumLevel)
        {
            return 0;
        }

        var baseReward = GetMonsterBaseReward(monsterLevel);
        if (baseReward == 0)
        {
            return 0;
        }

        var memberCount = Math.Clamp(partyMemberCount, 1, 4);
        var namedMultiplier = IsNamedMonster(monsterIndex) ? 3.0 : 1.0;
        // monster_reward_ref already stores the per-kill reward. Dividing the
        // table again made the legacy minimum reward (for content seven or
        // more levels below the character) fall below one and disappear when
        // converted to the integer protocol value.
        var value = baseReward
            * _rates.MonsterBonusRate
            * dungeon.ExperienceMultiplier
            * GetDifficultyRate(difficulty)
            * GetPartyRate(memberCount)
            * GetMonsterKindRate(monsterType)
            * namedMultiplier
            * GetLevelPenalty(characterLevel, monsterLevel)
            * _operatorMonsterMultiplier
            / memberCount;
        return ToUInt32(value);
    }

    public GameDungeonClearExperienceBreakdown CalculateClearExperience(
        int characterLevel,
        DungeonDefinition dungeon,
        byte difficulty,
        byte resultCode,
        int totalKilledMonsterCount,
        int partyMemberCount = 1,
        DungeonClearBonusContext? bonuses = null)
    {
        if (characterLevel >= CharacterExperienceCatalog.MaximumLevel
            || totalKilledMonsterCount <= 0)
        {
            return GameDungeonClearExperienceBreakdown.Empty;
        }

        var baseReward = GetMonsterBaseReward(dungeon.BasisLevel);
        if (baseReward == 0)
        {
            return GameDungeonClearExperienceBreakdown.Empty;
        }

        var memberCount = Math.Clamp(partyMemberCount, 1, 4);
        var basePerMember = baseReward
            * Math.Max(0, totalKilledMonsterCount)
            / 2.0
            * _rates.ClearBonusRate
            * dungeon.ExperienceMultiplier
            * GetDifficultyRate(difficulty)
            * GetLevelPenalty(characterLevel, dungeon.BasisLevel)
            * _operatorClearMultiplier
            / memberCount;
        var withoutParty = ToUInt32(basePerMember);
        var withParty = ToUInt32(basePerMember * GetPartyRate(memberCount));

        // The legacy packet represents party EXP as a positive component. If
        // an operator configures a party rate below one, fold that reduction
        // into base EXP rather than emitting an impossible negative bonus.
        var baseExperience = Math.Min(withoutParty, withParty);
        var partyBonus = withParty - baseExperience;
        var rankBonus = Math.Min(
            ToUInt32(withParty * GetRankRate(resultCode)),
            uint.MaxValue - withParty);

        bonuses ??= new DungeonClearBonusContext();
        var remaining = uint.MaxValue - withParty - rankBonus;
        // Each component uses the same base, never the preceding bonus total.
        // Cap components themselves so the client sum equals the persisted grant.
        uint Bonus(double rate, bool minimumOne = false)
        {
            rate = NormalizeRate(rate, 0);
            var value = ToUInt32(withParty * rate);
            if (minimumOne && rate > 0 && withParty > 0)
            {
                value = Math.Max(1u, value);
            }

            value = Math.Min(value, remaining);
            remaining -= value;
            return value;
        }

        return new GameDungeonClearExperienceBreakdown
        {
            BaseExperience = baseExperience,
            RankBonus = rankBonus,
            PartyBonus = partyBonus,
            AvatarBonus = Bonus(bonuses.HasAvatar ? memberCount == 1 ? 0.02 : 0.05 : 0, true),
            CreatureBonus = Bonus(bonuses.HasCreature ? memberCount == 1 ? 0.02 : 0.05 : 0, true),
            MentorBonus = Bonus(bonuses.MentorBonusRate),
            EventBonus = Bonus(bonuses.EventBonusRate),
            BlackDiamondBonus = Bonus(bonuses.HasBlackDiamond ? _blackDiamondClearBonusRate : 0),
            ChannelBonus = Bonus(bonuses.ChannelBonusRate)
        };
    }

    private static IReadOnlyDictionary<ushort, double> LoadChannelBonusRates(
        ScriptFileSystem scripts, int serverNumber, int channelNumber)
    {
        const string path = "etc/channel_info.etc";
        var result = new Dictionary<ushort, double>();
        if (!scripts.FileExists(path))
        {
            return result;
        }

        var text = Regex.Replace(scripts.ReadAllText(path, ScriptEncoding), @"//[^\r\n]*", "");
        var groups = new Dictionary<string, ushort[]>(StringComparer.OrdinalIgnoreCase);
        foreach (Match block in Regex.Matches(text, @"(?ims)^\s*\[dungeon\](.*?)^\s*\[/dungeon\]"))
        {
            var body = block.Groups[1].Value;
            var category = Regex.Match(body, @"`\[([^\]]+)\]`");
            if (category.Success)
            {
                groups[category.Groups[1].Value] = Regex.Matches(body, @"(?m)^\s*(\d+)\s*$")
                    .Select(match => ushort.TryParse(match.Groups[1].Value, out var id) ? id : (ushort)0)
                    .Where(id => id != 0).ToArray();
            }
        }

        foreach (Match block in Regex.Matches(text, @"(?ims)^\s*\[server\](.*?)^\s*\[/server\]"))
        {
            var body = block.Groups[1].Value;
            var server = Regex.Match(body, @"^\s*(\d+)");
            if (!server.Success || !int.TryParse(server.Groups[1].Value, out var id) || id != serverNumber)
            {
                continue;
            }

            foreach (Match row in Regex.Matches(body,
                @"(?m)^\s*(\d+)\s+(?:<[^>]+>|`[^`]*`)\s+\d+\s+`\[([^\]]+)\]`\s+(\d+)"))
            {
                if (int.TryParse(row.Groups[1].Value, out var channel) && channel == channelNumber
                    && double.TryParse(row.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var percent)
                    && groups.TryGetValue(row.Groups[2].Value, out var dungeons))
                {
                    foreach (var dungeonId in dungeons)
                    {
                        result[dungeonId] = percent / 100.0;
                    }
                }
            }
        }

        return result;
    }

    public static double GetLevelPenalty(int characterLevel, int contentLevel)
    {
        var difference = contentLevel - characterLevel;
        return difference switch
        {
            <= -7 => 0.05,
            -6 => 0.20,
            -5 => 0.50,
            -4 => 0.75,
            >= -3 and <= 0 => 1.00,
            >= 1 and <= 3 => 1.12,
            >= 4 and <= 5 => 1.00,
            6 => 0.75,
            7 => 0.70,
            8 => 0.60,
            9 => 0.50,
            _ => 0.05
        };
    }

    public static bool IsNamedMonster(ushort monsterIndex) =>
        monsterIndex is >= 50_000 and <= 51_000
        or >= 56_000 and <= 60_000;

    private static ExperienceRateState LoadRates(
        ScriptFileSystem scripts,
        ILogger logger)
    {
        if (!scripts.FileExists(ServerParameterPath))
        {
            logger.LogWarning(
                "Dungeon EXP parameters were not found at {Path}; using neutral rates.",
                ServerParameterPath);
            return ExperienceRateState.Default;
        }

        try
        {
            var text = scripts.ReadAllText(ServerParameterPath, ScriptEncoding);
            return new ExperienceRateState(
                ReadRateRow(text, "monster exp bonusrate", [1.0], 1)[0],
                ReadRateRow(text, "clear exp bonusrate", [1.0], 1)[0],
                ReadRateRow(
                    text,
                    "party user number exp bonusrate",
                    [1.0, 1.0, 1.0, 1.0],
                    4),
                ReadRateRow(
                    text,
                    "dungeon difficulty exp bonusrate",
                    [1.0, 1.0, 1.0, 1.0],
                    4),
                ReadRateRow(
                    text,
                    "clear rank exp bonusrate",
                    [0, 0, 0, 0, 0],
                    5),
                // The reference server's monster EXP path indexes the same
                // four slots populated by this legacy server-parameter row.
                ReadRateRow(
                    text,
                    "drop bonusrate of monster kind",
                    [1.0, 1.0, 1.0, 1.0],
                    4));
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Could not parse dungeon EXP parameters from {Path}; using neutral rates.",
                ServerParameterPath);
            return ExperienceRateState.Default;
        }
    }

    private static double[] ReadRateRow(
        string text,
        string tag,
        double[] fallback,
        int count)
    {
        var marker = Regex.Match(
            text,
            $@"(?im)^\s*\[{Regex.Escape(tag)}\]\s*(?<inline>[^\r\n]*)",
            RegexOptions.CultureInvariant);
        if (!marker.Success)
        {
            return PadRates(fallback, fallback, count);
        }

        var candidates = new List<string> { marker.Groups["inline"].Value };
        var followingText = text[(marker.Index + marker.Length)..];
        foreach (var line in followingText.Split(["\r\n", "\n", "\r"], StringSplitOptions.None))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('['))
            {
                break;
            }

            candidates.Add(line);
        }

        foreach (var candidate in candidates)
        {
            var data = candidate.Split("//", 2, StringSplitOptions.None)[0];
            var values = NumberPattern.Matches(data)
                .Select(match => double.TryParse(
                    match.Value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var value)
                        ? NormalizeRate(value, 0)
                        : 0)
                .ToArray();
            if (values.Length > 0)
            {
                return PadRates(values, fallback, count);
            }
        }

        return PadRates(fallback, fallback, count);
    }

    private static double[] PadRates(
        double[] values,
        double[] fallback,
        int count)
    {
        var result = new double[count];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = index < values.Length
                ? NormalizeRate(values[index], 0)
                : index < fallback.Length
                    ? NormalizeRate(fallback[index], 0)
                    : 0;
        }

        return result;
    }

    private static double NormalizeRate(double value, double fallback) =>
        double.IsFinite(value) && value >= 0 ? value : fallback;

    private static uint ToUInt32(double value)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            return 0;
        }

        return (uint)Math.Min(uint.MaxValue, Math.Floor(value));
    }

    private static Encoding CreateScriptEncoding()
    {
        return PvfEncodings.Cp949();
    }

    private sealed record ExperienceRateState(
        double MonsterBonusRate,
        double ClearBonusRate,
        double[] PartyRates,
        double[] DifficultyRates,
        double[] RankRates,
        double[] MonsterKindRates)
    {
        public static ExperienceRateState Default { get; } = new(
            1,
            1,
            [1, 1, 1, 1],
            [1, 1, 1, 1],
            [0, 0, 0, 0, 0],
            [1, 1, 1, 1]);
    }
}
