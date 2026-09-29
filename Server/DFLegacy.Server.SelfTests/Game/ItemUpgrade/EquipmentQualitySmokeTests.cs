using DFLegacy.Protocol;
using DFLegacy.Server;
using Microsoft.Extensions.Logging.Abstractions;

internal static class EquipmentQualitySmokeTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        CheckProtocolVectors(check);
        CheckQualityRolls(check);

        var fixtureRoot = Path.Combine(
            Path.GetTempPath(),
            $"dflegacy-equipment-quality-{Guid.NewGuid():N}");
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
            var reseals = new ResealCatalog(
                scripts,
                NullLogger<ResealCatalog>.Instance);
            reseals.Initialize();
            CheckPlans(check, items, reseals);
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
        var reseals = new ResealCatalog(
            scripts,
            NullLogger<ResealCatalog>.Instance);
        reseals.Initialize();
        check(items.TryGetDefinition(
                    EquipmentQuality.KaleidoBoxItemId,
                    out var kaleido)
                && kaleido.ScriptPath.Equals(
                    "stackable/cash/box_kaleido.stk",
                    StringComparison.OrdinalIgnoreCase)
                && kaleido.ScriptKind == ItemScriptKind.Stackable
                && kaleido.InventoryCategory == ItemInventoryCategory.Consumable
                && kaleido.TypeTag == "etc"
                && kaleido.AttachType == ItemAttachType.Trade
                && kaleido.Grade == 1
                && kaleido.Rarity == 3
                && items.TryGetDefinition(27_000, out var starterEquipment)
                && starterEquipment.ScriptKind == ItemScriptKind.Equipment
                && starterEquipment.InventoryCategory
                    == ItemInventoryCategory.Equipment
                && items.TryGetDefinition(26_034, out var title)
                && title.IsTitle
                && title.TypeTag == "title name"
                && CharacterItemWireProjection.Project(
                    new CharacterItemRecord(
                        10,
                        title.Id,
                        1,
                        EquipmentQualitySeed: EquipmentQuality.TopQualitySeed),
                    items).AddInfo == EquipmentQuality.MiddleQualitySeed
                && items.TryGetDefinition(
                    CharacterItemSealing.GoldenWaxItemId,
                    out var wax)
                && wax.ScriptPath.Equals(
                    "stackable/cash/candle_goldwax.stk",
                    StringComparison.OrdinalIgnoreCase)
                && reseals.IsAvailable
                && reseals.GradeBandCount == 5
                && reseals.RarityCount == 5,
            "real PVF identifies both CMD84 materials, title exclusions and the five-row reseal tables");
    }

    private static void CheckProtocolVectors(Action<bool, string> check)
    {
        var request = Convert.FromHexString("15000900686B4900");
        check(GameProtocolEngine.TryParseResetItemAttributeCommand(
                    request,
                    out var command)
                && command == new GameResetItemAttributeCommand(9, 27_496, 73)
                && !GameProtocolEngine.TryParseResetItemAttributeCommand(
                    request.Append((byte)0).ToArray(),
                    out _),
            "CMD84 parses only the target-client 8-byte quality-adjustment request");

        var success = GameProtocolEngine.CreateResetItemAttributeReply(
            materialSlot: 73,
            materialRemaining: 16,
            operationType: (ushort)ItemAttributeModificationOperation.AdjustQuality);
        var resealSuccess = GameProtocolEngine.CreateResetItemAttributeReply(
            materialSlot: 41,
            materialRemaining: 5,
            operationType: (ushort)ItemAttributeModificationOperation.Reseal);
        var failure = GameProtocolEngine.CreateResetItemAttributeFailure(
            (byte)ItemAttributeModificationFailure.InsufficientMaterial);
        var unsupportedItem = GameProtocolEngine.CreateResetItemAttributeFailure(
            (byte)ItemAttributeModificationFailure.UnsupportedItem);
        check(success.Type == GameProtocolEngine.CommandPacketType
                && success.ProtocolId
                    == GameProtocolEngine.ResetItemAttributeCommand
                && success.Payload.SequenceEqual(
                    Convert.FromHexString("014900100000000200"))
                && resealSuccess.Payload.SequenceEqual(
                    Convert.FromHexString("012900050000000100"))
                && failure.Payload.SequenceEqual(
                    Convert.FromHexString("0016"))
                && unsupportedItem.Payload.SequenceEqual(
                    Convert.FromHexString("0013"))
                && (byte)ItemAttributeModificationFailure.ResealLimit == 13
                && (byte)ItemAttributeModificationFailure.InvalidMaterial == 17
                && (byte)ItemAttributeModificationFailure.AlreadySealed == 18
                && (byte)ItemAttributeModificationFailure.UnsupportedItem == 19
                && (byte)ItemAttributeModificationFailure.InsufficientMaterial == 22,
            "CMD84 success carries material slot/count and operation type exactly as the target client decodes, while failures retain their one-byte error codes");
    }

    private static void CheckQualityRolls(Action<bool, string> check)
    {
        var seededRoll = EquipmentQuality.RollSeed(new SeededRandomSource(123));
        var seededExpected = checked(
            1u + (uint)new SeededRandomSource(123).Next(
                checked((int)EquipmentQuality.MaximumRandomSeed)));
        var ordinaryRoll = EquipmentQuality.RollDifferentSeed(
            currentSeed: 1,
            new SequenceDropRandomSource(123));
        var fallbackRoll = EquipmentQuality.RollDifferentSeed(
            currentSeed: 5,
            new SequenceDropRandomSource(4, 4, 4, 4, 4, 4, 4, 4));
        check(seededRoll == seededExpected
                && seededRoll >= 1u
                && seededRoll <= EquipmentQuality.MaximumRandomSeed
                && ordinaryRoll == 124
                && fallbackRoll == 4
                && EquipmentQuality.MaximumRandomSeed == 999_999_997
                && EquipmentQuality.TopQualitySeed == 999_999_998,
            "equipment quality samples IDropRandomSource (seeded substitute keeps it deterministic), uses DF60A1's 1..999999997 range, and retains the deterministic changed-value fallback");

        var beforeReset = EquipmentQuality.GetNpcShopSeed(
            new DateTimeOffset(2026, 8, 13, 5, 59, 59, TimeSpan.FromHours(8)));
        var atReset = EquipmentQuality.GetNpcShopSeed(
            new DateTimeOffset(2026, 8, 13, 6, 0, 0, TimeSpan.FromHours(8)));
        var beforeNextReset = EquipmentQuality.GetNpcShopSeed(
            new DateTimeOffset(2026, 8, 14, 5, 59, 59, TimeSpan.FromHours(8)));
        check(beforeReset == 46_212
                && atReset == 46_213
                && beforeNextReset == atReset,
            "all NPC shops share the date-derived equipment-quality seed and advance it at local 06:00");
    }

    private static void CheckPlans(
        Action<bool, string> check,
        ItemCatalog items,
        ResealCatalog reseals)
    {
        var sourceMain = new Dictionary<ushort, CharacterItemRecord>
        {
            [9] = new(
                9,
                1_000,
                1,
                State: 5,
                Durability: 35,
                EquipmentQualitySeed: 10),
            [41] = new(41, EquipmentQuality.KaleidoBoxItemId, 2)
        };
        check(ItemAttributeModificationPlanner.TryCreate(
                    sourceMain,
                    new Dictionary<ushort, CharacterItemRecord>(),
                    requestedTargetSlot: 9,
                    expectedTargetItemId: 1_000,
                    materialSlot: 41,
                    items,
                    reseals,
                    new SequenceDropRandomSource(123),
                    out var bagPlan,
                    out var bagFailure)
                && bagFailure == ItemAttributeModificationFailure.None
                && bagPlan.TargetListType == 0
                && bagPlan.TargetSlot == 9
                && bagPlan.PreviousQualitySeed == 10
                && bagPlan.NewQualitySeed == 124
                && bagPlan.MaterialRemaining == 1
                && bagPlan.MainInventory[9] is
                {
                    CountOrValue: 1,
                    State: 5,
                    EquipmentQualitySeed: 124
                }
                && bagPlan.MainInventory[41].CountOrValue == 1
                && sourceMain[9].EquipmentQualitySeed == 10
                && sourceMain[41].CountOrValue == 2,
            "quality planner changes only the equipment seed and consumes one Kaleido box without mutating inputs");

        var equipped = new Dictionary<ushort, CharacterItemRecord>
        {
            [16] = new(
                16,
                1_000,
                1,
                State: 7,
                Durability: 35)
        };
        var materialOnly = new Dictionary<ushort, CharacterItemRecord>
        {
            [41] = new(41, EquipmentQuality.KaleidoBoxItemId, 1)
        };
        check(ItemAttributeModificationPlanner.TryCreate(
                    materialOnly,
                    equipped,
                    requestedTargetSlot: 7,
                    expectedTargetItemId: 1_000,
                    materialSlot: 41,
                    items,
                    reseals,
                    new SequenceDropRandomSource(1),
                    out var wornPlan,
                    out _)
                && wornPlan.TargetListType == 3
                && wornPlan.TargetSlot == 16
                && wornPlan.PreviousQualitySeed == 1
                && wornPlan.NewQualitySeed == 2
                && wornPlan.Equipment[16].State == 7
                && wornPlan.Equipment[16].EquipmentQualitySeed == 2
                && wornPlan.MainInventory.Count == 0,
            "CMD84 maps compact worn-accessory slots and preserves reinforcement while removing an exhausted box stack");

        var adjustedWire = CharacterItemWireProjection.Project(
            bagPlan.MainInventory[9],
            items);
        var legacyWire = CharacterItemWireProjection.Project(
            new CharacterItemRecord(10, 1_000, 1, State: 3, Durability: 35),
            items);
        check(adjustedWire is { AddInfo: 124, ItemAttr: 5 }
                && legacyWire is { AddInfo: 1, ItemAttr: 3 },
            "ordinary equipment sends quality through add_info and reinforcement through item_attr, including legacy seed-one records");

        var titleInventory = new Dictionary<ushort, CharacterItemRecord>
        {
            [10] = new(
                10,
                1_001,
                1,
                EquipmentQualitySeed: EquipmentQuality.TopQualitySeed),
            [41] = new(41, EquipmentQuality.KaleidoBoxItemId, 2)
        };
        var titleWire = CharacterItemWireProjection.Project(
            titleInventory[10],
            items);
        var mailedTitleWire = CharacterItemWireProjection.Project(
            new CharacterMailAttachmentRecord(
                1_001,
                1,
                EquipmentQualitySeed: 123_456_789),
            items);
        check(items.TryGetDefinition(1_001, out var titleDefinition)
                && titleDefinition.IsTitle
                && titleDefinition.TypeTag == "title name"
                && titleWire.AddInfo == EquipmentQuality.MiddleQualitySeed
                && mailedTitleWire.AddInfo == EquipmentQuality.MiddleQualitySeed
                && !ItemAttributeModificationPlanner.TryCreate(
                    titleInventory,
                    new Dictionary<ushort, CharacterItemRecord>(),
                    requestedTargetSlot: 10,
                    expectedTargetItemId: 1_001,
                    materialSlot: 41,
                    items,
                    reseals,
                    new SequenceDropRandomSource(0),
                    out _,
                    out var titleFailure)
                && titleFailure == ItemAttributeModificationFailure.UnsupportedItem
                && titleInventory[10].EquipmentQualitySeed
                    == EquipmentQuality.TopQualitySeed
                && titleInventory[41].CountOrValue == 2,
            "title equipment always projects as middle grade and rejects Kaleido adjustment without consuming its material");

        check(CharacterInventoryPlanner.TryPlace(
                    new Dictionary<ushort, CharacterItemRecord>(),
                    new CharacterMailAttachmentRecord(
                        1_000,
                        1,
                        State: 4,
                        Durability: 35,
                        EquipmentQualitySeed: 456),
                    items,
                    uint.MaxValue,
                    out var placement,
                    out _)
                && placement.Inventory[placement.DestinationSlot] is
                {
                    State: 4,
                    EquipmentQualitySeed: 456
                },
            "equipment quality survives generic inventory placement used by mail, shops, crafting and warehouse withdrawal");

        var unsealedState = CharacterItemSealing.SetResealCount(
            new CharacterItemRecord(
                9,
                1_002,
                1,
                State: 5,
                Durability: 35,
                SealState: CharacterItemSealing.Unsealed,
                EquipmentQualitySeed: 456),
            resealCount: 2);
        var resealMain = new Dictionary<ushort, CharacterItemRecord>
        {
            [9] = unsealedState,
            [41] = new(41, CharacterItemSealing.GoldenWaxItemId, 20)
        };
        check(items.TryGetDefinition(1_002, out var sealingDefinition)
                && reseals.TryGetCost(
                    sealingDefinition,
                    currentResealCount: 2,
                    out var resealCost)
                && resealCost == 17
                && ItemAttributeModificationPlanner.TryCreate(
                    resealMain,
                    new Dictionary<ushort, CharacterItemRecord>(),
                    requestedTargetSlot: 9,
                    expectedTargetItemId: 1_002,
                    materialSlot: 41,
                    items,
                    reseals,
                    new SequenceDropRandomSource(),
                    out var resealPlan,
                    out var resealFailure)
                && resealFailure == ItemAttributeModificationFailure.None
                && resealPlan.Operation
                    == ItemAttributeModificationOperation.Reseal
                && resealPlan.MaterialConsumed == 17
                && resealPlan.MaterialRemaining == 3
                && resealPlan.PreviousResealCount == 2
                && resealPlan.NewResealCount == 3
                && resealPlan.MainInventory[9].SealState
                    == CharacterItemSealing.Sealed
                && CharacterItemSealing.GetResealCount(
                    resealPlan.MainInventory[9]) == 3
                && (resealPlan.MainInventory[9].State
                    & CharacterItemSealing.ReinforcementMask) == 5
                && resealPlan.MainInventory[9].EquipmentQualitySeed == 456
                && resealMain[9].SealState == CharacterItemSealing.Unsealed
                && CharacterItemSealing.GetResealCount(resealMain[9]) == 2
                && resealMain[41].CountOrValue == 20,
            "reseal uses grade/rarity/count cost, seals the item, increments the high-bit counter and preserves reinforcement and quality without mutating inputs");

        var alreadySealed = new Dictionary<ushort, CharacterItemRecord>(resealMain)
        {
            [9] = unsealedState with { SealState = CharacterItemSealing.Sealed }
        };
        var exhaustedReseals = new Dictionary<ushort, CharacterItemRecord>(resealMain)
        {
            [9] = CharacterItemSealing.SetResealCount(
                unsealedState,
                CharacterItemSealing.MaximumResealCount)
        };
        var insufficientWax = new Dictionary<ushort, CharacterItemRecord>(resealMain)
        {
            [41] = new(41, CharacterItemSealing.GoldenWaxItemId, 16)
        };
        check(!TryReseal(alreadySealed, out var sealedFailure)
                && sealedFailure == ItemAttributeModificationFailure.AlreadySealed
                && !TryReseal(exhaustedReseals, out var limitFailure)
                && limitFailure == ItemAttributeModificationFailure.ResealLimit
                && !TryReseal(insufficientWax, out var waxFailure)
                && waxFailure == ItemAttributeModificationFailure.InsufficientMaterial
                && alreadySealed[41].CountOrValue == 20
                && exhaustedReseals[41].CountOrValue == 20
                && insufficientWax[41].CountOrValue == 16,
            "reseal returns the target client's distinct already-sealed, seven-use-limit and insufficient-wax errors without partial consumption");

        bool TryReseal(
            IReadOnlyDictionary<ushort, CharacterItemRecord> inventory,
            out ItemAttributeModificationFailure failure) =>
            ItemAttributeModificationPlanner.TryCreate(
                inventory,
                new Dictionary<ushort, CharacterItemRecord>(),
                9,
                1_002,
                41,
                items,
                reseals,
                new SequenceDropRandomSource(),
                out _,
                out failure);

        var freeEquipmentWithWax = new Dictionary<ushort, CharacterItemRecord>(sourceMain)
        {
            [41] = new(41, CharacterItemSealing.GoldenWaxItemId, 20)
        };
        var missingMaterial = new Dictionary<ushort, CharacterItemRecord>(sourceMain);
        missingMaterial.Remove(41);
        check(!ItemAttributeModificationPlanner.TryCreate(
                    freeEquipmentWithWax,
                    new Dictionary<ushort, CharacterItemRecord>(),
                    9,
                    1_000,
                    41,
                    items,
                    reseals,
                    new SequenceDropRandomSource(),
                    out _,
                    out var unsupportedReseal)
                && unsupportedReseal
                    == ItemAttributeModificationFailure.UnsupportedItem
                && !ItemAttributeModificationPlanner.TryCreate(
                    missingMaterial,
                    new Dictionary<ushort, CharacterItemRecord>(),
                    9,
                    1_000,
                    41,
                    items,
                    reseals,
                    new SequenceDropRandomSource(),
                    out _,
                    out var missingMaterialFailure)
                && missingMaterialFailure
                    == ItemAttributeModificationFailure.InvalidMaterial
                && freeEquipmentWithWax[41].CountOrValue == 20,
            "CMD84 rejects non-sealing reseal targets as error 19 and missing or unknown materials as error 17 without consuming inventory");

        var wrongMaterial = new Dictionary<ushort, CharacterItemRecord>(sourceMain)
        {
            [41] = new CharacterItemRecord(41, 16, 2)
        };
        check(!ItemAttributeModificationPlanner.TryCreate(
                    wrongMaterial,
                    new Dictionary<ushort, CharacterItemRecord>(),
                    9,
                    1_000,
                    41,
                    items,
                    reseals,
                    new SequenceDropRandomSource(0),
                    out _,
                    out var materialFailure)
                && materialFailure
                    == ItemAttributeModificationFailure.InvalidMaterial,
            "quality adjustment accepts only item 15 as its consumable material");
    }

    private static async Task CheckAtomicStoreAsync(
        Action<bool, string> check,
        string fixtureRoot)
    {
        var options = new ServerOptions
        {
            DataPath = Path.Combine(fixtureRoot, "quality-state.json")
        };
        var store = new JsonGameStore(
            options,
            NullLogger<JsonGameStore>.Instance);
        await store.InitializeAsync();
        var character = (await store.GetCharactersAsync("test"))[0];
        var expectedMain = new CharacterItemRecord[]
        {
            new(41, EquipmentQuality.KaleidoBoxItemId, 1)
        };
        var expectedEquipment = new CharacterItemRecord[]
        {
            new(
                16,
                1_000,
                1,
                State: 7,
                Durability: 35,
                EquipmentQualitySeed: 10)
        };
        var prepared = await store.SaveCharacterInventoryAsync(
            "test",
            character.Id,
            character.Gold,
            expectedMain,
            expectedEquipment);
        var plannedMain = Array.Empty<CharacterItemRecord>();
        var plannedEquipment = new CharacterItemRecord[]
        {
            expectedEquipment[0] with { EquipmentQualitySeed = 101 }
        };
        var committed = prepared is null
            ? null
            : await store.CommitItemAttributeModificationAsync(
                "test",
                character.Id,
                expectedMain,
                expectedEquipment,
                plannedMain,
                plannedEquipment);
        var staleCommit = await store.CommitItemAttributeModificationAsync(
            "test",
            character.Id,
            expectedMain,
            expectedEquipment,
            plannedMain,
            plannedEquipment);

        var reloadedStore = new JsonGameStore(
            options,
            NullLogger<JsonGameStore>.Instance);
        await reloadedStore.InitializeAsync();
        var reloaded = (await reloadedStore.GetCharactersAsync("test"))[0];
        check(committed is not null
                && committed.Inventory?.Count == 0
                && committed.Equipment?.SequenceEqual(plannedEquipment) == true
                && staleCommit is null
                && reloaded.Inventory?.Count == 0
                && reloaded.Equipment?.SequenceEqual(plannedEquipment) == true,
            "CMD84 store commit persists material consumption and target changes atomically and rejects stale replay");

        var expectedResealMain = new CharacterItemRecord[]
        {
            new(
                9,
                1_002,
                1,
                State: 5,
                Durability: 35,
                SealState: CharacterItemSealing.Unsealed,
                EquipmentQualitySeed: 456),
            new(41, CharacterItemSealing.GoldenWaxItemId, 20)
        };
        var resealedItem = CharacterItemSealing.SetResealCount(
            expectedResealMain[0] with
            {
                SealState = CharacterItemSealing.Sealed
            },
            resealCount: 1);
        var plannedResealMain = new CharacterItemRecord[]
        {
            resealedItem,
            expectedResealMain[1] with { CountOrValue = 5 }
        };
        var preparedReseal = await store.SaveCharacterInventoryAsync(
            "test",
            character.Id,
            character.Gold,
            expectedResealMain,
            plannedEquipment);
        var committedReseal = preparedReseal is null
            ? null
            : await store.CommitItemAttributeModificationAsync(
                "test",
                character.Id,
                expectedResealMain,
                plannedEquipment,
                plannedResealMain,
                plannedEquipment);
        var resealReloadStore = new JsonGameStore(
            options,
            NullLogger<JsonGameStore>.Instance);
        await resealReloadStore.InitializeAsync();
        var resealReloaded = (await resealReloadStore.GetCharactersAsync("test"))[0];
        check(committedReseal?.Inventory?.SequenceEqual(plannedResealMain) == true
                && resealReloaded.Inventory?.SequenceEqual(plannedResealMain) == true
                && CharacterItemSealing.GetResealCount(
                    resealReloaded.Inventory![0]) == 1
                && (resealReloaded.Inventory[0].State
                    & CharacterItemSealing.ReinforcementMask) == 5
                && resealReloaded.Inventory[0].SealState
                    == CharacterItemSealing.Sealed,
            "CMD84 atomically persists reseal state, reseal count, reinforcement and exact wax consumption across reload");
    }

    private static void CreateFixture(string root)
    {
        var equipmentDirectory = Path.Combine(
            root,
            "equipment",
            "character",
            "test",
            "weapon");
        var cashDirectory = Path.Combine(root, "stackable", "cash");
        var etcDirectory = Path.Combine(root, "etc");
        Directory.CreateDirectory(equipmentDirectory);
        Directory.CreateDirectory(cashDirectory);
        Directory.CreateDirectory(etcDirectory);
        File.WriteAllText(
            Path.Combine(root, "equipment", "equipment.lst"),
            "1000 `character/test/weapon/test.equ`\n"
            + "1001 `character/test/title.equ`\n"
            + "1002 `character/test/weapon/sealing.equ`\n");
        File.WriteAllText(
            Path.Combine(equipmentDirectory, "test.equ"),
            "[name] `Test Weapon`\n"
            + "[equipment type] `weapon`\n"
            + "[attach type] `free`\n"
            + "[grade] 1\n"
            + "[rarity] 0\n"
            + "[minimum level] 1\n"
            + "[durability] 35\n");
        File.WriteAllText(
            Path.Combine(equipmentDirectory, "sealing.equ"),
            "[name] `Sealing Weapon`\n"
            + "[equipment type] `weapon`\n"
            + "[attach type] `[sealing]`\n"
            + "[grade] 35\n"
            + "[rarity] 2\n"
            + "[minimum level] 35\n"
            + "[durability] 35\n");
        File.WriteAllText(
            Path.Combine(root, "equipment", "character", "test", "title.equ"),
            "[name] `Test Title`\n"
            + "[equipment type] `[title name]` 1\n"
            + "[attach type] `[free]`\n"
            + "[grade] 1\n"
            + "[rarity] 0\n");
        File.WriteAllText(
            Path.Combine(root, "stackable", "stackable.lst"),
            "14 `cash/candle_goldwax.stk`\n"
            + "15 `cash/box_kaleido.stk`\n"
            + "16 `cash/not_kaleido.stk`\n");
        WriteStackable(
            Path.Combine(cashDirectory, "candle_goldwax.stk"),
            "Golden Wax");
        WriteStackable(
            Path.Combine(cashDirectory, "box_kaleido.stk"),
            "Kaleido Box");
        WriteStackable(
            Path.Combine(cashDirectory, "not_kaleido.stk"),
            "Other Box");
        File.WriteAllText(
            Path.Combine(etcDirectory, "reseal.etc"),
            "[grade]\n1\n2\n3\n5\n5\n[/grade]\n"
            + "[rarity weight]\n1\n2\n3\n4\n8\n[/rarity weight]\n"
            + "[extended rarity weight]\n0\n0\n1\n1\n1\n[/extended rarity weight]\n");
    }

    private static void WriteStackable(string path, string name) =>
        File.WriteAllText(
            path,
            $"[name] `{name}`\n"
            + "[stackable type] `[etc]` 0\n"
            + "[attach type] `trade`\n"
            + "[grade] 1\n"
            + "[rarity] 3\n"
            + "[stack limit] 100\n");

    private sealed class SequenceDropRandomSource(params int[] values)
        : IDropRandomSource
    {
        private readonly Queue<int> _values = new(values);

        public int Next(int exclusiveMaximum)
        {
            if (_values.Count == 0)
            {
                throw new InvalidOperationException(
                    "The fixed quality sequence was exhausted.");
            }

            var value = _values.Dequeue();
            if (value < 0 || value >= exclusiveMaximum)
            {
                throw new ArgumentOutOfRangeException(nameof(values));
            }

            return value;
        }
    }
}
