-- ============================================================================
-- DFLegacy SQLite 存储基线。
-- 设计约定（详见 docs/design/08-sqlite-storage.md）：
--
--   * 命名统称 60A1；schema_metadata.baseline_id = 100（数值），
--     PRAGMA user_version 同值双写互验；其它值 = 异构库，拒绝启动。
--
--   * 表设计模式以 docs/sql/86S4A21_schema.sql（ServerS4A21 基线）为参考骨架：
--       - 每域一表、蛇形命名；可变子表带 created_at/updated_at 审计列；
--         显式 idx_* 索引与 CHECK 约束；
--       - 物品采用单表 + list_type 容器码 + item_uid 代理主键
--         （86 的 character_inventory_items 形态；早期草案的五张分容器表
--         据此合一——五容器共用同一 record，加容器不改表）；
--       - 基线零业务语句：早期草案里的建库 UPDATE（位置修正）与
--         account_dungeon_progress 合并 INSERT 一律移出基线，
--         归一次性导入器 / 增量迁移；基线只留 DDL 与静态种子
--         （server_settings 播种，同 86 的 dungeon_limit_config 先例）。
--
--   * 功能面：上限兼容 docs/sql/60A1_draft_schema.sql（早期草案留档）的
--     全部功能（lock_state / hunt_trigger_format / account_dungeon_progress /
--     character_exp_boost 以"预留"形式保留，当前代码不读写）；
--     86 版独有系统全部裁剪：item_core BLOB 物品模型、账号仓库/货物、
--     pvp_matches/pvp_match_results 战报、佣兵三表、公会/好友/黑名单、
--     活动系列(event_*)、每日挑战/成就/称号簿/每日计数器、租借/晶体契约/
--     名称装饰卡、限购/可用次数/副本进次限额、inventory_audit_log、
--     character_init_flags/subtype0/subtype1 协议镜像表、账号设置 BLOB、
--     get_userinfo_template 协议模板表。
--
--   * 物品列不采用 86 的 item_core 99B BLOB：其成立前提是"DB 行 = 线上
--     99B 物品结构体的逐字节暂存"（86 客户端 wire 即该结构体，库→线路
--     零映射）；60A1 的物品线路字段由 Network/Builders/ 分组构建器逐字段编码，
--     不存在可逐字节搬运的 core 结构。扁平列可查、类型安全，
--     字节级行为由 PacketDumper golden 测试在协议层守护。
--
--   * 所有时间戳列：Unix 秒（INTEGER）；审计型时间戳（created_at 等）：ISO-8601 文本。
--   * 连接 PRAGMA 不落基线 SQL：journal_mode=WAL 与 foreign_keys 由
--     SqliteDatabaseBootstrap 在事务外设置——基线 DDL 按单事务执行（见 D2），
--     journal_mode 在活动事务内执行会报错；foreign_keys 在事务内是 no-op，
--     实际经连接串 ForeignKeys=true 每连接生效。PRAGMA user_version 例外：
--     可随事务提交，保留在基线内直接设置（见下方）。
--   * GUID 外部标识保留为 TEXT 列（account_uid / character_uid / instance_id），
--     Admin API 与存储层公开签名继续使用 Guid。
--   * account_id / character_id 由存储层显式分配：账号按 AccountUidRules 分段
--     （测试 1..10_000_000，保留 10_000_001..17_999_999，普通 >= 18_000_000），
--     AUTOINCREMENT 仅用于防止删除后 id 复用，不要依赖它做隐式取号。
-- ============================================================================

-- 基线标识与 schema 版本。新库由启动流程（SqliteDatabaseBootstrap）建表后
-- 在同一事务写入本行；增量迁移机制见 docs/design/08-sqlite-storage.md D3。
-- baseline_id = 100（60A1 基线，数值）；与 PRAGMA user_version 同值双写互验，
-- 不一致拒绝启动；其它值 = 异构库（提示备份移走）。
CREATE TABLE IF NOT EXISTS schema_metadata (
    singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1),
    baseline_id INTEGER NOT NULL,
    schema_version INTEGER NOT NULL,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

