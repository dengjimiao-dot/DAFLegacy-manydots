namespace DFLegacy.Server;

public readonly record struct AntibotReport(
    ushort Sequence,
    uint DeclaredLength,
    int RecordKind);

// CMD 161 (60CN ENUM_CMDPACKET_ANTIBOT). The client emits these reports as
// fire-and-forget heartbeats: with no answer coming back it keeps advancing
// its report counter and neither retries nor punishes the session, so the
// server parses them only for the log trail and must not reply.
public static class AntibotReportParser
{
    public static bool TryParse(ReadOnlySpan<byte> body, out AntibotReport report)
    {
        report = default;
        if (body.Length < 6)
        {
            return false;
        }

        var sequence = (ushort)(body[0] | (body[1] << 8));
        var declaredLength = (uint)(body[2]
            | (body[3] << 8)
            | (body[4] << 16)
            | (body[5] << 24));
        if (declaredLength > body.Length - 6)
        {
            return false;
        }

        var recordKind = declaredLength >= 2
            ? (body[6] << 8) | body[7]
            : 0;
        report = new AntibotReport(sequence, declaredLength, recordKind);
        return true;
    }
}
