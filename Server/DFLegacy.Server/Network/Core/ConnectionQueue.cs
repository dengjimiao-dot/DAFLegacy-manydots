using System.Threading.Channels;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// The only legal way to put bytes on a connection's stream (07 §4.4).
/// Senders enqueue; the per-connection single-writer pump serializes frames.
/// Backpressure blocks the sender when the bounded queue is full — packets
/// are never dropped or reordered.
/// </summary>
public interface IConnectionQueue : IAsyncDisposable
{
    ValueTask SendAsync(GameServerPacket packet, string traceLabel = "", CancellationToken cancellationToken = default);

    ValueTask SendBatchAsync(IReadOnlyList<GameServerPacket> packets, CancellationToken cancellationToken = default);
}

/// <summary>
/// A queued packet plus the completion source its sender awaits. Awaiting
/// real write completion keeps the legacy inline-write timing ("the caller
/// waits until the frame is on the stream") during the strangler migration.
/// </summary>
internal readonly record struct OutboundItem(
    GameServerPacket Packet,
    string TraceLabel,
    TaskCompletionSource Completion);

/// <summary>
/// Bounded per-connection outbound channel plus its single-writer pump task
/// (07 §4.4). The pump is the only writer on the stream and the single
/// accounting point for send counting and packet logging (07 §4.8.1 U4).
/// Closing completes the channel, drains already queued frames, then stops —
/// the stream never receives a torn packet.
/// </summary>
public sealed class ConnectionQueue : IConnectionQueue, IAsyncDisposable
{
    private const int DefaultCapacity = 256;

    private readonly Channel<OutboundItem> _channel;
    private readonly Task _pumpTask;

    public ConnectionQueue(
        Stream stream,
        RuntimeSession session,
        Action<GameServerPacket> reportSent,
        CancellationToken disconnecting = default,
        int capacity = DefaultCapacity)
    {
        _channel = Channel.CreateBounded<OutboundItem>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
        });
        _pumpTask = PumpAsync(stream, session, reportSent, disconnecting);
    }

    public async ValueTask SendAsync(
        GameServerPacket packet,
        string traceLabel = "",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        var item = new OutboundItem(
            packet,
            traceLabel,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        await _channel.Writer.WriteAsync(item, cancellationToken);
        await item.Completion.Task;
    }

    public async ValueTask SendBatchAsync(
        IReadOnlyList<GameServerPacket> packets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packets);
        var pending = new Task[packets.Count];
        for (var index = 0; index < packets.Count; index++)
        {
            var packet = packets[index]
                ?? throw new ArgumentException("The batch contains a null packet.", nameof(packets));
            var item = new OutboundItem(
                packet,
                string.Empty,
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            // Bounded backpressure applies between batch members: a slow peer
            // blocks the sender instead of growing the queue (07 §4.4).
            await _channel.Writer.WriteAsync(item, cancellationToken);
            pending[index] = item.Completion.Task;
        }

        foreach (var task in pending)
        {
            await task;
        }
    }

    private async Task PumpAsync(
        Stream stream,
        RuntimeSession session,
        Action<GameServerPacket> reportSent,
        CancellationToken disconnecting)
    {
        try
        {
            await foreach (var item in _channel.Reader.ReadAllAsync())
            {
                try
                {
                    await item.Packet.WriteAsync(stream, disconnecting);
                    Interlocked.Increment(ref session.SentPackets);
                    reportSent(item.Packet);
                    item.Completion.TrySetResult();
                }
                catch (OperationCanceledException) when (disconnecting.IsCancellationRequested)
                {
                    item.Completion.TrySetCanceled(disconnecting);
                }
                catch (Exception exception)
                {
                    RuntimeLogger.Warn(
                        exception,
                        "Could not deliver outbound packet {ProtocolId} on session {SessionId}.",
                        item.Packet.ProtocolId,
                        session.Id);
                    item.Completion.TrySetException(exception);
                }
            }
        }
        catch (ChannelClosedException)
        {
            // Writer completed by connection teardown after a full drain.
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _pumpTask;
    }
}
