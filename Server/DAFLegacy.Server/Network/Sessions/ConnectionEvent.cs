namespace DAFLegacy.Server;

/// <summary>
/// One cross-thread connection event (07 §4.3). Background services, timers
/// and cross-session pushers only enqueue events; the owning connection's
/// read loop drains them and performs any session-state mutation or send.
/// Payload slots stay small so future event kinds extend the record instead
/// of switching to per-kind channel types.
/// </summary>
public enum ConnectionEventKind
{
    PremiumServiceExpired,
    PremiumServiceUpdate,
    MailAlarm,
    CeraValueUpdated,
    FatigueReset,
    WeaknessRecovered,
    DungeonSelectionExpired,
    DungeonDeathTimeoutExpired,
    DungeonCardSelectionExpired,
}

public readonly record struct ConnectionEvent(
    ConnectionEventKind Kind,
    byte ServiceType = 0,
    int Value = 0,
    long Generation = 0);
