# DFLegacy 设计文档汇总

- 更新:2026-09-29
- 本目录收录服务端结构性设计文档;各文档状态以其自身标注 + 代码实际落地核定,与本页冲突时以代码为真。

## 状态图例

| 状态 | 含义 |
| --- | --- |
| **草案** | 设计已成文(含"草案/待实施/设计定稿"等各文档自身表述),代码尚未实施 |
| **已实现** | 设计已随代码落地,文档即实现依据与行为说明 |
| **已作废** | 提案被后续方案取代,仅作技术记录留档 |

## 文档索引

| # | 文档 | 主题 | 状态 | 时间线 |
| --- | --- | --- | --- | --- |
| 00 | [设计开档总纲:跨平台可移植性重构](00-kickoff.md) | 可移植性重构的开档总纲、原则与 01–06 文档导航 | 总纲 | 2026-09-24 标注:01–06 已实施、07 待实施 |
| 01 | [目标架构:Server 层与 Client 层](01-target-architecture.md) | Server/Client 分层目标架构与平台边界 | **已实现** | 2026-09-24 标注,重构已落地 |
| 02 | [可移植性契约 R1–R11](02-portability-contracts.md) | "可移植"的可判定定义(R1–R11;路径锚点、编码契约等) | **已实现** | 同上 |
| 03 | [构建、发布与 CI](03-build-release-ci.md) | 构建、发布流程与 CI(publish 脚本、发布布局) | **已实现** | 同上 |
| 04 | [迁移路线、验收与风险](04-migration-plan.md) | 跨平台重构的迁移路线、验收标准与风险登记 | **已实现** | 同上 |
| 05 | [RandomNative 跨平台化提案](05-randomnative-crossplatform.md) | 原生随机模块跨平台化提案 | **已作废** | 后继方案 06,仅留档 |
| 06 | [随机源降级为纯托管实现](06-random-source-managed.md) | 服务端随机源托管化(`Infrastructure/`,原 `Random/` 已并入),与 RandomNative 解耦(模块保留至 Client 层) | **已实现** | 同上 |
| 07 | [协议层重构:收发包管线与分发架构](07-protocol-layer-refactor.md) | 把 `HandleClientAsync`(约 12,654 行、71 协议号巨链)重构为「注册表分发 + 通用管线(状态门槛/出站计账)+ 6 大类 15 组 handler(组名对齐 ServerS4A21,另预留组 16–20)+ 出站 Channel 单写者 + 统一定时器」架构 | **草案(待实施)** | 2026-09-28 复核;2026-09-29 复审刷新实测、增补 4.8 大类划分、命名规范与预留组 |
| 08 | [SQLite 存储迁移(JsonGameStore → SQLite)](08-sqlite-storage.md) | 以同签名 `SqliteGameStore` 替换全量重写 JSON 的 `JsonGameStore`:增量写、事务边界、基线+增量迁移、一次性数据导入 | **草案** | 2026-09-28 起草;2026-09-29 基线重构定稿(统称 60A1) |
| 09 | [DAF-MCP 总网关(一):协议包实时监听](09-mcp-packet-tap.md) | 服务端内置标准 MCP 服务器(默认关闭),`packet_watch` 游标长轮询实时暴露收发包(报文名+明文富化),供智能体边抓边分析协议 | **已实现** | 2026-09 定稿;2026-09-29 随 commit `7e12da7` 落地 |

## 各文档要点与代码落点

### 07 · 协议层重构(草案)

- 三个结构性问题:分发不可扩展(P1)、单写者靠纪律(P2,已有小喇叭 fire-and-forget 直写裂缝)、定时器语义埋在业务里(P3)。
- 方案:71 协议号 → 15 个 handler 组、6 个功能大类(组名对齐 ServerS4A21 词汇,4.8.3 映射表);`GameCommandRegistry` 字典分发(启动期重复注册即失败)+ 通用管线(状态门槛声明式收口、出站计账单点);`ChannelSessionState` 分域状态;有界 `Channel<T>` 出站泵;单点 `WhenAny` 定时器调度。strangler 式 R0–R7 迁移,约 31–50 人日。
- 硬约束:字节级行为冻结(golden 测试先行,旧实现产出即标准);协议核心层与领域层零改动。
- 代码现状:未动工(`GameCommandRegistry`/`ConnectionContext`/出站泵均不存在)。

### 08 · SQLite 存储迁移(草案)

