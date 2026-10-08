using System.Net;
using DAFLegacy.Protocol;
using DAFLegacy.Server;

/// <summary>
/// 端点按会话认领自测（docs/design/10 §4.2 批次 2/3）：精确认领的唯一性、解析总序
/// 四级、单连接等价性与共享 IP 收紧、混合场景（同 IP 一报一未报）。认领与解析均为
/// 纯函数，输入为中枢连接快照（TownConnectionView）。
/// </summary>
internal static class GameplayEndpointClaimSmokeTests
{
    private static readonly IPAddress TcpIp = IPAddress.Parse("192.168.1.10");
    private static readonly IPAddress PublicIp = IPAddress.Parse("203.0.113.7");

    public static void Run(Action<bool, string> check)
    {
        var observed = new IPEndPoint(PublicIp, 40001);

        static TownConnectionView View(Guid sessionId, GameNatReport? report) =>
            new(sessionId, TcpIp, report);

        static GameNatReport Report(IPAddress publicIp, ushort publicPort) =>
            new(NatType: 2,
                LocalAddress: TcpIp,
                PublicAddress: publicIp,
                PublicPort: publicPort,
                Mtu: 1472);

        // 精确认领：唯一自报命中 → 返回该会话。
        var claimedId = Guid.NewGuid();
        var candidates = new List<TownConnectionView>
        {
            View(claimedId, Report(PublicIp, 40001)),
            View(Guid.NewGuid(), null),
        };
        var match = GameplayDatagramService.FindUniqueClaimTarget(candidates, observed);
        check(match == claimedId,
            "an exact self-report match resolves to the unique claiming session");

        // 无命中：全部未自报 → null。
        match = GameplayDatagramService.FindUniqueClaimTarget(
            [View(Guid.NewGuid(), null), View(Guid.NewGuid(), null)], observed);
        check(match is null,
            "no self-reported match yields no claim");

        // 多连接自报同端点 → 无法区分 → null。
        match = GameplayDatagramService.FindUniqueClaimTarget(
            [View(Guid.NewGuid(), Report(PublicIp, 40001)), View(Guid.NewGuid(), Report(PublicIp, 40001))],
            observed);
        check(match is null,
            "two connections reporting the same endpoint cannot be distinguished");

        // 解析总序第一级：认领端点优先于一切。
        var observedByAddress = new Dictionary<string, IPEndPoint>
        {
            [TcpIp.ToString()] = new IPEndPoint(PublicIp, 40001),
        };
        var claimedEndpoint = new IPEndPoint(PublicIp, 40001);
        var resolved = GameplayDatagramService.ResolveEndpoint(
            claimedEndpoint, TcpIp, sharedIp: true, observedByAddress, Report(IPAddress.Parse("203.0.113.9"), 55555));
        check(resolved is not null && resolved.Equals(claimedEndpoint),
            "a claimed endpoint wins over shared-IP observation and stale self-report");

        // 第二级：非共享 IP 采用观测端点（现行语义，优先于自报）。
        resolved = GameplayDatagramService.ResolveEndpoint(
            null, TcpIp, sharedIp: false, observedByAddress, Report(IPAddress.Parse("203.0.113.9"), 55555));
        check(resolved is not null && resolved.Equals(observedByAddress[TcpIp.ToString()]),
            "an unshared IP keeps the observation-first behaviour (single-session equivalence)");

        // 第三级收紧：共享 IP 且无认领时不得采用共享观测值，落到自报公网端点。
        resolved = GameplayDatagramService.ResolveEndpoint(
            null, TcpIp, sharedIp: true, observedByAddress, Report(PublicIp, 40099));
        check(resolved is not null && resolved.Port == 40099,
            "a shared IP without a claim falls back to the self-reported endpoint, not the shared observation");

        // 第四级：自报无效（端口 0）→ 无端点。
        resolved = GameplayDatagramService.ResolveEndpoint(
            null, TcpIp, sharedIp: true, observedByAddress, Report(PublicIp, 0));
        check(resolved is null,
            "an invalid self-report yields no endpoint");

        // 混合场景（审核补充项）：同 IP 一报一未报——已报者被认领，未报者的探测不学习。
        var reporterId = Guid.NewGuid();
        var mixed = new List<TownConnectionView>
        {
            View(reporterId, Report(PublicIp, 40001)),
            View(Guid.NewGuid(), null),
        };
        check(GameplayDatagramService.FindUniqueClaimTarget(mixed, new IPEndPoint(PublicIp, 40001)) == reporterId,
            "the reporting session claims its own probe");
        check(GameplayDatagramService.FindUniqueClaimTarget(mixed, new IPEndPoint(PublicIp, 40002)) is null,
            "the silent session's probe claims nobody and learns nothing");

        // CMD 2 第 4 字段（客户端 ICMP/MTU 探测结果，复位占位 -1）的归一化：区间内原样，
        // 越界（含探测未完成的 -1 与 0）回落 1472，避免占位值被当 MTU 通告给队友。
        static GameNatReport Probe(uint value) => new(
            NatType: 2,
            LocalAddress: TcpIp,
            PublicAddress: PublicIp,
            PublicPort: 40001,
            Mtu: value);
        check(GameplayDatagramService.NormalizeNatReport(Probe(1472)).Mtu == 1472
            && GameplayDatagramService.NormalizeNatReport(Probe(576)).Mtu == 576
            && GameplayDatagramService.NormalizeNatReport(Probe(65507)).Mtu == 65507,
            "an in-range ICMP/MTU probe value is advertised unchanged");
        check(GameplayDatagramService.NormalizeNatReport(Probe(uint.MaxValue)).Mtu
                == GameDatagramProtocol.FallbackReportedMtu
            && GameplayDatagramService.NormalizeNatReport(Probe(0)).Mtu
                == GameDatagramProtocol.FallbackReportedMtu
            && GameplayDatagramService.NormalizeNatReport(Probe(65508)).Mtu
                == GameDatagramProtocol.FallbackReportedMtu,
            "an out-of-range ICMP/MTU probe value falls back to the advertised default");
    }
}
