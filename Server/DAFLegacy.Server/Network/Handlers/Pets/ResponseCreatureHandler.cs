using System.Buffers.Binary;
using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

/// <summary>
/// RESPONSE_CREATURE (CMD 104): re-sends the equipped Creature response the client waits for (07 §5 group 12).
/// </summary>
public sealed class ResponseCreatureHandler(ChannelSessionSenders senders) : IGameCommandHandler
{

    public ushort ProtocolId => (byte)CmdPacketType.RESPONSE_CREATURE;

    public string Name => "ResponseCreature";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        await senders.SendEquippedCreatureResponseAsync(context, cancellationToken);
    }
}
