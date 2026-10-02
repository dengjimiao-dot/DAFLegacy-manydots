-- ============================================================================
-- DAFLegacy SQLite 存储基线（统称 60A1）。设计决议与迁移机制见
-- docs/design/08-sqlite-storage.md。本文件是全仓唯一的基线定义；增量步见
-- Server/DAFLegacy.Server/Sqlite/SqliteMigrations.cs。
--
--   * schema_metadata.baseline_id = 100（恒定，异构库检测）；
--     schema_metadata.schema_version 与 PRAGMA user_version 同值双写互验，
--     基线建库时初值均为 100，此后每个增量步在同一事务内推进两者。
--   * 时间戳：游戏数值时间用 Unix 秒（INTEGER），审计时间戳用 ISO-8601 文本。
--   * 连接 PRAGMA（journal_mode=WAL、foreign_keys）不在本文件设置，由启动
--     流程在事务外生效；user_version 可随事务提交，是本文件唯一的 PRAGMA。
--   * 账号资产默认 0：accounts.cash（账号共享点券钱包）与 characters.gold。
--   * 文末「未实现功能的预留表」当前代码不读写，仅作已定设计的落位。
-- ============================================================================

-- 基线标识与 schema 版本。新库由启动流程建表后在同一事务写入本行；
-- 增量迁移机制见 docs/design/08-sqlite-storage.md D3。
-- baseline_id = 100（恒定，异构库检测）：其它值 = 异构库，拒绝启动
-- （提示备份移走）。
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

