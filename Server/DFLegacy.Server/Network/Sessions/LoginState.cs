namespace DFLegacy.Server;

/// <summary>
/// Account identity established by LOGIN (CMD 1). Field mapping follows the
/// HandleClientAsync closure variables one to one (07 §4.3); semantics stay
/// frozen during the migration.
/// </summary>
public sealed class LoginState
{
    public string AccountName { get; set; } = string.Empty;
    public uint AccountUid { get; set; } = AccountUidRules.InvalidUid;
}
