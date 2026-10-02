using System.Net;
using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

/// <summary>Town presence update pushed by the entrance session when the
/// character enters a town area or moves inside it.</summary>
public sealed record PartyPresenceUpdate(
    Guid SessionId,
    Guid CharacterId,
    uint AccountUid,
    ushort UserId,
    string Name,
    byte Level,
    byte Job,
    byte TownId,
    byte AreaId,
    short X,
    short Y,
    byte Direction,
    int ChannelNumber);

/// <summary>
/// Coordinates party state with cross-session delivery for the 60A1
/// protocol. Flow ported from ServerS4A21: SET_PARTY_INFO creates or edits a
/// party, REQUEST_PEER type 0 invites (or applies), RESPONSE_PEER type 0
/// accepts, LEAVE_PARTY walks out, WALKOUT_PARTY_MEMBER kicks by slot.
///
/// Town presence itself lives in <see cref="TownPresenceService"/> (docs/design/10
/// §4.1 G4, batch 3): this coordinator is a consumer — its presence methods
/// delegate to the hub, and the ACT1 client targeting model below still
/// applies through the hub's table. The client can only target users present
/// in its local town-user table, which is created by USERINFO mode 0
/// (find-or-create at 0x4FDF60) and positioned by USER_AREA / AREA_USERS —
/// both update-only for known records — so every town arrival exchanges
/// appearances with the other residents of the area.
///
/// After every membership change the coordinator also publishes the party's
/// UDP endpoint table (NOTI 11). The peer records advertise the server's
/// gameplay UDP listener — not the members' own addresses — because party
/// real-time datagrams are relayed through the server: direct
/// client-to-client UDP is routinely dropped by client firewalls, and the
/// relay keeps the endpoint a client dialles identical to the endpoint its
/// datagrams actually arrive from.
/// </summary>
public sealed class PartyCoordinator(
    ServerOptions options,
    CharacterSessionRegistry characterSessions,
    TownPresenceService town,
    GameplayDatagramService gameplayDatagram)
{
    private readonly PartyManager _parties = new();
    private readonly TownPresenceService _town = town;

    private const byte WalkoutReasonLeft = 0;
    private const byte WalkoutReasonKicked = 1;

    // ------------------------------------------------------------------
    // Party commands (called from the entrance read loop).
    // ------------------------------------------------------------------

    public PartyOpResult SetPartyInfo(PartyMemberIdentity member, PartySettingsRequest settings)
    {
        var result = _parties.SetPartyInfo(
            member,
            settings.TitleKind,
            settings.TitleBytes,
            settings.UserMax);
        if (!result.Ok)
        {
            return result;
        }

        if (result.RepairedUserMax is { } repairedUserMax)
        {
            // Explains a rename whose capacity byte arrived out of range: the
            // client echoes the capacity stored in its own party record
            // (0x7D6327) when the create window is reopened on an existing
            // party, which the radios can never produce.
            RuntimeLogger.Info(
                "SET_PARTY_INFO from character {CharacterId} carried capacity {RequestedUserMax} outside 2..4; applied {AppliedUserMax}.",
                member.CharacterId,
                settings.UserMax,
                repairedUserMax);
        }

        if (result.PriorPartyLeave is { } prior)
        {
            PublishDeparture(prior, reason: WalkoutReasonLeft, notifyLeaver: true);
        }

        if (result.Created)
        {
            SendFormation(result.Party!);
            BroadcastPartyEndpoints(result.Party!);
            SendUdpHost(result.Party!);
        }
        else
        {
            // Settings-only refresh: the roster block is omitted so member
            // slots and the leader tail byte survive unchanged.
            var update = PartyPacketBuilders.CreatePartyInfo(
                [BuildBlock(result.Party!, PartySettingsBlockType)]);
            foreach (var remaining in result.Party!.Members)
            {
                SendToSession(remaining.SessionId, update);
            }
        }

        return result;
    }

    public PartyOpResult LeaveParty(PartyMemberIdentity member)
    {
        var result = _parties.Leave(member.UserId, member.SessionId);
        if (!result.Ok)
        {
            return result;
        }

        PublishDeparture(result, reason: WalkoutReasonLeft, notifyLeaver: true);
        return result;
    }

    public PartyOpResult Kick(PartyMemberIdentity leader, byte targetSlot)
    {
        var party = _parties.GetPartySnapshotByUser(leader.UserId);
        var target = party?.GetMemberBySlot(targetSlot);
        if (target is null)
        {
            return PartyOpResult.Fail("empty_slot");
        }

        var result = _parties.Kick(targetSlot, leader.UserId, leader.SessionId);
        if (!result.Ok)
        {
            return result;
        }

        SendToSession(target.SessionId, DungeonPacketBuilders.CreatePartyClear(party!.PartyId));
        PublishSurvivorRefresh(result, reason: WalkoutReasonKicked);
        return result;
    }

    /// <summary>CHANGE_HOST（CMD 131）：转让队长并广播 NOTI 26（房主槽位）+ NOTI 9
    /// （花名册）。60A1 对该命令无应答 case（P-35），客户端以 NOTI 变更感知结果。</summary>
    public PartyOpResult TransferLeader(PartyMemberIdentity leader, byte targetSlot)
    {
        var result = _parties.TransferLeader(leader.UserId, leader.SessionId, targetSlot);
        if (!result.Ok)
        {
            return result;
        }

        SendUdpHost(result.Party!);
        SendFormation(result.Party!);
        return result;
    }

    /// <summary>REQUEST_PEER type 0: invite a town user, or apply to join
    /// their party when they already lead one. Other request types (item
    /// trade, PvP room) are not implemented and are rejected silently.</summary>
    public PartyOpResult RequestPeer(PartyMemberIdentity from, PeerRequestFrame request)
    {
        if (request.RequestType != 0)
        {
            return PartyOpResult.Fail("unsupported_request_type");
        }

        var target = FindPresence(request.TargetUserId);
        if (target is null)
        {
            return PartyOpResult.Fail("target_not_online");
        }
        if (FindPresence(from.UserId) is null)
        {
            // Without town presence the target's client cannot resolve the
            // inviter for the NOTI 7 popup and would drop it silently.
            return PartyOpResult.Fail("requester_not_present");
        }

        if (!_parties.RecordInvite(
                request.TargetUserId,
                target.SessionId,
                from.UserId,
                from.SessionId,
                out var failureReason))
        {
            return PartyOpResult.Fail(failureReason);
        }

        SendToSession(
            target.SessionId,
            PartyPacketBuilders.CreatePeerRequest(from.UserId, requestType: 0, request.PeerId));
        return new PartyOpResult
        {
            Ok = true,
            TargetUserId = request.TargetUserId,
        };
    }

    /// <summary>RESPONSE_PEER: type 0 with zero value accepts the pending
    /// invite; anything else cancels it.</summary>
    public PartyOpResult RespondPeer(PartyMemberIdentity responder, PeerResponseFrame response)
    {
        var inviterPresence = FindPresence(response.PeerUserId);
        var inviterSessionId = inviterPresence?.SessionId ?? Guid.Empty;
        if (!PartyPacketBuilders.IsAcceptedPeerResponse(response))
        {
            if (inviterSessionId != Guid.Empty)
            {
                _parties.CancelInvite(
                    responder.UserId,
                    responder.SessionId,
                    response.PeerUserId,
                    inviterSessionId);
            }

            return new PartyOpResult { Ok = true, TargetUserId = response.PeerUserId };
        }

        if (inviterPresence is null)
        {
            return PartyOpResult.Fail("inviter_not_online");
        }

        var inviter = new PartyMemberIdentity(
            inviterPresence.SessionId,
            inviterPresence.CharacterId,
            inviterPresence.AccountUid,
            inviterPresence.UserId,
            inviterPresence.Name,
            inviterPresence.Level,
            inviterPresence.Job);
        var result = _parties.AcceptInvite(responder, inviter, out _);
        if (!result.Ok)
        {
            return result;
        }

        if (result.PriorPartyLeave is { } prior)
        {
            PublishDeparture(prior, reason: WalkoutReasonLeft, notifyLeaver: true);
        }

        // Formation phases follow the ServerS4A21 reference order (proven
        // against official captures): mode-3 detail snapshots first (each
        // member receives every peer's USERINFO mode 3 — the only packet that
        // creates the peer connection object at user+0x110 the dungeon-gate
        // P2P handshake requires; town presence appearances do NOT create it,
        // see packet-detail-party-60cn.md 数据面第 2 条), then endpoints
        // pre-roster, the accept ack to the inviter, the roster LAST, then
        // endpoints again so the bindings land on the final member objects,
        // then the host designation.
        ExchangePartyDetails(result.Party!);
        BroadcastPartyEndpoints(result.Party!);
        SendToSession(
            inviter.SessionId,
            PartyPacketBuilders.CreatePeerResponse(responder.UserId, responseType: 0, (uint)inviter.UserId));
        SendFormation(result.Party!);
        BroadcastPartyEndpoints(result.Party!);
        SendUdpHost(result.Party!);
        return result;
    }

    // ------------------------------------------------------------------
    // Town presence (delegated to TownPresenceService, batch 3).
    // ------------------------------------------------------------------

    /// <summary>Delegates to the hub: upsert plus area entity exchange.</summary>
    public void PublishTownPresence(
        PartyPresenceUpdate update,
        GameServerPacket appearancePacket)
    {
        ArgumentNullException.ThrowIfNull(appearancePacket);
        _town.PublishTownPresence(update, appearancePacket);
    }

    /// <summary>Delegates to the hub: movement sync.</summary>
    public void UpdatePresencePosition(Guid sessionId, short x, short y, byte direction) =>
        _town.UpdatePresencePosition(sessionId, x, y, direction);

    /// <summary>Delegates to the hub: same-area delivery.</summary>
    public int SendToSameArea(Guid sessionId, GameServerPacket packet) =>
        _town.SendToSameArea(sessionId, packet);

    /// <summary>Delegates to the hub: name lookup delivery.</summary>
    public bool TrySendToName(string targetName, GameServerPacket packet) =>
        _town.TrySendToName(targetName, packet);

    /// <summary>Delegates to the hub: name lookup delivery with the whisper
    /// appearance preface (see hub remarks).</summary>
    public bool TrySendToName(
        string targetName,
        GameServerPacket packet,
        Guid? senderSessionId,
        GameServerPacket? senderAppearance) =>
        _town.TrySendToName(targetName, packet, senderSessionId, senderAppearance);

    /// <summary>Area departure via the hub. Called on return-to-character-select.</summary>
    public void RemovePresence(Guid sessionId) => _town.RemovePresence(sessionId);

    /// <summary>Full disconnect cleanup: town presence via the hub plus party
    /// membership (leadership passes to the lowest occupied slot, generation
    /// preserved). The disconnecting connection cannot receive packets anymore.</summary>
    public void OnSessionClosed(Guid sessionId)
    {
        var userId = _town.FindBySession(sessionId)?.UserId;
        _town.RemovePresence(sessionId);
        if (userId is not { } memberUserId)
        {
            return;
        }

        var result = _parties.OnSessionDisconnected(memberUserId, sessionId);
        if (result.Ok)
        {
            PublishSurvivorRefresh(result, reason: WalkoutReasonLeft);
        }
        else
        {
            RuntimeLogger.Debug(
                "Party cleanup for session {SessionId}: {Reason}.",
                sessionId,
                result.Reason);
        }
    }

    private TownPresenceEntry? FindPresenceBySession(Guid sessionId) => _town.FindBySession(sessionId);

    private TownPresenceEntry? FindPresence(ushort userId) => _town.FindByUserId(userId);

    // ------------------------------------------------------------------
    // Party packet publication.
    // ------------------------------------------------------------------

    private const byte PartyFormationBlockType = 0;
    private const byte PartySettingsBlockType = 1;

    /// <summary>Announces a membership change to the survivors. A retired
    /// generation must be cleared first: PARTY_INFO type 0 is applied as an
    /// ordered slot diff, so a stale id would corrupt the roster.</summary>
    private void PublishSurvivorRefresh(PartyOpResult result, byte reason)
    {
        var walkout = PartyPacketBuilders.CreateWalkout(result.TargetUserId, reason);
        if (result.Disbanded || result.Party is not { } party)
        {
            return;
        }

        foreach (var remaining in party.Members)
        {
            SendToSession(remaining.SessionId, walkout);
        }

        if (result.RetiredParty is { } retired)
        {
            var clear = DungeonPacketBuilders.CreatePartyClear(retired.PartyId);
            foreach (var remaining in party.Members)
            {
                SendToSession(remaining.SessionId, clear);
            }
        }

        BroadcastPartyEndpoints(party);
        SendFormation(party);
        BroadcastPartyEndpoints(party);
        SendUdpHost(party);
    }

    /// <summary>
    /// Re-publishes the endpoint table after a member reports a new UDP
    /// endpoint (SET_UDP_IP_PORT). No-op outside a party.
    /// </summary>
    public void RefreshPartyEndpoints(Guid sessionId)
    {
        if (FindPresenceBySession(sessionId) is not { } entry)
        {
            return;
        }

        if (_parties.GetPartySnapshotByUser(entry.UserId) is { } party)
        {
            BroadcastPartyEndpoints(party);
        }
    }

    /// <summary>
    /// Relay targets for the gameplay datagram service: the other members of
    /// the sender's party. Null when the sender is not in a party.
    /// </summary>
    public IReadOnlyList<Guid>? GetPartyPeerSessionIds(Guid sessionId)
    {
        if (FindPresenceBySession(sessionId) is not { } entry
            || _parties.GetPartySnapshotByUser(entry.UserId) is not { } party
            || party.Count < 2)
        {
            return null;
        }

        return party.Members
            .Where(member => member.SessionId != sessionId)
            .Select(member => member.SessionId)
            .ToList();
    }

    /// <summary>
    /// Delivers a packet to every online member of the sender's party,
    /// including the sender, and returns the recipient count (0 when the
    /// sender holds no party). Party chat uses this instead of the town-area
    /// fan-out so members split across areas still reach each other; the
    /// sender's own copy is required because the ACT1 client renders an
    /// outgoing chat line locally only for whispers (sub_8495B0's local echo
    /// sits inside its type 1/7 branch).
    /// </summary>
    public int SendToParty(Guid sessionId, GameServerPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (FindPresenceBySession(sessionId) is not { } entry
            || _parties.GetPartySnapshotByUser(entry.UserId) is not { } party)
        {
            return 0;
        }

        foreach (var member in party.Members)
        {
            SendToSession(member.SessionId, packet);
        }

        return party.Members.Count;
    }

    /// <summary>组队成员互发 mode-3 详情快照：每个成员收到每个队友的 USERINFO
    /// mode 3（对端连接对象的唯一创建源；数据面前置，11 号 P0）。</summary>
    private void ExchangePartyDetails(Party party)
    {
        foreach (var recipient in party.Members)
        {
            foreach (var member in party.Members)
            {
                if (member.SessionId == recipient.SessionId)
                {
                    continue;
                }

                if (characterSessions.TryBuildDetails(member.SessionId, out var details))
                {
                    SendToSession(recipient.SessionId, details);
                }
            }
        }
    }

    /// <summary>
    /// NOTI 11 with one record per member, built per recipient: the
    /// recipient's own record keeps its real reported endpoint while every
    /// peer record advertises the server's gameplay UDP listener (the relay
    /// forwards between members, so a client dialling a peer and a client
    /// receiving from a peer both see the same endpoint). Follows the A21
    /// BuildForRelay split of self-real versus peer-relayed records.
    /// </summary>
    private void BroadcastPartyEndpoints(Party party)
    {
        var relayAddress = IPAddress.Parse(options.GameplayDatagram.Host);
        var relayPort = checked((ushort)options.GameplayDatagram.PrimaryPort);
        foreach (var recipient in party.Members)
        {
            var records = new List<GamePeerInfo>();
            foreach (var member in party.Members)
            {
                byte natType = 2;
                uint mtu = 1472;
                var reported = gameplayDatagram.TryGetPeerInfo(
                    member.SessionId,
                    member.UserId,
                    member.AccountUid,
                    out var reportedPeer);
                if (reported)
                {
                    natType = reportedPeer.NatType;
                    mtu = reportedPeer.Mtu;
                }

                var isSelf = member.SessionId == recipient.SessionId;
                records.Add(new GamePeerInfo(
                    member.UserId,
                    isSelf && reported ? reportedPeer.LocalAddress : relayAddress,
                    isSelf && reported ? reportedPeer.PublicAddress : relayAddress,
                    isSelf && reported ? reportedPeer.PublicPort : relayPort,
                    member.AccountUid,
                    natType,
                    mtu));
            }

            SendToSession(
                recipient.SessionId,
                PartyPacketBuilders.CreateUdpPeerInfo(records));
        }
    }

    /// <summary>
    /// NOTI 26 designates the room host by roster slot (ACT1 case 0x41F144:
    /// u8 slot into the UDP manager singleton at 0xD01D00+0x28). Without it
    /// the client does not start its peer datagram path at all, which kept
    /// every member on the "连接中" connecting status.
    /// </summary>
    private void SendUdpHost(Party party)
    {
        var host = PartyPacketBuilders.CreateUdpHost(party.LeaderSlot);
        foreach (var member in party.Members)
        {
            SendToSession(member.SessionId, host);
        }
    }

    private void PublishDeparture(PartyOpResult result, byte reason, bool notifyLeaver)
    {
        if (notifyLeaver && result.Party is not null)
        {
            var leaverClearId = result.RetiredParty?.PartyId ?? result.Party.PartyId;
            SendToSession(
                result.TargetSessionId,
                DungeonPacketBuilders.CreatePartyClear(leaverClearId));
        }

        PublishSurvivorRefresh(result, reason);
    }

    private void SendFormation(Party party)
    {
        var formation = PartyPacketBuilders.CreatePartyInfo([BuildBlock(party, PartyFormationBlockType)]);
        foreach (var member in party.Members)
        {
            SendToSession(member.SessionId, formation);
        }
    }

    private static PartyInfoWireBlock BuildBlock(Party party, byte blockType) =>
        new(
            party.PartyId,
            blockType,
            party.TitleKind,
            party.UserMax,
            party.MembersBySlot()
                .Select(member => new PartyWireSlot(member.UserId, 0))
                .ToArray(),
            party.LeaderSlot,
            party.TitleBytes);

    // ------------------------------------------------------------------
    // Party delivery helper.
    // ------------------------------------------------------------------

    private bool SendToSession(Guid sessionId, GameServerPacket packet)
    {
        if (characterSessions.NotifyGamePacket(sessionId, packet))
        {
            return true;
        }

        RuntimeLogger.Warn(
            "Dropped a {ProtocolId} packet for session {SessionId}: the connection is gone.",
            packet.ProtocolId,
            sessionId);
        return false;
    }
}
