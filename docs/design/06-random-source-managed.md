# 06 · 随机源降级为纯托管实现（服务端与 DFLegacy.RandomNative 解耦，模块保留至 Client 层）

- 状态：**已实施**（2026-09-24 标注）。服务端随机源托管化已落地（`Server/DFLegacy.Server/Infrastructure/`；2026-09-29 目录修订：原 `Random/` 三文件已并入 `Infrastructure/`，契约由本档与类头注释约束，见 07 §11）；**修订：`DFLegacy.RandomNative` 不删除**，保留至 `Client/DFLegacy.RandomNative/`，仅解除服务端耦合。下文以"删除模块"为结论的表述按此修订理解
- 上级文档：[00 开档总纲](00-kickoff.md) · [01 目标架构](01-target-architecture.md) · [02 可移植性契约](02-portability-contracts.md) · [03 构建、发布与 CI](03-build-release-ci.md) · [04 迁移计划](04-migration-plan.md)
- 参考实现：`ServerS4A21`（下称"参考项目"）
- **取代 [05 · RandomNative 跨平台化提案](05-randomnative-crossplatform.md)**：本提案不做原生模块的跨平台移植；服务端与模块解耦（模块保留至 Client 层），05 的结论仍作废
- 验证环境：macOS 26.6.2 / Apple clang 21.0.0 / dotnet SDK 10.0.301

---

## 1. 决策

**服务端与 `DFLegacy.RandomNative` 解耦，把服务端裁决随机改为纯托管的单一实现**（模块保留至 `Client/DFLegacy.RandomNative/`，仅移出服务端），参考参考项目的随机概率模块设计。

一句话理由：这个原生模块的全部作用是把 CPU 的 RDSEED 硬件熵源接进服务端，而 [05 · §1](05-randomnative-crossplatform.md) 已经确认它**对玩法结果是零影响**——掉落、奖励、强化、合成需要的分布质量，操作系统或运行时的伪随机已经完全满足。为这一点收益维持一个跨平台原生工程（含 RID 探测、`DllImportResolver`、独立 CMake 工具链、CTest、以及 `MSB4278` 导致的"永远进不了解决方案"），成本与收益不成比例。

服务端与它解耦能一次性消掉以下全部问题，而不是逐个绕过：

| 原问题 | 解耦后 |
| --- | --- |
| 写死的 `LibraryName = "DFLegacy.RandomNative.dll"` | 服务端代码里不再有任何原生库名（本就是"搬目录"解决不了的那条，见 §3.4） |
| 跨平台编译（M6） | 不做：服务端只剩 C#，模块保持 Windows 专属 |
| `.slnx` 成员资格（`MSB4278`） | 2026-09-23 修订：原生项目回到解决方案（仅 IDE 可见，`<Build Project="false" />` 不参与构建），`MSB4278` 对解决方案级 `dotnet build` 重新成立——服务端按项目构建，不受影响 |
| `ProjectReference` / `BuildRandomNativeReference` / `RandomNativeBinary` / 硬编码 `Content`（4 处编译期耦合） | 全部删除 |
| RID 布局、`SetDllImportResolver`、探测顺序（02 · R2 的机制） | 服务端无实例，规则可留可删 |
| CTest `rdseed_native`、`build-native.ps1`、CMake 预设 | 随模块保留至 Client 层，不再属于服务端交付 |

**边界**：本提案只治理"服务端裁决随机"。**协议层会话密钥必须继续使用 `RandomNumberGenerator`**，不得改走伪随机（§4.1）。

---

## 2. 参考项目的设计（事实摘录）

参考项目的随机模块由两条明确的分类规则、两个极小的实现组成。

### 2.1 成文规则

`Docs/服务端业务开发规范.md` §5.8 原文：

```text
### 5.8 随机、时间和 PVF

- 客户端可复算的掉落使用房间 `DnfLcg`。
- 其他服务端随机使用 `Infrastructure.ServerRandom`，不得自行 `new Random()`。
```

