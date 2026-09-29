using System.Text;
using DFLegacy.Protocol;
using DFLegacy.Server;
using Microsoft.Extensions.Logging.Abstractions;

internal static class AvatarCompoundSmokeTests
{
    private static readonly string[] CompoundPartNames =
    [
        "hat", "hair", "face", "coat", "pants", "shoes", "neck", "belt"
    ];

    public static void Run(Action<bool, string> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        CheckProtocolVectors(check);

        var fixtureRoot = Path.Combine(
            Path.GetTempPath(),
            $"dflegacy-avatar-compound-{Guid.NewGuid():N}");
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
            var catalog = new AvatarCompoundCatalog(
                scripts,
                items,
                NullLogger<AvatarCompoundCatalog>.Instance);
            catalog.Initialize();

            CheckCatalog(check, catalog);
            CheckPlans(check, catalog);
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
        var catalog = new AvatarCompoundCatalog(
            scripts,
            items,
            NullLogger<AvatarCompoundCatalog>.Instance);
        catalog.Initialize();

        var validDefinitions = catalog.Count == 5;
        var validPools = true;
        for (byte job = 0; job < 5; job++)
        {
            validPools &= catalog.TryGetDefinition(job, out var definition)
                && definition.MaterialItemId == 21
                && definition.MaterialCount == 1
                && definition.Grade == 1
                && definition.UpperGrade == 2
                && definition.RareRates.Count == AvatarCompoundCatalog.CompoundPartCount
                && definition.UpperRareRates.Count
                    == AvatarCompoundCatalog.CompoundPartCount
                && definition.RareItems.Count == AvatarCompoundCatalog.CompoundPartCount
                && definition.NormalItems.Count == AvatarCompoundCatalog.CompoundPartCount;
            if (!validPools)
            {
                break;
            }

            for (byte part = 0; part < AvatarCompoundCatalog.CompoundPartCount; part++)
            {
                validPools &= catalog.TrySelectResult(
                        job,
                        part,
                        1,
                        1,
                        new SequenceDropRandomSource(0, 0),
                        out var rare)
                    && rare.IsRare
                    && rare.RareRate > 0
                    && catalog.TryGetAvatarMetadata(rare.ItemId, out var rareMetadata)
                    && rareMetadata.Job == job
                    && rareMetadata.Part == part
                    && rareMetadata.OptionCount > 0
                    && catalog.TrySelectResult(
                        job,
                        part,
                        1,
                        1,
                        new SequenceDropRandomSource(9_999, 0),
                        out var normal)
                    && !normal.IsRare
                    && normal.RareRate > 0
                    && normal.ItemId != rare.ItemId
                    && catalog.TryGetAvatarMetadata(normal.ItemId, out var normalMetadata)
                    && normalMetadata.Job == job
                    && normalMetadata.Part == part
                    && normalMetadata.OptionCount > 0;
                if (!validPools)
                {
                    break;
                }
            }
        }

        check(validDefinitions && validPools,
            "real compoundavatar.etc exposes five complete job tables with valid weighted avatar results");
        check(catalog.TryGetDefinition(1, out var fighter)
                && fighter.RareRates[3] == 1_000
                && fighter.UpperRareRates[3] == 300
                && catalog.TrySelectResult(
                    1,
                    3,
                    2,
                    2,
                    new SequenceDropRandomSource(0, 0),
                    out var upperRare)
                && upperRare == new AvatarCompoundSelection(44_739, 300, true)
                && catalog.TryGetAvatarMetadata(44_739, out var coatMetadata)
                && coatMetadata == new AvatarCompoundItemMetadata(1, 3, 2, 36)
                && catalog.TryGetAvatarMetadata(52_690, out var mageCoatMetadata)
                && mageCoatMetadata.OptionCount == 44
                && catalog.TryGetAvatarMetadata(51_846, out var mageFaceMetadata)
                && mageFaceMetadata.OptionCount == 4,
            "real fighter and mage avatar grades, rates, rare pieces and selectable option rows match DF2008");
    }

