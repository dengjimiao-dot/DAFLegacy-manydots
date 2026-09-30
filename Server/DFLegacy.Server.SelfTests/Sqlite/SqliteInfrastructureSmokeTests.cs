using DFLegacy.Server;
using DFLegacy.Server.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace DFLegacy.Server.SelfTests.Sqlite;

/// <summary>
/// SQLite 基础设施自检（docs/design/08-sqlite-storage.md M0 验收项）：新建库、
/// 重复初始化去重、异构基线拒绝启动、user_version 与 schema_metadata 双写互验、
/// 坏基线不留半初始化文件，以及增量步版本连续性。
/// </summary>
internal static class SqliteInfrastructureSmokeTests
{
    public static void Run(Action<bool, string> check)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"dflegacy-sqlite-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            CreatedDatabaseIsSeeded(root, check);
            RepeatedInitializationIsIdempotent(root, check);
            ForeignBaselineIsRejected(root, check);
            VersionMetadataMustAgree(root, check);
            CorruptBaselineLeavesNoDatabase(root, check);
            PresetCharacterIsNotSeededForNewTestAccounts(root, check);
            ServerSettingsAreTheSingleSource(root, check);
            StoreRoundTripsThroughTheDatabase(root, check);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ServerOptions OptionsFor(string root, string fileName) =>
        new() { DatabasePath = Path.Combine(root, fileName) };

    private static void CreatedDatabaseIsSeeded(string root, Action<bool, string> check)
    {
        var options = OptionsFor(root, "created.db");
        var store = TestGameStore.Create(options);
        store.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
        var database = TestGameStore.CreateDatabase(options);

        var tables = ReadTableNames(database);
        check(new[]
            {
                "schema_metadata",
                "accounts",
                "account_premium",
                "characters",
                "character_items",
                "character_skills",
                "character_quests",
                "character_completed_quests",
                "character_dungeon_unlocks",
                "character_dungeon_progress",
                "character_mail",
                "character_fatigue",
                "server_settings"
            }.All(tables.Contains),
            "a new database is created from the baseline schema with every implemented table");

        check(ReadInt64(database, "PRAGMA user_version;") == 100
                && ReadMetadata(database) == (100, 100),
            "a new database writes baseline_id and schema_version 100 and mirrors it into PRAGMA user_version");

        var journalMode = ReadString(database, "PRAGMA journal_mode;");
        check(string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase),
            "the bootstrap enables WAL outside any transaction");

