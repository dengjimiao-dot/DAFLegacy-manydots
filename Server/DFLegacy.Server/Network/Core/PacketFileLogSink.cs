using System.Text;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// 逐包十六进制捕获(自参考项目 PacketFileLogger.cs 迁移,变体:实现
/// <see cref="IPacketTapSink"/> 挂在既有 Tap 漏斗上,不做全局静态)。
/// 默认关闭;开启后每收发一帧写两条:元信息行(方向/类型/协议/长度/CRC/
/// 明文解码口径)与受限长度的 body、wire 十六进制。只做排版,不涉及任何
/// 加解密(PacketPlainText 等工具在 Network/Core 独立提供)。
/// </summary>
public sealed class PacketFileLogSink : IPacketTapSink, IDisposable
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);

    private readonly BoundedAsyncLogQueue _queue;
    private readonly Task _consumerTask;
    private readonly string _logPath;
    private readonly long _maximumFileBytes;
    private readonly int _hexDumpLimit;
    private readonly string _header;
    private long _lastDropNotice;
    private int _disposed;

    public PacketFileLogSink(PacketCaptureOptions options, string homeDirectory)
    {
        ArgumentNullException.ThrowIfNull(options);
        var directory = string.IsNullOrWhiteSpace(options.Directory)
            ? Path.Combine(homeDirectory, "logs")
            : PathResolver.Resolve(options.Directory, homeDirectory);
        Directory.CreateDirectory(directory);
        _logPath = Path.Combine(directory, options.FileName);
        _maximumFileBytes = options.MaximumFileBytes;
        _hexDumpLimit = options.HexDumpLimit;

        _header =
            $"=== DFLegacy packet capture started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==="
            + Environment.NewLine
            + "=== RX wire frames: 10B client header + payload; TX wire: 6B server header + payload ==="
            + Environment.NewLine;
        File.WriteAllText(_logPath, _header, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        _queue = new BoundedAsyncLogQueue(
            options.QueueCapacity > 0 ? options.QueueCapacity : 4096);
        _consumerTask = Task.Run(ProcessQueueAsync);
    }

    public string LogPath => _logPath;

    public bool Active => true;

    public void Publish(
        Guid sessionId,
        string service,
        string direction,
        byte type,
        byte protocolId,
        int totalLength,
        uint crc32,
        bool crcValid,
        TapDecodeMode decodeMode,
        ReadOnlySpan<byte> body,
        ReadOnlySpan<byte> wire = default)
    {
        var protocolName = PacketNames.TryGet(type, protocolId, out var name) ? name : "-";
        var line = new StringBuilder();
        line.Append('[')
            .Append(DateTime.Now.ToString("HH:mm:ss.fff"))
            .Append("] [")
            .Append(direction)
            .Append("] ")
            .Append(sessionId.ToString("N")[..8])
            .Append(' ')
            .Append(service)
            .Append(" type=")
            .Append(type)
            .Append(" protocol=0x")
            .Append(protocolId.ToString("X2"))
            .Append(" name=")
            .Append(protocolName)
            .Append(" len=")
            .Append(totalLength)
            .Append(" crc=0x")
            .Append(crc32.ToString("X8"))
            .Append(" valid=")
            .Append(crcValid)
            .Append(" decode=")
            .Append(decodeMode)
            .Append(Environment.NewLine);

        AppendHexLine(line, "  body: ", body);
        AppendHexLine(line, "  wire: ", wire);
        _queue.TryEnqueue(line.ToString());
    }

    private void AppendHexLine(StringBuilder line, string label, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            return;
        }

        var limit = Math.Min(bytes.Length, _hexDumpLimit);
        line.Append(label)
            .Append('(')
            .Append(bytes.Length)
            .Append("B) [")
            .Append(HexDump.ToSpacedHex(bytes[..limit]))
            .Append(bytes.Length > limit ? " ...]" : "]")
            .Append(Environment.NewLine);
    }

    private async Task ProcessQueueAsync()
    {
        await foreach (var entry in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                if (_maximumFileBytes > 0
                    && new FileInfo(_logPath).Length + entry.Length > _maximumFileBytes)
                {
                    Rotate();
                }

                File.AppendAllText(
                    _logPath,
                    entry,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            catch (Exception exception)
            {
                // 抓包失败绝不能影响在线传输。
                Console.Error.WriteLine($"[PacketCapture] append failed: {exception.Message}");
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
        File.WriteAllText(
            _logPath,
            _header,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

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
                    "[PacketCapture] shutdown timed out before pending packets were written.");
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[PacketCapture] shutdown wait failed: {exception.Message}");
        }
    }
}
