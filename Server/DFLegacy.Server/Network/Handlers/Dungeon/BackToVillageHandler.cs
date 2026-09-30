using System.Buffers.Binary;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// BACK_2_VILLAGE (CMD 142): returns from the dungeon-selection screen (07 §5 group 13).
/// </summary>
public sealed class BackToVillageHandler(
    ChannelSessionSenders senders) : IGameCommandHandler
{
    public ushort ProtocolId => (byte)CmdPacketType.BACK_2_VILLAGE;

    public string Name => "BACK_2_VILLAGE";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        if (session.Character.Active is { TutorialStarted: false })
        {
            RuntimeLogger.Info(
                "Ignored return from dungeon selection for {CharacterId} until the tutorial is complete.",
                session.Character.Active.Id);
            return;
        }

        // The DFLegacy dungeon-selection screen uses command
        // 142 instead of the in-dungeon GIVEUP_GAME command.
        await senders.ReturnToTownFromDungeonAsync(context, "back-to-village");
    }
}
