using System.Buffers.Binary;
using System.Text.Json;
using DAFLegacy.Protocol;
using DAFLegacy.Server;
using Microsoft.Extensions.Logging.Abstractions;

internal static class CeraShopAvatarPurchaseSmokeTests
{
    public static void Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), $"daflegacy-cera-avatar-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "equipment"));
            Directory.CreateDirectory(Path.Combine(root, "stackable"));
            File.WriteAllText(Path.Combine(root, "equipment", "equipment.lst"),
                "39000 `avatar.equ`\n39001 `no-options.equ`\n");
            File.WriteAllText(Path.Combine(root, "equipment", "avatar.equ"),
                "[name] `selectable avatar`\n[equipment type] `[coat avatar]`\n"
                + "[attach type] `[free]`\n[cash] 450\n"
                + "[avatar select ability]\n"
                + "// `[SKILL_LEVEL]` `[gunner]` 99 1\n"
                + "`[SKILL_LEVEL]` `[gunner]` 47 1\n"
                + "`[SKILL_LEVEL]` `[gunner]` 14 1\n"
                + "`[SKILL_LEVEL]` `[gunner]` 33 1\n"
                + "`[SKILL_LEVEL]` `[gunner]` 37 1\n"
                + "`[SKILL_LEVEL]` `[gunner]` 4 1 // fifth option\n"
                + "[/avatar select ability]\n");
            File.WriteAllText(Path.Combine(root, "equipment", "no-options.equ"),
                "[equipment type] `[coat avatar]`\n[cash] 450\n");
            File.WriteAllText(Path.Combine(root, "stackable", "stackable.lst"), "8 `item.stk`\n");
            File.WriteAllText(Path.Combine(root, "stackable", "item.stk"),
                "[name] `ordinary item`\n[stackable type] `[material]`\n");
            var scripts = new ScriptFileSystem(
                new ServerOptions { ScriptPvfPath = string.Empty, SkillScriptPath = root },
                NullLogger<ScriptFileSystem>.Instance);
            var items = new ItemCatalog(scripts, NullLogger<ItemCatalog>.Instance);
            items.Initialize();
            check(items.TryGetDefinition(39_000, out var definition)
                    && definition.AvatarSelectableAbilityCount == 5,
                "avatar purchase caches selectable rows without counting job tokens or comments as extra options");

            var product = new CeraShopProduct(240_092, 39_000, 1, 3, 2_970, "avatar");
            var allChoicesPreserved = true;
            for (ushort period = 1; period <= 3; period++)
            {
                for (byte ability = 0; ability < 5; ability++)
                {
                    if (!CeraShopPurchaseItemPlanner.TryCreate(
                            product with { AttributeValue = period }, ability, items, out var attachment)
                        || !CharacterAvatarInventoryPlanner.TryPlace(
                            new Dictionary<ushort, CharacterItemRecord>(), attachment, items, 23,
                            out var placement, out _))
                    {
                        allChoicesPreserved = false;
                        continue;
                    }

                    var worn = CharacterItemIdentity.MoveToSlot(placement.Inventory[23], 3, items);
                    var reloaded = JsonSerializer.Deserialize<CharacterItemRecord>(
                        JsonSerializer.Serialize(worn))!;
                    var returned = CharacterItemIdentity.MoveToSlot(reloaded, 23, items);
                    var normalized = CharacterAvatarInventoryPlanner.Normalize([returned], items);
                    var wire = CharacterItemWireProjection.Project(normalized.Inventory[23], items);
                    var packet = InventoryPacketBuilders.CreateItemList(1,
                        [new GameInventoryEntry(23, 39_000, wire.AddInfo,
                            wire.ItemAttr, wire.Durability, wire.SealState)]);
                    allChoicesPreserved &= attachment.AvatarAbilityIndex == ability
                        && attachment.AvatarRemainingSeconds == 0
                        && attachment.InstanceId != Guid.Empty
                        && returned.InstanceId == attachment.InstanceId
                        && returned.AvatarAbilityIndex == ability
                        && packet.ProtocolId == 13 && packet.Payload.Length == 15
                        && BinaryPrimitives.ReadUInt32LittleEndian(packet.Payload.AsSpan(7, 4)) == 0
                        && BinaryPrimitives.ReadUInt16LittleEndian(packet.Payload.AsSpan(12, 2)) == ability;
                }
            }
            check(allChoicesPreserved,
                "every Cera avatar option remains separate from period codes across purchase, equip, JSON reload and NOTI 13");
            check(!CeraShopPurchaseItemPlanner.TryCreate(product, 5, items, out _)
                    && !CeraShopPurchaseItemPlanner.TryCreate(product, 254, items, out _)
                    && !CeraShopPurchaseItemPlanner.TryCreate(product, 255, items, out _)
                    && !CeraShopPurchaseItemPlanner.TryCreate(
                        product with { ItemId = 39_001 }, 0, items, out _),
                "avatar purchase rejects invalid or missing selectable rows instead of silently choosing the first option");
            check(CeraShopPurchaseItemPlanner.TryCreate(
                        new CeraShopProduct(100_008, 8, 10, 0, 100, "item"), 255, items, out var ordinary)
                    && ordinary.AvatarAbilityIndex is null && ordinary.AvatarRemainingSeconds is null
                    && ordinary.CountOrValue == 10,
                "non-avatar Cera purchases retain the ignored option sentinel and ordinary inventory metadata");

            _ = CeraShopPurchaseItemPlanner.TryCreate(product, 4, items, out var overflow);
            var full = Enumerable.Range(0, CharacterAvatarInventoryLayout.Capacity)
                .ToDictionary(slot => (ushort)slot, slot => new CharacterItemRecord((ushort)slot, 39_000, 1));
            var overflowRejected = !CharacterAvatarInventoryPlanner.TryPlace(
                full, overflow, items, out _, out var failure)
                && failure == InventoryPlacementFailure.Full;
            var mailAttachment = CharacterInventoryPlanner.CreateMailAttachments(overflow, items).Single();
            var restoredMail = JsonSerializer.Deserialize<CharacterMailAttachmentRecord>(
                JsonSerializer.Serialize(mailAttachment))!;
            check(overflowRejected && full.Count == CharacterAvatarInventoryLayout.Capacity
                    && restoredMail.AvatarAbilityIndex == 4 && restoredMail.AvatarRemainingSeconds == 0
                    && restoredMail.InstanceId == overflow.InstanceId,
                "full avatar inventory sends the selected permanent ability and UUID through the existing overflow-mail path");
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
