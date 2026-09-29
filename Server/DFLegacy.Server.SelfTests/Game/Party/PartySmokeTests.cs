using DFLegacy.Protocol;
using DFLegacy.Server;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Byte-level expectations follow the 60A1 client disassembly:
/// SET_PARTY_INFO writer 0x7D5F86, REQUEST_PEER writer 0x4FE9B0,
/// RESPONSE_PEER accept writer 0x4FEBC0, WALKOUT writer 0x8520F3, and the
/// NOTI 9/7/8/10 readers at 0x41B7A1 / 0x41B4C8 / 0x41B5F1 / 0x41C41A.
/// </summary>
internal static class PartySmokeTests
{
    public static void Run(Action<bool, string> check)
    {
        ParseSetPartyInfo(check);
        ParsePeerFrames(check);
        BuildPartyInfo(check);
        BuildCurrentCharacterDetails(check);
        BuildPeerNotifications(check);
        PartyManagerLifecycle(check);
        RunCoordinatorFlow(check);
    }

    private static void BuildCurrentCharacterDetails(Action<bool, string> check)
    {
        // Empty character: mode + u16 count + u16 uid + u32 exp + u32 stat
        // length + 81 stats + u8 equipment count + u8 skill count + u8
        // creature level.
        var plain = GameProtocolEngine.CreateCurrentCharacterDetails(1, mode: 1);
        check(plain.ProtocolId == 2 && plain.Payload.Length == 97,
            "USERINFO mode 1 carries the bare 97-byte detail body");

        // Party fan-out uses mode 3: same body plus the 28-byte zero tail
        // (u32×3 + u8×4 guild context, u32×3 title ids) the reader at
        // 0x41AEE0 consumes to drive the member connection UI. A tail-less
        // mode-3 body made the client read past the packet and time out.
        var fanout = GameProtocolEngine.CreateCurrentCharacterDetails(1, mode: 3);
        byte[] expectedTail = new byte[28];
        check(fanout.Payload.Length == 125
            && fanout.Payload.AsSpan(97).SequenceEqual(expectedTail)
            && fanout.Payload[0] == 3,
            "USERINFO mode 3 appends the 28-byte zero connection tail");
    }

    private static void ParseSetPartyInfo(Action<bool, string> check)
    {
        // Preset title (kind 1) with the capacity radio on "4".
        check(GameProtocolEngine.TryParseSetPartyInfo(
                Convert.FromHexString("03000104"),
                out var preset)
            && preset.TitleKind == 1
            && preset.TitleBytes.Length == 0
            && preset.UserMax == 4,
            "SET_PARTY_INFO preset shape parses kind and user max");

        // Custom title (kind 0) carries a u32-prefixed name: "队伍" in CP936.
        var title = PvfEncodings.Cp936Lossy().GetBytes("队伍");
        var custom = new List<byte> { 0x03, 0x00, 0x00 };
        custom.AddRange(BitConverter.GetBytes((uint)title.Length));
        custom.AddRange(title);
        custom.Add(3);
        check(GameProtocolEngine.TryParseSetPartyInfo(custom.ToArray(), out var customRequest)
            && customRequest.TitleKind == 0
            && customRequest.TitleBytes.AsSpan().SequenceEqual(title)
            && customRequest.UserMax == 3,
            "SET_PARTY_INFO custom-title shape parses the dstr and capacity");

        // The client reader caps the title dstr at 0x40 bytes (0x41B94C).
        var oversized = new List<byte> { 0x03, 0x00, 0x00 };
        oversized.AddRange(BitConverter.GetBytes(0x40u));
        oversized.AddRange(new byte[0x40]);
        oversized.Add(2);
        check(!GameProtocolEngine.TryParseSetPartyInfo(oversized.ToArray(), out _)
            && !GameProtocolEngine.TryParseSetPartyInfo([0x03, 0x00], out _),
            "SET_PARTY_INFO rejects over-cap titles and truncated bodies");

        // Regression: the party block parses every 10-14 body with every
        // parser. A WALKOUT-shaped 3-byte body (seq + slot=1) must be
        // rejected, never throw — an IndexOutOfRange here killed the whole
        // connection on live servers (2026-09-28 log).
        check(!GameProtocolEngine.TryParseSetPartyInfo([0x15, 0x00, 0x01], out _),
            "SET_PARTY_INFO parser tolerates WALKOUT-shaped bodies");
    }

