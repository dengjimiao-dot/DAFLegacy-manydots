namespace DFLegacy.Server;

public sealed record CeraShopContentReward(
    ushort ItemId,
    uint Count,
    ushort AvatarAbilityIndex = 0);

public sealed record CeraShopContentPlacementPlan(
    Dictionary<ushort, CharacterItemRecord> MainInventory,
    Dictionary<ushort, CharacterItemRecord> AvatarInventory,
    Dictionary<ushort, CharacterItemRecord> CreatureInventory,
    IReadOnlyList<CharacterMailAttachmentRecord> OverflowItems,
    IReadOnlyList<CeraShopContentReward> Rewards);

public static class CeraShopContentPlanner
{
    public static bool TryCreatePurchase(
        IReadOnlyDictionary<ushort, CharacterItemRecord> mainInventory,
        IReadOnlyDictionary<ushort, CharacterItemRecord> avatarInventory,
        IReadOnlyDictionary<ushort, CharacterItemRecord> creatureInventory,
        ItemDefinition containerDefinition,
        uint containerCount,
        ItemCatalog catalog,
        uint weightLimit,
        IDropRandomSource random,
        Func<ushort, bool>? shouldSettleAsEffect,
        out CeraShopContentPlacementPlan plan,
        out string failure)
    {
        plan = null!;
        failure = string.Empty;
        if (containerCount == 0)
        {
            failure = "the Cera-shop container quantity is zero";
            return false;
        }

        var rewards = new List<CeraShopContentReward>();
        if (containerDefinition.CeraBooster is { } booster)
        {
            for (var opened = 0u; opened < containerCount; opened++)
            {
                rewards.AddRange(booster.SelectAll(random).Select(reward =>
                    new CeraShopContentReward(
                        reward.ItemId,
                        reward.Count,
                        reward.AvatarAbilityIndex)));
            }
        }
        else if (containerDefinition.CeraPackage is { } package)
        {
            foreach (var reward in package.Rewards)
            {
                var totalCount = (ulong)reward.Count * containerCount;
                if (totalCount == 0 || totalCount > uint.MaxValue)
                {
                    failure = $"Cera package reward {reward.ItemId} count overflowed";
                    return false;
                }

                rewards.Add(new CeraShopContentReward(
                    reward.ItemId,
                    checked((uint)totalCount)));
            }
        }
        else
        {
            failure = "the purchased item is not a Cera booster or Cera package";
            return false;
        }

        return TryPlace(
            mainInventory,
            avatarInventory,
            creatureInventory,
            rewards,
            catalog,
            weightLimit,
            shouldSettleAsEffect,
            out plan,
            out failure);
    }

    public static bool TryPlace(
        IReadOnlyDictionary<ushort, CharacterItemRecord> mainInventory,
        IReadOnlyDictionary<ushort, CharacterItemRecord> avatarInventory,
        IReadOnlyDictionary<ushort, CharacterItemRecord> creatureInventory,
        IReadOnlyList<CeraShopContentReward> rewards,
        ItemCatalog catalog,
        uint weightLimit,
        Func<ushort, bool>? shouldSettleAsEffect,
        out CeraShopContentPlacementPlan plan,
        out string failure)
    {
        plan = null!;
        failure = string.Empty;
        if (rewards.Count == 0)
        {
            failure = "the Cera-shop container produced no rewards";
            return false;
        }

        var plannedMain = mainInventory.ToDictionary(pair => pair.Key, pair => pair.Value);
        var plannedAvatar = avatarInventory.ToDictionary(pair => pair.Key, pair => pair.Value);
        var plannedCreature = creatureInventory.ToDictionary(pair => pair.Key, pair => pair.Value);
        var overflow = new List<CharacterMailAttachmentRecord>();
        foreach (var reward in rewards)
        {
            if (reward.Count == 0
                || !catalog.TryGetDefinition(reward.ItemId, out var rewardDefinition))
            {
                failure = $"reward item {reward.ItemId} is not present in the item catalog";
                return false;
            }

            if (shouldSettleAsEffect?.Invoke(reward.ItemId) == true)
            {
                continue;
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
                // The DF2008 client reads this field as remaining seconds.
                // Cera-shop rewards are permanent until expiration is implemented.
                AvatarRemainingSeconds: isAvatar ? 0u : null,
                AvatarAbilityIndex: isAvatar
                    ? reward.AvatarAbilityIndex
                    : null),
                rewardDefinition);

            foreach (var chunk in CharacterInventoryPlanner.CreateMailAttachments(
                         attachment,
                         catalog))
            {
                if (isAvatar)
                {
                    if (CharacterAvatarInventoryPlanner.TryPlace(
                            plannedAvatar,
                            chunk,
                            catalog,
                            out var avatarPlacement,
                            out _))
                    {
                        plannedAvatar = avatarPlacement.Inventory;
                    }
                    else
                    {
                        overflow.Add(chunk);
                    }

                    continue;
                }

                if (rewardDefinition.InventoryCategory == ItemInventoryCategory.Creature)
                {
                    var creatureItem = new CharacterItemRecord(
                        ushort.MaxValue,
                        chunk.ItemId,
                        chunk.CountOrValue,
                        chunk.State,
                        chunk.Durability,
                        chunk.SealState,
                        InstanceId: chunk.InstanceId);
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
                        overflow.Add(chunk);
                    }

                    continue;
                }

                if (CharacterInventoryPlanner.TryPlace(
                        plannedMain,
                        chunk,
                        catalog,
                        weightLimit,
                        out var inventoryPlacement,
                        out _))
                {
                    plannedMain = inventoryPlacement.Inventory;
                }
                else
                {
                    overflow.Add(chunk);
                }
            }
        }

        plan = new CeraShopContentPlacementPlan(
            plannedMain,
            plannedAvatar,
            plannedCreature,
            overflow.AsReadOnly(),
            rewards);
        return true;
    }
}
