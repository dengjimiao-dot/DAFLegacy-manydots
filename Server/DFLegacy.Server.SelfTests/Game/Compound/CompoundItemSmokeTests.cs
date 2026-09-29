using DFLegacy.Protocol;
using DFLegacy.Server;
using Microsoft.Extensions.Logging.Abstractions;

internal static class CompoundItemSmokeTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        CheckProtocolVectors(check);

        var fixtureRoot = Path.Combine(
            Path.GetTempPath(),
            $"dflegacy-compound-item-{Guid.NewGuid():N}");
        try
        {
            CreateFixture(fixtureRoot);
            var scripts = new ScriptFileSystem(
                new ServerOptions
                {
                    ScriptPvfPath = string.Empty,
                    SkillScriptPath = fixtureRoot
                },
                NullLogger<ScriptFileSystem>.Instance);
            var items = new ItemCatalog(
                scripts,
                NullLogger<ItemCatalog>.Instance);
            items.Initialize();
            var catalog = new CompoundCatalog(
                scripts,
                NullLogger<CompoundCatalog>.Instance);
            catalog.Initialize();

            CheckCatalog(check, catalog);
            CheckPlans(check, catalog, items);
            await CheckAtomicStoreAsync(check, fixtureRoot);
        }
        finally
        {
            if (Directory.Exists(fixtureRoot))
            {
                Directory.Delete(fixtureRoot, recursive: true);
            }
        }
    }

    public static void CheckRealPvf(
        Action<bool, string> check,
        ScriptFileSystem scripts,
        ItemCatalog items)
    {
        var catalog = new CompoundCatalog(
            scripts,
            NullLogger<CompoundCatalog>.Instance);
        catalog.Initialize();

        check(catalog.Count == 748
                && catalog.TryGetDefinition(5_001, out var haste)
                && haste.RecipeType == 2
                && haste.GoldCost == 0
                && haste.Ingredients.SequenceEqual(
                [
                    new CompoundItemAmount(3_055, 30),
                    new CompoundItemAmount(3_037, 23),
                    new CompoundItemAmount(3_012, 8)
                ])
                && haste.Results.SequenceEqual(
                [
                    new CompoundItemAmount(1_006, 30)
                ])
                && haste.RequiredSkills.SequenceEqual(
                [
                    new CompoundSkillRequirement(184, 1)
                ]),
            "real PVF loads all 748 executable recipes and preserves the haste recipe's production skill and 30-unit result");
        check(new ushort[] { 5_358, 5_367, 5_376, 5_385 }.All(itemId =>
                items.TryGetDefinition(itemId, out var recipeItem)
                && recipeItem.TypeTag == "recipe"
                && !catalog.TryGetDefinition(itemId, out _)),
            "real PVF excludes four malformed level-47 weapon recipes whose six material quantities are all zero");
        check(catalog.TryGetDefinition(5_057, out var cloth)
                && cloth.RecipeType == 0
                && cloth.GoldCost == 50
                && cloth.Ingredients.SequenceEqual(
                [
                    new CompoundItemAmount(3_030, 1)
                ])
                && cloth.Results.SequenceEqual(
                [
                    new CompoundItemAmount(3_134, 1)
                ])
                && cloth.RequiredSkills.Count == 0,
            "real PVF recipe 5057 separates its material row from the 50-gold input row");
    }

    private static void CheckProtocolVectors(Action<bool, string> check)
    {
        check(GameProtocolEngine.CompoundItemCommand == 27
                && GameProtocolEngine.TryParseCompoundItemCommand(
                    Convert.FromHexString("1500DE1301020001"),
                    out var direct)
                && direct == new GameCompoundItemCommand(0x15, 5_086, true, 2)
                && GameProtocolEngine.TryParseCompoundItemCommand(
                    Convert.FromHexString("1600090000020001"),
                    out var slotted)
                && slotted == new GameCompoundItemCommand(0x16, 9, false, 2)
                && !GameProtocolEngine.TryParseCompoundItemCommand(
                    Convert.FromHexString("1500DE1302000000"),
                    out _),
            "CMD27 parses both target-client source modes and rejects invalid fixed bytes");

        var reply = GameProtocolEngine.CreateCompoundItemReply(
        [
            new GameCompoundItemConsumption(0, 0, 900),
            new GameCompoundItemConsumption(0, 9, 100)
        ],
        [
            new GameCompoundItemResult(4, 3_038, 2, 0, 0)
        ]);
        var failure = GameProtocolEngine.CreateCompoundItemFailure(19);
        check(reply.Type == GameProtocolEngine.CommandPacketType
                && reply.ProtocolId == GameProtocolEngine.CompoundItemCommand
                && reply.Payload.SequenceEqual(Convert.FromHexString(
                    "01020000008403000000090064000000010400DE0B02000000000000"))
                && failure.Payload.SequenceEqual(Convert.FromHexString("0013")),
            "CMD27 success uses the target client's one-byte result flag and exact consumption/result rows");
    }

    private static void CheckCatalog(
        Action<bool, string> check,
        CompoundCatalog catalog)
    {
        check(catalog.Count == 1
                && catalog.RecipeItemIds.SequenceEqual(new ushort[] { 5_000 })
                && catalog.TryGetDefinition(5_000, out var recipe)
                && recipe.RecipeType == 5
                && recipe.Ingredients.SequenceEqual(
                [
                    new CompoundItemAmount(3_001, 2),
                    new CompoundItemAmount(3_002, 1)
                ])
                && recipe.GoldCost == 25
                && recipe.Results.SequenceEqual(
                [
                    new CompoundItemAmount(1_000, 1),
                    new CompoundItemAmount(3_003, 3)
                ])
                && recipe.RequiredSkills.SequenceEqual(
                [
                    new CompoundSkillRequirement(184, 2)
                ]),
            "compound catalog parses materials, gold, multiple result rows, recipe type and skill requirement");
    }

    private static void CheckPlans(
        Action<bool, string> check,
        CompoundCatalog catalog,
        ItemCatalog items)
    {
        var source = CreateSourceInventory(includeRecipe: true);
        var skills = new[] { new CharacterSkillRecord(0, 184, 2) };
        check(CompoundItemPlanner.TryCreate(
                    source,
                    currentGold: 100,
                    skills,
                    sourceValue: 41,
                    sourceIsItemId: false,
                    craftCount: 2,
                    weightLimit: uint.MaxValue,
                    catalog,
                    items,
                    out var plan,
                    out var failure)
                && failure == CompoundItemFailure.None
                && plan.RecipeItemId == 5_000
                && plan.RecipeType == 5
                && plan.CraftCount == 2
                && plan.GoldCost == 50
                && plan.RemainingGold == 50
                && plan.ConsumedItems.SequenceEqual(
                [
                    new CompoundItemConsumption(0, 0, 50),
                    new CompoundItemConsumption(0, 41, 0),
                    new CompoundItemConsumption(0, 73, 0),
                    new CompoundItemConsumption(0, 74, 1),
                    new CompoundItemConsumption(0, 75, 0)
                ])
                && plan.CreatedItems.SequenceEqual(
                [
                    new CompoundItemCreation(9, 1_000, 1, 0, 35),
                    new CompoundItemCreation(10, 1_000, 1, 0, 35),
                    new CompoundItemCreation(76, 3_003, 10, 0, 0)
                ])
                && !plan.MainInventory.ContainsKey(41)
                && !plan.MainInventory.ContainsKey(73)
                && plan.MainInventory[74].CountOrValue == 1
                && !plan.MainInventory.ContainsKey(75)
                && plan.MainInventory[76].CountOrValue == 10
                && source[41].CountOrValue == 2
                && source[73].CountOrValue == 2
                && source[74].CountOrValue == 3
                && source[75].CountOrValue == 2
                && source[76].CountOrValue == 4,
            "compound planner atomically aggregates split materials, consumes recipes/gold and places equipment plus stackable results");

        var directSource = CreateSourceInventory(includeRecipe: false);
        check(CompoundItemPlanner.TryCreate(
                    directSource,
                    100,
                    skills,
                    sourceValue: 5_000,
                    sourceIsItemId: true,
                    craftCount: 1,
                    uint.MaxValue,
                    catalog,
                    items,
                    out var directPlan,
                    out _)
                && directPlan.ConsumedItems.All(item => item.Slot != 41)
                && directPlan.CreatedItems.Count == 2,
            "compound planner accepts the target client's direct recipe-item-id mode without consuming a recipe instance");

        check(!CompoundItemPlanner.TryCreate(
                    source,
                    100,
                    Array.Empty<CharacterSkillRecord>(),
                    41,
                    false,
                    1,
                    uint.MaxValue,
                    catalog,
                    items,
                    out _,
                    out var skillFailure)
                && skillFailure == CompoundItemFailure.InvalidRecipe,
            "compound planner rejects a missing or under-level production skill with client error 19");
        check(!CompoundItemPlanner.TryCreate(
                    source,
                    24,
                    skills,
                    41,
                    false,
                    1,
                    uint.MaxValue,
                    catalog,
                    items,
                    out _,
                    out var goldFailure)
                && goldFailure == CompoundItemFailure.InsufficientGold,
            "compound planner rejects insufficient gold with client error 10");

        var missingMaterial = CreateSourceInventory(includeRecipe: true);
        missingMaterial.Remove(75);
        check(!CompoundItemPlanner.TryCreate(
                    missingMaterial,
                    100,
                    skills,
                    41,
                    false,
                    1,
                    uint.MaxValue,
                    catalog,
                    items,
                    out _,
                    out var materialFailure)
                && materialFailure == CompoundItemFailure.InsufficientMaterial
                && missingMaterial[41].CountOrValue == 2
                && missingMaterial[73].CountOrValue == 2,
            "compound planner reports missing materials without mutating its source inventory");

        var lateCapacityFailure = CreateSourceInventory(includeRecipe: true);
        check(!CompoundItemPlanner.TryCreate(
                    lateCapacityFailure,
                    100,
                    skills,
                    41,
                    false,
                    1,
                    weightLimit: 10,
                    catalog,
                    items,
                    out _,
                    out var lateFailure)
                && lateFailure == CompoundItemFailure.InventoryFull
                && !lateCapacityFailure.ContainsKey(9)
                && lateCapacityFailure[41].CountOrValue == 2
                && lateCapacityFailure[73].CountOrValue == 2
                && lateCapacityFailure[75].CountOrValue == 2
                && lateCapacityFailure[76].CountOrValue == 4,
            "compound planner discards an already planned equipment result when a later result exceeds capacity");

        var fullInventory = CreateSourceInventory(includeRecipe: true);
        for (ushort slot = CharacterInventoryLayout.EquipmentSlotStart;
             slot < CharacterInventoryLayout.EquipmentSlotEnd;
             slot++)
        {
            fullInventory[slot] = new CharacterItemRecord(
                slot,
                1_000,
                1,
                Durability: 35);
        }

        for (ushort slot = CharacterInventoryLayout.QuickSlotStart;
             slot < CharacterInventoryLayout.QuickSlotEnd;
             slot++)
        {
            fullInventory[slot] = new CharacterItemRecord(
                slot,
                1_000,
                1,
                Durability: 35);
        }

        check(!CompoundItemPlanner.TryCreate(
                    fullInventory,
                    100,
                    skills,
                    41,
                    false,
                    1,
                    uint.MaxValue,
                    catalog,
                    items,
                    out _,
                    out var fullFailure)
                && fullFailure == CompoundItemFailure.InventoryFull
                && fullInventory[41].CountOrValue == 2
                && fullInventory[73].CountOrValue == 2,
            "compound planner rolls back every consumption when any result has no inventory space");
    }

    private static async Task CheckAtomicStoreAsync(
        Action<bool, string> check,
        string fixtureRoot)
    {
        var options = new ServerOptions
        {
            DataPath = Path.Combine(fixtureRoot, "compound-state.json")
        };
        var store = new JsonGameStore(
            options,
            NullLogger<JsonGameStore>.Instance);
        await store.InitializeAsync();
        var character = (await store.GetCharactersAsync("test"))[0];
        var expectedInventory = new CharacterItemRecord[]
        {
            new(41, 5_000, 2),
            new(73, 3_001, 4)
        };
        var prepared = await store.SaveCharacterInventoryAsync(
            "test",
            character.Id,
            100,
            expectedInventory,
            character.Equipment ?? []);
        var plannedInventory = new CharacterItemRecord[]
        {
            new(41, 5_000, 1),
            new(73, 3_001, 2),
            new(76, 3_003, 3)
        };
        var committed = prepared is null
            ? null
            : await store.CommitCompoundItemAsync(
                "test",
                character.Id,
                expectedGold: 100,
                expectedInventory,
                remainingGold: 75,
                plannedInventory);
        var staleCommit = await store.CommitCompoundItemAsync(
            "test",
            character.Id,
            expectedGold: 100,
            expectedInventory,
            remainingGold: 0,
            Array.Empty<CharacterItemRecord>());

        var reloadedStore = new JsonGameStore(
            options,
            NullLogger<JsonGameStore>.Instance);
        await reloadedStore.InitializeAsync();
        var reloaded = (await reloadedStore.GetCharactersAsync("test"))[0];
        check(committed is not null
                && committed.Gold == 75
                && committed.Inventory?.SequenceEqual(plannedInventory) == true
                && staleCommit is null
                && reloaded.Gold == 75
                && reloaded.Inventory?.SequenceEqual(plannedInventory) == true,
            "compound store commit persists gold and inventory together and rejects a stale replay without partial writes");
    }

    private static Dictionary<ushort, CharacterItemRecord> CreateSourceInventory(
        bool includeRecipe)
    {
        var inventory = new Dictionary<ushort, CharacterItemRecord>
        {
            [73] = new(73, 3_001, 2),
            [74] = new(74, 3_001, 3),
            [75] = new(75, 3_002, 2),
            [76] = new(76, 3_003, 4)
        };
        if (includeRecipe)
        {
            inventory[41] = new CharacterItemRecord(41, 5_000, 2);
        }

        return inventory;
    }

    private static void CreateFixture(string root)
    {
        var equipmentDirectory = Path.Combine(
            root,
            "equipment",
            "character",
            "test",
            "weapon");
        var recipeDirectory = Path.Combine(root, "stackable", "recipe");
        var materialDirectory = Path.Combine(root, "stackable", "material");
        Directory.CreateDirectory(equipmentDirectory);
        Directory.CreateDirectory(recipeDirectory);
        Directory.CreateDirectory(materialDirectory);

        File.WriteAllText(
            Path.Combine(root, "equipment", "equipment.lst"),
            "1000 `character/test/weapon/test.equ`\n");
        File.WriteAllText(
            Path.Combine(equipmentDirectory, "test.equ"),
            "[name] `Test Product`\n"
            + "[equipment type] `[weapon]`\n"
            + "[attach type] `free`\n"
            + "[weight] 1\n"
            + "[durability] 35\n");
        File.WriteAllText(
            Path.Combine(root, "stackable", "stackable.lst"),
            "5000 `recipe/test_recipe.stk`\n"
            + "3001 `material/input_a.stk`\n"
            + "3002 `material/input_b.stk`\n"
            + "3003 `material/output.stk`\n");
        File.WriteAllText(
            Path.Combine(recipeDirectory, "test_recipe.stk"),
            "[name] `Test Recipe`\n"
            + "[stackable type] `[recipe]` 5\n"
            + "[attach type] `free`\n"
            + "[weight] 1\n"
            + "[stack limit] 255\n"
            + "[int data]\n"
            + "3\n"
            + "3001 2\n"
            + "3002 1\n"
            + "0 25 // gold\n"
            + "2\n"
            + "1000 1\n"
            + "3003 3\n"
            + "1\n"
            + "184 2\n"
            + "1\n"
            + "[/int data]\n"
            + "[string data]\n"
            + "`[craftmanship]`\n"
            + "[/string data]\n");
        WriteMaterial(
            Path.Combine(materialDirectory, "input_a.stk"),
            "Input A",
            stackLimit: 100);
        WriteMaterial(
            Path.Combine(materialDirectory, "input_b.stk"),
            "Input B",
            stackLimit: 100);
        WriteMaterial(
            Path.Combine(materialDirectory, "output.stk"),
            "Output",
            stackLimit: 10);
    }

    private static void WriteMaterial(
        string path,
        string name,
        int stackLimit) =>
        File.WriteAllText(
            path,
            $"[name] `{name}`\n"
            + "[stackable type] `[material]`\n"
            + "[attach type] `free`\n"
            + "[weight] 1\n"
            + $"[stack limit] {stackLimit}\n");
}