    private static void CheckProtocolVectors(Action<bool, string> check)
    {
        check(GameProtocolEngine.TryParseCompoundAvatarCommand(
                    Convert.FromHexString("1500090000003412000078560000250506"),
                    out var synthetic)
                && synthetic == new GameCompoundAvatarCommand(
                    9,
                    0x1234,
                    0x5678,
                    0x25,
                    5,
                    6)
                && GameProtocolEngine.TryParseCompoundAvatarCommand(
                    Convert.FromHexString("11000300010024DD000024DD0100040001"),
                    out var fighter)
                && fighter == new GameCompoundAvatarCommand(
                    3,
                    56_612,
                    56_612,
                    4,
                    0,
                    1)
                && !GameProtocolEngine.TryParseCompoundAvatarCommand(
                    Convert.FromHexString("9300030001004CAE00004CAE010018000100"),
                    out _),
            "CMD102 parses the target-client fixed 17-byte request and rejects trailing bytes");

        var reply = GameProtocolEngine.CreateCompoundAvatarReply(
        [
            new GameAvatarCompoundConsumption(0, 3, 0),
            new GameAvatarCompoundConsumption(1, 0, 0),
            new GameAvatarCompoundConsumption(1, 1, 0)
        ],
        resultSlot: 0,
        resultItemId: 44_739,
        remainingSeconds: 0,
        selectedOption: 24);
        var failure = GameProtocolEngine.CreateCompoundAvatarFailure();
        check(reply.Type == GameProtocolEngine.CommandPacketType
                && reply.ProtocolId == GameProtocolEngine.CompoundAvatarCommand
                && reply.Payload.SequenceEqual(Convert.FromHexString(
                    "01030003000000000001000000000000010100000000000000C3AE000000001800"))
                && failure.Type == GameProtocolEngine.CommandPacketType
                && failure.ProtocolId == GameProtocolEngine.CompoundAvatarCommand
                && failure.Payload.SequenceEqual(Convert.FromHexString("0004")),
            "CMD102 success and failure replies match the 2008DF target-client decoder layout");
    }

    private static void CheckCatalog(
        Action<bool, string> check,
        AvatarCompoundCatalog catalog)
    {
        check(catalog.Count == 1
                && catalog.TryGetDefinition(1, out var definition)
                && definition.MaterialItemId == 21
                && definition.MaterialCount == 1
                && definition.Grade == 1
                && definition.UpperGrade == 2
                && definition.RareRates[3] == 1_000
                && definition.UpperRareRates[3] == 300
                && definition.RareItems.Count == 8
                && definition.NormalItems.Count == 8,
            "avatar compound catalog reads job, material, grades, rates and all eight part pools");

        check(catalog.TrySelectResult(
                    1,
                    3,
                    1,
                    1,
                    new SequenceDropRandomSource(0, 0),
                    out var rare)
                && rare == new AvatarCompoundSelection(1_100, 1_000, true)
                && catalog.TrySelectResult(
                    1,
                    3,
                    1,
                    1,
                    new SequenceDropRandomSource(9_999, 0),
                    out var normal)
                && normal == new AvatarCompoundSelection(1_200, 1_000, false)
                && catalog.TrySelectResult(
                    1,
                    3,
                    2,
                    2,
                    new SequenceDropRandomSource(0, 0),
                    out var upperRare)
                && upperRare == new AvatarCompoundSelection(1_100, 300, true)
                && catalog.TryGetAvatarMetadata(1_100, out var metadata)
                && metadata == new AvatarCompoundItemMetadata(1, 3, 2, 4),
            "avatar compound uses separate base and upper rare rates and reads result option rows");
    }

