namespace DAFLegacy.Server;

/// <summary>
/// Character-screen request parsers (07 §4.7): CREATE_CHARACTER (CMD 5) and
/// DELETE_CHARACTER (CMD 6) bodies.
/// </summary>
internal static class CharacterParsers
{
    internal static bool TryReadCreateCharacter(byte[] body, out byte job, out byte[] nameBytes)
    {
        job = 0;
        nameBytes = [];
        if (body.Length < 7)
        {
            return false;
        }

        job = body[2];
        return ClientBodyParsers.TryReadLengthPrefixedBytes(body, 3, out nameBytes, out _)
            && nameBytes.Length is > 0 and < 31;
    }

    internal static bool TryReadDeleteCharacter(byte[] body, out byte slot)
    {
        slot = 0;
        if (body.Length < 3)
        {
            return false;
        }

        slot = body[2];
        return slot < MaximumTransferRoster.MaximumVisibleCharacters;
    }
}
