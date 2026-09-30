namespace DAFLegacy.Server.Sqlite;

/// <summary>
/// SQLite 落盘实现（docs/design/08-sqlite-storage.md D4）。领域逻辑全部来自
/// <see cref="GameStoreCore"/>，本类只做两件事：
///
/// <list type="number">
/// <item>库文件不存在时由 <see cref="SqliteDatabaseBootstrap"/> 按基线
/// <c>act1_schema.sql</c> 建库；已存在时直接使用，不重建，也不做任何数据搬运。</item>
/// <item>启动时整库读入内存 record 树，并记录指纹基线。</item>
/// </list>
///
/// 每次保存按 diff 在单个 IMMEDIATE 事务内增量落盘。**空库保持为空**：不播种
/// test/test 账号，也不迁移任何既有档——账号与角色一律经 Admin API 或客户端流程
/// 显式创建。
/// </summary>
public sealed class SqliteGameStore(
    ILogger<GameStoreCore> logger,
    IGameDatabase database,
    CharacterExperienceCatalog? experienceCatalog = null,
    ItemCatalog? itemCatalog = null)
    : GameStoreCore(logger, experienceCatalog, itemCatalog)
{
    private readonly SqliteStateStore _store = new(database, logger);

    /// <summary>库文件路径；启动日志与运维提示用。</summary>
    public string DatabasePath => _store.DatabasePath;

    /// <summary>按配置解析库路径与基线 schema 路径；基线缺失时不建库。</summary>
    public static IGameDatabase CreateDatabase(ServerOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        var baselineSchemaPath = SqlitePaths.BaselineSchemaPath;
        if (!File.Exists(baselineSchemaPath))
        {
            throw new FileNotFoundException(
                $"SQLite baseline schema not found: {baselineSchemaPath}");
        }

        return new GameDatabase(
            SqlitePaths.ResolveDatabasePath(options),
            baselineSchemaPath,
            logger);
    }

    protected override Task<bool> TryLoadStateLockedAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = _store.Load();
        Accounts = snapshot.Accounts;
        NextCharacterNo = snapshot.NextCharacterNo;
        NextNormalAccountUid = snapshot.NextNormalAccountUid;
        NextTestAccountUid = snapshot.NextTestAccountUid;
        FatigueDayKey = snapshot.FatigueDayKey;
        Logger.LogInformation(
            "Loaded {AccountCount} account(s) from SQLite: {DatabasePath}",
            snapshot.Accounts.Count,
            DatabasePath);
        return Task.FromResult(true);
    }

    protected override Task PersistStateLockedAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _store.Apply(new SqliteStateSnapshot(
            Accounts,
            NextCharacterNo,
            NextNormalAccountUid,
            NextTestAccountUid,
            FatigueDayKey));
        return Task.CompletedTask;
    }
}