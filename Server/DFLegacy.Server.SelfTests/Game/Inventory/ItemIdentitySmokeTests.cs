using System.Text.Json;
using DFLegacy.Server;
using Microsoft.Extensions.Logging.Abstractions;

internal static class ItemIdentitySmokeTests
{
    public static async Task RunAsync(
        ItemCatalog catalog,
        Action<bool, string> check)
    {
        check(catalog.TryGetDefinition(10_000, out var equipmentDefinition)
            && CharacterItemIdentity.RequiresInstanceId(equipmentDefinition)
            && catalog.TryGetDefinition(39_000, out var avatarDefinition)
            && CharacterItemIdentity.RequiresInstanceId(avatarDefinition)
            && catalog.TryGetDefinition(50_000, out var creatureDefinition)
            && CharacterItemIdentity.RequiresInstanceId(creatureDefinition)
            && catalog.TryGetDefinition(50_001, out var artifactDefinition)
            && CharacterItemIdentity.RequiresInstanceId(artifactDefinition)
            && catalog.TryGetDefinition(3_176, out var stackableDefinition)
            && !CharacterItemIdentity.RequiresInstanceId(stackableDefinition),
            "only non-stack equipment definitions require server instance ids");

        var preservedId = CharacterItemIdentity.CreateInstanceId();
        check(CharacterInventoryPlanner.TryPlace(
                new Dictionary<ushort, CharacterItemRecord>(),
                new CharacterMailAttachmentRecord(
                    10_000,
                    2,
                    Durability: 1_000,
                    InstanceId: preservedId),
                catalog,
                uint.MaxValue,
                out var equipmentPlacement,
                out _)
            && equipmentPlacement.Inventory.Count == 2
            && equipmentPlacement.Inventory.Values.Any(item =>
                item.InstanceId == preservedId)
            && equipmentPlacement.Inventory.Values.All(item =>
                item.InstanceId != Guid.Empty)
            && equipmentPlacement.Inventory.Values
                .Select(item => item.InstanceId)
                .Distinct()
                .Count() == 2,
            "non-stack equipment allocation preserves the first identity and creates one unique id per additional instance");

        check(CharacterInventoryPlanner.TryPlace(
                new Dictionary<ushort, CharacterItemRecord>(),
                new CharacterMailAttachmentRecord(
                    3_176,
                    2,
                    InstanceId: CharacterItemIdentity.CreateInstanceId()),
                catalog,
                uint.MaxValue,
                out var stackablePlacement,
                out _)
            && stackablePlacement.Inventory.Values.All(item =>
                item.InstanceId == Guid.Empty),
            "stackable items never inherit equipment instance ids");

        check(CharacterAvatarInventoryPlanner.TryPlace(
                new Dictionary<ushort, CharacterItemRecord>(),
                new CharacterMailAttachmentRecord(39_000, 2),
                catalog,
                out var avatarPlacement,
                out _)
            && avatarPlacement.Inventory.Count == 2
            && avatarPlacement.Inventory.Values.All(item =>
                item.InstanceId != Guid.Empty)
            && avatarPlacement.Inventory.Values
                .Select(item => item.InstanceId)
                .Distinct()
                .Count() == 2,
            "Avatar allocation creates a distinct identity for every non-stack instance");

        check(CharacterCreatureInventoryPlanner.TryPlace(
                new Dictionary<ushort, CharacterItemRecord>(),
                new CharacterItemRecord(ushort.MaxValue, 50_001, 1),
                catalog,
                preferredEmptySlot: null,
                out var artifactPlacement,
                out _)
            && artifactPlacement.Inventory.Values.Single().InstanceId != Guid.Empty,
            "Creature artifacts receive equipment instance ids");

        var mailAttachments = CharacterInventoryPlanner.CreateMailAttachments(
            new CharacterMailAttachmentRecord(10_000, 3, Durability: 1_000),
            catalog);
        check(mailAttachments.Count == 3
            && mailAttachments.All(item => item.InstanceId != Guid.Empty)
            && mailAttachments.Select(item => item.InstanceId).Distinct().Count() == 3,
            "mail splitting creates one unique identity per equipment attachment");

        var groundState = new DungeonGroundItemState();
        var groundItem = groundState.Add(
            new DungeonGeneratedDrop(false, 10_000, 1),
            ownerUserId: 1,
            sourceMonsterId: 2,
            roomX: 3,
            roomY: 4,
            catalog: catalog);
        check(groundItem.PreservedItem is
            {
                InstanceId: var groundInstanceId
            }
            && groundInstanceId != Guid.Empty
            && DungeonPickupPlanner.TryPlanItem(
                new Dictionary<ushort, CharacterItemRecord>(),
                groundItem,
                catalog,
                uint.MaxValue,
                out var pickupPlan,
                out _)
            && pickupPlan.Inventory.Values.Single().InstanceId == groundInstanceId,
            "generated equipment keeps the same identity from ground creation through pickup");

        await CheckLegacyMigrationAsync(catalog, check);
    }