    private static void ParsePeerFrames(Action<bool, string> check)
    {
        // REQUEST_PEER (invite): u16 target 5, type 0, u32 peer id 0x0A.
        check(GameProtocolEngine.TryParseRequestPeer(
                Convert.FromHexString("03000500000A000000"),
                out var invite)
            && invite.TargetUserId == 5
            && invite.RequestType == 0
            && invite.PeerId == 0x0A,
            "REQUEST_PEER parses target uid, type and peer id");

        // RESPONSE_PEER accept: exactly the seven payload bytes the client
        // accept path writes (0x4FEBC0).
        check(GameProtocolEngine.TryParseResponsePeer(
                Convert.FromHexString("030005000000000000"),
                out var accept)
            && GameProtocolEngine.IsAcceptedPeerResponse(accept)
            && accept.PeerUserId == 5,
            "RESPONSE_PEER accept frame matches the client's 7-byte shape");

        check(GameProtocolEngine.TryParseResponsePeer(
                Convert.FromHexString("030005000100020000"),
                out var refuse)
            && !GameProtocolEngine.IsAcceptedPeerResponse(refuse),
            "RESPONSE_PEER with a non-zero type is a refusal");

        check(GameProtocolEngine.TryParseWalkoutPartyMember(
                [0x01, 0x00, 0x02], out var slot)
            && slot == 2
            && !GameProtocolEngine.TryParseWalkoutPartyMember([0x01, 0x00, 0x04], out _),
            "WALKOUT parses the roster slot and rejects out-of-range slots");
    }

    private static void BuildPartyInfo(Action<bool, string> check)
    {
        var party = GameProtocolEngine.CreatePartyInfo(
        [
            new PartyInfoWireBlock(
                PartyId: 5,
                BlockType: 0,
                TitleKind: 1,
                UserMax: 4,
                Members:
                [
                    new PartyWireSlot(7, 0),
                    new PartyWireSlot(9, 0),
                ],
                LeaderSlot: 0),
        ]);
        byte[] expectedFormation =
        [
            0x01, 0x00, // block count
            0x05, 0x00, // party id
            0x00, // block type: full formation
            0x01, // title kind
            0x04, // user max
            0x07, 0x00, 0x00, // slot 0
            0x09, 0x00, 0x00, // slot 1
            0xFF, 0xFF, 0x00, // slot 2 (empty)
            0xFF, 0xFF, 0x00, // slot 3 (empty)
            0x00, // leader slot
        ];
        check(party.Type == 0 && party.ProtocolId == 9,
            "PARTY_INFO frames NOTI 9");
        check(party.Payload.AsSpan().SequenceEqual(expectedFormation),
            "PARTY_INFO formation block matches the 60CN read order");

        // Kind-0 titles render from the client's local string 0x272; the
        // settings pair never carries title bytes on the wire.
        var settingsOnly = GameProtocolEngine.CreatePartyInfo(
        [
            new PartyInfoWireBlock(5, 1, 0, 3, [], 0),
        ]);
        byte[] expectedSettings = [0x01, 0x00, 0x05, 0x00, 0x01, 0x00, 0x03];
        check(settingsOnly.Payload.AsSpan().SequenceEqual(expectedSettings),
            "PARTY_INFO type 1 carries only the settings pair");

        var clear = GameProtocolEngine.CreatePartyClear(5);
        byte[] expectedClear = [0x01, 0x00, 0x05, 0x00, 0x03];
        check(clear.Payload.AsSpan().SequenceEqual(expectedClear),
            "PARTY_INFO type 3 is the five-byte clear block");
    }

