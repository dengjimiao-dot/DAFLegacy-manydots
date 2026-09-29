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
    ILoggerFactory loggerFactory,
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
    // Protocol-layer dispatch (07 §4.6): mounted command handlers take over
    // their protocol exclusively; unmounted ids keep flowing through the
    // legacy branch chain until their group migrates.
    private GameCommandRegistry _gameCommands = null!;
    private readonly GameCommandDispatcher _gameCommandDispatcher = new(logger);
    private readonly CharacterPacketBuilders _characterPacketBuilders =
        new(store, itemCatalog, pvpExperienceCatalog, ceraShopCatalog);
    private ChannelSessionSenders _sessionSenders = null!;
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
    // Assembly of the dispatch table (07 §4.6): handler construction is
    // manual because every dependency is already a constructor parameter
    // here; a duplicate registration throws before any listener starts.
    private GameCommandRegistry BuildGameCommandRegistry(
        ServerOptions options,
        ILoggerFactory loggerFactory)
    {
        var registry = new GameCommandRegistry();
        registry.Register("session.connection", new CheckConnectionHandler(options));
        registry.Register("session.connection", new ExitHandler(loggerFactory.CreateLogger<ExitHandler>()));
        registry.Register("session.connection", new AntibotHandler(loggerFactory.CreateLogger<AntibotHandler>()));
        registry.Register("economy.shop", new CeraShopBuyHandler(
            store,
            itemCatalog,
            ceraShopCatalog,
            options,
            runtime,
            characterSessions,
            loggerFactory.CreateLogger<CeraShopBuyHandler>(),
            _characterPacketBuilders,
            _sessionSenders));
        registry.Register("economy.shop", new BuyItemHandler(
            store,
            itemCatalog,
            characterSessions,
            loggerFactory.CreateLogger<BuyItemHandler>(),
            _characterPacketBuilders,
            _sessionSenders));
        registry.Register("economy.shop", new SellItemHandler(
            store,
            itemCatalog,
            loggerFactory.CreateLogger<SellItemHandler>(),
            _characterPacketBuilders,
            _sessionSenders));
        registry.Register("economy.shop", new RepairEquipmentHandler(
            store,
            itemCatalog,
            loggerFactory.CreateLogger<RepairEquipmentHandler>(),
            _characterPacketBuilders,
            _sessionSenders));
        registry.Register("economy.shop", new ItemTradeStateHandler(
            store,
            itemCatalog,
            loggerFactory.CreateLogger<ItemTradeStateHandler>(),
            _characterPacketBuilders,
            _sessionSenders));
        registry.Register("economy.shop", new GenerateCeraTicketHandler(
            loggerFactory.CreateLogger<GenerateCeraTicketHandler>()));
        registry.Register("economy.inventory", new SortItemHandler(
            store,
            itemCatalog,
            loggerFactory.CreateLogger<SortItemHandler>(),
            _characterPacketBuilders));
        registry.Register("economy.inventory", new MoveItemSpaceHandler(
            store,
            itemCatalog,
            loggerFactory.CreateLogger<MoveItemSpaceHandler>(),
            _characterPacketBuilders,
            _sessionSenders,
            premiumBenefitCatalog));
        registry.Register("economy.inventory", new DeleteItemHandler(
            options,
            runtime,
            store,
            itemCatalog,
            loggerFactory.CreateLogger<DeleteItemHandler>(),
            _characterPacketBuilders,
            _sessionSenders));
        registry.Register("economy.inventory", new UseStackableItemHandler(
            options,
            runtime,
            store,
            itemCatalog,
            spCatalog,
            experienceCatalog,
            pvpExperienceCatalog,
            characterSessions,
            loggerFactory.CreateLogger<UseStackableItemHandler>(),
            _characterPacketBuilders,
            _sessionSenders));
        registry.Register("economy.inventory", new DecreaseDurabilityHandler(
            store,
            itemCatalog,
            loggerFactory.CreateLogger<DecreaseDurabilityHandler>(),
            _characterPacketBuilders));
        registry.Register("progression.skill", new IncreaseStatusHandler(
            loggerFactory.CreateLogger<IncreaseStatusHandler>(),
            store,
            itemCatalog,
            skillCatalog,
            spCatalog,
            experienceCatalog,
            _sessionSenders));
        registry.Register("progression.skill", new BuySkillHandler(
            loggerFactory.CreateLogger<BuySkillHandler>(),
            store,
            skillCatalog,
            premiumBenefitCatalog,
            _sessionSenders));
        registry.Register("progression.skill", new ChangeSkillSlotHandler(
            store,
            skillCatalog,
            loggerFactory.CreateLogger<ChangeSkillSlotHandler>()));
        registry.Register("progression.quest", new AcceptQuestHandler(
            loggerFactory.CreateLogger<AcceptQuestHandler>(),
            store,
            itemCatalog,
            questCatalog,
            dungeonCatalog,
            _characterPacketBuilders,
            _sessionSenders));
        registry.Register("progression.quest", new GiveupQuestHandler(
            loggerFactory.CreateLogger<GiveupQuestHandler>(),
            store,
            questCatalog,
            _sessionSenders));
        registry.Register("progression.quest", new SetQuestTriggerHandler(
            loggerFactory.CreateLogger<SetQuestTriggerHandler>(),
            store,
            questCatalog,
            _sessionSenders));
        registry.Register("progression.quest", new FinishQuestHandler(
            loggerFactory.CreateLogger<FinishQuestHandler>(),
            characterSessions,
            store,
            itemCatalog,
            questCatalog,
            dungeonCatalog,
            skillCatalog,
            experienceCatalog,
            spCatalog,
            _characterPacketBuilders,
            _sessionSenders));
        registry.Register("social.mailbox", new MailboxOpenHandler(_sessionSenders));
        registry.Register("social.mailbox", new MailboxExtractHandler(
            store,
            itemCatalog,
            _characterPacketBuilders,
            _sessionSenders));
        registry.Register("social.mailbox", new MailboxStateHandler(store, _sessionSenders));
        registry.Register("social.mailbox", new MailboxSendHandler(
            store,
            itemCatalog,
            characterSessions,
            _characterPacketBuilders,
            _sessionSenders));
        registry.Register("social.chat", new SendMessageHandler(
            store,
            characterSessions,
            partyCoordinator,
            options,
            loggerFactory.CreateLogger<SendMessageHandler>(),
            _characterPacketBuilders,
            _sessionSenders));
        registry.Register("social.chat", new CreatureSendMessageHandler(
            loggerFactory.CreateLogger<CreatureSendMessageHandler>(),
            _sessionSenders));
        registry.Register("social.chat", new CreatureScriptMessageHandler(
            loggerFactory.CreateLogger<CreatureScriptMessageHandler>(),
            _sessionSenders));
        registry.Register("session.login", new LoginHandler(store, loggerFactory.CreateLogger<LoginHandler>()));
        registry.Register("session.login", new SetUdpEndpointHandler(gameplayDatagram, partyCoordinator));
        registry.Register("progression.character", new GetUserInfoHandler(_characterPacketBuilders));
        registry.Register("progression.character", new CreateCharacterHandler(store, _characterPacketBuilders));
        registry.Register("progression.character", new DeleteCharacterHandler(store, _characterPacketBuilders));
        registry.Register("progression.character", new SelectCharacterHandler(
            store,
            itemCatalog,
            skillCatalog,
            questCatalog,
            dungeonCatalog,
            characterStatCatalog,
            premiumBenefitCatalog,
            characterSessions,
            _characterPacketBuilders,
            _sessionSenders,
            loggerFactory.CreateLogger<SelectCharacterHandler>()));
        registry.Register("progression.character", new ReturnSelectCharacterHandler(
            store,
            characterSessions,
            partyCoordinator,
            loggerFactory.CreateLogger<ReturnSelectCharacterHandler>()));
        return registry;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Entrance.Enabled)
        {
            logger.LogWarning("Entrance listener is disabled.");
            return;
        }

        _sessionSenders = new ChannelSessionSenders(
            store,
            itemCatalog,
            characterStatCatalog,
            questCatalog,
            dungeonCatalog,
            pvpExperienceCatalog,
            _characterPacketBuilders,
            loggerFactory.CreateLogger<ChannelSessionSenders>());
        _gameCommands = BuildGameCommandRegistry(options, loggerFactory);

        // Startup listing of the mounted dispatch table (07 §4.6): one line
        // per registered group so a mis-mounted protocol is visible at boot.
        foreach (var group in _gameCommands.Entries.GroupBy(entry => entry.Group))
        {
            logger.LogInformation(
                "Game command group {Group} mounted: {Handlers}",
                group.Key,
                string.Join(", ", group.Select(entry => $"{entry.Handler.Name}({entry.Handler.ProtocolId})")));
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
            // Session state object (07 §4.3): the former closure variables
            // of this method live on the state domains now; `session` is the
            // method-wide alias that handlers reach through ctx.State as well.
            var sessionState = new ChannelSessionState();
            var session = sessionState;
            // Per-run dungeon dependencies: the run recorder needs its
            // catalog and the return-to-town anchor snapshots the
            // pre-dungeon position, exactly as the former initializers did.
            session.Dungeon.Run = new DungeonRunState(dungeonCatalog);
            session.Dungeon.ReturnTownId = session.Character.TownId;
            session.Dungeon.ReturnAreaId = session.Character.AreaId;
            session.Dungeon.ReturnX = session.Character.X;
            session.Dungeon.ReturnY = session.Character.Y;
            session.Dungeon.ReturnDirection = session.Character.Direction;

            CharacterWarehouseUpgradeItemSettlement PlanWarehouseUpgradeSettlement(
                IEnumerable<CharacterItemRecord> inventory,
                IEnumerable<CharacterItemRecord> warehouse,
                IEnumerable<ushort>? acquiredItemIds = null)
            {
                var settlement = CharacterWarehouseProgression.SettleUpgradeItems(
                    inventory,
                    warehouse,
                    session.Inventory.WarehouseCapacity);
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

            logger.LogInformation(
                "Entrance client {SessionId} connected from {Remote} on local port {Port}",
                runtimeSession.Id, remote, localPort);

            using var stream = new SerializedWriteStream(client.GetStream());
            // Protocol-layer per-connection plumbing (07 §4.2/§4.4): the
            // outbound queue pumps every handler send through the
            // single-writer channel; the context replaces the former
            // closure world for handlers migrated off the legacy chain.
            // Declared before the try so teardown drains the pump before
            // the stream is disposed.
            var outboundQueue = new OutboundQueue(
                stream,
                runtimeSession,
                packet => LogPacket("TX", runtimeSession, packet),
                logger,
                disconnecting: serverToken);
            var connectionContext = new ConnectionContext(
                runtimeSession,
                remoteEndpoint,
                isChannelPort,
                sessionState,
                outboundQueue,
                logger,
                serverToken);
            connectionContext.KickConnection = KickConnection;
                PacketFrame? queuedRequest = null;
                Task<PacketFrame?>? pendingPacketRead = null;
            // Pump-side adjacent-frame read for the CHANGE_SKILLSLOT swap
            // pair: delay, availability probe, decode and accounting stay in
            // the transport layer; a non-matching frame is queued back.
            connectionContext.ReadAdjacentFrame = async (delay, accept, token) =>
            {
                await Task.Delay(delay, token);
                if (client.Available <= 0)
                {
                    return default;
                }

                var adjacentRaw = await PacketFrame.ReadAsync(
                    stream,
                    options.MaximumPacketLength,
                    token);
                if (adjacentRaw is null)
                {
                    return default;
                }

                if (!accept(adjacentRaw))
                {
                    queuedRequest = adjacentRaw;
                    return default;
                }

                if (!gameClientCipher!.TryDecode(adjacentRaw, out var adjacentDecoded))
                {
                    LogPacket("RX-ENC", runtimeSession, adjacentRaw);
                    logger.LogWarning("Could not decode the adjacent skill quick-slot command.");
                    return new AdjacentFrameResult(null, DecodeFailed: true);
                }

                runtime.CountPacket(runtimeSession);
                runtimeSession.LastProtocolId = adjacentDecoded.ProtocolId;
                LogPacket("RX", runtimeSession, adjacentDecoded, adjacentRaw);
                return new AdjacentFrameResult(adjacentDecoded, DecodeFailed: false);
            };

            try
            {
                async Task<bool> TryDispatchGameCommandAsync(PacketFrame frame)
                {
                    if (frame.Type != GameProtocolEngine.CommandPacketType
                        || !_gameCommands.TryGet(frame.ProtocolId, out var handler))
                    {
                        return false;
                    }

                    return await _gameCommandDispatcher.TryDispatchAsync(
                        connectionContext,
                        frame,
                        handler,
                        serverToken);
                }

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

            bool AdoptSavedWarehouseUpgradeState(CharacterRecord savedCharacter) =>
                _sessionSenders.AdoptSavedWarehouseUpgradeState(connectionContext, savedCharacter);

            uint GetInventoryWeightLimit(CharacterRecord character) =>
                _sessionSenders.GetInventoryWeightLimit(connectionContext, character);

                Task SendWarehouseStateAsync() =>
                    _sessionSenders.SendWarehouseStateAsync(connectionContext, serverToken);

                async Task SendTypedMessageNotificationAsync(
                    GameMessageType messageType,
                    ushort targetAreaUserId,
                    byte[] messageBytes)
                {
                    try
                    {
                        if (session.Character.Active is null)
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
                            session.Character.Active?.Id);
                    }
                }

                async Task SendPopupNotificationAsync(byte[] messageBytes)
                {
                    try
                    {
                        if (session.Character.Active is null)
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
                            session.Character.Active?.Id);
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
                        if (session.Character.Active is null)
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
                            session.Character.Active?.Id);
                    }
                }

                // Registers this session's town entity with the party
                // coordinator so same-area residents can see and invite this
                // character. The appearance packet is built here because only
                // this connection owns the equipped-item dictionaries.
                void PublishTownPresence()
                {
                    if (session.Character.Active is null)
                    {
                        return;
                    }

                    var appearance = CreateTownAppearancePacket(
                        session.Character.Active,
                        session.Character.LocalUserId,
                        session.Inventory.Equipped.Values,
                        session.Inventory.Creature.Values,
                        HasActivePremiumService(GameProtocolEngine.BlackDiamondServiceType));
                    partyCoordinator.PublishTownPresence(
                        new PartyPresenceUpdate(
                            runtimeSession.Id,
                            session.Character.Active.Id,
                            session.Login.AccountUid,
                            session.Character.LocalUserId,
                            session.Character.Active.Name,
                            (byte)Math.Clamp(session.Character.Active.Level, (byte)1, byte.MaxValue),
                            (byte)Math.Clamp(session.Character.Active.Job, byte.MinValue, byte.MaxValue),
                            session.Character.TownId,
                            session.Character.AreaId,
                            session.Character.X,
                            session.Character.Y,
                            session.Character.Direction),
                        appearance);
                }

                GameSkillEntry[] GetCurrentGameSkills() => _sessionSenders.GetCurrentGameSkills(connectionContext);

                int ApplyLevelUpSkillPoints(CharacterExperienceGrant progression)
                {
                    var granted = spCatalog.CalculateLevelUpReward(
                        progression.PreviousLevel,
                        progression.Level);
                    if (granted == 0)
                    {
                        return 0;
                    }

                    session.Skill.CurrentSp = checked(session.Skill.CurrentSp + granted);
                    return granted;
                }

                Task SendCurrentSkillInfoAsync() =>
                    _sessionSenders.SendCurrentSkillInfoAsync(connectionContext, serverToken);
                Task SendPvpRecordAsync() =>
                    _sessionSenders.SendPvpRecordAsync(connectionContext, serverToken);

                async Task<CharacterMailRecord[]> SendMailboxStateAsync(bool notifyNewMail)
                {
                    if (session.Character.Active is null)
                    {
                        return [];
                    }

                    var mails = await store.GetCharacterMailsAsync(
                        session.Character.Active.Id,
                        serverToken);
                    session.Mailbox.KnownIds.Clear();
                    session.Mailbox.KnownIds.UnionWith(mails.Select(mail => mail.Id));
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
                    if (session.Character.Active is null)
                    {
                        return;
                    }

                    var mails = knownMails
                        ?? await store.GetCharacterMailsAsync(session.Character.Active.Id, serverToken);
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
                        session.Character.Active.Id,
                        serverToken);
                }

                Task SendPremiumServiceInfoAsync(byte serviceType, uint remainSeconds) =>
                    _sessionSenders.SendPremiumServiceInfoAsync(connectionContext, serviceType, remainSeconds, serverToken);
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
                        if (session.Character.Active is null)
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
                            session.Character.Active?.Id);
                    }
                }

                bool HasActivePremiumService(byte serviceType) =>
                    _sessionSenders.HasActivePremiumService(connectionContext, serviceType);

                byte[] GetActivePremiumServiceTypes() =>
                    _sessionSenders.GetActivePremiumServiceTypes(connectionContext);

                byte GetProjectedStaminaRecovery() =>
                    _sessionSenders.GetProjectedStaminaRecovery(connectionContext);

                async Task SendCurrentEquipmentStateAsync(
                    GameCharacterCombatStats? validatedStats = null)
                {
                    if (session.Character.Active is null)
                    {
                        return;
                    }

                    var projectedStats = validatedStats
                        ?? characterStatCatalog.Get(session.Character.Active);
                    var equipment = GameProtocolEngine.CreateCurrentCharacterDetails(
                        session.Character.LocalUserId,
                        experience: session.Character.Active.Experience,
                        learnedSkills: GetCurrentGameSkills(),
                        combatStats: projectedStats,
                        equippedItems: CreateWornEquipmentSnapshot(
                            session.Inventory.Equipped.Values,
                            session.Inventory.Creature),
                        equippedCreatureLevel: GetEquippedCreatureLevel(
                            session.Inventory.Creature),
                        staminaRecoveryPercentage: GetProjectedStaminaRecovery());
                    await equipment.WriteAsync(stream, serverToken);
                    Interlocked.Increment(ref runtimeSession.SentPackets);
                    LogPacket("TX", runtimeSession, equipment);
                    session.Character.LastProjectedCombatStats = projectedStats;
                }

                async Task SendEquippedCreatureDeathAsync(bool force = false)
                {
                    if (session.Character.Active is null
                        || !session.Inventory.Creature.TryGetValue(
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
                        session.Weakness.CreatureDeathNotificationsSent.Remove(creatureUid);
                        return;
                    }

                    if (!force
                        && session.Weakness.CreatureDeathNotificationsSent.Contains(creatureUid))
                    {
                        return;
                    }

                    var died = GameProtocolEngine.CreateDiedCreature(session.Character.LocalUserId);
                    await died.WriteAsync(stream, serverToken);
                    Interlocked.Increment(ref runtimeSession.SentPackets);
                    LogPacket("TX", runtimeSession, died);
                    session.Weakness.CreatureDeathNotificationsSent.Add(creatureUid);
                    logger.LogInformation(
                        "Sent DIED_CREATURE for Creature uid {CreatureUid}, area user {UserId}.",
                        creatureUid,
                        session.Character.LocalUserId);
                }

                async Task SendEquippedCreatureHungerAsync(
                    bool forceDeathNotification = false)
                {
                    if (session.Character.Active is null
                        || !session.Inventory.Creature.TryGetValue(
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
                    if (session.Character.Active is null
                        || !session.Inventory.Creature.ContainsKey(
                            CharacterCreatureInventoryLayout.EquippedCreatureSlot))
                    {
                        return;
                    }

                    var creatureResponse =
                        GameProtocolEngine.CreateCreatureResponse(session.Character.LocalUserId);
                    await creatureResponse.WriteAsync(stream, serverToken);
                    Interlocked.Increment(ref runtimeSession.SentPackets);
                    LogPacket("TX", runtimeSession, creatureResponse);
                }

                Task SaveCurrentLocationAsync(CancellationToken cancellationToken) =>
                    SessionLocationPersistence.SaveCurrentLocationAsync(
                        store,
                        session,
                        cancellationToken);

                async Task EnableEmptyBossRoomSettlementAsync(DungeonRoomDefinition room)
                {
                    if (room.Monsters.Length != 0
                        || session.Dungeon.ClearEnabled
                        || room.MapType != DungeonMapType.Boss)
                    {
                        return;
                    }

                    // War-room scripts (2000-2002) use timed dynamic spawns
                    // rather than static .map monster rows. Until that mode is
                    // simulated, an empty terminal room must remain settleable.
                    session.Dungeon.ClearEnabled = true;
                    var enableClear = GameProtocolEngine.CreateEnableClearDungeon();
                    await enableClear.WriteAsync(stream, serverToken);
                    Interlocked.Increment(ref runtimeSession.SentPackets);
                    LogPacket("TX", runtimeSession, enableClear);
                    await CompleteClearQuestTriggersAsync(
                        "empty boss-room settlement");
                    logger.LogInformation(
                        "Dungeon {DungeonId} empty boss room ({RoomX},{RoomY}) enabled settlement.",
                        session.Dungeon.CurrentDungeonId,
                        session.Dungeon.RoomX,
                        session.Dungeon.RoomY);
                }

                async Task SendTownAppearanceAsync(bool waitForTownActor = false)
                {
                    if (session.Character.Active is null || session.Dungeon.CurrentDungeonId != 0)
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
                        session.Character.Active,
                        session.Character.LocalUserId,
                        session.Inventory.Equipped.Values,
                        session.Inventory.Creature.Values,
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
                    if (session.Character.Active is null)
                    {
                        return;
                    }

                    var validation = characterStatCatalog.Validate(
                        session.Character.Active,
                        session.Character.LastProjectedCombatStats);
                    if (session.Dungeon.CurrentDungeonId == 0)
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
                        session.Character.Active.Id,
                        session.Character.Active.Level,
                        session.Character.Active.Job,
                        session.Character.Active.GrowType & 0x0F,
                        Math.Max(session.Character.Active.AwakeningType, session.Character.Active.GrowType >> 4),
                        !validation.IsCorrect,
                        validation.MismatchedFields.Count == 0
                            ? "none"
                            : string.Join(',', validation.MismatchedFields));
                }

                async Task SendInitialStaminaStateAsync()
                {
                    if (session.Weakness.StaminaStateInitialized || session.Character.Active is null)
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
                    session.Weakness.StaminaStateInitialized = true;
                }

                async Task SendSilentTownStaminaStateAsync()
                {
                    if (session.Character.Active is null || session.Dungeon.CurrentDungeonId != 0)
                    {
                        return;
                    }

                    // AREA_USERS recreates the town actor with empty stamina.
                    // NOTI 33 restores a normal town actor without a recovery
                    // sound, but its client handler also fills current HP/MP.
                    // It must therefore never be sent while inside a dungeon.
                    var stamina = session.Character.Active.IsWeakened
                        && !HasActivePremiumService(
                            GameProtocolEngine.BlackDiamondServiceType)
                            ? GameProtocolEngine.CreateStamina(
                                session.Character.Active.WeaknessRecovery)
                            : GameProtocolEngine.CreateFailClearDungeon(
                                DungeonWeaknessPolicy.FullStamina);
                    await stamina.WriteAsync(stream, serverToken);
                    Interlocked.Increment(ref runtimeSession.SentPackets);
                    LogPacket("TX", runtimeSession, stamina);
                }

                Task SendTownFatigueStateAsync() =>
                    _sessionSenders.SendTownFatigueStateAsync(connectionContext, serverToken);
                async Task ConsumeDungeonRoomFatigueAsync(
                    byte roomX,
                    byte roomY)
                {
                    if (session.Character.Active is null || session.Dungeon.CurrentDungeonId == 0)
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
                            session.Character.Active.Id,
                            maximumFatigue,
                            serverToken);
                    if (consumption.Character is null)
                    {
                        logger.LogWarning(
                            "Could not consume dungeon-room fatigue because character {CharacterId} was not found.",
                            session.Character.Active.Id);
                        return;
                    }

                    session.Character.Active = consumption.Character;
                    if (!consumption.Consumed)
                    {
                        logger.LogInformation(
                            "Dungeon {DungeonId} room ({RoomX},{RoomY}) consumed no fatigue because user {UserId} is already at {UsedFatigue}/{MaximumFatigue}.",
                            session.Dungeon.CurrentDungeonId,
                            roomX,
                            roomY,
                            session.Character.LocalUserId,
                            consumption.UsedFatigue,
                            maximumFatigue);
                        return;
                    }

                    session.Dungeon.ConsumedFatigue++;

                    var fatigue = GameProtocolEngine.CreateFatigue(
                        consumption.UsedFatigue,
                        maximumFatigue,
                        premiumFatigue);
                    await fatigue.WriteAsync(stream, serverToken);
                    Interlocked.Increment(ref runtimeSession.SentPackets);
                    LogPacket("TX", runtimeSession, fatigue);
                    logger.LogInformation(
                        "Dungeon {DungeonId} room ({RoomX},{RoomY}) consumed one fatigue for user {UserId}: used {PreviousUsedFatigue}->{UsedFatigue}, remaining={RemainingFatigue}/{MaximumFatigue}.",
                        session.Dungeon.CurrentDungeonId,
                        roomX,
                        roomY,
                        session.Character.LocalUserId,
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
                        if (!session.Inventory.Creature.TryGetValue(artifactSlot, out var artifact)
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

                Task<bool> SettleDungeonCreatureHungerAsync(bool notifyClient, CancellationToken cancellationToken) =>
                    _sessionSenders.SettleDungeonCreatureHungerAsync(connectionContext, notifyClient, cancellationToken);

                async Task GrantDungeonCreatureExperienceAsync()
                {
                    if (session.Dungeon.ConsumedFatigue == 0
                        || session.Character.Active is null
                        || !session.Inventory.Creature.TryGetValue(
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
                        session.Dungeon.ConsumedFatigue = 0;
                        return;
                    }

                    var previousLevel = creature.CreatureLevel ?? 1;
                    var bonusRate =
                        GetEquippedCreatureExperienceBonusRate(previousLevel);
                    var grant = creatureExperienceCatalog.Grant(
                        creature.CreatureExperience ?? 0,
                        previousLevel,
                        creatureDefinition.CreatureSpecies,
                        session.Dungeon.ConsumedFatigue,
                        bonusRate);
                    if (grant.GrantedExperience == 0)
                    {
                        session.Dungeon.ConsumedFatigue = 0;
                        return;
                    }

                    var plannedCreatureInventory = session.Inventory.Creature.ToDictionary();
                    plannedCreatureInventory[
                        CharacterCreatureInventoryLayout.EquippedCreatureSlot] =
                        creature with
                        {
                            CreatureExperience = grant.Experience,
                            CreatureLevel = grant.Level
                        };
                    var saved = await store.SaveCharacterItemSpacesAsync(
                        session.Login.AccountName,
                        session.Character.Active.Id,
                        session.Currency.Gold,
                        session.Inventory.Main.Values,
                        session.Inventory.Equipped.Values,
                        session.Inventory.Warehouse.Values,
                        serverToken,
                        avatarInventory: session.Inventory.Avatar.Values,
                        creatureInventory: plannedCreatureInventory.Values);
                    if (saved is null)
                    {
                        logger.LogWarning(
                            "Could not persist Creature experience for character {CharacterId} after clearing dungeon {DungeonId}.",
                            session.Character.Active.Id,
                            session.Dungeon.CurrentDungeonId);
                        return;
                    }

                    session.Inventory.Creature = plannedCreatureInventory;
                    session.Character.Active = session.Character.Active with
                    {
                        CreatureInventory = plannedCreatureInventory.Values
                            .OrderBy(item => item.Slot)
                            .ToList()
                    };
                    session.Dungeon.ConsumedFatigue = 0;
                    var experienceNotification =
                        GameProtocolEngine.CreateCreatureGainExperience(
                            grant.Level,
                            grant.Experience);
                    await experienceNotification.WriteAsync(stream, serverToken);
                    Interlocked.Increment(ref runtimeSession.SentPackets);
                    LogPacket("TX", runtimeSession, experienceNotification);
                    logger.LogInformation(
                        "Dungeon {DungeonId} granted Creature uid {CreatureUid} {GrantedExperience} experience from {ConsumedFatigue} fatigue and {BonusExperience} artifact bonus: exp {PreviousExperience}->{Experience}, level {PreviousLevel}->{Level}.",
                        session.Dungeon.CurrentDungeonId,
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
                    if (session.Character.Active is null)
                    {
                        return (false, granted);
                    }

                    var plannedInventory = session.Inventory.Main.ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value);
                    var plannedWarehouseCapacity = session.Inventory.WarehouseCapacity;
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
                                GetInventoryWeightLimit(session.Character.Active),
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
                    if (normalizedGold == session.Currency.Gold
                        && !granted.Any(value => value)
                        && overflowMails.Count == 0)
                    {
                        return (true, granted);
                    }

                    var saved = await store.SaveCharacterInventoryAsync(
                        session.Login.AccountName,
                        session.Character.Active.Id,
                        normalizedGold,
                        plannedInventory.Values,
                        session.Inventory.Equipped.Values,
                        serverToken,
                        overflowMails,
                        warehouseCapacity: plannedWarehouseCapacity,
                        expectedWarehouseCapacity: session.Inventory.WarehouseCapacity);
                    if (saved is null)
                    {
                        return (false, new bool[itemIds.Count]);
                    }

                    var warehouseCapacityChanged =
                        AdoptSavedWarehouseUpgradeState(saved);
                    session.Currency.Gold = normalizedGold;
                    if (overflowMails.Count > 0)
                    {
                        characterSessions.NotifyMailReceived(session.Character.Active.CharacterNo);
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
                    if (session.Dungeon.ClearReward is null
                        || selectedCardIndex >= GameProtocolEngine.ClearRewardCardColumnCount)
                    {
                        return (false, false, null);
                    }

                    if (session.Dungeon.FreeCardIndex.HasValue)
                    {
                        return (
                            session.Dungeon.FreeCardIndex == selectedCardIndex,
                            false,
                            session.Dungeon.ClearReward.FreeItemId
                                ?? session.Dungeon.ClearReward.PremiumItemId);
                    }

                    var goldBeforeGrant = session.Currency.Gold;
                    var settledGold = (int)Math.Min(
                        int.MaxValue,
                        (long)session.Currency.Gold + session.Dungeon.ClearReward.FreeGoldAmount);
                    var freeGrant = await SaveClearRewardEquipmentAsync(
                        settledGold,
                        [
                            session.Dungeon.ClearReward.FreeItemId,
                            session.Dungeon.ClearReward.PremiumItemId
                        ]);
                    if (!freeGrant.Saved)
                    {
                        return (false, false, null);
                    }

                    session.Dungeon.FreeCardIndex = selectedCardIndex;
                    session.Dungeon.CardSelectionDeadline = null;
                    var rewardItemId = freeGrant.Granted[0]
                        ? session.Dungeon.ClearReward.FreeItemId
                        : freeGrant.Granted[1]
                            ? session.Dungeon.ClearReward.PremiumItemId
                            : null;
                    return (
                        true,
                        session.Currency.Gold != goldBeforeGrant
                            || freeGrant.Granted.Any(granted => granted),
                        rewardItemId);
                }

                async Task SendCardSelectionSnapshotAsync(bool inventoryChanged)
                {
                    if (session.Dungeon.ClearReward is null)
                    {
                        return;
                    }

                    var selectCardReply = GameProtocolEngine.CreateSelectCardReply(
                        session.Dungeon.FreeCardIndex,
                        session.Dungeon.GoldCardIndex,
                        goldRewardRevealed: session.Dungeon.GoldCardIndex.HasValue,
                        goldRewardItemId: session.Dungeon.ClearReward.GoldItemId);
                    await selectCardReply.WriteAsync(stream, serverToken);
                    Interlocked.Increment(ref runtimeSession.SentPackets);
                    LogPacket("TX", runtimeSession, selectCardReply);
                    if (!inventoryChanged)
                    {
                        return;
                    }

                    var inventoryRefresh = CreateMainInventoryPacket(
                        session.Currency.Gold,
                        session.Currency.VictoryPoints,
                        session.Inventory.Main.Values);
                    await inventoryRefresh.WriteAsync(stream, serverToken);
                    Interlocked.Increment(ref runtimeSession.SentPackets);
                    LogPacket("TX", runtimeSession, inventoryRefresh);
                }

                async Task<bool> TryAutomaticallySelectFreeCardAsync(string source)
                {
                    if (session.Dungeon.ClearReward is null || session.Dungeon.FreeCardIndex.HasValue)
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
                            session.Dungeon.CurrentDungeonId,
                            session.Character.LocalUserId,
                            source);
                        return false;
                    }

                    // DNF 1.0.1.9 consumes the same CMD 74 ownership snapshot
                    // for manual and TimerCardSelect assignments.
                    await SendCardSelectionSnapshotAsync(
                        automaticGrant.InventoryChanged);
                    logger.LogInformation(
                        "Dungeon {DungeonId} automatically selected free card {CardIndex} for user {UserId} through {Source}; reward={RewardItemId}",
                        session.Dungeon.CurrentDungeonId,
                        automaticCardIndex.Value,
                        session.Character.LocalUserId,
                        source,
                        automaticGrant.RewardItemId);
                    return true;
                }

                uint GetAuthoritativeQuestTrigger(
                    QuestDefinition definition,
                    IEnumerable<CharacterItemRecord>? inventory,
                    int gold,
                    uint victoryPoints) =>
                    _sessionSenders.GetAuthoritativeQuestTrigger(
                        connectionContext, definition, inventory, gold, victoryPoints);
                List<CharacterQuestRecord> RefreshSeekingQuestTriggers(
                    IEnumerable<CharacterQuestRecord> quests,
                    IEnumerable<CharacterItemRecord> inventory,
                    int gold,
                    uint victoryPoints,
                    out List<(ushort QuestId, uint OldTrigger, uint NewTrigger)> triggerChanges) =>
                    _sessionSenders.RefreshSeekingQuestTriggers(
                        connectionContext, quests, inventory, gold, victoryPoints, out triggerChanges);
                Task GrantQuestItemDropsAsync(
                    Func<QuestDefinition, IEnumerable<CharacterItemRecord>, IReadOnlyList<QuestItemDrop>> planDrops,
                    string source) =>
                    _sessionSenders.GrantQuestItemDropsAsync(connectionContext, planDrops, source, serverToken);
                Task GrantMonsterQuestItemsAsync(int monsterIndex) =>
                    GrantQuestItemDropsAsync(
                        (quest, inventory) => QuestItemDropPlanner.PlanMonster(
                            quest,
                            session.Dungeon.CurrentDungeonId,
                            session.Dungeon.Difficulty,
                            monsterIndex,
                            inventory,
                            chance => GameRandomSource.Shared.Next(100) < chance),
                        $"monster {monsterIndex}");

                Task GrantClearQuestItemsAsync() =>
                    GrantQuestItemDropsAsync(
                        (quest, inventory) => QuestItemDropPlanner.PlanClear(
                            quest,
                            session.Dungeon.CurrentDungeonId,
                            session.Dungeon.Difficulty,
                            inventory,
                        chance => GameRandomSource.Shared.Next(100) < chance),
                        $"dungeon {session.Dungeon.CurrentDungeonId} clear");

                async Task CompleteClearQuestTriggersAsync(string source)
                {
                    if (session.Character.Active is null || session.Dungeon.CurrentDungeonId == 0)
                    {
                        return;
                    }

                    var completedSimpleClearQuestIds = session.Quest.Current
                        .Where(quest => quest.Trigger != 0)
                        .Where(quest =>
                            questCatalog.TryGetDefinition(
                                quest.QuestId,
                                out var definition)
                            && ((session.Dungeon.ClearEnabled
                                    && QuestDungeonClearRequirement.IsSimpleClear(definition)
                                    && QuestDungeonClearRequirement.Matches(
                                        definition, session.Dungeon.CurrentDungeonId, session.Dungeon.Difficulty))
                                || session.Dungeon.Run.HasClearedMap(
                                    QuestMapRequirement.GetTargetMapId(definition))))
                        .Select(quest => quest.QuestId)
                        .ToHashSet();
                    if (completedSimpleClearQuestIds.Count == 0)
                    {
                        return;
                    }

                    var plannedQuests = session.Quest.Current
                        .Select(quest => completedSimpleClearQuestIds.Contains(quest.QuestId)
                            ? quest with { Trigger = 0 }
                            : quest)
                        .ToList();
                    var savedQuestProgress = await store.SaveCharacterQuestsAsync(
                        session.Login.AccountName,
                        session.Character.Active.Id,
                        plannedQuests,
                        session.Quest.CompletedIds,
                        cancellationToken: serverToken);
                    if (savedQuestProgress is null)
                    {
                        logger.LogWarning(
                            "Could not persist clear quest progress after {Source} in dungeon {DungeonId}.",
                            source,
                            session.Dungeon.CurrentDungeonId);
                        return;
                    }

                    session.Quest.Current = plannedQuests;
                    session.Character.Active = session.Character.Active with
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
                            session.Dungeon.CurrentDungeonId,
                            session.Dungeon.Difficulty);
                    }
                }

                async Task SendDungeonSelectionEligibilityAsync()
                {
                    var questsMet = session.Dungeon.SelectionWorldMap?.MeetsHellQuests(session.Quest.CompletedIds) == true;
                    var missingItems = session.Dungeon.SelectionWorldMap is { HasHellDungeon: true }
                        && !session.Dungeon.SelectionWorldMap.HasHellItems(session.Inventory.Main.Values);
                    var packet = GameProtocolEngine.CreateEnterSelectDungeon(
                        questsMet, missingItems ? [(ushort)0] : []);
                    await packet.WriteAsync(stream, serverToken);
                    Interlocked.Increment(ref runtimeSession.SentPackets);
                    LogPacket("TX", runtimeSession, packet);
                    logger.LogInformation(
                        "World map {WorldMapId} eligibility for {CharacterId}: hellQuests={QuestsMet}, missingHellItems={MissingItems}.",
                        session.Dungeon.SelectionWorldMap?.Id, session.Character.Active?.Id, questsMet, missingItems);
                }

                async Task ReturnToTownFromDungeonAsync(
                    string reason,
                    bool forcedDungeonExit = false)
                {
                    if (session.Dungeon.CurrentDungeonId == 0 && !session.Dungeon.InSelection)
                    {
                        logger.LogWarning(
                            "Ignored {Reason} return-to-town request because the user is not in a dungeon or dungeon selection.",
                            reason);
                        return;
                    }

                    if (session.Dungeon.CurrentDungeonId != 0 && !session.Dungeon.TutorialActive)
                    {
                        await _sessionSenders.SettleDungeonCreatureHungerAsync(connectionContext, 
                            notifyClient: true,
                            serverToken);
                    }

                    var leavingDungeonId = session.Dungeon.CurrentDungeonId;
                    var leftDungeonSelection = session.Dungeon.InSelection;
                    var gainedExperience = session.Dungeon.MonsterExperienceGained;
                    var leveledUp = session.Dungeon.LeveledUp;
                    if (session.Character.Active is not null && gainedExperience > 0)
                    {
                        var savedProgression = await store.SaveCharacterProgressionAsync(
                            session.Login.AccountName,
                            session.Character.Active.Id,
                            session.Character.Active.Level,
                            session.Character.Active.Experience,
                            session.Skill.CurrentSp,
                            serverToken);
                        if (savedProgression is not null)
                        {
                            session.Character.Active = session.Character.Active with
                            {
                                Level = savedProgression.Level,
                                Experience = savedProgression.Experience,
                                Sp = savedProgression.Sp
                            };
                        }
                    }

                    var hasBlackDiamond = false;
                    if (session.Character.Active is not null)
                    {
                        var blackDiamondState = await store.GetPremiumServiceAsync(
                            session.Login.AccountName,
                            serverToken);
                        hasBlackDiamond = blackDiamondState.Active;
                        if (blackDiamondState.Active
                            && blackDiamondState.ExpiresAt is { } blackDiamondExpiry)
                        {
                            session.Premium.ExpiryUnix[
                                GameProtocolEngine.BlackDiamondServiceType] =
                                blackDiamondExpiry;
                        }
                        else
                        {
                            session.Premium.ExpiryUnix.TryRemove(
                                GameProtocolEngine.BlackDiamondServiceType,
                                out _);
                        }
                    }

                    var enteredWeakness = false;
                    if (session.Character.Active is not null
                        && !session.Character.Active.IsWeakened
                        && DungeonWeaknessPolicy.ShouldApply(
                            session.Character.Active.Level,
                            forcedDungeonExit,
                            dungeonActive: leavingDungeonId != 0,
                            dungeonCleared: session.Dungeon.ClearEnabled,
                            hasBlackDiamond))
                    {
                        var weakenedCharacter = await store.SetCharacterWeaknessRecoveryAsync(
                            session.Character.Active.Id,
                            DungeonWeaknessPolicy.InitialRecovery,
                            serverToken);
                        if (weakenedCharacter is not null)
                        {
                            session.Character.Active = weakenedCharacter;
                            session.Currency.Gold = session.Character.Active.Gold;
                            enteredWeakness = true;
                        }
                        else
                        {
                            logger.LogError(
                                "Could not persist weakness for character {CharacterId} while returning from dungeon {DungeonId}; the client will retain normal stamina.",
                                session.Character.Active.Id,
                                leavingDungeonId);
                        }
                    }

                    session.Dungeon.CurrentDungeonId = 0;
                    session.Dungeon.Layout = null;
                    session.Dungeon.Room = null;
                    session.Dungeon.InSelection = false;
                    session.Dungeon.SelectionDeadline = null;
                    session.Dungeon.SelectionWorldMap = null;
                    session.Dungeon.AliveMonsters.Clear();
                    session.Dungeon.ReportedBossDeaths.Clear();
                    session.Dungeon.BossCascadeDeaths.Clear();
                    session.Dungeon.DestroyedPassiveObjects.Clear();
                    session.Dungeon.GroundItems.ResetDungeon();
                    session.Dungeon.Run.Stop();
                    session.Dungeon.TutorialActive = false;
                    session.Dungeon.CharacterAlive = true;
                    session.Dungeon.DeathGeneration++;
                    session.Dungeon.DeathTimeout = null;
                    session.Dungeon.Difficulty = 0;
                    session.Dungeon.ClearEnabled = false;
                    session.Dungeon.ResultSent = false;
                    session.Dungeon.CardLayoutSent = false;
                    session.Dungeon.CardRightsSent = false;
                    session.Dungeon.CardSelectionDeadline = null;
                    session.Dungeon.ClearReward = null;
                    session.Dungeon.FreeCardIndex = null;
                    session.Dungeon.GoldCardIndex = null;
                    session.Dungeon.MonsterExperienceGained = 0;
                    session.Dungeon.ClearExperience = null;
                    session.Dungeon.ConsumedFatigue = 0;
                    session.Dungeon.CreatureStomachCheckpointSeconds = null;
                    session.Dungeon.LeveledUp = false;
                    session.Dungeon.NormalMonsterKills = 0;
                    session.Dungeon.ChampionMonsterKills = 0;
                    session.Dungeon.BossMonsterKills = 0;
                    session.Dungeon.ChampionRewardOrdinal = 0;
                    session.Character.TownId = session.Dungeon.ReturnTownId;
                    session.Character.AreaId = session.Dungeon.ReturnAreaId;
                    session.Character.X = session.Dungeon.ReturnX;
                    session.Character.Y = session.Dungeon.ReturnY;
                    session.Character.Direction = session.Dungeon.ReturnDirection;
                    session.Character.MovementValue = 100;
                    await SaveCurrentLocationAsync(serverToken);

                    var townState = GameProtocolEngine.CreateUserState(
                        session.Character.LocalUserId,
                        state: 0);
                    await townState.WriteAsync(stream, serverToken);
                    Interlocked.Increment(ref runtimeSession.SentPackets);
                    LogPacket("TX", runtimeSession, townState);

                    var townUser = new GameTownUser(
                        session.Character.LocalUserId,
                        session.Character.X,
                        session.Character.Y,
                        session.Character.Direction);
                    var userArea = GameProtocolEngine.CreateUserArea(
                        session.Character.TownId,
                        session.Character.AreaId,
                        townUser);
                    await userArea.WriteAsync(stream, serverToken);
                    Interlocked.Increment(ref runtimeSession.SentPackets);
                    LogPacket("TX", runtimeSession, userArea);

                    var areaUsers = GameProtocolEngine.CreateAreaUsers(
                        session.Character.TownId,
                        session.Character.AreaId,
                        [townUser]);
                    await areaUsers.WriteAsync(stream, serverToken);
                    Interlocked.Increment(ref runtimeSession.SentPackets);
                    LogPacket("TX", runtimeSession, areaUsers);
                    session.Character.AreaRosterInitialized = true;
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
                            session.Character.LocalUserId,
                            reason);
                    }
                    else
                    {
                        logger.LogInformation(
                            "Dungeon {DungeonId} returned user {UserId} to town through {Reason}; exp={Experience}, weakened={IsWeakened}, enteredWeakness={EnteredWeakness}, blackDiamond={HasBlackDiamond}.",
                            leavingDungeonId,
                            session.Character.LocalUserId,
                            reason,
                            gainedExperience,
                            session.Character.Active?.IsWeakened ?? false,
                            enteredWeakness,
                            hasBlackDiamond);
                    }
                }

                Task SendInventoryMoveNoOpAsync(
                    byte sourceListType,
                    ushort sourceSlot,
                    byte destinationListType,
                    ushort destinationSlot,
                    string reason) =>
                    _sessionSenders.SendInventoryMoveNoOpAsync(
                        connectionContext, sourceListType, sourceSlot, destinationListType, destinationSlot, reason, serverToken);

                Task SendMainInventoryMoveDeltaAsync(params ushort[] slots) =>
                    _sessionSenders.SendMainInventoryMoveDeltaAsync(connectionContext, serverToken, slots);

                IReadOnlyList<ushort> GetAcceptableQuestIds() =>
                    _sessionSenders.GetAcceptableQuestIds(connectionContext);

                IReadOnlyList<ushort> GetVisibleDungeonIds() =>
                    _sessionSenders.GetVisibleDungeonIds(connectionContext);

                Task SendDungeonPermissionsAsync() =>
                    _sessionSenders.SendDungeonPermissionsAsync(connectionContext, serverToken);

                Task SendAcceptableQuestListAsync() =>
                    _sessionSenders.SendAcceptableQuestListAsync(connectionContext, serverToken);
                async Task CompleteTutorialAndReturnAsync(string reason)
                {
                    if (session.Character.Active is null
                        || !session.Dungeon.TutorialActive
                        || !session.Dungeon.ClearEnabled
                        || session.Dungeon.AliveMonsters.Count != 0)
                    {
                        logger.LogWarning(
                            "Ignored tutorial completion because its room is not cleared for user {UserId}.",
                            session.Character.LocalUserId);
                        return;
                    }

                    CharacterQuestRecord? tutorialQuest = null;
                    var tutorialQuestWasActive = session.Quest.Current.Any(quest =>
                        quest.QuestId == QuestCatalog.TutorialCompletionQuestId);
                    if (!tutorialQuestWasActive
                        && !session.Quest.CompletedIds.Contains(QuestCatalog.TutorialCompletionQuestId))
                    {
                        if (session.Quest.Current.Count >= 3)
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
                        session.Login.AccountName,
                        session.Character.Active.Id,
                        tutorialQuest: tutorialQuest,
                        level: session.Character.Active.Level,
                        experience: session.Character.Active.Experience,
                        skillPoints: session.Skill.CurrentSp,
                        cancellationToken: serverToken);
                    if (saved is null)
                    {
                        logger.LogWarning(
                            "Could not persist tutorial completion for character {CharacterId}.",
                            session.Character.Active.Id);
                        return;
                    }

                    session.Character.Active = session.Character.Active with
                    {
                        TutorialStarted = saved.TutorialStarted,
                        Quests = saved.Quests,
                        CompletedQuestIds = saved.CompletedQuestIds,
                        Level = saved.Level,
                        Experience = saved.Experience,
                        Sp = saved.Sp
                    };
                    session.Quest.Current = (saved.Quests ?? [])
                        .Where(quest => quest.QuestId != ushort.MaxValue)
                        .Take(3)
                        .ToList();
                    session.Quest.CompletedIds = (saved.CompletedQuestIds ?? []).ToHashSet();
                    var tutorialQuestActivated = !tutorialQuestWasActive
                        && session.Quest.Current.Any(quest =>
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
                            session.Character.Active.Id);
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
                        if (isChannelPort && session.Dungeon.InSelection
                            && session.Dungeon.SelectionDeadline is { } selectionDeadline)
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
                        else if (isChannelPort && session.Dungeon.DeathTimeout is { } deathTimeout)
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
                                if (session.Dungeon.DeathTimeout is { } currentTimeout
                                    && currentTimeout.Generation == deathTimeout.Generation
                                    && !session.Dungeon.CharacterAlive
                                    && session.Dungeon.CurrentDungeonId != 0)
                                {
                                    session.Dungeon.DeathTimeout = null;
                                    logger.LogInformation(
                                        "Dungeon death countdown expired for user {UserId} in dungeon {DungeonId}; returning to town.",
                                        session.Character.LocalUserId,
                                        session.Dungeon.CurrentDungeonId);
                                    await ReturnToTownFromDungeonAsync(
                                        "death-countdown-expired",
                                        forcedDungeonExit: true);
                                }

                                continue;
                            }
                        }
                        else if (isChannelPort
                            && session.Dungeon.CardSelectionDeadline is { } cardSelectionDeadline)
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
                                if (session.Dungeon.CardSelectionDeadline
                                        == cardSelectionDeadline)
                                {
                                    session.Dungeon.CardSelectionDeadline = null;
                                    if (session.Dungeon.CardRightsSent
                                        && session.Dungeon.ClearReward is not null
                                        && !session.Dungeon.FreeCardIndex.HasValue
                                        && !await TryAutomaticallySelectFreeCardAsync(
                                            "card-selection-timer"))
                                    {
                                        session.Dungeon.CardSelectionDeadline =
                                            DateTimeOffset.UtcNow
                                            + TimeSpan.FromSeconds(1);
                                    }
                                }

                                continue;
                            }
                        }
                        else if (isChannelPort && !session.Premium.ExpiryUnix.IsEmpty)
                        {
                            var observedExpiries = session.Premium.ExpiryUnix.ToArray();
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
                                    if (session.Premium.ExpiryUnix.TryGetValue(
                                            expired.Key,
                                            out var currentExpiry)
                                        && currentExpiry <= now
                                        && session.Premium.ExpiryUnix.TryRemove(expired.Key, out _)
                                        && session.Character.Active is not null)
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

                    if (Interlocked.Exchange(ref session.Mailbox.PendingAlarm, 0) != 0
                        && session.Character.Active is not null)
                    {
                        await SendNewMailAlarmAsync();
                    }

                    while (session.Premium.PendingUpdates.TryDequeue(out var premiumUpdate))
                    {
                        if (session.Character.Active is not null)
                        {
                            await SendPremiumServiceInfoAsync(
                                premiumUpdate.ServiceType,
                                premiumUpdate.RemainingSeconds);
                        }
                    }

                    var queuedCeraValue = Interlocked.Exchange(
                        ref session.Premium.PendingCeraValue,
                        -1);
                    if (queuedCeraValue >= 0 && session.Character.Active is not null)
                    {
                        session.Currency.Cash = queuedCeraValue;
                        var ceraNotification = GameProtocolEngine.CreateCeraUpdate(
                            success: true,
                            value: (uint)queuedCeraValue);
                        await ceraNotification.WriteAsync(stream, serverToken);
                        Interlocked.Increment(ref runtimeSession.SentPackets);
                        LogPacket("TX", runtimeSession, ceraNotification);
                    }

                    if (Volatile.Read(ref session.Weakness.PendingFatigueReset) != 0
                        && session.Character.Active is not null)
                    {
                        session.Character.Active = session.Character.Active with { UsedFatigue = 0 };
                        if (session.Dungeon.CurrentDungeonId == 0)
                        {
                            await SendTownFatigueStateAsync();
                        }
                    }

                    var queuedWeaknessRecovery = Interlocked.Exchange(
                        ref session.Weakness.PendingWeaknessRecovery,
                        -1);
                    if (queuedWeaknessRecovery >= 0 && session.Character.Active is not null)
                    {
                        session.Character.Active = session.Character.Active with
                        {
                            WeaknessRecovery = (byte)queuedWeaknessRecovery,
                            LegacyIsWeakened = null
                        };
                    }

                    if (isChannelPort)
                    {
                        if (await TryDispatchGameCommandAsync(request))
                        {
                            continue;
                        }













                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.CompoundItemCommand)
                        {
                            if (session.Character.Active is null
                                || session.Dungeon.CurrentDungeonId != 0
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

                            if (session.Inventory.CompoundReplay.TryGetValue(
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
                                    session.Currency.Gold,
                                    session.Currency.VictoryPoints,
                                    session.Inventory.Main.Values);
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

                            var expectedGold = session.Currency.Gold;
                            var expectedInventory = session.Inventory.Main.Values
                                .OrderBy(item => item.Slot)
                                .ToArray();
                            if (!CompoundItemPlanner.TryCreate(
                                    session.Inventory.Main,
                                    session.Currency.Gold,
                                    session.Skill.Current,
                                    compoundItemCommand.SourceValue,
                                    compoundItemCommand.SourceIsItemId,
                                    compoundItemCommand.CraftCount,
                                    GetInventoryWeightLimit(session.Character.Active),
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
                                session.Login.AccountName,
                                session.Character.Active.Id,
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
                            session.Currency.Gold = savedCompoundItem.Gold;
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
                            while (session.Inventory.CompoundReplay.Count >= CompoundReplayCapacity
                                && session.Inventory.CompoundReplayOrder.TryDequeue(out var oldestSequence))
                            {
                                session.Inventory.CompoundReplay.Remove(oldestSequence);
                            }

                            session.Inventory.CompoundReplay.Add(
                                compoundItemCommand.Sequence,
                                new CompoundItemReplayEntry(
                                    request.Body.ToArray(),
                                    compoundItemReply,
                                    refreshWarehouse));
                            session.Inventory.CompoundReplayOrder.Enqueue(compoundItemCommand.Sequence);
                            await compoundItemReply.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, compoundItemReply);
                            // DF2008 does not reliably apply the CMD27 delta when
                            // a consumed recipe slot is immediately reused by the
                            // crafted result. Always follow the acknowledgement
                            // with the authoritative main-inventory snapshot.
                            var inventoryRefresh = CreateMainInventoryPacket(
                                session.Currency.Gold,
                                session.Currency.VictoryPoints,
                                session.Inventory.Main.Values);
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
                                session.Currency.Gold);
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.DisjointItemCommand)
                        {
                            if (session.Character.Active is null || request.Body.Length < 5)
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
                                    session.Inventory.Main,
                                    sourceSlot,
                                    itemSpace,
                                    GetInventoryWeightLimit(session.Character.Active),
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
                                    session.Inventory.Warehouse.Values,
                                    disjointPlan.ResponseRewards.Select(reward =>
                                        reward.ItemId));
                            var hasDisjointWarehouseUpgrade =
                                disjointPlan.ResponseRewards.Any(reward =>
                                    CharacterWarehouseProgression
                                        .TryGetItemTargetCapacity(reward.ItemId, out _));
                            var savedDisjoint = await store.SaveCharacterItemSpacesAsync(
                                session.Login.AccountName,
                                session.Character.Active.Id,
                                session.Currency.Gold,
                                disjointWarehouseSettlement.Inventory,
                                session.Inventory.Equipped.Values,
                                disjointWarehouseSettlement.Warehouse,
                                serverToken,
                                avatarInventory: session.Inventory.Avatar.Values,
                                creatureInventory: session.Inventory.Creature.Values,
                                warehouseCapacity:
                                    disjointWarehouseSettlement.Capacity,
                                expectedWarehouseCapacity: session.Inventory.WarehouseCapacity);
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
                                    session.Currency.Gold,
                                    session.Currency.VictoryPoints,
                                    session.Inventory.Main.Values);
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
                            if (session.Character.Active is null || request.Body.Length < 4)
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
                                    session.Inventory.Main,
                                    session.Inventory.Avatar,
                                    session.Inventory.Creature,
                                    sourceSlot,
                                    session.Character.Active.Level,
                                    session.Currency.Gold,
                                    GetInventoryWeightLimit(session.Character.Active),
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
                                    session.Inventory.Warehouse.Values,
                                    lotteryPlan.Reward.ItemId == 0
                                        ? []
                                        : [lotteryPlan.Reward.ItemId]);
                            var isLotteryWarehouseUpgrade =
                                CharacterWarehouseProgression.TryGetItemTargetCapacity(
                                    lotteryPlan.Reward.ItemId,
                                    out _);
                            var savedLottery = await store.SaveCharacterItemSpacesAsync(
                                session.Login.AccountName,
                                session.Character.Active.Id,
                                lotteryPlan.Gold,
                                lotteryWarehouseSettlement.Inventory,
                                session.Inventory.Equipped.Values,
                                lotteryWarehouseSettlement.Warehouse,
                                serverToken,
                                avatarInventory: lotteryPlan.AvatarInventory.Values,
                                creatureInventory: lotteryPlan.CreatureInventory.Values,
                                warehouseCapacity:
                                    lotteryWarehouseSettlement.Capacity,
                                expectedWarehouseCapacity: session.Inventory.WarehouseCapacity);
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
                            session.Currency.Gold = lotteryPlan.Gold;
                            session.Inventory.Avatar = (savedLottery.AvatarInventory ?? [])
                                .ToDictionary(item => item.Slot);
                            session.Inventory.Creature = (savedLottery.CreatureInventory ?? [])
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
                                    session.Currency.Gold,
                                    session.Currency.VictoryPoints,
                                    session.Inventory.Main.Values);
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
                                             session.Inventory.Creature.Values))
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
                                session.Currency.Gold);
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
                            var validRename = session.Character.Active is not null
                                && parsedRename
                                && renameItemSpace == 7
                                && CharacterCreatureInventoryPlanner.TryRename(
                                    session.Inventory.Creature,
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
                                    session.Character.Active is null
                                        ? "no active character"
                                        : renameFailure);
                                continue;
                            }

                            var renamingCharacter = session.Character.Active!;
                            var savedRename = await store.SaveCharacterItemSpacesAsync(
                                session.Login.AccountName,
                                renamingCharacter.Id,
                                session.Currency.Gold,
                                session.Inventory.Main.Values,
                                session.Inventory.Equipped.Values,
                                session.Inventory.Warehouse.Values,
                                serverToken,
                                avatarInventory: session.Inventory.Avatar.Values,
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

                            session.Character.Active = savedRename;
                            session.Inventory.Creature = renamePlan.Inventory;
                            var renameNotification =
                                GameProtocolEngine.CreateCreatureRename(
                                    session.Character.LocalUserId,
                                    EncodeCreatureName(
                                        renamePlan.CreatureName,
                                        session.Inventory.Creature[
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
                                         session.Inventory.Creature.Values))
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
                                session.Character.Active.Id);
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
                            var validHatch = session.Character.Active is not null
                                && parsedHatch
                                && hatchItemSpace == 7
                                && CharacterCreatureInventoryPlanner.TryHatch(
                                    session.Inventory.Creature,
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
                                    session.Character.Active is null
                                        ? "no active character"
                                        : hatchFailure);
                                continue;
                            }

                            var hatchingCharacter = session.Character.Active!;
                            var savedHatch = await store.SaveCharacterItemSpacesAsync(
                                session.Login.AccountName,
                                hatchingCharacter.Id,
                                session.Currency.Gold,
                                session.Inventory.Main.Values,
                                session.Inventory.Equipped.Values,
                                session.Inventory.Warehouse.Values,
                                serverToken,
                                avatarInventory: session.Inventory.Avatar.Values,
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

                            session.Character.Active = savedHatch;
                            session.Inventory.Creature = hatchPlan.Inventory;
                            var hatchReply = GameProtocolEngine.CreateHatchCreatureReply(
                                success: true);
                            await hatchReply.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, hatchReply);
                            foreach (var creaturePacket in CreateCreatureInventoryPackets(
                                         session.Inventory.Creature.Values))
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
                                session.Character.Active.Id);
                            continue;
                        }









                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.UpgradeItemCommand)
                        {
                            if (session.Character.Active is null
                                || session.Dungeon.CurrentDungeonId != 0
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
                                    session.Inventory.Main,
                                    session.Inventory.Equipped,
                                    session.Currency.Gold,
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
                                    session.Login.AccountName,
                                    session.Character.Active.Id,
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
                            session.Inventory.Equipped = (savedReinforcement.Equipment ?? [])
                                .ToDictionary(item => item.Slot);
                            session.Currency.Gold = savedReinforcement.Gold;
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
                                    session.Currency.Gold,
                                    session.Currency.VictoryPoints,
                                    session.Inventory.Main.Values,
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
                                session.Currency.Gold,
                                reinforcementPlan.FailureRoll,
                                EquipmentReinforcementCatalog.FailureRollUpperBound);
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId
                                == GameProtocolEngine.ResetItemAttributeCommand)
                        {
                            if (session.Character.Active is null
                                || session.Dungeon.CurrentDungeonId != 0
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
                                    session.Inventory.Main,
                                    session.Inventory.Equipped,
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
                                    session.Login.AccountName,
                                    session.Character.Active.Id,
                                    session.Inventory.Main.Values,
                                    session.Inventory.Equipped.Values,
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
                            session.Inventory.Equipped = (savedModification.Equipment ?? [])
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
                                ? session.Inventory.Equipped[modificationPlan.TargetSlot]
                                : session.Inventory.Main[modificationPlan.TargetSlot];
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
                            if (session.Character.Active is null
                                || session.Dungeon.CurrentDungeonId != 0
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
                                    session.Inventory.Main,
                                    session.Inventory.Avatar,
                                    session.Character.Active.Job,
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
                                    session.Character.Active.Job,
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
                                    session.Login.AccountName,
                                    session.Character.Active.Id,
                                    session.Currency.Gold,
                                    compoundPlan.MainInventory.Values,
                                    session.Inventory.Equipped.Values,
                                    session.Inventory.Warehouse.Values,
                                    serverToken,
                                    avatarInventory: compoundPlan.AvatarInventory.Values,
                                    creatureInventory: session.Inventory.Creature.Values);
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
                            session.Inventory.Equipped = (savedCompound.Equipment ?? [])
                                .ToDictionary(item => item.Slot);
                            session.Inventory.Avatar = (savedCompound.AvatarInventory ?? [])
                                .ToDictionary(item => item.Slot);
                            session.Inventory.Creature = (savedCompound.CreatureInventory ?? [])
                                .ToDictionary(item => item.Slot);
                            session.Currency.Gold = savedCompound.Gold;
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
                                session.Currency.Gold,
                                session.Currency.VictoryPoints,
                                session.Inventory.Main.Values);
                            await inventoryRefresh.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, inventoryRefresh);

                            var avatarRefresh =
                                CreateAvatarInventoryPacket(session.Inventory.Avatar.Values);
                            await avatarRefresh.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, avatarRefresh);
                            logger.LogInformation(
                                "Compounded avatars for job {Job}, part {Part}: items {FirstItemId}/{SecondItemId} at {FirstSlot}/{SecondSlot} -> {ResultItemId} at {ResultSlot}, option={Option}, rare={Rare}, rate={RareRate}/{RateUpperBound}, material={MaterialItemId}x{MaterialCount}, remaining={MaterialRemaining}.",
                                session.Character.Active.Job,
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
                            if (session.Character.Active is null || session.Dungeon.CurrentDungeonId == 0)
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
                                    0 => session.Inventory.Main,
                                    7 => session.Inventory.Creature,
                                    // The planner rejects unsupported spaces before
                                    // consulting this fallback dictionary.
                                    _ => session.Inventory.Main
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
                                groundItem = session.Dungeon.GroundItems.AddPlayerDropped(
                                    dropPlan.DroppedItem,
                                    session.Character.LocalUserId,
                                    session.Dungeon.RoomX,
                                    session.Dungeon.RoomY,
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
                                : session.Inventory.Main;
                            var plannedCreatureInventory = dropPlan.ItemSpace == 7
                                ? dropPlan.Inventory
                                : session.Inventory.Creature;
                            var savedDrop = await store.SaveCharacterItemSpacesAsync(
                                session.Login.AccountName,
                                session.Character.Active.Id,
                                session.Currency.Gold,
                                plannedMainInventory.Values,
                                session.Inventory.Equipped.Values,
                                session.Inventory.Warehouse.Values,
                                serverToken,
                                avatarInventory: session.Inventory.Avatar.Values,
                                creatureInventory: plannedCreatureInventory.Values);
                            if (savedDrop is null)
                            {
                                session.Dungeon.GroundItems.Remove(groundItem.GroundId);
                                var error =
                                    GameProtocolEngine.CreateDropItemErrorReply(
                                        19,
                                        replyItemSpace);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                continue;
                            }

                            session.Character.Active = savedDrop;
                            session.Inventory.Main = plannedMainInventory;
                            session.Inventory.Creature = plannedCreatureInventory;

                            var dropNotification = GameProtocolEngine.CreateDropItem(
                                session.Character.LocalUserId,
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
                                session.Dungeon.CurrentDungeonId,
                                session.Dungeon.RoomX,
                                session.Dungeon.RoomY);
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
                                session.Character.X = BinaryPrimitives.ReadInt16LittleEndian(
                                    request.Body.AsSpan(2, sizeof(short)));
                                session.Character.Y = BinaryPrimitives.ReadInt16LittleEndian(
                                    request.Body.AsSpan(4, sizeof(short)));
                                session.Character.Direction = request.Body[6];
                                session.Character.MovementValue = BinaryPrimitives.ReadUInt16LittleEndian(
                                    request.Body.AsSpan(7, sizeof(ushort)));
                            }

                            // Movement is only mirrored while the actor is a
                            // town entity; dungeon-run positions stay private.
                            if (session.Dungeon.CurrentDungeonId == 0 && !session.Dungeon.InSelection)
                            {
                                partyCoordinator.UpdatePresencePosition(
                                    runtimeSession.Id,
                                    session.Character.X,
                                    session.Character.Y,
                                    session.Character.Direction);
                            }

                            // The local client already owns its movement.
                            // Re-sending USER_POSITION or AREA_USERS on every
                            // one-second update makes it rebuild the area and
                            // snaps the character back. Seed the roster once
                            // when entering the town session, then only retain
                            // subsequent coordinates server-side.
                            if (!session.Character.AreaRosterInitialized)
                            {
                                var townUser = new GameTownUser(
                                    session.Character.LocalUserId,
                                    session.Character.X,
                                    session.Character.Y,
                                    session.Character.Direction);

                                var areaUsers = GameProtocolEngine.CreateAreaUsers(
                                    session.Character.TownId,
                                    session.Character.AreaId,
                                    [townUser]);
                                await areaUsers.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, areaUsers);
                                session.Character.AreaRosterInitialized = true;
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
                                session.Character.TownId = request.Body[2];
                                session.Character.AreaId = request.Body[3];
                                session.Character.X = BinaryPrimitives.ReadInt16LittleEndian(
                                    request.Body.AsSpan(4, sizeof(short)));
                                session.Character.Y = BinaryPrimitives.ReadInt16LittleEndian(
                                    request.Body.AsSpan(6, sizeof(short)));
                            }

                            await SaveCurrentLocationAsync(serverToken);

                            var townUser = new GameTownUser(
                                session.Character.LocalUserId,
                                session.Character.X,
                                session.Character.Y,
                                session.Character.Direction);
                            var userArea = GameProtocolEngine.CreateUserArea(
                                session.Character.TownId,
                                session.Character.AreaId,
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
                                session.Character.TownId,
                                session.Character.AreaId,
                                [townUser]);
                            await areaUsers.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, areaUsers);
                            session.Character.AreaRosterInitialized = true;
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
                            if (session.Character.Active is null
                                || session.Dungeon.CurrentDungeonId != 0
                                || session.Dungeon.InSelection)
                            {
                                var invalidState =
                                    GameProtocolEngine.CreateRecoverStaminaError(4);
                                await invalidState.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, invalidState);
                                continue;
                            }

                            var recoveryCost = staminaRecoveryCatalog.GetCost(
                                session.Character.Active.Level);
                            var recovery = await store.RecoverCharacterWeaknessAsync(
                                session.Character.Active.Id,
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
                                    session.Character.Active.Id,
                                    recovery.Failure,
                                    recoveryCost,
                                    session.Currency.Gold);
                                continue;
                            }

                            session.Character.Active = recovery.Character;
                            session.Currency.Gold = session.Character.Active.Gold;
                            var recoveryReply =
                                GameProtocolEngine.CreateRecoverStaminaReply(session.Currency.Gold);
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
                                session.Character.Active.Id,
                                recoveryCost,
                                session.Currency.Gold);
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
                            var requiresTutorial = session.Character.Active is { TutorialStarted: false };
                            if (session.Character.Active is null
                                || (!requiresTutorial
                                    && !DungeonFatigueRules.CanEnterNewDungeon(
                                        session.Character.Active.UsedFatigue,
                                        maximumFatigue)))
                            {
                                var fatigueError = GameProtocolEngine
                                    .CreateEnterDungeonSelectionInsufficientFatigueReply();
                                await fatigueError.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, fatigueError);
                                logger.LogInformation(
                                    "Rejected dungeon-selection entry for user {UserId}: used fatigue {UsedFatigue}/{MaximumFatigue}.",
                                    session.Character.LocalUserId,
                                    session.Character.Active?.UsedFatigue ?? 0,
                                    maximumFatigue);
                                continue;
                            }

                            // DFLegacy transitions to dungeon selection after state=1,
                            // the UDP peer table, local party-host index, and
                            // notification 27's compact solo-session body.
                            session.Dungeon.ReturnTownId = session.Character.TownId;
                            session.Dungeon.ReturnAreaId = session.Character.AreaId;
                            session.Dungeon.ReturnX = session.Character.X;
                            session.Dungeon.ReturnY = session.Character.Y;
                            session.Dungeon.ReturnDirection = session.Character.Direction;
                            session.Dungeon.InSelection = true;
                            session.Dungeon.SelectionWorldMap = worldMapCatalog.TryGetForGate(
                                session.Character.TownId, session.Character.AreaId, out var gateWorldMap)
                                ? gateWorldMap : null;
                            // Return before DF2008 auto-selects at 30.3 seconds.
                            session.Dungeon.SelectionDeadline = requiresTutorial
                                ? null : DateTimeOffset.UtcNow.AddSeconds(30);
                            await SaveCurrentLocationAsync(serverToken);
                            var dungeonState = GameProtocolEngine.CreateUserState(
                                session.Character.LocalUserId,
                                state: 1);
                            await dungeonState.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, dungeonState);

                            var peerInfo = gameplayDatagram.TryGetPeerInfo(
                                runtimeSession.Id,
                                session.Character.LocalUserId,
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
                            if (session.Character.Active is { TutorialStarted: false })
                            {
                                logger.LogInformation(
                                    "Ignored return from dungeon selection for {CharacterId} until the tutorial is complete.",
                                    session.Character.Active.Id);
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
                            var partyIdentity = session.Character.Active is null
                                ? null
                                : new PartyMemberIdentity(
                                    runtimeSession.Id,
                                    session.Character.Active.Id,
                                    session.Login.AccountUid,
                                    session.Character.LocalUserId,
                                    session.Character.Active.Name,
                                    (byte)Math.Clamp(session.Character.Active.Level, (byte)1, byte.MaxValue),
                                    (byte)Math.Clamp(session.Character.Active.Job, byte.MinValue, byte.MaxValue));
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

                            var requiresTutorial = session.Character.Active is { TutorialStarted: false };
                            if (!requiresTutorial && session.Dungeon.InSelection
                                && session.Dungeon.SelectionDeadline is { } entryDeadline
                                && DateTimeOffset.UtcNow >= entryDeadline)
                            {
                                // A ready packet may win Task.WhenAny at the deadline.
                                // Check again before layout generation or ticket/fatigue consumption.
                                await ReturnToTownFromDungeonAsync("selection-countdown-expired");
                                continue;
                            }

                            if (!requiresTutorial && !session.Dungeon.InSelection)
                            {
                                logger.LogWarning("Ignored stale dungeon selection for {CharacterId}.", session.Character.Active?.Id);
                                continue;
                            }

                            if (!requiresTutorial && dungeonOption != 0
                                && (session.Dungeon.SelectionWorldMap is null
                                    || !session.Dungeon.SelectionWorldMap.DungeonIds.Contains(dungeonId)
                                    || !session.Dungeon.SelectionWorldMap.MeetsHellQuests(session.Quest.CompletedIds)
                                    || !session.Dungeon.SelectionWorldMap.HasHellItems(session.Inventory.Main.Values)
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
                                    session.Character.Active!.Id,
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
                                        session.Character.Active?.Id);
                                    continue;
                                }

                                var tutorialMonsters = GameProtocolEngine.CreateTutorialMonsters();
                                session.Dungeon.InSelection = false;
                                session.Dungeon.CurrentDungeonId = GameProtocolEngine.TutorialDungeonId;
                                session.Dungeon.Layout = null;
                                session.Dungeon.RoomX = 0;
                                session.Dungeon.RoomY = 0;
                                session.Dungeon.Difficulty = difficulty;
                                session.Dungeon.Room = new DungeonRoomDefinition(
                                    GameProtocolEngine.TutorialMapId,
                                    DungeonMapType.Boss,
                                    tutorialMonsters,
                                    Topology: 'B',
                                    HasScriptBossActor: false,
                                    PassiveObjects: [],
                                    PassiveItemSpawns: []);
                                session.Dungeon.AliveMonsters.Clear();
                                session.Dungeon.AliveMonsters.UnionWith(
                                    tutorialMonsters.Select(monster => monster.UniqueId));
                                session.Dungeon.ReportedBossDeaths.Clear();
                                session.Dungeon.BossCascadeDeaths.Clear();
                                session.Dungeon.DestroyedPassiveObjects.Clear();
                                session.Dungeon.GroundItems.ResetDungeon();
                                session.Dungeon.Run.Stop();
                                session.Dungeon.TutorialActive = true;
                                session.Dungeon.CharacterAlive = true;
                                session.Dungeon.DeathGeneration++;
                                session.Dungeon.DeathTimeout = null;
                                session.Dungeon.ClearEnabled = false;
                                session.Dungeon.ResultSent = false;
                                session.Dungeon.CardLayoutSent = false;
                                session.Dungeon.CardRightsSent = false;
                                session.Dungeon.CardSelectionDeadline = null;
                                session.Dungeon.ClearReward = null;
                                session.Dungeon.FreeCardIndex = null;
                                session.Dungeon.GoldCardIndex = null;
                                session.Dungeon.MonsterExperienceGained = 0;
                                session.Dungeon.ClearExperience = null;
                                session.Dungeon.ConsumedFatigue = 0;
                                session.Dungeon.CreatureStomachCheckpointSeconds = null;
                                session.Dungeon.LeveledUp = false;
                                session.Dungeon.NormalMonsterKills = 0;
                                session.Dungeon.ChampionMonsterKills = 0;
                                session.Dungeon.BossMonsterKills = 0;
                                session.Dungeon.ChampionRewardOrdinal = 0;

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
                                    session.Character.Active!.Id,
                                    tutorialMonsters.Length,
                                    difficulty,
                                    dungeonOption);
                                continue;
                            }

                            if (session.Character.Active is null
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
                                    session.Character.Active.Id);
                                continue;
                            }

                            var maximumDifficulty =
                                DungeonDifficultyProgression.GetMaximumDifficulty(
                                    session.Character.Active.DungeonProgress,
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
                                    session.Character.Active.Id,
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

                            var unfinishedQuestDefinitions = session.Quest.Current
                                .Where(quest => quest.Trigger != 0)
                                .Select(quest => questCatalog.TryGetDefinition(quest.QuestId, out var definition)
                                    ? definition : null)
                                .OfType<QuestDefinition>();
                            if (dungeonCatalog.TryApplyQuestMap(
                                    dungeonLayout, unfinishedQuestDefinitions, out var questMapId))
                            {
                                logger.LogInformation(
                                    "Dungeon {DungeonId} selected rescue quest map {MapId} for character {CharacterId}.",
                                    dungeonId, questMapId, session.Character.Active.Id);
                            }

                            if (!session.Dungeon.Run.TryStart(
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
                            if (session.Dungeon.Run.HellMode)
                            {
                                var plannedInventory = session.Inventory.Main.ToDictionary();
                                if (session.Dungeon.SelectionWorldMap is null
                                    || !session.Dungeon.SelectionWorldMap.TryConsumeHellItems(session.Inventory.Main, out plannedInventory))
                                {
                                    session.Dungeon.Run.Stop();
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

                                var changedTickets = session.Inventory.Main.Values
                                    .Where(item => !plannedInventory.TryGetValue(item.Slot, out var remaining)
                                        || remaining.CountOrValue != item.CountOrValue)
                                    .OrderBy(item => item.Slot).ToArray();
                                consumedHellTicket = changedTickets.FirstOrDefault();

                                var savedTicketConsumption =
                                    await store.SaveCharacterInventoryAsync(
                                        session.Login.AccountName,
                                        session.Character.Active.Id,
                                        session.Currency.Gold,
                                        plannedInventory.Values,
                                        session.Inventory.Equipped.Values,
                                        serverToken);
                                if (savedTicketConsumption is null)
                                {
                                    session.Dungeon.Run.Stop();
                                    var ticketSaveError = GameProtocolEngine.CreateCommandError(
                                        request,
                                        errorCode: 19);
                                    await ticketSaveError.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, ticketSaveError);
                                    continue;
                                }

                                session.Character.Active = savedTicketConsumption;
                                session.Inventory.Main = plannedInventory;
                                pendingHellTicketUpdate = GameProtocolEngine.CreateUpdateItemList(
                                    0,
                                    changedTickets.Select(item => plannedInventory.TryGetValue(item.Slot, out var remaining)
                                        ? ToGameInventoryEntry(remaining)
                                        : new GameInventoryEntry(item.Slot, ushort.MaxValue, 0)).ToArray());
                            }

                            session.Dungeon.InSelection = false;
                            session.Dungeon.TutorialActive = false;
                            session.Dungeon.CurrentDungeonId = dungeonId;
                            session.Dungeon.Layout = dungeonLayout;
                            session.Dungeon.RoomX = session.Dungeon.Run.RoomX;
                            session.Dungeon.RoomY = session.Dungeon.Run.RoomY;
                            session.Dungeon.Difficulty = difficulty;
                            session.Dungeon.Room = initialTransition.Room;
                            session.Dungeon.AliveMonsters.Clear();
                            session.Dungeon.AliveMonsters.UnionWith(
                                session.Dungeon.Room.Monsters.Select(monster => monster.UniqueId));
                            session.Dungeon.ReportedBossDeaths.Clear();
                            session.Dungeon.BossCascadeDeaths.Clear();
                            session.Dungeon.DestroyedPassiveObjects.Clear();
                            session.Dungeon.GroundItems.ResetDungeon();
                            session.Dungeon.CharacterAlive = true;
                            session.Dungeon.DeathGeneration++;
                            session.Dungeon.DeathTimeout = null;
                            session.Dungeon.ClearEnabled = false;
                            session.Dungeon.ResultSent = false;
                            session.Dungeon.CardLayoutSent = false;
                            session.Dungeon.CardRightsSent = false;
                            session.Dungeon.CardSelectionDeadline = null;
                            session.Dungeon.ClearReward = null;
                            session.Dungeon.FreeCardIndex = null;
                            session.Dungeon.GoldCardIndex = null;
                            session.Dungeon.MonsterExperienceGained = 0;
                            session.Dungeon.ClearExperience = null;
                            session.Dungeon.ConsumedFatigue = 0;
                            session.Dungeon.CreatureStomachCheckpointSeconds = null;
                            session.Dungeon.LeveledUp = false;
                            session.Dungeon.NormalMonsterKills = 0;
                            session.Dungeon.ChampionMonsterKills = 0;
                            session.Dungeon.BossMonsterKills = 0;
                            session.Dungeon.ChampionRewardOrdinal = 0;

                            byte hellRoomX = 0;
                            byte hellRoomY = 0;
                            if (session.Dungeon.Run.HellMode)
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
                                session.Dungeon.Room,
                                session.Dungeon.GroundItems,
                                session.Character.LocalUserId,
                                session.Dungeon.RoomX,
                                session.Dungeon.RoomY);
                            var startMap = GameProtocolEngine.CreateStartMap(
                                roomX: session.Dungeon.RoomX,
                                roomY: session.Dungeon.RoomY,
                                mapId: session.Dungeon.Room.MapId,
                                monsters: session.Dungeon.Room.Monsters,
                                passiveObjectDrops: passiveObjectDrops,
                                randomSeed: session.Dungeon.Room.ClientRandomSeed,
                                roomStateFlag: session.Dungeon.Run.RoomStateFlag,
                                hellPartyMode: session.Dungeon.Run.CurrentHellPartyMode);
                            await startMap.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, startMap);
                            session.Dungeon.CreatureStomachCheckpointSeconds =
                                DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                            await ConsumeDungeonRoomFatigueAsync(
                                session.Dungeon.RoomX,
                                session.Dungeon.RoomY);
                            await EnableEmptyBossRoomSettlementAsync(session.Dungeon.Room);
                            logger.LogInformation(
                                "Dungeon {DungeonId} selected maze {MazeIndex}, start ({StartX},{StartY}), boss ({BossX},{BossY}), start map {MapId}, monsters={MonsterCount}, champions={ChampionCount}, mapChampionBase={MapChampionBase}, clientSeed={ClientSeed}, hellMode={HellMode}, partyMode={HellPartyMode}, ticket={TicketItemId}.",
                                dungeonId,
                                dungeonLayout.MazeIndex,
                                dungeonLayout.StartX,
                                dungeonLayout.StartY,
                                dungeonLayout.BossX,
                                dungeonLayout.BossY,
                                session.Dungeon.Room.MapId,
                                session.Dungeon.Room.Monsters.Length,
                                session.Dungeon.Room.Monsters.Count(monster =>
                                    monster.Type == GameDungeonMonsterTypes.Champion),
                                session.Dungeon.Room.BaseChampionCount,
                                session.Dungeon.Room.ClientRandomSeed,
                                session.Dungeon.Run.HellMode,
                                session.Dungeon.Run.CurrentHellPartyMode,
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
                            if (session.Character.Active is not null
                                && session.Dungeon.CurrentDungeonId != 0
                                && skillCatalog.TryGetDefinition(
                                    session.Character.Active.Job,
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
                                    session.Character.Active.Job,
                                    skillId,
                                    usedSkill.ConsumedItemId,
                                    usedSkill.ConsumedItemCount);
                            }
                            else
                            {
                                logger.LogInformation(
                                    "USE_SKILL job={Job}, skill={SkillId}, dungeon={DungeonId}; no configured item transaction.",
                                    session.Character.Active?.Job,
                                    skillId,
                                    session.Dungeon.CurrentDungeonId);
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
                                && session.Dungeon.CurrentDungeonId != 0
                                && session.Dungeon.Layout is not null)
                            {
                                var targetX = request.Body[2];
                                var targetY = request.Body[3];
                                if (targetX == session.Dungeon.RoomX
                                    && targetY == session.Dungeon.RoomY)
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
                                        session.Dungeon.CurrentDungeonId,
                                        targetX,
                                        targetY);
                                }
                                else if (session.Dungeon.Run.TryMoveRoom(
                                        targetX,
                                        targetY,
                                        out var transition))
                                {
                                    var previousRoomX = session.Dungeon.RoomX;
                                    var previousRoomY = session.Dungeon.RoomY;
                                    await _sessionSenders.SettleDungeonCreatureHungerAsync(connectionContext, 
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
                                        session.Dungeon.GroundItems.DetachAllPassiveObjectItems(
                                            previousRoomX,
                                            previousRoomY);
                                    }

                                    session.Dungeon.RoomX = targetX;
                                    session.Dungeon.RoomY = targetY;
                                    session.Dungeon.ClearEnabled = false;
                                    session.Dungeon.ResultSent = false;
                                    session.Dungeon.CardLayoutSent = false;
                                    session.Dungeon.CardRightsSent = false;
                                    session.Dungeon.CardSelectionDeadline = null;
                                    session.Dungeon.ClearReward = null;
                                    session.Dungeon.FreeCardIndex = null;
                                    session.Dungeon.GoldCardIndex = null;
                                    session.Dungeon.AliveMonsters.Clear();
                                    session.Dungeon.ReportedBossDeaths.Clear();
                                    session.Dungeon.BossCascadeDeaths.Clear();
                                    session.Dungeon.DestroyedPassiveObjects.Clear();
                                    session.Dungeon.Room = transition.Room;
                                    session.Dungeon.AliveMonsters.UnionWith(
                                        session.Dungeon.Room.Monsters.Select(monster =>
                                            monster.UniqueId));
                                    if (session.Dungeon.Run.IsCurrentHellRoomCleared)
                                    {
                                        session.Dungeon.GroundItems.DetachAllPassiveObjectItems(
                                            targetX,
                                            targetY);
                                    }
                                    GameServerPacket nextMap;
                                    if (transition.ReuseClientRoomState)
                                    {
                                        nextMap = GameProtocolEngine.CreateCachedStartMap(
                                            targetX,
                                            targetY,
                                            session.Dungeon.Room.ClientRandomSeed);
                                    }
                                    else
                                    {
                                        var passiveObjectDrops = transition.FirstVisit
                                            ? RegisterPassiveObjectDrops(
                                                session.Dungeon.Room,
                                                session.Dungeon.GroundItems,
                                                session.Character.LocalUserId,
                                                targetX,
                                                targetY)
                                            : GetRegisteredPassiveObjectDrops(
                                                session.Dungeon.GroundItems,
                                                targetX,
                                                targetY);
                                        nextMap = GameProtocolEngine.CreateStartMap(
                                            targetX,
                                            targetY,
                                            session.Dungeon.Room.MapId,
                                            session.Dungeon.Room.Monsters,
                                            passiveObjectDrops,
                                            randomSeed: session.Dungeon.Room.ClientRandomSeed,
                                            roomStateFlag: session.Dungeon.Run.RoomStateFlag,
                                            hellPartyMode: session.Dungeon.Run.CurrentHellPartyMode);
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
                                        session.Dungeon.Room);
                                    logger.LogInformation(
                                        "Dungeon {DungeonId} moved to room ({RoomX},{RoomY}), map {MapId}, monsters={MonsterCount}, champions={ChampionCount}, mapChampionBase={MapChampionBase}, clientSeed={ClientSeed}, firstVisit={FirstVisit}, reuseClientRoomState={ReuseClientRoomState}, hellPartyMode={HellPartyMode}, hellRoomClearedOnExit={HellRoomClearedOnExit}.",
                                        session.Dungeon.CurrentDungeonId,
                                        targetX,
                                        targetY,
                                        session.Dungeon.Room.MapId,
                                        session.Dungeon.Room.Monsters.Length,
                                        session.Dungeon.Room.Monsters.Count(monster =>
                                            monster.Type == GameDungeonMonsterTypes.Champion),
                                        session.Dungeon.Room.BaseChampionCount,
                                        session.Dungeon.Room.ClientRandomSeed,
                                        transition.FirstVisit,
                                        transition.ReuseClientRoomState,
                                        session.Dungeon.Run.CurrentHellPartyMode,
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
                                        session.Dungeon.CurrentDungeonId,
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
                            var bossIds = session.Dungeon.Room is not null
                                ? session.Dungeon.Room.Monsters
                                    .Where(monster => monster.Type
                                        == GameDungeonMonsterTypes.Boss)
                                    .Select(monster => monster.UniqueId)
                                    .ToHashSet()
                                : [];
                            var validBoss = parsed
                                && session.Dungeon.Room is not null
                                && session.Dungeon.Room.MapType == DungeonMapType.Boss
                                && bossDieCheck.ParticipantId == session.Character.LocalUserId
                                && bossIds.Contains(bossDieCheck.BossId);

                            GameServerPacket bossDieReply;
                            if (validBoss)
                            {
                                session.Dungeon.ReportedBossDeaths.Add(bossDieCheck.BossId);
                                var allBossesReported = bossIds.All(
                                    session.Dungeon.ReportedBossDeaths.Contains);
                                if (allBossesReported)
                                {
                                    session.Dungeon.BossCascadeDeaths.UnionWith(
                                        session.Dungeon.AliveMonsters.Where(monsterId =>
                                            !bossIds.Contains(monsterId)));
                                }

                                bossDieReply = GameProtocolEngine.CreateBossDieCheck(
                                    success: true,
                                    allBossesReported,
                                    bossDieCheck.BossId);
                                logger.LogInformation(
                                    "Dungeon {DungeonId} accepted BOSS_DIE_CHECK from participant {ParticipantId} for boss {BossId}; reported={ReportedCount}/{BossCount}, native-all-die={AllBossesReported}, checksum={SkillChecksum:X8}.",
                                    session.Dungeon.CurrentDungeonId,
                                    bossDieCheck.ParticipantId,
                                    bossDieCheck.BossId,
                                    session.Dungeon.ReportedBossDeaths.Count,
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
                                    session.Dungeon.CurrentDungeonId,
                                    session.Dungeon.RoomX,
                                    session.Dungeon.RoomY,
                                    parsed,
                                    parsed ? bossDieCheck.ParticipantId : 0,
                                    session.Character.LocalUserId,
                                    parsed ? bossDieCheck.BossId : 0,
                                    session.Dungeon.Room?.MapType);
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
                                session.Dungeon.CurrentDungeonId,
                                session.Dungeon.RoomX,
                                session.Dungeon.RoomY);
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.DieCharacterCommand)
                        {
                            if (session.Dungeon.CurrentDungeonId == 0
                                || session.Character.Active is null
                                || !session.Dungeon.CharacterAlive)
                            {
                                var error = GameProtocolEngine.CreateCommandError(
                                    request,
                                    errorCode: 19);
                                await error.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, error);
                                logger.LogWarning(
                                    "Rejected DIE_CHARACTER for user {UserId}: dungeon={DungeonId}, hasCharacter={HasCharacter}, alive={Alive}.",
                                    session.Character.LocalUserId,
                                    session.Dungeon.CurrentDungeonId,
                                    session.Character.Active is not null,
                                    session.Dungeon.CharacterAlive);
                                continue;
                            }

                            session.Dungeon.CharacterAlive = false;
                            session.Dungeon.DeathGeneration++;
                            session.Dungeon.DeathTimeout = (
                                session.Dungeon.DeathGeneration,
                                DateTimeOffset.UtcNow.AddSeconds(10));
                            var dieState = GameProtocolEngine.CreateDieState(
                                session.Character.LocalUserId,
                                isAlive: false);
                            await dieState.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, dieState);
                            logger.LogInformation(
                                "User {UserId} entered authoritative dungeon death state in dungeon {DungeonId} room ({RoomX},{RoomY}).",
                                session.Character.LocalUserId,
                                session.Dungeon.CurrentDungeonId,
                                session.Dungeon.RoomX,
                                session.Dungeon.RoomY);
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
                                || session.Dungeon.CurrentDungeonId == 0
                                || session.Character.Active is null)
                            {
                                errorCode = 19;
                            }
                            else if (useCoinCommand.TargetUserId != session.Character.LocalUserId)
                            {
                                errorCode = 21;
                            }
                            else if (session.Dungeon.CharacterAlive)
                            {
                                errorCode = 18;
                            }
                            else if (!CharacterInventoryPlanner.TryConsumeRevivalCoin(
                                         session.Inventory.Main,
                                         out var plannedInventory))
                            {
                                errorCode = 17;
                            }
                            else
                            {
                                var saved = await store.SaveCharacterInventoryAsync(
                                    session.Login.AccountName,
                                    session.Character.Active.Id,
                                    session.Currency.Gold,
                                    plannedInventory.Values,
                                    session.Inventory.Equipped.Values,
                                    serverToken);
                                if (saved is null)
                                {
                                    errorCode = 19;
                                }
                                else
                                {
                                    session.Character.Active = saved;
                                    session.Inventory.Main = plannedInventory;
                                    session.Dungeon.CharacterAlive = true;
                                    session.Dungeon.DeathGeneration++;
                                    session.Dungeon.DeathTimeout = null;

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
                                        session.Character.LocalUserId,
                                        session.Dungeon.CurrentDungeonId,
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
                                session.Character.LocalUserId,
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
                                    if (session.Dungeon.Room is not null
                                        && session.Dungeon.Run.TryMarkPassiveObjectDestroyed(
                                            dieCommand.EntityId,
                                            out var passiveObjectIndex))
                                    {
                                        session.Dungeon.DestroyedPassiveObjects.Add(passiveObjectIndex);
                                        var activatedItemCount = session.Dungeon.GroundItems.ActivatePassiveObject(
                                            passiveObjectIndex,
                                            session.Dungeon.RoomX,
                                            session.Dungeon.RoomY);
                                        session.Dungeon.Room = session.Dungeon.Run.CurrentRoom
                                            ?? session.Dungeon.Room;
                                        logger.LogInformation(
                                            "Passive object code {PassiveObjectCode}, association {PassiveObjectIndex} destroyed in room ({RoomX},{RoomY}); activatedItems={ActivatedItemCount}.",
                                            dieCommand.EntityId,
                                            passiveObjectIndex,
                                            session.Dungeon.RoomX,
                                            session.Dungeon.RoomY,
                                            activatedItemCount);
                                    }
                                    else
                                    {
                                        logger.LogWarning(
                                            "Ignored unknown passive object {PassiveObjectIndex} in room ({RoomX},{RoomY}).",
                                            dieCommand.EntityId,
                                            session.Dungeon.RoomX,
                                            session.Dungeon.RoomY);
                                    }

                                    continue;
                                }

                                var monsterUniqueId = dieCommand.EntityId;
                                var wasHellFiend = session.Dungeon.Run.IsHellFiend(monsterUniqueId);
                                var hellPartyMode = session.Dungeon.Run.CurrentHellPartyMode;
                                var isBossCascadeDeath =
                                    session.Dungeon.BossCascadeDeaths.Remove(monsterUniqueId);
                                var wasAlive = session.Dungeon.AliveMonsters.Remove(monsterUniqueId);
                                var defeatedMonster = wasAlive
                                    && session.Dungeon.Room is not null
                                    ? session.Dungeon.Room.Monsters.FirstOrDefault(monster =>
                                        monster.UniqueId == monsterUniqueId)
                                    : null;
                                if (!wasAlive || defeatedMonster is null)
                                {
                                    logger.LogWarning(
                                        "Ignored stale or unknown monster death {MonsterId} in room ({RoomX},{RoomY}).",
                                        monsterUniqueId,
                                        session.Dungeon.RoomX,
                                        session.Dungeon.RoomY);
                                    continue;
                                }

                                session.Dungeon.Run.MarkMonsterDefeated(monsterUniqueId);

                                var championRewardOrdinal =
                                    GameProtocolEngine.NoChampionRewardOrdinal;

                                switch (defeatedMonster.Type)
                                {
                                    case GameDungeonMonsterTypes.Champion:
                                    case GameDungeonMonsterTypes.SuperChampion:
                                        session.Dungeon.ChampionMonsterKills = Math.Min(
                                            ushort.MaxValue,
                                            session.Dungeon.ChampionMonsterKills + 1);
                                        if (session.Dungeon.ChampionRewardOrdinal
                                            < GameProtocolEngine.MaximumChampionRewardOrdinal)
                                        {
                                            championRewardOrdinal = checked((sbyte)(
                                                ++session.Dungeon.ChampionRewardOrdinal));
                                        }
                                        break;
                                    case GameDungeonMonsterTypes.Boss:
                                        session.Dungeon.BossMonsterKills = Math.Min(
                                            ushort.MaxValue,
                                            session.Dungeon.BossMonsterKills + 1);
                                        break;
                                    default:
                                        session.Dungeon.NormalMonsterKills = Math.Min(
                                            ushort.MaxValue,
                                            session.Dungeon.NormalMonsterKills + 1);
                                        break;
                                }

                                var protocolDrops = new List<GameDungeonDrop>(6);
                                if (!isBossCascadeDeath)
                                {
                                    foreach (var generatedDrop in dropGenerator.Generate(
                                                 defeatedMonster.Level,
                                                 defeatedMonster.Type,
                                                 session.Dungeon.Difficulty))
                                    {
                                        var groundItem = session.Dungeon.GroundItems.Add(
                                            generatedDrop,
                                            session.Character.LocalUserId,
                                            monsterUniqueId,
                                            session.Dungeon.RoomX,
                                            session.Dungeon.RoomY,
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
                                        var groundItem = session.Dungeon.GroundItems.Add(
                                            generatedDrop,
                                            session.Character.LocalUserId,
                                            monsterUniqueId,
                                            session.Dungeon.RoomX,
                                            session.Dungeon.RoomY,
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
                                        var hellLevel = dungeonCatalog.TryGetDefinition(session.Dungeon.CurrentDungeonId, out var hellDefinition)
                                            ? hellDefinition.BasisLevel : defeatedMonster.Level;
                                        var hellHit = hellDropCatalog.TryRollEquipment(
                                            hellLevel,
                                            session.Dungeon.Difficulty,
                                            hellPartyMode,
                                            GameRandomSource.Shared,
                                            out var hellItemId,
                                            options.Drop.RatePercent / 100.0 * options.Drop.EconomicRate,
                                            options.Drop.ForceDrops);
                                        logger.LogInformation(
                                            "Hell drop result for character {CharacterId}, dungeon {DungeonId}, monster {MonsterIndex}/{UniqueId}: hit={Hit}, item={ItemId}.",
                                            session.Character.Active?.Id, session.Dungeon.CurrentDungeonId, defeatedMonster.MonsterIndex,
                                            monsterUniqueId, hellHit, hellItemId);
                                        if (hellHit)
                                        {
                                            var hellGroundItem = session.Dungeon.GroundItems.Add(
                                                new DungeonGeneratedDrop(
                                                    IsGold: false,
                                                    hellItemId,
                                                    CountOrValue: 1),
                                                session.Character.LocalUserId,
                                                monsterUniqueId,
                                                session.Dungeon.RoomX,
                                                session.Dungeon.RoomY,
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

                                if (!isBossCascadeDeath && !session.Dungeon.TutorialActive)
                                {
                                    await GrantMonsterQuestItemsAsync(
                                        defeatedMonster.MonsterIndex);
                                }

                                if (!isBossCascadeDeath && session.Character.Active is not null)
                                {
                                    var gainedExperience =
                                        dungeonCatalog.TryGetDefinition(
                                            session.Dungeon.CurrentDungeonId,
                                            out var experienceDungeon)
                                            ? dungeonExperienceCatalog.CalculateMonsterExperience(
                                                session.Character.Active.Level,
                                                defeatedMonster.Level,
                                                defeatedMonster.Type,
                                                defeatedMonster.MonsterIndex,
                                                experienceDungeon,
                                                session.Dungeon.Difficulty,
                                                partyMemberCount: 1)
                                            : 0;
                                    var progression = experienceCatalog.Grant(
                                        session.Character.Active,
                                        gainedExperience);
                                    if (progression.GrantedExperience > 0)
                                    {
                                        var grantedSkillPoints = ApplyLevelUpSkillPoints(progression);
                                        session.Character.Active = session.Character.Active with
                                        {
                                            Level = progression.Level,
                                            Experience = progression.Experience,
                                            Sp = session.Skill.CurrentSp
                                        };
                                        var savedProgression =
                                            await store.SaveCharacterProgressionAsync(
                                                session.Login.AccountName,
                                                session.Character.Active.Id,
                                                session.Character.Active.Level,
                                                session.Character.Active.Experience,
                                                session.Skill.CurrentSp,
                                                serverToken);
                                        if (savedProgression is null)
                                        {
                                            throw new InvalidOperationException(
                                                $"Could not persist monster experience for character {session.Character.Active.Id}.");
                                        }

                                        session.Character.Active = savedProgression;
                                        session.Dungeon.MonsterExperienceGained = (uint)Math.Min(
                                            uint.MaxValue,
                                            (ulong)session.Dungeon.MonsterExperienceGained
                                            + progression.GrantedExperience);
                                        session.Dungeon.LeveledUp |= progression.LeveledUp;

                                        var experienceGained =
                                            GameProtocolEngine.CreateExperienceGained(
                                                checked((byte)Math.Clamp(
                                                    session.Character.Active.Level,
                                                    1,
                                                    byte.MaxValue)),
                                                session.Character.Active.Experience,
                                                extraExperienceA: 0,
                                                extraExperienceB: 0,
                                                skillPoints: checked((ushort)Math.Min(
                                                    session.Skill.CurrentSp,
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
                                            session.Character.Active.Id,
                                            progression.GrantedExperience,
                                            session.Character.Active.Experience,
                                            session.Character.Active.Level);
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
                                        session.Dungeon.RoomX,
                                        session.Dungeon.RoomY);
                                }

                                var clearDecision = DungeonRoomClearRules.Evaluate(
                                    session.Dungeon.Room!,
                                    defeatedMonster,
                                    session.Dungeon.AliveMonsters);
                                if (clearDecision.ForcedMonsterIds.Length > 0)
                                {
                                    logger.LogWarning(
                                        "Dungeon {DungeonId} received the final boss MONSTER_DIE with {MonsterCount} monsters still alive in room ({RoomX},{RoomY}); waiting for the native BOSS_DIE_CHECK/ALLDIE_MONSTER cascade instead of injecting a solo UDP peer packet.",
                                        session.Dungeon.CurrentDungeonId,
                                        clearDecision.ForcedMonsterIds.Length,
                                        session.Dungeon.RoomX,
                                        session.Dungeon.RoomY);
                                }

                                if (clearDecision.ShouldEnableClear && !session.Dungeon.ClearEnabled)
                                {
                                    session.Dungeon.ClearEnabled = true;
                                    if (!session.Dungeon.TutorialActive)
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
                                        session.Dungeon.CurrentDungeonId,
                                        session.Dungeon.RoomX,
                                        session.Dungeon.RoomY,
                                        clearDecision.Reason);
                                }

                                if (!session.Dungeon.TutorialActive)
                                {
                                    await CompleteClearQuestTriggersAsync(
                                        $"map {session.Dungeon.Room!.MapId} monster death");
                                }
                            }
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.GetItemCommand)
                        {
                            if (request.Body.Length < 4
                                || session.Dungeon.CurrentDungeonId == 0
                                || session.Character.Active is null)
                            {
                                continue;
                            }

                            var groundId = BinaryPrimitives.ReadUInt16LittleEndian(
                                request.Body.AsSpan(2, sizeof(ushort)));
                            if (!session.Dungeon.GroundItems.TryGet(
                                    groundId,
                                    session.Dungeon.RoomX,
                                    session.Dungeon.RoomY,
                                    out var groundItem)
                                || groundItem.OwnerUserId != session.Character.LocalUserId)
                            {
                                logger.LogWarning(
                                    "Ignored invalid or duplicate pickup of ground item {GroundId} for user {UserId}.",
                                    groundId,
                                    session.Character.LocalUserId);
                                continue;
                            }

                            if (groundItem.IsGold)
                            {
                                int updatedGold;
                                try
                                {
                                    updatedGold = checked(
                                        session.Currency.Gold + checked((int)groundItem.CountOrValue));
                                }
                                catch (OverflowException)
                                {
                                    logger.LogWarning(
                                        "Gold pickup {GroundId} would overflow the character balance.",
                                        groundId);
                                    continue;
                                }

                                var saved = await store.SaveCharacterInventoryAsync(
                                    session.Login.AccountName,
                                    session.Character.Active.Id,
                                    updatedGold,
                                    session.Inventory.Main.Values,
                                    session.Inventory.Equipped.Values,
                                    serverToken);
                                if (saved is null)
                                {
                                    logger.LogWarning(
                                        "Gold pickup {GroundId} was not persisted; the ground item was retained.",
                                        groundId);
                                    continue;
                                }

                                session.Character.Active = saved;
                                session.Currency.Gold = updatedGold;
                                session.Dungeon.GroundItems.Remove(groundId);
                                var pickup = GameProtocolEngine.CreateGoldItemPickup(
                                    groundId,
                                    session.Character.LocalUserId,
                                    groundItem.CountOrValue);
                                await pickup.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, pickup);

                                var refresh = CreateMainInventoryPacket(
                                    session.Currency.Gold,
                                    session.Currency.VictoryPoints,
                                    session.Inventory.Main.Values);
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
                                    session.Inventory.WarehouseCapacity,
                                    groundUpgradeTarget);
                                var savedUpgrade = await store.SaveCharacterInventoryAsync(
                                    session.Login.AccountName,
                                    session.Character.Active.Id,
                                    session.Currency.Gold,
                                    session.Inventory.Main.Values,
                                    session.Inventory.Equipped.Values,
                                    serverToken,
                                    warehouseCapacity: plannedCapacity,
                                    expectedWarehouseCapacity: session.Inventory.WarehouseCapacity);
                                if (savedUpgrade is null)
                                {
                                    logger.LogWarning(
                                        "Warehouse-upgrade pickup {GroundId} was not persisted; the ground item was retained.",
                                        groundId);
                                    continue;
                                }

                                _ = AdoptSavedWarehouseUpgradeState(savedUpgrade);
                                session.Dungeon.GroundItems.Remove(groundId);
                                var upgradePickup =
                                    GameProtocolEngine.CreateInventoryItemPickup(
                                        groundId,
                                        session.Character.LocalUserId,
                                        ushort.MaxValue);
                                await upgradePickup.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, upgradePickup);

                                var inventoryRefresh = CreateMainInventoryPacket(
                                    session.Currency.Gold,
                                    session.Currency.VictoryPoints,
                                    session.Inventory.Main.Values);
                                await inventoryRefresh.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, inventoryRefresh);
                                await SendWarehouseStateAsync();
                                continue;
                            }

                            if (!CharacterInventoryPlanner.CanCarryAdditionalItem(
                                    session.Inventory.Main.Values,
                                    session.Inventory.Equipped.Values,
                                    session.Inventory.Creature.Values,
                                    groundItem.ItemId,
                                    groundItem.CountOrValue,
                                    characterStatCatalog.Get(session.Character.Active).InventoryLimit,
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
                                        session.Inventory.Creature,
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
                                        session.Login.AccountName,
                                        session.Character.Active.Id,
                                        session.Currency.Gold,
                                        session.Inventory.Main.Values,
                                        session.Inventory.Equipped.Values,
                                        session.Inventory.Warehouse.Values,
                                        serverToken,
                                        avatarInventory: session.Inventory.Avatar.Values,
                                        creatureInventory:
                                            creaturePickupPlan.Inventory.Values);
                                if (savedCreaturePickup is null)
                                {
                                    logger.LogWarning(
                                        "Creature item pickup {GroundId} was not persisted; the ground item was retained.",
                                        groundId);
                                    continue;
                                }

                                session.Character.Active = savedCreaturePickup;
                                session.Inventory.Creature = creaturePickupPlan.Inventory;
                                session.Dungeon.GroundItems.Remove(groundId);
                                var creaturePickup =
                                    GameProtocolEngine.CreateInventoryItemPickup(
                                        groundId,
                                        session.Character.LocalUserId,
                                        creaturePickupPlan.DestinationSlot);
                                await creaturePickup.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, creaturePickup);

                                foreach (var creatureRefresh in
                                         CreateCreatureInventoryPackets(
                                             session.Inventory.Creature.Values))
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
                                        session.Inventory.Avatar,
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
                                        session.Login.AccountName,
                                        session.Character.Active.Id,
                                        session.Currency.Gold,
                                        session.Inventory.Main.Values,
                                        session.Inventory.Equipped.Values,
                                        session.Inventory.Warehouse.Values,
                                        serverToken,
                                        avatarInventory: avatarPickupPlan.Inventory.Values);
                                if (savedAvatarPickup is null)
                                {
                                    logger.LogWarning(
                                        "Avatar item pickup {GroundId} was not persisted; the ground item was retained.",
                                        groundId);
                                    continue;
                                }

                                session.Character.Active = savedAvatarPickup;
                                session.Inventory.Avatar = avatarPickupPlan.Inventory;
                                session.Dungeon.GroundItems.Remove(groundId);
                                var avatarPickup =
                                    GameProtocolEngine.CreateInventoryItemPickup(
                                        groundId,
                                        session.Character.LocalUserId,
                                        avatarPickupPlan.DestinationSlot);
                                await avatarPickup.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, avatarPickup);

                                var avatarRefresh = CreateAvatarInventoryPacket(
                                    session.Inventory.Avatar.Values);
                                await avatarRefresh.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, avatarRefresh);
                                continue;
                            }

                            if (!DungeonPickupPlanner.TryPlanItem(
                                    session.Inventory.Main,
                                    groundItem,
                                    itemCatalog,
                                    GetInventoryWeightLimit(session.Character.Active),
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
                                session.Login.AccountName,
                                session.Character.Active.Id,
                                session.Currency.Gold,
                                pickupPlan.Inventory.Values,
                                session.Inventory.Equipped.Values,
                                serverToken);
                            if (savedInventory is null)
                            {
                                logger.LogWarning(
                                    "Item pickup {GroundId} was not persisted; the ground item was retained.",
                                    groundId);
                                continue;
                            }

                            session.Character.Active = savedInventory;
                            session.Inventory.Main = pickupPlan.Inventory;
                            session.Dungeon.GroundItems.Remove(groundId);
                            var itemPickup = GameProtocolEngine.CreateInventoryItemPickup(
                                groundId,
                                session.Character.LocalUserId,
                                pickupPlan.DestinationSlot);
                            await itemPickup.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, itemPickup);

                            var itemRefresh = CreateMainInventoryPacket(
                                session.Currency.Gold,
                                session.Currency.VictoryPoints,
                                session.Inventory.Main.Values,
                                pickupPlan.DestinationSlot);
                            await itemRefresh.WriteAsync(stream, serverToken);
                            Interlocked.Increment(ref runtimeSession.SentPackets);
                            LogPacket("TX", runtimeSession, itemRefresh);
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.SetPlayResultCommand)
                        {
                            if (session.Dungeon.TutorialActive)
                            {
                                logger.LogInformation(
                                    "Ignored normal settlement command for tutorial dungeon {DungeonId}; waiting for tutorial module {ModuleId}.",
                                    session.Dungeon.CurrentDungeonId,
                                    GameProtocolEngine.TutorialCompletionModule);
                                continue;
                            }

                            if (session.Dungeon.ClearEnabled && !session.Dungeon.ResultSent)
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
                                    session.Character.LocalUserId,
                                    clientResultValue);
                                await playResult.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, playResult);

                                if (await _sessionSenders.SettleDungeonCreatureHungerAsync(connectionContext, 
                                        notifyClient: true,
                                        serverToken))
                                {
                                    await GrantDungeonCreatureExperienceAsync();
                                }
                                else
                                {
                                    session.Dungeon.ConsumedFatigue = 0;
                                }
                                session.Dungeon.CreatureStomachCheckpointSeconds = null;

                                var hasDungeonDefinition =
                                    dungeonCatalog.TryGetDefinition(
                                        session.Dungeon.CurrentDungeonId,
                                        out var settledDungeon);
                                var clearGrantedSkillPoints = 0;
                                if (session.Dungeon.ClearExperience is null)
                                {
                                    // Clear EXP is a separate reason from the
                                    // per-monster grants accumulated above.
                                    var calculatedClearExperience =
                                        session.Character.Active is not null
                                        && hasDungeonDefinition
                                            ? dungeonExperienceCatalog.CalculateClearExperience(
                                                session.Character.Active.Level,
                                                settledDungeon,
                                                session.Dungeon.Difficulty,
                                                resultCode,
                                                session.Dungeon.NormalMonsterKills
                                                + session.Dungeon.ChampionMonsterKills
                                                + session.Dungeon.BossMonsterKills,
                                                partyMemberCount: 1,
                                                bonuses: new DungeonClearBonusContext(
                                                    HasAvatar: session.Inventory.Avatar.Values.Any(item =>
                                                        itemCatalog.TryGetDefinition(item.ItemId, out var avatar)
                                                        && CharacterAvatarInventoryLayout.IsCompatibleWornSlot(avatar, item.Slot)),
                                                    HasCreature: session.Inventory.Creature.TryGetValue(
                                                        CharacterCreatureInventoryLayout.EquippedCreatureSlot,
                                                        out var clearCreature) && clearCreature.CountOrValue != 0,
                                                    HasBlackDiamond: HasActivePremiumService(GameProtocolEngine.BlackDiamondServiceType),
                                                    MentorBonusRate: DungeonClearBonusContext.GetMentorBonusRate(
                                                        session.Character.Active, characterSessions.IsOnline),
                                                    EventBonusRate: dungeonExperienceCatalog.GetEventBonusRate(DateTimeOffset.UtcNow),
                                                    ChannelBonusRate: dungeonExperienceCatalog.GetChannelBonusRate(session.Dungeon.CurrentDungeonId)))
                                            : GameDungeonClearExperienceBreakdown.Empty;

                                    if (session.Character.Active is not null
                                        && (session.Dungeon.MonsterExperienceGained > 0
                                            || calculatedClearExperience.TotalExperience > 0))
                                    {
                                        var clearProgression = experienceCatalog.Grant(
                                            session.Character.Active,
                                            calculatedClearExperience.TotalExperience);
                                        clearGrantedSkillPoints =
                                            spCatalog.CalculateLevelUpReward(
                                                clearProgression.PreviousLevel,
                                                clearProgression.Level);
                                        var settledSp = checked(
                                            session.Skill.CurrentSp + clearGrantedSkillPoints);
                                        var savedProgression =
                                            await store.SaveCharacterProgressionAsync(
                                                session.Login.AccountName,
                                                session.Character.Active.Id,
                                                clearProgression.Level,
                                                clearProgression.Experience,
                                                settledSp,
                                                serverToken);
                                        if (savedProgression is null)
                                        {
                                            throw new InvalidOperationException(
                                                $"Could not persist dungeon settlement for character {session.Character.Active.Id}.");
                                        }

                                        session.Skill.CurrentSp = settledSp;
                                        session.Character.Active = savedProgression;
                                        session.Dungeon.LeveledUp |= clearProgression.LeveledUp;
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
                                    session.Dungeon.ClearExperience = calculatedClearExperience;
                                }

                                var settledClearExperience =
                                    session.Dungeon.ClearExperience
                                    ?? GameDungeonClearExperienceBreakdown.Empty;
                                var generationLevel = hasDungeonDefinition
                                    ? settledDungeon.BasisLevel
                                    : checked((byte)Math.Clamp(
                                        session.Character.Active?.Level ?? 1,
                                        1,
                                        byte.MaxValue));
                                session.Dungeon.ClearReward ??= clearRewardGenerator.Generate(
                                    generationLevel,
                                    session.Dungeon.Difficulty,
                                    HasActivePremiumService(
                                        GameProtocolEngine.BlackDiamondServiceType),
                                    !hasDungeonDefinition || settledDungeon.GoldCardUse,
                                    partyMemberCount: 1,
                                    normalMonsterKills: session.Dungeon.NormalMonsterKills,
                                    championMonsterKills: session.Dungeon.ChampionMonsterKills,
                                    bossMonsterKills: session.Dungeon.BossMonsterKills);
                                var clearReward =
                                    GameProtocolEngine.CreateClearDungeonReward(
                                        session.Character.LocalUserId,
                                        session.Dungeon.ClearReward.GoldCardCost,
                                        session.Dungeon.ClearReward.FreeGoldAmount,
                                        session.Dungeon.ClearReward.FreeItemId,
                                        goldCardEnabled: session.Dungeon.ClearReward.GoldCardEnabled,
                                        goldItemId: session.Dungeon.ClearReward.GoldItemId,
                                        premiumCardEnabled: HasActivePremiumService(
                                            GameProtocolEngine.BlackDiamondServiceType),
                                        premiumItemId: session.Dungeon.ClearReward.PremiumItemId,
                                        experience: settledClearExperience);
                                await clearReward.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, clearReward);
                                logger.LogInformation(
                                    "Dungeon {DungeonId} clear-card rewards prepared for user {UserId}: normalKills={NormalKills}, championKills={ChampionKills}, bossKills={BossKills}, championAnimationCount={ChampionAnimationCount}, freeGold={FreeGold}, freeItem={FreeItemId}, premiumItem={PremiumItemId}, goldItem={GoldItemId}, partyMemberCount={PartyMemberCount}.",
                                    session.Dungeon.CurrentDungeonId,
                                    session.Character.LocalUserId,
                                    session.Dungeon.NormalMonsterKills,
                                    session.Dungeon.ChampionMonsterKills,
                                    session.Dungeon.BossMonsterKills,
                                    session.Dungeon.ChampionRewardOrdinal,
                                    session.Dungeon.ClearReward.FreeGoldAmount,
                                    session.Dungeon.ClearReward.FreeItemId,
                                    session.Dungeon.ClearReward.PremiumItemId,
                                    session.Dungeon.ClearReward.GoldItemId,
                                    1);
                                session.Dungeon.ResultSent = true;
                                if (session.Dungeon.MonsterExperienceGained > 0
                                    || settledClearExperience.TotalExperience > 0)
                                {
                                    await SendCurrentEquipmentStateAsync();
                                    if (session.Dungeon.LeveledUp)
                                    {
                                        await SendCurrentSkillInfoAsync();
                                        await ValidateAndCorrectCharacterStatsAsync(
                                            "dungeon clear experience level-up");
                                        await SendAcceptableQuestListAsync();
                                    }
                                }

                                if (hasClearScore
                                    && session.Character.Active is not null
                                    && DungeonDifficultyProgression.TryGetNextDifficulty(
                                        session.Dungeon.Difficulty,
                                        resultCode,
                                        out var nextDifficulty))
                                {
                                    var previousMaximum =
                                        DungeonDifficultyProgression.GetMaximumDifficulty(
                                            session.Character.Active.DungeonProgress,
                                            session.Dungeon.CurrentDungeonId);
                                    if (previousMaximum < nextDifficulty)
                                    {
                                        var savedDifficulty =
                                            await store.UnlockNextDungeonDifficultyAsync(
                                                session.Login.AccountName,
                                                session.Character.Active.Id,
                                                session.Dungeon.CurrentDungeonId,
                                                session.Dungeon.Difficulty,
                                                serverToken);
                                        if (savedDifficulty is not null)
                                        {
                                            session.Character.Active = savedDifficulty;
                                            // DF2008 uses an incremental NOTI 5 while the client is
                                            // still in dungeon state. The client compares this one
                                            // record with its cached permission, opens the difficulty
                                            // unlock window, and then updates the cache.
                                            var difficultyUnlocked =
                                                GameProtocolEngine.CreateDungeonPermissions(
                                                    [new GameDungeonPermissionEntry(
                                                        session.Dungeon.CurrentDungeonId,
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
                                                session.Dungeon.CurrentDungeonId,
                                                nextDifficulty,
                                                session.Dungeon.Difficulty,
                                                resultCode);
                                        }
                                        else
                                        {
                                            logger.LogWarning(
                                                "Could not persist dungeon {DungeonId} difficulty {Difficulty} for character {CharacterId}.",
                                                session.Dungeon.CurrentDungeonId,
                                                nextDifficulty,
                                                session.Character.Active.Id);
                                        }
                                    }
                                }

                                if (!hasClearScore)
                                {
                                    logger.LogWarning(
                                        "Dungeon {DungeonId} settlement did not contain the 24-byte score layout; higher difficulty rank gates used result code zero.",
                                        session.Dungeon.CurrentDungeonId);
                                }
                                else if (!clearScore.IsWireResultCodeConsistent)
                                {
                                    logger.LogWarning(
                                        "Dungeon {DungeonId} ignored inconsistent client result code {WireResultCode}; calculated {CalculatedResultCode} from score {CalculatedScore}.",
                                        session.Dungeon.CurrentDungeonId,
                                        clearScore.WireResultCode,
                                        clearScore.CalculatedResultCode,
                                        clearScore.CalculatedScore);
                                }

                                logger.LogInformation(
                                    "Dungeon {DungeonId} settlement sent for user {UserId}, result {ResultValue}, resultCode={ResultCode}, goldCost={GoldCost}, monsterExp={MonsterExperience}, clearExp={ClearExperience}, base={BaseExperience}, rank={RankBonus}, party={PartyBonus}, mentor={MentorBonus}, avatar={AvatarBonus}, creature={CreatureBonus}, event={EventBonus}, blackDiamond={BlackDiamondBonus}, channel={ChannelBonus}",
                                    session.Dungeon.CurrentDungeonId,
                                    session.Character.LocalUserId,
                                    clientResultValue,
                                    resultCode,
                                    session.Dungeon.ClearReward.GoldCardCost,
                                    session.Dungeon.MonsterExperienceGained,
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
                            if (session.Dungeon.TutorialActive)
                            {
                                if (session.Dungeon.ClearEnabled && session.Dungeon.AliveMonsters.Count == 0)
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

                            if (session.Dungeon.ResultSent && !session.Dungeon.CardLayoutSent)
                            {
                                session.Dungeon.CardLayoutSent = true;
                                var scoreScrollReply =
                                    GameProtocolEngine.CreateScoreScrollStateReply();
                                await scoreScrollReply.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, scoreScrollReply);
                                logger.LogInformation(
                                    "Dungeon {DungeonId} score scroll acknowledged for user {UserId}",
                                    session.Dungeon.CurrentDungeonId,
                                    session.Character.LocalUserId);
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

                            if (session.Dungeon.TutorialActive
                                && session.Dungeon.ClearEnabled
                                && session.Dungeon.AliveMonsters.Count == 0
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
                            if (session.Dungeon.CardLayoutSent && !session.Dungeon.CardRightsSent)
                            {
                                session.Dungeon.CardRightsSent = true;
                                session.Dungeon.CardSelectionDeadline =
                                    DateTimeOffset.UtcNow
                                    + ClearRewardCardSelectionTimeout;
                                var cardRights =
                                    GameProtocolEngine.CreateCardSelectRightState();
                                await cardRights.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, cardRights);
                                logger.LogInformation(
                                    "Dungeon {DungeonId} card selection rights sent for user {UserId}; automatic free-card selection in {TimeoutSeconds}s",
                                    session.Dungeon.CurrentDungeonId,
                                    session.Character.LocalUserId,
                                    ClearRewardCardSelectionTimeout.TotalSeconds);
                            }
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.SelectCardCommand)
                        {
                            if (session.Dungeon.CardRightsSent && session.Dungeon.ClearReward is not null)
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
                                        session.Character.LocalUserId);
                                }
                                else if (cardType == 0 && !session.Dungeon.FreeCardIndex.HasValue)
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
                                    && !session.Dungeon.GoldCardIndex.HasValue
                                    && session.Dungeon.ClearReward.GoldCardEnabled
                                    && session.Dungeon.ClearReward.GoldCardCost <= int.MaxValue
                                    && session.Currency.Gold >= (int)session.Dungeon.ClearReward.GoldCardCost)
                                {
                                    var goldGrant = await SaveClearRewardEquipmentAsync(
                                        session.Currency.Gold - (int)session.Dungeon.ClearReward.GoldCardCost,
                                        [session.Dungeon.ClearReward.GoldItemId]);
                                    if (goldGrant.Saved)
                                    {
                                        session.Dungeon.GoldCardIndex = selectedCardIndex;
                                        selectionAccepted = true;
                                        selectedRewardItemId = goldGrant.Granted[0]
                                            ? session.Dungeon.ClearReward.GoldItemId
                                            : null;
                                        inventoryChanged = true;
                                    }
                                }
                                else if (cardType == 0
                                    && session.Dungeon.FreeCardIndex == selectedCardIndex)
                                {
                                    selectionAccepted = true;
                                    selectedRewardItemId = session.Dungeon.ClearReward.FreeItemId
                                        ?? session.Dungeon.ClearReward.PremiumItemId;
                                }
                                else if (cardType == 1
                                    && session.Dungeon.GoldCardIndex == selectedCardIndex)
                                {
                                    selectionAccepted = true;
                                    selectedRewardItemId = session.Dungeon.ClearReward.GoldItemId;
                                }
                                else if (cardType is not 0 and not 1)
                                {
                                    logger.LogWarning(
                                        "Ignored invalid dungeon card type {CardType} from user {UserId}",
                                        cardType,
                                        session.Character.LocalUserId);
                                }

                                // CMD 74 supports only type 0 (free) and type 1
                                // (paid). A black-diamond character's second
                                // free reward is NOTI 35 group 3 at the same
                                // selected column and is settled with type 0.
                                await SendCardSelectionSnapshotAsync(inventoryChanged);
                                logger.LogInformation(
                                    "Dungeon {DungeonId} card type {CardType}, index {CardIndex} processed for user {UserId}; accepted={Accepted}, reward={RewardItemId}",
                                    session.Dungeon.CurrentDungeonId,
                                    cardType,
                                    selectedCardIndex,
                                    session.Character.LocalUserId,
                                    selectionAccepted,
                                    selectedRewardItemId);
                            }
                            else
                            {
                                logger.LogWarning(
                                    "Ignored dungeon card selection before card rights for user {UserId}",
                                    session.Character.LocalUserId);
                            }
                            continue;
                        }

                        if (request.Type == GameProtocolEngine.CommandPacketType
                            && request.ProtocolId == GameProtocolEngine.GiveupGameCommand)
                        {
                            if (session.Character.Active is { TutorialStarted: false })
                            {
                                var tutorialError = GameProtocolEngine.CreateCommandError(
                                    request,
                                    errorCode: 4);
                                await tutorialError.WriteAsync(stream, serverToken);
                                Interlocked.Increment(ref runtimeSession.SentPackets);
                                LogPacket("TX", runtimeSession, tutorialError);
                                logger.LogInformation(
                                    "Rejected tutorial give-up for character {CharacterId}.",
                                    session.Character.Active.Id);
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
                            if (session.Dungeon.TutorialActive)
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
                                && session.Dungeon.CardRightsSent
                                && session.Dungeon.ClearReward is not null
                                && !session.Dungeon.FreeCardIndex.HasValue)
                            {
                                if (!await TryAutomaticallySelectFreeCardAsync(
                                    "eplp-fallback"))
                                {
                                    logger.LogWarning(
                                        "Deferred dungeon {DungeonId} EPLP completion because automatic free-card settlement failed for user {UserId}",
                                        session.Dungeon.CurrentDungeonId,
                                        session.Character.LocalUserId);
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
                                var freeCardWasSelected = session.Dungeon.FreeCardIndex.HasValue;
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
                                forceDeathNotification: session.Dungeon.CurrentDungeonId != 0);
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

                    if (await TryDispatchGameCommandAsync(request))
                    {
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
                // Complete the outbound channel and drain the pump before the
                // stream is disposed, so no queued frame tears (07 §4.4).
                await outboundQueue.DisposeAsync();
                if (session.Character.Active is not null)
                {
                    try
                    {
                        if (session.Dungeon.CurrentDungeonId != 0
                            && session.Dungeon.CreatureStomachCheckpointSeconds
                                is long finalCheckpoint)
                        {
                            var finalNow = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                            var finalElapsedSeconds = checked((uint)Math.Min(
                                uint.MaxValue,
                                Math.Max(0L, finalNow - finalCheckpoint)));
                            if (finalElapsedSeconds != 0
                                && CreatureHungerPlanner.TrySettle(
                                    session.Inventory.Creature,
                                    finalElapsedSeconds,
                                    itemCatalog,
                                    out var finalHungerPlan)
                                && finalHungerPlan.Changed)
                            {
                                await store.SaveCharacterItemSpacesAsync(
                                    session.Login.AccountName,
                                    session.Character.Active.Id,
                                    session.Currency.Gold,
                                    session.Inventory.Main.Values,
                                    session.Inventory.Equipped.Values,
                                    session.Inventory.Warehouse.Values,
                                    CancellationToken.None,
                                    avatarInventory: session.Inventory.Avatar.Values,
                                    creatureInventory:
                                        finalHungerPlan.Inventory.Values);
                            }
                        }

                        var saveTownId = session.Dungeon.CurrentDungeonId == 0
                            ? session.Character.TownId
                            : session.Dungeon.ReturnTownId;
                        var saveAreaId = session.Dungeon.CurrentDungeonId == 0
                            ? session.Character.AreaId
                            : session.Dungeon.ReturnAreaId;
                        var saveX = session.Dungeon.CurrentDungeonId == 0 ? session.Character.X : session.Dungeon.ReturnX;
                        var saveY = session.Dungeon.CurrentDungeonId == 0 ? session.Character.Y : session.Dungeon.ReturnY;
                        var saveDirection = session.Dungeon.CurrentDungeonId == 0
                            ? session.Character.Direction
                            : session.Dungeon.ReturnDirection;
                        await store.SaveCharacterLocationAsync(
                            session.Login.AccountName,
                            session.Character.Active.Id,
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
                            session.Character.Active.Id);
                    }
                }

                partyCoordinator.OnSessionClosed(runtimeSession.Id);
                characterSessions.Release(session.Character.Lease);
                session.Character.Lease = null;
                characterSessions.DetachPacketChannel(runtimeSession.Id);
                gameplayDatagram.UnregisterChannelSession(runtimeSession.Id);
                runtime.Remove(runtimeSession.Id);
                logger.LogInformation("Entrance client {SessionId} disconnected.", runtimeSession.Id);
            }
        }
    }

    private static int ToIncreaseStatusWireAmount(IncreaseStatusEffect effect) =>
        effect.Type is IncreaseStatusType.SkillPoints or IncreaseStatusType.Experience
            ? effect.Amount
            : checked(effect.Amount * 10);

    private static CreateCharacterMailRequest[] CreateOverflowMailRequests(
        IEnumerable<CharacterMailAttachmentRecord> items,
        string source) =>
        CharacterPacketBuilders.CreateOverflowMailRequests(items, source);

    private GameServerPacket CreateMainInventoryPacket(
        int gold,
        uint victoryPoints,
        IEnumerable<CharacterItemRecord> inventory,
        ushort? preferredLastSlot = null) =>
        _characterPacketBuilders.CreateMainInventoryPacket(gold, victoryPoints, inventory, preferredLastSlot);

    private GameServerPacket CreateWarehousePacket(
        IEnumerable<CharacterItemRecord> warehouse,
        ushort warehouseCapacity) =>
        _characterPacketBuilders.CreateWarehousePacket(warehouse, warehouseCapacity);

    private GameServerPacket CreateAvatarInventoryPacket(
        IEnumerable<CharacterItemRecord> avatarInventory) =>
        _characterPacketBuilders.CreateAvatarInventoryPacket(avatarInventory);

    private IEnumerable<GameServerPacket> CreateCreatureInventoryPackets(
        IEnumerable<CharacterItemRecord> creatureInventory) =>
        _characterPacketBuilders.CreateCreatureInventoryPackets(creatureInventory);

    private GameServerPacket CreateCreatureInventoryItemListPacket(
        IEnumerable<CharacterItemRecord> creatureInventory) =>
        _characterPacketBuilders.CreateCreatureInventoryItemListPacket(creatureInventory);

    private byte[] EncodeCreatureName(string? name, ushort itemId) =>
        _characterPacketBuilders.EncodeCreatureName(name, itemId);

    private GameInventoryEntry[] CreateWornEquipmentSnapshot(
        IEnumerable<CharacterItemRecord> equipment,
        IReadOnlyDictionary<ushort, CharacterItemRecord> creatureInventory) =>
        _characterPacketBuilders.CreateWornEquipmentSnapshot(equipment, creatureInventory);

    private GameServerPacket CreateTownAppearancePacket(
        CharacterRecord character,
        ushort userId,
        IEnumerable<CharacterItemRecord> equipment,
        IEnumerable<CharacterItemRecord> creatureInventory,
        bool hasBlackDiamond) =>
        _characterPacketBuilders.CreateTownAppearancePacket(
            character,
            userId,
            equipment,
            creatureInventory,
            hasBlackDiamond);

    private static bool TryGetInventorySpace(
        byte listType,
        Dictionary<ushort, CharacterItemRecord> mainInventory,
        Dictionary<ushort, CharacterItemRecord> avatarInventory,
        Dictionary<ushort, CharacterItemRecord> equippedInventory,
        Dictionary<ushort, CharacterItemRecord> warehouseInventory,
        out Dictionary<ushort, CharacterItemRecord> space) =>
        ChannelSessionSenders.TryGetInventorySpace(
            listType, mainInventory, avatarInventory, equippedInventory, warehouseInventory, out space);

    private static bool IsInventorySlotValid(
        byte listType,
        ushort slot,
        ushort warehouseCapacity) =>
        ChannelSessionSenders.IsInventorySlotValid(listType, slot, warehouseCapacity);

    private bool IsCompatibleMainInventorySlot(
        CharacterItemRecord item,
        ushort slot) =>
        _sessionSenders.IsCompatibleMainInventorySlot(item, slot);

    private bool IsCompatibleInventorySlot(
        byte listType,
        CharacterItemRecord item,
        ushort slot,
        ushort warehouseCapacity) =>
        _sessionSenders.IsCompatibleInventorySlot(listType, item, slot, warehouseCapacity);

    private ushort FindFreeCompatibleMainInventorySlot(
        IReadOnlyDictionary<ushort, CharacterItemRecord> inventory,
        CharacterItemRecord item) =>
        _sessionSenders.FindFreeCompatibleMainInventorySlot(inventory, item);

    private ushort FindPartialStackSlot(
        IReadOnlyDictionary<ushort, CharacterItemRecord> inventory,
        CharacterItemRecord item,
        byte listType,
        ushort? excludedSlot) =>
        _sessionSenders.FindPartialStackSlot(inventory, item, listType, excludedSlot);

    private GameInventoryEntry ToGameInventoryEntry(CharacterItemRecord item) =>
        _characterPacketBuilders.ToGameInventoryEntry(item);

    private GameMailEntry ToGameMailEntry(CharacterMailRecord mail) =>
        _characterPacketBuilders.ToGameMailEntry(mail);

    private static byte GetEquippedCreatureLevel(
        IReadOnlyDictionary<ushort, CharacterItemRecord> creatureInventory) =>
        CharacterPacketBuilders.GetEquippedCreatureLevel(creatureInventory);

    internal static bool ShouldUseIncrementalMainMoveRefresh(
        byte sourceListType,
        ushort sourceSlot,
        byte destinationListType,
        ushort destinationSlot) =>
        sourceListType == 0
        && destinationListType == 0
        && (CharacterInventoryLayout.IsQuickSlot(sourceSlot)
            || CharacterInventoryLayout.IsQuickSlot(destinationSlot));

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

    private bool IsEquipmentItem(ushort itemId) => itemCatalog.IsEquipment(itemId);


    private static bool TryReadLengthPrefixedBytes(
        byte[] source,
        int offset,
        out byte[] value,
        out int nextOffset) =>
        ClientBodyParsers.TryReadLengthPrefixedBytes(source, offset, out value, out nextOffset);

    private static Encoding CreateClientEncoding()
    {
        return PvfEncodings.Cp936Lossy();
    }


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
