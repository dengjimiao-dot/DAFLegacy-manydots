using DFLegacy.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

internal static class McpModuleLoaderSmokeTests
{
    public static void Run(Action<bool, string> check)
    {
        // 加载器纪律（docs/design/09-mcp-packet-tap.md §12）：任何失败只记警告、
        // 不抛出、不注册服务，游戏服务照常。

        // Enabled=false（默认）→ 短路，不触碰程序集加载。
        var disabledServices = new ServiceCollection();
        McpModuleLoader.TryAttach(
            disabledServices,
            new ServerOptions(),
            NullLogger.Instance);
        check(disabledServices.Count == 0,
            "module loader short-circuits without touching assembly loading when MCP is disabled");

        // Enabled=true 但输出目录无 DFLegacy.Mcp.dll → 记警告并继续。
        // SelfTests 输出目录刻意不放置模块 DLL，这里走的就是缺 DLL 分支。
        var missingServices = new ServiceCollection();
        McpModuleLoader.TryAttach(
            missingServices,
            new ServerOptions
            {
                Mcp = new McpOptions { Enabled = true, Host = "127.0.0.1", Port = 12222 }
            },
            WarningProbeLogger.Instance);
        check(missingServices.Count == 0,
            "module loader keeps the container empty when the module DLL is absent");

        var warned = WarningProbeLogger.Instance.SawWarning("DFLegacy.Mcp");
        check(warned,
            "module loader logs a warning when the module DLL cannot be loaded");

        // DLL 在位但初始化抛错的分支（警告 + 继续、TapSink 保持 null）由集成
        // 冒烟覆盖：SelfTests 引用面内没有模块程序集，无法在不破坏"源码级
        // 拔除后全解决方案构建通过"验收的前提下注入抛错的模块。
    }
}

/// <summary>只捕捉含指定片段的 Warning，供加载器分支断言。</summary>
internal sealed class WarningProbeLogger : ILogger
{
    public static readonly WarningProbeLogger Instance = new();

    private readonly List<string> _warnings = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel == LogLevel.Warning)
        {
            _warnings.Add(formatter(state, exception));
        }
    }

    public bool SawWarning(string fragment) =>
        _warnings.Any(message => message.Contains(fragment, StringComparison.Ordinal));
}
