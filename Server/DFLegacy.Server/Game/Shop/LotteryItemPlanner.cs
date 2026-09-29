namespace DFLegacy.Server;

public enum LotteryUseFailure
{
    None,
    InvalidItem,
    LevelTooLow,
    InsufficientGold,
    InventoryFull,
    GoldLimit
}

public sealed record LotteryItemPlacementPlan(
    ushort SourceSlot,
    ushort DestinationSlot,
    LotteryRewardDefinition Reward,
    uint ResponseCountOrValue,
    ushort InstanceValue,
    byte ItemAttribute,
    int Gold,
    Dictionary<ushort, CharacterItemRecord> MainInventory,
    Dictionary<ushort, CharacterItemRecord> AvatarInventory,
    Dictionary<ushort, CharacterItemRecord> CreatureInventory);

public static class LotteryItemPlanner
{
    public static bool TryCreate(
        IReadOnlyDictionary<ushort, CharacterItemRecord> mainInventory,
        IReadOnlyDictionary<ushort, CharacterItemRecord> avatarInventory,
        IReadOnlyDictionary<ushort, CharacterItemRecord> creatureInventory,
        ushort sourceSlot,
        int characterLevel,
        int currentGold,
        uint weightLimit,
        int roll,
        ItemCatalog catalog,
        out LotteryItemPlacementPlan plan,
        out LotteryUseFailure failure)
    {
        plan = null!;
        failure = LotteryUseFailure.None;
        if (!CharacterInventoryLayout.IsMainItemSlot(sourceSlot)
            || !mainInventory.TryGetValue(sourceSlot, out var sourceItem)
            || sourceItem.CountOrValue == 0
            || !catalog.TryGetDefinition(sourceItem.ItemId, out var sourceDefinition)
            || sourceDefinition.Lottery is null)
        {
            failure = LotteryUseFailure.InvalidItem;
            return false;
        }

        if (sourceDefinition.MinimumLevel.GetValueOrDefault(1) > characterLevel)
        {
            failure = LotteryUseFailure.LevelTooLow;
            return false;
        }

        var useCost = Math.Max(0, sourceDefinition.LotteryUseCost ?? 0);
        var availableGold = Math.Max(0, currentGold);
        if (availableGold < useCost)
        {
            failure = LotteryUseFailure.InsufficientGold;
            return false;
        }

        var goldAfterCost = availableGold - useCost;
        var reward = sourceDefinition.Lottery.Select(roll);
        var plannedMain = mainInventory.ToDictionary(pair => pair.Key, pair => pair.Value);
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

        var plannedAvatar = avatarInventory.ToDictionary(pair => pair.Key, pair => pair.Value);
        var plannedCreature = creatureInventory.ToDictionary(pair => pair.Key, pair => pair.Value);
        if (reward.ItemId == 0)
        {
            var nextGold = (long)goldAfterCost + reward.CountOrValue;
            if (nextGold > int.MaxValue)
            {
                failure = LotteryUseFailure.GoldLimit;
                return false;
            }

            plan = new LotteryItemPlacementPlan(
                sourceSlot,
                CharacterInventoryLayout.GoldSlot,
                reward,
                reward.CountOrValue,
                0,
                0,
                checked((int)nextGold),
                plannedMain,
                plannedAvatar,
                plannedCreature);
            return true;
        }

        if (!catalog.TryGetDefinition(reward.ItemId, out var rewardDefinition))
        {
            failure = LotteryUseFailure.InvalidItem;
            return false;
        }

        if (CharacterWarehouseProgression.TryGetItemTargetCapacity(
                reward.ItemId,
                out _))
        {
            plan = new LotteryItemPlacementPlan(
                sourceSlot,
                sourceSlot,
                reward,
                reward.CountOrValue,
                0,
                0,
                goldAfterCost,
                plannedMain,
                plannedAvatar,
                plannedCreature);
            return true;
        }

        var durability = rewardDefinition.IsEquipment
            && rewardDefinition.InventoryCategory == ItemInventoryCategory.Equipment
                ? catalog.GetInitialDurability(reward.ItemId)
                : (ushort)0;
        var sealState = rewardDefinition.AttachType == ItemAttachType.Sealing
            ? (byte)1
            : (byte)0;
        var attachment = CharacterItemIdentity.Ensure(
            new CharacterMailAttachmentRecord(
            reward.ItemId,
            reward.CountOrValue,
            Durability: durability,
            SealState: sealState,
            AvatarRemainingSeconds: rewardDefinition.InventoryCategory == ItemInventoryCategory.Avatar
                ? 0u
                : null,
            AvatarAbilityIndex: rewardDefinition.InventoryCategory
                    == ItemInventoryCategory.Avatar
                ? (ushort)0
                : null),
            rewardDefinition);

        ushort destinationSlot;
        switch (rewardDefinition.InventoryCategory)
        {
            case ItemInventoryCategory.Avatar:
                if (!CharacterAvatarInventoryPlanner.TryPlace(
                        plannedAvatar,
                        attachment,
                        catalog,
                        out var avatarPlacement,
                        out _))
                {
                    failure = LotteryUseFailure.InventoryFull;
                    return false;
                }

                destinationSlot = avatarPlacement.DestinationSlot;
                plannedAvatar = avatarPlacement.Inventory;
                break;

            case ItemInventoryCategory.Creature:
                if (!CharacterCreatureInventoryPlanner.TryPlace(
                        plannedCreature,
                        new CharacterItemRecord(
                            ushort.MaxValue,
                            attachment.ItemId,
                            attachment.CountOrValue,
                            attachment.State,
                            attachment.Durability,
                            attachment.SealState,
                            InstanceId: attachment.InstanceId),
                        catalog,
                        preferredEmptySlot: null,
                        out var creaturePlacement,
                        out _))
                {
                    failure = LotteryUseFailure.InventoryFull;
                    return false;
                }

                destinationSlot = creaturePlacement.DestinationSlot;
                plannedCreature = creaturePlacement.Inventory;
                break;

            default:
                if (!CharacterInventoryPlanner.TryPlace(
                        plannedMain,
                        attachment,
                        catalog,
                        weightLimit,
                        out var inventoryPlacement,
                        out _))
                {
                    failure = LotteryUseFailure.InventoryFull;
                    return false;
                }

                destinationSlot = inventoryPlacement.DestinationSlot;
                plannedMain = inventoryPlacement.Inventory;
                break;
        }

        plan = new LotteryItemPlacementPlan(
            sourceSlot,
            destinationSlot,
            reward,
            rewardDefinition.InventoryCategory == ItemInventoryCategory.Avatar
                ? 0u
                : reward.CountOrValue,
            rewardDefinition.InventoryCategory == ItemInventoryCategory.Avatar
                ? (ushort)0
                : durability,
            0,
            goldAfterCost,
            plannedMain,
            plannedAvatar,
            plannedCreature);
        return true;
    }
}
