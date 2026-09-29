using System.Collections.Concurrent;

namespace DFLegacy.Server;

/// <summary>
/// Premium service expiries and pending account-currency pushes (07 §4.3).
/// The connection-local snapshot exists because CharacterRecord deliberately
/// contains no Premium persistence.
/// </summary>
public sealed class PremiumState
{
    public ConcurrentQueue<(byte ServiceType, uint RemainingSeconds)> PendingUpdates = new();

    public ConcurrentDictionary<byte, long> ExpiryUnix = new();

    /// <summary>Pending cash value from the account service; -1 means none.</summary>
    public int PendingCeraValue = -1;
}
