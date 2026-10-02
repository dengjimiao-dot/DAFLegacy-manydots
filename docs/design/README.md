# DAFLegacy 设计文档汇总

- 更新:2026-10-01
- 本目录收录服务端结构性设计文档;各文档状态以其自身标注 + 代码实际落地核定,与本页冲突时以代码为真。

## 状态图例

| 状态 | 含义 |
| --- | --- |
| **草案** | 设计已成文(含"草案/待实施/设计定稿"等各文档自身表述),代码尚未实施 |
| **已实现** | 设计已随代码落地,文档即实现依据与行为说明 |
| **已作废** | 提案被后续方案取代,仅作技术记录留档 |

## 文档索引

| # | 文档 | 主题 | 状态 |
| --- | --- | --- | --- |
| 00 | [设计开档总纲:跨平台可移植性重构](00-kickoff.md) | 可移植性重构的开档总纲、原则与 01–06 文档导航 | 总纲(01–06 已实施) |
| 01 | [目标架构:Server 层与 Client 层](01-target-architecture.md) | Server/Client 分层目标架构与平台边界 | **已实现** |
| 02 | [可移植性契约 R1–R11](02-portability-contracts.md) | "可移植"的可判定定义(R1–R11;路径锚点、编码契约等) | **已实现** |
| 03 | [构建、发布与 CI](03-build-release-ci.md) | 构建、发布流程与 CI(publish 脚本、发布布局) | **已实现** |
| 04 | [迁移路线、验收与风险](04-migration-plan.md) | 跨平台重构的迁移路线、验收标准与风险登记 | **已实现** |
| 05 | [RandomNative 跨平台化提案](05-randomnative-crossplatform.md) | 原生随机模块跨平台化提案 | **已作废**(后继方案 06,仅留档) |
| 06 | [随机源降级为纯托管实现](06-random-source-managed.md) | 服务端随机源托管化(`Infrastructure/`,原 `Random/` 已并入),与 RandomNative 解耦(模块保留至 Client 层) | **已实现** |
| 07 | [协议层重构:收发包管线与分发架构](07-protocol-layer-refactor.md) | 71 协议自巨链迁入「注册表分发 + 通用管线(状态门槛/出站计账)+ 6 大类 15 组 handler + 出站 Channel 单写者 + 定时器基建」架构,现行形态见其 §11 | **已实现** |
| 08 | [SQLite 存储迁移(已落地)](08-sqlite-storage.md) | `JsonGameStore` 已整体移除，改为 SQLite 增量写 + 事务边界 + 基线/增量迁移(取舍见该文 §10) | **已实现** |
| 09 | [DAF-MCP 总网关(一):协议包实时监听](09-mcp-packet-tap.md) | 服务端内置标准 MCP 服务器(默认关闭),`packet_watch` 游标长轮询实时暴露收发包(报文名+明文富化),供智能体边抓边分析协议 | **已实现** |
| 10 | [网络数据面整体优化:城镇广播中枢与 UDP/NAT](10-udp-nat-dataplane.md) | `TownPresenceService` 中枢独立化(观察者/查找/三级广播/背压/网络端点态)+ UDP 端点学习按会话认领 + NAT 探测长度校验与限流 + 聚合遥测;三批次实施,线上字节零变更 | **已实现**(真机待 P-36) |
| 11 | [社交系统迁移计划](11-social-systems-migration.md) | 组队补完、城镇广播基建、好友/黑名单、玩家交易、个人商店、工会、PVP 的分阶段回填(每系统 IDA 验证先行;P0-P6 依赖序与验收标准) | **实施中**(P0/P2/P3/P4 落地,P5/P6 待真机后) |
| 12 | [社交系统存储设计](12-sql-social-storage.md) | P0-P6 的 SQL 面:预置 7 表零 DDL 启用、事务边界(成交即时换主不留账)、黑名单锚定裁决、工会增量列候选、实施前置裁决(J1/J8 已闭合) | **实施中** |

## 各文档要点与代码落点

### 07 · 协议层重构(已实现)

- 三个结构性问题:分发不可扩展(P1)、单写者靠纪律(P2,已有小喇叭 fire-and-forget 直写裂缝)、定时器语义埋在业务里(P3)。
- 方案:71 协议号 → 15 个 handler 组、6 个功能大类(组名对齐 ServerS4A21 词汇,4.8.3 映射表);`GameCommandRegistry` 字典分发(启动期重复注册即失败)+ 通用管线(状态门槛声明式收口、出站计账单点);`ChannelSessionState` 分域状态;有界 `Channel<T>` 出站泵;单点 `WhenAny` 定时器调度。strangler 式 R0–R7 迁移。
- 硬约束:字节级行为冻结(golden 测试先行,旧实现产出即标准);协议核心层与领域层零改动。
- 落地形态:71/71 协议挂载新链路,登记偏差与遗留项见该文 §11(定时器收编与真机全流程冒烟待办见 `PENDING.md` P-10、P-01)。

### 08 · SQLite 存储迁移(已实现)

