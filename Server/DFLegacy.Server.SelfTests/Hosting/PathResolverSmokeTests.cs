using DFLegacy.Server;

internal static class PathResolverSmokeTests
{
    public static void Run(Action<bool, string> check)
    {
        check(PathResolver.Normalize("..\\Script.pvf") == "../Script.pvf",
            "Normalize rewrites backslashes to forward slashes");
        check(PathResolver.Normalize("data/state.json") == "data/state.json",
            "Normalize leaves forward slashes untouched");

        var anchor = Path.Combine(Path.GetTempPath(), "dflegacy-anchor");
        var resolved = PathResolver.Resolve("data/state.json", anchor);
        check(Path.IsPathRooted(resolved)
                && resolved.EndsWith(Path.Combine("data", "state.json"), StringComparison.Ordinal),
            "Resolve anchors configured paths at the given home");

        // 旧的反斜杠配置在同一锚点下解析出同一路径（R3 兼容性）。
        check(PathResolver.Resolve("..\\Script.pvf", anchor)
                == PathResolver.Resolve("../Script.pvf", anchor),
            "backslash and slash configurations resolve identically");

        // 锚点优先级：--home > DFLEGACY_HOME > BaseDirectory（R5）。
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        check(PathResolver.ResolveHome(new[] { "--home", "/tmp/dflegacy" }, environment.GetOrEmptyFallback)
                == Path.GetFullPath("/tmp/dflegacy"),
            "ResolveHome prefers the --home argument");
        environment["DFLEGACY_HOME"] = "/var/lib/dflegacy";
        check(PathResolver.ResolveHome(Array.Empty<string>(), environment.GetOrEmptyFallback)
                == Path.GetFullPath("/var/lib/dflegacy"),
            "ResolveHome falls back to DFLEGACY_HOME");
        check(PathResolver.ResolveHome(Array.Empty<string>(), _ => null)
                == AppContext.BaseDirectory,
            "ResolveHome defaults to AppContext.BaseDirectory");
    }
}

file static class DictionaryExtensions
{
    public static string? GetOrEmptyFallback(
        this IReadOnlyDictionary<string, string> dictionary,
        string name) =>
        dictionary.TryGetValue(name, out var value) ? value : null;
}
