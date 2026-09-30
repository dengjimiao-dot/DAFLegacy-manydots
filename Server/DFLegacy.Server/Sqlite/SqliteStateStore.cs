using Microsoft.Data.Sqlite;

namespace DFLegacy.Server.Sqlite;

/// <param name="FatigueDayKey">
/// 全局疲劳日键。库里按角色存 <c>character_fatigue.last_reset_at</c>（06:00 边界），
/// 本值取各角色边界的最大值派生；日键滚动由内核统一把各角色已消耗值归零。
/// </param>
internal sealed record SqliteStateSnapshot(
    List<AccountRecord> Accounts,
    uint NextCharacterNo,
    uint NextNormalAccountUid,
    uint NextTestAccountUid,
    int FatigueDayKey);

/// <summary>
/// 状态落盘（docs/design/08-sqlite-storage.md D4）。内存树与「上次已落盘」的指纹
/// 做 diff，只重写变更部分，全部包在单个 IMMEDIATE 事务里：复合操作要么全成要么
/// 全不成，不再有「改完内存再一次落盘」的丢失窗口。
///
/// <para>写入粒度是「账号标量 + 单个角色子树」：角色行删除后由外键级联清空其
/// 物品、技能、任务、副本、邮件、疲劳行，再整棵重插。这已把单次操作的代价与全库
/// 规模解耦（不再整文件重写）；再细到行级字段 diff 需要逐列维护可空语义，收益与
/// 风险不成比例，当前数据量下刻意不做。</para>
/// </summary>
internal sealed class SqliteStateStore(IGameDatabase database, ILogger logger)
{
    private readonly Dictionary<uint, string> _accountFingerprints = [];
    private readonly Dictionary<uint, string> _characterFingerprints = [];
    private int _persistedFatigueDayKey;

    public string DatabasePath => database.DatabasePath;

    /// <summary>整库读出内存树，并记录指纹基线（供后续增量写比对）。</summary>
    public SqliteStateSnapshot Load()
    {
        var snapshot = database.Read(LoadWithin);
        RecordFingerprints(snapshot);
        return snapshot;
    }

    /// <summary>
    /// 清空并按快照整库重建（播种与一次性导入用）。单事务，失败整体回滚，库回到
    /// 调用前的空基线。
    /// </summary>
    public void ReplaceAll(SqliteStateSnapshot snapshot)
    {
        database.Write((connection, transaction) =>
        {
            DeleteAllRows(connection, transaction);
            InsertSnapshot(connection, transaction, snapshot);
        });
        RecordFingerprints(snapshot);
        logger.LogInformation(
            "Replaced SQLite state for {AccountCount} account(s) at {DatabasePath}.",
            snapshot.Accounts.Count,
            DatabasePath);
    }

    /// <summary>
    /// 按 diff 增量落盘；无变更时不发任何写语句。删除一律排在插入之前：换角色
    /// （<c>ReplaceAllCharactersAsync</c>）会让新旧角色短暂同名并存，而
    /// <c>characters.name</c> 与 (account_id, slot_index) 都是唯一约束。
    /// </summary>
    public void Apply(SqliteStateSnapshot snapshot)
    {
        database.Write((connection, transaction) =>
        {
            var liveAccountUids = new HashSet<uint>();
            var liveCharacterNos = new HashSet<uint>();
            foreach (var account in snapshot.Accounts)
            {
                liveAccountUids.Add(account.AccountUid);
                foreach (var character in account.Characters)
                    liveCharacterNos.Add(character.CharacterNo);
            }

            foreach (var removedAccountUid in _accountFingerprints.Keys
                         .Where(uid => !liveAccountUids.Contains(uid))
                         .ToArray())
            {
                Execute(
                    connection,
                    transaction,
                    "DELETE FROM accounts WHERE account_id = @accountId;",
                    ("@accountId", (long)removedAccountUid));
            }

            foreach (var removedCharacterNo in _characterFingerprints.Keys
                         .Where(no => !liveCharacterNos.Contains(no))
                         .ToArray())
            {
                Execute(
                    connection,
                    transaction,
                    "DELETE FROM characters WHERE character_id = @characterId;",
                    ("@characterId", (long)removedCharacterNo));
            }

            var mentorIds = BuildMentorIdMap(snapshot);
            var fatigueBoundary = SqliteStateMapper.FatigueBoundaryUnixSeconds(
                snapshot.FatigueDayKey);
            var wroteCharacter = false;

            foreach (var account in snapshot.Accounts)
            {
                var changedAccount = !_accountFingerprints.TryGetValue(
                    account.AccountUid,
                    out var accountFingerprint)
                    || !string.Equals(
                        accountFingerprint,
                        SqliteStateMapper.Fingerprint(account),
                        StringComparison.Ordinal);
                if (changedAccount)
                {
                    UpsertAccount(connection, transaction, account);
                    ReplacePremiumServices(connection, transaction, account);
                }

                foreach (var character in account.Characters)
                {
                    if (_characterFingerprints.TryGetValue(character.CharacterNo, out var fingerprint)
                        && string.Equals(
                            fingerprint,
                            SqliteStateMapper.Fingerprint(character),
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    ReplaceCharacter(
                        connection,
                        transaction,
                        account,
                        character,
                        mentorIds,
                        fatigueBoundary);
                    wroteCharacter = true;
                }
            }

            // 日键滚动但各角色已消耗值本就为 0 时，指纹无变化、上面的循环不会写
            // 任何行；此处补一次全角色边界推进，避免 last_reset_at 永远停在旧日。
            if (!wroteCharacter && _persistedFatigueDayKey != snapshot.FatigueDayKey)
            {
                Execute(
                    connection,
                    transaction,
                    @"
UPDATE character_fatigue
SET last_reset_at = @lastResetAt
WHERE last_reset_at <> @lastResetAt;",
                    ("@lastResetAt", fatigueBoundary));
            }
        });

        RecordFingerprints(snapshot);
        logger.LogDebug(
            "Applied SQLite state diff for {AccountCount} account(s).",
            snapshot.Accounts.Count);
    }

    private static SqliteStateSnapshot LoadWithin(SqliteConnection connection)
    {
        var childrenByCharacterId = ReadCharacterChildren(connection);
        var premiumByAccount = ReadPremiumServices(connection);
        var fatigueDayKey = ReadFatigueDayKey(connection);
        var accounts = new List<AccountRecord>();
        uint maximumCharacterNo = 0;
        var nextTestAccountUid = 1u;
        var nextNormalAccountUid = AccountUidRules.NormalUidStart;

        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT account_id, account_uid, username, password_hash, oicq_code,
       preset_characters_initialized, cash
FROM accounts
ORDER BY account_id;";
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var accountUid = checked((uint)SqliteStateMapper.ReadInt64(
                    reader,
                    "account_id"));
                var cash = SqliteStateMapper.ReadInt32(reader, "cash");
                var characters = childrenByCharacterId
                    .Where(entry => entry.Value.AccountUid == accountUid)
                    .OrderBy(entry => entry.Key)
                    .Select(entry => ProjectCash(entry.Value.Character, cash))
                    .ToList();
                foreach (var character in characters)
                {
                    maximumCharacterNo = Math.Max(maximumCharacterNo, character.CharacterNo);
                }

                AdvanceAccountUidCounter(
                    accountUid,
                    ref nextTestAccountUid,
                    ref nextNormalAccountUid);
                accounts.Add(new AccountRecord(
                    Id: SqliteStateMapper.ParseUid(
                        SqliteStateMapper.ReadString(reader, "account_uid")),
                    UserName: SqliteStateMapper.ReadString(reader, "username"),
                    PasswordHash: SqliteStateMapper.ReadString(reader, "password_hash"),
                    Characters: characters,
                    AccountUid: accountUid,
                    PresetCharactersInitialized:
                        SqliteStateMapper.ReadInt64(
                            reader,
                            "preset_characters_initialized") != 0,
                    PremiumServices: premiumByAccount.GetValueOrDefault(accountUid),
                    Cash: cash,
                    OicqCode: SqliteStateMapper.ReadString(reader, "oicq_code")));
            }
        }

        // 计数器推进到已导入最大值之上，不再单独建表存计数器（D5）。
        return new SqliteStateSnapshot(
            accounts,
            maximumCharacterNo == uint.MaxValue ? uint.MaxValue : maximumCharacterNo + 1,
            nextNormalAccountUid,
            nextTestAccountUid,
            fatigueDayKey);
    }

