using System.Buffers.Binary;
using DAFLegacy.Protocol;
using DAFLegacy.Server;
using DAFLegacy.Server.SelfTests.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

internal static class RevivalCoinVoucherSmokeTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), $"daflegacy-coin-voucher-{Guid.NewGuid():N}");
        try
        {
            var scripts = CreateFixture(root);
            var items = new ItemCatalog(scripts, NullLogger<ItemCatalog>.Instance);
            items.Initialize();
            CheckCatalog(check, items);

            var options = new ServerOptions { DatabasePath = Path.Combine(root, "state.db") };
            TestGameStore.SeedDefaultTestAccount(options);
            var store = TestGameStore.Create(options);
            await store.InitializeAsync();
            var created = await store.CreateCharacterAsync("test", new CreateCharacterRequest("CoinVoucher", 2));
            var character = created.Character!;
            var inventory = new[]
            {
                new CharacterItemRecord(1, 1, 160), new CharacterItemRecord(64, 7180, 2),
                new CharacterItemRecord(65, 7181, 2), new CharacterItemRecord(66, 42, 2)
            };
            _ = await store.SaveCharacterInventoryAsync("test", character.Id, character.Gold, inventory, []);
            var one = items.Definitions[7180].IncreaseStatus!;
            var first = await store.UseIncreaseStatusItemAsync("test", character.Id, 64, 7180, one);
            check(first.Success && first.RemainingItemCount == 1
                    && Coins(first.Character) == 161 && Count(first.Character, 64) == 1
                    && first.Character!.Sp == character.Sp && first.Character.Experience == character.Experience,
                "CMD32 7180 consumes one voucher and adds exactly one reserved-slot revival coin without changing SP or experience");
            var last = await store.UseIncreaseStatusItemAsync("test", character.Id, 64, 7180, one);
            var duplicate = await store.UseIncreaseStatusItemAsync("test", character.Id, 64, 7180, one);
            check(last.Success && last.RemainingItemCount == 0 && Coins(last.Character) == 162
                    && Count(last.Character, 64) == 0 && !duplicate.Success
                    && duplicate.Failure == IncreaseStatusItemUseFailure.ItemStateChanged,
                "the last voucher removes its source stack and replaying an empty slot cannot award more coins");
            var five = await store.UseIncreaseStatusItemAsync("test", character.Id, 65, 7181,
                items.Definitions[7181].IncreaseStatus!);
            var general = await store.UseIncreaseStatusItemAsync("test", character.Id, 66, 42,
                items.Definitions[42].IncreaseStatus!);
            check(five.Success && five.RemainingItemCount == 1 && Coins(five.Character) == 167
                    && general.Success && general.RemainingItemCount == 1 && Coins(general.Character) == 168,
                "native sibling vouchers 7181 and 42 use the same transaction for five and one revival coins");

            var reloadedStore = TestGameStore.Create(options);
            await reloadedStore.InitializeAsync();
            var reloaded = (await reloadedStore.GetCharactersAsync("test"))
                .Single(candidate => candidate.Id == character.Id);
            check(Coins(reloaded) == 168 && Count(reloaded, 64) == 0
                    && Count(reloaded, 65) == 1 && Count(reloaded, 66) == 1,
                "coin credits and voucher decrements persist together across store reload");

            var full = Enumerable.Range(CharacterInventoryLayout.QuickSlotStart,
                    CharacterInventoryLayout.QuestSlotEnd - CharacterInventoryLayout.QuickSlotStart)
                .Select(slot => new CharacterItemRecord((ushort)slot, 8, 100)).ToDictionary(item => item.Slot);
            full[3] = new(3, 7180, 2);
            _ = await store.SaveCharacterInventoryAsync("test", character.Id, character.Gold, full.Values, []);
            var fullUse = await store.UseIncreaseStatusItemAsync("test", character.Id, 3, 7180, one);
            check(fullUse.Success && Coins(fullUse.Character) == 1 && Count(fullUse.Character, 3) == 1
                    && fullUse.Character!.Inventory!.Count == full.Count + 1,
                "full normal and quick-slot bags can use a voucher; its credit creates only the reserved coin slot");

            var overflow = new[] { new CharacterItemRecord(1, 1, uint.MaxValue), new CharacterItemRecord(64, 7180, 1) };
            _ = await store.SaveCharacterInventoryAsync("test", character.Id, character.Gold, overflow, []);
            var blocked = await store.UseIncreaseStatusItemAsync("test", character.Id, 64, 7180, one);
            var wrongId = await store.UseIncreaseStatusItemAsync("test", character.Id, 64, 7181, one);
            var balanceUse = await store.UseIncreaseStatusItemAsync("test", character.Id, 1, 1, one);
            var unchanged = (await store.GetCharactersAsync("test")).Single(candidate => candidate.Id == character.Id);
            check(!blocked.Success && blocked.Failure == IncreaseStatusItemUseFailure.BalanceOverflow
                    && !wrongId.Success && !balanceUse.Success
                    && Coins(unchanged) == uint.MaxValue && Count(unchanged, 64) == 1,
                "overflow, mismatched item IDs and using the balance as a voucher reject without consuming or awarding anything");
            _ = await store.SaveCharacterInventoryAsync("test", character.Id, character.Gold,
                [new CharacterItemRecord(1, 8, 1), new CharacterItemRecord(64, 7180, 1)], []);
            var occupied = await store.UseIncreaseStatusItemAsync("test", character.Id, 64, 7180, one);
            check(!occupied.Success && occupied.Failure == IncreaseStatusItemUseFailure.ItemStateChanged,
                "a noncoin item occupying the reserved balance slot is not silently overwritten by voucher use");

            var reply = SkillPacketBuilders.CreateIncreaseStatusReply(64, (byte)one.Type, one.Amount);
            check((byte)one.Type == 13 && reply.ProtocolId == 32 && reply.Payload[3] != 11
                    && reply.Payload.SequenceEqual(new byte[] { 1, 64, 0, 13, 1, 0, 0, 0, 0, 0, 0, 0 }),
                "coin CMD32 uses native type 13 with common consumption and raw counts, avoiding DF2008 type 11's guild-score string 3034");
            var balanceUpdate = InventoryPacketBuilders.CreateUpdateItemList(0, [new GameInventoryEntry(1, 1, 161)]);
            check(balanceUpdate.ProtocolId == 14 && balanceUpdate.Payload.Length == 15
                    && BinaryPrimitives.ReadUInt16LittleEndian(balanceUpdate.Payload.AsSpan(3)) == 1
                    && BinaryPrimitives.ReadUInt16LittleEndian(balanceUpdate.Payload.AsSpan(5)) == 1
                    && BinaryPrimitives.ReadUInt32LittleEndian(balanceUpdate.Payload.AsSpan(7)) == 161,
                "the follow-up NOTI14 synchronizes the absolute revival coin balance in main-list slot one");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    public static void CheckCatalog(Action<bool, string> check, ItemCatalog items)
    {
        foreach (var (id, count) in new[] { ((ushort)7180, 1), ((ushort)7181, 5), ((ushort)42, 1) })
        {
            check(items.TryGetDefinition(id, out var item) && !item.IsEquipment
                    && item.IncreaseStatus == new IncreaseStatusEffect(IncreaseStatusType.RevivalCoins, count),
                $"coin voucher {id} caches its native increase_status effect of {count} coins");
        }
    }

    private static uint Coins(CharacterRecord? character) =>
        character?.Inventory?.Where(item => item.ItemId == CharacterInventoryLayout.RevivalCoinItemId)
            .Aggregate(0u, (total, item) => checked(total + item.CountOrValue)) ?? 0;

    private static uint Count(CharacterRecord? character, ushort slot) =>
        character?.Inventory?.FirstOrDefault(item => item.Slot == slot)?.CountOrValue ?? 0;

    private static ScriptFileSystem CreateFixture(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "stackable/event/newyear"));
        Directory.CreateDirectory(Path.Combine(root, "stackable/cash"));
        File.WriteAllText(Path.Combine(root, "stackable/stackable.lst"),
            "1 `coin.stk`\n8 `potion.stk`\n42 `cash/coin_general.stk`\n"
            + "7180 `event/newyear/coin_newyear.stk`\n7181 `event/newyear/coin_book.stk`\n");
        foreach (var file in new[] { "coin", "potion", "cash/coin_general", "event/newyear/coin_newyear", "event/newyear/coin_book" })
        {
            File.WriteAllText(Path.Combine(root, $"stackable/{file}.stk"),
                "[stackable type] `[waste]` 1\n[stack limit] 1000\n[weight] 1\n[attach type] `[trade]`\n");
        }

        return new ScriptFileSystem(new ServerOptions { ScriptPvfPath = "", SkillScriptPath = root },
            NullLogger<ScriptFileSystem>.Instance);
    }
}
