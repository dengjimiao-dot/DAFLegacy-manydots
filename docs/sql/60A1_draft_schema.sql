-- ============================================================================
-- 60A1 早期草案留档。已被 60A1_schema.sql（正式基线）取代：其功能面已并入
-- 基线的预留列/表；本文件仅留档参照，不再演进。
-- ============================================================================

PRAGMA foreign_keys = ON;
PRAGMA journal_mode = WAL;

CREATE TABLE IF NOT EXISTS server_settings (
    settings_id INTEGER PRIMARY KEY CHECK(settings_id = 1),
    -- 下面这些都是**在国服基准之上的倍数**（基准写死在 RewardRateSettings：
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
-- 🔴 这里原来有一句"把旧版本默认的 10 倍经验迁回 1 倍"：
--     UPDATE server_settings SET experience_multiplier = 1.0
--     WHERE settings_id = 1 AND experience_multiplier = 10.0;
-- schema.sql **每次启动都会执行**，所以它不是一次性迁移 —— 只要管理员在 GM 网页把
-- 经验倍率设成正好 10（私服最常用的值之一），下次重启就被它悄悄改回 1。
-- 这正是测试反馈里「用 GM 设置经验倍率保存以后，下次重启服务端又变成初始 1 倍」。
-- 老库里如果确实停在 10.0，现在就当作管理员的选择保留。已删除，勿再加回。

-- 怪物基础经验（按怪物等级）。对应官方的数据库表 monster_reward_ref
-- （df_game_r 里 `select level, exp from monster_reward_ref`）—— 官方这张表也不在
-- PVF 里，就是放数据库的。首次启动由服务端用代码里的默认表播种，之后改这里即可，
-- 不用改代码、也不用动 PVF。
CREATE TABLE IF NOT EXISTS monster_base_exp (
    level INTEGER PRIMARY KEY,
    exp INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS accounts (
    account_id INTEGER PRIMARY KEY AUTOINCREMENT,
    account_name TEXT NOT NULL UNIQUE COLLATE NOCASE,
    password TEXT NOT NULL DEFAULT '',
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    last_login_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS characters (
    character_id INTEGER PRIMARY KEY AUTOINCREMENT,
    account_id INTEGER NOT NULL,
    slot_index INTEGER NOT NULL,
    name TEXT NOT NULL UNIQUE COLLATE NOCASE,
    job INTEGER NOT NULL,
    grow_type INTEGER NOT NULL DEFAULT 0,
    level INTEGER NOT NULL DEFAULT 1,
    exp INTEGER NOT NULL DEFAULT 0,
    skill_points INTEGER NOT NULL DEFAULT 0,
    gold INTEGER NOT NULL DEFAULT 0,
    cash INTEGER NOT NULL DEFAULT 0,
    pvp_points INTEGER NOT NULL DEFAULT 0,
    pvp_grade INTEGER NOT NULL DEFAULT 0,
    -- 金库容量：新角色 8 格；老角色由迁移一次性回填 120（见 SqliteGameStore 的迁移块，
    -- 不能靠这个列默认值取巧——它对新角色也生效）。
    storage_capacity INTEGER NOT NULL DEFAULT 8,
    inventory_initialized INTEGER NOT NULL DEFAULT 0,
    tutorial_started INTEGER NOT NULL DEFAULT 0,
    is_weakened INTEGER NOT NULL DEFAULT 0,
    town_id INTEGER NOT NULL DEFAULT 1,
    -- Town 1 area 1 is Elvengard/Gate.map (the Seria room).
    area_id INTEGER NOT NULL DEFAULT 1,
    pos_x INTEGER NOT NULL DEFAULT 474,
    -- Centre of Gate.map's [town movable area] (330,324,289,24).
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
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY(account_id) REFERENCES accounts(account_id) ON DELETE CASCADE,
    UNIQUE(account_id, slot_index)
);

CREATE INDEX IF NOT EXISTS ix_characters_account_slot
    ON characters(account_id, slot_index);

CREATE TABLE IF NOT EXISTS character_items (
    character_id INTEGER NOT NULL,
    slot_index INTEGER NOT NULL,
    item_id INTEGER NOT NULL,
    item_count INTEGER NOT NULL DEFAULT 1,
    item_state INTEGER NOT NULL DEFAULT 0,
    durability INTEGER NOT NULL DEFAULT 0,
    seal_state INTEGER NOT NULL DEFAULT 0,
    expires_at INTEGER NOT NULL DEFAULT 0,
    creature_name TEXT NOT NULL DEFAULT '',
    lock_state INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY(character_id, slot_index),
    FOREIGN KEY(character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS character_storage (
    character_id INTEGER NOT NULL,
    slot_index INTEGER NOT NULL,
    item_id INTEGER NOT NULL,
    item_count INTEGER NOT NULL DEFAULT 1,
    item_state INTEGER NOT NULL DEFAULT 0,
    durability INTEGER NOT NULL DEFAULT 0,
    seal_state INTEGER NOT NULL DEFAULT 0,
    lock_state INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY(character_id, slot_index),
    FOREIGN KEY(character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS character_equipment (
    character_id INTEGER NOT NULL,
    slot_index INTEGER NOT NULL,
    item_id INTEGER NOT NULL,
    item_count INTEGER NOT NULL DEFAULT 1,
    item_state INTEGER NOT NULL DEFAULT 0,
    durability INTEGER NOT NULL DEFAULT 0,
    seal_state INTEGER NOT NULL DEFAULT 0,
    expires_at INTEGER NOT NULL DEFAULT 0,
    creature_name TEXT NOT NULL DEFAULT '',
    lock_state INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY(character_id, slot_index),
    FOREIGN KEY(character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS character_avatars (
    character_id INTEGER NOT NULL,
    slot_index INTEGER NOT NULL,
    item_id INTEGER NOT NULL,
    item_count INTEGER NOT NULL DEFAULT 1,
    item_state INTEGER NOT NULL DEFAULT 0,
    durability INTEGER NOT NULL DEFAULT 0,
    seal_state INTEGER NOT NULL DEFAULT 0,
    expires_at INTEGER NOT NULL DEFAULT 0,
    clone_item_id INTEGER NOT NULL DEFAULT 0,
    lock_state INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY(character_id, slot_index),
    FOREIGN KEY(character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS character_skills (
    character_id INTEGER NOT NULL,
    slot_index INTEGER NOT NULL,
    skill_id INTEGER NOT NULL,
    skill_level INTEGER NOT NULL DEFAULT 1,
    PRIMARY KEY(character_id, skill_id),
    UNIQUE(character_id, slot_index),
    FOREIGN KEY(character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS character_quests (
    character_id INTEGER NOT NULL,
    quest_id INTEGER NOT NULL,
    trigger_value INTEGER NOT NULL DEFAULT 0,
    hunt_trigger_format INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY(character_id, quest_id),
    FOREIGN KEY(character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS character_completed_quests (
    character_id INTEGER NOT NULL,
    quest_id INTEGER NOT NULL,
    completed_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY(character_id, quest_id),
    FOREIGN KEY(character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS character_dungeon_progress (
    character_id INTEGER NOT NULL,
    dungeon_id INTEGER NOT NULL,
    max_difficulty INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY(character_id, dungeon_id),
    FOREIGN KEY(character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);

-- 副本通关/难度解锁是**账号级**的（官方 df_game_r：
--   读 `seLect dungeon from member_dungeon where m_id=<账号>`，
--   账号表没有记录时回退成「把该账号下所有未删除角色的记录合并」
--   `select B.dungeon from charac_info A inner join charac_dungeon B ...`；
--   写 `upDate member_dungeon set dungeon='...' where m_id=<账号>`）。
-- 上面那张 character_dungeon_progress 是我们早期按角色存的，留着当迁移来源。
CREATE TABLE IF NOT EXISTS account_dungeon_progress (
    account_id INTEGER NOT NULL,
    dungeon_id INTEGER NOT NULL,
    max_difficulty INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY(account_id, dungeon_id),
    FOREIGN KEY(account_id) REFERENCES accounts(account_id) ON DELETE CASCADE
);

-- 一次性合并：同账号所有角色取每个副本的最高难度（对应官方的回退合并那一步）。
INSERT OR IGNORE INTO account_dungeon_progress(account_id, dungeon_id, max_difficulty)
SELECT c.account_id, p.dungeon_id, MAX(p.max_difficulty)
  FROM character_dungeon_progress p
  JOIN characters c ON c.character_id = p.character_id
 GROUP BY c.account_id, p.dungeon_id;

-- Migrate the old value taken from Elvengard.twn's [gate] return point.
-- That point belongs to area 0 and is outside area 1's movable rectangle.
UPDATE characters
SET pos_y = 336, updated_at = CURRENT_TIMESTAMP
WHERE town_id = 1 AND area_id = 1 AND pos_x = 474 AND pos_y = 234;

-- Builds before 2026-08-01 mislabeled Elvengard area 0 as the Seria room.
UPDATE characters
SET area_id = 1, pos_x = 474, pos_y = 336, updated_at = CURRENT_TIMESTAMP
WHERE town_id = 1 AND area_id = 0
  AND ((pos_x = 474 AND pos_y = 234) OR (pos_x = 405 AND pos_y = 255));

-- 邮件。服务端可以直接往这里插一条，玩家开邮箱就能领 —— 不用改角色的背包表。
-- 附件的六个字段和物品记录一一对应（客户端 NOTI 97 会原样喂进 sub_559D60）。
CREATE TABLE IF NOT EXISTS character_mail (
    mail_id INTEGER PRIMARY KEY AUTOINCREMENT,
    character_id INTEGER NOT NULL,
    sender TEXT NOT NULL DEFAULT '',
    gold INTEGER NOT NULL DEFAULT 0,
    item_id INTEGER NOT NULL DEFAULT 0,
    item_count INTEGER NOT NULL DEFAULT 0,
    item_state INTEGER NOT NULL DEFAULT 0,
    durability INTEGER NOT NULL DEFAULT 0,
    seal_state INTEGER NOT NULL DEFAULT 0,
    extra INTEGER NOT NULL DEFAULT 0,
    created_at INTEGER NOT NULL DEFAULT 0,
    expires_at INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY(character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS idx_character_mail_character ON character_mail(character_id);

-- 7 号容器（宠物 / 宠物装备）。客户端 sub_43DA10 里 listType 7 -> dword_D7F2B4，
-- 容量 238 格（创建点 sub_446BE0(7, 0xEE)）。归属规则见
-- EquipmentScript.IsCreatureContainerItem：[equipment type] 是
-- creature 或 artifact red/blue/green 的走这里。
CREATE TABLE IF NOT EXISTS character_creature_items (
    character_id INTEGER NOT NULL,
    slot_index INTEGER NOT NULL,
    item_id INTEGER NOT NULL,
    item_count INTEGER NOT NULL DEFAULT 1,
    item_state INTEGER NOT NULL DEFAULT 0,
    durability INTEGER NOT NULL DEFAULT 0,
    seal_state INTEGER NOT NULL DEFAULT 0,
    lock_state INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY(character_id, slot_index),
    FOREIGN KEY(character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);

-- 账号级契约 / premium。22=霸王，27=达人，29=黑钻。
CREATE TABLE IF NOT EXISTS account_premium (
    account_id INTEGER NOT NULL,
    premium_type INTEGER NOT NULL,
    expires_at INTEGER NOT NULL,
    PRIMARY KEY(account_id, premium_type),
    FOREIGN KEY(account_id) REFERENCES accounts(account_id) ON DELETE CASCADE
);

-- 疲劳值。consumed = 今天已消耗，last_reset_at = 上次归零的那个"上午 6 点"边界（Unix 秒）。
-- 服务端不常开，所以重置靠边界比较而不是定时器（见 FatigueState.ResetIfDue）。
CREATE TABLE IF NOT EXISTS character_fatigue (
    character_id INTEGER NOT NULL PRIMARY KEY,
    consumed INTEGER NOT NULL DEFAULT 0,
    last_reset_at INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY(character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);

-- 经验药（远古精灵的秘药一族）的状态。结算面板上的「活动」那一行。
-- rate = 额外通关经验比例（PVF [exp bonus rate]），expires_at = 绝对到期时刻（Unix 秒）。
-- 物品说明写的是「不会因死亡/回城/重新登录而消失，但下线时也会计算效果时间」——
-- 所以存绝对时刻而不是剩余时长，见 ExperienceBoostState。
CREATE TABLE IF NOT EXISTS character_exp_boost (
    character_id INTEGER NOT NULL PRIMARY KEY,
    item_id INTEGER NOT NULL DEFAULT 0,
    rate REAL NOT NULL DEFAULT 0,
    expires_at INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY(character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);
