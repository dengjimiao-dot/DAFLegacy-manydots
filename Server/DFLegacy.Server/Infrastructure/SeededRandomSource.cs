namespace DFLegacy.Server;

// 确定性实现：给定种子的可复现序列，用于自测与问题复现。
// 注意 System.Random 非线程安全（并发调用会损坏内部状态），
// 因此本类型按实例单线程使用，不共享。
public sealed class SeededRandomSource : IDropRandomSource
{
    private readonly Random _random;

    public SeededRandomSource(int seed) => _random = new Random(seed);

    public int Next(int exclusiveMaximum)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(exclusiveMaximum);
        return _random.Next(exclusiveMaximum);
    }
}
