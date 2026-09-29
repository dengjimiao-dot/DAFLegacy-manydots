using System.Text;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// SEND_MESSAGE (CMD 17): megaphone broadcasts, whispers and map chat (07 §5 group 5).
/// </summary>
public sealed class SendMessageHandler(JsonGameStore store,
    CharacterSessionRegistry characterSessions,
    PartyCoordinator partyCoordinator,
    ServerOptions options,
    ILogger<SendMessageHandler> logger,
    CharacterPacketBuilders builders,
    ChannelSessionSenders senders) : IGameCommandHandler
{
    private static readonly Encoding ClientEncoding = PvfEncodings.Cp936Lossy();

    private const ushort ServerMegaphoneItemId = 36;
    private const ushort ChannelMegaphoneItemId = 37;

    public ushort ProtocolId => GameProtocolEngine.SendMessageCommand;

    public string Name => "SEND_MESSAGE";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        if (session.Character.Active is null
            || !ChatParsers.TryReadSendMessage(request.Body, out var messageRequest))
        {
            logger.LogWarning(
                "Rejected malformed SEND_MESSAGE request for character {CharacterId}: body={Body}.",
                session.Character.Active?.Id,
                Convert.ToHexString(request.Body));
            return;
        }

        // Speaker megaphones (PVF items 36/37) are used
        // through CMD 17 rather than USE_STACKABLE: the
        // request names the megaphone slot so the server
        // consumes the stack and rebroadcasts the text as
        // NOTI 130. Wire layout:
        // docs/protocol/new-packet-formats-60cn.md §1.
        if (messageRequest.MessageType
            is (byte)GameMessageType.Type13SpeakerYellow
            or (byte)GameMessageType.Type14SpeakerYellow
            or (byte)GameMessageType.Type15SpeakerYellow)
        {
            // The slot content is authoritative: items 36
            // (server megaphone) and 37 (channel megaphone)
            // are both accepted whichever speaker type the
            // client picked, so the type-to-item mapping
            // cannot drift from the PVF.
            if (messageRequest.SlotOrReserved > ushort.MaxValue
                || !session.Inventory.Main.TryGetValue(
                    (ushort)messageRequest.SlotOrReserved,
                    out var megaphoneItem)
                || megaphoneItem.ItemId
                    is not (ServerMegaphoneItemId
                        or ChannelMegaphoneItemId)
                || megaphoneItem.CountOrValue == 0)
            {
                await senders.SendPopupNotificationSafeAsync(context, 
                    ClientEncoding.GetBytes("喇叭使用失败，请确认道具状态"));
                logger.LogWarning(
                    "Rejected megaphone message type {MessageType} from character {CharacterId}; target={TargetOrItemSpace}, slot/reserved={SlotOrReserved}.",
                    messageRequest.MessageType,
                    session.Character.Active.Id,
                    messageRequest.TargetOrItemSpace,
                    messageRequest.SlotOrReserved);
                return;
            }

            var megaphoneSlot = (ushort)messageRequest.SlotOrReserved;
            var remainingCount = megaphoneItem.CountOrValue - 1;
            if (remainingCount == 0)
            {
                session.Inventory.Main.Remove(megaphoneSlot);
            }
            else
            {
                session.Inventory.Main[megaphoneSlot] = megaphoneItem with
                {
                    CountOrValue = remainingCount
                };
            }

            session.Character.Active = await store.SaveCharacterInventoryAsync(
                session.Login.AccountName,
                session.Character.Active.Id,
                session.Currency.Gold,
                session.Inventory.Main.Values,
                session.Inventory.Equipped.Values,
                cancellationToken) ?? session.Character.Active;

            var megaphoneEntry = remainingCount == 0
                ? new GameInventoryEntry(megaphoneSlot, ushort.MaxValue, 0)
                : builders.ToGameInventoryEntry(session.Inventory.Main[megaphoneSlot]);
            var megaphoneInventoryUpdate =
                GameProtocolEngine.CreateUpdateItemList(
                    0,
                    [megaphoneEntry]);
            await context.Outbound.SendAsync(megaphoneInventoryUpdate, Name, cancellationToken);

            // Routing per client case 130 (0x427518): the
            // packet flag is compared with the local
            // channel number — equal renders the chat
            // line ("频道N name : msg") and runs the
            // bubble path (sub_849280 -> sub_846A10)
            // that resolves the speaker by the packet
            // uid in the town-user table, so same-area
            // residents get the bubble above the
            // speaker's head. Every session of this
            // process shares the configured channel, so
            // one speaker-flagged broadcast per session
            // is a single, correctly labeled display
            // with the bubble; the banner-only route is
            // for other-channel clients, which never
            // occur in this process.
            var megaphoneRecipients =
                characterSessions.NotifyMegaphoneAll(
                    (GameMessageType)messageRequest.MessageType,
                    checked((byte)options.Channel.ChannelNumber),
                    session.Character.LocalUserId,
                    ClientEncoding.GetBytes(session.Character.Active.Name),
                    messageRequest.MessageBytes);
            logger.LogInformation(
                "Megaphone type {MessageType} from {CharacterName} (slot {Slot}, stack remaining {Remaining}) broadcast to {Recipients} sessions: {Message}",
                messageRequest.MessageType,
                session.Character.Active.Name,
                megaphoneSlot,
                remainingCount,
                megaphoneRecipients,
                ClientEncoding.GetString(messageRequest.MessageBytes));
            return;
        }

        // Direct messages: 1:1 chat is CMD 17 type 18
        // (DNF.exe sub_811C20), and the chat-window
        // whispers are types 1/7 (shared sender
        // sub_8495B0). All three carry the partner name
        // as a trailing dstr; the type 1/7 u16 target is
        // the partner's area-user id.
        //
        // Downlink must NOT echo the request type: the
        // client routes NOTI 12 type 18 into its open
        // 1:1 window via vmt+76 (sub_810A90), which
        // feeds the packet-less name straight into
        // strlen (sub_810560) and crashes the client —
        // NOTI 12 carries no name field, so type 18 is
        // an unusable downlink. The original server
        // therefore delivers whispers as NOTI 12 type 1
        // (light-green private line, speaker resolved
        // from the town-user table by the uid). The
        // sender needs no echo: both send paths insert
        // the outgoing text locally (sub_811C20 /
        // sub_8495B0 call sub_811A00).
        if (messageRequest.MessageType
            is (byte)GameMessageType.Type01LightGreen
                or (byte)GameMessageType.Type07LightGreen
                or (byte)GameMessageType.Type18DirectOutput)
        {
            var partnerName = ClientEncoding.GetString(
                messageRequest.TargetNameBytes);
            var delivered = partyCoordinator.TrySendToName(
                partnerName,
                GameProtocolEngine.CreateMessageNotification(
                    GameMessageType.Type01LightGreen,
                    session.Character.LocalUserId,
                    messageRequest.MessageBytes));
            if (delivered)
            {
                logger.LogInformation(
                    "Whisper type {MessageType} from {CharacterName} delivered to {PartnerName} as NOTI 12 type 1: {Message}",
                    messageRequest.MessageType,
                    session.Character.Active.Name,
                    partnerName,
                    ClientEncoding.GetString(messageRequest.MessageBytes));
            }
            else
            {
                await senders.SendTypedMessageNotificationSafeAsync(context, 
                    GameMessageType.Type00DualRouteBrown,
                    0,
                    ClientEncoding.GetBytes(
                        $"{partnerName} 不在线，无法发送悄悄话"));
                logger.LogInformation(
                    "Whisper type {MessageType} from {CharacterName} to offline partner {PartnerName}: {Message}",
                    messageRequest.MessageType,
                    session.Character.Active.Name,
                    partnerName,
                    ClientEncoding.GetString(messageRequest.MessageBytes));
            }

            return;
        }

        // DNF 1.0.1.9 uses message type 3 for map chat. The
        // original server routes it to the sender's village
        // and area, and success is represented by NOTI 12;
        // there is no separate successful CMD 17 reply.
        if (messageRequest.MessageType != (byte)GameMessageType.Type03White)
        {
            logger.LogWarning(
                "Unsupported SEND_MESSAGE type {MessageType} from character {CharacterId}; target/item-space={TargetOrItemSpace}, slot/reserved={SlotOrReserved}.",
                messageRequest.MessageType,
                session.Character.Active.Id,
                messageRequest.TargetOrItemSpace,
                messageRequest.SlotOrReserved);
            return;
        }

        await senders.SendTypedMessageNotificationSafeAsync(context, 
            GameMessageType.Type03White,
            session.Character.LocalUserId,
            messageRequest.MessageBytes);
        // NOTI 12's u16 is the speaker's area-user id, not
        // the recipient's: case 12 resolves it through the
        // town-user table (sub_849280 -> sub_846A10
        // @0x846A27) and renders the speech bubble above
        // that character, so every same-area resident needs
        // their own copy carrying the speaker's uid. The
        // sender's copy above already covers the own
        // bubble; same-town-different-area sessions stay
        // out of range, matching the original routing.
        var mapChatRecipients = partyCoordinator.SendToSameArea(
            context.Runtime.Id,
            GameProtocolEngine.CreateMessageNotification(
                GameMessageType.Type03White,
                session.Character.LocalUserId,
                messageRequest.MessageBytes));
        logger.LogInformation(
            "Map chat from {CharacterName} in town {TownId}, area {AreaId} delivered to {Recipients} nearby sessions: {Message}",
            session.Character.Active.Name,
            session.Character.TownId,
            session.Character.AreaId,
            mapChatRecipients,
            ClientEncoding.GetString(messageRequest.MessageBytes));
    }
}
