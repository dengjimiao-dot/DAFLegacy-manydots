using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// Town-scene senders (07 §4.7): appearance/equipment projections,
/// fatigue and stamina state, presence publishing.
/// </summary>
public sealed partial class ChannelSessionSenders
{
    public async Task SendCurrentEquipmentStateAsync(
    ConnectionContext context,
    GameCharacterCombatStats? validatedStats = null,
    CancellationToken cancellationToken = default)
    {
        if (context.State.Character.Active is null)
        {
            return;
        }

        var projectedStats = validatedStats
            ?? characterStatCatalog.Get(context.State.Character.Active);
        var equipment = CharactersPacketBuilders.CreateCurrentCharacterDetails(
            context.State.Character.LocalUserId,
            experience: context.State.Character.Active.Experience,
            learnedSkills: GetCurrentGameSkills(context),
            combatStats: projectedStats,
            equippedItems: builders.CreateWornEquipmentSnapshot(
                context.State.Inventory.Equipped.Values,
                context.State.Inventory.Creature),
            equippedCreatureLevel: CharacterPacketBuilders.GetEquippedCreatureLevel(
                context.State.Inventory.Creature),
            staminaRecoveryPercentage: GetProjectedStaminaRecovery(context));
        await context.Queue.SendAsync(equipment, "CHARACTER_DETAILS", cancellationToken);
        context.State.Character.LastProjectedCombatStats = projectedStats;
    }

    public async Task SendTownAppearanceAsync(
    ConnectionContext context,
    bool waitForTownActor = false,
    CancellationToken cancellationToken = default)
    {
        if (context.State.Character.Active is null || context.State.Dungeon.CurrentDungeonId != 0)
        {
            return;
        }

        if (waitForTownActor)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        // The original server equips the Creature and establishes
        // its town state before broadcasting the mode-zero basic
        // user record containing item id, name and alive state.
        await SendEquippedCreatureHungerAsync(
            context,
            // Only AREA_USERS reconstruction needs to replay a
            // persisted death marker. Ordinary equipment updates
            // keep the transition de-duplicated.
            forceDeathNotification: waitForTownActor,
            cancellationToken);

        var appearance = builders.CreateTownAppearancePacket(
            context.State.Character.Active,
            context.State.Character.LocalUserId,
            context.State.Inventory.Equipped.Values,
            context.State.Inventory.Creature.Values,
            HasActivePremiumService(context, GameProtocolConstants.BlackDiamondServiceType));
        await context.Queue.SendAsync(appearance, "TOWN_APPEARANCE", cancellationToken);

        // Mode zero is the compact town-actor appearance
        // projection: Avatar slots 0..8, weapon slot 9, plus the
        // dedicated title and Creature fields. It drops other worn
        // objects omitted from its compact list, so immediately
        // rebuild the authoritative equipment projection with
        // subtype one.
        await SendCurrentEquipmentStateAsync(context, cancellationToken: cancellationToken);
        await SendInitialStaminaStateAsync(context, cancellationToken);
    }

    public async Task ValidateAndCorrectCharacterStatsAsync(
    ConnectionContext context,
    string reason,
    CancellationToken cancellationToken = default)
    {
        if (context.State.Character.Active is null)
        {
            return;
        }

        var validation = characterStatCatalog.Validate(
            context.State.Character.Active,
            context.State.Character.LastProjectedCombatStats);
        if (context.State.Dungeon.CurrentDungeonId == 0)
        {
            // Mode zero first updates level/growtype on the town
            // actor. Mode one then overwrites the unequipped stat
            // block with the PVF-derived authoritative snapshot.
            await SendTownAppearanceAsync(context, cancellationToken: cancellationToken);
        }
        else
        {
            await SendCurrentEquipmentStateAsync(context, validation.Expected, cancellationToken);
        }

        RuntimeLogger.Info(
            "Validated PVF character attributes after {Reason} for {CharacterId}; level={Level}, job={Job}, growType={GrowType}, awakening={AwakeningType}, correctionRequired={CorrectionRequired}, mismatches={Mismatches}.",
            reason,
            context.State.Character.Active.Id,
            context.State.Character.Active.Level,
            context.State.Character.Active.Job,
            context.State.Character.Active.GrowType & 0x0F,
            Math.Max(context.State.Character.Active.AwakeningType, context.State.Character.Active.GrowType >> 4),
            !validation.IsCorrect,
            validation.MismatchedFields.Count == 0
                ? "none"
                : string.Join(',', validation.MismatchedFields));
    }

    public async Task SendInitialStaminaStateAsync(
    ConnectionContext context,
    CancellationToken cancellationToken = default)
    {
        if (context.State.Weakness.StaminaStateInitialized || context.State.Character.Active is null)
        {
            return;
        }

        // DFLegacy NOTI 4 owns the weakness percentage. Persisted
        // weakness is restored after the authoritative combat-stat
        // projection; active black diamond always projects full stamina.
        var stamina = StaminaPacketBuilders.CreateStamina(
            GetProjectedStaminaRecovery(context));
        await context.Queue.SendAsync(stamina, "STAMINA_NOTI", cancellationToken);
        context.State.Weakness.StaminaStateInitialized = true;
    }

    // ---- Inventory-move senders and the Creature-move planner cluster
    // (migrated verbatim from EntranceService; the dungeon branches keep
    // delegating wrappers until group 13 migrates).

