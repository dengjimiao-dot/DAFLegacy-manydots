namespace DFLegacy.Server.Sqlite;

/// <summary>
/// SQLite 资产路径（docs/design/08-sqlite-storage.md D1）。库文件走 R5 锚点
/// （<c>--home</c> / DFLEGACY_HOME / BaseDirectory，相对路径以 HomeDirectory 为
/// 基准）；基线 schema 随发布落在输出目录的 <c>Sqlite/</c> 下，是全仓唯一的基线
/// 定义。基线只含 DDL 与静态种子，不含数据种子：账号与预置角色由启动流程播种。
/// </summary>
public static class SqlitePaths
{
    public const string BaselineSchemaFileName = "act1_schema.sql";

    private static string OutputDirectory => AppContext.BaseDirectory;

    public static string ResolveDatabasePath(ServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return PathResolver.Resolve(options.DatabasePath, options.HomeDirectory);
    }

    public static string BaselineSchemaPath =>
        Path.Combine(OutputDirectory, "Sqlite", BaselineSchemaFileName);
}