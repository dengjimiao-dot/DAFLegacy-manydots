using System.Text;

namespace DFLegacy.Server;

public sealed class StaminaRecoveryCatalog
{
    private const string ScriptPath = "etc/serverparameter.etc";
    private const string StartTag = "[stamina recovery cost]";
    private const string EndTag = "[/stamina recovery cost]";

    private static readonly int[] FallbackCosts =
    [
        0, 0, 0, 0, 0, 505, 1425, 2345, 3265, 4184,
        5104, 6024, 6944, 7864, 8783, 9703, 10623, 11543, 16617, 17843,
        19070, 20296, 21522, 22749, 23975, 25202, 26428, 27654, 28881, 30107,
        31334, 32560, 33786, 35013, 36239, 37466, 38692, 39918, 41145, 42371,
        43598, 44824, 46050, 47277, 48503, 49730, 50956, 52182, 53409, 61465,
        62844, 64224, 65604, 66983, 68363, 69743, 71123, 72502, 73882, 75262,
        76641, 78021, 79401, 80780, 82160, 83540, 84920, 86299, 87679, 89059,
        90438, 91818, 93198, 94577, 95957, 97337, 98716, 100096, 101476, 102856,
        104235, 105615, 106995, 108374, 109754, 111134, 112514, 113893, 115273,
        116653, 118032, 119412, 120792, 122171, 123551, 124931, 126311, 127690,
        129070
    ];

    private readonly ScriptFileSystem _scripts;
    private readonly ILogger<StaminaRecoveryCatalog> _logger;
    private readonly Lazy<int[]> _costs;

    public StaminaRecoveryCatalog(
        ScriptFileSystem scripts,
        ILogger<StaminaRecoveryCatalog> logger)
    {
        _scripts = scripts;
        _logger = logger;
        _costs = new Lazy<int[]>(
            LoadCosts,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public int Count => _costs.Value.Length;

    public void Initialize() => _ = _costs.Value;

    public int GetCost(int level)
    {
        var costs = _costs.Value;
        if (costs.Length == 0)
        {
            return 0;
        }

        var normalizedLevel = Math.Max(1, level);
        return Math.Max(0, costs[Math.Min(costs.Length, normalizedLevel) - 1]);
    }

    internal static int[] Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var start = text.IndexOf(StartTag, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return [];
        }

        start += StartTag.Length;
        var end = text.IndexOf(EndTag, start, StringComparison.OrdinalIgnoreCase);
        if (end < 0)
        {
            end = text.Length;
        }

        var costs = new List<int>();
        foreach (var rawLine in text[start..end].Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var comment = rawLine.IndexOf("//", StringComparison.Ordinal);
            var value = comment < 0 ? rawLine : rawLine[..comment];
            if (int.TryParse(value.Trim(), out var cost))
            {
                costs.Add(Math.Max(0, cost));
            }
        }

        return costs.ToArray();
    }

    private int[] LoadCosts()
    {
        if (!_scripts.FileExists(ScriptPath))
        {
            _logger.LogWarning(
                "Stamina recovery table {ScriptPath} was not found in {Source}; using the DF2008 fallback table.",
                ScriptPath,
                _scripts.SourceDescription);
            return FallbackCosts.ToArray();
        }

        try
        {
            var parsed = Parse(_scripts.ReadAllText(ScriptPath, PvfEncodings.Cp949()));
            if (parsed.Length == 0)
            {
                throw new InvalidDataException(
                    $"{ScriptPath} does not contain any {StartTag} rows.");
            }

            _logger.LogInformation(
                "Cached {Count} stamina recovery costs from {Source}.",
                parsed.Length,
                _scripts.SourceDescription);
            return parsed;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Could not parse stamina recovery costs from {ScriptPath}; using the DF2008 fallback table.",
                ScriptPath);
            return FallbackCosts.ToArray();
        }
    }
}
