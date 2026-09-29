using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// Active character projection: lease, wire identity, town position and the
/// last computed combat stats. Defaults mirror the HandleClientAsync closure
/// initializers one to one (07 §4.3).
/// </summary>
public sealed class CharacterState
{
    public CharacterRecord? Active { get; set; }

    /// <summary>Claim lease of the active character; null before SELECT_CHARACTER.</summary>
    public CharacterSessionLease? Lease { get; set; }

    public GameCharacterCombatStats? LastProjectedCombatStats { get; set; }

    /// <summary>
    /// Wire identity from CharacterNo so two simultaneous clients get distinct
    /// town/party user ids. Assigned at SELECT_CHARACTER; 1 is the
    /// pre-selection placeholder.
    /// </summary>
    public ushort LocalUserId { get; set; } = 1;

    public byte TownId { get; set; } = 1;
    public byte AreaId { get; set; } = 1;
    public short X { get; set; } = 474;
    public short Y { get; set; } = 234;
    public byte Direction { get; set; } = 5;
    public ushort MovementValue { get; set; } = 100;
    public bool AreaRosterInitialized { get; set; }
}
