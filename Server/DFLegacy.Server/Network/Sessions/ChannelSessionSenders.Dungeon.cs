using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// Dungeon-flow senders (07 §4.7): permissions, return-to-town,
/// selection eligibility, room fatigue and Creature experience.
/// </summary>
public sealed partial class ChannelSessionSenders
{
    public IReadOnlyList<ushort> GetVisibleDungeonIds(ConnectionContext context) =>
        HiddenDungeonUnlockCatalog.ResolveVisibleDungeonIds(
            dungeonCatalog.DungeonIds,
            context.State.Character.Active?.UnlockedDungeonIds)
        .Where(dungeonId => dungeonId != GameProtocolConstants.TutorialDungeonId)
        .ToArray();

    public IReadOnlyList<GameDungeonPermissionEntry> GetDungeonPermissions(ConnectionContext context)
    {
        var permissions = GetVisibleDungeonIds(context)
            .Select(dungeonId => new GameDungeonPermissionEntry(
                dungeonId,
                DungeonDifficultyProgression.GetMaximumDifficulty(
                    context.State.Character.Active?.DungeonProgress,
                dungeonId)))
            .ToList();
        if (context.State.Character.Active is { TutorialStarted: false }
            && permissions.All(permission =>
                permission.DungeonId != GameProtocolConstants.TutorialDungeonId))
        {
            permissions.Add(new GameDungeonPermissionEntry(
                GameProtocolConstants.TutorialDungeonId,
                ClearState: 0));
        }

        return permissions;
    }

    public async Task SendDungeonPermissionsAsync(ConnectionContext context, CancellationToken cancellationToken)
    {
        var permissions = DungeonPacketBuilders.CreateDungeonPermissions(
            GetDungeonPermissions(context));
        await context.Outbound.SendAsync(permissions, "DUNGEON_PERMISSIONS", cancellationToken);
    }

    /// <summary>
    /// Settles warehouse-capacity upgrades against the current session
    /// capacity; the former PlanWarehouseUpgradeSettlement local function.
    /// </summary>

