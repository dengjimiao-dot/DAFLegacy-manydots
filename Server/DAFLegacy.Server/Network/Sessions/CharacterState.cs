using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

/// <summary>
/// Active character projection: lease, wire identity, town position and the
/// last computed combat stats. Defaults mirror the former HandleClientAsync
/// closure initializers one to one (07 §4.3). Members are public fields so
/// ref-based drains (Interlocked/Volatile) keep working on moved variables.
/// </summary>
public sealed class CharacterState
{
    public CharacterRecord? Active;

    /// <summary>Claim lease of the active character; null before SELECT_CHARACTER.</summary>
    public CharacterSessionLease? Lease;

    public GameCharacterCombatStats? LastProjectedCombatStats;

    /// <summary>
    /// Wire identity from CharacterNo so two simultaneous clients get distinct
    /// town/party user ids. Assigned at SELECT_CHARACTER; 1 is the
    /// pre-selection placeholder.
    /// </summary>
    public ushort LocalUserId = 1;

    public byte TownId = 1;
    public byte AreaId = 1;
    public short X = 474;
    public short Y = 234;
    public byte Direction = 5;
    public ushort MovementValue = 100;
    public bool AreaRosterInitialized;
}
