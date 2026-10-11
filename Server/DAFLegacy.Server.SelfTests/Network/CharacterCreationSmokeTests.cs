using DAFLegacy.Protocol;
using DAFLegacy.Server;
using DAFLegacy.Server.SelfTests.Sqlite;

internal static class CharacterCreationSmokeTests
{
    private static readonly PacketFrame Request = new(GameProtocolConstants.CommandPacketType,
        (byte)CmdPacketType.CREATE_CHARACTER, 0, [1, 0]);

    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), $"daflegacy-create-character-{Guid.NewGuid():N}");
        try
        {
            var options = new ServerOptions { DatabasePath = Path.Combine(root, "state.db") };
            var store = TestGameStore.Create(options);
            await store.InitializeAsync();
            var accountA = await store.CreateAccountAsync(
                new CreateAccountRequest("creation-a", "test-password", OicqCode: "10001"));
            var accountB = await store.CreateAccountAsync(
                new CreateAccountRequest("creation-b", "test-password", OicqCode: "10002"));
            var uidA = accountA.Account!.AccountUid;
            var uidB = accountB.Account!.AccountUid;

            foreach (var name in new[] { "游戏管理员", " 游戏管理员 ", "GM", "gm", "Gm", " gM " })
            {
                var game = await store.CreateCharacterAsync(uidA, NewRequest(name));
                var http = await store.CreateCharacterAsync("creation-a", NewRequest(name));
                check(IsFailure(game, CharacterCreationFailure.NameUnavailable, 0x12)
                        && IsFailure(http, CharacterCreationFailure.NameUnavailable, 0x12),
                    $"reserved character name '{name}' is rejected by UID and username creation paths with CMD5 error 0x12/string 683");
            }
            check((await store.GetCharactersAsync("creation-a")).Length == 0,
                "rejected reserved names do not allocate a visible character or consume a roster slot");

            foreach (var name in new[] { "GMPlayer", "游戏管理员1" })
            {
                var allowed = await store.CreateCharacterAsync(uidA, NewRequest(name));
                check(allowed.Created && allowed.Character!.Name == name && allowed.Character.Sp == 0,
                    "only the two reserved names are blocked, not other names sharing a prefix");
            }

            var created = await store.CreateCharacterAsync(uidA, NewRequest("  UniqueName  "));
            var repeated = await store.CreateCharacterAsync(uidB, NewRequest("uniquename"));
            check(created.Created && created.Failure == CharacterCreationFailure.None
                    && created.Character!.Name == "UniqueName"
                    && CharacterCreationReply.Create(Request, created).Payload.SequenceEqual(new byte[] { 1 })
                    && IsFailure(repeated, CharacterCreationFailure.NameUnavailable, 0x12),
                "successful creation uses CMD5 success/string 680 and global case-insensitive duplicates use error 0x12/string 683");

            var race = await Task.WhenAll(
                store.CreateCharacterAsync(uidA, NewRequest("CreationRace")),
                store.CreateCharacterAsync(uidB, NewRequest("creationrace")));
            check(race.Count(result => result.Created) == 1
                    && race.Count(result => IsFailure(result, CharacterCreationFailure.NameUnavailable, 0x12)) == 1,
                "concurrent creation across accounts keeps the name uniqueness check and insertion under the same store gate");

            var current = await store.GetCharactersAsync("creation-b");
            for (var index = current.Length; index < MaximumTransferRoster.MaximumVisibleCharacters; index++)
            {
                var filled = await store.CreateCharacterAsync(uidB, NewRequest($"RosterFill{index}"));
                check(filled.Created, "fill the configured character roster limit for the capacity rejection test");
            }
            var full = await store.CreateCharacterAsync(uidB, NewRequest("FullReject"));
            check(IsFailure(full, CharacterCreationFailure.RosterFull, 0x04)
                    && (await store.GetCharactersAsync("creation-b")).Length == MaximumTransferRoster.MaximumVisibleCharacters,
                "the configured roster limit rejects CMD5 with error 4/string 682 rather than a DB or name error");

            var invalidName = await store.CreateCharacterAsync(uidA, NewRequest("   "));
            var invalidLevel = await store.CreateCharacterAsync(uidA, NewRequest("BadLevel") with { Level = 0 });
            var missingAccount = await store.CreateCharacterAsync(uint.MaxValue, NewRequest("NoAccount"));
            check(IsFailure(invalidName, CharacterCreationFailure.InvalidName, 0x12)
                    && IsFailure(invalidLevel, CharacterCreationFailure.InvalidRequest, 0x13)
                    && IsFailure(missingAccount, CharacterCreationFailure.AccountNotFound, 0x13),
                "invalid name, unsupported request and missing account failures are not mislabeled as storage errors");

            var retried = await store.CreateCharacterAsync(uidA, NewRequest("SaveRetry"));
            check(retried.Created && retried.Character!.CharacterNo > created.Character!.CharacterNo,
                "after the earlier failures the same path can still create normally, without a ghost roster entry or ID rollback");

            var reload = TestGameStore.Create(options);
            await reload.InitializeAsync();
            var reloadedA = await reload.GetCharactersAsync("creation-a");
            check(reloadedA.Any(character => character.Id == retried.Character!.Id)
                    && reloadedA.All(character => character.Name is not ("GM" or "游戏管理员"))
                    && (await reload.GetCharactersAsync("creation-b")).Length == MaximumTransferRoster.MaximumVisibleCharacters,
                "allowed creations persist across reload while forbidden names and rejected extra slots never enter the database");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static CreateCharacterRequest NewRequest(string name) => new(name, Job: 2, GrowType: 0, Level: 1);

    private static bool IsFailure(CreateCharacterResult result, CharacterCreationFailure failure, byte errorCode)
    {
        var packet = CharacterCreationReply.Create(Request, result);
        return !result.Created && result.Character is null && result.Failure == failure
            && packet.Type == GameProtocolConstants.CommandPacketType && packet.ProtocolId == 5
            && packet.Payload.SequenceEqual(new byte[] { 0, errorCode });
    }
}
