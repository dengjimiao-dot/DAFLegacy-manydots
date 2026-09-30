using Microsoft.Data.Sqlite;

namespace DFLegacy.Server.Sqlite;

/// <summary>
/// 库访问入口（docs/design/08-sqlite-storage.md D1）。短连接模型：每次操作现开
/// 连接、结束即关；连接串带 ForeignKeys=true，每连接 PRAGMA busy_timeout=5000。
/// 事务边界由调用方以 <see cref="Write{T}"/> 表达，一个业务操作一个事务。
/// </summary>
public interface IGameDatabase
{
    string DatabasePath { get; }

    string BaselineSchemaPath { get; }

    string ConnectionString { get; }

    SqliteConnection OpenConnection();

    T Read<T>(Func<SqliteConnection, T> action);

    T Write<T>(
        Func<SqliteConnection, SqliteTransaction, T> action,
        bool immediate = true);

    void Write(
        Action<SqliteConnection, SqliteTransaction> action,
        bool immediate = true);
}