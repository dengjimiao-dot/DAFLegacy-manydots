using System.Text;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// CREATURE_SCRIPT_MESSAGE (CMD 132): creature talk with an equipped Creature (07 §5 group 5).
/// </summary>
public sealed class CreatureScriptMessageHandler(
    ) : IGameCommandHandler
{
    private static readonly Encoding ClientEncoding = PvfEncodings.Cp936Lossy();


    public ushort ProtocolId => (byte)CmdPacketType.CREATURE_SCRIPT_MESSAGE;

    public string Name => "CREATURE_SCRIPT_MESSAGE";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        if (session.Character.Active is null
            || !ChatParsers.TryReadSendMessage(request.Body, out var messageRequest))
        {
            RuntimeLogger.Warn(
                "Rejected malformed CREATURE_SCRIPT_MESSAGE request for character {CharacterId}: body={Body}.",
                session.Character.Active?.Id,
                Convert.ToHexString(request.Body));
            return;
        }

        // DNF.exe 0x722583 builds CMD 132 as the same chat
        // body used by CMD 17. The original server accepts
        // it only for a character with an equipped Creature;
        // the client-provided sender id is not authoritative.
        session.Inventory.Creature.TryGetValue(
            CharacterCreatureInventoryLayout
                .EquippedCreatureSlot,
            out var equippedCreature);
        if (messageRequest.MessageType
                is not ((byte)GameMessageType.Type02LightCyan)
                and not ((byte)GameMessageType.Type03White)
            || session.Character.TownId == 0
            || equippedCreature is null
            || equippedCreature.ItemId == 0
            || equippedCreature.CountOrValue == 0)
        {
            RuntimeLogger.Warn(
                "Ignored CREATURE_SCRIPT_MESSAGE type {MessageType} from character {CharacterId}; town={TownId}, equippedCreature={CreatureId}.",
                messageRequest.MessageType,
                session.Character.Active.Id,
                session.Character.TownId,
                equippedCreature?.ItemId ?? 0);
            return;
        }

        var creatureScriptMessage = ChatPacketBuilders.CreateCreatureScriptMessageNotification(
                (GameMessageType)messageRequest.MessageType,
                session.Character.LocalUserId,
                messageRequest.MessageBytes);
        await context.Queue.SendAsync(creatureScriptMessage, Name, cancellationToken);
        RuntimeLogger.Info(
            "Creature script message from {CharacterName}'s Creature {CreatureId}: {Message}",
            session.Character.Active.Name,
            equippedCreature.ItemId,
            ClientEncoding.GetString(messageRequest.MessageBytes));
    }
}
