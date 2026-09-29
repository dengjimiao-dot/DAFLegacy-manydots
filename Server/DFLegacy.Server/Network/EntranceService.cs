using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

public sealed class EntranceService(
    ServerOptions options,
    RuntimeState runtime,
    JsonGameStore store,
    CharacterSessionRegistry characterSessions,
    PartyCoordinator partyCoordinator,
    SkillCatalog skillCatalog,
    QuestCatalog questCatalog,
    CharacterExperienceCatalog experienceCatalog,
    DungeonExperienceCatalog dungeonExperienceCatalog,
    PvpExperienceCatalog pvpExperienceCatalog,
    CharacterSpCatalog spCatalog,
    CharacterStatCatalog characterStatCatalog,
    CreatureExperienceCatalog creatureExperienceCatalog,
    StaminaRecoveryCatalog staminaRecoveryCatalog,
    ItemCatalog itemCatalog,
    CompoundCatalog compoundCatalog,
    AvatarCompoundCatalog avatarCompoundCatalog,
    EquipmentReinforcementCatalog reinforcementCatalog,
    ResealCatalog resealCatalog,
    DisjointCatalog disjointCatalog,
    CeraShopCatalog ceraShopCatalog,
    PremiumBenefitCatalog premiumBenefitCatalog,
    DungeonCatalog dungeonCatalog,
    WorldMapCatalog worldMapCatalog,
    DungeonDropGenerator dropGenerator,
    MonsterChampionDropCatalog monsterChampionDrops,
    HellDropCatalog hellDropCatalog,
    ClearRewardGenerator clearRewardGenerator,
    GameplayDatagramService gameplayDatagram,
    ScriptFileSystem scripts,
    ILogger<EntranceService> logger) : BackgroundService
{
    private const ushort RiverLetheItemId = 3;
    private const ushort RiverLetheBellItemId = 28;
    private const ushort ServerMegaphoneItemId = 36;
    private const ushort ChannelMegaphoneItemId = 37;
    private const ushort FatigueRecoveryItemId = 7_518;
    private const ushort LevelUpCouponItemId = 7_519;
    private const ushort FatigueRecoveryUseThreshold = 126;
    private const ushort FatigueRecoveryAmount = 30;
    private const ushort BoboCreatureItemId = 63_023;
    private const ushort EquippedTitleSlot = 10;
    private const int CompoundReplayCapacity = 256;
    private static readonly TimeSpan ClearRewardCardSelectionTimeout =
        TimeSpan.FromSeconds(4);
    private const string FatigueRecoveryRejectedMessage =
        "疲劳值低于126点时才可以使用";
    private const string LevelUpCouponSuccessMessage = "Lv上升";
    private const string LevelUpCouponLevelLimitMessage = "角色已达到最大Lv上限";
    private readonly List<TcpListener> _listeners = [];
    private readonly object _contentGate = new();
    private EntranceContent? _content;
    private static readonly Encoding ClientEncoding = CreateClientEncoding();
    private static readonly GameSkillEntry[] DebugSwordmanSkills =
    [
        new(5, 20), new(8, 20), new(11, 20), new(6, 20),
        new(13, 15), new(18, 8), new(25, 10), new(36, 20),
        new(41, 10), new(44, 20), new(46, 8), new(16, 20),
        new(17, 5), new(28, 1), new(29, 1), new(30, 10)
    ];
    private static readonly IReadOnlyDictionary<int, GameSkillEntry[]> InitialSkillsByJob =
        new Dictionary<int, GameSkillEntry[]>
        {
            // Script/character/*/*.chr -> [initial value] / [skill].
            [0] =
            [
                new(181, 2, 102), new(182, 2, 103), new(184, 1, 104),
                new(173, 1, 105), new(179, 5, 106),
                new(5, 1, 0), new(46, 1, 1)
            ],
            [1] =
            [
                new(181, 3, 102), new(182, 3, 103), new(184, 1, 104),
                new(172, 1, 105), new(179, 5, 106),
                new(5, 1, 0), new(46, 1, 1)
            ],
            [2] =
            [
                new(181, 2, 102), new(184, 1, 103), new(185, 1, 104),
                new(179, 5, 105), new(4, 1, 0), new(47, 1, 1)
            ],
            [3] =
            [
                new(181, 3, 102), new(182, 3, 103), new(184, 2, 104),
                new(187, 1, 105), new(179, 5, 106),
                new(11, 1, 0), new(12, 1, 1)
            ],
            [4] =
            [
                new(181, 1, 102), new(182, 1, 103), new(184, 1, 104),
                new(179, 5, 105), new(173, 1, 106),
                new(1, 1, 0), new(51, 1, 1)
            ]
        };
    private static readonly (ushort ItemId, uint Count)[] TestStarterInventoryItems =
    [
        (8, 100), (27093, 1), (14873, 1), (12874, 1),
        (10874, 1), (16873, 1), (18874, 1), (22021, 1),
        (20093, 1), (24032, 1), (27546, 1), (28223, 1),
        (3037, 1000)
    ];
    private static readonly (ushort ItemId, uint Count)[] NormalStarterInventoryItems =
    [
        (26043, 1),
        (8, 100)
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Entrance.Enabled)
        {
            logger.LogWarning("Entrance listener is disabled.");
            return;
        }

        var address = IPAddress.Parse(options.Entrance.Host);
        var ports = new[] { options.Entrance.Port }
            .Concat(options.Entrance.AdditionalPorts)
            .Where(port => port is > 0 and <= 65535)
            .Distinct()
            .ToArray();

        foreach (var port in ports)
        {
            var listener = new TcpListener(address, port);
            listener.Start();
            _listeners.Add(listener);
            logger.LogInformation("Entrance listener ready at {Address}:{Port}", address, port);
        }

        try
        {
            await Task.WhenAll(_listeners.Select(listener =>
                AcceptLoopAsync(listener, ((IPEndPoint)listener.LocalEndpoint).Port, stoppingToken)));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            foreach (var listener in _listeners)
            {
                listener.Stop();
            }
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, int port, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var client = await listener.AcceptTcpClientAsync(stoppingToken);
            _ = HandleClientAsync(client, port, stoppingToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, int localPort, CancellationToken serverToken)
    {
        using (client)
        {
            client.NoDelay = true;
            using var connectionCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(serverToken);
            serverToken = connectionCancellation.Token;

            void KickConnection()
            {
                connectionCancellation.Cancel();
                client.Close();
            }

            var remoteEndpoint = client.Client.RemoteEndPoint as IPEndPoint;
            var remote = remoteEndpoint?.ToString() ?? "unknown";
            var isChannelPort = localPort == options.Channel.Port;
            var runtimeSession = runtime.Add($"{(isChannelPort ? "channel" : "entrance")}:{localPort}", remote);
            if (isChannelPort && remoteEndpoint is not null)
            {
                gameplayDatagram.RegisterChannelSession(
                    runtimeSession,
                    remoteEndpoint.Address);
            }
            var protocolSession = new EntranceSessionState();
            var gameClientCipher = isChannelPort ? new GameClientCipherState() : null;
            var accountName = string.Empty;
            var accountUid = AccountUidRules.InvalidUid;
            // The wire identity comes from CharacterNo so two simultaneous
            // clients get distinct town/party user ids. It is assigned at
            // SELECT_CHARACTER; every packet that names the local user reads
            // this closure variable. 1 is the pre-selection placeholder.
            ushort localUserId = 1;
            CharacterRecord? activeCharacter = null;
            GameCharacterCombatStats? lastProjectedCombatStats = null;
            CharacterSessionLease? activeCharacterLease = null;
            var mainInventory = new Dictionary<ushort, CharacterItemRecord>();
            var avatarInventory = new Dictionary<ushort, CharacterItemRecord>();
            var equippedInventory = new Dictionary<ushort, CharacterItemRecord>();
            var warehouseInventory = new Dictionary<ushort, CharacterItemRecord>();
            var creatureInventory = new Dictionary<ushort, CharacterItemRecord>();
            // NOTI 100 is a transition notification. Keep it connection-local
            // so repeated hunger/state refreshes do not replay a death event;
            // town actor reconstruction explicitly requests it again.
            var creatureDeathNotificationsSent = new HashSet<uint>();
            var warehouseCapacity = CharacterWarehouseProgression.InitialCapacity;

            uint GetInventoryWeightLimit(CharacterRecord character) =>
                CharacterInventoryPlanner.CalculateMainInventoryWeightAllowance(
                    characterStatCatalog.Get(character).InventoryLimit,
                    equippedInventory.Values,
                    creatureInventory.Values,
                    itemCatalog);

            CharacterWarehouseUpgradeItemSettlement PlanWarehouseUpgradeSettlement(
                IEnumerable<CharacterItemRecord> inventory,
                IEnumerable<CharacterItemRecord> warehouse,
                IEnumerable<ushort>? acquiredItemIds = null)
            {
                var settlement = CharacterWarehouseProgression.SettleUpgradeItems(
                    inventory,
                    warehouse,
                    warehouseCapacity);
                var targetCapacity = settlement.Capacity;
                foreach (var itemId in acquiredItemIds ?? [])
                {
                    targetCapacity = CharacterWarehouseProgression.ApplyItemTarget(
                        targetCapacity,
                        itemId);
                }

                return targetCapacity == settlement.Capacity
                    ? settlement
                    : settlement with { Capacity = targetCapacity };
            }

            bool AdoptSavedWarehouseUpgradeState(CharacterRecord savedCharacter)
            {
                var previousCapacity = warehouseCapacity;
                activeCharacter = savedCharacter;
                mainInventory = (savedCharacter.Inventory ?? [])
                    .ToDictionary(item => item.Slot);
                warehouseInventory = (savedCharacter.Warehouse ?? [])
                    .ToDictionary(item => item.Slot);
                warehouseCapacity = CharacterWarehouseProgression.Normalize(
                    savedCharacter.WarehouseCapacity);
                return warehouseCapacity != previousCapacity;
            }

            var currentSkills = new List<CharacterSkillRecord>();
            var currentQuests = new List<CharacterQuestRecord>();
            var completedQuestIds = new HashSet<ushort>();
            var currentSp = 0;
            var currentGold = 50_000;
            var currentCash = 0;
            uint currentVictoryPoints = 0;
            byte currentTownId = 1;
            byte currentAreaId = 1;
            ushort currentDungeonId = 0;
            var inDungeonSelection = false;
            WorldMapDefinition? selectionWorldMap = null;
            DateTimeOffset? dungeonSelectionDeadline = null;
            byte currentDungeonRoomX = 0;
            byte currentDungeonRoomY = 0;
            byte currentDungeonDifficulty = 0;
            DungeonLayout? currentDungeonLayout = null;
            DungeonRoomDefinition? currentDungeonRoom = null;
            var aliveDungeonMonsters = new HashSet<ushort>();
            var reportedBossDeaths = new HashSet<ushort>();
            var bossCascadeMonsterDeaths = new HashSet<ushort>();
            var destroyedPassiveObjects = new HashSet<byte>();
            var groundDungeonItems = new DungeonGroundItemState();
            var dungeonRun = new DungeonRunState(dungeonCatalog);
            var tutorialDungeonActive = false;
            var dungeonCharacterAlive = true;
            long dungeonDeathGeneration = 0;
            (long Generation, DateTimeOffset Deadline)? dungeonDeathTimeout = null;
            var dungeonClearEnabled = false;
            var dungeonResultSent = false;
            var dungeonCardLayoutSent = false;
            var dungeonCardRightsSent = false;
            DateTimeOffset? dungeonCardSelectionDeadline = null;
            DungeonClearRewardRoll? dungeonClearReward = null;
            byte? dungeonFreeCardIndex = null;
            byte? dungeonGoldCardIndex = null;
            uint dungeonMonsterExperienceGained = 0;
            GameDungeonClearExperienceBreakdown? dungeonClearExperience = null;
            uint dungeonConsumedFatigue = 0;
            long? dungeonCreatureStomachCheckpointSeconds = null;
            var dungeonLeveledUp = false;
            var dungeonNormalMonsterKills = 0;
            var dungeonChampionMonsterKills = 0;
            var dungeonBossMonsterKills = 0;
            // 13338 displays at most eleven Champion/SuperChampion reward
            // animations before the clear-card screen. This ordinal is per
            // dungeon run and is independent from the kill-count weighting.
            var dungeonChampionRewardOrdinal = 0;
            short currentX = 474;
            short currentY = 234;
            byte currentDirection = 5;
            ushort currentMovementValue = 100;
            byte dungeonReturnTownId = currentTownId;
            byte dungeonReturnAreaId = currentAreaId;
            short dungeonReturnX = currentX;
            short dungeonReturnY = currentY;
            byte dungeonReturnDirection = currentDirection;
            var areaRosterInitialized = false;
            var staminaStateInitialized = false;
            var pendingMailAlarm = 0;
            var pendingPremiumUpdates =
                new ConcurrentQueue<(byte ServiceType, uint RemainingSeconds)>();
            // Connection-local snapshot of account-owned Premium services.
            // CharacterRecord deliberately contains no Premium persistence.
            var premiumExpiryUnix = new ConcurrentDictionary<byte, long>();
            var pendingCeraValue = -1;
            var pendingFatigueReset = 0;
            var pendingWeaknessRecovery = -1;
            var knownMailboxIds = new HashSet<uint>();
            var compoundReplay = new Dictionary<ushort, CompoundItemReplayEntry>();
            var compoundReplayOrder = new Queue<ushort>();
            logger.LogInformation(
                "Entrance client {SessionId} connected from {Remote} on local port {Port}",
                runtimeSession.Id, remote, localPort);

            try
            {
                using var stream = new SerializedWriteStream(client.GetStream());
                // Cross-session delivery channel: PartyCoordinator queues
                // packets here from other sessions' read loops; the write
                // itself stays on this connection's serialized stream.
                characterSessions.AttachPacketChannel(
                    runtimeSession.Id,
                    packet => _ = SendCrossSessionPacketAsync(packet));

                async Task SendCrossSessionPacketAsync(GameServerPacket packet)
                {
                    try
                    {
                        await packet.WriteAsync(stream, serverToken);
                        Interlocked.Increment(ref runtimeSession.SentPackets);
                        LogPacket("TX", runtimeSession, packet);
                    }
                    catch (OperationCanceledException) when (serverToken.IsCancellationRequested)
                    {
                    }
                    catch (Exception exception)
                    {
                        logger.LogWarning(
                            exception,
                            "Could not deliver a cross-session packet {ProtocolId} on session {SessionId}.",
                            packet.ProtocolId,
                            runtimeSession.Id);
                    }
                }

                async Task SendWarehouseStateAsync()
                {
                    var warehouseRefresh = CreateWarehousePacket(
                        warehouseInventory.Values,
                        warehouseCapacity);
                    await warehouseRefresh.WriteAsync(stream, serverToken);
                    Interlocked.Increment(ref runtimeSession.SentPackets);
                    LogPacket("TX", runtimeSession, warehouseRefresh);
                }

                async Task SendTypedMessageNotificationAsync(
                    GameMessageType messageType,
                    ushort targetAreaUserId,
                    byte[] messageBytes)
                {
                    try
                    {
                        if (activeCharacter is null)
                        {
                            return;
                        }

                        var messageNotification =
                            GameProtocolEngine.CreateMessageNotification(
                                messageType,
                                targetAreaUserId,
                                messageBytes);
                        await messageNotification.WriteAsync(stream, serverToken);
                        Interlocked.Increment(ref runtimeSession.SentPackets);
                        LogPacket("TX", runtimeSession, messageNotification);
                    }
                    catch (OperationCanceledException) when (serverToken.IsCancellationRequested)
                    {
                    }
                    catch (Exception exception)
                    {
                        logger.LogWarning(
                            exception,
                            "Could not send message notification to character {CharacterId}.",
                            activeCharacter?.Id);
                    }
                }

                async Task SendPopupNotificationAsync(byte[] messageBytes)
                {
                    try
                    {
                        if (activeCharacter is null)
                        {
                            return;
                        }

                        var popupNotification =
                            GameProtocolEngine.CreatePopupNotification(messageBytes);
                        await popupNotification.WriteAsync(stream, serverToken);
                        Interlocked.Increment(ref runtimeSession.SentPackets);
                        LogPacket("TX", runtimeSession, popupNotification);
                    }
                    catch (OperationCanceledException) when (serverToken.IsCancellationRequested)
                    {
                    }
                    catch (Exception exception)
                    {
                        logger.LogWarning(
                            exception,
                            "Could not send popup notification to character {CharacterId}.",
                            activeCharacter?.Id);
                    }
                }

                async Task SendMegaphoneNotificationAsync(
                    GameMessageType messageType,
                    byte ownerFlag,
                    ushort targetAreaUserId,
                    byte[] senderName,
                    byte[] messageBytes)
                {
                    try
                    {
                        if (activeCharacter is null)
                        {
                            return;
                        }

                        var megaphoneNotification =
                            GameProtocolEngine.CreateMegaphoneNotification(
                                messageType,
                                ownerFlag,
                                targetAreaUserId,
                                senderName,
                                messageBytes);
                        await megaphoneNotification.WriteAsync(stream, serverToken);
                        Interlocked.Increment(ref runtimeSession.SentPackets);
                        LogPacket("TX", runtimeSession, megaphoneNotification);
                    }
                    catch (OperationCanceledException) when (serverToken.IsCancellationRequested)
                    {
                    }
                    catch (Exception exception)
                    {
                        logger.LogWarning(
                            exception,
                            "Could not send megaphone notification to character {CharacterId}.",
                            activeCharacter?.Id);
                    }
                }

                // Registers this session's town entity with the party
                // coordinator so same-area residents can see and invite this
                // character. The appearance packet is built here because only
                // this connection owns the equipped-item dictionaries.
                void PublishTownPresence()
                {
                    if (activeCharacter is null)
                    {
                        return;
                    }

                    var appearance = CreateTownAppearancePacket(
                        activeCharacter,
                        localUserId,
                        equippedInventory.Values,
                        creatureInventory.Values,
                        HasActivePremiumService(GameProtocolEngine.BlackDiamondServiceType));
                    partyCoordinator.PublishTownPresence(
                        new PartyPresenceUpdate(
                            runtimeSession.Id,
                            activeCharacter.Id,
                            accountUid,
                            localUserId,
                            activeCharacter.Name,
                            (byte)Math.Clamp(activeCharacter.Level, (byte)1, byte.MaxValue),
                            (byte)Math.Clamp(activeCharacter.Job, byte.MinValue, byte.MaxValue),
                            currentTownId,
                            currentAreaId,
                            currentX,
                            currentY,
                            currentDirection),
                        appearance);
                }

                GameSkillEntry[] GetCurrentGameSkills() => currentSkills
                    .OrderBy(skill => skill.Slot)
                    .Select(skill => new GameSkillEntry(skill.SkillId, skill.Level, skill.Slot))
                    .ToArray();

                int ApplyLevelUpSkillPoints(CharacterExperienceGrant progression)
                {
                    var granted = spCatalog.CalculateLevelUpReward(
                        progression.PreviousLevel,
                        progression.Level);
                    if (granted == 0)
                    {
                        return 0;
                    }

                    currentSp = checked(currentSp + granted);
                    return granted;
                }

                async Task SendCurrentSkillInfoAsync()
                {
                    var skillInfo = GameProtocolEngine.CreateSkillInfo(
                        checked((ushort)Math.Min(currentSp, ushort.MaxValue)),
                        GetCurrentGameSkills());
                    await skillInfo.WriteAsync(stream, serverToken);
                    Interlocked.Increment(ref runtimeSession.SentPackets);
                    LogPacket("TX", runtimeSession, skillInfo);
                }

                async Task SendPvpRecordAsync()
                {
                    if (activeCharacter is null)
                    {
                        return;
                    }

                    // PvpPoints is the source of truth. Older state files may
                    // have persisted the pre-boundary grade (for example,
                    // 2,000 points as grade 1), so derive the wire rank from
                    // the cumulative PVF thresholds on every full sync.
                    var grade = pvpExperienceCatalog.GetGradeForPoints(
                        activeCharacter.PvpPoints);
                    var pvpRecord = GameProtocolEngine.CreatePvpRecord(
                        activeCharacter.PvpWins,
                        activeCharacter.PvpLosses,
                        activeCharacter.PvpPoints,
                        pvpExperienceCatalog.GetCurrentRankPoint(grade),
                        pvpExperienceCatalog.GetNextRankPoint(grade),
                        grade,
                        activeCharacter.PvpGradeExtension);
                    await pvpRecord.WriteAsync(stream, serverToken);
                    Interlocked.Increment(ref runtimeSession.SentPackets);
                    LogPacket("TX", runtimeSession, pvpRecord);

                    var refreshedQuests = currentQuests.Select(quest =>
                        questCatalog.TryGetDefinition(quest.QuestId, out var definition)
                            && QuestPvpRankRequirement.IsPvpRank(definition)
                            ? quest with { Trigger = QuestPvpRankRequirement.GetTrigger(definition, grade) }
                            : quest).ToList();
                    var changedQuests = refreshedQuests.Where((quest, index) =>
                        quest.Trigger != currentQuests[index].Trigger).ToArray();
                    if (changedQuests.Length == 0)
                    {
                        return;
                    }

                    var saved = await store.SaveCharacterQuestsAsync(
                        accountName, activeCharacter.Id, refreshedQuests, completedQuestIds, serverToken);
                    if (saved is null)
                    {
                        logger.LogWarning("Could not persist PvP-rank quest progress for {CharacterId}.", activeCharacter.Id);
                        return;
                    }
                    currentQuests = refreshedQuests;
                    activeCharacter = activeCharacter with { Quests = saved.Quests };
                    foreach (var quest in changedQuests)
                    {
                        var update = GameProtocolEngine.CreateSetQuestTriggerReply(quest.QuestId, quest.Trigger);
                        await update.WriteAsync(stream, serverToken);
                        Interlocked.Increment(ref runtimeSession.SentPackets);
                        LogPacket("TX", runtimeSession, update);
                    }
                }

                async Task<CharacterMailRecord[]> SendMailboxStateAsync(bool notifyNewMail)
                {
                    if (activeCharacter is null)
                    {
                        return [];
                    }

                    var mails = await store.GetCharacterMailsAsync(
                        activeCharacter.Id,
                        serverToken);
                    knownMailboxIds.Clear();
                    knownMailboxIds.UnionWith(mails.Select(mail => mail.Id));
                    var mailboxPackets = GameProtocolEngine.CreateMailboxSnapshot(
                        mails.Select(ToGameMailEntry).ToArray());
                    foreach (var packet in mailboxPackets)
                    {
                        await packet.WriteAsync(stream, serverToken);
                        Interlocked.Increment(ref runtimeSession.SentPackets);
                        LogPacket("TX", runtimeSession, packet);
                    }

                    if (notifyNewMail)
                    {
                        await SendNewMailAlarmAsync(mails);
                    }

                    return mails;
                }

                async Task SendNewMailAlarmAsync(
                    IReadOnlyCollection<CharacterMailRecord>? knownMails = null)
                {
                    if (activeCharacter is null)
                    {
                        return;
                    }

                    var mails = knownMails
                        ?? await store.GetCharacterMailsAsync(activeCharacter.Id, serverToken);
                    var newMailCount = mails.Count(mail => mail.IsNew);
                    if (newMailCount == 0)
                    {
                        return;
                    }

                    var alarm = GameProtocolEngine.CreateMailboxAlarm(
                        checked((short)Math.Min(newMailCount, short.MaxValue)));
                    await alarm.WriteAsync(stream, serverToken);
                    Interlocked.Increment(ref runtimeSession.SentPackets);
                    LogPacket("TX", runtimeSession, alarm);
                    await store.AcknowledgeCharacterMailNotificationsAsync(
                        activeCharacter.Id,
                        serverToken);
                }

                async Task SendPremiumServiceInfoAsync(
                    byte serviceType,
                    uint remainSeconds)
                {
                    if (remainSeconds == 0)
                    {
                        premiumExpiryUnix.TryRemove(serviceType, out _);
                    }
                    else
                    {
                        var expiryUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                            + (long)remainSeconds;
                        premiumExpiryUnix[serviceType] = expiryUnix;
                    }

                    var premiumNotification =
                        GameProtocolEngine.CreatePremiumServiceNotification(
                            action: GameProtocolEngine.PremiumServiceUpdateAction,
                            serviceType,
                            remainSeconds);
                    await premiumNotification.WriteAsync(stream, serverToken);
                    Interlocked.Increment(ref runtimeSession.SentPackets);
                    LogPacket("TX", runtimeSession, premiumNotification);

                    if (serviceType == GameProtocolEngine.BlackDiamondServiceType
                        && activeCharacter is not null
                        && currentDungeonId == 0)
                    {
                        var appearance = CreateTownAppearancePacket(
                            activeCharacter,
                            localUserId,
                            equippedInventory.Values,
                            creatureInventory.Values,
                            HasActivePremiumService(
                                GameProtocolEngine.BlackDiamondServiceType));
                        await appearance.WriteAsync(stream, serverToken);
                        Interlocked.Increment(ref runtimeSession.SentPackets);
                        LogPacket("TX", runtimeSession, appearance);
                        await SendTownFatigueStateAsync();
                    }
                }

                Task SendMessageNotificationAsync(
                    ushort targetAreaUserId,
                    byte[] messageBytes) =>
                    SendTypedMessageNotificationAsync(
                        GameMessageType.Type16Brown,
                        targetAreaUserId,
                        messageBytes);

                async Task SendWeaknessRecoveryNotificationAsync(byte recovery)
                {
                    try
                    {
                        if (activeCharacter is null)
                        {
                            return;
                        }

                        var stamina = GameProtocolEngine.CreateStamina(recovery);
                        await stamina.WriteAsync(stream, serverToken);
                        Interlocked.Increment(ref runtimeSession.SentPackets);
                        LogPacket("TX", runtimeSession, stamina);
                    }
                    catch (OperationCanceledException) when (serverToken.IsCancellationRequested)
                    {
                    }
                    catch (Exception exception)
                    {
                        logger.LogWarning(
                            exception,
                            "Could not send weakness recovery notification to character {CharacterId}.",
                            activeCharacter?.Id);
                    }
                }

                bool HasActivePremiumService(byte serviceType) =>
                    premiumExpiryUnix.TryGetValue(serviceType, out var expiryUnix)
                    && expiryUnix > DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                byte[] GetActivePremiumServiceTypes()
                {
                    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    return premiumExpiryUnix
                        .Where(entry => entry.Value > now)
                        .Select(entry => entry.Key)
                        .ToArray();
                }

                byte GetProjectedStaminaRecovery()
                {
                    if (activeCharacter is null
                        || !activeCharacter.IsWeakened
                        || HasActivePremiumService(
                            GameProtocolEngine.BlackDiamondServiceType))
                    {
                        return DungeonWeaknessPolicy.FullStamina;
                    }

                    return (byte)Math.Clamp(
                        activeCharacter.WeaknessRecovery,
                        DungeonWeaknessPolicy.MinimumRecovery,
                        DungeonWeaknessPolicy.FullStamina);
                }

                async Task SendCurrentEquipmentStateAsync(
                    GameCharacterCombatStats? validatedStats = null)
                {
                    if (activeCharacter is null)
                    {
                        return;
                    }

                    var projectedStats = validatedStats
                        ?? characterStatCatalog.Get(activeCharacter);
                    var equipment = GameProtocolEngine.CreateCurrentCharacterDetails(
                        localUserId,
                        experience: activeCharacter.Experience,
                        learnedSkills: GetCurrentGameSkills(),
                        combatStats: projectedStats,
                        equippedItems: CreateWornEquipmentSnapshot(
                            equippedInventory.Values,
                            creatureInventory),
                        equippedCreatureLevel: GetEquippedCreatureLevel(
                            creatureInventory),
                        staminaRecoveryPercentage: GetProjectedStaminaRecovery());
                    await equipment.WriteAsync(stream, serverToken);
                    Interlocked.Increment(ref runtimeSession.SentPackets);
                    LogPacket("TX", runtimeSession, equipment);
                    lastProjectedCombatStats = projectedStats;
                }

                async Task SendEquippedCreatureDeathAsync(bool force = false)
                {
                    if (activeCharacter is null
                        || !creatureInventory.TryGetValue(
                            CharacterCreatureInventoryLayout.EquippedCreatureSlot,
                            out var equippedCreature)
                        || equippedCreature.CountOrValue == 0)
                    {
                        return;
                    }

                    var creatureUid = equippedCreature.CountOrValue;
                    var stomach = Math.Clamp(
                        (int)(equippedCreature.CreatureStomach ?? 100),
                        0,
                        CreatureHungerPlanner.MaximumStomach);
                    if (stomach != 0)
                    {
                        creatureDeathNotificationsSent.Remove(creatureUid);
                        return;
                    }

                    if (!force
                        && creatureDeathNotificationsSent.Contains(creatureUid))
                    {
                        return;
                    }

                    var died = GameProtocolEngine.CreateDiedCreature(localUserId);
                    await died.WriteAsync(stream, serverToken);
                    Interlocked.Increment(ref runtimeSession.SentPackets);
                    LogPacket("TX", runtimeSession, died);
                    creatureDeathNotificationsSent.Add(creatureUid);
                    logger.LogInformation(
                        "Sent DIED_CREATURE for Creature uid {CreatureUid}, area user {UserId}.",
                        creatureUid,
                        localUserId);
                }

                async Task SendEquippedCreatureHungerAsync(
                    bool forceDeathNotification = false)
                {
                    if (activeCharacter is null
                        || !creatureInventory.TryGetValue(
                            CharacterCreatureInventoryLayout.EquippedCreatureSlot,
                            out var equippedCreature)
                        || equippedCreature.CountOrValue == 0)
                    {
                        return;
                    }

                    var creatureState = GameProtocolEngine.CreateCreatureState(
                        equippedCreature.CountOrValue,
                        (uint)Math.Clamp(
                            (int)(equippedCreature.CreatureStomach ?? 100),
                            0,
                            CreatureHungerPlanner.MaximumStomach));
                    await creatureState.WriteAsync(stream, serverToken);
                    Interlocked.Increment(ref runtimeSession.SentPackets);
                    LogPacket("TX", runtimeSession, creatureState);
                    await SendEquippedCreatureDeathAsync(forceDeathNotification);
                }

                async Task SendEquippedCreatureResponseAsync()
                {
                    if (activeCharacter is null
                        || !creatureInventory.ContainsKey(
                            CharacterCreatureInventoryLayout.EquippedCreatureSlot))
                    {
                        return;
                    }

                    var creatureResponse =
                        GameProtocolEngine.CreateCreatureResponse(localUserId);
                    await creatureResponse.WriteAsync(stream, serverToken);
                    Interlocked.Increment(ref runtimeSession.SentPackets);
                    LogPacket("TX", runtimeSession, creatureResponse);
                }

                async Task SaveCurrentLocationAsync(CancellationToken cancellationToken)
                {
                    if (activeCharacter is null)
                    {
                        return;
                    }

                    activeCharacter = await store.SaveCharacterLocationAsync(
                        accountName,
                        activeCharacter.Id,
                        currentTownId,
                        currentAreaId,
                        currentX,
                        currentY,
                        currentDirection,
                        cancellationToken) ?? activeCharacter;
                }

                async Task EnableEmptyBossRoomSettlementAsync(DungeonRoomDefinition room)
                {
                    if (room.Monsters.Length != 0
                        || dungeonClearEnabled
                        || room.MapType != DungeonMapType.Boss)
                    {
                        return;
                    }

                    // War-room scripts (2000-2002) use timed dynamic spawns
                    // rather than static .map monster rows. Until that mode is
                    // simulated, an empty terminal room must remain settleable.
                    dungeonClearEnabled = true;
                    var enableClear = GameProtocolEngine.CreateEnableClearDungeon();
                    await enableClear.WriteAsync(stream, serverToken);
                    Interlocked.Increment(ref runtimeSession.SentPackets);
                    LogPacket("TX", runtimeSession, enableClear);
                    await CompleteClearQuestTriggersAsync(
                        "empty boss-room settlement");
                    logger.LogInformation(
                        "Dungeon {DungeonId} empty boss room ({RoomX},{RoomY}) enabled settlement.",
                        currentDungeonId,
                        currentDungeonRoomX,
                        currentDungeonRoomY);
                }

                async Task SendTownAppearanceAsync(bool waitForTownActor = false)
                {
                    if (activeCharacter is null || currentDungeonId != 0)
                    {
                        return;
                    }

                    if (waitForTownActor)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(100), serverToken);
                    }

                    // The original server equips the Creature and establishes
                    // its town state before broadcasting the mode-zero basic
                    // user record containing item id, name and alive state.
                    await SendEquippedCreatureHungerAsync(
                        // Only AREA_USERS reconstruction needs to replay a
                        // persisted death marker. Ordinary equipment updates
                        // keep the transition de-duplicated.
                        forceDeathNotification: waitForTownActor);

                    var appearance = CreateTownAppearancePacket(
                        activeCharacter,
                        localUserId,
                        equippedInventory.Values,
                        creatureInventory.Values,
                        HasActivePremiumService(
                            GameProtocolEngine.BlackDiamondServiceType));
                    await appearance.WriteAsync(stream, serverToken);
                    Interlocked.Increment(ref runtimeSession.SentPackets);
                    LogPacket("TX", runtimeSession, appearance);

                    // Mode zero is the compact town-actor appearance
                    // projection: Avatar slots 0..8, weapon slot 9, plus the
                    // dedicated title and Creature fields. It drops other worn
                    // objects omitted from its compact list, so immediately
                    // rebuild the authoritative equipment projection with
                    // subtype one.
                    await SendCurrentEquipmentStateAsync();
                    await SendInitialStaminaStateAsync();
                }

                async Task ValidateAndCorrectCharacterStatsAsync(string reason)
                {
                    if (activeCharacter is null)
                    {
                        return;
                    }

                    var validation = characterStatCatalog.Validate(
                        activeCharacter,
                        lastProjectedCombatStats);
                    if (currentDungeonId == 0)
                    {
                        // Mode zero first updates level/growtype on the town
                        // actor. Mode one then overwrites the unequipped stat
                        // block with the PVF-derived authoritative snapshot.
                        await SendTownAppearanceAsync();
                    }
                    else
                    {
                        await SendCurrentEquipmentStateAsync(validation.Expected);
                    }

                    logger.LogInformation(
                        "Validated PVF character attributes after {Reason} for {CharacterId}; level={Level}, job={Job}, growType={GrowType}, awakening={AwakeningType}, correctionRequired={CorrectionRequired}, mismatches={Mismatches}.",
                        reason,
                        activeCharacter.Id,
                        activeCharacter.Level,
                        activeCharacter.Job,
                        activeCharacter.GrowType & 0x0F,
                        Math.Max(activeCharacter.AwakeningType, activeCharacter.GrowType >> 4),
                        !validation.IsCorrect,
                        validation.MismatchedFields.Count == 0
                            ? "none"
                            : string.Join(',', validation.MismatchedFields));
                }

                async Task SendInitialStaminaStateAsync()
                {
                    if (staminaStateInitialized || activeCharacter is null)
                    {
                        return;
                    }

                    // DFLegacy NOTI 4 owns the weakness percentage. Persisted
                    // weakness is restored after the authoritative combat-stat
                    // projection; active black diamond always projects full stamina.
                    var stamina = GameProtocolEngine.CreateStamina(
                        GetProjectedStaminaRecovery());
                    await stamina.WriteAsync(stream, serverToken);
                    Interlocked.Increment(ref runtimeSession.SentPackets);
                    LogPacket("TX", runtimeSession, stamina);
                    staminaStateInitialized = true;
                }

                async Task SendSilentTownStaminaStateAsync()
                {
                    if (activeCharacter is null || currentDungeonId != 0)
                    {
                        return;
                    }

                    // AREA_USERS recreates the town actor with empty stamina.
                    // NOTI 33 restores a normal town actor without a recovery
                    // sound, but its client handler also fills current HP/MP.
                    // It must therefore never be sent while inside a dungeon.
                    var stamina = activeCharacter.IsWeakened
                        && !HasActivePremiumService(
                            GameProtocolEngine.BlackDiamondServiceType)
                            ? GameProtocolEngine.CreateStamina(
                                activeCharacter.WeaknessRecovery)
                            : GameProtocolEngine.CreateFailClearDungeon(
                                DungeonWeaknessPolicy.FullStamina);
                    await stamina.WriteAsync(stream, serverToken);
                    Interlocked.Increment(ref runtimeSession.SentPackets);
                    LogPacket("TX", runtimeSession, stamina);
                }

                async Task SendTownFatigueStateAsync()
                {
                    Interlocked.Exchange(ref pendingFatigueReset, 0);
                    // AREA_USERS constructs the town HUD. EVENT_INFO then selects
                    // and initializes its normal/premium fatigue-bar resources.
                    var eventInfo = GameProtocolEngine.CreateEventInfo();
                    await eventInfo.WriteAsync(stream, serverToken);
                    Interlocked.Increment(ref runtimeSession.SentPackets);
                    LogPacket("TX", runtimeSession, eventInfo);

                    var premiumFatigue = HasActivePremiumService(
                            GameProtocolEngine.BlackDiamondServiceType)
                        ? GameProtocolEngine.BlackDiamondBonusFatigue
                        : (ushort)0;
                    var maximumFatigue = checked((ushort)(
                        GameProtocolEngine.DefaultMaximumFatigue + premiumFatigue));
                    var fatigue = GameProtocolEngine.CreateFatigue(
                        usedFatigue: Math.Min(
                            activeCharacter?.UsedFatigue ?? 0,
                            maximumFatigue),
                        maximumFatigue: maximumFatigue,
                        premiumFatigue: premiumFatigue);
                    await fatigue.WriteAsync(stream, serverToken);
                    Interlocked.Increment(ref runtimeSession.SentPackets);
                    LogPacket("TX", runtimeSession, fatigue);
                }

                async Task ConsumeDungeonRoomFatigueAsync(
                    byte roomX,
                    byte roomY)
                {
                    if (activeCharacter is null || currentDungeonId == 0)
                    {
                        return;
                    }

                    var premiumFatigue = HasActivePremiumService(
                            GameProtocolEngine.BlackDiamondServiceType)
                        ? GameProtocolEngine.BlackDiamondBonusFatigue
                        : (ushort)0;
                    var maximumFatigue = checked((ushort)(
                        GameProtocolEngine.DefaultMaximumFatigue + premiumFatigue));
                    var consumption =
                        await store.ConsumeDungeonRoomFatigueAsync(
                            activeCharacter.Id,
                            maximumFatigue,
                            serverToken);
                    if (consumption.Character is null)
                    {
                        logger.LogWarning(
                            "Could not consume dungeon-room fatigue because character {CharacterId} was not found.",
                            activeCharacter.Id);
                        return;
                    }

                    activeCharacter = consumption.Character;
                    if (!consumption.Consumed)
                    {
                        logger.LogInformation(
                            "Dungeon {DungeonId} room ({RoomX},{RoomY}) consumed no fatigue because user {UserId} is already at {UsedFatigue}/{MaximumFatigue}.",
                            currentDungeonId,
                            roomX,
                            roomY,
                            localUserId,
                            consumption.UsedFatigue,
                            maximumFatigue);
                        return;
                    }

                    dungeonConsumedFatigue++;

                    var fatigue = GameProtocolEngine.CreateFatigue(
                        consumption.UsedFatigue,
                        maximumFatigue,
                        premiumFatigue);
                    await fatigue.WriteAsync(stream, serverToken);
                    Interlocked.Increment(ref runtimeSession.SentPackets);
                    LogPacket("TX", runtimeSession, fatigue);
                    logger.LogInformation(
                        "Dungeon {DungeonId} room ({RoomX},{RoomY}) consumed one fatigue for user {UserId}: used {PreviousUsedFatigue}->{UsedFatigue}, remaining={RemainingFatigue}/{MaximumFatigue}.",
                        currentDungeonId,
                        roomX,
                        roomY,
                        localUserId,
                        consumption.PreviousUsedFatigue,
                        consumption.UsedFatigue,
                        maximumFatigue - consumption.UsedFatigue,
                        maximumFatigue);
                }

                decimal GetEquippedCreatureExperienceBonusRate(byte creatureLevel)
                {
                    decimal bonusRate = 0;
                    foreach (var artifactSlot in new[]
                             {
                                 CharacterCreatureInventoryLayout.EquippedArtifactRedSlot,
                                 CharacterCreatureInventoryLayout.EquippedArtifactBlueSlot,
                                 CharacterCreatureInventoryLayout.EquippedArtifactGreenSlot
                             })
                    {
                        if (!creatureInventory.TryGetValue(artifactSlot, out var artifact)
                            || !itemCatalog.TryGetDefinition(
                                artifact.ItemId,
                                out var definition)
                            || !CharacterCreatureInventoryLayout.TryGetEquippedSlot(
                                definition,
                                out var expectedSlot)
                            || expectedSlot != artifactSlot
                            || definition.CreatureExperienceAmountRate is not decimal rate
                            || definition.CreatureMinimumLevel is int minimumLevel
                                && minimumLevel > 0
                                && creatureLevel < minimumLevel)
                        {
                            continue;
                        }

                        bonusRate += rate;
                    }

                    return bonusRate;
                }

                async Task<bool> SettleDungeonCreatureHungerAsync(
                    bool notifyClient,
                    CancellationToken cancellationToken)
                {
                    if (dungeonCreatureStomachCheckpointSeconds is not long checkpoint)
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

                    if (activeCharacter is null
                        || !CreatureHungerPlanner.TrySettle(
                            creatureInventory,
                            elapsedSeconds,
                            itemCatalog,
                            out var hungerPlan))
                    {
                        dungeonCreatureStomachCheckpointSeconds = now;
                        return true;
                    }

                    if (!hungerPlan.Changed)
                    {
                        dungeonCreatureStomachCheckpointSeconds = now;
                        return true;
                    }

                    var saved = await store.SaveCharacterItemSpacesAsync(
                        accountName,
                        activeCharacter.Id,
                        currentGold,
                        mainInventory.Values,
                        equippedInventory.Values,
                        warehouseInventory.Values,
                        cancellationToken,
                        avatarInventory: avatarInventory.Values,
                        creatureInventory: hungerPlan.Inventory.Values);
                    if (saved is null)
                    {
                        logger.LogWarning(
                            "Could not persist Creature stomach settlement for character {CharacterId} in dungeon {DungeonId}.",
                            activeCharacter.Id,
                            currentDungeonId);
                        return false;
                    }

                    creatureInventory = hungerPlan.Inventory;
                    activeCharacter = activeCharacter with
                    {
                        CreatureInventory = hungerPlan.Inventory.Values
                            .OrderBy(item => item.Slot)
                            .ToList()
                    };
                    dungeonCreatureStomachCheckpointSeconds = now;

                    if (hungerPlan.BecameDead)
                    {
                        // The 13339 timer path emits NOTI 100 exactly when the
                        // effective stomach crosses below one.  Do this even
                        // for a silent settlement so the client cannot keep a
                        // live map object after a transactional refresh.
                        await SendEquippedCreatureDeathAsync();
                    }

                    if (notifyClient && hungerPlan.StomachChanged)
                    {
                        var stomachState = GameProtocolEngine.CreateCreatureState(
                            hungerPlan.CreatureUid,
                            hungerPlan.Stomach);
                        await stomachState.WriteAsync(stream, cancellationToken);
                        Interlocked.Increment(ref runtimeSession.SentPackets);
                        LogPacket("TX", runtimeSession, stomachState);
                    }

                    if (notifyClient && hungerPlan.ConsumedFoodCount != 0)
                    {
                        var creatureItems = CreateCreatureInventoryItemListPacket(
                            creatureInventory.Values);
                        await creatureItems.WriteAsync(stream, cancellationToken);
                        Interlocked.Increment(ref runtimeSession.SentPackets);
                        LogPacket("TX", runtimeSession, creatureItems);
                    }

                    logger.LogInformation(
                        "Dungeon {DungeonId} settled {ElapsedSeconds}s of Creature hunger for uid {CreatureUid}: stomach {PreviousStomach}->{Stomach}, remainder {PreviousRemainder}->{Remainder}s, auto-consumed food item 24 x{ConsumedFoodCount}.",
                        currentDungeonId,
                        hungerPlan.ElapsedSeconds,
                        hungerPlan.CreatureUid,
                        hungerPlan.PreviousStomach,
                        hungerPlan.Stomach,
                        hungerPlan.PreviousRemainderSeconds,
                        hungerPlan.RemainderSeconds,
                        hungerPlan.ConsumedFoodCount);
                    return true;
                }

                async Task GrantDungeonCreatureExperienceAsync()
                {
                    if (dungeonConsumedFatigue == 0
                        || activeCharacter is null
                        || !creatureInventory.TryGetValue(
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
                        dungeonConsumedFatigue = 0;
                        return;
                    }

                    var previousLevel = creature.CreatureLevel ?? 1;
                    var bonusRate =
                        GetEquippedCreatureExperienceBonusRate(previousLevel);
                    var grant = creatureExperienceCatalog.Grant(
                        creature.CreatureExperience ?? 0,
                        previousLevel,
                        creatureDefinition.CreatureSpecies,
                        dungeonConsumedFatigue,
                        bonusRate);
                    if (grant.GrantedExperience == 0)
                    {
                        dungeonConsumedFatigue = 0;
                        return;
                    }

                    var plannedCreatureInventory = creatureInventory.ToDictionary();
                    plannedCreatureInventory[
                        CharacterCreatureInventoryLayout.EquippedCreatureSlot] =
                        creature with
                        {
                            CreatureExperience = grant.Experience,
                            CreatureLevel = grant.Level
                        };
                    var saved = await store.SaveCharacterItemSpacesAsync(
                        accountName,
                        activeCharacter.Id,
                        currentGold,
                        mainInventory.Values,
                        equippedInventory.Values,
                        warehouseInventory.Values,
                        serverToken,
                        avatarInventory: avatarInventory.Values,
                        creatureInventory: plannedCreatureInventory.Values);
                    if (saved is null)
                    {
                        logger.LogWarning(
                            "Could not persist Creature experience for character {CharacterId} after clearing dungeon {DungeonId}.",
                            activeCharacter.Id,
                            currentDungeonId);
                        return;
                    }

                    creatureInventory = plannedCreatureInventory;
                    activeCharacter = activeCharacter with
                    {
                        CreatureInventory = plannedCreatureInventory.Values
                            .OrderBy(item => item.Slot)
                            .ToList()
                    };
                    dungeonConsumedFatigue = 0;
                    var experienceNotification =
                        GameProtocolEngine.CreateCreatureGainExperience(
                            grant.Level,
                            grant.Experience);
                    await experienceNotification.WriteAsync(stream, serverToken);
                    Interlocked.Increment(ref runtimeSession.SentPackets);
                    LogPacket("TX", runtimeSession, experienceNotification);
                    logger.LogInformation(
                        "Dungeon {DungeonId} granted Creature uid {CreatureUid} {GrantedExperience} experience from {ConsumedFatigue} fatigue and {BonusExperience} artifact bonus: exp {PreviousExperience}->{Experience}, level {PreviousLevel}->{Level}.",
                        currentDungeonId,
                        creature.CountOrValue,
                        grant.GrantedExperience,
                        grant.BaseExperience,
                        grant.BonusExperience,
                        grant.PreviousExperience,
                        grant.Experience,
                        grant.PreviousLevel,
                        grant.Level);
                }

                async Task SendEnterGameWorldCompleteAsync()
                {
                    // USERINFO locks keyboard and mouse commands at priority
                    // two. DFLegacy notification 136 clears both locks after the
                    // local actor has been constructed by AREA_USERS.
                    var gameWorldComplete =
                        GameProtocolEngine.CreateEnterGameWorldComplete();
                    await gameWorldComplete.WriteAsync(stream, serverToken);
                    Interlocked.Increment(ref runtimeSession.SentPackets);
                    LogPacket("TX", runtimeSession, gameWorldComplete);
                }

                async Task<(bool Saved, bool[] Granted)> SaveClearRewardEquipmentAsync(
                    int targetGold,
                    IReadOnlyList<ushort?> itemIds)
                {
                    var granted = new bool[itemIds.Count];
                    if (activeCharacter is null)
                    {
                        return (false, granted);
                    }

                    var plannedInventory = mainInventory.ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value);
                    var plannedWarehouseCapacity = warehouseCapacity;
                    var overflowMails = new List<CreateCharacterMailRequest>();
                    for (var index = 0; index < itemIds.Count; index++)
                    {
                        if (!itemIds[index].HasValue)
                        {
                            continue;
                        }

                        if (CharacterWarehouseProgression.TryGetItemTargetCapacity(
                                itemIds[index]!.Value,
                                out var targetCapacity))
                        {
                            plannedWarehouseCapacity = Math.Max(
                                plannedWarehouseCapacity,
                                targetCapacity);
                            granted[index] = true;
                            continue;
                        }

                        var rewardItem = CharacterItemIdentity.Ensure(
                            new CharacterMailAttachmentRecord(
                            itemIds[index]!.Value,
                            1,
                            Durability: itemCatalog.GetInitialDurability(
                                itemIds[index]!.Value),
                            SealState: CharacterItemSealing.GetInitialSealState(
                                itemCatalog,
                                itemIds[index]!.Value)),
                            itemCatalog);
                        if (CharacterInventoryPlanner.TryPlace(
                                plannedInventory,
                                rewardItem,
                                itemCatalog,
                                GetInventoryWeightLimit(activeCharacter),
                                out var plan,
                                out _))
                        {
                            plannedInventory = plan.Inventory;
                            granted[index] = true;
                            continue;
                        }

                        if (!itemCatalog.TryGetDefinition(
                                rewardItem.ItemId,
                                out _))
                        {
                            continue;
                        }

                        // Card rewards must never become ground drops. Preserve
                        // the generated instance in the same inventory/mail transaction.
                        overflowMails.AddRange(CreateOverflowMailRequests(
                            [rewardItem],
                            "Dungeon clear reward"));

                        granted[index] = true;
                    }

                    var normalizedGold = Math.Max(0, targetGold);
                    if (normalizedGold == currentGold
                        && !granted.Any(value => value)
                        && overflowMails.Count == 0)
                    {
                        return (true, granted);
                    }

                    var saved = await store.SaveCharacterInventoryAsync(
                        accountName,
                        activeCharacter.Id,
                        normalizedGold,
                        plannedInventory.Values,
                        equippedInventory.Values,
                        serverToken,
                        overflowMails,
                        warehouseCapacity: plannedWarehouseCapacity,
                        expectedWarehouseCapacity: warehouseCapacity);
                    if (saved is null)
                    {
                        return (false, new bool[itemIds.Count]);
                    }

                    var warehouseCapacityChanged =
                        AdoptSavedWarehouseUpgradeState(saved);
                    currentGold = normalizedGold;
                    if (overflowMails.Count > 0)
                    {
                        characterSessions.NotifyMailReceived(activeCharacter.CharacterNo);
                    }

                    if (warehouseCapacityChanged)
                    {
                        await SendWarehouseStateAsync();
                    }

                    return (true, granted);
                }

                async Task<(bool Saved, bool InventoryChanged, ushort? RewardItemId)>
                    GrantFreeClearRewardAsync(byte selectedCardIndex)
                {
                    if (dungeonClearReward is null
                        || selectedCardIndex >= GameProtocolEngine.ClearRewardCardColumnCount)
                    {
                        return (false, false, null);
                    }

                    if (dungeonFreeCardIndex.HasValue)
                    {
                        return (
                            dungeonFreeCardIndex == selectedCardIndex,
                            false,
                            dungeonClearReward.FreeItemId
                                ?? dungeonClearReward.PremiumItemId);
                    }

                    var goldBeforeGrant = currentGold;
                    var settledGold = (int)Math.Min(
                        int.MaxValue,
                        (long)currentGold + dungeonClearReward.FreeGoldAmount);
                    var freeGrant = await SaveClearRewardEquipmentAsync(
                        settledGold,
                        [
                            dungeonClearReward.FreeItemId,
                            dungeonClearReward.PremiumItemId
                        ]);
                    if (!freeGrant.Saved)
                    {
                        return (false, false, null);
                    }

                    dungeonFreeCardIndex = selectedCardIndex;
                    dungeonCardSelectionDeadline = null;
                    var rewardItemId = freeGrant.Granted[0]
                        ? dungeonClearReward.FreeItemId
                        : freeGrant.Granted[1]
                            ? dungeonClearReward.PremiumItemId
                            : null;
                    return (
                        true,
                        currentGold != goldBeforeGrant
                            || freeGrant.Granted.Any(granted => granted),
                        rewardItemId);
                }

                async Task SendCardSelectionSnapshotAsync(bool inventoryChanged)
                {
                    if (dungeonClearReward is null)
                    {
                        return;
                    }

                    var selectCardReply = GameProtocolEngine.CreateSelectCardReply(
                        dungeonFreeCardIndex,
                        dungeonGoldCardIndex,
                        goldRewardRevealed: dungeonGoldCardIndex.HasValue,
                        goldRewardItemId: dungeonClearReward.GoldItemId);
                    await selectCardReply.WriteAsync(stream, serverToken);
                    Interlocked.Increment(ref runtimeSession.SentPackets);
                    LogPacket("TX", runtimeSession, selectCardReply);
                    if (!inventoryChanged)
                    {
                        return;
                    }

                    var inventoryRefresh = CreateMainInventoryPacket(
                        currentGold,
                        currentVictoryPoints,
                        mainInventory.Values);
                    await inventoryRefresh.WriteAsync(stream, serverToken);
                    Interlocked.Increment(ref runtimeSession.SentPackets);
                    LogPacket("TX", runtimeSession, inventoryRefresh);
                }

                async Task<bool> TryAutomaticallySelectFreeCardAsync(string source)
                {
                    if (dungeonClearReward is null || dungeonFreeCardIndex.HasValue)
                    {
                        return true;
                    }

                    var automaticCardIndex =
                        ClearRewardCardPlanner.FindLowestAvailableFreeCardIndex(
                            GameProtocolEngine.ClearRewardCardColumnCount,
                            []);
                    if (!automaticCardIndex.HasValue)
                    {
                        return true;
                    }

                    var automaticGrant = await GrantFreeClearRewardAsync(
                        automaticCardIndex.Value);
                    if (!automaticGrant.Saved)
                    {
                        logger.LogWarning(
                            "Could not automatically settle dungeon {DungeonId} free card for user {UserId} through {Source}",
                            currentDungeonId,
                            localUserId,
                            source);
                        return false;
                    }

                    // DNF 1.0.1.9 consumes the same CMD 74 ownership snapshot
                    // for manual and TimerCardSelect assignments.
                    await SendCardSelectionSnapshotAsync(
                        automaticGrant.InventoryChanged);
                    logger.LogInformation(
                        "Dungeon {DungeonId} automatically selected free card {CardIndex} for user {UserId} through {Source}; reward={RewardItemId}",
                        currentDungeonId,
                        automaticCardIndex.Value,
                        localUserId,
                        source,
                        automaticGrant.RewardItemId);
                    return true;
                }

                uint GetAuthoritativeQuestTrigger(
                    QuestDefinition definition,
                    IEnumerable<CharacterItemRecord>? inventory,
                    int gold,
                    uint victoryPoints) => QuestPvpRankRequirement.IsPvpRank(definition)
                        ? QuestPvpRankRequirement.GetTrigger(definition,
                            pvpExperienceCatalog.GetGradeForPoints(activeCharacter?.PvpPoints ?? 0))
                        : QuestItemRequirementPlanner.GetInitialTrigger(
                            definition, inventory, gold, victoryPoints);

                List<CharacterQuestRecord> RefreshSeekingQuestTriggers(
                    IEnumerable<CharacterQuestRecord> quests,
                    IEnumerable<CharacterItemRecord> inventory,
                    int gold,
                    uint victoryPoints,
                    out List<(ushort QuestId, uint OldTrigger, uint NewTrigger)> changes)
                {
                    changes = [];
                    var refreshed = new List<CharacterQuestRecord>();
                    foreach (var quest in quests)
                    {
                        if (!questCatalog.TryGetDefinition(quest.QuestId, out var definition)
                            || !QuestItemRequirementPlanner.IsSeeking(definition))
                        {
                            refreshed.Add(quest);
                            continue;
                        }

                        var trigger = QuestItemRequirementPlanner.GetInitialTrigger(
                            definition,
                            inventory,
                            gold,
                            victoryPoints);
                        refreshed.Add(quest with { Trigger = trigger });
                        if (trigger != quest.Trigger)
                        {
                            changes.Add((quest.QuestId, quest.Trigger, trigger));
                        }
                    }

                    return refreshed;
                }

                async Task GrantQuestItemDropsAsync(
                    Func<
                        QuestDefinition,
                        IEnumerable<CharacterItemRecord>,
                        IReadOnlyList<QuestItemDrop>> planDrops,
                    string source)
                {
                    if (activeCharacter is null || currentDungeonId == 0)
                    {
                        return;
                    }

                    var plannedInventory = mainInventory.ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value);
                    var plannedWarehouseCapacity = warehouseCapacity;
                    var changedSlots = new HashSet<ushort>();
                    var grantedDrops = new List<(ushort QuestId, QuestItemDrop Drop)>();
                    foreach (var activeQuest in currentQuests.ToArray())
                    {
                        if (!questCatalog.TryGetDefinition(
                                activeQuest.QuestId,
                                out var definition))
                        {
                            continue;
                        }

                        foreach (var drop in planDrops(definition, plannedInventory.Values))
                        {
                            if (CharacterWarehouseProgression.TryGetItemTargetCapacity(
                                    drop.ItemId,
                                    out var targetCapacity))
                            {
                                plannedWarehouseCapacity = Math.Max(
                                    plannedWarehouseCapacity,
                                    targetCapacity);
                                grantedDrops.Add((activeQuest.QuestId, drop));
                                continue;
                            }

                            var taskItem = CharacterItemIdentity.Ensure(
                                new CharacterMailAttachmentRecord(
                                drop.ItemId,
                                drop.Count,
                                Durability: itemCatalog.GetInitialDurability(drop.ItemId),
                                SealState: CharacterItemSealing.GetInitialSealState(
                                    itemCatalog,
                                    drop.ItemId)),
                                itemCatalog);
                            if (!CharacterInventoryPlanner.TryPlace(
                                    plannedInventory,
                                    taskItem,
                                    itemCatalog,
                                    GetInventoryWeightLimit(activeCharacter),
                                    out var placement,
                                    out var failure))
                            {
                                logger.LogWarning(
                                    "Could not grant quest item {ItemId} x{Count} for quest {QuestId} from {Source}: {Failure}.",
                                    drop.ItemId,
                                    drop.Count,
                                    activeQuest.QuestId,
                                    source,
                                    failure);
                                continue;
                            }

                            plannedInventory = placement.Inventory;
                            changedSlots.UnionWith(placement.ChangedSlots);
                            grantedDrops.Add((activeQuest.QuestId, drop));
                        }
                    }

                    var plannedQuests = RefreshSeekingQuestTriggers(
                        currentQuests,
                        plannedInventory.Values,
                        currentGold,
                        currentVictoryPoints,
                        out var triggerChanges);
                    if (changedSlots.Count == 0
                        && triggerChanges.Count == 0
                        && plannedWarehouseCapacity == warehouseCapacity)
                    {
                        return;
                    }

                    var saved = await store.SaveCharacterQuestProgressAsync(
                        accountName,
                        activeCharacter.Id,
                        plannedInventory.Values,
                        plannedQuests,
                        completedQuestIds,
                        serverToken,
                        warehouseCapacity: plannedWarehouseCapacity,
                        expectedWarehouseCapacity: warehouseCapacity);
                    if (saved is null)
                    {
                        logger.LogWarning(
                            "Could not persist quest item grants from {Source}; inventory and quest progress were left unchanged.",
                            source);
                        return;
                    }

                    var warehouseCapacityChanged =
                        AdoptSavedWarehouseUpgradeState(saved);
                    currentQuests = plannedQuests;
                    if (changedSlots.Count > 0)
                    {
                        var itemUpdate = GameProtocolEngine.CreateUpdateItemList(
                            0,
                            changedSlots
                                .OrderBy(slot => slot)
                                .Select(slot => ToGameInventoryEntry(mainInventory[slot]))
                                .ToArray());
                        await itemUpdate.WriteAsync(stream, serverToken);
                        Interlocked.Increment(ref runtimeSession.SentPackets);
                        LogPacket("TX", runtimeSession, itemUpdate);
                    }

                    if (warehouseCapacityChanged)
                    {
                        await SendWarehouseStateAsync();
                    }

                    foreach (var change in triggerChanges)
                    {
                        var triggerUpdate = GameProtocolEngine.CreateSetQuestTriggerReply(
                            change.QuestId,
                            change.NewTrigger);
                        await triggerUpdate.WriteAsync(stream, serverToken);
                        Interlocked.Increment(ref runtimeSession.SentPackets);
                        LogPacket("TX", runtimeSession, triggerUpdate);
                    }

                    foreach (var grant in grantedDrops)
                    {
                        logger.LogInformation(
                            "Granted quest item from {Source}: quest={QuestId}, item={ItemId}, count={Count}.",
                            source,
                            grant.QuestId,
                            grant.Drop.ItemId,
                            grant.Drop.Count);
                    }
                }

                Task GrantMonsterQuestItemsAsync(int monsterIndex) =>
                    GrantQuestItemDropsAsync(
                        (quest, inventory) => QuestItemDropPlanner.PlanMonster(
                            quest,
                            currentDungeonId,
                            currentDungeonDifficulty,
                            monsterIndex,
                            inventory,
                            chance => GameRandomSource.Shared.Next(100) < chance),
                        $"monster {monsterIndex}");

                Task GrantClearQuestItemsAsync() =>
                    GrantQuestItemDropsAsync(
                        (quest, inventory) => QuestItemDropPlanner.PlanClear(
                            quest,
                            currentDungeonId,
                            currentDungeonDifficulty,
                            inventory,
                        chance => GameRandomSource.Shared.Next(100) < chance),
                        $"dungeon {currentDungeonId} clear");

                async Task CompleteClearQuestTriggersAsync(string source)
                {
                    if (activeCharacter is null || currentDungeonId == 0)
                    {
                        return;
                    }

                    var completedSimpleClearQuestIds = currentQuests
                        .Where(quest => quest.Trigger != 0)
                        .Where(quest =>
                            questCatalog.TryGetDefinition(
                                quest.QuestId,
                                out var definition)
                            && ((dungeonClearEnabled
                                    && QuestDungeonClearRequirement.IsSimpleClear(definition)
                                    && QuestDungeonClearRequirement.Matches(
                                        definition, currentDungeonId, currentDungeonDifficulty))
                                || dungeonRun.HasClearedMap(
                                    QuestMapRequirement.GetTargetMapId(definition))))
                        .Select(quest => quest.QuestId)
                        .ToHashSet();
                    if (completedSimpleClearQuestIds.Count == 0)
                    {
                        return;
                    }

                    var plannedQuests = currentQuests
                        .Select(quest => completedSimpleClearQuestIds.Contains(quest.QuestId)
                            ? quest with { Trigger = 0 }
                            : quest)
                        .ToList();
                    var savedQuestProgress = await store.SaveCharacterQuestsAsync(
                        accountName,
                        activeCharacter.Id,
                        plannedQuests,
                        completedQuestIds,
                        cancellationToken: serverToken);
                    if (savedQuestProgress is null)
                    {
                        logger.LogWarning(
                            "Could not persist clear quest progress after {Source} in dungeon {DungeonId}.",
                            source,
                            currentDungeonId);
                        return;
                    }

                    currentQuests = plannedQuests;
                    activeCharacter = activeCharacter with
                    {
                        Quests = savedQuestProgress.Quests,
                        CompletedQuestIds = savedQuestProgress.CompletedQuestIds
                    };
                    foreach (var questId in completedSimpleClearQuestIds.Order())
                    {
                        var triggerUpdate =
                            GameProtocolEngine.CreateSetQuestTriggerReply(
                                questId,
                                trigger: 0);
                        await triggerUpdate.WriteAsync(stream, serverToken);
                        Interlocked.Increment(ref runtimeSession.SentPackets);
                        LogPacket("TX", runtimeSession, triggerUpdate);
                        logger.LogInformation(
                            "Completed clear quest {QuestId} after {Source} in dungeon {DungeonId} difficulty {Difficulty}.",
                            questId,
                            source,
                            currentDungeonId,
                            currentDungeonDifficulty);
                    }
                }

                async Task SendDungeonSelectionEligibilityAsync()
                {
                    var questsMet = selectionWorldMap?.MeetsHellQuests(completedQuestIds) == true;
                    var missingItems = selectionWorldMap is { HasHellDungeon: true }
                        && !selectionWorldMap.HasHellItems(mainInventory.Values);
                    var packet = GameProtocolEngine.CreateEnterSelectDungeon(
                        questsMet, missingItems ? [(ushort)0] : []);
                    await packet.WriteAsync(stream, serverToken);
                    Interlocked.Increment(ref runtimeSession.SentPackets);
                    LogPacket("TX", runtimeSession, packet);
                    logger.LogInformation(
                        "World map {WorldMapId} eligibility for {CharacterId}: hellQuests={QuestsMet}, missingHellItems={MissingItems}.",
                        selectionWorldMap?.Id, activeCharacter?.Id, questsMet, missingItems);
                }

                async Task ReturnToTownFromDungeonAsync(
                    string reason,
                    bool forcedDungeonExit = false)
                {
                    if (currentDungeonId == 0 && !inDungeonSelection)
                    {
                        logger.LogWarning(
                            "Ignored {Reason} return-to-town request because the user is not in a dungeon or dungeon selection.",
                            reason);
                        return;
                    }

                    if (currentDungeonId != 0 && !tutorialDungeonActive)
                    {
                        await SettleDungeonCreatureHungerAsync(
                            notifyClient: true,
                            serverToken);
                    }

                    var leavingDungeonId = currentDungeonId;
                    var leftDungeonSelection = inDungeonSelection;
                    var gainedExperience = dungeonMonsterExperienceGained;
                    var leveledUp = dungeonLeveledUp;
                    if (activeCharacter is not null && gainedExperience > 0)
                    {
                        var savedProgression = await store.SaveCharacterProgressionAsync(
                            accountName,
                            activeCharacter.Id,
                            activeCharacter.Level,
                            activeCharacter.Experience,
                            currentSp,
                            serverToken);
                        if (savedProgression is not null)
                        {
                            activeCharacter = activeCharacter with
                            {
                                Level = savedProgression.Level,
                                Experience = savedProgression.Experience,
                                Sp = savedProgression.Sp
                            };
                        }
                    }

                    var hasBlackDiamond = false;
                    if (activeCharacter is not null)
                    {
                        var blackDiamondState = await store.GetPremiumServiceAsync(
                            accountName,
                            serverToken);
                        hasBlackDiamond = blackDiamondState.Active;
                        if (blackDiamondState.Active
                            && blackDiamondState.ExpiresAt is { } blackDiamondExpiry)
                        {
                            premiumExpiryUnix[
                                GameProtocolEngine.BlackDiamondServiceType] =
                                blackDiamondExpiry;
                        }
                        else
                        {
                            premiumExpiryUnix.TryRemove(
                                GameProtocolEngine.BlackDiamondServiceType,
                                out _);
                        }
                    }

                    var enteredWeakness = false;
                    if (activeCharacter is not null
                        && !activeCharacter.IsWeakened
                        && DungeonWeaknessPolicy.ShouldApply(
                            activeCharacter.Level,
                            forcedDungeonExit,
                            dungeonActive: leavingDungeonId != 0,
                            dungeonCleared: dungeonClearEnabled,
                            hasBlackDiamond))
                    {
                        var weakenedCharacter = await store.SetCharacterWeaknessRecoveryAsync(
                            activeCharacter.Id,
                            DungeonWeaknessPolicy.InitialRecovery,
                            serverToken);
                        if (weakenedCharacter is not null)
                        {
                            activeCharacter = weakenedCharacter;
                            currentGold = activeCharacter.Gold;
                            enteredWeakness = true;
                        }
                        else
                        {
                            logger.LogError(
                                "Could not persist weakness for character {CharacterId} while returning from dungeon {DungeonId}; the client will retain normal stamina.",
                                activeCharacter.Id,
                                leavingDungeonId);
                        }
                    }

                    currentDungeonId = 0;
                    currentDungeonLayout = null;
                    currentDungeonRoom = null;
                    inDungeonSelection = false;
                    dungeonSelectionDeadline = null;
                    selectionWorldMap = null;
                    aliveDungeonMonsters.Clear();
                    reportedBossDeaths.Clear();
                    bossCascadeMonsterDeaths.Clear();
                    destroyedPassiveObjects.Clear();
                    groundDungeonItems.ResetDungeon();
                    dungeonRun.Stop();
                    tutorialDungeonActive = false;
                    dungeonCharacterAlive = true;
                    dungeonDeathGeneration++;
                    dungeonDeathTimeout = null;
                    currentDungeonDifficulty = 0;
                    dungeonClearEnabled = false;
                    dungeonResultSent = false;
                    dungeonCardLayoutSent = false;
                    dungeonCardRightsSent = false;
                    dungeonCardSelectionDeadline = null;
                    dungeonClearReward = null;
                    dungeonFreeCardIndex = null;
                    dungeonGoldCardIndex = null;
                    dungeonMonsterExperienceGained = 0;
                    dungeonClearExperience = null;
                    dungeonConsumedFatigue = 0;
                    dungeonCreatureStomachCheckpointSeconds = null;
                    dungeonLeveledUp = false;
                    dungeonNormalMonsterKills = 0;
                    dungeonChampionMonsterKills = 0;
                    dungeonBossMonsterKills = 0;
                    dungeonChampionRewardOrdinal = 0;
                    currentTownId = dungeonReturnTownId;
                    currentAreaId = dungeonReturnAreaId;
                    currentX = dungeonReturnX;
                    currentY = dungeonReturnY;
                    currentDirection = dungeonReturnDirection;
                    currentMovementValue = 100;
                    await SaveCurrentLocationAsync(serverToken);

                    var townState = GameProtocolEngine.CreateUserState(
                        localUserId,
                        state: 0);
                    await townState.WriteAsync(stream, serverToken);
                    Interlocked.Increment(ref runtimeSession.SentPackets);
                    LogPacket("TX", runtimeSession, townState);

                    var townUser = new GameTownUser(
                        localUserId,
                        currentX,
                        currentY,
                        currentDirection);
                    var userArea = GameProtocolEngine.CreateUserArea(
                        currentTownId,
                        currentAreaId,
                        townUser);
                    await userArea.WriteAsync(stream, serverToken);
                    Interlocked.Increment(ref runtimeSession.SentPackets);
                    LogPacket("TX", runtimeSession, userArea);

                    var areaUsers = GameProtocolEngine.CreateAreaUsers(
                        currentTownId,
                        currentAreaId,
                        [townUser]);
                    await areaUsers.WriteAsync(stream, serverToken);
                    Interlocked.Increment(ref runtimeSession.SentPackets);
                    LogPacket("TX", runtimeSession, areaUsers);
                    areaRosterInitialized = true;
                    await SendTownFatigueStateAsync();
                    await SendTownAppearanceAsync(waitForTownActor: true);
                    await SendSilentTownStaminaStateAsync();
                    await SendEnterGameWorldCompleteAsync();
                    // The town return bypasses SET_USER_AREA, so refresh the
                    // town presence (and any same-area residents' view) here.
                    PublishTownPresence();
                    if (leveledUp)
                    {
                        await SendCurrentSkillInfoAsync();
                        await SendAcceptableQuestListAsync();
                    }

                    if (leftDungeonSelection && leavingDungeonId == 0)
                    {
                        logger.LogInformation(
                            "Dungeon selection returned user {UserId} to town through {Reason}.",
                            localUserId,
                            reason);
                    }
                    else
                    {
                        logger.LogInformation(
                            "Dungeon {DungeonId} returned user {UserId} to town through {Reason}; exp={Experience}, weakened={IsWeakened}, enteredWeakness={EnteredWeakness}, blackDiamond={HasBlackDiamond}.",
                            leavingDungeonId,
                            localUserId,
                            reason,
                            gainedExperience,
                            activeCharacter?.IsWeakened ?? false,
                            enteredWeakness,
                            hasBlackDiamond);
                    }
                }

                async Task SendInventoryMoveNoOpAsync(
                    byte sourceListType,
                    ushort sourceSlot,
                    byte destinationListType,
                    ushort destinationSlot,
                    string reason)
                {
                    var noOpReply = GameProtocolEngine.CreateMoveItemSpaceReply(
                        sourceListType,
                        sourceSlot,
                        movedCount: 0,
                        destinationListType,
                        destinationSlot);
                    await noOpReply.WriteAsync(stream, serverToken);
                    Interlocked.Increment(ref runtimeSession.SentPackets);
                    LogPacket("TX", runtimeSession, noOpReply);

                    if (sourceListType == 0 || destinationListType == 0)
                    {
                        var mainRefresh = CreateMainInventoryPacket(
                            currentGold,
                            currentVictoryPoints,
                            mainInventory.Values);
                        await mainRefresh.WriteAsync(stream, serverToken);
                        Interlocked.Increment(ref runtimeSession.SentPackets);
                        LogPacket("TX", runtimeSession, mainRefresh);
                    }

                    if (sourceListType == 2 || destinationListType == 2)
                    {
                        var warehouseRefresh = CreateWarehousePacket(
                            warehouseInventory.Values,
                            warehouseCapacity);
                        await warehouseRefresh.WriteAsync(stream, serverToken);
                        Interlocked.Increment(ref runtimeSession.SentPackets);
                        LogPacket("TX", runtimeSession, warehouseRefresh);
                    }

                    if (sourceListType == 1 || destinationListType == 1)
                    {
                        var avatarRefresh = CreateAvatarInventoryPacket(
                            avatarInventory.Values);
                        await avatarRefresh.WriteAsync(stream, serverToken);
                        Interlocked.Increment(ref runtimeSession.SentPackets);
                        LogPacket("TX", runtimeSession, avatarRefresh);
                    }

                    if (sourceListType == 7 || destinationListType == 7)
                    {
                        foreach (var creatureRefresh in CreateCreatureInventoryPackets(
                                     creatureInventory.Values))
                        {
                            await creatureRefresh.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, creatureRefresh);
                        }
                    }

                    if ((sourceListType == 3 || destinationListType == 3)
                        && sourceListType != 7
                        && destinationListType != 7)
                    {
                        await SendCurrentEquipmentStateAsync();
                    }

                    logger.LogInformation(
                        "Completed rejected inventory move as a no-op from {SourceList}:{SourceSlot} to {DestinationList}:{DestinationSlot}: {Reason}.",
                        sourceListType,
                        sourceSlot,
                        destinationListType,
                        destinationSlot,
                        reason);
                }

                async Task SendMainInventoryMoveDeltaAsync(params ushort[] slots)
                {
                    foreach (var slot in slots
                                 .Where(CharacterInventoryLayout.IsMainItemSlot)
                                 .Distinct())
                    {
                        var entry = mainInventory.TryGetValue(slot, out var item)
                            ? ToGameInventoryEntry(item)
                            : new GameInventoryEntry(slot, ushort.MaxValue, 0);
                        var update = GameProtocolEngine.CreateUpdateItemList(0, [entry]);
                        await update.WriteAsync(stream, serverToken);
                        Interlocked.Increment(ref runtimeSession.SentPackets);
                        LogPacket("TX", runtimeSession, update);
                    }
                }

                IReadOnlyList<ushort> GetAcceptableQuestIds() => activeCharacter is null
                    ? []
                    : questCatalog.GetAcceptableQuestIds(
                        activeCharacter,
                        currentQuests,
                        completedQuestIds,
                        currentTownId == 1 ? 2 : null,
                        itemCatalog);

                IReadOnlyList<ushort> GetVisibleDungeonIds() =>
                    HiddenDungeonUnlockCatalog.ResolveVisibleDungeonIds(
                        dungeonCatalog.DungeonIds,
                        activeCharacter?.UnlockedDungeonIds)
                    .Where(dungeonId => dungeonId != GameProtocolEngine.TutorialDungeonId)
                    .ToArray();

                IReadOnlyList<GameDungeonPermissionEntry> GetDungeonPermissions()
                {
                    var permissions = GetVisibleDungeonIds()
                        .Select(dungeonId => new GameDungeonPermissionEntry(
                            dungeonId,
                            DungeonDifficultyProgression.GetMaximumDifficulty(
                                activeCharacter?.DungeonProgress,
                            dungeonId)))
                        .ToList();
                    if (activeCharacter is { TutorialStarted: false }
                        && permissions.All(permission =>
                            permission.DungeonId != GameProtocolEngine.TutorialDungeonId))
                    {
                        permissions.Add(new GameDungeonPermissionEntry(
                            GameProtocolEngine.TutorialDungeonId,
                            ClearState: 0));
                    }

                    return permissions;
                }

                async Task SendDungeonPermissionsAsync()
                {
                    var permissions = GameProtocolEngine.CreateDungeonPermissions(
                        GetDungeonPermissions());
                    await permissions.WriteAsync(stream, serverToken);
                    Interlocked.Increment(ref runtimeSession.SentPackets);
                    LogPacket("TX", runtimeSession, permissions);
                }

                async Task SendAcceptableQuestListAsync()
                {
                    var acceptable = GameProtocolEngine.CreateAcceptableQuestList(
                        GetAcceptableQuestIds());
                    await acceptable.WriteAsync(stream, serverToken);
                    Interlocked.Increment(ref runtimeSession.SentPackets);
                    LogPacket("TX", runtimeSession, acceptable);
                }

                async Task CompleteTutorialAndReturnAsync(string reason)
                {
                    if (activeCharacter is null
                        || !tutorialDungeonActive
                        || !dungeonClearEnabled
                        || aliveDungeonMonsters.Count != 0)
                    {
                        logger.LogWarning(
                            "Ignored tutorial completion because its room is not cleared for user {UserId}.",
                            localUserId);
                        return;
                    }

                    CharacterQuestRecord? tutorialQuest = null;
                    var tutorialQuestWasActive = currentQuests.Any(quest =>
                        quest.QuestId == QuestCatalog.TutorialCompletionQuestId);
                    if (!tutorialQuestWasActive
                        && !completedQuestIds.Contains(QuestCatalog.TutorialCompletionQuestId))
                    {
                        if (currentQuests.Count >= 3)
                        {
                            logger.LogWarning(
                                "Tutorial completion quest {QuestId} was not activated because the active quest list is full.",
                                QuestCatalog.TutorialCompletionQuestId);
                        }
                        else if (!questCatalog.TryGetDefinition(
                                     QuestCatalog.TutorialCompletionQuestId,
                                     out var definition)
                                 || !QuestCatalog.IsTutorialCompletionQuestDefinition(definition))
                        {
                            logger.LogWarning(
                                "Tutorial completion quest {QuestId} is missing or does not match the target client definition.",
                                QuestCatalog.TutorialCompletionQuestId);
                        }
                        else
                        {
                            tutorialQuest = new CharacterQuestRecord(
                                QuestCatalog.TutorialCompletionQuestId,
                                Trigger: 0);
                        }
                    }

                    var saved = await store.CompleteCharacterTutorialAsync(
                        accountName,
                        activeCharacter.Id,
                        tutorialQuest: tutorialQuest,
                        level: activeCharacter.Level,
                        experience: activeCharacter.Experience,
                        skillPoints: currentSp,
                        cancellationToken: serverToken);
                    if (saved is null)
                    {
                        logger.LogWarning(
                            "Could not persist tutorial completion for character {CharacterId}.",
                            activeCharacter.Id);
                        return;
                    }

                    activeCharacter = activeCharacter with
                    {
                        TutorialStarted = saved.TutorialStarted,
                        Quests = saved.Quests,
                        CompletedQuestIds = saved.CompletedQuestIds,
                        Level = saved.Level,
                        Experience = saved.Experience,
                        Sp = saved.Sp
                    };
                    currentQuests = (saved.Quests ?? [])
                        .Where(quest => quest.QuestId != ushort.MaxValue)
                        .Take(3)
                        .ToList();
                    completedQuestIds = (saved.CompletedQuestIds ?? []).ToHashSet();
                    var tutorialQuestActivated = !tutorialQuestWasActive
                        && currentQuests.Any(quest =>
                            quest.QuestId == QuestCatalog.TutorialCompletionQuestId);
                    if (tutorialQuestActivated)
                    {
                        var acceptQuest = GameProtocolEngine.CreateAcceptQuestReply(
                            QuestCatalog.TutorialCompletionQuestId,
                            trigger: 1);
                        await acceptQuest.WriteAsync(stream, serverToken);
                        Interlocked.Increment(ref runtimeSession.SentPackets);
                        LogPacket("TX", runtimeSession, acceptQuest);

                        var readyQuest = GameProtocolEngine.CreateSetQuestTriggerReply(
                            QuestCatalog.TutorialCompletionQuestId,
                            trigger: 0);
                        await readyQuest.WriteAsync(stream, serverToken);
                        Interlocked.Increment(ref runtimeSession.SentPackets);
                        LogPacket("TX", runtimeSession, readyQuest);
                        await SendDungeonPermissionsAsync();
                        await SendAcceptableQuestListAsync();
                        logger.LogInformation(
                            "Activated tutorial completion quest {QuestId} for character {CharacterId}.",
                            QuestCatalog.TutorialCompletionQuestId,
                            activeCharacter.Id);
                    }

                    await ReturnToTownFromDungeonAsync(reason);
                }

                if (isChannelPort)
                {
                    // The launcher applies the process-local channel callback
                    // after the client's startup integrity pass. This client
                    // can reach port 7001 a few hundred milliseconds earlier,
                    // and it never retries a greeting handled by the retired
                    // callback. Hold the first channel packet until that patch
                    // window has safely completed.
                    await Task.Delay(TimeSpan.FromMilliseconds(750), serverToken);
                    var greeting = GameProtocolEngine.CreateCheckConnection(protocolSession.SessionToken);
                    await greeting.WriteAsync(stream, serverToken);
                    Interlocked.Increment(ref runtimeSession.SentPackets);
                    LogPacket("TX", runtimeSession, greeting);
                }

                PacketFrame? queuedRequest = null;
                Task<PacketFrame?>? pendingPacketRead = null;
                while (!serverToken.IsCancellationRequested)
                {
                    var request = queuedRequest;
                    queuedRequest = null;
                    if (request is null)
                    {
                        pendingPacketRead ??= PacketFrame.ReadAsync(
                            stream,
                            options.MaximumPacketLength,
                            serverToken).AsTask();
                        if (isChannelPort && inDungeonSelection
                            && dungeonSelectionDeadline is { } selectionDeadline)
                        {
                            var remaining = selectionDeadline - DateTimeOffset.UtcNow;
                            if (remaining > TimeSpan.Zero)
                            {
                                var completed = await Task.WhenAny(
                                    pendingPacketRead, Task.Delay(remaining, serverToken));
                                if (completed == pendingPacketRead)
                                {
                                    request = await pendingPacketRead;
                                    pendingPacketRead = null;
                                    if (request is null)
                                    {
                                        break;
                                    }
                                }
                            }

                            if (request is null)
                            {
                                // DF2008's CMD 16 failure handler (0x410B05) retries
                                // after 30.3 s, but returns without unlocking controls
                                // when selected index +0x200 is zero (0x410B1C).
                                // Never rely solely on the client's CMD 142 to exit.
                                await ReturnToTownFromDungeonAsync("selection-countdown-expired");
                                continue;
                            }
                        }
                        else if (isChannelPort && dungeonDeathTimeout is { } deathTimeout)
                        {
                            var remaining = deathTimeout.Deadline - DateTimeOffset.UtcNow;
                            if (remaining > TimeSpan.Zero)
                            {
                                var completed = await Task.WhenAny(
                                    pendingPacketRead,
                                    Task.Delay(remaining, serverToken));
                                if (completed == pendingPacketRead)
                                {
                                    request = await pendingPacketRead;
                                    pendingPacketRead = null;
                                    if (request is null)
                                    {
                                        break;
                                    }
                                }
                            }

                            if (request is null)
                            {
                                if (dungeonDeathTimeout is { } currentTimeout
                                    && currentTimeout.Generation == deathTimeout.Generation
                                    && !dungeonCharacterAlive
                                    && currentDungeonId != 0)
                                {
                                    dungeonDeathTimeout = null;
                                    logger.LogInformation(
                                        "Dungeon death countdown expired for user {UserId} in dungeon {DungeonId}; returning to town.",
                                        localUserId,
                                        currentDungeonId);
                                    await ReturnToTownFromDungeonAsync(
                                        "death-countdown-expired",
                                        forcedDungeonExit: true);
                                }

                                continue;
                            }
                        }
                        else if (isChannelPort
                            && dungeonCardSelectionDeadline is { } cardSelectionDeadline)
                        {
                            var remaining = cardSelectionDeadline - DateTimeOffset.UtcNow;
                            if (remaining > TimeSpan.Zero)
                            {
                                var completed = await Task.WhenAny(
                                    pendingPacketRead,
                                    Task.Delay(remaining, serverToken));
                                if (completed == pendingPacketRead)
                                {
                                    request = await pendingPacketRead;
                                    pendingPacketRead = null;
                                    if (request is null)
                                    {
                                        break;
                                    }
                                }
                            }

                            if (request is null)
                            {
                                if (dungeonCardSelectionDeadline
                                        == cardSelectionDeadline)
                                {
                                    dungeonCardSelectionDeadline = null;
                                    if (dungeonCardRightsSent
                                        && dungeonClearReward is not null
                                        && !dungeonFreeCardIndex.HasValue
                                        && !await TryAutomaticallySelectFreeCardAsync(
                                            "card-selection-timer"))
                                    {
                                        dungeonCardSelectionDeadline =
                                            DateTimeOffset.UtcNow
                                            + TimeSpan.FromSeconds(1);
                                    }
                                }

                                continue;
                            }
                        }
                        else if (isChannelPort && !premiumExpiryUnix.IsEmpty)
                        {
                            var observedExpiries = premiumExpiryUnix.ToArray();
                            var nextExpiry = observedExpiries.Min(pair => pair.Value);
                            var remaining = DateTimeOffset
                                .FromUnixTimeSeconds(nextExpiry)
                                - DateTimeOffset.UtcNow;
                            var waitTime = remaining > TimeSpan.FromDays(1)
                                ? TimeSpan.FromDays(1)
                                : remaining;
                            if (waitTime > TimeSpan.Zero)
                            {
                                var completed = await Task.WhenAny(
                                    pendingPacketRead,
                                    Task.Delay(waitTime, serverToken));
                                if (completed == pendingPacketRead)
                                {
                                    request = await pendingPacketRead;
                                    pendingPacketRead = null;
                                    if (request is null)
                                    {
                                        break;
                                    }
                                }
                            }

                            if (request is null)
                            {
                                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                                foreach (var expired in observedExpiries.Where(pair =>
                                             pair.Value <= now))
                                {
                                    if (premiumExpiryUnix.TryGetValue(
                                            expired.Key,
                                            out var currentExpiry)
                                        && currentExpiry <= now
                                        && premiumExpiryUnix.TryRemove(expired.Key, out _)
                                        && activeCharacter is not null)
                                    {
                                        await SendPremiumServiceInfoAsync(expired.Key, 0);
                                    }
                                }

                                continue;
                            }
                        }
                        else
                        {
                            request = await pendingPacketRead;
                            pendingPacketRead = null;
                        }
                    }

                    if (request is null)
                    {
                        break;
                    }

                    PacketFrame? rawFrame = null;
                    if (isChannelPort)
                    {
                        rawFrame = request; // wire bytes before seed decryption
                        if (!gameClientCipher!.TryDecode(request, out var decodedRequest))
                        {
                            LogPacket("RX-ENC", runtimeSession, request);
                            logger.LogWarning(
                                "Could not recover the channel payload seed for packet type {Type}, protocol {ProtocolId}; {CandidateCount} candidate(s) remain.",
                                request.Type, request.ProtocolId, gameClientCipher!.CandidateCount);
                            continue;
                        }

                        request = decodedRequest;
                        logger.LogDebug(
                            "Accepted channel payload with {CandidateCount} compatible rolling-seed candidate(s).",
                            gameClientCipher.CandidateCount);
                    }

                    runtime.CountPacket(runtimeSession);
                    runtimeSession.LastProtocolId = request.ProtocolId;
                    LogPacket("RX", runtimeSession, request, rawFrame);

                    if (options.RejectBadCrc32 && !request.HasValidCrc32)
                    {
                        logger.LogWarning("Rejected frame with bad CRC32 from {SessionId}", runtimeSession.Id);
                        break;
                    }

                    if (Interlocked.Exchange(ref pendingMailAlarm, 0) != 0
                        && activeCharacter is not null)
                    {
                        await SendNewMailAlarmAsync();
                    }

                    while (pendingPremiumUpdates.TryDequeue(out var premiumUpdate))
                    {
                        if (activeCharacter is not null)
                        {
                            await SendPremiumServiceInfoAsync(
                                premiumUpdate.ServiceType,
                                premiumUpdate.RemainingSeconds);
                        }
                    }

                    var queuedCeraValue = Interlocked.Exchange(
                        ref pendingCeraValue,
                        -1);
                    if (queuedCeraValue >= 0 && activeCharacter is not null)
                    {
                        currentCash = queuedCeraValue;
                        var ceraNotification = GameProtocolEngine.CreateCeraUpdate(
                            success: true,
                            value: (uint)queuedCeraValue);
                        await ceraNotification.WriteAsync(stream, serverToken);
                        Interlocked.Increment(ref runtimeSession.SentPackets);
                        LogPacket("TX", runtimeSession, ceraNotification);
                    }

                    if (Volatile.Read(ref pendingFatigueReset) != 0
                        && activeCharacter is not null)
                    {
                        activeCharacter = activeCharacter with { UsedFatigue = 0 };
                        if (currentDungeonId == 0)
                        {
                            await SendTownFatigueStateAsync();
                        }
                    }

                    var queuedWeaknessRecovery = Interlocked.Exchange(
                        ref pendingWeaknessRecovery,
                        -1);
                    if (queuedWeaknessRecovery >= 0 && activeCharacter is not null)
                    {
                        activeCharacter = activeCharacter with
                        {
                            WeaknessRecovery = (byte)queuedWeaknessRecovery,
                            LegacyIsWeakened = null
                        };
                    }

                    if (isChannelPort)
                    {
                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.LoginCommand)
                        {
                            if (!TryReadLoginCredentials(
                                    request.Body,
                                    out var loginName,
                                    out var loginPassword))
                            {
                                var malformedLoginError = GameProtocolEngine.CreateCommandError(
                                    request,
                                    errorCode: 2);
                                await malformedLoginError.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, malformedLoginError);
                                logger.LogWarning(
                                    "Rejected malformed login command for channel session {SessionId}.",
                                    runtimeSession.Id);
                                continue;
                            }

                            var authenticated = loginPassword is not null
                                ? await store.AuthenticateAsync(
                                    loginName,
                                    loginPassword,
                                    serverToken)
                                : await store.FindAccountAsync(loginName, serverToken);
                            if (authenticated is null)
                            {
                                var loginError = GameProtocolEngine.CreateCommandError(
                                    request,
                                    errorCode: 2);
                                await loginError.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, loginError);
                                continue;
                            }

                            accountName = authenticated.UserName;
                            accountUid = authenticated.AccountUid;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.SetUdpEndpointCommand)
                        {
                var parsed = GameDatagramProtocol.TryParseNatReport(
                    request.Body,
                    out var natReport);
                if (parsed)
                {
                    gameplayDatagram.SetNatReport(
                        runtimeSession.Id,
                        natReport);
                    // Party members need the fresh endpoint right away or
                    // their client keeps the "连接中" connecting status.
                    partyCoordinator.RefreshPartyEndpoints(runtimeSession.Id);
                }

                            var endpointReply = parsed
                                ? GameProtocolEngine.CreateCommandReply(request, success: true)
                                : GameProtocolEngine.CreateCommandError(request, errorCode: 2);
                            await endpointReply.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, endpointReply);
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.GetUserInfoCommand)
                        {
                            var userInfo = await CreateUserInfoAsync(accountUid, serverToken);
                            await userInfo.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, userInfo);
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId
                                == GameProtocolEngine.ReturnSelectCharacterCommand)
                        {
                            if (activeCharacter is { TutorialStarted: false })
                            {
                                var tutorialError = GameProtocolEngine.CreateCommandError(
                                    request,
                                    errorCode: 4);
                                await tutorialError.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, tutorialError);
                                logger.LogInformation(
                                    "Rejected return to character selection for {CharacterId} until the tutorial is complete.",
                                    activeCharacter.Id);
                                continue;
                            }

                            var returningCharacterId = activeCharacter?.Id;
                            if (activeCharacter is not null)
                            {
                                if (currentDungeonId != 0 && dungeonMonsterExperienceGained > 0)
                                {
                                    activeCharacter = await store.SaveCharacterProgressionAsync(
                                        accountName,
                                        activeCharacter.Id,
                                        activeCharacter.Level,
                                        activeCharacter.Experience,
                                        currentSp,
                                        serverToken) ?? activeCharacter;
                                }

                                // Command 7 is the town menu's "Select
                                // Character" action. Persist the last stable
                                // town position before detaching the active
                                // character from this channel session.
                                await SaveCurrentLocationAsync(serverToken);
                            }

                            // Leaving the game world detaches town presence
                            // and party membership before the lease goes.
                            partyCoordinator.OnSessionClosed(runtimeSession.Id);
                            characterSessions.Release(activeCharacterLease);
                            activeCharacterLease = null;
                            activeCharacter = null;
                            lastProjectedCombatStats = null;
                            premiumExpiryUnix.Clear();
                            mainInventory.Clear();
                            avatarInventory.Clear();
                            equippedInventory.Clear();
                            warehouseInventory.Clear();
                            warehouseCapacity = CharacterWarehouseProgression.InitialCapacity;
                            currentSkills.Clear();
                            currentQuests.Clear();
                            completedQuestIds.Clear();
                            currentSp = 0;
                            currentGold = 0;
                            currentCash = 0;
                            currentVictoryPoints = 0;
                            currentDungeonId = 0;
                            currentDungeonLayout = null;
                            currentDungeonRoom = null;
                            inDungeonSelection = false;
                            currentDungeonRoomX = 0;
                            currentDungeonRoomY = 0;
                            currentDungeonDifficulty = 0;
                            aliveDungeonMonsters.Clear();
                            reportedBossDeaths.Clear();
                            bossCascadeMonsterDeaths.Clear();
                            destroyedPassiveObjects.Clear();
                            groundDungeonItems.ResetDungeon();
                            dungeonRun.Stop();
                            tutorialDungeonActive = false;
                            dungeonCharacterAlive = true;
                            dungeonDeathGeneration++;
                            dungeonDeathTimeout = null;
                            dungeonClearEnabled = false;
                            dungeonResultSent = false;
                            dungeonCardLayoutSent = false;
                            dungeonCardRightsSent = false;
                            dungeonCardSelectionDeadline = null;
                            dungeonClearReward = null;
                            dungeonFreeCardIndex = null;
                            dungeonGoldCardIndex = null;
                            dungeonMonsterExperienceGained = 0;
                            dungeonClearExperience = null;
                            dungeonConsumedFatigue = 0;
                            dungeonCreatureStomachCheckpointSeconds = null;
                            dungeonLeveledUp = false;
                            dungeonNormalMonsterKills = 0;
                            dungeonChampionMonsterKills = 0;
                            dungeonBossMonsterKills = 0;
                            dungeonChampionRewardOrdinal = 0;
                            currentMovementValue = 100;
                            areaRosterInitialized = false;
                            staminaStateInitialized = false;

                            var returnReply = GameProtocolEngine.CreateCommandReply(
                                request,
                                success: true);
                            await returnReply.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, returnReply);

                            // The 60CN client refreshes the roster itself with a
                            // GET_USERINFO (CMD 8) right after this acknowledgement;
                            // pushing the roster here too arrives as a second list
                            // and makes the select-screen event notice pop up twice.
                            logger.LogInformation(
                                "Returned character {CharacterId} to character selection for account {Account}.",
                                returningCharacterId,
                                accountName);
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.SelectCharacterCommand)
                        {
                            var characters = await store.GetCharactersAsync(accountUid, serverToken);
                            var slot = request.Body.Length >= 3 ? request.Body[2] : 0;
                            var character = characters.FirstOrDefault(candidate =>
                                candidate.Slot == slot);
                            if (character is null)
                            {
                                var selectError = GameProtocolEngine.CreateCommandError(request, errorCode: 2);
                                await selectError.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, selectError);
                                continue;
                            }

                            if (activeCharacterLease is not null
                                && activeCharacterLease.CharacterNo != character.CharacterNo)
                            {
                                await SaveCurrentLocationAsync(serverToken);
                                characterSessions.Release(activeCharacterLease);
                                activeCharacterLease = null;
                                activeCharacter = null;
                                lastProjectedCombatStats = null;
                                premiumExpiryUnix.Clear();
                            }

                            activeCharacterLease = await characterSessions.ClaimAsync(
                                character.CharacterNo,
                                character.Id,
                                runtimeSession.Id,
                                KickConnection,
                                serverToken,
                                () => Interlocked.Exchange(ref pendingMailAlarm, 1),
                                (serviceType, remainSeconds) =>
                                {
                                    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                                    var expiry = remainSeconds > long.MaxValue - now
                                        ? long.MaxValue
                                        : now + (long)remainSeconds;
                                    if (remainSeconds == 0)
                                    {
                                        premiumExpiryUnix.TryRemove(serviceType, out _);
                                    }
                                    else
                                    {
                                        premiumExpiryUnix[serviceType] = expiry;
                                    }
                                    pendingPremiumUpdates.Enqueue((serviceType, remainSeconds));
                                },
                                value => Interlocked.Exchange(
                                    ref pendingCeraValue,
                                    value > int.MaxValue
                                        ? int.MaxValue
                                        : (int)value),
                                () => Interlocked.Exchange(
                                    ref pendingFatigueReset,
                                    1),
                                (messageType, targetAreaUserId, messageBytes) =>
                                    _ = SendTypedMessageNotificationAsync(
                                        messageType,
                                        targetAreaUserId,
                                        messageBytes),
                                (messageType, ownerFlag, targetAreaUserId, senderName, messageBytes) =>
                                    _ = SendMegaphoneNotificationAsync(
                                        messageType,
                                        ownerFlag,
                                        targetAreaUserId,
                                        senderName,
                                        messageBytes),
                                messageBytes =>
                                    _ = SendPopupNotificationAsync(messageBytes),
                                recovery =>
                                {
                                    Interlocked.Exchange(
                                        ref pendingWeaknessRecovery,
                                        recovery);
                                    _ = SendWeaknessRecoveryNotificationAsync(recovery);
                                },
                                (dungeonId, maximumDifficulty) =>
                                {
                                    if (activeCharacter is null)
                                    {
                                        return;
                                    }

                                    activeCharacter = activeCharacter with
                                    {
                                        UnlockedDungeonIds =
                                            (activeCharacter.UnlockedDungeonIds ?? [])
                                            .Append(dungeonId)
                                            .Where(id => id != 0)
                                            .Distinct()
                                            .OrderBy(id => id)
                                            .ToList(),
                                        DungeonProgress = DungeonDifficultyProgression.Normalize(
                                                (activeCharacter.DungeonProgress ?? [])
                                                .Where(entry => entry.DungeonId != dungeonId)
                                                .Append(new CharacterDungeonProgressRecord(
                                                    dungeonId,
                                                    maximumDifficulty)))
                                            .ToList()
                                    };
                                    _ = SendDungeonPermissionsAsync();
                                });

                            // Claim the wire identity for this session.
                            localUserId = (ushort)(character.CharacterNo & 0xFFFF);
                            if (localUserId == 0)
                            {
                                localUserId = 1;
                            }

                            // A displaced connection performs its final save before
                            // releasing the character lease. Reload after the claim
                            // so this session cannot start from the pre-kick snapshot.
                            character = (await store.GetCharactersAsync(accountUid, serverToken))
                                .FirstOrDefault(candidate =>
                                    candidate.CharacterNo == character.CharacterNo)
                                ?? character;
                            var persistedWarehouseCapacity = character.WarehouseCapacity;
                            warehouseCapacity = CharacterWarehouseProgression.Normalize(
                                persistedWarehouseCapacity);
                            character = character with
                            {
                                WarehouseCapacity = warehouseCapacity
                            };
                            activeCharacter = character;
                            lastProjectedCombatStats = null;
                            staminaStateInitialized = false;
                            currentGold = Math.Max(0, character.Gold);
                            currentCash = Math.Max(0, character.Cash);
                            currentVictoryPoints = character.VictoryPoints;
                            bool IsAvatarItem(CharacterItemRecord item) =>
                                itemCatalog.TryGetDefinition(item.ItemId, out var definition)
                                && definition.InventoryCategory == ItemInventoryCategory.Avatar;
                            bool IsCreatureItem(CharacterItemRecord item) =>
                                itemCatalog.TryGetDefinition(item.ItemId, out var definition)
                                && definition.InventoryCategory == ItemInventoryCategory.Creature;

                            var savedInventory = character.Inventory
                                ?? CreateStarterInventory(accountUid);
                            var savedWarehouse = character.Warehouse ?? [];
                            var residualWarehouseUpgrades =
                                CharacterWarehouseProgression.SettleUpgradeItems(
                                    savedInventory,
                                    savedWarehouse,
                                    warehouseCapacity);
                            savedInventory = residualWarehouseUpgrades.Inventory.ToList();
                            savedWarehouse = residualWarehouseUpgrades.Warehouse.ToList();
                            warehouseCapacity = residualWarehouseUpgrades.Capacity;
                            character = character with
                            {
                                Inventory = savedInventory,
                                Warehouse = savedWarehouse,
                                WarehouseCapacity = warehouseCapacity
                            };
                            activeCharacter = character;
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
                            equippedInventory = new Dictionary<ushort, CharacterItemRecord>();
                            foreach (var item in savedEquipment
                                         .Where(item => item.ItemId != 0)
                                         .OrderBy(item => item.Slot))
                            {
                                if (itemCatalog.TryGetDefinition(item.ItemId, out var definition)
                                    && definition.InventoryCategory
                                        == ItemInventoryCategory.Avatar)
                                {
                                    var normalizedAvatarItem = NormalizeAvatarInstance(
                                        CharacterItemSealing.UnsealWhenEquipped(
                                            item,
                                            definition));
                                    if (CharacterAvatarInventoryLayout.IsCompatibleWornSlot(
                                            definition,
                                            item.Slot)
                                        && equippedInventory.TryAdd(
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
                                    && equippedInventory.TryAdd(
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
                                    warehouseCapacity);
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
                                PlanWarehouseUpgradeSettlement(
                                    normalizedInventory.Inventory.Values,
                                    normalizedWarehouse.Warehouse.Values,
                                    loginOverflowCandidates.Select(item => item.ItemId));
                            var consumedOverflowWarehouseUpgrade =
                                loginOverflowCandidates.Any(item =>
                                    CharacterWarehouseProgression
                                        .TryGetItemTargetCapacity(item.ItemId, out _));
                            mainInventory = normalizedWarehouseUpgrades.Inventory
                                .ToDictionary(item => item.Slot);
                            avatarInventory = normalizedAvatarInventory.Inventory;
                            warehouseInventory = normalizedWarehouseUpgrades.Warehouse
                                .ToDictionary(item => item.Slot);
                            warehouseCapacity = normalizedWarehouseUpgrades.Capacity;
                            creatureInventory = normalizedCreatureInventory.Inventory;
                            var loginOverflowItems = loginOverflowCandidates
                                .Where(item => !CharacterWarehouseProgression
                                    .TryGetItemTargetCapacity(item.ItemId, out _))
                                .ToArray();
                            var loginOverflowMails = CreateOverflowMailRequests(
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
                                || persistedWarehouseCapacity != warehouseCapacity
                                || migratedEquipmentSlots)
                            {
                                var repairedCharacter =
                                    await store.SaveCharacterItemSpacesAsync(
                                    accountName,
                                    character.Id,
                                    currentGold,
                                    mainInventory.Values,
                                    equippedInventory.Values,
                                    warehouseInventory.Values,
                                    serverToken,
                                    loginOverflowMails,
                                    avatarInventory.Values,
                                    creatureInventory.Values,
                                    warehouseCapacity);
                                if (repairedCharacter is null)
                                {
                                    characterSessions.Release(activeCharacterLease);
                                    activeCharacterLease = null;
                                    activeCharacter = null;
                                    lastProjectedCombatStats = null;
                                    premiumExpiryUnix.Clear();
                                    var repairError = GameProtocolEngine.CreateCommandError(
                                        request,
                                        errorCode: 4);
                                    await repairError.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, repairError);
                                    logger.LogError(
                                        "Could not repair inventory layout for character {CharacterId}; selection was rejected to avoid losing an item.",
                                        character.Id);
                                    continue;
                                }

                                activeCharacter = repairedCharacter;
                                if (loginOverflowMails.Length > 0)
                                {
                                    characterSessions.NotifyMailReceived(character.CharacterNo);
                                }
                            }

                            if (TryPlanLegacyPremiumContractRedemption(
                                    mainInventory,
                                    out var redeemedContractInventory,
                                    out var redeemedContractGrants))
                            {
                                var redeemedCharacter =
                                    await store.SaveCharacterCeraShopPurchaseAsync(
                                        accountName,
                                        activeCharacter.Id,
                                        currentCash,
                                        currentGold,
                                        redeemedContractInventory.Values,
                                        equippedInventory.Values,
                                        serverToken,
                                        avatarInventory: avatarInventory.Values,
                                        creatureInventory: creatureInventory.Values,
                                        premiumServiceGrants: redeemedContractGrants,
                                        expectedCash: currentCash);
                                if (redeemedCharacter is not null)
                                {
                                    activeCharacter = redeemedCharacter;
                                    mainInventory = redeemedContractInventory;
                                    logger.LogInformation(
                                        "Redeemed {ContractCount} legacy Cera-shop contract stack(s) while selecting character {CharacterId}.",
                                        redeemedContractGrants.Count,
                                        activeCharacter.Id);
                                }
                                else
                                {
                                    logger.LogError(
                                        "Could not redeem legacy Cera-shop contract items for character {CharacterId}; the items were retained.",
                                        activeCharacter.Id);
                                }
                            }

                            var premiumStates = await store.GetPremiumServicesAsync(
                                accountName,
                                serverToken);
                            var effectiveSkillLevel =
                                premiumBenefitCatalog.GetEffectiveSkillLevel(
                                    activeCharacter.Level,
                                    premiumStates
                                        .Where(state => state.Active)
                                        .Select(state => state.ServiceType));

                            currentSp = Math.Max(0, activeCharacter.Sp);
                            if (activeCharacter.Skills is null)
                            {
                                currentSkills = skillCatalog
                                    .CreateInitialLayout(character.Job, GetInitialSkills(character))
                                    .ToList();
                                activeCharacter = await store.SaveCharacterSkillsAsync(
                                    accountName,
                                    character.Id,
                                    currentSp,
                                    currentSkills,
                                    serverToken) ?? activeCharacter;
                            }
                            else
                            {
                                currentSkills = activeCharacter.Skills
                                    .Where(skill => skill.Level > 0)
                                    .OrderBy(skill => skill.Slot)
                                    .ToList();
                                var normalizedSkills = skillCatalog.NormalizeLayout(
                                    activeCharacter.Job,
                                    activeCharacter.GrowType,
                                    activeCharacter.AwakeningType,
                                    currentSkills,
                                    out var skillLayoutChanged);
                                if (skillLayoutChanged)
                                {
                                    currentSkills = normalizedSkills.ToList();
                                    activeCharacter = await store.SaveCharacterSkillsAsync(
                                        accountName,
                                        character.Id,
                                        currentSp,
                                        currentSkills,
                                        serverToken) ?? activeCharacter;
                                    logger.LogInformation(
                                        "Migrated character {CharacterId} to the DFLegacy skill-slot layout.",
                                        character.Id);
                                }
                            }

                            var reconciledSkills = skillCatalog.ReconcileOverLevelSkills(
                                activeCharacter.Job,
                                activeCharacter.GrowType,
                                activeCharacter.AwakeningType,
                                effectiveSkillLevel,
                                currentSkills,
                                out var refundedContractSkillPoints,
                                out var contractSkillRefunds);
                            if (!currentSkills.SequenceEqual(reconciledSkills))
                            {
                                currentSkills = reconciledSkills.ToList();
                                currentSp = checked((int)Math.Min(
                                    int.MaxValue,
                                    (long)currentSp + refundedContractSkillPoints));
                                activeCharacter = await store.SaveCharacterSkillsAsync(
                                    accountName,
                                    character.Id,
                                    currentSp,
                                    currentSkills,
                                    serverToken) ?? activeCharacter;
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

                            currentQuests = (activeCharacter.Quests ?? [])
                                .Where(quest => quest.QuestId != ushort.MaxValue)
                                .DistinctBy(quest => quest.QuestId)
                                .Take(3)
                                .ToList();
                            completedQuestIds = (activeCharacter.CompletedQuestIds ?? [])
                                .ToHashSet();
                            if (activeCharacter.Quests is null
                                || activeCharacter.CompletedQuestIds is null
                                || !currentQuests.SequenceEqual(activeCharacter.Quests.Take(3))
                                || completedQuestIds.Count != activeCharacter.CompletedQuestIds.Distinct().Count())
                            {
                                activeCharacter = await store.SaveCharacterQuestsAsync(
                                    accountName,
                                    character.Id,
                                    currentQuests,
                                    completedQuestIds,
                                    serverToken) ?? activeCharacter;
                            }

                            var migrateLegacyDungeonProgress =
                                activeCharacter.DungeonProgress is null;
                            var migratedDungeonUnlocks = (activeCharacter.UnlockedDungeonIds ?? [])
                                .Concat(HiddenDungeonUnlockCatalog.ResolveUnlockedDungeonIds(
                                    currentQuests.Select(quest => quest.QuestId)
                                        .Concat(completedQuestIds),
                                    questCatalog,
                                    dungeonCatalog))
                                .Where(dungeonId => dungeonId != 0)
                                .Distinct()
                                .OrderBy(dungeonId => dungeonId)
                                .ToArray();
                            var storedDungeonUnlocks = (activeCharacter.UnlockedDungeonIds ?? [])
                                .Where(dungeonId => dungeonId != 0)
                                .Distinct()
                                .OrderBy(dungeonId => dungeonId)
                                .ToArray();
                            if (activeCharacter.UnlockedDungeonIds is null
                                || !storedDungeonUnlocks.SequenceEqual(migratedDungeonUnlocks))
                            {
                                activeCharacter = await store.SaveCharacterDungeonUnlocksAsync(
                                    accountName,
                                    character.Id,
                                    migratedDungeonUnlocks,
                                    serverToken) ?? activeCharacter;
                            }

                            var normalizedDungeonProgress = migrateLegacyDungeonProgress
                                ? HiddenDungeonUnlockCatalog.ResolveVisibleDungeonIds(
                                        dungeonCatalog.DungeonIds,
                                        migratedDungeonUnlocks)
                                    .Select(dungeonId =>
                                        new CharacterDungeonProgressRecord(dungeonId, 1))
                                    .ToArray()
                                : DungeonDifficultyProgression.Normalize(
                                    activeCharacter.DungeonProgress);
                            var storedDungeonProgress =
                                DungeonDifficultyProgression.Normalize(
                                    activeCharacter.DungeonProgress);
                            if (migrateLegacyDungeonProgress
                                || !storedDungeonProgress.SequenceEqual(
                                    normalizedDungeonProgress))
                            {
                                activeCharacter = await store.SaveCharacterDungeonProgressAsync(
                                    accountName,
                                    character.Id,
                                    normalizedDungeonProgress,
                                    serverToken) ?? activeCharacter;
                                if (migrateLegacyDungeonProgress)
                                {
                                    logger.LogInformation(
                                        "Migrated character {CharacterId} dungeon permissions to persistent maximum difficulties.",
                                        character.Id);
                                }
                            }

                            currentTownId = activeCharacter.TownId is >= 1 and <= 5
                                ? activeCharacter.TownId
                                : (byte)1;
                            var loginSpawn = GetTownGateSpawn(currentTownId);
                            currentAreaId = loginSpawn.AreaId;
                            currentX = loginSpawn.X;
                            currentY = loginSpawn.Y;
                            currentDirection = loginSpawn.Direction;
                            currentMovementValue = 100;
                            areaRosterInitialized = false;
                            premiumExpiryUnix.Clear();
                            foreach (var premiumState in premiumStates.Where(state =>
                                         state.Active
                                         && state.ExpiresAt is not null))
                            {
                                premiumExpiryUnix[premiumState.ServiceType] =
                                    premiumState.ExpiresAt!.Value;
                            }

                            var blackDiamondService = premiumStates.FirstOrDefault(state =>
                                state.ServiceType == GameProtocolEngine.BlackDiamondServiceType);
                            var blackDiamondActive = blackDiamondService?.Active == true;
                            var selectReply = GameProtocolEngine.CreateSelectCharacterReply(
                                request,
                                characterId: (uint)slot + 1,
                                serverId: localUserId,
                                usedFatigue: Math.Min(
                                    activeCharacter.UsedFatigue,
                                    blackDiamondActive
                                        ? GameProtocolEngine.BlackDiamondMaximumFatigue
                                        : GameProtocolEngine.DefaultMaximumFatigue),
                                maximumFatigue: blackDiamondActive
                                    ? GameProtocolEngine.BlackDiamondMaximumFatigue
                                    : GameProtocolEngine.DefaultMaximumFatigue,
                                premiumFatigue: blackDiamondActive
                                    ? GameProtocolEngine.BlackDiamondBonusFatigue
                                    : (ushort)0,
                                cash: (uint)currentCash,
                                townId: currentTownId,
                                completedTutorialFlags: activeCharacter.TutorialStarted
                                    ? GameProtocolEngine.DefaultCompletedTutorialFlags
                                    : 0u,
                                activeQuests: currentQuests
                                    .Select(quest => new GameQuestEntry(quest.QuestId, quest.Trigger))
                                    .ToArray(),
                                premiumServices: premiumStates
                                    .Where(state => state.Active)
                                    .Select(state => new GamePremiumServiceEntry(
                                        state.ServiceType,
                                        state.RemainingSeconds))
                                    .ToArray(),
                                completedQuestIds: completedQuestIds.Order().ToArray(),
                                questCompletionMapping: questCatalog.CompletionMapping);
                            await selectReply.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, selectReply);

                            var currentCharacter = CreateTownAppearancePacket(
                                activeCharacter,
                                localUserId,
                                equippedInventory.Values,
                                creatureInventory.Values,
                                blackDiamondActive);
                            await currentCharacter.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, currentCharacter);

                            var learnedSkills = GetCurrentGameSkills();
                            var projectedCombatStats =
                                characterStatCatalog.Get(activeCharacter);
                            var currentCharacterDetails =
                                GameProtocolEngine.CreateCurrentCharacterDetails(
                                    localUserId,
                                    experience: activeCharacter.Experience,
                                    learnedSkills: learnedSkills,
                                    combatStats: projectedCombatStats,
                                    equippedItems: CreateWornEquipmentSnapshot(
                                        equippedInventory.Values,
                                        creatureInventory),
                                    equippedCreatureLevel: GetEquippedCreatureLevel(
                                        creatureInventory),
                                    staminaRecoveryPercentage:
                                        GetProjectedStaminaRecovery());
                            await currentCharacterDetails.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, currentCharacterDetails);
                            lastProjectedCombatStats = projectedCombatStats;

                            foreach (var initialPacket in CreateTownInitializationPackets(
                                         currentSp,
                                         learnedSkills,
                                         currentGold,
                                         currentVictoryPoints,
                                         mainInventory.Values,
                                         equippedInventory.Values,
                                         avatarInventory.Values,
                                         warehouseInventory.Values,
                                         warehouseCapacity,
                                         creatureInventory.Values,
                                         GetAcceptableQuestIds(),
                                         GetDungeonPermissions()))
                            {
                                await initialPacket.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, initialPacket);
                            }

                            await SendPvpRecordAsync();

                            // DFLegacy does not issue MAILBOX_OPEN while entering
                            // town, so deliver the current character's mailbox
                            // snapshot as part of character initialization.
                            await SendMailboxStateAsync(notifyNewMail: true);
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.SendMessageCommand)
                        {
                            if (activeCharacter is null
                                || !TryReadSendMessage(request.Body, out var messageRequest))
                            {
                                logger.LogWarning(
                                    "Rejected malformed SEND_MESSAGE request for character {CharacterId}: body={Body}.",
                                    activeCharacter?.Id,
                                    Convert.ToHexString(request.Body));
                                continue;
                            }

                            // Speaker megaphones (PVF items 36/37) are used
                            // through CMD 17 rather than USE_STACKABLE: the
                            // request names the megaphone slot so the server
                            // consumes the stack and rebroadcasts the text as
                            // NOTI 130. Wire layout:
                            // docs/protocol/new-packet-formats-60cn.md §1.
                            if (messageRequest.MessageType
                                is (byte)GameMessageType.Type13SpeakerYellow
                                or (byte)GameMessageType.Type14SpeakerYellow
                                or (byte)GameMessageType.Type15SpeakerYellow)
                            {
                                // The slot content is authoritative: items 36
                                // (server megaphone) and 37 (channel megaphone)
                                // are both accepted whichever speaker type the
                                // client picked, so the type-to-item mapping
                                // cannot drift from the PVF.
                                if (messageRequest.SlotOrReserved > ushort.MaxValue
                                    || !mainInventory.TryGetValue(
                                        (ushort)messageRequest.SlotOrReserved,
                                        out var megaphoneItem)
                                    || megaphoneItem.ItemId
                                        is not (ServerMegaphoneItemId
                                            or ChannelMegaphoneItemId)
                                    || megaphoneItem.CountOrValue == 0)
                                {
                                    await SendPopupNotificationAsync(
                                        ClientEncoding.GetBytes("喇叭使用失败，请确认道具状态"));
                                    logger.LogWarning(
                                        "Rejected megaphone message type {MessageType} from character {CharacterId}; target={TargetOrItemSpace}, slot/reserved={SlotOrReserved}.",
                                        messageRequest.MessageType,
                                        activeCharacter.Id,
                                        messageRequest.TargetOrItemSpace,
                                        messageRequest.SlotOrReserved);
                                    continue;
                                }

                                var megaphoneSlot = (ushort)messageRequest.SlotOrReserved;
                                var remainingCount = megaphoneItem.CountOrValue - 1;
                                if (remainingCount == 0)
                                {
                                    mainInventory.Remove(megaphoneSlot);
                                }
                                else
                                {
                                    mainInventory[megaphoneSlot] = megaphoneItem with
                                    {
                                        CountOrValue = remainingCount
                                    };
                                }

                                activeCharacter = await store.SaveCharacterInventoryAsync(
                                    accountName,
                                    activeCharacter.Id,
                                    currentGold,
                                    mainInventory.Values,
                                    equippedInventory.Values,
                                    serverToken) ?? activeCharacter;

                                var megaphoneEntry = remainingCount == 0
                                    ? new GameInventoryEntry(megaphoneSlot, ushort.MaxValue, 0)
                                    : ToGameInventoryEntry(mainInventory[megaphoneSlot]);
                                var megaphoneInventoryUpdate =
                                    GameProtocolEngine.CreateUpdateItemList(
                                        0,
                                        [megaphoneEntry]);
                                await megaphoneInventoryUpdate.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, megaphoneInventoryUpdate);

                                // Routing per client case 130 (0x427518): the
                                // packet flag is compared with the local
                                // channel number — equal renders the chat
                                // line ("频道N name : msg") and runs the
                                // bubble path (sub_849280 -> sub_846A10)
                                // that resolves the speaker by the packet
                                // uid in the town-user table, so same-area
                                // residents get the bubble above the
                                // speaker's head. Every session of this
                                // process shares the configured channel, so
                                // one speaker-flagged broadcast per session
                                // is a single, correctly labeled display
                                // with the bubble; the banner-only route is
                                // for other-channel clients, which never
                                // occur in this process.
                                var megaphoneRecipients =
                                    characterSessions.NotifyMegaphoneAll(
                                        (GameMessageType)messageRequest.MessageType,
                                        checked((byte)options.Channel.ChannelNumber),
                                        localUserId,
                                        ClientEncoding.GetBytes(activeCharacter.Name),
                                        messageRequest.MessageBytes);
                                logger.LogInformation(
                                    "Megaphone type {MessageType} from {CharacterName} (slot {Slot}, stack remaining {Remaining}) broadcast to {Recipients} sessions: {Message}",
                                    messageRequest.MessageType,
                                    activeCharacter.Name,
                                    megaphoneSlot,
                                    remainingCount,
                                    megaphoneRecipients,
                                    ClientEncoding.GetString(messageRequest.MessageBytes));
                                continue;
                            }

                            // Direct messages: 1:1 chat is CMD 17 type 18
                            // (DNF.exe sub_811C20), and the chat-window
                            // whispers are types 1/7 (shared sender
                            // sub_8495B0). All three carry the partner name
                            // as a trailing dstr; the type 1/7 u16 target is
                            // the partner's area-user id.
                            //
                            // Downlink must NOT echo the request type: the
                            // client routes NOTI 12 type 18 into its open
                            // 1:1 window via vmt+76 (sub_810A90), which
                            // feeds the packet-less name straight into
                            // strlen (sub_810560) and crashes the client —
                            // NOTI 12 carries no name field, so type 18 is
                            // an unusable downlink. The original server
                            // therefore delivers whispers as NOTI 12 type 1
                            // (light-green private line, speaker resolved
                            // from the town-user table by the uid). The
                            // sender needs no echo: both send paths insert
                            // the outgoing text locally (sub_811C20 /
                            // sub_8495B0 call sub_811A00).
                            if (messageRequest.MessageType
                                is (byte)GameMessageType.Type01LightGreen
                                    or (byte)GameMessageType.Type07LightGreen
                                    or (byte)GameMessageType.Type18DirectOutput)
                            {
                                var partnerName = ClientEncoding.GetString(
                                    messageRequest.TargetNameBytes);
                                var delivered = partyCoordinator.TrySendToName(
                                    partnerName,
                                    GameProtocolEngine.CreateMessageNotification(
                                        GameMessageType.Type01LightGreen,
                                        localUserId,
                                        messageRequest.MessageBytes));
                                if (delivered)
                                {
                                    logger.LogInformation(
                                        "Whisper type {MessageType} from {CharacterName} delivered to {PartnerName} as NOTI 12 type 1: {Message}",
                                        messageRequest.MessageType,
                                        activeCharacter.Name,
                                        partnerName,
                                        ClientEncoding.GetString(messageRequest.MessageBytes));
                                }
                                else
                                {
                                    await SendTypedMessageNotificationAsync(
                                        GameMessageType.Type00DualRouteBrown,
                                        0,
                                        ClientEncoding.GetBytes(
                                            $"{partnerName} 不在线，无法发送悄悄话"));
                                    logger.LogInformation(
                                        "Whisper type {MessageType} from {CharacterName} to offline partner {PartnerName}: {Message}",
                                        messageRequest.MessageType,
                                        activeCharacter.Name,
                                        partnerName,
                                        ClientEncoding.GetString(messageRequest.MessageBytes));
                                }

                                continue;
                            }

                            // DNF 1.0.1.9 uses message type 3 for map chat. The
                            // original server routes it to the sender's village
                            // and area, and success is represented by NOTI 12;
                            // there is no separate successful CMD 17 reply.
                            if (messageRequest.MessageType != (byte)GameMessageType.Type03White)
                            {
                                logger.LogWarning(
                                    "Unsupported SEND_MESSAGE type {MessageType} from character {CharacterId}; target/item-space={TargetOrItemSpace}, slot/reserved={SlotOrReserved}.",
                                    messageRequest.MessageType,
                                    activeCharacter.Id,
                                    messageRequest.TargetOrItemSpace,
                                    messageRequest.SlotOrReserved);
                                continue;
                            }

                            await SendTypedMessageNotificationAsync(
                                GameMessageType.Type03White,
                                localUserId,
                                messageRequest.MessageBytes);
                            // NOTI 12's u16 is the speaker's area-user id, not
                            // the recipient's: case 12 resolves it through the
                            // town-user table (sub_849280 -> sub_846A10
                            // @0x846A27) and renders the speech bubble above
                            // that character, so every same-area resident needs
                            // their own copy carrying the speaker's uid. The
                            // sender's copy above already covers the own
                            // bubble; same-town-different-area sessions stay
                            // out of range, matching the original routing.
                            var mapChatRecipients = partyCoordinator.SendToSameArea(
                                runtimeSession.Id,
                                GameProtocolEngine.CreateMessageNotification(
                                    GameMessageType.Type03White,
                                    localUserId,
                                    messageRequest.MessageBytes));
                            logger.LogInformation(
                                "Map chat from {CharacterName} in town {TownId}, area {AreaId} delivered to {Recipients} nearby sessions: {Message}",
                                activeCharacter.Name,
                                currentTownId,
                                currentAreaId,
                                mapChatRecipients,
                                ClientEncoding.GetString(messageRequest.MessageBytes));
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId
                                == GameProtocolEngine.CreatureSendMessageCommand)
                        {
                            if (activeCharacter is null
                                || !TryReadSendMessage(request.Body, out var messageRequest))
                            {
                                logger.LogWarning(
                                    "Rejected malformed CREATURE_SEND_MESSAGE request for character {CharacterId}: body={Body}.",
                                    activeCharacter?.Id,
                                    Convert.ToHexString(request.Body));
                                continue;
                            }

                            creatureInventory.TryGetValue(
                                CharacterCreatureInventoryLayout.EquippedCreatureSlot,
                                out var equippedCreature);
                            if (messageRequest.MessageType
                                    is not ((byte)GameMessageType.Type02LightCyan)
                                    and not ((byte)GameMessageType.Type03White)
                                || equippedCreature?.ItemId != BoboCreatureItemId
                                || equippedCreature.CountOrValue == 0)
                            {
                                logger.LogWarning(
                                    "Ignored CREATURE_SEND_MESSAGE type {MessageType} from character {CharacterId}; equippedCreature={CreatureId}.",
                                    messageRequest.MessageType,
                                    activeCharacter.Id,
                                    equippedCreature?.ItemId ?? 0);
                                continue;
                            }

                            // DNF.exe 0x70DA68 builds CMD 124 as type, a
                            // reserved u16, a client-supplied u32 area-user id,
                            // and a CP936 string. NOTI 126 omits that u32 and
                            // uses the same payload reader as NOTI 131 at
                            // 0x41CA99, so rebuild it with the authoritative id.
                            var creatureMessage = GameProtocolEngine
                                .CreateCreatureMessageNotification(
                                    (GameMessageType)messageRequest.MessageType,
                                    localUserId,
                                    messageRequest.MessageBytes);
                            await creatureMessage.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, creatureMessage);
                            logger.LogInformation(
                                "Creature automatic message from {CharacterName}'s Bobo uid={CreatureUid}: {Message}",
                                activeCharacter.Name,
                                equippedCreature.CountOrValue,
                                ClientEncoding.GetString(messageRequest.MessageBytes));
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId
                                == GameProtocolEngine.CreatureScriptMessageCommand)
                        {
                            if (activeCharacter is null
                                || !TryReadSendMessage(request.Body, out var messageRequest))
                            {
                                logger.LogWarning(
                                    "Rejected malformed CREATURE_SCRIPT_MESSAGE request for character {CharacterId}: body={Body}.",
                                    activeCharacter?.Id,
                                    Convert.ToHexString(request.Body));
                                continue;
                            }

                            // DNF.exe 0x722583 builds CMD 132 as the same chat
                            // body used by CMD 17. The original server accepts
                            // it only for a character with an equipped Creature;
                            // the client-provided sender id is not authoritative.
                            creatureInventory.TryGetValue(
                                CharacterCreatureInventoryLayout
                                    .EquippedCreatureSlot,
                                out var equippedCreature);
                            if (messageRequest.MessageType
                                    is not ((byte)GameMessageType.Type02LightCyan)
                                    and not ((byte)GameMessageType.Type03White)
                                || currentTownId == 0
                                || equippedCreature is null
                                || equippedCreature.ItemId == 0
                                || equippedCreature.CountOrValue == 0)
                            {
                                logger.LogWarning(
                                    "Ignored CREATURE_SCRIPT_MESSAGE type {MessageType} from character {CharacterId}; town={TownId}, equippedCreature={CreatureId}.",
                                    messageRequest.MessageType,
                                    activeCharacter.Id,
                                    currentTownId,
                                    equippedCreature?.ItemId ?? 0);
                                continue;
                            }

                            var creatureScriptMessage = GameProtocolEngine
                                .CreateCreatureScriptMessageNotification(
                                    (GameMessageType)messageRequest.MessageType,
                                    localUserId,
                                    messageRequest.MessageBytes);
                            await creatureScriptMessage.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, creatureScriptMessage);
                            logger.LogInformation(
                                "Creature script message from {CharacterName}'s Creature {CreatureId}: {Message}",
                                activeCharacter.Name,
                                equippedCreature.ItemId,
                                ClientEncoding.GetString(messageRequest.MessageBytes));
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.MailboxOpenCommand)
                        {
                            if (activeCharacter is null)
                            {
                                var error = GameProtocolEngine.CreateCommandError(
                                    request,
                                    errorCode: 22);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            var openReply = GameProtocolEngine.CreateMailboxOpenReply();
                            await openReply.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, openReply);
                            await SendMailboxStateAsync(notifyNewMail: true);
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId
                                == GameProtocolEngine.MailboxExtractItemCommand)
                        {
                            if (activeCharacter is null
                                || !TryReadMailboxExtract(request.Body, out var mailId))
                            {
                                var error = GameProtocolEngine.CreateCommandError(
                                    request,
                                    errorCode: 10);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            var mail = (await store.GetCharacterMailsAsync(
                                    activeCharacter.Id,
                                    serverToken))
                                .FirstOrDefault(candidate => candidate.Id == mailId);
                            if (mail is null
                                || mail.Gold == 0 && mail.Attachment is null
                                || mail.Gold > int.MaxValue - currentGold
                                || !TryPlanMailAttachment(
                                    mainInventory,
                                    avatarInventory,
                                    creatureInventory,
                                    mail.Attachment,
                                    itemCatalog,
                                    GetInventoryWeightLimit(activeCharacter),
                                    out var plannedInventory,
                                    out var plannedAvatarInventory,
                                    out var plannedCreatureInventory,
                                    out var mailDestinationListType,
                                    out var destinationSlot))
                            {
                                var error = GameProtocolEngine.CreateCommandError(
                                    request,
                                    errorCode: mail is null ? (byte)10 : (byte)4);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            var plannedGold = checked(currentGold + mail.Gold);
                            var mailUpgradeTarget = warehouseCapacity;
                            var isWarehouseUpgradeAttachment = mail.Attachment is { } attachment
                                && CharacterWarehouseProgression.TryGetItemTargetCapacity(
                                    attachment.ItemId,
                                    out mailUpgradeTarget);
                            var plannedWarehouseCapacity = isWarehouseUpgradeAttachment
                                ? Math.Max(warehouseCapacity, mailUpgradeTarget)
                                : warehouseCapacity;
                            var claim = await store.ClaimCharacterMailAsync(
                                accountName,
                                activeCharacter.Id,
                                mail.Id,
                                plannedGold,
                                plannedInventory.Values,
                                equippedInventory.Values,
                                serverToken,
                                plannedAvatarInventory.Values,
                                plannedCreatureInventory.Values,
                                warehouseCapacity: plannedWarehouseCapacity,
                                expectedWarehouseCapacity: warehouseCapacity);
                            if (claim is null)
                            {
                                var error = GameProtocolEngine.CreateCommandError(
                                    request,
                                    errorCode: 10);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            var warehouseCapacityChanged =
                                AdoptSavedWarehouseUpgradeState(claim.Character);
                            currentGold = plannedGold;
                            avatarInventory = (claim.Character.AvatarInventory ?? [])
                                .ToDictionary(item => item.Slot);
                            creatureInventory = (claim.Character.CreatureInventory ?? [])
                                .ToDictionary(item => item.Slot);
                            var extractReply = GameProtocolEngine.CreateMailboxExtractReply(mail.Id);
                            await extractReply.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, extractReply);

                            if (mail.Gold != 0 || mailDestinationListType == 0)
                            {
                                var inventoryUpdate = CreateMainInventoryPacket(
                                    currentGold,
                                    currentVictoryPoints,
                                    mainInventory.Values,
                                    destinationSlot);
                                await inventoryUpdate.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, inventoryUpdate);
                            }

                            if (mailDestinationListType == 1)
                            {
                                var avatarUpdate = CreateAvatarInventoryPacket(
                                    avatarInventory.Values);
                                await avatarUpdate.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, avatarUpdate);
                            }

                            if (mailDestinationListType == 7)
                            {
                                foreach (var creatureUpdate in
                                         CreateCreatureInventoryPackets(
                                             creatureInventory.Values))
                                {
                                    await creatureUpdate.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, creatureUpdate);
                                }
                            }

                            if (warehouseCapacityChanged
                                || isWarehouseUpgradeAttachment)
                            {
                                await SendWarehouseStateAsync();
                            }

                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.MailboxStateCommand)
                        {
                            if (activeCharacter is null
                                || !TryReadMailboxState(
                                    request.Body,
                                    out var mailId,
                                    out var mailState))
                            {
                                var error = GameProtocolEngine.CreateCommandError(
                                    request,
                                    errorCode: 10);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            var isKnownMail = knownMailboxIds.Contains(mailId);
                            if (mailState == 0)
                            {
                                var deleted = await store.DeleteCharacterMailAsync(
                                    activeCharacter.Id,
                                    mailId,
                                    serverToken);
                                if (!deleted && !isKnownMail)
                                {
                                    var error = GameProtocolEngine.CreateCommandError(
                                        request,
                                        errorCode: 10);
                                    await error.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, error);
                                    continue;
                                }

                                knownMailboxIds.Remove(mailId);
                            }
                            else if (!isKnownMail
                                || !await store.SetCharacterMailStateAsync(
                                    activeCharacter.Id,
                                    mailId,
                                    mailState,
                                    serverToken))
                            {
                                var error = GameProtocolEngine.CreateCommandError(
                                    request,
                                    errorCode: 10);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            var stateReply = GameProtocolEngine.CreateMailboxStateReply(
                                mailId,
                                mailState);
                            await stateReply.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, stateReply);
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.MailboxSendCommand)
                        {
                            if (activeCharacter is null
                                || !TryReadMailboxSend(request.Body, out var sendRequest))
                            {
                                var error = GameProtocolEngine.CreateCommandError(
                                    request,
                                    errorCode: 10);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            if (sendRequest.Gold > currentGold)
                            {
                                var error = GameProtocolEngine.CreateCommandError(
                                    request,
                                    errorCode: 17);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            var plannedInventory = mainInventory.ToDictionary(
                                pair => pair.Key,
                                pair => pair.Value);
                            CharacterMailAttachmentRecord? attachment = null;
                            ushort? sourceSlot = null;
                            if (sendRequest.ItemId != 0)
                            {
                                if (sendRequest.ListType != 0
                                    || sendRequest.CountOrValue == 0
                                    || !plannedInventory.TryGetValue(
                                        sendRequest.Slot,
                                        out var sourceItem)
                                    || sourceItem.ItemId != sendRequest.ItemId)
                                {
                                    var error = GameProtocolEngine.CreateCommandError(
                                        request,
                                        errorCode: 7);
                                    await error.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, error);
                                    continue;
                                }

                                if (!itemCatalog.TryGetDefinition(
                                        sourceItem.ItemId,
                                        out var sourceDefinition)
                                    || !CharacterItemSealing.CanTrade(
                                        sourceItem,
                                        sourceDefinition))
                                {
                                    var error = GameProtocolEngine.CreateCommandError(
                                        request,
                                        errorCode: 7);
                                    await error.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, error);
                                    continue;
                                }

                                var isEquipment = sourceDefinition.IsEquipment;
                                if (!isEquipment
                                    && sendRequest.CountOrValue > sourceItem.CountOrValue)
                                {
                                    var error = GameProtocolEngine.CreateCommandError(
                                        request,
                                        errorCode: 7);
                                    await error.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, error);
                                    continue;
                                }

                                var attachmentValue = isEquipment
                                    ? sourceItem.CountOrValue
                                    : sendRequest.CountOrValue;
                                attachment = new CharacterMailAttachmentRecord(
                                    sourceItem.ItemId,
                                    attachmentValue,
                                    sourceItem.State,
                                    sourceItem.Durability,
                                    sourceItem.SealState,
                                    EquipmentQualitySeed:
                                        sourceItem.EquipmentQualitySeed,
                                    InstanceId: sourceItem.InstanceId);
                                sourceSlot = sourceItem.Slot;
                                var remaining = isEquipment
                                    ? 0
                                    : sourceItem.CountOrValue - attachmentValue;
                                if (remaining == 0)
                                {
                                    plannedInventory.Remove(sourceItem.Slot);
                                }
                                else
                                {
                                    plannedInventory[sourceItem.Slot] = sourceItem with
                                    {
                                        CountOrValue = remaining
                                    };
                                }
                            }
                            else if (sendRequest.ListType != 0
                                || sendRequest.Slot != 0
                                || sendRequest.CountOrValue != 0)
                            {
                                var error = GameProtocolEngine.CreateCommandError(
                                    request,
                                    errorCode: 7);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            var plannedGold = currentGold - sendRequest.Gold;
                            var sendResult = await store.SendCharacterMailFromCharacterAsync(
                                accountName,
                                activeCharacter.Id,
                                sendRequest.Recipient,
                                new CreateCharacterMailRequest(
                                    activeCharacter.Name,
                                    sendRequest.Text,
                                    sendRequest.Gold,
                                    attachment),
                                plannedGold,
                                plannedInventory.Values,
                                equippedInventory.Values,
                                serverToken);
                            if (!sendResult.Sent || sendResult.Sender is null)
                            {
                                var errorCode = sendResult.Failure switch
                                {
                                    CharacterMailSendFailure.RecipientMailboxFull => (byte)90,
                                    CharacterMailSendFailure.SenderStateChanged => (byte)17,
                                    CharacterMailSendFailure.RecipientNotFound => (byte)10,
                                    _ => (byte)7
                                };
                                var error = GameProtocolEngine.CreateCommandError(
                                    request,
                                    errorCode);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            activeCharacter = sendResult.Sender;
                            currentGold = plannedGold;
                            mainInventory = plannedInventory;
                            var sendReply = GameProtocolEngine.CreateCommandReply(
                                request,
                                success: true);
                            await sendReply.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, sendReply);

                            var senderUpdates = new List<GameInventoryEntry>();
                            if (sendRequest.Gold != 0)
                            {
                                senderUpdates.Add(new GameInventoryEntry(
                                    Slot: 0,
                                    ItemId: 0,
                                    CountOrValue: checked((uint)currentGold)));
                            }

                            if (sourceSlot.HasValue)
                            {
                                senderUpdates.Add(mainInventory.TryGetValue(
                                        sourceSlot.Value,
                                        out var remainingItem)
                                    ? ToGameInventoryEntry(remainingItem)
                                    : new GameInventoryEntry(
                                        sourceSlot.Value,
                                        ushort.MaxValue,
                                        0));
                            }

                            if (senderUpdates.Count > 0)
                            {
                                var inventoryUpdate = GameProtocolEngine.CreateUpdateItemList(
                                    0,
                                    senderUpdates);
                                await inventoryUpdate.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, inventoryUpdate);
                            }

                            characterSessions.NotifyMailReceived(
                                sendResult.RecipientCharacterId);
                            if (sendResult.RecipientCharacterId == activeCharacter.Id
                                && Interlocked.Exchange(ref pendingMailAlarm, 0) != 0)
                            {
                                await SendNewMailAlarmAsync();
                            }

                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.AcceptQuestCommand)
                        {
                            if (activeCharacter is null || request.Body.Length < 4)
                            {
                                var error = GameProtocolEngine.CreateCommandError(request, 23);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            var questId = BinaryPrimitives.ReadUInt16LittleEndian(
                                request.Body.AsSpan(2, 2));
                            if (currentQuests.Count >= 3)
                            {
                                var error = GameProtocolEngine.CreateCommandError(request, 4);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            var acceptableQuestIds = GetAcceptableQuestIds();
                            if (!acceptableQuestIds.Contains(questId)
                                || !questCatalog.TryGetDefinition(questId, out var definition))
                            {
                                var error = GameProtocolEngine.CreateCommandError(request, 18);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            var acceptancePlan = QuestAcceptancePlanner.Create(
                                mainInventory,
                                definition,
                                itemCatalog,
                                GetInventoryWeightLimit(activeCharacter));
                            if (!acceptancePlan.Success)
                            {
                                var error = GameProtocolEngine.CreateCommandError(request, 17);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                logger.LogWarning(
                                    "Rejected quest {QuestId} because dependency item grants failed: {Failure} ({Error})",
                                    questId,
                                    acceptancePlan.Failure,
                                    acceptancePlan.Error);
                                continue;
                            }

                            var plannedCompletedQuestIds = completedQuestIds.ToHashSet();
                            if (definition.Repeatable)
                            {
                                plannedCompletedQuestIds.Remove(questId);
                            }

                            var acceptedQuest = new CharacterQuestRecord(
                                questId,
                                GetAuthoritativeQuestTrigger(
                                    definition,
                                    acceptancePlan.Inventory.Values,
                                    currentGold,
                                    currentVictoryPoints));
                            var plannedQuests = currentQuests
                                .Append(acceptedQuest)
                                .ToList();
                            var questDungeonUnlocks = HiddenDungeonUnlockCatalog.ResolveQuestDungeonUnlockIds(
                                questId, definition, dungeonCatalog);
                            var unlockedHiddenDungeon = questDungeonUnlocks.Any(dungeonId =>
                                !(activeCharacter.UnlockedDungeonIds ?? []).Contains(dungeonId));
                            var savedQuestProgress = await store.SaveCharacterQuestProgressAsync(
                                accountName,
                                activeCharacter.Id,
                                acceptancePlan.Inventory.Values,
                                plannedQuests,
                                plannedCompletedQuestIds,
                                serverToken,
                                unlockedDungeonIds: questDungeonUnlocks);
                            if (savedQuestProgress is null)
                            {
                                var error = GameProtocolEngine.CreateCommandError(request, 18);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                logger.LogWarning(
                                    "Rejected quest {QuestId} because the quest and dependency-item transaction could not be persisted.",
                                    questId);
                                continue;
                            }

                            activeCharacter = savedQuestProgress;
                            mainInventory = (savedQuestProgress.Inventory ?? [])
                                .ToDictionary(item => item.Slot);
                            currentQuests.Clear();
                            currentQuests.AddRange(savedQuestProgress.Quests ?? []);
                            completedQuestIds.Clear();
                            completedQuestIds.UnionWith(
                                savedQuestProgress.CompletedQuestIds ?? []);

                            var acceptQuestReply = GameProtocolEngine.CreateAcceptQuestReply(
                                questId,
                                acceptedQuest.Trigger,
                                acceptancePlan.InsertedItems
                                    .Select(item => new GameQuestInsertedItem(
                                        item.Slot,
                                        item.ItemId,
                                        item.CountOrSeed))
                                    .ToArray());
                            await acceptQuestReply.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, acceptQuestReply);
                            if (definition.DeleteNpcIndex >= 0)
                            {
                                logger.LogInformation(
                                    "Quest {QuestId} accepted with trigger {Trigger}; client delete-NPC index is {NpcIndex}.",
                                    questId, acceptedQuest.Trigger, definition.DeleteNpcIndex);
                            }
                            if (unlockedHiddenDungeon)
                            {
                                await SendDungeonPermissionsAsync();
                            }
                            await SendAcceptableQuestListAsync();
                            logger.LogInformation(
                                "Accepted quest {QuestId} and granted {Count} dependency-item slot updates: {Items}.",
                                questId,
                                acceptancePlan.InsertedItems.Count,
                                string.Join(
                                    ", ",
                                    acceptancePlan.InsertedItems.Select(item =>
                                        $"{item.ItemId}x{item.CountOrSeed}@{item.Slot}")));
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.GiveupQuestCommand)
                        {
                            if (activeCharacter is null || request.Body.Length < 4)
                            {
                                var error = GameProtocolEngine.CreateCommandError(request, 19);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            var questId = BinaryPrimitives.ReadUInt16LittleEndian(
                                request.Body.AsSpan(2, 2));
                            var questIndex = currentQuests.FindIndex(quest => quest.QuestId == questId);
                            if (questIndex < 0)
                            {
                                var error = GameProtocolEngine.CreateCommandError(request, 19);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            if (questCatalog.TryGetDefinition(questId, out var definition)
                                && !definition.CanGiveup)
                            {
                                var error = GameProtocolEngine.CreateCommandError(request, 20);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            currentQuests.RemoveAt(questIndex);
                            activeCharacter = await store.SaveCharacterQuestsAsync(
                                accountName,
                                activeCharacter.Id,
                                currentQuests,
                                completedQuestIds,
                                serverToken) ?? activeCharacter;

                            var giveupQuestReply = GameProtocolEngine.CreateGiveupQuestReply(questId);
                            await giveupQuestReply.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, giveupQuestReply);
                            await SendAcceptableQuestListAsync();
                            logger.LogInformation("Gave up quest {QuestId}.", questId);
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.SetQuestTriggerCommand)
                        {
                            // DF2008 sends sequence:u16, questId:u16 and a one-byte
                            // trigger action. 0x10/0x20/0x40 decrement one of the
                            // three packed nine-bit remaining counters; it does not send
                            // the resulting u32 trigger value back to the server.
                            if (activeCharacter is null || request.Body.Length < 5)
                            {
                                var error = GameProtocolEngine.CreateCommandError(request, 22);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            var questId = BinaryPrimitives.ReadUInt16LittleEndian(
                                request.Body.AsSpan(2, 2));
                            var questIndex = currentQuests.FindIndex(quest => quest.QuestId == questId);
                            if (questIndex < 0)
                            {
                                var error = GameProtocolEngine.CreateCommandError(request, 22);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            var oldTrigger = currentQuests[questIndex].Trigger;
                            var triggerAction = request.Body[4];
                            var trigger = oldTrigger;
                            var validTrigger = false;
                            if (questCatalog.TryGetDefinition(questId, out var definition))
                            {
                                if (QuestItemRequirementPlanner.IsSeeking(definition))
                                {
                                    trigger = QuestItemRequirementPlanner.GetInitialTrigger(
                                        definition,
                                        mainInventory.Values,
                                        currentGold,
                                        currentVictoryPoints);
                                    validTrigger = true;
                                }
                                else if (QuestPvpRankRequirement.IsPvpRank(definition))
                                {
                                    // Native PvP quests send 0 when satisfied and 1
                                    // when rank falls below the requirement. Neither
                                    // action may override the authoritative rank.
                                    validTrigger = triggerAction is 0 or 1;
                                    trigger = GetAuthoritativeQuestTrigger(
                                        definition, mainInventory.Values, currentGold, currentVictoryPoints);
                                }
                                else if (QuestMeetNpcRequirement.IsMeetNpc(definition))
                                {
                                    validTrigger = QuestMeetNpcRequirement.TryApplyClientCompletion(
                                        definition,
                                        oldTrigger,
                                        triggerAction,
                                        out trigger);
                                }
                                else if (QuestMapRequirement.IsClearMap(definition))
                                {
                                    validTrigger = QuestMapRequirement.TryApplyClientCompletion(
                                        definition, triggerAction, dungeonRun, out trigger);
                                }
                                else if (QuestDungeonClearRequirement.IsDungeonClearCondition(
                                             definition))
                                {
                                    // DF2008 evaluates all eight
                                    // [condition under clear] subtypes locally.
                                    // When the configured clear condition is met it
                                    // sends CMD 35 with action 0, meaning complete.
                                    validTrigger = currentDungeonId != 0
                                        && dungeonClearEnabled
                                        && QuestDungeonClearRequirement.Matches(
                                            definition,
                                            currentDungeonId,
                                            currentDungeonDifficulty)
                                        && QuestDungeonClearRequirement.TryApplyClientCompletion(
                                            definition,
                                            triggerAction,
                                            out trigger);
                                }
                                else
                                {
                                    validTrigger = definition.TryApplyTriggerAction(
                                        oldTrigger,
                                        triggerAction,
                                        out trigger);
                                }
                            }

                            if (!validTrigger)
                            {
                                var error = GameProtocolEngine.CreateCommandError(request, 22);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                logger.LogWarning(
                                    "Rejected quest {QuestId} trigger action 0x{Action:X2}.",
                                    questId,
                                    triggerAction);
                                continue;
                            }

                            currentQuests[questIndex] = currentQuests[questIndex] with
                            {
                                Trigger = trigger
                            };
                            var savedQuestProgress = await store.SaveCharacterQuestsAsync(
                                accountName,
                                activeCharacter.Id,
                                currentQuests,
                                completedQuestIds,
                                serverToken);
                            if (savedQuestProgress is not null)
                            {
                                activeCharacter = activeCharacter with
                                {
                                    Quests = savedQuestProgress.Quests,
                                    CompletedQuestIds =
                                        savedQuestProgress.CompletedQuestIds
                                };
                            }

                            var setQuestTriggerReply = GameProtocolEngine.CreateSetQuestTriggerReply(questId, trigger);
                            await setQuestTriggerReply.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, setQuestTriggerReply);
                            logger.LogInformation(
                                "Updated quest {QuestId} trigger from {OldTrigger} to {Trigger} through action 0x{Action:X2}.",
                                questId,
                                oldTrigger,
                                trigger,
                                triggerAction);
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.FinishQuestCommand)
                        {
                            if (activeCharacter is null || request.Body.Length < 4)
                            {
                                var error = GameProtocolEngine.CreateCommandError(request, 22);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            var questId = BinaryPrimitives.ReadUInt16LittleEndian(
                                request.Body.AsSpan(2, 2));
                            var questIndex = currentQuests.FindIndex(quest => quest.QuestId == questId);
                            var hasQuestDefinition = questCatalog.TryGetDefinition(
                                questId,
                                out var questDefinition);
                            if (questIndex < 0 || !hasQuestDefinition)
                            {
                                var error = GameProtocolEngine.CreateCommandError(request, 22);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            if (QuestItemRequirementPlanner.IsSeeking(questDefinition)
                                || QuestPvpRankRequirement.IsPvpRank(questDefinition))
                            {
                                var authoritativeTrigger =
                                    GetAuthoritativeQuestTrigger(
                                        questDefinition,
                                        mainInventory.Values,
                                        currentGold,
                                        currentVictoryPoints);
                                if (currentQuests[questIndex].Trigger != authoritativeTrigger)
                                {
                                    currentQuests[questIndex] = currentQuests[questIndex] with
                                    {
                                        Trigger = authoritativeTrigger
                                    };
                                    var savedQuestProgress =
                                        await store.SaveCharacterQuestsAsync(
                                            accountName,
                                            activeCharacter.Id,
                                            currentQuests,
                                            completedQuestIds,
                                            serverToken);
                                    if (savedQuestProgress is null)
                                    {
                                        var error = GameProtocolEngine.CreateCommandError(request, 22);
                                        await error.WriteAsync(stream, serverToken);
                                        Interlocked.Increment(ref runtimeSession.SentPackets);
                                        LogPacket("TX", runtimeSession, error);
                                        continue;
                                    }

                                    activeCharacter = activeCharacter with
                                    {
                                        Quests = savedQuestProgress.Quests,
                                        CompletedQuestIds =
                                            savedQuestProgress.CompletedQuestIds
                                    };
                                    var triggerUpdate =
                                        GameProtocolEngine.CreateSetQuestTriggerReply(
                                            questId,
                                            authoritativeTrigger);
                                    await triggerUpdate.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, triggerUpdate);
                                }
                            }

                            if (!questDefinition.IsTriggerComplete(
                                    currentQuests[questIndex].Trigger))
                            {
                                var error = GameProtocolEngine.CreateCommandError(request, 22);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                logger.LogWarning(
                                    "Rejected unfinished quest {QuestId}; remaining trigger={Trigger}.",
                                    questId,
                                    currentQuests[questIndex].Trigger);
                                continue;
                            }

                            if (!questDefinition.TryGetGrowthReward(
                                    out var questChainType,
                                    out var questGrowNumber))
                            {
                                var error = GameProtocolEngine.CreateCommandError(request, 22);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                logger.LogWarning(
                                    "Rejected quest {QuestId} with invalid growth reward {RewardType}:{GrowthReward}.",
                                    questId,
                                    questDefinition.RewardType,
                                    questDefinition.GrowthReward);
                                continue;
                            }

                            var previousGrowType = activeCharacter.GrowType & 0x0F;
                            var previousAwakeningType = Math.Max(
                                activeCharacter.AwakeningType,
                                Math.Max(0, activeCharacter.GrowType >> 4));
                            if ((questChainType == 1 && previousGrowType != 0)
                                || (questChainType == 2
                                    && (previousGrowType == 0
                                        || previousAwakeningType != 0)))
                            {
                                var error = GameProtocolEngine.CreateCommandError(request, 22);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                logger.LogWarning(
                                    "Rejected stale growth quest {QuestId}; chain={ChainType}, growType={GrowType}, awakening={AwakeningType}.",
                                    questId,
                                    questChainType,
                                    previousGrowType,
                                    previousAwakeningType);
                                continue;
                            }

                            if (!QuestItemRequirementPlanner.TryConsume(
                                    questDefinition,
                                    mainInventory,
                                    currentGold,
                                    currentVictoryPoints,
                                    out var consumptionPlan))
                            {
                                var error = GameProtocolEngine.CreateCommandError(request, 22);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                logger.LogWarning(
                                    "Rejected quest {QuestId} because its seeking requirements changed before completion.",
                                    questId);
                                continue;
                            }

                            CharacterCreatureEvolutionPlan? creatureEvolution = null;
                            if (questDefinition.RewardType == "creature evolution"
                                && !CharacterCreatureInventoryPlanner.TryEvolve(
                                    creatureInventory,
                                    questDefinition,
                                    itemCatalog,
                                    out creatureEvolution,
                                    out var evolutionFailure))
                            {
                                var error = GameProtocolEngine.CreateCommandError(request, 22);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                logger.LogWarning(
                                    "Could not finish Creature evolution quest {QuestId}: {Reason}",
                                    questId,
                                    evolutionFailure);
                                continue;
                            }

                            var selectionIndex = request.Body.Length >= 6
                                ? BinaryPrimitives.ReadUInt16LittleEndian(
                                    request.Body.AsSpan(4, sizeof(ushort)))
                                : ushort.MaxValue;
                            var rewardPlan = QuestRewardPlanner.Create(
                                activeCharacter with
                                {
                                    Gold = currentGold - consumptionPlan.GoldCost,
                                    VictoryPoints = currentVictoryPoints
                                        - consumptionPlan.VictoryPointCost,
                                    Inventory = consumptionPlan.Inventory.Values.ToList()
                                },
                                questDefinition,
                                selectionIndex == ushort.MaxValue ? -1 : selectionIndex,
                                itemCatalog,
                                GetInventoryWeightLimit(activeCharacter));
                            if (!rewardPlan.Success)
                            {
                                var error = GameProtocolEngine.CreateCommandError(request, 22);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                logger.LogWarning(
                                    "Could not finish quest {QuestId}: {Reason}",
                                    questId,
                                    rewardPlan.Error);
                                continue;
                            }

                            var questExperience = experienceCatalog.CalculateQuestExperience(
                                activeCharacter,
                                questDefinition);
                            var progression = experienceCatalog.Grant(
                                activeCharacter,
                                questExperience);
                            var grantedSkillPoints = spCatalog.CalculateLevelUpReward(
                                progression.PreviousLevel,
                                progression.Level);
                            var nextSkillPoints = checked(currentSp + grantedSkillPoints);
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
                                    activeCharacter.Job,
                                    nextGrowType);
                                nextSkills = skillCatalog.CreateResetLayout(
                                    activeCharacter.Job,
                                    nextGrowType,
                                    currentSkills,
                                    InitialSkillsByJob.TryGetValue(
                                        activeCharacter.Job,
                                        out var initialSkills)
                                            ? initialSkills
                                            : [],
                                    awakeningType: nextAwakeningType);
                                nextSkillPoints = checked(
                                    spCatalog.CalculateTotalReward(progression.Level)
                                    + Math.Max(0, activeCharacter.BonusSp));
                            }
                            else if (questChainType == 2)
                            {
                                nextAwakeningType = questGrowNumber;
                                growthSkillChanges = skillCatalog.GetAwakeningSkillChanges(
                                    activeCharacter.Job,
                                    nextGrowType,
                                    nextAwakeningType);
                                if (!skillCatalog.TryApplyGrowthSkillChanges(
                                        activeCharacter.Job,
                                        currentSkills,
                                        growthSkillChanges,
                                        out nextSkills))
                                {
                                    var error = GameProtocolEngine.CreateCommandError(request, 22);
                                    await error.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, error);
                                    logger.LogWarning(
                                        "Rejected awakening quest {QuestId}: no valid skill slot remains for its PVF grants.",
                                        questId);
                                    continue;
                                }
                            }

                            if (growthAdvanced
                                && (nextSkills is null
                                    || !SkillCatalog.AreGrowthSkillChangesApplied(
                                        nextSkills,
                                        growthSkillChanges)))
                            {
                                var error = GameProtocolEngine.CreateCommandError(request, 22);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                logger.LogWarning(
                                    "Rejected growth quest {QuestId}: its PVF skill changes could not be applied atomically.",
                                    questId);
                                continue;
                            }

                            var nextVictoryPoints = currentVictoryPoints
                                - consumptionPlan.VictoryPointCost;
                            var remainingQuests = currentQuests
                                .Where((_, index) => index != questIndex)
                                .ToList();
                            var nextQuests = RefreshSeekingQuestTriggers(
                                remainingQuests,
                                rewardPlan.Inventory,
                                rewardPlan.Gold,
                                nextVictoryPoints,
                                out var nextQuestTriggerChanges);
                            var nextCompletedQuestIds = completedQuestIds.ToHashSet();
                            nextCompletedQuestIds.Add(questId);

                            var nextCreatureInventory = creatureEvolution?.Inventory
                                ?? creatureInventory;
                            var questGroundRewards = currentDungeonId != 0
                                ? rewardPlan.MailedItems
                                    .Where(item => itemCatalog.TryGetDefinition(
                                            item.ItemId,
                                            out var definition)
                                        && CharacterItemSealing.CanTrade(item, definition))
                                    .ToArray()
                                : [];
                            var questMailRewards = currentDungeonId == 0
                                ? rewardPlan.MailedItems
                                : rewardPlan.MailedItems.Except(questGroundRewards).ToArray();
                            var completedCharacter = await store.CompleteCharacterQuestAsync(
                                accountName,
                                activeCharacter.Id,
                                questId,
                                progression.Level,
                                progression.Experience,
                                nextSkillPoints,
                                rewardPlan.Gold,
                                rewardPlan.Inventory,
                                nextQuests,
                                nextCompletedQuestIds,
                                serverToken,
                                CreateOverflowMailRequests(
                                    questMailRewards,
                                    "Quest reward"),
                                creatureInventory: nextCreatureInventory.Values,
                                skills: nextSkills,
                                growType: growthAdvanced ? nextGrowType : null,
                                awakeningType: growthAdvanced ? nextAwakeningType : null,
                                victoryPoints: nextVictoryPoints,
                                warehouseCapacity: rewardPlan.WarehouseCapacity,
                                expectedWarehouseCapacity: warehouseCapacity,
                                unlockedDungeonIds: HiddenDungeonUnlockCatalog.ResolveQuestDungeonUnlockIds(
                                    questId, questDefinition, dungeonCatalog));
                            if (completedCharacter is null)
                            {
                                var error = GameProtocolEngine.CreateCommandError(request, 22);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            activeCharacter = completedCharacter;
                            currentSp = Math.Max(0, completedCharacter.Sp);
                            currentGold = completedCharacter.Gold;
                            currentVictoryPoints = completedCharacter.VictoryPoints;
                            var warehouseCapacityChanged = completedCharacter.WarehouseCapacity
                                != warehouseCapacity;
                            warehouseCapacity = CharacterWarehouseProgression.Normalize(
                                completedCharacter.WarehouseCapacity);
                            mainInventory = (completedCharacter.Inventory ?? [])
                                .ToDictionary(item => item.Slot);
                            creatureInventory = nextCreatureInventory;
                            currentQuests = nextQuests;
                            completedQuestIds = nextCompletedQuestIds;
                            currentSkills = (completedCharacter.Skills ?? [])
                                .Where(skill => skill.Level > 0)
                                .OrderBy(skill => skill.Slot)
                                .ToList();
                            if (questMailRewards.Count > 0)
                            {
                                characterSessions.NotifyMailReceived(activeCharacter.CharacterNo);
                            }

                            foreach (var rewardItem in questGroundRewards)
                            {
                                var groundItem = groundDungeonItems.Add(
                                    new DungeonGeneratedDrop(
                                        false,
                                        rewardItem.ItemId,
                                        rewardItem.CountOrValue),
                                    localUserId,
                                    ushort.MaxValue,
                                    currentDungeonRoomX,
                                    currentDungeonRoomY,
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
                                    x: currentX,
                                    y: currentY,
                                    catalog: itemCatalog);
                                var dropNotification = GameProtocolEngine.CreateDropItem(
                                    localUserId,
                                    currentX,
                                    currentY,
                                    new GameDungeonDrop(
                                        groundItem.GroundId,
                                        groundItem.ItemId,
                                        groundItem.GetClientAddInfo(itemCatalog),
                                        groundItem.GetClientDurability(itemCatalog),
                                        groundItem.OwnerUserId,
                                        groundItem.GetClientItemAttr(itemCatalog)));
                                await dropNotification.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, dropNotification);
                            }

                            if (creatureEvolution is not null)
                            {
                                var evolutionNotification =
                                    GameProtocolEngine.CreateCreatureEvolution(
                                        checked((byte)questDefinition.EvolutionCreatureKind),
                                        localUserId);
                                await evolutionNotification.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, evolutionNotification);

                                foreach (var creaturePacket in CreateCreatureInventoryPackets(
                                             creatureInventory.Values))
                                {
                                    await creaturePacket.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, creaturePacket);
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

                            var finishQuestReply = GameProtocolEngine.CreateFinishQuestReply(
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
                            await finishQuestReply.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, finishQuestReply);
                            var inventoryRefresh = CreateMainInventoryPacket(
                                currentGold,
                                currentVictoryPoints,
                                mainInventory.Values);
                            await inventoryRefresh.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, inventoryRefresh);
                            if (warehouseCapacityChanged)
                            {
                                var warehouseRefresh = CreateWarehousePacket(
                                    warehouseInventory.Values,
                                    warehouseCapacity);
                                await warehouseRefresh.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, warehouseRefresh);
                            }
                            foreach (var change in nextQuestTriggerChanges)
                            {
                                var triggerUpdate =
                                    GameProtocolEngine.CreateSetQuestTriggerReply(
                                        change.QuestId,
                                        change.NewTrigger);
                                await triggerUpdate.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, triggerUpdate);
                            }
                            if (progression.LeveledUp || growthAdvanced)
                            {
                                await SendCurrentSkillInfoAsync();
                                await ValidateAndCorrectCharacterStatsAsync(
                                    progression.LeveledUp
                                        ? "quest level-up"
                                        : questChainType == 1
                                            ? "class advancement"
                                            : "awakening growth change");
                            }
                            else
                            {
                                await SendCurrentEquipmentStateAsync();
                            }

                            await SendDungeonPermissionsAsync();
                            await SendAcceptableQuestListAsync();
                            logger.LogInformation(
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
                                currentSp,
                                progression.PreviousLevel,
                                progression.Level,
                                previousGrowType,
                                completedCharacter.GrowType & 0x0F,
                                previousAwakeningType,
                                completedCharacter.AwakeningType);
                            if (creatureEvolution is not null)
                            {
                                logger.LogInformation(
                                    "Evolved Creature uid {CreatureUid} in slot {Slot}: item {SourceItemId}->{EvolvedItemId} for quest {QuestId}.",
                                    creatureEvolution.CreatureUid,
                                    creatureEvolution.Slot,
                                    creatureEvolution.SourceItemId,
                                    creatureEvolution.EvolvedItemId,
                                    questId);
                            }
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.CompoundItemCommand)
                        {
                            if (activeCharacter is null
                                || currentDungeonId != 0
                                || !GameProtocolEngine.TryParseCompoundItemCommand(
                                    request.Body,
                                    out var compoundItemCommand))
                            {
                                var error = GameProtocolEngine.CreateCompoundItemFailure(
                                    (byte)CompoundItemFailure.InvalidRecipe);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            if (compoundReplay.TryGetValue(
                                    compoundItemCommand.Sequence,
                                    out var replay))
                            {
                                if (!request.Body.AsSpan().SequenceEqual(replay.RequestBody))
                                {
                                    var conflict =
                                        GameProtocolEngine.CreateCompoundItemFailure(
                                            (byte)CompoundItemFailure.SequenceConflict);
                                    await conflict.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, conflict);
                                    logger.LogWarning(
                                        "Rejected compound sequence {Sequence} reused by a different request.",
                                        compoundItemCommand.Sequence);
                                    continue;
                                }

                                await replay.Reply.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, replay.Reply);
                                var replayRefresh = CreateMainInventoryPacket(
                                    currentGold,
                                    currentVictoryPoints,
                                    mainInventory.Values);
                                await replayRefresh.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, replayRefresh);
                                if (replay.RefreshWarehouse)
                                {
                                    await SendWarehouseStateAsync();
                                }

                                logger.LogInformation(
                                    "Replayed compound acknowledgement for duplicate sequence {Sequence}.",
                                    compoundItemCommand.Sequence);
                                continue;
                            }

                            var expectedGold = currentGold;
                            var expectedInventory = mainInventory.Values
                                .OrderBy(item => item.Slot)
                                .ToArray();
                            if (!CompoundItemPlanner.TryCreate(
                                    mainInventory,
                                    currentGold,
                                    currentSkills,
                                    compoundItemCommand.SourceValue,
                                    compoundItemCommand.SourceIsItemId,
                                    compoundItemCommand.CraftCount,
                                    GetInventoryWeightLimit(activeCharacter),
                                    compoundCatalog,
                                    itemCatalog,
                                    out var compoundItemPlan,
                                    out var compoundItemFailure))
                            {
                                var error = GameProtocolEngine.CreateCompoundItemFailure(
                                    (byte)compoundItemFailure);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                logger.LogWarning(
                                    "Rejected compound recipe source={SourceType}:{SourceValue}, count={CraftCount}: {Failure}.",
                                    compoundItemCommand.SourceIsItemId ? "item" : "slot",
                                    compoundItemCommand.SourceValue,
                                    compoundItemCommand.CraftCount,
                                    compoundItemFailure);
                                continue;
                            }

                            var savedCompoundItem = await store.CommitCompoundItemAsync(
                                accountName,
                                activeCharacter.Id,
                                expectedGold,
                                expectedInventory,
                                compoundItemPlan.RemainingGold,
                                compoundItemPlan.MainInventory.Values,
                                serverToken);
                            if (savedCompoundItem is null)
                            {
                                var error = GameProtocolEngine.CreateCompoundItemFailure(
                                    (byte)CompoundItemFailure.InvalidRecipe);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                logger.LogWarning(
                                    "Rejected stale compound commit for sequence {Sequence}, recipe {RecipeItemId}.",
                                    compoundItemCommand.Sequence,
                                    compoundItemPlan.RecipeItemId);
                                continue;
                            }

                            var warehouseCapacityChanged =
                                AdoptSavedWarehouseUpgradeState(savedCompoundItem);
                            currentGold = savedCompoundItem.Gold;
                            var hasWarehouseUpgradeResult =
                                compoundItemPlan.CreatedItems.Any(item =>
                                    CharacterWarehouseProgression
                                        .TryGetItemTargetCapacity(item.ItemId, out _));
                            var refreshWarehouse = warehouseCapacityChanged
                                || hasWarehouseUpgradeResult;
                            var compoundItemReply =
                                GameProtocolEngine.CreateCompoundItemReply(
                                    compoundItemPlan.ConsumedItems.Select(item =>
                                        new GameCompoundItemConsumption(
                                            item.ListType,
                                            item.Slot,
                                            item.RemainingCount)).ToArray(),
                                    compoundItemPlan.CreatedItems.Select(item =>
                                        new GameCompoundItemResult(
                                            item.Slot,
                                            item.ItemId,
                                            item.CountOrValue,
                                            item.State,
                                            item.Durability)).ToArray());
                            while (compoundReplay.Count >= CompoundReplayCapacity
                                && compoundReplayOrder.TryDequeue(out var oldestSequence))
                            {
                                compoundReplay.Remove(oldestSequence);
                            }

                            compoundReplay.Add(
                                compoundItemCommand.Sequence,
                                new CompoundItemReplayEntry(
                                    request.Body.ToArray(),
                                    compoundItemReply,
                                    refreshWarehouse));
                            compoundReplayOrder.Enqueue(compoundItemCommand.Sequence);
                            await compoundItemReply.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, compoundItemReply);
                            // DF2008 does not reliably apply the CMD27 delta when
                            // a consumed recipe slot is immediately reused by the
                            // crafted result. Always follow the acknowledgement
                            // with the authoritative main-inventory snapshot.
                            var inventoryRefresh = CreateMainInventoryPacket(
                                currentGold,
                                currentVictoryPoints,
                                mainInventory.Values);
                            await inventoryRefresh.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, inventoryRefresh);

                            if (refreshWarehouse)
                            {
                                await SendWarehouseStateAsync();
                            }

                            logger.LogInformation(
                                "Compounded recipe {RecipeItemId} from {SourceType}:{SourceValue}: count={CraftCount}, recipeType={RecipeType}, goldCost={GoldCost}, consumedSlots={ConsumedCount}, createdSlots={CreatedCount}, gold={Gold}.",
                                compoundItemPlan.RecipeItemId,
                                compoundItemCommand.SourceIsItemId ? "item" : "slot",
                                compoundItemCommand.SourceValue,
                                compoundItemPlan.CraftCount,
                                compoundItemPlan.RecipeType,
                                compoundItemPlan.GoldCost,
                                compoundItemPlan.ConsumedItems.Count,
                                compoundItemPlan.CreatedItems.Count,
                                currentGold);
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.DisjointItemCommand)
                        {
                            if (activeCharacter is null || request.Body.Length < 5)
                            {
                                var error = GameProtocolEngine.CreateCommandError(
                                    request,
                                    errorCode: 17);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            var sourceSlot = BinaryPrimitives.ReadUInt16LittleEndian(
                                request.Body.AsSpan(2, sizeof(ushort)));
                            var itemSpace = request.Body[4];
                            var additionalItemRoll = GameRandomSource.Shared.Next(int.MaxValue);
                            var jackpotRoll = GameRandomSource.Shared.Next(
                                DisjointCatalog.JackpotRollUpperBound);
                            if (!DisjointItemPlanner.TryCreate(
                                    mainInventory,
                                    sourceSlot,
                                    itemSpace,
                                    GetInventoryWeightLimit(activeCharacter),
                                    additionalItemRoll,
                                    jackpotRoll,
                                    itemCatalog,
                                    disjointCatalog,
                                    out var disjointPlan,
                                    out var disjointFailure))
                            {
                                var errorCode = disjointFailure
                                    == DisjointItemFailure.InventoryFull
                                        ? (byte)4
                                        : (byte)17;
                                var error = GameProtocolEngine.CreateCommandError(
                                    request,
                                    errorCode);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                logger.LogWarning(
                                    "Rejected equipment disjoint at slot {SourceSlot}, itemSpace={ItemSpace}: {Failure}.",
                                    sourceSlot,
                                    itemSpace,
                                    disjointFailure);
                                continue;
                            }

                            var disjointWarehouseSettlement =
                                PlanWarehouseUpgradeSettlement(
                                    disjointPlan.MainInventory.Values,
                                    warehouseInventory.Values,
                                    disjointPlan.ResponseRewards.Select(reward =>
                                        reward.ItemId));
                            var hasDisjointWarehouseUpgrade =
                                disjointPlan.ResponseRewards.Any(reward =>
                                    CharacterWarehouseProgression
                                        .TryGetItemTargetCapacity(reward.ItemId, out _));
                            var savedDisjoint = await store.SaveCharacterItemSpacesAsync(
                                accountName,
                                activeCharacter.Id,
                                currentGold,
                                disjointWarehouseSettlement.Inventory,
                                equippedInventory.Values,
                                disjointWarehouseSettlement.Warehouse,
                                serverToken,
                                avatarInventory: avatarInventory.Values,
                                creatureInventory: creatureInventory.Values,
                                warehouseCapacity:
                                    disjointWarehouseSettlement.Capacity,
                                expectedWarehouseCapacity: warehouseCapacity);
                            if (savedDisjoint is null)
                            {
                                var error = GameProtocolEngine.CreateCommandError(
                                    request,
                                    errorCode: 17);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            var warehouseCapacityChanged =
                                AdoptSavedWarehouseUpgradeState(savedDisjoint);
                            var disjointReply = GameProtocolEngine.CreateDisjointItemReply(
                                disjointPlan.SourceSlot,
                                disjointPlan.ItemSpace,
                                disjointPlan.ResponseRewards);
                            await disjointReply.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, disjointReply);
                            if (warehouseCapacityChanged || hasDisjointWarehouseUpgrade)
                            {
                                var inventoryRefresh = CreateMainInventoryPacket(
                                    currentGold,
                                    currentVictoryPoints,
                                    mainInventory.Values);
                                await inventoryRefresh.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, inventoryRefresh);
                                await SendWarehouseStateAsync();
                            }
                            logger.LogInformation(
                                "Disjointed item {SourceItemId} at slot {SourceSlot}; additionalRoll={AdditionalRoll}, jackpotRoll={JackpotRoll}, rewards={Rewards}.",
                                disjointPlan.SourceItemId,
                                sourceSlot,
                                additionalItemRoll,
                                jackpotRoll,
                                string.Join(
                                    ", ",
                                    disjointPlan.ResponseRewards.Select(reward =>
                                        $"{reward.ItemId}x{reward.Count}@{reward.DestinationSlot}")));
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.UseLotteryItemCommand)
                        {
                            if (activeCharacter is null || request.Body.Length < 4)
                            {
                                var error = GameProtocolEngine.CreateCommandError(
                                    request,
                                    errorCode: 17);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            var sourceSlot = BinaryPrimitives.ReadUInt16LittleEndian(
                                request.Body.AsSpan(2, sizeof(ushort)));
                            var roll = GameRandomSource.Shared.Next(
                                LotteryItemDefinition.RollUpperBound);
                            if (!LotteryItemPlanner.TryCreate(
                                    mainInventory,
                                    avatarInventory,
                                    creatureInventory,
                                    sourceSlot,
                                    activeCharacter.Level,
                                    currentGold,
                                    GetInventoryWeightLimit(activeCharacter),
                                    roll,
                                    itemCatalog,
                                    out var lotteryPlan,
                                    out var lotteryFailure))
                            {
                                var errorCode = lotteryFailure switch
                                {
                                    LotteryUseFailure.LevelTooLow => (byte)14,
                                    LotteryUseFailure.InsufficientGold => (byte)19,
                                    LotteryUseFailure.InventoryFull => (byte)4,
                                    LotteryUseFailure.GoldLimit => (byte)19,
                                    _ => (byte)17
                                };
                                var error = GameProtocolEngine.CreateCommandError(
                                    request,
                                    errorCode);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                logger.LogWarning(
                                    "Rejected lottery item use at slot {SourceSlot}: {Failure}.",
                                    sourceSlot,
                                    lotteryFailure);
                                continue;
                            }

                            var lotteryWarehouseSettlement =
                                PlanWarehouseUpgradeSettlement(
                                    lotteryPlan.MainInventory.Values,
                                    warehouseInventory.Values,
                                    lotteryPlan.Reward.ItemId == 0
                                        ? []
                                        : [lotteryPlan.Reward.ItemId]);
                            var isLotteryWarehouseUpgrade =
                                CharacterWarehouseProgression.TryGetItemTargetCapacity(
                                    lotteryPlan.Reward.ItemId,
                                    out _);
                            var savedLottery = await store.SaveCharacterItemSpacesAsync(
                                accountName,
                                activeCharacter.Id,
                                lotteryPlan.Gold,
                                lotteryWarehouseSettlement.Inventory,
                                equippedInventory.Values,
                                lotteryWarehouseSettlement.Warehouse,
                                serverToken,
                                avatarInventory: lotteryPlan.AvatarInventory.Values,
                                creatureInventory: lotteryPlan.CreatureInventory.Values,
                                warehouseCapacity:
                                    lotteryWarehouseSettlement.Capacity,
                                expectedWarehouseCapacity: warehouseCapacity);
                            if (savedLottery is null)
                            {
                                var error = GameProtocolEngine.CreateCommandError(
                                    request,
                                    errorCode: 17);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            var warehouseCapacityChanged =
                                AdoptSavedWarehouseUpgradeState(savedLottery);
                            currentGold = lotteryPlan.Gold;
                            avatarInventory = (savedLottery.AvatarInventory ?? [])
                                .ToDictionary(item => item.Slot);
                            creatureInventory = (savedLottery.CreatureInventory ?? [])
                                .ToDictionary(item => item.Slot);
                            var lotteryReply = GameProtocolEngine.CreateUseLotteryItemReply(
                                lotteryPlan.SourceSlot,
                                lotteryPlan.DestinationSlot,
                                lotteryPlan.Reward.ItemId,
                                lotteryPlan.ResponseCountOrValue,
                                lotteryPlan.InstanceValue,
                                lotteryPlan.ItemAttribute);
                            await lotteryReply.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, lotteryReply);

                            if (warehouseCapacityChanged || isLotteryWarehouseUpgrade)
                            {
                                var inventoryRefresh = CreateMainInventoryPacket(
                                    currentGold,
                                    currentVictoryPoints,
                                    mainInventory.Values);
                                await inventoryRefresh.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, inventoryRefresh);
                                await SendWarehouseStateAsync();
                            }

                            if (lotteryPlan.Reward.ItemId != 0
                                && itemCatalog.TryGetDefinition(
                                    lotteryPlan.Reward.ItemId,
                                    out var lotteryRewardDefinition)
                                && lotteryRewardDefinition.InventoryCategory
                                    == ItemInventoryCategory.Creature)
                            {
                                foreach (var creaturePacket in CreateCreatureInventoryPackets(
                                             creatureInventory.Values))
                                {
                                    await creaturePacket.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, creaturePacket);
                                }
                            }

                            logger.LogInformation(
                                "Used lottery item at slot {SourceSlot}; roll={Roll}, reward={RewardItemId} x{RewardCount}, destination={DestinationSlot}, gold={Gold}.",
                                sourceSlot,
                                roll,
                                lotteryPlan.Reward.ItemId,
                                lotteryPlan.Reward.CountOrValue,
                                lotteryPlan.DestinationSlot,
                                currentGold);
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.IncreaseStatusCommand)
                        {
                            if (activeCharacter is null || request.Body.Length < 4)
                            {
                                var error = GameProtocolEngine.CreateCommandError(
                                    request,
                                    errorCode: 17);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            var sourceSlot = BinaryPrimitives.ReadUInt16LittleEndian(
                                request.Body.AsSpan(2, sizeof(ushort)));
                            if (!mainInventory.TryGetValue(sourceSlot, out var sourceItem))
                            {
                                var error = GameProtocolEngine.CreateCommandError(
                                    request,
                                    errorCode: 17);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                logger.LogWarning(
                                    "Rejected increase-status command for slot {SourceSlot}: the slot is empty.",
                                    sourceSlot);
                                continue;
                            }

                            if (sourceItem.ItemId is RiverLetheItemId or RiverLetheBellItemId)
                            {
                                var resetSkills = skillCatalog.CreateResetLayout(
                                    activeCharacter.Job,
                                    activeCharacter.GrowType,
                                    currentSkills,
                                    InitialSkillsByJob.TryGetValue(
                                        activeCharacter.Job,
                                        out var initialSkills)
                                            ? initialSkills
                                            : [],
                                    awakeningType: activeCharacter.AwakeningType);
                                var accumulatedLevelSkillPoints =
                                    spCatalog.CalculateTotalReward(activeCharacter.Level);
                                var resetResult = await store.UseSkillResetItemAsync(
                                    accountName,
                                    activeCharacter.Id,
                                    sourceSlot,
                                    sourceItem.ItemId,
                                    accumulatedLevelSkillPoints,
                                    resetSkills,
                                    serverToken);
                                if (!resetResult.Success || resetResult.Character is null)
                                {
                                    var error = GameProtocolEngine.CreateCommandError(
                                        request,
                                        errorCode: 17);
                                    await error.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, error);
                                    logger.LogWarning(
                                        "Rejected skill-reset item {ItemId} at slot {SourceSlot}: persisted state changed ({Failure}).",
                                        sourceItem.ItemId,
                                        sourceSlot,
                                        resetResult.Failure);
                                    continue;
                                }

                                activeCharacter = resetResult.Character;
                                currentSp = resetResult.ResetSkillPoints;
                                currentSkills = (activeCharacter.Skills ?? [])
                                    .Where(skill => skill.Level > 0)
                                    .OrderBy(skill => skill.Slot)
                                    .ToList();
                                mainInventory = (activeCharacter.Inventory ?? [])
                                    .ToDictionary(item => item.Slot);

                                // The reference server refreshes the complete skill tree before
                                // acknowledging INCREASE_STATUS type 10.
                                await SendCurrentSkillInfoAsync();
                                var resetReply = GameProtocolEngine.CreateIncreaseStatusReply(
                                    sourceSlot,
                                    statusType: (byte)IncreaseStatusType.SkillReset,
                                    amount: 1);
                                await resetReply.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, resetReply);
                                logger.LogInformation(
                                    "Used skill-reset item {ItemId} at slot {SourceSlot}; SP reset to level rewards {LevelSkillPoints} + bonus {BonusSp} = {SkillPoints}, restored skills={SkillCount}, preserved guild skills={GuildSkillCount}, stack remaining={RemainingCount}.",
                                    sourceItem.ItemId,
                                    sourceSlot,
                                    accumulatedLevelSkillPoints,
                                    activeCharacter.BonusSp,
                                    currentSp,
                                    currentSkills.Count,
                                    currentSkills.Count(skill =>
                                        skillCatalog.TryGetDefinition(
                                            activeCharacter.Job,
                                            skill.SkillId,
                                            out var definition)
                                        && definition.IsGuildSkill),
                                    resetResult.RemainingItemCount);
                                continue;
                            }

                            if (!itemCatalog.TryGetDefinition(
                                    sourceItem.ItemId,
                                    out var sourceDefinition)
                                || sourceDefinition.IncreaseStatus is not { Amount: > 0 })
                            {
                                var error = GameProtocolEngine.CreateCommandError(
                                    request,
                                    errorCode: 17);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                logger.LogWarning(
                                    "Rejected increase-status command for slot {SourceSlot}: the slot does not contain a cached permanent-status item.",
                                    sourceSlot);
                                continue;
                            }

                            var increaseStatus = sourceDefinition.IncreaseStatus;
                            if (increaseStatus.Type == IncreaseStatusType.Experience)
                            {
                                var useExperienceResult =
                                    await store.UseExperienceBookItemAsync(
                                        accountName,
                                        activeCharacter.Id,
                                        sourceSlot,
                                        sourceItem.ItemId,
                                        checked((uint)increaseStatus.Amount),
                                        experienceCatalog,
                                        spCatalog,
                                        serverToken);
                                if (!useExperienceResult.Success
                                    || useExperienceResult.Character is null
                                    || useExperienceResult.Progression is null)
                                {
                                    var error = GameProtocolEngine.CreateCommandError(
                                        request,
                                        errorCode: 17);
                                    await error.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, error);
                                    logger.LogWarning(
                                        "Rejected experience book {ItemId} at slot {SourceSlot}: persisted item state changed or experience cannot be granted ({Failure}).",
                                        sourceItem.ItemId,
                                        sourceSlot,
                                        useExperienceResult.Failure);
                                    continue;
                                }

                                var progression = useExperienceResult.Progression;
                                activeCharacter = useExperienceResult.Character;
                                currentSp = Math.Max(0, activeCharacter.Sp);
                                mainInventory = (activeCharacter.Inventory ?? [])
                                    .ToDictionary(item => item.Slot);

                                var experienceReply =
                                    GameProtocolEngine.CreateIncreaseStatusReply(
                                        sourceSlot,
                                        statusType: (byte)IncreaseStatusType.Experience,
                                        amount: checked((int)progression.GrantedExperience),
                                        firstAuxiliaryValue: checked((ushort)Math.Min(
                                            useExperienceResult.GrantedSkillPoints,
                                            ushort.MaxValue)),
                                        secondAuxiliaryValue: 0);
                                await experienceReply.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, experienceReply);

                                if (progression.LeveledUp)
                                {
                                    await SendCurrentSkillInfoAsync();
                                    await ValidateAndCorrectCharacterStatsAsync(
                                        "experience-book level-up");
                                }
                                else
                                {
                                    await SendCurrentEquipmentStateAsync();
                                }

                                if (progression.LeveledUp)
                                {
                                    await SendAcceptableQuestListAsync();
                                }

                                logger.LogInformation(
                                    "Used experience book {ItemId} at slot {SourceSlot}; experience=+{Experience}, cumulative={CumulativeExperience}, level={PreviousLevel}->{Level}, SP=+{SkillPoints}, stack remaining={RemainingCount}.",
                                    sourceItem.ItemId,
                                    sourceSlot,
                                    progression.GrantedExperience,
                                    progression.Experience,
                                    progression.PreviousLevel,
                                    progression.Level,
                                    useExperienceResult.GrantedSkillPoints,
                                    useExperienceResult.RemainingItemCount);
                                continue;
                            }

                            var useResult = await store.UseIncreaseStatusItemAsync(
                                accountName,
                                activeCharacter.Id,
                                sourceSlot,
                                sourceItem.ItemId,
                                increaseStatus,
                                serverToken);
                            if (!useResult.Success || useResult.Character is null)
                            {
                                var error = GameProtocolEngine.CreateCommandError(
                                    request,
                                    errorCode: 17);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                logger.LogWarning(
                                    "Rejected permanent-status item {ItemId} at slot {SourceSlot}: persisted item state changed ({Failure}).",
                                    sourceItem.ItemId,
                                    sourceSlot,
                                    useResult.Failure);
                                continue;
                            }

                            activeCharacter = useResult.Character;
                            currentSp = Math.Max(0, activeCharacter.Sp);
                            mainInventory = (activeCharacter.Inventory ?? [])
                                .ToDictionary(item => item.Slot);
                            var responseAmount = ToIncreaseStatusWireAmount(
                                increaseStatus);
                            var increaseStatusReply = GameProtocolEngine.CreateIncreaseStatusReply(
                                sourceSlot,
                                statusType: (byte)increaseStatus.Type,
                                amount: responseAmount);
                            await increaseStatusReply.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, increaseStatusReply);
                            logger.LogInformation(
                                "Used permanent-status item {ItemId} at slot {SourceSlot}; type={StatusType}, persisted amount={Amount}, response amount={ResponseAmount}, remaining SP={RemainingSp}, cumulative bonuses=[SP:{BonusSp}, HP:{BonusHp}, MP:{BonusMp}, STR:{BonusStrength}, VIT:{BonusVitality}, INT:{BonusIntelligence}, SPI:{BonusSpirit}, MOVE:{BonusMovementSpeed}, ALLR:{BonusAllResistance}], stack remaining={RemainingCount}.",
                                sourceItem.ItemId,
                                sourceSlot,
                                increaseStatus.Type,
                                increaseStatus.Amount,
                                responseAmount,
                                currentSp,
                                activeCharacter.BonusSp,
                                activeCharacter.BonusMaximumHp,
                                activeCharacter.BonusMaximumMp,
                                activeCharacter.BonusStrength,
                                activeCharacter.BonusVitality,
                                activeCharacter.BonusIntelligence,
                                activeCharacter.BonusSpirit,
                                activeCharacter.BonusMovementSpeed,
                                activeCharacter.BonusAllElementResistance,
                                useResult.RemainingItemCount);
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.BuySkillCommand)
                        {
                            if (activeCharacter is null || request.Body.Length < 3)
                            {
                                var error = GameProtocolEngine.CreateCommandError(request, 4);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            // DNF 1.0.1.9 command 31 carries one u8 skill id
                            // after the two-byte command sequence.
                            var wireSkillIndex = request.Body[2];
                            if (!skillCatalog.TryGetDefinition(
                                    activeCharacter.Job,
                                    wireSkillIndex,
                                    out var definition))
                            {
                                var error = GameProtocolEngine.CreateCommandError(request, 4);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            var existingIndex = currentSkills.FindIndex(skill =>
                                skill.SkillId == definition.SkillId);
                            var currentLevel = existingIndex >= 0
                                ? currentSkills[existingIndex].Level
                                : 0;
                            var targetLevel = currentLevel + 1;
                            var maximumLevel = definition.MaximumLevelFor(
                                activeCharacter.GrowType,
                                activeCharacter.AwakeningType);
                            var hasPrerequisites = true;
                            for (var index = 0; index + 1 < definition.Prerequisites.Length; index += 2)
                            {
                                var prerequisiteId = definition.Prerequisites[index];
                                var prerequisiteLevel = definition.Prerequisites[index + 1];
                                if (prerequisiteId is < byte.MinValue or > byte.MaxValue
                                    || !currentSkills.Any(skill =>
                                        skill.SkillId == (byte)prerequisiteId
                                        && skill.Level >= prerequisiteLevel))
                                {
                                    hasPrerequisites = false;
                                    break;
                                }
                            }

                            var cost = definition.CostFor(currentLevel, targetLevel);
                            var requiredCharacterLevel =
                                definition.RequiredCharacterLevelFor(targetLevel);
                            var effectiveCharacterLevel =
                                premiumBenefitCatalog.GetEffectiveSkillLevel(
                                    activeCharacter.Level,
                                    GetActivePremiumServiceTypes());
                            if (maximumLevel <= 0
                                || targetLevel > maximumLevel
                                || requiredCharacterLevel > effectiveCharacterLevel
                                || !hasPrerequisites
                                || cost > currentSp)
                            {
                                var errorCode = cost > currentSp ? (byte)2 : (byte)18;
                                var error = GameProtocolEngine.CreateCommandError(request, errorCode);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                logger.LogInformation(
                                    "Rejected skill {SkillId} level {TargetLevel}: character level {CharacterLevel}, Premium-effective level {EffectiveLevel}, required level {RequiredLevel}, SP {CurrentSp}/{Cost}.",
                                    definition.SkillId,
                                    targetLevel,
                                    activeCharacter.Level,
                                    effectiveCharacterLevel,
                                    requiredCharacterLevel,
                                    currentSp,
                                    cost);
                                continue;
                            }

                            byte learnedSkillSlot;
                            if (existingIndex >= 0)
                            {
                                currentSkills[existingIndex] = currentSkills[existingIndex] with
                                {
                                    Level = checked((byte)targetLevel)
                                };
                                learnedSkillSlot = currentSkills[existingIndex].Slot;
                            }
                            else
                            {
                                var occupied = currentSkills
                                    .Select(skill => skill.Slot)
                                    .ToHashSet();
                                var slot = skillCatalog.AllocateSlot(definition, occupied);
                                if (!slot.HasValue)
                                {
                                    var error = GameProtocolEngine.CreateCommandError(request, 1);
                                    await error.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, error);
                                    continue;
                                }

                                currentSkills.Add(new CharacterSkillRecord(
                                    slot.Value,
                                    definition.SkillId,
                                    checked((byte)targetLevel)));
                                learnedSkillSlot = slot.Value;
                            }

                            currentSp -= cost;
                            currentSkills = currentSkills.OrderBy(skill => skill.Slot).ToList();
                            activeCharacter = await store.SaveCharacterSkillsAsync(
                                accountName,
                                activeCharacter.Id,
                                currentSp,
                                currentSkills,
                                serverToken) ?? activeCharacter;

                            var buySkillReply = GameProtocolEngine.CreateBuySkillReply(
                                checked((ushort)Math.Min(currentSp, ushort.MaxValue)),
                                learnedSkillSlot,
                                definition.SkillId,
                                checked((byte)targetLevel));
                            await buySkillReply.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, buySkillReply);
                            logger.LogInformation(
                                "Learned skill {SkillId} level {Level}; cost {Cost} SP, {RemainingSp} SP remains.",
                                definition.SkillId,
                                targetLevel,
                                cost,
                                currentSp);
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.ChangeSkillSlotCommand)
                        {
                            if (activeCharacter is null || request.Body.Length < 4)
                            {
                                var invalidQuickSlotReply =
                                    GameProtocolEngine.CreateCommandError(request, errorCode: 1);
                                await invalidQuickSlotReply.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, invalidQuickSlotReply);
                                continue;
                            }

                            var sourceQuickSlot = request.Body[2];
                            var destinationQuickSlot = request.Body[3];

                            // Dropping a skill onto an occupied quick slot sends
                            // spare->target and target->source as two adjacent
                            // commands. Wait for both before acknowledging them
                            // in wire order, so the target is not left visibly
                            // empty between the two client-side swaps.
                            await Task.Delay(TimeSpan.FromMilliseconds(5), serverToken);
                            PacketFrame? adjacentRawRequest = null;
                            if (client.Available > 0)
                            {
                                adjacentRawRequest = await PacketFrame.ReadAsync(
                                    stream,
                                    options.MaximumPacketLength,
                                    serverToken);
                            }

                            if (adjacentRawRequest is not null
                                && adjacentRawRequest.Type == GameProtocolEngine.CommandPacketType
                                && adjacentRawRequest.ProtocolId
                                    == GameProtocolEngine.ChangeSkillSlotCommand)
                            {
                                if (!gameClientCipher!.TryDecode(
                                        adjacentRawRequest,
                                        out var adjacentRequest))
                                {
                                    LogPacket("RX-ENC", runtimeSession, adjacentRawRequest);
                                    logger.LogWarning(
                                        "Could not decode the adjacent skill quick-slot command.");
                                    continue;
                                }

                                runtime.CountPacket(runtimeSession);
                                runtimeSession.LastProtocolId = adjacentRequest.ProtocolId;
                                LogPacket("RX", runtimeSession, adjacentRequest);

                                if (adjacentRequest.Body.Length >= 4)
                                {
                                    var adjacentSourceSlot = adjacentRequest.Body[2];
                                    var adjacentDestinationSlot = adjacentRequest.Body[3];
                                    var isCompoundMove = adjacentSourceSlot == destinationQuickSlot;
                                    var replies = new[]
                                    {
                                        (Request: request,
                                            Source: sourceQuickSlot,
                                            Destination: destinationQuickSlot),
                                        (Request: adjacentRequest,
                                            Source: adjacentSourceSlot,
                                            Destination: adjacentDestinationSlot)
                                    };

                                    IReadOnlyList<CharacterSkillRecord> candidateSkills = currentSkills;
                                    var rejected = false;
                                    var rejectionReason = string.Empty;
                                    foreach (var (_, replySourceSlot, replyDestinationSlot) in replies)
                                    {
                                        if (!skillCatalog.TrySwapSlots(
                                            activeCharacter.Job,
                                            candidateSkills,
                                            replySourceSlot,
                                            replyDestinationSlot,
                                            out var swappedSkills,
                                            out rejectionReason))
                                        {
                                            rejected = true;
                                            break;
                                        }

                                        candidateSkills = swappedSkills;
                                    }

                                    if (!rejected && !currentSkills.SequenceEqual(candidateSkills))
                                    {
                                        currentSkills = candidateSkills.ToList();
                                        activeCharacter = await store.SaveCharacterSkillsAsync(
                                            accountName,
                                            activeCharacter.Id,
                                            currentSp,
                                            currentSkills,
                                            serverToken) ?? activeCharacter;
                                    }

                                    foreach (var (replyRequest, replySourceSlot, replyDestinationSlot) in replies)
                                    {
                                        var quickSlotBatchReply = rejected
                                            ? GameProtocolEngine.CreateCommandError(
                                                replyRequest,
                                                errorCode: 1)
                                            : GameProtocolEngine.CreateChangeSkillSlotReply(
                                                replySourceSlot,
                                                replyDestinationSlot);
                                        await quickSlotBatchReply.WriteAsync(stream, serverToken);
                                        Interlocked.Increment(ref runtimeSession.SentPackets);
                                        LogPacket("TX", runtimeSession, quickSlotBatchReply);
                                    }

                                    if (rejected)
                                    {
                                        logger.LogWarning(
                                            "Rejected adjacent skill-slot changes {FirstSource}->{FirstDestination}, {SecondSource}->{SecondDestination}: {Reason}.",
                                            sourceQuickSlot,
                                            destinationQuickSlot,
                                            adjacentSourceSlot,
                                            adjacentDestinationSlot,
                                            rejectionReason);
                                    }
                                    else
                                    {
                                        logger.LogInformation(
                                            "Changed adjacent skill slots {FirstSource}->{FirstDestination}, {SecondSource}->{SecondDestination}; compound={Compound}.",
                                            sourceQuickSlot,
                                            destinationQuickSlot,
                                            adjacentSourceSlot,
                                            adjacentDestinationSlot,
                                            isCompoundMove);
                                    }

                                    continue;
                                }
                            }
                            else if (adjacentRawRequest is not null)
                            {
                                queuedRequest = adjacentRawRequest;
                            }

                            var skillSlotMoveAccepted = skillCatalog.TrySwapSlots(
                                activeCharacter.Job,
                                currentSkills,
                                sourceQuickSlot,
                                destinationQuickSlot,
                                out var movedSkills,
                                out var simpleRejectionReason);
                            if (skillSlotMoveAccepted && !currentSkills.SequenceEqual(movedSkills))
                            {
                                currentSkills = movedSkills.ToList();
                                activeCharacter = await store.SaveCharacterSkillsAsync(
                                    accountName,
                                    activeCharacter.Id,
                                    currentSp,
                                    currentSkills,
                                    serverToken) ?? activeCharacter;
                            }

                            var quickSlotReply = skillSlotMoveAccepted
                                ? GameProtocolEngine.CreateChangeSkillSlotReply(
                                    sourceQuickSlot,
                                    destinationQuickSlot)
                                : GameProtocolEngine.CreateCommandError(request, errorCode: 1);
                            await quickSlotReply.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, quickSlotReply);
                            if (skillSlotMoveAccepted)
                            {
                                logger.LogInformation(
                                    "Changed skill slot from {SourceSlot} to {DestinationSlot}.",
                                    sourceQuickSlot,
                                    destinationQuickSlot);
                            }
                            else
                            {
                                logger.LogWarning(
                                    "Rejected skill-slot change {SourceSlot}->{DestinationSlot}: {Reason}.",
                                    sourceQuickSlot,
                                    destinationQuickSlot,
                                    simpleRejectionReason);
                            }

                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.SortItemCommand)
                        {
                            var parsedSort = TryReadSortItem(
                                request.Body,
                                out var itemSpace);
                            if (activeCharacter is null
                                || !parsedSort
                                || itemSpace is not (0 or 2 or 7))
                            {
                                var errorReply = GameProtocolEngine.CreateSortItemError(
                                    itemSpace);
                                await errorReply.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, errorReply);
                                logger.LogWarning(
                                    "Rejected item sort for item space {ItemSpace}; active character: {HasCharacter}, parsed: {Parsed}.",
                                    itemSpace,
                                    activeCharacter is not null,
                                    parsedSort);
                                continue;
                            }

                            var arrangedMain = itemSpace == 0
                                ? CharacterInventorySorter.SortMain(mainInventory, itemCatalog)
                                : mainInventory;
                            var arrangedWarehouse = itemSpace == 2
                                ? CharacterInventorySorter.SortWarehouse(
                                    warehouseInventory,
                                    itemCatalog,
                                    warehouseCapacity)
                                : warehouseInventory;
                            var arrangedCreature = itemSpace == 7
                                ? CharacterCreatureInventorySorter.Sort(creatureInventory)
                                : creatureInventory;
                            var savedSort = await store.SaveCharacterItemSpacesAsync(
                                accountName,
                                activeCharacter.Id,
                                currentGold,
                                arrangedMain.Values,
                                equippedInventory.Values,
                                arrangedWarehouse.Values,
                                serverToken,
                                avatarInventory: avatarInventory.Values,
                                creatureInventory: arrangedCreature.Values);
                            if (savedSort is null)
                            {
                                var errorReply = GameProtocolEngine.CreateSortItemError(
                                    itemSpace);
                                await errorReply.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, errorReply);
                                logger.LogWarning(
                                    "Could not persist item sort for character {CharacterId} in item space {ItemSpace}.",
                                    activeCharacter.Id,
                                    itemSpace);
                                continue;
                            }

                            activeCharacter = savedSort;
                            mainInventory = arrangedMain;
                            warehouseInventory = arrangedWarehouse;
                            creatureInventory = arrangedCreature;

                            var sortReply = GameProtocolEngine.CreateSortItemReply(itemSpace);
                            await sortReply.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, sortReply);

                            // The 60CN client's incremental item path (listType
                            // != 3) ignores 0xFFFF tombstones and announces adds,
                            // so a relocation can only be conveyed by the full
                            // list rebuild (NOTI 13), which is silent.
                            var sortedPackets = itemSpace switch
                            {
                                0 => [CreateMainInventoryPacket(
                                    currentGold,
                                    currentVictoryPoints,
                                    mainInventory.Values)],
                                2 => [CreateWarehousePacket(
                                    warehouseInventory.Values,
                                    warehouseCapacity)],
                                7 => CreateCreatureInventoryPackets(
                                        creatureInventory.Values)
                                    .ToArray(),
                                _ => Array.Empty<GameServerPacket>()
                            };
                            foreach (var sortedItems in sortedPackets)
                            {
                                await sortedItems.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, sortedItems);
                            }

                            logger.LogInformation(
                                "Sorted item space {ItemSpace} for character {CharacterId}.",
                                itemSpace,
                                activeCharacter.Id);
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId ==
                                GameProtocolEngine.RenameCreatureCommand)
                        {
                            var parsedRename = TryReadRenameCreature(
                                request.Body,
                                out var renameCardSlot,
                                out var renameItemSpace,
                                out var renameNameBytes);
                            var requestedCreatureName = parsedRename
                                ? ClientEncoding.GetString(renameNameBytes)
                                : string.Empty;
                            var renameFailure = string.Empty;
                            CharacterCreatureRenamePlan renamePlan = null!;
                            var validRename = activeCharacter is not null
                                && parsedRename
                                && renameItemSpace == 7
                                && CharacterCreatureInventoryPlanner.TryRename(
                                    creatureInventory,
                                    renameCardSlot,
                                    requestedCreatureName,
                                    itemCatalog,
                                    out renamePlan,
                                    out renameFailure);
                            if (!validRename)
                            {
                                var renameError =
                                    GameProtocolEngine.CreateRenameCreatureReply(
                                        success: false,
                                        errorCode: 4);
                                await renameError.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, renameError);
                                logger.LogWarning(
                                    "Rejected Creature rename itemSpace={ItemSpace}, cardSlot={CardSlot}, parsed={Parsed}: {Failure}.",
                                    renameItemSpace,
                                    renameCardSlot,
                                    parsedRename,
                                    activeCharacter is null
                                        ? "no active character"
                                        : renameFailure);
                                continue;
                            }

                            var renamingCharacter = activeCharacter!;
                            var savedRename = await store.SaveCharacterItemSpacesAsync(
                                accountName,
                                renamingCharacter.Id,
                                currentGold,
                                mainInventory.Values,
                                equippedInventory.Values,
                                warehouseInventory.Values,
                                serverToken,
                                avatarInventory: avatarInventory.Values,
                                creatureInventory: renamePlan.Inventory.Values);
                            if (savedRename is null)
                            {
                                var renameError =
                                    GameProtocolEngine.CreateRenameCreatureReply(
                                        success: false,
                                        errorCode: 2);
                                await renameError.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, renameError);
                                logger.LogWarning(
                                    "Could not persist Creature rename using card slot {CardSlot} for character {CharacterId}.",
                                    renameCardSlot,
                                    renamingCharacter.Id);
                                continue;
                            }

                            activeCharacter = savedRename;
                            creatureInventory = renamePlan.Inventory;
                            var renameNotification =
                                GameProtocolEngine.CreateCreatureRename(
                                    localUserId,
                                    EncodeCreatureName(
                                        renamePlan.CreatureName,
                                        creatureInventory[
                                            CharacterCreatureInventoryLayout
                                                .EquippedCreatureSlot].ItemId));
                            await renameNotification.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, renameNotification);

                            var renameReply =
                                GameProtocolEngine.CreateRenameCreatureReply(
                                    success: true,
                                    renameCardSlot,
                                    renameItemSpace);
                            await renameReply.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, renameReply);
                            foreach (var creaturePacket in CreateCreatureInventoryPackets(
                                         creatureInventory.Values))
                            {
                                await creaturePacket.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, creaturePacket);
                            }

                            logger.LogInformation(
                                "Renamed equipped Creature uid={CreatureUid} to {CreatureName} using card slot {CardSlot} for character {CharacterId}.",
                                renamePlan.CreatureUid,
                                renamePlan.CreatureName,
                                renameCardSlot,
                                activeCharacter.Id);
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId ==
                                GameProtocolEngine.ResponseCreatureCommand)
                        {
                            await SendEquippedCreatureResponseAsync();
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.HatchCreatureCommand)
                        {
                            var parsedHatch = TryReadHatchCreature(
                                request.Body,
                                out var hatchItemSpace,
                                out var hatchSlot);
                            var hatchFailure = string.Empty;
                            CharacterCreatureHatchPlan hatchPlan = null!;
                            var validHatch = activeCharacter is not null
                                && parsedHatch
                                && hatchItemSpace == 7
                                && CharacterCreatureInventoryPlanner.TryHatch(
                                    creatureInventory,
                                    hatchSlot,
                                    itemCatalog,
                                    out hatchPlan,
                                    out hatchFailure);
                            if (!validHatch)
                            {
                                var hatchError = GameProtocolEngine.CreateHatchCreatureReply(
                                    success: false,
                                    errorCode: 4);
                                await hatchError.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, hatchError);
                                logger.LogWarning(
                                    "Rejected Creature hatch itemSpace={ItemSpace}, slot={Slot}, parsed={Parsed}: {Failure}.",
                                    hatchItemSpace,
                                    hatchSlot,
                                    parsedHatch,
                                    activeCharacter is null
                                        ? "no active character"
                                        : hatchFailure);
                                continue;
                            }

                            var hatchingCharacter = activeCharacter!;
                            var savedHatch = await store.SaveCharacterItemSpacesAsync(
                                accountName,
                                hatchingCharacter.Id,
                                currentGold,
                                mainInventory.Values,
                                equippedInventory.Values,
                                warehouseInventory.Values,
                                serverToken,
                                avatarInventory: avatarInventory.Values,
                                creatureInventory: hatchPlan.Inventory.Values);
                            if (savedHatch is null)
                            {
                                var hatchError = GameProtocolEngine.CreateHatchCreatureReply(
                                    success: false,
                                    errorCode: 2);
                                await hatchError.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, hatchError);
                                logger.LogWarning(
                                    "Could not persist Creature hatch at slot {Slot} for character {CharacterId}.",
                                    hatchSlot,
                                    hatchingCharacter.Id);
                                continue;
                            }

                            activeCharacter = savedHatch;
                            creatureInventory = hatchPlan.Inventory;
                            var hatchReply = GameProtocolEngine.CreateHatchCreatureReply(
                                success: true);
                            await hatchReply.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, hatchReply);
                            foreach (var creaturePacket in CreateCreatureInventoryPackets(
                                         creatureInventory.Values))
                            {
                                await creaturePacket.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, creaturePacket);
                            }

                            logger.LogInformation(
                                "Hatched Creature egg {EggItemId} into {CreatureItemId} at slot {Slot} for character {CharacterId}.",
                                hatchPlan.EggItemId,
                                hatchPlan.CreatureItemId,
                                hatchPlan.Slot,
                                activeCharacter.Id);
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.MoveItemSpaceCommand)
                        {
                            var parsedMove = TryReadInventoryMove(
                                request.Body,
                                out var sourceListType,
                                out var sourceSlot,
                                out var moveCount,
                                out var destinationListType,
                                out var destinationSlot);
                            if (activeCharacter is not null
                                && parsedMove
                                && (sourceListType == 7 || destinationListType == 7))
                            {
                                if (currentDungeonId != 0
                                    && !await SettleDungeonCreatureHungerAsync(
                                        notifyClient: false,
                                        serverToken))
                                {
                                    await SendInventoryMoveNoOpAsync(
                                        sourceListType,
                                        sourceSlot,
                                        destinationListType,
                                        destinationSlot,
                                        "creature hunger persistence failed");
                                    continue;
                                }

                                if (!TryPlanCreatureInventoryMove(
                                        warehouseInventory,
                                        warehouseCapacity,
                                        creatureInventory,
                                        sourceListType,
                                        sourceSlot,
                                        moveCount,
                                        destinationListType,
                                        destinationSlot,
                                        out var creatureMove,
                                        out var creatureMoveFailure))
                                {
                                    await SendInventoryMoveNoOpAsync(
                                        sourceListType,
                                        sourceSlot,
                                        destinationListType,
                                        destinationSlot,
                                        creatureMoveFailure);
                                    continue;
                                }

                                var savedCreatureMove =
                                    await store.SaveCharacterItemSpacesAsync(
                                        accountName,
                                        activeCharacter.Id,
                                        currentGold,
                                        mainInventory.Values,
                                        equippedInventory.Values,
                                        creatureMove.Warehouse.Values,
                                        serverToken,
                                        avatarInventory: avatarInventory.Values,
                                        creatureInventory: creatureMove.CreatureInventory.Values);
                                if (savedCreatureMove is null)
                                {
                                    await SendInventoryMoveNoOpAsync(
                                        sourceListType,
                                        sourceSlot,
                                        destinationListType,
                                        destinationSlot,
                                        "creature inventory persistence failed");
                                    continue;
                                }

                                activeCharacter = savedCreatureMove;
                                warehouseInventory = creatureMove.Warehouse;
                                creatureInventory = creatureMove.CreatureInventory;
                                var creatureMoveReply =
                                    GameProtocolEngine.CreateMoveItemSpaceReply(
                                        sourceListType,
                                        sourceSlot,
                                        creatureMove.MovedCount,
                                        destinationListType,
                                        creatureMove.ReplyDestinationSlot);
                                await creatureMoveReply.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, creatureMoveReply);

                                foreach (var creaturePacket in
                                         CreateCreatureInventoryPackets(
                                             creatureInventory.Values))
                                {
                                    await creaturePacket.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, creaturePacket);
                                }

                                if (creatureMove.EquippedSlotsChanged)
                                {
                                    if (creatureMove.EquippedCreatureChanged)
                                    {
                                        // A newly constructed equipped
                                        // Creature object needs its death
                                        // transition again, even when the same
                                        // persisted uid was equipped earlier.
                                        creatureDeathNotificationsSent.Clear();
                                    }

                                    if (creatureMove.EquippedCreatureChanged
                                        && currentDungeonId == 0)
                                    {
                                        // State must precede the mode-zero actor
                                        // refresh so a first equipped Creature is
                                        // constructed instead of merely replacing
                                        // an already existing map object.
                                        await SendTownAppearanceAsync();
                                    }
                                    else
                                    {
                                        await SendCurrentEquipmentStateAsync();
                                        if (creatureMove.EquippedCreatureChanged)
                                        {
                                            await SendEquippedCreatureHungerAsync();
                                        }
                                    }
                                }

                                if (sourceListType == 2 || destinationListType == 2)
                                {
                                    var warehousePacket = CreateWarehousePacket(
                                        warehouseInventory.Values,
                                        warehouseCapacity);
                                    await warehousePacket.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, warehousePacket);
                                }

                                logger.LogInformation(
                                    "Moved Creature-space item {ItemId} from {SourceList}:{SourceSlot} to {DestinationList}:{DestinationSlot}; count={MovedCount}.",
                                    creatureMove.ItemId,
                                    sourceListType,
                                    sourceSlot,
                                    destinationListType,
                                    destinationSlot,
                                    creatureMove.MovedCount);
                                continue;
                            }

                            if (activeCharacter is null
                                || !parsedMove
                                || !IsInventorySlotValid(
                                    sourceListType,
                                    sourceSlot,
                                    warehouseCapacity)
                                || (destinationSlot != ushort.MaxValue
                                    && !IsInventorySlotValid(
                                        destinationListType,
                                        destinationSlot,
                                        warehouseCapacity))
                                || !TryGetInventorySpace(
                                    sourceListType,
                                    mainInventory,
                                    avatarInventory,
                                    equippedInventory,
                                    warehouseInventory,
                                    out var sourceSpace)
                                || !TryGetInventorySpace(
                                    destinationListType,
                                    mainInventory,
                                    avatarInventory,
                                    equippedInventory,
                                    warehouseInventory,
                                    out var destinationSpace)
                                || !sourceSpace.TryGetValue(sourceSlot, out var sourceItem))
                            {
                                // DFLegacy encodes a move into an empty main-list
                                // slot in reverse: the empty target is sent as
                                // the source and the occupied origin as the
                                // destination. This is used by both quick-slot
                                // and bag empty-position moves.
                                if (activeCharacter is not null
                                    && parsedMove
                                    && sourceListType == 0
                                    && destinationListType == 0
                                    && sourceSlot >= 3
                                    && destinationSlot >= 3
                                    && !mainInventory.ContainsKey(sourceSlot)
                                    && mainInventory.TryGetValue(
                                        destinationSlot,
                                        out var itemToMoveIntoEmptySlot)
                                    && IsCompatibleMainInventorySlot(
                                        itemToMoveIntoEmptySlot,
                                        sourceSlot))
                                {
                                    mainInventory.Remove(destinationSlot);
                                    mainInventory[sourceSlot] = itemToMoveIntoEmptySlot with
                                    {
                                        Slot = sourceSlot
                                    };
                                    activeCharacter = await store.SaveCharacterInventoryAsync(
                                        accountName,
                                        activeCharacter.Id,
                                        currentGold,
                                        mainInventory.Values,
                                        equippedInventory.Values,
                                        serverToken) ?? activeCharacter;

                                    var reverseMoveReply =
                                        GameProtocolEngine.CreateMoveItemSpaceReply(
                                            sourceListType,
                                            sourceSlot,
                                            itemToMoveIntoEmptySlot.CountOrValue,
                                            destinationListType,
                                            destinationSlot);
                                    await reverseMoveReply.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, reverseMoveReply);

                                    if (ShouldUseIncrementalMainMoveRefresh(
                                            sourceListType,
                                            sourceSlot,
                                            destinationListType,
                                            destinationSlot))
                                    {
                                        // A complete list-zero rebuild makes the old
                                        // client select the category of the last bag
                                        // item. Quick slots have no category, so moving
                                        // an item there used to jump to the material tab.
                                        await SendMainInventoryMoveDeltaAsync(
                                            destinationSlot,
                                            sourceSlot);
                                    }
                                    else
                                    {
                                        var refreshMain = CreateMainInventoryPacket(
                                            currentGold,
                                            currentVictoryPoints,
                                            mainInventory.Values,
                                            preferredLastSlot: sourceSlot);
                                        await refreshMain.WriteAsync(stream, serverToken);
                                        Interlocked.Increment(ref runtimeSession.SentPackets);
                                        LogPacket("TX", runtimeSession, refreshMain);
                                    }

                                    logger.LogInformation(
                                        "Moved item {ItemId} from empty-target encoded 0:{DestinationSlot} to 0:{SourceSlot}",
                                        itemToMoveIntoEmptySlot.ItemId,
                                        destinationSlot,
                                        sourceSlot);
                                    continue;
                                }

                                // Avatar-bag empty targets are encoded in the
                                // same reverse form as main-bag empty targets.
                                if (activeCharacter is not null
                                    && parsedMove
                                    && sourceListType == 1
                                    && destinationListType == 1
                                    && !avatarInventory.ContainsKey(sourceSlot)
                                    && avatarInventory.TryGetValue(
                                        destinationSlot,
                                        out var avatarToMove)
                                    && IsCompatibleInventorySlot(
                                        1,
                                        avatarToMove,
                                        sourceSlot,
                                        warehouseCapacity))
                                {
                                    avatarInventory.Remove(destinationSlot);
                                    avatarInventory[sourceSlot] = avatarToMove with
                                    {
                                        Slot = sourceSlot
                                    };
                                    activeCharacter = await store.SaveCharacterItemSpacesAsync(
                                        accountName,
                                        activeCharacter.Id,
                                        currentGold,
                                        mainInventory.Values,
                                        equippedInventory.Values,
                                        warehouseInventory.Values,
                                        serverToken,
                                        avatarInventory: avatarInventory.Values)
                                        ?? activeCharacter;

                                    var reverseAvatarReply =
                                        GameProtocolEngine.CreateMoveItemSpaceReply(
                                            sourceListType,
                                            sourceSlot,
                                            avatarToMove.CountOrValue,
                                            destinationListType,
                                            destinationSlot);
                                    await reverseAvatarReply.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, reverseAvatarReply);

                                    var refreshAvatar = CreateAvatarInventoryPacket(
                                        avatarInventory.Values);
                                    await refreshAvatar.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, refreshAvatar);
                                    continue;
                                }

                                // Unequipping an Avatar also arrives as list 1
                                // empty source -> list 3 occupied destination.
                                if (activeCharacter is not null
                                    && parsedMove
                                    && sourceListType == 1
                                    && destinationListType == 3
                                    && !avatarInventory.ContainsKey(sourceSlot)
                                    && equippedInventory.TryGetValue(
                                        destinationSlot,
                                        out var avatarToUnequip)
                                    && IsCompatibleInventorySlot(
                                        1,
                                        avatarToUnequip,
                                        sourceSlot,
                                        warehouseCapacity)
                                    && itemCatalog.TryGetDefinition(
                                        avatarToUnequip.ItemId,
                                        out var avatarDefinition)
                                    && CharacterAvatarInventoryLayout.IsCompatibleWornSlot(
                                        avatarDefinition,
                                        destinationSlot))
                                {
                                    equippedInventory.Remove(destinationSlot);
                                    var unsealedAvatar =
                                        CharacterItemSealing.UnsealWhenEquipped(
                                            avatarToUnequip,
                                            avatarDefinition);
                                    avatarInventory[sourceSlot] = unsealedAvatar with
                                    {
                                        Slot = sourceSlot,
                                        State = 0
                                    };
                                    activeCharacter = await store.SaveCharacterItemSpacesAsync(
                                        accountName,
                                        activeCharacter.Id,
                                        currentGold,
                                        mainInventory.Values,
                                        equippedInventory.Values,
                                        warehouseInventory.Values,
                                        serverToken,
                                        avatarInventory: avatarInventory.Values)
                                        ?? activeCharacter;

                                    var avatarUnequipReply =
                                        GameProtocolEngine.CreateMoveItemSpaceReply(
                                            sourceListType,
                                            sourceSlot,
                                            avatarToUnequip.CountOrValue,
                                            destinationListType,
                                            destinationSlot);
                                    await avatarUnequipReply.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, avatarUnequipReply);

                                    var refreshAvatar = CreateAvatarInventoryPacket(
                                        avatarInventory.Values);
                                    await refreshAvatar.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, refreshAvatar);

                                    await Task.Delay(
                                        TimeSpan.FromMilliseconds(100),
                                        serverToken);
                                    if (currentDungeonId == 0)
                                    {
                                        await SendTownAppearanceAsync();
                                    }
                                    else
                                    {
                                        await SendCurrentEquipmentStateAsync();
                                    }

                                    logger.LogInformation(
                                        "Unequipped Avatar item {ItemId} from 3:{WornSlot} to 1:{BagSlot}.",
                                        avatarToUnequip.ItemId,
                                        destinationSlot,
                                        sourceSlot);
                                    continue;
                                }

                                // Dropping a cargo item onto an empty bag slot is
                                // encoded in reverse by DFLegacy, just like an
                                // unequip: the empty main slot is the source and
                                // the occupied cargo slot is the destination.
                                if (activeCharacter is not null
                                    && parsedMove
                                    && sourceListType == 0
                                    && destinationListType == 2
                                    && sourceSlot >= 3
                                    && destinationSlot < warehouseCapacity
                                    && !mainInventory.ContainsKey(sourceSlot)
                                    && warehouseInventory.TryGetValue(
                                        destinationSlot,
                                        out var itemToWithdraw)
                                    && itemCatalog.TryGetDefinition(
                                        itemToWithdraw.ItemId,
                                        out _))
                                {
                                    var withdrawnCount = moveCount <= 0
                                        ? itemToWithdraw.CountOrValue
                                        : Math.Min(itemToWithdraw.CountOrValue, (uint)moveCount);
                                    var withdrawal = new CharacterMailAttachmentRecord(
                                        itemToWithdraw.ItemId,
                                        withdrawnCount,
                                        itemToWithdraw.State,
                                        itemToWithdraw.Durability,
                                        itemToWithdraw.SealState,
                                        EquipmentQualitySeed:
                                            itemToWithdraw.EquipmentQualitySeed,
                                        InstanceId: itemToWithdraw.InstanceId);
                                    if (!CharacterInventoryPlanner.TryPlace(
                                            mainInventory,
                                            withdrawal,
                                            itemCatalog,
                                            GetInventoryWeightLimit(activeCharacter),
                                            sourceSlot,
                                            out var withdrawalPlan,
                                            out var withdrawalFailure))
                                    {
                                        await SendInventoryMoveNoOpAsync(
                                            sourceListType,
                                            sourceSlot,
                                            destinationListType,
                                            destinationSlot,
                                            $"warehouse withdrawal failed: {withdrawalFailure}");
                                        continue;
                                    }

                                    if (withdrawnCount < itemToWithdraw.CountOrValue)
                                    {
                                        warehouseInventory[destinationSlot] = itemToWithdraw with
                                        {
                                            CountOrValue = itemToWithdraw.CountOrValue - withdrawnCount
                                        };
                                    }
                                    else
                                    {
                                        warehouseInventory.Remove(destinationSlot);
                                    }

                                    var savedWithdrawal = await store.SaveCharacterItemSpacesAsync(
                                        accountName,
                                        activeCharacter.Id,
                                        currentGold,
                                        withdrawalPlan.Inventory.Values,
                                        equippedInventory.Values,
                                        warehouseInventory.Values,
                                        serverToken);
                                    if (savedWithdrawal is null)
                                    {
                                        warehouseInventory[destinationSlot] = itemToWithdraw;

                                        await SendInventoryMoveNoOpAsync(
                                            sourceListType,
                                            sourceSlot,
                                            destinationListType,
                                            destinationSlot,
                                            "warehouse withdrawal persistence failed");
                                        continue;
                                    }

                                    activeCharacter = savedWithdrawal;
                                    mainInventory = withdrawalPlan.Inventory;

                                    var withdrawReply =
                                        GameProtocolEngine.CreateMoveItemSpaceReply(
                                            sourceListType,
                                            sourceSlot,
                                            withdrawnCount,
                                            destinationListType,
                                            destinationSlot);
                                    await withdrawReply.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, withdrawReply);

                                    foreach (var refresh in new[]
                                             {
                                                  CreateMainInventoryPacket(
                                                      currentGold,
                                                      currentVictoryPoints,
                                                      mainInventory.Values,
                                                      withdrawalPlan.DestinationSlot),
                                                 CreateWarehousePacket(
                                                     warehouseInventory.Values,
                                                     warehouseCapacity)
                                             })
                                    {
                                        await refresh.WriteAsync(stream, serverToken);
                                        Interlocked.Increment(ref runtimeSession.SentPackets);
                                        LogPacket("TX", runtimeSession, refresh);
                                    }

                                    logger.LogInformation(
                                        "Withdrew item {ItemId} from reverse-encoded cargo slot {CargoSlot}; main destination starts at {MainSlot}.",
                                        itemToWithdraw.ItemId,
                                        destinationSlot,
                                        withdrawalPlan.DestinationSlot);
                                    continue;
                                }

                                // This client unequips by selecting an empty
                                // bag slot as the source and the occupied worn
                                // slot as the destination. Treat that request
                                // as a reverse swap into the empty bag slot.
                                if (activeCharacter is not null
                                    && parsedMove
                                    && sourceListType == 0
                                    && sourceSlot >= 3
                                    && destinationListType == 3
                                    && equippedInventory.TryGetValue(
                                        destinationSlot,
                                        out var itemToUnequip)
                                    && IsCompatibleMainInventorySlot(
                                        itemToUnequip,
                                        sourceSlot))
                                {
                                    equippedInventory.Remove(destinationSlot);
                                    var unsealedItem =
                                        CharacterItemSealing.UnsealWhenEquipped(
                                            itemToUnequip,
                                            itemCatalog);
                                    mainInventory[sourceSlot] = CharacterItemIdentity.MoveToSlot(
                                        unsealedItem,
                                        sourceSlot,
                                        itemCatalog);
                                    activeCharacter = await store.SaveCharacterInventoryAsync(
                                        accountName,
                                        activeCharacter.Id,
                                        currentGold,
                                        mainInventory.Values,
                                        equippedInventory.Values,
                                        serverToken) ?? activeCharacter;

                                    var unequipReply =
                                        GameProtocolEngine.CreateMoveItemSpaceReply(
                                            sourceListType,
                                            sourceSlot,
                                            itemToUnequip.CountOrValue,
                                            destinationListType,
                                            destinationSlot);
                                    await unequipReply.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, unequipReply);

                                    // Protocol 14 treats every added record as
                                    // newly acquired and displays a loot notice.
                                    // Protocol 13 refreshes the same bag state
                                    // silently and is already used after sales.
                                    var refreshBag = CreateMainInventoryPacket(
                                        currentGold,
                                        currentVictoryPoints,
                                        mainInventory.Values,
                                        preferredLastSlot: sourceSlot);
                                    await refreshBag.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, refreshBag);

                                    // Do not free the worn object in the same UI
                                    // tick. Once the drag controller releases
                                    // its pointer, the normal list-3 removal is
                                    // safe and updates the equipment panel.
                                    await Task.Delay(
                                        TimeSpan.FromMilliseconds(100),
                                        serverToken);

                                    if (currentDungeonId == 0
                                        && destinationSlot is 9 or EquippedTitleSlot)
                                    {
                                        await SendTownAppearanceAsync();
                                    }
                                    else
                                    {
                                        // Repeated protocol-14 removals leave
                                        // this client with a stale worn-object
                                        // pointer and crash on the second
                                        // unequip. Subtype one owns the full
                                        // equipment projection and safely
                                        // removes every omitted worn slot.
                                        await SendCurrentEquipmentStateAsync();
                                    }

                                    logger.LogInformation(
                                        "Unequipped item {ItemId} from 3:{SourceSlot} to 0:{DestinationSlot}",
                                        itemToUnequip.ItemId,
                                        destinationSlot,
                                        sourceSlot);

                                    continue;
                                }

                                if (activeCharacter is not null && parsedMove)
                                {
                                    await SendInventoryMoveNoOpAsync(
                                        sourceListType,
                                        sourceSlot,
                                        destinationListType,
                                        destinationSlot,
                                        "invalid or unavailable source/destination");
                                }
                                else
                                {
                                    var error = GameProtocolEngine.CreateCommandError(request, 4);
                                    await error.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, error);
                                }

                                if (activeCharacter is not null
                                    && parsedMove
                                    && sourceListType == 0
                                    && destinationListType == 0)
                                {
                                    var refreshMain = CreateMainInventoryPacket(
                                        currentGold,
                                        currentVictoryPoints,
                                        mainInventory.Values);
                                    await refreshMain.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, refreshMain);
                                }

                                continue;
                            }

                            // A forward cargo withdrawal carries the occupied
                            // cargo source and an explicit empty bag target.
                            // Treat it as a newly entering bag item: fill every
                            // compatible partial stack before allocating a slot.
                            if (sourceListType == 2
                                && destinationListType == 0
                                && !mainInventory.ContainsKey(destinationSlot)
                                && IsCompatibleMainInventorySlot(sourceItem, destinationSlot))
                            {
                                var withdrawnCount = moveCount <= 0
                                    ? sourceItem.CountOrValue
                                    : Math.Min(sourceItem.CountOrValue, (uint)moveCount);
                                var withdrawal = new CharacterMailAttachmentRecord(
                                    sourceItem.ItemId,
                                    withdrawnCount,
                                    sourceItem.State,
                                    sourceItem.Durability,
                                    sourceItem.SealState,
                                    EquipmentQualitySeed:
                                        sourceItem.EquipmentQualitySeed,
                                    InstanceId: sourceItem.InstanceId);
                                if (!CharacterInventoryPlanner.TryPlace(
                                        mainInventory,
                                        withdrawal,
                                        itemCatalog,
                                        GetInventoryWeightLimit(activeCharacter),
                                        destinationSlot,
                                        out var withdrawalPlan,
                                        out var withdrawalFailure))
                                {
                                    await SendInventoryMoveNoOpAsync(
                                        sourceListType,
                                        sourceSlot,
                                        destinationListType,
                                        destinationSlot,
                                        $"forward warehouse withdrawal failed: {withdrawalFailure}");
                                    continue;
                                }

                                var plannedWarehouse = warehouseInventory.ToDictionary(
                                    pair => pair.Key,
                                    pair => pair.Value);
                                if (withdrawnCount < sourceItem.CountOrValue)
                                {
                                    plannedWarehouse[sourceSlot] = sourceItem with
                                    {
                                        CountOrValue = sourceItem.CountOrValue - withdrawnCount
                                    };
                                }
                                else
                                {
                                    plannedWarehouse.Remove(sourceSlot);
                                }

                                var savedWithdrawal = await store.SaveCharacterItemSpacesAsync(
                                    accountName,
                                    activeCharacter.Id,
                                    currentGold,
                                    withdrawalPlan.Inventory.Values,
                                    equippedInventory.Values,
                                    plannedWarehouse.Values,
                                    serverToken);
                                if (savedWithdrawal is null)
                                {
                                    await SendInventoryMoveNoOpAsync(
                                        sourceListType,
                                        sourceSlot,
                                        destinationListType,
                                        destinationSlot,
                                        "forward warehouse withdrawal persistence failed");
                                    continue;
                                }

                                activeCharacter = savedWithdrawal;
                                mainInventory = withdrawalPlan.Inventory;
                                warehouseInventory = plannedWarehouse;

                                var withdrawReply =
                                    GameProtocolEngine.CreateMoveItemSpaceReply(
                                        sourceListType,
                                        sourceSlot,
                                        withdrawnCount,
                                        destinationListType,
                                        destinationSlot);
                                await withdrawReply.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, withdrawReply);

                                foreach (var refresh in new[]
                                         {
                                             CreateMainInventoryPacket(
                                                 currentGold,
                                                 currentVictoryPoints,
                                                 mainInventory.Values,
                                                 withdrawalPlan.DestinationSlot),
                                             CreateWarehousePacket(
                                                 warehouseInventory.Values,
                                                 warehouseCapacity)
                                         })
                                {
                                    await refresh.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, refresh);
                                }

                                logger.LogInformation(
                                    "Withdrew item {ItemId} from forward cargo slot {CargoSlot}; automatic main destination starts at {MainSlot}.",
                                    sourceItem.ItemId,
                                    sourceSlot,
                                    withdrawalPlan.DestinationSlot);
                                continue;
                            }

                            if (destinationSlot == ushort.MaxValue)
                            {
                                destinationSlot = FindPartialStackSlot(
                                    destinationSpace,
                                    sourceItem,
                                    destinationListType,
                                    sourceSpace == destinationSpace ? sourceSlot : null);
                                if (destinationSlot == ushort.MaxValue)
                                {
                                    destinationSlot = destinationListType == 0
                                        ? FindFreeCompatibleMainInventorySlot(
                                            destinationSpace,
                                            sourceItem)
                                        : FindFreeInventorySlot(
                                            destinationSpace,
                                            0,
                                            destinationListType switch
                                            {
                                                1 => CharacterAvatarInventoryLayout.Capacity,
                                                2 => warehouseCapacity,
                                                3 => 23,
                                                _ => 0
                                            });
                                }
                            }

                            if (destinationSlot == ushort.MaxValue
                                || !IsInventorySlotValid(
                                    destinationListType,
                                    destinationSlot,
                                    warehouseCapacity)
                                || !IsCompatibleInventorySlot(
                                    destinationListType,
                                    sourceItem,
                                    destinationSlot,
                                    warehouseCapacity))
                            {
                                await SendInventoryMoveNoOpAsync(
                                    sourceListType,
                                    sourceSlot,
                                    destinationListType,
                                    destinationSlot,
                                    "destination slot is invalid or incompatible");
                                continue;
                            }

                            if (destinationListType == 2
                                && itemCatalog.TryGetDefinition(
                                    sourceItem.ItemId,
                                    out var warehouseDefinition)
                                && warehouseDefinition.InventoryCategory
                                    == ItemInventoryCategory.Avatar)
                            {
                                await SendInventoryMoveNoOpAsync(
                                    sourceListType,
                                    sourceSlot,
                                    destinationListType,
                                    destinationSlot,
                                    "avatar items cannot enter character storage");
                                continue;
                            }

                            if (destinationListType == 3
                                && itemCatalog.TryGetDefinition(
                                    sourceItem.ItemId,
                                    out var equipmentDefinition)
                                && !premiumBenefitCatalog.CanEquipByLevel(
                                    activeCharacter.Level,
                                    GetActivePremiumServiceTypes(),
                                    equipmentDefinition))
                            {
                                await SendInventoryMoveNoOpAsync(
                                    sourceListType,
                                    sourceSlot,
                                    destinationListType,
                                    destinationSlot,
                                    $"equipment level {equipmentDefinition.MinimumLevel.GetValueOrDefault()} exceeds the Premium-effective character level");
                                continue;
                            }

                            var movedWithinMainList = sourceListType == 0
                                && destinationListType == 0;
                            if (sourceListType == destinationListType
                                && sourceSlot == destinationSlot)
                            {
                                var noOpReply = GameProtocolEngine.CreateMoveItemSpaceReply(
                                    sourceListType,
                                    sourceSlot,
                                    sourceItem.CountOrValue,
                                    destinationListType,
                                    destinationSlot);
                                await noOpReply.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, noOpReply);
                                continue;
                            }

                            destinationSpace.TryGetValue(destinationSlot, out var displacedItem);
                            if (displacedItem is not null
                                && !IsCompatibleInventorySlot(
                                    sourceListType,
                                    displacedItem,
                                    sourceSlot,
                                    warehouseCapacity))
                            {
                                await SendInventoryMoveNoOpAsync(
                                    sourceListType,
                                    sourceSlot,
                                    destinationListType,
                                    destinationSlot,
                                    "displaced item is incompatible with the source slot");
                                continue;
                            }

                            if (sourceListType == 3 || destinationListType == 3)
                            {
                                sourceItem = CharacterItemSealing.UnsealWhenEquipped(
                                    sourceItem,
                                    itemCatalog);
                                if (displacedItem is not null)
                                {
                                    displacedItem = CharacterItemSealing.UnsealWhenEquipped(
                                        displacedItem,
                                        itemCatalog);
                                }
                            }

                            var requestedCount = moveCount <= 0
                                ? sourceItem.CountOrValue
                                : Math.Min(sourceItem.CountOrValue, (uint)moveCount);
                            var canStack = displacedItem is not null
                                && sourceListType != 3
                                && destinationListType != 3
                                && !IsEquipmentItem(sourceItem.ItemId)
                                && sourceItem.ItemId == displacedItem.ItemId
                                && sourceItem.State == displacedItem.State
                                && sourceItem.Durability == displacedItem.Durability
                                && sourceItem.SealState == displacedItem.SealState;
                            var canSplit = sourceListType != 3
                                && destinationListType != 3
                                && displacedItem is null
                                && requestedCount < sourceItem.CountOrValue;

                            if (canStack)
                            {
                                var stackLimit = itemCatalog.TryGetDefinition(
                                        sourceItem.ItemId,
                                        out var stackDefinition)
                                    ? CharacterInventoryPlanner.GetStackLimit(stackDefinition)
                                    : CharacterInventoryPlanner.DefaultStackLimit;
                                var available = stackLimit > displacedItem!.CountOrValue
                                    ? stackLimit - displacedItem.CountOrValue
                                    : 0;
                                if (available == 0)
                                {
                                    await SendInventoryMoveNoOpAsync(
                                        sourceListType,
                                        sourceSlot,
                                        destinationListType,
                                        destinationSlot,
                                        "destination stack is full");
                                    continue;
                                }

                                requestedCount = Math.Min(requestedCount, available);
                                var stackedCount = checked(
                                    displacedItem!.CountOrValue + requestedCount);
                                destinationSpace[destinationSlot] = displacedItem with
                                {
                                    CountOrValue = stackedCount
                                };
                                if (requestedCount < sourceItem.CountOrValue)
                                {
                                    sourceSpace[sourceSlot] = sourceItem with
                                    {
                                        CountOrValue = sourceItem.CountOrValue - requestedCount
                                    };
                                }
                                else
                                {
                                    sourceSpace.Remove(sourceSlot);
                                }
                            }
                            else if (canSplit)
                            {
                                sourceSpace[sourceSlot] = sourceItem with
                                {
                                    CountOrValue = sourceItem.CountOrValue - requestedCount
                                };
                                destinationSpace[destinationSlot] = sourceItem with
                                {
                                    Slot = destinationSlot,
                                    CountOrValue = requestedCount
                                };
                            }
                            else
                            {
                                sourceSpace.Remove(sourceSlot);
                                destinationSpace[destinationSlot] = CharacterItemIdentity.MoveToSlot(
                                    sourceItem, destinationSlot, itemCatalog);

                                if (displacedItem is not null)
                                {
                                    destinationSpace.Remove(destinationSlot);
                                    destinationSpace[destinationSlot] = CharacterItemIdentity.MoveToSlot(
                                        sourceItem, destinationSlot, itemCatalog);
                                    sourceSpace[sourceSlot] = CharacterItemIdentity.MoveToSlot(
                                        displacedItem, sourceSlot, itemCatalog);
                                }
                            }

                            activeCharacter = await store.SaveCharacterItemSpacesAsync(
                                accountName,
                                activeCharacter.Id,
                                currentGold,
                                mainInventory.Values,
                                equippedInventory.Values,
                                warehouseInventory.Values,
                                serverToken,
                                avatarInventory: avatarInventory.Values) ?? activeCharacter;

                            var moveReply = GameProtocolEngine.CreateMoveItemSpaceReply(
                                sourceListType,
                                sourceSlot,
                                requestedCount,
                                destinationListType,
                                destinationSlot);
                            await moveReply.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, moveReply);

                            if (sourceListType == 2 || destinationListType == 2)
                            {
                                if (sourceListType == 2 && destinationListType == 0)
                                {
                                    // CMD 19 already removes a deposited item from the
                                    // main inventory. A redundant full list-zero snapshot
                                    // makes the old client select the category of the last
                                    // reconstructed item, unexpectedly changing the active
                                    // bag tab. Withdrawals still need the snapshot because
                                    // automatic stacking can update more than one bag slot.
                                    var refreshMain = CreateMainInventoryPacket(
                                        currentGold,
                                        currentVictoryPoints,
                                        mainInventory.Values,
                                        preferredLastSlot: destinationSlot);
                                    await refreshMain.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, refreshMain);
                                }

                                var refreshWarehouse = CreateWarehousePacket(
                                    warehouseInventory.Values,
                                    warehouseCapacity);
                                await refreshWarehouse.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, refreshWarehouse);

                                logger.LogInformation(
                                    "Moved character cargo item {ItemId} from {SourceList}:{SourceSlot} to {DestinationList}:{DestinationSlot}.",
                                    sourceItem.ItemId,
                                    sourceListType,
                                    sourceSlot,
                                    destinationListType,
                                    destinationSlot);
                                continue;
                            }

                            if (sourceListType == 0)
                            {
                                // Protocol 19's internal removal is guarded by
                                // a client-side pending-operation vector. Some
                                // equipment paths leave that vector empty. A
                                // move into an occupied worn slot swaps the old
                                // equipment back into this source slot, so the
                                // authoritative update must preserve that item
                                // instead of always deleting the source.
                                if (ShouldUseIncrementalMainMoveRefresh(
                                        sourceListType,
                                        sourceSlot,
                                        destinationListType,
                                        destinationSlot))
                                {
                                    // CMD 19 completes the client transaction; NOTI 14
                                    // only reconciles the two changed slots and therefore
                                    // does not reconstruct every inventory category.
                                    await SendMainInventoryMoveDeltaAsync(
                                        sourceSlot,
                                        destinationSlot);
                                }
                                else if (movedWithinMainList
                                    || (destinationListType == 3 && displacedItem is not null))
                                {
                                    // DFLegacy does not reliably replace existing
                                    // objects across the quick-slot (3..8) and
                                    // bag (9+) regions, nor during an occupied
                                    // equipment swap, when protocol 14 carries
                                    // only one slot. Refresh the complete main
                                    // list so both endpoints are authoritative.
                                    var refreshBag = CreateMainInventoryPacket(
                                        currentGold,
                                        currentVictoryPoints,
                                        mainInventory.Values,
                                        preferredLastSlot: movedWithinMainList
                                            ? destinationSlot
                                            : sourceSlot);
                                    await refreshBag.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, refreshBag);
                                }
                                else
                                {
                                    var sourceUpdateEntry = mainInventory.TryGetValue(
                                        sourceSlot,
                                        out var sourceReplacement)
                                            ? ToGameInventoryEntry(sourceReplacement)
                                            : new GameInventoryEntry(
                                                sourceSlot,
                                                ushort.MaxValue,
                                                0);
                                    var updateSourceItem =
                                        GameProtocolEngine.CreateUpdateItemList(
                                            0,
                                            [sourceUpdateEntry]);
                                    await updateSourceItem.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, updateSourceItem);
                                }
                            }

                            if (sourceListType == 1 || destinationListType == 1)
                            {
                                var refreshAvatar = CreateAvatarInventoryPacket(
                                    avatarInventory.Values);
                                await refreshAvatar.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, refreshAvatar);
                            }

                            if (sourceListType == 3 || destinationListType == 3)
                            {
                                if (destinationListType == 3)
                                {
                                    if (displacedItem is null)
                                    {
                                        var addWornItem =
                                            GameProtocolEngine.CreateUpdateItemList(
                                                3,
                                                [ToGameInventoryEntry(
                                                    equippedInventory[destinationSlot])]);
                                        await addWornItem.WriteAsync(stream, serverToken);
                                        Interlocked.Increment(ref runtimeSession.SentPackets);
                                        LogPacket("TX", runtimeSession, addWornItem);
                                    }
                                }

                                // The launcher enables the client's existing
                                // notification-14 construction path for empty
                                // worn slots. Keep that incremental path for
                                // additions; removals are synchronized below
                                // through the subtype-one snapshot.
                            }

                            var replacedWornItem = destinationListType == 3
                                && displacedItem is not null;
                            if (replacedWornItem)
                            {
                                // The drag controller still owns the old worn
                                // object while the command reply is processed.
                                await Task.Delay(
                                    TimeSpan.FromMilliseconds(100),
                                    serverToken);
                            }

                            if (currentDungeonId == 0
                                && ((sourceListType == 3
                                        && sourceSlot <= EquippedTitleSlot)
                                    || (destinationListType == 3
                                        && destinationSlot <= EquippedTitleSlot)))
                            {
                                await SendTownAppearanceAsync();
                            }
                            else if (sourceListType == 3 || replacedWornItem)
                            {
                                await SendCurrentEquipmentStateAsync();
                            }

                            logger.LogInformation(
                                "Moved item {ItemId} from {SourceList}:{SourceSlot} to {DestinationList}:{DestinationSlot}",
                                sourceItem.ItemId,
                                sourceListType,
                                sourceSlot,
                                destinationListType,
                                destinationSlot);
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.DeleteItemCommand)
                        {
                            if (!options.EnablePacketTracing)
                            {
                                // Snapshot-only (no tap): the RX funnel call in
                                // LogPacket already owns tap publication for
                                // this frame.
                                runtime.TracePacketAlways(
                                    runtimeSession,
                                    "RX-CONSUME-ITEMS",
                                    request,
                                    options.HexDumpLimit);
                            }

                            if (activeCharacter is null
                                || !TryReadConsumeItems(
                                    request.Body,
                                    out var listType,
                                    out var requestedConsumptions)
                                || listType != 0)
                            {
                                logger.LogWarning(
                                    "Rejected consume-items transaction activeCharacter={HasCharacter}, body={Body}.",
                                    activeCharacter is not null,
                                    Convert.ToHexString(request.Body));
                                continue;
                            }

                            var completionEntries =
                                new List<GameItemConsumptionEntry>(
                                    requestedConsumptions.Length);
                            var consumptionBySlot = new Dictionary<ushort, ulong>();
                            var validTransaction = true;
                            foreach (var requestedConsumption in requestedConsumptions)
                            {
                                if (!mainInventory.TryGetValue(
                                        requestedConsumption.Slot,
                                        out var inventoryItem)
                                    || requestedConsumption.RequestedCount == 0
                                    || requestedConsumption.ItemId != inventoryItem.ItemId
                                    || IsEquipmentItem(inventoryItem.ItemId))
                                {
                                    logger.LogWarning(
                                        "Consume-items transaction has an invalid entry operation={Operation}, slot={Slot}, requestedItem={RequestedItemId}, actualItem={ActualItemId}, requested={RequestedCount}.",
                                        requestedConsumption.Operation,
                                        requestedConsumption.Slot,
                                        requestedConsumption.ItemId,
                                        inventoryItem?.ItemId,
                                        requestedConsumption.RequestedCount);
                                    validTransaction = false;
                                    break;
                                }

                                var accumulated = consumptionBySlot.GetValueOrDefault(
                                    requestedConsumption.Slot);
                                accumulated += requestedConsumption.RequestedCount;
                                if (accumulated > inventoryItem.CountOrValue)
                                {
                                    logger.LogWarning(
                                        "Consume-items transaction exceeds slot={Slot} stack item={ItemId}, requestedTotal={RequestedTotal}, current={CurrentCount}.",
                                        requestedConsumption.Slot,
                                        inventoryItem.ItemId,
                                        accumulated,
                                        inventoryItem.CountOrValue);
                                    validTransaction = false;
                                    break;
                                }

                                consumptionBySlot[requestedConsumption.Slot] =
                                    accumulated;
                                completionEntries.Add(
                                    new GameItemConsumptionEntry(
                                        requestedConsumption.Slot,
                                        requestedConsumption.RequestedCount));
                            }

                            if (!validTransaction)
                            {
                                var deleteItemError =
                                    GameProtocolEngine.CreateDeleteItemError(
                                        listType,
                                        errorCode: 0x11);
                                await deleteItemError.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, deleteItemError);
                                continue;
                            }

                            foreach (var (slot, requestedCount) in consumptionBySlot)
                            {
                                var inventoryItem = mainInventory[slot];
                                var remaining = inventoryItem.CountOrValue
                                    - checked((uint)requestedCount);
                                if (remaining == 0)
                                {
                                    mainInventory.Remove(slot);
                                }
                                else
                                {
                                    mainInventory[slot] = inventoryItem with
                                    {
                                        CountOrValue = remaining
                                    };
                                }
                            }

                            activeCharacter = await store.SaveCharacterItemSpacesAsync(
                                accountName,
                                activeCharacter.Id,
                                currentGold,
                                mainInventory.Values,
                                equippedInventory.Values,
                                warehouseInventory.Values,
                                serverToken,
                                avatarInventory: avatarInventory.Values) ?? activeCharacter;

                            foreach (var requestedConsumption in requestedConsumptions)
                            {
                                logger.LogInformation(
                                    "Consumed material listType={ListType}, operation={Operation}, slot={Slot}, item={ItemId}, consumed={ConsumedCount}, remaining={RemainingCount}.",
                                    listType,
                                    requestedConsumption.Operation,
                                    requestedConsumption.Slot,
                                    requestedConsumption.ItemId,
                                    requestedConsumption.RequestedCount,
                                    mainInventory.TryGetValue(
                                        requestedConsumption.Slot,
                                        out var remainingItem)
                                        ? remainingItem.CountOrValue
                                        : 0);
                            }

                            var deleteItemReply =
                                GameProtocolEngine.CreateDeleteItemReply(
                                    listType,
                                    completionEntries);
                            await deleteItemReply.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, deleteItemReply);
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.UseStackableItemCommand)
                        {
                            if (!options.EnablePacketTracing)
                            {
                                runtime.TracePacketAlways(
                                    runtimeSession,
                                    "RX-DUNGEON-ITEM",
                                    request,
                                    options.HexDumpLimit);
                            }

                            var parsedStackableItemUse = TryReadStackableItemUse(
                                request.Body,
                                out var slot,
                                out var listType);
                            CharacterItemRecord? consumedItem = null;
                            if (activeCharacter is not null && parsedStackableItemUse)
                            {
                                if (listType == 0)
                                {
                                    mainInventory.TryGetValue(slot, out consumedItem);
                                }
                                else if (listType == 7)
                                {
                                    creatureInventory.TryGetValue(slot, out consumedItem);
                                }
                            }

                            if (activeCharacter is null
                                || !parsedStackableItemUse
                                || listType is not (0 or 7)
                                || consumedItem is null
                                || consumedItem.CountOrValue == 0
                                || listType == 0 && IsEquipmentItem(consumedItem.ItemId)
                                || listType == 7
                                    && consumedItem.ItemId != CreatureHungerPlanner.FoodItemId)
                            {
                                var error = GameProtocolEngine.CreateUseStackableItemError(
                                    errorCode: 17,
                                    listType);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                logger.LogWarning(
                                    "Rejected stackable-item use activeCharacter={HasCharacter}, dungeon={DungeonId}, parsed={Parsed}, listType={ListType}, slot={Slot}, body={Body}.",
                                    activeCharacter is not null,
                                    currentDungeonId,
                                    parsedStackableItemUse,
                                    listType,
                                    slot,
                                    Convert.ToHexString(request.Body));
                                continue;
                            }

                            if (listType == 7)
                            {
                                // 13339's CCreature::UseFeed accepts the
                                // Creature-space food item in town or in a
                                // dungeon. Settle elapsed dungeon hunger first
                                // so the revival decision uses authoritative
                                // stomach data rather than a stale snapshot.
                                if (currentDungeonId != 0
                                    && !await SettleDungeonCreatureHungerAsync(
                                        notifyClient: true,
                                        serverToken))
                                {
                                    var settlementError =
                                        GameProtocolEngine.CreateUseStackableItemError(
                                            errorCode: 17,
                                            listType);
                                    await settlementError.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, settlementError);
                                    continue;
                                }

                                if (!creatureInventory.TryGetValue(
                                        slot,
                                        out var foodItem)
                                    || !creatureInventory.TryGetValue(
                                        CharacterCreatureInventoryLayout
                                            .EquippedCreatureSlot,
                                        out var equippedCreature)
                                    || equippedCreature.CountOrValue == 0)
                                {
                                    var creatureFoodError =
                                        GameProtocolEngine.CreateUseStackableItemError(
                                            errorCode: 17,
                                            listType);
                                    await creatureFoodError.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, creatureFoodError);
                                    logger.LogWarning(
                                        "Rejected Creature food use at slot {Slot}: equipped Creature is unavailable.",
                                        slot);
                                    continue;
                                }

                                var previousStomach = (byte)Math.Clamp(
                                    (int)(equippedCreature.CreatureStomach ?? 100),
                                    0,
                                    CreatureHungerPlanner.MaximumStomach);
                                var plannedCreatureInventory = creatureInventory
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
                                        accountName,
                                        activeCharacter.Id,
                                        currentGold,
                                        mainInventory.Values,
                                        equippedInventory.Values,
                                        warehouseInventory.Values,
                                        serverToken,
                                        avatarInventory: avatarInventory.Values,
                                        creatureInventory:
                                            plannedCreatureInventory.Values);
                                if (savedCreatureFood is null)
                                {
                                    var creatureFoodSaveError =
                                        GameProtocolEngine.CreateUseStackableItemError(
                                            errorCode: 17,
                                            listType);
                                    await creatureFoodSaveError.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, creatureFoodSaveError);
                                    logger.LogWarning(
                                        "Could not persist Creature food use at slot {Slot} for character {CharacterId}.",
                                        slot,
                                        activeCharacter.Id);
                                    continue;
                                }

                                activeCharacter = savedCreatureFood;
                                mainInventory = (savedCreatureFood.Inventory ?? [])
                                    .ToDictionary(item => item.Slot);
                                equippedInventory = (savedCreatureFood.Equipment ?? [])
                                    .ToDictionary(item => item.Slot);
                                warehouseInventory = (savedCreatureFood.Warehouse ?? [])
                                    .ToDictionary(item => item.Slot);
                                avatarInventory = (savedCreatureFood.AvatarInventory ?? [])
                                    .ToDictionary(item => item.Slot);
                                creatureInventory = (savedCreatureFood.CreatureInventory ?? [])
                                    .ToDictionary(item => item.Slot);

                                if (previousStomach == 0)
                                {
                                    creatureDeathNotificationsSent.Remove(
                                        equippedCreature.CountOrValue);
                                    var revival =
                                        GameProtocolEngine.CreateRevivalCreature(
                                            localUserId);
                                    await revival.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, revival);
                                }

                                var creatureState = GameProtocolEngine.CreateCreatureState(
                                    equippedCreature.CountOrValue,
                                    restoredStomach);
                                await creatureState.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, creatureState);

                                var creatureFoodReply =
                                    GameProtocolEngine.CreateUseStackableItemReply(
                                        slot,
                                        listType);
                                await creatureFoodReply.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, creatureFoodReply);
                                foreach (var creaturePacket in
                                         CreateCreatureInventoryPackets(
                                             creatureInventory.Values))
                                {
                                    await creaturePacket.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, creaturePacket);
                                }

                                logger.LogInformation(
                                    "Used Creature food item {ItemId} at slot {Slot}; stomach {PreviousStomach}->{Stomach}, revived={Revived}.",
                                    foodItem.ItemId,
                                    slot,
                                    previousStomach,
                                    restoredStomach,
                                    previousStomach == 0);
                                continue;
                            }

                            if (consumedItem.ItemId == FatigueRecoveryItemId)
                            {
                                var premiumFatigue = HasActivePremiumService(
                                        GameProtocolEngine.BlackDiamondServiceType)
                                    ? GameProtocolEngine.BlackDiamondBonusFatigue
                                    : (ushort)0;
                                var maximumFatigue = checked((ushort)(
                                    GameProtocolEngine.DefaultMaximumFatigue + premiumFatigue));
                                var useResult = await store.UseFatigueRecoveryItemAsync(
                                    accountName,
                                    activeCharacter.Id,
                                    slot,
                                    FatigueRecoveryItemId,
                                    maximumFatigue,
                                    FatigueRecoveryUseThreshold,
                                    FatigueRecoveryAmount,
                                    serverToken);
                                if (!useResult.Success || useResult.Character is null)
                                {
                                    var error = GameProtocolEngine.CreateUseStackableItemError(
                                        errorCode: 17,
                                        listType);
                                    await error.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, error);
                                    if (useResult.Failure
                                        == FatigueRecoveryItemUseFailure.FatigueNotLowEnough)
                                    {
                                        var popup = GameProtocolEngine.CreatePopupNotification(
                                            ClientEncoding.GetBytes(FatigueRecoveryRejectedMessage));
                                        await popup.WriteAsync(stream, serverToken);
                                        Interlocked.Increment(ref runtimeSession.SentPackets);
                                        LogPacket("TX", runtimeSession, popup);
                                    }
                                    logger.LogInformation(
                                        "Rejected fatigue recovery item {ItemId} at slot {Slot}; failure={Failure}, remaining fatigue={RemainingFatigue}/{MaximumFatigue}, stack={RemainingCount}.",
                                        FatigueRecoveryItemId,
                                        slot,
                                        useResult.Failure,
                                        maximumFatigue - useResult.PreviousUsedFatigue,
                                        maximumFatigue,
                                        useResult.RemainingItemCount);
                                    continue;
                                }

                                activeCharacter = useResult.Character;
                                mainInventory = (activeCharacter.Inventory ?? [])
                                    .ToDictionary(item => item.Slot);

                                var fatigue = GameProtocolEngine.CreateFatigue(
                                    usedFatigue: useResult.UsedFatigue,
                                    maximumFatigue: maximumFatigue,
                                    premiumFatigue: premiumFatigue);
                                await fatigue.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, fatigue);

                                var fatigueItemReply =
                                    GameProtocolEngine.CreateUseStackableItemReply(
                                        slot,
                                        listType);
                                await fatigueItemReply.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, fatigueItemReply);
                                logger.LogInformation(
                                    "Used fatigue recovery item {ItemId} at slot {Slot}; restored={RecoveryAmount}, remaining fatigue {PreviousRemaining}->{Remaining}/{MaximumFatigue}, stack={RemainingCount}.",
                                    FatigueRecoveryItemId,
                                    slot,
                                    FatigueRecoveryAmount,
                                    maximumFatigue - useResult.PreviousUsedFatigue,
                                    maximumFatigue - useResult.UsedFatigue,
                                    maximumFatigue,
                                    useResult.RemainingItemCount);
                                continue;
                            }

                            if (consumedItem.ItemId == LevelUpCouponItemId)
                            {
                                var useResult = await store.UseLevelUpCouponItemAsync(
                                    accountName,
                                    activeCharacter.Id,
                                    slot,
                                    LevelUpCouponItemId,
                                    experienceCatalog,
                                    spCatalog,
                                    serverToken);
                                if (!useResult.Success
                                    || useResult.Character is null
                                    || useResult.Progression is null)
                                {
                                    var error = GameProtocolEngine.CreateUseStackableItemError(
                                        errorCode: 17,
                                        listType);
                                    await error.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, error);
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
                                    await SendPopupNotificationAsync(
                                        ClientEncoding.GetBytes(rejectedMessage));
                                    logger.LogInformation(
                                        "Rejected level-up coupon {ItemId} at slot {Slot}; failure={Failure}, level={Level}, stack={RemainingCount}.",
                                        LevelUpCouponItemId,
                                        slot,
                                        useResult.Failure,
                                        activeCharacter.Level,
                                        useResult.RemainingItemCount);
                                    continue;
                                }

                                var progression = useResult.Progression;
                                activeCharacter = useResult.Character;
                                currentSp = Math.Max(0, activeCharacter.Sp);
                                mainInventory = (activeCharacter.Inventory ?? [])
                                    .ToDictionary(item => item.Slot);

                                var levelUpItemReply =
                                    GameProtocolEngine.CreateUseStackableItemReply(
                                        slot,
                                        listType);
                                await levelUpItemReply.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, levelUpItemReply);
                                await SendTypedMessageNotificationAsync(
                                    GameMessageType.Type16Brown,
                                    0,
                                    ClientEncoding.GetBytes(LevelUpCouponSuccessMessage));

                                await SendCurrentSkillInfoAsync();
                                await ValidateAndCorrectCharacterStatsAsync(
                                    "level-up coupon");
                                await SendAcceptableQuestListAsync();

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
                                continue;
                            }

                            if (listType == 0
                                && pvpExperienceCatalog.TryGetBookExperience(
                                    consumedItem.ItemId,
                                    out var pvpBookExperience))
                            {
                                var pvpUseResult = await store.UsePvpExperienceItemAsync(
                                    accountName,
                                    activeCharacter.Id,
                                    slot,
                                    consumedItem.ItemId,
                                    pvpBookExperience,
                                    pvpExperienceCatalog,
                                    serverToken);
                                if (!pvpUseResult.Success
                                    || pvpUseResult.Character is null)
                                {
                                    var error = GameProtocolEngine.CreateUseStackableItemError(
                                        errorCode: 17,
                                        listType);
                                    await error.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, error);
                                    logger.LogInformation(
                                        "Rejected PvP experience item {ItemId} at slot {Slot}; failure={Failure}, stack={RemainingCount}.",
                                        consumedItem.ItemId,
                                        slot,
                                        pvpUseResult.Failure,
                                        pvpUseResult.RemainingItemCount);
                                    continue;
                                }

                                activeCharacter = pvpUseResult.Character;
                                mainInventory = (activeCharacter.Inventory ?? [])
                                    .ToDictionary(item => item.Slot);

                                var pvpItemReply =
                                    GameProtocolEngine.CreateUseStackableItemReply(
                                        slot,
                                        listType);
                                await pvpItemReply.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, pvpItemReply);

                                var pvpInventoryRefresh = CreateMainInventoryPacket(
                                    currentGold,
                                    currentVictoryPoints,
                                    mainInventory.Values);
                                await pvpInventoryRefresh.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, pvpInventoryRefresh);

                                await SendPvpRecordAsync();
                                await SendTypedMessageNotificationAsync(
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
                                    activeCharacter.PvpWins,
                                    activeCharacter.PvpPlayCount,
                                    activeCharacter.PvpCount,
                                    pvpUseResult.RemainingItemCount);
                                continue;
                            }

                            if (itemCatalog.TryGetDefinition(
                                    consumedItem.ItemId,
                                    out var boosterItemDefinition)
                                && boosterItemDefinition.CeraBooster is not null)
                            {
                                CeraBoosterOpeningPlan boosterPlan = null!;
                                var boosterFailure = "Cera boosters can only be opened in town";
                                var canOpenBooster = currentDungeonId == 0
                                    && CeraBoosterOpeningPlanner.TryCreate(
                                        mainInventory,
                                        avatarInventory,
                                        creatureInventory,
                                        slot,
                                        itemCatalog,
                                        GetInventoryWeightLimit(activeCharacter),
                                        GameRandomSource.Shared,
                                        out boosterPlan,
                                        out boosterFailure);
                                if (!canOpenBooster)
                                {
                                    var error = GameProtocolEngine.CreateUseStackableItemError(
                                        errorCode: 17,
                                        listType);
                                    await error.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, error);
                                    logger.LogWarning(
                                        "Rejected Cera booster item {ItemId} at slot {Slot}; dungeon={DungeonId}, failure={Failure}.",
                                        consumedItem.ItemId,
                                        slot,
                                        currentDungeonId,
                                        boosterFailure);
                                    continue;
                                }

                                var warehouseUpgradeSettlement =
                                    PlanWarehouseUpgradeSettlement(
                                        boosterPlan.MainInventory.Values,
                                        warehouseInventory.Values,
                                        boosterPlan.Rewards.Select(reward => reward.ItemId));
                                var hasWarehouseUpgradeReward = boosterPlan.Rewards.Any(reward =>
                                    CharacterWarehouseProgression.TryGetItemTargetCapacity(
                                        reward.ItemId,
                                        out _));
                                var overflowMails = CreateOverflowMailRequests(
                                    boosterPlan.OverflowItems.Where(item =>
                                        !CharacterWarehouseProgression
                                            .TryGetItemTargetCapacity(item.ItemId, out _)),
                                    "Cera booster reward");
                                var savedCharacter = await store.SaveCharacterItemSpacesAsync(
                                    accountName,
                                    activeCharacter.Id,
                                    currentGold,
                                    warehouseUpgradeSettlement.Inventory,
                                    equippedInventory.Values,
                                    warehouseUpgradeSettlement.Warehouse,
                                    serverToken,
                                    overflowMails,
                                    boosterPlan.AvatarInventory.Values,
                                    boosterPlan.CreatureInventory.Values,
                                    warehouseCapacity:
                                        warehouseUpgradeSettlement.Capacity,
                                    expectedWarehouseCapacity: warehouseCapacity);
                                if (savedCharacter is null)
                                {
                                    var error = GameProtocolEngine.CreateUseStackableItemError(
                                        errorCode: 17,
                                        listType);
                                    await error.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, error);
                                    logger.LogWarning(
                                        "Could not persist Cera booster item {ItemId} at slot {Slot}.",
                                        consumedItem.ItemId,
                                        slot);
                                    continue;
                                }

                                var warehouseCapacityChanged =
                                    AdoptSavedWarehouseUpgradeState(savedCharacter);
                                avatarInventory = (savedCharacter.AvatarInventory ?? [])
                                    .ToDictionary(item => item.Slot);
                                creatureInventory = (savedCharacter.CreatureInventory ?? [])
                                    .ToDictionary(item => item.Slot);

                                var boosterReply =
                                    GameProtocolEngine.CreateUseStackableItemReply(
                                        slot,
                                        listType);
                                await boosterReply.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, boosterReply);

                                var mainRefresh = CreateMainInventoryPacket(
                                    currentGold,
                                    currentVictoryPoints,
                                    mainInventory.Values);
                                await mainRefresh.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, mainRefresh);

                                var avatarRefresh = CreateAvatarInventoryPacket(
                                    avatarInventory.Values);
                                await avatarRefresh.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, avatarRefresh);

                                foreach (var creatureRefresh in CreateCreatureInventoryPackets(
                                             creatureInventory.Values))
                                {
                                    await creatureRefresh.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, creatureRefresh);
                                }

                                if (warehouseCapacityChanged || hasWarehouseUpgradeReward)
                                {
                                    await SendWarehouseStateAsync();
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
                                    await SendMessageNotificationAsync(
                                        // A zero target keeps the legacy chat renderer
                                        // from prefixing the local character name.
                                        0,
                                        ClientEncoding.GetBytes(
                                            $"获得[{rewardName}]{rewardCount}个"));
                                }

                                if (overflowMails.Length > 0)
                                {
                                    await SendMessageNotificationAsync(
                                        0,
                                        ClientEncoding.GetBytes(
                                            "背包空间不足，部分奖励已发送至邮箱。"));
                                }

                                if (overflowMails.Length > 0)
                                {
                                    characterSessions.NotifyMailReceived(
                                        activeCharacter.CharacterNo);
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
                                continue;
                            }

                            if (currentDungeonId == 0)
                            {
                                var error = GameProtocolEngine.CreateUseStackableItemError(
                                    errorCode: 17,
                                    listType);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                logger.LogWarning(
                                    "Rejected non-fatigue stackable item {ItemId} outside a dungeon at slot {Slot}.",
                                    consumedItem.ItemId,
                                    slot);
                                continue;
                            }

                            var remaining = consumedItem.CountOrValue - 1;
                            if (remaining == 0)
                            {
                                mainInventory.Remove(slot);
                            }
                            else
                            {
                                mainInventory[slot] = consumedItem with
                                {
                                    CountOrValue = remaining
                                };
                            }

                            activeCharacter = await store.SaveCharacterInventoryAsync(
                                accountName,
                                activeCharacter.Id,
                                currentGold,
                                mainInventory.Values,
                                equippedInventory.Values,
                                serverToken) ?? activeCharacter;

                            // Command 47 success is result:u8, slot:u16 and
                            // list-type:u8. The client uses it to release its
                            // pending state and apply the local stack update.
                            var dungeonItemReply =
                                GameProtocolEngine.CreateUseStackableItemReply(
                                    slot,
                                    listType);
                            await dungeonItemReply.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, dungeonItemReply);
                            logger.LogInformation(
                                "Consumed dungeon item {ItemId} from listType={ListType}, slot={Slot}; remaining={Remaining}, body={Body}.",
                                consumedItem.ItemId,
                                listType,
                                slot,
                                remaining,
                                Convert.ToHexString(request.Body));
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId
                                == GameProtocolEngine.GenerateCeraTicketCommand)
                        {
                            var ticket = Guid.NewGuid().ToString("N");
                            var ticketReply = GameProtocolEngine.CreateCeraTicketReply(
                                ticket);
                            await ticketReply.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, ticketReply);
                            logger.LogInformation(
                                "Generated a DFLegacy Cera web ticket for character {CharacterId}.",
                                activeCharacter?.Id);
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.CeraShopBuyCommand)
                        {
                            if (!options.EnablePacketTracing)
                            {
                                // Cera-shop protocol work is still being
                                // completed. Keep only this small decoded
                                // request in the admin trace so a rejected
                                // product can be diagnosed without enabling
                                // high-volume tracing for movement packets.
                                runtime.TracePacketAlways(
                                    runtimeSession,
                                    "RX-CERA",
                                    request,
                                    options.HexDumpLimit);
                            }

                            if (activeCharacter is null
                                || !TryReadCeraShopPurchase(
                                    request.Body,
                                    out var requestedProducts,
                                    out var isGift)
                                || isGift)
                            {
                                var error = GameProtocolEngine.CreateCeraShopBuyError(
                                    errorCode: 4);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                logger.LogWarning(
                                    "Rejected malformed Cera-shop request activeCharacter={HasCharacter}, body={Body}.",
                                    activeCharacter is not null,
                                    Convert.ToHexString(request.Body));
                                continue;
                            }

                            var errorProduct = requestedProducts[0];
                            var errorClientCategory = -1;
                            var plannedInventory = mainInventory.ToDictionary(
                                pair => pair.Key,
                                pair => pair.Value);
                            var plannedAvatarInventory = avatarInventory.ToDictionary(
                                pair => pair.Key,
                                pair => pair.Value);
                            var plannedCreatureInventory = creatureInventory.ToDictionary(
                                pair => pair.Key,
                                pair => pair.Value);
                            var plannedWarehouseCapacity = warehouseCapacity;
                            var warehouseUpgraded = false;
                            var overflowMails = new List<CreateCharacterMailRequest>();
                            var immediateContainerOverflow =
                                new List<CharacterMailAttachmentRecord>();
                            var immediateContainerRewards =
                                new List<CeraShopContentReward>();
                            var purchaseReplyRewards =
                                new List<IReadOnlyList<GameCeraShopReward>>();
                            var immediateContainerMailCount = 0;
                            var openedContainerCount = 0UL;
                            var premiumServiceGrants =
                                new List<PremiumServicePurchaseGrant>();
                            var plannedGold = currentGold;
                            var plannedVictoryPoints = currentVictoryPoints;
                            var totalCashPrice = 0L;
                            var purchaseValid = true;
                            foreach (var requestedProduct in requestedProducts)
                            {
                                var purchaseReplyIndex = purchaseReplyRewards.Count;
                                purchaseReplyRewards.Add([]);
                                errorClientCategory = -1;
                                if (!ceraShopCatalog.TryGetProduct(
                                        requestedProduct.CommodityNo,
                                        out var product))
                                {
                                    purchaseValid = false;
                                    errorProduct = requestedProduct;
                                    break;
                                }

                                if (!product.UsesMainInventory
                                    && requestedProduct.AttributeValue
                                        != product.AttributeValue)
                                {
                                    purchaseValid = false;
                                    errorProduct = requestedProduct;
                                    break;
                                }

                                errorClientCategory = product.ClientCategory;
                                totalCashPrice += product.CashPrice;
                                if (totalCashPrice > int.MaxValue)
                                {
                                    purchaseValid = false;
                                    errorProduct = requestedProduct;
                                    break;
                                }

                                if (!itemCatalog.TryGetDefinition(
                                        product.ItemId,
                                        out var itemDefinition)
                                    || itemDefinition.InventoryCategory
                                            is not (ItemInventoryCategory.Avatar
                                                or ItemInventoryCategory.Creature)
                                        && !product.UsesMainInventory
                                    || itemDefinition.IsEquipment
                                        && product.Quantity != 1)
                                {
                                    purchaseValid = false;
                                    errorProduct = requestedProduct;
                                    break;
                                }

                                var isWarehouseUpgradeProduct =
                                    product.WarehouseUpgradeCapacity.HasValue
                                    || CharacterWarehouseProgression
                                        .TryGetItemTargetCapacity(
                                            product.ItemId,
                                            out _);
                                if (isWarehouseUpgradeProduct)
                                {
                                    var targetCapacity = product.WarehouseUpgradeCapacity
                                        ?? CharacterWarehouseProgression.ApplyItemTarget(
                                            plannedWarehouseCapacity,
                                            product.ItemId);
                                    // Each target-client STK carries an absolute capacity.
                                    // It is settled as an effect and never enters the bag.
                                    if (currentDungeonId != 0
                                        || product.Quantity != 1)
                                    {
                                        purchaseValid = false;
                                        errorProduct = requestedProduct;
                                        break;
                                    }

                                    plannedWarehouseCapacity = Math.Max(
                                        plannedWarehouseCapacity,
                                        targetCapacity);
                                    warehouseUpgraded = true;
                                    continue;
                                }

                                if (product.PremiumContract is { } contract)
                                {
                                    var grantedDays = (long)contract.Days
                                        * product.Quantity;
                                    if (grantedDays is <= 0 or > int.MaxValue)
                                    {
                                        purchaseValid = false;
                                        errorProduct = requestedProduct;
                                        break;
                                    }

                                    premiumServiceGrants.Add(new(
                                        contract.ServiceType,
                                        (int)grantedDays));
                                    continue;
                                }

                                if (product.DirectCurrencyGrant is { } currencyGrant)
                                {
                                    if (!CeraShopDirectCurrencyPlanner.TryApply(
                                            plannedGold,
                                            plannedVictoryPoints,
                                            currencyGrant,
                                            product.Quantity,
                                            out plannedGold,
                                            out plannedVictoryPoints))
                                    {
                                        purchaseValid = false;
                                        errorProduct = requestedProduct;
                                        break;
                                    }

                                    continue;
                                }

                                if (itemDefinition.CeraBooster is not null
                                    || itemDefinition.CeraPackage is not null)
                                {
                                    if (!CeraShopContentPlanner.TryCreatePurchase(
                                            plannedInventory,
                                            plannedAvatarInventory,
                                            plannedCreatureInventory,
                                            itemDefinition,
                                            product.Quantity,
                                            itemCatalog,
                                            GetInventoryWeightLimit(activeCharacter),
                                            GameRandomSource.Shared,
                                            rewardItemId =>
                                                CharacterWarehouseProgression
                                                    .TryGetItemTargetCapacity(
                                                        rewardItemId,
                                                        out _)
                                                || ceraShopCatalog.TryGetPremiumContract(
                                                    rewardItemId,
                                                    out _)
                                                || ceraShopCatalog.TryGetDirectCurrencyGrant(
                                                    rewardItemId,
                                                    out _),
                                            out var contentsPlan,
                                            out var contentsFailure))
                                    {
                                        purchaseValid = false;
                                        errorProduct = requestedProduct;
                                        logger.LogWarning(
                                            "Could not plan immediate Cera-shop container {ItemId}: {Failure}.",
                                            product.ItemId,
                                            contentsFailure);
                                        break;
                                    }

                                    var contentEffectsValid = true;
                                    foreach (var reward in contentsPlan.Rewards)
                                    {
                                        if (ceraShopCatalog.TryGetPremiumContract(
                                                reward.ItemId,
                                                out var rewardContract))
                                        {
                                            var grantedDays = (long)rewardContract.Days
                                                * reward.Count;
                                            if (grantedDays is <= 0 or > int.MaxValue)
                                            {
                                                contentEffectsValid = false;
                                                break;
                                            }

                                            premiumServiceGrants.Add(new(
                                                rewardContract.ServiceType,
                                                checked((int)grantedDays)));
                                        }
                                        else if (ceraShopCatalog.TryGetDirectCurrencyGrant(
                                                     reward.ItemId,
                                                     out var rewardCurrencyGrant)
                                            && !CeraShopDirectCurrencyPlanner.TryApply(
                                                plannedGold,
                                                plannedVictoryPoints,
                                                rewardCurrencyGrant,
                                                reward.Count,
                                                out plannedGold,
                                                out plannedVictoryPoints))
                                        {
                                            contentEffectsValid = false;
                                            break;
                                        }
                                    }

                                    if (!contentEffectsValid)
                                    {
                                        purchaseValid = false;
                                        errorProduct = requestedProduct;
                                        logger.LogWarning(
                                            "Could not settle immediate effects from Cera-shop container {ItemId}.",
                                            product.ItemId);
                                        break;
                                    }

                                    // Original Cera-shop processing expands both
                                    // [cera booster] and [cera package] during the
                                    // purchase transaction. The container itself is
                                    // therefore never inserted into the inventory.
                                    plannedInventory = contentsPlan.MainInventory;
                                    plannedAvatarInventory = contentsPlan.AvatarInventory;
                                    plannedCreatureInventory = contentsPlan.CreatureInventory;
                                    immediateContainerOverflow.AddRange(
                                        contentsPlan.OverflowItems);
                                    immediateContainerRewards.AddRange(contentsPlan.Rewards);
                                    purchaseReplyRewards[purchaseReplyIndex] = contentsPlan
                                        .Rewards
                                        .GroupBy(reward => reward.ItemId)
                                        .Select(group => new GameCeraShopReward(
                                            group.Key,
                                            checked((uint)group.Aggregate(
                                                0UL,
                                                (total, reward) => checked(
                                                    total + reward.Count)))))
                                        .ToArray();
                                    openedContainerCount = checked(
                                        openedContainerCount + product.Quantity);
                                    continue;
                                }

                                var purchasedItem = CharacterItemIdentity.Ensure(
                                    new CharacterMailAttachmentRecord(
                                    product.ItemId,
                                    product.Quantity,
                                    Durability: itemDefinition.InventoryCategory
                                        == ItemInventoryCategory.Equipment
                                            ? itemCatalog.GetInitialDurability(product.ItemId)
                                            : itemDefinition.IsEquipment
                                                ? (ushort)1_000
                                                : (ushort)0,
                                    SealState: CharacterItemSealing.GetInitialSealState(
                                        itemDefinition)),
                                    itemDefinition);
                                var placementFailure = InventoryPlacementFailure.None;
                                if (itemDefinition.InventoryCategory
                                        == ItemInventoryCategory.Avatar
                                    && CharacterAvatarInventoryPlanner.TryPlace(
                                        plannedAvatarInventory,
                                        purchasedItem,
                                        itemCatalog,
                                        out var avatarPurchasePlan,
                                        out placementFailure))
                                {
                                    plannedAvatarInventory = avatarPurchasePlan.Inventory;
                                    continue;
                                }

                                if (itemDefinition.InventoryCategory
                                        == ItemInventoryCategory.Creature
                                    && CharacterCreatureInventoryPlanner.TryPlace(
                                        plannedCreatureInventory,
                                        new CharacterItemRecord(
                                            ushort.MaxValue,
                                            purchasedItem.ItemId,
                                            purchasedItem.CountOrValue,
                                            purchasedItem.State,
                                            purchasedItem.Durability,
                                            purchasedItem.SealState,
                                            InstanceId: purchasedItem.InstanceId),
                                        itemCatalog,
                                        preferredEmptySlot: null,
                                        out var creaturePurchasePlan,
                                        out placementFailure))
                                {
                                    plannedCreatureInventory =
                                        creaturePurchasePlan.Inventory;
                                    continue;
                                }

                                if (itemDefinition.InventoryCategory
                                        is not (ItemInventoryCategory.Avatar
                                            or ItemInventoryCategory.Creature)
                                    && CharacterInventoryPlanner.TryPlace(
                                        plannedInventory,
                                        purchasedItem,
                                        itemCatalog,
                                        GetInventoryWeightLimit(activeCharacter),
                                        out var purchasePlan,
                                        out placementFailure))
                                {
                                    plannedInventory = purchasePlan.Inventory;
                                    continue;
                                }

                                if (placementFailure == InventoryPlacementFailure.InvalidItem)
                                {
                                    purchaseValid = false;
                                    errorProduct = requestedProduct;
                                    break;
                                }

                                overflowMails.AddRange(CreateOverflowMailRequests(
                                    CharacterInventoryPlanner.CreateMailAttachments(
                                        purchasedItem,
                                        itemCatalog),
                                    "Cera shop purchase"));
                            }

                            if (purchaseValid && immediateContainerRewards.Count > 0)
                            {
                                var capacityBeforeContents = plannedWarehouseCapacity;
                                var upgradeSettlement =
                                    CharacterWarehouseProgression.SettleUpgradeItems(
                                        plannedInventory.Values,
                                        warehouseInventory.Values,
                                        plannedWarehouseCapacity);
                                plannedInventory = upgradeSettlement.Inventory
                                    .ToDictionary(item => item.Slot);
                                plannedWarehouseCapacity = upgradeSettlement.Capacity;
                                foreach (var reward in immediateContainerRewards)
                                {
                                    plannedWarehouseCapacity =
                                        CharacterWarehouseProgression.ApplyItemTarget(
                                            plannedWarehouseCapacity,
                                            reward.ItemId);
                                }

                                warehouseUpgraded |= plannedWarehouseCapacity
                                    != capacityBeforeContents;
                                var containerOverflowMails = CreateOverflowMailRequests(
                                    immediateContainerOverflow.Where(item =>
                                        !CharacterWarehouseProgression
                                            .TryGetItemTargetCapacity(item.ItemId, out _)),
                                    "Cera shop container reward");
                                immediateContainerMailCount = containerOverflowMails.Length;
                                overflowMails.AddRange(containerOverflowMails);
                            }

                            if (!purchaseValid || currentCash < totalCashPrice)
                            {
                                var error = GameProtocolEngine.CreateCeraShopBuyError(
                                    errorCode: 4,
                                    errorClientCategory,
                                    errorProduct.CommodityNo);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                logger.LogWarning(
                                    "Rejected Cera-shop purchase commodity={CommodityNo}, products={Count}, cash={Cash}, price={Price}.",
                                    errorProduct.CommodityNo,
                                    requestedProducts.Length,
                                    currentCash,
                                    totalCashPrice);
                                continue;
                            }

                            var nextCash = currentCash - checked((int)totalCashPrice);
                            var savedCharacter =
                                await store.SaveCharacterCeraShopPurchaseAsync(
                                    accountName,
                                    activeCharacter.Id,
                                    nextCash,
                                    plannedGold,
                                    plannedInventory.Values,
                                    equippedInventory.Values,
                                    serverToken,
                                    overflowMails,
                                    plannedAvatarInventory.Values,
                                    plannedCreatureInventory.Values,
                                    premiumServiceGrants,
                                    victoryPoints: plannedVictoryPoints,
                                    expectedCash: currentCash,
                                    warehouseCapacity: plannedWarehouseCapacity,
                                    expectedWarehouseCapacity: warehouseCapacity);
                            if (savedCharacter is null)
                            {
                                var error = GameProtocolEngine.CreateCeraShopBuyError(
                                    errorCode: 4,
                                    errorClientCategory,
                                    errorProduct.CommodityNo);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            _ = AdoptSavedWarehouseUpgradeState(savedCharacter);
                            currentCash = nextCash;
                            currentGold = savedCharacter.Gold;
                            currentVictoryPoints = savedCharacter.VictoryPoints;
                            avatarInventory = (savedCharacter.AvatarInventory ?? [])
                                .ToDictionary(item => item.Slot);
                            creatureInventory = (savedCharacter.CreatureInventory ?? [])
                                .ToDictionary(item => item.Slot);
                            var accountCharacters = await store.GetCharactersAsync(
                                accountName,
                                serverToken);
                            foreach (var accountCharacter in accountCharacters.Where(
                                         character => character.Id != activeCharacter.Id))
                            {
                                characterSessions.NotifyCera(
                                    accountCharacter.Id,
                                    checked((uint)currentCash));
                            }
                            if (overflowMails.Count > 0)
                            {
                                characterSessions.NotifyMailReceived(activeCharacter.CharacterNo);
                            }

                            if (premiumServiceGrants.Count > 0)
                            {
                                var premiumStates = await store.GetPremiumServicesAsync(
                                    accountName,
                                    serverToken);
                                foreach (var serviceType in premiumServiceGrants
                                             .Select(grant => grant.ServiceType)
                                             .Distinct())
                                {
                                    var premiumState = premiumStates.FirstOrDefault(state =>
                                        state.ServiceType == serviceType);
                                    if (premiumState?.Active == true)
                                    {
                                        await SendPremiumServiceInfoAsync(
                                            serviceType,
                                            premiumState.RemainingSeconds);
                                        foreach (var accountCharacter in accountCharacters.Where(
                                                     character => character.Id
                                                         != activeCharacter.Id))
                                        {
                                            characterSessions.NotifyPremiumInfo(
                                                accountCharacter.Id,
                                                serviceType,
                                                premiumState.RemainingSeconds);
                                        }
                                    }
                                }
                            }
                            for (var index = 0; index < requestedProducts.Length; index++)
                            {
                                var requestedProduct = requestedProducts[index];
                                // The request option byte is commonly 0xFF
                                // and is not the category consumed by the
                                // command-67 response handler. Return the
                                // category of the resolved catalog product so
                                // the client closes and refreshes the correct
                                // Cera-shop panel.
                                _ = ceraShopCatalog.TryGetProduct(
                                    requestedProduct.CommodityNo,
                                    out var purchasedProduct);
                                var buyReply =
                                    GameProtocolEngine.CreateCeraShopBuyReply(
                                        purchasedProduct.ClientCategory,
                                        requestedProduct.CommodityNo,
                                        purchaseReplyRewards[index]);
                                await buyReply.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, buyReply);
                            }

                            if (warehouseUpgraded)
                            {
                                var warehouseRefresh = CreateWarehousePacket(
                                    warehouseInventory.Values,
                                    warehouseCapacity);
                                await warehouseRefresh.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, warehouseRefresh);
                            }

                            var mainRefresh = CreateMainInventoryPacket(
                                currentGold,
                                currentVictoryPoints,
                                mainInventory.Values);
                            await mainRefresh.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, mainRefresh);

                            var avatarRefresh = CreateAvatarInventoryPacket(
                                avatarInventory.Values);
                            await avatarRefresh.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, avatarRefresh);

                            foreach (var creatureRefresh in CreateCreatureInventoryPackets(
                                         creatureInventory.Values))
                            {
                                await creatureRefresh.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, creatureRefresh);
                            }

                            var cashUpdate = GameProtocolEngine.CreateCeraUpdate(
                                success: true,
                                value: (uint)currentCash);
                            await cashUpdate.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, cashUpdate);

                            // The command-67 result window is limited and does not
                            // replace the authoritative inventory refresh. Keep the
                            // chat entries so every reward and its full count remain
                            // visible even when a package contains many item kinds.
                            foreach (var rewardGroup in immediateContainerRewards
                                         .GroupBy(reward => reward.ItemId))
                            {
                                var rewardCount = rewardGroup.Aggregate(
                                    0UL,
                                    (total, reward) => checked(total + reward.Count));
                                var rewardName = itemCatalog.TryGetDefinition(
                                        rewardGroup.Key,
                                        out var rewardDefinition)
                                    && !string.IsNullOrWhiteSpace(rewardDefinition.Name)
                                        ? rewardDefinition.Name
                                        : $"item {rewardGroup.Key}";
                                await SendMessageNotificationAsync(
                                    0,
                                    ClientEncoding.GetBytes(
                                        $"获得[{rewardName}]{rewardCount}个"));
                            }

                            if (immediateContainerMailCount > 0)
                            {
                                await SendMessageNotificationAsync(
                                    0,
                                    ClientEncoding.GetBytes(
                                        "背包空间不足，部分奖励已发送至邮箱。"));
                            }

                            var purchasedProductSummary = string.Join(
                                ", ",
                                requestedProducts.Select(requestedProduct =>
                                    ceraShopCatalog.TryGetProduct(
                                        requestedProduct.CommodityNo,
                                        out var purchasedProduct)
                                        ? $"{purchasedProduct.CommodityNo}:item={purchasedProduct.ItemId}x{purchasedProduct.Quantity}@{purchasedProduct.CashPrice}"
                                        : $"{requestedProduct.CommodityNo}:unresolved"));
                            logger.LogInformation(
                                "Completed Cera-shop purchase [{Products}] ({Count} product(s)) for {Price}; cash={Cash}, gold={Gold}, victoryPoints={VictoryPoints}, openedContainers={OpenedContainerCount}, containerRewards={ContainerRewardCount}, PremiumGrants={PremiumGrantCount}, WarehouseCapacity={WarehouseCapacity}.",
                                purchasedProductSummary,
                                requestedProducts.Length,
                                totalCashPrice,
                                currentCash,
                                currentGold,
                                currentVictoryPoints,
                                openedContainerCount,
                                immediateContainerRewards.Count,
                                premiumServiceGrants.Count,
                                warehouseCapacity);
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.BuyItemCommand)
                        {
                            if (activeCharacter is null
                                || !TryReadBuyItem(
                                    request.Body,
                                    out var itemId,
                                    out var countOrQualitySeed))
                            {
                                var error = GameProtocolEngine.CreateCommandError(request, 4);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            if (!itemCatalog.TryGetDefinition(itemId, out var itemDefinition))
                            {
                                var error = GameProtocolEngine.CreateCommandError(request, 4);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            var count = itemDefinition.IsEquipment
                                ? 1u
                                : (uint)Math.Clamp(countOrQualitySeed, 1, 999);
                            if (!itemCatalog.TryGetPurchasePrice(itemId, out var unitPrice))
                            {
                                var error = GameProtocolEngine.CreateCommandError(request, 4);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                logger.LogWarning(
                                    "Rejected NPC-shop purchase of item {ItemId}: its cached item definition disappeared during request processing.",
                                    itemId);
                                continue;
                            }

                            var totalPrice = checked(unitPrice * (int)count);
                            if (currentGold < totalPrice)
                            {
                                var error = GameProtocolEngine.CreateCommandError(request, 4);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            var purchasedItem = CharacterItemIdentity.Ensure(
                                new CharacterMailAttachmentRecord(
                                itemId,
                                count,
                                Durability: itemDefinition.InventoryCategory
                                    == ItemInventoryCategory.Equipment
                                        ? itemCatalog.GetInitialDurability(itemId)
                                        : itemDefinition.IsEquipment
                                            ? (ushort)1_000
                                            : (ushort)0,
                                SealState: CharacterItemSealing.GetInitialSealState(
                                    itemDefinition),
                                EquipmentQualitySeed:
                                    itemDefinition.InventoryCategory
                                        == ItemInventoryCategory.Equipment
                                    && !itemDefinition.IsTitle
                                        ? EquipmentQuality.GetNpcShopSeed(
                                            DateTimeOffset.Now)
                                        : null),
                                itemDefinition);
                            var plannedInventory = mainInventory.ToDictionary(
                                pair => pair.Key,
                                pair => pair.Value);
                            var plannedAvatarInventory = avatarInventory.ToDictionary(
                                pair => pair.Key,
                                pair => pair.Value);
                            var plannedCreatureInventory = creatureInventory.ToDictionary(
                                pair => pair.Key,
                                pair => pair.Value);
                            var isWarehouseUpgradeItem =
                                CharacterWarehouseProgression.TryGetItemTargetCapacity(
                                    itemId,
                                    out var warehouseUpgradeTarget);
                            var plannedWarehouseCapacity = isWarehouseUpgradeItem
                                ? Math.Max(warehouseCapacity, warehouseUpgradeTarget)
                                : warehouseCapacity;
                            var overflowMails = Array.Empty<CreateCharacterMailRequest>();
                            ushort? destinationSlot = null;
                            var destinationListType = itemDefinition.InventoryCategory switch
                            {
                                ItemInventoryCategory.Avatar => (byte)1,
                                ItemInventoryCategory.Creature => (byte)7,
                                _ => (byte)0
                            };
                            var placementFailure = InventoryPlacementFailure.None;
                            if (isWarehouseUpgradeItem)
                            {
                                destinationSlot = null;
                            }
                            else if (itemDefinition.InventoryCategory
                                         == ItemInventoryCategory.Avatar
                                     && CharacterAvatarInventoryPlanner.TryPlace(
                                         plannedAvatarInventory,
                                         purchasedItem,
                                         itemCatalog,
                                         preferredEmptySlot: null,
                                         out var avatarPurchasePlan,
                                         out placementFailure))
                            {
                                plannedAvatarInventory = avatarPurchasePlan.Inventory;
                                destinationSlot = avatarPurchasePlan.DestinationSlot;
                            }
                            else if (itemDefinition.InventoryCategory
                                         == ItemInventoryCategory.Creature
                                     && CharacterCreatureInventoryPlanner.TryPlace(
                                         plannedCreatureInventory,
                                         new CharacterItemRecord(
                                             ushort.MaxValue,
                                             purchasedItem.ItemId,
                                             purchasedItem.CountOrValue,
                                             purchasedItem.State,
                                             purchasedItem.Durability,
                                             purchasedItem.SealState,
                                             InstanceId: purchasedItem.InstanceId),
                                         itemCatalog,
                                         preferredEmptySlot: null,
                                         out var creaturePurchasePlan,
                                         out placementFailure))
                            {
                                plannedCreatureInventory = creaturePurchasePlan.Inventory;
                                destinationSlot = creaturePurchasePlan.DestinationSlot;
                            }
                            else if (itemDefinition.InventoryCategory
                                         is not (ItemInventoryCategory.Avatar
                                             or ItemInventoryCategory.Creature)
                                     && CharacterInventoryPlanner.TryPlace(
                                     plannedInventory,
                                     purchasedItem,
                                     itemCatalog,
                                     GetInventoryWeightLimit(activeCharacter),
                                     out var purchasePlan,
                                     out placementFailure))
                            {
                                plannedInventory = purchasePlan.Inventory;
                                destinationSlot = purchasePlan.DestinationSlot;
                            }
                            else if (placementFailure is InventoryPlacementFailure.InvalidItem
                                     or InventoryPlacementFailure.UnsupportedCategory)
                            {
                                var error = GameProtocolEngine.CreateCommandError(request, 4);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }
                            else
                            {
                                overflowMails = CreateOverflowMailRequests(
                                    CharacterInventoryPlanner.CreateMailAttachments(
                                        purchasedItem,
                                        itemCatalog),
                                    "Town shop purchase");
                            }

                            var nextGold = currentGold - totalPrice;
                            var savedCharacter = await store.SaveCharacterItemSpacesAsync(
                                accountName,
                                activeCharacter.Id,
                                nextGold,
                                plannedInventory.Values,
                                equippedInventory.Values,
                                warehouseInventory.Values,
                                serverToken,
                                overflowMails,
                                avatarInventory: plannedAvatarInventory.Values,
                                creatureInventory: plannedCreatureInventory.Values,
                                warehouseCapacity: plannedWarehouseCapacity,
                                expectedWarehouseCapacity: warehouseCapacity);
                            if (savedCharacter is null)
                            {
                                var error = GameProtocolEngine.CreateCommandError(request, 4);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            var warehouseCapacityChanged =
                                AdoptSavedWarehouseUpgradeState(savedCharacter);
                            activeCharacter = savedCharacter;
                            mainInventory = (savedCharacter.Inventory ?? [])
                                .ToDictionary(item => item.Slot);
                            avatarInventory = (savedCharacter.AvatarInventory ?? [])
                                .ToDictionary(item => item.Slot);
                            creatureInventory = (savedCharacter.CreatureInventory ?? [])
                                .ToDictionary(item => item.Slot);
                            currentGold = nextGold;
                            if (overflowMails.Length > 0)
                            {
                                characterSessions.NotifyMailReceived(activeCharacter.CharacterNo);
                            }

                            var buyReply = GameProtocolEngine.CreateBuyItemReply(
                                updatedGold: (uint)currentGold,
                                updatedVictoryPoints: currentVictoryPoints,
                                auxiliaryItemSpaceValue: 0,
                                updatedCera: (uint)currentCash,
                                slot: destinationSlot ?? ushort.MaxValue,
                                itemId: purchasedItem.ItemId,
                                countOrValue: purchasedItem.CountOrValue);
                            await buyReply.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, buyReply);
                            var mainRefresh = CreateMainInventoryPacket(
                                currentGold,
                                currentVictoryPoints,
                                mainInventory.Values,
                                preferredLastSlot: destinationSlot);
                            await mainRefresh.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, mainRefresh);
                            if (destinationListType == 1)
                            {
                                var avatarRefresh = CreateAvatarInventoryPacket(
                                    avatarInventory.Values);
                                await avatarRefresh.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, avatarRefresh);
                            }
                            else if (destinationListType == 7)
                            {
                                foreach (var creatureRefresh in CreateCreatureInventoryPackets(
                                             creatureInventory.Values))
                                {
                                    await creatureRefresh.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, creatureRefresh);
                                }
                            }
                            if (warehouseCapacityChanged || isWarehouseUpgradeItem)
                            {
                                await SendWarehouseStateAsync();
                            }
                            logger.LogInformation(
                                "Bought item {ItemId} x{Count} for {UnitPrice} each ({TotalPrice} total); listType={ListType}, slot={Slot}, overflowMail={OverflowMail}, gold={Gold}",
                                itemId,
                                count,
                                unitPrice,
                                totalPrice,
                                destinationListType,
                                destinationSlot,
                                overflowMails.Length,
                                currentGold);
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.SellItemCommand)
                        {
                            if (activeCharacter is null
                                || !TryReadSellItem(
                                    request.Body,
                                    out var listType,
                                    out var slot,
                                    out var requestedCount)
                                || listType != 0
                                || !mainInventory.TryGetValue(slot, out var soldItem)
                                || !itemCatalog.TryCalculateSellPrice(
                                    soldItem.ItemId,
                                    soldItem.Durability,
                                    out var unitSellPrice))
                            {
                                var error = GameProtocolEngine.CreateCommandError(request, 0x11);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            var soldCount = (uint)Math.Clamp(
                                requestedCount,
                                1,
                                (int)Math.Min(soldItem.CountOrValue, int.MaxValue));
                            var remaining = soldItem.CountOrValue - soldCount;
                            if (remaining == 0)
                            {
                                mainInventory.Remove(slot);
                            }
                            else
                            {
                                mainInventory[slot] = soldItem with { CountOrValue = remaining };
                            }

                            currentGold = checked(currentGold
                                + unitSellPrice * (int)soldCount);
                            activeCharacter = await store.SaveCharacterInventoryAsync(
                                accountName,
                                activeCharacter.Id,
                                currentGold,
                                mainInventory.Values,
                                equippedInventory.Values,
                                serverToken) ?? activeCharacter;
                            var sellReply = GameProtocolEngine.CreateSellItemReply(
                                updatedGold: checked((uint)currentGold),
                                itemSpace: listType,
                                slot,
                                soldCount: checked((ushort)soldCount));
                            await sellReply.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, sellReply);
                            logger.LogInformation(
                                "Sold item {ItemId} x{Count} from slot {Slot}; unitPrice={UnitPrice}, durability={Durability}, gold={Gold}",
                                soldItem.ItemId,
                                soldCount,
                                slot,
                                unitSellPrice,
                                soldItem.Durability,
                                currentGold);
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId
                                == GameProtocolEngine.RepairEquipmentCommand)
                        {
                            if (activeCharacter is null
                                || !TryReadRepairEquipment(
                                    request.Body,
                                    out var itemSpace,
                                    out var repairSlot))
                            {
                                var error = GameProtocolEngine.CreateCommandError(
                                    request,
                                    0x11);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            var plannedMain = mainInventory.ToDictionary(
                                pair => pair.Key,
                                pair => pair.Value);
                            var plannedEquipment = equippedInventory.ToDictionary(
                                pair => pair.Key,
                                pair => pair.Value);
                            var repairTargets = new List<(
                                Dictionary<ushort, CharacterItemRecord> Space,
                                CharacterItemRecord Item,
                                ushort MaximumDurability,
                                int Price)>();
                            var inDungeonRepair = currentDungeonId != 0;

                            void AddRepairTarget(
                                Dictionary<ushort, CharacterItemRecord> space,
                                CharacterItemRecord item)
                            {
                                if (!itemCatalog.TryGetMaximumDurability(
                                        item.ItemId,
                                        out var maximumDurability)
                                    || item.Durability >= maximumDurability
                                    || !itemCatalog.TryCalculateRepairPrice(
                                        item.ItemId,
                                        item.Durability,
                                        inDungeonRepair,
                                        out var repairPrice))
                                {
                                    return;
                                }

                                repairTargets.Add((
                                    space,
                                    item,
                                    maximumDurability,
                                    repairPrice));
                            }

                            if (repairSlot == ushort.MaxValue)
                            {
                                foreach (var item in plannedEquipment.Values)
                                {
                                    AddRepairTarget(plannedEquipment, item);
                                }

                                foreach (var item in plannedMain.Values)
                                {
                                    AddRepairTarget(plannedMain, item);
                                }
                            }
                            else
                            {
                                var selectedSpace = itemSpace == 0
                                    ? plannedMain
                                    : plannedEquipment;
                                if (!selectedSpace.TryGetValue(
                                        repairSlot,
                                        out var selectedItem)
                                    || !itemCatalog.TryGetMaximumDurability(
                                        selectedItem.ItemId,
                                        out var maximumDurability)
                                    || selectedItem.Durability >= maximumDurability
                                    || !itemCatalog.TryCalculateRepairPrice(
                                        selectedItem.ItemId,
                                        selectedItem.Durability,
                                        inDungeonRepair,
                                        out var repairPrice))
                                {
                                    var error = GameProtocolEngine.CreateCommandError(
                                        request,
                                        0x11);
                                    await error.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, error);
                                    continue;
                                }

                                repairTargets.Add((
                                    selectedSpace,
                                    selectedItem,
                                    maximumDurability,
                                    repairPrice));
                            }

                            var totalRepairPrice = repairTargets.Sum(
                                target => (long)target.Price);
                            if (totalRepairPrice > currentGold)
                            {
                                var error = GameProtocolEngine.CreateCommandError(
                                    request,
                                    10);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            foreach (var target in repairTargets)
                            {
                                target.Space[target.Item.Slot] = target.Item with
                                {
                                    Durability = target.MaximumDurability
                                };
                            }

                            var nextGold = checked(currentGold - (int)totalRepairPrice);
                            var savedRepair = await store.SaveCharacterItemSpacesAsync(
                                accountName,
                                activeCharacter.Id,
                                nextGold,
                                plannedMain.Values,
                                plannedEquipment.Values,
                                warehouseInventory.Values,
                                serverToken,
                                avatarInventory: avatarInventory.Values,
                                creatureInventory: creatureInventory.Values);
                            if (savedRepair is null)
                            {
                                var error = GameProtocolEngine.CreateCommandError(
                                    request,
                                    4);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            activeCharacter = savedRepair;
                            currentGold = nextGold;
                            mainInventory = plannedMain;
                            equippedInventory = plannedEquipment;
                            var repairReply =
                                GameProtocolEngine.CreateRepairEquipmentReply(
                                    checked((uint)currentGold),
                                    itemSpace,
                                    repairSlot);
                            await repairReply.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, repairReply);
                            logger.LogInformation(
                                "Repaired {RepairCount} equipment item(s) at {ItemSpace}:{Slot}; cost={Cost}, gold={Gold}, dungeonRate={DungeonRate}",
                                repairTargets.Count,
                                itemSpace,
                                repairSlot,
                                totalRepairPrice,
                                currentGold,
                                inDungeonRepair);
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.UpgradeItemCommand)
                        {
                            if (activeCharacter is null
                                || currentDungeonId != 0
                                || !GameProtocolEngine.TryParseUpgradeItemCommand(
                                    request.Body,
                                    out var upgradeCommand))
                            {
                                var error = GameProtocolEngine.CreateCommandError(
                                    request,
                                    (byte)EquipmentReinforcementFailure.InvalidTarget);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            if (!EquipmentReinforcementPlanner.TryCreate(
                                    mainInventory,
                                    equippedInventory,
                                    currentGold,
                                    upgradeCommand.TargetSlot,
                                    upgradeCommand.TargetItemId,
                                    upgradeCommand.MaterialSlot,
                                    itemCatalog,
                                    reinforcementCatalog,
                                    GameRandomSource.Shared,
                                    out var reinforcementPlan,
                                    out var reinforcementFailure))
                            {
                                var error = GameProtocolEngine.CreateCommandError(
                                    request,
                                    (byte)reinforcementFailure);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                logger.LogWarning(
                                    "Rejected equipment reinforcement for item {ItemId} at slot {Slot} with material slot {MaterialSlot}: {Failure}.",
                                    upgradeCommand.TargetItemId,
                                    upgradeCommand.TargetSlot,
                                    upgradeCommand.MaterialSlot,
                                    reinforcementFailure);
                                continue;
                            }

                            var savedReinforcement =
                                await store.SaveCharacterInventoryAsync(
                                    accountName,
                                    activeCharacter.Id,
                                    reinforcementPlan.RemainingGold,
                                    reinforcementPlan.MainInventory.Values,
                                    reinforcementPlan.Equipment.Values,
                                    serverToken);
                            if (savedReinforcement is null)
                            {
                                var error = GameProtocolEngine.CreateCommandError(
                                    request,
                                    (byte)EquipmentReinforcementFailure.InvalidTarget);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            var warehouseCapacityChanged =
                                AdoptSavedWarehouseUpgradeState(savedReinforcement);
                            equippedInventory = (savedReinforcement.Equipment ?? [])
                                .ToDictionary(item => item.Slot);
                            currentGold = savedReinforcement.Gold;
                            if (warehouseCapacityChanged)
                            {
                                await SendWarehouseStateAsync();
                            }

                            // DF2008 refreshes a bag target before consuming the
                            // CMD83 acknowledgement. Worn targets are updated by
                            // the acknowledgement itself.
                            if (reinforcementPlan.TargetListType == 0)
                            {
                                var inventoryRefresh = CreateMainInventoryPacket(
                                    currentGold,
                                    currentVictoryPoints,
                                    mainInventory.Values,
                                    preferredLastSlot: reinforcementPlan.TargetSlot);
                                await inventoryRefresh.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, inventoryRefresh);
                            }

                            var reinforcementReply =
                                GameProtocolEngine.CreateUpgradeItemReply(
                                    reinforcementPlan.MaterialSlot,
                                    reinforcementPlan.MaterialRemaining,
                                    reinforcementPlan.ResultCode,
                                    reinforcementPlan.NewLevel,
                                    reinforcementPlan.TargetSlot);
                            await reinforcementReply.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, reinforcementReply);
                            logger.LogInformation(
                                "Reinforced item {ItemId} at {ListType}:{Slot}: level={PreviousLevel}->{NewLevel}, result={ResultCode}, destroyed={Destroyed}, material={MaterialItemId}x{MaterialCount}, goldCost={GoldCost}, gold={Gold}, roll={Roll}/{RollUpperBound}.",
                                reinforcementPlan.TargetItemId,
                                reinforcementPlan.TargetListType,
                                reinforcementPlan.TargetSlot,
                                reinforcementPlan.PreviousLevel,
                                reinforcementPlan.NewLevel,
                                reinforcementPlan.ResultCode,
                                reinforcementPlan.Destroyed,
                                reinforcementPlan.Cost.MaterialItemId,
                                reinforcementPlan.Cost.MaterialCount,
                                reinforcementPlan.Cost.Gold,
                                currentGold,
                                reinforcementPlan.FailureRoll,
                                EquipmentReinforcementCatalog.FailureRollUpperBound);
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId
                                == GameProtocolEngine.ResetItemAttributeCommand)
                        {
                            if (activeCharacter is null
                                || currentDungeonId != 0
                                || !GameProtocolEngine.TryParseResetItemAttributeCommand(
                                    request.Body,
                                    out var resetCommand))
                            {
                                var error =
                                    GameProtocolEngine.CreateResetItemAttributeFailure(
                                        (byte)ItemAttributeModificationFailure.InvalidTarget);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            if (!ItemAttributeModificationPlanner.TryCreate(
                                    mainInventory,
                                    equippedInventory,
                                    resetCommand.TargetSlot,
                                    resetCommand.TargetItemId,
                                    resetCommand.MaterialSlot,
                                    itemCatalog,
                                    resealCatalog,
                                    GameRandomSource.Shared,
                                    out var modificationPlan,
                                    out var modificationFailure))
                            {
                                var error =
                                    GameProtocolEngine.CreateResetItemAttributeFailure(
                                        (byte)modificationFailure);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                logger.LogWarning(
                                    "Rejected item-attribute modification for item {ItemId} at slot {Slot} with material slot {MaterialSlot}: {Failure}.",
                                    resetCommand.TargetItemId,
                                    resetCommand.TargetSlot,
                                    resetCommand.MaterialSlot,
                                    modificationFailure);
                                continue;
                            }

                            var savedModification =
                                await store.CommitItemAttributeModificationAsync(
                                    accountName,
                                    activeCharacter.Id,
                                    mainInventory.Values,
                                    equippedInventory.Values,
                                    modificationPlan.MainInventory.Values,
                                    modificationPlan.Equipment.Values,
                                    serverToken);
                            if (savedModification is null)
                            {
                                var error =
                                    GameProtocolEngine.CreateResetItemAttributeFailure(
                                        (byte)ItemAttributeModificationFailure.InvalidTarget);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            var warehouseCapacityChanged =
                                AdoptSavedWarehouseUpgradeState(savedModification);
                            equippedInventory = (savedModification.Equipment ?? [])
                                .ToDictionary(item => item.Slot);
                            if (warehouseCapacityChanged)
                            {
                                await SendWarehouseStateAsync();
                            }

                            var modificationReply =
                                GameProtocolEngine.CreateResetItemAttributeReply(
                                    modificationPlan.MaterialSlot,
                                    modificationPlan.MaterialRemaining,
                                    (ushort)modificationPlan.Operation);
                            await modificationReply.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, modificationReply);

                            var adjustedItem = modificationPlan.TargetListType == 3
                                ? equippedInventory[modificationPlan.TargetSlot]
                                : mainInventory[modificationPlan.TargetSlot];
                            var targetUpdate =
                                GameProtocolEngine.CreateUpdateItemList(
                                    modificationPlan.TargetListType,
                                    [ToGameInventoryEntry(adjustedItem)]);
                            await targetUpdate.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, targetUpdate);

                            logger.LogInformation(
                                "Completed item-attribute operation {Operation} for item {ItemId} at {ListType}:{Slot}: material={MaterialItemId}x{MaterialConsumed}, remaining={MaterialRemaining}, quality={PreviousSeed}->{NewSeed}, reseals={PreviousResealCount}->{NewResealCount}.",
                                modificationPlan.Operation,
                                modificationPlan.TargetItemId,
                                modificationPlan.TargetListType,
                                modificationPlan.TargetSlot,
                                modificationPlan.MaterialItemId,
                                modificationPlan.MaterialConsumed,
                                modificationPlan.MaterialRemaining,
                                modificationPlan.PreviousQualitySeed,
                                modificationPlan.NewQualitySeed,
                                modificationPlan.PreviousResealCount,
                                modificationPlan.NewResealCount);
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.CompoundAvatarCommand)
                        {
                            if (activeCharacter is null
                                || currentDungeonId != 0
                                || !GameProtocolEngine.TryParseCompoundAvatarCommand(
                                    request.Body,
                                    out var compoundCommand))
                            {
                                var error =
                                    GameProtocolEngine.CreateCompoundAvatarFailure();
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            if (!AvatarCompoundPlanner.TryCreate(
                                    mainInventory,
                                    avatarInventory,
                                    activeCharacter.Job,
                                    compoundCommand.MaterialSlot,
                                    compoundCommand.FirstAvatarItemId,
                                    compoundCommand.SecondAvatarItemId,
                                    compoundCommand.SelectedOption,
                                    compoundCommand.FirstAvatarSlot,
                                    compoundCommand.SecondAvatarSlot,
                                    avatarCompoundCatalog,
                                    GameRandomSource.Shared,
                                    out var compoundPlan,
                                    out var compoundFailure))
                            {
                                var error =
                                    GameProtocolEngine.CreateCompoundAvatarFailure();
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                logger.LogWarning(
                                    "Rejected avatar compound for job {Job}, material slot {MaterialSlot}, items {FirstItemId}/{SecondItemId}, slots {FirstSlot}/{SecondSlot}, option {Option}: {Failure}.",
                                    activeCharacter.Job,
                                    compoundCommand.MaterialSlot,
                                    compoundCommand.FirstAvatarItemId,
                                    compoundCommand.SecondAvatarItemId,
                                    compoundCommand.FirstAvatarSlot,
                                    compoundCommand.SecondAvatarSlot,
                                    compoundCommand.SelectedOption,
                                    compoundFailure);
                                continue;
                            }

                            var savedCompound =
                                await store.SaveCharacterItemSpacesAsync(
                                    accountName,
                                    activeCharacter.Id,
                                    currentGold,
                                    compoundPlan.MainInventory.Values,
                                    equippedInventory.Values,
                                    warehouseInventory.Values,
                                    serverToken,
                                    avatarInventory: compoundPlan.AvatarInventory.Values,
                                    creatureInventory: creatureInventory.Values);
                            if (savedCompound is null)
                            {
                                var error =
                                    GameProtocolEngine.CreateCompoundAvatarFailure();
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            var warehouseCapacityChanged =
                                AdoptSavedWarehouseUpgradeState(savedCompound);
                            equippedInventory = (savedCompound.Equipment ?? [])
                                .ToDictionary(item => item.Slot);
                            avatarInventory = (savedCompound.AvatarInventory ?? [])
                                .ToDictionary(item => item.Slot);
                            creatureInventory = (savedCompound.CreatureInventory ?? [])
                                .ToDictionary(item => item.Slot);
                            currentGold = savedCompound.Gold;
                            if (warehouseCapacityChanged)
                            {
                                await SendWarehouseStateAsync();
                            }

                            var compoundReply =
                                GameProtocolEngine.CreateCompoundAvatarReply(
                                    [
                                        new GameAvatarCompoundConsumption(
                                            0,
                                            compoundPlan.MaterialSlot,
                                            compoundPlan.MaterialRemaining),
                                        new GameAvatarCompoundConsumption(
                                            1,
                                            compoundPlan.RequestedFirstSlot,
                                            0),
                                        new GameAvatarCompoundConsumption(
                                            1,
                                            compoundPlan.RequestedSecondSlot,
                                            0)
                                    ],
                                    compoundPlan.ResolvedFirstSlot,
                                    compoundPlan.ResultItemId,
                                    remainingSeconds: 0,
                                    selectedOption: compoundPlan.SelectedOption);
                            await compoundReply.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, compoundReply);

                            var inventoryRefresh = CreateMainInventoryPacket(
                                currentGold,
                                currentVictoryPoints,
                                mainInventory.Values);
                            await inventoryRefresh.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, inventoryRefresh);

                            var avatarRefresh =
                                CreateAvatarInventoryPacket(avatarInventory.Values);
                            await avatarRefresh.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, avatarRefresh);
                            logger.LogInformation(
                                "Compounded avatars for job {Job}, part {Part}: items {FirstItemId}/{SecondItemId} at {FirstSlot}/{SecondSlot} -> {ResultItemId} at {ResultSlot}, option={Option}, rare={Rare}, rate={RareRate}/{RateUpperBound}, material={MaterialItemId}x{MaterialCount}, remaining={MaterialRemaining}.",
                                activeCharacter.Job,
                                compoundPlan.ResultPart,
                                compoundCommand.FirstAvatarItemId,
                                compoundCommand.SecondAvatarItemId,
                                compoundPlan.ResolvedFirstSlot,
                                compoundPlan.ResolvedSecondSlot,
                                compoundPlan.ResultItemId,
                                compoundPlan.ResolvedFirstSlot,
                                compoundPlan.SelectedOption,
                                compoundPlan.IsRare,
                                compoundPlan.RareRate,
                                AvatarCompoundCatalog.RareRateUpperBound,
                                compoundPlan.MaterialItemId,
                                compoundPlan.MaterialConsumed,
                                compoundPlan.MaterialRemaining);
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.SetItemTradeStateCommand)
                        {
                            if (activeCharacter is null
                                || !TryReadSetItemTradeState(
                                    request.Body,
                                    out var listType,
                                    out var slot,
                                    out var state)
                                || !TryGetInventorySpace(
                                    listType,
                                    mainInventory,
                                    avatarInventory,
                                    equippedInventory,
                                    warehouseInventory,
                                    out var itemSpace)
                                || !itemSpace.TryGetValue(slot, out var item)
                                || !itemCatalog.TryCalculateSellPrice(
                                    item.ItemId,
                                    item.Durability,
                                    out var unitSellPrice))
                            {
                                var error = GameProtocolEngine.CreateCommandError(request, 0x11);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            var soldCount = Math.Min(
                                item.CountOrValue,
                                state == 0 ? 1u : state);
                            var remaining = item.CountOrValue - soldCount;
                            if (remaining == 0)
                            {
                                itemSpace.Remove(slot);
                            }
                            else
                            {
                                itemSpace[slot] = item with
                                {
                                    CountOrValue = remaining
                                };
                            }

                            currentGold = checked(
                                currentGold
                                + unitSellPrice * (int)soldCount);
                            activeCharacter = await store.SaveCharacterItemSpacesAsync(
                                accountName,
                                activeCharacter.Id,
                                currentGold,
                                mainInventory.Values,
                                equippedInventory.Values,
                                warehouseInventory.Values,
                                serverToken,
                                avatarInventory: avatarInventory.Values) ?? activeCharacter;

                            var stateReply =
                                GameProtocolEngine.CreateSetItemTradeStateReply(
                                    item.ItemId,
                                    listType);
                            await stateReply.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, stateReply);

                            var sellReply = GameProtocolEngine.CreateSellItemReply(
                                updatedGold: checked((uint)currentGold),
                                itemSpace: listType,
                                slot,
                                soldCount: checked((ushort)soldCount));
                            await sellReply.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, sellReply);

                            logger.LogInformation(
                                "Sold item {ItemId} x{SoldCount} through item-trade state at {ListType}:{Slot}; unitPrice={UnitPrice}, durability={Durability}, gold={Gold}",
                                item.ItemId,
                                soldCount,
                                listType,
                                slot,
                                unitSellPrice,
                                item.Durability,
                                currentGold);
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.DropItemCommand)
                        {
                            var replyItemSpace = request.Body.Length > 6
                                ? request.Body[6]
                                : (byte)0;
                            if (!GameProtocolEngine.TryParseDropItemCommand(
                                    request.Body,
                                    out var dropCommand))
                            {
                                var error =
                                    GameProtocolEngine.CreateDropItemErrorReply(
                                        17,
                                        replyItemSpace);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            replyItemSpace = dropCommand.ItemSpace;
                            if (activeCharacter is null || currentDungeonId == 0)
                            {
                                var error =
                                    GameProtocolEngine.CreateDropItemErrorReply(
                                        19,
                                        replyItemSpace);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            IReadOnlyDictionary<ushort, CharacterItemRecord>
                                sourceInventory = dropCommand.ItemSpace switch
                                {
                                    0 => mainInventory,
                                    7 => creatureInventory,
                                    // The planner rejects unsupported spaces before
                                    // consulting this fallback dictionary.
                                    _ => mainInventory
                                };
                            if (!DungeonItemDropPlanner.TryPlan(
                                    sourceInventory,
                                    dropCommand.ItemSpace,
                                    dropCommand.Slot,
                                    dropCommand.Count,
                                    itemCatalog,
                                    out var dropPlan,
                                    out var dropFailure))
                            {
                                var errorCode = dropFailure is
                                    DungeonItemDropFailure.NotTradeable
                                        or DungeonItemDropFailure.InvalidItem
                                        or DungeonItemDropFailure.UnsupportedItemSpace
                                    ? (byte)23
                                    : (byte)17;
                                var error =
                                    GameProtocolEngine.CreateDropItemErrorReply(
                                        errorCode,
                                        replyItemSpace);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                logger.LogInformation(
                                    "Rejected dungeon item drop from {ItemSpace}:{Slot} x{Count}: {Failure}.",
                                    dropCommand.ItemSpace,
                                    dropCommand.Slot,
                                    dropCommand.Count,
                                    dropFailure);
                                continue;
                            }

                            DungeonGroundItem groundItem;
                            try
                            {
                                groundItem = groundDungeonItems.AddPlayerDropped(
                                    dropPlan.DroppedItem,
                                    localUserId,
                                    currentDungeonRoomX,
                                    currentDungeonRoomY,
                                    dropCommand.X,
                                    dropCommand.Y);
                            }
                            catch (InvalidOperationException exception)
                            {
                                logger.LogWarning(
                                    exception,
                                    "Could not allocate a ground id for item {ItemId}.",
                                    dropPlan.DroppedItem.ItemId);
                                var error =
                                    GameProtocolEngine.CreateDropItemErrorReply(
                                        19,
                                        replyItemSpace);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            var plannedMainInventory = dropPlan.ItemSpace == 0
                                ? dropPlan.Inventory
                                : mainInventory;
                            var plannedCreatureInventory = dropPlan.ItemSpace == 7
                                ? dropPlan.Inventory
                                : creatureInventory;
                            var savedDrop = await store.SaveCharacterItemSpacesAsync(
                                accountName,
                                activeCharacter.Id,
                                currentGold,
                                plannedMainInventory.Values,
                                equippedInventory.Values,
                                warehouseInventory.Values,
                                serverToken,
                                avatarInventory: avatarInventory.Values,
                                creatureInventory: plannedCreatureInventory.Values);
                            if (savedDrop is null)
                            {
                                groundDungeonItems.Remove(groundItem.GroundId);
                                var error =
                                    GameProtocolEngine.CreateDropItemErrorReply(
                                        19,
                                        replyItemSpace);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            activeCharacter = savedDrop;
                            mainInventory = plannedMainInventory;
                            creatureInventory = plannedCreatureInventory;

                            var dropNotification = GameProtocolEngine.CreateDropItem(
                                localUserId,
                                dropCommand.X,
                                dropCommand.Y,
                                new GameDungeonDrop(
                                    groundItem.GroundId,
                                    groundItem.ItemId,
                                    groundItem.GetClientAddInfo(itemCatalog),
                                    groundItem.GetClientDurability(itemCatalog),
                                    groundItem.OwnerUserId,
                                    groundItem.GetClientItemAttr(itemCatalog)));
                            await dropNotification.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, dropNotification);

                            var dropReply = GameProtocolEngine.CreateDropItemReply(
                                dropCommand.ItemSpace,
                                dropCommand.Slot,
                                dropCommand.Count);
                            await dropReply.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, dropReply);
                            logger.LogInformation(
                                "Dropped item {ItemId} x{Count} from {ItemSpace}:{Slot} as ground item {GroundId} in dungeon {DungeonId} room ({RoomX},{RoomY}).",
                                groundItem.ItemId,
                                groundItem.CountOrValue,
                                dropCommand.ItemSpace,
                                dropCommand.Slot,
                                groundItem.GroundId,
                                currentDungeonId,
                                currentDungeonRoomX,
                                currentDungeonRoomY);
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId
                                == GameProtocolEngine.DecreaseDurabilityCommand)
                        {
                            if (activeCharacter is null
                                || currentDungeonId == 0
                                || !TryReadDecreaseDurability(
                                    request.Body,
                                    out var equipmentSlot)
                                || equipmentSlot is < 9 or >= 23
                                || !equippedInventory.TryGetValue(
                                    equipmentSlot,
                                    out var usedEquipment)
                                || !itemCatalog.TryGetMaximumDurability(
                                    usedEquipment.ItemId,
                                    out _))
                            {
                                var error = GameProtocolEngine.CreateCommandError(
                                    request,
                                    17);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            if (usedEquipment.Durability == 0)
                            {
                                var error = GameProtocolEngine.CreateCommandError(
                                    request,
                                    22);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            var plannedEquipment = equippedInventory.ToDictionary(
                                pair => pair.Key,
                                pair => pair.Value);
                            plannedEquipment[equipmentSlot] = usedEquipment with
                            {
                                Durability = checked((ushort)(
                                    usedEquipment.Durability - 1))
                            };
                            var savedDurability =
                                await store.SaveCharacterItemSpacesAsync(
                                    accountName,
                                    activeCharacter.Id,
                                    currentGold,
                                    mainInventory.Values,
                                    plannedEquipment.Values,
                                    warehouseInventory.Values,
                                    serverToken,
                                    avatarInventory: avatarInventory.Values,
                                    creatureInventory: creatureInventory.Values);
                            if (savedDurability is null)
                            {
                                var error = GameProtocolEngine.CreateCommandError(
                                    request,
                                    4);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            activeCharacter = savedDurability;
                            equippedInventory = plannedEquipment;
                            var durabilityReply =
                                GameProtocolEngine.CreateDecreaseDurabilityReply(
                                    equipmentSlot);
                            await durabilityReply.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, durabilityReply);
                            logger.LogDebug(
                                "Decreased equipped item {ItemId} durability at slot {Slot}: {OldDurability}->{NewDurability}",
                                usedEquipment.ItemId,
                                equipmentSlot,
                                usedEquipment.Durability,
                                usedEquipment.Durability - 1);
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.SetUserPositionCommand)
                        {
                            // DNF 1.0.1.9 SET_USER_POSITION is:
                            // sequence, int16 x, int16 y, byte direction,
                            // uint16 movement value. Establishing AREA_USERS
                            // here is what enables the local map's town-exit
                            // trigger to emit SET_USER_AREA.
                            if (request.Body.Length >= 9)
                            {
                                currentX = BinaryPrimitives.ReadInt16LittleEndian(
                                    request.Body.AsSpan(2, sizeof(short)));
                                currentY = BinaryPrimitives.ReadInt16LittleEndian(
                                    request.Body.AsSpan(4, sizeof(short)));
                                currentDirection = request.Body[6];
                                currentMovementValue = BinaryPrimitives.ReadUInt16LittleEndian(
                                    request.Body.AsSpan(7, sizeof(ushort)));
                            }

                            // Movement is only mirrored while the actor is a
                            // town entity; dungeon-run positions stay private.
                            if (currentDungeonId == 0 && !inDungeonSelection)
                            {
                                partyCoordinator.UpdatePresencePosition(
                                    runtimeSession.Id,
                                    currentX,
                                    currentY,
                                    currentDirection);
                            }

                            // The local client already owns its movement.
                            // Re-sending USER_POSITION or AREA_USERS on every
                            // one-second update makes it rebuild the area and
                            // snaps the character back. Seed the roster once
                            // when entering the town session, then only retain
                            // subsequent coordinates server-side.
                            if (!areaRosterInitialized)
                            {
                                var townUser = new GameTownUser(
                                    localUserId,
                                    currentX,
                                    currentY,
                                    currentDirection);

                                var areaUsers = GameProtocolEngine.CreateAreaUsers(
                                    currentTownId,
                                    currentAreaId,
                                    [townUser]);
                                await areaUsers.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, areaUsers);
                                areaRosterInitialized = true;
                                await SendTownFatigueStateAsync();
                                await SendTownAppearanceAsync(waitForTownActor: true);
                                await SendEnterGameWorldCompleteAsync();
                                PublishTownPresence();
                            }
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.SetUserAreaCommand)
                        {
                            // sequence, byte town, byte area, int16 x, int16 y
                            if (request.Body.Length >= 8)
                            {
                                currentTownId = request.Body[2];
                                currentAreaId = request.Body[3];
                                currentX = BinaryPrimitives.ReadInt16LittleEndian(
                                    request.Body.AsSpan(4, sizeof(short)));
                                currentY = BinaryPrimitives.ReadInt16LittleEndian(
                                    request.Body.AsSpan(6, sizeof(short)));
                            }

                            await SaveCurrentLocationAsync(serverToken);

                            var townUser = new GameTownUser(
                                localUserId,
                                currentX,
                                currentY,
                                currentDirection);
                            var userArea = GameProtocolEngine.CreateUserArea(
                                currentTownId,
                                currentAreaId,
                                townUser);
                            await userArea.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, userArea);

                            // USER_AREA deliberately ignores the local user;
                            // AREA_USERS commits the local scene transition but
                            // recreates the town actor with stamina zero. Rebuild
                            // its projections, then restore stamina through the
                            // client's silent NOTI 33 path.
                            var areaUsers = GameProtocolEngine.CreateAreaUsers(
                                currentTownId,
                                currentAreaId,
                                [townUser]);
                            await areaUsers.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, areaUsers);
                            areaRosterInitialized = true;
                            await SendTownFatigueStateAsync();
                            await SendTownAppearanceAsync(waitForTownActor: true);
                            await SendSilentTownStaminaStateAsync();
                            await SendEnterGameWorldCompleteAsync();
                            PublishTownPresence();
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.RecoverStaminaCommand)
                        {
                            if (activeCharacter is null
                                || currentDungeonId != 0
                                || inDungeonSelection)
                            {
                                var invalidState =
                                    GameProtocolEngine.CreateRecoverStaminaError(4);
                                await invalidState.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, invalidState);
                                continue;
                            }

                            var recoveryCost = staminaRecoveryCatalog.GetCost(
                                activeCharacter.Level);
                            var recovery = await store.RecoverCharacterWeaknessAsync(
                                activeCharacter.Id,
                                recoveryCost,
                                serverToken);
                            if (!recovery.Success || recovery.Character is null)
                            {
                                var errorCode = recovery.Failure switch
                                {
                                    WeaknessRecoveryFailure.NotWeakened => (byte)18,
                                    WeaknessRecoveryFailure.InsufficientGold => (byte)22,
                                    _ => (byte)4
                                };
                                var error =
                                    GameProtocolEngine.CreateRecoverStaminaError(errorCode);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                logger.LogInformation(
                                    "Rejected stamina recovery for character {CharacterId}: failure={Failure}, cost={Cost}, gold={Gold}.",
                                    activeCharacter.Id,
                                    recovery.Failure,
                                    recoveryCost,
                                    currentGold);
                                continue;
                            }

                            activeCharacter = recovery.Character;
                            currentGold = activeCharacter.Gold;
                            var recoveryReply =
                                GameProtocolEngine.CreateRecoverStaminaReply(currentGold);
                            await recoveryReply.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, recoveryReply);

                            var fullStamina = GameProtocolEngine.CreateStamina(
                                DungeonWeaknessPolicy.FullStamina);
                            await fullStamina.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, fullStamina);
                            logger.LogInformation(
                                "Recovered character {CharacterId} from weakness for {Cost} gold; remaining gold={Gold}.",
                                activeCharacter.Id,
                                recoveryCost,
                                currentGold);
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.StartGameCommand)
                        {
                            // Walking into a [dungeon gate] emits command 15.
                            // DF2008 handles exhausted party members in this command,
                            // before entering the dungeon-selection screen.
                            var maximumFatigue = HasActivePremiumService(
                                    GameProtocolEngine.BlackDiamondServiceType)
                                ? GameProtocolEngine.BlackDiamondMaximumFatigue
                                : GameProtocolEngine.DefaultMaximumFatigue;
                            var requiresTutorial = activeCharacter is { TutorialStarted: false };
                            if (activeCharacter is null
                                || (!requiresTutorial
                                    && !DungeonFatigueRules.CanEnterNewDungeon(
                                        activeCharacter.UsedFatigue,
                                        maximumFatigue)))
                            {
                                var fatigueError = GameProtocolEngine
                                    .CreateEnterDungeonSelectionInsufficientFatigueReply();
                                await fatigueError.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, fatigueError);
                                logger.LogInformation(
                                    "Rejected dungeon-selection entry for user {UserId}: used fatigue {UsedFatigue}/{MaximumFatigue}.",
                                    localUserId,
                                    activeCharacter?.UsedFatigue ?? 0,
                                    maximumFatigue);
                                continue;
                            }

                            // DFLegacy transitions to dungeon selection after state=1,
                            // the UDP peer table, local party-host index, and
                            // notification 27's compact solo-session body.
                            dungeonReturnTownId = currentTownId;
                            dungeonReturnAreaId = currentAreaId;
                            dungeonReturnX = currentX;
                            dungeonReturnY = currentY;
                            dungeonReturnDirection = currentDirection;
                            inDungeonSelection = true;
                            selectionWorldMap = worldMapCatalog.TryGetForGate(
                                currentTownId, currentAreaId, out var gateWorldMap)
                                ? gateWorldMap : null;
                            // Return before DF2008 auto-selects at 30.3 seconds.
                            dungeonSelectionDeadline = requiresTutorial
                                ? null : DateTimeOffset.UtcNow.AddSeconds(30);
                            await SaveCurrentLocationAsync(serverToken);
                            var dungeonState = GameProtocolEngine.CreateUserState(
                                localUserId,
                                state: 1);
                            await dungeonState.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, dungeonState);

                            var peerInfo = gameplayDatagram.TryGetPeerInfo(
                                runtimeSession.Id,
                                localUserId,
                                accountId: 1,
                                out var localPeer)
                                    ? new[] { localPeer }
                                    : [];
                            var udpPeers = GameProtocolEngine.CreateUdpPeerInfo(peerInfo);
                            await udpPeers.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, udpPeers);

                            var udpHost = GameProtocolEngine.CreateUdpHost();
                            await udpHost.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, udpHost);

                            await SendDungeonPermissionsAsync();

                            await SendDungeonSelectionEligibilityAsync();
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.BackToVillageCommand)
                        {
                            if (activeCharacter is { TutorialStarted: false })
                            {
                                logger.LogInformation(
                                    "Ignored return from dungeon selection for {CharacterId} until the tutorial is complete.",
                                    activeCharacter.Id);
                                continue;
                            }

                            // The DFLegacy dungeon-selection screen uses command
                            // 142 instead of the in-dungeon GIVEUP_GAME command.
                            await ReturnToTownFromDungeonAsync("back-to-village");
                            continue;
                        }

                        // -----------------------------------------------------
                        // Party block (CMD 10-14). The client sends a two-byte
                        // sequence number before every command body, so the
                        // parsers below read fields from offset 2.
                        // -----------------------------------------------------
                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId is >= 10 and <= 14)
                        {
                            var partyIdentity = activeCharacter is null
                                ? null
                                : new PartyMemberIdentity(
                                    runtimeSession.Id,
                                    activeCharacter.Id,
                                    accountUid,
                                    localUserId,
                                    activeCharacter.Name,
                                    (byte)Math.Clamp(activeCharacter.Level, (byte)1, byte.MaxValue),
                                    (byte)Math.Clamp(activeCharacter.Job, byte.MinValue, byte.MaxValue));
                            if (partyIdentity is null)
                            {
                                // Padded so the client's error-code readers
                                // stay inside the payload (see
                                // CreatePeerCommandError).
                                var identityError = GameProtocolEngine.CreatePeerCommandError(
                                    request,
                                    errorCode: 0);
                                await identityError.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, identityError);
                                continue;
                            }

                            // A malformed party body must never kill the
                            // connection — parse and dispatch defensively.
                            PartyOpResult partyResult;
                            byte partyErrorCode;
                            try
                            {
                                var parsedSettings = GameProtocolEngine.TryParseSetPartyInfo(
                                    request.Body,
                                    out var partySettings);
                                var parsedPeerRequest = GameProtocolEngine.TryParseRequestPeer(
                                    request.Body,
                                    out var peerRequest);
                                var parsedPeerResponse = GameProtocolEngine.TryParseResponsePeer(
                                    request.Body,
                                    out var peerResponse);
                                var parsedWalkoutSlot = GameProtocolEngine.TryParseWalkoutPartyMember(
                                    request.Body,
                                    out var walkoutSlot);

                                (partyResult, partyErrorCode) = request.ProtocolId switch
                                {
                                    GameProtocolEngine.SetPartyInfoCommand => parsedSettings
                                        ? (partyCoordinator.SetPartyInfo(partyIdentity, partySettings), (byte)8)
                                        : (PartyOpResult.Fail("malformed_set_party_info"), (byte)0),
                                    GameProtocolEngine.LeavePartyCommand =>
                                        (partyCoordinator.LeaveParty(partyIdentity), (byte)0x12),
                                    GameProtocolEngine.WalkoutPartyMemberCommand => parsedWalkoutSlot
                                        ? (partyCoordinator.Kick(partyIdentity, walkoutSlot), (byte)4)
                                        : (PartyOpResult.Fail("malformed_walkout"), (byte)0),
                                    GameProtocolEngine.RequestPeerCommand => parsedPeerRequest
                                        ? (partyCoordinator.RequestPeer(partyIdentity, peerRequest), (byte)5)
                                        : (PartyOpResult.Fail("malformed_request_peer"), (byte)0),
                                    GameProtocolEngine.ResponsePeerCommand => parsedPeerResponse
                                        ? (partyCoordinator.RespondPeer(partyIdentity, peerResponse), (byte)1)
                                        : (PartyOpResult.Fail("malformed_response_peer"), (byte)0),
                                    _ => (PartyOpResult.Fail("unknown_party_command"), (byte)0),
                                };
                            }
                            catch (Exception exception)
                            {
                                logger.LogWarning(
                                    exception,
                                    "Party command {ProtocolId} from {CharacterId} threw; replying failure.",
                                    request.ProtocolId,
                                    partyIdentity.CharacterId);
                                partyResult = PartyOpResult.Fail("party_command_exception");
                                partyErrorCode = 0;
                            }

                            var partyIsSettingsFamily =
                                request.ProtocolId
                                    is GameProtocolEngine.SetPartyInfoCommand
                                    or GameProtocolEngine.LeavePartyCommand
                                    or GameProtocolEngine.WalkoutPartyMemberCommand;
                            GameServerPacket partyReply;
                            if (partyResult.Ok)
                            {
                                partyReply = GameProtocolEngine.CreateCommandReply(
                                    request,
                                    success: true);
                            }
                            else
                            {
                                partyReply = partyIsSettingsFamily
                                    ? GameProtocolEngine.CreatePartyCommandError(
                                        request,
                                        partyErrorCode)
                                    : GameProtocolEngine.CreatePeerCommandError(
                                        request,
                                        partyErrorCode);
                            }

                            await partyReply.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, partyReply);
                            if (!partyResult.Ok)
                            {
                                logger.LogInformation(
                                    "Rejected party command {ProtocolId} for {CharacterId}: {Reason}.",
                                    request.ProtocolId,
                                    partyIdentity.CharacterId,
                                    partyResult.Reason);
                            }

                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.SelectDungeonCommand)
                        {
                            // DFLegacy command 16 is sequence, u16 dungeon id,
                            // byte difficulty and byte option flag.
                            var dungeonId = request.Body.Length >= 4
                                ? BinaryPrimitives.ReadUInt16LittleEndian(
                                    request.Body.AsSpan(2, sizeof(ushort)))
                                : (ushort)1;
                            var difficulty = request.Body.Length >= 5
                                ? request.Body[4]
                                : (byte)0;
                            var dungeonOption = request.Body.Length >= 6
                                ? request.Body[5]
                                : (byte)0;

                            var requiresTutorial = activeCharacter is { TutorialStarted: false };
                            if (!requiresTutorial && inDungeonSelection
                                && dungeonSelectionDeadline is { } entryDeadline
                                && DateTimeOffset.UtcNow >= entryDeadline)
                            {
                                // A ready packet may win Task.WhenAny at the deadline.
                                // Check again before layout generation or ticket/fatigue consumption.
                                await ReturnToTownFromDungeonAsync("selection-countdown-expired");
                                continue;
                            }

                            if (!requiresTutorial && !inDungeonSelection)
                            {
                                logger.LogWarning("Ignored stale dungeon selection for {CharacterId}.", activeCharacter?.Id);
                                continue;
                            }

                            if (!requiresTutorial && dungeonOption != 0
                                && (selectionWorldMap is null
                                    || !selectionWorldMap.DungeonIds.Contains(dungeonId)
                                    || !selectionWorldMap.MeetsHellQuests(completedQuestIds)
                                    || !selectionWorldMap.HasHellItems(mainInventory.Values)
                                    || !dungeonCatalog.IsHellDungeon(dungeonId)))
                            {
                                var rejected = GameProtocolEngine.CreateCommandError(request, errorCode: 14);
                                await rejected.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, rejected);
                                await SendDungeonSelectionEligibilityAsync();
                                continue;
                            }
                            if (requiresTutorial
                                && dungeonId != GameProtocolEngine.TutorialDungeonId)
                            {
                                var tutorialRequiredError = GameProtocolEngine.CreateCommandError(
                                    request,
                                    errorCode: 4);
                                await tutorialRequiredError.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, tutorialRequiredError);
                                logger.LogInformation(
                                    "Rejected dungeon {DungeonId} for character {CharacterId} until tutorial dungeon {TutorialDungeonId} is complete.",
                                    dungeonId,
                                    activeCharacter!.Id,
                                    GameProtocolEngine.TutorialDungeonId);
                                continue;
                            }

                            if (dungeonId == GameProtocolEngine.TutorialDungeonId)
                            {
                                if (!requiresTutorial)
                                {
                                    var closedTutorialError = GameProtocolEngine.CreateCommandError(
                                        request,
                                        errorCode: 4);
                                    await closedTutorialError.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, closedTutorialError);
                                    logger.LogInformation(
                                        "Rejected tutorial dungeon replay for character {CharacterId}.",
                                        activeCharacter?.Id);
                                    continue;
                                }

                                var tutorialMonsters = GameProtocolEngine.CreateTutorialMonsters();
                                inDungeonSelection = false;
                                currentDungeonId = GameProtocolEngine.TutorialDungeonId;
                                currentDungeonLayout = null;
                                currentDungeonRoomX = 0;
                                currentDungeonRoomY = 0;
                                currentDungeonDifficulty = difficulty;
                                currentDungeonRoom = new DungeonRoomDefinition(
                                    GameProtocolEngine.TutorialMapId,
                                    DungeonMapType.Boss,
                                    tutorialMonsters,
                                    Topology: 'B',
                                    HasScriptBossActor: false,
                                    PassiveObjects: [],
                                    PassiveItemSpawns: []);
                                aliveDungeonMonsters.Clear();
                                aliveDungeonMonsters.UnionWith(
                                    tutorialMonsters.Select(monster => monster.UniqueId));
                                reportedBossDeaths.Clear();
                                bossCascadeMonsterDeaths.Clear();
                                destroyedPassiveObjects.Clear();
                                groundDungeonItems.ResetDungeon();
                                dungeonRun.Stop();
                                tutorialDungeonActive = true;
                                dungeonCharacterAlive = true;
                                dungeonDeathGeneration++;
                                dungeonDeathTimeout = null;
                                dungeonClearEnabled = false;
                                dungeonResultSent = false;
                                dungeonCardLayoutSent = false;
                                dungeonCardRightsSent = false;
                                dungeonCardSelectionDeadline = null;
                                dungeonClearReward = null;
                                dungeonFreeCardIndex = null;
                                dungeonGoldCardIndex = null;
                                dungeonMonsterExperienceGained = 0;
                                dungeonClearExperience = null;
                                dungeonConsumedFatigue = 0;
                                dungeonCreatureStomachCheckpointSeconds = null;
                                dungeonLeveledUp = false;
                                dungeonNormalMonsterKills = 0;
                                dungeonChampionMonsterKills = 0;
                                dungeonBossMonsterKills = 0;
                                dungeonChampionRewardOrdinal = 0;

                                var tutorialInfo =
                                    GameProtocolEngine.CreateTutorialDungeonInfo(difficulty);
                                await tutorialInfo.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, tutorialInfo);

                                var tutorialStartMap =
                                    GameProtocolEngine.CreateTutorialStartMap();
                                await tutorialStartMap.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, tutorialStartMap);
                                logger.LogInformation(
                                    "Started mandatory tutorial dungeon/scene {TutorialId} for character {CharacterId}; monsters={MonsterCount}, difficulty={Difficulty}, option={Option}; no PVF dungeon or map definition was queried.",
                                    GameProtocolEngine.TutorialDungeonId,
                                    activeCharacter!.Id,
                                    tutorialMonsters.Length,
                                    difficulty,
                                    dungeonOption);
                                continue;
                            }

                            if (activeCharacter is null
                                || !dungeonCatalog.TryGetDefinition(dungeonId, out var dungeon))
                            {
                                logger.LogWarning(
                                    "Client selected unknown dungeon {DungeonId}; request ignored.",
                                    dungeonId);
                                continue;
                            }

                            if (!GetVisibleDungeonIds().Contains(dungeonId))
                            {
                                var lockedDungeonError = GameProtocolEngine.CreateCommandError(
                                    request,
                                    errorCode: 18);
                                await lockedDungeonError.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, lockedDungeonError);
                                logger.LogWarning(
                                    "Rejected locked hidden dungeon {DungeonId} for character {CharacterId}.",
                                    dungeonId,
                                    activeCharacter.Id);
                                continue;
                            }

                            var maximumDifficulty =
                                DungeonDifficultyProgression.GetMaximumDifficulty(
                                    activeCharacter.DungeonProgress,
                                    dungeonId);
                            if (difficulty > maximumDifficulty)
                            {
                                var lockedDifficultyError =
                                    GameProtocolEngine.CreateCommandError(
                                        request,
                                        errorCode: 18);
                                await lockedDifficultyError.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, lockedDifficultyError);
                                logger.LogWarning(
                                    "Rejected locked dungeon difficulty {Difficulty} for dungeon {DungeonId}; character {CharacterId} has maximum {MaximumDifficulty}.",
                                    difficulty,
                                    dungeonId,
                                    activeCharacter.Id,
                                    maximumDifficulty);
                                continue;
                            }

                            if (!dungeonCatalog.TryCreateLayout(
                                    dungeonId,
                                    out var dungeonLayout,
                                    partyMemberCount: 1))
                            {
                                logger.LogWarning(
                                    "Dungeon {DungeonId} has no usable maze layout; request ignored.",
                                    dungeonId);
                                continue;
                            }

                            var unfinishedQuestDefinitions = currentQuests
                                .Where(quest => quest.Trigger != 0)
                                .Select(quest => questCatalog.TryGetDefinition(quest.QuestId, out var definition)
                                    ? definition : null)
                                .OfType<QuestDefinition>();
                            if (dungeonCatalog.TryApplyQuestMap(
                                    dungeonLayout, unfinishedQuestDefinitions, out var questMapId))
                            {
                                logger.LogInformation(
                                    "Dungeon {DungeonId} selected rescue quest map {MapId} for character {CharacterId}.",
                                    dungeonId, questMapId, activeCharacter.Id);
                            }

                            if (!dungeonRun.TryStart(
                                    dungeonLayout,
                                    difficulty,
                                    dungeonOption,
                                    out var initialTransition))
                            {
                                logger.LogWarning(
                                    "Dungeon {DungeonId} maze {MazeIndex} start room ({RoomX},{RoomY}) has no map.",
                                    dungeonId,
                                    dungeonLayout.MazeIndex,
                                    dungeonLayout.StartX,
                                    dungeonLayout.StartY);
                                continue;
                            }

                            CharacterItemRecord? consumedHellTicket = null;
                            GameServerPacket? pendingHellTicketUpdate = null;
                            if (dungeonRun.HellMode)
                            {
                                var plannedInventory = mainInventory.ToDictionary();
                                if (selectionWorldMap is null
                                    || !selectionWorldMap.TryConsumeHellItems(mainInventory, out plannedInventory))
                                {
                                    dungeonRun.Stop();
                                    var ticketError = GameProtocolEngine.CreateCommandError(
                                        request,
                                        errorCode: 14);
                                    await ticketError.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, ticketError);
                                    await SendDungeonSelectionEligibilityAsync();
                                    logger.LogInformation(
                                        "Rejected hell entry for dungeon {DungeonId}: no matching ticket ({TicketIds}).",
                                        dungeonId,
                                        string.Join(',', dungeon.HellTicketItemIds));
                                    continue;
                                }

                                var changedTickets = mainInventory.Values
                                    .Where(item => !plannedInventory.TryGetValue(item.Slot, out var remaining)
                                        || remaining.CountOrValue != item.CountOrValue)
                                    .OrderBy(item => item.Slot).ToArray();
                                consumedHellTicket = changedTickets.FirstOrDefault();

                                var savedTicketConsumption =
                                    await store.SaveCharacterInventoryAsync(
                                        accountName,
                                        activeCharacter.Id,
                                        currentGold,
                                        plannedInventory.Values,
                                        equippedInventory.Values,
                                        serverToken);
                                if (savedTicketConsumption is null)
                                {
                                    dungeonRun.Stop();
                                    var ticketSaveError = GameProtocolEngine.CreateCommandError(
                                        request,
                                        errorCode: 19);
                                    await ticketSaveError.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, ticketSaveError);
                                    continue;
                                }

                                activeCharacter = savedTicketConsumption;
                                mainInventory = plannedInventory;
                                pendingHellTicketUpdate = GameProtocolEngine.CreateUpdateItemList(
                                    0,
                                    changedTickets.Select(item => plannedInventory.TryGetValue(item.Slot, out var remaining)
                                        ? ToGameInventoryEntry(remaining)
                                        : new GameInventoryEntry(item.Slot, ushort.MaxValue, 0)).ToArray());
                            }

                            inDungeonSelection = false;
                            tutorialDungeonActive = false;
                            currentDungeonId = dungeonId;
                            currentDungeonLayout = dungeonLayout;
                            currentDungeonRoomX = dungeonRun.RoomX;
                            currentDungeonRoomY = dungeonRun.RoomY;
                            currentDungeonDifficulty = difficulty;
                            currentDungeonRoom = initialTransition.Room;
                            aliveDungeonMonsters.Clear();
                            aliveDungeonMonsters.UnionWith(
                                currentDungeonRoom.Monsters.Select(monster => monster.UniqueId));
                            reportedBossDeaths.Clear();
                            bossCascadeMonsterDeaths.Clear();
                            destroyedPassiveObjects.Clear();
                            groundDungeonItems.ResetDungeon();
                            dungeonCharacterAlive = true;
                            dungeonDeathGeneration++;
                            dungeonDeathTimeout = null;
                            dungeonClearEnabled = false;
                            dungeonResultSent = false;
                            dungeonCardLayoutSent = false;
                            dungeonCardRightsSent = false;
                            dungeonCardSelectionDeadline = null;
                            dungeonClearReward = null;
                            dungeonFreeCardIndex = null;
                            dungeonGoldCardIndex = null;
                            dungeonMonsterExperienceGained = 0;
                            dungeonClearExperience = null;
                            dungeonConsumedFatigue = 0;
                            dungeonCreatureStomachCheckpointSeconds = null;
                            dungeonLeveledUp = false;
                            dungeonNormalMonsterKills = 0;
                            dungeonChampionMonsterKills = 0;
                            dungeonBossMonsterKills = 0;
                            dungeonChampionRewardOrdinal = 0;

                            byte hellRoomX = 0;
                            byte hellRoomY = 0;
                            if (dungeonRun.HellMode)
                            {
                                _ = dungeonCatalog.TryGetHellRoom(
                                    dungeonId,
                                    out _,
                                    out hellRoomX,
                                    out hellRoomY);
                            }

                            var dungeonInfo = GameProtocolEngine.CreateDungeonInfo(
                                dungeonId,
                                difficulty,
                                mazeIndex: dungeonLayout.MazeIndex,
                                bossX: dungeonLayout.BossX,
                                bossY: dungeonLayout.BossY,
                                flagA: hellRoomX,
                                flagB: hellRoomY);
                            await dungeonInfo.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, dungeonInfo);
                            if (pendingHellTicketUpdate is not null)
                            {
                                await pendingHellTicketUpdate.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, pendingHellTicketUpdate);
                            }

                            var passiveObjectDrops = RegisterPassiveObjectDrops(
                                currentDungeonRoom,
                                groundDungeonItems,
                                localUserId,
                                currentDungeonRoomX,
                                currentDungeonRoomY);
                            var startMap = GameProtocolEngine.CreateStartMap(
                                roomX: currentDungeonRoomX,
                                roomY: currentDungeonRoomY,
                                mapId: currentDungeonRoom.MapId,
                                monsters: currentDungeonRoom.Monsters,
                                passiveObjectDrops: passiveObjectDrops,
                                randomSeed: currentDungeonRoom.ClientRandomSeed,
                                roomStateFlag: dungeonRun.RoomStateFlag,
                                hellPartyMode: dungeonRun.CurrentHellPartyMode);
                            await startMap.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, startMap);
                            dungeonCreatureStomachCheckpointSeconds =
                                DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                            await ConsumeDungeonRoomFatigueAsync(
                                currentDungeonRoomX,
                                currentDungeonRoomY);
                            await EnableEmptyBossRoomSettlementAsync(currentDungeonRoom);
                            logger.LogInformation(
                                "Dungeon {DungeonId} selected maze {MazeIndex}, start ({StartX},{StartY}), boss ({BossX},{BossY}), start map {MapId}, monsters={MonsterCount}, champions={ChampionCount}, mapChampionBase={MapChampionBase}, clientSeed={ClientSeed}, hellMode={HellMode}, partyMode={HellPartyMode}, ticket={TicketItemId}.",
                                dungeonId,
                                dungeonLayout.MazeIndex,
                                dungeonLayout.StartX,
                                dungeonLayout.StartY,
                                dungeonLayout.BossX,
                                dungeonLayout.BossY,
                                currentDungeonRoom.MapId,
                                currentDungeonRoom.Monsters.Length,
                                currentDungeonRoom.Monsters.Count(monster =>
                                    monster.Type == GameDungeonMonsterTypes.Champion),
                                currentDungeonRoom.BaseChampionCount,
                                currentDungeonRoom.ClientRandomSeed,
                                dungeonRun.HellMode,
                                dungeonRun.CurrentHellPartyMode,
                                consumedHellTicket?.ItemId ?? 0);
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.UseSkillCommand)
                        {
                            if (!options.EnablePacketTracing)
                            {
                                runtime.TracePacketAlways(
                                    runtimeSession,
                                    "RX-USE-SKILL",
                                    request,
                                    options.HexDumpLimit);
                            }

                            var skillId = request.Body.Length >= 3
                                ? request.Body[2]
                                : byte.MaxValue;
                            if (activeCharacter is not null
                                && currentDungeonId != 0
                                && skillCatalog.TryGetDefinition(
                                    activeCharacter.Job,
                                    skillId,
                                    out var usedSkill)
                                && usedSkill.ConsumedItemId != 0
                                && usedSkill.ConsumedItemCount > 0)
                            {
                                // Command 18 is emitted before USE_SKILL and
                                // owns both the authoritative material
                                // deduction and the client's pending item
                                // transaction. Do not consume the stack twice.
                                logger.LogInformation(
                                    "USE_SKILL job={Job}, skill={SkillId}, configuredItem={ItemId} x{ConfiguredCount}; material handled by command 18.",
                                    activeCharacter.Job,
                                    skillId,
                                    usedSkill.ConsumedItemId,
                                    usedSkill.ConsumedItemCount);
                            }
                            else
                            {
                                logger.LogInformation(
                                    "USE_SKILL job={Job}, skill={SkillId}, dungeon={DungeonId}; no configured item transaction.",
                                    activeCharacter?.Job,
                                    skillId,
                                    currentDungeonId);
                            }

                            var useSkillReply =
                                GameProtocolEngine.CreateUseSkillReply();
                            await useSkillReply.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, useSkillReply);

                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.MoveMapCommand)
                        {
                            // DFLegacy command 48 is sequence, target room X and
                            // target room Y. Sending the next START_MAP packet
                            // completes the door transition after room clear.
                            if (request.Body.Length >= 4
                                && currentDungeonId != 0
                                && currentDungeonLayout is not null)
                            {
                                var targetX = request.Body[2];
                                var targetY = request.Body[3];
                                if (targetX == currentDungeonRoomX
                                    && targetY == currentDungeonRoomY)
                                {
                                    var duplicateMoveError =
                                        GameProtocolEngine.CreateCommandError(
                                            request,
                                            errorCode: 4);
                                    await duplicateMoveError.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, duplicateMoveError);
                                    logger.LogDebug(
                                        "Rejected duplicate MOVE_MAP for current dungeon {DungeonId} room ({RoomX},{RoomY}).",
                                        currentDungeonId,
                                        targetX,
                                        targetY);
                                }
                                else if (dungeonRun.TryMoveRoom(
                                        targetX,
                                        targetY,
                                        out var transition))
                                {
                                    var previousRoomX = currentDungeonRoomX;
                                    var previousRoomY = currentDungeonRoomY;
                                    await SettleDungeonCreatureHungerAsync(
                                        notifyClient: true,
                                        serverToken);
                                    var moveReply = GameProtocolEngine.CreateCommandReply(
                                        request,
                                        success: true);
                                    await moveReply.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, moveReply);

                                    // Ground items belong to their originating room. Moving
                                    // rooms must not synthesize NOTI 39: DNF 1.0.1.9 treats
                                    // its non-zero routing bytes as dice results, while the
                                    // authoritative item remains unclaimed on the server.

                                    if (transition.CompletedHellRoomOnExit)
                                    {
                                        groundDungeonItems.DetachAllPassiveObjectItems(
                                            previousRoomX,
                                            previousRoomY);
                                    }

                                    currentDungeonRoomX = targetX;
                                    currentDungeonRoomY = targetY;
                                    dungeonClearEnabled = false;
                                    dungeonResultSent = false;
                                    dungeonCardLayoutSent = false;
                                    dungeonCardRightsSent = false;
                                    dungeonCardSelectionDeadline = null;
                                    dungeonClearReward = null;
                                    dungeonFreeCardIndex = null;
                                    dungeonGoldCardIndex = null;
                                    aliveDungeonMonsters.Clear();
                                    reportedBossDeaths.Clear();
                                    bossCascadeMonsterDeaths.Clear();
                                    destroyedPassiveObjects.Clear();
                                    currentDungeonRoom = transition.Room;
                                    aliveDungeonMonsters.UnionWith(
                                        currentDungeonRoom.Monsters.Select(monster =>
                                            monster.UniqueId));
                                    if (dungeonRun.IsCurrentHellRoomCleared)
                                    {
                                        groundDungeonItems.DetachAllPassiveObjectItems(
                                            targetX,
                                            targetY);
                                    }
                                    GameServerPacket nextMap;
                                    if (transition.ReuseClientRoomState)
                                    {
                                        nextMap = GameProtocolEngine.CreateCachedStartMap(
                                            targetX,
                                            targetY,
                                            currentDungeonRoom.ClientRandomSeed);
                                    }
                                    else
                                    {
                                        var passiveObjectDrops = transition.FirstVisit
                                            ? RegisterPassiveObjectDrops(
                                                currentDungeonRoom,
                                                groundDungeonItems,
                                                localUserId,
                                                targetX,
                                                targetY)
                                            : GetRegisteredPassiveObjectDrops(
                                                groundDungeonItems,
                                                targetX,
                                                targetY);
                                        nextMap = GameProtocolEngine.CreateStartMap(
                                            targetX,
                                            targetY,
                                            currentDungeonRoom.MapId,
                                            currentDungeonRoom.Monsters,
                                            passiveObjectDrops,
                                            randomSeed: currentDungeonRoom.ClientRandomSeed,
                                            roomStateFlag: dungeonRun.RoomStateFlag,
                                            hellPartyMode: dungeonRun.CurrentHellPartyMode);
                                    }
                                    await nextMap.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, nextMap);
                                    if (transition.FirstVisit)
                                    {
                                        await ConsumeDungeonRoomFatigueAsync(
                                            targetX,
                                            targetY);
                                    }
                                    await EnableEmptyBossRoomSettlementAsync(
                                        currentDungeonRoom);
                                    logger.LogInformation(
                                        "Dungeon {DungeonId} moved to room ({RoomX},{RoomY}), map {MapId}, monsters={MonsterCount}, champions={ChampionCount}, mapChampionBase={MapChampionBase}, clientSeed={ClientSeed}, firstVisit={FirstVisit}, reuseClientRoomState={ReuseClientRoomState}, hellPartyMode={HellPartyMode}, hellRoomClearedOnExit={HellRoomClearedOnExit}.",
                                        currentDungeonId,
                                        targetX,
                                        targetY,
                                        currentDungeonRoom.MapId,
                                        currentDungeonRoom.Monsters.Length,
                                        currentDungeonRoom.Monsters.Count(monster =>
                                            monster.Type == GameDungeonMonsterTypes.Champion),
                                        currentDungeonRoom.BaseChampionCount,
                                        currentDungeonRoom.ClientRandomSeed,
                                        transition.FirstVisit,
                                        transition.ReuseClientRoomState,
                                        dungeonRun.CurrentHellPartyMode,
                                        transition.CompletedHellRoomOnExit);
                                }
                                else
                                {
                                    var moveError = GameProtocolEngine.CreateCommandError(
                                        request,
                                        errorCode: 4);
                                    await moveError.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, moveError);
                                    logger.LogWarning(
                                        "Dungeon {DungeonId} rejected disconnected or unavailable room ({RoomX},{RoomY})",
                                        currentDungeonId,
                                        targetX,
                                        targetY);
                                }
                            }
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.BossDieCheckCommand)
                        {
                            var parsed = GameProtocolEngine.TryParseBossDieCheckCommand(
                                request.Body,
                                out var bossDieCheck);
                            var bossIds = currentDungeonRoom is not null
                                ? currentDungeonRoom.Monsters
                                    .Where(monster => monster.Type
                                        == GameDungeonMonsterTypes.Boss)
                                    .Select(monster => monster.UniqueId)
                                    .ToHashSet()
                                : [];
                            var validBoss = parsed
                                && currentDungeonRoom is not null
                                && currentDungeonRoom.MapType == DungeonMapType.Boss
                                && bossDieCheck.ParticipantId == localUserId
                                && bossIds.Contains(bossDieCheck.BossId);

                            GameServerPacket bossDieReply;
                            if (validBoss)
                            {
                                reportedBossDeaths.Add(bossDieCheck.BossId);
                                var allBossesReported = bossIds.All(
                                    reportedBossDeaths.Contains);
                                if (allBossesReported)
                                {
                                    bossCascadeMonsterDeaths.UnionWith(
                                        aliveDungeonMonsters.Where(monsterId =>
                                            !bossIds.Contains(monsterId)));
                                }

                                bossDieReply = GameProtocolEngine.CreateBossDieCheck(
                                    success: true,
                                    allBossesReported,
                                    bossDieCheck.BossId);
                                logger.LogInformation(
                                    "Dungeon {DungeonId} accepted BOSS_DIE_CHECK from participant {ParticipantId} for boss {BossId}; reported={ReportedCount}/{BossCount}, native-all-die={AllBossesReported}, checksum={SkillChecksum:X8}.",
                                    currentDungeonId,
                                    bossDieCheck.ParticipantId,
                                    bossDieCheck.BossId,
                                    reportedBossDeaths.Count,
                                    bossIds.Count,
                                    allBossesReported,
                                    bossDieCheck.SkillChecksum);
                            }
                            else
                            {
                                bossDieReply = GameProtocolEngine.CreateBossDieCheck(
                                    success: false,
                                    allBossesReported: false,
                                    errorCode: 2);
                                logger.LogWarning(
                                    "Rejected BOSS_DIE_CHECK in dungeon {DungeonId}, room ({RoomX},{RoomY}); parsed={Parsed}, participant={ParticipantId}, expectedParticipant={ExpectedParticipantId}, boss={BossId}, mapType={MapType}.",
                                    currentDungeonId,
                                    currentDungeonRoomX,
                                    currentDungeonRoomY,
                                    parsed,
                                    parsed ? bossDieCheck.ParticipantId : 0,
                                    localUserId,
                                    parsed ? bossDieCheck.BossId : 0,
                                    currentDungeonRoom?.MapType);
                            }

                            await bossDieReply.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, bossDieReply);
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.AllDieMonsterCommand)
                        {
                            logger.LogInformation(
                                "Accepted DFLegacy ALLDIE_MONSTER for dungeon {DungeonId}, room ({RoomX},{RoomY}); waiting for normal monster-death confirmations.",
                                currentDungeonId,
                                currentDungeonRoomX,
                                currentDungeonRoomY);
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.DieCharacterCommand)
                        {
                            if (currentDungeonId == 0
                                || activeCharacter is null
                                || !dungeonCharacterAlive)
                            {
                                var error = GameProtocolEngine.CreateCommandError(
                                    request,
                                    errorCode: 19);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                logger.LogWarning(
                                    "Rejected DIE_CHARACTER for user {UserId}: dungeon={DungeonId}, hasCharacter={HasCharacter}, alive={Alive}.",
                                    localUserId,
                                    currentDungeonId,
                                    activeCharacter is not null,
                                    dungeonCharacterAlive);
                                continue;
                            }

                            dungeonCharacterAlive = false;
                            dungeonDeathGeneration++;
                            dungeonDeathTimeout = (
                                dungeonDeathGeneration,
                                DateTimeOffset.UtcNow.AddSeconds(10));
                            var dieState = GameProtocolEngine.CreateDieState(
                                localUserId,
                                isAlive: false);
                            await dieState.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, dieState);
                            logger.LogInformation(
                                "User {UserId} entered authoritative dungeon death state in dungeon {DungeonId} room ({RoomX},{RoomY}).",
                                localUserId,
                                currentDungeonId,
                                currentDungeonRoomX,
                                currentDungeonRoomY);
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.UseCoinCommand)
                        {
                            byte? errorCode = null;
                            GameUseCoinCommand useCoinCommand = default;
                            if (!GameProtocolEngine.TryParseUseCoinCommand(
                                    request.Body,
                                    out useCoinCommand)
                                || currentDungeonId == 0
                                || activeCharacter is null)
                            {
                                errorCode = 19;
                            }
                            else if (useCoinCommand.TargetUserId != localUserId)
                            {
                                errorCode = 21;
                            }
                            else if (dungeonCharacterAlive)
                            {
                                errorCode = 18;
                            }
                            else if (!CharacterInventoryPlanner.TryConsumeRevivalCoin(
                                         mainInventory,
                                         out var plannedInventory))
                            {
                                errorCode = 17;
                            }
                            else
                            {
                                var saved = await store.SaveCharacterInventoryAsync(
                                    accountName,
                                    activeCharacter.Id,
                                    currentGold,
                                    plannedInventory.Values,
                                    equippedInventory.Values,
                                    serverToken);
                                if (saved is null)
                                {
                                    errorCode = 19;
                                }
                                else
                                {
                                    activeCharacter = saved;
                                    mainInventory = plannedInventory;
                                    dungeonCharacterAlive = true;
                                    dungeonDeathGeneration++;
                                    dungeonDeathTimeout = null;

                                    // DFLegacy restores the actor from DIE_STATE
                                    // before its USE_COIN reply closes the revive UI.
                                    // The reply also decrements the client's local
                                    // virtual-coin object, so no NOTI 14 is sent.
                                    var reviveState = GameProtocolEngine.CreateDieState(
                                        useCoinCommand.TargetUserId,
                                        isAlive: true);
                                    await reviveState.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, reviveState);

                                    var useCoinReply =
                                        GameProtocolEngine.CreateUseCoinReply(
                                            useCoinCommand.TargetUserId);
                                    await useCoinReply.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, useCoinReply);
                                    logger.LogInformation(
                                        "User {UserId} revived in dungeon {DungeonId}; revival coins remaining={RemainingCoins}.",
                                        localUserId,
                                        currentDungeonId,
                                        plannedInventory.GetValueOrDefault(
                                            CharacterInventoryLayout.RevivalCoinSlot)
                                            ?.CountOrValue ?? 0);
                                    continue;
                                }
                            }

                            var error = GameProtocolEngine.CreateCommandError(
                                request,
                                errorCode!.Value);
                            await error.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, error);
                            logger.LogWarning(
                                "Rejected USE_COIN for user {UserId}, target={TargetUserId}, error={ErrorCode}.",
                                localUserId,
                                useCoinCommand.TargetUserId,
                                errorCode.Value);
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.DieMonsterCommand)
                        {
                            if (GameProtocolEngine.TryParseDieMonsterCommand(
                                    request.Body,
                                    out var dieCommand))
                            {
                                if (dieCommand.IsPassiveObject)
                                {
                                    if (currentDungeonRoom is not null
                                        && dungeonRun.TryMarkPassiveObjectDestroyed(
                                            dieCommand.EntityId,
                                            out var passiveObjectIndex))
                                    {
                                        destroyedPassiveObjects.Add(passiveObjectIndex);
                                        var activatedItemCount = groundDungeonItems.ActivatePassiveObject(
                                            passiveObjectIndex,
                                            currentDungeonRoomX,
                                            currentDungeonRoomY);
                                        currentDungeonRoom = dungeonRun.CurrentRoom
                                            ?? currentDungeonRoom;
                                        logger.LogInformation(
                                            "Passive object code {PassiveObjectCode}, association {PassiveObjectIndex} destroyed in room ({RoomX},{RoomY}); activatedItems={ActivatedItemCount}.",
                                            dieCommand.EntityId,
                                            passiveObjectIndex,
                                            currentDungeonRoomX,
                                            currentDungeonRoomY,
                                            activatedItemCount);
                                    }
                                    else
                                    {
                                        logger.LogWarning(
                                            "Ignored unknown passive object {PassiveObjectIndex} in room ({RoomX},{RoomY}).",
                                            dieCommand.EntityId,
                                            currentDungeonRoomX,
                                            currentDungeonRoomY);
                                    }

                                    continue;
                                }

                                var monsterUniqueId = dieCommand.EntityId;
                                var wasHellFiend = dungeonRun.IsHellFiend(monsterUniqueId);
                                var hellPartyMode = dungeonRun.CurrentHellPartyMode;
                                var isBossCascadeDeath =
                                    bossCascadeMonsterDeaths.Remove(monsterUniqueId);
                                var wasAlive = aliveDungeonMonsters.Remove(monsterUniqueId);
                                var defeatedMonster = wasAlive
                                    && currentDungeonRoom is not null
                                    ? currentDungeonRoom.Monsters.FirstOrDefault(monster =>
                                        monster.UniqueId == monsterUniqueId)
                                    : null;
                                if (!wasAlive || defeatedMonster is null)
                                {
                                    logger.LogWarning(
                                        "Ignored stale or unknown monster death {MonsterId} in room ({RoomX},{RoomY}).",
                                        monsterUniqueId,
                                        currentDungeonRoomX,
                                        currentDungeonRoomY);
                                    continue;
                                }

                                dungeonRun.MarkMonsterDefeated(monsterUniqueId);

                                var championRewardOrdinal =
                                    GameProtocolEngine.NoChampionRewardOrdinal;

                                switch (defeatedMonster.Type)
                                {
                                    case GameDungeonMonsterTypes.Champion:
                                    case GameDungeonMonsterTypes.SuperChampion:
                                        dungeonChampionMonsterKills = Math.Min(
                                            ushort.MaxValue,
                                            dungeonChampionMonsterKills + 1);
                                        if (dungeonChampionRewardOrdinal
                                            < GameProtocolEngine.MaximumChampionRewardOrdinal)
                                        {
                                            championRewardOrdinal = checked((sbyte)(
                                                ++dungeonChampionRewardOrdinal));
                                        }
                                        break;
                                    case GameDungeonMonsterTypes.Boss:
                                        dungeonBossMonsterKills = Math.Min(
                                            ushort.MaxValue,
                                            dungeonBossMonsterKills + 1);
                                        break;
                                    default:
                                        dungeonNormalMonsterKills = Math.Min(
                                            ushort.MaxValue,
                                            dungeonNormalMonsterKills + 1);
                                        break;
                                }

                                var protocolDrops = new List<GameDungeonDrop>(6);
                                if (!isBossCascadeDeath)
                                {
                                    foreach (var generatedDrop in dropGenerator.Generate(
                                                 defeatedMonster.Level,
                                                 defeatedMonster.Type,
                                                 currentDungeonDifficulty))
                                    {
                                        var groundItem = groundDungeonItems.Add(
                                            generatedDrop,
                                            localUserId,
                                            monsterUniqueId,
                                            currentDungeonRoomX,
                                            currentDungeonRoomY,
                                            x: unchecked((short)defeatedMonster.X),
                                            y: unchecked((short)defeatedMonster.Y),
                                            catalog: itemCatalog);
                                        protocolDrops.Add(new GameDungeonDrop(
                                            groundItem.GroundId,
                                            groundItem.ItemId,
                                            groundItem.GetClientAddInfo(itemCatalog),
                                            groundItem.GetClientDurability(itemCatalog),
                                            groundItem.OwnerUserId));
                                    }

                                    foreach (var generatedDrop in monsterChampionDrops.Roll(
                                                 defeatedMonster.MonsterIndex,
                                                 defeatedMonster.Type))
                                    {
                                        var groundItem = groundDungeonItems.Add(
                                            generatedDrop,
                                            localUserId,
                                            monsterUniqueId,
                                            currentDungeonRoomX,
                                            currentDungeonRoomY,
                                            x: unchecked((short)defeatedMonster.X),
                                            y: unchecked((short)defeatedMonster.Y),
                                            catalog: itemCatalog);
                                        protocolDrops.Add(new GameDungeonDrop(
                                            groundItem.GroundId,
                                            groundItem.ItemId,
                                            groundItem.GetClientAddInfo(itemCatalog),
                                            groundItem.GetClientDurability(itemCatalog),
                                            groundItem.OwnerUserId));
                                    }

                                    if (wasHellFiend && dropGenerator.Enabled)
                                    {
                                        var hellLevel = dungeonCatalog.TryGetDefinition(currentDungeonId, out var hellDefinition)
                                            ? hellDefinition.BasisLevel : defeatedMonster.Level;
                                        var hellHit = hellDropCatalog.TryRollEquipment(
                                            hellLevel,
                                            currentDungeonDifficulty,
                                            hellPartyMode,
                                            GameRandomSource.Shared,
                                            out var hellItemId,
                                            options.Drop.RatePercent / 100.0 * options.Drop.EconomicRate,
                                            options.Drop.ForceDrops);
                                        logger.LogInformation(
                                            "Hell drop result for character {CharacterId}, dungeon {DungeonId}, monster {MonsterIndex}/{UniqueId}: hit={Hit}, item={ItemId}.",
                                            activeCharacter?.Id, currentDungeonId, defeatedMonster.MonsterIndex,
                                            monsterUniqueId, hellHit, hellItemId);
                                        if (hellHit)
                                        {
                                            var hellGroundItem = groundDungeonItems.Add(
                                                new DungeonGeneratedDrop(
                                                    IsGold: false,
                                                    hellItemId,
                                                    CountOrValue: 1),
                                                localUserId,
                                                monsterUniqueId,
                                                currentDungeonRoomX,
                                                currentDungeonRoomY,
                                                x: unchecked((short)defeatedMonster.X),
                                                y: unchecked((short)defeatedMonster.Y),
                                                catalog: itemCatalog);
                                            protocolDrops.Add(new GameDungeonDrop(
                                                hellGroundItem.GroundId,
                                                hellGroundItem.ItemId,
                                                hellGroundItem.GetClientAddInfo(itemCatalog),
                                                hellGroundItem.GetClientDurability(itemCatalog),
                                                hellGroundItem.OwnerUserId));
                                        }
                                    }
                                }

                                var monsterDie =
                                    GameProtocolEngine.CreateMonsterDie(
                                        monsterUniqueId,
                                        protocolDrops,
                                        championRewardOrdinal);
                                await monsterDie.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, monsterDie);

                                if (!isBossCascadeDeath && !tutorialDungeonActive)
                                {
                                    await GrantMonsterQuestItemsAsync(
                                        defeatedMonster.MonsterIndex);
                                }

                                if (!isBossCascadeDeath && activeCharacter is not null)
                                {
                                    var gainedExperience =
                                        dungeonCatalog.TryGetDefinition(
                                            currentDungeonId,
                                            out var experienceDungeon)
                                            ? dungeonExperienceCatalog.CalculateMonsterExperience(
                                                activeCharacter.Level,
                                                defeatedMonster.Level,
                                                defeatedMonster.Type,
                                                defeatedMonster.MonsterIndex,
                                                experienceDungeon,
                                                currentDungeonDifficulty,
                                                partyMemberCount: 1)
                                            : 0;
                                    var progression = experienceCatalog.Grant(
                                        activeCharacter,
                                        gainedExperience);
                                    if (progression.GrantedExperience > 0)
                                    {
                                        var grantedSkillPoints = ApplyLevelUpSkillPoints(progression);
                                        activeCharacter = activeCharacter with
                                        {
                                            Level = progression.Level,
                                            Experience = progression.Experience,
                                            Sp = currentSp
                                        };
                                        var savedProgression =
                                            await store.SaveCharacterProgressionAsync(
                                                accountName,
                                                activeCharacter.Id,
                                                activeCharacter.Level,
                                                activeCharacter.Experience,
                                                currentSp,
                                                serverToken);
                                        if (savedProgression is null)
                                        {
                                            throw new InvalidOperationException(
                                                $"Could not persist monster experience for character {activeCharacter.Id}.");
                                        }

                                        activeCharacter = savedProgression;
                                        dungeonMonsterExperienceGained = (uint)Math.Min(
                                            uint.MaxValue,
                                            (ulong)dungeonMonsterExperienceGained
                                            + progression.GrantedExperience);
                                        dungeonLeveledUp |= progression.LeveledUp;

                                        var experienceGained =
                                            GameProtocolEngine.CreateExperienceGained(
                                                checked((byte)Math.Clamp(
                                                    activeCharacter.Level,
                                                    1,
                                                    byte.MaxValue)),
                                                activeCharacter.Experience,
                                                extraExperienceA: 0,
                                                extraExperienceB: 0,
                                                skillPoints: checked((ushort)Math.Min(
                                                    currentSp,
                                                    ushort.MaxValue)));
                                        await experienceGained.WriteAsync(
                                            stream,
                                            serverToken);
                                        Interlocked.Increment(
                                            ref runtimeSession.SentPackets);
                                        LogPacket(
                                            "TX",
                                            runtimeSession,
                                            experienceGained);
                                        logger.LogInformation(
                                            "Monster {MonsterId} (index {MonsterIndex}, level {MonsterLevel}, type {MonsterType}) granted character {CharacterId} {GrantedExperience} EXP; cumulative EXP is {Experience} at level {Level}.",
                                            monsterUniqueId,
                                            defeatedMonster.MonsterIndex,
                                            defeatedMonster.Level,
                                            defeatedMonster.Type,
                                            activeCharacter.Id,
                                            progression.GrantedExperience,
                                            activeCharacter.Experience,
                                            activeCharacter.Level);
                                        if (grantedSkillPoints > 0)
                                        {
                                            logger.LogInformation(
                                                "Monster {MonsterId} advanced character level {OldLevel}->{Level} and granted {SkillPoints} SP.",
                                                monsterUniqueId,
                                                progression.PreviousLevel,
                                                progression.Level,
                                                grantedSkillPoints);
                                        }

                                        if (progression.LeveledUp)
                                        {
                                            await SendCurrentSkillInfoAsync();
                                            await ValidateAndCorrectCharacterStatsAsync(
                                                "monster experience level-up");
                                        }
                                    }
                                }

                                if (protocolDrops.Count > 0)
                                {
                                    logger.LogInformation(
                                        "Monster {MonsterId} generated {DropCount} ground drops in room ({RoomX},{RoomY}).",
                                        monsterUniqueId,
                                        protocolDrops.Count,
                                        currentDungeonRoomX,
                                        currentDungeonRoomY);
                                }

                                var clearDecision = DungeonRoomClearRules.Evaluate(
                                    currentDungeonRoom!,
                                    defeatedMonster,
                                    aliveDungeonMonsters);
                                if (clearDecision.ForcedMonsterIds.Length > 0)
                                {
                                    logger.LogWarning(
                                        "Dungeon {DungeonId} received the final boss MONSTER_DIE with {MonsterCount} monsters still alive in room ({RoomX},{RoomY}); waiting for the native BOSS_DIE_CHECK/ALLDIE_MONSTER cascade instead of injecting a solo UDP peer packet.",
                                        currentDungeonId,
                                        clearDecision.ForcedMonsterIds.Length,
                                        currentDungeonRoomX,
                                        currentDungeonRoomY);
                                }

                                if (clearDecision.ShouldEnableClear && !dungeonClearEnabled)
                                {
                                    dungeonClearEnabled = true;
                                    if (!tutorialDungeonActive)
                                    {
                                        await GrantClearQuestItemsAsync();
                                    }
                                    var enableClear =
                                        GameProtocolEngine.CreateEnableClearDungeon();
                                    await enableClear.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, enableClear);
                                    logger.LogInformation(
                                        "Dungeon {DungeonId} boss map room ({RoomX},{RoomY}) cleared by {ClearReason}; settlement enabled",
                                        currentDungeonId,
                                        currentDungeonRoomX,
                                        currentDungeonRoomY,
                                        clearDecision.Reason);
                                }

                                if (!tutorialDungeonActive)
                                {
                                    await CompleteClearQuestTriggersAsync(
                                        $"map {currentDungeonRoom!.MapId} monster death");
                                }
                            }
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.GetItemCommand)
                        {
                            if (request.Body.Length < 4
                                || currentDungeonId == 0
                                || activeCharacter is null)
                            {
                                continue;
                            }

                            var groundId = BinaryPrimitives.ReadUInt16LittleEndian(
                                request.Body.AsSpan(2, sizeof(ushort)));
                            if (!groundDungeonItems.TryGet(
                                    groundId,
                                    currentDungeonRoomX,
                                    currentDungeonRoomY,
                                    out var groundItem)
                                || groundItem.OwnerUserId != localUserId)
                            {
                                logger.LogWarning(
                                    "Ignored invalid or duplicate pickup of ground item {GroundId} for user {UserId}.",
                                    groundId,
                                    localUserId);
                                continue;
                            }

                            if (groundItem.IsGold)
                            {
                                int updatedGold;
                                try
                                {
                                    updatedGold = checked(
                                        currentGold + checked((int)groundItem.CountOrValue));
                                }
                                catch (OverflowException)
                                {
                                    logger.LogWarning(
                                        "Gold pickup {GroundId} would overflow the character balance.",
                                        groundId);
                                    continue;
                                }

                                var saved = await store.SaveCharacterInventoryAsync(
                                    accountName,
                                    activeCharacter.Id,
                                    updatedGold,
                                    mainInventory.Values,
                                    equippedInventory.Values,
                                    serverToken);
                                if (saved is null)
                                {
                                    logger.LogWarning(
                                        "Gold pickup {GroundId} was not persisted; the ground item was retained.",
                                        groundId);
                                    continue;
                                }

                                activeCharacter = saved;
                                currentGold = updatedGold;
                                groundDungeonItems.Remove(groundId);
                                var pickup = GameProtocolEngine.CreateGoldItemPickup(
                                    groundId,
                                    localUserId,
                                    groundItem.CountOrValue);
                                await pickup.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, pickup);

                                var refresh = CreateMainInventoryPacket(
                                    currentGold,
                                    currentVictoryPoints,
                                    mainInventory.Values);
                                await refresh.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, refresh);
                                continue;
                            }

                            if (CharacterWarehouseProgression.TryGetItemTargetCapacity(
                                    groundItem.ItemId,
                                    out var groundUpgradeTarget))
                            {
                                var plannedCapacity = Math.Max(
                                    warehouseCapacity,
                                    groundUpgradeTarget);
                                var savedUpgrade = await store.SaveCharacterInventoryAsync(
                                    accountName,
                                    activeCharacter.Id,
                                    currentGold,
                                    mainInventory.Values,
                                    equippedInventory.Values,
                                    serverToken,
                                    warehouseCapacity: plannedCapacity,
                                    expectedWarehouseCapacity: warehouseCapacity);
                                if (savedUpgrade is null)
                                {
                                    logger.LogWarning(
                                        "Warehouse-upgrade pickup {GroundId} was not persisted; the ground item was retained.",
                                        groundId);
                                    continue;
                                }

                                _ = AdoptSavedWarehouseUpgradeState(savedUpgrade);
                                groundDungeonItems.Remove(groundId);
                                var upgradePickup =
                                    GameProtocolEngine.CreateInventoryItemPickup(
                                        groundId,
                                        localUserId,
                                        ushort.MaxValue);
                                await upgradePickup.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, upgradePickup);

                                var inventoryRefresh = CreateMainInventoryPacket(
                                    currentGold,
                                    currentVictoryPoints,
                                    mainInventory.Values);
                                await inventoryRefresh.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, inventoryRefresh);
                                await SendWarehouseStateAsync();
                                continue;
                            }

                            if (!CharacterInventoryPlanner.CanCarryAdditionalItem(
                                    mainInventory.Values,
                                    equippedInventory.Values,
                                    creatureInventory.Values,
                                    groundItem.ItemId,
                                    groundItem.CountOrValue,
                                    characterStatCatalog.Get(activeCharacter).InventoryLimit,
                                    itemCatalog,
                                    out var carriedWeight,
                                    out var pickupWeight,
                                    out var carriedWeightLimit))
                            {
                                // Keep the authoritative ground record and its ID. The client has
                                // already hidden the item for its optimistic pickup animation;
                                // CMD 46 error 0x15 rolls that state back and drops it visually.
                                // NOTI 39 would falsely confirm pickup, while NOTI 40 would create
                                // a second ground object instead of releasing the existing one.
                                var overweightReply =
                                    GameProtocolEngine.CreateGetItemSilentRejection(request);
                                await overweightReply.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, overweightReply);

                                var combinedWeight = carriedWeight > ulong.MaxValue - pickupWeight
                                    ? ulong.MaxValue
                                    : carriedWeight + pickupWeight;
                                var excessWeight = combinedWeight - carriedWeightLimit;
                                var overweightMessage = FormattableString.Invariant(
                                    $"将超出物品栏负重上限 {excessWeight / 1000d:F3}kg.");
                                var notice = GameProtocolEngine.CreateMessageNotification(
                                    GameMessageType.Type16Brown,
                                    // A zero target suppresses the local-character prefix.
                                    0,
                                    ClientEncoding.GetBytes(overweightMessage));
                                await notice.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, notice);
                                logger.LogInformation(
                                    "Ground item {GroundId} pickup rejected as overweight; current={CurrentWeight}, incoming={IncomingWeight}, limit={WeightLimit}, excess={ExcessWeight}; authoritative ground state was retained for the client's pickup rollback.",
                                    groundId,
                                    carriedWeight,
                                    pickupWeight,
                                    carriedWeightLimit,
                                    excessWeight);
                                continue;
                            }

                            if (itemCatalog.TryGetDefinition(
                                    groundItem.ItemId,
                                    out var groundDefinition)
                                && groundDefinition.InventoryCategory
                                    == ItemInventoryCategory.Creature)
                            {
                                var creatureGroundItem = groundItem.PreservedItem is
                                    { } preservedCreatureItem
                                    ? preservedCreatureItem with
                                    {
                                        Slot = ushort.MaxValue,
                                        CountOrValue = groundItem.CountOrValue
                                    }
                                    : new CharacterItemRecord(
                                        ushort.MaxValue,
                                        groundItem.ItemId,
                                        groundItem.CountOrValue,
                                        Durability: groundDefinition.IsEquipment
                                            ? (ushort)1_000
                                            : (ushort)0,
                                        InstanceId: groundItem.PreservedItem?.InstanceId
                                            ?? Guid.Empty);
                                if (!CharacterCreatureInventoryPlanner.TryPlace(
                                        creatureInventory,
                                        creatureGroundItem,
                                        itemCatalog,
                                        preferredEmptySlot: null,
                                        out var creaturePickupPlan,
                                        out var creaturePickupFailure))
                                {
                                    logger.LogInformation(
                                        "Ground Creature item {GroundId} cannot enter Creature inventory ({Failure}); it remains in the room.",
                                        groundId,
                                        creaturePickupFailure);
                                    continue;
                                }

                                var savedCreaturePickup =
                                    await store.SaveCharacterItemSpacesAsync(
                                        accountName,
                                        activeCharacter.Id,
                                        currentGold,
                                        mainInventory.Values,
                                        equippedInventory.Values,
                                        warehouseInventory.Values,
                                        serverToken,
                                        avatarInventory: avatarInventory.Values,
                                        creatureInventory:
                                            creaturePickupPlan.Inventory.Values);
                                if (savedCreaturePickup is null)
                                {
                                    logger.LogWarning(
                                        "Creature item pickup {GroundId} was not persisted; the ground item was retained.",
                                        groundId);
                                    continue;
                                }

                                activeCharacter = savedCreaturePickup;
                                creatureInventory = creaturePickupPlan.Inventory;
                                groundDungeonItems.Remove(groundId);
                                var creaturePickup =
                                    GameProtocolEngine.CreateInventoryItemPickup(
                                        groundId,
                                        localUserId,
                                        creaturePickupPlan.DestinationSlot);
                                await creaturePickup.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, creaturePickup);

                                foreach (var creatureRefresh in
                                         CreateCreatureInventoryPackets(
                                             creatureInventory.Values))
                                {
                                    await creatureRefresh.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, creatureRefresh);
                                }

                                continue;
                            }

                            if (itemCatalog.TryGetDefinition(
                                    groundItem.ItemId,
                                    out groundDefinition)
                                && groundDefinition.InventoryCategory
                                    == ItemInventoryCategory.Avatar)
                            {
                                var preservedAvatarItem = groundItem.PreservedItem;
                                var avatarGroundItem = new CharacterMailAttachmentRecord(
                                    groundItem.ItemId,
                                    groundItem.CountOrValue,
                                    State: preservedAvatarItem?.State ?? 0,
                                    Durability: preservedAvatarItem?.Durability ?? 1_000,
                                    SealState: preservedAvatarItem?.SealState ?? 0,
                                    AvatarRemainingSeconds:
                                        preservedAvatarItem?.AvatarRemainingSeconds,
                                    AvatarAbilityIndex:
                                        preservedAvatarItem?.AvatarAbilityIndex,
                                    InstanceId: preservedAvatarItem?.InstanceId
                                        ?? Guid.Empty);
                                if (!CharacterAvatarInventoryPlanner.TryPlace(
                                        avatarInventory,
                                        avatarGroundItem,
                                        itemCatalog,
                                        out var avatarPickupPlan,
                                        out var avatarPickupFailure))
                                {
                                    logger.LogInformation(
                                        "Ground Avatar item {GroundId} cannot enter Avatar inventory ({Failure}); it remains in the room.",
                                        groundId,
                                        avatarPickupFailure);
                                    continue;
                                }

                                var savedAvatarPickup =
                                    await store.SaveCharacterItemSpacesAsync(
                                        accountName,
                                        activeCharacter.Id,
                                        currentGold,
                                        mainInventory.Values,
                                        equippedInventory.Values,
                                        warehouseInventory.Values,
                                        serverToken,
                                        avatarInventory: avatarPickupPlan.Inventory.Values);
                                if (savedAvatarPickup is null)
                                {
                                    logger.LogWarning(
                                        "Avatar item pickup {GroundId} was not persisted; the ground item was retained.",
                                        groundId);
                                    continue;
                                }

                                activeCharacter = savedAvatarPickup;
                                avatarInventory = avatarPickupPlan.Inventory;
                                groundDungeonItems.Remove(groundId);
                                var avatarPickup =
                                    GameProtocolEngine.CreateInventoryItemPickup(
                                        groundId,
                                        localUserId,
                                        avatarPickupPlan.DestinationSlot);
                                await avatarPickup.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, avatarPickup);

                                var avatarRefresh = CreateAvatarInventoryPacket(
                                    avatarInventory.Values);
                                await avatarRefresh.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, avatarRefresh);
                                continue;
                            }

                            if (!DungeonPickupPlanner.TryPlanItem(
                                    mainInventory,
                                    groundItem,
                                    itemCatalog,
                                    GetInventoryWeightLimit(activeCharacter),
                                    out var pickupPlan,
                                    out var pickupFailure))
                            {
                                logger.LogInformation(
                                    "Ground item {GroundId} cannot enter the inventory ({Failure}); it remains in the room.",
                                    groundId,
                                    pickupFailure);
                                continue;
                            }

                            var savedInventory = await store.SaveCharacterInventoryAsync(
                                accountName,
                                activeCharacter.Id,
                                currentGold,
                                pickupPlan.Inventory.Values,
                                equippedInventory.Values,
                                serverToken);
                            if (savedInventory is null)
                            {
                                logger.LogWarning(
                                    "Item pickup {GroundId} was not persisted; the ground item was retained.",
                                    groundId);
                                continue;
                            }

                            activeCharacter = savedInventory;
                            mainInventory = pickupPlan.Inventory;
                            groundDungeonItems.Remove(groundId);
                            var itemPickup = GameProtocolEngine.CreateInventoryItemPickup(
                                groundId,
                                localUserId,
                                pickupPlan.DestinationSlot);
                            await itemPickup.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, itemPickup);

                            var itemRefresh = CreateMainInventoryPacket(
                                currentGold,
                                currentVictoryPoints,
                                mainInventory.Values,
                                pickupPlan.DestinationSlot);
                            await itemRefresh.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, itemRefresh);
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.SetPlayResultCommand)
                        {
                            if (tutorialDungeonActive)
                            {
                                logger.LogInformation(
                                    "Ignored normal settlement command for tutorial dungeon {DungeonId}; waiting for tutorial module {ModuleId}.",
                                    currentDungeonId,
                                    GameProtocolEngine.TutorialCompletionModule);
                                continue;
                            }

                            if (dungeonClearEnabled && !dungeonResultSent)
                            {
                                var hasClearScore = DungeonClearScore.TryParse(
                                    request.Body,
                                    out var clearScore);
                                var resultCode = hasClearScore
                                    ? clearScore.CalculatedResultCode
                                    : (byte)0;
                                var clientResultValue = request.Body.Length >= 6
                                    ? BinaryPrimitives.ReadUInt32LittleEndian(
                                        request.Body.AsSpan(2, sizeof(uint)))
                                    : 0u;
                                var playResult = GameProtocolEngine.CreatePlayResult(
                                    localUserId,
                                    clientResultValue);
                                await playResult.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, playResult);

                                if (await SettleDungeonCreatureHungerAsync(
                                        notifyClient: true,
                                        serverToken))
                                {
                                    await GrantDungeonCreatureExperienceAsync();
                                }
                                else
                                {
                                    dungeonConsumedFatigue = 0;
                                }
                                dungeonCreatureStomachCheckpointSeconds = null;

                                var hasDungeonDefinition =
                                    dungeonCatalog.TryGetDefinition(
                                        currentDungeonId,
                                        out var settledDungeon);
                                var clearGrantedSkillPoints = 0;
                                if (dungeonClearExperience is null)
                                {
                                    // Clear EXP is a separate reason from the
                                    // per-monster grants accumulated above.
                                    var calculatedClearExperience =
                                        activeCharacter is not null
                                        && hasDungeonDefinition
                                            ? dungeonExperienceCatalog.CalculateClearExperience(
                                                activeCharacter.Level,
                                                settledDungeon,
                                                currentDungeonDifficulty,
                                                resultCode,
                                                dungeonNormalMonsterKills
                                                + dungeonChampionMonsterKills
                                                + dungeonBossMonsterKills,
                                                partyMemberCount: 1,
                                                bonuses: new DungeonClearBonusContext(
                                                    HasAvatar: avatarInventory.Values.Any(item =>
                                                        itemCatalog.TryGetDefinition(item.ItemId, out var avatar)
                                                        && CharacterAvatarInventoryLayout.IsCompatibleWornSlot(avatar, item.Slot)),
                                                    HasCreature: creatureInventory.TryGetValue(
                                                        CharacterCreatureInventoryLayout.EquippedCreatureSlot,
                                                        out var clearCreature) && clearCreature.CountOrValue != 0,
                                                    HasBlackDiamond: HasActivePremiumService(GameProtocolEngine.BlackDiamondServiceType),
                                                    MentorBonusRate: DungeonClearBonusContext.GetMentorBonusRate(
                                                        activeCharacter, characterSessions.IsOnline),
                                                    EventBonusRate: dungeonExperienceCatalog.GetEventBonusRate(DateTimeOffset.UtcNow),
                                                    ChannelBonusRate: dungeonExperienceCatalog.GetChannelBonusRate(currentDungeonId)))
                                            : GameDungeonClearExperienceBreakdown.Empty;

                                    if (activeCharacter is not null
                                        && (dungeonMonsterExperienceGained > 0
                                            || calculatedClearExperience.TotalExperience > 0))
                                    {
                                        var clearProgression = experienceCatalog.Grant(
                                            activeCharacter,
                                            calculatedClearExperience.TotalExperience);
                                        clearGrantedSkillPoints =
                                            spCatalog.CalculateLevelUpReward(
                                                clearProgression.PreviousLevel,
                                                clearProgression.Level);
                                        var settledSp = checked(
                                            currentSp + clearGrantedSkillPoints);
                                        var savedProgression =
                                            await store.SaveCharacterProgressionAsync(
                                                accountName,
                                                activeCharacter.Id,
                                                clearProgression.Level,
                                                clearProgression.Experience,
                                                settledSp,
                                                serverToken);
                                        if (savedProgression is null)
                                        {
                                            throw new InvalidOperationException(
                                                $"Could not persist dungeon settlement for character {activeCharacter.Id}.");
                                        }

                                        currentSp = settledSp;
                                        activeCharacter = savedProgression;
                                        dungeonLeveledUp |= clearProgression.LeveledUp;
                                        if (clearProgression.LeveledUp)
                                        {
                                            logger.LogInformation(
                                                "Dungeon clear advanced character level {OldLevel}->{Level} and granted {SkillPoints} SP.",
                                                clearProgression.PreviousLevel,
                                                clearProgression.Level,
                                                clearGrantedSkillPoints);
                                        }
                                    }

                                    // A non-null value is the per-run settlement marker.
                                    // Cache it only after progression persistence succeeds.
                                    dungeonClearExperience = calculatedClearExperience;
                                }

                                var settledClearExperience =
                                    dungeonClearExperience
                                    ?? GameDungeonClearExperienceBreakdown.Empty;
                                var generationLevel = hasDungeonDefinition
                                    ? settledDungeon.BasisLevel
                                    : checked((byte)Math.Clamp(
                                        activeCharacter?.Level ?? 1,
                                        1,
                                        byte.MaxValue));
                                dungeonClearReward ??= clearRewardGenerator.Generate(
                                    generationLevel,
                                    currentDungeonDifficulty,
                                    HasActivePremiumService(
                                        GameProtocolEngine.BlackDiamondServiceType),
                                    !hasDungeonDefinition || settledDungeon.GoldCardUse,
                                    partyMemberCount: 1,
                                    normalMonsterKills: dungeonNormalMonsterKills,
                                    championMonsterKills: dungeonChampionMonsterKills,
                                    bossMonsterKills: dungeonBossMonsterKills);
                                var clearReward =
                                    GameProtocolEngine.CreateClearDungeonReward(
                                        localUserId,
                                        dungeonClearReward.GoldCardCost,
                                        dungeonClearReward.FreeGoldAmount,
                                        dungeonClearReward.FreeItemId,
                                        goldCardEnabled: dungeonClearReward.GoldCardEnabled,
                                        goldItemId: dungeonClearReward.GoldItemId,
                                        premiumCardEnabled: HasActivePremiumService(
                                            GameProtocolEngine.BlackDiamondServiceType),
                                        premiumItemId: dungeonClearReward.PremiumItemId,
                                        experience: settledClearExperience);
                                await clearReward.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, clearReward);
                                logger.LogInformation(
                                    "Dungeon {DungeonId} clear-card rewards prepared for user {UserId}: normalKills={NormalKills}, championKills={ChampionKills}, bossKills={BossKills}, championAnimationCount={ChampionAnimationCount}, freeGold={FreeGold}, freeItem={FreeItemId}, premiumItem={PremiumItemId}, goldItem={GoldItemId}, partyMemberCount={PartyMemberCount}.",
                                    currentDungeonId,
                                    localUserId,
                                    dungeonNormalMonsterKills,
                                    dungeonChampionMonsterKills,
                                    dungeonBossMonsterKills,
                                    dungeonChampionRewardOrdinal,
                                    dungeonClearReward.FreeGoldAmount,
                                    dungeonClearReward.FreeItemId,
                                    dungeonClearReward.PremiumItemId,
                                    dungeonClearReward.GoldItemId,
                                    1);
                                dungeonResultSent = true;
                                if (dungeonMonsterExperienceGained > 0
                                    || settledClearExperience.TotalExperience > 0)
                                {
                                    await SendCurrentEquipmentStateAsync();
                                    if (dungeonLeveledUp)
                                    {
                                        await SendCurrentSkillInfoAsync();
                                        await ValidateAndCorrectCharacterStatsAsync(
                                            "dungeon clear experience level-up");
                                        await SendAcceptableQuestListAsync();
                                    }
                                }

                                if (hasClearScore
                                    && activeCharacter is not null
                                    && DungeonDifficultyProgression.TryGetNextDifficulty(
                                        currentDungeonDifficulty,
                                        resultCode,
                                        out var nextDifficulty))
                                {
                                    var previousMaximum =
                                        DungeonDifficultyProgression.GetMaximumDifficulty(
                                            activeCharacter.DungeonProgress,
                                            currentDungeonId);
                                    if (previousMaximum < nextDifficulty)
                                    {
                                        var savedDifficulty =
                                            await store.UnlockNextDungeonDifficultyAsync(
                                                accountName,
                                                activeCharacter.Id,
                                                currentDungeonId,
                                                currentDungeonDifficulty,
                                                serverToken);
                                        if (savedDifficulty is not null)
                                        {
                                            activeCharacter = savedDifficulty;
                                            // DF2008 uses an incremental NOTI 5 while the client is
                                            // still in dungeon state. The client compares this one
                                            // record with its cached permission, opens the difficulty
                                            // unlock window, and then updates the cache.
                                            var difficultyUnlocked =
                                                GameProtocolEngine.CreateDungeonPermissions(
                                                    [new GameDungeonPermissionEntry(
                                                        currentDungeonId,
                                                        nextDifficulty)]);
                                            await difficultyUnlocked.WriteAsync(
                                                stream,
                                                serverToken);
                                            Interlocked.Increment(
                                                ref runtimeSession.SentPackets);
                                            LogPacket(
                                                "TX",
                                                runtimeSession,
                                                difficultyUnlocked);
                                            logger.LogInformation(
                                                "Unlocked dungeon {DungeonId} difficulty {Difficulty} after clearing difficulty {ClearedDifficulty} with result code {ResultCode}.",
                                                currentDungeonId,
                                                nextDifficulty,
                                                currentDungeonDifficulty,
                                                resultCode);
                                        }
                                        else
                                        {
                                            logger.LogWarning(
                                                "Could not persist dungeon {DungeonId} difficulty {Difficulty} for character {CharacterId}.",
                                                currentDungeonId,
                                                nextDifficulty,
                                                activeCharacter.Id);
                                        }
                                    }
                                }

                                if (!hasClearScore)
                                {
                                    logger.LogWarning(
                                        "Dungeon {DungeonId} settlement did not contain the 24-byte score layout; higher difficulty rank gates used result code zero.",
                                        currentDungeonId);
                                }
                                else if (!clearScore.IsWireResultCodeConsistent)
                                {
                                    logger.LogWarning(
                                        "Dungeon {DungeonId} ignored inconsistent client result code {WireResultCode}; calculated {CalculatedResultCode} from score {CalculatedScore}.",
                                        currentDungeonId,
                                        clearScore.WireResultCode,
                                        clearScore.CalculatedResultCode,
                                        clearScore.CalculatedScore);
                                }

                                logger.LogInformation(
                                    "Dungeon {DungeonId} settlement sent for user {UserId}, result {ResultValue}, resultCode={ResultCode}, goldCost={GoldCost}, monsterExp={MonsterExperience}, clearExp={ClearExperience}, base={BaseExperience}, rank={RankBonus}, party={PartyBonus}, mentor={MentorBonus}, avatar={AvatarBonus}, creature={CreatureBonus}, event={EventBonus}, blackDiamond={BlackDiamondBonus}, channel={ChannelBonus}",
                                    currentDungeonId,
                                    localUserId,
                                    clientResultValue,
                                    resultCode,
                                    dungeonClearReward.GoldCardCost,
                                    dungeonMonsterExperienceGained,
                                    settledClearExperience.TotalExperience,
                                    settledClearExperience.BaseExperience,
                                    settledClearExperience.RankBonus,
                                    settledClearExperience.PartyBonus,
                                    settledClearExperience.MentorBonus,
                                    settledClearExperience.AvatarBonus,
                                    settledClearExperience.CreatureBonus,
                                    settledClearExperience.EventBonus,
                                    settledClearExperience.BlackDiamondBonus,
                                    settledClearExperience.ChannelBonus);
                            }
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.ScoreScrollStateCommand)
                        {
                            if (tutorialDungeonActive)
                            {
                                if (dungeonClearEnabled && aliveDungeonMonsters.Count == 0)
                                {
                                    await CompleteTutorialAndReturnAsync(
                                        "tutorial-score-scroll-fallback");
                                }
                                else
                                {
                                    logger.LogInformation(
                                        "Ignored tutorial score-scroll command before the tutorial room was cleared.");
                                }
                                continue;
                            }

                            if (dungeonResultSent && !dungeonCardLayoutSent)
                            {
                                dungeonCardLayoutSent = true;
                                var scoreScrollReply =
                                    GameProtocolEngine.CreateScoreScrollStateReply();
                                await scoreScrollReply.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, scoreScrollReply);
                                logger.LogInformation(
                                    "Dungeon {DungeonId} score scroll acknowledged for user {UserId}",
                                    currentDungeonId,
                                    localUserId);
                            }
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.TutorialCompletionCommand)
                        {
                            var tutorialModuleReply = GameProtocolEngine.CreateCommandReply(
                                request,
                                success: true);
                            await tutorialModuleReply.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, tutorialModuleReply);

                            if (tutorialDungeonActive
                                && dungeonClearEnabled
                                && aliveDungeonMonsters.Count == 0
                                && GameProtocolEngine.IsTutorialCompletionModule(request.Body))
                            {
                                await CompleteTutorialAndReturnAsync(
                                    "tutorial-final-module");
                            }
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.CardSelectRightStateCommand)
                        {
                            if (dungeonCardLayoutSent && !dungeonCardRightsSent)
                            {
                                dungeonCardRightsSent = true;
                                dungeonCardSelectionDeadline =
                                    DateTimeOffset.UtcNow
                                    + ClearRewardCardSelectionTimeout;
                                var cardRights =
                                    GameProtocolEngine.CreateCardSelectRightState();
                                await cardRights.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, cardRights);
                                logger.LogInformation(
                                    "Dungeon {DungeonId} card selection rights sent for user {UserId}; automatic free-card selection in {TimeoutSeconds}s",
                                    currentDungeonId,
                                    localUserId,
                                    ClearRewardCardSelectionTimeout.TotalSeconds);
                            }
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.SelectCardCommand)
                        {
                            if (dungeonCardRightsSent && dungeonClearReward is not null)
                            {
                                var cardType = request.Body.Length >= 3
                                    ? request.Body[2]
                                    : (byte)0;
                                var selectedCardIndex = request.Body.Length >= 4
                                    ? request.Body[3]
                                    : (byte)0;
                                var inventoryChanged = false;
                                var selectionAccepted = false;
                                ushort? selectedRewardItemId = null;
                                if (selectedCardIndex >= 4)
                                {
                                    logger.LogWarning(
                                        "Ignored invalid dungeon card index {CardIndex} from user {UserId}",
                                        selectedCardIndex,
                                        localUserId);
                                }
                                else if (cardType == 0 && !dungeonFreeCardIndex.HasValue)
                                {
                                    var freeGrant = await GrantFreeClearRewardAsync(
                                        selectedCardIndex);
                                    if (freeGrant.Saved)
                                    {
                                        selectionAccepted = true;
                                        selectedRewardItemId = freeGrant.RewardItemId;
                                        inventoryChanged = freeGrant.InventoryChanged;
                                    }
                                }
                                else if (cardType == 1
                                    && !dungeonGoldCardIndex.HasValue
                                    && dungeonClearReward.GoldCardEnabled
                                    && dungeonClearReward.GoldCardCost <= int.MaxValue
                                    && currentGold >= (int)dungeonClearReward.GoldCardCost)
                                {
                                    var goldGrant = await SaveClearRewardEquipmentAsync(
                                        currentGold - (int)dungeonClearReward.GoldCardCost,
                                        [dungeonClearReward.GoldItemId]);
                                    if (goldGrant.Saved)
                                    {
                                        dungeonGoldCardIndex = selectedCardIndex;
                                        selectionAccepted = true;
                                        selectedRewardItemId = goldGrant.Granted[0]
                                            ? dungeonClearReward.GoldItemId
                                            : null;
                                        inventoryChanged = true;
                                    }
                                }
                                else if (cardType == 0
                                    && dungeonFreeCardIndex == selectedCardIndex)
                                {
                                    selectionAccepted = true;
                                    selectedRewardItemId = dungeonClearReward.FreeItemId
                                        ?? dungeonClearReward.PremiumItemId;
                                }
                                else if (cardType == 1
                                    && dungeonGoldCardIndex == selectedCardIndex)
                                {
                                    selectionAccepted = true;
                                    selectedRewardItemId = dungeonClearReward.GoldItemId;
                                }
                                else if (cardType is not 0 and not 1)
                                {
                                    logger.LogWarning(
                                        "Ignored invalid dungeon card type {CardType} from user {UserId}",
                                        cardType,
                                        localUserId);
                                }

                                // CMD 74 supports only type 0 (free) and type 1
                                // (paid). A black-diamond character's second
                                // free reward is NOTI 35 group 3 at the same
                                // selected column and is settled with type 0.
                                await SendCardSelectionSnapshotAsync(inventoryChanged);
                                logger.LogInformation(
                                    "Dungeon {DungeonId} card type {CardType}, index {CardIndex} processed for user {UserId}; accepted={Accepted}, reward={RewardItemId}",
                                    currentDungeonId,
                                    cardType,
                                    selectedCardIndex,
                                    localUserId,
                                    selectionAccepted,
                                    selectedRewardItemId);
                            }
                            else
                            {
                                logger.LogWarning(
                                    "Ignored dungeon card selection before card rights for user {UserId}",
                                    localUserId);
                            }
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.GiveupGameCommand)
                        {
                            if (activeCharacter is { TutorialStarted: false })
                            {
                                var tutorialError = GameProtocolEngine.CreateCommandError(
                                    request,
                                    errorCode: 4);
                                await tutorialError.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, tutorialError);
                                logger.LogInformation(
                                    "Rejected tutorial give-up for character {CharacterId}.",
                                    activeCharacter.Id);
                                continue;
                            }

                            // The in-dungeon Return to Town button uses command
                            // 45 in DNF 1.0.1.9. It does not use EPLP (75),
                            // which is reserved for the post-settlement screen.
                            await ReturnToTownFromDungeonAsync(
                                "giveup-game",
                                forcedDungeonExit: true);
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.EplpCommand)
                        {
                            if (tutorialDungeonActive)
                            {
                                logger.LogInformation(
                                    "Ignored normal EPLP settlement command during the tutorial.");
                                continue;
                            }

                            var state = request.Body.Length >= 3
                                ? request.Body[2]
                                : (byte)0;
                            var option = request.Body.Length >= 4
                                ? request.Body[3]
                                : (byte)0;

                            if (state == 1
                                && dungeonCardRightsSent
                                && dungeonClearReward is not null
                                && !dungeonFreeCardIndex.HasValue)
                            {
                                if (!await TryAutomaticallySelectFreeCardAsync(
                                    "eplp-fallback"))
                                {
                                    logger.LogWarning(
                                        "Deferred dungeon {DungeonId} EPLP completion because automatic free-card settlement failed for user {UserId}",
                                        currentDungeonId,
                                        localUserId);
                                    continue;
                                }
                            }

                            var eplpReply =
                                GameProtocolEngine.CreateEplpReply(state, option);
                            await eplpReply.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, eplpReply);

                            if (state == 1)
                            {
                                var freeCardWasSelected = dungeonFreeCardIndex.HasValue;
                                await ReturnToTownFromDungeonAsync(
                                    freeCardWasSelected
                                        ? "settlement-after-card"
                                        : "settlement-without-card");
                            }
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.FinishLoadingCommand)
                        {
                            var loadingReply = GameProtocolEngine.CreateCommandReply(request, success: true);
                            await loadingReply.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, loadingReply);

                            // Command 40 tells the server that the local town
                            // scene has finished loading. DNF 1.0.1.9 then
                            // expects notification 30 to release its remaining
                            // town-side managers. This build's handler consumes
                            // no notification payload.
                            var loadingNotification = GameProtocolEngine.CreateFinishLoading();
                            await loadingNotification.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, loadingNotification);
                            // FinishLoading reconstructs the local map actor.
                            // Replay a persisted zero-stomach death marker for
                            // dungeon scenes even if this connection already
                            // reported the same transition in an earlier map.
                            await SendEquippedCreatureHungerAsync(
                                forceDeathNotification: currentDungeonId != 0);
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.DeleteCharacterCommand)
                        {
                            var deleted = false;
                            var slot = (byte)0;
                            if (TryReadDeleteCharacter(request.Body, out slot))
                            {
                                var delete = await store.DeleteCharacterAsync(
                                    accountUid,
                                    slot,
                                    serverToken);
                                deleted = delete.Deleted;
                            }

                            var deleteReply = deleted
                                ? GameProtocolEngine.CreateDeleteCharacterReply(slot)
                                : GameProtocolEngine.CreateCommandError(request, errorCode: 2);
                            await deleteReply.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, deleteReply);

                            if (deleted)
                            {
                                var userInfo = await CreateUserInfoAsync(accountUid, serverToken);
                                await userInfo.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, userInfo);
                            }

                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.CreateCharacterCommand)
                        {
                            var created = false;
                            if (TryReadCreateCharacter(request.Body, out var job, out var nameBytes))
                            {
                                var create = await store.CreateCharacterAsync(
                                    accountUid,
                                    new CreateCharacterRequest(ClientEncoding.GetString(nameBytes), job, 0, 1),
                                    serverToken);
                                created = create.Created;
                            }

                            var createReply = created
                                ? GameProtocolEngine.CreateCommandReply(request, success: true)
                                : GameProtocolEngine.CreateCommandError(request, errorCode: 2);
                            await createReply.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, createReply);

                            if (created)
                            {
                                var userInfo = await CreateUserInfoAsync(accountUid, serverToken);
                                await userInfo.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, userInfo);
                            }

                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.AntibotCommand)
                        {
                            // Fire-and-forget client report: the heartbeat counter
                            // advances without any answer ever coming back, so the
                            // report is consumed quietly instead of tripping the
                            // unknown-packet warning once per minute.
                            var parsedAntibot = AntibotReportParser.TryParse(
                                request.Body,
                                out var antibotReport);
                            logger.LogDebug(
                                "Antibot report seq={Seq} parsed={Parsed} kind={Kind} declaredBytes={Bytes}.",
                                antibotReport.Sequence,
                                parsedAntibot,
                                antibotReport.RecordKind,
                                antibotReport.DeclaredLength);
                            continue;
                        }

                        var gameReply = request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.CheckConnectionCommand
                            ? GameProtocolEngine.CreateChannelInfo(
                                ClientEncoding.GetBytes($"{options.Channel.Name} {options.Channel.ChannelNumber}"),
                                channelType: 1,
                                result: 1,
                                serverId: 1,
                                channelNumber: checked((byte)options.Channel.ChannelNumber),
                                seed: checked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                                [Encoding.ASCII.GetBytes(options.GameplayDatagram.Host)],
                                port1: checked((uint)options.GameplayDatagram.PrimaryPort),
                                port2: checked((uint)options.GameplayDatagram.SecondaryPort))
                            : GameProtocolEngine.Handle(request, protocolSession.SessionToken);
                        if (gameReply is null)
                        {
                            logger.LogWarning(
                                "No channel handler for packet type {Type}, protocol {ProtocolId}; frame retained in logs.",
                                request.Type, request.ProtocolId);
                            continue;
                        }

                        await gameReply.WriteAsync(stream, serverToken);
                        Interlocked.Increment(ref runtimeSession.SentPackets);
                        LogPacket("TX", runtimeSession, gameReply);
                        continue;
                    }

                    var reply = EntranceProtocolEngine.Handle(request, protocolSession, LoadContent());
                    if (reply is null)
                    {
                        logger.LogWarning(
                            "No entrance handler for packet type {Type}, protocol {ProtocolId}; frame retained in logs.",
                            request.Type, request.ProtocolId);
                        continue;
                    }

                    await reply.WriteAsync(stream, serverToken);
                    Interlocked.Increment(ref runtimeSession.SentPackets);
                    LogPacket("TX", runtimeSession, reply);
                }
            }
            catch (OperationCanceledException) when (serverToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Entrance session {SessionId} ended with an error.", runtimeSession.Id);
            }
            finally
            {
                if (activeCharacter is not null)
                {
                    try
                    {
                        if (currentDungeonId != 0
                            && dungeonCreatureStomachCheckpointSeconds
                                is long finalCheckpoint)
                        {
                            var finalNow = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                            var finalElapsedSeconds = checked((uint)Math.Min(
                                uint.MaxValue,
                                Math.Max(0L, finalNow - finalCheckpoint)));
                            if (finalElapsedSeconds != 0
                                && CreatureHungerPlanner.TrySettle(
                                    creatureInventory,
                                    finalElapsedSeconds,
                                    itemCatalog,
                                    out var finalHungerPlan)
                                && finalHungerPlan.Changed)
                            {
                                await store.SaveCharacterItemSpacesAsync(
                                    accountName,
                                    activeCharacter.Id,
                                    currentGold,
                                    mainInventory.Values,
                                    equippedInventory.Values,
                                    warehouseInventory.Values,
                                    CancellationToken.None,
                                    avatarInventory: avatarInventory.Values,
                                    creatureInventory:
                                        finalHungerPlan.Inventory.Values);
                            }
                        }

                        var saveTownId = currentDungeonId == 0
                            ? currentTownId
                            : dungeonReturnTownId;
                        var saveAreaId = currentDungeonId == 0
                            ? currentAreaId
                            : dungeonReturnAreaId;
                        var saveX = currentDungeonId == 0 ? currentX : dungeonReturnX;
                        var saveY = currentDungeonId == 0 ? currentY : dungeonReturnY;
                        var saveDirection = currentDungeonId == 0
                            ? currentDirection
                            : dungeonReturnDirection;
                        await store.SaveCharacterLocationAsync(
                            accountName,
                            activeCharacter.Id,
                            saveTownId,
                            saveAreaId,
                            saveX,
                            saveY,
                            saveDirection,
                            CancellationToken.None);
                    }
                    catch (Exception exception)
                    {
                        logger.LogWarning(
                            exception,
                            "Failed to persist the final location for character {CharacterId}.",
                            activeCharacter.Id);
                    }
                }

                partyCoordinator.OnSessionClosed(runtimeSession.Id);
                characterSessions.Release(activeCharacterLease);
                activeCharacterLease = null;
                characterSessions.DetachPacketChannel(runtimeSession.Id);
                gameplayDatagram.UnregisterChannelSession(runtimeSession.Id);
                runtime.Remove(runtimeSession.Id);
                logger.LogInformation("Entrance client {SessionId} disconnected.", runtimeSession.Id);
            }
        }
    }

    private async Task<GameServerPacket> CreateUserInfoAsync(uint accountUid, CancellationToken cancellationToken)
    {
        var characters = await store.GetCharactersAsync(accountUid, cancellationToken);
        var summaries = characters
            .Take(MaximumTransferRoster.MaximumVisibleCharacters)
            .Select(character => new GameCharacterSummary(
                character.Slot,
                EncodeCharacterName(character.Name),
                (byte)Math.Clamp(character.Job, byte.MinValue, byte.MaxValue),
                (byte)Math.Clamp(character.GrowType, 0, 15),
                (byte)Math.Clamp(character.Level, 1, byte.MaxValue),
                (character.Equipment ?? [])
                    .Where(item => item.ItemId != 0
                        && (item.Slot == 9
                            || itemCatalog.TryGetDefinition(
                                item.ItemId,
                                out var definition)
                            && CharacterAvatarInventoryLayout.IsCompatibleWornSlot(
                                definition,
                                item.Slot)))
                    .Select(ToGameInventoryEntry)
                    .ToArray(),
                character.CharacterNo,
                (byte)Math.Clamp(character.AwakeningType, byte.MinValue, byte.MaxValue),
                pvpExperienceCatalog.GetGradeForPoints(character.PvpPoints)))
            .ToArray();
        return GameProtocolEngine.CreateUserInfo(summaries);
    }

    private static int ToIncreaseStatusWireAmount(IncreaseStatusEffect effect) =>
        effect.Type is IncreaseStatusType.SkillPoints or IncreaseStatusType.Experience
            ? effect.Amount
            : checked(effect.Amount * 10);

    private static CreateCharacterMailRequest[] CreateOverflowMailRequests(
        IEnumerable<CharacterMailAttachmentRecord> items,
        string source) =>
        items.Select(item => new CreateCharacterMailRequest(
                "DFLegacy",
                $"{source}: delivered because the inventory was full or overweight.",
                Attachment: item))
            .ToArray();

    private static IReadOnlyList<GameSkillEntry> GetInitialSkills(CharacterRecord character)
    {
        var skills = InitialSkillsByJob.TryGetValue(character.Job, out var initial)
            ? initial.AsEnumerable()
            : [];

        // DebugConfig.txt defines its larger skill set for the level-60,
        // grow-type-2 swordman only. Merge it with the mandatory base skills
        // and keep the highest level when an id appears in both lists.
        if (character.Job == 0 && character.GrowType == 2 && character.Level >= 50)
        {
            skills = skills.Concat(DebugSwordmanSkills);
        }

        return skills
            .GroupBy(skill => skill.SkillId)
            .Select(group =>
            {
                var highest = group.OrderByDescending(skill => skill.Level).First();
                var stableSlot = group.FirstOrDefault(skill => skill.Slot.HasValue)?.Slot;
                return highest with { Slot = stableSlot ?? highest.Slot };
            })
            .OrderBy(skill => skill.Slot ?? byte.MaxValue)
            .ThenBy(skill => skill.SkillId)
            .ToArray();
    }

    private IEnumerable<GameServerPacket> CreateTownInitializationPackets(
        int skillPoints,
        IReadOnlyList<GameSkillEntry> panelSkills,
        int gold,
        uint victoryPoints,
        IEnumerable<CharacterItemRecord> inventory,
        IEnumerable<CharacterItemRecord> equipment,
        IEnumerable<CharacterItemRecord> avatarInventory,
        IEnumerable<CharacterItemRecord> warehouse,
        ushort warehouseCapacity,
        IEnumerable<CharacterItemRecord> creatureInventory,
        IReadOnlyList<ushort> acceptableQuestIds,
        IReadOnlyList<GameDungeonPermissionEntry> dungeonPermissions)
    {
        // Dungeon permissions are u16-counted (u16 id, u8 state) records, and
        // the acceptable-quest list is u8-counted. The stamina notification is
        // sent later, after AREA_USERS has created the local town actor.
        yield return GameProtocolEngine.CreateDungeonPermissions(dungeonPermissions);

        yield return CreateMainInventoryPacket(gold, victoryPoints, inventory);
        yield return CreateAvatarInventoryPacket(avatarInventory);
        yield return CreateWarehousePacket(warehouse, warehouseCapacity);
        foreach (var creaturePacket in CreateCreatureInventoryPackets(creatureInventory))
        {
            yield return creaturePacket;
        }
        yield return GameProtocolEngine.CreateSkillInfo(
            (ushort)Math.Clamp(skillPoints, ushort.MinValue, ushort.MaxValue),
            panelSkills);
        yield return GameProtocolEngine.CreateAcceptableQuestList(acceptableQuestIds);
    }

    internal static IReadOnlyList<(ushort ItemId, uint Count)>
        GetStarterInventoryItems(uint accountUid) =>
        AccountUidRules.IsTest(accountUid)
            ? TestStarterInventoryItems
            : AccountUidRules.IsNormal(accountUid)
                ? NormalStarterInventoryItems
                : [];

    private List<CharacterItemRecord> CreateStarterInventory(uint accountUid) =>
        GetStarterInventoryItems(accountUid)
            .Select((item, index) => CharacterItemIdentity.Ensure(
                new CharacterItemRecord(
                    Slot: checked((ushort)(index + 9)),
                    ItemId: item.ItemId,
                    CountOrValue: item.Count,
                    Durability: itemCatalog.GetInitialDurability(item.ItemId)),
                itemCatalog))
            .ToList();

    private bool TryPlanLegacyPremiumContractRedemption(
        IReadOnlyDictionary<ushort, CharacterItemRecord> inventory,
        out Dictionary<ushort, CharacterItemRecord> plannedInventory,
        out IReadOnlyList<PremiumServicePurchaseGrant> grants)
    {
        plannedInventory = inventory.ToDictionary(pair => pair.Key, pair => pair.Value);
        var daysByService = new Dictionary<byte, long>();
        foreach (var item in inventory.Values)
        {
            if (!ceraShopCatalog.TryGetPremiumContract(item.ItemId, out var contract)
                || item.CountOrValue == 0)
            {
                continue;
            }

            var itemDays = (long)contract.Days * item.CountOrValue;
            daysByService[contract.ServiceType] = Math.Min(
                int.MaxValue,
                daysByService.GetValueOrDefault(contract.ServiceType) + itemDays);
            plannedInventory.Remove(item.Slot);
        }

        grants = daysByService
            .Select(pair => new PremiumServicePurchaseGrant(
                pair.Key,
                checked((int)pair.Value)))
            .ToArray();
        return grants.Count > 0;
    }

    private GameServerPacket CreateMainInventoryPacket(
        int gold,
        uint victoryPoints,
        IEnumerable<CharacterItemRecord> inventory,
        ushort? preferredLastSlot = null)
    {
        var entries = inventory
            .Where(item => item.ItemId != 0
                && !CharacterWarehouseProgression.TryGetItemTargetCapacity(
                    item.ItemId,
                    out _)
                && (item.Slot >= CharacterInventoryLayout.QuickSlotStart
                    || (item.Slot == CharacterInventoryLayout.RevivalCoinSlot
                        && item.ItemId
                            == CharacterInventoryLayout.RevivalCoinItemId)))
            .OrderBy(item => preferredLastSlot.HasValue
                && item.Slot == preferredLastSlot.Value
                    ? 1
                    : 0)
            .ThenBy(item => item.Slot)
            .Select(ToGameInventoryEntry)
            .ToArray();
        return GameProtocolEngine.CreateMainItemList(
            checked((uint)Math.Max(0, gold)),
            victoryPoints,
            entries);
    }

    private GameServerPacket CreateWarehousePacket(
        IEnumerable<CharacterItemRecord> warehouse,
        ushort warehouseCapacity)
    {
        var entries = warehouse
            .Where(item => item.Slot < warehouseCapacity
                && item.ItemId != 0
                && !CharacterWarehouseProgression.TryGetItemTargetCapacity(
                    item.ItemId,
                    out _))
            .OrderBy(item => item.Slot)
            .Select(ToGameInventoryEntry)
            .ToArray();
        return GameProtocolEngine.CreateItemList(
            2,
            entries,
            warehouseCapacity);
    }

    private GameServerPacket CreateAvatarInventoryPacket(
        IEnumerable<CharacterItemRecord> avatarInventory)
    {
        var entries = avatarInventory
            .Where(item => CharacterAvatarInventoryLayout.IsBagSlot(item.Slot)
                && item.ItemId != 0)
            .OrderBy(item => item.Slot)
            .Select(ToGameInventoryEntry)
            .ToArray();
        return GameProtocolEngine.CreateItemList(1, entries);
    }

    private IEnumerable<GameServerPacket> CreateCreatureInventoryPackets(
        IEnumerable<CharacterItemRecord> creatureInventory)
    {
        var items = creatureInventory
            .Where(item => CharacterCreatureInventoryLayout.IsInventorySlot(item.Slot)
                && item.ItemId != 0)
            .OrderBy(item => item.Slot)
            .ToArray();
        var creatures = items
            .Where(item => itemCatalog.TryGetDefinition(item.ItemId, out var definition)
                && CharacterCreatureInventoryLayout.IsCreature(definition))
            .GroupBy(item => item.CountOrValue)
            .Select(group => group.First())
            .Where(item => item.CountOrValue != 0)
            .Select(item => new GameCreatureInfoEntry(
                item.CountOrValue,
                item.CreatureStomach ?? 100,
                item.CreatureExperience ?? 0,
                item.CreatureLevel ?? 1,
                EncodeCreatureName(item.CreatureName, item.ItemId),
                item.CreatureNoCharge ?? false))
            .ToArray();
        yield return GameProtocolEngine.CreateCreatureInfo(creatures);
        yield return CreateCreatureInventoryItemListPacket(items);
    }

    private GameServerPacket CreateCreatureInventoryItemListPacket(
        IEnumerable<CharacterItemRecord> creatureInventory)
    {
        var entries = creatureInventory
            .Where(item => CharacterCreatureInventoryLayout.IsInventorySlot(item.Slot)
                && item.ItemId != 0)
            .OrderBy(item => item.Slot)
            .Select(ToGameInventoryEntry)
            .ToArray();
        return GameProtocolEngine.CreateItemList(7, entries);
    }

    private byte[] EncodeCreatureName(string? name, ushort itemId)
    {
        if (string.IsNullOrWhiteSpace(name)
            && itemCatalog.TryGetDefinition(itemId, out var definition))
        {
            name = definition.Name;
        }

        name = string.IsNullOrWhiteSpace(name) ? "Creature" : name;
        var output = new byte[29];
        ClientEncoding.GetEncoder().Convert(
            name.AsSpan(),
            output.AsSpan(),
            flush: true,
            out _,
            out var bytesUsed,
            out _);
        return output.AsSpan(0, bytesUsed).ToArray();
    }

    private static byte GetEquippedCreatureLevel(
        IReadOnlyDictionary<ushort, CharacterItemRecord> creatureInventory) =>
        creatureInventory.TryGetValue(
                CharacterCreatureInventoryLayout.EquippedCreatureSlot,
                out var creature)
            ? creature.CreatureLevel ?? 1
            : (byte)0;

    private GameInventoryEntry[] CreateWornEquipmentSnapshot(
        IEnumerable<CharacterItemRecord> equipment,
        IReadOnlyDictionary<ushort, CharacterItemRecord> creatureInventory)
    {
        var snapshot = equipment
            .Where(item => item.Slot is < CharacterCreatureInventoryLayout.ClientEquippedCreatureSlot
                or > CharacterCreatureInventoryLayout.ClientEquippedArtifactGreenSlot)
            .Select(ToGameInventoryEntry)
            .ToList();
        var creatureSlots = new (ushort StorageSlot, ushort ClientSlot)[]
        {
            (CharacterCreatureInventoryLayout.EquippedCreatureSlot,
                CharacterCreatureInventoryLayout.ClientEquippedCreatureSlot),
            (CharacterCreatureInventoryLayout.EquippedArtifactRedSlot,
                CharacterCreatureInventoryLayout.ClientEquippedArtifactRedSlot),
            (CharacterCreatureInventoryLayout.EquippedArtifactBlueSlot,
                CharacterCreatureInventoryLayout.ClientEquippedArtifactBlueSlot),
            (CharacterCreatureInventoryLayout.EquippedArtifactGreenSlot,
                CharacterCreatureInventoryLayout.ClientEquippedArtifactGreenSlot)
        };
        foreach (var (storageSlot, clientSlot) in creatureSlots)
        {
            if (creatureInventory.TryGetValue(storageSlot, out var item))
            {
                snapshot.Add(ToGameInventoryEntry(item with { Slot = clientSlot }));
            }
        }

        return snapshot.OrderBy(item => item.Slot).ToArray();
    }

    private IEnumerable<GameServerPacket> CreateInventoryRefreshPackets(
        int gold,
        uint victoryPoints,
        IEnumerable<CharacterItemRecord> inventory,
        IEnumerable<CharacterItemRecord> equipment)
    {
        yield return CreateMainInventoryPacket(gold, victoryPoints, inventory);
    }

    private GameInventoryEntry ToGameInventoryEntry(CharacterItemRecord item)
    {
        var wire = CharacterItemWireProjection.Project(item, itemCatalog);
        return new GameInventoryEntry(
            item.Slot,
            item.ItemId,
            wire.AddInfo,
            wire.ItemAttr,
            wire.Durability,
            wire.SealState);
    }

    private GameMailEntry ToGameMailEntry(CharacterMailRecord mail)
    {
        GameMailAttachment? attachment = null;
        if (mail.Attachment is not null)
        {
            var wire = CharacterItemWireProjection.Project(
                mail.Attachment,
                itemCatalog);
            attachment = new GameMailAttachment(
                mail.Attachment.ItemId,
                wire.AddInfo,
                wire.ItemAttr,
                wire.Durability,
                wire.SealState);
        }

        return new GameMailEntry(
            mail.Id,
            EncodeMailField(mail.Sender, 29, "DFLegacy"),
            EncodeMailField(mail.Text, 511, string.Empty),
            checked((uint)Math.Max(0, mail.Gold)),
            attachment,
            mail.SentAt,
            mail.ExpiresAt,
            mail.State is 2 or 3 ? mail.State : (ushort)1);
    }

    private static CharacterItemRecord NormalizeAvatarInstance(
        CharacterItemRecord item) =>
        item with
        {
            CountOrValue = 1,
            Durability = 0,
            AvatarRemainingSeconds = item.AvatarRemainingSeconds ?? 0,
            AvatarAbilityIndex = item.AvatarAbilityIndex ?? 0
        };

    private static byte[] EncodeMailField(
        string value,
        int maximumBytes,
        string fallback)
    {
        value ??= fallback;
        var output = new byte[maximumBytes];
        ClientEncoding.GetEncoder().Convert(
            value.AsSpan(),
            output.AsSpan(),
            flush: true,
            out _,
            out var bytesUsed,
            out _);
        if (bytesUsed == 0 && fallback.Length != 0)
        {
            return ClientEncoding.GetBytes(fallback);
        }

        return output.AsSpan(0, bytesUsed).ToArray();
    }

    private static bool TryPlanMailAttachment(
        IReadOnlyDictionary<ushort, CharacterItemRecord> inventory,
        IReadOnlyDictionary<ushort, CharacterItemRecord> avatarInventory,
        IReadOnlyDictionary<ushort, CharacterItemRecord> creatureInventory,
        CharacterMailAttachmentRecord? attachment,
        ItemCatalog catalog,
        uint weightLimit,
        out Dictionary<ushort, CharacterItemRecord> plannedInventory,
        out Dictionary<ushort, CharacterItemRecord> plannedAvatarInventory,
        out Dictionary<ushort, CharacterItemRecord> plannedCreatureInventory,
        out byte? destinationListType,
        out ushort? destinationSlot)
    {
        plannedInventory = inventory.ToDictionary(pair => pair.Key, pair => pair.Value);
        plannedAvatarInventory = avatarInventory.ToDictionary(
            pair => pair.Key,
            pair => pair.Value);
        plannedCreatureInventory = creatureInventory.ToDictionary(
            pair => pair.Key,
            pair => pair.Value);
        destinationListType = null;
        destinationSlot = null;
        if (attachment is null)
        {
            return true;
        }

        if (CharacterWarehouseProgression.TryGetItemTargetCapacity(
                attachment.ItemId,
                out _))
        {
            destinationListType = 0;
            return true;
        }

        if (!catalog.TryGetDefinition(attachment.ItemId, out var definition))
        {
            return false;
        }

        if (definition.InventoryCategory == ItemInventoryCategory.Avatar)
        {
            if (!CharacterAvatarInventoryPlanner.TryPlace(
                    avatarInventory,
                    attachment,
                    catalog,
                    out var avatarPlacement,
                    out _))
            {
                return false;
            }

            plannedAvatarInventory = avatarPlacement.Inventory;
            destinationListType = 1;
            destinationSlot = avatarPlacement.DestinationSlot;
            return true;
        }

        if (definition.InventoryCategory == ItemInventoryCategory.Creature)
        {
            var creatureItem = new CharacterItemRecord(
                ushort.MaxValue,
                attachment.ItemId,
                attachment.CountOrValue,
                attachment.State,
                attachment.Durability,
                attachment.SealState,
                InstanceId: attachment.InstanceId);
            if (!CharacterCreatureInventoryPlanner.TryPlace(
                    creatureInventory,
                    creatureItem,
                    catalog,
                    preferredEmptySlot: null,
                    out var creaturePlacement,
                    out _))
            {
                return false;
            }

            plannedCreatureInventory = creaturePlacement.Inventory;
            destinationListType = 7;
            destinationSlot = creaturePlacement.DestinationSlot;
            return true;
        }

        if (!CharacterInventoryPlanner.TryPlace(
                inventory,
                attachment,
                catalog,
                weightLimit,
                out var placement,
                out _))
        {
            return false;
        }

        plannedInventory = placement.Inventory;
        destinationListType = 0;
        destinationSlot = placement.DestinationSlot;
        return true;
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

    private GameServerPacket CreateTownAppearancePacket(
        CharacterRecord character,
        ushort userId,
        IEnumerable<CharacterItemRecord> equipment,
        IEnumerable<CharacterItemRecord> creatureInventory,
        bool hasBlackDiamond)
    {
        var equipmentSnapshot = equipment.ToArray();

        return GameProtocolEngine.CreateCurrentCharacterInfo(
            new GameCharacterSummary(
                0,
                EncodeCharacterName(character.Name),
                (byte)Math.Clamp(character.Job, byte.MinValue, byte.MaxValue),
                (byte)Math.Clamp(character.GrowType, 0, 15),
                (byte)Math.Clamp(character.Level, 1, byte.MaxValue),
                AwakeningType: (byte)Math.Clamp(
                    character.AwakeningType,
                    byte.MinValue,
                    byte.MaxValue),
                PvpGrade: pvpExperienceCatalog.GetGradeForPoints(
                    character.PvpPoints)),
            userId,
            equipmentSnapshot
                .Where(item => item.Slot <= EquippedTitleSlot)
                .Select(ToGameInventoryEntry)
                .ToArray(),
            CreateEquippedCreatureAppearance(creatureInventory),
            hasBlackDiamond);
    }

    private GameCreatureAppearanceEntry? CreateEquippedCreatureAppearance(
        IEnumerable<CharacterItemRecord> creatureInventory)
    {
        var creature = creatureInventory.FirstOrDefault(item =>
            item.Slot == CharacterCreatureInventoryLayout.EquippedCreatureSlot
            && item.ItemId != 0);
        return creature is null
            ? null
            : new GameCreatureAppearanceEntry(
                creature.ItemId,
                EncodeCreatureName(creature.CreatureName, creature.ItemId),
                State: 1);
    }

    private bool TryPlanCreatureInventoryMove(
        IReadOnlyDictionary<ushort, CharacterItemRecord> warehouse,
        ushort warehouseCapacity,
        IReadOnlyDictionary<ushort, CharacterItemRecord> creatureInventory,
        byte sourceListType,
        ushort sourceSlot,
        int moveCount,
        byte destinationListType,
        ushort destinationSlot,
        out CreatureInventoryMovePlan plan,
        out string failure)
    {
        plan = null!;
        failure = string.Empty;
        if (sourceListType is not (2 or 3 or 7)
            || destinationListType is not (2 or 3 or 7)
            || sourceListType != 7 && destinationListType != 7)
        {
            failure = "Creature space only supports its native equipment slots and character storage";
            return false;
        }

        if (!IsCreatureMoveSlotValid(sourceListType, sourceSlot, warehouseCapacity)
            || destinationSlot != ushort.MaxValue
                && !IsCreatureMoveSlotValid(
                    destinationListType,
                    destinationSlot,
                    warehouseCapacity))
        {
            failure = "Creature move contains an invalid slot";
            return false;
        }

        var plannedWarehouse = warehouse.ToDictionary(pair => pair.Key, pair => pair.Value);
        var plannedCreature = creatureInventory.ToDictionary(
            pair => pair.Key,
            pair => pair.Value);
        var actualSourceList = sourceListType;
        var actualSourceClientSlot = sourceSlot;
        var actualDestinationList = destinationListType;
        var actualDestinationClientSlot = destinationSlot;
        var actualSourceStorageSlot = MapCreatureEndpointSlot(
            actualSourceList,
            actualSourceClientSlot);
        var actualDestinationStorageSlot = actualDestinationClientSlot == ushort.MaxValue
            ? ushort.MaxValue
            : MapCreatureEndpointSlot(
                actualDestinationList,
                actualDestinationClientSlot);
        var sourceSpace = GetCreatureMoveSpace(
            actualSourceList,
            plannedWarehouse,
            plannedCreature);
        var destinationSpace = GetCreatureMoveSpace(
            actualDestinationList,
            plannedWarehouse,
            plannedCreature);
        if (!sourceSpace.TryGetValue(actualSourceStorageSlot, out var sourceItem))
        {
            if (actualDestinationStorageSlot == ushort.MaxValue
                || !destinationSpace.TryGetValue(
                    actualDestinationStorageSlot,
                    out sourceItem))
            {
                failure = "Creature move source is empty";
                return false;
            }

            (actualSourceList, actualDestinationList) =
                (actualDestinationList, actualSourceList);
            (actualSourceClientSlot, actualDestinationClientSlot) =
                (actualDestinationClientSlot, actualSourceClientSlot);
            (actualSourceStorageSlot, actualDestinationStorageSlot) =
                (actualDestinationStorageSlot, actualSourceStorageSlot);
            sourceSpace = GetCreatureMoveSpace(
                actualSourceList,
                plannedWarehouse,
                plannedCreature);
            destinationSpace = GetCreatureMoveSpace(
                actualDestinationList,
                plannedWarehouse,
                plannedCreature);
        }

        if (!itemCatalog.TryGetDefinition(sourceItem.ItemId, out var sourceDefinition))
        {
            failure = "Creature move item is unknown";
            return false;
        }

        if (actualSourceList == 3 || actualDestinationList == 3)
        {
            sourceItem = CharacterItemSealing.UnsealWhenEquipped(
                sourceItem,
                sourceDefinition);
        }

        if (actualDestinationClientSlot == ushort.MaxValue)
        {
            actualDestinationClientSlot = FindCreatureMoveDestination(
                destinationSpace,
                actualDestinationList,
                sourceItem,
                sourceDefinition,
                actualSourceList == actualDestinationList
                    ? actualSourceClientSlot
                    : null,
                warehouseCapacity);
            actualDestinationStorageSlot = actualDestinationClientSlot == ushort.MaxValue
                ? ushort.MaxValue
                : MapCreatureEndpointSlot(
                    actualDestinationList,
                    actualDestinationClientSlot);
        }

        if (actualDestinationClientSlot == ushort.MaxValue
            || !IsCreatureMoveSlotValid(
                actualDestinationList,
                actualDestinationClientSlot,
                warehouseCapacity)
            || !IsCompatibleCreatureMoveSlot(
                actualDestinationList,
                sourceDefinition,
                actualDestinationClientSlot,
                warehouseCapacity))
        {
            failure = "Creature move destination is full or incompatible";
            return false;
        }

        if (actualSourceList == actualDestinationList
            && actualSourceStorageSlot == actualDestinationStorageSlot)
        {
            plan = new CreatureInventoryMovePlan(
                plannedWarehouse,
                plannedCreature,
                sourceDefinition.IsStackable ? sourceItem.CountOrValue : 1,
                destinationSlot == ushort.MaxValue
                    ? actualDestinationClientSlot
                    : destinationSlot,
                sourceItem.ItemId,
                EquippedSlotsChanged: false,
                EquippedCreatureChanged: false);
            return true;
        }

        destinationSpace.TryGetValue(actualDestinationStorageSlot, out var displacedItem);
        if (displacedItem is not null
            && (!itemCatalog.TryGetDefinition(
                    displacedItem.ItemId,
                    out var displacedDefinition)
                || !IsCompatibleCreatureMoveSlot(
                    actualSourceList,
                    displacedDefinition,
                    actualSourceClientSlot,
                    warehouseCapacity)))
        {
            failure = "Creature move would displace an incompatible item";
            return false;
        }

        if (displacedItem is not null
            && (actualSourceList == 3 || actualDestinationList == 3))
        {
            displacedItem = CharacterItemSealing.UnsealWhenEquipped(
                displacedItem,
                itemCatalog);
        }

        var availableQuantity = sourceDefinition.IsStackable
            ? sourceItem.CountOrValue
            : 1u;
        var requestedQuantity = moveCount <= 0
            ? availableQuantity
            : Math.Min(availableQuantity, checked((uint)moveCount));
        if (requestedQuantity == 0)
        {
            failure = "Creature move count is zero";
            return false;
        }

        var canStack = sourceDefinition.IsStackable
            && displacedItem is not null
            && displacedItem.ItemId == sourceItem.ItemId
            && displacedItem.State == sourceItem.State
            && displacedItem.Durability == sourceItem.Durability
            && displacedItem.SealState == sourceItem.SealState;
        if (canStack)
        {
            var stackLimit = CharacterInventoryPlanner.GetStackLimit(sourceDefinition);
            var available = stackLimit > displacedItem!.CountOrValue
                ? stackLimit - displacedItem.CountOrValue
                : 0;
            if (available == 0)
            {
                failure = "Creature destination stack is full";
                return false;
            }

            requestedQuantity = Math.Min(requestedQuantity, available);
            destinationSpace[actualDestinationStorageSlot] = displacedItem with
            {
                CountOrValue = displacedItem.CountOrValue + requestedQuantity
            };
            if (requestedQuantity == sourceItem.CountOrValue)
            {
                sourceSpace.Remove(actualSourceStorageSlot);
            }
            else
            {
                sourceSpace[actualSourceStorageSlot] = sourceItem with
                {
                    CountOrValue = sourceItem.CountOrValue - requestedQuantity
                };
            }
        }
        else if (sourceDefinition.IsStackable
            && displacedItem is null
            && requestedQuantity < sourceItem.CountOrValue)
        {
            sourceSpace[actualSourceStorageSlot] = sourceItem with
            {
                CountOrValue = sourceItem.CountOrValue - requestedQuantity
            };
            destinationSpace[actualDestinationStorageSlot] = sourceItem with
            {
                Slot = actualDestinationStorageSlot,
                CountOrValue = requestedQuantity
            };
        }
        else
        {
            sourceSpace.Remove(actualSourceStorageSlot);
            destinationSpace[actualDestinationStorageSlot] = sourceItem with
            {
                Slot = actualDestinationStorageSlot
            };
            if (displacedItem is not null)
            {
                sourceSpace[actualSourceStorageSlot] = displacedItem with
                {
                    Slot = actualSourceStorageSlot
                };
            }
        }

        var normalizedCreature = CharacterCreatureInventoryPlanner.Normalize(
            plannedCreature.Values,
            itemCatalog);
        if (normalizedCreature.OverflowItems.Count != 0)
        {
            failure = "Creature move normalization overflowed";
            return false;
        }

        var equippedChanged = Enumerable.Range(
                CharacterCreatureInventoryLayout.EquippedCreatureSlot,
                CharacterCreatureInventoryLayout.Capacity
                    - CharacterCreatureInventoryLayout.EquippedCreatureSlot)
            .Select(checkedSlot => checked((ushort)checkedSlot))
            .Any(slot => !Equals(
                creatureInventory.GetValueOrDefault(slot),
                normalizedCreature.Inventory.GetValueOrDefault(slot)));
        var equippedCreatureChanged = !Equals(
            creatureInventory.GetValueOrDefault(
                CharacterCreatureInventoryLayout.EquippedCreatureSlot),
            normalizedCreature.Inventory.GetValueOrDefault(
                CharacterCreatureInventoryLayout.EquippedCreatureSlot));
        plan = new CreatureInventoryMovePlan(
            plannedWarehouse,
            normalizedCreature.Inventory,
            requestedQuantity,
            destinationSlot == ushort.MaxValue
                ? actualDestinationClientSlot
                : destinationSlot,
            sourceItem.ItemId,
            equippedChanged,
            equippedCreatureChanged);
        return true;
    }

    private ushort FindCreatureMoveDestination(
        IReadOnlyDictionary<ushort, CharacterItemRecord> destination,
        byte destinationListType,
        CharacterItemRecord item,
        ItemDefinition definition,
        ushort? excludedSlot,
        ushort warehouseCapacity)
    {
        if (destinationListType == 3)
        {
            if (!CharacterCreatureInventoryLayout.TryGetEquippedSlot(
                    definition,
                    out var equippedStorageSlot)
                || destination.ContainsKey(equippedStorageSlot))
            {
                return ushort.MaxValue;
            }

            return MapCreatureStorageSlotToClient(equippedStorageSlot);
        }

        if (definition.IsStackable)
        {
            var stackLimit = CharacterInventoryPlanner.GetStackLimit(definition);
            var partial = destination.Values
                .Where(candidate => candidate.Slot != excludedSlot
                    && candidate.ItemId == item.ItemId
                    && candidate.State == item.State
                    && candidate.Durability == item.Durability
                    && candidate.SealState == item.SealState
                    && candidate.CountOrValue < stackLimit
                    && IsCompatibleCreatureMoveSlot(
                        destinationListType,
                        definition,
                        candidate.Slot,
                        warehouseCapacity))
                .OrderBy(candidate => candidate.Slot)
                .Select(candidate => candidate.Slot)
                .DefaultIfEmpty(ushort.MaxValue)
                .First();
            if (partial != ushort.MaxValue)
            {
                return partial;
            }
        }

        if (destinationListType == 2)
        {
            return FindFreeInventorySlot(
                destination,
                0,
                warehouseCapacity);
        }

        if (!CharacterCreatureInventoryLayout.TryGetBagRange(
                definition,
                out var start,
                out var end))
        {
            return ushort.MaxValue;
        }

        return FindFreeInventorySlot(destination, start, end);
    }

    private bool IsCompatibleCreatureMoveSlot(
        byte listType,
        ItemDefinition definition,
        ushort slot,
        ushort warehouseCapacity) => listType switch
    {
        2 => definition.InventoryCategory != ItemInventoryCategory.Avatar
            && slot < warehouseCapacity,
        3 => definition.InventoryCategory == ItemInventoryCategory.Creature
            && CharacterCreatureInventoryLayout.TryGetEquippedSlot(
                definition,
                out var equippedSlot)
            && equippedSlot == MapCreatureEndpointSlot(3, slot),
        7 => definition.InventoryCategory == ItemInventoryCategory.Creature
            && CharacterCreatureInventoryLayout.IsCompatibleSlot(definition, slot),
        _ => false
    };

    private static bool IsCreatureMoveSlotValid(
        byte listType,
        ushort slot,
        ushort warehouseCapacity) =>
        listType switch
        {
            2 => slot < warehouseCapacity,
            3 => slot is >= CharacterCreatureInventoryLayout.ClientEquippedCreatureSlot
                and <= CharacterCreatureInventoryLayout.ClientEquippedArtifactGreenSlot,
            7 => CharacterCreatureInventoryLayout.IsInventorySlot(slot),
            _ => false
        };

    private static Dictionary<ushort, CharacterItemRecord> GetCreatureMoveSpace(
        byte listType,
        Dictionary<ushort, CharacterItemRecord> warehouse,
        Dictionary<ushort, CharacterItemRecord> creatureInventory) =>
        listType is 3 or 7 ? creatureInventory : warehouse;

    private static ushort MapCreatureEndpointSlot(byte listType, ushort slot) =>
        listType == 3
            ? slot switch
            {
                CharacterCreatureInventoryLayout.ClientEquippedCreatureSlot =>
                    CharacterCreatureInventoryLayout.EquippedCreatureSlot,
                CharacterCreatureInventoryLayout.ClientEquippedArtifactRedSlot =>
                    CharacterCreatureInventoryLayout.EquippedArtifactRedSlot,
                CharacterCreatureInventoryLayout.ClientEquippedArtifactBlueSlot =>
                    CharacterCreatureInventoryLayout.EquippedArtifactBlueSlot,
                CharacterCreatureInventoryLayout.ClientEquippedArtifactGreenSlot =>
                    CharacterCreatureInventoryLayout.EquippedArtifactGreenSlot,
                _ => ushort.MaxValue
            }
            : slot;

    private static ushort MapCreatureStorageSlotToClient(ushort slot) => slot switch
    {
        CharacterCreatureInventoryLayout.EquippedCreatureSlot =>
            CharacterCreatureInventoryLayout.ClientEquippedCreatureSlot,
        CharacterCreatureInventoryLayout.EquippedArtifactRedSlot =>
            CharacterCreatureInventoryLayout.ClientEquippedArtifactRedSlot,
        CharacterCreatureInventoryLayout.EquippedArtifactBlueSlot =>
            CharacterCreatureInventoryLayout.ClientEquippedArtifactBlueSlot,
        CharacterCreatureInventoryLayout.EquippedArtifactGreenSlot =>
            CharacterCreatureInventoryLayout.ClientEquippedArtifactGreenSlot,
        _ => ushort.MaxValue
    };

    private sealed record CreatureInventoryMovePlan(
        Dictionary<ushort, CharacterItemRecord> Warehouse,
        Dictionary<ushort, CharacterItemRecord> CreatureInventory,
        uint MovedCount,
        ushort ReplyDestinationSlot,
        ushort ItemId,
        bool EquippedSlotsChanged,
        bool EquippedCreatureChanged);

    private static bool TryGetInventorySpace(
        byte listType,
        Dictionary<ushort, CharacterItemRecord> mainInventory,
        Dictionary<ushort, CharacterItemRecord> avatarInventory,
        Dictionary<ushort, CharacterItemRecord> equippedInventory,
        Dictionary<ushort, CharacterItemRecord> warehouseInventory,
        out Dictionary<ushort, CharacterItemRecord> space)
    {
        if (listType == 0)
        {
            space = mainInventory;
            return true;
        }

        if (listType == 3)
        {
            space = equippedInventory;
            return true;
        }

        if (listType == 1)
        {
            space = avatarInventory;
            return true;
        }

        if (listType == 2)
        {
            space = warehouseInventory;
            return true;
        }

        space = null!;
        return false;
    }

    private static ushort FindFreeInventorySlot(
        IReadOnlyDictionary<ushort, CharacterItemRecord> inventory,
        ushort firstSlot,
        int exclusiveUpperBound = 256)
    {
        for (var slot = (int)firstSlot; slot < exclusiveUpperBound; slot++)
        {
            if (!inventory.ContainsKey((ushort)slot))
            {
                return (ushort)slot;
            }
        }

        return ushort.MaxValue;
    }

    private static bool IsInventorySlotValid(
        byte listType,
        ushort slot,
        ushort warehouseCapacity) => listType switch
    {
        0 => CharacterInventoryLayout.IsMainItemSlot(slot),
        1 => CharacterAvatarInventoryLayout.IsBagSlot(slot),
        2 => slot < warehouseCapacity,
        3 => slot < 23,
        _ => false
    };

    private bool IsCompatibleMainInventorySlot(
        CharacterItemRecord item,
        ushort slot) =>
        itemCatalog.TryGetDefinition(item.ItemId, out var definition)
        && definition.InventoryCategory is not (
            ItemInventoryCategory.Avatar or ItemInventoryCategory.Creature)
        && CharacterInventoryLayout.IsCompatibleSlot(definition, slot);

    private bool IsCompatibleInventorySlot(
        byte listType,
        CharacterItemRecord item,
        ushort slot,
        ushort warehouseCapacity)
    {
        if (!itemCatalog.TryGetDefinition(item.ItemId, out var definition))
        {
            return false;
        }

        return listType switch
        {
            0 => definition.InventoryCategory is not (
                    ItemInventoryCategory.Avatar or ItemInventoryCategory.Creature)
                && CharacterInventoryLayout.IsCompatibleSlot(definition, slot),
            1 => definition.InventoryCategory == ItemInventoryCategory.Avatar
                && CharacterAvatarInventoryLayout.IsBagSlot(slot),
            2 => definition.InventoryCategory != ItemInventoryCategory.Avatar
                && slot < warehouseCapacity,
            3 when CharacterAvatarInventoryLayout.IsWornSlot(slot) =>
                CharacterAvatarInventoryLayout.IsCompatibleWornSlot(definition, slot),
            3 => slot < 23
                && definition.InventoryCategory == ItemInventoryCategory.Equipment,
            _ => false
        };
    }

    private ushort FindFreeCompatibleMainInventorySlot(
        IReadOnlyDictionary<ushort, CharacterItemRecord> inventory,
        CharacterItemRecord item)
    {
        if (!itemCatalog.TryGetDefinition(item.ItemId, out var definition))
        {
            return ushort.MaxValue;
        }

        if (CharacterInventoryLayout.TryGetCategoryRange(
                definition.InventoryCategory,
                out var start,
                out var end))
        {
            var categorySlot = FindFreeInventorySlot(inventory, start, end);
            if (categorySlot != ushort.MaxValue)
            {
                return categorySlot;
            }
        }

        return FindFreeInventorySlot(
            inventory,
            CharacterInventoryLayout.QuickSlotStart,
            CharacterInventoryLayout.QuickSlotEnd);
    }

    private ushort FindPartialStackSlot(
        IReadOnlyDictionary<ushort, CharacterItemRecord> inventory,
        CharacterItemRecord item,
        byte listType,
        ushort? excludedSlot)
    {
        if (listType is not (0 or 2)
            || !itemCatalog.TryGetDefinition(item.ItemId, out var definition)
            || !definition.IsStackable
            || listType == 2
                && definition.InventoryCategory == ItemInventoryCategory.Avatar)
        {
            return ushort.MaxValue;
        }

        var stackLimit = CharacterInventoryPlanner.GetStackLimit(definition);
        return inventory.Values
            .Where(candidate => candidate.Slot != excludedSlot
                && candidate.ItemId == item.ItemId
                && candidate.State == item.State
                && candidate.Durability == item.Durability
                && candidate.SealState == item.SealState
                && candidate.CountOrValue < stackLimit
                && (listType != 0
                    || CharacterInventoryLayout.IsCompatibleSlot(
                        definition,
                        candidate.Slot)))
            .OrderBy(candidate => candidate.Slot)
            .Select(candidate => candidate.Slot)
            .DefaultIfEmpty(ushort.MaxValue)
            .First();
    }

    private static bool TryReadInventoryMove(
        byte[] body,
        out byte sourceListType,
        out ushort sourceSlot,
        out int moveCount,
        out byte destinationListType,
        out ushort destinationSlot)
    {
        sourceListType = 0;
        sourceSlot = 0;
        moveCount = 0;
        destinationListType = 0;
        destinationSlot = 0;
        if (body.Length < 20)
        {
            return false;
        }

        sourceListType = body[2];
        var signedSourceSlot = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(3, 2));
        moveCount = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(7, 4));
        destinationListType = body[11];
        var signedDestinationSlot = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(12, 2));
        if (signedSourceSlot < 0 || moveCount < 0)
        {
            return false;
        }

        sourceSlot = (ushort)signedSourceSlot;
        destinationSlot = signedDestinationSlot < 0
            ? ushort.MaxValue
            : (ushort)signedDestinationSlot;
        return true;
    }

    internal static bool ShouldUseIncrementalMainMoveRefresh(
        byte sourceListType,
        ushort sourceSlot,
        byte destinationListType,
        ushort destinationSlot) =>
        sourceListType == 0
        && destinationListType == 0
        && (CharacterInventoryLayout.IsQuickSlot(sourceSlot)
            || CharacterInventoryLayout.IsQuickSlot(destinationSlot));

    private static bool TryReadSortItem(byte[] body, out byte itemSpace)
    {
        itemSpace = 0;
        if (body.Length < 3)
        {
            return false;
        }

        itemSpace = body[2];
        return true;
    }

    private static bool TryReadHatchCreature(
        byte[] body,
        out byte itemSpace,
        out ushort slot)
    {
        itemSpace = 0;
        slot = 0;
        if (body.Length < 5)
        {
            return false;
        }

        itemSpace = body[2];
        slot = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(3, 2));
        return true;
    }

    private static bool TryReadRenameCreature(
        byte[] body,
        out ushort cardSlot,
        out byte itemSpace,
        out byte[] nameBytes)
    {
        cardSlot = 0;
        itemSpace = 0;
        nameBytes = [];
        const int fixedLength = 9;
        if (body.Length < fixedLength)
        {
            return false;
        }

        cardSlot = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(2, 2));
        itemSpace = body[4];
        var nameLength = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(5, 4));
        if (nameLength is 0 or > 12
            || nameLength > int.MaxValue
            || body.Length != fixedLength + (int)nameLength)
        {
            return false;
        }

        nameBytes = body.AsSpan(fixedLength, (int)nameLength).ToArray();
        return !nameBytes.Contains((byte)0);
    }

    private static bool TryReadBuyItem(
        byte[] body,
        out ushort itemId,
        out int countOrQualitySeed)
    {
        itemId = 0;
        countOrQualitySeed = 1;
        if (body.Length < 4)
        {
            return false;
        }

        // Stackable purchases use the second value as the count. Equipment
        // purchases use it as the client's daily shop-quality seed; the
        // server remains authoritative and recomputes that seed at 06:00.
        // Later clients widened both values to i32, while this target build
        // also emits the compact u16/u16 form.
        if (body.Length >= 6)
        {
            var rawItemId = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(2, 4));
            if (rawItemId is > 0 and <= ushort.MaxValue)
            {
                itemId = (ushort)rawItemId;
                if (body.Length >= 10)
                {
                    countOrQualitySeed = BinaryPrimitives.ReadInt32LittleEndian(
                        body.AsSpan(6, 4));
                }

                return true;
            }
        }

        itemId = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(2, 2));
        if (body.Length >= 6)
        {
            countOrQualitySeed = BinaryPrimitives.ReadUInt16LittleEndian(
                body.AsSpan(4, 2));
        }
        else if (body.Length >= 5)
        {
            countOrQualitySeed = body[4];
        }

        return itemId != 0;
    }

    private static bool TryReadStackableItemUse(
        byte[] body,
        out ushort slot,
        out byte listType)
    {
        slot = ushort.MaxValue;
        listType = byte.MaxValue;
        if (body.Length < 5)
        {
            return false;
        }

        // DNF 1.0.1.9 USE_STACKABLE is sequence:u16,
        // slot:u16, list-type:u8.
        slot = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(2, 2));
        listType = body[4];
        return slot is >= 3 and < ushort.MaxValue;
    }

    private static bool TryReadConsumeItems(
        byte[] body,
        out byte listType,
        out ConsumeItemsRequestEntry[] entries)
    {
        listType = byte.MaxValue;
        entries = [];
        if (body.Length < 4)
        {
            return false;
        }

        // sequence:u16, list-type:u8, count:u8, followed by:
        // operation:u16, slot:u16, item-id:u16, requested-count:u32.
        // The client appends a four-byte command token after all entries.
        listType = body[2];
        var count = body[3];
        const int entrySize = sizeof(ushort) * 3 + sizeof(uint);
        if (count == 0 || count > 32 || body.Length < 4 + count * entrySize)
        {
            return false;
        }

        var parsed = new ConsumeItemsRequestEntry[count];
        var offset = 4;
        for (var index = 0; index < count; index++)
        {
            parsed[index] = new ConsumeItemsRequestEntry(
                BinaryPrimitives.ReadUInt16LittleEndian(
                    body.AsSpan(offset, sizeof(ushort))),
                BinaryPrimitives.ReadUInt16LittleEndian(
                    body.AsSpan(offset + 2, sizeof(ushort))),
                BinaryPrimitives.ReadUInt16LittleEndian(
                    body.AsSpan(offset + 4, sizeof(ushort))),
                BinaryPrimitives.ReadUInt32LittleEndian(
                    body.AsSpan(offset + 6, sizeof(uint))));
            offset += entrySize;
        }

        entries = parsed;
        return true;
    }

    private static bool TryReadCeraShopPurchase(
        byte[] body,
        out CeraShopPurchaseRequestItem[] products,
        out bool isGift)
    {
        products = [];
        isGift = false;
        if (body.Length < 4)
        {
            return false;
        }

        var offset = 2;
        var giftMode = body[offset++];
        if (giftMode > 1)
        {
            return false;
        }

        if (giftMode == 1)
        {
            isGift = true;
            if (body.Length < offset + sizeof(uint))
            {
                return false;
            }

            var recipientLength = BinaryPrimitives.ReadUInt32LittleEndian(
                body.AsSpan(offset, sizeof(uint)));
            offset += sizeof(uint);
            if (recipientLength is 0 or > 30
                || recipientLength > (uint)(body.Length - offset))
            {
                return false;
            }

            offset += checked((int)recipientLength);
        }

        if (offset >= body.Length)
        {
            return false;
        }

        var count = body[offset++];
        if (count == 0 || count > 32)
        {
            return false;
        }

        var requiredLength = offset + count * 7;
        if (body.Length != requiredLength)
        {
            return false;
        }

        var parsed = new CeraShopPurchaseRequestItem[count];
        for (var index = 0; index < count; index++)
        {
            var productType = body[offset];
            var attributeValue = BinaryPrimitives.ReadUInt16LittleEndian(
                body.AsSpan(offset + 1, sizeof(ushort)));
            var commodityNo = BinaryPrimitives.ReadUInt32LittleEndian(
                body.AsSpan(offset + 3, sizeof(uint)));
            if (commodityNo == 0)
            {
                return false;
            }

            parsed[index] = new CeraShopPurchaseRequestItem(
                productType,
                attributeValue,
                commodityNo);
            offset += 7;
        }

        products = parsed;
        return true;
    }

    private static bool TryReadRepairEquipment(
        byte[] body,
        out byte itemSpace,
        out ushort slot)
    {
        itemSpace = 0;
        slot = 0;
        if (body.Length < 5 || body[2] is not (0 or 3))
        {
            return false;
        }

        itemSpace = body[2];
        slot = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(3, 2));
        return slot == ushort.MaxValue
            || IsInventorySlotValid(
                itemSpace,
                slot,
                CharacterWarehouseProgression.InitialCapacity);
    }

    private static bool TryReadDecreaseDurability(
        byte[] body,
        out byte slot)
    {
        slot = 0;
        if (body.Length < 3)
        {
            return false;
        }

        slot = body[2];
        return true;
    }

    private static bool TryReadSellItem(
        byte[] body,
        out byte listType,
        out ushort slot,
        out int count)
    {
        listType = 0;
        slot = 0;
        count = 0;
        if (body.Length == 5 && body[2] is 0 or 3)
        {
            listType = body[2];
            slot = body[3];
            count = body[4];
            return count > 0;
        }

        if (body.Length >= 7 && body[2] is 0 or 3)
        {
            listType = body[2];
            var signedSlot = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(3, 2));
            count = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(5, 2));
            if (signedSlot < 0)
            {
                return false;
            }

            slot = (ushort)signedSlot;
            return count > 0;
        }

        if (body.Length < 6)
        {
            return false;
        }

        var legacySlot = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(2, 2));
        count = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(4, 2));
        if (legacySlot < 0)
        {
            return false;
        }

        slot = (ushort)legacySlot;
        return count > 0;
    }

    private static bool TryReadSetItemTradeState(
        byte[] body,
        out byte listType,
        out ushort slot,
        out ushort state)
    {
        listType = 0;
        slot = 0;
        state = 0;
        if (body.Length < 7 || body[2] is not (0 or 3))
        {
            return false;
        }

        listType = body[2];
        slot = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(3, 2));
        state = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(5, 2));
        return true;
    }

    private bool IsEquipmentItem(ushort itemId) => itemCatalog.IsEquipment(itemId);

    private readonly record struct CeraShopPurchaseRequestItem(
        byte ProductType,
        ushort AttributeValue,
        uint CommodityNo);
    private readonly record struct ConsumeItemsRequestEntry(
        ushort Operation,
        ushort Slot,
        ushort ItemId,
        uint RequestedCount);

    internal readonly record struct SendMessageRequest(
        byte MessageType,
        ushort TargetOrItemSpace,
        uint SlotOrReserved,
        byte[] MessageBytes,
        byte[] TargetNameBytes);

    private static byte[] EncodeCharacterName(string name)
    {
        var bytes = ClientEncoding.GetBytes(name);
        if (bytes.Length is > 0 and < 31)
        {
            return bytes;
        }

        return Encoding.ASCII.GetBytes(name.Length == 0 ? "DFLegacy" : name[..Math.Min(name.Length, 20)]);
    }

    internal static bool TryReadSendMessage(
        byte[] body,
        out SendMessageRequest request)
    {
        request = default;
        const int payloadOffset = sizeof(ushort);
        const int messageOffset = payloadOffset
            + sizeof(byte)
            + sizeof(ushort)
            + sizeof(uint)
            + sizeof(uint);
        if (body.Length < messageOffset)
        {
            return false;
        }

        var messageType = body[payloadOffset];
        var targetOrItemSpace = BinaryPrimitives.ReadUInt16LittleEndian(
            body.AsSpan(payloadOffset + sizeof(byte), sizeof(ushort)));
        var slotOrReserved = BinaryPrimitives.ReadUInt32LittleEndian(
            body.AsSpan(payloadOffset + sizeof(byte) + sizeof(ushort), sizeof(uint)));
        var messageLength = BinaryPrimitives.ReadUInt32LittleEndian(
            body.AsSpan(
                payloadOffset + sizeof(byte) + sizeof(ushort) + sizeof(uint),
                sizeof(uint)));
        if (messageLength is 0 or > 0xFF
            || messageLength > body.Length - messageOffset)
        {
            return false;
        }

        var messageBytes = body.AsSpan(messageOffset, (int)messageLength).ToArray();
        if (messageBytes.Contains((byte)0))
        {
            return false;
        }

        // Direct messages (type 18, and the type 1/7 chat-window whispers)
        // append a second CP936 dstr naming the chat partner: DNF.exe
        // sub_811C20 writes type 18 + zero u16 + zero u32 + text + partner
        // name (a 20-byte window buffer), and the shared sender sub_8495B0
        // appends the same name dstr for types 1 and 7. Every other type
        // ends at the text, so their bodies must not carry trailing bytes.
        var targetNameBytes = Array.Empty<byte>();
        if (messageType
            is (byte)GameMessageType.Type01LightGreen
                or (byte)GameMessageType.Type07LightGreen
                or (byte)GameMessageType.Type18DirectOutput)
        {
            var nameOffset = messageOffset + (int)messageLength;
            if (body.Length < nameOffset + sizeof(uint)
                || !TryReadTargetName(
                    body.AsSpan(nameOffset),
                    out targetNameBytes))
            {
                return false;
            }
        }
        else if (body.Length != messageOffset + (int)messageLength)
        {
            return false;
        }

        request = new SendMessageRequest(
            messageType,
            targetOrItemSpace,
            slotOrReserved,
            messageBytes,
            targetNameBytes);
        return true;
    }

    private static bool TryReadTargetName(
        ReadOnlySpan<byte> span,
        out byte[] nameBytes)
    {
        nameBytes = [];
        var nameLength = BinaryPrimitives.ReadUInt32LittleEndian(span);
        // sub_8495B0 rejects partner names above 0x1E bytes before sending.
        if (nameLength is 0 or > 0x1E
            || nameLength != span.Length - sizeof(uint))
        {
            return false;
        }

        nameBytes = span.Slice(sizeof(uint), (int)nameLength).ToArray();
        return !nameBytes.Contains((byte)0);
    }

    internal static bool TryReadLoginCredentials(
        byte[] body,
        out string name,
        out string? password)
    {
        name = "test";
        password = null;
        if (!TryReadLengthPrefixedBytes(body, 2, out var bytes, out var offset))
        {
            return false;
        }

        name = ClientEncoding.GetString(bytes);
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (offset == body.Length)
        {
            return true;
        }

        if (!TryReadLengthPrefixedBytes(body, offset, out var passwordBytes, out _))
        {
            return false;
        }

        // The current launcher-backed client appends machine/session metadata
        // after the length-prefixed password. Authentication only consumes the
        // two credential fields; requiring the password to end at the packet
        // boundary silently left the channel session on its former test-account
        // defaults.
        password = ClientEncoding.GetString(passwordBytes);
        return !string.IsNullOrEmpty(password);
    }

    private static bool TryReadCreateCharacter(byte[] body, out byte job, out byte[] nameBytes)
    {
        job = 0;
        nameBytes = [];
        if (body.Length < 7)
        {
            return false;
        }

        job = body[2];
        return TryReadLengthPrefixedBytes(body, 3, out nameBytes, out _)
            && nameBytes.Length is > 0 and < 31;
    }

    private static bool TryReadDeleteCharacter(byte[] body, out byte slot)
    {
        slot = 0;
        if (body.Length < 3)
        {
            return false;
        }

        slot = body[2];
        return slot < MaximumTransferRoster.MaximumVisibleCharacters;
    }

    private static bool TryReadMailboxExtract(byte[] body, out uint mailId)
    {
        mailId = 0;
        if (body.Length < 2 + sizeof(uint))
        {
            return false;
        }

        mailId = BinaryPrimitives.ReadUInt32LittleEndian(
            body.AsSpan(2, sizeof(uint)));
        return mailId != 0;
    }

    private static bool TryReadMailboxState(
        byte[] body,
        out uint mailId,
        out ushort state)
    {
        mailId = 0;
        state = 0;
        if (body.Length != sizeof(ushort) + sizeof(uint) + sizeof(ushort))
        {
            return false;
        }

        mailId = BinaryPrimitives.ReadUInt32LittleEndian(
            body.AsSpan(sizeof(ushort), sizeof(uint)));
        state = BinaryPrimitives.ReadUInt16LittleEndian(
            body.AsSpan(sizeof(ushort) + sizeof(uint), sizeof(ushort)));
        return mailId != 0 && state is 0 or 2 or 3;
    }

    private static bool TryReadMailboxSend(
        byte[] body,
        out MailboxSendRequest request)
    {
        request = default;
        if (!TryReadLengthPrefixedBytes(body, 2, out var recipientBytes, out var offset)
            || recipientBytes.Length is 0 or >= 30
            || body.Length - offset
                < sizeof(uint) + sizeof(byte) + sizeof(ushort) + sizeof(ushort) + sizeof(uint))
        {
            return false;
        }

        var goldValue = BinaryPrimitives.ReadUInt32LittleEndian(
            body.AsSpan(offset, sizeof(uint)));
        offset += sizeof(uint);
        if (goldValue > int.MaxValue)
        {
            return false;
        }

        var listType = body[offset++];
        var slot = BinaryPrimitives.ReadUInt16LittleEndian(
            body.AsSpan(offset, sizeof(ushort)));
        offset += sizeof(ushort);
        var itemId = BinaryPrimitives.ReadUInt16LittleEndian(
            body.AsSpan(offset, sizeof(ushort)));
        offset += sizeof(ushort);
        var countOrValue = BinaryPrimitives.ReadUInt32LittleEndian(
            body.AsSpan(offset, sizeof(uint)));
        offset += sizeof(uint);
        if (!TryReadLengthPrefixedBytes(body, offset, out var textBytes, out var endOffset)
            || textBytes.Length >= 512
            || endOffset != body.Length)
        {
            return false;
        }

        var recipient = ClientEncoding.GetString(recipientBytes).Trim();
        if (string.IsNullOrWhiteSpace(recipient) || recipient.Length > 20)
        {
            return false;
        }

        request = new MailboxSendRequest(
            recipient,
            checked((int)goldValue),
            listType,
            slot,
            itemId,
            countOrValue,
            ClientEncoding.GetString(textBytes));
        return true;
    }

    private static bool TryReadLengthPrefixedBytes(
        byte[] source,
        int offset,
        out byte[] value,
        out int nextOffset)
    {
        value = [];
        nextOffset = offset;
        if (offset < 0 || source.Length - offset < sizeof(uint))
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(offset, sizeof(uint)));
        offset += sizeof(uint);
        if (length > int.MaxValue || source.Length - offset < (int)length)
        {
            return false;
        }

        value = source.AsSpan(offset, (int)length).ToArray();
        nextOffset = offset + (int)length;
        return true;
    }

    private static Encoding CreateClientEncoding()
    {
        return PvfEncodings.Cp936Lossy();
    }

    private readonly record struct MailboxSendRequest(
        string Recipient,
        int Gold,
        byte ListType,
        ushort Slot,
        ushort ItemId,
        uint CountOrValue,
        string Text);

    private GamePassiveObjectDrop[] RegisterPassiveObjectDrops(
        DungeonRoomDefinition room,
        DungeonGroundItemState groundItems,
        ushort ownerUserId,
        byte roomX,
        byte roomY)
    {
        var result = new List<GamePassiveObjectDrop>(room.PassiveItemSpawns.Length);
        foreach (var spawn in room.PassiveItemSpawns)
        {
            var groundItem = groundItems.AddPassive(
                spawn.Drop,
                ownerUserId,
                spawn.PassiveObjectIndex,
                spawn.AssociationSlot,
                roomX,
                roomY,
                x: unchecked((short)spawn.X),
                y: unchecked((short)spawn.Y),
                catalog: itemCatalog);
            result.Add(new GamePassiveObjectDrop(
                spawn.AssociationSlot,
                groundItem.GroundId,
                groundItem.ItemId,
                groundItem.GetClientAddInfo(itemCatalog),
                groundItem.OwnerUserId));
        }

        return result.ToArray();
    }

    private GamePassiveObjectDrop[] GetRegisteredPassiveObjectDrops(
        DungeonGroundItemState groundItems,
        byte roomX,
        byte roomY) =>
        groundItems.GetPassiveObjectItems(roomX, roomY)
            .Select(item => new GamePassiveObjectDrop(
                item.PassiveItemSlot,
                item.GroundId,
                item.ItemId,
                item.GetClientAddInfo(itemCatalog),
                item.OwnerUserId))
            .ToArray();

    private EntranceContent LoadContent()
    {
        if (_content is not null)
        {
            return _content;
        }

        lock (_contentGate)
        {
            if (_content is not null)
            {
                return _content;
            }

            byte[]? channelScript = null;
            const string virtualPath = "etc/channel_info.etc";
            if (scripts.FileExists(virtualPath))
            {
                channelScript = scripts.ReadAllBytes(virtualPath);
            }
            else
            {
                logger.LogWarning(
                    "{VirtualPath} is missing from {Source}; protocol 9 download is disabled.",
                    virtualPath,
                    scripts.SourceDescription);
            }

            var channel = options.Channel;
            var metadata = EntranceMetadata.BuildSingleLocalChannel(new EntranceChannel(
                channel.Name,
                channel.Host,
                channel.Port,
                channel.ChannelNumber,
                channel.MaximumUsers,
                channel.CurrentUsers));
            _content = new EntranceContent(channelScript, metadata);
            return _content;
        }
    }

    private sealed record CompoundItemReplayEntry(
        byte[] RequestBody,
        GameServerPacket Reply,
        bool RefreshWarehouse);

    private static string ToSpacedHex(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        return string.Create(bytes.Length * 3 - 1, bytes.ToArray(), (span, buffer) =>
        {
            const string HexChars = "0123456789ABCDEF";
            for (var i = 0; i < buffer.Length; i++)
            {
                span[i * 3] = HexChars[buffer[i] >> 4];
                span[i * 3 + 1] = HexChars[buffer[i] & 0xF];
                if (i < buffer.Length - 1)
                {
                    span[i * 3 + 2] = ' ';
                }
            }
        });
    }

    private void LogPacket(string direction, RuntimeSession session, PacketFrame frame, PacketFrame? rawFrame = null)
    {
        // 漏斗无条件收集（Tap 激活时供智能体抓包），tracing 门卫只挡控制台
        // hex 与日志（docs/design/09-mcp-packet-tap.md §5.2）。
        runtime.TracePacket(session, direction, frame, options.HexDumpLimit, rawFrame);
        if (!options.EnablePacketTracing)
        {
            return;
        }

        var bodyLimit = Math.Min(frame.Body.Length, options.HexDumpLimit);
        var body = ToSpacedHex(frame.Body.AsSpan(0, bodyLimit));
        var wire = (rawFrame ?? frame).Encode();
        var rawLimit = Math.Min(wire.Length, options.HexDumpLimit + PacketFrame.HeaderLength);
        var raw = ToSpacedHex(wire.AsSpan(0, rawLimit));
        string? plainText = null;
        try
        {
            plainText = PacketPlainText.Decode(frame.Type, frame.ProtocolId, frame.Body);
        }
        catch
        {
            // Tracing must never break the session.
        }

        var idLabel = frame.Type == GameProtocolEngine.NotificationPacketType ? "NOTI" : "CMD";
        // Received command bodies are the TryDecode product (the wire carries
        // the client's rolling-transform ciphertext); server-built frames are
        // plaintext, so only RX keeps the decode label.
        var bodyLabel = direction == "RX" ? "TryDecodeBody" : "body";
        logger.LogInformation(
            "{IdLabel} [{SessionId}] Packet={Id}({IdHex}) len={Length} {BodyLabel}=({BodyLen}B) [{Body}]{BodySuffix} crc={Crc:X8} valid={Valid} text={Text}\n  raw: {Raw}{RawSuffix}",
            idLabel, session.Id, frame.ProtocolId, $"0x{frame.ProtocolId:X2}",
            frame.TotalLength, bodyLabel, frame.Body.Length, body, frame.Body.Length > bodyLimit ? "..." : "",
            frame.DeclaredCrc32, frame.HasValidCrc32, plainText ?? "-",
            raw, wire.Length > rawLimit ? "..." : "");
    }

    private void LogPacket(string direction, RuntimeSession session, GameServerPacket packet)
    {
        runtime.TracePacket(session, direction, packet, options.HexDumpLimit);
        if (!options.EnablePacketTracing)
        {
            return;
        }

        var payloadLimit = Math.Min(packet.Payload.Length, options.HexDumpLimit);
        var payloadDump = ToSpacedHex(packet.Payload.AsSpan(0, payloadLimit));
        var wire = packet.Encode();
        var rawLimit = Math.Min(wire.Length, options.HexDumpLimit + GameServerPacket.HeaderLength);
        var raw = ToSpacedHex(wire.AsSpan(0, rawLimit));
        string? plainText = null;
        try
        {
            plainText = PacketPlainText.DecodeServer(packet.Type, packet.ProtocolId, packet.Payload);
        }
        catch
        {
            // Tracing must never break the session.
        }

        var idLabel = packet.Type == GameProtocolEngine.NotificationPacketType ? "NOTI" : "CMD";
        logger.LogInformation(
            "{IdLabel} [{SessionId}] Packet={Id}({IdHex}) len={Length} body=({BodyLen}B) [{Body}]{BodySuffix} text={Text}\n  raw: {Raw}{RawSuffix}",
            idLabel, session.Id, packet.ProtocolId, $"0x{packet.ProtocolId:X2}",
            packet.TotalLength, packet.Payload.Length, payloadDump, packet.Payload.Length > payloadLimit ? "..." : "",
            plainText ?? "-",
            raw, wire.Length > rawLimit ? "..." : "");
    }
}
