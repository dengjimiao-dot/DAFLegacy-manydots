namespace DFLegacy.Server;

/// <summary>Mailbox session cache and pending alarm flag (07 §4.3).</summary>
public sealed class MailboxState
{
    /// <summary>Pending new-mail alarm count drained per processed frame.</summary>
    public int PendingAlarm;

    public HashSet<uint> KnownIds = [];
}
