#!/usr/bin/env python3
"""DAFLegacy：把 data/state.json 中的 test 账号导出为 60A1 数据种子 SQL。

对应 docs/design/08-sqlite-storage.md §6 的导入规则。工具期一次性实现（Python，
与 tools/ 下其它脚本同惯例）；服务端内的正式导入器（运行期导入本地完整
state.json）在 SqliteGameStore 落地后由 M2 里程碑提供，本脚本只负责产出
**可分发的 test 账号种子 SQL**，不再生成二进制样例库。

用法：
    python tools/export_state_sql.py --state docs/sql/state.json \
        --schema docs/sql/act1_schema.sql [--out docs/sql/data_schema.sql]

行为：
  * 只导出 username == 'test' 的账号（含其全部角色/物品/技能/任务/邮件等
    子数据），其余账号一律不进入产物 —— 分发产物不含真实账号数据。
  * 产物为纯 SQL（单事务）：先对全新库执行 act1_schema.sql 建表，再应用
    本文件即得仅含 test 账号的样例库。
  * 落盘前先在内存库回放（schema + 产物 SQL），与源 JSON 逐字段校验
    （行数 + 每账号/每角色/每物品关键列比对），校验不过不写文件。

不在本脚本复刻、留给服务端启动流程的运行期规范化（依赖 PVF/代码表）：
NormalizeCharacterExperience、SettleWarehouseUpgradeItems、预置角色刷新。
"""

from __future__ import annotations

import argparse
import json
import sqlite3
import sys
from datetime import datetime, timedelta
from pathlib import Path

BASELINE_ID = 100
SCHEMA_VERSION = 100  # 与基线 PRAGMA user_version 同值（08 文档 D3 互验不变量）
TEST_USERNAME = "test"

# AccountUidRules（Game/Account/AccountIdentity.cs）
INVALID_UID = 0
MAXIMUM_TEST_UID = 10_000_000
NORMAL_UID_START = 18_000_000

# 缺省值：账号资产默认 0（与 60A1 基线及 JsonGameStore 记录默认值一致），
# 仅在 JSON 字段为 null 时使用。
DEFAULT_ACCOUNT_CASH = 0
DEFAULT_CHARACTER_GOLD = 0
FULL_STAMINA = 100  # DungeonWeaknessPolicy.FullStamina

# GameProtocolEngine 契约类型
BLACK_DIAMOND_SERVICE_TYPE = 16
LEGACY_OVERLORD_CONTRACT_SERVICE_TYPE = 17
OVERLORD_CONTRACT_SERVICE_TYPE = 22

# JSON 物品列表 → 容器码（60 客户端线上 listType）
CONTAINERS = {
    "Inventory": 0,          # 背包
    "AvatarInventory": 1,    # 时装
    "Warehouse": 2,          # 金库
    "Equipment": 3,          # 穿戴
    "CreatureInventory": 7,  # 宠物
}

ITEM_COLUMNS = ("item_id", "item_count", "item_state", "durability", "seal_state",
                "avatar_remaining_seconds", "avatar_ability_index",
                "creature_stomach", "creature_experience", "creature_level",
                "creature_name", "creature_no_charge",
                "creature_stomach_remainder_seconds", "equipment_quality_seed",
                "instance_id")

