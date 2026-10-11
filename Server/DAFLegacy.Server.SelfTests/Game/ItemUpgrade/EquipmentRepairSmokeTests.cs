using System.Buffers.Binary;
using DAFLegacy.Protocol;
using DAFLegacy.Server;
using DAFLegacy.Server.SelfTests.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

internal static class EquipmentRepairSmokeTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), $"daflegacy-repair-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "equipment"));
            Directory.CreateDirectory(Path.Combine(root, "stackable"));
            File.WriteAllText(Path.Combine(root, "equipment/equipment.lst"),
                "10000 `weapon.equ`\n10001 `coat.equ`\n10002 `nodur.equ`\n10003 `noprice.equ`\n");
            File.WriteAllText(Path.Combine(root, "equipment/weapon.equ"),
                "[equipment type] `[weapon]`\n[grade] 30\n[price] 6000\n[durability] 40\n");
            File.WriteAllText(Path.Combine(root, "equipment/coat.equ"),
                "[equipment type] `[coat]`\n[grade] 20\n[price] 4000\n[durability] 30\n");
            File.WriteAllText(Path.Combine(root, "equipment/nodur.equ"), "[equipment type] `[title]`\n");
            File.WriteAllText(Path.Combine(root, "equipment/noprice.equ"), "[equipment type] `[weapon]`\n[durability] 40\n");
            File.WriteAllText(Path.Combine(root, "stackable/stackable.lst"), "8 `potion.stk`\n");
            File.WriteAllText(Path.Combine(root, "stackable/potion.stk"), "[stackable type] `[waste]` 0\n");
            var options = new ServerOptions { ScriptPvfPath = "", SkillScriptPath = root };
            var scripts = new ScriptFileSystem(options, NullLogger<ScriptFileSystem>.Instance);
            var items = new ItemCatalog(scripts, NullLogger<ItemCatalog>.Instance);
            var bag = new CharacterItemRecord(30, 10000, 1, State: 0x45, Durability: 10,
                SealState: 1, EquipmentQualitySeed: 0x12345678, InstanceId: Guid.NewGuid());
            var inventory = new Dictionary<ushort, CharacterItemRecord>
            {
                [30] = bag, [3] = bag with { Slot = 3, Durability = 0, InstanceId = Guid.NewGuid() },
                [31] = new(31, 10002, 1), [41] = new(41, 8, 10)
            };
            var worn = new Dictionary<ushort, CharacterItemRecord>
            {
                [9] = bag with { Slot = 9, Durability = 0, InstanceId = Guid.NewGuid() },
                [1] = new(1, 10001, 1, Durability: 15, InstanceId: Guid.NewGuid())
            };
            check(EquipmentRepairPlanner.TryCreate(inventory, worn, 10_000, 0, ushort.MaxValue, false,
                        items, out var all, out var failure)
                    && failure == EquipmentRepairFailure.None && all.Price == 2600 && all.RemainingGold == 7400
                    && all.MainInventory.Values.SequenceEqual(inventory.Values)
                    && all.Equipment[9].Durability == 40 && all.Equipment[1].Durability == 30
                    && all.RepairedItems.Count == 2 && all.RepairedItems.All(item => item.ItemSpace == 3)
                    && all.SynchronizationItems.Where(item => item.ItemSpace == 0).Select(item => item.Item)
                        .SequenceEqual(new[] { inventory[3], bag }),
                "repair-all charges and restores only worn equipment; backpack and quick-slot items stay unchanged and are explicitly synchronized after the legacy all-repair reply");
            check(EquipmentRepairPlanner.TryCreate(inventory, worn, 10_000, 3, ushort.MaxValue, false,
                        items, out var allFromWorn, out _) && allFromWorn.Price == all.Price,
                "the repair-all sentinel has the same worn-only business range from either supported item space");

            check(EquipmentRepairPlanner.TryCreate(inventory, worn, 10_000, 0, 30, false, items,
                        out var single, out _) && single.Price == 1575 && single.RemainingGold == 8425
                    && single.MainInventory[30] == bag with { Durability = 40 }
                    && single.MainInventory[3] == inventory[3] && single.Equipment[9] == worn[9]
                    && single.SynchronizationItems is [{ ItemSpace: 0, Item.Slot: 30 }],
                "a selected backpack repair changes only its slot and preserves UUID, quality seed, reinforcement and sealing state");
            check(EquipmentRepairPlanner.TryCreate(single.MainInventory, single.Equipment,
                        single.RemainingGold, 0, 30, false, items, out var repeated, out _)
                    && repeated.Price == 0 && repeated.RemainingGold == single.RemainingGold
                    && repeated.MainInventory[30] == single.MainInventory[30]
                    && repeated.SynchronizationItems.Count == 1,
                "a valid full-durability single repair succeeds for zero gold and still has a durability reconciliation row");
            var freeFull = new Dictionary<ushort, CharacterItemRecord> { [30] = new(30, 10003, 1, Durability: 40) };
            check(EquipmentRepairPlanner.TryCreate(freeFull, worn, 0, 0, 30, false, items, out var zero, out _)
                    && zero.Price == 0 && zero.RemainingGold == 0,
                "an already-full valid item does not require pricing metadata or funds for a zero-cost success");
            check(EquipmentRepairPlanner.TryCreate(inventory, worn, 10_000, 0, 30, true, items,
                        out var dungeon, out _) && dungeon.Price == 1732,
                "single repair keeps the existing dungeon multiplier and PVF-price truncation order");
            check(EquipmentRepairPlanner.TryCreate(inventory, all.Equipment, 0, 0, ushort.MaxValue, false,
                        items, out var fullWorn, out _) && fullWorn.Price == 0
                    && fullWorn.RepairedItems.Count == 0 && fullWorn.MainInventory[30] == bag
                    && fullWorn.SynchronizationItems.Any(item => item.ItemSpace == 0 && item.Item == bag),
                "when worn equipment is already full, repair-all remains free and restores the unchanged damaged backpack display");
            check(!EquipmentRepairPlanner.TryCreate(inventory, worn, 2599, 0, ushort.MaxValue, false,
                        items, out _, out var insufficient) && insufficient == EquipmentRepairFailure.InsufficientGold
                    && !EquipmentRepairPlanner.TryCreate(inventory, worn, 10_000, 0, 41, false, items, out _, out _)
                    && !EquipmentRepairPlanner.TryCreate(inventory, worn, 10_000, 0, 31, false, items, out _, out _)
                    && !EquipmentRepairPlanner.TryCreate(inventory, worn, 10_000, 0, 32, false, items, out _, out _)
                    && !EquipmentRepairPlanner.TryCreate(inventory, worn, 10_000, 7, 30, false, items, out _, out _)
                    && !EquipmentRepairPlanner.TryCreate(inventory, worn, 10_000, 3, 23, false, items, out _, out _)
                    && inventory[30] == bag && worn[9].Durability == 0,
                "insufficient funds, nonrepairable items, missing slots and unsupported spaces reject without changing source inventories");

            var reply = ShopPacketBuilders.CreateRepairEquipmentReply(7400, 0, ushort.MaxValue);
            check(reply.ProtocolId == 25 && reply.Payload.SequenceEqual(new byte[] { 1, 0xE8, 0x1C, 0, 0, 0, 0xFF, 0xFF }),
                "DF2008 CMD25 still returns success/u32 absolute gold/u8 item space/u16 slot");
            var projection = CharacterItemWireProjection.Project(bag, items);
            var sync = InventoryPacketBuilders.CreateUpdateItemList(0, [new GameInventoryEntry(bag.Slot, bag.ItemId,
                projection.AddInfo, projection.ItemAttr, projection.Durability, projection.SealState)]);
            check(sync.ProtocolId == 14 && sync.Payload.Length == 15
                    && BinaryPrimitives.ReadUInt16LittleEndian(sync.Payload.AsSpan(12)) == 10
                    && BinaryPrimitives.ReadUInt32LittleEndian(sync.Payload.AsSpan(7)) == bag.EquipmentQualitySeed,
                "the post-reply NOTI14 preserves the real backpack durability and equipment add_info rather than silently repairing it");

            var storeOptions = new ServerOptions
            {
                ScriptPvfPath = "",
                SkillScriptPath = root,
                DatabasePath = Path.Combine(root, "state.db")
            };
            TestGameStore.SeedDefaultTestAccount(storeOptions);
            var store = TestGameStore.Create(storeOptions);
            await store.InitializeAsync();
            var role = (await store.CreateCharacterAsync("test", new CreateCharacterRequest("RepairOnlyWorn", Level: 1))).Character!;
            _ = await store.SaveCharacterItemSpacesAsync("test", role.Id, all.RemainingGold,
                all.MainInventory.Values, all.Equipment.Values, []);
            var reload = TestGameStore.Create(storeOptions);
            await reload.InitializeAsync();
            var saved = (await reload.GetCharactersAsync("test")).Single(candidate => candidate.Id == role.Id);
            check(saved.Gold == 7400 && saved.Inventory!.Single(item => item.Slot == 30) == bag
                    && saved.Inventory!.Single(item => item.Slot == 3) == inventory[3]
                    && saved.Equipment!.Single(item => item.Slot == 9).Durability == 40,
                "repair-all persists the matching worn-only gold debit while keeping backpack and quick-slot durability/UUID unchanged");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
