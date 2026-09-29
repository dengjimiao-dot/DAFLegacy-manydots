using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// One mounted command handler owning exactly one protocol id (07 §4.6).
/// Handlers are thin orchestrations: parse the request, call Game/ domain
/// functions, enqueue replies through <see cref="ConnectionContext.Outbound"/>.
/// They must not hold cross-connection state; shared dependencies are
/// constructor-injected read-only catalogs or stores only.
/// </summary>
public interface IGameCommandHandler
{
    ushort ProtocolId { get; }

    /// <summary>Diagnostic label, usually the packet name such as "MAILBOX_OPEN".</summary>
    string Name { get; }

    /// <summary>Session phase required before dispatch (07 §4.8.1 U3).</summary>
    CommandPhase RequiredPhase { get; }

    /// <summary>True when the handler only makes sense on the channel port.</summary>
    bool RequiresChannelPort { get; }

    ValueTask HandleAsync(ConnectionContext context, PacketFrame frame, CancellationToken cancellationToken);
}
