namespace DFLegacy.Server;

/// <summary>Outcome of a party operation, consumed by the coordinator to
/// decide which packets go to whom. All party references are detached
/// snapshots taken under the manager lock.</summary>
public sealed record PartyOpResult
{
    public bool Ok { get; init; }
    public string Reason { get; init; } = string.Empty;
    public Party? Party { get; init; }

    /// <summary>The retired client generation. Non-null means survivors must
    /// clear this party id before a fresh formation is rendered (the ACT1
    /// client applies PARTY_INFO type 0 as an ordered slot diff, so reusing a
    /// party id across a leader change corrupts the roster).</summary>
    public Party? RetiredParty { get; init; }

    public bool Created { get; init; }
    public bool Disbanded { get; init; }
    public bool LeaderChanged { get; init; }
    public ushort NewLeaderUserId { get; init; }

    /// <summary>The affected member (join target, leaver, or kicked member).</summary>
    public ushort TargetUserId { get; init; }

    /// <summary>Session of the affected member, so the coordinator can still
    /// address it (the leaver must clear the retired party id).</summary>
    public Guid TargetSessionId { get; init; }

    /// <summary>Members still in the party after the operation.</summary>
    public IReadOnlyList<PartyMember> RemainingMembers { get; init; } = [];

    /// <summary>Leaving result of a previous party the actor belonged to;
    /// its survivors still need a departure notification.</summary>
    public PartyOpResult? PriorPartyLeave { get; init; }

    public static PartyOpResult Fail(string reason) => new() { Ok = false, Reason = reason };
}

/// <summary>
/// Party lifecycle and registry, ported from the ServerS4A21 PartyManager and
/// reduced to what the 60A1 protocol exercises: SET_PARTY_INFO
/// create/edit, REQUEST_PEER/RESPONSE_PEER type-0 invites, LEAVE_PARTY,
/// WALKOUT kick by slot, and disconnect cleanup. Thread-safe; the coordinator
/// resolves sessions and builds packets, this class only owns state.
/// </summary>
public sealed class PartyManager
{
    private readonly object _lock = new();
    private readonly Dictionary<int, Party> _parties = new();
    private readonly Dictionary<ushort, int> _userToParty = new();

    // Pending invites bind both sides to the session ids that saw the
    // request, so a stale connection cannot consume or replay a newer
    // session's invite. Only the latest invite per invitee is kept.
    private readonly Dictionary<ushort, PendingPartyInvite> _pendingInvites = new();
    private int _nextPartyId = 1;

    public Party? GetPartyByUser(ushort userId)
    {
        lock (_lock)
        {
            return LookupPartyByUserLocked(userId);
        }
    }

    public Party? GetPartySnapshotByUser(ushort userId)
    {
        lock (_lock)
        {
            return LookupPartyByUserLocked(userId)?.CreateSnapshot();
        }
    }

    public Party? GetPartySnapshot(int partyId)
    {
        lock (_lock)
        {
            return _parties.TryGetValue(partyId, out var party)
                ? party.CreateSnapshot()
                : null;
        }
    }

