namespace DFLegacy.Server;

/// <summary>
/// Fatigue reset, weakness recovery and creature death notification pending
/// flags (07 §4.3); also carries the stamina-state initialization gate that
/// RECOVER_STAMINA shares with the weakness flow.
/// </summary>
public sealed class WeaknessState
{
    public int PendingFatigueReset;

    /// <summary>Pending weakness recovery value; -1 means none.</summary>
    public int PendingWeaknessRecovery = -1;

    // NOTI 100 is a transition notification. Keep it connection-local
    // so repeated hunger/state refreshes do not replay a death event;
    // town actor reconstruction explicitly requests it again.
    public HashSet<uint> CreatureDeathNotificationsSent = [];

    public bool StaminaStateInitialized;
}