同一文档另有两处相关约定：`Infrastructure/*` 的职责包含"数据库、时钟、随机数、路径、运行时组合根"（第 49 行）；以及"不得新增第二个类似的 Service、Repository、缓存、**随机源**或生命周期管理器，除非文档说明现有所有者无法复用"（第 228 行）——即**随机源必须单一所有权**。

### 2.2 服务端裁决随机：`ServerRandom`

`Server/DfoServer/Infrastructure/ServerRandom.cs`，全文 35 行：

```csharp
using System;

namespace DfoServer.Infrastructure
{
    // 服务端裁决随机的唯一入口(线程安全)。
    //
    // 全仓随机数分两类, 不可混用:
    // - 客户端同步随机: 必须走房间 LCG(DnfLcg, 种子随开图包下发, 与客户端逐步对齐),
    //   掉落序列等凡是客户端要按同一种子重放的都属此类, 换随机源会破坏对齐;
    // - 服务端裁决随机: 结果只体现在服务端后续下发的数据里(选迷宫/冠军怪抽取/
    //   神秘商店 NPC/深渊难度权重等), 用本类。
    //
    // System.Random 非线程安全, 并发调用会损坏内部状态(之后恒返回 0);
    // 副本域存在脱离会话串行的定时器线程, 因此统一加锁。
    public static class ServerRandom
    {
        private static readonly Random _rng = new Random();
        private static readonly object _lock = new object();

        public static int Next()
        {
            lock (_lock) return _rng.Next();
        }

        public static int Next(int maxValue)
        {
            lock (_lock) return _rng.Next(maxValue);
        }

        public static int Next(int minValue, int maxValue)
        {
            lock (_lock) return _rng.Next(minValue, maxValue);
        }
    }
}
```

三个要点：**唯一入口**（不是"一个工具类"，是"全仓禁自行 `new Random()`"）、**线程安全**（理由写得很具体：`System.Random` 并发会损坏内部状态，之后恒返回 0；副本域有脱离会话串行的定时器线程）、**实现极简**（`Random` + `lock`）。

### 2.3 客户端同步随机：`DnfLcg`

`Server/DfoServer/Game/Dungeon/DnfLcg.cs`：

```csharp
public sealed class DnfLcg
{
    private uint _seed;

    public DnfLcg(uint seed) => _seed = seed;

    public uint Seed => _seed;

    public int Next()
    {
        uint v2 = _seed * 1103515245u + 12345u;
        uint v3 = v2 * 1103515245u + 12345u;
        uint v4 = v3 * 1103515245u + 12345u;
        _seed = v4;

        int hi2 = (int)((v2 >> 16) & 0x7FF);
        int hi3 = (int)((v3 >> 16) & 0x3FF);
        int hi4 = (int)((v4 >> 16) & 0x3FF);
        return ((hi2 << 10) ^ hi3) << 10 | hi4;
    }

    public int Next(int max)
    {
        if (max <= 0) return Next();
        return Next() % max;
    }
}
```

这是**客户端自己的随机算法**的服务端复刻：glibc LCG 常数（`1103515245` / `12345`）连续走三步，取三步各自的高 11/10/10 位拼成一个 31 位结果；`Next(max)` 用**取模**而不是拒绝采样——因为它要与客户端逐位一致，偏差是客户端语义的一部分，不能"修正"。

消费方是掉落链路：`DropGenerator`（构造函数注入 `DnfLcg`）、`MonsterDropConfig.RollRarity/ChooseEquipment/ChooseStackable`、`HellMonsterDropConfig`、`DimensionDropSystem`、`WorldDropSystem`、`IndependentDropSystem`、`ClearRewardGenerator` 等。

### 2.4 规模

| 指标 | 参考项目 |
| --- | --- |
| `ServerRandom` 调用点 | **88** 处，分布在 25+ 个文件 |
| 重载使用分布 | `Next(N)` 为主（权重表、百分比、列表下标），另有 `Next()` 与 `Next(min,max)` |
| `ServerRandom` 实现 | 35 行 |
| `DnfLcg` 实现 | 22 行 |

