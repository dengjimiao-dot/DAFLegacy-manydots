using Microsoft.Data.Sqlite;

namespace DFLegacy.Server.Sqlite;

/// <summary>
/// 基线 + 增量两段式迁移器（docs/design/08-sqlite-storage.md D3）。
///
/// <para>基线永远是当前全量 schema（docs/sql/act1_schema.sql，60A1 基线）：新库零迁移
/// 直建，建库事务内写入 <c>schema_metadata</c> 行（baseline_id 与 schema_version 同为
/// <see cref="BaselineVersion"/>）并随基线 SQL 提交 <c>PRAGMA user_version</c>。</para>
///
/// <para>此后新增功能只在 <see cref="Steps"/> 末尾追加，版本从
/// <see cref="BaselineVersion"/> + 1 起严格连续；每步一个事务，事务内依次执行
/// 步 SQL → 推进 schema_metadata → 推进 user_version，两者永不漂移。步内只准写
/// SQL，禁止调用业务 Service 或读 PVF（那属于导入器/启动流程的职责）。</para>
///
/// <para>启动互验两条不变量：<c>baseline_id == 100</c>（数值，恒不变化，其它值即
/// 异构库，拒绝启动并提示备份移走）与 <c>PRAGMA user_version ==
/// schema_metadata.schema_version</c>。任一不成立都拒绝启动，不在服务启动路径中
/// 猜测性转换。</para>
/// </summary>
internal static class SqliteMigrations
{
    /// <summary>60A1 基线标识，恒定值；异构库检测的唯一依据。</summary>
    internal const int BaselineId = 100;

    /// <summary>基线建库时的 schema_version 与 user_version 初值。</summary>
    internal const int BaselineVersion = 100;

    private static readonly IReadOnlyList<MigrationStep> Steps =
    [
        // 后续新增功能从 101 开始追加，例：
        // new MigrationStep(101, "add_some_column", ApplyAddSomeColumn),
    ];

    internal static int CurrentVersion =>
        Steps.Count == 0 ? BaselineVersion : Steps[Steps.Count - 1].Version;

    /// <summary>基线建库后写入/对齐版本元数据，与基线 SQL 的 user_version 同步。</summary>
    internal static void MarkCurrent(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
INSERT INTO schema_metadata (
    singleton_id, baseline_id, schema_version, created_at, updated_at
) VALUES (
    1, @baselineId, @schemaVersion, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
)
ON CONFLICT(singleton_id) DO UPDATE SET
    baseline_id = excluded.baseline_id,
    schema_version = excluded.schema_version,
    updated_at = CURRENT_TIMESTAMP;";
        command.Parameters.AddWithValue("@baselineId", BaselineId);
        command.Parameters.AddWithValue("@schemaVersion", CurrentVersion);
        command.ExecuteNonQuery();

        SetUserVersion(connection, transaction, CurrentVersion);
    }

    /// <summary>校验基线与双写版本，必要时逐步推进增量迁移。</summary>
    internal static void Apply(SqliteConnection connection, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var metadata = ReadMetadata(connection);
        if (metadata.BaselineId != BaselineId)
        {
            throw new InvalidOperationException(
                $"Not a DFLegacy database (baseline_id {BaselineId} required, " +
                $"found {DescribeBaseline(metadata.BaselineId)}). " +
                "Move it aside and restart to recreate.");
        }

        var version = ReadVersion(connection);
        if (version > CurrentVersion || metadata.SchemaVersion > CurrentVersion)
        {
            throw new InvalidOperationException(
                $"Schema v{Math.Max(version, metadata.SchemaVersion)} is newer than " +
                $"supported v{CurrentVersion}. Upgrade the server.");
        }

        if (version != metadata.SchemaVersion)
        {
            throw new InvalidOperationException(
                $"Schema version mismatch: user_version={version}, " +
                $"schema_version={metadata.SchemaVersion}.");
        }

        foreach (var step in Steps)
        {
            if (step.Version <= version)
                continue;
            if (step.Version != version + 1)
            {
                throw new InvalidOperationException(
                    $"Migration versions are not contiguous: current={version}, " +
                    $"next={step.Version}.");
            }

            using (var transaction = connection.BeginTransaction())
            {
                step.Apply(connection, transaction);
                WriteVersion(connection, transaction, step.Version);
                transaction.Commit();
            }

            version = step.Version;
            logger.LogInformation(
                "Applied migration v{Version} ({Name}).",
                step.Version,
                step.Name);
        }
    }

    internal static long ReadVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string DescribeBaseline(int baselineId) =>
        baselineId == 0 ? "none" : baselineId.ToString();

    private static (int BaselineId, int SchemaVersion) ReadMetadata(
        SqliteConnection connection)
    {
        using (var exists = connection.CreateCommand())
        {
            exists.CommandText = @"
SELECT COUNT(*)
FROM sqlite_master
WHERE type = 'table' AND name = 'schema_metadata';";
            if (Convert.ToInt32(exists.ExecuteScalar()) == 0)
                return (0, 0);
        }

        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT baseline_id, schema_version
FROM schema_metadata
WHERE singleton_id = 1;";
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return (0, 0);

        return (
            reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
            reader.IsDBNull(1) ? 0 : reader.GetInt32(1));
    }

    private static void SetUserVersion(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int version)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA user_version = {version};";
        command.ExecuteNonQuery();
    }

    private static void WriteVersion(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int version)
    {
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = @"
UPDATE schema_metadata
SET schema_version = @schemaVersion,
    updated_at = CURRENT_TIMESTAMP
WHERE singleton_id = 1 AND baseline_id = @baselineId;";
            command.Parameters.AddWithValue("@schemaVersion", version);
            command.Parameters.AddWithValue("@baselineId", BaselineId);
            if (command.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException(
                    "Schema metadata row missing; cannot record migration version.");
            }
        }

        SetUserVersion(connection, transaction, version);
    }

    private static void ExecuteSql(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static bool ColumnExists(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        string columnName)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static void AddColumnIfMissing(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        string columnName,
        string columnDefinition)
    {
        if (ColumnExists(connection, transaction, tableName, columnName))
            return;

        ExecuteSql(
            connection,
            transaction,
            $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};");
    }

    private sealed class MigrationStep(
        int version,
        string name,
        Action<SqliteConnection, SqliteTransaction> apply)
    {
        internal int Version { get; } = version;

        internal string Name { get; } = name;

        internal Action<SqliteConnection, SqliteTransaction> Apply { get; } = apply;
    }
}