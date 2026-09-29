# 07 · 协议层重构:收发包管线与分发架构

- 状态:**实施中**(2026-09-29 复审刷新两侧实测、勘误 N5;同日增补 4.8 通用处理管线、4.8.3 命名规范、4.8.4 未实现功能预留组,并按第三轮合理性复审修正三处机制缺陷——Events 通道化、族接管形态、跨会话推送硬规则;架构决议见 §10。同日 R0 与 R1 组 2-3(除 CMD 4)已落地,实施记录与登记偏差见 §11)
- 上级文档:[00 开档总纲](00-kickoff.md) · [01 目标架构](01-target-architecture.md) · [04 迁移路线](04-migration-plan.md)
- 关联文档:[协议枚举全表](../protocol/client-packet-enums-60cn.md) · [× ServerS4A21 对比](../servers4a21/comparison-vs-servers4a21.md)

---

## 1. 背景与动机

### 1.1 现状收发链路

```text
TCP(游戏协议, channel/entrance 双端口)
       └─ EntranceService.HandleClientAsync          ← 单方法约 12,654 行(L167–12820;文件全文 14,832 行,2026-09-29 实测)
       ├─ PacketFrame.ReadAsync                 10B 头 + body,4MB 上限,坏帧抛异常断连     [Protocol/, 保留]
       ├─ GameClientCipherState.TryDecode       CRC 做 oracle 的种子盲试解密(仅 channel 口) [Protocol/, 保留]
       ├─ Task.WhenAny(读帧, Task.Delay) ×4     内联定时器(premium 到期/翻牌截止等)交织,L2345/2373/2413/2460
       ├─ Interlocked 标志回排(5 个)             pendingMailAlarm / pendingPremiumUpdates(ConcurrentQueue) /
       │                                        pendingCeraValue / pendingFatigueReset / pendingWeaknessRecovery
       └─ 69 处具名 `request.ProtocolId` 判等   if/else 链(71 个协议号:69 具名无一复用
                                             + 组队族 CMD 10-14 区间接管)+ ≈216 个局部函数共享约 30 个闭包变量
            ├─ GameProtocolEngine.Create*       纯函数构造回包                             [Protocol/, 保留]
            ├─ frame.WriteAsync(stream) ×343    直写 NetworkStream(单写者靠"都写在同一条异步流里"的纪律)
            └─ 跨会话推送                        邮件提醒/小喇叭(NOTI 130)等经 SessionRegistry 回调,
                                                在调用者线程 `_ = Send…Async(...)` fire-and-forget 直写他连 stream
                                                (EntranceService.cs:2844;全文件 SemaphoreSlim=0 / Channel<T>=0)

UDP(NAT 探测/保活/血量同步)
  └─ GameplayDatagramService + GameDatagramProtocol                                          [保留]
```

### 1.2 三个结构性问题

| 编号 | 问题 | 证据(2026-09-28 实测) |
| --- | --- | --- |
| P1 | **分发不可扩展**:新增协议 = 在巨型方法里找上下文插分支;handler 无法独立测试、无法独立演进 | `HandleClientAsync` 单方法约 12,654 行、69 处具名 `request.ProtocolId` 判等 + 组队族 CMD 10–14 区间接管(合计 71 个协议号)、约 216 个局部函数共享约 30 个闭包变量(小喇叭等新协议仍只能往里加) |
| P2 | **单写者靠纪律**:任何新增后台发送源(公告、活动、跨会话)都必须记得"回排到读循环",绕过即产生并发写竞争 | 343 处 `WriteAsync(stream` 裸直写;SemaphoreSlim/Channel<T> 均为 0。**已现裂缝**:小喇叭(NOTI 130)落地时,跨会话推送走了 `_ = SendMegaphoneNotificationAsync(...)` fire-and-forget 直写他连 stream(L2844)——正是本问题预言的失败模式,幸好当前读循环在该窗口无并发写 |
| P3 | **定时器语义埋在业务里**:`Task.WhenAny(读帧, Task.Delay)` 的交织模式复制了多份,时序行为无法整体推理 | `await Task.WhenAny(` 4 份拷贝(L2345/2373/2413/2460),每处都与 `Task.Delay` 同语句交织;另有多处纯 sleep 型 Task.Delay |

### 1.3 已有资产(本次不动)

- **协议核心层**:`PacketFrame` / `GameClientPayloadCipher` / `GameDatagramProtocol` / `Crc32` / `EntranceCrypto`——现代 span/record 风格、纯函数,质量高,**字节级行为冻结**。
- **领域层**:`Game/{Account,Antibot,Compound,Creature,Drop,Dungeon,Inventory,ItemUpgrade,Party,Quest,Shop}`(11 域 42 文件,Planner/Policy 纯函数)与 `GameWorld/` 25 个目录表——handler 化之后只是薄编排。
- **自测框架**:`DFLegacy.Server.SelfTests`(Check + SmokeTests 模式)与 **PacketDumper 黄金字节库**(`tools/packet_bytes.json`,约 49 条 NOTI + 66 条 CMD 样本,含客户端算法解密回放自检)。
- **可观测性**:`RuntimeState` tracing / 管理页 / `LogPacket`(报文名反查 + 明文字段解码 + 原始字节)。

### 1.4 与 ServerS4A21 的对照结论