---

## 3. DFLegacy 现状与差距

### 3.1 现状

`src/DFLegacy.Server/GameRandomSource.cs`（约 249 行）实现三级提供程序链：RDSEED（原生）→ 操作系统 CSPRNG（`RandomNumberGenerator.GetInt32`）→ 托管伪随机（`Random.Shared`），并把加载失败、拒绝采样、能力标记、5 个计数器全部包在一个类里。业务侧通过 `IDropRandomSource`（单一方法 `int Next(int exclusiveMaximum)`）取值，共 **42 处调用点、16 个文件**。

### 3.2 差距一：没有"客户端同步随机"这一类

DFLegacy 有 `ClientRandomSeed` 的概念，但它只是**由裁决随机生成后下发给客户端**：

- `DungeonRunState.cs:297` `CreateClientRandomSeed(IDropRandomSource random)` —— 从裁决随机抽 32 位组装种子
- `DungeonCatalog.cs:150` `ClientRandomSeed { get; init; }` —— 存进房间定义
- `EntranceService.cs:10440,10465,10612,10633,10658` —— 随房间包下发

**全仓库没有任何 LCG 实现**（检索 `1103515245` / `Lcg` / `ClientRandom` 均无命中），服务端从不复算客户端序列。而参考项目的规则是"客户端可复算的掉落必须走房间 LCG"。

**这是本提案唯一需要你确认的领域问题**（§7 · Q1）：DFLegacy 的客户端是否会从这个种子复算掉落序列？

- 若**不会**（客户端只把种子用于表现层）：DFLegacy 的随机只有"裁决"一类，本提案直接落地，`DnfLcg` 不必移植。
- 若**会**：那么 DFLegacy **今天就存在对齐缺口**——服务端掉落走的是 `GameRandomSource`，客户端复算走的是种子驱动的 LCG，两者必然不一致。这种情况需要照搬参考项目：把可复算的掉落改走移植版 `DnfLcg`，并让 `ClientRandomSeed` 成为该 LCG 的种子。

无论哪种答案，**种子与序列必须同源**这条规则都应当现在就记录进文档。

### 3.3 差距二：模块外还有两处 `RandomNumberGenerator`

| 位置 | 用途 | 处置 |
| --- | --- | --- |
| `EntranceProtocolEngine.cs:10` | `GetBytes(32)` 会话密钥材料 | **必须保持 CSPRNG**。这是密码学用途，改走伪随机会是安全退化，不是简化 |
| `JsonGameStore.cs:5545` | 分配唯一邮件 ID（`GetInt32(1, int.MaxValue)` + 1024 次重试） | 属"唯一性分配"，非玩法裁决。建议**原样保留**，但在模块注释中记为显式例外，避免"单一入口"规则被后续改动无声破坏 |

这两处说明：**参考项目的"两类"规则在 DFLegacy 必须扩成"三类"**（§4.1）。

### 3.4 顺带说明：`LibraryName` 与目录位置无关

上一轮已经实测清楚（对照实验：同一份原生库放下 `runtimes/osx-arm64/native/libDFLegacy.RandomNative.dylib`）：

```text
A) DllImport("DFLegacy.RandomNative") + SetDllImportResolver → 调用成功
B) DllImport("DFLegacy.RandomNative.dll")（现版写法）        → DllNotFoundException
```

即：去掉写死文件名的**唯一**办法是"逻辑名 + 解析器"或"彻底不要原生库"，**把模块搬到哪个目录都无效**——`DllImport` 的字符串由运行时探测和解析器决定，与源码位置毫无关系。本提案选择后者，因此这类问题整体消失。

---

## 4. 设计提案

### 4.1 三类随机（比参考项目多一类）

