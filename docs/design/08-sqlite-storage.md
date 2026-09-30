# 08 · SQLite 存储迁移（JsonGameStore → SQLite）

- 状态：**已实现**（SQLite 存储落地，`JsonGameStore` 已删除；关键取舍与判据见 §10，遗留项见 `PENDING.md` P-25/P-26/P-27）。

- 日期：2026-09-28
- 上级文档：[04 · 迁移计划](04-migration-plan.md)（§5 Q6 的遗留议题）、[01 · 目标架构](01-target-architecture.md)
- 关联文档：
  - [02 · 可移植性契约](02-portability-contracts.md)（R5 数据锚点、R7 状态完整落盘）
  - [Server/DAFLegacy.Server/Sqlite/act1_schema.sql](../../Server/DAFLegacy.Server/Sqlite/act1_schema.sql)（**基线 schema 唯一定名来源**：表设计模式取自 86S4A21，功能面上限兼容早期草案；命名统称 60A1；随发布拷到输出目录 `Sqlite/`）
  - [docs/sql/86S4A21_schema.sql](../sql/86S4A21_schema.sql)（参照项目 ServerS4A21 的 schema；**表设计模式参考基准**，仅取模式不搬功能）
  - [feature-backfill-plan-vs-s4a21](../servers4a21/feature-backfill-plan-vs-s4a21.md) §2.3（存储层结构性差异对照）

---

## 1. 背景与目标

迁移前，服务端全部磁盘持久化集中在 `Persistence/JsonGameStore.cs`（约 5,600 行，DI 单例，
已随本方案移除）：整个对象树（账号 → 角色 → 物品/技能/任务/邮件…）序列化为单个
`data/state.json`，每次操作**全量重写**（tmp + `File.Move` 原子替换）。04·Q6 已记录其缺口：

- 每次写都是整文件重写，写放大随数据量线性增长，且写入路径串行在一把 `SemaphoreSlim` 上；
- 无事务边界：跨账号/跨角色的复合变更（仓库升级结算 + 邮件 + 物品）靠「改完内存再一次落盘」，
  崩溃时丢最近一次操作且无法部分回滚；
- 不支持多实例共享数据（README 已知缺口）；
- 无增量演进的版本机制，兼容迁移以「启动时 9 个 `Migrate*Locked` 重写整个 JSON」实现。

参照项目 ServerS4A21 已采用 SQLite（`Microsoft.Data.Sqlite`），其连接管理、基线+增量迁移、
事务边界模型可直接借鉴。本文档定义 DAFLegacy 的 SQLite 存储方案，不携带旧档转换（§6）。

目标：

- **G1** 持久化层换成 SQLite：增量写、事务边界、WAL，消除整文件重写；
- **G2** 不携带旧档转换：无库按基线建空库并播种 `test/test`，有库直接使用；
- **G3** 存储层公开签名不变（Admin API、EntranceService、后台服务零改动或最小改动）；
- **G4** schema 具备基线 + 增量版本机制，后续加功能不再重写全量数据。

非目标：不引入 ORM；不引入 86 版的过渡性功能表（见 §4 D7）；不改 Admin API 的对外标识形态（§9 Q1）。

### 1.1 与 JSON 方案的性能对比（2026-09-29 实测）

**结论先行：迁移的卖点不是"现在更快"。** 当前规模下 JSON 完全够用，SQLite 的价值在于
四件随数据增长必然恶化的事：写放大的增长曲线、事务边界、并发停顿、演进成本。

实测锚点（样例数据 `docs/sql/state.json`：2 账号 / 4 角色 / 341 物品，260KB；
Python json 计时，C# `System.Text.Json` 同量级）：整库序列化（每次操作都要做）
≈ **0.7 ms**，整库读取 ≈ 0.6 ms。即当前每次操作的全量重写体感为零——
**决策依据是曲线与正确性，不是眼前速度。**

| 维度 | state.json（现状） | SQLite（本方案） | 变化性质 |
|---|---|---|---|
| 单次写延迟 | O(全库)：0.7ms@260KB，随数据**线性**增长（25MB 量级 ≈ 70ms + 整文件 IO） | O(变更行)：小事务 0.1–1ms，**与库大小无关** | 增长曲线 O(N)→O(1) |
| 并发停顿 | 全局 `SemaphoreSlim`，所有会话排在一次全量写之后；慢写=全员卡 | 每事务毫秒级；WAL 下读不阻塞写 | 停顿源消失 |
| 磁盘 IO | 每操作整文件写 + tmp 双倍峰值占用 | WAL 追加 + 周期 checkpoint，页级写 | 写放大骤降 |
| 崩溃一致性 | tmp+`File.Move` 单次保存原子；跨对象复合操作（仓库升级=容量+物品+邮件）无事务边界，崩溃丢最近操作 | 一操作一 IMMEDIATE 事务，复合操作同事务；WAL 崩溃恢复，无撕裂文件 | 正确性提升 |
| 启动 | 全量读 + 9 个 `Migrate*Locked` 整库重写式迁移 | 增量迁移只走未执行的版本步 | 演进成本 |
| 多实例 | 不支持（README 已知缺口） | WAL + busy_timeout 同机多进程（写排队读并发） | 新能力 |
| 游戏读路径 | 内存 | 内存（同签名设计，不变） | 无差别 |
| 可读性/运维 | 文件直接可读可 grep | 需 SQL（sqlite3 CLI / Admin API / MCP 存档工具缓解） | JSON 占优 |
| 新增成本 | — | 首个 NuGet 依赖；备份需连 `-wal/-shm` 或 `VACUUM INTO`（K1）；坏基线拒绝启动（I5，特性亦是运维项） | 代价 |

