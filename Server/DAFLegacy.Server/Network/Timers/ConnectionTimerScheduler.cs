namespace DAFLegacy.Server;

/// <summary>
/// Per-connection timer registry (07 §4.5). Due callbacks run on the timer
/// thread and may only enqueue into the owning connection's
/// <see cref="ChannelSessionState.Events"/>; the read loop interleaves them
/// with inbound frames at the single scheduling point.
/// </summary>
public interface IConnectionTimers
{
    IDisposable Register(TimeSpan due, Func<ConnectionContext, CancellationToken, ValueTask> callback);
}

/// <summary>
/// Monotonic fire-once timers. Each registration links its cancellation to
/// the connection's disconnect token; disposing a registration cancels it.
/// </summary>
public sealed class ConnectionTimerScheduler : IConnectionTimers
{
    private readonly ConnectionContext _owner;
    private readonly CancellationToken _disconnecting;

    public ConnectionTimerScheduler(ConnectionContext owner, CancellationToken disconnecting)
    {
        _owner = owner;
        _disconnecting = disconnecting;
    }

    public IDisposable Register(TimeSpan due, Func<ConnectionContext, CancellationToken, ValueTask> callback)
    {
        var registration = new TimerRegistration(_owner, due, callback, _disconnecting);
        registration.Start();
        return registration;
    }

    private sealed class TimerRegistration(
        ConnectionContext owner,
        TimeSpan due,
        Func<ConnectionContext, CancellationToken, ValueTask> callback,
        CancellationToken disconnecting) : IDisposable
    {
        private readonly CancellationTokenSource _cancellation =
            CancellationTokenSource.CreateLinkedTokenSource(disconnecting);

        public void Start() => _ = RunAsync();

        private async Task RunAsync()
        {
            try
            {
                await Task.Delay(due, _cancellation.Token);
                await callback(owner, _cancellation.Token);
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                RuntimeLogger.Warn(
                    exception,
                    "Connection timer callback failed on session {SessionId}.",
                    owner.Runtime.Id);
            }
        }

        public void Dispose() => _cancellation.Cancel();
    }
}
