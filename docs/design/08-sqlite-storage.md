# 08 · SQLite 存储迁移（JsonGameStore → SQLite）

- 状态：**草案**（2026-09-28 起草；2026-09-29 基线重构定稿：统称 **60A1**，表设计模式取自 86S4A21、功能面上限兼容早期草案，见 D7）
- 日期：2026-09-28
- 上级文档：[04 · 迁移计划](04-migration-plan.md)（§5 Q6 的遗留议题）、[01 · 目标架构](01-target-architecture.md)
- 关联文档：
  - [02 · 可移植性契约](02-portability-contracts.md)（R5 数据锚点、R7 状态完整落盘）
  - [docs/sql/60A1_schema.sql](../sql/60A1_schema.sql)（**本次产出的最终基线 schema**：表设计模式取自 86S4A21，功能面上限兼容早期草案；命名统称 60A1）
  - [docs/sql/60A1_draft_schema.sql](../sql/60A1_draft_schema.sql)（早期 60 版草案；本基线的**功能上限参照**，不再演进）
  - [docs/sql/86S4A21_schema.sql](../sql/86S4A21_schema.sql)（参照项目 ServerS4A21 的 schema；**表设计模式参考基准**，仅取模式不搬功能）
  - [feature-backfill-plan-vs-s4a21](../servers4a21/feature-backfill-plan-vs-s4a21.md) §2.3（存储层结构性差异对照）

---

## 1. 背景与目标

当前服务端全部磁盘持久化集中在 `Persistence/JsonGameStore.cs`（约 5,600 行，DI 单例）：
整个对象树（账号 → 角色 → 物品/技能/任务/邮件…）序列化为单个 `data/state.json`，
每次操作**全量重写**（tmp + `File.Move` 原子替换）。04·Q6 已记录其缺口：

- 每次写都是整文件重写，写放大随数据量线性增长，且写入路径串行在一把 `SemaphoreSlim` 上；
- 无事务边界：跨账号/跨角色的复合变更（仓库升级结算 + 邮件 + 物品）靠「改完内存再一次落盘」，
  崩溃时丢最近一次操作且无法部分回滚；
- 不支持多实例共享数据（README 已知缺口）；
- 无增量演进的版本机制，兼容迁移以「启动时 9 个 `Migrate*Locked` 重写整个 JSON」实现。

参照项目 ServerS4A21 已采用 SQLite（`Microsoft.Data.Sqlite`），其连接管理、基线+增量迁移、
事务边界模型可直接借鉴。本文档定义 DFLegacy 的 SQLite 存储方案与一次性数据迁移设计。

目标：

- **G1** 持久化层换成 SQLite：增量写、事务边界、WAL，消除整文件重写；
- **G2** 现有 `state.json` 数据一次性无损迁入；
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

## 2. 现状盘点（JsonGameStore）

| 项 | 现状 |
|---|---|
| 落盘文件 | `PathResolver.Resolve(DataPath, HomeDirectory)`，默认 `<home>/data/state.json`（R5 锚点） |
| 数据形态 | `JsonGameStoreState`：`Accounts`（`AccountRecord` 树，含全部 `CharacterRecord`）+ 3 个 ID 计数器 + `FatigueDayKey` |
| 写模型 | write-through：约 70 个 async 公有方法，每个方法末尾 `SaveLockedAsync()` 全量重写；并发 = 单把 `SemaphoreSlim` + record `with` 不可变替换 |
| 原子性 | tmp 文件 + `File.Move(overwrite)`；无事务日志、无部分提交 |
| 启动 | `InitializeAsync()`：读取（兼容数组/对象两种根格式）→ 9 个 `Migrate*Locked` 规范化 → 有变更即保存；文件不存在则播种 `test/test` 账号 + 预置角色 |
| 消费方 | `Program.cs`（约 15 条 Admin API 路由）、`Network/EntranceService`（30+ 调用点，主消费者）、`Services/FatigueResetService`、`Services/WeaknessRecoveryService`、SelfTests 4 个文件 |
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
| 52 个仓储类 + 手工组合根 | **不搬**：保持 `SqliteGameStore` 单类同签名 | DFLegacy 持久化入口唯一、数据量小；分仓储只增加改动面（G3） |
| 高频域脏标记 + 分钟级批量刷盘 | **不搬**：每个 Store 方法一个事务直接提交 | 60 版写频率（每客户端操作一次）远低于 86 版背包场景；直写消除「内存与库不一致」窗口 |
| `item_core` 99 字节 BLOB 物品模型 | **不搬**：扁平列（见 §4 D6） | 60 版物品字段少且稳定，BLOB 引入编解码层纯属过度设计 |
| 86 版过渡功能表（佣兵/公会/活动/PvP 战报/协议模板表等） | **不搬** | 低版本不存在这些功能（§4 D7） |
| 基线 + 增量迁移机制、连接/PRAGMA/事务模型、坏库拒绝启动 | **照搬** | 经过验证的核心设施 |

