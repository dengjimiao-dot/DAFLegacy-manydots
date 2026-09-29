using System.Threading.Channels;
using DFLegacy.Protocol;
using DFLegacy.Server;
using Microsoft.Extensions.Logging;

namespace DFLegacy.Mcp;

/// <summary>
/// G1 抓包枢纽（docs/design/09-mcp-packet-tap.md §4.2/§6/§7）：实现
/// <see cref="IPacketTapSink"/> 接缝，Publish 内完成字节拷贝截断 → 报文名/明文
/// 富化 → 记全局 seq → 入 ring → 按订阅者谓词预筛后扇出。所有追踪路径都
/// try/catch 包裹——追踪绝不影响会话。
/// </summary>
public sealed class PacketTapHub : IPacketTapSink
{
    public const int RingCapacity = 4096;
    public const int MaxBodyBytes = 4096;
    public const int MaxWireBytes = 8192;
    public const int SubscriberChannelCapacity = 1024;
    public const int MaxWatchLimit = 200;
    public const int MaxWaitMilliseconds = 60_000;

    private readonly object _gate = new();
    private readonly Queue<PacketTapEvent> _ring = new();
    private readonly List<PacketTapSubscription> _subscribers = [];
    private readonly ILogger<PacketTapHub> _logger;
    private long _seq;

    public PacketTapHub(ILogger<PacketTapHub> logger)
    {
        _logger = logger;
    }

    public bool Active => true;

    /// <summary>已分配的最后一个事件序号（无事件时为 0）。</summary>
    public long CurrentSeq
    {
        get { lock (_gate) { return _seq; } }
    }

    /// <summary>ring 中最旧事件的 seq；ring 为空时为 CurrentSeq + 1。</summary>
    public long OldestSeq
    {
        get { lock (_gate) { return _ring.Count > 0 ? _ring.Peek().Seq : _seq + 1; } }
    }

    public int RingCount
    {
        get { lock (_gate) { return _ring.Count; } }
    }

    public int SubscriberCount
    {
        get { lock (_gate) { return _subscribers.Count; } }
    }

    void IPacketTapSink.Publish(
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
        ReadOnlySpan<byte> wire)
    {
        try
        {
            PublishCore(sessionId, service, direction, type, protocolId, totalLength,
                crc32, crcValid, decodeMode, body, wire);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Packet tap publish failed; the event is dropped.");
        }
    }

    private void PublishCore(
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
        ReadOnlySpan<byte> wire)
    {
        var bodyCopy = body.ToArray();
        var bodyTruncated = bodyCopy.Length > MaxBodyBytes;
        if (bodyTruncated)
        {
            bodyCopy = bodyCopy[..MaxBodyBytes];
        }

        byte[]? wireCopy = null;
        var wireTruncated = false;
        if (!wire.IsEmpty)
        {
            wireCopy = wire.Length > MaxWireBytes ? wire[..MaxWireBytes].ToArray() : wire.ToArray();
            wireTruncated = wire.Length > MaxWireBytes;
        }

        string? plainText = null;
        try
        {
            plainText = decodeMode switch
            {
                TapDecodeMode.ClientCmd => PacketPlainText.Decode(type, protocolId, bodyCopy),
                TapDecodeMode.Server => PacketPlainText.DecodeServer(type, protocolId, bodyCopy),
                _ => null,
            };
        }
        catch
        {
            // 明文解码失败降级为 null，绝不让追踪影响会话。
        }

        PacketNames.TryGet(type, protocolId, out var packetName);
        var timestamp = DateTimeOffset.Now;

        lock (_gate)
        {
            var @event = new PacketTapEvent(
                ++_seq,
                timestamp,
                sessionId,
                service,
                direction,
                type,
                protocolId,
                packetName,
                totalLength,
                crc32,
                crcValid,
                bodyCopy,
                bodyTruncated,
                wireCopy,
                wireTruncated,
                plainText);
            _ring.Enqueue(@event);
            while (_ring.Count > RingCapacity)
            {
                _ring.Dequeue();
            }

            foreach (var subscriber in _subscribers)
            {
                if (!subscriber.Predicate(@event))
                {
                    continue;
                }

                if (subscriber.Channel.Writer.TryWrite(@event))
                {
                    continue;
                }

                // 通道满：丢最旧、保最新（DropOldest 语义），并如实计数。
                // 写入全部发生在 _gate 内，这里不会有并发写竞争。
                subscriber.Channel.Reader.TryRead(out _);
                subscriber.Channel.Writer.TryWrite(@event);
                Interlocked.Increment(ref subscriber.DroppedCount);
            }
        }
    }

    /// <summary>按 seq 取单个留存事件（packet_get）；已被 ring 淘汰时返回 null。</summary>
    public PacketTapEvent? GetBySeq(long seq)
    {
        lock (_gate)
        {
            foreach (var @event in _ring)
            {
                if (@event.Seq == seq)
                {
                    return @event;
                }
            }

            return null;
        }
    }

