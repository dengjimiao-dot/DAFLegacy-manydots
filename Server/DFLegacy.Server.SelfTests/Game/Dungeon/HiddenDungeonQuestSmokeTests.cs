using System.Buffers.Binary;
using DFLegacy.Protocol;
using DFLegacy.Server;
using Microsoft.Extensions.Logging.Abstractions;

internal static class HiddenDungeonQuestSmokeTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), $"dflegacy-hidden-quests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "quest"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "quest", "questmappingtable.tbl"),
                "[quest mapping table]\n4 `->` 524\n116 `->` 672\n42 `->` 255\n43 `->` 4096\n[/quest mapping table]\n");
            var options = new ServerOptions
            {
                DataPath = Path.Combine(root, "state.json"),
                ScriptPvfPath = "",
                SkillScriptPath = root
            };
            var scripts = new ScriptFileSystem(options, NullLogger<ScriptFileSystem>.Instance);
            var quests = new QuestCatalog(scripts, NullLogger<QuestCatalog>.Instance);
            check(quests.CompletionMapping[116] == 672,
                "quest completion uses the PVF mapping index rather than the quest ID");
            var store = new JsonGameStore(options, NullLogger<JsonGameStore>.Instance);
            await store.InitializeAsync();
            var character = (await store.GetCharactersAsync("test"))[0];
            check(!HiddenDungeonUnlockCatalog.ResolveVisibleDungeonIds([9], character.UnlockedDungeonIds).Contains((ushort)9),
                "hidden dungeon is absent before its unlock quest is accepted");
            character = (await store.SaveCharacterQuestProgressAsync("test", character.Id,
                character.Inventory ?? [], [new CharacterQuestRecord(116, 1)], [],
                unlockedDungeonIds: [9]))!;
            check(character.Quests!.Single().Trigger == 1 && character.UnlockedDungeonIds!.Contains(9),
                "quest acceptance atomically saves progress and hidden dungeon access");
            character = (await store.UnlockNextDungeonDifficultyAsync("test", character.Id, 9, 0))!;
            character = (await store.CompleteCharacterQuestAsync("test", character.Id, 116,
                character.Level, character.Experience, character.Sp, character.Gold,
                character.Inventory ?? [], [], [116], unlockedDungeonIds: [9]))!;
            var reloaded = new JsonGameStore(options, NullLogger<JsonGameStore>.Instance);
            await reloaded.InitializeAsync();
            character = (await reloaded.GetCharactersAsync("test"))[0];
            check(character.Quests!.Count == 0 && character.CompletedQuestIds!.Contains(116)
                    && character.UnlockedDungeonIds!.Contains(9)
                    && DungeonDifficultyProgression.GetMaximumDifficulty(character.DungeonProgress, 9) == 1,
                "completed hidden quest remains visible with adventure difficulty after JSON reload");
            var request = new PacketFrame(GameProtocolEngine.CommandPacketType,
                GameProtocolEngine.SelectCharacterCommand, 0, [0, 0, 0]);
            var packet = GameProtocolEngine.CreateSelectCharacterReply(request,
                completedQuestIds: [116, 4, 42, 43, 116, 9999],
                questCompletionMapping: quests.CompletionMapping);
            var payload = packet.Payload;
            check(payload[36] == 3 && payload[37] == 0
                    && BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(38, 4)) == 32
                    && payload[42 + 31] == 0x80
                    && payload[74] == 1 && payload[79 + 1] == 0x10 && payload[79 + 20] == 1
                    && payload[111] == 8 && payload[116] == 1
                    && payload[148] == 1 && payload.Length == 153,
                "CMD4 completion groups encode sparse mapping bits, length prefixes and following town fields correctly");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