一句话：**迁移买的是"数据涨 100 倍后单次操作仍恒定毫秒级"与"复合操作要么全成要么
全不成"**；本方案中承载这两点的是 D3（基线+增量迁移）与 D4（一方法一事务）。
在数据到几十 MB 之前，速度不构成决策依据。

## 2. 迁移前盘点（JsonGameStore，已移除）

| 项 | 现状 |
|---|---|
| 落盘文件 | `PathResolver.Resolve(DataPath, HomeDirectory)`，默认 `<home>/data/state.json`（R5 锚点） |
| 数据形态 | `JsonGameStoreState`：`Accounts`（`AccountRecord` 树，含全部 `CharacterRecord`）+ 3 个 ID 计数器 + `FatigueDayKey` |
| 写模型 | write-through：约 70 个 async 公有方法，每个方法末尾 `SaveLockedAsync()` 全量重写；并发 = 单把 `SemaphoreSlim` + record `with` 不可变替换 |
| 原子性 | tmp 文件 + `File.Move(overwrite)`；无事务日志、无部分提交 |
| 启动 | `InitializeAsync()`：读取（兼容数组/对象两种根格式）→ 9 个 `Migrate*Locked` 规范化 → 有变更即保存；文件不存在则播种 `test/test` 账号 + 预置角色 |
| 消费方 | `Program.cs`（24 条 `/api/` Admin 路由，另有充值/GM 页面、`/health` 等 7 条页面路由）、`Network/EntranceService`（30+ 调用点，主消费者）、`Infrastructure/FatigueResetService`、`Infrastructure/WeaknessRecoveryService`、SelfTests 4 个文件 |
| 不碰盘的层 | `Game/`（纯规则）、`GameWorld/`（PVF 静态目录）、`RuntimeState`（进程内观测）、`SerializedWriteStream`（socket 写串行化） |

**结论：替换面极小。** 磁盘持久化唯一入口是 `JsonGameStore` 一个类，理论上做一个同签名的
`SqliteGameStore` 即可完成切换（§4 D4）。

## 3. 参照：ServerS4A21 的 SQLite 方案与取舍

参照项目要点（详见其 `Infrastructure/IGameDatabase.cs`、`GameDatabase.cs`、
`Infrastructure/SqliteDatabaseBootstrap.cs`、`Sqlite/SqliteMigrations.cs`、`Sqlite/item_schema.sql`）：

- **短连接**：每次操作现开 `SqliteConnection`，连接串设 `ForeignKeys=true`，每连接
  `PRAGMA busy_timeout=5000`；初始化时设一次 `journal_mode=WAL`（持久属性）。
- **事务边界 = 业务结果**：`Write<T>(conn, tx => …)` 默认 IMMEDIATE 事务；仓储提供
  `(conn, tx)` 静态变体供跨表大事务复用。
- **基线 + 增量两段式迁移**：`item_schema.sql` 永远是当前全量基线（新库零迁移直建）；
  `SqliteMigrations.Steps` 只装基线之后的增量；`schema_metadata(baseline_id, schema_version)`
  与 `PRAGMA user_version` 双写互验；迁移纯 SQL、每步一事务、版本严格连续；坏基线拒绝启动。
- **高频域内存真源**：背包等高频域用「脏槽位标记 + 每分钟批量刷盘 + 会话结束强刷」。

**取舍（本项目的不同选择）：**

| 参照做法 | 本项目决策 | 理由 |
|---|---|---|
| 52 个仓储类 + 手工组合根 | **不搬**：保持 `SqliteGameStore` 单类同签名 | DAFLegacy 持久化入口唯一、数据量小；分仓储只增加改动面（G3） |
| 高频域脏标记 + 分钟级批量刷盘 | **不搬**：每个 Store 方法一个事务直接提交 | 60 版写频率（每客户端操作一次）远低于 86 版背包场景；直写消除「内存与库不一致」窗口 |
| `item_core` 99 字节 BLOB 物品模型 | **不搬**：扁平列（见 §4 D6） | 60 版物品字段少且稳定，BLOB 引入编解码层纯属过度设计 |
| 86 版过渡功能表（佣兵/公会/活动/PvP 战报/协议模板表等） | **不搬** | 低版本不存在这些功能（§4 D7） |
| 基线 + 增量迁移机制、连接/PRAGMA/事务模型、坏库拒绝启动 | **照搬** | 经过验证的核心设施 |

## 4. 目标架构与决策

### D1 技术栈与连接管理