    private static CharacterRecord ProjectCash(CharacterRecord character, int cash) =>
        character.Cash == cash ? character : character with { Cash = cash };

    private static int ReadFatigueDayKey(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT last_reset_at
FROM character_fatigue
WHERE last_reset_at > 0
ORDER BY last_reset_at DESC
LIMIT 1;";
        if (command.ExecuteScalar() is not { } scalar || Convert.ToInt64(scalar) <= 0)
            return GameStoreCore.GetFatigueDayKey(DateTimeOffset.Now);

        return SqliteStateMapper.FatigueDayKeyFromBoundary(Convert.ToInt64(scalar));
    }

    private static void AdvanceAccountUidCounter(
        uint accountUid,
        ref uint nextTestAccountUid,
        ref uint nextNormalAccountUid)
    {
        if (AccountUidRules.IsTest(accountUid))
        {
            if (accountUid == AccountUidRules.MaximumTestUid)
                nextTestAccountUid = AccountUidRules.MaximumTestUid;
            else if (nextTestAccountUid <= accountUid)
                nextTestAccountUid = accountUid + 1;
        }
        else if (AccountUidRules.IsNormal(accountUid)
            && nextNormalAccountUid <= accountUid)
        {
            nextNormalAccountUid = accountUid == uint.MaxValue
                ? uint.MaxValue
                : accountUid + 1;
        }
    }

