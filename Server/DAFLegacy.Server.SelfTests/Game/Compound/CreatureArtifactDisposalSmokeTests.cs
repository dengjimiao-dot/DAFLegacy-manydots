using DAFLegacy.Protocol;
using DAFLegacy.Server;
using DAFLegacy.Server.SelfTests.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

internal static class CreatureArtifactDisposalSmokeTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), $"daflegacy-artifact-disposal-{Guid.NewGuid():N}");
        try
        {
            CreateFixture(root);
            var scripts = new ScriptFileSystem(
                new ServerOptions { ScriptPvfPath = string.Empty, SkillScriptPath = root },
                NullLogger<ScriptFileSystem>.Instance);
            var items = new ItemCatalog(scripts, NullLogger<ItemCatalog>.Instance);
            var disjoint = new DisjointCatalog(scripts, NullLogger<DisjointCatalog>.Instance);
            items.Initialize();
            disjoint.Initialize();
            var main = new Dictionary<ushort, CharacterItemRecord>
            {
                [9] = new(9, 1_000, 1, InstanceId: Guid.NewGuid()),
                [73] = new(73, 3_037, 10)
            };
            var creatures = new Dictionary<ushort, CharacterItemRecord>
            {
                [0] = new(0, 63_500, 123, InstanceId: Guid.NewGuid()),
                [140] = new(140, 63_501, 1, State: 5, InstanceId: Guid.NewGuid()),
                [141] = new(141, 63_601, 1, InstanceId: Guid.NewGuid()),
                [188] = new(188, 63_701, 1, InstanceId: Guid.NewGuid()),
                [239] = new(239, 63_501, 1, InstanceId: Guid.NewGuid())
            };
            var snapshot = creatures.Values.ToArray();
            var entries = new ItemDeletionRequestEntry[]
            {
                new(0, 140, 63_501, 1), new(0, 141, 63_601, 1), new(0, 188, 63_701, 1)
            };
            check(InventoryParsers.TryReadConsumeItems(
                    Convert.FromHexString("5700070100008C000DF8010000006195BD7D"),
                    out var wireListType, out var wireEntries)
                    && wireListType == 7
                    && wireEntries.Single() == entries[0],
                "DF2008 CMD18 Creature artifact wire vector preserves list type, bag slot, item ID and count");
            check(ItemDeletionPlanner.TryCreate(creatures, 7, entries, items, out var deletion)
                    && deletion.Inventory.Count == 2 && deletion.Inventory[0] == creatures[0]
                    && deletion.Inventory[239] == creatures[239]
                    && creatures.Values.SequenceEqual(snapshot),
                "CMD18 deletes red, blue and green bag artifacts without consuming the creature or worn artifact, or mutating input");
            check(deletion is not null
                    && InventoryPacketBuilders.CreateDeleteItemReply(7, deletion.CompletionEntries).Payload.SequenceEqual(
                        new byte[] { 1, 7, 3, 140, 0, 1, 0, 0, 0, 141, 0, 1, 0, 0, 0, 188, 0, 1, 0, 0, 0 })
                    && InventoryPacketBuilders.CreateDeleteItemError(7, 0x11).Payload.SequenceEqual(
                        new byte[] { 0, 0x11, 7 }),
                "CMD18 success and failure echo Creature list seven so DF2008 can release its pending inventory operation");
            check(!ItemDeletionPlanner.TryCreate(creatures, 7, [entries[0], entries[0]], items, out _)
                    && !ItemDeletionPlanner.TryCreate(creatures, 7, [new(0, 140, 63_601, 1)], items, out _)
                    && !ItemDeletionPlanner.TryCreate(creatures, 7, [new(0, 239, 63_501, 1)], items, out _)
                    && !ItemDeletionPlanner.TryCreate(creatures, 7, [new(0, 0, 63_500, 1)], items, out _)
                    && !ItemDeletionPlanner.TryCreate(creatures, 7, [new(0, 140, 63_501, 0)], items, out _)
                    && !ItemDeletionPlanner.TryCreate(creatures, 1, [entries[0]], items, out _)
                    && creatures.Values.SequenceEqual(snapshot),
                "artifact deletion rejects duplicate units, wrong item IDs, worn slots, creature bodies and unsupported spaces atomically");
            check(ItemDeletionPlanner.TryCreate(main, 0,
                        [new(2, 73, 3_037, 2), new(2, 73, 3_037, 3)], items, out var materials)
                    && materials.Inventory[73].CountOrValue == 5 && main[73].CountOrValue == 10
                    && !ItemDeletionPlanner.TryCreate(main, 0, [new(2, 73, 3_037, 11)], items, out _)
                    && !ItemDeletionPlanner.TryCreate(main, 0, [new(0, 9, 1_000, 1)], items, out _),
                "existing main-inventory material consumption still aggregates counts and rejects overdraw or ordinary equipment");

            var allArtifactResultsValid = true;
            foreach (var entry in entries)
            {
                if (!DisjointItemPlanner.TryCreate(main, creatures, entry.Slot, 7, uint.MaxValue,
                        0, 500, items, disjoint, out var result, out var failure))
                {
                    allArtifactResultsValid = false;
                    continue;
                }

                var reply = CraftPacketBuilders.CreateDisjointItemReply(entry.Slot, 7, result.ResponseRewards);
                allArtifactResultsValid &= failure == DisjointItemFailure.None
                    && !result.CreatureInventory.ContainsKey(entry.Slot)
                    && result.CreatureInventory.Count == creatures.Count - 1
                    && result.CreatureInventory[239] == creatures[239]
                    && result.MainInventory[9] == main[9]
                    && result.MainInventory[73].CountOrValue > main[73].CountOrValue
                    && reply.ProtocolId == 28 && reply.Payload[3] == 7
                    && creatures.Values.SequenceEqual(snapshot);
            }
            check(allArtifactResultsValid,
                "CMD28 disjoints all three artifact colors from list seven and adds PVF-generated materials to the main bag in one plan");
            check(!DisjointItemPlanner.TryCreate(main, creatures, 239, 7, uint.MaxValue,
                        0, 500, items, disjoint, out _, out _)
                    && !DisjointItemPlanner.TryCreate(main, creatures, 0, 7, uint.MaxValue,
                        0, 500, items, disjoint, out _, out _)
                    && !DisjointItemPlanner.TryCreate(main, creatures, 140, 7, 0,
                        0, 500, items, disjoint, out _, out var fullFailure)
                    && fullFailure == DisjointItemFailure.InventoryFull
                    && DisjointItemPlanner.TryCreate(main, creatures, 9, 0, uint.MaxValue,
                        0, 500, items, disjoint, out var ordinary, out _)
                    && ordinary.CreatureInventory.Values.SequenceEqual(snapshot)
                    && creatures.Values.SequenceEqual(snapshot) && main[73].CountOrValue == 10,
                "failed artifact disjoint and ordinary equipment disjoint never consume worn artifacts or other Creature inventory");
            var fullMain = Enumerable.Range(CharacterInventoryLayout.MaterialSlotStart,
                    CharacterInventoryLayout.MaterialSlotEnd - CharacterInventoryLayout.MaterialSlotStart)
                .Concat(Enumerable.Range(CharacterInventoryLayout.QuickSlotStart,
                    CharacterInventoryLayout.QuickSlotEnd - CharacterInventoryLayout.QuickSlotStart))
                .ToDictionary(slot => (ushort)slot, slot => new CharacterItemRecord((ushort)slot, 3_037, 1_000));
            check(!DisjointItemPlanner.TryCreate(fullMain, creatures, 140, 7, uint.MaxValue,
                        0, 500, items, disjoint, out _, out var fullBagFailure)
                    && fullBagFailure == DisjointItemFailure.InventoryFull
                    && fullMain.Values.All(item => item.CountOrValue == 1_000)
                    && creatures.Values.SequenceEqual(snapshot),
                "full material and quick-slot bags reject artifact disjoint without losing the artifact or partially granting rewards");
            check(DungeonItemDropPlanner.TryPlan(creatures, 7, 140, 1, items, out var drop, out _)
                    && drop.DroppedItem.InstanceId == creatures[140].InstanceId
                    && drop.DroppedItem.State == 5,
                "existing CMD50 tradeable artifact ground-drop support retains its UUID and metadata");

            await CheckPersistenceAsync(check, root, main, creatures, items, disjoint);
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
        Dictionary<ushort, CharacterItemRecord> main, Dictionary<ushort, CharacterItemRecord> creatures,
        ItemCatalog items, DisjointCatalog disjoint)
    {
        var options = new ServerOptions { DatabasePath = Path.Combine(root, "state.db") };
        TestGameStore.SeedDefaultTestAccount(options);
        var store = TestGameStore.Create(options);
        await store.InitializeAsync();
        var created = await store.CreateCharacterAsync("test",
            new CreateCharacterRequest("ArtifactTest", 0));
        var character = created.Character!;
        _ = DisjointItemPlanner.TryCreate(main, creatures, 140, 7, uint.MaxValue,
            0, 500, items, disjoint, out var plan, out _);
        var saved = await store.SaveCharacterItemSpacesAsync("test", character.Id, character.Gold,
            plan.MainInventory.Values, [], [], creatureInventory: plan.CreatureInventory.Values);
        var reloadedStore = TestGameStore.Create(options);
        await reloadedStore.InitializeAsync();
        var reloaded = (await reloadedStore.GetCharactersAsync("test"))
            .Single(candidate => candidate.Id == character.Id);
        check(saved is not null
                && reloaded.Inventory!.SequenceEqual(plan.MainInventory.Values.OrderBy(item => item.Slot))
                && reloaded.CreatureInventory!.SequenceEqual(plan.CreatureInventory.Values.OrderBy(item => item.Slot)),
            "artifact disjoint persists source removal and generated materials together across server-data reload");
        var inventory = reloaded.CreatureInventory!.ToDictionary(item => item.Slot);
        _ = ItemDeletionPlanner.TryCreate(inventory, 7, [new(0, 141, 63_601, 1)], items, out var deletion);
        _ = await reloadedStore.SaveCharacterItemSpacesAsync("test", character.Id, character.Gold,
            reloaded.Inventory!, [], [], creatureInventory: deletion.Inventory.Values);
        var finalStore = TestGameStore.Create(options);
        await finalStore.InitializeAsync();
        var final = (await finalStore.GetCharactersAsync("test"))
            .Single(candidate => candidate.Id == character.Id);
        var finalCreatures = final.CreatureInventory ?? [];
        check(finalCreatures.All(item => item.Slot is not (140 or 141))
                && finalCreatures.Single(item => item.Slot == 239) == creatures[239]
                && !ItemDeletionPlanner.TryCreate(finalCreatures.ToDictionary(item => item.Slot),
                    7, [new(0, 141, 63_601, 1)], items, out _),
            "artifact deletion persists while preserving other UUIDs and rejects a repeated deletion of the empty source slot");
    }

    private static void CreateFixture(string root)
    {
        foreach (var directory in new[] { "equipment", "stackable", "etc" })
        {
            Directory.CreateDirectory(Path.Combine(root, directory));
        }
        File.WriteAllText(Path.Combine(root, "equipment", "equipment.lst"),
            "1000 `ordinary.equ`\n63500 `creature.equ`\n63501 `red.equ`\n63601 `blue.equ`\n63701 `green.equ`\n");
        foreach (var (file, type, attach) in new[]
                 { ("ordinary", "coat", "free"), ("red", "artifact red", "free"),
                   ("blue", "artifact blue", "sealing"), ("green", "artifact green", "trade"),
                   ("creature", "creature", "trade") })
        {
            File.WriteAllText(Path.Combine(root, "equipment", $"{file}.equ"),
                $"[name] `{file}`\n[equipment type] `[{type}]`\n[attach type] `[{attach}]`\n"
                + "[grade] 5\n[rarity] 1\n[price] 2500\n[weight] 50\n");
        }
        File.WriteAllText(Path.Combine(root, "stackable", "stackable.lst"), "3037 `cube.stk`\n");
        File.WriteAllText(Path.Combine(root, "stackable", "cube.stk"),
            "[name] `cube`\n[stackable type] `[material]`\n[stack limit] 1000\n[weight] 1\n");
        File.WriteAllText(Path.Combine(root, "etc", "disjoint.etc"),
            "[cube index]\n`[no element]`\n3037\n[/cube index]\n"
            + "[cube creation const] 150 1.0 1.2\n[additional result]\n0\n0\n"
            + "[additional result const]\n0.0\n0.0\n");
    }
}