INSERT_COLUMNS = {
    "accounts": ("account_id", "account_uid", "username", "password_hash",
                 "oicq_code", "preset_characters_initialized", "cash",
                 "created_at", "last_login_at"),
    "account_premium": ("account_id", "premium_type", "expires_at"),
    "characters": ("character_id", "character_uid", "account_id", "slot_index",
                   "name", "job", "grow_type", "awakening_type", "is_preset",
                   "level", "exp", "skill_points", "bonus_skill_points", "gold",
                   "victory_points", "pvp_points", "pvp_grade", "pvp_wins",
                   "pvp_losses", "pvp_play_count", "pvp_count",
                   "pvp_grade_extension", "warehouse_capacity",
                   "tutorial_started", "weakness_recovery", "town_id",
                   "area_id", "pos_x", "pos_y", "direction", "bonus_strength",
                   "bonus_intelligence", "bonus_vitality", "bonus_spirit",
                   "bonus_maximum_hp", "bonus_maximum_mp",
                   "bonus_movement_speed", "bonus_all_resistance",
                   "mentor_character_id", "mentor_exp_bonus_percent",
                   "created_at", "updated_at"),
    "character_items": ("character_id", "list_type", "slot_index") + ITEM_COLUMNS,
    "character_skills": ("character_id", "slot_index", "skill_id", "skill_level"),
    "character_quests": ("character_id", "quest_id", "trigger_value"),
    "character_completed_quests": ("character_id", "quest_id", "completed_at",
                                   "rewarded_at"),
    "character_dungeon_unlocks": ("character_id", "dungeon_id"),
    "character_dungeon_progress": ("character_id", "dungeon_id", "max_difficulty"),
    "character_mail": ("character_id", "sender", "text", "gold", "item_id",
                       "item_count", "item_state", "durability", "seal_state",
                       "avatar_remaining_seconds", "avatar_ability_index",
                       "equipment_quality_seed", "instance_id", "is_new",
                       "state", "created_at", "expires_at"),
    "character_fatigue": ("character_id", "consumed", "last_reset_at"),
    "schema_metadata": ("singleton_id", "baseline_id", "schema_version",
                        "created_at", "updated_at"),
}


def iso_now() -> str:
    return datetime.now().isoformat(timespec="seconds")


def b(value, default=False) -> int:
    """JSON bool/None → 0/1，镜像 C# 可分布尔的缺省语义。"""
    if value is None:
        return 1 if default else 0
    return 1 if value else 0


def num(value, default=0):
    return value if value is not None else default


def guid_text(value) -> str:
    return str(value) if value else ""


def fatigue_boundary_unix(fatigue_day_key: int) -> int:
    """旧全局 FatigueDayKey（本地 yyyyMMdd，06:00 前算前一天）→
    该疲劳日 06:00 边界的 Unix 秒。新库按每角色 last_reset_at 边界比较滚动，
    下一次 06:00（边界 + 24h）触发归零，与旧行为等价。"""
    now = datetime.now()
    if fatigue_day_key:
        try:
            day = datetime.strptime(str(fatigue_day_key), "%Y%m%d")
        except ValueError:
            day = None
        if day is not None:
            boundary = day + timedelta(hours=6)
            if boundary <= now:
                return int(boundary.timestamp())
    boundary = now.replace(hour=6, minute=0, second=0, microsecond=0)
    if boundary > now:
        boundary -= timedelta(days=1)
    return int(boundary.timestamp())


def normalize_premium_services(account: dict) -> list[tuple[int, int]]:
    """镜像 MigratePremiumServicesLocked：兼容字段并入 + 旧霸王类型重映射 +
    按类型取最大到期（0 = 无到期，即 JSON 的 null）。"""
    services = [dict(s) for s in (account.get("PremiumServices") or [])]
    legacy_black_diamond = account.get("PremiumExpireAt")  # LegacyBlackDiamondExpireAt
    if legacy_black_diamond:
        services.append({"ServiceType": BLACK_DIAMOND_SERVICE_TYPE,
                         "ExpiresAt": legacy_black_diamond})
    merged: dict[int, int] = {}
    for service in services:
        service_type = int(num(service.get("ServiceType")))
        if service_type == LEGACY_OVERLORD_CONTRACT_SERVICE_TYPE:
            service_type = OVERLORD_CONTRACT_SERVICE_TYPE
        expiry = int(num(service.get("ExpiresAt")))
        merged[service_type] = max(merged.get(service_type, 0), expiry)
    return sorted(merged.items())