    /// <summary>
    /// 读全部角色及其子集合。可空集合读回时归一为非 null 空表：JSON 模型里 null
    /// 表示「尚未规范化」，内核与登录流程见到 null 会重写一次；SQLite 每次落盘都
    /// 写入规范化后的集合，故该门控永不触发（见 SqliteStateMapper 头注）。
    /// </summary>
    private static Dictionary<uint, CharacterEntry> ReadCharacterChildren(
        SqliteConnection connection)
    {
        var items = ReadItems(connection);
        var skills = ReadSkills(connection);
        var activeQuests = ReadQuests(connection);
        var completedQuests = ReadCompletedQuests(connection);
        var unlocks = ReadUnlocks(connection);
        var progress = ReadDungeonProgress(connection);
        var mailbox = ReadMailbox(connection);
        var fatigue = ReadFatigue(connection);

        var entries = new Dictionary<uint, CharacterEntry>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = @"
SELECT character_id, character_uid, account_id, slot_index, name, job,
       grow_type, awakening_type, is_preset, level, exp, skill_points,
       bonus_skill_points, gold, victory_points, pvp_points, pvp_grade,
       pvp_wins, pvp_losses, pvp_play_count, pvp_count, pvp_grade_extension,
       warehouse_capacity, tutorial_started, weakness_recovery, town_id,
       area_id, pos_x, pos_y, direction, bonus_strength, bonus_intelligence,
       bonus_vitality, bonus_spirit, bonus_maximum_hp, bonus_maximum_mp,
       bonus_movement_speed, bonus_all_resistance, mentor_character_id,
       mentor_exp_bonus_percent
FROM characters
ORDER BY account_id, slot_index;";
            using var reader = command.ExecuteReader();
            var rows = new List<(uint CharacterId, uint AccountUid, CharacterRow Row)>();
            while (reader.Read())
            {
                var characterId = checked((uint)SqliteStateMapper.ReadInt64(
                    reader,
                    "character_id"));
                rows.Add((
                    characterId,
                    checked((uint)SqliteStateMapper.ReadInt64(reader, "account_id")),
                    CharacterRow.Read(reader)));
            }

            // 师父以整数列存放，读回 record 需还原 Guid：先建全服 character_id→Guid
            // 映射。悬空 id 视为无师父（docs/design/08 · §8 K3）。
            var uidByCharacterId = rows.ToDictionary(
                row => row.CharacterId,
                row => row.Row.Id);
            foreach (var (characterId, accountUid, row) in rows)
            {
                var character = row.ToRecord(characterId, accountUid, fatigue.GetValueOrDefault(characterId));
                character = character with
                {
                    Inventory = Pick(items, characterId, SqliteStateMapper.InventoryListType),
                    Equipment = Pick(items, characterId, SqliteStateMapper.EquipmentListType),
                    Warehouse = Pick(items, characterId, SqliteStateMapper.WarehouseListType),
                    AvatarInventory = Pick(items, characterId, SqliteStateMapper.AvatarListType),
                    CreatureInventory = Pick(items, characterId, SqliteStateMapper.CreatureListType),
                    Skills = skills.GetValueOrDefault(characterId) ?? [],
                    Quests = activeQuests.GetValueOrDefault(characterId) ?? [],
                    CompletedQuestIds = completedQuests
                        .TryGetValue(characterId, out var completed)
                            ? completed.Completed
                            : [],
                    RewardedQuestIds = completedQuests
                        .TryGetValue(characterId, out var rewarded)
                            ? rewarded.Rewarded
                            : [],
                    UnlockedDungeonIds = unlocks.GetValueOrDefault(characterId) ?? [],
                    DungeonProgress = progress.GetValueOrDefault(characterId) ?? [],
                    Mailbox = mailbox.GetValueOrDefault(characterId) ?? [],
                    MentorCharacterId = row.MentorCharacterId <= 0
                        ? null
                        : uidByCharacterId.TryGetValue(
                                checked((uint)row.MentorCharacterId),
                                out var mentorUid)
                            && mentorUid != Guid.Empty
                            ? mentorUid
                            : null
                };
                entries[characterId] = new CharacterEntry(accountUid, character);
            }
        }

