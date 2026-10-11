using System.Buffers.Binary;
using DAFLegacy.Protocol;
using DAFLegacy.Server;
using DAFLegacy.Server.Sqlite;
using DAFLegacy.Server.SelfTests.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

internal static class DungeonRequiredItemSmokeTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), $"daflegacy-dungeon-admission-{Guid.NewGuid():N}");
        try
        {
            var catalog = CreateCatalog(root);
            check(catalog.TryGetDefinition(33, out var consumed)
                    && consumed.RequiredItemId == 4083 && consumed.RequiredItemCount == 1
                    && consumed.ConsumeRequiredItem,
                "DGN admission parses item ID, count and consumption flag, excluding comment numbers");
            check(catalog.TryGetDefinition(34, out var retained)
                    && retained.RequiredItemId == 4083 && retained.RequiredItemCount == 3
                    && !retained.ConsumeRequiredItem,
                "required item with flag zero remains an admission requirement, not a free-entry exemption");
            check(catalog.TryGetDefinition(35, out var multiple)
                    && multiple.RequiredItemCount == 3 && multiple.ConsumeRequiredItem,
                "required item parameters can start on the next line");
            foreach (var id in new ushort[] { 1, 36, 37, 38 })
            {
                check(catalog.TryGetDefinition(id, out var unrestricted)
                        && unrestricted.RequiredItemId == 0
                        && unrestricted.TryConsumeRequiredItem(
                            new Dictionary<ushort, CharacterItemRecord>(), out _),
                    $"dungeon {id} without an active admission requirement preserves free entry");
            }

            check(!catalog.TryGetDefinition(39, out _) && !catalog.TryGetDefinition(40, out _)
                    && !catalog.TryGetDefinition(41, out _) && !catalog.TryGetDefinition(42, out _)
                    && !catalog.TryGetDefinition(43, out _),
                "malformed admission declarations cannot silently turn a restricted dungeon into free entry");

            var inventory = new[]
            {
                new CharacterItemRecord(1, 4083, 1),
                new CharacterItemRecord(73, 4083, 4),
                new CharacterItemRecord(74, 4150, 2),
                new CharacterItemRecord(75, 3037, 7)
            }.ToDictionary(item => item.Slot);
            var snapshot = inventory.ToDictionary();
            check(consumed.TryConsumeRequiredItem(inventory, out var one)
                    && !one.ContainsKey(1) && one[73] == inventory[73]
                    && one[75] == inventory[75] && inventory.Values.SequenceEqual(snapshot.Values),
                "entry consumes exactly one configured item, including quick-slot stacks, without mutating live inventory");
            check(multiple.TryConsumeRequiredItem(inventory, out var several)
                    && !several.ContainsKey(1) && several[73].CountOrValue == 2
                    && several[75] == inventory[75],
                "admission count can span several bag stacks without removing unrelated items");
            check(retained.TryConsumeRequiredItem(inventory, out var unchanged)
                    && unchanged.Values.SequenceEqual(snapshot.Values),
                "nonconsuming admission validates the full count and preserves every item");
            var missing = new Dictionary<ushort, CharacterItemRecord>
            {
                [73] = new(73, 4083, 2), [74] = inventory[74]
            };
            check(!multiple.TryConsumeRequiredItem(missing, out var rejected)
                    && rejected.Values.SequenceEqual(missing.Values)
                    && !retained.TryConsumeRequiredItem(missing, out _)
                    && !consumed.TryConsumeRequiredItem(
                        new Dictionary<ushort, CharacterItemRecord> { [73] = new(73, 4083, 0) }, out _),
                "missing, insufficient and zero-count tickets reject admission without partial consumption, even with flag zero");
            var largeStacks = new Dictionary<ushort, CharacterItemRecord>
            {
                [73] = new(73, 4083, uint.MaxValue), [74] = new(74, 4083, uint.MaxValue)
            };
            check(multiple.TryConsumeRequiredItem(largeStacks, out var largeRemaining)
                    && largeRemaining[73].CountOrValue == uint.MaxValue - 3
                    && largeRemaining[74].CountOrValue == uint.MaxValue,
                "ticket totals do not overflow 32-bit stack counts");

            var hell = new WorldMapDefinition(3, true, [33, 35], [], [new(4150, 1)]);
            check(consumed.TryConsumeRequiredItem(inventory, out var admission)
                    && hell.TryConsumeHellItems(admission, out var combined)
                    && !combined.ContainsKey(1) && combined[74].CountOrValue == 1
                    && inventory.Values.SequenceEqual(snapshot.Values),
                "ordinary admission and hell items compose into one nonmutating inventory plan");
            var sharedTicketHell = hell with { HellItems = [new(4083, 3)] };
            check(multiple.TryConsumeRequiredItem(inventory, out admission)
                    && !sharedTicketHell.TryConsumeHellItems(admission, out _)
                    && inventory.Values.SequenceEqual(snapshot.Values),
                "overlapping normal and hell costs are checked on the remaining plan and cannot partially charge the live bag");

            var error = DungeonPacketBuilders.CreateSelectDungeonMissingRequiredItemReply();
            check(error.Type == GameProtocolConstants.CommandPacketType && error.ProtocolId == 16
                    && error.Payload.SequenceEqual(new byte[] { 0, 17, 0 })
                    && DungeonPacketBuilders.CreateSelectDungeonMissingRequiredItemReply(3).Payload
                        .SequenceEqual(new byte[] { 0, 17, 3 }),
                "DF2008 CMD16 missing admission reply includes error 17 and its one-byte party index for native string 2100");
            var removal = InventoryPacketBuilders.CreateUpdateItemList(0,
                [new GameInventoryEntry(1, ushort.MaxValue, 0)]);
            check(removal.Type == GameProtocolConstants.NotificationPacketType && removal.ProtocolId == 14
                    && removal.Payload.Length == 15 && removal.Payload[0] == 0
                    && BinaryPrimitives.ReadUInt16LittleEndian(removal.Payload.AsSpan(1)) == 1
                    && BinaryPrimitives.ReadUInt16LittleEndian(removal.Payload.AsSpan(5)) == ushort.MaxValue,
                "consumed admission slots use the existing DF2008 NOTI14 main-bag removal layout");

            await CheckPersistenceAsync(check, root, consumed, inventory, hell);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task CheckPersistenceAsync(Action<bool, string> check, string root,
        DungeonDefinition dungeon, Dictionary<ushort, CharacterItemRecord> inventory,
        WorldMapDefinition hell)
    {
        var options = new ServerOptions { DatabasePath = Path.Combine(root, "state.db") };
        TestGameStore.SeedDefaultTestAccount(options);
        var store = TestGameStore.Create(options);
        await store.InitializeAsync();
        var created = await store.CreateCharacterAsync("test", new CreateCharacterRequest("EntryTest", 0));
        var character = created.Character!;
        var equipment = new[] { new CharacterItemRecord(12, 26043, 1, InstanceId: Guid.NewGuid()) };
        _ = await store.SaveCharacterInventoryAsync("test", character.Id, character.Gold,
            inventory.Values, equipment);
        _ = dungeon.TryConsumeRequiredItem(inventory, out var admission);
        _ = hell.TryConsumeHellItems(admission, out var combined);
        var failed = await store.SaveCharacterInventoryAsync("test", Guid.Empty, character.Gold,
            combined.Values, equipment);
        var before = (await store.GetCharactersAsync("test")).Single(candidate => candidate.Id == character.Id);
        check(failed is null && before.Inventory!.SequenceEqual(inventory.Values.OrderBy(item => item.Slot)),
            "a failed admission save leaves persisted inventory unchanged");
        var saved = await store.SaveCharacterInventoryAsync("test", character.Id, character.Gold,
            combined.Values, equipment);
        var reloadedStore = TestGameStore.Create(options);
        await reloadedStore.InitializeAsync();
        var reloaded = (await reloadedStore.GetCharactersAsync("test"))
            .Single(candidate => candidate.Id == character.Id);
        check(saved is not null
                && reloaded.Inventory!.SequenceEqual(combined.Values.OrderBy(item => item.Slot))
                && reloaded.Equipment!.SequenceEqual(equipment)
                && reloaded.Gold == character.Gold && reloaded.UsedFatigue == character.UsedFatigue,
            "successful admission persists both item costs together across reload and preserves gold, fatigue and equipment");
        check(dungeon.TryConsumeRequiredItem(
                new Dictionary<ushort, CharacterItemRecord> { [1] = new(1, 4083, 1) }, out var last)
                && !dungeon.TryConsumeRequiredItem(last, out _),
            "after the last ticket is consumed a new run cannot enter again");
    }

    private static DungeonCatalog CreateCatalog(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "dungeon"));
        Directory.CreateDirectory(Path.Combine(root, "map"));
        var requirements = new Dictionary<ushort, string>
        {
            [1] = "", [33] = "[required item] 4083 1 1 // flag 0 means retain; 999 is a comment\n",
            [34] = "[required item] 4083 3 0\n", [35] = "[required item]\n4083 3 1\n",
            [36] = "[required item] -1 0 0\n", [37] = "[required item] 0 0 0\n",
            [38] = "[required item] 4083 0 1\n", [39] = "[required item] 4083 1 // missing flag 1\n",
            [40] = "[required item] 70000 1 1\n", [41] = "[required item] 4083 -1 1\n",
            [42] = "[required item] 4083 1 2\n", [43] = "[required item] 999999999999 1 1\n"
        };
        File.WriteAllText(Path.Combine(root, "dungeon/dungeon.lst"),
            string.Join('\n', requirements.Keys.Select(id => $"{id} `{id}.dgn`")));
        foreach (var (id, requirement) in requirements)
        {
            File.WriteAllText(Path.Combine(root, $"dungeon/{id}.dgn"), requirement
                + "[minimum required level] 1\n[basis level] 1\n[maze info]\n[size] 2 1\n"
                + "[greed]\n`BBEE`\n[start map]\n0 0\n[/start map]\n"
                + "[boss map]\n1 0\n[/boss map]\n");
        }

        File.WriteAllText(Path.Combine(root, "map/map.lst"), "");
        var options = new ServerOptions { ScriptPvfPath = "", SkillScriptPath = root };
        var scripts = new ScriptFileSystem(options, NullLogger<ScriptFileSystem>.Instance);
        return new DungeonCatalog(scripts,
            new ObjectDropGenerator(new ServerSettings(),
                new ObjectDropCatalog(scripts, NullLogger<ObjectDropCatalog>.Instance),
                new GoldDropCatalog(scripts, NullLogger<GoldDropCatalog>.Instance)),
            NullLogger<DungeonCatalog>.Instance);
    }
}