    /// <summary>
    /// Applies SET_PARTY_INFO. A requester without a party creates one and
    /// becomes leader; an existing party updates its title and capacity.
    /// Fails when the identity is stale, the title exceeds the client's
    /// 0x40-byte reader cap, or the capacity byte is not one the ACT1 client
    /// can send.
    /// </summary>
    public PartyOpResult SetPartyInfo(
        PartyMemberIdentity member,
        byte titleKind,
        byte[] titleBytes,
        byte userMax)
    {
        var (userId, sessionId) = (member.UserId, member.SessionId);
        if (userId == 0 || sessionId == Guid.Empty)
        {
            return PartyOpResult.Fail("invalid_member_identity");
        }
        if (!PartyConstants.IsSupportedCapacity(userMax))
        {
            return PartyOpResult.Fail("unsupported_user_max");
        }
        if (titleBytes.Length > PartyConstants.MaximumTitleBytes)
        {
            return PartyOpResult.Fail("title_too_long");
        }

        lock (_lock)
        {
            // A requester without a party creates one; the party's own leader
            // edits it in place. There is no cross-party switch here: being
            // in any party routes to the edit path, which only the leader
            // may run.
            var existing = LookupPartyByUserLocked(userId);
            if (existing is null)
            {
                var fresh = new Party(_nextPartyId++)
                {
                    LeaderUserId = userId,
                    TitleKind = titleKind,
                    TitleBytes = (byte[])titleBytes.Clone(),
                    UserMax = userMax,
                };
                fresh.TryAddMember(member.ToMember());
                _parties[fresh.PartyId] = fresh;
                _userToParty[userId] = fresh.PartyId;

                return new PartyOpResult
                {
                    Ok = true,
                    Created = true,
                    Party = fresh.CreateSnapshot(),
                    TargetUserId = userId,
                    TargetSessionId = sessionId,
                    RemainingMembers = fresh.MembersBySlot(),
                };
            }

            var party = existing;
            if (party.LeaderUserId != userId || party.GetMember(userId)!.SessionId != sessionId)
            {
                return PartyOpResult.Fail("not_leader");
            }
            if (party.Count > userMax)
            {
                return PartyOpResult.Fail("member_count_exceeds_user_max");
            }

            party.TitleKind = titleKind;
            party.TitleBytes = (byte[])titleBytes.Clone();
            party.UserMax = userMax;

            return new PartyOpResult
            {
                Ok = true,
                Created = false,
                Party = party.CreateSnapshot(),
                TargetUserId = userId,
                TargetSessionId = sessionId,
                RemainingMembers = party.MembersBySlot(),
            };
        }
    }

    /// <summary>Registers a type-0 peer request (invite or join application).</summary>
    public bool RecordInvite(
        ushort inviteeUserId,
        Guid inviteeSessionId,
        ushort inviterUserId,
        Guid inviterSessionId,
        out string failureReason)
    {
        failureReason = string.Empty;
        if (inviteeUserId == 0 ||
            inviterUserId == 0 ||
            inviteeUserId == inviterUserId ||
            inviteeSessionId == Guid.Empty ||
            inviterSessionId == Guid.Empty)
        {
            failureReason = "invalid_invite";
            return false;
        }

        lock (_lock)
        {
            var inviterInParty = TryGetMemberLocked(
                inviterUserId, out var inviterParty, out var inviterMember);
            var inviteeInParty = TryGetMemberLocked(
                inviteeUserId, out var inviteeParty, out var inviteeMember);
            if (inviterInParty && inviterMember.SessionId != inviterSessionId ||
                inviteeInParty && inviteeMember.SessionId != inviteeSessionId)
            {
                failureReason = "stale_session";
                return false;
            }
            if (inviterInParty && inviteeInParty)
            {
                failureReason = "both_in_party";
                return false;
            }
            if (inviterInParty)
            {
                if (inviterParty.LeaderUserId != inviterUserId)
                {
                    failureReason = "not_leader";
                    return false;
                }
                if (inviterParty.IsFull)
                {
                    failureReason = "party_full";
                    return false;
                }
            }
            if (inviteeInParty && inviteeParty.IsFull)
            {
                failureReason = "party_full";
                return false;
            }

            _pendingInvites[inviteeUserId] = new PendingPartyInvite
            {
                InviteeSessionId = inviteeSessionId,
                InviterUserId = inviterUserId,
                InviterSessionId = inviterSessionId,
                InviterPartyId = inviterParty?.PartyId ?? 0,
                InviteePartyId = inviteeParty?.PartyId ?? 0,
            };
            return true;
        }
    }