## 4. 目标架构与决策

### D1 技术栈与连接管理

- 引入 `Microsoft.Data.Sqlite`（csproj 目前零 NuGet 依赖，这是第一个包）。
- `IGameDatabase`/`GameDatabase`：短连接 + 每连接 `busy_timeout=5000` + 连接串
  `ForeignKeys=true`；`Write<T>` 默认 IMMEDIATE 事务；`Read<T>` 裸连接委托。
- 库路径沿用 R5 锚点：`PathResolver.Resolve("data/game.db", HomeDirectory)`，
  可由 `server.json` 的 `DataPath` 覆盖（与 state.json 同锚点规则）。
- 启动初始化一次：`PRAGMA journal_mode=WAL`（持久）。

### D2 初始化与失败清理

仿 `SqliteDatabaseBootstrap`：每库每进程只初始化一次（`InitLock` + 已初始化路径去重）。
库不存在 → 单事务执行基线 SQL + 写 `schema_metadata`；失败 → 删除 `.db/.db-wal/.db-shm`
三件套后抛出，绝不留下半初始化的库。

### D3 基线 + 增量迁移机制

- `schema_metadata`（singleton 行：`baseline_id`、`schema_version`）+ `PRAGMA user_version`
  双写互验，不一致拒绝启动。
- `baseline_id = 100`（数值，60A1 基线；`PRAGMA user_version` 同值 100 双写互验，
  基线 SQL 内直接设置）；其它值 = 异构库，拒绝启动（提示备份移走）。
- 增量步骤表 `Steps = [(version, name, Apply(conn, tx))]`：版本严格连续、每步独立事务、
  **只准写 SQL，禁止调用业务 Service/PVF**；执行序 = `step.Apply → 更新 metadata →
  PRAGMA user_version`。
- JSON 时代的「启动 9 连迁移」由 §6 的导入器承接，入库后不再需要整库重写式迁移。

### D4 存取策略：同签名替换

- 新建 `Persistence/SqliteGameStore`，**公开方法签名与 `JsonGameStore` 完全一致**
  （Guid 入参、返回 `CharacterRecord`/`AccountRecord`）。
- `Program.cs` 组合根把 `AddSingleton<JsonGameStore>()` 换成
  `AddSingleton<JsonGameStore>(sp => new SqliteGameStore(...))`（或抽出共同接口，二选一，
  实施时按改动面最小者定）；EntranceService/后台服务/Admin API 代码零改动。
- 方法内部模式从「锁内改树 + 全量落盘」改为「**一个方法 = 一个 IMMEDIATE 事务**」：
  读相关行 → 内存组装 record（`with`）→ 同事务写回。跨对象复合操作
  （如仓库升级：容量 + 物品 + 邮件）天然落在同一事务，替代旧「改完内存一次落盘」。
- 并发模型：`SemaphoreSlim(1,1)` 保留为进程内写串行化（行为与现状一致），库层另有
  WAL + busy_timeout 兜底。后续如需多实例，再移除进程内锁、依赖库锁（不在本期）。

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
- JSON 根部的三个计数器（`NextCharacterNo` 等）由「导入时推进到已导入最大值 +
  AUTOINCREMENT 防复用」取代，不再单独建表存计数器。
- 邮件 Id：JSON 是全库随机分配的 uint，入库改为 `AUTOINCREMENT` 全局自增
  （重编号安全性见 §8 K2）。

### D6 货币与字段语义修正

- **点券单源化**：JSON 里 `AccountRecord.Cash` 是共享钱包真源、`CharacterRecord.Cash`
  是同步镜像（`MigrateAccountCashLocked` 维护相等）。入库后只存 `accounts.cash`，
  角色表不再有 `cash` 列；SqliteGameStore 组装 `CharacterRecord` 时从账号行投影填充，
  内存模型无感。
