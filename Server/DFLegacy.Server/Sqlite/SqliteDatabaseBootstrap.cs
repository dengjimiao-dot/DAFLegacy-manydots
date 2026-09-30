using Microsoft.Data.Sqlite;

namespace DFLegacy.Server.Sqlite;

/// <summary>
/// 库初始化（docs/design/08-sqlite-storage.md D2）。每个库文件每进程只初始化一次
/// （进程内去重 + 路径键）：文件不存在时在单个事务内执行基线 DDL 并写入版本元数据；
/// 文件存在时只校验基线并推进增量迁移。没有正确 baseline_id 的历史库被拒绝启动。
///
/// <para>连接级 PRAGMA 一律不进基线 SQL：<c>journal_mode=WAL</c> 在活动事务内执行
/// 会直接报错，故在事务外设置；<c>foreign_keys</c> 经连接串每连接生效。基线 SQL 里
/// 唯一的 PRAGMA 是可随事务提交的 <c>user_version</c>。</para>
///
/// <para>新建库失败时删除 .db/.db-wal/.db-shm 三件套再抛出，绝不留下半初始化的库；
/// 最坏情况回到「无库 + 原始 state.json」，可重复导入（§8 K6）。</para>
/// </summary>
public static class SqliteDatabaseBootstrap
{
    private static readonly object InitLock = new();
    private static readonly HashSet<string> InitializedPaths =
        new(StringComparer.OrdinalIgnoreCase);

    public static string Initialize(
        string databasePath,
        string baselineSchemaPath,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var connectionString = BuildConnectionString(databasePath);
        var key = Path.GetFullPath(databasePath);

        lock (InitLock)
        {
            if (InitializedPaths.Contains(key))
                return connectionString;

            var databaseExists = File.Exists(key);
            EnsureDatabaseDirectory(key);

            try
            {
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    if (databaseExists)
                    {
                        ValidateAndUpgradeCurrentDatabase(connection, logger);
                    }
                    else
                    {
                        CreateCurrentDatabase(
                            connection,
                            File.ReadAllText(baselineSchemaPath));
                    }

                    // WAL 持久生效：读写不互锁，消除并发写者的 database is locked。
                    using var walCommand = connection.CreateCommand();
                    walCommand.CommandText = "PRAGMA journal_mode=WAL;";
                    walCommand.ExecuteScalar();
                }
            }
            catch
            {
                if (!databaseExists)
                    DeleteDatabaseFiles(key);
                throw;
            }

            InitializedPaths.Add(key);
            logger.LogInformation(
                "SQLite ready: {DatabasePath} (baseline {BaselineId}, schema v{SchemaVersion}).",
                key,
                SqliteMigrations.BaselineId,
                SqliteMigrations.CurrentVersion);
        }

        return connectionString;
    }

    /// <summary>供自检用：解除本进程对该路径的初始化去重，便于重复初始化断言。</summary>
    internal static void ForgetInitializedPath(string databasePath)
    {
        lock (InitLock)
        {
            InitializedPaths.Remove(Path.GetFullPath(databasePath));
        }
    }

    public static string BuildConnectionString(string databasePath) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            ForeignKeys = true,
            DefaultTimeout = 5
        }.ConnectionString;

    private static void CreateCurrentDatabase(
        SqliteConnection connection,
        string baselineSql)
    {
        using (var transaction = connection.BeginTransaction())
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = baselineSql;
                command.ExecuteNonQuery();
            }

            SqliteMigrations.MarkCurrent(connection, transaction);
            transaction.Commit();
        }
    }

    private static void ValidateAndUpgradeCurrentDatabase(
        SqliteConnection connection,
        ILogger logger) =>
        SqliteMigrations.Apply(connection, logger);

    private static void EnsureDatabaseDirectory(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
    }

    private static void DeleteDatabaseFiles(string databasePath)
    {
        DeleteIfExists(databasePath);
        DeleteIfExists(databasePath + "-wal");
        DeleteIfExists(databasePath + "-shm");
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 保留原始初始化异常；残留文件可由启动日志定位。
        }
    }
}