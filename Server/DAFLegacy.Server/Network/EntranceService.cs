using System.Net;
using System.Net.Sockets;
using DAFLegacy.Protocol;
using DAFLegacy.Server.Sqlite;

namespace DAFLegacy.Server;

public sealed class EntranceService(
    ServerOptions options,
    ServerSettings settings,
    RuntimeState runtime,
    IGameStore store,
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
    TownPresenceService townPresence,
    TradeSessionRegistry trades,
    EntranceChannelDirectory channelDirectory,
    ILoggerFactory loggerFactory
) : BackgroundService
{
    // Protocol-layer dispatch (07 §4.6): mounted command handlers take over
    // their protocol exclusively; unmounted ids keep flowing through the
    // legacy branch chain until their group migrates.
    private GameCommandRegistry _gameCommands = null!;
    private readonly GameCommandDispatcher _gameCommandDispatcher = new();
    private readonly PacketTraceLogger _packetTrace = new(runtime, options);
    private ClientWhenAnyHandler _clientWhenAny = null!;
    private readonly CharacterPacketBuilders _characterPacketBuilders =
        new(store, itemCatalog, pvpExperienceCatalog, ceraShopCatalog);
    private ChannelSessionSenders _sessionSenders = null!;
    // Assembly of the dispatch table (07 §4.6): handler construction is
    // manual because every dependency is already a constructor parameter
    // here; a duplicate registration throws before any listener starts.
    private GameCommandRegistry BuildGameCommandRegistry(
        ServerOptions options,
        ILoggerFactory loggerFactory)
    {
        RuntimeLogger.Configure(loggerFactory);
        var registry = new GameCommandRegistry();
        // 连接有效性检查 CMD 0 (0x00) ENUM_CMDPACKET_CHECK_CONNECTION
        registry.Register("session.connection", new CheckConnectionHandler(options, channelDirectory));
        // 客户端退出通知 CMD 3 (0x03) ENUM_CMDPACKET_EXIT
        registry.Register("session.connection", new ExitHandler());
        // 反外挂探测上报 CMD 161 (0xA1) ENUM_CMDPACKET_ANTIBOT
        registry.Register("session.connection", new AntibotHandler());
        // 物品合成 CMD 27 (0x1B) ENUM_CMDPACKET_COMPOUND_ITEM
        registry.Register("economy.craft", new CompoundItemHandler(
            store,
            itemCatalog,
            compoundCatalog,
            _characterPacketBuilders,
            _sessionSenders));
        // 物品分解 CMD 28 (0x1C) ENUM_CMDPACKET_DISJOINT_ITEM
        registry.Register("economy.craft", new DisjointItemHandler(
            store,
            itemCatalog,
            disjointCatalog,
            _characterPacketBuilders,
            _sessionSenders));
        // 使用抽奖类道具 CMD 29 (0x1D) ENUM_CMDPACKET_USE_LOTTERY_ITEM
        registry.Register("economy.craft", new UseLotteryItemHandler(
            store,
            itemCatalog,
            _characterPacketBuilders,
            _sessionSenders));
        // 装备强化 CMD 83 (0x53) ENUM_CMDPACKET_UPGRADE_ITEM
        registry.Register("economy.craft", new UpgradeItemHandler(
            store,
            itemCatalog,
            reinforcementCatalog,
            _characterPacketBuilders,
            _sessionSenders));
        // 装备属性重置(解封) CMD 84 (0x54) ENUM_CMDPACKET_RESET_ITEM_ATTR
        registry.Register("economy.craft", new ResetItemAttributeHandler(
            store,
            itemCatalog,
            resealCatalog,
            _characterPacketBuilders,
            _sessionSenders));
        // 时装合成 CMD 102 (0x66) ENUM_CMDPACKET_COMPOUND_AVATAR
        registry.Register("economy.craft", new CompoundAvatarHandler(
            store: store,
            avatarCompoundCatalog: avatarCompoundCatalog,
            builders: _characterPacketBuilders,
            senders: _sessionSenders));
        // 丢弃物品 CMD 50 (0x32) ENUM_CMDPACKET_DROP_ITEM
        registry.Register("economy.craft", new DropItemHandler(
            store: store,
            itemCatalog: itemCatalog));
        // 拾取掉落物品 CMD 46 (0x2E) ENUM_CMDPACKET_GET_ITEM
        registry.Register("economy.craft", new GetItemHandler(
            store: store,
            itemCatalog: itemCatalog,
            characterStatCatalog: characterStatCatalog,
            builders: _characterPacketBuilders,
            senders: _sessionSenders));
        // 点券商城购买 CMD 67 (0x43) ENUM_CMDPACKET_BUY_CERASHOP_ITEM
        registry.Register("economy.shop", new CeraShopBuyHandler(
            store,
            itemCatalog,
            ceraShopCatalog,
            options,
            runtime,
            characterSessions,
            _characterPacketBuilders,
            _sessionSenders));
        // 金币购买商店物品 CMD 21 (0x15) ENUM_CMDPACKET_BUY_ITEM
        registry.Register("economy.shop", new BuyItemHandler(
            store,
            itemCatalog,
            characterSessions,
            _characterPacketBuilders,
            _sessionSenders));
        // 出售物品 CMD 24 (0x18) ENUM_CMDPACKET_SELL_ITEM
        registry.Register("economy.shop", new SellItemHandler(
            store: store,
            itemCatalog: itemCatalog));
        // 修理装备耐久 CMD 25 (0x19) ENUM_CMDPACKET_REPAIR_EQUIPMENT
        registry.Register("economy.shop", new RepairEquipmentHandler(
            store: store,
            itemCatalog: itemCatalog));
        // 交易锁定状态设置 CMD 26 (0x1A) ENUM_CMDPACKET_SET_ITEMTRADE_STATE
        registry.Register("economy.shop", new ItemTradeStateHandler(
            store: store,
            itemCatalog: itemCatalog,
            trades: trades,
            characterSessions: characterSessions));
        // 生成点券票据 CMD 68 (0x44) ENUM_CMDPACKET_GEN_CERATICKET
        registry.Register("economy.shop", new GenerateCeraTicketHandler());
        // 点券余额刷新查询 CMD 66 (0x42) ENUM_CMDPACKET_CERA
        registry.Register("economy.shop", new CeraQueryHandler());
        // 疲劳恢复 CMD 9 (0x09) ENUM_CMDPACKET_RECOVER_STAMINA
        registry.Register("progression.stamina", new RecoverStaminaHandler(
            store: store,
            staminaRecoveryCatalog: staminaRecoveryCatalog));
        // 进入副本开始游戏 CMD 15 (0x0F) ENUM_CMDPACKET_START_GAME
        registry.Register("combat.dungeon", new StartGameHandler(
            store: store,
            worldMapCatalog: worldMapCatalog,
            gameplayDatagram: gameplayDatagram,
            senders: _sessionSenders));
        // 副本内返回城镇 CMD 142 (0x8E) ENUM_CMDPACKET_BACK_2_VILLAGE
        registry.Register("combat.dungeon", new BackToVillageHandler(
            senders: _sessionSenders));
        // 选择副本 CMD 16 (0x10) ENUM_CMDPACKET_SELECT_DUNGEON
        registry.Register("combat.dungeon", new SelectDungeonHandler(
            store: store,
            questCatalog: questCatalog,
            dungeonCatalog: dungeonCatalog,
            builders: _characterPacketBuilders,
            senders: _sessionSenders));
        // 使用技能 CMD 41 (0x29) ENUM_CMDPACKET_USE_SKILL
        registry.Register("combat.dungeon", new UseSkillHandler(
            runtime: runtime,
            options: options,
            skillCatalog: skillCatalog));
        // 副本内移动 CMD 48 (0x30) ENUM_CMDPACKET_MOVE_MAP
        registry.Register("combat.dungeon", new MoveMapHandler(
            senders: _sessionSenders));
        // 领主死亡判定 CMD 127 (0x7F) ENUM_CMDPACKET_BOSS_DIE_CHECK
        registry.Register("combat.dungeon", new BossDieCheckHandler());
        // 全部怪物死亡通报 CMD 148 (0x94) ENUM_CMDPACKET_ALLDIE_MONSTER
        registry.Register("combat.dungeon", new AllDieMonsterHandler());
        // 角色死亡处理 CMD 43 (0x2B) ENUM_CMDPACKET_DIE_CHARACTER
        registry.Register("combat.dungeon", new DieCharacterHandler());
        // 使用复活币 CMD 44 (0x2C) ENUM_CMDPACKET_USE_COIN
        registry.Register("combat.dungeon", new UseCoinHandler(
            store: store));
        // 怪物死亡结算(经验与掉落) CMD 42 (0x2A) ENUM_CMDPACKET_DIE_MONSTER
        registry.Register("combat.dungeon", new DieMonsterHandler(
            dungeonExperienceCatalog: dungeonExperienceCatalog,
            hellDropCatalog: hellDropCatalog,
            monsterChampionDrops: monsterChampionDrops,
            settings: settings,
            itemCatalog: itemCatalog,
            store: store,
            dungeonCatalog: dungeonCatalog,
            experienceCatalog: experienceCatalog,
            dropGenerator: dropGenerator,
            senders: _sessionSenders));
        // 副本通关结算 CMD 49 (0x31) ENUM_CMDPACKET_SET_PLAY_RESULT
        registry.Register("combat.dungeon", new SetPlayResultHandler(
            dungeonExperienceCatalog: dungeonExperienceCatalog,
            store: store,
            itemCatalog: itemCatalog,
            dungeonCatalog: dungeonCatalog,
            experienceCatalog: experienceCatalog,
            spCatalog: spCatalog,
            clearRewardGenerator: clearRewardGenerator,
            characterSessions: characterSessions,
            senders: _sessionSenders));
        // 计分卷轴状态查询 CMD 72 (0x48) ENUM_CMDPACKET_SCORE_SCROLL_STATE
        registry.Register("combat.dungeon", new ScoreScrollStateHandler(
            senders: _sessionSenders));
        // 教学关完成标记 CMD 153 (0x99) ENUM_CMDPACKET_CHANGE_TUTORIAL_FLAG
        registry.Register("combat.dungeon", new TutorialCompletionHandler(
            senders: _sessionSenders));
        // 翻牌权状态查询 CMD 73 (0x49) ENUM_CMDPACKET_CARD_SELECT_RIGHT_STATE
        registry.Register("combat.dungeon", new CardSelectRightStateHandler());
        // 通关翻牌选择 CMD 74 (0x4A) ENUM_CMDPACKET_SELECT_CARD
        registry.Register("combat.dungeon", new SelectCardHandler(
            senders: _sessionSenders));
        // 放弃副本 CMD 45 (0x2D) ENUM_CMDPACKET_GIVEUP_GAME
        registry.Register("combat.dungeon", new GiveupGameHandler(
            senders: _sessionSenders));
        // 副本结算握手(EPLP) CMD 75 (0x4B) ENUM_CMDPACKET_EPLP_COMMAND
        registry.Register("combat.dungeon", new EplpHandler(
            senders: _sessionSenders));
        // 副本加载完成通知 CMD 40 (0x28) ENUM_CMDPACKET_FINISH_LOADING
        registry.Register("combat.dungeon", new FinishLoadingHandler(
            senders: _sessionSenders));
        // 精灵改名 CMD 103 (0x67) ENUM_CMDPACKET_RENAME_CREATURE
        registry.Register("world.pets", new RenameCreatureHandler(
            store,
            itemCatalog,
            _characterPacketBuilders));
        // 精灵孵化 CMD 105 (0x69) ENUM_CMDPACKET_HATCH_CREATURE
        registry.Register("world.pets", new HatchCreatureHandler(
            store,
            itemCatalog,
            _characterPacketBuilders));
        // 精灵信息应答 CMD 104 (0x68) ENUM_CMDPACKET_RESPONSE_CREATURE
        registry.Register("world.pets", new ResponseCreatureHandler(_sessionSenders));
        // 城镇位置同步 CMD 37 (0x25) ENUM_CMDPACKET_SET_USER_POSITION
        registry.Register("world.town", new SetUserPositionHandler(
            partyCoordinator: partyCoordinator,
            senders: _sessionSenders));
        // 城镇区域切换 CMD 38 (0x26) ENUM_CMDPACKET_SET_USER_AREA
        registry.Register("world.town", new SetUserAreaHandler(
            store: store,
            senders: _sessionSenders));
        // 个人商店列表轮询 CMD 82 (0x52) ENUM_CMDPACKET_CALL_MEMER_LIST
        registry.Register("world.town", new PrivateStorePollHandler());
        // 个人商店（11 号 P4，economy.personal-shop 组）：88/90/86/87/89 请求侧
        // 布局已取证（P-35）；上架物品通道与 85 购买应答细节待真机（P-36）。
        var storeRegistry = new PrivateStoreRegistry();
        registry.Register("economy.personal-shop", new CreatePrivateStoreHandler(storeRegistry, townPresence));
        registry.Register("economy.personal-shop", new CompletePrivateStoreHandler(storeRegistry));
        registry.Register("economy.personal-shop", new EnterPrivateStoreHandler(storeRegistry, townPresence));
        registry.Register("economy.personal-shop", new ExitPrivateStoreHandler());
        registry.Register("economy.personal-shop", new RemovePrivateStoreHandler(storeRegistry));

        // 好友与黑名单（11 号 P2，social.friends 组）：CMD 130 由 canned 应答升级为
        // 真实黑名单查询（P-35 布局取证）。
        registry.Register("social.friends", new AddFriendHandler(store, townPresence, characterSessions));
        registry.Register("social.friends", new RemoveFriendHandler(store, townPresence, characterSessions));
        registry.Register("social.friends", new RegisterToBlacklistHandler(store));
        registry.Register("social.friends", new DeleteToBlacklistHandler(store));
        registry.Register("social.friends", new RequestBlacklistHandler(store));
        {
            // Party family (07 §4.6 族接管): one shared core, five thin shells.
            var partyCore = new PartyHandlerCore(partyCoordinator);
            // 组队邀请请求 CMD 10 (0x0A) ENUM_CMDPACKET_REQUEST_PEER
            registry.Register("social.party", new RequestPeerHandler(partyCore));
            // 组队邀请应答 CMD 11 (0x0B) ENUM_CMDPACKET_RESPONSE_PEER
            registry.Register("social.party", new ResponsePeerHandler(partyCore));
            // 队伍信息设置 CMD 12 (0x0C) ENUM_CMDPACKET_SET_PARTY_INFO
            registry.Register("social.party", new SetPartyInfoHandler(partyCore));
            // 主动退队 CMD 13 (0x0D) ENUM_CMDPACKET_LEAVE_PARTY
            registry.Register("social.party", new LeavePartyHandler(partyCore));
            // 队长移除队员 CMD 14 (0x0E) ENUM_CMDPACKET_WALKOUT_PARTY_MEMBER
            registry.Register("social.party", new WalkoutPartyMemberHandler(partyCore));
            // 转让队长 CMD 131 (0x83) ENUM_CMDPACKET_CHANGE_HOST（无应答，NOTI 26/9 广播）
            registry.Register("social.party", new TransferLeaderHandler(partyCore));
        }
        // 整理背包 CMD 20 (0x14) ENUM_CMDPACKET_SORT_ITEM
        registry.Register("economy.inventory", new SortItemHandler(
            store,
            itemCatalog,
            _characterPacketBuilders));
        // 物品跨栏位移动(背包/仓库/装扮/精灵) CMD 19 (0x13) ENUM_CMDPACKET_MOVE_ITEMSPACE
        registry.Register("economy.inventory", new MoveItemSpaceHandler(
            store,
            itemCatalog,
            _characterPacketBuilders,
            _sessionSenders,
            premiumBenefitCatalog));
        // 删除物品 CMD 18 (0x12) ENUM_CMDPACKET_DELETE_ITEM
        registry.Register("economy.inventory", new DeleteItemHandler(
            options: options,
            runtime: runtime,
            store: store,
            itemCatalog: itemCatalog));
        // 使用可堆叠道具 CMD 47 (0x2F) ENUM_CMDPACKET_USE_STACKABLE
        registry.Register("economy.inventory", new UseStackableItemHandler(
            options,
            runtime,
            store,
            itemCatalog,
            spCatalog,
            experienceCatalog,
            pvpExperienceCatalog,
            characterSessions,
            _characterPacketBuilders,
            _sessionSenders));
        // 装备耐久扣减 CMD 51 (0x33) ENUM_CMDPACKET_DECREASE_DURABILITY
        registry.Register("economy.inventory", new DecreaseDurabilityHandler(
            store: store,
            itemCatalog: itemCatalog));
        // 状态点分配 CMD 32 (0x20) ENUM_CMDPACKET_INCREASE_STATUS
        registry.Register("progression.skill", new IncreaseStatusHandler(
            store,
            itemCatalog,
            skillCatalog,
            spCatalog,
            experienceCatalog,
            _sessionSenders));
        // 购买技能 CMD 31 (0x1F) ENUM_CMDPACKET_BUY_SKILL
        registry.Register("progression.skill", new BuySkillHandler(
            store,
            skillCatalog,
            premiumBenefitCatalog,
            _sessionSenders));
        // 技能快速槽互换 CMD 30 (0x1E) ENUM_CMDPACKET_CHANGE_SKILLSLOT
        registry.Register("progression.skill", new ChangeSkillSlotHandler(
            store,
            skillCatalog));
        // 接受任务 CMD 33 (0x21) ENUM_CMDPACKET_ACCEPT_QUEST
        registry.Register("progression.quest", new AcceptQuestHandler(
            store: store,
            itemCatalog: itemCatalog,
            questCatalog: questCatalog,
            dungeonCatalog: dungeonCatalog,
            senders: _sessionSenders));
        // 放弃任务 CMD 34 (0x22) ENUM_CMDPACKET_GIVEUP_QUEST
        registry.Register("progression.quest", new GiveupQuestHandler(
            store,
            questCatalog,
            _sessionSenders));
        // 任务触发条件上报 CMD 35 (0x23) ENUM_CMDPACKET_SET_QUEST_TRIGGER
        registry.Register("progression.quest", new SetQuestTriggerHandler(
            store,
            questCatalog,
            _sessionSenders));
        // 完成任务结算奖励 CMD 36 (0x24) ENUM_CMDPACKET_FINISH_QUEST
        registry.Register("progression.quest", new FinishQuestHandler(
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
        // 打开邮箱 CMD 99 (0x63) ENUM_CMDPACKET_MAILBOX_OPEN
        registry.Register("social.mailbox", new MailboxOpenHandler(_sessionSenders));
        // 提取邮件附件 CMD 98 (0x62) ENUM_CMDPACKET_MAILBOX_EXTRACT_ITEM
        registry.Register("social.mailbox", new MailboxExtractHandler(
            store,
            itemCatalog,
            _characterPacketBuilders,
            _sessionSenders));
        // 邮件状态更新(已读/删除) CMD 144 (0x90) ENUM_CMDPACKET_CHANGE_LETTER_STAT
        registry.Register("social.mailbox", new MailboxStateHandler(
            store: store));
        // 发送邮件 CMD 97 (0x61) ENUM_CMDPACKET_MAILBOX_SEND
        registry.Register("social.mailbox", new MailboxSendHandler(
            store: store,
            itemCatalog: itemCatalog,
            characterSessions: characterSessions,
            builders: _characterPacketBuilders));
        // 聊天消息发送 CMD 17 (0x11) ENUM_CMDPACKET_SEND_MESSAGE
        registry.Register("social.chat", new SendMessageHandler(
            store,
            characterSessions,
            partyCoordinator,
            _characterPacketBuilders,
            _sessionSenders));
        // 精灵聊天消息 CMD 124 (0x7C) ENUM_CMDPACKET_CREATURE_SEND_MESSAGE
        registry.Register("social.chat", new CreatureSendMessageHandler());
        // 精灵脚本消息 CMD 132 (0x84) ENUM_CMDPACKET_CREATURE_SCRIPT_MESSAGE
        registry.Register("social.chat", new CreatureScriptMessageHandler());
        // 账号登录 CMD 1 (0x01) ENUM_CMDPACKET_LOGIN
        registry.Register("session.login", new LoginHandler(store));
        // UDP 端点上报 CMD 2 (0x02) ENUM_CMDPACKET_SET_UDP_IP_PORT
        registry.Register("session.login", new SetUdpEndpointHandler(gameplayDatagram, partyCoordinator));
        // 用户信息查询 CMD 8 (0x08) ENUM_CMDPACKET_GET_USERINFO
        registry.Register("progression.character", new GetUserInfoHandler(_characterPacketBuilders));
        // 创建角色 CMD 5 (0x05) ENUM_CMDPACKET_CREATE_CHARACTER
        registry.Register("progression.character", new CreateCharacterHandler(store, _characterPacketBuilders));
        // 删除角色 CMD 6 (0x06) ENUM_CMDPACKET_DELETE_CHARACTER
        registry.Register("progression.character", new DeleteCharacterHandler(store, _characterPacketBuilders));
        // 选择角色进入游戏 CMD 4 (0x04) ENUM_CMDPACKET_SELECT_CHARACTER
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
            _sessionSenders));
        // 返回选角界面 CMD 7 (0x07) ENUM_CMDPACKET_RETURN_SELECT_CHARACTER
        registry.Register("progression.character", new ReturnSelectCharacterHandler(
            store,
            characterSessions,
            partyCoordinator));
        return registry;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Entrance.Enabled)
        {
            RuntimeLogger.Warn("Entrance listener is disabled.");
            return;
        }

        _sessionSenders = new ChannelSessionSenders(
            store,
            itemCatalog,
            channelDirectory,
            options,
            characterStatCatalog,
            spCatalog,
            creatureExperienceCatalog,
            characterSessions,
            townPresence,
            partyCoordinator,
            questCatalog,
            dungeonCatalog,
            pvpExperienceCatalog,
            _characterPacketBuilders);
        _gameCommands = BuildGameCommandRegistry(options, loggerFactory);
        _clientWhenAny = new ClientWhenAnyHandler(
            options,
            runtime,
            store,
            characterSessions,
            partyCoordinator,
            gameplayDatagram,
            itemCatalog,
            dungeonCatalog,
            _gameCommands,
            _gameCommandDispatcher,
            _sessionSenders,
            _packetTrace,
            trades);

        // Startup listing of the mounted dispatch table (07 §4.6): one line
        // per registered group so a mis-mounted protocol is visible at boot.
        foreach (var group in _gameCommands.Entries.GroupBy(entry => entry.Group))
        {
            RuntimeLogger.Info(
                "Game command group {Group} mounted: {Handlers}",
                group.Key,
                string.Join(", ", group.Select(entry => $"{entry.Handler.Name}({entry.Handler.ProtocolId})")));
        }

        var address = IPAddress.Parse(options.Entrance.Host);
        // M2 per-channel listeners: every catalog channel owns PortBase+number
        // and the legacy Channel.Port keeps the first-entry identity (fallback
        // single channel when no catalog). Entrance ports exclude Channel.Port
        // — that port belongs to the channel listener family.
        var listeners = new List<(TcpListener Listener, EntranceSessionChannel? Channel)>();
        var boundPorts = new HashSet<int>();
        foreach (var port in options.Entrance.AdditionalPorts.Prepend(options.Entrance.Port))
        {
            if (port is not (> 0 and <= 65535) || !boundPorts.Add(port))
            {
                continue;
            }

            if (port == options.Channel.Port)
            {
                RuntimeLogger.Info(
                    "Entrance port {Port} equals the channel port; it is served by the channel listener.",
                    port);
                continue;
            }

            listeners.Add((new TcpListener(address, port), null));
        }

        foreach (var entry in channelDirectory.Catalog ?? [])
        {
            var port = options.Channel.PortFor(entry.ChannelNumber);
            if (port is not (> 0 and <= 65535) || !boundPorts.Add(port))
            {
                RuntimeLogger.Warn(
                    "Channel {ChannelNumber} listener port {Port} is invalid or already bound; skipped.",
                    entry.ChannelNumber,
                    port);
                continue;
            }

            listeners.Add((
                new TcpListener(IPAddress.Parse(options.Channel.Host), port),
                new EntranceSessionChannel(entry.ChannelNumber, entry.DisplayName)));
        }

        if (boundPorts.Add(options.Channel.Port))
        {
            listeners.Add((
                new TcpListener(IPAddress.Parse(options.Channel.Host), options.Channel.Port),
                channelDirectory.Session));
        }

        foreach (var (listener, _) in listeners)
        {
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            RuntimeLogger.Info(
                "Entrance listener ready at {Address}:{Port}",
                address,
                port);
        }

        try
        {
            await Task.WhenAll(listeners.Select(entry =>
                AcceptClientConnectionsAsync(
                    entry.Listener,
                    ((IPEndPoint)entry.Listener.LocalEndpoint).Port,
                    entry.Channel,
                    stoppingToken)));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            foreach (var (listener, _) in listeners)
            {
                listener.Stop();
            }
        }
    }

    private async Task AcceptClientConnectionsAsync(
        TcpListener listener,
        int port,
        EntranceSessionChannel? channel,
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var client = await listener.AcceptTcpClientAsync(stoppingToken);
            _ = _clientWhenAny.HandleClientWhenAny(client, port, channel, stoppingToken);
        }
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
}
