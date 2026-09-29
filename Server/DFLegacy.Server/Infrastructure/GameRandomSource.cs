namespace DFLegacy.Server;

// 全仓随机数分三类，不得混用（与 docs/design/06 的规则一致）：
// - 密码学随机：会话密钥等不可预测材料，必须走 RandomNumberGenerator，
//   不得改走伪随机（EntranceProtocolEngine 的会话密钥材料）；
// - 服务端裁决随机：掉落、奖励、强化、合成、商店内容等，只允许走本模块；
// - 客户端可复算随机：客户端要按同一种子重放的序列，当前无实现；
//   若将来引入，必须移植客户端 LCG 且保留其取模语义，不得用本类替代。
// 例外：JsonGameStore 的唯一邮件 ID 分配属于唯一性分配，不是玩法裁决，
// 保留 RandomNumberGenerator 用法，见该处注释。

// 服务端裁决随机的唯一生产实现。
//
// 线程安全：Random.Shared 自 .NET 6 起按线程持有状态，可直接并发调用。
//
// 无偏：Random.Shared.Next(bound) 内部按拒绝采样取值，不存在取模偏置。
//
// 环境约束：这里刻意不使用密码学随机——裁决随机对玩法结果的影响只需
// 分布正确与不可预测到"玩家无法在正常游玩中利用"的程度。
public sealed record GameRandomStatus(string Provider);

public sealed class GameRandomSource : IDropRandomSource
{
    public static GameRandomSource Shared { get; } = new();

    private GameRandomSource()
    {
    }

    public int Next(int exclusiveMaximum)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(exclusiveMaximum);
        return Random.Shared.Next(exclusiveMaximum);
    }

    public GameRandomStatus GetStatus() => new("Random.Shared");
}
