using Microsoft.Data.Sqlite;

namespace DFLegacy.Server.Sqlite;

/// <inheritdoc cref="IGameDatabase"/>
public sealed class GameDatabase : IGameDatabase
{
    private const int BusyTimeoutMilliseconds = 5000;

    public GameDatabase(string databasePath, string baselineSchemaPath, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("databasePath is empty", nameof(databasePath));
        if (string.IsNullOrWhiteSpace(baselineSchemaPath))
            throw new ArgumentException("baselineSchemaPath is empty", nameof(baselineSchemaPath));
        ArgumentNullException.ThrowIfNull(logger);

        DatabasePath = Path.GetFullPath(databasePath);
        BaselineSchemaPath = Path.GetFullPath(baselineSchemaPath);
        ConnectionString = SqliteDatabaseBootstrap.Initialize(
            DatabasePath,
            BaselineSchemaPath,
            logger);
    }

    private GameDatabase(
        string databasePath,
        string baselineSchemaPath,
        string connectionString)
    {
        DatabasePath = databasePath;
        BaselineSchemaPath = baselineSchemaPath;
        ConnectionString = connectionString;
    }

    public string DatabasePath { get; }

    public string BaselineSchemaPath { get; }

    public string ConnectionString { get; }

    /// <summary>
    /// 挂接到已初始化过的连接串（自检与多实现对跑用）：绕过本进程的初始化去重，
    /// 但仍要求调用方保证该库已经过 <see cref="SqliteDatabaseBootstrap"/>。
    /// </summary>
    public static GameDatabase AttachInitialized(
        string connectionString,
        string baselineSchemaPath)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("connectionString is empty", nameof(connectionString));

        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.DataSource))
            throw new ArgumentException("connectionString has no data source", nameof(connectionString));

        var databasePath = builder.DataSource == ":memory:"
            ? builder.DataSource
            : Path.GetFullPath(builder.DataSource);
        return new GameDatabase(
            databasePath,
            string.IsNullOrWhiteSpace(baselineSchemaPath)
                ? string.Empty
                : Path.GetFullPath(baselineSchemaPath),
            connectionString);
    }

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"PRAGMA busy_timeout={BusyTimeoutMilliseconds};";
            command.ExecuteNonQuery();
        }

        return connection;
    }

    public T Read<T>(Func<SqliteConnection, T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        using var connection = OpenConnection();
        return action(connection);
    }

    public T Write<T>(
        Func<SqliteConnection, SqliteTransaction, T> action,
        bool immediate = true)
    {
        ArgumentNullException.ThrowIfNull(action);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: !immediate);
        var result = action(connection, transaction);
        transaction.Commit();
        return result;
    }

    public void Write(
        Action<SqliteConnection, SqliteTransaction> action,
        bool immediate = true)
    {
        ArgumentNullException.ThrowIfNull(action);
        Write(
            (connection, transaction) =>
            {
                action(connection, transaction);
                return true;
            },
            immediate);
    }
}