class IdAllocator:
    """镜像 MigrateIdentityLocked 的取号：账号按 AccountUidRules 分段
    （username=='test' 走测试段），角色全局单调，均跳过已占用号。"""

    def __init__(self):
        self.used_account_uids: set[int] = set()
        self.used_character_nos: set[int] = set()

    def allocate_account_uid(self, is_test: bool) -> int:
        if is_test:
            candidate = 1
            while candidate in self.used_account_uids:
                candidate += 1
            if candidate > MAXIMUM_TEST_UID:
                raise SystemExit("测试账号 uid 段已耗尽（>10,000,000）")
        else:
            candidate = NORMAL_UID_START
            while candidate in self.used_account_uids:
                candidate += 1
        self.used_account_uids.add(candidate)
        return candidate

    def allocate_character_no(self) -> int:
        candidate = max(self.used_character_nos, default=0) + 1
        self.used_character_nos.add(candidate)
        return candidate


def build_rows(state: dict):
    """三遍式：分配 id → 组装行（师徒 Guid 解析为整数 id）→ 交由调用方渲染。"""
    accounts = state.get("Accounts") if isinstance(state, dict) else state
    if accounts is None:
        raise SystemExit("state.json 根格式不认识（既不是对象也不是数组）")

    stamp = iso_now()
    allocator = IdAllocator()
    fatigue_boundary = fatigue_boundary_unix(
        int(num(state.get("FatigueDayKey"))) if isinstance(state, dict) else 0)

    # ---- 第一遍：分配 account_uid / character_no ----
    plan = []  # (account_json, account_id, [(character_json, character_id), ...])
    for account in accounts:
        user_name = str(account.get("UserName") or "").strip()
        is_test = user_name.lower() == TEST_USERNAME

        account_uid = int(num(account.get("AccountUid")))
        if account_uid == INVALID_UID or account_uid in allocator.used_account_uids:
            account_id = allocator.allocate_account_uid(is_test)
        else:
            account_id = account_uid
            allocator.used_account_uids.add(account_uid)

        characters = []
        for slot, character in enumerate(account.get("Characters") or []):
            character_no = int(num(character.get("CharacterNo")))
            if character_no == 0 or character_no in allocator.used_character_nos:
                character_id = allocator.allocate_character_no()
            else:
                character_id = character_no
                allocator.used_character_nos.add(character_no)
            characters.append((character, character_id, slot))
        plan.append((account, account_id, user_name, characters))

    account_guid_to_id = {guid_text(a.get("Id")): aid
                          for a, aid, _, _ in plan}
    character_guid_to_id = {guid_text(c.get("Id")): cid
                            for _, _, _, chars in plan for c, cid, _ in chars}

    # ---- 第二遍：组装行 ----
    stats = {"accounts": 0, "characters": 0}
    account_rows, premium_rows = [], []
    character_rows, item_rows, skill_rows = [], [], []
    quest_rows, completed_rows, unlock_rows, progress_rows = [], [], [], []
    mail_rows, fatigue_rows = [], []

    for account, account_id, user_name, characters in plan:
        account_rows.append((
            account_id,
            guid_text(account.get("Id")), user_name,
            str(account.get("PasswordHash") or ""),
            str(account.get("OicqCode") or ""),
            b(account.get("PresetCharactersInitialized")),
            int(num(account.get("Cash"), DEFAULT_ACCOUNT_CASH)),
            stamp, stamp))
        premium_rows.extend((account_id, service_type, expiry) for
                            service_type, expiry in
                            normalize_premium_services(account))
        stats["accounts"] += 1

        for character, character_id, slot in characters:
            mentor_id = character_guid_to_id.get(
                guid_text(character.get("MentorCharacterId")), 0)
            character_rows.append((
                character_id,
                guid_text(character.get("Id")), account_id, slot,
                str(character.get("Name") or ""),
                int(num(character.get("Job"))),
                int(num(character.get("GrowType"))),
                int(num(character.get("AwakeningType"))),
                b(character.get("IsPreset")),
                int(num(character.get("Level"), 1)),
                int(num(character.get("Experience"))),
                int(num(character.get("Sp"))),
                int(num(character.get("BonusSp"))),
                int(num(character.get("Gold"), DEFAULT_CHARACTER_GOLD)),
                int(num(character.get("VictoryPoints"))),
                int(num(character.get("PvpPoints"))),
                int(num(character.get("PvpGrade"))),
                int(num(character.get("PvpWins"))),
                int(num(character.get("PvpLosses"))),
                int(num(character.get("PvpPlayCount"))),
                int(num(character.get("PvpCount"))),
                int(num(character.get("PvpGradeExtension"))),
                int(num(character.get("WarehouseCapacity"), 8)),
                b(character.get("TutorialStarted"), default=True),
                int(num(character.get("WeaknessRecovery"), FULL_STAMINA)),
                int(num(character.get("TownId"), 1)),
                int(num(character.get("AreaId"), 1)),
                int(num(character.get("X"), 474)),
                int(num(character.get("Y"), 234)),
                int(num(character.get("Direction"), 5)),
                int(num(character.get("BonusStrength"))),
                int(num(character.get("BonusIntelligence"))),
                int(num(character.get("BonusVitality"))),
                int(num(character.get("BonusSpirit"))),
                int(num(character.get("BonusMaximumHp"))),
                int(num(character.get("BonusMaximumMp"))),
                int(num(character.get("BonusMovementSpeed"))),
                int(num(character.get("BonusAllElementResistance"))),
                mentor_id,
                int(num(character.get("MentorExperienceBonusPercent"))),
                stamp, stamp))
            stats["characters"] += 1

            for json_key, container in CONTAINERS.items():
                for item in (character.get(json_key) or []):
                    item_rows.append((
                        character_id, container, int(num(item.get("Slot"))),
                        int(num(item.get("ItemId"))),
                        int(num(item.get("CountOrValue"))),
                        int(num(item.get("State"))),
                        int(num(item.get("Durability"))),
                        int(num(item.get("SealState"))),
                        int(num(item.get("AvatarExpireAt"))),
                        int(num(item.get("AvatarAbilityIndex"))),
                        int(num(item.get("CreatureStomach"))),
                        int(num(item.get("CreatureExperience"))),
                        int(num(item.get("CreatureLevel"))),
                        str(item.get("CreatureName") or ""),
                        b(item.get("CreatureNoCharge")),
                        int(num(item.get("CreatureStomachRemainderSeconds"))),
                        int(num(item.get("EquipmentQualitySeed"))),
                        guid_text(item.get("InstanceId"))))

            for skill in (character.get("Skills") or []):
                skill_rows.append((character_id, int(num(skill.get("Slot"))),
                                   int(num(skill.get("SkillId"))),
                                   int(num(skill.get("Level")))))
            for quest in (character.get("Quests") or []):
                quest_rows.append((character_id, int(num(quest.get("QuestId"))),
                                   int(num(quest.get("Trigger")))))
            rewarded = {int(q) for q in (character.get("RewardedQuestIds") or [])}
            for quest_id in (character.get("CompletedQuestIds") or []):
                completed_rows.append((
                    character_id, int(quest_id), stamp,
                    stamp if int(quest_id) in rewarded else None))
            for dungeon_id in (character.get("UnlockedDungeonIds") or []):
                unlock_rows.append((character_id, int(dungeon_id)))
            for progress in (character.get("DungeonProgress") or []):
                progress_rows.append((
                    character_id, int(num(progress.get("DungeonId"))),
                    int(num(progress.get("MaximumDifficulty")))))
            for mail in (character.get("Mailbox") or []):
                att = mail.get("Attachment") or {}
                mail_rows.append((
                    character_id,
                    str(mail.get("Sender") or ""), str(mail.get("Text") or ""),
                    int(num(mail.get("Gold"))),
                    int(num(att.get("ItemId"))), int(num(att.get("CountOrValue"))),
                    int(num(att.get("State"))), int(num(att.get("Durability"))),
                    int(num(att.get("SealState"))),
                    int(num(att.get("AvatarExpireAt"))),
                    int(num(att.get("AvatarAbilityIndex"))),
                    int(num(att.get("EquipmentQualitySeed"))),
                    guid_text(att.get("InstanceId")),
                    b(mail.get("IsNew"), default=True), int(num(mail.get("State"), 1)),
                    int(num(mail.get("SentAt"))), int(num(mail.get("ExpiresAt")))))
            fatigue_rows.append((character_id, int(num(character.get("UsedFatigue"))),
                                 fatigue_boundary))

    return stats, (account_rows, premium_rows, character_rows, item_rows,
                   skill_rows, quest_rows, completed_rows, unlock_rows,
                   progress_rows, mail_rows, fatigue_rows)


