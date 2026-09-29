using System.Text;

namespace DFLegacy.Server;

/// <summary>
/// 统一文件日志器(自参考项目 FileLogger.cs 迁移,变体:DI 注册的
/// <see cref="ILoggerProvider"/>,不做全局静态)。组合根把它挂进
/// Microsoft.Extensions.Logging 管线后,控制台与文件共享同一批日志事件;
/// 业务线程只入队,单个后台任务按序落盘,队列有界且非阻塞
/// (docs/design/07 §11 日志统一)。
/// </summary>
public sealed class BoundedQueueFileLoggerProvider : ILoggerProvider
{
    private const int DefaultQueueCapacity = 8192;
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);

    private readonly BoundedAsyncLogQueue _queue;
    private readonly Task _consumerTask;
    private readonly string _logPath;
    private readonly long _maximumFileBytes;
    private readonly string _header;
    private long _lastDropNotice;
    private int _disposed;

    public BoundedQueueFileLoggerProvider(LoggingOptions options, string homeDirectory)
    {
        ArgumentNullException.ThrowIfNull(options);
        var directory = string.IsNullOrWhiteSpace(options.Directory)
            ? homeDirectory
            : PathResolver.Resolve(options.Directory, homeDirectory);
        Directory.CreateDirectory(directory);
        _logPath = Path.Combine(directory, options.FileName);
        _maximumFileBytes = options.MaximumFileBytes;

        // server.log 含中文诊断字段,显式写入 BOM 方便 Windows 查看器识别 UTF-8。
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        _header = $"=== DFLegacy.Server started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===";
        File.WriteAllText(_logPath, _header + Environment.NewLine, encoding);

        _queue = new BoundedAsyncLogQueue(
            options.QueueCapacity > 0 ? options.QueueCapacity : DefaultQueueCapacity);
        _consumerTask = Task.Run(() => ProcessQueueAsync(encoding));
    }

    /// <summary>日志文件的完整路径,供启动日志提示。</summary>
    public string LogPath => _logPath;

    public ILogger CreateLogger(string categoryName) =>
        new BoundedQueueFileLogger(this, categoryName);

    internal void Enqueue(string line)
    {
        if (_queue.TryEnqueue(line))
        {
            return;
        }

        // 丢弃提示按 1、1000 的倍数节流,避免队列满时反过来刷爆 stderr。
        var dropped = _queue.DroppedCount;
        if (dropped == 1 || dropped % 1000 == 0)
        {
            var previous = Interlocked.Exchange(ref _lastDropNotice, dropped);
            if (previous != dropped)
            {
                Console.Error.WriteLine(
                    $"[FileLogger] bounded queue full; dropped={dropped}.");
            }
        }
    }

    private async Task ProcessQueueAsync(Encoding encoding)
    {
        await foreach (var line in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                if (_maximumFileBytes > 0
                    && new FileInfo(_logPath).Length > _maximumFileBytes)
                {
                    Rotate();
                }

                // 仅后台消费者访问日志文件,无需额外加锁;每次写完立即关闭句柄。
                File.AppendAllText(_logPath, line, encoding);
            }
            catch (Exception exception)
            {
                // 不能回头调用日志(会再次入队形成递归);单条失败不终止消费者。
                Console.Error.WriteLine($"[FileLogger] append failed: {exception.Message}");
            }
        }
    }

    private void Rotate()
    {
        var rotatedPath = _logPath + ".1";
        if (File.Exists(rotatedPath))
        {
            File.Delete(rotatedPath);
        }

        File.Move(_logPath, rotatedPath);
        File.WriteAllText(_logPath, _header + Environment.NewLine, new UTF8Encoding(true));
    }

    /// <summary>停止接收新日志并等待已入队日志落盘(参考项目 Shutdown 语义)。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.TryComplete();
        try
        {
            if (!_consumerTask.Wait(ShutdownTimeout))
            {
                Console.Error.WriteLine(
                    "[FileLogger] shutdown timed out before pending logs were written.");
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[FileLogger] shutdown wait failed: {exception.Message}");
        }
    }

    private sealed class BoundedQueueFileLogger(
        BoundedQueueFileLoggerProvider provider,
        string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            // 时间戳在调用线程生成,记录事件实际发生的时刻而非后台落盘时刻。
            var line = new StringBuilder();
            line.Append('[')
                .Append(DateTime.Now.ToString("HH:mm:ss.fff"))
                .Append("] [")
                .Append(ToLevelLabel(logLevel))
                .Append("] ")
                .Append(categoryName)
                .Append(": ")
                .Append(formatter(state, exception));
            if (exception is not null)
            {
                line.Append(Environment.NewLine).Append(exception);
            }

            line.Append(Environment.NewLine);
            provider.Enqueue(line.ToString());
        }

        private static string ToLevelLabel(LogLevel logLevel) => logLevel switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???",
        };
    }
}