    private static void BuildPeerNotifications(Action<bool, string> check)
    {
        var invite = GameProtocolEngine.CreatePeerRequest(0x1042, 0, 0x0A);
        byte[] expectedInvite =
        [
            0x42, 0x10, // inviter uid
            0x00, // request type
            0x0A, 0x00, 0x00, 0x00, // peer id
            0x00, 0x00, // state words (type 0 only)
            0x00, 0x00,
        ];
        check(invite.ProtocolId == 7 && invite.Payload.Length == 11,
            "NOTI 7 invite popup carries 11 bytes for type 0");
        check(invite.Payload.AsSpan().SequenceEqual(expectedInvite),
            "NOTI 7 invite payload matches the client read order");

        var ack = GameProtocolEngine.CreatePeerResponse(7, 0, 5);
        byte[] expectedAck = [0x07, 0x00, 0x00, 0x05, 0x00, 0x00, 0x00];
        check(ack.ProtocolId == 8 && ack.Payload.AsSpan().SequenceEqual(expectedAck),
            "NOTI 8 accept ack matches the 7-byte client shape");

        var walkout = GameProtocolEngine.CreateWalkout(9, 1);
        check(walkout.ProtocolId == 10
            && walkout.Payload.AsSpan().SequenceEqual(new byte[] { 0x09, 0x01 }),
            "NOTI 10 walkout is u8 uid plus u8 reason");

        var leave = GameProtocolEngine.CreateUserLeave(0x0102);
        check(leave.ProtocolId == 6
            && leave.Payload.AsSpan().SequenceEqual(new byte[] { 0x02, 0x01 }),
            "NOTI 6 user leave is the u16 uid");

        // Failure replies put the error byte where each reply-dispatcher case
        // expects it: payload[1] for the settings family (12/13/14) and
        // payload[2] for the peer pair (10/11), with one trailing byte.
        var settingsError = GameProtocolEngine.CreatePartyCommandError(
            new PacketFrame(1, 13, 0, [0x01, 0x00]),
            0x12);
        check(settingsError.Payload.AsSpan().SequenceEqual(new byte[] { 0x00, 0x12, 0x00, 0x00 }),
            "LEAVE_PARTY failure carries code 0x12 at payload[1]");
        var peerError = GameProtocolEngine.CreatePeerCommandError(
            new PacketFrame(1, 10, 0, [0x01, 0x00]),
            5);
        check(peerError.Payload.AsSpan().SequenceEqual(new byte[] { 0x00, 0x00, 0x05, 0x00 }),
            "REQUEST_PEER failure carries code 5 at payload[2]");
    }

    private static PartyMemberIdentity Identity(Guid session, ushort userId, string name) =>
        new(session, Guid.NewGuid(), (uint)(100 + userId), userId, name, 10, 0);

