using System.Text;
using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

/// <summary>
/// SEND_MESSAGE (CMD 17): megaphone broadcasts, whispers and map chat (07 §5 group 5).
/// </summary>
public sealed class SendMessageHandler(IGameStore store,
    CharacterSessionRegistry characterSessions,
    PartyCoordinator partyCoordinator,
    CharacterPacketBuilders builders,
    ChannelSessionSenders senders) : IGameCommandHandler
{
    private static readonly Encoding ClientEncoding = PvfEncodings.Cp936Lossy();

    private const ushort ServerMegaphoneItemId = 36;
    private const ushort ChannelMegaphoneItemId = 37;

    public ushort ProtocolId => (byte)CmdPacketType.SEND_MESSAGE;

    public string Name => "SEND_MESSAGE";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        if (session.Character.Active is null
            || !ChatParsers.TryReadSendMessage(request.Body, out var messageRequest))
        {
            RuntimeLogger.Warn(
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
                RuntimeLogger.Warn(
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
                InventoryPacketBuilders.CreateUpdateItemList(
                    0,
                    [megaphoneEntry]);
            await context.Queue.SendAsync(megaphoneInventoryUpdate, Name, cancellationToken);

            // Routing per client case 130 (0x427518): the
            // packet flag is compared with the local
            // channel number — equal renders the chat
            // line ("频道N name : msg") and runs the
            // bubble path (sub_849280 -> sub_846A10)
            // that resolves the speaker by the packet
            // uid in the town-user table, so same-area
            // residents get the bubble above the
            // speaker's head. The flag carries the
            // speaker's listener-bound channel (M2);
            // cross-channel recipients fail the
            // comparison and render the banner-only
            // copy, which is the client's native
            // other-channel display.
            var megaphoneRecipients =
                characterSessions.NotifyMegaphoneAll(
                    (GameMessageType)messageRequest.MessageType,
                    checked((byte)(session.SessionChannel?.Number
                        ?? senders.SessionChannel.Number)),
                    session.Character.LocalUserId,
                    ClientEncoding.GetBytes(session.Character.Active.Name),
                    messageRequest.MessageBytes);
            RuntimeLogger.Info(
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
                ChatPacketBuilders.CreateMessageNotification(
                    GameMessageType.Type01LightGreen,
                    session.Character.LocalUserId,
                    messageRequest.MessageBytes),
                context.Runtime.Id,
                senders.BuildTownAppearancePacket(context));
            if (delivered)
            {
                RuntimeLogger.Info(
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
                RuntimeLogger.Info(
                    "Whisper type {MessageType} from {CharacterName} to offline partner {PartnerName}: {Message}",
                    messageRequest.MessageType,
                    session.Character.Active.Name,
                    partnerName,
                    ClientEncoding.GetString(messageRequest.MessageBytes));
            }

            return;
        }

        // Party chat is CMD 17 type 2 in town and type 11 inside a
        // dungeon channel: the chat window's channel selector feeds
        // sub_849E80 (selector index 1 = party) and sub_8495B0 writes
        // the resulting type, guarding it with string 282 ("party
        // chat is only available inside a party"). The display side
        // groups both types into one colour and one chat tab
        // (sub_8431E0 / sub_845580), so the type passes through
        // unchanged. Routing follows the party roster instead of the
        // town area so members who moved apart keep hearing each
        // other; the sender needs its own copy because the client
        // renders an outgoing line locally for whispers only, and the
        // uid is the speaker's area-user id, which case 12 resolves
        // against the town-user table for the line name and bubble.
        if (messageRequest.MessageType
            is (byte)GameMessageType.Type02LightCyan
                or (byte)GameMessageType.Type11LightCyan)
        {
            var partyChatRecipients = partyCoordinator.SendToParty(
                context.Runtime.Id,
                ChatPacketBuilders.CreateMessageNotification(
                    (GameMessageType)messageRequest.MessageType,
                    session.Character.LocalUserId,
                    messageRequest.MessageBytes));
            if (partyChatRecipients == 0)
            {
                RuntimeLogger.Warn(
                    "Party chat type {MessageType} from character {CharacterId} reached nobody: the sender holds no party.",
                    messageRequest.MessageType,
                    session.Character.Active.Id);
                return;
            }

            RuntimeLogger.Info(
                "Party chat type {MessageType} from {CharacterName} delivered to {Recipients} party sessions: {Message}",
                messageRequest.MessageType,
                session.Character.Active.Name,
                partyChatRecipients,
                ClientEncoding.GetString(messageRequest.MessageBytes));
            return;
        }

        // DNF 1.0.1.9 uses message type 3 for map chat. The
        // original server routes it to the sender's village
        // and area, and success is represented by NOTI 12;
        // there is no separate successful CMD 17 reply.
        if (messageRequest.MessageType != (byte)GameMessageType.Type03White)
        {
            RuntimeLogger.Warn(
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
            ChatPacketBuilders.CreateMessageNotification(
                GameMessageType.Type03White,
                session.Character.LocalUserId,
                messageRequest.MessageBytes));
        RuntimeLogger.Info(
            "Map chat from {CharacterName} in town {TownId}, area {AreaId} delivered to {Recipients} nearby sessions: {Message}",
            session.Character.Active.Name,
            session.Character.TownId,
            session.Character.AreaId,
            mapChatRecipients,
            ClientEncoding.GetString(messageRequest.MessageBytes));
    }
}