    private static async Task CheckLegacyMigrationAsync(
        ItemCatalog catalog,
        Action<bool, string> check)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"dflegacy-item-id-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var statePath = Path.Combine(root, "state.json");
            var accountUid = AccountUidRules.NormalUidStart;
            var characterId = Guid.NewGuid();
            var duplicateId = CharacterItemIdentity.CreateInstanceId();
            var character = new CharacterRecord(
                characterId,
                "IdentityTest",
                Job: 0,
                GrowType: 2,
                Level: 1,
                Sp: 0,
                Cash: 0,
                Inventory:
                [
                    new CharacterItemRecord(9, 10_000, 1, InstanceId: duplicateId),
                    new CharacterItemRecord(73, 3_176, 2)
                ],
                Equipment:
                [
                    new CharacterItemRecord(2, 12_007, 1)
                ],
                Warehouse:
                [
                    new CharacterItemRecord(0, 10_000, 1, InstanceId: duplicateId)
                ],
                Mailbox:
                [
                    new CharacterMailRecord(
                        1,
                        "DFLegacy",
                        "identity",
                        0,
                        new CharacterMailAttachmentRecord(10_000, 1),
                        SentAt: 1,
                        ExpiresAt: 0)
                ],
                AvatarInventory:
                [
                    new CharacterItemRecord(0, 39_000, 1)
                ],
                CreatureInventory:
                [
                    new CharacterItemRecord(0, 50_000, 77)
                ],
                CharacterNo: 1,
                AccountUid: accountUid);
            var account = new AccountRecord(
                Guid.NewGuid(),
                "identity-test",
                "unused",
                [character],
                accountUid);
            await File.WriteAllTextAsync(
                statePath,
                JsonSerializer.Serialize(new
                {
                    Accounts = new[] { account },
                    NextCharacterNo = 2u,
                    NextNormalAccountUid = accountUid + 1,
                    NextTestAccountUid = 1u,
                    FatigueDayKey = 0
                }));

            var options = new ServerOptions { DataPath = statePath };
            var store = new JsonGameStore(
                options,
                NullLogger<JsonGameStore>.Instance,
                itemCatalog: catalog);
            await store.InitializeAsync();
            var migrated = (await store.GetCharactersAsync("identity-test")).Single();
            var migratedIds = CollectEquipmentIds(migrated, catalog);
            check(migratedIds.Count == 6
                && migratedIds.All(instanceId => instanceId != Guid.Empty)
                && migratedIds.Distinct().Count() == migratedIds.Count
                && migrated.Inventory!.Single(item => item.ItemId == 3_176).InstanceId
                    == Guid.Empty,
                "startup migration assigns unique ids across inventory, equipment, warehouse, Avatar, Creature and mail while leaving stacks unchanged");

            var reloadedStore = new JsonGameStore(
                options,
                NullLogger<JsonGameStore>.Instance,
                itemCatalog: catalog);
            await reloadedStore.InitializeAsync();
            var reloaded = (await reloadedStore.GetCharactersAsync("identity-test")).Single();
            check(CollectEquipmentIds(reloaded, catalog).SequenceEqual(migratedIds),
                "equipment instance ids remain stable after persistence and reload");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static List<Guid> CollectEquipmentIds(
        CharacterRecord character,
        ItemCatalog catalog)
    {
        return (character.Inventory ?? [])
            .Concat(character.Equipment ?? [])
            .Concat(character.Warehouse ?? [])
            .Concat(character.AvatarInventory ?? [])
            .Concat(character.CreatureInventory ?? [])
            .Where(item => catalog.TryGetDefinition(item.ItemId, out var definition)
                && CharacterItemIdentity.RequiresInstanceId(definition))
            .Select(item => item.InstanceId)
            .Concat((character.Mailbox ?? [])
                .Where(mail => mail.Attachment is { } attachment
                    && catalog.TryGetDefinition(attachment.ItemId, out var definition)
                    && CharacterItemIdentity.RequiresInstanceId(definition))
                .Select(mail => mail.Attachment!.InstanceId))
            .ToList();
    }
}