- 现状唯一持久化入口 `Persistence/JsonGameStore.cs`(约 5,600 行):单文件 `state.json` 全量重写、无事务边界。
- 方案:`SqliteGameStore` 同签名替换(公开签名不变,Admin API/EntranceService 零改动);基线 + 增量两段式迁移;`state.json` 单事务一次性导入,失败即回滚删库、原文件保留。里程碑 M0–M3。
- schema 基线:[docs/sql/60A1_schema.sql](../sql/60A1_schema.sql)(统称 60A1,16 张表;**表设计模式取自 [86S4A21](../sql/86S4A21_schema.sql)、功能面上限兼容 [60A1 早期草案留档](../sql/60A1_draft_schema.sql)**;`lock_state`/`hunt_trigger_format`/`account_dungeon_progress`/`character_exp_boost` 为草案兼容预留;不采纳 86 的 item_core BLOB,物品走扁平列 + item_uid 代理主键)。
- 代码现状:未动工(无 `SqliteGameStore`/`Microsoft.Data.Sqlite` 引用;`docs/sql/60A1_seed_test.sql` 为 `tools/export_state_sql.py` 导出的 **test 账号数据种子 SQL**,不再生成二进制样例库)。

### 09 · DAF-MCP 协议包实时监听(已实现)

- 代码落点:
  - `Server/DFLegacy.Mcp/`:独立模块项目(`McpModule`、`McpHost`、`Tap/PacketTap{Event,Hub,Filter,Tools}`、`Tap/PacketNamesResource`)——§6 全部 7 个工具(`packet_watch/get/wait`、`session_list`、`packet_names_search`、`protocol_decode`、`tap_status`)均已提供。
  - `Server/DFLegacy.Server/Network/Core/IPacketTapSink.cs`:遥测接缝;`Infrastructure/RuntimeState.cs`:漏斗改造;`Infrastructure/McpModuleLoader.cs`:反射加载器。
- 配置:`server.json` → `DFLegacy.Mcp.{Enabled,Host,Port}`(`Enabled` 代码默认 `false`;当前本地 `server.json` 已开启,`127.0.0.1:12222`)。发布:`publish-mcp.bat` / `publish-mcp.sh` 单独发布模块 DLL。
- 接入与验收:见 [README.md §MCP](../../README.md)(智能体接入配置、工具清单)。
- 尚未实施(属后续方向,不影响本档状态):G2 PVF 工具组(§15.2)、鉴权、pcap 导出、stdio 适配、包注入(§14)。

## 上级文档链(00–06)

00–06 系列已随原工作区同步入库(2026-09-29),均为跨平台可移植性重构的设计与实施记录;源码注释中的引用现已可本地解析:

- `PathResolver.cs`、`PvfEncodings.cs` → [02 · 可移植性契约](02-portability-contracts.md)(R3/R4/R5)
- `DFLegacy.Launcher.csproj` → [01 · 目标架构](01-target-architecture.md)(3.3 补丁资产、2.4)
- `JsonGameStore.cs`、`GameRandomSource.cs` → [06 · 随机源纯托管](06-random-source-managed.md)(随机三分类、唯一性分配规则)
- 正文中的"04·Q6""沿用 04 执行原则"等引用 → [04 · 迁移路线](04-migration-plan.md)
- 编号 07–09 延续该系列次序(09 曾误题"07",已改正)。

## 目录外关联文档

- **[docs/protocol/](../protocol/)**:60A1 客户端协议逆向(枚举全表、各域包格式详表、帧加密、60a4 握手、事件系统)——07/09 的对照与逆向依据;分册导航见 [protocol/README.md](../protocol/README.md)。
- **[docs/servers4a21/](../servers4a21/README.md)**:ServerS4A21 参考服资料(共有协议对比、功能回填计划、参考分册)——仅作参考,结论须经 IDA 验证。
- **[docs/sql/](../sql/)**:schema 三件套(60A1 正式基线 / 60A1 早期草案留档(功能上限参照) / 86S4A21 模式参照)与 test 账号数据种子 SQL(见 08 条目)。
- **[ADMIN_API.md](../../ADMIN_API.md)** / **[README.md](../../README.md)**:管理接口、运行配置选项表、MCP 接入说明。

## 备注

- `/docs` 整体在 `.gitignore` 中:本目录文档仅本地维护,不随仓库分发;提交信息(如 `7e12da7`)是跨仓库对照实现状态的锚点。
- 版本称谓自 2026-09-29 起统一为 **60A1**:历史提法 60CN-ACT1(客户端)、60S1A1/60S1A4(schema 草案)在文档与代码注释中均已收敛为 60A1;`docs/protocol/*-60cn.md` 等**文件名**暂保留原名。
