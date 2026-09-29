using System.Text;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// SELECT_CHARACTER (CMD 4): claims the character lease (a displaced session
/// is kicked and awaited), repairs and adopts the persisted five-space
/// inventory, redeems legacy Cera-shop contract stacks, migrates skills,
/// quests and dungeon progress, then streams the character details and the
/// town initialization burst. Moved verbatim from the legacy branch (07 §5
/// group 3, §6 R1); the initialization burst builders live on
/// <see cref="CharacterPacketBuilders"/>.
/// </summary>
public sealed partial class SelectCharacterHandler(
    JsonGameStore store,
    ItemCatalog itemCatalog,
    SkillCatalog skillCatalog,
    QuestCatalog questCatalog,
    DungeonCatalog dungeonCatalog,
    CharacterStatCatalog characterStatCatalog,
    PremiumBenefitCatalog premiumBenefitCatalog,
    CharacterSessionRegistry characterSessions,
    CharacterPacketBuilders builders,
    ChannelSessionSenders senders,
    ILogger<SelectCharacterHandler> logger) : IGameCommandHandler
{
    private static readonly Encoding ClientEncoding = PvfEncodings.Cp936Lossy();

    public ushort ProtocolId => (byte)CmdPacketType.SELECT_CHARACTER;

    public string Name => "SELECT_CHARACTER";

    // The branch performs its own active-character and lease handling; the
    // coarse gate stays open for the pre-selection flow (07 §4.8.1).
    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        var characters = await store.GetCharactersAsync(session.Login.AccountUid, cancellationToken);
        var slot = request.Body.Length >= 3 ? request.Body[2] : 0;
        var character = characters.FirstOrDefault(candidate =>
            candidate.Slot == slot);
        if (character is null)
        {
            var selectError = ConnectionPacketBuilders.CreateCommandError(request, errorCode: 2);
            await context.Outbound.SendAsync(selectError, Name, cancellationToken);
            return;
        }

        if (session.Character.Lease is not null
            && session.Character.Lease.CharacterNo != character.CharacterNo)
        {
            await SessionLocationPersistence.SaveCurrentLocationAsync(store, session, cancellationToken);
            characterSessions.Release(session.Character.Lease);
            session.Character.Lease = null;
            session.Character.Active = null;
            session.Character.LastProjectedCombatStats = null;
            session.Premium.ExpiryUnix.Clear();
        }

        session.Character.Lease = await characterSessions.ClaimAsync(
            character.CharacterNo,
            character.Id,
            context.Runtime.Id,
            () => context.KickConnection?.Invoke(),
            cancellationToken,
            () => session.Events.Writer.TryWrite(new ConnectionEvent(ConnectionEventKind.MailAlarm)),
            (serviceType, remainSeconds) =>
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var expiry = remainSeconds > long.MaxValue - now
                    ? long.MaxValue
                    : now + (long)remainSeconds;
                if (remainSeconds == 0)
                {
                    session.Premium.ExpiryUnix.TryRemove(serviceType, out _);
                }
                else
                {
                    session.Premium.ExpiryUnix[serviceType] = expiry;
                }
                session.Events.Writer.TryWrite(new ConnectionEvent(
                    ConnectionEventKind.PremiumServiceUpdate,
                    serviceType,
                    remainSeconds > int.MaxValue ? int.MaxValue : (int)remainSeconds));
            },
            value => session.Events.Writer.TryWrite(new ConnectionEvent(
                ConnectionEventKind.CeraValueUpdated,
                Value: value > int.MaxValue
                    ? int.MaxValue
                    : (int)value)),
            () => session.Events.Writer.TryWrite(new ConnectionEvent(ConnectionEventKind.FatigueReset)),
            (messageType, targetAreaUserId, messageBytes) =>
                _ = SendTypedMessageNotificationSafeAsync(
                    context,
                    messageType,
                    targetAreaUserId,
                    messageBytes),
            (messageType, ownerFlag, targetAreaUserId, senderName, messageBytes) =>
                _ = SendMegaphoneNotificationSafeAsync(
                    context,
                    messageType,
                    ownerFlag,
                    targetAreaUserId,
                    senderName,
                    messageBytes),
            messageBytes =>
                _ = SendPopupNotificationSafeAsync(context, messageBytes),
            recovery =>
            {
                session.Events.Writer.TryWrite(new ConnectionEvent(
                    ConnectionEventKind.WeaknessRecovered,
                    Value: recovery));
                _ = SendWeaknessRecoveryNotificationSafeAsync(context, recovery);
            },
            (dungeonId, maximumDifficulty) =>
            {
                if (session.Character.Active is null)
                {
                    return;
                }

                session.Character.Active = session.Character.Active with
                {
                    UnlockedDungeonIds =
                        (session.Character.Active.UnlockedDungeonIds ?? [])
                        .Append(dungeonId)
                        .Where(id => id != 0)
                        .Distinct()
                        .OrderBy(id => id)
                        .ToList(),
                    DungeonProgress = DungeonDifficultyProgression.Normalize(
                            (session.Character.Active.DungeonProgress ?? [])
                            .Where(entry => entry.DungeonId != dungeonId)
                            .Append(new CharacterDungeonProgressRecord(
                                dungeonId,
                                maximumDifficulty)))
                        .ToList()
                };
                _ = SendDungeonPermissionsSafeAsync(context);
            });

        // Claim the wire identity for this session.
        session.Character.LocalUserId = (ushort)(character.CharacterNo & 0xFFFF);
        if (session.Character.LocalUserId == 0)
        {
            session.Character.LocalUserId = 1;
        }

        // A displaced connection performs its final save before
        // releasing the character lease. Reload after the claim
        // so this session cannot start from the pre-kick snapshot.
        character = (await store.GetCharactersAsync(session.Login.AccountUid, cancellationToken))
            .FirstOrDefault(candidate =>
                candidate.CharacterNo == character.CharacterNo)
            ?? character;
        var persistedWarehouseCapacity = character.WarehouseCapacity;
        session.Inventory.WarehouseCapacity = CharacterWarehouseProgression.Normalize(
            persistedWarehouseCapacity);
        character = character with
        {
            WarehouseCapacity = session.Inventory.WarehouseCapacity
        };
        session.Character.Active = character;
        session.Character.LastProjectedCombatStats = null;
        session.Weakness.StaminaStateInitialized = false;
        session.Currency.Gold = Math.Max(0, character.Gold);
        session.Currency.Cash = Math.Max(0, character.Cash);
        session.Currency.VictoryPoints = character.VictoryPoints;
        bool IsAvatarItem(CharacterItemRecord item) =>
            itemCatalog.TryGetDefinition(item.ItemId, out var definition)
            && definition.InventoryCategory == ItemInventoryCategory.Avatar;
        bool IsCreatureItem(CharacterItemRecord item) =>
            itemCatalog.TryGetDefinition(item.ItemId, out var definition)
            && definition.InventoryCategory == ItemInventoryCategory.Creature;

        var savedInventory = character.Inventory
            ?? builders.CreateStarterInventory(session.Login.AccountUid);
        var savedWarehouse = character.Warehouse ?? [];
        var residualWarehouseUpgrades =
            CharacterWarehouseProgression.SettleUpgradeItems(
                savedInventory,
                savedWarehouse,
                session.Inventory.WarehouseCapacity);
        savedInventory = residualWarehouseUpgrades.Inventory.ToList();
        savedWarehouse = residualWarehouseUpgrades.Warehouse.ToList();
        session.Inventory.WarehouseCapacity = residualWarehouseUpgrades.Capacity;
        character = character with
        {
            Inventory = savedInventory,
            Warehouse = savedWarehouse,
            WarehouseCapacity = session.Inventory.WarehouseCapacity
        };
        session.Character.Active = character;
        var savedEquipment = character.Equipment ?? [];
        var savedAvatarInventory = character.AvatarInventory ?? [];
        var savedCreatureInventory = character.CreatureInventory ?? [];
        var displacedMainItems = savedAvatarInventory
            .Concat(savedCreatureInventory)
            .Where(item => !IsAvatarItem(item) && !IsCreatureItem(item))
            .Select(item => item with { Slot = ushort.MaxValue })
            .ToList();
        var displacedAvatarItems = savedInventory
            .Where(IsAvatarItem)
            .Concat(savedWarehouse.Where(IsAvatarItem))
            .Concat(savedCreatureInventory.Where(IsAvatarItem))
            .Select(item => item with { Slot = ushort.MaxValue })
            .ToList();
        var displacedCreatureItems = savedInventory
            .Where(IsCreatureItem)
            .Concat(savedAvatarInventory.Where(IsCreatureItem))
            .Select(item => item with { Slot = ushort.MaxValue })
            .ToList();
        var repairedAvatarEquipment = false;
        var repairedEquipmentState = false;
        session.Inventory.Equipped = new Dictionary<ushort, CharacterItemRecord>();
        foreach (var item in savedEquipment
                     .Where(item => item.ItemId != 0)
                     .OrderBy(item => item.Slot))
        {
            if (itemCatalog.TryGetDefinition(item.ItemId, out var definition)
                && definition.InventoryCategory
                    == ItemInventoryCategory.Avatar)
            {
                var normalizedAvatarItem = CharacterPacketBuilders.NormalizeAvatarInstance(
                    CharacterItemSealing.UnsealWhenEquipped(
                        item,
                        definition));
                if (CharacterAvatarInventoryLayout.IsCompatibleWornSlot(
                        definition,
                        item.Slot)
                    && session.Inventory.Equipped.TryAdd(
                        item.Slot,
                        normalizedAvatarItem))
                {
                    repairedAvatarEquipment |=
                        !normalizedAvatarItem.Equals(item);
                    if (item.CountOrValue > 1)
                    {
                        displacedAvatarItems.Add(item with
                        {
                            Slot = ushort.MaxValue,
                            CountOrValue = item.CountOrValue - 1
                        });
                    }

                    continue;
                }

                displacedAvatarItems.Add(item with
                {
                    Slot = ushort.MaxValue
                });
                continue;
            }

            if (definition?.InventoryCategory
                == ItemInventoryCategory.Creature)
            {
                displacedCreatureItems.Add(item with
                {
                    Slot = ushort.MaxValue
                });
                continue;
            }

            var normalizedEquipmentItem =
                CharacterItemSealing.UnsealWhenEquipped(
                    item,
                    itemCatalog) with
            {
                Durability = itemCatalog.NormalizeDurability(
                    item.ItemId,
                    item.Durability)
            };
            if (item.Slot is >= 9 and < 23
                && session.Inventory.Equipped.TryAdd(
                    item.Slot,
                    normalizedEquipmentItem))
            {
                repairedEquipmentState |=
                    !normalizedEquipmentItem.Equals(item);
                continue;
            }

            displacedMainItems.Add(item with { Slot = ushort.MaxValue });
        }

        var normalizedInventory = CharacterInventoryPlanner.Normalize(
            savedInventory
                .Where(item => !IsAvatarItem(item) && !IsCreatureItem(item))
                .Concat(displacedMainItems),
            itemCatalog);
        var normalizedAvatarInventory =
            CharacterAvatarInventoryPlanner.Normalize(
                savedAvatarInventory
                    .Where(IsAvatarItem)
                    .Concat(displacedAvatarItems),
                itemCatalog);
        var normalizedWarehouse =
            CharacterInventoryPlanner.NormalizeWarehouse(
                savedWarehouse.Where(item => !IsAvatarItem(item)),
                itemCatalog,
                session.Inventory.WarehouseCapacity);
        var normalizedCreatureInventory =
            CharacterCreatureInventoryPlanner.Normalize(
                savedCreatureInventory
                    .Where(IsCreatureItem)
                    .Concat(displacedCreatureItems),
                itemCatalog);
        var loginOverflowCandidates = normalizedInventory.OverflowItems
            .Concat(normalizedAvatarInventory.OverflowItems)
            .Concat(normalizedWarehouse.OverflowItems)
            .Concat(normalizedCreatureInventory.OverflowItems)
            .ToArray();
        var normalizedWarehouseUpgrades =
            senders.PlanWarehouseUpgradeSettlement(context, 
                normalizedInventory.Inventory.Values,
                normalizedWarehouse.Warehouse.Values,
                loginOverflowCandidates.Select(item => item.ItemId));
        var consumedOverflowWarehouseUpgrade =
            loginOverflowCandidates.Any(item =>
                CharacterWarehouseProgression
                    .TryGetItemTargetCapacity(item.ItemId, out _));
        session.Inventory.Main = normalizedWarehouseUpgrades.Inventory
            .ToDictionary(item => item.Slot);
        session.Inventory.Avatar = normalizedAvatarInventory.Inventory;
        session.Inventory.Warehouse = normalizedWarehouseUpgrades.Warehouse
            .ToDictionary(item => item.Slot);
        session.Inventory.WarehouseCapacity = normalizedWarehouseUpgrades.Capacity;
        session.Inventory.Creature = normalizedCreatureInventory.Inventory;
        var loginOverflowItems = loginOverflowCandidates
            .Where(item => !CharacterWarehouseProgression
                .TryGetItemTargetCapacity(item.ItemId, out _))
            .ToArray();
        var loginOverflowMails = CharacterPacketBuilders.CreateOverflowMailRequests(
            loginOverflowItems,
            "Login inventory repair");
        var migratedEquipmentSlots = displacedMainItems.Count != 0
            || displacedAvatarItems.Count != 0
            || savedInventory.Any(IsAvatarItem)
            || savedWarehouse.Any(IsAvatarItem)
            || savedAvatarInventory.Any(item => !IsAvatarItem(item))
            || savedInventory.Any(IsCreatureItem)
            || savedAvatarInventory.Any(IsCreatureItem)
            || savedCreatureInventory.Any(item => !IsCreatureItem(item))
            || repairedAvatarEquipment
            || repairedEquipmentState;
        if (character.Inventory is null
            || character.Equipment is null
            || character.Warehouse is null
            || character.AvatarInventory is null
            || character.CreatureInventory is null
            || normalizedInventory.Changed
            || normalizedAvatarInventory.Changed
            || normalizedWarehouse.Changed
            || normalizedCreatureInventory.Changed
            || residualWarehouseUpgrades.ConsumedItems
            || normalizedWarehouseUpgrades.ConsumedItems
            || consumedOverflowWarehouseUpgrade
            || persistedWarehouseCapacity != session.Inventory.WarehouseCapacity
            || migratedEquipmentSlots)
        {
            var repairedCharacter =
                await store.SaveCharacterItemSpacesAsync(
                session.Login.AccountName,
                character.Id,
                session.Currency.Gold,
                session.Inventory.Main.Values,
                session.Inventory.Equipped.Values,
                session.Inventory.Warehouse.Values,
                cancellationToken,
                loginOverflowMails,
                session.Inventory.Avatar.Values,
                session.Inventory.Creature.Values,
                session.Inventory.WarehouseCapacity);
            if (repairedCharacter is null)
            {
                characterSessions.Release(session.Character.Lease);
                session.Character.Lease = null;
                session.Character.Active = null;
                session.Character.LastProjectedCombatStats = null;
                session.Premium.ExpiryUnix.Clear();
                var repairError = ConnectionPacketBuilders.CreateCommandError(
                    request,
                    errorCode: 4);
                await context.Outbound.SendAsync(repairError, Name, cancellationToken);
                logger.LogError(
                    "Could not repair inventory layout for character {CharacterId}; selection was rejected to avoid losing an item.",
                    character.Id);
                return;
            }

            session.Character.Active = repairedCharacter;
            if (loginOverflowMails.Length > 0)
            {
                characterSessions.NotifyMailReceived(character.CharacterNo);
            }
        }

        if (builders.TryPlanLegacyPremiumContractRedemption(
                session.Inventory.Main,
                out var redeemedContractInventory,
                out var redeemedContractGrants))
        {
            var redeemedCharacter =
                await store.SaveCharacterCeraShopPurchaseAsync(
                    session.Login.AccountName,
                    session.Character.Active.Id,
                    session.Currency.Cash,
                    session.Currency.Gold,
                    redeemedContractInventory.Values,
                    session.Inventory.Equipped.Values,
                    cancellationToken,
                    avatarInventory: session.Inventory.Avatar.Values,
                    creatureInventory: session.Inventory.Creature.Values,
                    premiumServiceGrants: redeemedContractGrants,
                    expectedCash: session.Currency.Cash);
            if (redeemedCharacter is not null)
            {
                session.Character.Active = redeemedCharacter;
                session.Inventory.Main = redeemedContractInventory;
                logger.LogInformation(
                    "Redeemed {ContractCount} legacy Cera-shop contract stack(s) while selecting character {CharacterId}.",
                    redeemedContractGrants.Count,
                    session.Character.Active.Id);
            }
            else
            {
                logger.LogError(
                    "Could not redeem legacy Cera-shop contract items for character {CharacterId}; the items were retained.",
                    session.Character.Active.Id);
            }
        }

        var premiumStates = await store.GetPremiumServicesAsync(
            session.Login.AccountName,
            cancellationToken);
        var effectiveSkillLevel =
            premiumBenefitCatalog.GetEffectiveSkillLevel(
                session.Character.Active.Level,
                premiumStates
                    .Where(state => state.Active)
                    .Select(state => state.ServiceType));

        session.Skill.CurrentSp = Math.Max(0, session.Character.Active.Sp);
        if (session.Character.Active.Skills is null)
        {
            session.Skill.Current = skillCatalog
                .CreateInitialLayout(character.Job, CharacterPacketBuilders.GetInitialSkills(character))
                .ToList();
            session.Character.Active = await store.SaveCharacterSkillsAsync(
                session.Login.AccountName,
                character.Id,
                session.Skill.CurrentSp,
                session.Skill.Current,
                cancellationToken) ?? session.Character.Active;
        }
        else
        {
            session.Skill.Current = session.Character.Active.Skills
                .Where(skill => skill.Level > 0)
                .OrderBy(skill => skill.Slot)
                .ToList();
            var normalizedSkills = skillCatalog.NormalizeLayout(
                session.Character.Active.Job,
                session.Character.Active.GrowType,
                session.Character.Active.AwakeningType,
                session.Skill.Current,
                out var skillLayoutChanged);
            if (skillLayoutChanged)
            {
                session.Skill.Current = normalizedSkills.ToList();
                session.Character.Active = await store.SaveCharacterSkillsAsync(
                    session.Login.AccountName,
                    character.Id,
                    session.Skill.CurrentSp,
                    session.Skill.Current,
                    cancellationToken) ?? session.Character.Active;
                logger.LogInformation(
                    "Migrated character {CharacterId} to the DFLegacy skill-slot layout.",
                    character.Id);
            }
        }

        var reconciledSkills = skillCatalog.ReconcileOverLevelSkills(
            session.Character.Active.Job,
            session.Character.Active.GrowType,
            session.Character.Active.AwakeningType,
            effectiveSkillLevel,
            session.Skill.Current,
            out var refundedContractSkillPoints,
            out var contractSkillRefunds);
        if (!session.Skill.Current.SequenceEqual(reconciledSkills))
        {
            session.Skill.Current = reconciledSkills.ToList();
            session.Skill.CurrentSp = checked((int)Math.Min(
                int.MaxValue,
                (long)session.Skill.CurrentSp + refundedContractSkillPoints));
            session.Character.Active = await store.SaveCharacterSkillsAsync(
                session.Login.AccountName,
                character.Id,
                session.Skill.CurrentSp,
                session.Skill.Current,
                cancellationToken) ?? session.Character.Active;
            logger.LogInformation(
                "Reconciled {RefundCount} over-level skill(s) for character {CharacterId} at effective level {EffectiveLevel}; refunded {RefundedSp} SP: {Refunds}.",
                contractSkillRefunds.Count,
                character.Id,
                effectiveSkillLevel,
                refundedContractSkillPoints,
                string.Join(
                    ", ",
                    contractSkillRefunds.Select(refund =>
                        $"{refund.SkillId}:{refund.PreviousLevel}->{refund.CurrentLevel}(+{refund.SkillPoints})")));
        }

        session.Quest.Current = (session.Character.Active.Quests ?? [])
            .Where(quest => quest.QuestId != ushort.MaxValue)
            .DistinctBy(quest => quest.QuestId)
            .Take(3)
            .ToList();
        session.Quest.CompletedIds = (session.Character.Active.CompletedQuestIds ?? [])
            .ToHashSet();
        if (session.Character.Active.Quests is null
            || session.Character.Active.CompletedQuestIds is null
            || !session.Quest.Current.SequenceEqual(session.Character.Active.Quests.Take(3))
            || session.Quest.CompletedIds.Count != session.Character.Active.CompletedQuestIds.Distinct().Count())
        {
            session.Character.Active = await store.SaveCharacterQuestsAsync(
                session.Login.AccountName,
                character.Id,
                session.Quest.Current,
                session.Quest.CompletedIds,
                cancellationToken) ?? session.Character.Active;
        }

        var migrateLegacyDungeonProgress =
            session.Character.Active.DungeonProgress is null;
        var migratedDungeonUnlocks = (session.Character.Active.UnlockedDungeonIds ?? [])
            .Concat(HiddenDungeonUnlockCatalog.ResolveUnlockedDungeonIds(
                session.Quest.Current.Select(quest => quest.QuestId)
                    .Concat(session.Quest.CompletedIds),
                questCatalog,
                dungeonCatalog))
            .Where(dungeonId => dungeonId != 0)
            .Distinct()
            .OrderBy(dungeonId => dungeonId)
            .ToArray();
        var storedDungeonUnlocks = (session.Character.Active.UnlockedDungeonIds ?? [])
            .Where(dungeonId => dungeonId != 0)
            .Distinct()
            .OrderBy(dungeonId => dungeonId)
            .ToArray();
        if (session.Character.Active.UnlockedDungeonIds is null
            || !storedDungeonUnlocks.SequenceEqual(migratedDungeonUnlocks))
        {
            session.Character.Active = await store.SaveCharacterDungeonUnlocksAsync(
                session.Login.AccountName,
                character.Id,
                migratedDungeonUnlocks,
                cancellationToken) ?? session.Character.Active;
        }

        var normalizedDungeonProgress = migrateLegacyDungeonProgress
            ? HiddenDungeonUnlockCatalog.ResolveVisibleDungeonIds(
                    dungeonCatalog.DungeonIds,
                    migratedDungeonUnlocks)
                .Select(dungeonId =>
                    new CharacterDungeonProgressRecord(dungeonId, 1))
                .ToArray()
            : DungeonDifficultyProgression.Normalize(
                session.Character.Active.DungeonProgress);
        var storedDungeonProgress =
            DungeonDifficultyProgression.Normalize(
                session.Character.Active.DungeonProgress);
        if (migrateLegacyDungeonProgress
            || !storedDungeonProgress.SequenceEqual(
                normalizedDungeonProgress))
        {
            session.Character.Active = await store.SaveCharacterDungeonProgressAsync(
                session.Login.AccountName,
                character.Id,
                normalizedDungeonProgress,
                cancellationToken) ?? session.Character.Active;
            if (migrateLegacyDungeonProgress)
            {
                logger.LogInformation(
                    "Migrated character {CharacterId} dungeon permissions to persistent maximum difficulties.",
                    character.Id);
            }
        }

        session.Character.TownId = session.Character.Active.TownId is >= 1 and <= 5
            ? session.Character.Active.TownId
            : (byte)1;
        var loginSpawn = GetTownGateSpawn(session.Character.TownId);
        session.Character.AreaId = loginSpawn.AreaId;
        session.Character.X = loginSpawn.X;
        session.Character.Y = loginSpawn.Y;
        session.Character.Direction = loginSpawn.Direction;
        session.Character.MovementValue = 100;
        session.Character.AreaRosterInitialized = false;
        session.Premium.ExpiryUnix.Clear();
        foreach (var premiumState in premiumStates.Where(state =>
                     state.Active
                     && state.ExpiresAt is not null))
        {
            session.Premium.ExpiryUnix[premiumState.ServiceType] =
                premiumState.ExpiresAt!.Value;
        }

        var blackDiamondService = premiumStates.FirstOrDefault(state =>
            state.ServiceType == GameProtocolConstants.BlackDiamondServiceType);
        var blackDiamondActive = blackDiamondService?.Active == true;
        var selectReply = CharactersPacketBuilders.CreateSelectCharacterReply(
            request,
            characterId: (uint)slot + 1,
            serverId: session.Character.LocalUserId,
            usedFatigue: Math.Min(
                session.Character.Active.UsedFatigue,
                blackDiamondActive
                    ? GameProtocolConstants.BlackDiamondMaximumFatigue
                    : GameProtocolConstants.DefaultMaximumFatigue),
            maximumFatigue: blackDiamondActive
                ? GameProtocolConstants.BlackDiamondMaximumFatigue
                : GameProtocolConstants.DefaultMaximumFatigue,
            premiumFatigue: blackDiamondActive
                ? GameProtocolConstants.BlackDiamondBonusFatigue
                : (ushort)0,
            cash: (uint)session.Currency.Cash,
            townId: session.Character.TownId,
            completedTutorialFlags: session.Character.Active.TutorialStarted
                ? GameProtocolConstants.DefaultCompletedTutorialFlags
                : 0u,
            activeQuests: session.Quest.Current
                .Select(quest => new GameQuestEntry(quest.QuestId, quest.Trigger))
                .ToArray(),
            premiumServices: premiumStates
                .Where(state => state.Active)
                .Select(state => new GamePremiumServiceEntry(
                    state.ServiceType,
                    state.RemainingSeconds))
                .ToArray(),
            completedQuestIds: session.Quest.CompletedIds.Order().ToArray(),
            questCompletionMapping: questCatalog.CompletionMapping);
        await context.Outbound.SendAsync(selectReply, Name, cancellationToken);

        var currentCharacter = builders.CreateTownAppearancePacket(
            session.Character.Active,
            session.Character.LocalUserId,
            session.Inventory.Equipped.Values,
            session.Inventory.Creature.Values,
            blackDiamondActive);
        await context.Outbound.SendAsync(currentCharacter, Name, cancellationToken);

        var learnedSkills = senders.GetCurrentGameSkills(context);
        var projectedCombatStats =
            characterStatCatalog.Get(session.Character.Active);
        var currentCharacterDetails =
            CharactersPacketBuilders.CreateCurrentCharacterDetails(
                session.Character.LocalUserId,
                experience: session.Character.Active.Experience,
                learnedSkills: learnedSkills,
                combatStats: projectedCombatStats,
                equippedItems: builders.CreateWornEquipmentSnapshot(
                    session.Inventory.Equipped.Values,
                    session.Inventory.Creature),
                equippedCreatureLevel: CharacterPacketBuilders.GetEquippedCreatureLevel(
                    session.Inventory.Creature),
                staminaRecoveryPercentage:
                    senders.GetProjectedStaminaRecovery(context));
        await context.Outbound.SendAsync(currentCharacterDetails, Name, cancellationToken);
        session.Character.LastProjectedCombatStats = projectedCombatStats;

        foreach (var initialPacket in builders.CreateTownInitializationPackets(
                     session.Skill.CurrentSp,
                     learnedSkills,
                     session.Currency.Gold,
                     session.Currency.VictoryPoints,
                     session.Inventory.Main.Values,
                     session.Inventory.Equipped.Values,
                     session.Inventory.Avatar.Values,
                     session.Inventory.Warehouse.Values,
                     session.Inventory.WarehouseCapacity,
                     session.Inventory.Creature.Values,
                     senders.GetAcceptableQuestIds(context),
                     senders.GetDungeonPermissions(context)))
        {
            await context.Outbound.SendAsync(initialPacket, Name, cancellationToken);
        }

        await senders.SendPvpRecordAsync(context, cancellationToken);

        // DFLegacy does not issue MAILBOX_OPEN while entering
        // town, so deliver the current character's mailbox
        // snapshot as part of character initialization.
        await senders.SendMailboxStateAsync(context, notifyNewMail: true, cancellationToken);
    }
    private static (byte AreaId, short X, short Y, byte Direction) GetTownGateSpawn(
        byte townId) =>
        (
            townId switch
            {
                2 => 5, // Hendon Myre/Gate_Hendon.map
                3 => 2, // West Coast/Gate_West.map
                _ => 1  // Elvengard, Alfhlyra and Storm Pass gate rooms
            },
            474,
            234,
            5);
}