    public bool CancelInvite(
        ushort inviteeUserId,
        Guid inviteeSessionId,
        ushort inviterUserId,
        Guid inviterSessionId)
    {
        lock (_lock)
        {
            if (!_pendingInvites.TryGetValue(inviteeUserId, out var invite) ||
                invite.InviteeSessionId != inviteeSessionId ||
                invite.InviterUserId != inviterUserId ||
                invite.InviterSessionId != inviterSessionId)
            {
                return false;
            }

            _pendingInvites.Remove(inviteeUserId);
            return true;
        }
    }

    /// <summary>
    /// Consumes the pending invite once and joins both sides. Neither party
    /// existing creates a fresh party led by the inviter; an inviter party
    /// absorbs the invitee; an invitee party absorbs the applying inviter.
    /// </summary>
    public PartyOpResult AcceptInvite(
        PartyMemberIdentity invitee,
        PartyMemberIdentity inviter,
        out string mode)
    {
        mode = string.Empty;
        if (invitee.SessionId == Guid.Empty || inviter.SessionId == Guid.Empty)
        {
            return PartyOpResult.Fail("invalid_member_identity");
        }

        lock (_lock)
        {
            if (!_pendingInvites.TryGetValue(invitee.UserId, out var invite) ||
                invite.InviteeSessionId != invitee.SessionId ||
                invite.InviterUserId != inviter.UserId ||
                invite.InviterSessionId != inviter.SessionId)
            {
                return PartyOpResult.Fail("invite_not_found_or_stale");
            }

            // An exact response consumes the request once; every later
            // failure requires a fresh REQUEST_PEER.
            _pendingInvites.Remove(invitee.UserId);

            var inviterInParty = TryGetMemberLocked(
                inviter.UserId, out var inviterParty, out var inviterMember);
            var inviteeInParty = TryGetMemberLocked(
                invitee.UserId, out var inviteeParty, out var inviteeMember);
            if (invite.InviterPartyId == 0)
            {
                if (inviterInParty)
                {
                    return PartyOpResult.Fail("inviter_party_changed");
                }
            }
            else if (!inviterInParty ||
                     inviterParty.PartyId != invite.InviterPartyId ||
                     inviterMember.SessionId != inviter.SessionId ||
                     inviterParty.LeaderUserId != inviter.UserId)
            {
                return PartyOpResult.Fail("inviter_not_current_leader");
            }

            if (invite.InviteePartyId == 0)
            {
                if (inviteeInParty)
                {
                    return PartyOpResult.Fail("invitee_party_changed");
                }
            }
            else if (!inviteeInParty ||
                     inviteeParty.PartyId != invite.InviteePartyId ||
                     inviteeMember.SessionId != invitee.SessionId)
            {
                return PartyOpResult.Fail("invitee_party_changed");
            }

            var destination = invite.InviterPartyId != 0 ? inviterParty : inviteeParty;
            if (destination?.IsFull == true)
            {
                return PartyOpResult.Fail("party_full");
            }

            if (invite.InviterPartyId == 0 && invite.InviteePartyId == 0)
            {
                mode = "create-inviter-party";
                var created = CreateLocked(inviter);
                if (!created.Ok)
                {
                    return created;
                }

                var joined = JoinLocked(created.Party!.PartyId, invitee);
                if (!joined.Ok)
                {
                    DisbandLocked(created.Party.PartyId);
                }

                return joined;
            }

            if (invite.InviterPartyId != 0)
            {
                mode = "invite-into-inviter-party";
                return JoinLocked(inviterParty.PartyId, invitee);
            }

            mode = "apply-into-invitee-party";
            return JoinLocked(inviteeParty.PartyId, inviter);
        }
    }

    public PartyOpResult Leave(ushort userId, Guid expectedSessionId)
    {
        lock (_lock)
        {
            if (!TryGetMemberLocked(userId, out _, out var member) ||
                member.SessionId != expectedSessionId)
            {
                return PartyOpResult.Fail("not_in_party_or_stale_session");
            }

            return LeaveLocked(userId) ?? PartyOpResult.Fail("not_in_party");
        }
    }