采纳(2026-09-29 复审维持,详见 [§10 对照决议](#10-servers4a21-对照2026-09-29-复审)):

| 采纳 | 不采纳 |
| --- | --- |
| 字典注册表分发 + **启动期重复注册即失败**(带来源分组) | 全局锁分帧器(所有客户端串行过一把锁 + 每次到达全量拷贝) |
| Handler / Parser / Builder 三层分文件 | 发包信封恒写 checksum=0 / seq=0(游戏包零完整性校验) |
| 发送侧显式串行化 + 批量发送的部分写语义 | 全局静态 FileLogger(虽有界队列+后台写,仍是隐藏静态依赖) |
| 未注册命令的 hex dump 日志 | 会话对象上堆积散标志位(其 `EnhancedClientSession` 已成反例:堆满 `A21*` 流程标志位与版本协商位) |
| SelfTest 注册表化运行器(`--selftest-xxx` 单跑 + `--selftest-all` 汇总表)(R0 落地) | 巨型 handler 文件(其 PvpRoomHandler 5,288 行/DungeonEntryHandler 4,938 行——分层不自动防巨石,本方案以"单 handler 文件上限"纪律约束,见 4.7) |

**参照点索引**(正文中所有借用 ServerS4A21 设计的位置均以「**参照 S4A21**」标注):

| 位置 | 参照对象(ServerS4A21) | 采纳形态 |
| --- | --- | --- |
| 4.3 会话状态分域 | `PlayerContext` partial 分域 | 同向参照 + 硬约束(字段一一对应、跨域禁入,防其 `EnhancedClientSession` 标志位堆积复现) |
| 4.4 出站管线 | 每会话 `SemaphoreSlim(1,1)` 发送锁 + 批量部分写 | 变体采纳(有界 Channel + 单写者泵,补其缺失的背压语义) |
| 4.5 定时器 | `ClockService`(716 行)"错过只补一次" | 语义基准(R5 时序对拍) |
| 4.6 分发注册表 | `GameCommandRegistry`(78 行):字典注册 + 重复即失败 + 来源分组;未注册 hex dump | 直接采纳(delegate 换接口,叠加报文名反查) |
| 4.7 Handler/Parser/Builder | `Network/{Handlers,Parsers,Builders}` 三层分目录(149/83/182 个文件,2026-09-29 实测) | 直接采纳 + 两项纪律修正(Parser 100% 外置、单文件 ≈800 行上限) |
| 6·R0 测试基建 | `SelfTestRegistry` + `--selftest-xxx` / `--selftest-all` 运行器 | 直接采纳 |
| 6·R1 登录初始化包 | `Builders/Init/InitPacketBuilderRegistry`(50+ body builder 分组注册) | 组织方式参考 |

## 2. 目标与非目标

**目标**

- G1 分发注册表化:协议号 → handler 一对一挂载,启动期校验冲突,未注册命令有日志。
- G2 连接会话状态对象化:`HandleClientAsync` 的局部变量收进 `ChannelSessionState` 分域状态对象,handler 只依赖自己需要的状态域。
- G3 发送通道化:出站统一走有界 `Channel<T>` + 每连接单写者泵,单写者由**机制**保证而非纪律;跨线程事件(邮件提醒、会员到期、点券、疲劳重置)统一变为"入队事件"。
- G4 定时器统一调度:premium 到期、翻牌截止、选择倒计时收进一个每连接调度器,事件与读帧在同一处 `WhenAny` 交织(保留现有时序语义)。
- G5 Parser / Builder 分层:请求解析从业务分支里抽出为纯函数(现有 `TryRead*` 迁移),回包构造继续用 `GameProtocolEngine.Create*`。
- G6 可测试性:每个 handler 可独立构造、独立断言(字节级 golden 测试)。

**非目标**

- N1 不改任何线上字节协议(帧头、加密、字段、时序对客户端可见行为全部不变)。
- N2 不改协议核心层(`Protocol/` 现有 13 个文件)。
- N3 不改领域逻辑行为(`Game/`、`GameWorld/` 只被调用,不被修改)。
- N4 不实现多频道/横向扩容,仅为它留路(每连接独立缓冲、无全局锁的传输层已满足前提)。
- N5 不动入口 AES 信道脚本协议(`EntranceProtocolEngine` 及其在 `EntranceService` 内的对应处理段)。2026-09-29 勘误:原文误引参考项目类名 `ChannelProtocolHandler`,本仓库无此符号。

## 3. 设计原则(不变量)

| 编号 | 不变量 | 说明 |
| --- | --- | --- |
| I1 | 每连接严格 FIFO | 一帧处理完(handler await 返回)才读下一帧——现有语义,客户端依赖它 |
| I2 | 每连接 TCP 单写者 | 由出站泵机制保证;任何代码只允许入队,不允许直写 stream |
| I3 | 坏帧即断连,不静默 | 长度非法/CRC 失败(启用时)/种子无法恢复 → 记日志断连(现状语义) |
| I4 | 协议核心层零改动 | 字节级行为由 golden 测试守护,重构期不得触碰 |
| I5 | 启动失败优于运行期错乱 | 重复注册、缺失依赖在启动装配期抛出(与 04 总纲"不静默"一致) |
| I6 | 搬代码与改行为严格分开 | 每阶段纯搬运 + 测试补齐,行为变更单独提交(沿用 04 执行原则) |

## 4. 目标架构

### 4.1 分层图

```text
┌─ Transport ──────────────────────────────────────────────┐
│ ChannelEndpoint(TcpListener, 每端口一个)                  │
│   └─ Connection(每连接: 读循环泵 + 出站泵 + 调度器)        │
├─ Framing / Cipher(Protocol/, 零改动)────────────────────┤
│   PacketFrame.ReadAsync → GameClientCipherState.TryDecode │
├─ Dispatch ───────────────────────────────────────────────┤
│   GameCommandRegistry: ushort → IGameCommandHandler       │
│   + 通用管线:状态门槛 / 出站计账 / 未注册日志(4.8)     │
├─ Handlers(Network/Handlers/<组>/, 新增,薄编排;大类见 4.8)┤
│   Parser(静态 TryParse → request record)                  │
│   → Game/ 领域函数(不动) → GameWorld/ 目录(不动)        │
│   → GameProtocolEngine.Create*(不动)                     │
├─ Session State ──────────────────────────────────────────┐
│   ChannelSessionState(Login/Roster/Character/Inventory/   │
│   Skill/Quest/Dungeon/Town/Mailbox 分域)                  │
├─ Outbound ───────────────────────────────────────────────┤
│   Channel<OutboundFrame>(有界) → 单写者泵 → stream        │
└───────────────────────────────────────────────────────────┘
```

### 4.2 连接上下文

`ConnectionContext` 是 handler 的唯一入口,替代"巨型方法的闭包世界":

```csharp
public sealed class ConnectionContext
{
    public RuntimeSession Runtime { get; }              // 现有 runtime 追踪/计数
    public IPEndPoint? Remote { get; }
    public bool IsChannelPort { get; }                  // 现场保留 isChannelPort 语义
    public ChannelSessionState State { get; }           // 会话状态(4.3)
    public IOutboundQueue Outbound { get; }             // 发送入队(4.4)
    public IConnectionTimers Timers { get; }            // 定时器注册(4.5)
    public IServiceProvider Services { get; }           // 目录/存储/领域服务(泵层与装配期使用;handler 依赖走构造注入,见 4.7)
    public ILogger Logger { get; }
    public CancellationToken Disconnecting { get; }     // 连接取消令牌(现 connectionCancellation)
}
```

### 4.3 会话状态对象(局部变量 → 分域)

> **参照 S4A21**:分域思路与其 `PlayerContext`(partial:Identity/Dungeon/Position/DeathTower)同向;差异点 = 以"字段一一对应 + 跨域禁入"的硬约束,规避其 `EnhancedClientSession` 上 `A21*` 散标志位堆积的失守形态。

`HandleClientAsync` 的局部变量按域收进 `ChannelSessionState`;**字段一一对应,迁移期不改语义**(附录 A 给出映射样表):

```csharp
public sealed class ChannelSessionState
{
    public LoginState        Login { get; }          // 账号名/uid/是否已认证
    public RosterState       Roster { get; }         // 角色名册缓存
    public CharacterState    Character { get; }      // activeCharacter/lease/战斗投影/城镇/区域/教程
    public InventoryState    Inventory { get; }      // main/avatar/equipped/warehouse/creature 五本字典 + 仓库容量
    public SkillState        Skill { get; }          // currentSkills/currentSp
    public QuestState        Quest { get; }          // currentQuests/completedQuestIds
    public DungeonState      Dungeon { get; }        // currentDungeonId/inDungeonSelection/翻牌截止/怪物会话
    public CurrencyState     Currency { get; }       // gold/cash/victoryPoints
    public MailboxState      Mailbox { get; }        // pendingMailAlarm 等
    public PremiumState      Premium { get; }        // premiumExpiryUnix/pendingPremiumUpdates/pendingCeraValue
    public WeaknessState     Weakness { get; }       // pendingWeaknessRecovery/creatureDeathNotifications
    public Channel<ConnectionEvent> Events { get; }     // 跨线程事件统一入队口:无界 Channel,SingleReader(仅本连接泵读取;4.5 的 WhenAny 依赖其可等待性)
}
```

### 4.4 出站管线(单写者由机制保证)

> **参照 S4A21(变体)**:其每会话 `SemaphoreSlim(1,1)` 发送锁 + 批量发送部分写语义,是"跨会话推送直接入锁即发"的机制化样板;本项目改为有界 Channel + 单写者泵——机制等价,但补上其缺失的背压语义(信号量方案队列无界,内存无上限)。

```csharp
public interface IOutboundQueue
{
    ValueTask SendAsync(GameServerPacket packet, string traceLabel = "", CancellationToken ct = default);
    ValueTask SendBatchAsync(IReadOnlyList<GameServerPacket> packets, CancellationToken ct = default);
}
```

- 每连接一个**有界** `Channel<OutboundFrame>`(容量如 256)+ 一个泵 task:`await channel.Reader.ReadAsync()` → `frame.WriteAsync(stream)` → 计数/trace。
- **背压策略 = 阻塞发送方**:channel 满时 `SendAsync` 等待,等价于现状"读循环被慢写阻塞",不丢包、不乱序。
- 关闭语义:连接取消 → 完成 channel → 泵排空已入队帧 → 关流(消灭"半包")。
- 现有 5 个 Interlocked 标志改为后台服务入队,读循环每帧处理完统一排空——语义与现状一致,但入口唯一。合法入队路径仅两条:纯发送(无会话逻辑)直接入 `Outbound`;需要会话状态参与逻辑的入 `State.Events`,由本连接泵处理后发送。
- **跨会话推送硬规则**:跨会话代码(小喇叭、邮件提醒等)只允许向**目标会话的 `State.Events`** 入队,由目标连接的泵自行执行发送;禁止跨会话直调目标 `Outbound.SendAsync`——有界 channel 满时阻塞的是发送方线程,会把慢客户端的背压传导给无关会话(P2 裂缝的机制化封口)。

### 4.5 定时器与后台事件

> **参照 S4A21**:`Infrastructure/ClockService.cs`(716 行)统一时间队列,语义已对齐旧服 TimerQueue——**错过即补触发一次,不逐次重放**;R5 时序对拍以此为基准。

```csharp
public interface IConnectionTimers
{
    IDisposable Register(TimeSpan due, Func<ConnectionContext, CancellationToken, ValueTask> callback);
}
```

- premium 到期 / 翻牌截止 / 选择倒计时全部改为注册式定时器;到期回调**入队**为 `ConnectionEvent`,与入站帧在同一处交织:

```csharp
var readTask = PacketFrame.ReadAsync(stream, ct: ct);
var eventTask = state.Events.Reader.WaitToReadAsync(...).AsTask();
var completed = await Task.WhenAny(readTask, eventTask);   // 与现状 WhenAny 语义一致,但只有这一处
```

- 现状"多个 else-if 各自 WhenAny"的时序行为收敛为单一调度点;单调时钟,不受系统时间跳变影响。

### 4.6 分发注册表

> **参照 S4A21(直接采纳)**:`Network/Protocol/GameCommandRegistry.cs`(78 行)——`Dictionary<ushort, delegate>` 注册、启动期重复注册抛异常(带来源组名)、未注册命令 hex dump;其约 278 条 CMD 注册规模已验证该形态。本方案两处增强:delegate 换 `IGameCommandHandler` 接口(类型化依赖 + 可独立构造),hex dump 叠加报文名反查(`PacketTypeNames`,359 个协议名,NOTI 175 + CMD 184)。

```csharp
public interface IGameCommandHandler
{
    ushort ProtocolId { get; }
    string Name { get; }   // 诊断用,如 "MAILBOX_OPEN"
    CommandPhase RequiredPhase { get; }     // 状态门槛(4.8.1),默认 CharacterSelected
    bool RequiresChannelPort { get; }       // 端口约束(4.8.1),默认 false
    ValueTask HandleAsync(ConnectionContext ctx, PacketFrame frame, CancellationToken ct);
}

public sealed class GameCommandRegistry
{
    public void Register(string group, IGameCommandHandler handler);  // 重复 → 启动失败(带 组名/协议/名称)
    public bool TryGet(ushort protocolId, out IGameCommandHandler handler);
    public IReadOnlyCollection<ushort> Registered { get; }            // 启动时打印清单
}
```

- 未注册命令:记 WARN + hex dump(沿用现状"Unhandled"日志,但补协议名反查,现 `PacketTypeNames` 已具备)。
- CRC/种子失败、坏帧的处理留在泵层,不进 handler(与现状一致)。
- 同号多义约定:若未来出现同一协议号需按请求体分流多种语义(参考项目现存此形态:REQUEST_PEER 经请求体嗅探分流交易/组队),仍注册**单个** handler 在其内部嗅探后委派子处理;禁止为此回到 if/else 巨链,亦不支持同号多 handler 注册。本项目 REQUEST_PEER(CMD 10)仅组队语义,当前 69 个具名协议号均一对一;组队族 CMD 10–14 为族接管(下条),即"族内分派"的现存样例(见 5·组 15)。
- 族接管形态:协议号相邻且解析同族(共用前缀/序号)的命令,按"一个共享 handler 核心 + 每协议号一个薄注册壳"落地——如组 15:5 个壳类各持 `ProtocolId`,委派同一 `PartyHandlerCore`。注册表仍按单号键独占、重复注册检测不变;"一对一挂载"约束的是注册表键位,不禁止壳共享核心。

### 4.7 Handler / Parser / Builder 约定

> **参照 S4A21(直接采纳 + 纪律修正)**:三层分目录组织来自其 `Network/{Handlers,Parsers,Builders}`(149/83/182 个文件,2026-09-29 实测);两处修正针对其反例——Parser 必须 100% 外置(其 mailbox 等复杂解析内联在 handler 里,复用与独立测试打折),单文件 ≈800 行上限(见本节末尾)。

```csharp
// Parser:纯函数,现 EntranceService.TryRead* 原样迁移
public static class MailboxParsers
{
    public static bool TryParseOpen(PacketFrame frame) => frame.Body.Length >= 3;
    public static bool TryParseExtract(PacketFrame frame, out uint mailId) { ... }
}

// Handler:薄编排——解析 → 调 Game/ 领域函数 → 入队回包
public sealed class MailboxOpenHandler(...) : IGameCommandHandler
{
    public ushort ProtocolId => GameProtocolEngine.MailboxOpenCommand;
    public async ValueTask HandleAsync(ConnectionContext ctx, PacketFrame frame, CancellationToken ct)
    {
        if (!MailboxParsers.TryParseOpen(frame)) { await ctx.Outbound.SendAsync(...error...); return; }
        var snapshot = MailboxProjector.BuildSnapshot(ctx.State.Mailbox, ctx.State.Character);
        await ctx.Outbound.SendAsync(GameProtocolEngine.CreateMailboxSnapshot(snapshot));
    }
}
```

- `GameProtocolEngine.Create*` 全部保留原样(纯函数、已被 golden 测试覆盖)。
- handler 禁止持有跨连接状态;需要共享的只允许注入只读目录/存储服务。
- **单文件上限纪律**:handler/局部 coordinator 文件超过约 800 行必须按协议或子流程拆分(partial 或子类型)。ServerS4A21 的教训:分层本身不防巨石,其 PvpRoomHandler(5,288 行)、DungeonEntryHandler(4,938 行)就是"handler 化之后再巨石化"的样例;本方案以 15 组为上限粒度、超限即拆,不允许组内再长成单文件。

### 4.8 通用协议处理与功能大类划分

> 2026-09-29 增补:把所有命令共享的通用处理从业务分支中收口为固定管线(4.8.1),handler 按"大类 → 组"两级归属(4.8.2)。分发判等同日全口径重测:69 处具名判等 + 组队族 CMD 10–14 区间接管,合计 71 个协议号(证据见 1.2)。

#### 4.8.1 通用处理管线(handler 不再重复实现的部分)

| 段 | 内容 | 落点 |
| --- | --- | --- |
| U1 帧与解密 | 读帧、CRC/种子解密、坏帧断连 | 泵层(`Protocol/` 冻结,N2) |
| U2 分发 | 注册表查表;同号多义与族内分派按 4.6 约定 | `GameCommandRegistry` |
| U3 状态门槛 | handler 声明所需会话阶段与端口,分发器统一校验 | 分发器前置(下述) |
| U4 出站计账 | 发送、SentPackets 计数、LogPacket 收进单点 | `IOutboundQueue.SendAsync`(4.4) |
| U5 未注册路径 | WARN + hex dump + 协议名反查 | 4.6 |

U3 的门槛模型(接口成员已并入 4.6 的 `IGameCommandHandler`):

```csharp
public enum CommandPhase { Any, Authenticated, CharacterSelected, InDungeon }
```

- 语义红线:门槛只收口**现有分支内已存在的阶段检查**(如 `activeCharacter is null` 即拒答),逐协议按现状登记,不新造拒绝路径;请求体长度校验属 Parser 职责(4.7),不入门槛。
- 门槛不合格的行为按各协议现状登记(静默忽略或错误回包),随迁移表逐条记录并纳入 golden 断言(见 7)。
- 零状态协议(CHECK_CONNECTION 0、EXIT 3、ANTIBOT 161 静默消费报告)声明 `Any`,是门槛框架的最小样例,恰为 R0 试点对象。
- 门槛只做粗粒度会话阶段;更细的场景门槛(仅城镇、副本内子状态等)留在 handler 内部判断,不扩 `CommandPhase` 枚举。

#### 4.8.2 功能大类:两级归属(大类 → 组 → handler)

15 个组归入 6 个功能大类;**大类用于注册分组、启动清单与测试选择,组仍是目录与迁移单元**:

| 大类 | 组(§5 编号,定名见 4.8.3) | 主状态域(4.3) | 主导阶段 |
| --- | --- | --- | --- |
| session 会话与账号 | 1 Connection、2 Login | Login | 未认证可达 |
| progression 角色与养成 | 3 Characters、4 Stamina、8 Skill、7 Quest | Roster/Character/Skill/Quest/Weakness | 已认证起 |
| social 社交与组队 | 5 Chat、6 Mailbox、15 Party | Mailbox/Character + 跨会话推送 | 已选角起 |
| economy 物品与经济 | 9 Inventory、10 Shop、11 Craft | Inventory/Currency | 已选角(非副本) |
| world 城镇与玩伴 | 12 Pets、14 Town | Inventory(creature)/Character | 已选角(城镇) |
| combat 副本与战斗 | 13 Dungeon | Dungeon/Character/Quest/Inventory | 副本生命周期 |

划分依据(新增协议按序判定归属):

1. 会话阶段相同(U3 门槛一致)者优先同大类;
2. 主状态域尽量不相交;跨域协议按"回包主导方"归属(任务触发物品变动,但推进与回包以 Quest 为主,归 Quest 组);
3. 与 `Game/` 领域目录、`GameProtocolEngine.Create*` 家族前缀对齐,保持一套词汇。

形态约定:

- 目录维持一层 `Network/Handlers/<组>/`(15 个,PascalCase);大类**不增设目录层级**,以注册组名两级化呈现:`Register("social.mailbox", handler)`——启动清单按大类分组打印,SelfTests 选择器按大类聚合(如 `--protocol-golden social`)。
- 查无可用组且确属新功能域时才增设组;新增组必须同步本表与 §5。

#### 4.8.3 命名规范(组名对齐 ServerS4A21)

> 2026-09-29 增补:功能文件夹与注册组名**尽量沿用参考项目既有词汇**(其 28 个注册组小写 kebab-case,如 `equipment-item-lock`;Handlers/Parsers/Builders 功能目录 PascalCase 且同名镜像,如 Pets、CeraShop、ExpertJob),无对应者自拟并登记偏差,便于两项目交叉查阅。

| # | 目录(PascalCase) | 注册组名 | S4A21 对应 | 偏差说明 |
| --- | --- | --- | --- | --- |
| 1 | Connection | session.connection | — | 其连接级处理位于 `Network/Core`,无 handler 组;自拟 |
| 2 | Login | session.login | 组 `login`;Builders/Login | 直接对齐 |
| 3 | Characters | progression.character | 组 `character`;Builders/Characters | 对齐,目录取其复数形态 |
| 4 | Stamina | progression.stamina | — | 其疲劳/会员走 Service + DB,无组;自拟 |
| 5 | Chat | social.chat | 组 `party-chat`(其合并组) | 我方拆分独立;词汇循其 ChatHandler |
| 6 | Mailbox | social.mailbox | 组 `mailbox` | 直接对齐 |
| 7 | Quest | progression.quest | 组 `quest`;Builders/Quest | 直接对齐 |
| 8 | Skill | progression.skill | 组 `skill` | 直接对齐 |
| 9 | Inventory | economy.inventory | 组 `inventory`;Builders/Inventory | 对齐,去原 `-core` 后缀 |
| 10 | Shop | economy.shop | Builders/CeraShop(子域) | 我组含买卖修与点券,宽于其 CeraShop;Cera 相关 builder 可循其词汇 |
| 11 | Craft | economy.craft | —(其抽奖在 Builders/Lottery) | 合成/强化/分解/抽奖无整组对应;自拟 |
| 12 | Pets | world.pets | 组 `pet`;Handlers/Pets | 对齐;60A1 报文与领域层用 Creature 词汇,仅组织目录取 Pets |
| 13 | Dungeon | combat.dungeon | 组 `dungeon`;Handlers/Dungeon | 直接对齐 |
| 14 | Town | world.town | 组 `town`;Builders/Town | 直接对齐 |
| 15 | Party | social.party | 组 `party-chat`(其合并组);Builders/Party | 目录对齐其 Party;注册层拆分 |

命名规则(新增组一律遵守):

1. **来源优先级**:S4A21 已有对应者直接沿用;无对应者自拟单词并登记本表;与 60A1 既有词汇冲突时,代码符号从客户端报文词汇(如 `Creature*` 常量),组织目录从本表。
2. **目录名**:PascalCase,复合词直接拼接(循其 `CeraShop`/`ExpertJob` 形态,不用连字符);Handlers/Parsers/Builders 三层同名镜像。
3. **注册组名**:小写,复合词 kebab-case(循其 `equipment-item-lock` 形态);两级 `<大类>.<组>`,大类前缀(session/progression/social/economy/world/combat)小写单词,仅用于注册与测试选择器,不落目录。
4. **类型名**:Handler 按 `<协议名>Handler`(如 `MailboxOpenHandler`);Parser 按 `<组>Parsers`(如 `MailboxParsers`);Builder 在 R7 拆分后按 `<组>PacketBuilders`(循其 `*BodyBuilder` 词汇)。
5. **协议号常量与枚举分向**:CMD 侧维持 `*Command` 后缀、NOTI 侧维持 `*Notification` 后缀(如 `RenameCreatureCommand` / `StaminaNotification`)——两向共用 0–255 号段且同号不同义(CMD 10 组队邀请、NOTI 10 离队播报),名称必须自带方向。R7 拆分或后续引入协议号枚举时,按方向分表并冠 `Cmd` / `Noti`(如 `CmdPacketType` / `NotiPacketType`,循参考项目 `PacketTypes` 词汇),禁止同表混装两个方向;帧类型标记常量(`CommandPacketType` / `NotificationPacketType`)是帧 Type 字节值,语义不同,维持现状命名。

#### 4.8.4 预留组(未实现功能的迁移占位)

> 2026-09-29 增补:按 README「已知缺口」为尚未实现的功能预留组位、命名与归属,使新功能落地时直接入位、不再触动既有分组。占位**只登记组织层信息,不预写 handler、不猜测协议字节**;实现一律遵循 AGENTS.md 的 D2(功能文档先行、确认后落地)与 D1(IDA + 抓包验证),参考项目结论仅作线索(D3)。

| # | 目录(预留) | 注册组名 | 大类 | S4A21 对应 | 范围与约束 |
| --- | --- | --- | --- | --- | --- |
| 16 | Trade | social.trade | social | 其 Trade handler 族(交易经 REQUEST_PEER 请求体嗅探与组队分流) | 玩家间物品/金币交易与双方确认。现状仅 NPC 侧 ITEMTRADE_STATE 26(组 10 Shop);本项目 REQUEST_PEER(CMD 10)仅组队语义(协议分册已验证),交易协议号与布局须实现时 IDA 逐 case 验证,不可套用其嗅探分流结论 |
| 17 | PersonalShop | economy.personal-shop | economy | —(无摆摊对应;近亲 Builders/Auction) | 摆摊、商品展示与购买流程;无对应,自拟命名登记 |
| 18 | Friends | social.friends | social | 注册组 `friend`;Builders/Friends、Parsers/Friends;其独立 BlacklistHandler | 好友与黑名单;黑名单并入本组(其独立成 handler,我方按状态域合并,登记偏差) |
| 19 | Guilds | social.guilds | social | 注册组 `guild-{members,join,creation,management}`;Builders/Guilds | 完整公会系统;战场、公会战等延伸玩法后续按需另立组(其 `event-joust` 可作词汇参照) |
| 20 | Pvp | combat.pvp | combat | Handlers/Pvp、Builders/Pvp;注册组 `misc-pvp`;PvpRoomHandler 5,288 行(巨石反例,见 4.7/10.2) | 决斗场房间/准备/对战/结算;决斗经验、等级、记录同步已有(现状散于既有分支,迁移时归入本组);落地必须按 4.7 单文件上限拆分,禁止复刻其巨石形态 |

预留规则:

- 组 15 Party 已有雏形(`Game/Party/`、`Network/PartyCoordinator.cs`,格式分册 `docs/protocol/packet-detail-party-60cn.md`);「多人进同一副本的协同编队」为组 15×组 13 的联合遗留项,实现时先补协议分册与功能文档,再动代码。
- 预留组启用时同步 4.8.2 大类表与 §5 分组表,README「已知缺口」对应条目随之收敛。
- 数据持久化缺口(JSON → SQLite)不在本档范围,由 08 号文档承载。

## 5. 分组映射(71 协议号 → 15 个 handler 组;大类归属见 4.8.2,组名定名见 4.8.3)

| # | 组(Network/Handlers/<组>/) | 协议(CMD, 60CN 号) | 现状行段(EntranceService.cs) | 触及状态域 |
| --- | --- | --- | --- | --- |
| 1 | Connection | CHECK_CONNECTION 0 / EXIT 3 / ANTIBOT 161(静默消费报告) / CHANNELINFO 推送 | 409-418, 429, 2967-2978, 12678 | Login |
| 2 | Login | LOGIN 1 / SET_UDP_IP_PORT 2 | 2481-2541 | Login, Premium |
| 3 | Characters | GET_USERINFO 8 / SELECT 4 / CREATE 5 / DELETE 6 / RETURN 7 | 2543-2684, 12173-12234 | Roster, Character, Inventory |
| 4 | Stamina | RECOVER_STAMINA 9 + NOTI 4/33 推送 | 9919-9981, 870-909 | Weakness, Currency |
| 5 | Chat | SEND_MESSAGE 17(含 type 13-15 小喇叭 → NOTI 130)/ CREATURE_SEND 124 / CREATURE_SCRIPT 132 | 3331-3477, 2768 附近(喇叭接线) | Character |
| 6 | Mailbox | OPEN 99 / EXTRACT 98 / SEND 97 / CHANGE_LETTER_STAT 144 + ALARM | 3479-3897, 14018-14133 | Mailbox, Currency |
| 7 | Quest | ACCEPT 33 / GIVEUP 34 / TRIGGER 35 / FINISH 36 | 3898-4740, 14018- | Quest, Inventory |
| 8 | Skill | CHANGE_SKILLSLOT 30 / BUY_SKILL 31 / INCREASE_STATUS 32(状态点分配) | 5438-5640 | Skill, Currency |
| 9 | Inventory | DELETE 18 / MOVE 19 / SORT 20 / USE_STACKABLE 47 / DECREASE_DUR 51 | 5760-5855, 6062-7194, 7196-8004, 9738-9820 | Inventory |
| 10 | Shop | BUY 21 / SELL 24 / REPAIR 25 / CERA 67 / GEN_CERATICKET 68 / ITEMTRADE_STATE 26 | 8006-8942, 9483-9568 | Inventory, Currency, Mailbox |
| 11 | Craft | COMPOUND 27 / DISJOINT 28 / LOTTERY 29 / UPGRADE 83 / RESET 84 / COMPOUND_AVATAR 102 / GET 46 / DROP 50 | 4740-5170, 9112-9481, 11242-11620 | Inventory, Dungeon |
| 12 | Pets | HATCH 105 / RENAME 103 / RESPONSE 104 + NOTI 100-107 | 5857-6062, 697, 7514 | Inventory(creature), Weakness |
| 13 | Dungeon | START_GAME 15 / SELECT_DUNGEON 16 / FINISH_LOADING 40 / MOVE_MAP 48 / DIE_MONSTER 42 / ALLDIE 148 / DIE_CHAR 43 / USE_COIN 44 / GIVEUP 45 / BACK_2_VILLAGE 142 / PLAY_RESULT 49 / 计分卷/翻牌 72-74 / EPLP 75(结算) / BOSS_DIE 127 / TUTORIAL 153 / USE_SKILL 41 | 9822 端-12171 区间多个段 | Dungeon, Character, Quest, Inventory |
| 14 | Town | SET_USER_POSITION 37 / SET_USER_AREA 38 / CALL_MEMER_LIST 82 / EXCHANGE 130 / EVENT_INFO 推送 / USERINFO 推送 | 9822-9917, 11242-, 916, 482-532 | Character, Town |
| 15 | Party | 组队族 CMD 10–14(REQUEST_PEER 10 / RESPONSE_PEER 11 / SET_PARTY_INFO 12 / LEAVE_PARTY 13 / WALKOUT_PARTY_MEMBER 14),按 4.6 族接管形态落地(单核心多壳);body 前置两字节序号,settings 子族(12/13/14)核心内分派;格式分册 `docs/protocol/packet-detail-party-60cn.md`,实现散于区间块与 `Network/PartyCoordinator.cs` | 10404 起区间块 | Character + 跨会话 SessionRegistry |

> 行段为 2026-09-28 时点的参考位置,其后文件又增约 370 行、整体下移,以 `GameProtocolEngine.*Command` 常量检索为准。
> 未实现功能的预留组(Trade / PersonalShop / Friends / Guilds / Pvp,组 16–20)见 4.8.4。

## 6. 迁移计划(strangler,每阶段独立 PR/可发布/可回滚)

| 阶段 | 内容 | 退出标准 | 风险 |
| --- | --- | --- | --- |
| **R0** | 基础设施:Registry / ConnectionContext / OutboundHub / TimerScheduler + 通用管线骨架(4.8.1 状态门槛校验器 + 出站计账收口)+ 试点协议(EXIT、CHECK_CONNECTION、ANTIBOT,零状态门槛样例);golden 测试框架落地(SelfTests/Protocol/,直接消费 PacketDumper 的 `packet_bytes.json` 样本库);SelfTests 入口注册表化(参照 S4A21 `--selftest-*` 运行器,拆分 9,647 行 Program.cs;与试点协议解耦,可并行推进) | 试点协议走新链路,真机登录无感;golden 断言齐 | 低 |
| **R1** | 握手 + 登录 + 角色(组 1-3,含 NOTI 2 全部子类型与顶号);登录初始化包**参照 S4A21** `InitPacketBuilderRegistry` 形态分组注册(其 50+ body builder) | 真机:登录→选角→建/删角→返回选角 全流程字节一致 | **高**(ACK/名册布局最复杂) |
| **R2** | 邮箱 + 聊天 + 任务 + 技能(组 5-8) | 真机:聊天回显/邮件全流程/任务接交/学技能 | 中(状态域简单,先行验证搬运模式) |
| **R3** | 物品三组(组 9-11,分支最多) | 真机:买/卖/修/整理/移动/删除/使用/合成/强化/分解/抽奖/拾取/丢弃 | 中(纯搬运量大) |
| **R4** | 宠物 + 城镇/系统 + 组队(组 12、14、15) | 真机:孵化/改名/召唤、进城位移/切区/访客列表、建队/邀请/应答/退队/踢人全流程 | 中 |
| **R5** | 副本/战斗(组 13;定时器最密集) | 真机:进图→清怪→结算→翻牌→回城 全程;翻牌截止/premium 到期时序与旧版对拍 | **高**(定时器等价性) |
| **R6** | 清退:删除全部旧分支与 5 个 Interlocked 标志,`HandleClientAsync` 收缩为"读帧→解密→分发"泵;疲劳/虚弱后台服务改事件入队 | `EntranceService.cs` < 1,500 行;全量真机回归 | 低 |
| **R7**(可选) | `GameProtocolEngine.cs`(3,754 行)按域拆分 builder 文件 | 纯文件搬移,字节零变化 | 低 |

执行原则(沿用 04):**同一协议的新旧实现不同时挂载**——注册表按协议号独占,切换即该协议完全走新链路;搬代码与改行为严格分开;每阶段合并后 `main` 可发布。

## 7. 测试与验收

1. **Golden bytes(核心手段)**:每协议准备 `请求帧样例 → 期望回包字节序列` 断言,样本来源:runtime tracing 现网抓取 + 手工构造;落 `SelfTests/Protocol/<组>GoldenTests.cs`,复用现有 `Check` 模式。R0 起随每阶段补齐,**旧实现产出的字节就是黄金样本**(先记录后切换)。
2. **密码学回环**:移植 `tools/crypto_roundtrip_test.py` 的断言到 SelfTests(种子收敛、CRC、NOTI ROR(x^0xB5,6) 互逆)。
3. **不变量测试**:FIFO 顺序、并发入站出站的单写者、坏帧断连、CRC 拒绝、种子恢复失败路径;状态门槛拒绝路径(未登录发聊天、无角色操作背包等,按 4.8.1 迁移登记表逐协议断言)。
4. **真机冒烟清单**(每阶段必跑):登录 → 选角 → 进城 → 聊天 → 买卖/修理 → 接交任务 → 学技能 → 进副本全程 → 结算翻牌 → 回城 → 收发邮件。
5. **时序对拍**(R5):premium 到期、翻牌截止在重构前后的触发时刻误差 < 1 个读帧周期。

## 8. 风险与缓解

| 风险 | 缓解 |
| --- | --- |
| 字节级行为回归 | golden 先行(旧实现产出即标准);I4 协议核心零改动 |
| 闭包局部函数的隐蔽耦合(约 30 个局部函数共享局部变量) | 以**状态域为单位整体搬运**,禁止半搬;编译器兜底(局部函数引用未迁移变量会报错) |
| 定时器时序漂移 | 单一调度点保留 WhenAny 语义;R5 时序对拍 |
| 迁移期新旧并存的行为分叉 | 注册表按协议独占挂载,无灰度并存 |
| 出站背压处理不当丢包 | 有界 channel 满即阻塞发送方(等价现状),禁止丢弃策略 |
| 慢客户端致 `Events` 无界堆积(泵阻塞在出站背压,事件排空停滞) | Events 无界不丢事件;堆积计数告警,由现有空闲断连/读超时兜底清理连接 |
| 工作量失控 | 8 阶段各自可停;R1/R5 两个高风险阶段单独排期 |

## 9. 工作量粗估(单人)

| 阶段 | 估算 |
| --- | --- |
| R0 基础设施 + golden 框架 | 4-6 人日 |
| R1 登录/角色 | 5-8 人日 |
| R2 邮箱/聊天/任务/技能 | 4-6 人日 |
| R3 物品三组 | 6-9 人日 |
| R4 宠物/城镇/组队 | 4-6 人日 |
| R5 副本/战斗 | 6-9 人日 |
| R6 清退 | 2-3 人日 |
| R7(可选)engine 拆分 | 2-3 人日 |
| **合计** | **约 31-50 人日** |

## 10. ServerS4A21 对照(2026-09-29 复审)

> 本节数据 2026-09-29 按两侧工作区现状重新实测(命令统计,非目测),取代 2026-09-28 首轮实测。上轮以来本项目侧因 FIX-01/FIX-02(整理物品方案回滚)与 DAF-MCP 抓包网关落地(ADD-05)发生代码漂移;参考项目侧 Builders/Parsers 有文件合并(220/86 → 182/83,聚合出多 builder 文件),文件组织仍在演进,引用数字以本节当期实测为准。包级字段差异见[对比文档](../servers4a21/comparison-vs-servers4a21.md),功能覆盖差距见[功能回填计划](../servers4a21/feature-backfill-plan-vs-s4a21.md)。

### 10.1 规模与协议处理形态对照(实测)

| 维度 | DFLegacy(60A1) | ServerS4A21(A21) |
| --- | --- | --- |
| 代码规模 | 123 个 .cs / 64,098 行(服务端+自测,不含 09-29 新增 Mcp 网关项目) | 1,184 个 .cs / 316,401 行(≈4.9×) |
| 协议覆盖 | 71 个协议号(69 处具名判等无一复用 + 组队族 CMD 10–14 区间接管),全部有字节级 golden 对照 | 约 278 条 CMD 注册、28 个注册组(30 次注册调用);CMD/NOTI 枚举全量表(CMD 1272 + NOTI 1219) |
| 请求分发 | `HandleClientAsync` 约 12,654 行内 if/else 巨链 | `Dictionary<ushort, delegate>` 注册表(GameCommandRegistry 78 行)+ GameProtocolHandler 1,064 行;重复注册启动期抛异常(带来源组名) |
| 分帧 | 每连接独立缓冲(PacketFrame,10B 头,4MB 上限) | FlexiblePacketProcessor:**全局一把锁**切帧,1MB 帧上限 / 2MB 连接缓冲 |
| 完整性/加密 | CRC32 + 种子盲试解密(channel 口),真校验 | 14B/15B 头明文,checksum/seq 恒写 0 不校验;AES 仅频道协议且密钥=日期串、明文入日志 |
| 会话状态 | 巨型方法局部变量(≈30 个闭包变量 + ≈216 个局部函数) | `EnhancedClientSession` + `PlayerContext`(partial 分域)+ SessionDirectory(CAS 顶号踢旧);但会话对象堆积 `A21*` 流程标志位与版本协商位 |
| 顶号/生命周期 | CharacterSessionRegistry.ClaimAsync 踢旧并**等待其释放** | `CanDispatch` 分发门槛拒收 stale session 包 |
| 出站写入 | 343 处裸直写 stream,零机制(SemaphoreSlim/Channel 均为 0) | 每会话 `SemaphoreSlim(1,1)` 串行 + 批量发送部分写语义 |
| 定时器 | 读循环内 WhenAny+Delay ×4 份拷贝 | ClockService 统一时间队列(716 行,对齐旧服语义:错过只补一次);premium 走 DB expire 字段不占 timer |
| 后台事件 | Interlocked 局部标志 + ConcurrentQueue → 读循环顶部排空(推送时延受客户端流量驱动) | 回调里直接 `SendPacketAsync`(过会话信号量),无需回排 |
| 领域层 | 纯函数 Planner/Policy(42 文件)+ 25 个 Catalog(12,451 行) | 有状态 Service + SQLite Repository 与静态 Policy 并存(Game/ 148k 行) |
| 装配 | Program.cs `AddHostedService` + ctor 注入 | 手写组合根 ServerRuntimeBuilder(1,000 行)+ 8 个纯参数容器,无 DI 容器 |
| 测试 | 20 文件 13,686 行;Program.cs 单文件 9,647 行 | 75 文件 45,609 行;SelfTestRegistry + `--selftest-xxx` 单跑 / `--selftest-all` 汇总表 |
| 可观测 | LogPacket:报文名反查 + 明文解码 + 原始字节(运行态);PacketDumper 黄金字节库 | FileLogger 全局静态(有界队列 + 后台写);PacketFileLogger 可选逐包 hex(默认关) |
| UDP | GameplayDatagram 双端口玩法同步 + ProbeService 被动收集 | PartyUdpRelay TURN 式中继(默认关,env 门控);HP% 实际走 TCP(0x00A6) |

### 10.2 架构优劣分析(协议处理视角)

**DFLegacy 相对优势(重构中必须保住的资产)**

1. **完整性与加密是真做**:CRC 做 oracle 的种子解密、字节级 golden(PacketDumper + 回放自检)。S4A21 全链路零完整性校验,加密形同虚设——同为私服,60CN 客户端强制校验是硬约束,S4A21 形态不可迁移。
2. **分帧无全局锁**:每连接独立缓冲天然多核扩展;S4A21 所有连接串行过一把锁 + 每次到达全量拷贝,连接数一多即瓶颈。
3. **领域层纯函数**:Planner/Policy 无隐藏状态,handler 化后单测只需值类型输入;S4A21 有状态 Service 与 SQLite 同层,单测要搭 repo。
4. **运行态可观测更强**:报文名反查 + 明文字段解码在日志里直接可读;S4A21 只有 hex(逐包解码需另开 capture 工具)。

**DFLegacy 相对劣势(即本重构动机,2026-09 全部复确认)**

1. P1/P2/P3(见 1.2)精确成立,且 P2 已出现现实裂缝(小喇叭 fire-and-forget 直写)。
2. 后台事件回排模式比 S4A21 的"直接过发送锁"多一跳,推送时延被客户端流量驱动(下一个包到达才排空,deadline-read 兜底)——G3/G4 的 Channel 化消除该时延差异。
3. 测试基建弱:9,647 行单文件入口 vs S4A21 的注册表运行器,选跑/汇总能力缺失。

**ServerS4A21 的可取之处**

1. **注册表分发在 278 协议规模下成立**:字典 + 启动期重复检测 + 来源分组,是本方案 G1 的规模验证(约 71 协议远未到该模式上限)。
2. 会话状态对象化方向正确(PlayerContext partial 分域、SessionDirectory CAS 顶号),只是纪律失守(散标志位回潮)——印证 G2 需要"分域 + 禁止跨域字段"的硬约束而非仅约定。
3. 出站每会话信号量是**机制**而非纪律,跨会话推送直接入锁即发——这是它对本项目最大的单项领先。
4. ClockService 统一时间队列(错过只补一次,对齐旧服 TimerQueue)为 G4 提供了现成的语义基准。
5. SelfTest 注册表运行器(单跑/全跑/汇总表)是测试基建的成熟形态。

**ServerS4A21 的结构性弱点(不采纳清单的事实依据)**

1. 全局锁分帧器 + 全量拷贝:形态性缺陷,不可修补只能绕开。
2. 零完整性校验(checksum/seq 恒 0)、频道密钥=日期串明文入日志:安全/健壮性倒挂。
3. **分层 ≠ 防巨石**:handler 化之后照样长出 PvpRoomHandler 5,288 行、DungeonEntryHandler 4,938 行;Parser 覆盖不全(83 parser 文件 vs 149 handler 文件),复杂解析内联回 handler(如 mailbox),复用与独立测试打折——G5 要求 Parser 100% 外置即为针对此。
4. 会话对象垃圾抽屉化:`A21*` 流程标志位、版本协商位直接堆在 EnhancedClientSession 上,新功能继续往上挂——"对象化"不等于"分域"。
5. 流程内 `Task.Delay` 充当延时(raid 奖励等):进程重启即丢,无调度器兜底。

### 10.3 决议(更新版)

维持原决议(2025 初版 → 2026-09 复核不变):

| 项 | 决议 | 理由 |
| --- | --- | --- |
| 字典注册表 + 启动期重复检测 + 来源分组 | **采纳**(4.6) | 消灭 if/else 巨链;冲突在启动期暴露;278 协议规模已验证 |
| Handler/Parser/Builder 分文件 | **采纳**(4.7) | 领域层已就绪,handler 只是薄编排 |
| 发送侧串行化 + 批量部分写语义 | **变体采纳**(4.4):有界 Channel + 单写者泵 | 比信号量更贴合"每连接单写者"现状,背压语义明确(信号量方案队列无界,内存无上限) |
| 全局锁分帧器 + 全量拷贝缓冲 | **不采纳** | 保持每连接独立缓冲、无全局锁 |
| checksum/seq 恒写 0、无完整性校验 | **不采纳** | 60CN 客户端校验 CRC 与种子,必须保留(协议核心层冻结) |
| 未注册命令 hex dump 日志 | **采纳**(4.6) | 附带报文名反查(359 个协议名) |
| 会话对象散标志位 | **不采纳** | G2 分域状态对象 + 跨域字段禁入;S4A21 反例已写入 1.4 |

本轮新增/修正决议:

| 项 | 决议 | 理由 |
| --- | --- | --- |
| SelfTest 注册表化运行器 | **采纳**(R0) | `--protocol-golden` 等按组单跑 + 汇总表;顺带拆分 9,647 行 Program.cs 入口 |
| 单 handler 文件上限纪律(≈800 行,超限按协议/子流程拆) | **新增**(4.7) | S4A21 证明"分层不自动防巨石"(PvpRoomHandler 5,288 行) |
| ClockService"错过只补一次"周期语义 | **采纳为 R5 对拍基准**(4.5) | 已对齐旧服 TimerQueue,避免重造语义 |
| `CanDispatch` stale-session 分发门槛 | **不采纳(已覆盖)** | 本项目 ClaimAsync 踢旧并等待释放,新登录与旧连接无并发窗口,无迟到包问题 |
| 登录初始化包分组注册(InitPacketBuilderRegistry 形态) | **参考**(R1) | S4A21 用 50+ 个 body builder + 注册表组织登录初始化包,R1(最复杂阶段)可沿用该组织方式 |
| 全局静态 FileLogger 批评 | **修正** | 其已升级有界队列 + 后台写,"逐包同步落盘"不成立;维持不采纳的理由降级为"全局静态隐藏依赖",逐包抓包(默认关)思路与现有 RuntimeState tracing 重合,不另引入 |
| 同号多义分发约定(单 handler 内嗅探委派) | **新增**(4.6) | 参考项目 REQUEST_PEER 按请求体分流交易/组队,为同号多义现存样例;约定不回到 if/else,亦不支持同号多 handler。本项目 REQUEST_PEER(CMD 10)仅组队语义,69 个具名协议号一对一;组队族 10–14 为单 handler 区间接管的现存样例 |
| 通用处理管线(U3 状态门槛声明式收口 + U4 出站计账单点) | **新增**(4.8.1) | 343 处"写+计数+日志"三连与散落阶段检查是 P1/P2 的日常形态;门槛语义按现状逐协议登记,不新造拒绝行为 |
| 功能大类两级划分(6 大类 15 组;注册组名两级、目录一层) | **新增**(4.8.2) | 大类服务启动清单与测试选择,组服务编译与迁移单元;避免参考项目 28 个平组无层次与深层目录嵌套两个极端 |
| 组名对齐 ServerS4A21 词汇(4.8.3 映射表) | **新增**(4.8.3) | 其 28 注册组/20 功能目录词汇经 278 协议规模验证;11 组直接沿用、4 组登记偏差自拟,交叉查阅零翻译成本;命名仅涉组织层,协议字节语义不受影响(AGENTS.md D3) |
| 未实现功能预留组占位(组 16–20:Trade/PersonalShop/Friends/Guilds/Pvp) | **新增**(4.8.4) | 与 README「已知缺口」对齐,新功能落地直接入位不触动既有分组;占位不含协议字节,实现仍走 AGENTS.md D1/D2/D3 的验证与文档先行流程 |
| Events 通道化(无界 Channel,SingleReader)+ 跨会话推送禁直调 SendAsync | **修正**(4.3/4.4/4.5) | 第三轮复审:ConcurrentQueue 与 WhenAny 等待语义矛盾;跨会话直调目标 Outbound 会把满 channel 背压传导给发送方会话(P2 的机制化封口) |
| 族接管形态(共享核心 + 每号薄注册壳) | **修正**(4.6/5·组 15) | 第三轮复审:单 ProtocolId 接口与 CMD 10–14 区间接管字面冲突;壳保住注册表一号一键与重复检测,核心保复用 |
| 协议号常量/枚举方向标识(Command / Notification 后缀;枚举按 Cmd / Noti 分表) | **新增**(4.8.3) | 双向共用 0–255 号段且同号不同义,名称不带方向必致误读;现行后缀体系已成立,成文防回潮 |

## 11. 实施记录(2026-09-29,R0 与 R1 组 2-3)

分支 `refactor/protocol-layer`。三条提交:R0 基建与试点、SelfTest 注册表运行器、R1 前置状态域对象化与组 2-3 六协议挂载。

### 11.1 已落地

- **R0 基建**:`Network/Dispatch/`(GameCommandRegistry、IGameCommandHandler、CommandPhase、GameCommandDispatcher)· `Network/Sessions/`(ChannelSessionState 十二域骨架 + ConnectionEvent)· `Network/Outbound/OutboundQueue`(有界 256 + 单写者泵 + IAsyncDisposable)· `Network/Timers/ConnectionTimerScheduler` · `Network/ConnectionContext`。
- **试点与组 2-3 挂载**(9 协议,注册组 `session.connection` / `session.login` / `progression.character`):CHECK_CONNECTION 0、EXIT 3、ANTIBOT 161、LOGIN 1、SET_UDP_IP_PORT 2、GET_USERINFO 8、CREATE_CHARACTER 5、DELETE_CHARACTER 6、RETURN_SELECT_CHARACTER 7。EXIT 的"body 短于 2 字节回落 No-channel-handler 警告"与 LOGIN 的"兜底 canned ACK([1,0,0,0,0])"均按现状逐字节保留。
- **状态域对象化先行**:HandleClientAsync 约 78 个闭包变量一次性迁入 11 个域类的公共字段(用字段而非属性:Interlocked/Volatile 排空需 ref 语义);旧链路经 `session` 别名访问,新 handler 经 `ctx.State` 访问同一存储,无双份状态。SELFTESTS 653 项内联检查全绿,行为零变化。
- **测试基建**:golden 框架 `SelfTests/Protocol/` 直接消费 `tools/packet_bytes.json`(csproj 复制到输出目录);SelfTest 入口注册表化(`--selftest-<name>` 单跑、`--selftest-list`、`--selftest-all` 汇总表;既有内联检查整体保留为 `program-inline` 套件,拆分随后续阶段抽取)。
- **Parser 外置**:`Network/Handlers/Login/LoginParsers`、`Network/Handlers/Characters/CharacterParsers`、共享 `Network/Parsers/ClientBodyParsers`(长度前缀字段读取逐字搬迁);`Network/Builders/CharacterPacket/CharacterPacketBuilders` 承接 USERINFO 名册/初始背包构建;`Network/Sessions/SessionLocationPersistence` 收口位置持久化。

### 11.2 登记偏差(相对 §4 原文)

| 偏差 | 理由 |
| --- | --- |
| `IOutboundQueue` 继承 `IAsyncDisposable` | 关闭语义(排空后关流)落在接口上,连接 finally 与测试统一经接口收口 |
| `ConnectionContext` 暂无 `Services` 成员(§4.2) | handler 依赖走构造注入,无 DI 容器可挂;出现真实消费者(泵层/装配期)再加 |
| U3 阶段门槛:迁移期由分发器执行,门槛不合格**回落旧分支**(按原拒绝路径应答);handler 内同时保留登记的门槛语义 | 迁移期字节保真最稳妥;清退旧分支(R6)后 handler 自含成为唯一权威 |
| 注册表在 `ExecuteAsync` 监听器启动前装配,而非类型构造期 | 装配依赖实例字段(构建器等);失败仍在启动期抛出(§3 I5 不变) |
| 基建目录 `Network/{Dispatch,Sessions,Outbound,Timers,Parsers,Builders,Core,Protocol}` | §4.1 分层图的落地名;4.8.3 映射表只覆盖 handler 组目录,此处登记防歧义。`Core`=通用工具(加解密/协议号枚举与报文名反查/包基元/PVF 读取/Tap 接口/日志队列;字段级解码器 `PacketPlainText` 留存未接入,见 §11.5),`Protocol`=协议核心(分帧/游戏与入口引擎/UDP 数据报协议),镜参考项目 `Network/{Core,Protocol}` 两极 |
| `CompoundItemReplayEntry` 从 EntranceService 私有嵌套记录迁至 `Game/Compound/`(public) | 状态域类需引用;属组织层搬迁,字节语义不变 |
| 状态域以**公共字段**而非属性承载 | §4.3"字段一一对应"的字面落地;ref 排空语义所需 |

### 11.3 R2 实施记录(组 5-8,同日续)

- **已挂载**:组 5 Chat(SEND_MESSAGE 17/CREATURE_SEND 124/CREATURE_SCRIPT 132,注册组 `social.chat`)、组 6 Mailbox(OPEN 99/EXTRACT 98/SEND 97/CHANGE_LETTER_STAT 144,`social.mailbox`)、组 7 Quest(ACCEPT 33/GIVEUP 34/TRIGGER 35/FINISH 36,`progression.quest`)、组 8 Skill(CHANGE_SKILLSLOT 30/BUY_SKILL 31/INCREASE_STATUS 32,`progression.skill`)。
- **Parser 外置续**:ChatParsers(SendMessageRequest+解析)、MailboxParsers(三个 TryReadMailbox* + MailboxSendRequest);测试引用同步。
- **共享助手归口**:ChannelSessionSenders 续接收任务流助手(触发计算/寻求刷新/任务物品授予/可接列表/技能面板)、城镇外观与装备状态发送簇(含精灵状态/初始体力)、仓库容量结算与重量上限;CharacterPacketBuilders 续接收邮件附件规划、城镇外观构建、物品/背包/仓库/精灵包家族与初始技能表。
- **新增泵层能力(登记偏差)**:`ConnectionContext.ReadAdjacentFrame(AdjacentFrameResult)`——CHANGE_SKILLSLOT 复合换位需要预读紧邻帧;延迟/可用性探测/解密/计账保持在传输层(§4.8.1 U1),不匹配帧回排 `queuedRequest`,解密失败复现"中断且不回复"路径。原分支在 handler 内直读 stream 的形态由此封口,成为 4.2 的第二项增补成员。
- **规模**:EntranceService.cs 14,832 → 10,791 行;新增 8 个 handler 组目录(PascalCase 对齐 4.8.3 表)。
- 阶段基线:SelfTests 全量(20 套件)通过;真机冒烟仍为遗留项。

### 11.3b R3 实施记录(组 9-11,同日续)

- **已挂载**:组 9 Inventory(SORT 20/MOVE 19/DELETE 18/USE_STACKABLE 47/DECREASE_DUR 51,`economy.inventory`)、组 10 Shop(BUY 21/SELL 24/REPAIR 25/CERA 67/GEN_CERATICKET 68/ITEMTRADE_STATE 26,`economy.shop`)、组 11 Craft(COMPOUND 27/DISJOINT 28/LOTTERY 29/UPGRADE 83/RESET 84/COMPOUND_AVATAR 102/DROP 50/GET 46,`economy.craft`),共 19 协议。
- **Parser 外置续**:InventoryParsers(5 解析器 + ConsumeItemsRequestEntry)、ShopParsers(5 解析器 + CeraShopPurchaseRequestItem);SelfTests 反射目标同步更新为解析器新家。
- **目录形态对齐 §4.8.3 三层镜像**:全部 `<组>Parsers` 从 `Network/Handlers/<组>/` 提升至与 Handlers 平级的 `Network/Parsers/<组>/`(Chat/Login/Characters/Mailbox/Shop/Inventory 六个同名目录;共享长度前缀读取器 ClientBodyParsers 置 Parsers 根),与参考项目 `Network/{Handlers,Parsers,Builders}` 三层形态一致。
- **CharacterPacketBuilders 按功能块拆为 partial**:主文件保留共享编码与物品投影,功能块分为 Roster/Skills/Inventory/Initialization/Creature/Town/Mail/Premium 八个 partial 文件(单文件 41-173 行),调用点零改动。
- **`Protocol/` 迁入 `Network/` 并按参考项目两极分治**(2026-09-29,镜 `Network/{Core,Protocol}`):
  - `Network/Protocol/`(协议核心,字节冷冻不变):PacketFrame/GameProtocolEngine/EntranceMetadata/EntranceProtocolEngine/GameDatagramProtocol/LegacyDatagramProtocol;
  - `Network/Core/`(通用工具统一管理):Crc32/EntranceCrypto/GameClientPayloadCipher/GameServerPacket(包基元+GamePayloadCipher 服务端加解密)/PacketNames/PacketPlainText/PvfArchiveReader(非协议格式读取工具,仅自测消费),以及自 `Tap/` 迁入的 IPacketTapSink(空目录 `Tap/` 移除);(两者后续处置见 §11.5:`PacketNames` 删除、报文名反查并入 `PacketTypes`;`PacketPlainText` 保留未接入)
  - 命名空间保持不变(`DFLegacy.Protocol`),调用点零改动;§1.3/§4.1 引用 `Protocol/` 的物理位置以本节为准,字节冻结(N2/I4)不受影响。
- **`Persistence/` 瘦身与 `Random/` 并入 `Infrastructure/`**(2026-09-29,按职责归位):`RuntimeState`(运行态遥测/Tap 漏斗/管理快照,非持久化)→ `Infrastructure/`;`SerializedWriteStream`(每连接写串行化,唯一消费方 EntranceService 泵层)→ `Network/Core/`;随机源三文件(`GameRandomSource`/`IDropRandomSource`/`SeededRandomSource`)→ `Infrastructure/`(对标参考项目 `Infrastructure/ServerRandom`),三类随机契约仍由 06 号文档与类头注释约束。`Persistence/` 只余 `JsonGameStore`(对标参考 `Sqlite/` 存储模块);`Properties/`(SDK/IDE 约定)不动,`Services/` 已随后并入 `Infrastructure/`(见下条)。
- **R4 组12/14/15 十二协议**(2026-09-29):Pets(RENAME 103/HATCH 105/RESPONSE 104,`world.pets`)、Town(SET_USER_POSITION 37/SET_USER_AREA 38/CALL_MEMER_LIST 82/EXCHANGE 130,`world.town`;82/130 复刻旧兜底直答字节)、Party(CMD 10-14 族接管:PartyHandlerCore + 5 薄壳,`social.party`)。
- **R5 组4/13 十九协议**(2026-09-29):Stamina(RECOVER_STAMINA 9,`progression.stamina`)、Dungeon 十八协议(15/16/40/41/42/43/44/45/48/49/72/73/74/75/127/142/148/153,`combat.dungeon`);副本流助手簇(回城/选择资格/房间疲劳/精灵经验/翻牌/清关结算/任务触发/被动掉落/内容加载/技能点)整体归口 `ChannelSessionSenders`。
- **参考项目 `Network/Core` 全量研究与迁移结论**(2026-09-29):已采纳——`PacketTypes` 枚举形态(→ 本项目 `CmdPacketType`/`NotiPacketType` 方向分表,130 常量迁入)、`FileLogger`/`PacketFileLogger`/`BoundedAsyncLogQueue`(→ 统一日志与逐包捕获)、`GameCommandRegistry` 形态(R0 起,配 `IGameCommandHandler`);本项目已覆盖——`Packet.cs`/`GamePacketWriter`(→ `PacketFrame`/`GameServerPacket`/`OutboundQueue` 批量发送)、`EncryptTool`(→ `EntranceCrypto`)、`GameNetworkConfig`(→ `ServerOptions`)、`MultiStructureTcpServer`(→ 各监听服务)、`IProtocolHandler`(→ `IGameCommandHandler`)、`EnhancedClientSession`(→ `ChannelSessionState`,并以其散标志位为反面纪律);已评估不采纳——`FlexiblePacketProcessor`(全局锁,§10.3)、`GameChannel*Policy`(单频道部署无对应业务规则);**评估登记**——`PartyUdpRelay`/`UdpRelayServer`(TURN 式每有序对中继):与本项目单口中继的差异、迁移前置条件(客户端 IDA 复核 + 双客户端副本内抓包)见 `docs/protocol/packet-detail-party-60cn.md` 末尾评估小节。2026-09-29 第二轮逐文件复核:分类不变;`PartyUdpRelay` 的判定问题已收窄为单条 IDA 问询(客户端收包分派按帧内槽位还是按源 ip:port 归属),另发现本项目数据面同 IP 多会话的端点归属歧义(候选缺陷),两项均见该分册「第二轮复核补记」。
- **R7 引擎按域拆分(2026-09-29,原 §6·R7 可选项,镜参考项目三层形态)**:`GameProtocolEngine.cs` 3,754 → **447 行**(仅协议号常量表 149 个 + 兜底 `Handle`);14 个分组构建器 `Network/Builders/<组>/<组>PacketBuilders.cs`(Connection/Chat/Mailbox/Quest/Skill/Characters/Pets/Inventory/Shop/Craft/Dungeon/Town/Party/Stamina,76-664 行);共享报文模型 24 个独立为 `Network/Protocol/PacketModels.cs`;长度前缀与 IPv4 写出器为 `Network/Builders/PacketBodyWriter.cs`。993 处调用点机械重命名,字节行为由 golden 样本与 653 项内联检查守护(全量重建 0 错误 0 告警)。附记:协议号常量仍以 `GameProtocolEngine.*Command/*Notification` 名义集中(4.8.3 规则 5 的 `Cmd/Noti` 枚举化留待后续独立变更——该枚举化已落地,见 §11.5);`Handle` 兜底已无线上路径(全部 71 协议经注册表),保留为防御性回退并登记于此。
- **`ChannelSessionSenders` 按功能块拆为 partial**(2026-09-29):2,697 行单文件拆为 10 个 partial(36-619 行,均低于 §4.7 上限)——主文件(共享状态读取与内容缓存)与 Social/Mailbox/Quest/Skill/Inventory/Creature/Town/Dungeon/DungeonRewards 九块;类名与全部调用点零改动。附记:其中的纯规划器(精灵移动规划簇、库存空间校验族)为 `Game/<域>/` 规划的候选归位对象,待后续触及相应域时再迁,当前以 partial 形态保持零改动。
- **R6 增量**(2026-09-29):旧 ANTIBOT 分支与兜底三元死路径清除;5 个跨线程 `Interlocked` 标志全部改为 `session.Events` 事件入队(租约回调生产、泵层原排空点按事件分派),对应状态字段自 Mailbox/Premium/Weakness 域移除。`EntranceService.cs` 14,832 → 1,918 行;注册表 74 项(71 协议 + 组队族 5 壳)。
- **R6 遗留(唯一未完成项)**:4 处 `Task.WhenAny(读帧, Task.Delay(deadline))` 定时点收编 `ConnectionTimerScheduler` 并归并为泵层单调度点(07 §4.5);该变更改变运行时定时触发路径,按 §7.5/§8 要求须在真机上做 premium 到期/翻牌截止/选择与死亡倒计时的时序对拍后方可视为完成。
- **统一日志系统 + 逐包捕获 + 工具拆分**(2026-09-29,自参考项目 `FileLogger.cs`/`PacketFileLogger.cs`/`BoundedAsyncLogQueue.cs` 迁移,变体:DI 而非全局静态——回应 §10.3 对全局静态 FileLogger 的批评):`Network/Core/BoundedQueueFileLoggerProvider`(ILoggerProvider,有界队列 8192 + 单后台消费者 + 排空退出)统一落盘 `logs/server.log`,与简单控制台共享同一批 ILogger 事件;`Network/Core/PacketFileLogSink`(IPacketTapSink,默认关闭,`DFLegacy:PacketCapture` 配置,64MB 轮转)挂既有 Tap 漏斗;漏斗扩为多端口并集(`RuntimeState.AddTapSink/RemoveTapSink`,MCP hub 与包捕获并存);`HexDump.ToSpacedHex` 自 LogPacket 拆出为 Core 共享工具(加解密/明文解码工具已在 Core,日志层仅做排版);`LoggingOptions`/`PacketCaptureOptions` 入 `Infrastructure/ServerOptions`;新自测套件 `file-logger`(队列丢包/落盘/轮转/捕获/多端口分发)。
- **`Hosting/` 并入 `Infrastructure/`,会话注册表入 `Game/Session/`**(2026-09-29,镜参考项目 `Infrastructure/` 与 `Game/Session/SessionDirectory`):`ServerOptions`/`PathResolver`/`AdminApiModels` → `Infrastructure/`;`CharacterSessionRegistry` → `Game/Session/`;空目录 `Hosting/` 移除。命名空间不变,调用点零改动。`Properties/` 为 SDK/IDE 约定(`AssemblyInfo.cs` 的 InternalsVisibleTo 与 launchSettings.json),不动;`Services/`(疲劳重置/虚弱恢复后台循环)已随后并入 `Infrastructure/`(见下条)。
- **共享助手归口**:ChannelSessionSenders 承接库存移动发送器/精灵移动规划簇/库存空间校验族(FindFreeInventorySlot/TryGetInventorySpace/IsInventorySlotValid/兼容槽与部分堆叠搜索)、SendPremiumServiceInfo/SendTownFatigueState;CreatureInventoryMovePlan 迁至 Game/Inventory/。
- **提交内变动记录**:耗时最长的 MOVE(CMD 19)分支约 1,100 行整体迁入 MoveItemSpaceHandler;其后的按子流程拆分已于同轮完成(四个 partial,见 §11.4)。
- **规模**:EntranceService.cs 14,832 → 5,128 行;累计挂载协议 43/71;SelfTests 20 套件全量通过(真机冒烟仍为遗留项)。

### 11.5 协议号枚举迁移与两个诊断工具的处置(2026-09-29)

- **协议号枚举迁移(补记,此前只入 CHANGELOG)**:`Network/Core/PacketTypes.cs` 新增 `CmdPacketType`(163 项)/`NotiPacketType`(154 项)方向分表,引擎内 130 个协议号常量迁入并删除(4.8.3 规则 5 的 `Cmd/Noti` 枚举化);数据源 `tools/packet_id_map.json`,无名占位(`NOTI_NO_NAME`)不入枚举、空档以注释登记。
- **`PacketNames.cs` 删除,报文名反查并入枚举单源**:原 `Network/Core/PacketNames.cs`(60A1 包名表生成的字符串表,枚举化后已是枚举反射包装)删除;协议号 → 报文名反查以 `PacketTypeNames.TryGetName` 落在 `Network/Core/PacketTypes.cs` 末尾——枚举成员名即客户端包名去前缀形态,只按客户端习惯补 `ENUM_CMDPACKET_` / `ENUM_NOTIPACKET_` 前缀,不再另存名称副本。包名表地址(.rdata 0xB54170 NOTI / 0xB54430 CMD)与逐项对照文档的指引随迁该文件头注释,证据链不断。六个调用点(`RuntimeState` ×2、`PacketFileLogSink`、MCP `PacketTapHub` / `PacketTapTools` / `PacketNamesResource`)改接新名;`packet_names_search` 工具与 `packet-names://cmd|noti` 资源行为不变。
- **`PacketPlainText.cs` 保留但断开接线**:字段级明文解码器不再被任何链路调用——`EntranceService.LogPacket` 两个重载去掉 `text=` 与解码调用(控制台追踪回到"原始字节 + crc"),`PacketTapHub` 停止富化 `PlainText`,`PacketTapEvent.PlainText` 字段与 MCP `protocol_decode` 工具(实现整体依赖该解码器)随之下线;解码实现本体原样保留,类头注明未接入,备后续离线工具或重新接入。`TapDecodeMode` 口径分类保留:它标识 body 来源(客户端上行/服务端下行/无口径),由 `packet-tap-funnel` / `file-logger` 自测守护,是解码器重新接入的接缝。
- **验证**:Server / Mcp / SelfTests 三项目全量重建 0 错误 0 告警;SelfTests 21 套件全绿(`protocol-golden`、`packet-tap-funnel`、`file-logger`、`mcp-module-loader` 均覆盖本次改动路径)。真机冒烟仍为遗留项(见 §11.4)。
- **同轮处置**:`tools/gen_packet_names.py` 删除——其唯一产物即本次删除的字符串表文件,脚本内写死机器绝对路径,且输出目录 `Server/DFLegacy.Server/Protocol/` 在 R7 目录重组后已不存在(保留即等于留一个会复活已删文件的入口)。`tools/PacketDumper` 保留待重写:它是 `tools/packet_bytes.json` 黄金样本库的生成器(SelfTests 经 csproj 直接消费),价值不可弃,但不在解决方案内、仍调用 R7 前的 `GameProtocolEngine.CreateXxx` 与 `PacketPlainText`,当前不能编译。另记:`PacketTypes.cs` 的重生成脚本尚未随枚举落地(当前为一次性生成),与 PacketDumper 重写一并待补。
- **`Services/` 并入 `Infrastructure/`**(2026-09-29,前置条件已具备):`FatigueResetService`(每日 06:00 疲劳重置)与 `WeaknessRecoveryService`(每分钟推进虚弱恢复)移入 `Infrastructure/`,空目录 `Services/` 移除;命名空间不变,调用点零改动。既定前置"R6 事件入队改造"经核实已完成——两者的通知回调在 `SelectCharacterHandler` 领租处即写目标会话 `session.Events`(`ConnectionEventKind.FatigueReset` / `WeaknessRecovered`),服务侧只经 `CharacterSessionRegistry` 转交回调,不再直写他人连接流。至此项目根只余 `Program.cs` 与 SDK 约定的 `Properties/`。
- **组队报文模型归位与 `Builders/CharacterPacket/` 目录**(2026-09-29,按 B1 语义化):① 五个组队 TCP 报文模型(`PartyWireSlot` / `PartyInfoWireBlock` / `PartySettingsRequest` / `PeerRequestFrame` / `PeerResponseFrame`)自 `Network/Protocol/GameDatagramProtocol.cs` 移出、逐字落 `Network/Protocol/PartyWireModels.cs`——它们属频道命令面(CMD 10-14 请求体、NOTI 7-10 块),不含 UDP 数据报结构,与数据报协议同文件属误置;② 初始化包家族 `CharacterPacketBuilders`(主文件 + 8 个功能块 partial,共 718 行)自 `Network/Builders/` 根移入 `Network/Builders/CharacterPacket/`,与同目录的组构建器 `CharactersPacketBuilders`(Characters 协议组,R7 产物,527 行静态类)区分开——两者此前仅差一个字母且同在 Builders 根,易混。**Builders 目录规则随之明确:根只放共享基建(`PacketBodyWriter.cs`),子目录放组构建器与流程家族**。命名空间不变,调用点零改动;全量重建 0 错误 0 告警,SelfTests 21 套件全绿。

### 11.4 遗留项

- **协议迁移**:已全部完成(71/71 协议挂载);纯规划器已归位 `Game/Inventory/`(空间校验族 + 精灵移动规划簇,senders 留薄委托);`MoveItemSpaceHandler` 已按子流程拆为四个 partial 文件(742/155/195/134 行,均低于 §4.7 上限)。
- **R6 定时器收编(唯一未完成的机制项)**:现状实现在 `Network/EntranceService.cs` 泵层读循环顶部(L1247 起的 `while` 内,`pendingPacketRead` 同一段 `if/else-if` 链),四处形状相同——`await Task.WhenAny(pendingPacketRead, Task.Delay(remaining, serverToken))`,超时后 `request` 保持 null,执行到期动作再 `continue` 重新武装:

  | # | 位置(2026-09-29 时点) | 触发状态(写入点) | 到期动作 |
  | --- | --- | --- | --- |
  | 1 | `EntranceService.cs:1263` | `Dungeon.SelectionDeadline`(StartGameHandler 设 30s) | `ReturnToTownFromDungeonAsync("selection-countdown-expired")` |
  | 2 | `EntranceService.cs:1291` | `Dungeon.DeathTimeout`(DieCharacterHandler 设,Generation 校验) | 校验 Generation 未变后强制回城 |
  | 3 | `EntranceService.cs:1331` | `Dungeon.CardSelectionDeadline`(CardSelectRightStateHandler 设 4s) | 自动翻牌/回城 |
  | 4 | `EntranceService.cs:1378` | `Premium.ExpiryUnix`(租约回调写入) | 到期服务 `SendPremiumServiceInfoAsync(type, 0)` |

  **收编方案(07 §4.5 既定设计)**:泵层合并为一个 `Task.WhenAny(readTask, eventsTask)`,四处 deadline 写入点(共约 12 处赋值/清空,分布于 StartGame/DieCharacter/CardSelectRightState/SelectDungeon/MoveMap/UseCoin/ReturnSelectCharacter 各 handler 与 `ChannelSessionSenders.Dungeon`)改为 `context.Timers.Register(due, 入队 ConnectionEvent)`;已作废的旧定时器靠到期事件内的二次校验(如 DeathTimeout 的 Generation 比对)空转。改动本身有限,但**触发时刻由"`Task.Delay` 到期"变为"调度器到期 + 下一次 `WhenAny` 唤醒"**,极端交错下可能存在亚帧级差异,**按 §7.5 必须真机对拍(premium 到期/翻牌截止/选择与死亡倒计时,误差 < 1 读帧周期)后方可视为完成**;未复测前不实施,以免失去现行实现的已知时序基线。
- **真机冒烟复测**:全部 71 协议的字节级等价性由 golden 样本与内联检查守护,但真机全流程回归(登录→选角→进城→聊天→买卖/修理→接交任务→学技能→副本全程→结算翻牌→回城→邮件,§7.4 清单)未做——这是当前最大的未验证面,建议合入 `main` 前补做;未复测的结论一律按"待真机确认"登记。
- **数据面归属(候选缺陷,待真机复现)**:`GameplayDatagramService` 的 UDP 端点学习按 TCP 源 IP 整体赋予(`ObserveEndpoint`),同机双开或同一 NAT 出口下多会话互相覆盖端点映射,中继转发可能错位;判定条件、修复方向与验证要求见 `docs/protocol/packet-detail-party-60cn.md`「第二轮复核补记」第 2 条。未复现前不改(现行实现是单会话场景的已知良好基线)。
