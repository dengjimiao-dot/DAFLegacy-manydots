namespace DAFLegacy.Server;

public enum SystemItemMailReason
{
    DungeonClearReward,
    InventoryRepair,
    QuestReward,
    CeraBoosterReward,
    CeraShopPurchase,
    CeraShopContainerReward,
    TownShopPurchase,
    LegacyQuestReward
}

public static class SystemMailTemplates
{
    public const string SenderName = "游戏管理员";
    public const string ItemReturnText = "物品栏空间不足或超过负重上限，物品已通过邮件返还，请注意查收。";
    public const string InventoryRepairText = "物品栏中的部分物品已通过邮件返还，请注意查收。";

    public static CreateCharacterMailRequest CreateItemReturn(
        CharacterMailAttachmentRecord attachment,
        SystemItemMailReason reason)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        var subject = reason switch
        {
            SystemItemMailReason.DungeonClearReward => "通关奖励",
            SystemItemMailReason.InventoryRepair => "物品栏整理",
            SystemItemMailReason.QuestReward => "任务奖励",
            SystemItemMailReason.CeraBoosterReward => "礼包奖励",
            SystemItemMailReason.CeraShopPurchase => "商城购买",
            SystemItemMailReason.CeraShopContainerReward => "商城礼包奖励",
            SystemItemMailReason.TownShopPurchase => "商店购买",
            SystemItemMailReason.LegacyQuestReward => "任务奖励补发",
            _ => throw new ArgumentOutOfRangeException(nameof(reason))
        };
        var text = reason == SystemItemMailReason.InventoryRepair ? InventoryRepairText : ItemReturnText;
        return new CreateCharacterMailRequest(SenderName, $"{subject}：{text}", Attachment: attachment);
    }
}