def sql_literal(value) -> str:
    if value is None:
        return "NULL"
    if isinstance(value, int):
        return str(value)
    return "'" + str(value).replace("'", "''") + "'"


def render_insert(table: str, rows: list[tuple]) -> str:
    columns = INSERT_COLUMNS[table]
    lines = [f"-- ---- {table}（{len(rows)} 行）----"]
    if not rows:
        lines.append(f"-- （无数据）")
        return "\n".join(lines) + "\n"
    lines.append(f"INSERT INTO {table} ({', '.join(columns)}) VALUES")
    lines.append(",\n".join(
        "  (" + ", ".join(sql_literal(value) for value in row) + ")"
        for row in rows) + ";")
    return "\n".join(lines) + "\n"


def render_sql(rows: tuple, stamp: str) -> str:
    (account_rows, premium_rows, character_rows, item_rows, skill_rows,
     quest_rows, completed_rows, unlock_rows, progress_rows, mail_rows,
     fatigue_rows) = rows

    parts = [
        "BEGIN TRANSACTION;",
        render_insert("schema_metadata",
                      [(1, BASELINE_ID, SCHEMA_VERSION, stamp, stamp)]),
        render_insert("accounts", account_rows),
        render_insert("account_premium", premium_rows),
        render_insert("characters", character_rows),
        render_insert("character_items", item_rows),
        render_insert("character_skills", skill_rows),
        render_insert("character_quests", quest_rows),
        render_insert("character_completed_quests", completed_rows),
        render_insert("character_dungeon_unlocks", unlock_rows),
        render_insert("character_dungeon_progress", progress_rows),
        render_insert("character_mail", mail_rows),
        render_insert("character_fatigue", fatigue_rows),
        "COMMIT;",
    ]
    return "\n".join(parts)


