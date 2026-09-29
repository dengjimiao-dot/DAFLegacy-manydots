using System.Net;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

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
    byte Direction);

/// <summary>
/// Coordinates party state with cross-session delivery for the 60A1
/// protocol. Flow ported from ServerS4A21: SET_PARTY_INFO creates or edits a
/// party, REQUEST_PEER type 0 invites (or applies), RESPONSE_PEER type 0
/// accepts, LEAVE_PARTY walks out, WALKOUT_PARTY_MEMBER kicks by slot.
///
/// The coordinator also keeps the town presence table. The ACT1 client can
/// only target users present in its local town-user table, which is created
/// by USERINFO mode 0 (find-or-create at 0x4FDF60) and positioned by
/// USER_AREA / AREA_USERS — both update-only for known records — so every
/// town arrival exchanges appearances with the other residents of the area.
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
    GameplayDatagramService gameplayDatagram,
    ILogger<PartyCoordinator> logger)
{
    private readonly PartyManager _parties = new();
    private readonly object _presenceLock = new();
    private readonly Dictionary<Guid, PresenceEntry> _presenceBySession = new();

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
            var update = GameProtocolEngine.CreatePartyInfo(
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

        SendToSession(target.SessionId, GameProtocolEngine.CreatePartyClear(party!.PartyId));
        PublishSurvivorRefresh(result, reason: WalkoutReasonKicked);
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
            GameProtocolEngine.CreatePeerRequest(from.UserId, requestType: 0, request.PeerId));
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
        if (!GameProtocolEngine.IsAcceptedPeerResponse(response))
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
        // against official captures): endpoints pre-roster, the accept ack to
        // the inviter, the roster LAST, then endpoints again so the bindings
        // land on the final member objects, then the host designation. No
        // USERINFO detail snapshots are exchanged here: the mode-3 tail fires
        // the client's inspect-window UI slots, and town presence already
        // delivered every member's appearance record to both clients.
        BroadcastPartyEndpoints(result.Party!);
        SendToSession(
            inviter.SessionId,
            GameProtocolEngine.CreatePeerResponse(responder.UserId, responseType: 0, (uint)inviter.UserId));
        SendFormation(result.Party!);
        BroadcastPartyEndpoints(result.Party!);
        SendUdpHost(result.Party!);
        return result;
    }

    // ------------------------------------------------------------------
    // Town presence.
    // ------------------------------------------------------------------

    /// <summary>
    /// Upserts the sender's presence and exchanges town entities with the
    /// other residents of the area: they learn the arrival through its
    /// appearance packet plus USER_AREA, and the arrival receives the cached
    /// appearance packets of everyone already present. Each session builds
    /// its own appearance packet because only the owning connection has the
    /// character's equipment state; the coordinator replays the cached bytes.
    /// </summary>
    public void PublishTownPresence(
        PartyPresenceUpdate update,
        GameServerPacket appearancePacket)
    {
        ArgumentNullException.ThrowIfNull(appearancePacket);
        PresenceEntry[] sameArea;
        PresenceEntry[] leftArea;
        var entry = new PresenceEntry(
            update.SessionId,
            update.CharacterId,
            update.AccountUid,
            update.UserId,
            update.Name,
            update.Level,
            update.Job,
            update.TownId,
            update.AreaId,
            update.X,
            update.Y,
            update.Direction,
            appearancePacket);
        lock (_presenceLock)
        {
            leftArea = [];
            if (_presenceBySession.TryGetValue(update.SessionId, out var previous))
            {
                if (previous.TownId != update.TownId || previous.AreaId != update.AreaId)
                {
                    leftArea = SameAreaSnapshotLocked(previous.TownId, previous.AreaId, update.SessionId);
                }

                _presenceBySession.Remove(update.SessionId);
            }
            _presenceBySession[update.SessionId] = entry;
            sameArea = SameAreaSnapshotLocked(update.TownId, update.AreaId, update.SessionId);
        }

        var userArea = GameProtocolEngine.CreateUserArea(
            update.TownId,
            update.AreaId,
            new GameTownUser(update.UserId, update.X, update.Y, update.Direction));
        foreach (var other in leftArea)
        {
            SendToSession(other.SessionId, GameProtocolEngine.CreateUserLeave(update.UserId));
        }
        foreach (var other in sameArea)
        {
            // The resident learns the arrival...
            SendToSession(other.SessionId, appearancePacket);
            SendToSession(other.SessionId, userArea);
            // ...and the arrival learns the resident.
            SendToSession(update.SessionId, other.Appearance);
            SendToSession(update.SessionId, GameProtocolEngine.CreateUserArea(
                other.TownId,
                other.AreaId,
                new GameTownUser(other.UserId, other.X, other.Y, other.Direction)));
        }
    }

    /// <summary>Movement sync: updates the stored position and pushes
    /// USER_POSITION to the other residents of the area.</summary>
    public void UpdatePresencePosition(Guid sessionId, short x, short y, byte direction)
    {
        PresenceEntry? entry;
        PresenceEntry[] sameArea;
        lock (_presenceLock)
        {
            if (!_presenceBySession.TryGetValue(sessionId, out entry))
            {
                return;
            }

            entry = entry with { X = x, Y = y, Direction = direction };
            _presenceBySession[sessionId] = entry;
            sameArea = SameAreaSnapshotLocked(entry.TownId, entry.AreaId, sessionId);
        }

        var position = GameProtocolEngine.CreateUserPosition(
            new GameTownUser(entry.UserId, x, y, direction));
        foreach (var other in sameArea)
        {
            SendToSession(other.SessionId, position);
        }
    }

    /// <summary>
    /// Delivers a packet to the other residents of the sender's town area
    /// (town presence table). Returns the number of recipients reached.
    /// </summary>
    public int SendToSameArea(Guid sessionId, GameServerPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        PresenceEntry[] sameArea;
        lock (_presenceLock)
        {
            if (!_presenceBySession.TryGetValue(sessionId, out var entry))
            {
                return 0;
            }

            sameArea = SameAreaSnapshotLocked(entry.TownId, entry.AreaId, sessionId);
        }

        foreach (var other in sameArea)
        {
            SendToSession(other.SessionId, packet);
        }

        return sameArea.Length;
    }

    /// <summary>
    /// Delivers a packet to the online character with the given name,
    /// resolved through the town presence table (the same table the ACT1
    /// client builds its chat target list from). False when nobody of that
    /// name currently has a town presence.
    /// </summary>
    public bool TrySendToName(string targetName, GameServerPacket packet)
    {
        ArgumentNullException.ThrowIfNull(targetName);
        ArgumentNullException.ThrowIfNull(packet);
        Guid sessionId;
        lock (_presenceLock)
        {
            PresenceEntry? match = null;
            foreach (var entry in _presenceBySession.Values)
            {
                if (string.Equals(entry.Name, targetName, StringComparison.Ordinal))
                {
                    match = entry;
                    break;
                }
            }

            if (match is null)
            {
                return false;
            }

            sessionId = match.SessionId;
        }

        return SendToSession(sessionId, packet);
    }

    /// <summary>Area departure: drops the presence and tells the area the
    /// user left. Called on return-to-character-select and disconnect.</summary>
    public void RemovePresence(Guid sessionId)
    {
        PresenceEntry[] sameArea;
        ushort userId;
        lock (_presenceLock)
        {
            if (!_presenceBySession.TryGetValue(sessionId, out var entry))
            {
                return;
            }

            userId = entry.UserId;
            _presenceBySession.Remove(sessionId);
            sameArea = SameAreaSnapshotLocked(entry.TownId, entry.AreaId, sessionId);
        }

        var leave = GameProtocolEngine.CreateUserLeave(userId);
        foreach (var other in sameArea)
        {
            SendToSession(other.SessionId, leave);
        }
    }

    /// <summary>Full disconnect cleanup: town presence plus party membership
    /// (leadership passes to the lowest occupied slot, generation preserved).
    /// The disconnecting connection cannot receive packets anymore.</summary>
    public void OnSessionClosed(Guid sessionId)
    {
        var userId = FindPresenceBySession(sessionId)?.UserId;
        RemovePresence(sessionId);
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
            logger.LogDebug(
                "Party cleanup for session {SessionId}: {Reason}.",
                sessionId,
                result.Reason);
        }
    }

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
        var walkout = GameProtocolEngine.CreateWalkout(result.TargetUserId, reason);
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
            var clear = GameProtocolEngine.CreatePartyClear(retired.PartyId);
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
                GameProtocolEngine.CreateUdpPeerInfo(records));
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
        var host = GameProtocolEngine.CreateUdpHost(party.LeaderSlot);
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
                GameProtocolEngine.CreatePartyClear(leaverClearId));
        }

        PublishSurvivorRefresh(result, reason);
    }

    private void SendFormation(Party party)
    {
        var formation = GameProtocolEngine.CreatePartyInfo([BuildBlock(party, PartyFormationBlockType)]);
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
            party.LeaderSlot);

    // ------------------------------------------------------------------
    // Presence storage and delivery helpers.
    // ------------------------------------------------------------------

    private sealed record PresenceEntry(
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
        GameServerPacket Appearance);

    private PresenceEntry[] SameAreaSnapshotLocked(byte townId, byte areaId, Guid excludeSessionId)
    {
        var matches = new List<PresenceEntry>();
        foreach (var entry in _presenceBySession.Values)
        {
            if (entry.SessionId != excludeSessionId &&
                entry.TownId == townId &&
                entry.AreaId == areaId)
            {
                matches.Add(entry);
            }
        }

        return matches.ToArray();
    }

    private PresenceEntry? FindPresenceBySession(Guid sessionId)
    {
        lock (_presenceLock)
        {
            return _presenceBySession.TryGetValue(sessionId, out var entry) ? entry : null;
        }
    }

    private PresenceEntry? FindPresence(ushort userId)
    {
        lock (_presenceLock)
        {
            foreach (var entry in _presenceBySession.Values)
            {
                if (entry.UserId == userId)
                {
                    return entry;
                }
            }
        }

        return null;
    }

    private bool SendToSession(Guid sessionId, GameServerPacket packet)
    {
        if (characterSessions.NotifyGamePacket(sessionId, packet))
        {
            return true;
        }

        logger.LogWarning(
            "Dropped a {ProtocolId} packet for session {SessionId}: the connection is gone.",
            packet.ProtocolId,
            sessionId);
        return false;
    }
}
