using DFLegacy.Server.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace DFLegacy.Server.SelfTests.Sqlite;

/// <summary>
/// 自检侧的游戏状态存取装配（docs/design/08-sqlite-storage.md M0/M1）。生产组合根在
/// Program.cs，本类只为测试提供两件事：按临时库路径造一个 <see cref="IGameStore"/>，
/// 以及在建库之后直接整库播种给定账号集合——后者替代了旧 JSON 夹具的写法。
///
/// <para>注意生产侧**不播种**：空库保持为空（§6）。因此凡是用例需要初始账号，
/// 都必须经 <see cref="Seed"/> 或 <see cref="SeedDefaultTestAccount"/> 显式声明，
/// 这与真实部署「先调 Admin API 建号」的顺序一致。</para>
/// </summary>
internal static class TestGameStore
{
    /// <summary>按配置的 DatabasePath 造 store；库不存在时按基线建空库。</summary>
    internal static IGameStore Create(
        ServerOptions options,
        CharacterExperienceCatalog? experienceCatalog = null,
        ItemCatalog? itemCatalog = null) =>
        new SqliteGameStore(
            NullLogger<GameStoreCore>.Instance,
            CreateDatabase(options),
            experienceCatalog,
            itemCatalog);

    /// <summary>
    /// 播种一个名为 test 的空账号（无角色）。预置角色由启动时的
    /// <c>InitializeTestAccountPresetsLocked</c> 经产品代码补齐——夹具只声明
    /// 「有这个账号」，不复制预置角色的构造逻辑。
    /// </summary>
    internal static void SeedDefaultTestAccount(ServerOptions options) =>
        Seed(
            options,
            [
                new AccountRecord(
                    Guid.NewGuid(),
                    "test",
                    "dfls-local:" + Convert.ToHexString(
                        System.Security.Cryptography.SHA256.HashData(
                            System.Text.Encoding.UTF8.GetBytes("test"))).ToLowerInvariant(),
                    [])
            ]);

    /// <summary>
    /// 建库并整库播种给定账号（相当于旧 JSON 夹具）。必须在 <see cref="Create"/>
    /// 之前调用：它负责创建库文件并清空既有内容。
    ///
    /// <para>播种会补齐存储层必须显式持有的三个 id/槽位字段：账号 uid、角色
    /// CharacterNo、角色 Slot。SQLite 侧它们是主键与唯一键的一部分，不能像旧 JSON
    /// 夹具那样留 0 交给启动迁移分配——播种发生在迁移之前，留 0 会让第二个角色覆盖
    /// 第一个（同名主键）。此处的分配规则与
    /// <c>GameStoreCore.MigrateIdentityLocked</c> 一致：账号 uid 按
    /// <see cref="AccountUidRules"/> 分段，CharacterNo 全局单调，Slot 取账号内序号。
    /// 夹具已显式给出的值原样保留。</para>
    /// </summary>
    internal static void Seed(
        ServerOptions options,
        IEnumerable<AccountRecord> accounts)
    {
        var normalized = NormalizeIds(accounts);
        var store = new SqliteStateStore(
            CreateDatabase(options),
            NullLogger.Instance);
        store.ReplaceAll(new SqliteStateSnapshot(
            normalized,
            NextCharacterNo: 1,
            NextNormalAccountUid: AccountUidRules.NormalUidStart,
            NextTestAccountUid: 1,
            FatigueDayKey: GameStoreCore.GetFatigueDayKey(DateTimeOffset.Now)));
    }

    private static List<AccountRecord> NormalizeIds(IEnumerable<AccountRecord> accounts)
    {
        var nextCharacterNo = 1u;
        var nextTestAccountUid = 1u;
        var nextNormalAccountUid = AccountUidRules.NormalUidStart;
        var result = new List<AccountRecord>();
        foreach (var account in accounts)
        {
            var accountUid = account.AccountUid;
            if (accountUid == AccountUidRules.InvalidUid)
            {
                // 与 GameStoreCore.AllocateAccountUidLocked 同规则：只有名为 test
                // 的账号走测试段，其余走普通段。走错段会让启动把预置角色塞进账号。
                accountUid = string.Equals(
                    account.UserName,
                    "test",
                    StringComparison.OrdinalIgnoreCase)
                    ? nextTestAccountUid++
                    : nextNormalAccountUid++;
            }
            else if (AccountUidRules.IsTest(accountUid))
            {
                nextTestAccountUid = Math.Max(nextTestAccountUid, accountUid + 1);
            }
            else if (AccountUidRules.IsNormal(accountUid))
            {
                nextNormalAccountUid = Math.Max(nextNormalAccountUid, accountUid + 1);
            }

            var characters = new List<CharacterRecord>(account.Characters.Count);
            for (var slot = 0; slot < account.Characters.Count; slot++)
            {
                var character = account.Characters[slot];
                var characterNo = character.CharacterNo;
                if (characterNo == 0)
                {
                    characterNo = nextCharacterNo;
                }

                nextCharacterNo = Math.Max(nextCharacterNo, characterNo + 1);
                characters.Add(character with
                {
                    CharacterNo = characterNo,
                    AccountUid = accountUid,
                    Slot = checked((byte)slot)
                });
            }

            result.Add(account with
            {
                AccountUid = accountUid,
                Characters = characters
            });
        }

        return result;
    }

    /// <summary>只建库不播种：用于验证空库播种路径。</summary>
    internal static IGameDatabase CreateDatabase(ServerOptions options) =>
        new GameDatabase(
            SqlitePaths.ResolveDatabasePath(options),
            SqlitePaths.BaselineSchemaPath,
            NullLogger.Instance);
}