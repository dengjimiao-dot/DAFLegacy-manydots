using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

/// <summary>
/// Party command family (CMD 10-14, 07 §4.6 族接管形态): one shared core
/// carrying the migrated dispatch, five thin shells owning the protocol ids
/// so the registry keeps one handler per id and duplicate detection intact.
/// The body of every party command starts with a two-byte sequence number,
/// so the parsers read fields from offset 2.
/// </summary>
public sealed class PartyHandlerCore(
    PartyCoordinator partyCoordinator)
{
    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        var partyIdentity = session.Character.Active is null
            ? null
            : new PartyMemberIdentity(
                context.Runtime.Id,
                session.Character.Active.Id,
                session.Login.AccountUid,
                session.Character.LocalUserId,
                session.Character.Active.Name,
                (byte)Math.Clamp(session.Character.Active.Level, (byte)1, byte.MaxValue),
                (byte)Math.Clamp(session.Character.Active.Job, byte.MinValue, byte.MaxValue));
        if (partyIdentity is null)
        {
            // Padded so the client's error-code readers
            // stay inside the payload (see
            // CreatePeerCommandError).
            var identityError = PartyPacketBuilders.CreatePeerCommandError(
                request,
                errorCode: 0);
            await context.Queue.SendAsync(identityError, "PARTY", cancellationToken);
            return;
        }

        // A malformed party body must never kill the
        // connection — parse and dispatch defensively.
        PartyOpResult partyResult;
        byte partyErrorCode;
        try
        {
            var parsedSettings = PartyPacketBuilders.TryParseSetPartyInfo(
                request.Body,
                out var partySettings);
            var parsedPeerRequest = PartyPacketBuilders.TryParseRequestPeer(
                request.Body,
                out var peerRequest);
            var parsedPeerResponse = PartyPacketBuilders.TryParseResponsePeer(
                request.Body,
                out var peerResponse);
            var parsedWalkoutSlot = PartyPacketBuilders.TryParseWalkoutPartyMember(
                request.Body,
                out var walkoutSlot);

            (partyResult, partyErrorCode) = request.ProtocolId switch
            {
                (byte)CmdPacketType.SET_PARTY_INFO => parsedSettings
                    ? (partyCoordinator.SetPartyInfo(partyIdentity, partySettings), (byte)8)
                    : (PartyOpResult.Fail("malformed_set_party_info"), (byte)0),
                (byte)CmdPacketType.LEAVE_PARTY =>
                    (partyCoordinator.LeaveParty(partyIdentity), (byte)0x12),
                (byte)CmdPacketType.WALKOUT_PARTY_MEMBER => parsedWalkoutSlot
                    ? (partyCoordinator.Kick(partyIdentity, walkoutSlot), (byte)4)
                    : (PartyOpResult.Fail("malformed_walkout"), (byte)0),
                (byte)CmdPacketType.REQUEST_PEER => parsedPeerRequest
                    ? (partyCoordinator.RequestPeer(partyIdentity, peerRequest), (byte)5)
                    : (PartyOpResult.Fail("malformed_request_peer"), (byte)0),
                (byte)CmdPacketType.RESPONSE_PEER => parsedPeerResponse
                    ? (partyCoordinator.RespondPeer(partyIdentity, peerResponse), (byte)1)
                    : (PartyOpResult.Fail("malformed_response_peer"), (byte)0),
                (byte)CmdPacketType.CHANGE_HOST => request.Body.Length >= 3
                    ? (partyCoordinator.TransferLeader(partyIdentity, request.Body[2]), (byte)0)
                    : (PartyOpResult.Fail("malformed_change_host"), (byte)0),
                _ => (PartyOpResult.Fail("unknown_party_command"), (byte)0),
            };
        }
        catch (Exception exception)
        {
            RuntimeLogger.Warn(
                exception,
                "Party command {ProtocolId} from {CharacterId} threw; replying failure.",
                request.ProtocolId,
                partyIdentity.CharacterId);
            partyResult = PartyOpResult.Fail("party_command_exception");
            partyErrorCode = 0;
        }

        // CHANGE_HOST 无应答 case（60A1 fire-and-forget，P-35）：成功经 NOTI 26/9
        // 广播感知，失败仅记日志；不回 Type-1 帧。
        if (request.ProtocolId == (byte)CmdPacketType.CHANGE_HOST)
        {
            return;
        }

        var partyIsSettingsFamily =
            request.ProtocolId
                is (byte)CmdPacketType.SET_PARTY_INFO
                or (byte)CmdPacketType.LEAVE_PARTY
                or (byte)CmdPacketType.WALKOUT_PARTY_MEMBER;
        GameServerPacket partyReply;
        if (partyResult.Ok)
        {
            partyReply = ConnectionPacketBuilders.CreateCommandReply(
                request,
                success: true);
        }
        else
        {
            partyReply = partyIsSettingsFamily
                ? PartyPacketBuilders.CreatePartyCommandError(
                    request,
                    partyErrorCode)
                : PartyPacketBuilders.CreatePeerCommandError(
                    request,
                    partyErrorCode);
        }

        await context.Queue.SendAsync(partyReply, "PARTY", cancellationToken);
        if (!partyResult.Ok)
        {
            RuntimeLogger.Info(
                "Rejected party command {ProtocolId} for {CharacterId}: {Reason}.",
                request.ProtocolId,
                partyIdentity.CharacterId,
                partyResult.Reason);
        }

    }
}
