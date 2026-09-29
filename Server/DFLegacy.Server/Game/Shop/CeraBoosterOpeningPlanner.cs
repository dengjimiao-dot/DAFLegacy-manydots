namespace DFLegacy.Server;

public sealed record CeraBoosterOpeningPlan(
    ushort SourceSlot,
    ushort BoosterItemId,
    Dictionary<ushort, CharacterItemRecord> MainInventory,
    Dictionary<ushort, CharacterItemRecord> AvatarInventory,
    Dictionary<ushort, CharacterItemRecord> CreatureInventory,
    IReadOnlyList<CharacterMailAttachmentRecord> OverflowItems,
    IReadOnlyList<CeraBoosterRewardDefinition> Rewards);

public static class CeraBoosterOpeningPlanner
{
    public static bool TryCreate(
        IReadOnlyDictionary<ushort, CharacterItemRecord> mainInventory,
        IReadOnlyDictionary<ushort, CharacterItemRecord> avatarInventory,
        IReadOnlyDictionary<ushort, CharacterItemRecord> creatureInventory,
        ushort sourceSlot,
        ItemCatalog catalog,
        uint weightLimit,
        IDropRandomSource random,
        out CeraBoosterOpeningPlan plan,
        out string failure)
    {
        plan = null!;
        failure = string.Empty;
        if (!CharacterInventoryLayout.IsMainItemSlot(sourceSlot)
            || !mainInventory.TryGetValue(sourceSlot, out var sourceItem)
            || sourceItem.CountOrValue == 0
            || !catalog.TryGetDefinition(sourceItem.ItemId, out var sourceDefinition)
            || sourceDefinition.CeraBooster is null)
        {
            failure = "the selected item is not a usable Cera booster";
            return false;
        }

        var rewards = sourceDefinition.CeraBooster.SelectAll(random);
        if (rewards.Count == 0)
        {
            failure = "the Cera booster produced no rewards";
            return false;
        }

        var plannedMain = mainInventory.ToDictionary(pair => pair.Key, pair => pair.Value);
        var plannedAvatar = avatarInventory.ToDictionary(pair => pair.Key, pair => pair.Value);
        var plannedCreature = creatureInventory.ToDictionary(pair => pair.Key, pair => pair.Value);
        var overflow = new List<CharacterMailAttachmentRecord>();
        if (sourceItem.CountOrValue == 1)
        {
            plannedMain.Remove(sourceSlot);
        }
        else
        {
            plannedMain[sourceSlot] = sourceItem with
            {
                CountOrValue = sourceItem.CountOrValue - 1
            };
        }

        foreach (var reward in rewards)
        {
            if (!catalog.TryGetDefinition(reward.ItemId, out var rewardDefinition))
            {
                failure = $"reward item {reward.ItemId} is not present in the item catalog";
                return false;
            }

            var isAvatar = rewardDefinition.InventoryCategory
                == ItemInventoryCategory.Avatar;
            var attachment = CharacterItemIdentity.Ensure(
                new CharacterMailAttachmentRecord(
                reward.ItemId,
                reward.Count,
                Durability: rewardDefinition.InventoryCategory
                        == ItemInventoryCategory.Equipment
                    ? catalog.GetInitialDurability(reward.ItemId)
                    : (ushort)0,
                SealState: rewardDefinition.AttachType == ItemAttachType.Sealing
                    ? (byte)1
                    : (byte)0,
                // The DF2008 client treats this field as a remaining period,
                // not an absolute timestamp. DFLegacy currently grants every
                // Cera-shop Avatar without a time limit.
                AvatarRemainingSeconds: isAvatar ? 0u : null,
                AvatarAbilityIndex: isAvatar
                    ? reward.AvatarAbilityIndex
                    : null),
                rewardDefinition);

            if (isAvatar)
            {
                if (CharacterAvatarInventoryPlanner.TryPlace(
                        plannedAvatar,
                        attachment,
                        catalog,
                        out var avatarPlacement,
                        out _))
                {
                    plannedAvatar = avatarPlacement.Inventory;
                }
                else
                {
                    overflow.AddRange(
                        CharacterInventoryPlanner.CreateMailAttachments(
                            attachment,
                            catalog));
                }

                continue;
            }

            if (rewardDefinition.InventoryCategory == ItemInventoryCategory.Creature)
            {
                var creatureItem = new CharacterItemRecord(
                    ushort.MaxValue,
                    attachment.ItemId,
                    attachment.CountOrValue,
                    attachment.State,
                    attachment.Durability,
                    attachment.SealState,
                    InstanceId: attachment.InstanceId);
                if (CharacterCreatureInventoryPlanner.TryPlace(
                        plannedCreature,
                        creatureItem,
                        catalog,
                        preferredEmptySlot: null,
                        out var creaturePlacement,
                        out _))
                {
                    plannedCreature = creaturePlacement.Inventory;
                }
                else
                {
                    overflow.AddRange(
                        CharacterInventoryPlanner.CreateMailAttachments(
                            attachment,
                            catalog));
                }

                continue;
            }

            if (CharacterInventoryPlanner.TryPlace(
                    plannedMain,
                    attachment,
                    catalog,
                    weightLimit,
                    out var inventoryPlacement,
                    out _))
            {
                plannedMain = inventoryPlacement.Inventory;
            }
            else
            {
                overflow.AddRange(CharacterInventoryPlanner.CreateMailAttachments(
                    attachment,
                    catalog));
            }
        }

        plan = new CeraBoosterOpeningPlan(
            sourceSlot,
            sourceItem.ItemId,
            plannedMain,
            plannedAvatar,
            plannedCreature,
            overflow.AsReadOnly(),
            rewards);
        return true;
    }

}
