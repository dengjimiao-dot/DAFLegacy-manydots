namespace DAFLegacy.Server;

/// <summary>
/// 后台「任务权限」批量完成的入选与结算规划：只补等级区间覆盖当前角色等级
/// （PVF <c>[level]</c> 下限 ≤ 角色等级 ≤ 上限）且职业、成长阶段一致的任务，
/// 从而推进当前等级段的任务链并解锁任务关联的隐藏副本；不做历史等级段的
/// 回溯补记。奖励结算复用启动期遗留奖励迁移的语义：物品能放进背包的直接
/// 入包，放不下的溢出部分转系统邮件；转职与觉醒链在角色未到达对应成长阶段
/// 时保留，由玩家自行完成。
/// </summary>
public static class QuestBulkCompletionPlanner
{
    public static IReadOnlyList<ushort> SelectLevelEligibleQuestIds(
        CharacterRecord character,
        QuestCatalog questCatalog)
    {
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(questCatalog);

        var primaryGrowType = character.GrowType & 0x0F;
        var awakeningType = Math.Max(
            character.AwakeningType,
            Math.Max(0, character.GrowType >> 4));
        var completed = (character.CompletedQuestIds ?? []).ToHashSet();
        var eligible = new List<ushort>();
        foreach (var definition in questCatalog.Definitions)
        {
            // 新手引导完成任务由 TutorialStarted 机制单独管辖，不参与批量补记。
            if (QuestCatalog.IsTutorialCompletionQuestDefinition(definition)
                || character.Level < definition.MinimumLevel
                || character.Level > definition.MaximumLevel
                || completed.Contains(definition.QuestId)
                || !QuestCatalog.MatchesJob(definition.Job, character.Job))
            {
                continue;
            }

            // 奖励配置不完整的成长任务在完成流程同样会被拒绝，这里保持一致跳过。
            if (!definition.TryGetGrowthReward(out var chainType, out var growNumber))
            {
                continue;
            }

            if (definition.GrowType >= 0 && definition.GrowType != primaryGrowType)
            {
                continue;
            }

            if (chainType == 1)
            {
                // 转职最终任务：仅在角色已转职且转职号一致时补记。
                if (primaryGrowType == 0 || growNumber != primaryGrowType)
                {
                    continue;
                }
            }
            else if (chainType == 2)
            {
                // 觉醒最终任务：仅在角色已觉醒且觉醒阶段一致时补记。
                if (primaryGrowType == 0 || awakeningType == 0 || growNumber != awakeningType)
                {
                    continue;
                }
            }
            else if (definition.JobChangeQuest == 1)
            {
                // 转职链中间任务：未转职的角色保留整条转职链。
                if (primaryGrowType == 0)
                {
                    continue;
                }
            }
            else if (definition.JobChangeQuest == 2)
            {
                // 觉醒链中间任务：已转职但未觉醒的角色保留觉醒体验。
                if (primaryGrowType == 0 || awakeningType == 0)
                {
                    continue;
                }
            }

            eligible.Add(definition.QuestId);
        }

        return eligible.OrderBy(questId => questId).ToArray();
    }

    /// <summary>
    /// 按启动期遗留奖励迁移的同款语义逐任务结算入选任务的奖励：任务经验与
    /// 升级 SP 按顺序模拟累计，固定奖励与金币直接入账，背包放不下的物品转为
    /// 系统邮件。需要玩家自选奖励（可选项）、物品缺失或金币溢出的任务不发放
    /// 奖励，计入 <see cref="QuestBulkRewardSettlement.SkippedRewardQuestIds"/>
    /// 由调用方回显。
    /// </summary>
    public static QuestBulkRewardSettlement PlanRewardSettlement(
        CharacterRecord character,
        IReadOnlyList<ushort> questIds,
        QuestCatalog questCatalog,
        ItemCatalog itemCatalog,
        CharacterExperienceCatalog experienceCatalog,
        CharacterSpCatalog spCatalog)
    {
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(questIds);
        ArgumentNullException.ThrowIfNull(questCatalog);
        ArgumentNullException.ThrowIfNull(itemCatalog);
        ArgumentNullException.ThrowIfNull(experienceCatalog);
        ArgumentNullException.ThrowIfNull(spCatalog);

        var simulated = character;
        var skillPoints = Math.Max(0, character.Sp);
        var rewarded = new List<ushort>();
        var skipped = new List<ushort>();
        var mails = new List<CreateCharacterMailRequest>();
        foreach (var questId in questIds)
        {
            // 无定义的任务无法结算奖励，仍会由存储层并入完成集合。
            if (!questCatalog.TryGetDefinition(questId, out var definition))
            {
                skipped.Add(questId);
                continue;
            }

            // 与迁移结算一致：背包权重上限放开，放不下的物品走邮件。
            var rewardPlan = QuestRewardPlanner.Create(
                simulated,
                definition,
                selectionIndex: -1,
                itemCatalog,
                uint.MaxValue);
            if (!rewardPlan.Success)
            {
                skipped.Add(questId);
                continue;
            }

            var questExperience = experienceCatalog.CalculateQuestExperience(
                simulated,
                definition);
            var progression = experienceCatalog.Grant(simulated, questExperience);
            skillPoints = checked(skillPoints + spCatalog.CalculateLevelUpReward(
                progression.PreviousLevel,
                progression.Level));
            simulated = simulated with
            {
                Level = progression.Level,
                Experience = progression.Experience,
                Gold = rewardPlan.Gold,
                Inventory = rewardPlan.Inventory,
                WarehouseCapacity = rewardPlan.WarehouseCapacity
            };
            mails.AddRange(CharacterPacketBuilders.CreateOverflowMailRequests(
                rewardPlan.MailedItems,
                "Quest reward"));
            rewarded.Add(questId);
        }

        return new QuestBulkRewardSettlement(
            simulated.Level,
            simulated.Experience,
            skillPoints,
            simulated.Gold,
            simulated.Inventory ?? [],
            CharacterWarehouseProgression.Normalize(simulated.WarehouseCapacity),
            mails,
            rewarded,
            skipped);
    }
}

/// <summary>批量完成任务的奖励结算结果：落盘所需的最终状态与逐任务去向。</summary>
public sealed record QuestBulkRewardSettlement(
    int Level,
    uint Experience,
    int SkillPoints,
    int Gold,
    IReadOnlyList<CharacterItemRecord> Inventory,
    ushort WarehouseCapacity,
    IReadOnlyList<CreateCharacterMailRequest> Mails,
    IReadOnlyList<ushort> RewardedQuestIds,
    IReadOnlyList<ushort> SkippedRewardQuestIds);
