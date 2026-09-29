using DFLegacy.Protocol;
using DFLegacy.Server;
using Microsoft.Extensions.Logging.Abstractions;

internal static class EquipmentReinforcementSmokeTests
{
    public static void Run(Action<bool, string> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        CheckProtocolVectors(check);

        var fixtureRoot = Path.Combine(
            Path.GetTempPath(),
            $"dflegacy-reinforcement-{Guid.NewGuid():N}");
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
            var catalog = new EquipmentReinforcementCatalog(
                scripts,
                NullLogger<EquipmentReinforcementCatalog>.Instance);
            catalog.Initialize();

            check(catalog.IsAvailable
                    && catalog.RowCount == 4
                    && items.TryGetDefinition(1_000, out var equipment)
                    && catalog.TryGetCost(0, equipment, out var firstCost)
                    && firstCost == new EquipmentReinforcementCost(
                        3_171,
                        1,
                        100,
                        0,
                        0,
                        1),
                "reinforcement catalog reads material, gold and chance rows from upgrade.etc");
            CheckPlans(check, items, catalog);
            CheckInstanceMoves(check, items);
        }
        finally
        {
            if (Directory.Exists(fixtureRoot))
            {
                Directory.Delete(fixtureRoot, recursive: true);
            }
        }
    }

    private static void CheckInstanceMoves(Action<bool, string> check, ItemCatalog items)
    {
        var first = new CharacterItemRecord(9, 1_000, 1,
            State: 0x45, Durability: 27, SealState: 0,
            EquipmentQualitySeed: 12345, InstanceId: Guid.NewGuid());
        var second = first with { State = 9, InstanceId = Guid.NewGuid() };
        var bag = CharacterItemIdentity.MoveToSlot(first, 20, items);
        var worn = CharacterItemIdentity.MoveToSlot(second, 9, items);
        var restored = System.Text.Json.JsonSerializer.Deserialize<CharacterItemRecord[]>(
            System.Text.Json.JsonSerializer.Serialize(new[] { bag, worn }))!;
        var byId = restored.ToDictionary(item => item.InstanceId);
        check(byId[first.InstanceId] == first with { Slot = 20 }
                && byId[second.InstanceId] == second with { Slot = 9 },
            "same-template equipment keeps UUID-owned reinforcement and metadata across moves and JSON reload");
        var equippedAgain = CharacterItemIdentity.MoveToSlot(byId[first.InstanceId], 9, items);
        check(equippedAgain == first
                && (equippedAgain.State & CharacterItemSealing.ReinforcementMask) == 5
                && CharacterItemSealing.GetResealCount(equippedAgain) == 2,
            "unequip and re-equip preserve +5 reinforcement and two reseals on the same UUID");
        var legacy = CharacterItemIdentity.MoveToSlot(first with { InstanceId = Guid.Empty }, 21, items);
        check(legacy.InstanceId != Guid.Empty && legacy.State == first.State
                && CharacterItemIdentity.MoveToSlot(legacy, 9, items).InstanceId == legacy.InstanceId,
            "legacy equipment receives one stable UUID without losing reinforcement");
    }

    public static void CheckRealPvf(
        Action<bool, string> check,
        ScriptFileSystem scripts,
        ItemCatalog items)
    {
        var catalog = new EquipmentReinforcementCatalog(
            scripts,
            NullLogger<EquipmentReinforcementCatalog>.Instance);
        catalog.Initialize();
        check(catalog.IsAvailable
                && catalog.RowCount >= EquipmentReinforcementCatalog.MaximumLevel
                && items.TryGetDefinition(27_000, out var starterWeapon)
                && catalog.TryGetCost(0, starterWeapon, out var firstCost)
                && firstCost.MaterialItemId == 3_171
                && firstCost.MaterialCount == 1
                && firstCost.FailureWeight == 0
                && firstCost.Gold > 0
                && catalog.TryGetCost(4, starterWeapon, out var fifthCost)
                && fifthCost.MaterialItemId == 3_171
                && fifthCost.MaterialCount == 5
                && fifthCost.FailureWeight == 20_000
                && fifthCost.PenaltyType == 2,
            "real upgrade.etc preserves literal Cokes counts and the +4 to +5 downgrade row");
    }

    private static void CheckProtocolVectors(Action<bool, string> check)
    {
        var request = Convert.FromHexString("15000900686B4900");
        check(GameProtocolEngine.TryParseUpgradeItemCommand(request, out var command)
                && command == new GameUpgradeItemCommand(9, 27_496, 73)
                && !GameProtocolEngine.TryParseUpgradeItemCommand(
                    Convert.FromHexString("2101010003000FA4213D"),
                    out _),
            "CMD83 parses only the target-client reinforcement request layout");

        var success = GameProtocolEngine.CreateUpgradeItemReply(
            73,
            913,
            0,
            4,
            17);
        var downgrade = GameProtocolEngine.CreateUpgradeItemReply(
            73,
            909,
            2,
            2,
            17);
        var destroyed = GameProtocolEngine.CreateUpgradeItemReply(
            73,
            896,
            3,
            12,
            17);
        check(success.Type == GameProtocolEngine.CommandPacketType
                && success.ProtocolId == GameProtocolEngine.UpgradeItemCommand
                && success.Payload.SequenceEqual(
                    Convert.FromHexString("0149009103000000041100"))
                && downgrade.Payload.SequenceEqual(
                    Convert.FromHexString("0149008D03000002021100"))
                && destroyed.Payload.SequenceEqual(
                    Convert.FromHexString("01490080030000030C110000")),
            "CMD83 success, downgrade and destruction replies match the target-client vectors");
    }

    private static void CheckPlans(
        Action<bool, string> check,
        ItemCatalog items,
        EquipmentReinforcementCatalog catalog)
    {
        var sourceMain = CreateMainInventory(level: 0, materialCount: 10);
        check(EquipmentReinforcementPlanner.TryCreate(
                    sourceMain,
                    new Dictionary<ushort, CharacterItemRecord>(),
                    1_000,
                    requestedTargetSlot: 9,
                    expectedTargetItemId: 1_000,
                    materialSlot: 49,
                    items,
                    catalog,
                    new FixedDropRandomSource(99_999),
                    out var success,
                    out var successFailure)
                && successFailure == EquipmentReinforcementFailure.None
                && success.ResultCode == 0
                && success.NewLevel == 1
                && success.RemainingGold == 900
                && success.MaterialRemaining == 9
                && success.MainInventory[9].State == 1
                && success.MainInventory[49].CountOrValue == 9
                && sourceMain[9].State == 0
                && sourceMain[49].CountOrValue == 10,
            "reinforcement success plans atomically consume the literal PVF material count and preserve inputs");

        var downgradeMain = CreateMainInventory(level: 2, materialCount: 10);
        check(EquipmentReinforcementPlanner.TryCreate(
                    downgradeMain,
                    new Dictionary<ushort, CharacterItemRecord>(),
                    1_000,
                    9,
                    1_000,
                    49,
                    items,
                    catalog,
                    new FixedDropRandomSource(99_999),
                    out var downgrade,
                    out _)
                && downgrade.Failed
                && !downgrade.Destroyed
                && downgrade.ResultCode == 2
                && downgrade.PreviousLevel == 2
                && downgrade.NewLevel == 1
                && downgrade.MaterialRemaining == 7
                && downgrade.MainInventory[9].State == 1,
            "penalty type two consumes costs and lowers reinforcement by one level");

        var destroyMain = CreateMainInventory(level: 3, materialCount: 10);
        check(EquipmentReinforcementPlanner.TryCreate(
                    destroyMain,
                    new Dictionary<ushort, CharacterItemRecord>(),
                    1_000,
                    9,
                    1_000,
                    49,
                    items,
                    catalog,
                    new FixedDropRandomSource(99_999),
                    out var destroyed,
                    out _)
                && destroyed.Failed
                && destroyed.Destroyed
                && destroyed.ResultCode == 3
                && destroyed.NewLevel == 3
                && destroyed.MaterialRemaining == 6
                && !destroyed.MainInventory.ContainsKey(9),
            "penalty type three destroys the target while retaining its old level in the reply");

        var equipped = new Dictionary<ushort, CharacterItemRecord>
        {
            [16] = CreateEquipment(slot: 16, level: 0)
        };
        var materialOnly = new Dictionary<ushort, CharacterItemRecord>
        {
            [49] = new(49, 3_171, 10)
        };
        check(EquipmentReinforcementPlanner.TryCreate(
                    materialOnly,
                    equipped,
                    1_000,
                    requestedTargetSlot: 7,
                    expectedTargetItemId: 1_000,
                    materialSlot: 49,
                    items,
                    catalog,
                    new FixedDropRandomSource(99_999),
                    out var equippedPlan,
                    out _)
                && equippedPlan.TargetListType == 3
                && equippedPlan.TargetSlot == 16
                && equippedPlan.Equipment[16].State == 1,
            "CMD83 maps compact worn-accessory slots to the persisted equipment slots");

        check(!EquipmentReinforcementPlanner.TryCreate(
                    CreateMainInventory(level: 0, materialCount: 0),
                    new Dictionary<ushort, CharacterItemRecord>(),
                    1_000,
                    9,
                    1_000,
                    49,
                    items,
                    catalog,
                    new FixedDropRandomSource(0),
                    out _,
                    out var materialFailure)
                && materialFailure == EquipmentReinforcementFailure.InsufficientMaterial,
            "reinforcement rejects a missing PVF material with client error 22");
        check(!EquipmentReinforcementPlanner.TryCreate(
                    CreateMainInventory(level: 0, materialCount: 10),
                    new Dictionary<ushort, CharacterItemRecord>(),
                    99,
                    9,
                    1_000,
                    49,
                    items,
                    catalog,
                    new FixedDropRandomSource(0),
                    out _,
                    out var goldFailure)
                && goldFailure == EquipmentReinforcementFailure.InsufficientGold,
            "reinforcement rejects insufficient gold with client error 10");
        check(!EquipmentReinforcementPlanner.TryCreate(
                    CreateMainInventory(level: EquipmentReinforcementCatalog.MaximumLevel,
                        materialCount: 10),
                    new Dictionary<ushort, CharacterItemRecord>(),
                    1_000,
                    9,
                    1_000,
                    49,
                    items,
                    catalog,
                    new FixedDropRandomSource(0),
                    out _,
                    out var maximumFailure)
                && maximumFailure == EquipmentReinforcementFailure.RuleUnavailable,
            "reinforcement rejects level 31 and missing table rows with client error 13");
    }

    private static Dictionary<ushort, CharacterItemRecord> CreateMainInventory(
        byte level,
        uint materialCount)
    {
        var inventory = new Dictionary<ushort, CharacterItemRecord>
        {
            [9] = CreateEquipment(slot: 9, level)
        };
        if (materialCount != 0)
        {
            inventory[49] = new CharacterItemRecord(49, 3_171, materialCount);
        }

        return inventory;
    }

    private static CharacterItemRecord CreateEquipment(ushort slot, byte level) =>
        new(
            slot,
            1_000,
            1,
            State: level,
            Durability: 35);

    private static void CreateFixture(string root)
    {
        var equipmentDirectory = Path.Combine(
            root,
            "equipment",
            "character",
            "test",
            "weapon");
        var stackableDirectory = Path.Combine(root, "stackable", "material");
        var etcDirectory = Path.Combine(root, "etc");
        Directory.CreateDirectory(equipmentDirectory);
        Directory.CreateDirectory(stackableDirectory);
        Directory.CreateDirectory(etcDirectory);
        File.WriteAllText(
            Path.Combine(root, "equipment", "equipment.lst"),
            "1000 `character/test/weapon/test.equ`\n");
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
            Path.Combine(root, "stackable", "stackable.lst"),
            "3171 `material/cokes.stk`\n");
        File.WriteAllText(
            Path.Combine(stackableDirectory, "cokes.stk"),
            "[name] `Cokes`\n"
            + "[stackable type] `material`\n"
            + "[attach type] `free`\n"
            + "[stack limit] 1000\n");
        File.WriteAllText(
            Path.Combine(etcDirectory, "upgrade.etc"),
            "[table]\n"
            + "0 0 0 0 0 0 0 0 3171 1\n"
            + "0 0 0 0 0 100000 1 0 3171 2\n"
            + "0 0 0 0 0 100000 2 0 3171 3\n"
            + "0 0 0 0 0 100000 3 0 3171 4\n"
            + "[cost]\n"
            + "0\n"
            + "100\n"
            + "200\n"
            + "300\n"
            + "400\n"
            + "[cost weights by rarity]\n"
            + "1\n1\n1\n1\n1\n"
            + "[type]\n"
            + "1\n1\n1\n1\n1\n1\n1\n1\n1\n1\n");
    }

    private sealed class FixedDropRandomSource(int value) : IDropRandomSource
    {
        public int Next(int exclusiveMaximum)
        {
            if (value < 0 || value >= exclusiveMaximum)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            return value;
        }
    }
}
