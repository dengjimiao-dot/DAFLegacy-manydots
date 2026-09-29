# 07 · 协议层重构:收发包管线与分发架构

- 状态:**待实施**(2026-09-28 按 ServerS4A21 工作区现状复核,数据全部实测,见 §10)
- 上级文档:[00 开档总纲](00-kickoff.md) · [01 目标架构](01-target-architecture.md) · [04 迁移路线](04-migration-plan.md)
- 关联文档:[协议枚举全表](../protocol/client-packet-enums-60cn.md) · [× ServerS4A21 对比](../servers4a21/comparison-vs-servers4a21.md)

---

## 1. 背景与动机

### 1.1 现状收发链路

```text
TCP(游戏协议, channel/entrance 双端口)
  └─ EntranceService.HandleClientAsync          ← 单方法 12,337 行(L166–12502;文件全文 14,463 行,2026-09 实测)
       ├─ PacketFrame.ReadAsync                 10B 头 + body,4MB 上限,坏帧抛异常断连     [Protocol/, 保留]
       ├─ GameClientCipherState.TryDecode       CRC 做 oracle 的种子盲试解密(仅 channel 口) [Protocol/, 保留]
       ├─ Task.WhenAny(读帧, Task.Delay) ×4     内联定时器(premium 到期/翻牌截止等)交织,L2275/2303/2343/2390
       ├─ Interlocked 标志回排(5 个)             pendingMailAlarm / pendingPremiumUpdates(ConcurrentQueue) /
       │                                        pendingCeraValue / pendingFatigueReset / pendingWeaknessRecovery
       └─ 57 个 `request.ProtocolId ==` 分支     if/else 链(55 个不同协议号) + ≈216 个局部函数共享约 30 个闭包变量
            ├─ GameProtocolEngine.Create*       纯函数构造回包                             [Protocol/, 保留]
            ├─ frame.WriteAsync(stream) ×340    直写 NetworkStream(单写者靠"都写在同一条异步流里"的纪律)
            └─ 跨会话推送                        邮件提醒/小喇叭(NOTI 130)等经 SessionRegistry 回调,
                                                在调用者线程 `_ = Send…Async(...)` fire-and-forget 直写他连 stream
                                                (EntranceService.cs:2768;全文件 SemaphoreSlim=0 / Channel<T>=0)

UDP(NAT 探测/保活/血量同步)
  └─ GameplayDatagramService + GameDatagramProtocol                                          [保留]
```

### 1.2 三个结构性问题

| 编号 | 问题 | 证据(2026-09-28 实测) |
| --- | --- | --- |
| P1 | **分发不可扩展**:新增协议 = 在巨型方法里找上下文插分支;handler 无法独立测试、无法独立演进 | `HandleClientAsync` 单方法 12,337 行、57 个 `request.ProtocolId ==` 分支(55 个不同协议号)、约 216 个局部函数共享约 30 个闭包变量(小喇叭等新协议仍只能往里加) |
| P2 | **单写者靠纪律**:任何新增后台发送源(公告、活动、跨会话)都必须记得"回排到读循环",绕过即产生并发写竞争 | 340 处 `WriteAsync(stream` 裸直写;SemaphoreSlim/Channel<T> 均为 0。**已现裂缝**:小喇叭(NOTI 130)落地时,跨会话推送走了 `_ = SendMegaphoneNotificationAsync(...)` fire-and-forget 直写他连 stream(L2768)——正是本问题预言的失败模式,幸好当前读循环在该窗口无并发写 |
| P3 | **定时器语义埋在业务里**:`Task.WhenAny(读帧, Task.Delay)` 的交织模式复制了多份,时序行为无法整体推理 | `await Task.WhenAny(` 4 份拷贝(L2275/2303/2343/2390),每处都与 `Task.Delay` 同语句交织;另有多处纯 sleep 型 Task.Delay |

### 1.3 已有资产(本次不动)

- **协议核心层**:`PacketFrame` / `GameClientPayloadCipher` / `GameDatagramProtocol` / `Crc32` / `EntranceCrypto`——现代 span/record 风格、纯函数,质量高,**字节级行为冻结**。
- **领域层**:`Game/{Account,Compound,Creature,Drop,Dungeon,Inventory,ItemUpgrade,Quest,Shop}`(Planner/Policy 纯函数)与 `GameWorld/` 25 个目录表——handler 化之后只是薄编排。
- **自测框架**:`DFLegacy.Server.SelfTests`(Check + SmokeTests 模式)与 **PacketDumper 黄金字节库**(`tools/packet_bytes.json`,约 46 条 NOTI + 65 条 CMD 样本,含客户端算法解密回放自检)。
- **可观测性**:`RuntimeState` tracing / 管理页 / `LogPacket`(报文名反查 + 明文字段解码 + 原始字节)。