    public async Task SendPremiumServiceInfoAsync(
    ConnectionContext context,
    byte serviceType,
    uint remainSeconds,
    CancellationToken cancellationToken = default)
    {
        if (remainSeconds == 0)
        {
            context.State.Premium.ExpiryUnix.TryRemove(serviceType, out _);
        }
        else
        {
            var expiryUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                + (long)remainSeconds;
            context.State.Premium.ExpiryUnix[serviceType] = expiryUnix;
        }

        var premiumNotification =
            StaminaPacketBuilders.CreatePremiumServiceNotification(
                action: GameProtocolConstants.PremiumServiceUpdateAction,
                serviceType,
                remainSeconds);
        await context.Queue.SendAsync(premiumNotification, "PREMIUM_INFO", cancellationToken);

        if (serviceType == GameProtocolConstants.BlackDiamondServiceType
            && context.State.Character.Active is not null
            && context.State.Dungeon.CurrentDungeonId == 0)
        {
            var appearance = builders.CreateTownAppearancePacket(
                context.State.Character.Active,
                context.State.Character.LocalUserId,
                context.State.Inventory.Equipped.Values,
                context.State.Inventory.Creature.Values,
                HasActivePremiumService(context,
                    GameProtocolConstants.BlackDiamondServiceType));
            await context.Queue.SendAsync(appearance, "PREMIUM_INFO", cancellationToken);
            await SendTownFatigueStateAsync(context, cancellationToken);
        }
    }

    public async Task SendTownFatigueStateAsync(
    ConnectionContext context,
    CancellationToken cancellationToken = default)
    {
        // AREA_USERS constructs the town HUD. EVENT_INFO then selects
        // and initializes its normal/premium fatigue-bar resources.
        var eventInfo = StaminaPacketBuilders.CreateEventInfo();
        await context.Queue.SendAsync(eventInfo, "TOWN_STAMINA", cancellationToken);

        var premiumFatigue = HasActivePremiumService(
                context,
                GameProtocolConstants.BlackDiamondServiceType)
            ? GameProtocolConstants.BlackDiamondBonusFatigue
            : (ushort)0;
        var maximumFatigue = checked((ushort)(
            GameProtocolConstants.DefaultMaximumFatigue + premiumFatigue));
        var fatigue = StaminaPacketBuilders.CreateFatigue(
            usedFatigue: Math.Min(
                context.State.Character.Active?.UsedFatigue ?? 0,
                maximumFatigue),
            maximumFatigue: maximumFatigue,
            premiumFatigue: premiumFatigue);
        await context.Queue.SendAsync(fatigue, "TOWN_STAMINA", cancellationToken);
    }

    // ---- Town presence and scene-transition senders (migrated verbatim
    // for the R4 Town group).

    public void PublishTownPresence(ConnectionContext context)
    {
        if (context.State.Character.Active is null)
        {
            return;
        }

        var appearance = builders.CreateTownAppearancePacket(
            context.State.Character.Active,
            context.State.Character.LocalUserId,
            context.State.Inventory.Equipped.Values,
            context.State.Inventory.Creature.Values,
            HasActivePremiumService(context, GameProtocolConstants.BlackDiamondServiceType));
        partyCoordinator.PublishTownPresence(
            new PartyPresenceUpdate(
                context.Runtime.Id,
                context.State.Character.Active.Id,
                context.State.Login.AccountUid,
                context.State.Character.LocalUserId,
                context.State.Character.Active.Name,
                (byte)Math.Clamp(context.State.Character.Active.Level, (byte)1, byte.MaxValue),
                (byte)Math.Clamp(context.State.Character.Active.Job, byte.MinValue, byte.MaxValue),
                context.State.Character.TownId,
                context.State.Character.AreaId,
                context.State.Character.X,
                context.State.Character.Y,
                context.State.Character.Direction),
            appearance);
    }

    public async Task SendEnterGameWorldCompleteAsync(
    ConnectionContext context,
    CancellationToken cancellationToken = default)
    {
        // USERINFO locks keyboard and mouse commands at priority
        // two. DFLegacy notification 136 clears both locks after the
        // local actor has been constructed by AREA_USERS.
        var gameWorldComplete =
            ConnectionPacketBuilders.CreateEnterGameWorldComplete();
        await context.Queue.SendAsync(gameWorldComplete, "TOWN_NOTI", cancellationToken);
    }

    public async Task SendSilentTownStaminaStateAsync(
    ConnectionContext context,
    CancellationToken cancellationToken = default)
    {
        if (context.State.Character.Active is null || context.State.Dungeon.CurrentDungeonId != 0)
        {
            return;
        }

        // AREA_USERS recreates the town actor with empty stamina.
        // NOTI 33 restores a normal town actor without a recovery
        // sound, but its client handler also fills current HP/MP.
        // It must therefore never be sent while inside a dungeon.
        var stamina = context.State.Character.Active.IsWeakened
            && !HasActivePremiumService(
                context,
                GameProtocolConstants.BlackDiamondServiceType)
                ? StaminaPacketBuilders.CreateStamina(
                    context.State.Character.Active.WeaknessRecovery)
                : DungeonPacketBuilders.CreateFailClearDungeon(
                    DungeonWeaknessPolicy.FullStamina);
        await context.Queue.SendAsync(stamina, "TOWN_NOTI", cancellationToken);
    }

    // ---- Dungeon flow helpers (migrated verbatim for R5: return-to-town,
    // selection eligibility, room fatigue, creature experience, clear-reward
    // cards and quest-item grants).

    public async Task SendWeaknessRecoveryNotificationSafeAsync(
        ConnectionContext context,
        byte recovery)
    {
        try
        {
            if (context.State.Character.Active is null)
            {
                return;
            }

            var stamina = StaminaPacketBuilders.CreateStamina(recovery);
            await context.Queue.SendAsync(stamina, "STAMINA_NOTI", context.Disconnecting);
        }
        catch (OperationCanceledException) when (context.Disconnecting.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            RuntimeLogger.Warn(
                exception,
                "Could not send weakness recovery notification to character {CharacterId}.",
                context.State.Character.Active?.Id);
        }
    }
}
