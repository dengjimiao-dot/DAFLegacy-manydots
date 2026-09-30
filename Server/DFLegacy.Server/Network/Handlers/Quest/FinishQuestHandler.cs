using System.Buffers.Binary;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// FINISH_QUEST (CMD 36): settles quest rewards, experience and growth-skill changes, then refreshes inventory and skill panels (07 §5 group 7).
/// </summary>
public sealed class FinishQuestHandler(
    CharacterSessionRegistry characterSessions, IGameStore store,
    ItemCatalog itemCatalog,
    QuestCatalog questCatalog,
    DungeonCatalog dungeonCatalog,
    SkillCatalog skillCatalog,
    CharacterExperienceCatalog experienceCatalog,
    CharacterSpCatalog spCatalog,
    CharacterPacketBuilders builders,
    ChannelSessionSenders senders) : IGameCommandHandler
{
    public ushort ProtocolId => (byte)CmdPacketType.FINISH_QUEST;

    public string Name => "FinishQuest";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        if (session.Character.Active is null || request.Body.Length < 4)
        {
            var error = ConnectionPacketBuilders.CreateCommandError(request, 22);
            await context.Queue.SendAsync(error, Name, cancellationToken);
            return;
        }

        var questId = BinaryPrimitives.ReadUInt16LittleEndian(
            request.Body.AsSpan(2, 2));
        var questIndex = session.Quest.Current.FindIndex(quest => quest.QuestId == questId);
        var hasQuestDefinition = questCatalog.TryGetDefinition(
            questId,
            out var questDefinition);
        if (questIndex < 0 || !hasQuestDefinition)
        {
            var error = ConnectionPacketBuilders.CreateCommandError(request, 22);
            await context.Queue.SendAsync(error, Name, cancellationToken);
            return;
        }

        if (QuestItemRequirementPlanner.IsSeeking(questDefinition)
            || QuestPvpRankRequirement.IsPvpRank(questDefinition))
        {
            var authoritativeTrigger =
                senders.GetAuthoritativeQuestTrigger(context,
                    questDefinition,
                    session.Inventory.Main.Values,
                    session.Currency.Gold,
                    session.Currency.VictoryPoints);
            if (session.Quest.Current[questIndex].Trigger != authoritativeTrigger)
            {
                session.Quest.Current[questIndex] = session.Quest.Current[questIndex] with
                {
                    Trigger = authoritativeTrigger
                };
                var savedQuestProgress =
                    await store.SaveCharacterQuestsAsync(
                        session.Login.AccountName,
                        session.Character.Active.Id,
                        session.Quest.Current,
                        session.Quest.CompletedIds,
                        cancellationToken);
                if (savedQuestProgress is null)
                {
                    var error = ConnectionPacketBuilders.CreateCommandError(request, 22);
                    await context.Queue.SendAsync(error, Name, cancellationToken);
                    return;
                }

                session.Character.Active = session.Character.Active with
                {
                    Quests = savedQuestProgress.Quests,
                    CompletedQuestIds =
                        savedQuestProgress.CompletedQuestIds
                };
                var triggerUpdate =
                    QuestPacketBuilders.CreateSetQuestTriggerReply(
                        questId,
                        authoritativeTrigger);
                await context.Queue.SendAsync(triggerUpdate, Name, cancellationToken);
            }
        }

        if (!questDefinition.IsTriggerComplete(
                session.Quest.Current[questIndex].Trigger))
        {
            var error = ConnectionPacketBuilders.CreateCommandError(request, 22);
            await context.Queue.SendAsync(error, Name, cancellationToken);
            RuntimeLogger.Warn(
                "Rejected unfinished quest {QuestId}; remaining trigger={Trigger}.",
                questId,
                session.Quest.Current[questIndex].Trigger);
            return;
        }

        if (!questDefinition.TryGetGrowthReward(
                out var questChainType,
                out var questGrowNumber))
        {
            var error = ConnectionPacketBuilders.CreateCommandError(request, 22);
            await context.Queue.SendAsync(error, Name, cancellationToken);
            RuntimeLogger.Warn(
                "Rejected quest {QuestId} with invalid growth reward {RewardType}:{GrowthReward}.",
                questId,
                questDefinition.RewardType,
                questDefinition.GrowthReward);
            return;
        }

        var previousGrowType = session.Character.Active.GrowType & 0x0F;
        var previousAwakeningType = Math.Max(
            session.Character.Active.AwakeningType,
            Math.Max(0, session.Character.Active.GrowType >> 4));
        if ((questChainType == 1 && previousGrowType != 0)
            || (questChainType == 2
                && (previousGrowType == 0
                    || previousAwakeningType != 0)))
        {
            var error = ConnectionPacketBuilders.CreateCommandError(request, 22);
            await context.Queue.SendAsync(error, Name, cancellationToken);
            RuntimeLogger.Warn(
                "Rejected stale growth quest {QuestId}; chain={ChainType}, growType={GrowType}, awakening={AwakeningType}.",
                questId,
                questChainType,
                previousGrowType,
                previousAwakeningType);
            return;
        }

        if (!QuestItemRequirementPlanner.TryConsume(
                questDefinition,
                session.Inventory.Main,
                session.Currency.Gold,
                session.Currency.VictoryPoints,
                out var consumptionPlan))
        {
            var error = ConnectionPacketBuilders.CreateCommandError(request, 22);
            await context.Queue.SendAsync(error, Name, cancellationToken);
            RuntimeLogger.Warn(
                "Rejected quest {QuestId} because its seeking requirements changed before completion.",
                questId);
            return;
        }

        CharacterCreatureEvolutionPlan? creatureEvolution = null;
        if (questDefinition.RewardType == "creature evolution"
            && !CharacterCreatureInventoryPlanner.TryEvolve(
                session.Inventory.Creature,
                questDefinition,
                itemCatalog,
                out creatureEvolution,
                out var evolutionFailure))
        {
            var error = ConnectionPacketBuilders.CreateCommandError(request, 22);
            await context.Queue.SendAsync(error, Name, cancellationToken);
            RuntimeLogger.Warn(
                "Could not finish Creature evolution quest {QuestId}: {Reason}",
                questId,
                evolutionFailure);
            return;
        }

        var selectionIndex = request.Body.Length >= 6
            ? BinaryPrimitives.ReadUInt16LittleEndian(
                request.Body.AsSpan(4, sizeof(ushort)))
            : ushort.MaxValue;
        var rewardPlan = QuestRewardPlanner.Create(
            session.Character.Active with
            {
                Gold = session.Currency.Gold - consumptionPlan.GoldCost,
                VictoryPoints = session.Currency.VictoryPoints
                    - consumptionPlan.VictoryPointCost,
                Inventory = consumptionPlan.Inventory.Values.ToList()
            },
            questDefinition,
            selectionIndex == ushort.MaxValue ? -1 : selectionIndex,
            itemCatalog,
            senders.GetInventoryWeightLimit(context, session.Character.Active));
        if (!rewardPlan.Success)
        {
            var error = ConnectionPacketBuilders.CreateCommandError(request, 22);
            await context.Queue.SendAsync(error, Name, cancellationToken);
            RuntimeLogger.Warn(
                "Could not finish quest {QuestId}: {Reason}",
                questId,
                rewardPlan.Error);
            return;
        }

        var questExperience = experienceCatalog.CalculateQuestExperience(
            session.Character.Active,
            questDefinition);
        var progression = experienceCatalog.Grant(
            session.Character.Active,
            questExperience);
        var grantedSkillPoints = spCatalog.CalculateLevelUpReward(
            progression.PreviousLevel,
            progression.Level);
        var nextSkillPoints = checked(session.Skill.CurrentSp + grantedSkillPoints);
        var nextGrowType = previousGrowType;
        var nextAwakeningType = previousAwakeningType;
        var growthAdvanced = questChainType != 0;
        IReadOnlyList<CharacterGrantedSkillChange> growthSkillChanges = [];
        IReadOnlyList<CharacterSkillRecord>? nextSkills = null;
        if (questChainType == 1)
        {
            nextGrowType = questGrowNumber;
            nextAwakeningType = 0;
            growthSkillChanges = skillCatalog.GetGrowTypeSkillChanges(
                session.Character.Active.Job,
                nextGrowType);
            nextSkills = skillCatalog.CreateResetLayout(
                session.Character.Active.Job,
                nextGrowType,
                session.Skill.Current,
                CharacterPacketBuilders.InitialSkillsByJob.TryGetValue(
                    session.Character.Active.Job,
                    out var initialSkills)
                        ? initialSkills
                        : [],
                awakeningType: nextAwakeningType);
            nextSkillPoints = checked(
                spCatalog.CalculateTotalReward(progression.Level)
                + Math.Max(0, session.Character.Active.BonusSp));
        }
        else if (questChainType == 2)
        {
            nextAwakeningType = questGrowNumber;
            growthSkillChanges = skillCatalog.GetAwakeningSkillChanges(
                session.Character.Active.Job,
                nextGrowType,
                nextAwakeningType);
            if (!skillCatalog.TryApplyGrowthSkillChanges(
                    session.Character.Active.Job,
                    session.Skill.Current,
                    growthSkillChanges,
                    out nextSkills))
            {
                var error = ConnectionPacketBuilders.CreateCommandError(request, 22);
                await context.Queue.SendAsync(error, Name, cancellationToken);
                RuntimeLogger.Warn(
                    "Rejected awakening quest {QuestId}: no valid skill slot remains for its PVF grants.",
                    questId);
                return;
            }
        }

        if (growthAdvanced
            && (nextSkills is null
                || !SkillCatalog.AreGrowthSkillChangesApplied(
                    nextSkills,
                    growthSkillChanges)))
        {
            var error = ConnectionPacketBuilders.CreateCommandError(request, 22);
            await context.Queue.SendAsync(error, Name, cancellationToken);
            RuntimeLogger.Warn(
                "Rejected growth quest {QuestId}: its PVF skill changes could not be applied atomically.",
                questId);
            return;
        }

        var nextVictoryPoints = session.Currency.VictoryPoints
            - consumptionPlan.VictoryPointCost;
        var remainingQuests = session.Quest.Current
            .Where((_, index) => index != questIndex)
            .ToList();
        var nextQuests = senders.RefreshSeekingQuestTriggers(context,
            remainingQuests,
            rewardPlan.Inventory,
            rewardPlan.Gold,
            nextVictoryPoints,
            out var nextQuestTriggerChanges);
        var nextCompletedQuestIds = session.Quest.CompletedIds.ToHashSet();
        nextCompletedQuestIds.Add(questId);

        var nextCreatureInventory = creatureEvolution?.Inventory
            ?? session.Inventory.Creature;
        var questGroundRewards = session.Dungeon.CurrentDungeonId != 0
            ? rewardPlan.MailedItems
                .Where(item => itemCatalog.TryGetDefinition(
                        item.ItemId,
                        out var definition)
                    && CharacterItemSealing.CanTrade(item, definition))
                .ToArray()
            : [];
        var questMailRewards = session.Dungeon.CurrentDungeonId == 0
            ? rewardPlan.MailedItems
            : rewardPlan.MailedItems.Except(questGroundRewards).ToArray();
        var completedCharacter = await store.CompleteCharacterQuestAsync(
            session.Login.AccountName,
            session.Character.Active.Id,
            questId,
            progression.Level,
            progression.Experience,
            nextSkillPoints,
            rewardPlan.Gold,
            rewardPlan.Inventory,
            nextQuests,
            nextCompletedQuestIds,
            cancellationToken,
            CharacterPacketBuilders.CreateOverflowMailRequests(
                questMailRewards,
                "Quest reward"),
            creatureInventory: nextCreatureInventory.Values,
            skills: nextSkills,
            growType: growthAdvanced ? nextGrowType : null,
            awakeningType: growthAdvanced ? nextAwakeningType : null,
            victoryPoints: nextVictoryPoints,
            warehouseCapacity: rewardPlan.WarehouseCapacity,
            expectedWarehouseCapacity: session.Inventory.WarehouseCapacity,
            unlockedDungeonIds: HiddenDungeonUnlockCatalog.ResolveQuestDungeonUnlockIds(
                questId, questDefinition, dungeonCatalog));
        if (completedCharacter is null)
        {
            var error = ConnectionPacketBuilders.CreateCommandError(request, 22);
            await context.Queue.SendAsync(error, Name, cancellationToken);
            return;
        }

        session.Character.Active = completedCharacter;
        session.Skill.CurrentSp = Math.Max(0, completedCharacter.Sp);
        session.Currency.Gold = completedCharacter.Gold;
        session.Currency.VictoryPoints = completedCharacter.VictoryPoints;
        var warehouseCapacityChanged = completedCharacter.WarehouseCapacity
            != session.Inventory.WarehouseCapacity;
        session.Inventory.WarehouseCapacity = CharacterWarehouseProgression.Normalize(
            completedCharacter.WarehouseCapacity);
        session.Inventory.Main = (completedCharacter.Inventory ?? [])
            .ToDictionary(item => item.Slot);
        session.Inventory.Creature = nextCreatureInventory;
        session.Quest.Current = nextQuests;
        session.Quest.CompletedIds = nextCompletedQuestIds;
        session.Skill.Current = (completedCharacter.Skills ?? [])
            .Where(skill => skill.Level > 0)
            .OrderBy(skill => skill.Slot)
            .ToList();
        if (questMailRewards.Count > 0)
        {
            characterSessions.NotifyMailReceived(session.Character.Active.CharacterNo);
        }

        foreach (var rewardItem in questGroundRewards)
        {
            var groundItem = session.Dungeon.GroundItems.Add(
                new DungeonGeneratedDrop(
                    false,
                    rewardItem.ItemId,
                    rewardItem.CountOrValue),
                session.Character.LocalUserId,
                ushort.MaxValue,
                session.Dungeon.RoomX,
                session.Dungeon.RoomY,
                new CharacterItemRecord(
                    ushort.MaxValue,
                    rewardItem.ItemId,
                    rewardItem.CountOrValue,
                    rewardItem.State,
                    rewardItem.Durability,
                    rewardItem.SealState,
                    rewardItem.AvatarRemainingSeconds,
                    rewardItem.AvatarAbilityIndex,
                    EquipmentQualitySeed: rewardItem.EquipmentQualitySeed,
                    InstanceId: rewardItem.InstanceId),
                x: session.Character.X,
                y: session.Character.Y,
                catalog: itemCatalog);
            var dropNotification = CraftPacketBuilders.CreateDropItem(
                session.Character.LocalUserId,
                session.Character.X,
                session.Character.Y,
                new GameDungeonDrop(
                    groundItem.GroundId,
                    groundItem.ItemId,
                    groundItem.GetClientAddInfo(itemCatalog),
                    groundItem.GetClientDurability(itemCatalog),
                    groundItem.OwnerUserId,
                    groundItem.GetClientItemAttr(itemCatalog)));
            await context.Queue.SendAsync(dropNotification, Name, cancellationToken);
        }

        if (creatureEvolution is not null)
        {
            var evolutionNotification =
                PetsPacketBuilders.CreateCreatureEvolution(
                    checked((byte)questDefinition.EvolutionCreatureKind),
                    session.Character.LocalUserId);
            await context.Queue.SendAsync(evolutionNotification, Name, cancellationToken);

            foreach (var creaturePacket in builders.CreateCreatureInventoryPackets(
                         session.Inventory.Creature.Values))
            {
                await context.Queue.SendAsync(creaturePacket, Name, cancellationToken);
            }
        }

        var insertedQuestRewards = rewardPlan.InsertedItems
            .Select(item => new GameQuestInsertedItem(
                item.Slot,
                item.ItemId,
                item.CountOrSeed))
            .ToList();
        if (questDefinition.GoldReward > 0)
        {
            insertedQuestRewards.Add(new GameQuestInsertedItem(
                0,
                0,
                checked((uint)questDefinition.GoldReward)));
        }

        var finishQuestReply = QuestPacketBuilders.CreateFinishQuestReply(
            questId,
            consumptionPlan.ChangedItems
                .Select(item => new GameQuestConsumedItem(
                    item.Slot,
                    item.RemainingCount))
                .ToArray(),
            insertedQuestRewards,
            questChainType,
            questGrowNumber,
            growthSkillChanges
                .Select(change => new GameQuestGrowthSkillChange(
                    change.SkillId,
                    change.SkillClass,
                    change.Level))
                .ToArray());
        await context.Queue.SendAsync(finishQuestReply, Name, cancellationToken);
        var inventoryRefresh = builders.CreateMainInventoryPacket(
            session.Currency.Gold,
            session.Currency.VictoryPoints,
            session.Inventory.Main.Values);
        await context.Queue.SendAsync(inventoryRefresh, Name, cancellationToken);
        if (warehouseCapacityChanged)
        {
            var warehouseRefresh = builders.CreateWarehousePacket(
                session.Inventory.Warehouse.Values,
                session.Inventory.WarehouseCapacity);
            await context.Queue.SendAsync(warehouseRefresh, Name, cancellationToken);
        }
        foreach (var change in nextQuestTriggerChanges)
        {
            var triggerUpdate =
                QuestPacketBuilders.CreateSetQuestTriggerReply(
                    change.QuestId,
                    change.NewTrigger);
            await context.Queue.SendAsync(triggerUpdate, Name, cancellationToken);
        }
        if (progression.LeveledUp || growthAdvanced)
        {
            await senders.SendCurrentSkillInfoAsync(context, cancellationToken);
            await senders.ValidateAndCorrectCharacterStatsAsync(
                context,
                progression.LeveledUp
                    ? "quest level-up"
                    : questChainType == 1
                        ? "class advancement"
                        : "awakening growth change");
        }
        else
        {
            await senders.SendCurrentEquipmentStateAsync(context, cancellationToken: cancellationToken);
        }

        await senders.SendDungeonPermissionsAsync(context, cancellationToken);
        await senders.SendAcceptableQuestListAsync(context, cancellationToken);
        RuntimeLogger.Info(
            "Finished quest {QuestId}; chain={ChainType}, consumedItems={ConsumedItemCount}, insertedItems={InsertedItemCount}, goldCost={GoldCost}, victoryPointCost={VictoryPointCost}, rewardItems={ItemCount}, gold=+{Gold}, exp=+{Experience}, SP={SkillPoints}, level={OldLevel}->{Level}, growType={OldGrowType}->{GrowType}, awakening={OldAwakening}->{Awakening}.",
            questId,
            questChainType,
            consumptionPlan.ChangedItems.Count,
            insertedQuestRewards.Count,
            consumptionPlan.GoldCost,
            consumptionPlan.VictoryPointCost,
            rewardPlan.GrantedItems.Count,
            questDefinition.GoldReward,
            progression.GrantedExperience,
            session.Skill.CurrentSp,
            progression.PreviousLevel,
            progression.Level,
            previousGrowType,
            completedCharacter.GrowType & 0x0F,
            previousAwakeningType,
            completedCharacter.AwakeningType);
        if (creatureEvolution is not null)
        {
            RuntimeLogger.Info(
                "Evolved Creature uid {CreatureUid} in slot {Slot}: item {SourceItemId}->{EvolvedItemId} for quest {QuestId}.",
                creatureEvolution.CreatureUid,
                creatureEvolution.Slot,
                creatureEvolution.SourceItemId,
                creatureEvolution.EvolvedItemId,
                questId);
        }
    }
}