        check(store.GetAccountsAsync().GetAwaiter().GetResult() is [],
            "an empty database stays empty: no account is seeded");
    }

    private static void RepeatedInitializationIsIdempotent(
        string root,
        Action<bool, string> check)
    {
        var options = OptionsFor(root, "created.db");
        var first = TestGameStore.CreateDatabase(options).ConnectionString;
        var second = TestGameStore.CreateDatabase(options).ConnectionString;
        check(string.Equals(first, second, StringComparison.Ordinal),
            "re-initializing the same database path reuses one connection string");

        TestGameStore.SeedDefaultTestAccount(options);
        var store = TestGameStore.Create(options);
        store.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
        var characters = store.GetCharactersAsync("test", CancellationToken.None)
            .GetAwaiter().GetResult();
        check(characters.Length == 1
                && characters[0].Name == GameStoreCore.PresetCharacterName,
            "an explicitly seeded test account keeps exactly one preset character across reopens");
    }

    private static void ForeignBaselineIsRejected(string root, Action<bool, string> check)
    {
        var options = OptionsFor(root, "foreign.db");
        var database = TestGameStore.CreateDatabase(options);
        // baseline_id != 100 即异构库：启动必须拒绝，且不做猜测性转换。
        database.Write(
            (connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE schema_metadata
SET baseline_id = 86
WHERE singleton_id = 1;";
                command.ExecuteNonQuery();
            });

        SqliteDatabaseBootstrap.ForgetInitializedPath(
            SqlitePaths.ResolveDatabasePath(options));
        check(Throws<InvalidOperationException>(
                () => TestGameStore.CreateDatabase(options)),
            "a database whose baseline_id is not 100 is rejected instead of converted");

        check(ReadInt64(
                GameDatabase.AttachInitialized(
                    database.ConnectionString,
                    string.Empty),
                "PRAGMA user_version;") == 100,
            "the rejected database is left untouched for manual inspection");
    }

    private static void VersionMetadataMustAgree(string root, Action<bool, string> check)
    {
        var options = OptionsFor(root, "drifted.db");
        var database = TestGameStore.CreateDatabase(options);
        database.Write(
            (connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "PRAGMA user_version = 101;";
                command.ExecuteNonQuery();
            });

        SqliteDatabaseBootstrap.ForgetInitializedPath(
            SqlitePaths.ResolveDatabasePath(options));
        check(Throws<InvalidOperationException>(
                () => TestGameStore.CreateDatabase(options)),
            "user_version drifting away from schema_metadata.schema_version is rejected");

        check(SqliteMigrations.CurrentVersion == 100
                && SqliteMigrations.BaselineId == 100,
            "the baseline is still at version 100 with no incremental steps applied");
    }

    private static void CorruptBaselineLeavesNoDatabase(
        string root,
        Action<bool, string> check)
    {
        check(File.Exists(SqlitePaths.BaselineSchemaPath),
            "the deployment ships Sqlite/act1_schema.sql next to the server assembly");

        // D2：基线不可用时建库失败，必须清掉 .db/.db-wal/.db-shm 三件套，绝不留
        // 半初始化的库（否则下一次启动会把残库当成既有数据）。
        var brokenDatabasePath = Path.Combine(root, "broken.db");
        var thrown = Throws<FileNotFoundException>(() =>
            SqliteDatabaseBootstrap.Initialize(
                brokenDatabasePath,
                Path.Combine(root, "no-such-baseline.sql"),
                NullLogger.Instance));
        check(thrown
                && !File.Exists(brokenDatabasePath)
                && !File.Exists(brokenDatabasePath + "-wal")
                && !File.Exists(brokenDatabasePath + "-shm"),
            "a failed bootstrap deletes the database, WAL and shared-memory files");

        var databasePath = Path.Combine(root, "healthy.db");
        TestGameStore.CreateDatabase(OptionsFor(root, "healthy.db"));
        // 最后一个连接关闭时 SQLite 会 checkpoint 并删除 -wal/-shm，故此处只断言库
        // 文件本身；WAL 是否生效由 journal_mode 断言（见 CreatedDatabaseIsSeeded）。
        check(File.Exists(databasePath),
            "a healthy bootstrap creates the database file");
    }

    /// <summary>
    /// 预置角色不是启动播种，而是「测试账号初始化」这一步的结果：账号由夹具或
    /// Admin API 显式建出后，启动流程给测试账号补预置角色。普通账号不补。
    /// </summary>
    private static void PresetCharacterIsNotSeededForNewTestAccounts(
        string root,
        Action<bool, string> check)
    {
        var options = OptionsFor(root, "preset-on-request.db");
        var store = TestGameStore.Create(options);
        store.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
        check(store.GetAccountsAsync().GetAwaiter().GetResult() is [],
            "a database with no accounts has none after initialization");

        store.CreateAccountAsync(
                new CreateAccountRequest(
                    "created-test",
                    "test-password",
                    TestAccount: true,
                    OicqCode: "10001"),
                CancellationToken.None)
            .GetAwaiter().GetResult();
        var testAccount = store.GetAccountsAsync().GetAwaiter().GetResult()
            .Single(account => account.UserName == "created-test");
        check(testAccount.Characters is [{ Name: GameStoreCore.PresetCharacterName }],
            "a test account created through the API receives its preset character at once");

        store.CreateAccountAsync(
                new CreateAccountRequest("created-normal", "test-password", OicqCode: "10002"),
                CancellationToken.None)
            .GetAwaiter().GetResult();
        var normalAccount = store.GetAccountsAsync().GetAwaiter().GetResult()
            .Single(account => account.UserName == "created-normal");
        check(normalAccount.Characters is [],
            "a normal account receives no preset character");
    }

    /// <summary>
    /// server_settings 是倍率与掉落开关的唯一真源：基线播种行为「国服原味 + 全量
    /// 掉落 + 深渊每怪 1 件 + 不掉配方」，启动读出的快照必须与之一致。
    /// </summary>
    private static void ServerSettingsAreTheSingleSource(string root, Action<bool, string> check)
    {
        var database = TestGameStore.CreateDatabase(OptionsFor(root, "settings.db"));
        var settings = ServerSettings.Read(database);
        check(settings is
        {
            ExperienceMultiplier: 1.0,
            ClearMultiplier: 1.0,
            GoldMultiplier: 1.0,
            NormalDropMultiplier: 1.0,
            HellDropMultiplier: 1.0,
            WorldDropRate: 1.0,
            ClearExpPerKill: 1.0,
            // 官方行为：每只怪最多 1 件、深渊不掉配方。
            HellDropRollTimes: 1,
            HellRecipeRate: 0.0,
            DropEnabled: true,
            DropRatePercent: 100.0,
            DropEconomicRate: 1.0,
            DropForce: false,
            ClearBonusBlackDiamondRate: 0.05,
            ClearBonusEventRate: 0.0,
            ClearBonusEventStartsAt: null,
            ClearBonusEventEndsAt: null
        },
            "the baseline server_settings row matches the official behaviour for every multiplier and drop control");

        check(settings.Describe().Contains("hellDropRollTimes=1", StringComparison.Ordinal)
                && settings.Describe().Contains("hellRecipeRate=0", StringComparison.Ordinal)
                && settings.Describe().Contains("dropRatePercent=100", StringComparison.Ordinal),
            "the startup description reports the effective values read from the database");

        // 改库即改生效值：GM 直接写本表，重启后读路径取到新值。
        database.Write(
            (connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE server_settings
SET drop_enabled = 0,
    experience_multiplier = 2.0,
    clear_bonus_event_rate = 0.5,
    clear_bonus_event_starts_at = '2026-10-01T00:00:00+00:00',
    clear_bonus_event_ends_at = '2026-10-08T00:00:00+00:00'
WHERE settings_id = 1;";
                command.ExecuteNonQuery();
            });

        var updated = ServerSettings.Read(database);
        check(updated is
            {
                DropEnabled: false,
                ExperienceMultiplier: 2.0,
                ClearBonusEventRate: 0.5
            }
            && updated.ClearBonusEventStartsAt
                == new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)
            && updated.ClearBonusEventEndsAt
                == new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero),
            "editing the server_settings row is what changes the effective values, including the event bonus window");
    }

    private static void StoreRoundTripsThroughTheDatabase(
        string root,
        Action<bool, string> check)
    {
        var options = OptionsFor(root, "roundtrip.db");
        var store = TestGameStore.Create(options);
        store.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
        var accountCreation = store.CreateAccountAsync(
                new CreateAccountRequest("roundtrip-user", "test-password", OicqCode: "20001"),
                CancellationToken.None)
            .GetAwaiter().GetResult();
        check(accountCreation.Created,
            "an account can be created against a fresh database");

        var created = store.CreateCharacterAsync(
                "roundtrip-user",
                new CreateCharacterRequest("RoundTrip", Job: 0, GrowType: 2, Level: 1),
                CancellationToken.None)
            .GetAwaiter().GetResult();
        check(created.Created && created.Character is not null,
            "a character can be created against a fresh database");

        var stored = store.SaveCharacterProgressionAsync(
                "roundtrip-user",
                created.Character!.Id,
                7,
                1_234,
                12,
                CancellationToken.None)
            .GetAwaiter().GetResult();
        check(stored is { Level: 7, Experience: 1_234, Sp: 12 },
            "progression is applied in memory before it is persisted");

        // 换一个 store 实例重开同一个库：读路径必须完全来自库。
        var reopened = TestGameStore.Create(options);
        reopened.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
        var reloaded = (reopened.GetCharactersAsync("roundtrip-user", CancellationToken.None)
            .GetAwaiter().GetResult())
            .Single(character => character.Id == created.Character.Id);
        check(reloaded.Level == 7
                && reloaded.Experience == 1_234
                && reloaded.Sp == 12
                && reloaded.CharacterNo == created.Character.CharacterNo,
            "reopening the database restores the persisted character state");
    }

    private static bool Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (TException)
        {
            return true;
        }
    }

    private static IReadOnlyCollection<string> ReadTableNames(IGameDatabase database) =>
        database.Read(connection =>
        {
            var names = new List<string>();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT name
FROM sqlite_master
WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                names.Add(reader.GetString(0));
            return names;
        });

    private static (int BaselineId, int SchemaVersion) ReadMetadata(IGameDatabase database) =>
        database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT baseline_id, schema_version
FROM schema_metadata
WHERE singleton_id = 1;";
            using var reader = command.ExecuteReader();
            return reader.Read()
                ? (reader.GetInt32(0), reader.GetInt32(1))
                : (0, 0);
        });

    private static long ReadInt64(IGameDatabase database, string sql) =>
        database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar());
        });

    private static string ReadString(IGameDatabase database, string sql) =>
        database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
        });
}