-- 基线建库时 schema_version 初值、PRAGMA user_version 与 baseline_id 同为 100；
-- 此后每个增量步在同一事务内同步推进 schema_version 与 PRAGMA user_version。
-- 启动互验不变量（见 D3）：baseline_id == 100（恒定，异构库检测）且
-- PRAGMA user_version == schema_metadata.schema_version（随迁移推进）。
PRAGMA user_version = 100;

CREATE TABLE IF NOT EXISTS server_settings (
    settings_id INTEGER PRIMARY KEY CHECK(settings_id = 1),
    -- 下面这些都是**在国服基准之上的倍数**（基准写死在服务端奖励率常量里：
    -- 经验 0.928 / 通关每怪 0.75 / 世界掉落 10000-十万分制）。1.0 = 原汁原味国服。
    experience_multiplier REAL NOT NULL DEFAULT 1.0,
    gold_multiplier REAL NOT NULL DEFAULT 1.0,
    -- 爆率系数，乘在 PVF 原值上；1.0 = 完全按 PVF。
    normal_drop_multiplier REAL NOT NULL DEFAULT 1.0,
    hell_drop_multiplier REAL NOT NULL DEFAULT 1.0,
    -- 世界掉落倍数（乘在基准 10000/十万 上，0 = 关掉）。PVF 里 worlddrop.etc 的
    -- drop rate 那一列全是 0，所以生效值是替换而非乘 PVF。
    world_drop_rate REAL NOT NULL DEFAULT 1.0,
    -- 通关经验每怪系数的倍数（乘在基准 0.75 上）。官方公式：击杀数 × 基数 ÷ 2 × 生效系数。
    clear_exp_per_kill REAL NOT NULL DEFAULT 1.0,
    -- 深渊掉落次数（独立 roll 次数）：拿 PVF 那个千分制命中率独立掷 N 次，
    -- 命中几次就产几件。1 = 官方行为（每只怪最多 1 件），默认 3，上限 5。
    hell_drop_roll_times INTEGER NOT NULL DEFAULT 3,
    -- 深渊怪掉「首饰设计图」的总命中率(0..1)。默认 0.05 = 5%,0 = 不掉。
    -- 和狗眼那 100% 互不挤占(逐档独立判定)。
    hell_recipe_rate REAL NOT NULL DEFAULT 0.05,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

INSERT OR IGNORE INTO server_settings(settings_id, experience_multiplier, gold_multiplier)
VALUES(1, 1.0, 1.0);

-- 怪物基础经验（按怪物等级）。对应官方的数据库表 monster_reward_ref
-- （df_game_r 里 `select level, exp from monster_reward_ref`）—— 官方这张表也不在
-- PVF 里，就是放数据库的。首次启动由服务端用代码里的默认表播种，之后改这里即可，
-- 不用改代码、也不用动 PVF。
CREATE TABLE IF NOT EXISTS monster_base_exp (
    level INTEGER PRIMARY KEY,
    exp INTEGER NOT NULL
);

-- ============================================================================
-- 账号
-- ============================================================================

-- 货币与服务在账号侧（86 模式：点券系挂 accounts，金币挂角色）。
CREATE TABLE IF NOT EXISTS accounts (
    account_id INTEGER PRIMARY KEY AUTOINCREMENT,   -- = JSON AccountUid，按 AccountUidRules 分段分配
    account_uid TEXT NOT NULL UNIQUE,               -- = JSON AccountRecord.Id（Guid），Admin API 标识
    username TEXT NOT NULL UNIQUE COLLATE NOCASE,   -- 代码比较用 OrdinalIgnoreCase，故 NOCASE
    password_hash TEXT NOT NULL DEFAULT '',         -- 存 SHA256 哈希（前缀 dfls-local:），不是明文
    oicq_code TEXT NOT NULL DEFAULT '',             -- 密码重置的匹配键（Admin API 按 OicqCode 找回）
    -- 预置角色是否已初始化（JSON PresetCharactersInitialized；
    -- 启动时 InitializeTestAccountPresets/RefreshPresetCharacters 以此为门控）。
    preset_characters_initialized INTEGER NOT NULL DEFAULT 0,
    -- 账号级共享点券钱包（充值/消费的唯一真源）。
    -- 旧 JSON 里 characters.cash 只是同步镜像，入库后不再落角色表。
    cash INTEGER NOT NULL DEFAULT 50000,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    last_login_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

-- 账号级契约 / premium。22=霸王，27=达人，29=黑钻（黑钻也承受 diamond 充值）。
-- expires_at = Unix 秒。对应 JSON AccountPremiumServiceRecord(ServiceType, ExpiresAt)。
CREATE TABLE IF NOT EXISTS account_premium (
    account_id INTEGER NOT NULL,
    premium_type INTEGER NOT NULL,
    expires_at INTEGER NOT NULL DEFAULT 0,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (account_id, premium_type),
    FOREIGN KEY (account_id) REFERENCES accounts(account_id) ON DELETE CASCADE
);

-- ============================================================================
-- 角色
-- ============================================================================

CREATE TABLE IF NOT EXISTS characters (
    character_id INTEGER PRIMARY KEY AUTOINCREMENT, -- = JSON CharacterNo，全局单调
    character_uid TEXT NOT NULL UNIQUE,             -- = JSON CharacterRecord.Id（Guid），Admin API 标识
    account_id INTEGER NOT NULL,
    slot_index INTEGER NOT NULL CHECK (slot_index >= 0),
    name TEXT NOT NULL UNIQUE COLLATE NOCASE,       -- 代码比较用 OrdinalIgnoreCase，故 NOCASE
    job INTEGER NOT NULL,
    grow_type INTEGER NOT NULL DEFAULT 0,
    awakening_type INTEGER NOT NULL DEFAULT 0,      -- 觉醒类型（登录时与 PVF 校验取 Max(awakening, grow_type>>4)）
    is_preset INTEGER NOT NULL DEFAULT 0,           -- 预置角色：启动时按代码模板刷新
    level INTEGER NOT NULL DEFAULT 1,
    exp INTEGER NOT NULL DEFAULT 0,
    skill_points INTEGER NOT NULL DEFAULT 0,        -- JSON Sp
    bonus_skill_points INTEGER NOT NULL DEFAULT 0,  -- JSON BonusSp（任务/活动奖励的额外 SP）
    gold INTEGER NOT NULL DEFAULT 50000,
    victory_points INTEGER NOT NULL DEFAULT 0,      -- 胜点（背包主容器刷新包第二槽位随金币一起下发）
    pvp_points INTEGER NOT NULL DEFAULT 0,
    -- Grade 0 = DF2008 未定级/新角色（客户端显示 10 级），不要在首个 PVP 点数前归一成 1。
    pvp_grade INTEGER NOT NULL DEFAULT 0,
    pvp_wins INTEGER NOT NULL DEFAULT 0,
    pvp_losses INTEGER NOT NULL DEFAULT 0,
    pvp_play_count INTEGER NOT NULL DEFAULT 0,      -- 对齐 legacy pvp_result 行语义
    pvp_count INTEGER NOT NULL DEFAULT 0,
    pvp_grade_extension INTEGER NOT NULL DEFAULT 0,
    -- 金库容量：新角色 8 格（CharacterWarehouseProgression.InitialCapacity），
    -- 升级事务推进；JSON WarehouseCapacity（早期草案名 storage_capacity）。
    warehouse_capacity INTEGER NOT NULL DEFAULT 8,
    tutorial_started INTEGER NOT NULL DEFAULT 1,
    -- 虚弱恢复进度 0..100（DungeonWeaknessPolicy.FullStamina=100）。
    -- <100 即虚弱（早期草案的 is_weakened 是它的派生值，不落库）；
    -- WeaknessRecoveryService 每分钟回复。
    weakness_recovery INTEGER NOT NULL DEFAULT 100 CHECK (weakness_recovery BETWEEN 0 AND 100),
    -- Town 1 area 1 is Elvengard/Gate.map (the Seria room).
    town_id INTEGER NOT NULL DEFAULT 1,
    area_id INTEGER NOT NULL DEFAULT 1,
    pos_x INTEGER NOT NULL DEFAULT 474,
    -- Centre of Gate.map's [town movable area] (330,324,289,24)。
    -- 旧档的 (474,234) 越出区域 1 可移动矩形，由一次性导入器修正为 336
    -- （早期草案的建库 UPDATE 语义；基线零业务语句，不放 UPDATE）。
    pos_y INTEGER NOT NULL DEFAULT 336,
    direction INTEGER NOT NULL DEFAULT 5,
    bonus_strength INTEGER NOT NULL DEFAULT 0,
    bonus_intelligence INTEGER NOT NULL DEFAULT 0,
    bonus_vitality INTEGER NOT NULL DEFAULT 0,
    bonus_spirit INTEGER NOT NULL DEFAULT 0,
    bonus_maximum_hp INTEGER NOT NULL DEFAULT 0,
    bonus_maximum_mp INTEGER NOT NULL DEFAULT 0,
    bonus_movement_speed INTEGER NOT NULL DEFAULT 0,
    bonus_all_resistance INTEGER NOT NULL DEFAULT 0,
    -- 师徒：仅存师父角色 id（0 = 无）；不设外键，删除师父后留下悬空 id，
    -- 读取方解析不到目标角色即视为无师父（与 JSON 现状等价）。
    mentor_character_id INTEGER NOT NULL DEFAULT 0,
    mentor_exp_bonus_percent INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (account_id) REFERENCES accounts(account_id) ON DELETE CASCADE,
    UNIQUE (account_id, slot_index)
);

CREATE INDEX IF NOT EXISTS idx_characters_account
    ON characters(account_id);

-- ============================================================================
-- 物品（单表 + list_type 容器码；86 的 character_inventory_items 形态）
-- ============================================================================

-- 角色全部物品落一张表，list_type 用 60A1 客户端线上的权威码
-- （EntranceService 移动物品/刷新包逐一对过）：
--   0 = 背包（main inventory）  1 = 时装（avatar）  2 = 金库（warehouse）
--   3 = 穿戴（equipment）       7 = 宠物（creature，容量 238 格）
-- 列集合 1:1 对应 CharacterItemRecord（五种容器共用同一 record），空位不落行。
-- 行标识：item_uid 代理主键（86 模式，邮件附件领取/管理定位按物理行）；
-- instance_id（Guid）是 Admin API 外部标识。lock_state 为早期草案兼容预留列
-- （物品上锁；当前代码无对应字段，不读写，恒 0）。
CREATE TABLE IF NOT EXISTS character_items (
    item_uid INTEGER PRIMARY KEY AUTOINCREMENT,
    character_id INTEGER NOT NULL,
    list_type INTEGER NOT NULL CHECK (list_type IN (0, 1, 2, 3, 7)),
    slot_index INTEGER NOT NULL CHECK (slot_index >= 0),
    item_id INTEGER NOT NULL,
    item_count INTEGER NOT NULL DEFAULT 0,          -- JSON CountOrValue：堆叠数
    -- 低 5 位强化等级 + 高 3 位已完成再封印次数。
    item_state INTEGER NOT NULL DEFAULT 0,
    durability INTEGER NOT NULL DEFAULT 0,
    -- [sealing] 装备创建时为 1，首次穿戴后落 0；DF2008 按 [attach type]=[trade] 处理。
    seal_state INTEGER NOT NULL DEFAULT 0,
    -- 早期草案兼容预留：物品上锁状态，当前代码不读写。
    lock_state INTEGER NOT NULL DEFAULT 0,
    -- 时装/宠物卡剩余秒数，0 = 永久。⚠ 是「剩余秒数」不是绝对到期时刻
    -- （DF2008 线路按 remaining-seconds 消费；勿改名成 expires_at；
    -- 早期草案名 expires_at 的语义是错的）。
    avatar_remaining_seconds INTEGER NOT NULL DEFAULT 0,
    avatar_ability_index INTEGER NOT NULL DEFAULT 0,
    -- 宠物五件套 + 饱食度余数（仅 list_type=7 使用，其余容器恒 0）。
    creature_stomach INTEGER NOT NULL DEFAULT 0,
    creature_experience INTEGER NOT NULL DEFAULT 0,
    creature_level INTEGER NOT NULL DEFAULT 0,
    creature_name TEXT NOT NULL DEFAULT '',
    creature_no_charge INTEGER NOT NULL DEFAULT 0,
    creature_stomach_remainder_seconds INTEGER NOT NULL DEFAULT 0,
    -- DF2008 把装备品质放在 32 位 add_info 种子上；与 item_count 分离保存。
    -- NPC 商店的每日统一品级种子由 EquipmentQuality.GetNpcShopSeed 按日期派生
    -- （06:00 重置），不入库。
    equipment_quality_seed INTEGER NOT NULL DEFAULT 0,
    -- 服务端物品唯一标识（Guid，可为空串）。DF2008 物品线路格式无 GUID。
    instance_id TEXT NOT NULL DEFAULT '',
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE (character_id, list_type, slot_index),
    FOREIGN KEY (character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);

-- ============================================================================
-- 技能 / 任务 / 副本
-- ============================================================================

CREATE TABLE IF NOT EXISTS character_skills (
    character_id INTEGER NOT NULL,
    slot_index INTEGER NOT NULL CHECK (slot_index >= 0),
    skill_id INTEGER NOT NULL,
    skill_level INTEGER NOT NULL DEFAULT 1,
    PRIMARY KEY (character_id, skill_id),
    UNIQUE (character_id, slot_index),
    FOREIGN KEY (character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);

-- 进行中任务：quest_id + 当前触发值。完成任务即从本表移除（进 completed 表）。
-- hunt_trigger_format 为早期草案兼容预留列（击杀类任务触发格式；
-- 当前 CharacterQuestRecord 只有 QuestId+Trigger，本列恒 0）。
CREATE TABLE IF NOT EXISTS character_quests (
    character_id INTEGER NOT NULL,
    quest_id INTEGER NOT NULL,
    trigger_value INTEGER NOT NULL DEFAULT 0,
    hunt_trigger_format INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (character_id, quest_id),
    FOREIGN KEY (character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);

-- 已完成任务 + 领奖状态：rewarded_at IS NULL = 完成未领奖
-- （JSON 的 CompletedQuestIds / RewardedQuestIds 两列表合一行；
-- 启动时 MigrateLegacyQuestRewardsAsync 以 rewarded_at 识别待补发对象）。
CREATE TABLE IF NOT EXISTS character_completed_quests (
    character_id INTEGER NOT NULL,
    quest_id INTEGER NOT NULL,
    completed_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    rewarded_at TEXT,
    PRIMARY KEY (character_id, quest_id),
    FOREIGN KEY (character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);

-- 已解锁副本 id 集合（JSON UnlockedDungeonIds；登录时按任务进度增量推导后回写）。
-- 早期草案漏掉本表；当前代码需要。
CREATE TABLE IF NOT EXISTS character_dungeon_unlocks (
    character_id INTEGER NOT NULL,
    dungeon_id INTEGER NOT NULL,
    PRIMARY KEY (character_id, dungeon_id),
    FOREIGN KEY (character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);

-- 各副本历史最高通关难度（JSON DungeonProgress；Admin API 按副本改写目标即本表）。
CREATE TABLE IF NOT EXISTS character_dungeon_progress (
    character_id INTEGER NOT NULL,
    dungeon_id INTEGER NOT NULL,
    max_difficulty INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (character_id, dungeon_id),
    FOREIGN KEY (character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);

-- 早期草案兼容预留：账号级副本进度（官方 df_game_r 的 member_dungeon 语义，
-- 读账号、无记录时合并全角色）。当前实现按角色存储（上表），本表不读写；
-- 早期草案的"每次启动合并 INSERT"不保留——合并属一次性导入/迁移逻辑，
-- 基线零业务语句。
CREATE TABLE IF NOT EXISTS account_dungeon_progress (
    account_id INTEGER NOT NULL,
    dungeon_id INTEGER NOT NULL,
    max_difficulty INTEGER NOT NULL DEFAULT 0,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (account_id, dungeon_id),
    FOREIGN KEY (account_id) REFERENCES accounts(account_id) ON DELETE CASCADE
);

-- ============================================================================
-- 邮件
-- ============================================================================

-- 单收件人、单附件模型（60A1 邮箱一封一个附件，客户端 NOTI 97 原样喂进
-- sub_559D60）。86 的 mailbox_messages/recipients/attachments 三表模式
-- 面向多收件人多附件 + 审计，60A1 版用不上，裁剪。
-- 上限 255 封、30 天有效期由存储层维护，不在 schema 里约束。
-- 服务端可直接往这里插一条，玩家开邮箱就能领。
CREATE TABLE IF NOT EXISTS character_mail (
    mail_id INTEGER PRIMARY KEY AUTOINCREMENT,      -- JSON 里是随机分配 uint，入库改全局自增
    character_id INTEGER NOT NULL,
    sender TEXT NOT NULL DEFAULT '',
    text TEXT NOT NULL DEFAULT '',
    gold INTEGER NOT NULL DEFAULT 0,
    -- 附件（item_id = 0 即无附件）：
    item_id INTEGER NOT NULL DEFAULT 0,
    item_count INTEGER NOT NULL DEFAULT 0,
    item_state INTEGER NOT NULL DEFAULT 0,
    durability INTEGER NOT NULL DEFAULT 0,
    seal_state INTEGER NOT NULL DEFAULT 0,
    avatar_remaining_seconds INTEGER NOT NULL DEFAULT 0,
    avatar_ability_index INTEGER NOT NULL DEFAULT 0,
    equipment_quality_seed INTEGER NOT NULL DEFAULT 0,
    instance_id TEXT NOT NULL DEFAULT '',
    -- 领取/过期状态：1=新(未读)、2=已读进保管箱、3=已处理(尚有金币/附件时拒绝置 3)。
    -- 代码侧创建恒 1、客户端仅可置 2/3（置 0 走删除），故 CHECK 收敛到 1..3。
    is_new INTEGER NOT NULL DEFAULT 1,
    state INTEGER NOT NULL DEFAULT 1 CHECK (state IN (1, 2, 3)),
    -- Unix 秒。created_at = JSON SentAt；expires_at = created_at + 30 天，0 = 永不过期。
    created_at INTEGER NOT NULL DEFAULT 0,
    expires_at INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY (character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS idx_character_mail_character ON character_mail(character_id);

-- ============================================================================
-- 周期状态
-- ============================================================================

-- 疲劳值。consumed = 本日已消耗；last_reset_at = 上次归零的那个「上午 6 点」边界
-- （Unix 秒）。服务端不常开，重置靠边界比较而不是定时器（旧 JSON 的全局
-- FatigueDayKey 入库后由每角色的 last_reset_at 取代）。
CREATE TABLE IF NOT EXISTS character_fatigue (
    character_id INTEGER NOT NULL PRIMARY KEY,
    consumed INTEGER NOT NULL DEFAULT 0,
    last_reset_at INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY (character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);

-- 早期草案兼容预留：经验药（远古精灵的秘药一族）的定时 buff 状态。
-- rate = 额外通关经验比例（PVF [exp bonus rate]），expires_at = 绝对到期时刻
-- （Unix 秒；物品说明「下线时也计算效果时间」，故存绝对时刻）。
-- 当前的三本经验书是即时使用（无定时 buff），不落此表；接入秘药时由
-- 物品使用路径写入。
CREATE TABLE IF NOT EXISTS character_exp_boost (
    character_id INTEGER NOT NULL PRIMARY KEY,
    item_id INTEGER NOT NULL DEFAULT 0,
    rate REAL NOT NULL DEFAULT 0,
    expires_at INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY (character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);
