using DFLegacy.Protocol;
using DFLegacy.Server;
using Microsoft.Extensions.Logging;

/// <summary>
/// 统一日志系统自测（docs/design/07 §11 日志统一）：
/// 有界队列丢包语义、ILoggerProvider 文件落盘与轮转、逐包捕获 sink、
/// Tap 多端口并集分发。
/// </summary>
internal static class FileLoggerSmokeTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        // 十六进制排版工具（自 LogPacket 拆出的共享工具）
        check(HexDump.ToSpacedHex([]) == string.Empty,
            "spaced hex of empty input stays empty");
        check(HexDump.ToSpacedHex([0x01, 0xAB, 0x0F]) == "01 AB 0F",
            "spaced hex renders uppercase bytes separated by single spaces");

        var root = Path.Combine(
            Path.GetTempPath(),
            "dflegacy-logging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await RunProviderChecksAsync(root, check);
            await RunPacketCaptureChecksAsync(root, check);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        RunQueueAndFanoutChecks(check);
    }

    private static async Task RunProviderChecksAsync(string root, Action<bool, string> check)
    {
        // 有界队列：满即丢弃并计数，不阻塞调用方
        var queue = new BoundedAsyncLogQueue(2);
        var accepted = queue.TryEnqueue("a") && queue.TryEnqueue("b");
        var rejected = queue.TryEnqueue("c");
        check(accepted && !rejected && queue.DroppedCount == 1,
            "bounded log queue drops instead of blocking once full");

        // 统一文件日志器：ILogger 事件落盘、级别与类别完整，Dispose 排空
        var provider = new BoundedQueueFileLoggerProvider(
            new LoggingOptions { Directory = "logs", FileName = "server.log", QueueCapacity = 64 },
            root);
        var logger = provider.CreateLogger("DFLegacy.Server.SmokeTests");
        logger.LogInformation("unified file logging {Value}", 42);
        logger.LogWarning("warning path");
        provider.Dispose();
        var text = await File.ReadAllTextAsync(provider.LogPath);
        check(text.Contains("unified file logging 42")
            && text.Contains("[WRN] DFLegacy.Server.SmokeTests: warning path"),
            "unified file logger persists ILogger events with level and category");

        // 轮转：超过上限时旧文件让位 .1
        var rotating = new BoundedQueueFileLoggerProvider(
            new LoggingOptions
            {
                Directory = "logs",
                FileName = "rotate.log",
                QueueCapacity = 256,
                MaximumFileBytes = 256,
            },
            root);
        var rotatingLogger = rotating.CreateLogger("Rotate");
        for (var index = 0; index < 64; index++)
        {
            rotatingLogger.LogInformation("padding line {Index} ................", index);
        }

        rotating.Dispose();
        check(File.Exists(rotating.LogPath + ".1"),
            "file logger rotates to .1 once the size cap is exceeded");
    }

    private static async Task RunPacketCaptureChecksAsync(string root, Action<bool, string> check)
    {
        var capturePath = string.Empty;
        using (var sink = new PacketFileLogSink(
            new PacketCaptureOptions
            {
                Enabled = true,
                Directory = "capture",
                HexDumpLimit = 4,
            },
            root))
        {
            capturePath = sink.LogPath;
            sink.Publish(
                Guid.NewGuid(),
                "channel:7001",
                "RX",
                GameProtocolEngine.CommandPacketType,
                (byte)CmdPacketType.LOGIN,
                totalLength: 12,
                crc32: 0x58C223BE,
                crcValid: true,
                TapDecodeMode.ClientCmd,
                body: [0x01, 0x00, 0xAB, 0xCD, 0xEF, 0x11],
                wire: [0xDE, 0xAD, 0xBE, 0xEF]);
        }

        var text = await File.ReadAllTextAsync(capturePath);
        check(text.Contains("name=ENUM_CMDPACKET_LOGIN")
            && text.Contains("decode=ClientCmd")
            && text.Contains("body: (6B) [01 00 AB CD ...]")
            && text.Contains("wire: (4B) [DE AD BE EF]"),
            "packet capture sink writes protocol name, decode mode and clamped hex");
    }

    private static void RunQueueAndFanoutChecks(Action<bool, string> check)
    {
        // Tap 多端口：单槽端口与注册端口并集分发，移除即停
        var runtime = new RuntimeState();
        var propertySink = new RecordingTapSink();
        var addedSink = new RecordingTapSink();
        runtime.TapSink = propertySink;
        runtime.AddTapSink(addedSink);
        var session = runtime.Add("channel:7001", "127.0.0.1:40000");
        runtime.TraceDatagram(session, "RX", [1, 2, 3], dumpLimit: 8);
        check(propertySink.Count == 1 && addedSink.Count == 1,
            "tap funnel fans out to the single-slot port and registered sinks");

        runtime.RemoveTapSink(addedSink);
        runtime.TraceDatagram(session, "RX", [1, 2, 3], dumpLimit: 8);
        check(propertySink.Count == 2 && addedSink.Count == 1,
            "removing a registered tap sink stops its deliveries");
    }

    private sealed class RecordingTapSink : IPacketTapSink
    {
        public bool Active => true;

        public int Count { get; private set; }

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
            ReadOnlySpan<byte> wire = default) =>
            Count++;
    }
}
