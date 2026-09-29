using DFLegacy.Protocol;
using DFLegacy.Server;

internal static class RandomSourceSmokeTests
{
    public static void Run(Action<bool, string> check)
    {
        // 确定性替身：同种子复现同一有界序列，异种子序列不同。
        var first = new SeededRandomSource(20260922);
        var second = new SeededRandomSource(20260922);
        var sequence = Enumerable.Range(0, 64).Select(_ => first.Next(1_000_000)).ToArray();
        var replay = Enumerable.Range(0, 64).Select(_ => second.Next(1_000_000)).ToArray();
        check(sequence.SequenceEqual(replay)
                && sequence.All(value => value is >= 0 and < 1_000_000),
            "SeededRandomSource reproduces the same bounded sequence for the same seed");
        var divergent = Enumerable.Range(0, 64).Select(_ => new SeededRandomSource(1).Next(4)).ToArray();
        var divergentOther = Enumerable.Range(0, 64).Select(_ => new SeededRandomSource(2).Next(4)).ToArray();
        check(!divergent.SequenceEqual(divergentOther),
            "different seeds produce different sequences");

        // 接口契约：非正上界立即失败，上界 1 恒返回 0。
        var threw = false;
        try
        {
            new SeededRandomSource(1).Next(0);
        }
        catch (ArgumentOutOfRangeException)
        {
            threw = true;
        }

        check(threw, "IDropRandomSource.Next rejects non-positive bounds");
        check(new SeededRandomSource(7).Next(1) == 0,
            "bounded next over an exclusive maximum of 1 always yields 0");

        // 生产实现：唯一实例、值域正确、状态面只剩 provider。
        check(GameRandomSource.Shared.Next(4) is >= 0 and < 4
                && GameRandomSource.Shared.GetStatus() is { Provider: "Random.Shared" },
            "shared game random source is the managed Random.Shared provider");

        // 密码学随机边界：会话密钥必须仍走 RandomNumberGenerator——
        // 32 字节且两次实例互不相同；若被换成确定性伪随机会立刻暴露。
        var keyA = new EntranceSessionState();
        var keyB = new EntranceSessionState();
        check(keyA.KeyMaterial.Length == 32
                && keyB.KeyMaterial.Length == 32
                && !keyA.KeyMaterial.AsSpan().SequenceEqual(keyB.KeyMaterial),
            "entrance session key material stays 32 unpredictable bytes from RandomNumberGenerator");
    }
}
