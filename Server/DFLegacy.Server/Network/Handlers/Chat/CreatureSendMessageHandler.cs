using System.Text;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// CREATURE_SEND_MESSAGE (CMD 124): the equipped Creature's automatic talk (07 §5 group 5).
/// </summary>
public sealed class CreatureSendMessageHandler(
    ILogger<CreatureSendMessageHandler> logger) : IGameCommandHandler
{
    private static readonly Encoding ClientEncoding = PvfEncodings.Cp936Lossy();

    private const ushort BoboCreatureItemId = 63_023;

    public ushort ProtocolId => (byte)CmdPacketType.CREATURE_SEND_MESSAGE;

    public string Name => "CREATURE_SEND_MESSAGE";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        if (session.Character.Active is null
            || !ChatParsers.TryReadSendMessage(request.Body, out var messageRequest))
        {
            logger.LogWarning(
                "Rejected malformed CREATURE_SEND_MESSAGE request for character {CharacterId}: body={Body}.",
                session.Character.Active?.Id,
                Convert.ToHexString(request.Body));
            return;
        }

        session.Inventory.Creature.TryGetValue(
            CharacterCreatureInventoryLayout.EquippedCreatureSlot,
            out var equippedCreature);
        if (messageRequest.MessageType
                is not ((byte)GameMessageType.Type02LightCyan)
                and not ((byte)GameMessageType.Type03White)
            || equippedCreature?.ItemId != BoboCreatureItemId
            || equippedCreature.CountOrValue == 0)
        {
            logger.LogWarning(
                "Ignored CREATURE_SEND_MESSAGE type {MessageType} from character {CharacterId}; equippedCreature={CreatureId}.",
                messageRequest.MessageType,
                session.Character.Active.Id,
                equippedCreature?.ItemId ?? 0);
            return;
        }

        // DNF.exe 0x70DA68 builds CMD 124 as type, a
        // reserved u16, a client-supplied u32 area-user id,
        // and a CP936 string. NOTI 126 omits that u32 and
        // uses the same payload reader as NOTI 131 at
        // 0x41CA99, so rebuild it with the authoritative id.
        var creatureMessage = ChatPacketBuilders.CreateCreatureMessageNotification(
                (GameMessageType)messageRequest.MessageType,
                session.Character.LocalUserId,
                messageRequest.MessageBytes);
        await context.Outbound.SendAsync(creatureMessage, Name, cancellationToken);
        logger.LogInformation(
            "Creature automatic message from {CharacterName}'s Bobo uid={CreatureUid}: {Message}",
            session.Character.Active.Name,
            equippedCreature.CountOrValue,
            ClientEncoding.GetString(messageRequest.MessageBytes));
    }
}