    public async Task SendDungeonPermissionsSafeAsync(ConnectionContext context)
    {
        try
        {
            var permissions = DungeonPacketBuilders.CreateDungeonPermissions(
                GetDungeonPermissions(context));
            await context.Outbound.SendAsync(permissions, "DUNGEON_PERMISSIONS", context.Disconnecting);
        }
        catch (OperationCanceledException) when (context.Disconnecting.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Could not send dungeon permissions to character {CharacterId}.",
                context.State.Character.Active?.Id);
        }
    }

    public async Task<bool> SettleDungeonCreatureHungerAsync(
    ConnectionContext context,
    bool notifyClient,
    CancellationToken cancellationToken)
    {
    if (context.State.Dungeon.CreatureStomachCheckpointSeconds is not long checkpoint)
    {
        return true;
    }

    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var elapsedSeconds = checked((uint)Math.Min(
        uint.MaxValue,
        Math.Max(0L, now - checkpoint)));
    if (elapsedSeconds == 0)
    {
        return true;
    }

    if (context.State.Character.Active is null
        || !CreatureHungerPlanner.TrySettle(
            context.State.Inventory.Creature,
            elapsedSeconds,
            itemCatalog,
            out var hungerPlan))
    {
        context.State.Dungeon.CreatureStomachCheckpointSeconds = now;
        return true;
    }

    if (!hungerPlan.Changed)
    {
        context.State.Dungeon.CreatureStomachCheckpointSeconds = now;
        return true;
    }

    var saved = await store.SaveCharacterItemSpacesAsync(
        context.State.Login.AccountName,
        context.State.Character.Active.Id,
        context.State.Currency.Gold,
        context.State.Inventory.Main.Values,
        context.State.Inventory.Equipped.Values,
        context.State.Inventory.Warehouse.Values,
        cancellationToken,
        avatarInventory: context.State.Inventory.Avatar.Values,
        creatureInventory: hungerPlan.Inventory.Values);
    if (saved is null)
    {
        logger.LogWarning(
            "Could not persist Creature stomach settlement for character {CharacterId} in dungeon {DungeonId}.",
            context.State.Character.Active.Id,
            context.State.Dungeon.CurrentDungeonId);
        return false;
    }

    context.State.Inventory.Creature = hungerPlan.Inventory;
    context.State.Character.Active = context.State.Character.Active with
    {
        CreatureInventory = hungerPlan.Inventory.Values
            .OrderBy(item => item.Slot)
            .ToList()
    };
    context.State.Dungeon.CreatureStomachCheckpointSeconds = now;

    if (hungerPlan.BecameDead)
    {
        // The 13339 timer path emits NOTI 100 exactly when the
        // effective stomach crosses below one.  Do this even
        // for a silent settlement so the client cannot keep a
        // live map object after a transactional refresh.
        await SendEquippedCreatureDeathAsync(context, cancellationToken: cancellationToken);
    }

    if (notifyClient && hungerPlan.StomachChanged)
    {
        var stomachState = PetsPacketBuilders.CreateCreatureState(
            hungerPlan.CreatureUid,
            hungerPlan.Stomach);
        await context.Outbound.SendAsync(stomachState, "CREATURE_STATE", cancellationToken);
    }

    if (notifyClient && hungerPlan.ConsumedFoodCount != 0)
    {
        var creatureItems = builders.CreateCreatureInventoryItemListPacket(
            context.State.Inventory.Creature.Values);
        await context.Outbound.SendAsync(creatureItems, "CREATURE_STATE", cancellationToken);
    }

    logger.LogInformation(
        "Dungeon {DungeonId} settled {ElapsedSeconds}s of Creature hunger for uid {CreatureUid}: stomach {PreviousStomach}->{Stomach}, remainder {PreviousRemainder}->{Remainder}s, auto-consumed food item 24 x{ConsumedFoodCount}.",
        context.State.Dungeon.CurrentDungeonId,
        hungerPlan.ElapsedSeconds,
        hungerPlan.CreatureUid,
        hungerPlan.PreviousStomach,
        hungerPlan.Stomach,
        hungerPlan.PreviousRemainderSeconds,
        hungerPlan.RemainderSeconds,
        hungerPlan.ConsumedFoodCount);
    return true;
    }

    public async Task ReturnToTownFromDungeonAsync(
    ConnectionContext context,
    string reason,
    bool forcedDungeonExit = false,
    CancellationToken cancellationToken = default)
    {
    if (context.State.Dungeon.CurrentDungeonId == 0 && !context.State.Dungeon.InSelection)
    {
        logger.LogWarning(
            "Ignored {Reason} return-to-town request because the user is not in a dungeon or dungeon selection.",
            reason);
        return;
    }

    if (context.State.Dungeon.CurrentDungeonId != 0 && !context.State.Dungeon.TutorialActive)
    {
        await SettleDungeonCreatureHungerAsync(context, 
            notifyClient: true,
            cancellationToken);
    }

    var leavingDungeonId = context.State.Dungeon.CurrentDungeonId;
    var leftDungeonSelection = context.State.Dungeon.InSelection;
    var gainedExperience = context.State.Dungeon.MonsterExperienceGained;
    var leveledUp = context.State.Dungeon.LeveledUp;
    if (context.State.Character.Active is not null && gainedExperience > 0)
    {
        var savedProgression = await store.SaveCharacterProgressionAsync(
            context.State.Login.AccountName,
            context.State.Character.Active.Id,
            context.State.Character.Active.Level,
            context.State.Character.Active.Experience,
            context.State.Skill.CurrentSp,
            cancellationToken);
        if (savedProgression is not null)
        {
            context.State.Character.Active = context.State.Character.Active with
            {
                Level = savedProgression.Level,
                Experience = savedProgression.Experience,
                Sp = savedProgression.Sp
            };
        }
    }

    var hasBlackDiamond = false;
    if (context.State.Character.Active is not null)
    {
        var blackDiamondState = await store.GetPremiumServiceAsync(
            context.State.Login.AccountName,
            cancellationToken);
        hasBlackDiamond = blackDiamondState.Active;
        if (blackDiamondState.Active
            && blackDiamondState.ExpiresAt is { } blackDiamondExpiry)
        {
            context.State.Premium.ExpiryUnix[
                GameProtocolConstants.BlackDiamondServiceType] =
                blackDiamondExpiry;
        }
        else
        {
            context.State.Premium.ExpiryUnix.TryRemove(
                GameProtocolConstants.BlackDiamondServiceType,
                out _);
        }
    }

    var enteredWeakness = false;
    if (context.State.Character.Active is not null
        && !context.State.Character.Active.IsWeakened
        && DungeonWeaknessPolicy.ShouldApply(
            context.State.Character.Active.Level,
            forcedDungeonExit,
            dungeonActive: leavingDungeonId != 0,
            dungeonCleared: context.State.Dungeon.ClearEnabled,
            hasBlackDiamond))
    {
        var weakenedCharacter = await store.SetCharacterWeaknessRecoveryAsync(
            context.State.Character.Active.Id,
            DungeonWeaknessPolicy.InitialRecovery,
            cancellationToken);
        if (weakenedCharacter is not null)
        {
            context.State.Character.Active = weakenedCharacter;
            context.State.Currency.Gold = context.State.Character.Active.Gold;
            enteredWeakness = true;
        }
        else
        {
            logger.LogError(
                "Could not persist weakness for character {CharacterId} while returning from dungeon {DungeonId}; the client will retain normal stamina.",
                context.State.Character.Active.Id,
                leavingDungeonId);
        }
    }

    context.State.Dungeon.CurrentDungeonId = 0;
    context.State.Dungeon.Layout = null;
    context.State.Dungeon.Room = null;
    context.State.Dungeon.InSelection = false;
    context.State.Dungeon.SelectionDeadline = null;
    context.State.Dungeon.SelectionWorldMap = null;
    context.State.Dungeon.AliveMonsters.Clear();
    context.State.Dungeon.ReportedBossDeaths.Clear();
    context.State.Dungeon.BossCascadeDeaths.Clear();
    context.State.Dungeon.DestroyedPassiveObjects.Clear();
    context.State.Dungeon.GroundItems.ResetDungeon();
    context.State.Dungeon.Run.Stop();
    context.State.Dungeon.TutorialActive = false;
    context.State.Dungeon.CharacterAlive = true;
    context.State.Dungeon.DeathGeneration++;
    context.State.Dungeon.DeathTimeout = null;
    context.State.Dungeon.Difficulty = 0;
    context.State.Dungeon.ClearEnabled = false;
    context.State.Dungeon.ResultSent = false;
    context.State.Dungeon.CardLayoutSent = false;
    context.State.Dungeon.CardRightsSent = false;
    context.State.Dungeon.CardSelectionDeadline = null;
    context.State.Dungeon.ClearReward = null;
    context.State.Dungeon.FreeCardIndex = null;
    context.State.Dungeon.GoldCardIndex = null;
    context.State.Dungeon.MonsterExperienceGained = 0;
    context.State.Dungeon.ClearExperience = null;
    context.State.Dungeon.ConsumedFatigue = 0;
    context.State.Dungeon.CreatureStomachCheckpointSeconds = null;
    context.State.Dungeon.LeveledUp = false;
    context.State.Dungeon.NormalMonsterKills = 0;
    context.State.Dungeon.ChampionMonsterKills = 0;
    context.State.Dungeon.BossMonsterKills = 0;
    context.State.Dungeon.ChampionRewardOrdinal = 0;
    context.State.Character.TownId = context.State.Dungeon.ReturnTownId;
    context.State.Character.AreaId = context.State.Dungeon.ReturnAreaId;
    context.State.Character.X = context.State.Dungeon.ReturnX;
    context.State.Character.Y = context.State.Dungeon.ReturnY;
    context.State.Character.Direction = context.State.Dungeon.ReturnDirection;
    context.State.Character.MovementValue = 100;
    await SessionLocationPersistence.SaveCurrentLocationAsync(store, context.State, cancellationToken);

    var townState = CharactersPacketBuilders.CreateUserState(
        context.State.Character.LocalUserId,
        state: 0);
    await context.Outbound.SendAsync(townState, "DUNGEON", cancellationToken);

    var townUser = new GameTownUser(
        context.State.Character.LocalUserId,
        context.State.Character.X,
        context.State.Character.Y,
        context.State.Character.Direction);
    var userArea = TownPacketBuilders.CreateUserArea(
        context.State.Character.TownId,
        context.State.Character.AreaId,
        townUser);
    await context.Outbound.SendAsync(userArea, "DUNGEON", cancellationToken);

    var areaUsers = TownPacketBuilders.CreateAreaUsers(
        context.State.Character.TownId,
        context.State.Character.AreaId,
        [townUser]);
    await context.Outbound.SendAsync(areaUsers, "DUNGEON", cancellationToken);
    context.State.Character.AreaRosterInitialized = true;
    await SendTownFatigueStateAsync(context, cancellationToken);
    await SendTownAppearanceAsync(context, waitForTownActor: true, cancellationToken);
    await SendSilentTownStaminaStateAsync(context, cancellationToken);
    await SendEnterGameWorldCompleteAsync(context, cancellationToken);
    // The town return bypasses SET_USER_AREA, so refresh the
    // town presence (and any same-area residents' view) here.
    PublishTownPresence(context);
    if (leveledUp)
    {
        await SendCurrentSkillInfoAsync(context, cancellationToken);
        await SendAcceptableQuestListAsync(context, cancellationToken);
    }

    if (leftDungeonSelection && leavingDungeonId == 0)
    {
        logger.LogInformation(
            "Dungeon selection returned user {UserId} to town through {Reason}.",
            context.State.Character.LocalUserId,
            reason);
    }
    else
    {
        logger.LogInformation(
            "Dungeon {DungeonId} returned user {UserId} to town through {Reason}; exp={Experience}, weakened={IsWeakened}, enteredWeakness={EnteredWeakness}, blackDiamond={HasBlackDiamond}.",
            leavingDungeonId,
            context.State.Character.LocalUserId,
            reason,
            gainedExperience,
            context.State.Character.Active?.IsWeakened ?? false,
            enteredWeakness,
            hasBlackDiamond);
    }
    }

    public async Task SendDungeonSelectionEligibilityAsync(
    ConnectionContext context,
    CancellationToken cancellationToken = default)
    {
    var questsMet = context.State.Dungeon.SelectionWorldMap?.MeetsHellQuests(context.State.Quest.CompletedIds) == true;
    var missingItems = context.State.Dungeon.SelectionWorldMap is { HasHellDungeon: true }
        && !context.State.Dungeon.SelectionWorldMap.HasHellItems(context.State.Inventory.Main.Values);
    var packet = DungeonPacketBuilders.CreateEnterSelectDungeon(
        questsMet, missingItems ? [(ushort)0] : []);
    await context.Outbound.SendAsync(packet, "DUNGEON", cancellationToken);
    logger.LogInformation(
        "World map {WorldMapId} eligibility for {CharacterId}: hellQuests={QuestsMet}, missingHellItems={MissingItems}.",
        context.State.Dungeon.SelectionWorldMap?.Id, context.State.Character.Active?.Id, questsMet, missingItems);
    }

    public async Task ConsumeDungeonRoomFatigueAsync(
    ConnectionContext context,
    byte roomX,
    byte roomY,
    CancellationToken cancellationToken = default)
    {
    if (context.State.Character.Active is null || context.State.Dungeon.CurrentDungeonId == 0)
    {
        return;
    }

    var premiumFatigue = HasActivePremiumService(context, GameProtocolConstants.BlackDiamondServiceType)
        ? GameProtocolConstants.BlackDiamondBonusFatigue
        : (ushort)0;
    var maximumFatigue = checked((ushort)(
        GameProtocolConstants.DefaultMaximumFatigue + premiumFatigue));
    var consumption =
        await store.ConsumeDungeonRoomFatigueAsync(
            context.State.Character.Active.Id,
            maximumFatigue,
            cancellationToken);
    if (consumption.Character is null)
    {
        logger.LogWarning(
            "Could not consume dungeon-room fatigue because character {CharacterId} was not found.",
            context.State.Character.Active.Id);
        return;
    }

    context.State.Character.Active = consumption.Character;
    if (!consumption.Consumed)
    {
        logger.LogInformation(
            "Dungeon {DungeonId} room ({RoomX},{RoomY}) consumed no fatigue because user {UserId} is already at {UsedFatigue}/{MaximumFatigue}.",
            context.State.Dungeon.CurrentDungeonId,
            roomX,
            roomY,
            context.State.Character.LocalUserId,
            consumption.UsedFatigue,
            maximumFatigue);
        return;
    }

    context.State.Dungeon.ConsumedFatigue++;

    var fatigue = StaminaPacketBuilders.CreateFatigue(
        consumption.UsedFatigue,
        maximumFatigue,
        premiumFatigue);
    await context.Outbound.SendAsync(fatigue, "DUNGEON", cancellationToken);
    logger.LogInformation(
        "Dungeon {DungeonId} room ({RoomX},{RoomY}) consumed one fatigue for user {UserId}: used {PreviousUsedFatigue}->{UsedFatigue}, remaining={RemainingFatigue}/{MaximumFatigue}.",
        context.State.Dungeon.CurrentDungeonId,
        roomX,
        roomY,
        context.State.Character.LocalUserId,
        consumption.PreviousUsedFatigue,
        consumption.UsedFatigue,
        maximumFatigue - consumption.UsedFatigue,
        maximumFatigue);
    }

    public async Task GrantDungeonCreatureExperienceAsync(
    ConnectionContext context,
    CancellationToken cancellationToken = default)
    {
    if (context.State.Dungeon.ConsumedFatigue == 0
        || context.State.Character.Active is null
        || !context.State.Inventory.Creature.TryGetValue(
            CharacterCreatureInventoryLayout.EquippedCreatureSlot,
            out var creature)
        || creature.CountOrValue == 0
        || (creature.CreatureStomach ?? 100) == 0
        || !itemCatalog.TryGetDefinition(
            creature.ItemId,
            out var creatureDefinition)
        || !CharacterCreatureInventoryLayout.IsCreature(
            creatureDefinition)
        || creatureDefinition.CreatureSubType == 1)
    {
        context.State.Dungeon.ConsumedFatigue = 0;
        return;
    }

    var previousLevel = creature.CreatureLevel ?? 1;
    var bonusRate =
        GetEquippedCreatureExperienceBonusRate(context, previousLevel);
    var grant = creatureExperienceCatalog.Grant(
        creature.CreatureExperience ?? 0,
        previousLevel,
        creatureDefinition.CreatureSpecies,
        context.State.Dungeon.ConsumedFatigue,
        bonusRate);
    if (grant.GrantedExperience == 0)
    {
        context.State.Dungeon.ConsumedFatigue = 0;
        return;
    }

    var plannedCreatureInventory = context.State.Inventory.Creature.ToDictionary();
    plannedCreatureInventory[
        CharacterCreatureInventoryLayout.EquippedCreatureSlot] =
        creature with
        {
            CreatureExperience = grant.Experience,
            CreatureLevel = grant.Level
        };
    var saved = await store.SaveCharacterItemSpacesAsync(
        context.State.Login.AccountName,
        context.State.Character.Active.Id,
        context.State.Currency.Gold,
        context.State.Inventory.Main.Values,
        context.State.Inventory.Equipped.Values,
        context.State.Inventory.Warehouse.Values,
        cancellationToken,
        avatarInventory: context.State.Inventory.Avatar.Values,
        creatureInventory: plannedCreatureInventory.Values);
    if (saved is null)
    {
        logger.LogWarning(
            "Could not persist Creature experience for character {CharacterId} after clearing dungeon {DungeonId}.",
            context.State.Character.Active.Id,
            context.State.Dungeon.CurrentDungeonId);
        return;
    }

    context.State.Inventory.Creature = plannedCreatureInventory;
    context.State.Character.Active = context.State.Character.Active with
    {
        CreatureInventory = plannedCreatureInventory.Values
            .OrderBy(item => item.Slot)
            .ToList()
    };
    context.State.Dungeon.ConsumedFatigue = 0;
    var experienceNotification =
        PetsPacketBuilders.CreateCreatureGainExperience(
            grant.Level,
            grant.Experience);
    await context.Outbound.SendAsync(experienceNotification, "DUNGEON", cancellationToken);
    logger.LogInformation(
        "Dungeon {DungeonId} granted Creature uid {CreatureUid} {GrantedExperience} experience from {ConsumedFatigue} fatigue and {BonusExperience} artifact bonus: exp {PreviousExperience}->{Experience}, level {PreviousLevel}->{Level}.",
        context.State.Dungeon.CurrentDungeonId,
        creature.CountOrValue,
        grant.GrantedExperience,
        grant.BaseExperience,
        grant.BonusExperience,
        grant.PreviousExperience,
        grant.Experience,
        grant.PreviousLevel,
        grant.Level);
    }
}