| 类别 | 用途 | 实现 | DFLegacy 实例 |
| --- | --- | --- | --- |
| **密码学随机** | 会话密钥、令牌、任何需要不可预测的字节 | `RandomNumberGenerator` | `EntranceProtocolEngine.cs:10` |
| **服务端裁决随机** | 掉落、奖励、强化、合成、商店内容、迷宫/怪物抽取 | **本提案的单一托管实现** | 42 处调用点 / 16 文件 |
| **客户端可复算随机** | 客户端要按同一种子复算的序列 | 客户端 LCG 的服务端复刻（本提案暂不引入，见 §7 · Q1） | **当前无** |
| （附）唯一性分配 | 唯一 ID 生成 | 保持现状 | `JsonGameStore.cs:5545` |

**三类不得混用**，与参考项目 §5.8 同源，并补上前两类：

- 密码学的东西**不得**走裁决随机（安全退化）；
- 裁决的东西**不得**自行 `new Random()`（分布与可测性退化，参考项目明文禁止）；
- 只要有一条序列客户端会复算，它**必须**走第三类，且取模语义原样保留。

建议把这段规则写进随机源模块（现 `Infrastructure/GameRandomSource.cs`）的文件头注释，作为与参考项目 `ServerRandom` 注释对等的成文约定。

### 4.2 有意保留 `IDropRandomSource` 注入缝（对参考项目的一处偏离）

参考项目的 `ServerRandom` 是**静态类**，无法注入替身，其自测只能测试间接路径。DFLegacy 的 `IDropRandomSource` 是更好的设计：16 个消费文件、以及自测里的 `SequenceDropRandomSource` / `ZeroDropRandomSource` / `FixedDropRandomSource` 都依赖它做确定性测试。

**因此本提案保留接口与全部调用点不动**，只替换实现。这满足参考项目"单一所有权"的实质（生产环境只有 `GameRandomSource.Shared` 一个实例），同时不牺牲可测性。这是一处有意的、有理由的偏离，不是遗漏。

### 4.3 新的托管实现

保留对外名字 `GameRandomSource.Shared`，使 42 处调用点零改动。约 249 行 → 约 20 行：

```csharp
namespace DFLegacy.Server;

// 掉落、奖励、强化、合成等一切"服务端裁决随机"的唯一取值入口。
public interface IDropRandomSource
{
    int Next(int exclusiveMaximum);
}

// 服务端裁决随机的唯一实现。
//
// 线程安全：Random.Shared 自 .NET 6 起按线程持有状态，本身即可并发调用，
// 因此不需要参考项目 ServerRandom 的那把锁，也不需要额外的同步封装。
//
// 无偏：Random.Shared.Next(bound) 内部按拒绝采样取值，不存在取模偏置，
// 因此 RDSEED 时代那段 rejectionThreshold 逻辑整体删除。
//
// 环境约束：这里刻意不使用密码学随机——裁决随机对玩法结果的影响只需
// 分布正确与不可预测到"玩家无法在正常游玩中利用"的程度。会话密钥等
// 密码学材料必须继续走 RandomNumberGenerator（见 EntranceProtocolEngine）。
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
}
```

测试与复现用的确定性实现（对应 [04 · M2](04-migration-plan.md) 里原本规划的 `ManagedRandomSource`）：

```csharp
// 确定性实现：给定种子的可复现序列，用于自测与问题复现。
// 注意 System.Random 非线程安全（参考项目 ServerRandom 注释里那条理由），
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
```

若 §7 · Q1 的答案是"客户端会复算掉落"，则追加移植版客户端 LCG（取模语义原样保留，不得改成拒绝采样）：

```csharp
// 客户端同步随机：与客户端逐位对齐，改动任何一处都会让客户端复算的掉落序列错位。
// Next(max) 的取模是客户端语义的一部分，不要"修正"成拒绝采样。
public sealed class ClientLcg(uint seed)
{
    private uint _seed = seed;

    public uint Seed => _seed;

    public int Next() { /* 三步 1103515245u/12345u + 高位拼接，与客户端一致 */ }

    public int Next(int max) => max <= 0 ? Next() : Next() % max;
}
```

### 4.4 状态面收敛

今天 `GameRandomStatus` 有 9 个字段（`PreferredProvider`、`CpuSupportsRdSeed`、`NativeBridgeLoaded`、`RdSeedEnabled` + 5 个计数器），被 6 处消费。