-- 全局倍率与掉落开关（GM 可改，运行时唯一真源）。server.json 不再承载这些值：
-- 服务端启动时读一次本表并打印，读路径全程用内存快照，改值需重启或走 GM 接口。
CREATE TABLE IF NOT EXISTS server_settings (
    settings_id INTEGER PRIMARY KEY CHECK (settings_id = 1),
    -- 下面这些都是**在国服基准之上的倍数**（基准写死在服务端奖励率常量里：
    -- 经验 0.928 / 通关每怪 0.75 / 世界掉落 10000-十万分制）。1.0 = 原汁原味国服。
    experience_multiplier REAL NOT NULL DEFAULT 1.0,      -- 怪物经验倍率（原 Experience.MonsterMultiplier）
    clear_multiplier REAL NOT NULL DEFAULT 1.0,            -- 通关经验倍率（原 Experience.ClearMultiplier）
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
    -- 命中几次就产几件。**官方行为 = 1（每只怪最多 1 件）**，基线按官方取 1；
    -- 上限 5 是本服可调上限，需要多件掉落时由 GM 改本行。
    hell_drop_roll_times INTEGER NOT NULL DEFAULT 1 CHECK (hell_drop_roll_times BETWEEN 1 AND 5),
    -- 深渊怪掉「首饰设计图」的总命中率(0..1)。与狗眼那 100% 互不挤占
    -- （逐档独立判定）。**官方行为 = 0（深渊不掉配方）**，基线按官方取 0；
    -- 想要额外配方掉落时由 GM 改本行。
    hell_recipe_rate REAL NOT NULL DEFAULT 0.0 CHECK (hell_recipe_rate BETWEEN 0.0 AND 1.0),

    -- 掉落总开关与两个全局系数（原 server.json 的 Drop 段）。
    drop_enabled INTEGER NOT NULL DEFAULT 1 CHECK (drop_enabled IN (0, 1)),
    -- 爆率百分比：100.0 = 全量掉落，0 = 全不掉。乘在所有掉率判定的阈值上。
    drop_rate_percent REAL NOT NULL DEFAULT 100.0 CHECK (drop_rate_percent >= 0.0),
    -- 金币经济系数：只作用于金币掉落判定，1.0 = 按 PVF 原值。
    drop_economic_rate REAL NOT NULL DEFAULT 1.0 CHECK (drop_economic_rate >= 0.0),
    -- 强制掉落：命中判定的概率分支被短路，只用于自检与排障，不作正常运营配置。
    drop_force INTEGER NOT NULL DEFAULT 0 CHECK (drop_force IN (0, 1)),

    -- 通关经验加成：黑钻恒定加成与限时活动加成。
    clear_bonus_black_diamond_rate REAL NOT NULL DEFAULT 0.05 CHECK (clear_bonus_black_diamond_rate BETWEEN 0.0 AND 1.0),
    clear_bonus_event_rate REAL NOT NULL DEFAULT 0.0 CHECK (clear_bonus_event_rate BETWEEN 0.0 AND 1.0),
    -- 活动加成生效窗口（ISO-8601 文本，NULL = 不限；两端都 NULL 时活动加成不生效）。
    clear_bonus_event_starts_at TEXT,
    clear_bonus_event_ends_at TEXT,

    -- 频道容量上报值（NOTI 13 / EntranceMetadata 用；非倍率，属服务器运行参数）。
    channel_maximum_users INTEGER NOT NULL DEFAULT 100 CHECK (channel_maximum_users >= 0),
    channel_current_users INTEGER NOT NULL DEFAULT 1 CHECK (channel_current_users >= 0),

    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

-- 基线播种：全 1.0/默认位，即「原汁原味国服 + 全量掉落 + 无活动加成」。
-- 服务端启动时读本行并打印；此后由 GM 直接改本表。
INSERT OR IGNORE INTO server_settings(settings_id)
VALUES(1);

-- 怪物基础经验（按等级），对应官方数据库表 monster_reward_ref（不在 PVF 内）；
-- 首次启动由服务端播种代码默认表，此后直接改本表。
CREATE TABLE IF NOT EXISTS monster_base_exp (
    level INTEGER PRIMARY KEY,
    exp INTEGER NOT NULL
);

-- ============================================================================
-- 账号
-- ============================================================================

-- 货币与服务在账号侧（86 模式：点券系挂 accounts，金币挂角色）。
CREATE TABLE IF NOT EXISTS accounts (
    account_id INTEGER PRIMARY KEY AUTOINCREMENT,   -- = JSON AccountUid，按 AccountUidRules 分段显式分配
    account_uid TEXT NOT NULL UNIQUE,               -- = JSON AccountRecord.Id（Guid），Admin API 标识
    username TEXT NOT NULL UNIQUE COLLATE NOCASE,   -- 代码比较用 OrdinalIgnoreCase，故 NOCASE
    password_hash TEXT NOT NULL DEFAULT '',         -- SHA256 哈希（前缀 dfls-local:）
    oicq_code TEXT NOT NULL DEFAULT '',             -- 密码找回匹配键
    -- 预置角色是否已初始化（启动时按代码模板刷新的门控）。
    preset_characters_initialized INTEGER NOT NULL DEFAULT 0,
    -- 账号级共享点券钱包，充值/消费的唯一真源（角色侧不落镜像列）。
    cash INTEGER NOT NULL DEFAULT 0,
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
    character_id INTEGER PRIMARY KEY AUTOINCREMENT, -- = JSON CharacterNo，全局单调，存储层显式分配
    character_uid TEXT NOT NULL UNIQUE,             -- = JSON CharacterRecord.Id（Guid），Admin API 标识
    account_id INTEGER NOT NULL,
    slot_index INTEGER NOT NULL CHECK (slot_index >= 0),
    -- 角色名唯一范围是「账号内」而非全服：预置角色名（PresetCharacterName）与
    -- 换档角色名册（MaximumTransferRoster.Archetypes 的固定档位名）按设计在每个
    -- 测试账号下各建一份，跨账号同名是被允许的。玩家自助建角另有全服判重
    -- （GameStoreCore.CreateCharacterLocked 比对全部账号），比本约束更严——
    -- 约束只保证单个账号名册内不出现重名。
    name TEXT NOT NULL COLLATE NOCASE,
    job INTEGER NOT NULL,
    grow_type INTEGER NOT NULL DEFAULT 0,
    awakening_type INTEGER NOT NULL DEFAULT 0,      -- 登录时与 PVF 校验取 Max(awakening, grow_type>>4)
    is_preset INTEGER NOT NULL DEFAULT 0,           -- 预置角色：启动时按代码模板刷新
    level INTEGER NOT NULL DEFAULT 1,
    exp INTEGER NOT NULL DEFAULT 0,
    skill_points INTEGER NOT NULL DEFAULT 0,        -- JSON Sp
    bonus_skill_points INTEGER NOT NULL DEFAULT 0,  -- JSON BonusSp（任务/活动奖励的额外 SP）
    gold INTEGER NOT NULL DEFAULT 0,
    victory_points INTEGER NOT NULL DEFAULT 0,      -- 胜点（背包主容器刷新包第二槽位随金币一起下发）
    pvp_points INTEGER NOT NULL DEFAULT 0,
    -- Grade 0 = 未定级/新角色（客户端显示 10 级），不要在首个 PVP 点数前归一成 1。
    pvp_grade INTEGER NOT NULL DEFAULT 0,
    pvp_wins INTEGER NOT NULL DEFAULT 0,
    pvp_losses INTEGER NOT NULL DEFAULT 0,
    pvp_play_count INTEGER NOT NULL DEFAULT 0,      -- 对齐 legacy pvp_result 行语义
    pvp_count INTEGER NOT NULL DEFAULT 0,
    pvp_grade_extension INTEGER NOT NULL DEFAULT 0,
    -- 金库容量：新角色 8 格（CharacterWarehouseProgression.InitialCapacity），
    -- 升级事务推进；JSON WarehouseCapacity。
    warehouse_capacity INTEGER NOT NULL DEFAULT 8,
    tutorial_started INTEGER NOT NULL DEFAULT 1,
    -- 虚弱恢复进度 0..100（DungeonWeaknessPolicy.FullStamina=100），<100 即虚弱
    -- （is_weakened 是它的派生值，不落库）；WeaknessRecoveryService 每分钟回复。
    weakness_recovery INTEGER NOT NULL DEFAULT 100 CHECK (weakness_recovery BETWEEN 0 AND 100),
    -- Town 1 area 1 is Elvengard/Gate.map (the Seria room).
    town_id INTEGER NOT NULL DEFAULT 1,
    area_id INTEGER NOT NULL DEFAULT 1,
    pos_x INTEGER NOT NULL DEFAULT 474,
    -- Centre of Gate.map's [town movable area] (330,324,289,24)。
    -- 旧档的 (474,234) 越出区域 1 可移动矩形，由一次性导入器修正为 336。
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
    -- 师徒仅存师父角色 id（0 = 无）；不设外键，删除师父后悬空 id 视为无师父。
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
-- 物品（单表 + list_type 容器码）
-- ============================================================================

-- 角色全部物品落一张表，list_type 用 60A1 客户端线上的权威容器码
-- （EntranceService 移动物品/刷新包逐一对过）：
--   0 = 背包（main inventory）  1 = 时装（avatar）  2 = 金库（warehouse）
--   3 = 穿戴（equipment）       7 = 宠物（creature，容量 238 格）
-- 列集合 1:1 对应 CharacterItemRecord（五种容器共用同一 record），空位不落行。
-- 行标识 item_uid 为代理主键：邮件附件领取/管理定位按物理行；instance_id
-- 是 Admin API 外部标识。
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
    -- 预留：物品上锁状态，当前代码不读写。
    lock_state INTEGER NOT NULL DEFAULT 0,
    -- 以下九列可空，对应 CharacterItemRecord 的九个可空标量：null 与 0 在线路
    -- 上不等价（消费侧一律写成 `?? 回退值`，如 EquipmentQualitySeed 回退
    -- CountOrValue、AvatarAbilityIndex 回退 Durability、CreatureStomach 回退
    -- 100），按 0 落库会改变下发字节，故必须保留 null。
    -- 时装/宠物卡剩余秒数，0 = 永久。DF2008 线路按剩余秒数消费，
    -- 是「剩余秒数」而非绝对到期时刻，勿改名成 expires_at。
    avatar_remaining_seconds INTEGER,
    avatar_ability_index INTEGER,
    -- 宠物五件套 + 饱食度余数（仅 list_type=7 使用，其余容器恒 NULL）。
    creature_stomach INTEGER,
    creature_experience INTEGER,
    creature_level INTEGER,
    creature_name TEXT,
    creature_no_charge INTEGER,
    creature_stomach_remainder_seconds INTEGER,
    -- 装备品质 32 位种子（DF2008 品质放在 add_info 种子上），与堆叠数分列；
    -- NPC 商店每日统一品级种子由代码按日期派生（06:00 重置），不入库。
    equipment_quality_seed INTEGER,
    -- 服务端物品唯一标识（Guid，可为空串）；DF2008 物品线路格式无 GUID。
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
-- hunt_trigger_format 为预留列（击杀类任务触发格式；当前
-- CharacterQuestRecord 只有 QuestId+Trigger，本列恒 0）。
CREATE TABLE IF NOT EXISTS character_quests (
    character_id INTEGER NOT NULL,
    quest_id INTEGER NOT NULL,
    trigger_value INTEGER NOT NULL DEFAULT 0,
    hunt_trigger_format INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (character_id, quest_id),
    FOREIGN KEY (character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);

-- 已完成任务：rewarded_at IS NULL = 完成未领奖（JSON 的 CompletedQuestIds /
-- RewardedQuestIds 两列表合一行；启动补发逻辑以 rewarded_at 识别待补发对象）。
CREATE TABLE IF NOT EXISTS character_completed_quests (
    character_id INTEGER NOT NULL,
    quest_id INTEGER NOT NULL,
    completed_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    rewarded_at TEXT,
    PRIMARY KEY (character_id, quest_id),
    FOREIGN KEY (character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);

-- 已解锁副本 id 集合（JSON UnlockedDungeonIds；登录时按任务进度增量推导后回写）。
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

-- 预留：账号级副本进度（官方 member_dungeon 语义，读账号、无记录时合并全角色）。
-- 当前按角色存储（上表），本表不读写；合并属一次性导入/迁移逻辑，基线零业务语句。
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
-- sub_559D60）。上限 255 封、30 天有效期由存储层维护，不在 schema 约束。
-- 服务端可直接往这里插一条，玩家开邮箱就能领。
--
-- mail_id 由存储层显式分配（GameStoreCore.AllocateMailIdLocked 随机取号，
-- docs/design/06 §4.1 登记的唯一性分配例外），**不是** AUTOINCREMENT：角色子树
-- 每次重写都会删旧行再整棵重插，若让库自行取号，客户端与 Admin API 手里的邮件
-- Id 会在任意一次保存后失效。显式写入使 Id 在重写前后保持不变。
CREATE TABLE IF NOT EXISTS character_mail (
    mail_id INTEGER PRIMARY KEY,                     -- 存储层显式分配，全库唯一
    character_id INTEGER NOT NULL,
    sender TEXT NOT NULL DEFAULT '',
    text TEXT NOT NULL DEFAULT '',
    gold INTEGER NOT NULL DEFAULT 0,
    -- 附件（item_id = 0 即无附件）。
    item_id INTEGER NOT NULL DEFAULT 0,
    item_count INTEGER NOT NULL DEFAULT 0,
    item_state INTEGER NOT NULL DEFAULT 0,
    durability INTEGER NOT NULL DEFAULT 0,
    seal_state INTEGER NOT NULL DEFAULT 0,
    -- 附件的三个可空标量，语义同 character_items 的同名列（null ≠ 0）。
    avatar_remaining_seconds INTEGER,
    avatar_ability_index INTEGER,
    equipment_quality_seed INTEGER,
    instance_id TEXT NOT NULL DEFAULT '',
    -- 状态机：1=新(未读)，2=已读进保管箱，3=已处理（尚有金币/附件时拒绝置 3）。
    is_new INTEGER NOT NULL DEFAULT 1,
    state INTEGER NOT NULL DEFAULT 1 CHECK (state IN (1, 2, 3)),
    -- Unix 秒；expires_at = created_at + 30 天，0 = 永不过期。
    created_at INTEGER NOT NULL DEFAULT 0,
    expires_at INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY (character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS idx_character_mail_character ON character_mail(character_id);

-- ============================================================================
-- 周期状态
-- ============================================================================

-- 疲劳值：consumed = 本日已消耗；last_reset_at = 上次归零的那个「上午 6 点」边界
-- （Unix 秒）。服务端不常开，重置靠边界比较而不是定时器（见 FatigueState.ResetIfDue）。
CREATE TABLE IF NOT EXISTS character_fatigue (
    character_id INTEGER NOT NULL PRIMARY KEY,
    consumed INTEGER NOT NULL DEFAULT 0,
    last_reset_at INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY (character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);

-- 预留：经验药（远古精灵的秘药一族）的定时 buff 状态。
-- rate = 额外通关经验比例（PVF [exp bonus rate]），expires_at = 绝对到期时刻
-- （Unix 秒；物品说明「下线时也计算效果时间」，故存绝对时刻）。
-- 当前三本经验书是即时使用（无定时 buff），不落此表；接入秘药时由
-- 物品使用路径写入。
CREATE TABLE IF NOT EXISTS character_exp_boost (
    character_id INTEGER NOT NULL PRIMARY KEY,
    item_id INTEGER NOT NULL DEFAULT 0,
    rate REAL NOT NULL DEFAULT 0,
    expires_at INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY (character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);

-- ============================================================================
-- 未实现功能的预留表（README「已知缺口」；当前代码不读写）
-- ============================================================================
--
-- 覆盖结论：多人组队、玩家交易、决斗场不设表——组队为会话态（成员绑定会话，
-- 掉线即散，队名经 NOTI 9 原生下发）；交易履约是既有表上按 item_uid 换主加
-- 双方 characters.gold 结转的单事务；决斗场结算写 characters.pvp_* 聚合列。
-- 协议号登记见 docs/protocol/client-packet-enums-60cn.md；各系统实现前按
-- AGENTS.md D1 以 IDA + 抓包定协议格式，未定论的值域不预设约束。

-- 私人商店（CMD 85-90；2026-10-01 骨架实现：摊位目录暂驻内存
-- PrivateStoreRegistry，本两表接线待购买应答值域与上架通道真机定案（P-36）；
-- 90 挂名完成与 88 建占位为两步。店名与商品目录需持久化（关店后重开沿用）；
-- 成交即时换主，不留账表。每角色至多一店。
CREATE TABLE IF NOT EXISTS private_stores (
    owner_character_id INTEGER PRIMARY KEY,
    store_name TEXT NOT NULL DEFAULT '',
    is_open INTEGER NOT NULL DEFAULT 0 CHECK (is_open IN (0, 1)),
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (owner_character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);

-- 上架条目按 item_uid 引用店主的一行 character_items（物理行引用）：上架期间
-- 物品行留在店主名下，成交或撤架时在同一事务内删本表行并换主或归位；
-- item_uid 即主键，一件物品全局至多一条上架记录。
CREATE TABLE IF NOT EXISTS private_store_items (
    item_uid INTEGER PRIMARY KEY,
    owner_character_id INTEGER NOT NULL,
    price_gold INTEGER NOT NULL CHECK (price_gold >= 0),
    count INTEGER NOT NULL DEFAULT 1 CHECK (count >= 1), -- 可堆叠上架数量（非堆叠恒 1）
    listed_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (owner_character_id) REFERENCES characters(character_id) ON DELETE CASCADE,
    FOREIGN KEY (item_uid) REFERENCES character_items(item_uid) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS idx_private_store_items_owner
    ON private_store_items(owner_character_id);

-- 好友（CMD 63/64；2026-10-01 已实现：单向关系、添加即生效无确认流，
-- 上下线通知按反向边扇出，本表反向索引即该查询）。任一方删除角色时两个方向的
-- 关系行都随外键级联清理。
CREATE TABLE IF NOT EXISTS character_friends (
    owner_character_id INTEGER NOT NULL,
    friend_character_id INTEGER NOT NULL,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (owner_character_id, friend_character_id),
    CHECK (owner_character_id <> friend_character_id),
    FOREIGN KEY (owner_character_id) REFERENCES characters(character_id) ON DELETE CASCADE,
    FOREIGN KEY (friend_character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS idx_character_friends_friend
    ON character_friends(friend_character_id);

-- 黑名单（CMD 128/129/130；2026-10-01 已实现，J1 裁决角色级锚定——客户端
-- 黑名单管理器键为名字，角色级兼容；密语拦截待 NOTI 65 语义定案）。
CREATE TABLE IF NOT EXISTS character_blacklist (
    owner_character_id INTEGER NOT NULL,
    target_character_id INTEGER NOT NULL,
    created_at INTEGER NOT NULL,                    -- unix 秒（CMD 130 应答按 tm 日/月-1/年-1900 消费）
    PRIMARY KEY (owner_character_id, target_character_id),
    CHECK (owner_character_id <> target_character_id),
    FOREIGN KEY (owner_character_id) REFERENCES characters(character_id) ON DELETE CASCADE,
    FOREIGN KEY (target_character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS idx_character_blacklist_target
    ON character_blacklist(target_character_id);

-- 公会（CMD 70/76 与 NOTI 70 已取证：信息包布局已解；建会族 71/166-168 零
-- 发送点——建会流静态不可定位，真机定案后实现（P-36）。只收录有协议面依据的
-- 核心实体；公会战与徽标格式未验证，不预设列。
-- 公会名唯一性沿用 characters.name 的 NOCASE 口径。
CREATE TABLE IF NOT EXISTS guilds (
    guild_id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL COLLATE NOCASE UNIQUE,
    level INTEGER NOT NULL DEFAULT 1 CHECK (level >= 1),
    -- 会长外键不带级联：删除会长角色前须先转让会长或解散公会。
    leader_character_id INTEGER NOT NULL UNIQUE,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (leader_character_id) REFERENCES characters(character_id)
);

-- 会长同时是本表一行成员；rank 档位编码实现时按协议定，不预设值域。
CREATE TABLE IF NOT EXISTS guild_members (
    character_id INTEGER PRIMARY KEY,
    guild_id INTEGER NOT NULL,
    rank INTEGER NOT NULL DEFAULT 0,
    joined_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (character_id) REFERENCES characters(character_id) ON DELETE CASCADE,
    FOREIGN KEY (guild_id) REFERENCES guilds(guild_id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS idx_guild_members_guild ON guild_members(guild_id);

-- 入会申请（CMD 78）：一人可同时申请多个公会，同一公会同一人仅一条；
-- 批准即写入 guild_members 并删申请行。
CREATE TABLE IF NOT EXISTS guild_applications (
    guild_id INTEGER NOT NULL,
    character_id INTEGER NOT NULL,
    applied_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (guild_id, character_id),
    FOREIGN KEY (guild_id) REFERENCES guilds(guild_id) ON DELETE CASCADE,
    FOREIGN KEY (character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS idx_guild_applications_character
    ON guild_applications(character_id);
