using System.Text.RegularExpressions;

namespace DFLegacy.Server;

/// <summary>
/// Cached PvP rank thresholds and the three DF2008 PvP experience books.
/// The client/server reference implementation reads the rank windows from
/// Etc/pvp_ref.etc; the item amounts are the fixed values used by the legacy
/// 13339 item-use hook (1121=1,000, 1122=10,000, 1123=100,000).
/// </summary>
public sealed class PvpExperienceCatalog
{
    private const string ScriptPath = "etc/pvp_ref.etc";

    private static readonly Regex TableRowPattern = new(
        @"^\s*(\d+)\s+(\d+)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly IReadOnlyDictionary<ushort, uint> BookExperienceByItemId =
        new Dictionary<ushort, uint>
        {
            [1121] = 1_000,
            [1122] = 10_000,
            [1123] = 100_000
        };

    private readonly ScriptFileSystem _scripts;
    private readonly ILogger<PvpExperienceCatalog> _logger;
    private readonly Lazy<PvpTable> _table;

    public PvpExperienceCatalog(
        ScriptFileSystem scripts,
        ILogger<PvpExperienceCatalog> logger)
    {
        _scripts = scripts;
        _logger = logger;
        _table = new Lazy<PvpTable>(
            LoadTable,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public int MaximumGrade => _table.Value.MaximumGrade;

    public int ThresholdCount => _table.Value.Thresholds.Count;

    public void Initialize() => _ = _table.Value;

    public bool TryGetBookExperience(ushort itemId, out uint experience) =>
        BookExperienceByItemId.TryGetValue(itemId, out experience);

    /// <summary>
    /// Mirrors RefPvpGrade::GetPvpGrade for the non-negative legacy range:
    /// grade 0 is the legacy unrated/new-character rank (displayed by the
    /// 2008 client as 10级) and has zero points. Positive points use the PVF
    /// rows as strict promotion thresholds, with grade 1 starting at zero. Values
    /// above the last PVF threshold remain at the last configured grade rather
    /// than producing an invalid client byte.
    /// </summary>
    public byte GetGradeForPoints(int points)
    {
        var table = _table.Value;
        var normalized = Math.Max(0, points);
        if (normalized == 0)
        {
            return 0;
        }

        if (table.Thresholds.Count == 0)
        {
            return 0;
        }

        foreach (var threshold in table.Thresholds)
        {
            // The value in pvp_ref.etc is the cumulative amount required to
            // leave that grade. Reaching it exactly promotes to the next
            // grade: 2,000 points is grade 2 (8级), with a fresh 0/6,000
            // progress window, rather than grade 1 (9级) at 2,000/2,000.
            if (normalized < threshold.Threshold)
            {
                return checked((byte)Math.Clamp(
                    threshold.Grade,
                    1,
                    byte.MaxValue));
            }
        }

        return checked((byte)Math.Clamp(
            table.Thresholds[^1].Grade,
            1,
            byte.MaxValue));
    }

    /// <summary>
    /// Returns the lower bound displayed by NOTI 48 for a grade. Grades 0 and
    /// 1 both have a zero lower bound; subsequent grades use the preceding PVF
    /// threshold.
    /// </summary>
    public int GetCurrentRankPoint(byte grade)
    {
        var table = _table.Value;
        if (table.Thresholds.Count == 0 || grade <= 1)
        {
            return 0;
        }

        var index = Math.Min(grade - 2, table.Thresholds.Count - 1);
        return table.Thresholds[index].Threshold;
    }

    /// <summary>
    /// Returns the upper bound displayed by NOTI 48. At the final grade it is
    /// held at the final PVF threshold, matching RefPvpGrade's max-grade path.
    /// </summary>
    public int GetNextRankPoint(byte grade)
    {
        var table = _table.Value;
        if (table.Thresholds.Count == 0)
        {
            return 0;
        }

        var index = Math.Clamp(grade - 1, 0, table.Thresholds.Count - 1);
        return table.Thresholds[index].Threshold;
    }

    private PvpTable LoadTable()
    {
        if (!_scripts.FileExists(ScriptPath))
        {
            _logger.LogWarning(
                "PvP reference table {ScriptPath} was not found in {Source}; PvP books will keep the unrated grade until the table is available.",
                ScriptPath,
                _scripts.SourceDescription);
            return PvpTable.Empty;
        }

        var rows = new SortedDictionary<int, int>();
        var inExperienceTable = false;
        var declaredMaximumGrade = 0;
        foreach (var rawLine in _scripts.ReadLines(ScriptPath))
        {
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            const string maxGradeTag = "[pvp experience max grade]";
            if (line.StartsWith(maxGradeTag, StringComparison.OrdinalIgnoreCase))
            {
                inExperienceTable = false;
                var value = line[maxGradeTag.Length..].Trim();
                if (int.TryParse(value, out var maxGrade) && maxGrade > 0)
                {
                    declaredMaximumGrade = maxGrade;
                }

                continue;
            }

            if (line.Equals("[pvp experience]", StringComparison.OrdinalIgnoreCase))
            {
                inExperienceTable = true;
                continue;
            }

            if (line.Equals("[/pvp experience]", StringComparison.OrdinalIgnoreCase))
            {
                inExperienceTable = false;
                continue;
            }

            if (line[0] == '[')
            {
                inExperienceTable = false;
                continue;
            }

            if (!inExperienceTable)
            {
                if (declaredMaximumGrade == 0
                    && int.TryParse(line, out var maxGrade)
                    && maxGrade > 0)
                {
                    declaredMaximumGrade = maxGrade;
                }

                continue;
            }

            var match = TableRowPattern.Match(line);
            if (!match.Success
                || !int.TryParse(match.Groups[1].Value, out var grade)
                || !int.TryParse(match.Groups[2].Value, out var threshold)
                || grade <= 0
                || threshold < 0)
            {
                throw new InvalidDataException(
                    $"{ScriptPath} contains an invalid PvP experience row: '{rawLine}'.");
            }

            if (!rows.TryAdd(grade, threshold))
            {
                throw new InvalidDataException(
                    $"{ScriptPath} contains duplicate PvP grade {grade}.");
            }
        }

        var thresholds = rows
            .Select(row => new PvpThreshold(row.Key, row.Value))
            .ToArray();
        if (thresholds.Length == 0)
        {
            _logger.LogWarning(
                "PvP reference table {ScriptPath} has no [pvp experience] rows in {Source}; PvP books will keep the unrated grade.",
                ScriptPath,
                _scripts.SourceDescription);
            return PvpTable.Empty;
        }

        var maximumGrade = declaredMaximumGrade > 0
            ? Math.Min(declaredMaximumGrade, thresholds[^1].Grade)
            : thresholds[^1].Grade;
        if (maximumGrade != thresholds[^1].Grade)
        {
            thresholds = thresholds
                .Where(row => row.Grade <= maximumGrade)
                .ToArray();
        }

        _logger.LogInformation(
            "Cached {Count} DFLegacy PvP rank thresholds from {Source}; maximum grade={MaximumGrade}.",
            thresholds.Length,
            _scripts.SourceDescription,
            maximumGrade);
        return new PvpTable(maximumGrade, thresholds);
    }

    private static string StripComment(string value)
    {
        var commentIndex = value.IndexOf("//", StringComparison.Ordinal);
        return commentIndex >= 0 ? value[..commentIndex] : value;
    }

    private sealed record PvpThreshold(int Grade, int Threshold);

    private sealed record PvpTable(int MaximumGrade, IReadOnlyList<PvpThreshold> Thresholds)
    {
        public static PvpTable Empty { get; } = new(1, []);
    }
}