    private static void PartyManagerLifecycle(Action<bool, string> check)
    {
        var manager = new PartyManager();
        var sessionA = Guid.NewGuid();
        var sessionB = Guid.NewGuid();
        var sessionC = Guid.NewGuid();
        var memberA = Identity(sessionA, 1, "A");
        var memberB = Identity(sessionB, 2, "B");
        var memberC = Identity(sessionC, 3, "C");

        var created = manager.SetPartyInfo(memberA, 1, [], 4);
        check(created.Ok
            && created.Created
            && created.Party!.LeaderUserId == 1
            && created.Party.MembersBySlot()[0].SlotIndex == 0,
            "SET_PARTY_INFO creates a solo party led by the requester in slot 0");

        var edited = manager.SetPartyInfo(memberA, 0, [0x41], 2);
        check(edited.Ok
            && !edited.Created
            && edited.Party!.PartyId == created.Party!.PartyId
            && edited.Party.UserMax == 2
            && edited.Party.TitleKind == 0,
            "SET_PARTY_INFO edits the leader's party in place under the same id");

        check(!manager.SetPartyInfo(
                memberA with { SessionId = Guid.NewGuid() }, 1, [], 4).Ok,
            "SET_PARTY_INFO rejects stale sessions");
        check(!manager.SetPartyInfo(Identity(sessionC, 5, "C"), 1, [], 5).Ok,
            "SET_PARTY_INFO rejects capacities the client cannot send");
        check(manager.SetPartyInfo(memberA, 1, [], 4).Ok,
            "the leader restores the capacity to 4 before inviting");

        check(manager.RecordInvite(2, sessionB, 1, sessionA, out _),
            "a free member can be invited");
        var joinedB = manager.AcceptInvite(memberB, memberA, out var modeB);
        check(joinedB.Ok
            && modeB == "invite-into-inviter-party"
            && joinedB.Party!.Members.Count == 2
            && joinedB.Party.GetMember(2)!.SlotIndex == 1,
            "the invitee joins the inviter's party in the next free slot");

        check(!manager.RecordInvite(3, sessionC, 2, sessionB, out _),
            "a non-leader cannot invite");
        check(manager.RecordInvite(3, sessionC, 1, sessionA, out _),
            "the leader can invite a further member");

        var joinedC = manager.AcceptInvite(memberC, memberA, out _);
        check(joinedC.Ok && joinedC.Party!.Members.Count == 3,
            "the third member fills slot 2");

        check(!manager.RecordInvite(3, sessionC, 1, sessionA, out _),
            "an existing member cannot be invited again");

        check(!manager.Kick(1, 3, sessionC).Ok,
            "a non-leader cannot kick");
        check(!manager.Kick(3, 1, sessionA).Ok,
            "kicking an empty slot fails");
        var kicked = manager.Kick(joinedC.Party!.GetMember(3)!.SlotIndex, 1, sessionA);
        check(kicked.Ok
            && kicked.TargetUserId == 3
            && kicked.Party!.Members.Count == 2,
            "the leader kicks by roster slot");

        var left = manager.Leave(1, sessionA);
        check(left.Ok
            && left.RetiredParty is not null
            && left.RetiredParty!.PartyId == created.Party!.PartyId
            && left.Party!.PartyId != created.Party.PartyId
            && left.LeaderChanged
            && left.NewLeaderUserId == 2
            && left.Party!.GetMember(2)!.SlotIndex == 0,
            "a leader leaving retires the party id and rebuilds with the successor at slot 0");

        var replay = manager.AcceptInvite(memberB, memberA, out _);
        check(!replay.Ok && replay.Reason == "invite_not_found_or_stale",
            "a consumed or cancelled invite cannot be replayed");

        check(manager.RecordInvite(2, sessionB, 1, sessionA, out _)
            && manager.CancelInvite(2, sessionB, 1, sessionA)
            && !manager.AcceptInvite(memberB, memberA, out _).Ok,
            "a refusal cancels the pending invite");

        // Disconnect path: the generation and slots stay, leadership moves.
        check(manager.Leave(2, sessionB).Ok,
            "the successor dissolves the leftover solo party first");
        manager.SetPartyInfo(memberA, 1, [], 4);
        check(manager.RecordInvite(2, sessionB, 1, sessionA, out _)
            && manager.AcceptInvite(memberB, memberA, out _).Ok,
            "the reconnect scenario re-forms a party");
        var generationId = manager.GetPartyByUser(2)!.PartyId;
        var disconnected = manager.OnSessionDisconnected(1, sessionA);
        check(disconnected.Ok
            && disconnected.RetiredParty is null
            && disconnected.LeaderChanged
            && disconnected.NewLeaderUserId == 2
            && disconnected.Party!.PartyId == generationId,
            "a disconnecting leader preserves the party generation");
    }

