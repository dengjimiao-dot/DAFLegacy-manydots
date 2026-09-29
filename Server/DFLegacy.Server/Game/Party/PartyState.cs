namespace DFLegacy.Server;

/// <summary>
/// A connection's party-relevant identity, resolved by the entrance session
/// from its active character. UserId is the wire identity everywhere (town
/// roster, party roster, kick targets); AccountUid feeds the NOTI 11
/// endpoint-table account field.
/// </summary>
public sealed record PartyMemberIdentity(
    Guid SessionId,
    Guid CharacterId,
    uint AccountUid,
    ushort UserId,
    string Name,
    byte Level,
    byte Job)
{
    public PartyMember ToMember() => new()
    {
        UserId = UserId,
        SessionId = SessionId,
        AccountUid = AccountUid,
        Name = Name,
        Level = Level,
        Job = Job,
    };
}

/// <summary>
/// Party constants recovered from the DNF.exe 60A1 client: the PARTY_INFO
/// roster loop at 0x41B9CD iterates exactly four slots and the party window
/// kick flow scans slots 0..3 (0x8520C8), so a party never holds more than
/// four members. The SET_PARTY_INFO dialog offers 2/3/4 capacity radios
/// (0x7D60E9 stores radio index + 2), while a party whose members all leave
/// through dungeon flows may transiently persist with a single member.
/// </summary>
public static class PartyConstants
{
    public const int MaxMembers = 4;

    public const ushort EmptySlotUserId = 0xFFFF;

    /// <summary>Capacity bytes the ACT1 client can send in SET_PARTY_INFO.</summary>
    public static bool IsSupportedCapacity(byte userMax) => userMax is >= 2 and <= 4;

    /// <summary>
    /// The PARTY_INFO settings reader caps the party title dstr at 0x40 bytes
    /// (0x41B94C pushes 0x40 into sub_8B3250). Longer titles leave the read
    /// cursor misaligned, so the server must reject or truncate them first.
    /// </summary>
    public const int MaximumTitleBytes = 0x3F;
}

/// <summary>One party member's session-bound identity snapshot. UserId is the
/// wire identity everywhere (town roster, party roster, kick targets); the
/// coordinator resolves SessionId back to a connection for delivery.</summary>
public sealed class PartyMember
{
    public required ushort UserId { get; init; }
    public required Guid SessionId { get; init; }
    public uint AccountUid { get; init; }
    public string Name { get; init; } = string.Empty;
    public byte Level { get; init; }
    public byte Job { get; init; }

    /// <summary>Roster slot 0..3. Slots never compact so the remaining
    /// members' PARTY_INFO positions stay stable across joins and leaves.</summary>
    public byte SlotIndex { get; set; }

    public PartyMember Clone() => new()
    {
        UserId = UserId,
        SessionId = SessionId,
        AccountUid = AccountUid,
        Name = Name,
        Level = Level,
        Job = Job,
        SlotIndex = SlotIndex,
    };
}

/// <summary>
/// One party's server-side state. The wire settings mirror what the ACT1
/// client sends in SET_PARTY_INFO (0x7D5F86: u8 title kind, an optional
/// length-prefixed custom title, u8 user max) and reads back in PARTY_INFO
/// (0x41B8F9: u8 kind, conditional dstr, u8 tail byte). The leader is not a
/// PARTY_INFO field; the client recovers it from the roster tail byte at
/// 0x41BAD7 and treats slot 0 as the manager slot.
/// </summary>
public sealed class Party
{
    private readonly List<PartyMember> _members = new(PartyConstants.MaxMembers);

    public Party(int partyId)
    {
        PartyId = partyId;
    }

    public int PartyId { get; }

    public ushort LeaderUserId { get; set; }

    /// <summary>SET_PARTY_INFO title kind: 0 = custom name follows, 1..5 = preset.</summary>
    public byte TitleKind { get; set; }

    public byte[] TitleBytes { get; set; } = [];

    public byte UserMax { get; set; } = PartyConstants.MaxMembers;

    public IReadOnlyList<PartyMember> Members => _members;

    public int Count => _members.Count;

    public int Capacity => PartyConstants.IsSupportedCapacity(UserMax)
        ? UserMax
        : PartyConstants.MaxMembers;

    public bool IsFull => _members.Count >= Capacity;

    public bool IsEmpty => _members.Count == 0;

    public bool Contains(ushort userId) => GetMember(userId) is not null;

    public bool IsLeader(ushort userId) => LeaderUserId == userId && Contains(userId);

    public PartyMember? GetMember(ushort userId)
    {
        foreach (var member in _members)
        {
            if (member.UserId == userId)
            {
                return member;
            }
        }

        return null;
    }

    public PartyMember? GetMemberBySlot(byte slotIndex)
    {
        foreach (var member in _members)
        {
            if (member.SlotIndex == slotIndex)
            {
                return member;
            }
        }

        return null;
    }

    /// <summary>Members ordered by slot; the PARTY_INFO wire order.</summary>
    public List<PartyMember> MembersBySlot()
    {
        var ordered = new List<PartyMember>(_members);
        ordered.Sort((left, right) => left.SlotIndex.CompareTo(right.SlotIndex));
        return ordered;
    }

    public byte LeaderSlot => GetMember(LeaderUserId)?.SlotIndex ?? 0;

    /// <summary>Allocates the lowest free slot 0..3, or -1 when full.</summary>
    private int AllocateSlot()
    {
        var used = new bool[PartyConstants.MaxMembers];
        foreach (var member in _members)
        {
            if (member.SlotIndex < PartyConstants.MaxMembers)
            {
                used[member.SlotIndex] = true;
            }
        }

        for (var slot = 0; slot < PartyConstants.MaxMembers; slot++)
        {
            if (!used[slot])
            {
                return slot;
            }
        }

        return -1;
    }

    public bool TryAddMember(PartyMember member)
    {
        ArgumentNullException.ThrowIfNull(member);
        if (IsFull || Contains(member.UserId))
        {
            return false;
        }

        var slot = AllocateSlot();
        if (slot < 0)
        {
            return false;
        }

        member.SlotIndex = (byte)slot;
        _members.Add(member);
        return true;
    }

    public bool RemoveMember(ushort userId)
    {
        for (var index = 0; index < _members.Count; index++)
        {
            if (_members[index].UserId == userId)
            {
                _members.RemoveAt(index);
                return true;
            }
        }

        return false;
    }

    /// <summary>Detached copy so packet builders cannot race a join or leave.</summary>
    public Party CreateSnapshot()
    {
        var snapshot = new Party(PartyId)
        {
            LeaderUserId = LeaderUserId,
            TitleKind = TitleKind,
            TitleBytes = (byte[])TitleBytes.Clone(),
            UserMax = UserMax,
        };
        foreach (var member in _members)
        {
            snapshot._members.Add(member.Clone());
        }

        return snapshot;
    }
}
