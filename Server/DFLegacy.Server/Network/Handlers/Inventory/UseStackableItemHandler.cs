using System.Buffers.Binary;
using System.Text;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// USE_STACKABLE_ITEM (CMD 47): consumes stackable items — fatigue/food, coupons, megaphones and other on-use effects (07 §5 group 9).
/// </summary>
public sealed class UseStackableItemHandler(
    ServerOptions options,
    RuntimeState runtime,
    JsonGameStore store,
    ItemCatalog itemCatalog,
    CharacterSpCatalog spCatalog,
    CharacterExperienceCatalog experienceCatalog,
    PvpExperienceCatalog pvpExperienceCatalog,
    CharacterSessionRegistry characterSessions,
    ILogger<UseStackableItemHandler> logger,
    CharacterPacketBuilders builders,
    ChannelSessionSenders senders) : IGameCommandHandler
{
    private const ushort FatigueRecoveryItemId = 7_518;
    private const ushort LevelUpCouponItemId = 7_519;
    private const ushort FatigueRecoveryUseThreshold = 126;
    private const ushort FatigueRecoveryAmount = 30;
    private const string FatigueRecoveryRejectedMessage =
        "疲劳值低于126点时才可以使用";
    private const string LevelUpCouponSuccessMessage = "Lv上升";
    private const string LevelUpCouponLevelLimitMessage = "角色已达到最大Lv上限";

    private static readonly Encoding ClientEncoding = PvfEncodings.Cp936Lossy();

    public ushort ProtocolId => GameProtocolEngine.UseStackableItemCommand;

    public string Name => "UseStackableItem";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        if (!options.EnablePacketTracing)
        {
            runtime.TracePacketAlways(
                context.Runtime,
                "RX-DUNGEON-ITEM",
                request,
                options.HexDumpLimit);
        }

        var parsedStackableItemUse = InventoryParsers.TryReadStackableItemUse(
            request.Body,
            out var slot,
            out var listType);
        CharacterItemRecord? consumedItem = null;
        if (session.Character.Active is not null && parsedStackableItemUse)
        {
            if (listType == 0)
            {
                session.Inventory.Main.TryGetValue(slot, out consumedItem);
            }
            else if (listType == 7)
            {
                session.Inventory.Creature.TryGetValue(slot, out consumedItem);
            }
        }

        if (session.Character.Active is null
            || !parsedStackableItemUse
            || listType is not (0 or 7)
            || consumedItem is null
            || consumedItem.CountOrValue == 0
            || listType == 0 && itemCatalog.IsEquipment(consumedItem.ItemId)
            || listType == 7
                && consumedItem.ItemId != CreatureHungerPlanner.FoodItemId)
        {
            var error = InventoryPacketBuilders.CreateUseStackableItemError(
                errorCode: 17,
                listType);
            await context.Outbound.SendAsync(error, Name, cancellationToken);
            logger.LogWarning(
                "Rejected stackable-item use session.Character.Active={HasCharacter}, dungeon={DungeonId}, parsed={Parsed}, listType={ListType}, slot={Slot}, body={Body}.",
                session.Character.Active is not null,
                session.Dungeon.CurrentDungeonId,
                parsedStackableItemUse,
                listType,
                slot,
                Convert.ToHexString(request.Body));
            return;
        }

        if (listType == 7)
        {
            // 13339's CCreature::UseFeed accepts the
            // Creature-space food item in town or in a
            // dungeon. Settle elapsed dungeon hunger first
            // so the revival decision uses authoritative
            // stomach data rather than a stale snapshot.
            if (session.Dungeon.CurrentDungeonId != 0
                && !await senders.SettleDungeonCreatureHungerAsync(context, 
                    notifyClient: true,
                    cancellationToken))
            {
                var settlementError =
                    InventoryPacketBuilders.CreateUseStackableItemError(
                        errorCode: 17,
                        listType);
                await context.Outbound.SendAsync(settlementError, Name, cancellationToken);
                return;
            }

            if (!session.Inventory.Creature.TryGetValue(
                    slot,
                    out var foodItem)
                || !session.Inventory.Creature.TryGetValue(
                    CharacterCreatureInventoryLayout
                        .EquippedCreatureSlot,
                    out var equippedCreature)
                || equippedCreature.CountOrValue == 0)
            {
                var creatureFoodError =
                    InventoryPacketBuilders.CreateUseStackableItemError(
                        errorCode: 17,
                        listType);
                await context.Outbound.SendAsync(creatureFoodError, Name, cancellationToken);
                logger.LogWarning(
                    "Rejected Creature food use at slot {Slot}: equipped Creature is unavailable.",
                    slot);
                return;
            }

            var previousStomach = (byte)Math.Clamp(
                (int)(equippedCreature.CreatureStomach ?? 100),
                0,
                CreatureHungerPlanner.MaximumStomach);
            var plannedCreatureInventory = session.Inventory.Creature
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            var remainingFood = foodItem.CountOrValue - 1;
            if (remainingFood == 0)
            {
                plannedCreatureInventory.Remove(slot);
            }
            else
            {
                plannedCreatureInventory[slot] = foodItem with
                {
                    CountOrValue = remainingFood
                };
            }

            var restoredStomach = checked((byte)Math.Min(
                CreatureHungerPlanner.MaximumStomach,
                previousStomach + CreatureHungerPlanner.FoodRecovery));
            plannedCreatureInventory[
                CharacterCreatureInventoryLayout.EquippedCreatureSlot] =
                equippedCreature with
                {
                    CreatureStomach = restoredStomach,
                    CreatureStomachRemainderSeconds =
                        previousStomach == 0
                            ? (byte)0
                            : equippedCreature
                                .CreatureStomachRemainderSeconds ?? 0
                };

            var savedCreatureFood =
                await store.SaveCharacterItemSpacesAsync(
                    session.Login.AccountName,
                    session.Character.Active.Id,
                    session.Currency.Gold,
                    session.Inventory.Main.Values,
                    session.Inventory.Equipped.Values,
                    session.Inventory.Warehouse.Values,
                    cancellationToken,
                    avatarInventory: session.Inventory.Avatar.Values,
                    creatureInventory:
                        plannedCreatureInventory.Values);
            if (savedCreatureFood is null)
            {
                var creatureFoodSaveError =
                    InventoryPacketBuilders.CreateUseStackableItemError(
                        errorCode: 17,
                        listType);
                await context.Outbound.SendAsync(creatureFoodSaveError, Name, cancellationToken);
                logger.LogWarning(
                    "Could not persist Creature food use at slot {Slot} for character {CharacterId}.",
                    slot,
                    session.Character.Active.Id);
                return;
            }

            session.Character.Active = savedCreatureFood;
            session.Inventory.Main = (savedCreatureFood.Inventory ?? [])
                .ToDictionary(item => item.Slot);
            session.Inventory.Equipped = (savedCreatureFood.Equipment ?? [])
                .ToDictionary(item => item.Slot);
            session.Inventory.Warehouse = (savedCreatureFood.Warehouse ?? [])
                .ToDictionary(item => item.Slot);
            session.Inventory.Avatar = (savedCreatureFood.AvatarInventory ?? [])
                .ToDictionary(item => item.Slot);
            session.Inventory.Creature = (savedCreatureFood.CreatureInventory ?? [])
                .ToDictionary(item => item.Slot);

            if (previousStomach == 0)
            {
                session.Weakness.CreatureDeathNotificationsSent.Remove(
                    equippedCreature.CountOrValue);
                var revival =
                    PetsPacketBuilders.CreateRevivalCreature(
                        session.Character.LocalUserId);
                await context.Outbound.SendAsync(revival, Name, cancellationToken);
            }

            var creatureState = PetsPacketBuilders.CreateCreatureState(
                equippedCreature.CountOrValue,
                restoredStomach);
            await context.Outbound.SendAsync(creatureState, Name, cancellationToken);

            var creatureFoodReply =
                InventoryPacketBuilders.CreateUseStackableItemReply(
                    slot,
                    listType);
            await context.Outbound.SendAsync(creatureFoodReply, Name, cancellationToken);
            foreach (var creaturePacket in
                     builders.CreateCreatureInventoryPackets(
                         session.Inventory.Creature.Values))
            {
                await context.Outbound.SendAsync(creaturePacket, Name, cancellationToken);
            }

            logger.LogInformation(
                "Used Creature food item {ItemId} at slot {Slot}; stomach {PreviousStomach}->{Stomach}, revived={Revived}.",
                foodItem.ItemId,
                slot,
                previousStomach,
                restoredStomach,
                previousStomach == 0);
            return;
        }

        if (consumedItem.ItemId == FatigueRecoveryItemId)
        {
            var premiumFatigue = senders.HasActivePremiumService(context, 
                    GameProtocolEngine.BlackDiamondServiceType)
                ? GameProtocolEngine.BlackDiamondBonusFatigue
                : (ushort)0;
            var maximumFatigue = checked((ushort)(
                GameProtocolEngine.DefaultMaximumFatigue + premiumFatigue));
            var useResult = await store.UseFatigueRecoveryItemAsync(
                session.Login.AccountName,
                session.Character.Active.Id,
                slot,
                FatigueRecoveryItemId,
                maximumFatigue,
                FatigueRecoveryUseThreshold,
                FatigueRecoveryAmount,
                cancellationToken);
            if (!useResult.Success || useResult.Character is null)
            {
                var error = InventoryPacketBuilders.CreateUseStackableItemError(
                    errorCode: 17,
                    listType);
                await context.Outbound.SendAsync(error, Name, cancellationToken);
                if (useResult.Failure
                    == FatigueRecoveryItemUseFailure.FatigueNotLowEnough)
                {
                    var popup = ChatPacketBuilders.CreatePopupNotification(
                        ClientEncoding.GetBytes(FatigueRecoveryRejectedMessage));
                    await context.Outbound.SendAsync(popup, Name, cancellationToken);
                }
                logger.LogInformation(
                    "Rejected fatigue recovery item {ItemId} at slot {Slot}; failure={Failure}, remaining fatigue={RemainingFatigue}/{MaximumFatigue}, stack={RemainingCount}.",
                    FatigueRecoveryItemId,
                    slot,
                    useResult.Failure,
                    maximumFatigue - useResult.PreviousUsedFatigue,
                    maximumFatigue,
                    useResult.RemainingItemCount);
                return;
            }

            session.Character.Active = useResult.Character;
            session.Inventory.Main = (session.Character.Active.Inventory ?? [])
                .ToDictionary(item => item.Slot);

            var fatigue = StaminaPacketBuilders.CreateFatigue(
                usedFatigue: useResult.UsedFatigue,
                maximumFatigue: maximumFatigue,
                premiumFatigue: premiumFatigue);
            await context.Outbound.SendAsync(fatigue, Name, cancellationToken);

            var fatigueItemReply =
                InventoryPacketBuilders.CreateUseStackableItemReply(
                    slot,
                    listType);
            await context.Outbound.SendAsync(fatigueItemReply, Name, cancellationToken);
            logger.LogInformation(
                "Used fatigue recovery item {ItemId} at slot {Slot}; restored={RecoveryAmount}, remaining fatigue {PreviousRemaining}->{Remaining}/{MaximumFatigue}, stack={RemainingCount}.",
                FatigueRecoveryItemId,
                slot,
                FatigueRecoveryAmount,
                maximumFatigue - useResult.PreviousUsedFatigue,
                maximumFatigue - useResult.UsedFatigue,
                maximumFatigue,
                useResult.RemainingItemCount);
            return;
        }

        if (consumedItem.ItemId == LevelUpCouponItemId)
        {
            var useResult = await store.UseLevelUpCouponItemAsync(
                session.Login.AccountName,
                session.Character.Active.Id,
                slot,
                LevelUpCouponItemId,
                experienceCatalog,
                spCatalog,
                cancellationToken);
            if (!useResult.Success
                || useResult.Character is null
                || useResult.Progression is null)
            {
                var error = InventoryPacketBuilders.CreateUseStackableItemError(
                    errorCode: 17,
                    listType);
                await context.Outbound.SendAsync(error, Name, cancellationToken);
                var rejectedMessage = useResult.Failure switch
                {
                    ExperienceBookItemUseFailure.LevelLimitReached =>
                        LevelUpCouponLevelLimitMessage,
                    ExperienceBookItemUseFailure.ItemStateChanged =>
                        "物品状态已发生变化，升级券使用失败",
                    ExperienceBookItemUseFailure.CharacterNotFound =>
                        "角色数据异常，升级券使用失败",
                    _ => "升级券使用失败"
                };
                await senders.SendPopupNotificationSafeAsync(context, 
                    ClientEncoding.GetBytes(rejectedMessage));
                logger.LogInformation(
                    "Rejected level-up coupon {ItemId} at slot {Slot}; failure={Failure}, level={Level}, stack={RemainingCount}.",
                    LevelUpCouponItemId,
                    slot,
                    useResult.Failure,
                    session.Character.Active.Level,
                    useResult.RemainingItemCount);
                return;
            }

            var progression = useResult.Progression;
            session.Character.Active = useResult.Character;
            session.Skill.CurrentSp = Math.Max(0, session.Character.Active.Sp);
            session.Inventory.Main = (session.Character.Active.Inventory ?? [])
                .ToDictionary(item => item.Slot);

            var levelUpItemReply =
                InventoryPacketBuilders.CreateUseStackableItemReply(
                    slot,
                    listType);
            await context.Outbound.SendAsync(levelUpItemReply, Name, cancellationToken);
            await senders.SendTypedMessageNotificationSafeAsync(context, 
                GameMessageType.Type16Brown,
                0,
                ClientEncoding.GetBytes(LevelUpCouponSuccessMessage));

            await senders.SendCurrentSkillInfoAsync(context, cancellationToken);
            await senders.ValidateAndCorrectCharacterStatsAsync(context, 
                "level-up coupon");
            await senders.SendAcceptableQuestListAsync(context, cancellationToken);

            logger.LogInformation(
                "Used level-up coupon {ItemId} at slot {Slot}; experience=+{Experience}, cumulative={CumulativeExperience}, level={PreviousLevel}->{Level}, SP=+{SkillPoints}, stack remaining={RemainingCount}.",
                LevelUpCouponItemId,
                slot,
                progression.GrantedExperience,
                progression.Experience,
                progression.PreviousLevel,
                progression.Level,
                useResult.GrantedSkillPoints,
                useResult.RemainingItemCount);
            return;
        }

        if (listType == 0
            && pvpExperienceCatalog.TryGetBookExperience(
                consumedItem.ItemId,
                out var pvpBookExperience))
        {
            var pvpUseResult = await store.UsePvpExperienceItemAsync(
                session.Login.AccountName,
                session.Character.Active.Id,
                slot,
                consumedItem.ItemId,
                pvpBookExperience,
                pvpExperienceCatalog,
                cancellationToken);
            if (!pvpUseResult.Success
                || pvpUseResult.Character is null)
            {
                var error = InventoryPacketBuilders.CreateUseStackableItemError(
                    errorCode: 17,
                    listType);
                await context.Outbound.SendAsync(error, Name, cancellationToken);
                logger.LogInformation(
                    "Rejected PvP experience item {ItemId} at slot {Slot}; failure={Failure}, stack={RemainingCount}.",
                    consumedItem.ItemId,
                    slot,
                    pvpUseResult.Failure,
                    pvpUseResult.RemainingItemCount);
                return;
            }

            session.Character.Active = pvpUseResult.Character;
            session.Inventory.Main = (session.Character.Active.Inventory ?? [])
                .ToDictionary(item => item.Slot);

            var pvpItemReply =
                InventoryPacketBuilders.CreateUseStackableItemReply(
                    slot,
                    listType);
            await context.Outbound.SendAsync(pvpItemReply, Name, cancellationToken);

            var pvpInventoryRefresh = builders.CreateMainInventoryPacket(
                session.Currency.Gold,
                session.Currency.VictoryPoints,
                session.Inventory.Main.Values);
            await context.Outbound.SendAsync(pvpInventoryRefresh, Name, cancellationToken);

            await senders.SendPvpRecordAsync(context, cancellationToken);
            await senders.SendTypedMessageNotificationSafeAsync(context, 
                GameMessageType.Type16Brown,
                0,
                ClientEncoding.GetBytes(
                    $"获得{pvpUseResult.GrantedExperience}点决斗经验值"));
            logger.LogInformation(
                "Used PvP experience item {ItemId} at slot {Slot}; PvP points {PreviousPoints}->{Points}, grade={Grade}, counters win={Wins}, play={PlayCount}, count={PvpCount}, stack remaining={RemainingCount}.",
                consumedItem.ItemId,
                slot,
                pvpUseResult.PreviousPoints,
                pvpUseResult.Points,
                pvpUseResult.Grade,
                session.Character.Active.PvpWins,
                session.Character.Active.PvpPlayCount,
                session.Character.Active.PvpCount,
                pvpUseResult.RemainingItemCount);
            return;
        }

        if (itemCatalog.TryGetDefinition(
                consumedItem.ItemId,
                out var boosterItemDefinition)
            && boosterItemDefinition.CeraBooster is not null)
        {
            CeraBoosterOpeningPlan boosterPlan = null!;
            var boosterFailure = "Cera boosters can only be opened in town";
            var canOpenBooster = session.Dungeon.CurrentDungeonId == 0
                && CeraBoosterOpeningPlanner.TryCreate(
                    session.Inventory.Main,
                    session.Inventory.Avatar,
                    session.Inventory.Creature,
                    slot,
                    itemCatalog,
                    senders.GetInventoryWeightLimit(context, session.Character.Active),
                    GameRandomSource.Shared,
                    out boosterPlan,
                    out boosterFailure);
            if (!canOpenBooster)
            {
                var error = InventoryPacketBuilders.CreateUseStackableItemError(
                    errorCode: 17,
                    listType);
                await context.Outbound.SendAsync(error, Name, cancellationToken);
                logger.LogWarning(
                    "Rejected Cera booster item {ItemId} at slot {Slot}; dungeon={DungeonId}, failure={Failure}.",
                    consumedItem.ItemId,
                    slot,
                    session.Dungeon.CurrentDungeonId,
                    boosterFailure);
                return;
            }

            var warehouseUpgradeSettlement =
                senders.PlanWarehouseUpgradeSettlement(context, 
                    boosterPlan.MainInventory.Values,
                    session.Inventory.Warehouse.Values,
                    boosterPlan.Rewards.Select(reward => reward.ItemId));
            var hasWarehouseUpgradeReward = boosterPlan.Rewards.Any(reward =>
                CharacterWarehouseProgression.TryGetItemTargetCapacity(
                    reward.ItemId,
                    out _));
            var overflowMails = CharacterPacketBuilders.CreateOverflowMailRequests(
                boosterPlan.OverflowItems.Where(item =>
                    !CharacterWarehouseProgression
                        .TryGetItemTargetCapacity(item.ItemId, out _)),
                "Cera booster reward");
            var savedCharacter = await store.SaveCharacterItemSpacesAsync(
                session.Login.AccountName,
                session.Character.Active.Id,
                session.Currency.Gold,
                warehouseUpgradeSettlement.Inventory,
                session.Inventory.Equipped.Values,
                warehouseUpgradeSettlement.Warehouse,
                cancellationToken,
                overflowMails,
                boosterPlan.AvatarInventory.Values,
                boosterPlan.CreatureInventory.Values,
                warehouseCapacity:
                    warehouseUpgradeSettlement.Capacity,
                expectedWarehouseCapacity: session.Inventory.WarehouseCapacity);
            if (savedCharacter is null)
            {
                var error = InventoryPacketBuilders.CreateUseStackableItemError(
                    errorCode: 17,
                    listType);
                await context.Outbound.SendAsync(error, Name, cancellationToken);
                logger.LogWarning(
                    "Could not persist Cera booster item {ItemId} at slot {Slot}.",
                    consumedItem.ItemId,
                    slot);
                return;
            }

            var warehouseCapacityChanged =
                senders.AdoptSavedWarehouseUpgradeState(context, savedCharacter);
            session.Inventory.Avatar = (savedCharacter.AvatarInventory ?? [])
                .ToDictionary(item => item.Slot);
            session.Inventory.Creature = (savedCharacter.CreatureInventory ?? [])
                .ToDictionary(item => item.Slot);

            var boosterReply =
                InventoryPacketBuilders.CreateUseStackableItemReply(
                    slot,
                    listType);
            await context.Outbound.SendAsync(boosterReply, Name, cancellationToken);

            var mainRefresh = builders.CreateMainInventoryPacket(
                session.Currency.Gold,
                session.Currency.VictoryPoints,
                session.Inventory.Main.Values);
            await context.Outbound.SendAsync(mainRefresh, Name, cancellationToken);

            var avatarRefresh = builders.CreateAvatarInventoryPacket(
                session.Inventory.Avatar.Values);
            await context.Outbound.SendAsync(avatarRefresh, Name, cancellationToken);

            foreach (var creatureRefresh in builders.CreateCreatureInventoryPackets(
                         session.Inventory.Creature.Values))
            {
                await context.Outbound.SendAsync(creatureRefresh, Name, cancellationToken);
            }

            if (warehouseCapacityChanged || hasWarehouseUpgradeReward)
            {
                await senders.SendWarehouseStateAsync(context, cancellationToken);
            }

            // CMD 47 has no result vector. Do not synthesize a CMD 67
            // response here: outside the Cera-shop transaction the
            // DF2008 handler retains invalid shop UI state and crashes.
            // Chat entries are the safe legacy result channel until
            // ijl15 provides a local result-window bridge.
            foreach (var rewardGroup in boosterPlan.Rewards
                         .GroupBy(reward => reward.ItemId))
            {
                var rewardCount = rewardGroup.Aggregate(
                    0u,
                    (total, reward) => checked(total + reward.Count));
                var rewardName = itemCatalog.TryGetDefinition(
                        rewardGroup.Key,
                        out var rewardDefinition)
                    && !string.IsNullOrWhiteSpace(rewardDefinition.Name)
                        ? rewardDefinition.Name
                        : $"item {rewardGroup.Key}";
                await senders.SendTypedMessageNotificationSafeAsync(context, 
                    GameMessageType.Type16Brown,
                    // A zero target keeps the legacy chat renderer
                    // from prefixing the local character name.
                    0,
                    ClientEncoding.GetBytes(
                        $"获得[{rewardName}]{rewardCount}个"));
            }

            if (overflowMails.Length > 0)
            {
                await senders.SendTypedMessageNotificationSafeAsync(context, 
                    GameMessageType.Type16Brown,
                    0,
                    ClientEncoding.GetBytes(
                        "背包空间不足，部分奖励已发送至邮箱。"));
            }

            if (overflowMails.Length > 0)
            {
                characterSessions.NotifyMailReceived(
                    session.Character.Active.CharacterNo);
            }

            logger.LogInformation(
                "Opened Cera booster item {ItemId} at slot {Slot}; rewards={Rewards}, overflowMails={OverflowMailCount}.",
                boosterPlan.BoosterItemId,
                slot,
                string.Join(
                    ", ",
                    boosterPlan.Rewards.Select(reward =>
                        $"{reward.Kind}:{reward.ItemId}x{reward.Count}")),
                overflowMails.Length);
            return;
        }

        if (session.Dungeon.CurrentDungeonId == 0)
        {
            var error = InventoryPacketBuilders.CreateUseStackableItemError(
                errorCode: 17,
                listType);
            await context.Outbound.SendAsync(error, Name, cancellationToken);
            logger.LogWarning(
                "Rejected non-fatigue stackable item {ItemId} outside a dungeon at slot {Slot}.",
                consumedItem.ItemId,
                slot);
            return;
        }

        var remaining = consumedItem.CountOrValue - 1;
        if (remaining == 0)
        {
            session.Inventory.Main.Remove(slot);
        }
        else
        {
            session.Inventory.Main[slot] = consumedItem with
            {
                CountOrValue = remaining
            };
        }

        session.Character.Active = await store.SaveCharacterInventoryAsync(
            session.Login.AccountName,
            session.Character.Active.Id,
            session.Currency.Gold,
            session.Inventory.Main.Values,
            session.Inventory.Equipped.Values,
            cancellationToken) ?? session.Character.Active;

        // Command 47 success is result:u8, slot:u16 and
        // list-type:u8. The client uses it to release its
        // pending state and apply the local stack update.
        var dungeonItemReply =
            InventoryPacketBuilders.CreateUseStackableItemReply(
                slot,
                listType);
        await context.Outbound.SendAsync(dungeonItemReply, Name, cancellationToken);
        logger.LogInformation(
            "Consumed dungeon item {ItemId} from listType={ListType}, slot={Slot}; remaining={Remaining}, body={Body}.",
            consumedItem.ItemId,
            listType,
            slot,
            remaining,
            Convert.ToHexString(request.Body));
    }
}