- 语义化改名（相对 早期草案）：`account_name→username`、`password→password_hash`
  （存的是 `dfls-local:` 前缀 SHA256，不是明文）、`storage_capacity→warehouse_capacity`、
  物品表 `expires_at→avatar_remaining_seconds`（**草案原名的语义是错的**：DF2008 把它当
  「剩余秒数」消费，0 = 永久，不是绝对到期时刻）。
- `is_weakened` 改为真源列 `weakness_recovery`（0..100，<100 即虚弱；`IsWeakened` 本来
  就是派生属性），否则每分钟回复进度无处可存。
- 容器列按线上/86 命名一律叫 `list_type`（0 背包 / 1 时装 / 2 金库 / 3 穿戴 / 7 宠物），
  不再用草案的 `container`。
- **不采纳 86 的 `item_core` 99 字节 BLOB 物品模型**：其成立前提是「DB 行 = 线上 99B
  物品结构体的逐字节暂存」——86 客户端的 wire 格式就是该结构体，库→线路零映射。
  60A1 的物品线路字段由 `GameProtocolEngine` 逐字段编码，不存在可逐字节搬运的
  core 结构；引入 BLOB 只会多出一层无人消费的编解码器，且列不可查（Admin 排查退化为
  hex）、单字段更新变成整包重写。60 物品 16 个字段少且稳定，扁平列 + PacketDumper
  golden 测试在协议层守护字节级行为，职责不混。

### D7 最终 schema：以 docs/sql/60A1_schema.sql 为基线

16 张表：`schema_metadata`、`server_settings`、`monster_base_exp`、`accounts`、
`account_premium`、`characters`、`character_items`、`character_skills`、`character_quests`、
`character_completed_quests`、`character_dungeon_unlocks`、`character_dungeon_progress`、
`account_dungeon_progress`、`character_mail`、`character_fatigue`、`character_exp_boost`
（+ SQLite 自管的 `sqlite_sequence`）。`schema_metadata.baseline_id = 100`（数值，
`PRAGMA user_version` 同值互验）。

设计原则（2026-09-29 重构定稿）：**表设计模式以 86S4A21 基线为参考骨架，裁剪 60 版
用不到的功能；功能面上限兼容早期草案**。

- **取自 86 的模式**：每域一表、蛇形命名；可变子表带 `created_at/updated_at` 审计列；
  显式 `idx_*` 索引与 CHECK 约束；物品单表 + `list_type` + `item_uid` 代理主键
  （86 的 `character_inventory_items` 形态，早期草案 的五张分容器表据此合一——五容器
  共用同一 record，加容器不改表）；`schema_metadata` 基线标识 + 版本（与 86 同构）；
  **基线零业务语句**——早期草案 里的建库 UPDATE（位置修正）与 `account_dungeon_progress`
  合并 INSERT 一律移出基线，归一次性导入器/增量迁移，基线只留 DDL 与静态种子
  （`server_settings` 播种，同 86 的 `dungeon_limit_config` 种子先例）。
- **86 独有系统裁剪**（60 客户端不存在对应系统）：`item_core` BLOB 物品模型（见 D6）、
  账号仓库/货物（`account_inventory_items`/`account_cargo_state`）、pvp 战报
  （`pvp_matches`/`pvp_match_results`）、佣兵三表、公会/好友/黑名单、活动系列
  （`event_*`）、每日挑战/成就/称号簿/每日计数器、租借/晶体契约/名称装饰卡、
  限购/可用次数/副本进次限额、`inventory_audit_log`、`character_init_flags`/
  `character_subtype0_fields`/`character_subtype1_fields` 协议镜像表、
  `account_settings` BLOB、`get_userinfo_template` 协议模板表。
- **功能上限兼容早期草案**（恢复为预留面，当前代码不读写、恒默认值）：

| 早期草案能力 | 处置 | 说明 |
|---|---|---|
| 物品 `lock_state`（上锁） | 恢复列（`character_items.lock_state`，默认 0） | 当前代码无对应字段，不读写 |
| 任务 `hunt_trigger_format` | 恢复列（`character_quests.hunt_trigger_format`，默认 0） | 同上 |
| `account_dungeon_progress`（账号级副本进度，官方 member_dungeon 语义） | 恢复表（预留） | 当前按角色存储（`character_dungeon_progress`）；早期草案 的"每次启动合并 INSERT"不保留，合并属导入/迁移逻辑 |
| `character_exp_boost`（经验药定时 buff） | 恢复表（预留） | 秘药一族 rate + 绝对到期时刻；当前三本经验书是即时使用，不落此表；接入秘药时由物品使用路径写入 |
| `storage_capacity` / `is_weakened` / `cash` / 邮件 `extra` | 形态保留（更名/换源） | → `warehouse_capacity`、→ `weakness_recovery`（0..100 真源）、→ `accounts.cash` 单源、→ 附件显式字段（见 D6） |

  明确不恢复：`clone_item_id`（克隆装扮，86 代系统；早期草案属过度收录，当前代码无对应）。