**提议收缩为一个字段**，保留记录与 `GetStatus()` 方法以免动调用点：

```csharp
public sealed record GameRandomStatus(string Provider);
```

删掉的是：`CpuSupportsRdSeed`、`NativeBridgeLoaded`、`RdSeedEnabled`、`RdSeedSamples`、`RdSeedUnavailable`、`RejectedSamples`、`OperatingSystemSamples`、`PseudoRandomSamples`——它们描述的全是"三级降级链现在走到哪一级"，链条没了，字段也就没有意义。

受影响的消费点（已逐个核对）：

| 位置 | 现状 | 处置 |
| --- | --- | --- |
| `Program.cs:75-81` | 启动日志打 4 个能力字段 | 改为打一个 provider 名 |
| `Program.cs:211` | `/api/status` 的 `Random` 字段 | 保留字段，载荷变为 `{ "provider": "..." }` |
| `tests/DFLegacy.SmokeTests/Program.cs:42-107` | 用注入委托构造 `GameRandomSource`，断言三级链与拒绝采样 | **整段删除**，改由 `SequenceDropRandomSource` 等替身覆盖接口语义 |
| `tests/.../EquipmentQualitySmokeTests.cs:152-175` | 同上，断言硬件种子 | 改用 `SeededRandomSource` 提供确定性种子 |
| `wwwroot/admin/admin.js` | **不消费该字段**（已实测，检索无命中） | 无需改动 |
| `ADMIN_API.md:113` | 记录 `random` 为"RDSEED、系统 CSPRNG 和回退源状态及计数" | 文案改写为单一提供程序 |
| `README.md:34,96` | 成文介绍原生模块与"没有该 dll 时回退" | 两处删除或改写 |

注意：`IDropRandomSource` 的接口与 42 处调用点**完全不动**，因此业务代码零改动，分布行为不变。

### 4.5 目标形态：文件布局与调用关系

