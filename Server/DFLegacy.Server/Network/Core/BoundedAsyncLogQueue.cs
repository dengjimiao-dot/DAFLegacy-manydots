using System.Threading.Channels;

namespace DFLegacy.Server;

/// <summary>
/// Fixed-capacity, non-blocking log queue (docs/design/07 §11 日志统一;
/// 形态参考参考项目 BoundedAsyncLogQueue)。网络处理路径绝不允许等待磁盘
/// I/O,也不允许无界堆积待写日志:队列满时 <see cref="TryEnqueue"/> 直接
/// 计一记丢弃并返回 false,由调用方按节流规则提示。
/// </summary>
internal sealed class BoundedAsyncLogQueue
{
    private readonly Channel<string> _channel;
    private long _droppedCount;

    public BoundedAsyncLogQueue(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(capacity, 0);

        Capacity = capacity;
        _channel = Channel.CreateBounded<string>(
            new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false,
            });
    }

    public int Capacity { get; }

    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    public bool TryEnqueue(string line)
    {
        if (_channel.Writer.TryWrite(line))
        {
            return true;
        }

        Interlocked.Increment(ref _droppedCount);
        return false;
    }

    public ChannelReader<string> Reader => _channel.Reader;

    public bool TryComplete() => _channel.Writer.TryComplete();
}