- 引入 `Microsoft.Data.Sqlite`（csproj 目前零 NuGet 依赖，这是第一个包）。
- `IGameDatabase`/`GameDatabase`：短连接 + 每连接 `busy_timeout=5000` + 连接串
  `ForeignKeys=true`；`Write<T>` 默认 IMMEDIATE 事务；`Read<T>` 裸连接委托。
- 库路径沿用 R5 锚点：`PathResolver.Resolve("Data/game.db", HomeDirectory)`，
  可由 `server.json` 的 `DataPath` 覆盖（与 state.json 同锚点规则）。
- 启动初始化一次：`PRAGMA journal_mode=WAL`（持久）。

### D2 初始化与失败清理

仿 `SqliteDatabaseBootstrap`：每库每进程只初始化一次（`InitLock` + 已初始化路径去重）。
库不存在 → 单事务执行基线 DDL + 写 `schema_metadata`；失败 → 删除 `.db/.db-wal/.db-shm`
三件套后抛出，绝不留下半初始化的库。

连接 PRAGMA 不进基线 SQL：`journal_mode=WAL` 与
`foreign_keys` 由 bootstrap 在事务外设置——WAL 在活动事务内执行会直接报错，
`foreign_keys` 在事务内是 no-op（实际经连接串 `ForeignKeys=true` 每连接生效）；
`PRAGMA user_version` 可随事务提交，是基线 SQL 里唯一的 PRAGMA。

### D3 基线 + 增量迁移机制

- `schema_metadata`（singleton 行：`baseline_id`、`schema_version`）+ `PRAGMA user_version`
  双写互验，不一致拒绝启动。
- 互验不变量为两条：
  1. `baseline_id == 100`（数值，60A1 基线，基线 SQL 内直接设置，**恒不变化**）；
     其它值 = 异构库，拒绝启动（提示备份移走）。
  2. `PRAGMA user_version == schema_metadata.schema_version`。基线建库时
     `schema_version` 初值与 `baseline_id`、`user_version` 同为 100；此后每个
     增量步在同一事务内同步推进两者，永不相漂移。
- 增量步骤表 `Steps = [(version, name, Apply(conn, tx))]`：版本严格连续、每步独立事务、
  **只准写 SQL，禁止调用业务 Service/PVF**；执行序 = `step.Apply → 更新 metadata →
  PRAGMA user_version`。
- JSON 时代的「启动 9 连迁移」不再存在：无库建库播种、有库直接用，不做旧档转换（§6）。

### D4 存取策略：内存树读取 + 账号级 diff 单事务落盘

- 领域逻辑集中在 `Sqlite/GameStoreCore`（原 `JsonGameStore.cs` 的全部逻辑），
  落盘是抽象钩子 `TryLoadStateLockedAsync` / `PersistStateLockedAsync`；唯一实现
  `Sqlite/SqliteGameStore` 提供 SQLite 落盘。公开面由 `Sqlite/IGameStore`
  （59 个方法）声明，消费方只依赖接口。
- `Program.cs` 组合根注册 `IGameDatabase` 与 `IGameStore → SqliteGameStore`；
  EntranceService / 后台服务 / Admin API 只把 `JsonGameStore` 换成 `IGameStore`，
  其余代码零改动。
- **写入粒度**：内存 record 树与「上次已落盘」的指纹做 diff，只重写变更部分，
  全部包在单个 IMMEDIATE 事务里；指纹取 record 的 JSON 序列化值（角色指纹排除
  `Cash`，因为点券是账号行单源投影，见 D6）。
  - 账号级：账号标量（用户名/密码/OICQ/预置标记/点券）+ `account_premium` 整表；
  - 角色级：角色行删除（外键级联清空其物品、技能、任务、副本、邮件、疲劳行）
    再整棵重插。
  - **删除一律排在插入之前**：换角色（`ReplaceAllCharactersAsync`）会让新旧角色
    短暂同名并存，而 `characters.name`（账号内唯一）与 `(account_id, slot_index)`
    都是唯一约束。
- 并发模型：`GameStoreCore` 保留 `SemaphoreSlim(1,1)` 作为进程内写串行化（行为与
  原 JSON 实现一致），库层另有 WAL + busy_timeout 兜底。
- 相比原「锁内改树 + 全量落盘」：单次操作不再整库重写，复合变更落在同一事务，
  崩溃要么全成要么全不成。

### D5 标识与主键

- `accounts.account_id` / `characters.character_id` = JSON 的 `AccountUid` / `CharacterNo`（INTEGER）。
  **由存储层显式分配**：账号按 `AccountUidRules` 分段（测试 1..10,000,000、保留段、
  普通 ≥18,000,000，`IsTest(uid)` 靠段判别，不能交给隐式自增取号）；角色全局单调。
  `AUTOINCREMENT` 仅防删除后复用。
- JSON Guid（`AccountRecord.Id`、`CharacterRecord.Id`、物品 `InstanceId`）原样保留为
  `accounts.account_uid` / `characters.character_uid` / `character_items.instance_id`（TEXT），
  Admin API 与存储层公开签名继续用 Guid —— 这是 G3 的关键。
