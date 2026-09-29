using DFLegacy.Server;

internal static class AntibotReportSmokeTests
{
    public static void Run(Action<bool, string> check)
    {
        // Captured 60CN heartbeat: seq u16 + u32 declared length + record
        // starting with a big-endian kind u16.
        byte[] heartbeat =
        [
            0x12, 0x00,
            0x0C, 0x00, 0x00, 0x00,
            0x00, 0x0A, 0xE5, 0xE5, 0x51, 0x47, 0x00, 0x04, 0x01, 0x00, 0x01, 0x00
        ];
        check(
            AntibotReportParser.TryParse(heartbeat, out var heartbeatReport),
            "the captured heartbeat parses");
        check(
            heartbeatReport is { Sequence: 0x12, DeclaredLength: 12, RecordKind: 0x000A },
            "heartbeat fields: seq 0x12, 12 declared bytes, kind 0x000A");

        // Login-time environment report: the declared length covers exactly
        // the remaining body.
        var loginBody = new byte[92];
        loginBody[0] = 0x02;
        loginBody[1] = 0x00;
        loginBody[2] = 0x56;
        loginBody[7] = 0x03;
        check(
            AntibotReportParser.TryParse(loginBody, out var loginReport),
            "the login report parses");
        check(
            loginReport is { Sequence: 2, DeclaredLength: 0x56, RecordKind: 0x0003 },
            "login fields: seq 2, 86 declared bytes, kind 0x0003");

        check(
            !AntibotReportParser.TryParse([0x01, 0x00, 0x0C], out _),
            "bodies shorter than seq + declared length are rejected");

        var lyingBody = new byte[10];
        lyingBody[2] = 0x40;
        check(
            !AntibotReportParser.TryParse(lyingBody, out _),
            "a declared length beyond the body is rejected");
    }
}