    public PacketTapSubscription Subscribe(Func<PacketTapEvent, bool> predicate)
    {
        var subscription = new PacketTapSubscription(this, predicate);
        lock (_gate)
        {
            // 与 Publish 同锁：StartSeq 采样与扇出注册原子，watch 不会漏也不会重。
            subscription.StartSeq = _seq + 1;
            _subscribers.Add(subscription);
        }

        return subscription;
    }

    public void Unsubscribe(PacketTapSubscription subscription)
    {
        lock (_gate)
        {
            _subscribers.Remove(subscription);
        }

        subscription.Channel.Writer.TryComplete();
    }

    /// <summary>
    /// 游标长轮询（§7）：tail -f 语义。先订阅、再按 afterSeq 语义回放 ring、
    /// 然后清空并等待订阅通道，直至 limit 或 waitMs。同一事件经 ring 回放与
    /// 订阅通道各到达一次时按 seq 去重，不会重复返回。
    /// </summary>
    public async Task<PacketTapWatchResult> WatchAsync(
        long afterSeq,
        int limit,
        int waitMs,
        PacketTapFilter? filter,
        CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, MaxWatchLimit);
        waitMs = Math.Clamp(waitMs, 0, MaxWaitMilliseconds);
        var predicate = filter?.Compile() ?? (_ => true);
        var subscription = Subscribe(predicate);
        try
        {
            long cursor;
            long ringOldestSeq;
            var cursorLapsed = false;
            var events = new List<PacketTapEvent>();

                lock (_gate)
            {
                ringOldestSeq = _ring.Count > 0 ? _ring.Peek().Seq : _seq + 1;
                long replayStart;
                if (afterSeq < 0)
                {
                    // -1 = 从留存最早开始回放。
                    replayStart = ringOldestSeq;
                }
                else if (afterSeq == 0)
                {
                    // 0 = 不回放，以订阅时刻为起点，只返回此后新产生的事件。
                    replayStart = subscription.StartSeq;
                }
                else
                {
                    // afterSeq = 下一个尚未读取的 seq（返回 seq >= afterSeq）。
                    // 游标已被 ring 淘汰时从留存最早开始，中间有缺口。
                    replayStart = afterSeq;
                    if (replayStart < ringOldestSeq)
                    {
                        cursorLapsed = true;
                        replayStart = ringOldestSeq;
                    }
                }

                cursor = replayStart - 1;
                foreach (var @event in _ring)
                {
                    if (events.Count >= limit)
                    {
                        break;
                    }

                    if (@event.Seq >= replayStart && predicate(@event))
                    {
                        events.Add(@event);
                        cursor = @event.Seq;
                    }
                }
            }

            while (events.Count < limit && subscription.Reader.TryRead(out var buffered))
            {
                if (buffered.Seq > cursor)
                {
                    events.Add(buffered);
                    cursor = buffered.Seq;
                }
            }

            if (events.Count < limit && waitMs > 0)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(waitMs);
                try
                {
                    while (events.Count < limit)
                    {
                        // WaitToReadAsync 挂起而非轮询（§7），无新包时最多等 waitMs。
                        if (!await subscription.Reader.WaitToReadAsync(timeout.Token).ConfigureAwait(false))
                        {
                            break;
                        }

                        while (events.Count < limit && subscription.Reader.TryRead(out var @event))
                        {
                            if (@event.Seq > cursor)
                            {
                                events.Add(@event);
                                cursor = @event.Seq;
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // waitMs 到期：返回已有内容。
                }
            }

            return new PacketTapWatchResult(
                events,
                cursor + 1,
                ringOldestSeq,
                cursorLapsed,
                subscription.Dropped);
        }
        finally
        {
            Unsubscribe(subscription);
        }
    }
}

/// <summary>单个 MCP 调用方的实时订阅（§1.5 订阅通道）。</summary>
public sealed class PacketTapSubscription : IDisposable
{
    private readonly PacketTapHub _hub;
    internal long DroppedCount;

    internal PacketTapSubscription(PacketTapHub hub, Func<PacketTapEvent, bool> predicate)
    {
        _hub = hub;
        Predicate = predicate;
        // 属性名 Channel 遮蔽了 System.Threading.Channels.Channel 类型，静态
        // 工厂需全限定调用。
        Channel = System.Threading.Channels.Channel.CreateBounded<PacketTapEvent>(
            new BoundedChannelOptions(PacketTapHub.SubscriberChannelCapacity)
        {
            // 满时由 hub 手工丢最旧并计数，故用 DropWrite 让 TryWrite 可失败。
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = true,
        });
    }

    internal Func<PacketTapEvent, bool> Predicate { get; }
    internal long StartSeq { get; set; }
    internal Channel<PacketTapEvent> Channel { get; }
    public ChannelReader<PacketTapEvent> Reader => Channel.Reader;

    /// <summary>该订阅通道积压丢弃计数（DropOldest），&gt;0 说明流量超过消费速度。</summary>
    public long Dropped => Interlocked.Read(ref DroppedCount);

    public void Dispose() => _hub.Unsubscribe(this);
}

/// <summary>packet_watch 的返回（§6.1）。</summary>
public sealed record PacketTapWatchResult(
    IReadOnlyList<PacketTapEvent> Events,
    long NextSeq,
    long RingOldestSeq,
    bool CursorLapsed,
    long SubscriberDropped);