- 物品行采用 86 模式的代理主键（取自其 `character_inventory_items`）：
  `character_items.item_uid INTEGER PRIMARY KEY AUTOINCREMENT` +
  `UNIQUE(character_id, list_type, slot_index)`。内部操作（邮件附件领取、管理定位）
  可按 `item_uid` 直接落到物理行；`instance_id`（Guid）继续作 Admin API 外部标识。
- 计数器（`NextCharacterNo` / `NextNormalAccountUid` / `NextTestAccountUid`）不单独建表：
  载入时按库内已存在的最大值推进（与 `MigrateIdentityLocked` 的推进规则一致），
  `AUTOINCREMENT` 仅防删除后复用。
- 邮件 Id **由存储层显式分配**（`GameStoreCore.AllocateMailIdLocked` 随机取号，
  docs/design/06 §4.1 登记的唯一性分配例外），`character_mail.mail_id` 因此是
  `INTEGER PRIMARY KEY` 而非 `AUTOINCREMENT`。理由见 §10.3。

### D6 货币与字段语义修正

- **点券单源化**：JSON 里 `AccountRecord.Cash` 是共享钱包真源、`CharacterRecord.Cash`
  是同步镜像（`MigrateAccountCashLocked` 维护相等）。入库后只存 `accounts.cash`，
  角色表不再有 `cash` 列；SqliteGameStore 组装 `CharacterRecord` 时从账号行投影填充，
  内存模型无感。
- 语义化改名：`account_name→username`、`password→password_hash`
  （存的是 `dfls-local:` 前缀 SHA256，不是明文）、`storage_capacity→warehouse_capacity`、
  物品表 `expires_at→avatar_remaining_seconds`（按「剩余秒数」语义消费：0 = 永久，
  不是绝对到期时刻）。
- `is_weakened` 改为真源列 `weakness_recovery`（0..100，<100 即虚弱；`IsWeakened` 本来
  就是派生属性），否则每分钟回复进度无处可存。
- 容器列按线上/86 命名一律叫 `list_type`（0 背包 / 1 时装 / 2 金库 / 3 穿戴 / 7 宠物），
  不再用草案的 `container`。
- **不采纳 86 的 `item_core` 99 字节 BLOB 物品模型**：其成立前提是「DB 行 = 线上 99B
  物品结构体的逐字节暂存」——86 客户端的 wire 格式就是该结构体，库→线路零映射。
  60A1 的物品线路字段由 `Network/Builders/` 分组构建器逐字段编码，不存在可逐字节搬运的
  core 结构；引入 BLOB 只会多出一层无人消费的编解码器，且列不可查（Admin 排查退化为
  hex）、单字段更新变成整包重写。60 物品 16 个字段少且稳定，扁平列 + PacketDumper
  golden 测试在协议层守护字节级行为，职责不混。

### D7 最终 schema：以 Sqlite/act1_schema.sql 为基线

23 张表：`schema_metadata`、`server_settings`、`monster_base_exp`、`accounts`、
`account_premium`、`characters`、`character_items`、`character_skills`、`character_quests`、
`character_completed_quests`、`character_dungeon_unlocks`、`character_dungeon_progress`、
`account_dungeon_progress`、`character_mail`、`character_fatigue`、`character_exp_boost`
（以上 16 张为已实现功能面）+ 预留 7 张：`character_friends`、`character_blacklist`、
`guilds`、`guild_members`、`guild_applications`、`private_stores`、`private_store_items`
（D8，未实现；另有 SQLite 自管的 `sqlite_sequence`）。
`schema_metadata.baseline_id = 100`（数值，`PRAGMA user_version` 同值互验）。
基线 SQL 全仓只保留一个文件：`Server/DAFLegacy.Server/Sqlite/act1_schema.sql`（种子
`data_schema.sql`；「统称 60A1」的基线代号不变）；早期草案留档
`docs/sql/60A1_draft_schema.sql` 已删除，其功能面并入基线预留列/表（D8）；
账号资产默认值为 0（`accounts.cash`、`characters.gold`，`CurrencyState` 初始值同步，
种子 `data_schema.sql` 同步再生成）。

设计原则：**表设计模式以 86S4A21 基线为参考骨架，裁剪 60 版
用不到的功能；功能面上限兼容早期草案**。

- **取自 86 的模式**：每域一表、蛇形命名；可变子表带 `created_at/updated_at` 审计列；
  显式 `idx_*` 索引与 CHECK 约束（`list_type` 容器码、三处 `slot_index` 非负、
  `weakness_recovery` 0..100、邮件 `state` 1..3——值域均经代码核实：
  虚弱恢复写入路径钳制在 10..100，邮件创建恒 1、客户端仅可置
  2/3、置 0 走删除）；物品单表 + `list_type` + `item_uid` 代理主键
  （86 的 `character_inventory_items` 形态；五容器共用同一 record，加容器不改表）；
  `schema_metadata` 基线标识 + 版本（与 86 同构）；
  **基线零业务语句**——基线只留 DDL 与静态种子
  （`server_settings` 播种，同 86 的 `dungeon_limit_config` 种子先例），
  数据修正类语句不进基线。