### 1.4 与 ServerS4A21 的对照结论

采纳(2026-09-28 复核后维持并扩充,详见 [§10 对照决议](#10-servers4a21-对照2026-09-28-复核)):

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
| 4.6 分发注册表 | `GameCommandRegistry`(78 行):字典注册 + 重复即失败 + 来源分组;未注册 hex dump | 直接采纳(delegate 换接口,叠加 PacketNames 反查) |
| 4.7 Handler/Parser/Builder | `Network/{Handlers,Parsers,Builders}` 三层分目录(145/86/220 个文件) | 直接采纳 + 两项纪律修正(Parser 100% 外置、单文件 ≈800 行上限) |
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
- N2 不改协议核心层(`Protocol/` 现有 11 个文件)。
- N3 不改领域逻辑行为(`Game/`、`GameWorld/` 只被调用,不被修改)。
- N4 不实现多频道/横向扩容,仅为它留路(每连接独立缓冲、无全局锁的传输层已满足前提)。
- N5 不动入口 AES 信道脚本协议(`EntranceProtocolEngine` / `ChannelProtocolHandler` 对应部分)。

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
├─ Handlers(Network/Handlers/<域>/, 新增,薄编排)──────────┤
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
    public IServiceProvider Services { get; }           // 目录/存储/领域服务(现有 ctor 依赖)
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
    public ConcurrentQueue<ConnectionEvent> Events { get; }  // 跨线程事件统一入队口
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
- 现有 5 个 Interlocked 标志改为后台服务往 `State.Events` 入队(或直接入站出站),读循环每帧处理完统一 drain——语义与现状一致,但入口唯一。

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

> **参照 S4A21(直接采纳)**:`Network/Protocol/GameCommandRegistry.cs`(78 行)——`Dictionary<ushort, delegate>` 注册、启动期重复注册抛异常(带来源组名)、未注册命令 hex dump;其约 278 条 CMD 注册规模已验证该形态。本方案两处增强:delegate 换 `IGameCommandHandler` 接口(类型化依赖 + 可独立构造),hex dump 叠加 `PacketNames` 反查协议名(307 个)。

```csharp
public interface IGameCommandHandler
{
    ushort ProtocolId { get; }
    string Name { get; }   // 诊断用,如 "MAILBOX_OPEN"
    ValueTask HandleAsync(ConnectionContext ctx, PacketFrame frame, CancellationToken ct);
}

public sealed class GameCommandRegistry
{
    public void Register(string group, IGameCommandHandler handler);  // 重复 → 启动失败(带 组名/协议/名称)
    public bool TryGet(ushort protocolId, out IGameCommandHandler handler);
    public IReadOnlyCollection<ushort> Registered { get; }            // 启动时打印清单
}
```

- 未注册命令:记 WARN + hex dump(沿用现状"Unhandled"日志,但补协议名反查,现 `PacketNames` 已具备)。
- CRC/种子失败、坏帧的处理留在泵层,不进 handler(与现状一致)。

### 4.7 Handler / Parser / Builder 约定

> **参照 S4A21(直接采纳 + 纪律修正)**:三层分目录组织来自其 `Network/{Handlers,Parsers,Builders}`(145/86/220 个文件);两处修正针对其反例——Parser 必须 100% 外置(其 mailbox 等复杂解析内联在 handler 里,复用与独立测试打折),单文件 ≈800 行上限(见本节末尾)。

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
- **单文件上限纪律**:handler/局部 coordinator 文件超过约 800 行必须按协议或子流程拆分(partial 或子类型)。ServerS4A21 的教训:分层本身不防巨石,其 PvpRoomHandler(5,288 行)、DungeonEntryHandler(4,938 行)就是"handler 化之后再巨石化"的样例;本方案以 14 组为上限粒度、超限即拆,不允许组内再长成单文件。

## 5. 分组映射(57 分支 → 14 个 handler 组)

| # | 组(Network/Handlers/<组>/) | 协议(CMD, 60CN 号) | 现状行段(EntranceService.cs) | 触及状态域 |
| --- | --- | --- | --- | --- |
| 1 | handshake | CHECK_CONNECTION 0 / EXIT 3 / CHANNELINFO 推送 | 409-418, 429, 2967-2978 | Login |
| 2 | login | LOGIN 1 / SET_UDP_IP_PORT 2 | 2481-2541 | Login, Premium |
| 3 | character | GET_USERINFO 8 / SELECT 4 / CREATE 5 / DELETE 6 / RETURN 7 | 2543-2684, 12173-12234 | Roster, Character, Inventory |
| 4 | stamina | RECOVER_STAMINA 9 + NOTI 4/33 推送 | 9919-9981, 870-909 | Weakness, Currency |
| 5 | chat | SEND_MESSAGE 17(含 type 13-15 小喇叭 → NOTI 130)/ CREATURE_SEND 124 / CREATURE_SCRIPT 132 | 3331-3477, 2768 附近(喇叭接线) | Character |
| 6 | mailbox | OPEN 99 / EXTRACT 98 / SEND 97 / CHANGE_LETTER_STAT 144 + ALARM | 3479-3897, 14018-14133 | Mailbox, Currency |
| 7 | quest | ACCEPT 33 / GIVEUP 34 / TRIGGER 35 / FINISH 36 | 3898-4740, 14018- | Quest, Inventory |
| 8 | skill | CHANGE_SKILLSLOT 30 / BUY_SKILL 31 | 5438-5640 | Skill, Currency |
| 9 | inventory-core | DELETE 18 / MOVE 19 / SORT 20 / USE_STACKABLE 47 / DECREASE_DUR 51 | 5760-5855, 6062-7194, 7196-8004, 9738-9820 | Inventory |
| 10 | shop | BUY 21 / SELL 24 / REPAIR 25 / CERA 67 / GEN_CERATICKET 68 / ITEMTRADE_STATE 26 | 8006-8942, 9483-9568 | Inventory, Currency, Mailbox |
| 11 | item-craft | COMPOUND 27 / DISJOINT 28 / LOTTERY 29 / UPGRADE 83 / RESET 84 / COMPOUND_AVATAR 102 / GET 46 / DROP 50 | 4740-5170, 9112-9481, 11242-11620 | Inventory, Dungeon |
| 12 | creature | HATCH 105 / RENAME 103 / RESPONSE 104 + NOTI 100-107 | 5857-6062, 697, 7514 | Inventory(creature), Weakness |
| 13 | dungeon | START_GAME 15 / SELECT_DUNGEON 16 / FINISH_LOADING 40 / MOVE_MAP 48 / DIE_MONSTER 42 / ALLDIE 148 / DIE_CHAR 43 / USE_COIN 44 / GIVEUP 45 / BACK_2_VILLAGE 142 / PLAY_RESULT 49 / 计分卷/翻牌 72-75 / BOSS_DIE 127 / TUTORIAL 153 / USE_SKILL 41 | 9822 端-12171 区间多个段 | Dungeon, Character, Quest, Inventory |
| 14 | town | SET_USER_POSITION 37 / SET_USER_AREA 38 / CALL_MEMER_LIST 82 / EXCHANGE 130 / EVENT_INFO 推送 / USERINFO 推送 | 9822-9917, 11242-, 916, 482-532 | Character, Town |

> 行段为迁移时点的参考位置,以 `GameProtocolEngine.*Command` 常量检索为准。

## 6. 迁移计划(strangler,每阶段独立 PR/可发布/可回滚)

| 阶段 | 内容 | 退出标准 | 风险 |
| --- | --- | --- | --- |
| **R0** | 基础设施:Registry / ConnectionContext / OutboundHub / TimerScheduler + 试点协议(EXIT、CHECK_CONNECTION);golden 测试框架落地(SelfTests/Protocol/,直接消费 PacketDumper 的 `packet_bytes.json` 样本库);SelfTests 入口注册表化(参照 S4A21 `--selftest-*` 运行器,拆分 9,609 行 Program.cs) | 试点两协议走新链路,真机登录无感;golden 断言齐 | 低 |
| **R1** | 握手 + 登录 + 角色(组 1-3,含 NOTI 2 全部子类型与顶号);登录初始化包**参照 S4A21** `InitPacketBuilderRegistry` 形态分组注册(其 50+ body builder) | 真机:登录→选角→建/删角→返回选角 全流程字节一致 | **高**(ACK/名册布局最复杂) |
| **R2** | 邮箱 + 聊天 + 任务 + 技能(组 5-8) | 真机:聊天回显/邮件全流程/任务接交/学技能 | 中(状态域简单,先行验证搬运模式) |
| **R3** | 物品三组(组 9-11,分支最多) | 真机:买/卖/修/整理/移动/删除/使用/合成/强化/分解/抽奖/拾取/丢弃 | 中(纯搬运量大) |
| **R4** | 宠物 + 城镇/系统(组 12、14) | 真机:孵化/改名/召唤、进城位移/切区/访客列表 | 中 |
| **R5** | 副本/战斗(组 13;定时器最密集) | 真机:进图→清怪→结算→翻牌→回城 全程;翻牌截止/premium 到期时序与旧版对拍 | **高**(定时器等价性) |
| **R6** | 清退:删除全部旧分支与 5 个 Interlocked 标志,`HandleClientAsync` 收缩为"读帧→解密→分发"泵;疲劳/虚弱后台服务改事件入队 | `EntranceService.cs` < 1,500 行;全量真机回归 | 低 |
| **R7**(可选) | `GameProtocolEngine.cs`(3,360 行)按域拆分 builder 文件 | 纯文件搬移,字节零变化 | 低 |

执行原则(沿用 04):**同一协议的新旧实现不同时挂载**——注册表按协议号独占,切换即该协议完全走新链路;搬代码与改行为严格分开;每阶段合并后 `main` 可发布。

## 7. 测试与验收

1. **Golden bytes(核心手段)**:每协议准备 `请求帧样例 → 期望回包字节序列` 断言,样本来源:runtime tracing 现网抓取 + 手工构造;落 `SelfTests/Protocol/<组>GoldenTests.cs`,复用现有 `Check` 模式。R0 起随每阶段补齐,**旧实现产出的字节就是黄金样本**(先记录后切换)。
2. **密码学回环**:移植 `tools/crypto_roundtrip_test.py` 的断言到 SelfTests(种子收敛、CRC、NOTI ROR(x^0xB5,6) 互逆)。
3. **不变量测试**:FIFO 顺序、并发入站出站的单写者、坏帧断连、CRC 拒绝、种子恢复失败路径。
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
| 工作量失控 | 8 阶段各自可停;R1/R5 两个高风险阶段单独排期 |

## 9. 工作量粗估(单人)

| 阶段 | 估算 |
| --- | --- |
| R0 基础设施 + golden 框架 | 4-6 人日 |
| R1 登录/角色 | 5-8 人日 |
| R2 邮箱/聊天/任务/技能 | 4-6 人日 |
| R3 物品三组 | 6-9 人日 |
| R4 宠物/城镇/系统 | 3-5 人日 |
| R5 副本/战斗 | 6-9 人日 |
| R6 清退 | 2-3 人日 |
| R7(可选)engine 拆分 | 2-3 人日 |
| **合计** | **约 30-49 人日** |

## 10. ServerS4A21 对照(2026-09-28 复核)

> 本节按 ServerS4A21 的 `Server/DfoServer` 工作区现状重新实测(命令统计,非目测),取代上一版"借鉴对照(决议)"单表。包级字段差异见[对比文档](../servers4a21/comparison-vs-servers4a21.md),功能覆盖差距见[功能回填计划](../servers4a21/feature-backfill-plan-vs-s4a21.md)。

### 10.1 规模与协议处理形态对照(实测)

| 维度 | DFLegacy(60A1) | ServerS4A21(A21) |
| --- | --- | --- |
| 代码规模 | 114 个 .cs / 60,605 行 | 1,182 个 .cs / 316,375 行(≈5.2×) |
| 协议覆盖 | 57 个 CMD 分支(55 个协议号),全部有字节级 golden 对照 | 约 278 条 CMD 注册、30 个注册组;CMD/NOTI 枚举全量表(CMD 1271 + NOTI 1219) |
| 请求分发 | `HandleClientAsync` 12,337 行内 if/else 巨链 | `Dictionary<ushort, delegate>` 注册表(GameCommandRegistry 78 行)+ GameProtocolHandler 1,064 行;重复注册启动期抛异常(带来源组名) |
| 分帧 | 每连接独立缓冲(PacketFrame,10B 头,4MB 上限) | FlexiblePacketProcessor:**全局一把锁**切帧,1MB 帧上限 / 2MB 连接缓冲 |
| 完整性/加密 | CRC32 + 种子盲试解密(channel 口),真校验 | 14B/15B 头明文,checksum/seq 恒写 0 不校验;AES 仅频道协议且密钥=日期串、明文入日志 |
| 会话状态 | 巨型方法局部变量(≈30 个闭包变量 + ≈216 个局部函数) | `EnhancedClientSession` + `PlayerContext`(partial 分域)+ SessionDirectory(CAS 顶号踢旧);但会话对象堆积 `A21*` 流程标志位与版本协商位 |
| 顶号/生命周期 | CharacterSessionRegistry.ClaimAsync 踢旧并**等待其释放** | `CanDispatch` 分发门槛拒收 stale session 包 |
| 出站写入 | 340 处裸直写 stream,零机制(SemaphoreSlim/Channel 均为 0) | 每会话 `SemaphoreSlim(1,1)` 串行 + 批量发送部分写语义 |
| 定时器 | 读循环内 WhenAny+Delay ×4 份拷贝 | ClockService 统一时间队列(716 行,对齐旧服语义:错过只补一次);premium 走 DB expire 字段不占 timer |
| 后台事件 | Interlocked 局部标志 + ConcurrentQueue → 读循环顶部排空(推送时延受客户端流量驱动) | 回调里直接 `SendPacketAsync`(过会话信号量),无需回排 |
| 领域层 | 纯函数 Planner/Policy(39 文件)+ 25 个 Catalog(12,451 行) | 有状态 Service + SQLite Repository 与静态 Policy 并存(Game/ 148k 行) |
| 装配 | Program.cs `AddHostedService` + ctor 注入 | 手写组合根 ServerRuntimeBuilder(1,000 行)+ 8 个纯参数容器,无 DI 容器 |
| 测试 | 16 文件 12,837 行;Program.cs 单文件 9,609 行 | 75 文件 45,609 行;SelfTestRegistry + `--selftest-xxx` 单跑 / `--selftest-all` 汇总表 |
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
3. 测试基建弱:9,609 行单文件入口 vs S4A21 的注册表运行器,选跑/汇总能力缺失。

**ServerS4A21 的可取之处**

1. **注册表分发在 278 协议规模下成立**:字典 + 启动期重复检测 + 来源分组,是本方案 G1 的规模验证(57 协议远未到该模式上限)。
2. 会话状态对象化方向正确(PlayerContext partial 分域、SessionDirectory CAS 顶号),只是纪律失守(散标志位回潮)——印证 G2 需要"分域 + 禁止跨域字段"的硬约束而非仅约定。
3. 出站每会话信号量是**机制**而非纪律,跨会话推送直接入锁即发——这是它对本项目最大的单项领先。
4. ClockService 统一时间队列(错过只补一次,对齐旧服 TimerQueue)为 G4 提供了现成的语义基准。
5. SelfTest 注册表运行器(单跑/全跑/汇总表)是测试基建的成熟形态。

**ServerS4A21 的结构性弱点(不采纳清单的事实依据)**

1. 全局锁分帧器 + 全量拷贝:形态性缺陷,不可修补只能绕开。
2. 零完整性校验(checksum/seq 恒 0)、频道密钥=日期串明文入日志:安全/健壮性倒挂。
3. **分层 ≠ 防巨石**:handler 化之后照样长出 PvpRoomHandler 5,288 行、DungeonEntryHandler 4,938 行;Parser 覆盖不全(86 parser 文件 vs 145 handler 文件),复杂解析内联回 handler(如 mailbox),复用与独立测试打折——G5 要求 Parser 100% 外置即为针对此。
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
| 未注册命令 hex dump 日志 | **采纳**(4.6) | 附带 PacketNames 反查协议名(307 个协议名) |
| 会话对象散标志位 | **不采纳** | G2 分域状态对象 + 跨域字段禁入;S4A21 反例已写入 1.4 |

本轮新增/修正决议:

| 项 | 决议 | 理由 |
| --- | --- | --- |
| SelfTest 注册表化运行器 | **采纳**(R0) | `--protocol-golden` 等按组单跑 + 汇总表;顺带拆分 9,609 行 Program.cs 入口 |
| 单 handler 文件上限纪律(≈800 行,超限按协议/子流程拆) | **新增**(4.7) | S4A21 证明"分层不自动防巨石"(PvpRoomHandler 5,288 行) |
| ClockService"错过只补一次"周期语义 | **采纳为 R5 对拍基准**(4.5) | 已对齐旧服 TimerQueue,避免重造语义 |
| `CanDispatch` stale-session 分发门槛 | **不采纳(已覆盖)** | 本项目 ClaimAsync 踢旧并等待释放,新登录与旧连接无并发窗口,无迟到包问题 |
| 登录初始化包分组注册(InitPacketBuilderRegistry 形态) | **参考**(R1) | S4A21 用 50+ 个 body builder + 注册表组织登录初始化包,R1(最复杂阶段)可沿用该组织方式 |
| 全局静态 FileLogger 批评 | **修正** | 其已升级有界队列 + 后台写,"逐包同步落盘"不成立;维持不采纳的理由降级为"全局静态隐藏依赖",逐包抓包(默认关)思路与现有 RuntimeState tracing 重合,不另引入 |