**没有"子模块"这一层。** 这里要避免一个术语陷阱：在托管世界里，`Random/` 是一个**模块目录**，不是子模块、不是独立程序集、不是独立构建单元——它就是同一个 `DFLegacy.Server.dll` 里的一组 `.cs` 文件。这与 [D3](00-kickoff.md#5-关键设计决策) 的决定一致（4 个托管项目收成 1 个项目 + 模块目录）。引入"子模块"只会凭空多出一个构建单元，而它没有任何可独立交付的边界。

**目录**（M0 之后的形态；当前 `GameRandomSource.cs` 平铺在项目根）：

```text
Server/DFLegacy.Server/
  Random/                          ← 模块目录（当前不存在）
    IDropRandomSource.cs           ← 消费缝，仅 1 个成员
    GameRandomSource.cs            ← 生产实现（Random.Shared），约 20 行
    SeededRandomSource.cs          ← 确定性实现，自测与问题复现用
    （可选）ClientLcg.cs            ← 仅当 §7 · Q1 的答案是"客户端会复算"
```

**调用关系**（方向不要画反）：

```text
业务侧：18 个文件 / 42 处调用点
   └─ 依赖 IDropRandomSource                    ← 抽象握在业务侧，不依赖具体实现
        ├─ 生产：GameRandomSource.Shared        ← 唯一实例
        └─ 自测：Sequence / Zero / Fixed / Seeded ← 确定性替身
GameRandomSource
   └─ 依赖 BCL 的 Random.Shared                 ← 不是我们交付的 DLL，也没有 P/Invoke
```

所以准确的描述不是"新模块被 `GameRandomSource` 调用"，而是 **`GameRandomSource` 本身就是那个模块的生产实现**。在两者之间再插一层门面没有收益——它们同属一个程序集、同一份 CI、同一次编译。

**命名决定：不保留 `RandomNative` 这个名字。** 实现里已经不含任何原生代码，留着这个名字只会延续一类误解——原 `01 · 6.1` 曾专门指出"这个名字、以及它曾经的位置，都容易引出错误结论"（该节已随服务端解耦移除）。模块按 [01 · 4.1](01-target-architecture.md#41-dflegacyserver主项目) 的目标命名叫 `Random/`，类型按职责命名。

**一处可选的重命名**：[01 · 4.1](01-target-architecture.md#41-dflegacyserver主项目) 与 [04 · M2](04-migration-plan.md) 规划的是 `IRandomSource`，本提案为换取零调用点改动而保留 `IDropRandomSource`。取舍是：改名更贴合设计文档的既有命名，但纯机械地触及 16 个文件、42 处调用点；不改名则业务代码一行不动。**建议先按 `IDropRandomSource` 落地，把重命名留作独立提交**——理由与 [01 · 9.4](01-target-architecture.md#94-命名空间策略分两步) 把"移动文件"和"改命名空间"拆成两步相同。

**本方案不产生任何新的 DLL 调用。** 项目里剩下的原生调用只有 `DFLegacy.Launcher/D3D9DeviceProbe.cs` 的 `d3d9.dll` / `user32.dll`，属 Client 层、在 Z5 的范围内，与本次改动无关；`RandomNumberGenerator` 是 BCL API，不是我们交付的二进制。

---

## 5. 原生资产处置清单

| 对象 | 说明 |
| --- | --- |
| `src/DFLegacy.RandomNative/` 整目录 | **移至 `Client/DFLegacy.RandomNative/` 保留**（修订：不再删除）。8 个文件：`CMakeLists.txt`、`build-native.ps1`、`DFLegacy.RandomNative.vcxproj{,.filters}`、`include/dflegacy_random.h`、`src/dflegacy_random.cpp`、`src/dflegacy_random.def`、`tests/random_native_tests.cpp` |
| `DFLegacy.Emulator.slnx` | ~~移除 `RandomNative` 条目~~ 2026-09-23 修订：条目保留（原生项目回到解决方案，仅 IDE 可见，不参与构建） |
| `DFLegacy.Server.csproj:8,9,17-19,23-28` | `RandomNativeBinary`、`BuildRandomNativeReference`、`ProjectReference`、硬编码 `Content` |
| `GameRandomSource.cs:145-250` | `NativeRdSeed` 与 `NativeMethods`（P/Invoke、探测、异常分类、`DllImportSearchPaths`） |
| `artifacts/native/random/` | 构建产物（已在 `.gitignore` 中，无需提交动作） |

## 6. 需要同步修订的既有设计

删除原生模块会让若干既有设计条目失去对象。**这些不是可选项——留着会让后续实施者照着一份有对象的规则去建一个不存在的组件。**

> **状态：本节已执行**（2026-09 修订：`DFLegacy.RandomNative` 改为保留至 Client 层，上文涉及"删除模块"的表述已随之再次修订，本表"删除原生模块"的处置相应改为"迁移保留"）。涉及**代码**的条目（自测 5 处、`Program.cs` 两处、`GameRandomSource` 本体）已随 M2 实施。

| 文档 | 条目 | 处置 |
| --- | --- | --- |
| [02](02-portability-contracts.md) | R2「原生能力的运行期发现」 | 变为零实例的规则；建议保留为"若将来引入原生资产"的前置约束，或直接删除 |
| [02](02-portability-contracts.md) | R10 第 2 条「自测新增 `Random/` 目录验证原生库缺失不抛异常」 | 改写为覆盖三类随机的边界（含密码学那一类不得被替换） |
| [02](02-portability-contracts.md) | R11 速查表与 `Program.cs` 组合根第 4 步（注册原生解析器） | 删除原生探测步骤 |
| [04](04-migration-plan.md) | **M2「随机源收拢」** | 大幅简化：无 RID 探测、无解析器、无原生内容项；保留"删 4 处编译期耦合"与"自测覆盖" |
| [04](04-migration-plan.md) | **M6「原生可移植」** | **整节删除** |
| [04](04-migration-plan.md) | Q15「`RandomNative` 层级归属」、K8、K10 | 删除（问题与风险都不存在了） |
| [03](03-build-release-ci.md) | §3.0 包内容表两行 native 产物、§3.2 Linux 包布局的 `.so` | 删除 |
| [03](03-build-release-ci.md) | §4.1 原生产物落地约定、§4.2 原生消费方式、§4.4 跨平台化 | §4.2/§4.4 删除；§4.1 的 `random/` 行删除（`ijl15/` 保留） |
| [03](03-build-release-ci.md) | §5.1 CI 里 `build-native.ps1 ... -RunTests`（`rdseed_native`） | 删除该项，只留 Ijl15 |
| [01](01-target-architecture.md) | §4.4 整节、§6.1/6.2/6.3、「一层里的三个项目」相关表述、§8.x 打包表 native 行 | 删除或改写；`Server/` 下只剩 2 个托管项目 |
| [00](00-kickoff.md) | D1/D2 决策、G2 目标、风险摘要、6 个子项目 → 5 个 | 改写 |
| [05](05-randomnative-crossplatform.md) | 全部 | **作废**（本提案取代） |

## 7. 待确认问题

| 编号 | 问题 | 影响 |
| --- | --- | --- |
| **Q1** | **DFLegacy 的客户端是否会从 `ClientRandomSeed` 复算掉落序列？** | 决定要不要移植 `DnfLcg`。若答案为"会"，则当前存在服务端/客户端掉落对齐缺口，本提案范围要扩大（§3.2） |
| Q2 | `GameRandomStatus` 是否可以收缩为一个 provider 字段？ | 会改动 6 处消费点（其中 2 处是自测断言），`admin.js` 已确认无影响 |
| Q3 | 是否要用 `SeededRandomSource` 之外再加一个"进程级可复现模式"（如 `server.json` 配一个全局种子，用于复现线上问题）？ | 参考项目无此机制；本提案默认不加 |
| Q4 | `JsonGameStore.cs:5545` 的邮件 ID 分配是否也收进裁决随机？ | 建议不收（属唯一性分配），但需写入例外清单，避免"单一入口"被无声破坏 |

## 8. 验收标准

| 检查 | 判据 |
| --- | --- |
| 源码无原生痕迹 | `git grep -i "RandomNative" Server/` 无结果（模块保留在 `Client/DFLegacy.RandomNative/`）；`git grep "DllImport" Server/DFLegacy.Server/` 无结果 |
| 构建图 | `DFLegacy.Server.csproj` 的 `ProjectReference` 不含原生项目（`.slnx` 2026-09-23 修订后重新含 `.vcxproj`，不以此为准） |
| 三平台启动 | 服务端在 win-x64 / linux-x64 / osx-arm64 均正常启动，日志 provider 字段为托管实现，无任何原生相关字段 |
| 分布式行为不变 | 42 处调用点的分布抽样行为与改动前一致（掉落/奖励/强化/合成各抽样自测） |
| 密码学未退化 | `git grep "RandomNumberGenerator" Server/DFLegacy.Server/` 仍命中 `Protocol/EntranceProtocolEngine.cs` |
| 包内容 | 任一发布产物中不存在 `runtimes/*/native/*` |
| 自测 | 自测项目在三平台通过，且不再使用 `GameRandomSource` 的注入委托构造器 |
| API | `/api/status` 正常返回；后台界面 `wwwroot/admin/` 正常加载 |

## 9. 落地步骤

1. 先答 §7 · Q1（决定是否需要 `DnfLcg`），并确认 Q2/Q4。
2. 按 §4.3 替换 `GameRandomSource`，新增 `SeededRandomSource`；按 §4.4 收缩状态面并改写 6 处消费点。
3. 按 §5 处置原生资产（模块迁至 `Client/DFLegacy.RandomNative/` 保留）并删除 csproj/slnx 的 4 处耦合。
4. 按 §6 逐条修订 00–04 五份文档，并给 05 标注作废。
5. 按 §8 验收；其中"三平台启动"与"分布式行为不变"两项必须在真实三平台上跑，而不是仅靠代码审阅。