- **86 独有系统裁剪**（60 客户端不存在对应系统）：`item_core` BLOB 物品模型（见 D6）、
  账号仓库/货物（`account_inventory_items`/`account_cargo_state`）、pvp 战报
  （`pvp_matches`/`pvp_match_results`）、佣兵三表、公会/好友/黑名单、活动系列
  （`event_*`）、每日挑战/成就/称号簿/每日计数器、租借/晶体契约/名称装饰卡、
  限购/可用次数/副本进次限额、`inventory_audit_log`、`character_init_flags`/
  `character_subtype0_fields`/`character_subtype1_fields` 协议镜像表、
  `account_settings` BLOB、`get_userinfo_template` 协议模板表。
- **功能上限预留面**（当前代码不读写、恒默认值）：

| 预留能力 | 处置 | 说明 |
|---|---|---|
| 物品 `lock_state`（上锁） | 预留列（`character_items.lock_state`，默认 0） | 当前代码无对应字段，不读写 |
| 任务 `hunt_trigger_format` | 预留列（`character_quests.hunt_trigger_format`，默认 0） | 同上 |
| `account_dungeon_progress`（账号级副本进度，官方 member_dungeon 语义） | 预留表 | 当前按角色存储（`character_dungeon_progress`） |
| `character_exp_boost`（经验药定时 buff） | 预留表 | 秘药一族 rate + 绝对到期时刻；当前三本经验书是即时使用，不落此表；接入秘药时由物品使用路径写入 |
| `storage_capacity` / `is_weakened` / `cash` / 邮件 `extra` | 形态保留（更名/换源） | → `warehouse_capacity`、→ `weakness_recovery`（0..100 真源）、→ `accounts.cash` 单源、→ 附件显式字段（见 D6） |

  明确不收录：`clone_item_id`（克隆装扮，86 代系统，当前代码无对应）。

- **当前实现补充的表列**：`character_dungeon_unlocks`、`character_completed_quests.rewarded_at`
  （领奖状态）、`character_mail.{text, is_new, state}`（保管箱状态机 1/2/3）、`characters`
  的觉醒/师徒/PvP 六列/胜点/预置标记等当前实现的持久化面（逐项落点见 §5 覆盖审计）；
  `server_settings`、`monster_base_exp` 保留待用（GM 倍率/怪物经验，见 §9 Q3）。

### D8 已知缺口功能的预留表设计（并入基线）

- 状态：**表已并入 act1_schema.sql，功能实现未开始**；任一系统实现前按 AGENTS.md D1 以
  IDA + 抓包定协议格式，经用户确认后动代码（D2）。
- 新表七张：
  - `character_friends` / `character_blacklist`：单向关系，character_id 为键，
    双外键级联——单服无跨服好友，任一方删除角色时双向清理即期望行为；
  - `guilds` / `guild_members` / `guild_applications`：会长外键不带级联
    （删除会长角色前须先转让或解散）；rank 档位编码不预设值域；
  - `private_stores` / `private_store_items`：每角色一店；上架条目按
    `item_uid` 物理行引用（一件物品全局至多一条上架记录），成交或撤架
    在同一事务内换主或归位。
- 明确不设表：组队（会话态）、玩家交易（履约为既有表上的单事务原子交换）、
  决斗场（结算写 `characters.pvp_*` 聚合列）——与 D7 裁剪审计表/战报表族的
  取向一致；后续需要留痕时按增量迁移增补。
- 约束：未逆向定论的值域与格式（公会档位、商店名上限、徽标与公会战格式等）
  不预设列约束；落地按 D3 增量迁移推进。

## 5. 已实现功能 → 表覆盖审计

审计方法：以存储层全部公有方法（即当前所有已实现的持久化面）+ Admin API +
两个后台服务为清单，逐项对应到 60A1 表列。**结论：全覆盖，无缺口。**
另对实际数据 `docs/sql/state.json` 做了键级核对：账号 9 键 / 角色 54 键 / 物品 16 键 /
邮件 9 键及 premium/quest/progress 子键全部落在记录字段与表列范围内。