    /// <summary>Leader-only kick resolved by the WALKOUT slot byte.</summary>
    public PartyOpResult Kick(byte targetSlot, ushort byUserId, Guid expectedBySessionId)
    {
        lock (_lock)
        {
            if (!TryGetMemberLocked(byUserId, out var party, out var byMember) ||
                byMember.SessionId != expectedBySessionId)
            {
                return PartyOpResult.Fail("not_in_party_or_stale_session");
            }
            if (party.LeaderUserId != byUserId)
            {
                return PartyOpResult.Fail("not_leader");
            }

            var target = party.GetMemberBySlot(targetSlot);
            if (target is null)
            {
                return PartyOpResult.Fail("empty_slot");
            }
            if (target.UserId == byUserId)
            {
                return PartyOpResult.Fail("cannot_kick_self");
            }

            party.RemoveMember(target.UserId);
            _userToParty.Remove(target.UserId);

            var result = new PartyOpResult
            {
                Ok = true,
                Party = party.CreateSnapshot(),
                TargetUserId = target.UserId,
                TargetSessionId = target.SessionId,
                RemainingMembers = party.MembersBySlot(),
            };
            if (party.IsEmpty)
            {
                _parties.Remove(party.PartyId);
                result = result with { Disbanded = true };
            }

            return result;
        }
    }

    /// <summary>Disconnect cleanup: pending invites tied to this session are
    /// dropped and the member leaves while the party generation is preserved
    /// (the lowest occupied slot inherits leadership in place).</summary>
    public PartyOpResult OnSessionDisconnected(ushort userId, Guid sessionId)
    {
        if (sessionId == Guid.Empty)
        {
            return PartyOpResult.Fail("invalid_session");
        }

        lock (_lock)
        {
            var staleInvitees = new List<ushort>();
            foreach (var pair in _pendingInvites)
            {
                var invite = pair.Value;
                var inviteeMatch = pair.Key == userId &&
                                   invite.InviteeSessionId == sessionId;
                var inviterMatch = invite.InviterUserId == userId &&
                                   invite.InviterSessionId == sessionId;
                if (inviteeMatch || inviterMatch)
                {
                    staleInvitees.Add(pair.Key);
                }
            }
            foreach (var inviteeUserId in staleInvitees)
            {
                _pendingInvites.Remove(inviteeUserId);
            }

            if (!TryGetMemberLocked(userId, out var party, out var member))
            {
                return PartyOpResult.Fail("not_in_party");
            }
            if (member.SessionId != sessionId)
            {
                return PartyOpResult.Fail("stale_session");
            }

            return LeaveLocked(userId, preservePartyOnLeaderExit: true) ??
                   PartyOpResult.Fail("not_in_party");
        }
    }

    private Party? LookupPartyByUserLocked(ushort userId)
    {
        return _userToParty.TryGetValue(userId, out var partyId) &&
               _parties.TryGetValue(partyId, out var party)
            ? party
            : null;
    }

    private bool TryGetMemberLocked(
        ushort userId,
        out Party party,
        out PartyMember member)
    {
        party = LookupPartyByUserLocked(userId)!;
        member = party?.GetMember(userId)!;
        return party is not null && member is not null;
    }

    private PartyOpResult CreateLocked(PartyMemberIdentity leader)
    {
        var party = new Party(_nextPartyId++) { LeaderUserId = leader.UserId };
        party.TryAddMember(leader.ToMember());
        _parties[party.PartyId] = party;
        _userToParty[leader.UserId] = party.PartyId;
        return new PartyOpResult
        {
            Ok = true,
            Created = true,
            Party = party.CreateSnapshot(),
            TargetUserId = leader.UserId,
            TargetSessionId = leader.SessionId,
            RemainingMembers = party.MembersBySlot(),
        };
    }