        return entries;
    }

    private static List<CharacterItemRecord> Pick(
        IReadOnlyDictionary<(uint CharacterId, int ListType), List<CharacterItemRecord>> items,
        uint characterId,
        int listType) =>
        items.TryGetValue((characterId, listType), out var list) ? list : [];

    private static Dictionary<(uint, int), List<CharacterItemRecord>> ReadItems(
        SqliteConnection connection)
    {
        var result = new Dictionary<(uint, int), List<CharacterItemRecord>>();
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT character_id, list_type, slot_index, item_id, item_count, item_state,
       durability, seal_state, avatar_remaining_seconds, avatar_ability_index,
       creature_stomach, creature_experience, creature_level, creature_name,
       creature_no_charge, creature_stomach_remainder_seconds,
       equipment_quality_seed, instance_id
FROM character_items
ORDER BY character_id, list_type, slot_index;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var key = (
                checked((uint)SqliteStateMapper.ReadInt64(reader, "character_id")),
                SqliteStateMapper.ReadInt32(reader, "list_type"));
            if (!result.TryGetValue(key, out var list))
            {
                list = [];
                result[key] = list;
            }

            list.Add(SqliteStateMapper.ReadItem(reader));
        }

        return result;
    }

    private static Dictionary<uint, List<CharacterSkillRecord>> ReadSkills(
        SqliteConnection connection)
    {
        var result = new Dictionary<uint, List<CharacterSkillRecord>>();
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT character_id, slot_index, skill_id, skill_level
FROM character_skills
ORDER BY character_id, slot_index;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var characterId = checked((uint)SqliteStateMapper.ReadInt64(
                reader,
                "character_id"));
            if (!result.TryGetValue(characterId, out var list))
            {
                list = [];
                result[characterId] = list;
            }

            list.Add(new CharacterSkillRecord(
                Slot: checked((byte)SqliteStateMapper.ReadInt64(reader, "slot_index")),
                SkillId: checked((byte)SqliteStateMapper.ReadInt64(reader, "skill_id")),
                Level: checked((byte)SqliteStateMapper.ReadInt64(
                    reader,
                    "skill_level"))));
        }

        return result;
    }

    private static Dictionary<uint, List<CharacterQuestRecord>> ReadQuests(
        SqliteConnection connection)
    {
        var result = new Dictionary<uint, List<CharacterQuestRecord>>();
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT character_id, quest_id, trigger_value
FROM character_quests
ORDER BY character_id, quest_id;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var characterId = checked((uint)SqliteStateMapper.ReadInt64(
                reader,
                "character_id"));
            if (!result.TryGetValue(characterId, out var list))
            {
                list = [];
                result[characterId] = list;
            }

            list.Add(new CharacterQuestRecord(
                QuestId: checked((ushort)SqliteStateMapper.ReadInt64(
                    reader,
                    "quest_id")),
                Trigger: checked((uint)SqliteStateMapper.ReadInt64(
                    reader,
                    "trigger_value"))));
        }

        return result;
    }

    private static Dictionary<uint, CompletedQuestSet> ReadCompletedQuests(
        SqliteConnection connection)
    {
        var result = new Dictionary<uint, CompletedQuestSet>();
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT character_id, quest_id, rewarded_at
FROM character_completed_quests
ORDER BY character_id, quest_id;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var characterId = checked((uint)SqliteStateMapper.ReadInt64(
                reader,
                "character_id"));
            if (!result.TryGetValue(characterId, out var set))
            {
                set = new CompletedQuestSet();
                result[characterId] = set;
            }

            var questId = checked((ushort)SqliteStateMapper.ReadInt64(
                reader,
                "quest_id"));
            set.Completed.Add(questId);
            if (!reader.IsDBNull(reader.GetOrdinal("rewarded_at")))
                set.Rewarded.Add(questId);
        }

        return result;
    }

    private static Dictionary<uint, List<ushort>> ReadUnlocks(SqliteConnection connection)
    {
        var result = new Dictionary<uint, List<ushort>>();
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT character_id, dungeon_id
FROM character_dungeon_unlocks
ORDER BY character_id, dungeon_id;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var characterId = checked((uint)SqliteStateMapper.ReadInt64(
                reader,
                "character_id"));
            if (!result.TryGetValue(characterId, out var list))
            {
                list = [];
                result[characterId] = list;
            }

            list.Add(checked((ushort)SqliteStateMapper.ReadInt64(reader, "dungeon_id")));
        }

        return result;
    }

    private static Dictionary<uint, List<CharacterDungeonProgressRecord>>
        ReadDungeonProgress(SqliteConnection connection)
    {
        var result = new Dictionary<uint, List<CharacterDungeonProgressRecord>>();
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT character_id, dungeon_id, max_difficulty
FROM character_dungeon_progress
ORDER BY character_id, dungeon_id;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var characterId = checked((uint)SqliteStateMapper.ReadInt64(
                reader,
                "character_id"));
            if (!result.TryGetValue(characterId, out var list))
            {
                list = [];
                result[characterId] = list;
            }

            list.Add(new CharacterDungeonProgressRecord(
                DungeonId: checked((ushort)SqliteStateMapper.ReadInt64(
                    reader,
                    "dungeon_id")),
                MaximumDifficulty: checked((byte)SqliteStateMapper.ReadInt64(
                    reader,
                    "max_difficulty"))));
        }

        return result;
    }

    private static Dictionary<uint, List<CharacterMailRecord>> ReadMailbox(
        SqliteConnection connection)
    {
        var result = new Dictionary<uint, List<CharacterMailRecord>>();
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT mail_id, character_id, sender, text, gold, item_id, item_count,
       item_state, durability, seal_state, avatar_remaining_seconds,
       avatar_ability_index, equipment_quality_seed, instance_id, is_new,
       state, created_at, expires_at
FROM character_mail
ORDER BY character_id, mail_id;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var characterId = checked((uint)SqliteStateMapper.ReadInt64(
                reader,
                "character_id"));
            if (!result.TryGetValue(characterId, out var list))
            {
                list = [];
                result[characterId] = list;
            }

            list.Add(SqliteStateMapper.ReadMail(reader));
        }

        return result;
    }

    private static Dictionary<uint, ushort> ReadFatigue(SqliteConnection connection)
    {
        var result = new Dictionary<uint, ushort>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT character_id, consumed FROM character_fatigue;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result[checked((uint)SqliteStateMapper.ReadInt64(reader, "character_id"))] =
                checked((ushort)SqliteStateMapper.ReadInt64(reader, "consumed"));
        }

        return result;
    }

    private static Dictionary<uint, List<AccountPremiumServiceRecord>>
        ReadPremiumServices(SqliteConnection connection)
    {
        var result = new Dictionary<uint, List<AccountPremiumServiceRecord>>();
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT account_id, premium_type, expires_at
FROM account_premium
ORDER BY account_id, premium_type;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var accountUid = checked((uint)SqliteStateMapper.ReadInt64(
                reader,
                "account_id"));
            if (!result.TryGetValue(accountUid, out var list))
            {
                list = [];
                result[accountUid] = list;
            }

            var expiresAt = SqliteStateMapper.ReadInt64(reader, "expires_at");
            list.Add(new AccountPremiumServiceRecord(
                ServiceType: checked((byte)SqliteStateMapper.ReadInt64(
                    reader,
                    "premium_type")),
                ExpiresAt: expiresAt <= 0 ? null : checked((uint)expiresAt)));
        }

        return result;
    }

    private void RecordFingerprints(SqliteStateSnapshot snapshot)
    {
        _accountFingerprints.Clear();
        _characterFingerprints.Clear();
        foreach (var account in snapshot.Accounts)
        {
            _accountFingerprints[account.AccountUid] =
                SqliteStateMapper.Fingerprint(account);
            foreach (var character in account.Characters)
            {
                _characterFingerprints[character.CharacterNo] =
                    SqliteStateMapper.Fingerprint(character);
            }
        }

        _persistedFatigueDayKey = snapshot.FatigueDayKey;
    }

    private static Dictionary<Guid, uint> BuildMentorIdMap(SqliteStateSnapshot snapshot)
    {
        var map = new Dictionary<Guid, uint>();
        foreach (var account in snapshot.Accounts)
        {
            foreach (var character in account.Characters)
            {
                if (character.Id != Guid.Empty)
                    map[character.Id] = character.CharacterNo;
            }
        }

        return map;
    }

    private static void DeleteAllRows(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        foreach (var sql in new[]
                 {
                     "DELETE FROM character_mail;",
                     "DELETE FROM character_fatigue;",
                     "DELETE FROM character_dungeon_progress;",
                     "DELETE FROM character_dungeon_unlocks;",
                     "DELETE FROM character_completed_quests;",
                     "DELETE FROM character_quests;",
                     "DELETE FROM character_skills;",
                     "DELETE FROM character_items;",
                     "DELETE FROM characters;",
                     "DELETE FROM account_premium;",
                     "DELETE FROM accounts;"
                 })
        {
            Execute(connection, transaction, sql);
        }
    }

    private static void InsertSnapshot(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SqliteStateSnapshot snapshot)
    {
        var mentorIds = BuildMentorIdMap(snapshot);
        var fatigueBoundary = SqliteStateMapper.FatigueBoundaryUnixSeconds(
            snapshot.FatigueDayKey);
        foreach (var account in snapshot.Accounts)
        {
            UpsertAccount(connection, transaction, account);
            ReplacePremiumServices(connection, transaction, account);
            foreach (var character in account.Characters)
            {
                ReplaceCharacter(
                    connection,
                    transaction,
                    account,
                    character,
                    mentorIds,
                    fatigueBoundary);
            }
        }
    }

    private static void UpsertAccount(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AccountRecord account)
    {
        Execute(
            connection,
            transaction,
            @"
INSERT INTO accounts (
    account_id, account_uid, username, password_hash, oicq_code,
    preset_characters_initialized, cash
) VALUES (
    @accountId, @accountUid, @username, @passwordHash, @oicqCode,
    @presetCharactersInitialized, @cash
)
ON CONFLICT(account_id) DO UPDATE SET
    account_uid = excluded.account_uid,
    username = excluded.username,
    password_hash = excluded.password_hash,
    oicq_code = excluded.oicq_code,
    preset_characters_initialized = excluded.preset_characters_initialized,
    cash = excluded.cash;",
            ("@accountId", (long)account.AccountUid),
            ("@accountUid", SqliteStateMapper.FormatUid(account.Id)),
            ("@username", account.UserName),
            ("@passwordHash", account.PasswordHash),
            ("@oicqCode", account.OicqCode ?? string.Empty),
            ("@presetCharactersInitialized",
                account.PresetCharactersInitialized ? 1L : 0L),
            ("@cash", (long)account.Cash));
    }

    private static void ReplacePremiumServices(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AccountRecord account)
    {
        Execute(
            connection,
            transaction,
            "DELETE FROM account_premium WHERE account_id = @accountId;",
            ("@accountId", (long)account.AccountUid));

        foreach (var service in account.PremiumServices ?? [])
        {
            Execute(
                connection,
                transaction,
                @"
INSERT INTO account_premium (account_id, premium_type, expires_at)
VALUES (@accountId, @premiumType, @expiresAt);",
                ("@accountId", (long)account.AccountUid),
                ("@premiumType", (long)service.ServiceType),
                ("@expiresAt", (long)(service.ExpiresAt ?? 0)));
        }
    }

    private static void ReplaceCharacter(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AccountRecord account,
        CharacterRecord character,
        IReadOnlyDictionary<Guid, uint> mentorIds,
        long fatigueBoundary)
    {
        // 删除角色行即由外键级联清空其物品、技能、任务、副本、邮件与疲劳行。
        Execute(
            connection,
            transaction,
            "DELETE FROM characters WHERE character_id = @characterId;",
            ("@characterId", (long)character.CharacterNo));

        Execute(
            connection,
            transaction,
            @"
INSERT INTO characters (
    character_id, character_uid, account_id, slot_index, name, job, grow_type,
    awakening_type, is_preset, level, exp, skill_points, bonus_skill_points,
    gold, victory_points, pvp_points, pvp_grade, pvp_wins, pvp_losses,
    pvp_play_count, pvp_count, pvp_grade_extension, warehouse_capacity,
    tutorial_started, weakness_recovery, town_id, area_id, pos_x, pos_y,
    direction, bonus_strength, bonus_intelligence, bonus_vitality, bonus_spirit,
    bonus_maximum_hp, bonus_maximum_mp, bonus_movement_speed,
    bonus_all_resistance, mentor_character_id, mentor_exp_bonus_percent
) VALUES (
    @characterId, @characterUid, @accountId, @slotIndex, @name, @job, @growType,
    @awakeningType, @isPreset, @level, @exp, @skillPoints, @bonusSkillPoints,
    @gold, @victoryPoints, @pvpPoints, @pvpGrade, @pvpWins, @pvpLosses,
    @pvpPlayCount, @pvpCount, @pvpGradeExtension, @warehouseCapacity,
    @tutorialStarted, @weaknessRecovery, @townId, @areaId, @posX, @posY,
    @direction, @bonusStrength, @bonusIntelligence, @bonusVitality, @bonusSpirit,
    @bonusMaximumHp, @bonusMaximumMp, @bonusMovementSpeed,
    @bonusAllResistance, @mentorCharacterId, @mentorExperienceBonusPercent
);",
            ("@characterId", (long)character.CharacterNo),
            ("@characterUid", SqliteStateMapper.FormatUid(character.Id)),
            ("@accountId", (long)account.AccountUid),
            ("@slotIndex", (long)character.Slot),
            ("@name", character.Name),
            ("@job", (long)character.Job),
            ("@growType", (long)character.GrowType),
            ("@awakeningType", (long)character.AwakeningType),
            ("@isPreset", character.IsPreset ? 1L : 0L),
            ("@level", (long)character.Level),
            ("@exp", (long)character.Experience),
            ("@skillPoints", (long)character.Sp),
            ("@bonusSkillPoints", (long)character.BonusSp),
            ("@gold", (long)character.Gold),
            ("@victoryPoints", (long)character.VictoryPoints),
            ("@pvpPoints", (long)character.PvpPoints),
            ("@pvpGrade", (long)character.PvpGrade),
            ("@pvpWins", (long)character.PvpWins),
            ("@pvpLosses", (long)character.PvpLosses),
            ("@pvpPlayCount", (long)character.PvpPlayCount),
            ("@pvpCount", (long)character.PvpCount),
            ("@pvpGradeExtension", (long)character.PvpGradeExtension),
            ("@warehouseCapacity", (long)character.WarehouseCapacity),
            ("@tutorialStarted", character.TutorialStarted ? 1L : 0L),
            ("@weaknessRecovery", (long)character.WeaknessRecovery),
            ("@townId", (long)character.TownId),
            ("@areaId", (long)character.AreaId),
            ("@posX", (long)character.X),
            ("@posY", (long)character.Y),
            ("@direction", (long)character.Direction),
            ("@bonusStrength", (long)character.BonusStrength),
            ("@bonusIntelligence", (long)character.BonusIntelligence),
            ("@bonusVitality", (long)character.BonusVitality),
            ("@bonusSpirit", (long)character.BonusSpirit),
            ("@bonusMaximumHp", (long)character.BonusMaximumHp),
            ("@bonusMaximumMp", (long)character.BonusMaximumMp),
            ("@bonusMovementSpeed", (long)character.BonusMovementSpeed),
            ("@bonusAllResistance", (long)character.BonusAllElementResistance),
            ("@mentorCharacterId", ResolveMentorId(character, mentorIds)),
            ("@mentorExperienceBonusPercent",
                (long)character.MentorExperienceBonusPercent));

        InsertItems(connection, transaction, character);
        InsertSkills(connection, transaction, character);
        InsertQuests(connection, transaction, character);
        InsertDungeonState(connection, transaction, character);
        InsertMailbox(connection, transaction, character);

        Execute(
            connection,
            transaction,
            @"
INSERT INTO character_fatigue (character_id, consumed, last_reset_at)
VALUES (@characterId, @consumed, @lastResetAt)
ON CONFLICT(character_id) DO UPDATE SET
    consumed = excluded.consumed,
    last_reset_at = excluded.last_reset_at;",
            ("@characterId", (long)character.CharacterNo),
            ("@consumed", (long)character.UsedFatigue),
            ("@lastResetAt", fatigueBoundary));
    }

    private static long ResolveMentorId(
        CharacterRecord character,
        IReadOnlyDictionary<Guid, uint> mentorIds) =>
        character.MentorCharacterId is { } mentorId
        && mentorId != Guid.Empty
        && mentorIds.TryGetValue(mentorId, out var mentorCharacterNo)
            ? mentorCharacterNo
            : 0L;

    private static void InsertItems(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CharacterRecord character)
    {
        InsertItems(
            connection,
            transaction,
            character.CharacterNo,
            character.Inventory,
            SqliteStateMapper.InventoryListType);
        InsertItems(
            connection,
            transaction,
            character.CharacterNo,
            character.Equipment,
            SqliteStateMapper.EquipmentListType);
        InsertItems(
            connection,
            transaction,
            character.CharacterNo,
            character.Warehouse,
            SqliteStateMapper.WarehouseListType);
        InsertItems(
            connection,
            transaction,
            character.CharacterNo,
            character.AvatarInventory,
            SqliteStateMapper.AvatarListType);
        InsertItems(
            connection,
            transaction,
            character.CharacterNo,
            character.CreatureInventory,
            SqliteStateMapper.CreatureListType);
    }

    private static void InsertItems(
        SqliteConnection connection,
        SqliteTransaction transaction,
        uint characterNo,
        IEnumerable<CharacterItemRecord>? items,
        int listType)
    {
        if (items is null)
            return;

        foreach (var item in items)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO character_items (
    character_id, list_type, slot_index, item_id, item_count, item_state,
    durability, seal_state, lock_state, avatar_remaining_seconds,
    avatar_ability_index, creature_stomach, creature_experience, creature_level,
    creature_name, creature_no_charge, creature_stomach_remainder_seconds,
    equipment_quality_seed, instance_id
) VALUES (
    @character_id, @list_type, @slot_index, @item_id, @item_count, @item_state,
    @durability, @seal_state, @lock_state, @avatar_remaining_seconds,
    @avatar_ability_index, @creature_stomach, @creature_experience, @creature_level,
    @creature_name, @creature_no_charge, @creature_stomach_remainder_seconds,
    @equipment_quality_seed, @instance_id
);";
            SqliteStateMapper.BindItem(command, characterNo, listType, item);
            command.ExecuteNonQuery();
        }
    }

    private static void InsertSkills(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CharacterRecord character)
    {
        if (character.Skills is null)
            return;

        foreach (var skill in character.Skills)
        {
            Execute(
                connection,
                transaction,
                @"
INSERT INTO character_skills (character_id, slot_index, skill_id, skill_level)
VALUES (@characterId, @slotIndex, @skillId, @skillLevel);",
                ("@characterId", (long)character.CharacterNo),
                ("@slotIndex", (long)skill.Slot),
                ("@skillId", (long)skill.SkillId),
                ("@skillLevel", (long)skill.Level));
        }
    }

    private static void InsertQuests(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CharacterRecord character)
    {
        if (character.Quests is not null)
        {
            foreach (var quest in character.Quests)
            {
                Execute(
                    connection,
                    transaction,
                    @"
INSERT INTO character_quests (character_id, quest_id, trigger_value)
VALUES (@characterId, @questId, @triggerValue);",
                    ("@characterId", (long)character.CharacterNo),
                    ("@questId", (long)quest.QuestId),
                    ("@triggerValue", (long)quest.Trigger));
            }
        }

        if (character.CompletedQuestIds is null)
            return;

        var rewarded = (character.RewardedQuestIds ?? []).ToHashSet();
        foreach (var questId in character.CompletedQuestIds)
        {
            // JSON 无完成/领奖时间源，两者只作「是否领过奖」的判别（docs/design/08
            // · §6.2），故一律戳本次落盘时刻，不承担时间语义。
            Execute(
                connection,
                transaction,
                @"
INSERT INTO character_completed_quests (
    character_id, quest_id, completed_at, rewarded_at
) VALUES (
    @characterId, @questId, CURRENT_TIMESTAMP, @rewardedAt
);",
                ("@characterId", (long)character.CharacterNo),
                ("@questId", (long)questId),
                ("@rewardedAt", rewarded.Contains(questId)
                    ? (object)DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")
                    : DBNull.Value));
        }
    }

    private static void InsertDungeonState(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CharacterRecord character)
    {
        if (character.UnlockedDungeonIds is not null)
        {
            foreach (var dungeonId in character.UnlockedDungeonIds)
            {
                Execute(
                    connection,
                    transaction,
                    @"
INSERT INTO character_dungeon_unlocks (character_id, dungeon_id)
VALUES (@characterId, @dungeonId);",
                    ("@characterId", (long)character.CharacterNo),
                    ("@dungeonId", (long)dungeonId));
            }
        }

        if (character.DungeonProgress is null)
            return;

        foreach (var entry in character.DungeonProgress)
        {
            Execute(
                connection,
                transaction,
                @"
INSERT INTO character_dungeon_progress (
    character_id, dungeon_id, max_difficulty
) VALUES (
    @characterId, @dungeonId, @maxDifficulty
);",
                ("@characterId", (long)character.CharacterNo),
                ("@dungeonId", (long)entry.DungeonId),
                ("@maxDifficulty", (long)entry.MaximumDifficulty));
        }
    }

    private static void InsertMailbox(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CharacterRecord character)
    {
        if (character.Mailbox is null)
            return;

        foreach (var mail in character.Mailbox)
        {
            Execute(
                connection,
                transaction,
                @"
INSERT INTO character_mail (
    mail_id, character_id, sender, text, gold, item_id, item_count, item_state,
    durability, seal_state, avatar_remaining_seconds, avatar_ability_index,
    equipment_quality_seed, instance_id, is_new, state, created_at, expires_at
) VALUES (
    @mailId, @characterId, @sender, @text, @gold, @itemId, @itemCount, @itemState,
    @durability, @sealState, @avatarRemainingSeconds, @avatarAbilityIndex,
    @equipmentQualitySeed, @instanceId, @isNew, @state, @createdAt, @expiresAt
);",
                ("@mailId", (long)mail.Id),
                ("@characterId", (long)character.CharacterNo),
                ("@sender", mail.Sender),
                ("@text", mail.Text),
                ("@gold", (long)mail.Gold),
                ("@itemId", (long)(mail.Attachment?.ItemId ?? 0)),
                ("@itemCount", (long)(mail.Attachment?.CountOrValue ?? 0)),
                ("@itemState", (long)(mail.Attachment?.State ?? 0)),
                ("@durability", (long)(mail.Attachment?.Durability ?? 0)),
                ("@sealState", (long)(mail.Attachment?.SealState ?? 0)),
                ("@avatarRemainingSeconds", ToSql(mail.Attachment?.AvatarRemainingSeconds)),
                ("@avatarAbilityIndex", ToSql(mail.Attachment?.AvatarAbilityIndex)),
                ("@equipmentQualitySeed", ToSql(mail.Attachment?.EquipmentQualitySeed)),
                ("@instanceId",
                    SqliteStateMapper.FormatUid(mail.Attachment?.InstanceId ?? default)),
                ("@isNew", mail.IsNew ? 1L : 0L),
                ("@state", (long)mail.State),
                ("@createdAt", (long)mail.SentAt),
                ("@expiresAt", (long)mail.ExpiresAt));
        }
    }

    private static object ToSql<T>(T? value) where T : struct =>
        value.HasValue ? value.Value : DBNull.Value;

    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    private sealed record CharacterEntry(uint AccountUid, CharacterRecord Character);

    private sealed class CompletedQuestSet
    {
        internal List<ushort> Completed { get; } = [];

        internal List<ushort> Rewarded { get; } = [];
    }

    private sealed record CharacterRow(
        Guid Id,
        byte Slot,
        string Name,
        int Job,
        int GrowType,
        int AwakeningType,
        bool IsPreset,
        int Level,
        uint Experience,
        int Sp,
        int BonusSp,
        int Gold,
        uint VictoryPoints,
        int PvpPoints,
        byte PvpGrade,
        int PvpWins,
        int PvpLosses,
        int PvpPlayCount,
        int PvpCount,
        byte PvpGradeExtension,
        ushort WarehouseCapacity,
        bool TutorialStarted,
        byte WeaknessRecovery,
        byte TownId,
        byte AreaId,
        short X,
        short Y,
        byte Direction,
        int BonusStrength,
        int BonusIntelligence,
        int BonusVitality,
        int BonusSpirit,
        int BonusMaximumHp,
        int BonusMaximumMp,
        int BonusMovementSpeed,
        int BonusAllElementResistance,
        long MentorCharacterId,
        byte MentorExperienceBonusPercent)
    {
        internal static CharacterRow Read(SqliteDataReader reader) =>
            new(
                Id: SqliteStateMapper.ParseUid(
                    SqliteStateMapper.ReadString(reader, "character_uid")),
                Slot: checked((byte)SqliteStateMapper.ReadInt64(reader, "slot_index")),
                Name: SqliteStateMapper.ReadString(reader, "name"),
                Job: SqliteStateMapper.ReadInt32(reader, "job"),
                GrowType: SqliteStateMapper.ReadInt32(reader, "grow_type"),
                AwakeningType: SqliteStateMapper.ReadInt32(reader, "awakening_type"),
                IsPreset: SqliteStateMapper.ReadInt64(reader, "is_preset") != 0,
                Level: SqliteStateMapper.ReadInt32(reader, "level"),
                Experience: checked((uint)SqliteStateMapper.ReadInt64(reader, "exp")),
                Sp: SqliteStateMapper.ReadInt32(reader, "skill_points"),
                BonusSp: SqliteStateMapper.ReadInt32(reader, "bonus_skill_points"),
                Gold: SqliteStateMapper.ReadInt32(reader, "gold"),
                VictoryPoints: checked((uint)SqliteStateMapper.ReadInt64(
                    reader,
                    "victory_points")),
                PvpPoints: SqliteStateMapper.ReadInt32(reader, "pvp_points"),
                PvpGrade: checked((byte)SqliteStateMapper.ReadInt64(reader, "pvp_grade")),
                PvpWins: SqliteStateMapper.ReadInt32(reader, "pvp_wins"),
                PvpLosses: SqliteStateMapper.ReadInt32(reader, "pvp_losses"),
                PvpPlayCount: SqliteStateMapper.ReadInt32(reader, "pvp_play_count"),
                PvpCount: SqliteStateMapper.ReadInt32(reader, "pvp_count"),
                PvpGradeExtension: checked((byte)SqliteStateMapper.ReadInt64(
                    reader,
                    "pvp_grade_extension")),
                WarehouseCapacity: checked((ushort)SqliteStateMapper.ReadInt64(
                    reader,
                    "warehouse_capacity")),
                TutorialStarted: SqliteStateMapper.ReadInt64(
                    reader,
                    "tutorial_started") != 0,
                WeaknessRecovery: checked((byte)SqliteStateMapper.ReadInt64(
                    reader,
                    "weakness_recovery")),
                TownId: checked((byte)SqliteStateMapper.ReadInt64(reader, "town_id")),
                AreaId: checked((byte)SqliteStateMapper.ReadInt64(reader, "area_id")),
                X: checked((short)SqliteStateMapper.ReadInt64(reader, "pos_x")),
                Y: checked((short)SqliteStateMapper.ReadInt64(reader, "pos_y")),
                Direction: checked((byte)SqliteStateMapper.ReadInt64(reader, "direction")),
                BonusStrength: SqliteStateMapper.ReadInt32(reader, "bonus_strength"),
                BonusIntelligence: SqliteStateMapper.ReadInt32(
                    reader,
                    "bonus_intelligence"),
                BonusVitality: SqliteStateMapper.ReadInt32(reader, "bonus_vitality"),
                BonusSpirit: SqliteStateMapper.ReadInt32(reader, "bonus_spirit"),
                BonusMaximumHp: SqliteStateMapper.ReadInt32(reader, "bonus_maximum_hp"),
                BonusMaximumMp: SqliteStateMapper.ReadInt32(reader, "bonus_maximum_mp"),
                BonusMovementSpeed: SqliteStateMapper.ReadInt32(
                    reader,
                    "bonus_movement_speed"),
                BonusAllElementResistance: SqliteStateMapper.ReadInt32(
                    reader,
                    "bonus_all_resistance"),
                MentorCharacterId: SqliteStateMapper.ReadInt64(
                    reader,
                    "mentor_character_id"),
                MentorExperienceBonusPercent: checked((byte)SqliteStateMapper.ReadInt64(
                    reader,
                    "mentor_exp_bonus_percent")));

        /// <summary>
        /// Cash 留 0，加载完成后按账号行统一投影（D6 点券单源化：角色表无 cash 列）。
        /// </summary>
        internal CharacterRecord ToRecord(uint characterId, uint accountUid, ushort usedFatigue) =>
            new(
                Id: Id,
                Name: Name,
                Job: Job,
                GrowType: GrowType,
                Level: Level,
                Sp: Sp,
                Cash: 0,
                Gold: Gold,
                Experience: Experience,
                CharacterNo: characterId,
                AccountUid: accountUid,
                Slot: Slot,
                IsPreset: IsPreset,
                UsedFatigue: usedFatigue,
                BonusSp: BonusSp,
                BonusMaximumHp: BonusMaximumHp,
                BonusMaximumMp: BonusMaximumMp,
                BonusStrength: BonusStrength,
                BonusVitality: BonusVitality,
                BonusIntelligence: BonusIntelligence,
                BonusSpirit: BonusSpirit,
                BonusMovementSpeed: BonusMovementSpeed,
                BonusAllElementResistance: BonusAllElementResistance,
                AwakeningType: AwakeningType,
                WarehouseCapacity: WarehouseCapacity,
                TutorialStarted: TutorialStarted,
                WeaknessRecovery: WeaknessRecovery,
                TownId: TownId,
                AreaId: AreaId,
                X: X,
                Y: Y,
                Direction: Direction,
                VictoryPoints: VictoryPoints,
                PvpPoints: PvpPoints,
                PvpGrade: PvpGrade,
                PvpWins: PvpWins,
                PvpLosses: PvpLosses,
                PvpPlayCount: PvpPlayCount,
                PvpCount: PvpCount,
                PvpGradeExtension: PvpGradeExtension,
                MentorExperienceBonusPercent: MentorExperienceBonusPercent);
    }
}