| 已实现功能（代码证据） | 持久化状态 | 落点 |
|---|---|---|
| 账号注册 / 登录 / Admin 改密 / 按 OicqCode 找回 | 用户名（忽略大小写）、密码哈希、OICQ、最后登录 | `accounts` |
| 点券充值 cera（`--add-cash-all`、`/api/client-pay/cera`） | 账号共享钱包 | `accounts.cash` |
| 黑钻充值 diamond / 契约购买 premium（`GrantDiamondToAccountAsync` 等） | 契约到期时刻（22/27/29） | `account_premium` |
| 角色创建 / 选角列表 / 删除角色 | 角色基础 + 槽位唯一 | `characters` |
| 背包 / 穿戴 / 金库 / 时装 / 宠物五容器：移动、整理、合成升级、分解、抽奖发奖 | 物品及其强化/再封印/耐久/封印/品质种子/InstanceId | `character_items`（container 0/1/2/3/7） |
| 金库扩容（升级事务 `TryResolveWarehouseCapacityUpdate`） | 容量 | `characters.warehouse_capacity` |
| 技能加点 / 洗点书 / SP 与额外 SP | 技能表 + SP 两列 | `character_skills`、`characters.skill_points/bonus_skill_points` |
| 任务接取 / 进度触发 / 完成 / 领奖（含启动 `MigrateLegacyQuestRewardsAsync`） | 进行中 / 已完成 / 已领奖 | `character_quests`、`character_completed_quests.rewarded_at` |
| 副本解锁推导回写 / 难度推进 / Admin 改难度 | 解锁集合 + 最高难度 | `character_dungeon_unlocks`、`character_dungeon_progress` |
| 邮件发送 / 附件领取 / 30 天过期 / 255 封上限 | 单附件 + 状态 + Unix 秒时间 | `character_mail` |
| 疲劳消耗 / 每日 06:00 重置（`FatigueResetService`） | 已消耗 + 重置边界 | `character_fatigue` |
| 虚弱结算 / 每分钟恢复（`WeaknessRecoveryService`） | 恢复进度 0..100 | `characters.weakness_recovery` |
| PvP 三本经验书 / 定级 / 战绩 | 点数/段位/胜负/次数/扩展 | `characters.pvp_*` |
| 胜点（背包刷新包随金币下发） | 胜点 | `characters.victory_points` |
| 觉醒（登录与 PVF 校验） | 觉醒类型 | `characters.awakening_type` |
| 师徒加成 | 师父 id + 加成百分比 | `characters.mentor_*` |
| 宠物孵化 / 改名 / 经验等级 / 饱食度 | 宠物五件套字段 | `character_items`（container=7 的 creature_* 列） |
| 时装剩余时间 | 剩余秒数 | `character_items.avatar_remaining_seconds` |
| 预置角色（test 账号播种与启动刷新） | 预置标记 + 初始化门控 | `characters.is_preset`、`accounts.preset_characters_initialized` |
| 组队（Game/Party，60A1 客户端 4 人队） | **会话态，不落盘**：成员绑定 SessionId，下线即散，无任何 store 调用 | 无表（设计如此） |
| 全服喇叭广播 | 瞬时广播，不落盘 | 无表（设计如此） |

## 6. 旧档转换（不提供）

`state.json → SQLite` 的一次性导入**不实现**。理由与替代行为：

- 转换需要一份与 `state.json` 同形的 JSON 解析器常驻服务端，而 JSON 存储本身已被
  移除——留下它只是为了迁完旧档，得不偿失。
- 转换是**有损且高风险**的一次性动作：邮件 Id、计数器、可空集合等语义要在两个
  模型之间人工对齐（本次实施中已经为此改了三处基线，见 §10.3），转换器等于把这些
  风险复制一遍。
- 需要保留旧档的用户继续用旧版本服务端即可；新库从空库起步，由启动流程播种
  `test/test` 账号与预置角色。

因此服务端**不做任何数据搬运**：库文件不存在时按基线建空库并播种，已存在时直接
使用、不重建（`SqliteDatabaseBootstrap` + `SqliteGameStore.TryLoadStateLockedAsync`）。

## 7. 实施里程碑（2026-09-30 结项状态）

| 里程碑 | 状态 | 说明 |
|---|---|---|
| M0 基础设施 | **已完成** | `Microsoft.Data.Sqlite` + `SQLitePCLRaw.bundle_e_sqlite3`；`Sqlite/{IGameDatabase,GameDatabase,SqliteDatabaseBootstrap,SqliteMigrations,SqlitePaths}`；基线 SQL 随发布拷到输出目录；SelfTests 新增 `sqlite-infrastructure` 套件（新建库/重复初始化去重/异构基线拒绝/版本双写互验/失败删三件套/存取往返） |
| M1 SqliteGameStore | **已完成** | 领域逻辑入 `Sqlite/GameStoreCore`，落盘为 `Sqlite/SqliteGameStore`；写入粒度见 §4 D4 |
| M2 建库与播种 | **已完成（范围缩小）** | 只做「无库则按基线建库并播种、有库直接用」；§6 的导入器取消 |
| M3 收尾 | **不适用** | 无 `JsonGameStore` 可删（已随本次改造移除）；`ADMIN_API.md` 头部已改为 `SqliteGameStore` |
| 真机回归 | **未做** | 见 `PENDING.md` P-25 |

## 8. 风险登记

- **K1 备份/运维方式变化**：WAL 模式下热备份不能只拷 `.db`（需连同 `.db-wal/.db-shm`，
  或用 `VACUUM INTO` / backup API）。便捷备份命令与运维说明待办见 `PENDING.md` P-26。
- **K2 邮件 Id 稳定性（已消解）**：`character_mail.mail_id` 由存储层显式分配，
  角色子树重写前后保持不变，客户端 `MAILBOXEXTRACT(98)` 与 Admin 脚本持有的
  Id 不因保存而失效（见 §10.3）。
