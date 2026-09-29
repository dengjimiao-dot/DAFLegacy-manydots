using System.Net;

namespace DFLegacy.Server;

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
        IOutboundQueue outbound,
        ILogger logger,
        CancellationToken disconnecting)
    {
        Runtime = runtime;
        Remote = remote;
        IsChannelPort = isChannelPort;
        State = state;
        Outbound = outbound;
        Logger = logger;
        Disconnecting = disconnecting;
        Timers = new ConnectionTimerScheduler(this, disconnecting);
    }

    /// <summary>Runtime tracing/counting session for this connection.</summary>
    public RuntimeSession Runtime { get; }

    public IPEndPoint? Remote { get; }

    /// <summary>Preserves the former isChannelPort semantics: channel (game) versus entrance port.</summary>
    public bool IsChannelPort { get; }

    public ChannelSessionState State { get; }

    public IOutboundQueue Outbound { get; }

    public IConnectionTimers Timers { get; }

    public ILogger Logger { get; }

    /// <summary>Connected to the connection cancellation source; cancelled when the connection dies.</summary>
    public CancellationToken Disconnecting { get; }

    /// <summary>
    /// Connection-level kick wired by the pump: closes the socket and cancels
    /// the connection (the former KickConnection local function). Handlers
    /// hand it to services that must terminate this connection, such as the
    /// character-session lease being claimed by a newer login.
    /// </summary>
    public Action? KickConnection { get; set; }
}