- **当前代码需要而 早期草案缺失的补充**：`character_dungeon_unlocks`（JSON
  `UnlockedDungeonIds` 草案漏掉）、`character_completed_quests.rewarded_at`（领奖状态）、
  `character_mail.{text, is_new, state}`（保管箱状态机 1/2/3）、`characters` 的
  觉醒/师徒/PvP 六列/胜点/预置标记等当前实现的持久化面（逐项落点见 §5 覆盖审计）；
  `server_settings`、`monster_base_exp` 保留待用（GM 倍率/怪物经验，见 §9 Q3）。

## 5. 已实现功能 → 表覆盖审计

审计方法：以 `JsonGameStore` 全部公有方法（即当前所有已实现的持久化面）+ Admin API +
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

## 6. 一次性数据导入（state.json → SQLite）

### 6.1 触发与安全

- 启动初始化时：目标库不存在（或无 `schema_metadata` 行）且 `state.json` 存在 → 进入导入。
- 导入在**单个 IMMEDIATE 事务**内完成：先执行基线 DDL，再搬数据，最后写 `schema_metadata`。
  任一步失败整体回滚，`state.json` 原样不动（§8 K6）。
- 成功后将 `state.json` 改名为 `state.json.imported-<yyyyMMddHHmmss>` 保留作备份
  （**不删除**；含密码哈希，处置同现状）。此后库为真源，同名 `state.json` 再出现仅告警不导入。
- 种子产物：`tools/export_state_sql.py`（Python，随 tools/ 既有工具惯例）把 `state.json`
  转换为**数据种子 SQL** —— 只保留 `test` 账号数据（其余账号一律不进入产物），产出
  `docs/sql/60A1_seed_test.sql`（全新库先执行 60A1 基线 schema 再应用本文件）；
  生成时先在内存库回放产物 SQL 并与源 JSON 逐字段校验，不过不落盘。不再生成二进制
  样例库。M2 的服务端内导入器（运行期导入本地**完整** `state.json`）以同一规则用 C# 落地。

### 6.2 搬运规则

| JSON | 规则 |
|---|---|
| `AccountRecord.Id` / `CharacterRecord.Id` / 物品 `InstanceId` | 原样入 `*_uid` / `instance_id`（TEXT） |
| `AccountUid` / `CharacterNo` | 非 0 直接作主键；为 0（早期数据）按 `AccountUidRules` 现场分配；全部导完后把「下一个可分配 id」推进到已导入最大值之上 |
| `AccountRecord.Cash` | 入 `accounts.cash`（null → 50,000 默认）。角色镜像与账号值由 `MigrateAccountCashLocked` 保证相等，以账号值为准 |
| `PremiumServices` / 兼容字段 `PremiumExpireAt` | 归并进 `account_premium`（执行旧 `MigratePremiumServicesLocked` 的归并逻辑后再清源字段） |
| 五个物品列表 | `Inventory/Equipment/Warehouse/AvatarInventory/CreatureInventory` → `character_items` container = 0/3/2/1/7 |
| `Quests` / `CompletedQuestIds` / `RewardedQuestIds` | 前者入 `character_quests`；后两者合入 `character_completed_quests`，领过奖的行带 `rewarded_at`（导完即跑一次 `MigrateLegacyQuestRewards` 逻辑补发遗留奖励） |
| `UnlockedDungeonIds` / `DungeonProgress` | 各入对应表（后者若为 null，执行登录期同款「按解锁推导难度 1」的旧迁移） |
| 邮件 `Id` | 弃用随机 uint，按 `AUTOINCREMENT` 重新编号；`SentAt/ExpiresAt/IsNew/State` 原样入列 |
| `UsedFatigue` + 根部 `FatigueDayKey` | `consumed` 原样；`last_reset_at` = 导入时刻最近的一个「上午 6 点」边界（Unix 秒），今日配额延续、明日照常滚动（全局日键由每角色边界列取代） |
| `MigrateIdentityLocked` / `MigrateItemInstanceIdsLocked` 等启动迁移 | 语义并入导入器（id 补齐、InstanceId 回填）；导入完成后启动流程不再对 SQLite 执行整库重写式迁移 |
| `NormalizeCharacterExperience` / `SettleWarehouseUpgradeItems` / 预置角色刷新 | **保留在启动流程**（依赖 PVF 目录/代码进度表，非存储迁移）：导入器只搬数，规范化照旧在服务端启动时走正常保存路径 |
| 位置 `(474, 234)` | 视为旧版错误落点（Elvengard `[gate]` 回归点，越出区域 1 可移动矩形），入库时一次性修正为 `(474, 336)`——即 早期草案 建库 UPDATE 的语义，移到导入器执行（基线零业务语句） |
| 物品行 | `item_uid` 由 AUTOINCREMENT 分配；`instance_id` 原样入库；预留列 `lock_state` 恒 0 |
| 任务行 | `hunt_trigger_format` 预留列恒 0 |
| `test/test` 默认账号 + 预置角色 | 仅当「无库且无 state.json」时播种，行为与现状一致 |