    private PartyOpResult JoinLocked(int partyId, PartyMemberIdentity member)
    {
        if (!_parties.TryGetValue(partyId, out var party))
        {
            return PartyOpResult.Fail("party_not_found");
        }

        var prior = LeaveLocked(member.UserId);
        var joinedMember = member.ToMember();
        if (!party.TryAddMember(joinedMember))
        {
            return PartyOpResult.Fail("party_full_or_duplicate");
        }

        _userToParty[joinedMember.UserId] = party.PartyId;
        return new PartyOpResult
        {
            Ok = true,
            Party = party.CreateSnapshot(),
            TargetUserId = joinedMember.UserId,
            TargetSessionId = joinedMember.SessionId,
            RemainingMembers = party.MembersBySlot(),
            PriorPartyLeave = prior?.Ok == true ? prior : null,
        };
    }

    private void DisbandLocked(int partyId)
    {
        if (!_parties.TryGetValue(partyId, out var party))
        {
            return;
        }

        foreach (var member in party.Members)
        {
            _userToParty.Remove(member.UserId);
        }
        _parties.Remove(partyId);
    }

    /// <summary>
    /// Removes a member, transferring leadership when needed. A leader leaving
    /// a multi-member party retires the old party id: the ACT1 client applies
    /// PARTY_INFO type 0 as an ordered slot 0..3 diff, so keeping the id while
    /// shifting members corrupts its roster. Survivors are rebuilt under a
    /// fresh id with the successor re-added first (slot 0). Disconnect and
    /// dungeon-return paths instead preserve the generation and slots.
    /// </summary>
    private PartyOpResult? LeaveLocked(
        ushort userId,
        bool preservePartyOnLeaderExit = false)
    {
        if (LookupPartyByUserLocked(userId) is not { } party)
        {
            return null;
        }

        var wasLeader = party.LeaderUserId == userId;
        var leaverSessionId = party.GetMember(userId)!.SessionId;
        var retiredSnapshot =
            wasLeader && party.Count > 1 && !preservePartyOnLeaderExit
                ? party.CreateSnapshot()
                : null;
        party.RemoveMember(userId);
        _userToParty.Remove(userId);

        var result = new PartyOpResult
        {
            Ok = true,
            Party = party.CreateSnapshot(),
            TargetUserId = userId,
            TargetSessionId = leaverSessionId,
        };

        if (party.IsEmpty)
        {
            _parties.Remove(party.PartyId);
            return result with { Disbanded = true };
        }

        if (wasLeader)
        {
            var survivors = party.MembersBySlot();
            var successor = survivors[0];
            if (preservePartyOnLeaderExit)
            {
                party.LeaderUserId = successor.UserId;
                return result with
                {
                    LeaderChanged = true,
                    NewLeaderUserId = successor.UserId,
                    RemainingMembers = party.MembersBySlot(),
                };
            }

            _parties.Remove(party.PartyId);
            foreach (var survivor in survivors)
            {
                _userToParty.Remove(survivor.UserId);
            }

            var rebuilt = new Party(_nextPartyId++)
            {
                LeaderUserId = successor.UserId,
                TitleKind = party.TitleKind,
                TitleBytes = (byte[])party.TitleBytes.Clone(),
                UserMax = party.UserMax,
            };
            foreach (var survivor in survivors)
            {
                rebuilt.TryAddMember(survivor.Clone());
            }

            _parties[rebuilt.PartyId] = rebuilt;
            foreach (var member in rebuilt.Members)
            {
                _userToParty[member.UserId] = rebuilt.PartyId;
            }

            result = result with
            {
                Party = rebuilt.CreateSnapshot(),
                RetiredParty = retiredSnapshot,
                LeaderChanged = true,
                NewLeaderUserId = successor.UserId,
            };
        }

        return result with { RemainingMembers = result.Party.MembersBySlot() };
    }

    private sealed class PendingPartyInvite
    {
        internal Guid InviteeSessionId;
        internal ushort InviterUserId;
        internal Guid InviterSessionId;
        internal int InviterPartyId;
        internal int InviteePartyId;
    }
}
