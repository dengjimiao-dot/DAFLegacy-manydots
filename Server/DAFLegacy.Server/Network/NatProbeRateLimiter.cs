using System.Net;

namespace DAFLegacy.Server;

/// <summary>
/// NAT 探测请求的按源 IP 固定窗口限流器（docs/design/10-udp-nat-dataplane.md §4.3）。
/// 窗口 1 秒；双端口监听共用同一实例——客户端以同一 socket 交替探测两端口，
/// 按 IP 聚合计数才是本意。<paramref name="limit"/> &lt;= 0 表示不限流（旁路）。
/// 时钟经 <see cref="TimeProvider"/> 注入，自测可驱动窗口翻转。
/// </summary>
public sealed class NatProbeRateLimiter
{
    // 伪造源地址可撑大窗口表；达到阈值时顺带清扫过期窗口兜底。
    private const int SweepThreshold = 1024;

    private readonly int _limit;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private readonly Dictionary<IPAddress, (long WindowStart, int Count)> _windows = new();

    public NatProbeRateLimiter(int limit, TimeProvider? timeProvider = null)
    {
        _limit = limit;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>为一次探测请求记账；返回 false 表示该源已超限，调用方静默丢弃。</summary>
    public bool TryAcquire(IPAddress source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (_limit <= 0)
        {
            return true;
        }

        var now = _timeProvider.GetTimestamp();
        var frequency = _timeProvider.TimestampFrequency;
        lock (_gate)
        {
            if (_windows.Count >= SweepThreshold)
            {
                PruneExpiredLocked(now, frequency);
            }

            if (_windows.TryGetValue(source, out var window) && now - window.WindowStart < frequency)
            {
                if (window.Count >= _limit)
                {
                    return false;
                }

                _windows[source] = (window.WindowStart, window.Count + 1);
                return true;
            }

            _windows[source] = (now, 1);
            return true;
        }
    }

    private void PruneExpiredLocked(long now, long frequency)
    {
        List<IPAddress>? expired = null;
        foreach (var (source, window) in _windows)
        {
            if (now - window.WindowStart >= frequency)
            {
                (expired ??= []).Add(source);
            }
        }

        if (expired is not null)
        {
            foreach (var source in expired)
            {
                _windows.Remove(source);
            }
        }
    }
}