    private static void RunCoordinatorFlow(Action<bool, string> check)
    {
        // Two connected sessions exchange presence and form a party over the
        // real delivery channels.
        var registry = new CharacterSessionRegistry(NullLogger<CharacterSessionRegistry>.Instance);
        var datagram = new GameplayDatagramService(
            new ServerOptions(),
            new RuntimeState(),
            NullLogger<GameplayDatagramService>.Instance);
        var coordinator = new PartyCoordinator(
            new ServerOptions(),
            registry,
            datagram,
            NullLogger<PartyCoordinator>.Instance);
        var inbox = new Dictionary<Guid, List<GameServerPacket>>();
        void Capture(Guid sessionId, GameServerPacket packet)
        {
            if (!inbox.TryGetValue(sessionId, out var list))
            {
                list = new List<GameServerPacket>();
                inbox[sessionId] = list;
            }

            list.Add(packet);
        }

        var sessionA = Guid.NewGuid();
        var sessionB = Guid.NewGuid();
        registry.AttachPacketChannel(sessionA, packet => Capture(sessionA, packet));
        registry.AttachPacketChannel(sessionB, packet => Capture(sessionB, packet));

        var identityA = Identity(sessionA, 1, "A");
        var identityB = Identity(sessionB, 2, "B");
        datagram.RegisterChannelSession(
            new RuntimeSession
            {
                Id = sessionA,
                Service = "test",
                RemoteEndpoint = "127.0.0.1"
            },
            System.Net.IPAddress.Loopback);
        datagram.RegisterChannelSession(
            new RuntimeSession
            {
                Id = sessionB,
                Service = "test",
                RemoteEndpoint = "127.0.0.1"
            },
            System.Net.IPAddress.Loopback);
        datagram.SetNatReport(sessionA, new GameNatReport(
            0,
            System.Net.IPAddress.Loopback,
            System.Net.IPAddress.Loopback,
            10001,
            1500));
        datagram.SetNatReport(sessionB, new GameNatReport(
            0,
            System.Net.IPAddress.Loopback,
            System.Net.IPAddress.Loopback,
            10002,
            1500));
        coordinator.PublishTownPresence(Presence(identityA), Appearance("A", 1));
        coordinator.PublishTownPresence(Presence(identityB), Appearance("B", 2));
        check(Typed(inbox, sessionA, 2).Count == 1
            && Typed(inbox, sessionB, 2).Count == 1
            && Typed(inbox, sessionA, 23).Count == 1
            && Typed(inbox, sessionB, 23).Count == 1,
            "town arrival exchanges USERINFO and USER_AREA between residents");

        var created = coordinator.SetPartyInfo(
            identityA,
            new PartySettingsRequest(1, [], 4));
        check(created.Ok
            && Typed(inbox, sessionA, 9).Count == 1
            && Typed(inbox, sessionA, 9)[0].Payload[4] == 0,
            "SET_PARTY_INFO formation reaches the creator as PARTY_INFO type 0");

        check(!coordinator.RequestPeer(
                identityA,
                new PeerRequestFrame(9, 0, 0)).Ok,
            "inviting an absent town user fails");

        var invite = coordinator.RequestPeer(
            identityA,
            new PeerRequestFrame(identityB.UserId, 0, 0x0A));
        check(invite.Ok
            && Typed(inbox, sessionB, 7).Count == 1
            && Typed(inbox, sessionB, 7)[0].Payload.AsSpan()
                .SequenceEqual(GameProtocolEngine.CreatePeerRequest(1, 0, 0x0A).Payload),
            "REQUEST_PEER delivers the invite popup to the target session");

        var joined = coordinator.RespondPeer(
            identityB,
            new PeerResponseFrame(identityA.UserId, 0, 0));
        var endpointsA = Typed(inbox, sessionA, 11);
        var endpointsB = Typed(inbox, sessionB, 11);
        check(joined.Ok
            && Typed(inbox, sessionA, 8).Count == 1
            && Typed(inbox, sessionA, 8)[0].Payload.AsSpan()
                .SequenceEqual(GameProtocolEngine.CreatePeerResponse(2, 0, 1).Payload)
            && Typed(inbox, sessionA, 9).Count == 2
            && Typed(inbox, sessionB, 9).Count == 1
            && joined.Party!.Members.Count == 2,
            "the accept ack reaches the inviter and both sides render the formation");
        // The creator already received a solo endpoint table at
        // SET_PARTY_INFO; the formation refresh carries both records.
        // Per-recipient endpoint table: the recipient's own record keeps its
        // real endpoint while peer records advertise the server's gameplay
        // listener (127.0.0.1:7002, port LE 5A 1B) because party datagrams
        // relay through it. Record stride is 21 bytes. The reference order is
        // endpoints pre-roster, accept ack, roster, endpoints post-roster
        // (rebinding onto the final member objects), host designation.
        check(endpointsA.Count == 3
            && endpointsB.Count == 2
            && endpointsA[1].Payload[0] == 2
            && endpointsA[1].Payload[1] == 0x01
            && endpointsA[1].Payload[11] == 0x11
            && endpointsA[1].Payload[12] == 0x27
            && endpointsA[1].Payload[22] == 0x02
            && endpointsA[1].Payload[32] == 0x5A
            && endpointsA[1].Payload[33] == 0x1B
            && endpointsA[2].Payload.AsSpan()
                .SequenceEqual(endpointsA[1].Payload)
            && endpointsB[0].Payload[11] == 0x5A
            && endpointsB[0].Payload[12] == 0x1B
            && endpointsB[0].Payload[32] == 0x12
            && endpointsB[1].Payload.AsSpan()
                .SequenceEqual(endpointsB[0].Payload),
            "the formation publishes self-real/peer-relay endpoint tables before and after the roster");
        var seqA = inbox[sessionA].Select(packet => (int)packet.ProtocolId).ToList();
        var seqB = inbox[sessionB].Select(packet => (int)packet.ProtocolId).ToList();
        check(MatchesRun(seqA, seqA.IndexOf(26) + 1, [11, 8, 9, 11, 26])
            && MatchesRun(seqB, seqB.IndexOf(7) + 1, [11, 9, 11, 26]),
            "the formation follows the reference phase order: endpoints, ack, roster, endpoints, host");
        check(Typed(inbox, sessionA, 26).Count == 2
            && Typed(inbox, sessionB, 26).Count == 1
            && Typed(inbox, sessionA, 26)[^1].Payload[0] == 0,
            "the formation designates the leader as UDP host (NOTI 26)");

        var slotB = joined.Party!.MembersBySlot()
            .First(member => member.UserId == identityB.UserId)
            .SlotIndex;
        var kicked = coordinator.Kick(identityA, slotB);
        check(kicked.Ok
            && Typed(inbox, sessionB, 9)[^1].Payload[4] == 3
            && Typed(inbox, sessionA, 9).Count == 3
            && Typed(inbox, sessionA, 10).Count == 1
            && Typed(inbox, sessionB, 10).Count == 0,
            "kicking clears the kicked member and refreshes the survivors");

        var refused = coordinator.RespondPeer(
            identityB,
            new PeerResponseFrame(identityA.UserId, 1, 2));
        check(refused.Ok
            && Typed(inbox, sessionB, 9).Count == 2,
            "a refusal never changes rosters");

        check(!registry.NotifyGamePacket(Guid.NewGuid(), GameProtocolEngine.CreateUserLeave(1)),
            "delivery to an unknown session reports failure");

        var leaveResult = coordinator.LeaveParty(identityA);
        check(leaveResult.Ok
            && Typed(inbox, sessionA, 9).Count == 4,
            "the last member leaving receives the party clear");
    }