    private static void CheckPlans(
        Action<bool, string> check,
        AvatarCompoundCatalog catalog)
    {
        var sourceMain = new Dictionary<ushort, CharacterItemRecord>
        {
            [3] = new(3, 21, 2)
        };
        var sourceAvatars = new Dictionary<ushort, CharacterItemRecord>
        {
            [0] = CreateAvatar(0, 1_000),
            [1] = CreateAvatar(1, 1_001)
        };
        check(AvatarCompoundPlanner.TryCreate(
                    sourceMain,
                    sourceAvatars,
                    characterJob: 1,
                    materialSlot: 3,
                    firstAvatarItemId: 1_000,
                    secondAvatarItemId: 1_001,
                    selectedOption: 3,
                    firstAvatarSlot: 0,
                    secondAvatarSlot: 1,
                    catalog,
                    new SequenceDropRandomSource(0, 0),
                    out var rarePlan,
                    out var rareFailure)
                && rareFailure == AvatarCompoundFailure.None
                && rarePlan.IsRare
                && rarePlan.RareRate == 1_000
                && rarePlan.MaterialRemaining == 1
                && rarePlan.MainInventory[3].CountOrValue == 1
                && rarePlan.ResultItemId == 1_100
                && rarePlan.ResolvedFirstSlot == 0
                && rarePlan.ResolvedSecondSlot == 1
                && rarePlan.AvatarInventory.Count == 1
                && rarePlan.AvatarInventory[0] is
                {
                    ItemId: 1_100,
                    CountOrValue: 1,
                    State: 0,
                    Durability: 0,
                    SealState: 0,
                    AvatarRemainingSeconds: 0,
                    AvatarAbilityIndex: 3
                }
                && sourceMain[3].CountOrValue == 2
                && sourceAvatars.Count == 2,
            "avatar compound plan atomically consumes one cube and two avatars into a permanent selected-option result");

        check(AvatarCompoundPlanner.TryCreate(
                    sourceMain,
                    sourceAvatars,
                    1,
                    3,
                    firstAvatarItemId: 1_001,
                    secondAvatarItemId: 1_000,
                    selectedOption: 0,
                    firstAvatarSlot: 0,
                    secondAvatarSlot: 1,
                    catalog,
                    new SequenceDropRandomSource(9_999, 0),
                    out var crossedPlan,
                    out _)
                && !crossedPlan.IsRare
                && crossedPlan.ResultItemId == 1_200
                && crossedPlan.ResolvedFirstSlot == 0,
            "avatar compound accepts the target client's crossed visual item order and can select the normal pool");

        var upperAvatars = new Dictionary<ushort, CharacterItemRecord>
        {
            [0] = CreateAvatar(0, 1_002),
            [1] = CreateAvatar(1, 1_003)
        };
        check(AvatarCompoundPlanner.TryCreate(
                    sourceMain,
                    upperAvatars,
                    1,
                    3,
                    1_002,
                    1_003,
                    0,
                    0,
                    1,
                    catalog,
                    new SequenceDropRandomSource(0, 0),
                    out var upperPlan,
                    out _)
                && upperPlan.IsRare
                && upperPlan.RareRate == 300,
            "upper-grade source avatars use the PVF upper rare rate instead of the base rate");

        var staleVisualAvatars = new Dictionary<ushort, CharacterItemRecord>
        {
            [10] = CreateAvatar(10, 1_000),
            [11] = CreateAvatar(11, 1_001)
        };
        check(AvatarCompoundPlanner.TryCreate(
                    sourceMain,
                    staleVisualAvatars,
                    1,
                    3,
                    1_000,
                    1_001,
                    0,
                    0,
                    1,
                    catalog,
                    new SequenceDropRandomSource(0, 0),
                    out var recoveredPlan,
                    out _)
                && recoveredPlan.RequestedFirstSlot == 0
                && recoveredPlan.RequestedSecondSlot == 1
                && recoveredPlan.ResolvedFirstSlot == 10
                && recoveredPlan.ResolvedSecondSlot == 11
                && recoveredPlan.AvatarInventory.ContainsKey(10)
                && !recoveredPlan.AvatarInventory.ContainsKey(11),
            "avatar compound recovers stale visual slots by matching two distinct persisted avatar instances");

        check(!AvatarCompoundPlanner.TryCreate(
                    sourceMain,
                    sourceAvatars,
                    1,
                    3,
                    1_000,
                    1_001,
                    selectedOption: 4,
                    firstAvatarSlot: 0,
                    secondAvatarSlot: 1,
                    catalog,
                    new SequenceDropRandomSource(0, 0),
                    out _,
                    out var optionFailure)
                && optionFailure == AvatarCompoundFailure.OptionOutOfRange
                && sourceMain[3].CountOrValue == 2
                && sourceAvatars.Count == 2,
            "avatar compound rejects an option row outside the result avatar without mutating inputs");

        var wrongPart = new Dictionary<ushort, CharacterItemRecord>
        {
            [0] = CreateAvatar(0, 1_000),
            [1] = CreateAvatar(1, 1_300)
        };
        check(!AvatarCompoundPlanner.TryCreate(
                    sourceMain,
                    wrongPart,
                    1,
                    3,
                    1_000,
                    1_300,
                    0,
                    0,
                    1,
                    catalog,
                    new SequenceDropRandomSource(0, 0),
                    out _,
                    out var partFailure)
                && partFailure == AvatarCompoundFailure.PartMismatch,
            "avatar compound rejects source avatars from different parts");

        var wrongJob = new Dictionary<ushort, CharacterItemRecord>
        {
            [0] = CreateAvatar(0, 1_400),
            [1] = CreateAvatar(1, 1_400)
        };
        check(!AvatarCompoundPlanner.TryCreate(
                    sourceMain,
                    wrongJob,
                    1,
                    3,
                    1_400,
                    1_400,
                    0,
                    0,
                    1,
                    catalog,
                    new SequenceDropRandomSource(0, 0),
                    out _,
                    out var jobFailure)
                && jobFailure == AvatarCompoundFailure.JobMismatch,
            "avatar compound rejects avatars belonging to another character job");
    }

