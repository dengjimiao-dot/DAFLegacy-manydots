using DAFLegacy.Server;
using DAFLegacy.Server.SelfTests.Sqlite;

/// <summary>
/// 社交存储自测（11 号 P2；docs/design/12 §3.2）：好友单向边的增删/幂等/反向边、
/// 黑名单角色级锚定、按名解析、账号 uid 解析与跨重启持久化（重开库后仍在）。
/// </summary>
internal static class SocialStoreSmokeTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "social-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var options = new ServerOptions { DatabasePath = Path.Combine(root, "state.db") };
        var store = TestGameStore.Create(options);
        await store.InitializeAsync();

        var account = await store.CreateAccountAsync(new CreateAccountRequest("social-test-acct", "password", OicqCode: "0000"));
        check(account.Created, $"the social test account is created (error={account.Error ?? "none"})");
        var hero = (await store.CreateCharacterAsync("social-test-acct", new CreateCharacterRequest("Hero", Job: 0, GrowType: 2, Level: 30)))
            .Character!;
        var pal = (await store.CreateCharacterAsync("social-test-acct", new CreateCharacterRequest("Pal", Job: 1, GrowType: 3, Level: 21)))
            .Character!;
        var stranger = (await store.CreateCharacterAsync("social-test-acct", new CreateCharacterRequest("Stranger", Job: 2, GrowType: 2, Level: 9)))
            .Character!;

        // 按名解析与账号 uid。
        var found = await store.FindCharacterByNameAsync("Pal");
        check(found?.Id == pal.Id, "FindCharacterByName resolves the exact character");
        check(await store.GetCharacterAccountUidAsync(pal.Id) == 0u + account.Account!.AccountUid,
            "the character resolves back to its account uid");
        check(await store.FindCharacterByNameAsync("Nobody") is null,
            "an unknown name resolves to nothing");

        // 好友：自加被拒；加陌生名被拒；正常添加后列表与反向边成立。
        var selfAdd = await store.AddFriendAsync(hero.Id, "Hero");
        check(!selfAdd.Ok, "befriending yourself is rejected");
        var ghostAdd = await store.AddFriendAsync(hero.Id, "Nobody");
        check(!ghostAdd.Ok, "befriending an unknown name is rejected");
        var add = await store.AddFriendAsync(hero.Id, "Pal");
        check(add.Ok, "adding an existing character as a one-way friend succeeds");
        await store.AddFriendAsync(hero.Id, "Pal");
        var heroFriends = await store.GetFriendsAsync(hero.Id);
        check(heroFriends.Length == 1
            && heroFriends[0].CharacterId == pal.Id
            && heroFriends[0].Name == "Pal"
            && heroFriends[0].Level == 21
            && heroFriends[0].Job == 1
            && heroFriends[0].Grow == 3,
            "the friend list stays deduplicated and carries the friend profile");
        check((await store.GetFriendOwnerIdsAsync(pal.Id)).SequenceEqual([hero.Id]),
            "the reverse edge lists Hero as the owner who added Pal");
        check((await store.GetFriendOwnerIdsAsync(hero.Id)).Length == 0,
            "Pal added nobody, so the reverse edge for Hero is empty");

        // 单向性：Pal 加 Hero 不影响 Hero 的列表 dedup。
        await store.AddFriendAsync(pal.Id, "Hero");
        check((await store.GetFriendsAsync(pal.Id)).Length == 1
            && (await store.GetFriendsAsync(hero.Id)).Length == 1,
            "the opposite-direction edge is stored independently");

        // 黑名单：加/幂等/列表/移除。
        var blacklistAdd = await store.AddBlacklistAsync(hero.Id, "Stranger");
        check(blacklistAdd.Ok, "adding a character to the blacklist succeeds");
        await store.AddBlacklistAsync(hero.Id, "Stranger");
        var blacklist = await store.GetBlacklistAsync(hero.Id);
        check(blacklist.Length == 1 && blacklist[0].TargetName == "Stranger" && blacklist[0].CreatedAtUnix > 0,
            "the blacklist stays deduplicated with a join timestamp");
        var removed = await store.RemoveBlacklistAsync(hero.Id, "Stranger");
        check(removed.Ok && (await store.GetBlacklistAsync(hero.Id)).Length == 0,
            "removing from the blacklist clears the row");

        // 删除好友（幂等）后列表收敛。
        check((await store.RemoveFriendAsync(hero.Id, "Pal")).Ok, "removing the friend succeeds");
        await store.RemoveFriendAsync(hero.Id, "Pal");
        check((await store.GetFriendsAsync(hero.Id)).Length == 0,
            "friend removal is idempotent");

        // 跨重启持久化：重开库后好友边与黑名单仍在。
        await store.AddFriendAsync(hero.Id, "Pal");
        await store.AddBlacklistAsync(hero.Id, "Stranger");
        var reloaded = TestGameStore.Create(options);
        await reloaded.InitializeAsync();
        var persistedFriends = await reloaded.GetFriendsAsync(hero.Id);
        var persistedBlacklist = await reloaded.GetBlacklistAsync(hero.Id);
        check(persistedFriends.Length == 1 && persistedFriends[0].Name == "Pal",
            "friend edges survive a store reopen");
        check(persistedBlacklist.Length == 1 && persistedBlacklist[0].TargetName == "Stranger",
            "blacklist rows survive a store reopen");
    }
}
