using Microsoft.Extensions.Logging.Abstractions;

namespace DFLegacy.Server;

/// <summary>
/// 静态日志门面：组合根在构造任何 handler 之前调用一次
/// <see cref="Configure"/>，此后各处直接以 RuntimeLogger.Info/Warn 等记录，
/// 全部事件共用同一分类（<see cref="CategoryName"/>），内部 logger 仅此
/// 一次初始化。未配置时用 NullLogger 兜底（静默丢弃），供自测直接
/// 调用时免装配。
/// </summary>
public static class RuntimeLogger
{
    private const string CategoryName = "DFLegacy.Server";

    private static ILogger _logger = NullLogger.Instance;

    public static void Configure(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _logger = loggerFactory.CreateLogger(CategoryName);
    }

    public static void Debug(string message, params object?[] args) =>
        _logger.LogDebug(message, args);

    public static void Info(string message, params object?[] args) =>
        _logger.LogInformation(message, args);

    public static void Warn(string message, params object?[] args) =>
        _logger.LogWarning(message, args);

    public static void Warn(Exception? exception, string message, params object?[] args) =>
        _logger.LogWarning(exception, message, args);

    public static void Error(string message, params object?[] args) =>
        _logger.LogError(message, args);
}