    private static CharacterItemRecord CreateAvatar(ushort slot, ushort itemId) =>
        new(
            slot,
            itemId,
            1,
            AvatarRemainingSeconds: 0,
            AvatarAbilityIndex: 0);

    private static void CreateFixture(string root)
    {
        var equipmentEntries = new (ushort Id, string RelativePath, string TypeTag, int Grade)[]
        {
            (1_000, "character/fighter/avatar/coat/source_a.equ", "coat avatar", 1),
            (1_001, "character/fighter/avatar/coat/source_b.equ", "coat avatar", 1),
            (1_002, "character/fighter/avatar/coat/upper_a.equ", "coat avatar", 2),
            (1_003, "character/fighter/avatar/coat/upper_b.equ", "coat avatar", 2),
            (1_100, "character/fighter/avatar/coat/rare.equ", "coat avatar", 2),
            (1_200, "character/fighter/avatar/coat/normal.equ", "coat avatar", 1),
            (1_300, "character/fighter/avatar/cap/wrong_part.equ", "hat avatar", 1),
            (1_400, "character/swordman/avatar/coat/wrong_job.equ", "coat avatar", 1)
        };
        Directory.CreateDirectory(Path.Combine(root, "equipment"));
        File.WriteAllText(
            Path.Combine(root, "equipment", "equipment.lst"),
            string.Join(
                '\n',
                equipmentEntries.Select(entry =>
                    $"{entry.Id} `{entry.RelativePath}`")) + "\n");
        foreach (var entry in equipmentEntries)
        {
            var path = Path.Combine(
                root,
                "equipment",
                entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(
                path,
                $"[name] `Avatar {entry.Id}`\n"
                + $"[equipment type] `[{entry.TypeTag}]`\n"
                + "[attach type] `[free]`\n"
                + $"[grade] {entry.Grade}\n"
                + "[avatar select ability]\n"
                + "`[option one]` 1\n"
                + "`[option two]` 2\n"
                + "`[option three]` 3\n"
                + "`[option four]` 4\n"
                + "[/avatar select ability]\n");
        }

        var materialDirectory = Path.Combine(root, "stackable", "material");
        Directory.CreateDirectory(materialDirectory);
        File.WriteAllText(
            Path.Combine(root, "stackable", "stackable.lst"),
            "21 `material/avatar_cube.stk`\n");
        File.WriteAllText(
            Path.Combine(materialDirectory, "avatar_cube.stk"),
            "[name] `Avatar Cube`\n"
            + "[stackable type] `[material]`\n"
            + "[attach type] `[free]`\n"
            + "[stack limit] 1000\n");

        var compoundDirectory = Path.Combine(root, "etc", "compoundavatar");
        Directory.CreateDirectory(compoundDirectory);
        File.WriteAllText(
            Path.Combine(root, "etc", "compoundavatar.etc"),
            "2 `compoundavatar/fighter.etc`\n");
        var definition = new StringBuilder()
            .AppendLine("[grade] 1")
            .AppendLine("[upper grade] 2")
            .AppendLine("[rare rate]");
        foreach (var part in CompoundPartNames)
        {
            definition.AppendLine($"`{part}` 1000");
        }

        definition.AppendLine("[/rare rate]")
            .AppendLine("[upper rare rate]");
        foreach (var part in CompoundPartNames)
        {
            definition.AppendLine($"`{part}` 300");
        }

        definition.AppendLine("[/upper rare rate]")
            .AppendLine("[material]")
            .AppendLine("1 21 1")
            .AppendLine("[/material]");
        foreach (var part in CompoundPartNames)
        {
            definition.AppendLine($"[{part} avatar]")
                .AppendLine("1")
                .AppendLine("1100 10")
                .AppendLine("1200 10")
                .AppendLine($"[/{part} avatar]");
        }

        File.WriteAllText(
            Path.Combine(compoundDirectory, "fighter.etc"),
            definition.ToString());
    }

    private sealed class SequenceDropRandomSource(params int[] values) : IDropRandomSource
    {
        private readonly Queue<int> _values = new(values);

        public int Next(int exclusiveMaximum)
        {
            if (_values.Count == 0)
            {
                throw new InvalidOperationException("The fixed random sequence was exhausted.");
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