- **K3 mentor 悬空（已实现）**：`mentor_character_id` 以整数列存放；载入时按
  `character_id → Guid` 映射还原，解析不到目标角色置 NULL（加成随之失效，语义即「无师父」）。
  写入时按全服 Guid→CharacterNo 映射解析，解析不到落 0。
- **K4 行为差异**：账号名/角色名大小写已对齐（代码 `OrdinalIgnoreCase` ↔ 列 `COLLATE NOCASE`；
  边界：NOCASE 仅折叠 ASCII 大小写，`OrdinalIgnoreCase` 是全 Unicode 折叠——对 ASCII 与
  CJK 名称两者等价，带变音符号的拉丁字母存在判别差异，本服名称字符集不触及该边界）；
  并发写从「文件锁全量串行」变为「库锁行级串行」，只松不紧。
- **K5 存取回归**：由 SelfTests 承载——夹具为 C# record 字面量直接播种数据库，
  `program-inline` 等套件在 SQLite 上原样跑通（含跨 store
  重开的往返断言）；`sqlite-infrastructure` 另覆盖基础设施不变量。
- **K6 建库中断（已实现）**：基线建库在单事务内完成，失败即删 `.db/.db-wal/.db-shm`
  三件套再抛出，不留半初始化的库；`sqlite-infrastructure` 断言该行为。
- **K7 备份方式（未做）**：WAL 模式下热备份需连 `-wal/-shm`，或用 `VACUUM INTO`。
  `--backup-db` 之类便捷命令未提供，见 `PENDING.md` P-26。

## 9. 待决问题

- **Q1 Admin API 标识形态**：继续用 Guid（`account_uid`/`character_uid`），还是切到整数 id？
  建议：本次不动（G3），待有跨服/联运需求再议。
- **Q2 组合根形态（已定）**：抽 `IGameStore` 接口（59 个方法），组合根注册
  `IGameStore → SqliteGameStore`。旧实现删除后接口保留：它是消费方与存储形态之间的
  唯一接缝，也是自检注入夹具库的入口。
- **Q3 `server_settings` / `monster_base_exp`（部分已定）**：`server_settings` 已定为**倍率与
  掉落开关的唯一真源**并完成接线（2026-09-30），原 server.json 的 `Drop` / `Experience` 两段与
  `Channel.MaximumUsers` / `CurrentUsers` 全部删除，见 §10.5。`monster_base_exp` 仍只建表不接线：
  怪物经验当前直接取自 PVF 的 `CharacterExperienceCatalog`，该表待官方
  `monster_reward_ref` 数据源确认后再启用。
- **Q4 GM 改参数的入口**：`server_settings` 现在可直接改库生效（重启后读），但尚无 GM 网页或
  Admin API 写入接口，改值需手工 SQL。是否补一个最小读写接口，见 `PENDING.md` P-27。

## 10. 关键取舍与判据

本节登记三处与直觉方案不同的关键取舍及其判据，均为有意决策。

### 10.1 存取采用「内存树 + 账号级 diff 单事务落盘」，未采用逐方法行级事务

- **采用**：内存 record 树仍是读路径（与 §1.1 表格「游戏读路径：内存」一致），
  `SaveLockedAsync` 只做「与上次落盘指纹 diff → 单个 IMMEDIATE 事务增量写」。
- **判据**：
  1. 内存树的跨账号/跨角色逻辑（点券单源化、预置角色刷新、仓库升级结算、经验归一）
     在逐方法行级化后会产生大量跨表拼接，且这些路径无真机可验证，正确性无法保证；
  2. 文档 §1.1 已把「单次写 O(全库) → O(变更行)」列为决策依据，该目标由 diff 落盘
     达成，写入粒度停在「账号标量 + 单个角色子树」不影响增长曲线；
  3. 事务边界这一真正要解决的问题（复合变更要么全成要么全不成）已由「一次操作一个
     事务」达成。
- **未获得的能力**：单字段更新仍会重写该角色的全部子行；跨进程多写者的行级合并
  （靠库锁，不靠字段 diff）。

### 10.2 不提供 `state.json` 一次性导入

见 §6。要点：不保留 JSON 解析器、不做有损转换；无库即播种空库并起 `test/test`。
副作用见 §10.3 第一条（可空语义）与 `PENDING.md` P-25 的真机回归项。

### 10.3 基线 schema 三处定案

| # | 取舍 | 判据 |
|---|---|---|---|
| 1 | `character_items` 与 `character_mail` 的 `avatar_*` / `creature_*` / `equipment_quality_seed` 列**可空**（非 `NOT NULL DEFAULT 0`） | 这些字段在内存模型里本就三态（null / 0 / 值），消费侧一律写成回退值（`EquipmentQualitySeed ?? CountOrValue`、`AvatarAbilityIndex ?? Durability`、`CreatureStomach ?? 100`）。按 0 落库会把回退值改成 0，直接改变下发字节。 |
| 2 | `characters.name` 取 `UNIQUE (account_id, name)`（非全局唯一） | 预置角色名（`GameStoreCore.PresetCharacterName`）与换档角色名册（`MaximumTransferRoster.Archetypes` 的固定档位名）按设计在每个账号下各建一份，跨账号同名是被允许的；玩家自助建角另有全服判重（`GameStoreCore.CreateCharacterLocked` 比对全部账号），比该约束更严。 |
| 3 | `character_mail.mail_id` 为 `INTEGER PRIMARY KEY`，由存储层显式分配（非自增） | 角色子树每次重写都删旧行再重插，若让库自行取号，客户端 `MAILBOXEXTRACT(98)` 与 Admin API 手里的 mail_id 会在任意一次保存后失效。显式写入使 Id 在重写前后不变。取号沿用 `GameStoreCore.AllocateMailIdLocked`（docs/design/06 §4.1 登记的唯一性分配例外）。 |

