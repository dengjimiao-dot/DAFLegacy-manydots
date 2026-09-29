using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// ANTIBOT (CMD 161): a fire-and-forget client report. The heartbeat counter
/// advances without any answer ever coming back, so the report is consumed
/// quietly instead of tripping the unknown-packet warning once per minute.
/// No reply is ever sent.
/// </summary>
public sealed class AntibotHandler(ILogger<AntibotHandler> logger) : IGameCommandHandler
{
    public ushort ProtocolId => GameProtocolEngine.AntibotCommand;

    public string Name => "ANTIBOT";

    // Silent consumption must work before authentication; the report is
    // parsed and logged, never answered (07 §4.8.1).
    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public ValueTask HandleAsync(
        ConnectionContext context,
        PacketFrame frame,
        CancellationToken cancellationToken)
    {
        var parsedAntibot = AntibotReportParser.TryParse(frame.Body, out var antibotReport);
        logger.LogDebug(
            "Antibot report seq={Seq} parsed={Parsed} kind={Kind} declaredBytes={Bytes}.",
            antibotReport.Sequence,
            parsedAntibot,
            antibotReport.RecordKind,
            antibotReport.DeclaredLength);
        return ValueTask.CompletedTask;
    }
}
