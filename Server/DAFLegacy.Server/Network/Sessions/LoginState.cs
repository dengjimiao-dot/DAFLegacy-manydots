namespace DAFLegacy.Server;

/// <summary>
/// Account identity established by LOGIN (CMD 1). Field mapping follows the
/// HandleClientAsync closure variables one to one (07 §4.3).
/// </summary>
public sealed class LoginState
{
    public string AccountName = string.Empty;
    public uint AccountUid = AccountUidRules.InvalidUid;
}
