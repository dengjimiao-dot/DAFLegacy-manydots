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
        registry.Register("economy.craft", new CompoundItemHandler(
            store,
            itemCatalog,
            compoundCatalog,
            loggerFactory.CreateLogger<CompoundItemHandler>(),
            _characterPacketBuilders,
            _sessionSenders));
        registry.Register("economy.craft", new DisjointItemHandler(
            store,
            itemCatalog,
            disjointCatalog,
            loggerFactory.CreateLogger<DisjointItemHandler>(),
            _characterPacketBuilders,
            _sessionSenders));
        registry.Register("economy.craft", new UseLotteryItemHandler(
            store,
            itemCatalog,
            loggerFactory.CreateLogger<UseLotteryItemHandler>(),
            _characterPacketBuilders,
            _sessionSenders));
        registry.Register("economy.craft", new UpgradeItemHandler(
            store,
            itemCatalog,
            reinforcementCatalog,
            loggerFactory.CreateLogger<UpgradeItemHandler>(),
            _characterPacketBuilders,
            _sessionSenders));
        registry.Register("economy.craft", new ResetItemAttributeHandler(
            store,
            itemCatalog,
            resealCatalog,
            loggerFactory.CreateLogger<ResetItemAttributeHandler>(),
            _characterPacketBuilders,
            _sessionSenders));
        registry.Register("economy.craft", new CompoundAvatarHandler(
            store: store,
            avatarCompoundCatalog: avatarCompoundCatalog,
            logger: loggerFactory.CreateLogger<CompoundAvatarHandler>(),
            builders: _characterPacketBuilders,
            senders: _sessionSenders));
        registry.Register("economy.craft", new DropItemHandler(
            store: store,
            itemCatalog: itemCatalog,
            logger: loggerFactory.CreateLogger<DropItemHandler>()));
        registry.Register("economy.craft", new GetItemHandler(
            store: store,
            itemCatalog: itemCatalog,
            characterStatCatalog: characterStatCatalog,
            logger: loggerFactory.CreateLogger<GetItemHandler>(),
            builders: _characterPacketBuilders,
            senders: _sessionSenders));
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
            store: store,
            itemCatalog: itemCatalog,
            logger: loggerFactory.CreateLogger<SellItemHandler>()));
        registry.Register("economy.shop", new RepairEquipmentHandler(
            store: store,
            itemCatalog: itemCatalog,
            logger: loggerFactory.CreateLogger<RepairEquipmentHandler>()));
        registry.Register("economy.shop", new ItemTradeStateHandler(
            store: store,
            itemCatalog: itemCatalog,
            logger: loggerFactory.CreateLogger<ItemTradeStateHandler>()));
        registry.Register("economy.shop", new GenerateCeraTicketHandler(
            loggerFactory.CreateLogger<GenerateCeraTicketHandler>()));
        registry.Register("progression.stamina", new RecoverStaminaHandler(
            store: store,
            staminaRecoveryCatalog: staminaRecoveryCatalog,
            logger: loggerFactory.CreateLogger<RecoverStaminaHandler>()));
        registry.Register("combat.dungeon", new StartGameHandler(
            store: store,
            worldMapCatalog: worldMapCatalog,
            gameplayDatagram: gameplayDatagram,
            logger: loggerFactory.CreateLogger<StartGameHandler>(),
            senders: _sessionSenders));
        registry.Register("combat.dungeon", new BackToVillageHandler(
            logger: loggerFactory.CreateLogger<BackToVillageHandler>(),
            senders: _sessionSenders));
        registry.Register("combat.dungeon", new SelectDungeonHandler(
            store: store,
            questCatalog: questCatalog,
            dungeonCatalog: dungeonCatalog,
            logger: loggerFactory.CreateLogger<SelectDungeonHandler>(),
            builders: _characterPacketBuilders,
            senders: _sessionSenders));
        registry.Register("combat.dungeon", new UseSkillHandler(
            runtime: runtime,
            options: options,
            skillCatalog: skillCatalog,
            logger: loggerFactory.CreateLogger<UseSkillHandler>()));
        registry.Register("combat.dungeon", new MoveMapHandler(
            logger: loggerFactory.CreateLogger<MoveMapHandler>(),
            senders: _sessionSenders));
        registry.Register("combat.dungeon", new BossDieCheckHandler(
            logger: loggerFactory.CreateLogger<BossDieCheckHandler>()));
        registry.Register("combat.dungeon", new AllDieMonsterHandler(
            logger: loggerFactory.CreateLogger<AllDieMonsterHandler>()));
        registry.Register("combat.dungeon", new DieCharacterHandler(
            logger: loggerFactory.CreateLogger<DieCharacterHandler>()));
        registry.Register("combat.dungeon", new UseCoinHandler(
            store: store,
            logger: loggerFactory.CreateLogger<UseCoinHandler>()));
        registry.Register("combat.dungeon", new DieMonsterHandler(
            dungeonExperienceCatalog: dungeonExperienceCatalog,
            hellDropCatalog: hellDropCatalog,
            monsterChampionDrops: monsterChampionDrops,
            options: options,
            itemCatalog: itemCatalog,
            store: store,
            dungeonCatalog: dungeonCatalog,
            experienceCatalog: experienceCatalog,
            dropGenerator: dropGenerator,
            logger: loggerFactory.CreateLogger<DieMonsterHandler>(),
            senders: _sessionSenders));
        registry.Register("combat.dungeon", new SetPlayResultHandler(
            dungeonExperienceCatalog: dungeonExperienceCatalog,
            store: store,
            itemCatalog: itemCatalog,
            dungeonCatalog: dungeonCatalog,
            experienceCatalog: experienceCatalog,
            spCatalog: spCatalog,
            clearRewardGenerator: clearRewardGenerator,
            characterSessions: characterSessions,
            logger: loggerFactory.CreateLogger<SetPlayResultHandler>(),
            senders: _sessionSenders));
        registry.Register("combat.dungeon", new ScoreScrollStateHandler(
            senders: _sessionSenders,
            logger: loggerFactory.CreateLogger<ScoreScrollStateHandler>()));
        registry.Register("combat.dungeon", new TutorialCompletionHandler(
            senders: _sessionSenders));
        registry.Register("combat.dungeon", new CardSelectRightStateHandler(
            logger: loggerFactory.CreateLogger<CardSelectRightStateHandler>()));
        registry.Register("combat.dungeon", new SelectCardHandler(
            logger: loggerFactory.CreateLogger<SelectCardHandler>(),
            senders: _sessionSenders));
        registry.Register("combat.dungeon", new GiveupGameHandler(
            logger: loggerFactory.CreateLogger<GiveupGameHandler>(),
            senders: _sessionSenders));
        registry.Register("combat.dungeon", new EplpHandler(
            logger: loggerFactory.CreateLogger<EplpHandler>(),
            senders: _sessionSenders));
        registry.Register("combat.dungeon", new FinishLoadingHandler(
            senders: _sessionSenders));
        registry.Register("world.pets", new RenameCreatureHandler(
            store,
            itemCatalog,
            loggerFactory.CreateLogger<RenameCreatureHandler>(),
            _characterPacketBuilders));
        registry.Register("world.pets", new HatchCreatureHandler(
            store,
            itemCatalog,
            loggerFactory.CreateLogger<HatchCreatureHandler>(),
            _characterPacketBuilders));
        registry.Register("world.pets", new ResponseCreatureHandler(_sessionSenders));
        registry.Register("world.town", new SetUserPositionHandler(
            partyCoordinator: partyCoordinator,
            senders: _sessionSenders));
        registry.Register("world.town", new SetUserAreaHandler(
            store: store,
            senders: _sessionSenders));
        registry.Register("world.town", new PrivateStorePollHandler());
        registry.Register("world.town", new ExchangeServerCharacterInfoHandler());
        {
            // Party family (07 §4.6 族接管): one shared core, five thin shells.
            var partyCore = new PartyHandlerCore(
                partyCoordinator,
                loggerFactory.CreateLogger<PartyHandlerCore>());
            registry.Register("social.party", new RequestPeerHandler(partyCore));
            registry.Register("social.party", new ResponsePeerHandler(partyCore));
            registry.Register("social.party", new SetPartyInfoHandler(partyCore));
            registry.Register("social.party", new LeavePartyHandler(partyCore));
            registry.Register("social.party", new WalkoutPartyMemberHandler(partyCore));
        }
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
            options: options,
            runtime: runtime,
            store: store,
            itemCatalog: itemCatalog,
            logger: loggerFactory.CreateLogger<DeleteItemHandler>()));
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
            store: store,
            itemCatalog: itemCatalog,
            logger: loggerFactory.CreateLogger<DecreaseDurabilityHandler>()));
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
            logger: loggerFactory.CreateLogger<AcceptQuestHandler>(),
            store: store,
            itemCatalog: itemCatalog,
            questCatalog: questCatalog,
            dungeonCatalog: dungeonCatalog,
            senders: _sessionSenders));
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
        registry.Register("social.mailbox", new MailboxStateHandler(
            store: store));
        registry.Register("social.mailbox", new MailboxSendHandler(
            store: store,
            itemCatalog: itemCatalog,
            characterSessions: characterSessions,
            builders: _characterPacketBuilders));
        registry.Register("social.chat", new SendMessageHandler(
            store,
            characterSessions,
            partyCoordinator,
            options,
            loggerFactory.CreateLogger<SendMessageHandler>(),
            _characterPacketBuilders,
            _sessionSenders));
        registry.Register("social.chat", new CreatureSendMessageHandler(
            logger: loggerFactory.CreateLogger<CreatureSendMessageHandler>()));
        registry.Register("social.chat", new CreatureScriptMessageHandler(
            logger: loggerFactory.CreateLogger<CreatureScriptMessageHandler>()));
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
            scripts,
            options,
            characterStatCatalog,
            spCatalog,
            creatureExperienceCatalog,
            characterSessions,
            partyCoordinator,
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

                // Registers this session's town entity with the party
                // coordinator so same-area residents can see and invite this
                // character. The appearance packet is built here because only
                // this connection owns the equipped-item dictionaries.
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

                    var alarm = MailboxPacketBuilders.CreateMailboxAlarm(
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
                Task SendTownFatigueStateAsync() =>
                    _sessionSenders.SendTownFatigueStateAsync(connectionContext, serverToken);
                Task<bool> TryAutomaticallySelectFreeCardAsync(string source) =>
                    _sessionSenders.TryAutomaticallySelectFreeCardAsync(connectionContext, source, serverToken);

                Task ReturnToTownFromDungeonAsync(string reason, bool forcedDungeonExit = false) =>
                    _sessionSenders.ReturnToTownFromDungeonAsync(connectionContext, reason, forcedDungeonExit, serverToken);

                if (isChannelPort)
                {
                    // The launcher applies the process-local channel callback
                    // after the client's startup integrity pass. This client
                    // can reach port 7001 a few hundred milliseconds earlier,
                    // and it never retries a greeting handled by the retired
                    // callback. Hold the first channel packet until that patch
                    // window has safely completed.
                    await Task.Delay(TimeSpan.FromMilliseconds(750), serverToken);
                    var greeting = ConnectionPacketBuilders.CreateCheckConnection(protocolSession.SessionToken);
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

                    while (session.Events.Reader.TryRead(out var connectionEvent))
                    {
                        switch (connectionEvent.Kind)
                        {
                            case ConnectionEventKind.MailAlarm:
                                if (session.Character.Active is not null)
                                {
                                    await SendNewMailAlarmAsync();
                                }

                                break;
                            case ConnectionEventKind.PremiumServiceUpdate:
                                if (session.Character.Active is not null)
                                {
                                    await SendPremiumServiceInfoAsync(
                                        connectionEvent.ServiceType,
                                        (uint)connectionEvent.Value);
                                }

                                break;
                            case ConnectionEventKind.CeraValueUpdated:
                                if (session.Character.Active is not null)
                                {
                                    session.Currency.Cash = connectionEvent.Value;
                                    var ceraNotification = ShopPacketBuilders.CreateCeraUpdate(
                                        success: true,
                                        value: (uint)connectionEvent.Value);
                                    await ceraNotification.WriteAsync(stream, serverToken);
                                    Interlocked.Increment(ref runtimeSession.SentPackets);
                                    LogPacket("TX", runtimeSession, ceraNotification);
                                }

                                break;
                            case ConnectionEventKind.FatigueReset:
                                if (session.Character.Active is not null)
                                {
                                    session.Character.Active = session.Character.Active with
                                    {
                                        UsedFatigue = 0
                                    };
                                    if (session.Dungeon.CurrentDungeonId == 0)
                                    {
                                        await SendTownFatigueStateAsync();
                                    }
                                }

                                break;
                            case ConnectionEventKind.WeaknessRecovered:
                                if (session.Character.Active is not null)
                                {
                                    session.Character.Active = session.Character.Active with
                                    {
                                        WeaknessRecovery = (byte)connectionEvent.Value,
                                        LegacyIsWeakened = null
                                    };
                                }

                                break;
                        }
                    }

                    if (isChannelPort)
                    {
                        if (await TryDispatchGameCommandAsync(request))
                        {
                            continue;
                        }










































                        // -----------------------------------------------------
                        // Party block (CMD 10-14). The client sends a two-byte
                        // sequence number before every command body, so the
                        // parsers below read fields from offset 2.
                        // -----------------------------------------------------


















                        // Last-resort fallback for commands the registry does not
                        // mount yet; CHECK_CONNECTION is dispatched by the registry
                        // (CheckConnectionHandler) before reaching this point.
                        var gameReply = GameProtocolEngine.Handle(
                            request,
                            protocolSession.SessionToken);
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

    private EntranceContent LoadContent() => _sessionSenders.LoadContent();

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
        var body = HexDump.ToSpacedHex(frame.Body.AsSpan(0, bodyLimit));
        var wire = (rawFrame ?? frame).Encode();
        var rawLimit = Math.Min(wire.Length, options.HexDumpLimit + PacketFrame.HeaderLength);
        var raw = HexDump.ToSpacedHex(wire.AsSpan(0, rawLimit));
        var idLabel = frame.Type == GameProtocolEngine.NotificationPacketType ? "NOTI" : "CMD";
        // Received command bodies are the TryDecode product (the wire carries
        // the client's rolling-transform ciphertext); server-built frames are
        // plaintext, so only RX keeps the decode label.
        var bodyLabel = direction == "RX" ? "TryDecodeBody" : "body";
        logger.LogInformation(
            "{IdLabel} [{SessionId}] Packet={Id}({IdHex}) len={Length} {BodyLabel}=({BodyLen}B) [{Body}]{BodySuffix} crc={Crc:X8} valid={Valid}\n  raw: {Raw}{RawSuffix}",
            idLabel, session.Id, frame.ProtocolId, $"0x{frame.ProtocolId:X2}",
            frame.TotalLength, bodyLabel, frame.Body.Length, body, frame.Body.Length > bodyLimit ? "..." : "",
            frame.DeclaredCrc32, frame.HasValidCrc32,
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
        var payloadDump = HexDump.ToSpacedHex(packet.Payload.AsSpan(0, payloadLimit));
        var wire = packet.Encode();
        var rawLimit = Math.Min(wire.Length, options.HexDumpLimit + GameServerPacket.HeaderLength);
        var raw = HexDump.ToSpacedHex(wire.AsSpan(0, rawLimit));
        var idLabel = packet.Type == GameProtocolEngine.NotificationPacketType ? "NOTI" : "CMD";
        logger.LogInformation(
            "{IdLabel} [{SessionId}] Packet={Id}({IdHex}) len={Length} body=({BodyLen}B) [{Body}]{BodySuffix}\n  raw: {Raw}{RawSuffix}",
            idLabel, session.Id, packet.ProtocolId, $"0x{packet.ProtocolId:X2}",
            packet.TotalLength, packet.Payload.Length, payloadDump, packet.Payload.Length > payloadLimit ? "..." : "",
            raw, wire.Length > rawLimit ? "..." : "");
    }
}