## 7. 实施里程碑

- **M0 基础设施**：引入 `Microsoft.Data.Sqlite`；`IGameDatabase/GameDatabase/
  SqliteDatabaseBootstrap/SqliteMigrations`；基线 SQL（60A1）随发布拷贝到输出目录；
  SelfTests：新建库 / 重复初始化 / 坏基线拒绝 / 迁移版本连续性。
- **M1 SqliteGameStore**：同签名实现全部约 70 个方法（一个方法一个事务）；
  现有 4 个 SelfTests 冒烟在 JSON/SQLite 两实现上对跑，结果一致才放行。
- **M2 导入与切换**：§6 导入器 + 组合根切换 + 默认播种；Admin API 手工回归
  （账号/角色/邮件/疲劳/充值/难度六组接口）。
- **M3 收尾**：观察一个稳定版本后删除 `JsonGameStore`；`ADMIN_API.md` 头部
  「持久化以 JsonGameStore.cs 为准」改为 SqliteGameStore；04·Q6 关闭。

## 8. 风险登记

- **K1 备份/运维方式变化**：WAL 模式下热备份不能只拷 `.db`（需连同 `.db-wal/.db-shm`，
  或用 `VACUUM INTO` / backup API）。实施时在 README/运维说明补充，提供 `--backup-db` 之类的
  便捷命令（待定）。
- **K2 邮件重编号**：导入后 mail_id 变化。客户端对邮件 Id 无跨会话引用（每次登录全量拉取），
  安全；但若存在导入前后按邮件 Id 引用的 Admin 脚本，需注意重编号。
- **K3 mentor 悬空**：`MentorCharacterId` 在 JSON 是无约束 Guid；导入时解析不到目标角色置 0
  （加成随之失效，语义即「无师父」）。
- **K4 行为差异**：账号名/角色名大小写已对齐（代码 `OrdinalIgnoreCase` ↔ 列 `COLLATE NOCASE`）；
  并发写从「文件锁全量串行」变为「库锁行级串行」，只松不紧。
- **K5 导入器回归**：导入正确性靠「导入后 `GET /api/accounts` 全量导出与 state.json 逐字段比对」
  的 SelfTest 断言兜底（M2 验收项）。
- **K6 导入中断**：单事务 + 库文件失败即删三件套，最坏情况回到「无库 + 原始 state.json」，
  可重复导入。

## 9. 待决问题

- **Q1 Admin API 标识形态**：继续用 Guid（`account_uid`/`character_uid`），还是切到整数 id？
  建议：本次不动（G3），待有跨服/联运需求再议。
- **Q2 组合根形态**：`AddSingleton<JsonGameStore>` 直接换成返回 `SqliteGameStore` 的工厂
  （零接口抽象，最快），还是抽 `IGameStore` 接口（为 M1 双实现对跑方便）？建议：抽接口，
  M3 删旧实现后可保留接口不删。
- **Q3 `server_settings` / `monster_base_exp`**：本次只建表 + 播种默认值，GM 倍率网页与
  怪物经验表的功能实现另行排期；是否随 M2 顺带做最小读写（读一次启动倍率）？建议：不做，
  保持本次改动面收敛。
