using DAFLegacy.Protocol;
using DAFLegacy.Server;
using DAFLegacy.Server.SelfTests.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

internal static class FullBagConsumptionSmokeTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), $"daflegacy-full-bag-items-{Guid.NewGuid():N}");
        ItemCatalog items;
        try
        {
            items = CreateItemCatalog(root);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }

        var inventory = Enumerable.Range(CharacterInventoryLayout.QuickSlotStart,
                CharacterInventoryLayout.QuestSlotEnd - CharacterInventoryLayout.QuickSlotStart)
            .ToDictionary(slot => (ushort)slot, slot => new CharacterItemRecord((ushort)slot,
                slot < CharacterInventoryLayout.QuickSlotEnd ? (ushort)8
                    : slot < CharacterInventoryLayout.EquipmentSlotEnd ? (ushort)10_000
                    : slot < CharacterInventoryLayout.ConsumableSlotEnd ? (ushort)8
                    : slot < CharacterInventoryLayout.MaterialSlotEnd ? (ushort)3037 : (ushort)4000,
                slot is >= CharacterInventoryLayout.EquipmentSlotStart
                    and < CharacterInventoryLayout.EquipmentSlotEnd ? 1u : 100u));
        var snapshot = inventory.ToDictionary();
        var grounds = new DungeonGroundItemState();
        var ground = grounds.Add(new DungeonGeneratedDrop(false, 3176, 1), 1, 0, 0, 0);
        var request = new PacketFrame(GameProtocolConstants.CommandPacketType,
            (byte)CmdPacketType.GET_ITEM, 0, [1, 0, (byte)ground.GroundId, 0]);

        check(!DungeonPickupPlanner.TryPlanItem(inventory, ground, items, uint.MaxValue,
                    out _, out var failure)
                && failure == InventoryPlacementFailure.Full
                && CraftPacketBuilders.CreateGetItemRejection(request,
                    inventoryFull: failure == InventoryPlacementFailure.Full).Payload
                    .SequenceEqual(new byte[] { 0, 4 })
                && grounds.TryGet(ground.GroundId, 0, 0, out var retained) && retained == ground
                && inventory.Values.SequenceEqual(snapshot.Values),
            "full main and quick-slot bags reject a new ground item with CMD46 error 4 while retaining the same ground object and inventory");
        var silent = CraftPacketBuilders.CreateGetItemRejection(request);
        check(silent.Type == GameProtocolConstants.CommandPacketType && silent.ProtocolId == 46
                && silent.Payload.SequenceEqual(new byte[] { 0, 0x15 })
                && CraftPacketBuilders.CreateGetItemSilentRejection(request).Payload.SequenceEqual(silent.Payload),
            "other pickup failures keep the silent DF2008 rollback, not a false NOTI39 pickup or a new NOTI40 ground object");

        check(ItemDeletionPlanner.TryCreate(inventory, 0,
                    [new(2, CharacterInventoryLayout.MaterialSlotStart, 3037, 5)], items, out var crystals)
                && crystals.Inventory[CharacterInventoryLayout.MaterialSlotStart].CountOrValue == 95
                && crystals.Inventory.Count == inventory.Count
                && inventory.Values.SequenceEqual(snapshot.Values)
                && InventoryPacketBuilders.CreateDeleteItemReply(0, crystals.CompletionEntries).Payload
                    .SequenceEqual(new byte[] { 1, 0, 1, 73, 0, 5, 0, 0, 0 }),
            "CMD18 skill-material consumption succeeds with every slot occupied and does not allocate a slot or deduct twice");
        check(ItemDeletionPlanner.TryCreate(inventory, 0,
                    [new(0, CharacterInventoryLayout.QuickSlotStart, 8, 1)], items, out var potion)
                && potion.Inventory[CharacterInventoryLayout.QuickSlotStart].CountOrValue == 99
                && potion.Inventory.Count == inventory.Count
                && InventoryPacketBuilders.CreateUseStackableItemReply(CharacterInventoryLayout.QuickSlotStart, 0)
                    .Payload.SequenceEqual(new byte[] { 1, 3, 0, 0 }),
            "pure potion consumption needs no free slot and CMD47 retains its slot/list completion layout");
        check(!DungeonPickupPlanner.TryPlanItem(potion.Inventory, ground, items, uint.MaxValue,
                    out _, out var stillFull) && stillFull == InventoryPlacementFailure.Full,
            "using one item from a remaining stack does not bypass capacity for new loot, boxes or gifts");
            var consumedLast = ItemDeletionPlanner.TryCreate(inventory, 0,
                    [new(2, CharacterInventoryLayout.MaterialSlotStart, 3037, 100)], items, out var lastStack);
            var freeFailure = InventoryPlacementFailure.None;
            var afterConsumptionDestination = (ushort)0;
            if (consumedLast)
            {
                DungeonPickupPlanner.TryPlanItem(lastStack.Inventory, ground, items, uint.MaxValue,
                    out var afterConsumption, out freeFailure);
                afterConsumptionDestination = afterConsumption.DestinationSlot;
            }
            check(consumedLast
                && !lastStack.Inventory.ContainsKey(CharacterInventoryLayout.MaterialSlotStart)
                && freeFailure == InventoryPlacementFailure.None
                && afterConsumptionDestination == CharacterInventoryLayout.MaterialSlotStart,
            "consuming the last material stack really frees its category slot for a later pickup");
        check(!ItemDeletionPlanner.TryCreate(inventory, 0,
                    [new(2, CharacterInventoryLayout.MaterialSlotStart, 3037, 101)], items, out _)
                && inventory.Values.SequenceEqual(snapshot.Values),
            "a full bag does not weaken item identity or available-count validation for consumption");

        var storeRoot = Path.Combine(Path.GetTempPath(), $"daflegacy-full-bag-consume-{Guid.NewGuid():N}");
        try
        {
            var options = new ServerOptions { DatabasePath = Path.Combine(storeRoot, "state.db") };
            TestGameStore.SeedDefaultTestAccount(options);
            var store = TestGameStore.Create(options);
            await store.InitializeAsync();
            var created = await store.CreateCharacterAsync("test", new CreateCharacterRequest("FullBagTest", 2));
            var character = created.Character!;
            _ = await store.SaveCharacterInventoryAsync("test", character.Id, character.Gold, inventory.Values, []);
            var saved = await store.SaveCharacterInventoryAsync("test", character.Id, character.Gold,
                crystals.Inventory.Values, []);
            var reloadedStore = TestGameStore.Create(options);
            await reloadedStore.InitializeAsync();
            var reloaded = (await reloadedStore.GetCharactersAsync("test"))
                .Single(candidate => candidate.Id == character.Id);
            check(saved is not null && reloaded.Inventory!.Count == inventory.Count
                    && reloaded.Inventory.Single(item => item.Slot == CharacterInventoryLayout.MaterialSlotStart)
                        .CountOrValue == 95,
                "full-bag skill-material consumption persists across a store reload without creating or discarding other items");
        }
        finally
        {
            if (Directory.Exists(storeRoot))
            {
                Directory.Delete(storeRoot, recursive: true);
            }
        }
    }

    private static ItemCatalog CreateItemCatalog(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "equipment"));
        Directory.CreateDirectory(Path.Combine(root, "stackable"));
        File.WriteAllText(Path.Combine(root, "equipment", "equipment.lst"),
            "10000 `weapon.equ`\n4000 `relic.equ`\n");
        File.WriteAllText(Path.Combine(root, "equipment", "weapon.equ"),
            "[equipment type] `[weapon]`\n[grade] 1\n[price] 100\n[durability] 10\n");
        File.WriteAllText(Path.Combine(root, "equipment", "relic.equ"),
            "[equipment type] `[weapon]`\n[grade] 1\n[price] 100\n[durability] 10\n");
        File.WriteAllText(Path.Combine(root, "stackable", "stackable.lst"),
            "8 `potion.stk`\n3037 `cube.stk`\n3176 `loot.stk`\n");
        foreach (var name in new[] { "potion", "cube", "loot" })
        {
            File.WriteAllText(Path.Combine(root, "stackable", $"{name}.stk"),
                $"[stackable type] `{(name == "potion" ? "waste" : "material")}` 0\n[stack limit] 1000\n[weight] 1\n");
        }

        var scripts = new ScriptFileSystem(
            new ServerOptions { ScriptPvfPath = string.Empty, SkillScriptPath = root },
            NullLogger<ScriptFileSystem>.Instance);
        var items = new ItemCatalog(scripts, NullLogger<ItemCatalog>.Instance);
        items.Initialize();
        return items;
    }
}
