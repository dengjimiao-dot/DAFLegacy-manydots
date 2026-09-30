using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// Shared pre-handler pipeline (07 §4.8.1 U3): enforces the per-handler port
/// and phase gates before the handler body runs. During the strangler
/// migration a gate failure returns false so the still-mounted legacy branch
/// reproduces the registered rejection path byte for byte; handlers keep
/// their own registered gate semantics inside <see cref="IGameCommandHandler.HandleAsync"/>
/// so they remain self-contained once the legacy chain is retired.
/// </summary>
public sealed class GameCommandDispatcher
{
    public async ValueTask<bool> TryDispatchAsync(
        ConnectionContext context,
        PacketFrame frame,
        IGameCommandHandler handler,
        CancellationToken cancellationToken)
    {
        if (handler.RequiresChannelPort && !context.IsChannelPort)
        {
            return false;
        }

        if (handler.RequiredPhase != CommandPhase.Any
            && context.State.CurrentPhase < handler.RequiredPhase)
        {
            RuntimeLogger.Debug(
                "Gate refused {Handler} (protocol {ProtocolId}) at phase {Phase}.",
                handler.Name,
                handler.ProtocolId,
                context.State.CurrentPhase);
            return false;
        }

        await handler.HandleAsync(context, frame, cancellationToken);
        return true;
    }
}