- 原现状:唯一持久化入口 `Persistence/JsonGameStore.cs`(约 5,600 行):单文件 `state.json` 全量重写、无事务边界。**该实现已整体移除**。
- 落地形态:领域逻辑集中在 `Server/DAFLegacy.Server/Sqlite/GameStoreCore.cs`(公开面 `IGameStore`,消费方只依赖接口),落盘为同目录 `SqliteGameStore.cs` + `SqliteStateStore.cs`;写入粒度为「内存树 diff → 单个 IMMEDIATE 事务增量写」,不再整库重写。基础设施 `GameDatabase` / `SqliteDatabaseBootstrap` / `SqliteMigrations` 参照 ServerS4A21 的短连接 + 基线/增量两段式迁移形态。
- 旧档不转换:无库则按基线建空库并播种 `test/test`,有库直接用。取舍与三处基线修正见该文 §10。
- schema 基线:[Server/DAFLegacy.Server/Sqlite/act1_schema.sql](../../Server/DAFLegacy.Server/Sqlite/act1_schema.sql)(统称 60A1,23 张表,其中 7 张为未实现功能的预留表,见该文档 D8;**表设计模式取自 [86S4A21](../sql/86S4A21_schema.sql)、功能面上限兼容已移除的早期草案**;`lock_state`/`hunt_trigger_format`/`account_dungeon_progress`/`character_exp_boost` 为草案兼容预留;不采纳 86 的 item_core BLOB,物品走扁平列 + item_uid 代理主键)。
- 验证:SelfTests 23 套件全绿,其中 `sqlite-infrastructure` 覆盖新建库/重复初始化去重/异构基线拒绝/版本双写互验/失败删三件套/存取往返;真机回归未做,见 `PENDING.md` P-25。

### 09 · DAF-MCP 协议包实时监听(已实现)

- 代码落点:
  - `Server/DAFLegacy.Mcp/`:独立模块项目(`McpModule`、`McpHost`、`Tap/PacketTap{Event,Hub,Filter,Tools}`、`Tap/PacketNamesResource`)——§6 全部 6 个工具(`packet_watch/get/wait`、`session_list`、`packet_names_search`、`tap_status`)均已提供。
  - `Server/DAFLegacy.Server/Network/Core/IPacketTapSink.cs`:遥测接缝;`Infrastructure/RuntimeState.cs`:漏斗改造;`Infrastructure/McpModuleLoader.cs`:反射加载器。
- 配置:`server.json` → `DAFLegacy.Mcp.{Enabled,Host,Port}`(`Enabled` 代码默认 `false`;当前本地 `server.json` 已开启,`127.0.0.1:12222`)。发布:`publish-mcp.bat` / `publish-mcp.sh` 单独发布模块 DLL。
- 接入与验收:见 [README.md §MCP](../../README.md)(智能体接入配置、工具清单)。
- 尚未实施(属后续方向,不影响本档状态):G2 PVF 工具组(§15.2)、鉴权、pcap 导出、stdio 适配、包注入(§14)。

## 上级文档链(00–06)

00–06 系列已随原工作区同步入库(2026-09-29),均为跨平台可移植性重构的设计与实施记录;源码注释中的引用现已可本地解析:

- `PathResolver.cs`、`PvfEncodings.cs` → [02 · 可移植性契约](02-portability-contracts.md)(R3/R4/R5)
- `DAFLegacy.Launcher.csproj` → [01 · 目标架构](01-target-architecture.md)(3.3 补丁资产、2.4)
- `JsonGameStore.cs`、`GameRandomSource.cs` → [06 · 随机源纯托管](06-random-source-managed.md)(随机三分类、唯一性分配规则)
- 正文中的"04·Q6""沿用 04 执行原则"等引用 → [04 · 迁移路线](04-migration-plan.md)
- 编号 07–09 延续该系列次序。

## 目录外关联文档

- **[docs/protocol/](../protocol/)**:60A1 客户端协议逆向(枚举全表、各域包格式详表、帧加密、60a4 握手、事件系统)——07/09 的对照与逆向依据;分册导航见 [protocol/README.md](../protocol/README.md)。
- **[docs/servers4a21/](../servers4a21/README.md)**:ServerS4A21 参考服资料(共有协议对比、功能回填计划、参考分册)——仅作参考,结论须经 IDA 验证。
- **[docs/sql/](../sql/)**:test 账号种子 `data_schema.sql`、样例数据 `state.json` 与 86S4A21 模式参照 `86S4A21_schema.sql`(60A1 正式基线在 `Server/DAFLegacy.Server/Sqlite/act1_schema.sql`,见 08 条目)。
- **[ADMIN_API.md](../../ADMIN_API.md)** / **[README.md](../../README.md)**:管理接口、运行配置选项表、MCP 接入说明。

## 备注

- `/docs` 整体在 `.gitignore` 中:本目录文档仅本地维护,不随仓库分发;提交信息(如 `7e12da7`)是跨仓库对照实现状态的锚点。
- 版本称谓自 2026-09-29 起统一为 **60A1**:历史提法 60CN-ACT1(客户端)、60S1A1/60S1A4(schema 草案)在文档与代码注释中均已收敛为 60A1;`docs/protocol/*-60cn.md` 等**文件名**暂保留原名。
