using System.Net;
using DAFLegacy.Protocol;
using DAFLegacy.Server;

/// <summary>
/// NAT 探测加固自测（docs/design/10 §4.3/§7 批次 1）：限流器窗口翻转、阈值边界、
/// 多 IP 隔离、不限流旁路、双端口共用实例；探测回包构造的长度校验。
/// </summary>
internal static class NatProbeHardeningSmokeTests
{
    public static void Run(Action<bool, string> check)
    {
        var clock = new FakeTimeProvider();

        // 阈值边界：limit=3 时同 IP 第 4 次被拒。
        var limiter = new NatProbeRateLimiter(limit: 3, clock);
        var source = IPAddress.Parse("10.0.0.1");
        check(limiter.TryAcquire(source) && limiter.TryAcquire(source) && limiter.TryAcquire(source),
            "the first three probes within the window pass");
        check(!limiter.TryAcquire(source),
            "the fourth probe from the same IP inside the window is rejected");

        // 窗口翻转：超过 1 秒窗口后重新放行并重置计数。
        clock.AdvanceSeconds(1);
        check(limiter.TryAcquire(source),
            "a probe after the window rolls over is accepted again");
        check(limiter.TryAcquire(source) && limiter.TryAcquire(source),
            "the fresh window still admits two more probes");
        check(!limiter.TryAcquire(source),
            "the fresh window rejects the fourth probe overall");

        // 多 IP 隔离：A 耗尽不影响 B。
        var other = IPAddress.Parse("10.0.0.2");
        check(limiter.TryAcquire(other),
            "a second IP is isolated from the exhausted source");

        // 双端口共用实例：同一 IP 经两端口交替探测按同一桶聚合计数。
        var shared = new NatProbeRateLimiter(limit: 2, clock);
        check(shared.TryAcquire(source) && shared.TryAcquire(source),
            "alternate probes to two ports from one IP consume the same bucket");
        check(!shared.TryAcquire(source),
            "the aggregated bucket rejects once the two-port budget is spent");

        // 不限流旁路：limit <= 0 恒放行。
        var unlimited = new NatProbeRateLimiter(limit: 0, clock);
        var allPass = true;
        for (var index = 0; index < 1000; index++)
        {
            allPass &= unlimited.TryAcquire(source);
        }

        check(allPass, "limit <= 0 bypasses rate limiting entirely");

        // 窗口表清扫：伪造源撑表时不泄漏（注入 1024+ 个源后仍可正常记账）。
        var sweeper = new NatProbeRateLimiter(limit: 1, clock);
        var sweepOk = true;
        for (var index = 0; index < 1200; index++)
        {
            sweepOk &= sweeper.TryAcquire(IPAddress.Parse($"10.{index / 256 % 256}.{index % 256}.7"));
        }

        check(sweepOk, "the window table stays functional while sweeping many spoofed sources");

        // 探测回包长度校验：客户端 sendto 恒 1 字节（值 1/5），其余长度一律拒绝。
        var remote = new IPEndPoint(IPAddress.Loopback, 40000);
        check(!GameDatagramProtocol.TryCreateNatProbeResponse([], remote, out _)
            && !GameDatagramProtocol.TryCreateNatProbeResponse([1, 2], remote, out _)
            && !GameDatagramProtocol.TryCreateNatProbeResponse(new byte[10], remote, out _)
            && !GameDatagramProtocol.TryCreateNatProbeResponse([9], remote, out _),
            "zero-length, oversized and unknown-type requests get no response");
        check(GameDatagramProtocol.TryCreateNatProbeResponse([1], remote, out var replyOne)
            && GameDatagramProtocol.TryCreateNatProbeResponse([5], remote, out var replyFive)
            && replyOne.Length == 7
            && replyFive.Length == 7
            && replyOne[0] == 2
            && replyFive[0] == 2,
            "one-byte probes of value 1 and 5 produce the 7-byte echo reply");
    }

    /// <summary>秒级假时钟：TimestampFrequency=1，时间戳即秒数。</summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        private long _seconds;

        public override long TimestampFrequency => 1;

        public override long GetTimestamp() => _seconds;

        public void AdvanceSeconds(int seconds) => _seconds += seconds;
    }
}
