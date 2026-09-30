namespace DAFLegacy.Server;

/// <summary>Mailbox session cache and pending alarm flag (07 §4.3).</summary>
public sealed class MailboxState
{
    public HashSet<uint> KnownIds = [];
}
