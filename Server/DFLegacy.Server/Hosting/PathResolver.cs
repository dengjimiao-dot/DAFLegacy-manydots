namespace DFLegacy.Server;

/// <summary>
/// 路径契约的唯一归属地（docs/design/02 · R3 / R5）。
/// 配置文件中的路径可使用任意分隔符；反斜杠写法继续兼容，
/// 归一集中在这里，各模块不得自行 Replace。
/// </summary>
public static class PathResolver
{
    /// <summary>把配置中的路径归一为正斜杠形式（两种输入分隔符都接受）。</summary>
    public static string Normalize(string configured) =>
        configured.Replace('\\', '/');

    /// <summary>归一后按 anchor 解析为当前平台的绝对路径。</summary>
    public static string Resolve(string configured, string anchor) =>
        Path.GetFullPath(
            Normalize(configured).Replace('/', Path.DirectorySeparatorChar),
            anchor);

    /// <summary>
    /// 配置与数据锚点：命令行 <c>--home &lt;path&gt;</c> 优先，
    /// 其次环境变量 <c>DFLEGACY_HOME</c>，默认仍是
    /// <see cref="AppContext.BaseDirectory"/>（保持现有 Windows 部署布局）。
    /// </summary>
    public static string ResolveHome(
        IReadOnlyList<string> args,
        Func<string, string?>? environment = null)
    {
        environment ??= static name => Environment.GetEnvironmentVariable(name);

        for (var index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(args[index], "--home", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(args[index + 1]))
            {
                return Path.GetFullPath(args[index + 1]);
            }
        }

        var fromEnvironment = environment("DFLEGACY_HOME");
        return string.IsNullOrWhiteSpace(fromEnvironment)
            ? AppContext.BaseDirectory
            : Path.GetFullPath(fromEnvironment);
    }
}
