using System.Net;
using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

/// <summary>
/// The single entry point handlers receive instead of the former giant
/// method's closure world (07 §4.2). Handlers depend only on the members
/// they need; session state stays behind <see cref="State"/> domains.
/// </summary>
public sealed class ConnectionContext
{
    public ConnectionContext(
        RuntimeSession runtime,
        IPEndPoint? remote,
        bool isChannelPort,
        ChannelSessionState state,
        IConnectionQueue queue,
        CancellationToken disconnecting)
    {
        Runtime = runtime;
        Remote = remote;
        IsChannelPort = isChannelPort;
        State = state;
        Queue = queue;
        Disconnecting = disconnecting;
        Timers = new ConnectionTimerScheduler(this, disconnecting);
    }

    /// <summary>Runtime tracing/counting session for this connection.</summary>
    public RuntimeSession Runtime { get; }

    public IPEndPoint? Remote { get; }

    /// <summary>Preserves the former isChannelPort semantics: channel (game) versus entrance port.</summary>
    public bool IsChannelPort { get; }

    public ChannelSessionState State { get; }

    public IConnectionQueue Queue { get; }

    public IConnectionTimers Timers { get; }

    /// <summary>Connected to the connection cancellation source; cancelled when the connection dies.</summary>
    public CancellationToken Disconnecting { get; }

    /// <summary>
    /// Connection-level kick wired by the pump: closes the socket and cancels
    /// the connection (the former KickConnection local function). Handlers
    /// hand it to services that must terminate this connection, such as the
    /// character-session lease being claimed by a newer login.
    /// </summary>
    public Action? KickConnection { get; set; }

    /// <summary>
    /// Reads the immediately following frame when one is already buffered,
    /// for the only lookahead consumer: the CHANGE_SKILLSLOT quick-slot swap
    /// pair. Wired by the pump, which keeps the delay, the cipher decode and
    /// the packet accounting in the transport layer (07 §4.8.1 U1); a frame
    /// the accept predicate rejects is queued back for the normal read loop.
    /// </summary>
    public Func<TimeSpan, Func<PacketFrame, bool>, CancellationToken, ValueTask<AdjacentFrameResult>>? ReadAdjacentFrame { get; set; }
}

/// <summary>
/// Outcome of a pump-side adjacent-frame read: <see cref="DecodeFailed"/>
/// reproduces the legacy branch's abort-without-reply path, an empty frame
/// means nothing was buffered (or the frame was queued back).
/// </summary>
public readonly record struct AdjacentFrameResult(PacketFrame? Frame, bool DecodeFailed);