def verify(conn: sqlite3.Connection, state: dict, stats: dict, rows: tuple) -> list[str]:
    """逐字段校验：行数 + 全部角色/物品的关键列集合比对。"""
    problems: list[str] = []
    accounts = state.get("Accounts") if isinstance(state, dict) else state

    def expect(name, actual, wanted):
        if actual != wanted:
            problems.append(f"{name}: 库={actual} 期望={wanted}")

    expect("accounts", conn.execute("SELECT COUNT(*) FROM accounts").fetchone()[0],
           stats["accounts"])
    expect("characters",
           conn.execute("SELECT COUNT(*) FROM characters").fetchone()[0],
           stats["characters"])
    expect("mails", conn.execute("SELECT COUNT(*) FROM character_mail").fetchone()[0],
           sum(len(c.get("Mailbox") or [])
               for a in accounts for c in (a.get("Characters") or [])))
    expect("premium", conn.execute("SELECT COUNT(*) FROM account_premium").fetchone()[0],
           len(rows[1]))

    # 物品逐容器行数 + 每件物品 (slot,item_id,count,state,durability,seal) 多重集
    for account in accounts:
        for character in (account.get("Characters") or []):
            name = character.get("Name")
            cid = conn.execute(
                "SELECT character_id FROM characters WHERE character_uid = ?",
                (guid_text(character.get("Id")),)).fetchone()
            if cid is None:
                problems.append(f"角色缺行: {name}")
                continue
            cid = cid[0]
            for json_key, container in CONTAINERS.items():
                wanted_items = sorted(
                    (int(num(t.get("Slot"))), int(num(t.get("ItemId"))),
                     int(num(t.get("CountOrValue"))), int(num(t.get("State"))),
                     int(num(t.get("Durability"))), int(num(t.get("SealState"))))
                    for t in (character.get(json_key) or []))
                stored_items = sorted(conn.execute(
                    "SELECT slot_index, item_id, item_count, item_state,"
                    " durability, seal_state FROM character_items"
                    " WHERE character_id=? AND list_type=?", (cid, container)))
                if wanted_items != stored_items:
                    problems.append(
                        f"{name} list_type={container}({json_key}):"
                        f" 库 {len(stored_items)} 行与 JSON"
                        f" {len(wanted_items)} 行内容不一致")
            expect(f"{name}.gold",
                   conn.execute("SELECT gold FROM characters WHERE character_id=?",
                                (cid,)).fetchone()[0],
                   int(num(character.get("Gold"), DEFAULT_CHARACTER_GOLD)))
            wanted_progress = sorted(
                (int(num(t.get("DungeonId"))),
                 int(num(t.get("MaximumDifficulty"))))
                for t in (character.get("DungeonProgress") or []))
            stored_progress = sorted(conn.execute(
                "SELECT dungeon_id, max_difficulty FROM character_dungeon_progress"
                " WHERE character_id=?", (cid,)))
            if wanted_progress != stored_progress:
                problems.append(f"{name}: 副本进度 库 {len(stored_progress)} 行"
                                f" 与 JSON {len(wanted_progress)} 行内容不一致")
    return problems


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--state", required=True, help="源 state.json 路径")
    parser.add_argument("--schema", required=True, help="act1_schema.sql 路径")
    parser.add_argument("--out", help="产物 SQL 路径（默认与 state.json 同目录"
                                      " data_schema.sql）")
    args = parser.parse_args()

    state_path = Path(args.state)
    schema_path = Path(args.schema)
    out_path = Path(args.out) if args.out else state_path.with_name("data_schema.sql")

    if not state_path.is_file():
        raise SystemExit(f"找不到 {state_path}")
    if not schema_path.is_file():
        raise SystemExit(f"找不到 {schema_path}")

    state = json.loads(state_path.read_text(encoding="utf-8-sig"))
    accounts = state.get("Accounts") if isinstance(state, dict) else state
    test_accounts = [a for a in (accounts or [])
                     if str(a.get("UserName") or "").strip().lower() == TEST_USERNAME]
    if not test_accounts:
        raise SystemExit(f"{state_path} 中找不到 username=='{TEST_USERNAME}' 的账号")

    filtered_state = dict(state) if isinstance(state, dict) else test_accounts
    if isinstance(filtered_state, dict):
        filtered_state["Accounts"] = test_accounts
    stats, rows = build_rows(filtered_state)
    sql_body = render_sql(rows, iso_now())

    # 落盘前回放校验：schema + 产物 SQL → 内存库，与源 JSON 逐字段比对。
    conn = sqlite3.connect(":memory:")
    conn.executescript(schema_path.read_text(encoding="utf-8-sig"))
    conn.executescript(sql_body)
    problems = verify(conn, filtered_state, stats, rows)
    conn.close()
    if problems:
        print("校验失败，不写产物:")
        for problem in problems:
            print("  -", problem)
        return 1

    header = f"""-- ============================================================================
-- DAFLegacy 60A1 数据种子（仅 test 账号）
-- 生成工具：tools/export_state_sql.py
-- 生成时间：{iso_now()}
-- 数据来源：{state_path}（只保留 username=='{TEST_USERNAME}' 的账号，
--           其余账号不进入本产物；密码哈希为公开默认凭据 test/test）
-- 适用基线：{BASELINE_ID}（schema_version {SCHEMA_VERSION}）
--
-- 用法（全新库）：
--   sqlite3 game.db < docs/sql/act1_schema.sql
--   sqlite3 game.db < docs/sql/data_schema.sql
--
-- 运行期规范化（NormalizeCharacterExperience / SettleWarehouseUpgradeItems /
-- 预置角色刷新）不在本文件内，由服务端启动流程执行。
-- ============================================================================
"""
    out_path.write_text(header + sql_body, encoding="utf-8")

    print(f"导出完成: {out_path}")
    print(f"  账号 {stats['accounts']}（{TEST_USERNAME}），角色 {stats['characters']}，"
          f"物品 {len(rows[3])}，邮件 {len(rows[9])}，"
          f"契约 {len(rows[1])}，完成任务 {len(rows[6])}")
    print("回放逐字段校验通过（账号/角色/物品/邮件行数与内容一致）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
