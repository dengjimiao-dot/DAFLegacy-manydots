using System.Text;

namespace DAFLegacy.Server;

/// <summary>
/// LOGIN (CMD 1) request parser (07 §4.7): the body carries two
/// length-prefixed strings, account name and optional password.
/// </summary>
internal static class LoginParsers
{
    private static readonly Encoding ClientEncoding = PvfEncodings.Cp936Lossy();

    internal static bool TryReadLoginCredentials(
        byte[] body,
        out string name,
        out string? password)
    {
        name = "test";
        password = null;
        if (!ClientBodyParsers.TryReadLengthPrefixedBytes(body, 2, out var bytes, out var offset))
        {
            return false;
        }

        name = ClientEncoding.GetString(bytes);
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (offset == body.Length)
        {
            return true;
        }

        if (!ClientBodyParsers.TryReadLengthPrefixedBytes(body, offset, out var passwordBytes, out _))
        {
            return false;
        }

        // The current launcher-backed client appends machine/session metadata
        // after the length-prefixed password. Authentication only consumes the
        // two credential fields; requiring the password to end at the packet
        // boundary silently left the channel session on its former test-account
        // defaults.
        password = ClientEncoding.GetString(passwordBytes);
        return !string.IsNullOrEmpty(password);
    }
}