    private static GameServerPacket Appearance(string name, ushort userId) =>
        GameProtocolEngine.CreateCurrentCharacterInfo(
            new GameCharacterSummary(0, System.Text.Encoding.ASCII.GetBytes(name), 0, 0, 10),
            userId,
            [],
            null,
            false);

    private static PartyPresenceUpdate Presence(PartyMemberIdentity identity) =>
        new(identity.SessionId,
            identity.CharacterId,
            identity.AccountUid,
            identity.UserId,
            identity.Name,
            identity.Level,
            identity.Job,
            TownId: 1,
            AreaId: 1,
            X: 100,
            Y: 120,
            Direction: 5);

    private static List<GameServerPacket> Typed(
        Dictionary<Guid, List<GameServerPacket>> inbox,
        Guid sessionId,
        byte protocolId) =>
        inbox.TryGetValue(sessionId, out var packets)
            ? packets.Where(packet => packet.ProtocolId == protocolId).ToList()
            : [];

    private static bool MatchesRun(
        IReadOnlyList<int> sequence,
        int start,
        IReadOnlyList<int> expected)
    {
        if (start < 0 || start + expected.Count > sequence.Count)
        {
            return false;
        }

        for (var index = 0; index < expected.Count; index++)
        {
            if (sequence[start + index] != expected[index])
            {
                return false;
            }
        }

        return true;
    }
}