### 10.4 JSON 时代残留的清理

| 残留 | 处置 | 判据 |
|---|---|---|
| `AccountRecord.LegacyBlackDiamondExpireAt`（`PremiumExpireAt` 兼容字段） | 删除 | 只在旧 JSON 中出现过；`MigratePremiumServicesLocked` 的归并分支随之删除（17→霸王契约号、同号取最大到期值、按号排序的归一保留） |
| `CharacterRecord.LegacyIsWeakened`（`IsWeakened` 布尔兼容字段） | 删除，连带 `MigrateWeaknessRecoveryLocked` 整条启动迁移 | 同上；恢复进度已是 `characters.weakness_recovery` 真源列（0..100，CHECK 约束），旧布尔字段无生产路径可产生 |
| `AccountRecord.Cash`（`int?`） | 改为非空、默认 0 | `null` 只在旧 JSON 中表示「未设置、启动时从首个角色推导」。`accounts.cash` 是 `NOT NULL DEFAULT 0`，null 无法往返；点券改为显式单源字段（`MigrateAccountCashLocked` 退化为「负值收敛 + 同步角色镜像」） |
| 可空集合（五个容器、`Quests`、`Mailbox` 等）的 null 语义 | 载入时归一为非 null 空表 | JSON 里 null 表示「尚未规范化」，内核与登录流程见到 null 会重写一次（`SelectCharacterHandler` 的容器/技能/任务/副本判空门控）。SQLite 每次落盘都写规范化后的集合，该门控永不触发 |
| record 上的 `JsonPropertyName` / `JsonIgnore` 特性 | 删除 | 无 JSON 消费者；`SqliteStateMapper` 的变更指纹仍借用 `System.Text.Json` 序列化 record，但只比对序列化值，与属性名无关 |

### 10.5 倍率与掉落开关以 server_settings 为唯一真源（server.json 不再提供）

- **唯一真源**：倍率与掉落开关的列全部在 `server_settings`——`server.json` 的 `Drop`
  （`Enabled` / `RatePercent` / `EconomicRate` / `ForceDrops`）、`Experience`
  （`MonsterMultiplier` / `ClearMultiplier` / `BlackDiamondClearBonusRate` /
  `ClearEventBonusRate` / `ClearEventStartsAt` / `ClearEventEndsAt`）两段，
  以及 `Channel.MaximumUsers` / `CurrentUsers` 不再提供；
  `ServerOptions` 中的 `DropOptions` 与 `ExperienceOptions` 类型已删除。
  启动时读一次并打印（`ServerSettings.Describe()`），
  之后读路径一律用这份不可变快照——掉落与经验判定每只怪、每次通关都触发，不能每次查库。
- **留在 server.json 的**：端口与监听地址、`Channel.Host` / `Port` / `ServerNumber` 与容量
  `Channel.MaximumUsers`（默认 10；`Channel.Name` / `ChannelNumber` 与 `Channel.CurrentUsers`
  不再提供——显示名/频道号由 PVF 频道目录提供，当前人数为在线会话
  动态值）、`DatabasePath` / `ScriptPvfPath` / `SkillScriptPath`（部署锚点）、
  `MaximumPacketLength` / `RejectBadCrc32` / `EnablePacketTracing` / `HexDumpLimit`（传输与诊断）。
- **基线默认按官方行为取值**：
  - `hell_drop_roll_times` = **1**——基线注释原文即「1 = 官方行为（每只怪最多 1 件）」，
    更大的值属本服可调上限，不是官方值；
  - `hell_recipe_rate` = **0**——深渊不掉首饰设计图是官方行为；
  - 其余倍率一律 1.0，开关为 `drop_enabled=1` / `drop_rate_percent=100` /
    `drop_economic_rate=1` / `drop_force=0`，活动加成为 0 且不设窗口。
  - `clear_bonus_black_diamond_rate` 保留 0.05：黑钻会员经验加成属既有实现行为，
    非倍率类开关，按「不改既有可观察行为」处理。
- **接线点**：`DropCatalog`（DungeonDropGenerator）、`ObjectDropCatalog`
  （ObjectDropGenerator）、`MonsterChampionDropCatalog`、`DungeonExperienceCatalog`、
  `DieMonsterHandler`、`ChannelSessionSenders`（频道容量上报）改注入 `ServerSettings`。
  `EntranceService` 透传该依赖。自检用例按各档位各造一个 `ServerSettings` 快照与对应
  Catalog。
