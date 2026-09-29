# ServerS4A21(86 级版本)已实现协议处理参考 · 总览

> - **整理对象**:`ServerS4A21` 的 `Server/DfoServer`(下称 A21),目标客户端为 86 级上限版本(仓库内代号 86JP/S4A21,分发入口方法即名 `OnPacketReceived_86JP`)。本文整理其**已实现的游戏协议处理**,供 DFLegacy 回填协议时参考。
> - **使用方式**:按域拆分的分册见文末导航。每协议条目含:请求解析、应答/推送布局、行为要点,均带 `文件:行号` 证据(行号为整理时点状态,仅供参考)。引用路径相对 `Server/DfoServer`。
> - **配套文档**:两版共有协议的逐项对比与定性见 [comparison-vs-servers4a21.md](comparison-vs-servers4a21.md);60A1 侧枚举全表见 [client-packet-enums-60cn.md](../protocol/client-packet-enums-60cn.md);回填实施方案见 [feature-backfill-plan-vs-s4a21.md](feature-backfill-plan-vs-s4a21.md)。**A21 的 NOTI/CMD 编号与 60A1 有漂移,回填时必须先过 60A1 枚举表换算,严禁照抄编号。A21 结论仅作参考——两版协议与用法存在差异,不得直接照搬;用于本项目的任何结论须经客户端主程序 IDA 数据库逐 case 验证。**

## 1. 传输与帧

### 1.1 监听端口与协议面(Core/GameNetworkConfig.cs:16-29)

| 端口 | 协议面 | 帧头 | 处理器 |
|---:|---|---|---|
| 10128 | 频道/登录前置(选区) | `ChannelPacketHeader`(11B) | `ChannelProtocolHandler` |
| 10011 / 10012 | 普通游戏频道(直连/代理) | `GamePacketHeader`(14B) | `GameProtocolHandler` |
| 10100 / 10162 | 100 频道(直连/代理) | `GamePacketHeader` | `GameProtocolHandler` |
| 10068 | 自由决斗频道 | `GamePacketHeader` | `GameProtocolHandler` |
| 10200 | 团本(Raid)频道 | `GamePacketHeader` | `GameProtocolHandler` |
| 12311 / 12312 | 初始 UDP(组队/约定) | — | `PartyUdpRelay` |
| 30000+256 / 30256+256 | 组队 / PVP UDP 中继端口池 | — | `UdpRelayServer` |

`MultiStructureTcpServer` 按端口绑定 `(处理器, 帧头结构)`(Core/MultiStructureTcpServer.cs:25-49),同一套 `GameProtocolHandler` 服务所有游戏端口;按频道号换算监听端口 `PortForChannel = 10000 + channelId`(GameNetworkConfig.cs:77)。

### 1.2 帧头(Core/Packet.cs:66-79)

客户端→服务端 `GamePacketHeader`(`Pack=1`,共 14B),handler 收到的 body 从第一个载荷字段开始(无 seq 前缀):

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u8 | cmd | 游戏协议内:0=特殊/心跳,1=业务 CMD(`GameProtocolHandler.cs:329-346` 只处理 cmd 1) |
| 1 | u16 | type | CMD 编号(小端) |
| 3 | u32 | length | **整帧长度**(含头) |
| 7 | u32 | checksum | 校验(未参与业务) |
| 11 | u16 | seq | 序号(未参与业务) |
| 13 | u8 | extra | 保留 |

频道面 `ChannelPacketHeader`(11B):`classification u8 + msg_no u8 + sLength u32 + check_sum u32 + ack u8`(Packet.cs:22-28)。

### 1.3 组帧与上限(Core/FlexiblePacketProcessor.cs:66-165)

按 `length` 字段切帧、粘包缓冲;单包上限 1MB,每客户端缓冲上限 2MB,越限抛异常断连。收包即原样落日志(Core/PacketFileLogger.Log,`GameProtocolHandler.cs:316` 记 `RECV`)。

### 1.4 服务端→客户端信封(Core/GamePacketEnvelopeBuilder.cs:8-24)

`GamePacketEnvelopeBuilder.Build(type, body)`:15B 头 = `cmd(1B:0x01 应答 / 0x00 主动推送) + type u16 + length u32(= body 长度 + 15) + 8B 零`。cmd=1 用于 CMD 应答,cmd=0 用于 NOTI 推送(`GameProtocolHandler.cs:444-455` SECURITY_STATUS 应答为 0x01 示例)。

### 1.5 字符串与加密

- dstr = `u32 字节长 + 原始字节`,编码 GBK/CP936(Infrastructure/ClientTextEncoding.cs)。
- AES-128-ECB + 压缩的 `EncryptTool`(Core/EncryptTool.cs)仅用于频道面脚本下发(ChannelProtocolHandler),游戏协议本身不加密。

## 2. 分发架构

### 2.1 入口与门禁(Protocol/GameProtocolHandler.cs:311-347)

1. `OnPacketReceived` 取 `GamePacketHeader` + body → `PacketFileLogger.Log("RECV", ...)`。
2. `CharacterSessionLifecycle.CanDispatch(session, header)` 会话门禁(未选角/切角过渡期拒绝分发)。
3. cmd 0 空块(心跳直接忽略);cmd 1 查 `GameCommandRegistry`(按 CMD 号注册的 `Dictionary<ushort, handler>`);未注册的记 `Unhandled CMD` 日志(含 body 十六进制),不回包。

### 2.2 注册组织(Protocol/GameProtocolHandler.cs:225-283)

约 30 个注册组(login/character/inventory/dungeon/skill/town/settings/quest/mailbox/mercenary/party-chat/raid/friend/guild×4/event-joust/expert-job/misc-pvp/…),组内按 CMD 号挂 handler;部分组在构造时由外部 Handler 类自带 `RegisterHandlers(d)` 注册(聊天/好友/黑名单/频道/PVP 房间)。

### 2.3 特征模式(回填时值得注意)

- **一号多义分流**:同一 CMD 先经谓词链分流(如 0x0013 MOVE 依次被 交易→死亡塔→骑士盾 劫持,`GameProtocolHandler.cs:465-474`;0x00D9 先 Raid 弹窗后彩票,`:425-430`)。
- **Raid 感知包装**:副本关键节点(结算/死亡/复活/退场)先走普通 handler,再触发 Raid 侧回调(`:618-697`)。
- **代际防竞态**:跨会话/切角操作统一经 `CharacterTransitionCoordinator.RunIfCurrentAsync` 校验后执行。
- **处理失败不回包**:handler 异常仅落日志并上抛(`:318-326`),无统一错误应答通道;错误码按域内约定放 body。

## 3. 会话生命周期与初始化包序列

连接建立→`OnClientConnected`→选角(CMD 4)成功后,服务端按固定序列推一批 init 包(角色全量状态),随后才开放业务分发。序列与各包 builder 见分册 01。

## 4. 已实现 CMD 总表(223 项)

共 223 个 CMD 注册项(含空实现)。空实现=收包后不做任何业务(多为 P2P 上报类),仅消掉 Unhandled 日志;未注册的 CMD 落兜底日志不回包。

### 登录/选角/设置/杂项(分册 01)

| CMD | 枚举名 | 处理入口 |
|---:|---|---|
| 0x0001 | LOGIN | _loginHandler.Handle_ENUM_CMDPACKET_LOGIN |
| 0x0005 | CREATE_CHARACTER | _characterSelectHandler.Handle_ENUM_CMDPACKET_CREATE_CHARACTER |
| 0x0006 | DELETE_CHARACTER | _characterSelectHandler.Handle_ENUM_CMDPACKET_DELETE_CHARACTER |
| 0x0008 | GET_USERINFO | _characterSelectHandler.Handle_ENUM_CMDPACKET_GET_USERINFO |
| 0x0009 | RECOVER_STAMINA | _staminaHandler.Handle_ENUM_CMDPACKET_RECOVER_STAMINA |
| 0x00c5 | SAVE_GAME_OPTION_1 | _settingsHandler.Handle_SAVE_GAME_OPTION_1 |
| 0x00c6 | SAVE_GAME_OPTION_2 | _settingsHandler.Handle_SAVE_GAME_OPTION_2(s, h, b) |
| 0x00fe | CHANGE_EMOTION | _settingsHandler.Handle_CHANGE_EMOTION |
| 0x0127 | CHANGE_CHARAC_SLOT | _characterSelectHandler.Handle_CHANGE_CHARAC_SLOT |
| 0x0170 | SAVE_GAME_OPTION_QUICKCHATTING | _settingsHandler.Handle_SAVE_QUICKCHAT(s, h, b) |
| 0x01c0 | SAVE_CHARACTER_OPTION | _settingsHandler.Handle_SAVE_CHARACTER_OPTION(s, h, b) |
| 0x0252 | SECURITY_STATUS | HandleSecurityStatus |
| 0x02b5 | CHECK_DOUBLE_CHARACTER_NAME | _characterSelectHandler.Handle_ENUM_CMDPACKET_CHECK_DOUBLE_CHARACTE... |
| 0x03cf | RENT_EQUIPMENT_ITEM | _rentalHandler.HandleRentWeapon |
| 0x04dd | CHECK_USER_CONNECTION | _loginHandler.Handle_ENUM_CMDPACKET_CHECK_USER_CONNECTION |

### 背包/物品(分册 02、03)

| CMD | 枚举名 | 处理入口 |
|---:|---|---|
| 0x0016 | SELL_ITEM | _inventoryHandler.Handle_ENUM_CMDPACKET_SELL_ITEM |
| 0x0017 | REPAIR_EQUIPMENT | _inventoryHandler.Handle_ENUM_CMDPACKET_REPAIR_EQUIPMENT |
| 0x0019 | COMPOUND_ITEM | _inventoryHandler.Handle_ENUM_CMDPACKET_COMPOUND_ITEM |
| 0x001a | DISJOINT_ITEM | _inventoryHandler.Handle_ENUM_CMDPACKET_DISJOINT_ITEM |
| 0x001b | USE_LOTTERY_ITEM | _lotteryItemHandler.HandleUseLotteryItem |
| 0x0050 | UPGRADE_ITEM | _inventoryHandler.Handle_ENUM_CMDPACKET_UPGRADE_ITEM |
| 0x0051 | RESET_ITEM_ATTR | _inventoryHandler.Handle_ENUM_CMDPACKET_RESET_ITEM_ATTR |
| 0x0063 | COMPOUND_AVATAR | _inventoryHandler.Handle_COMPOUND_AVATAR |
| 0x00A0 | USE_BOOSTER_ITEM / OPEN_SELECTABLE_PACKAGE | ⚠ 注册冲突:枚举 USE_BOOSTER_ITEM=0x00A0,与字面量 d[0x00A0]=Handle_OPEN_SELECTABLE_PACKAGE 撞号,字典后写者胜出→实际生效 Handle_USE_BOOSTER_ITEM(选定礼包 handler 不可达) |
| 0x00c9 | USE_EMBLEM | _inventoryHandler.Handle_AVATAR_EMBLEM_ATTACH |
| 0x00ca | DISJOINT_AVATAR | _inventoryHandler.Handle_DISJOINT_AVATAR |
| 0x00cc | PURIFY_ITEM | _inventoryHandler.Handle_ENUM_CMDPACKET_PURIFY_ITEM |
| 0x00cd | INVEST_ITEM_AMPLIFY_OPTION | _inventoryHandler.Handle_ENUM_CMDPACKET_INVEST_ITEM_AMPLIFY_OPTION |
| 0x00ce | ADD_AVATAR_SOCKET | _inventoryHandler.Handle_AVATAR_SOCKET_OPEN |
| 0x00d0 | USE_RANDOMBOX_ITEM | _inventoryHandler.Handle_OPEN_MAGIC_BOX_SINGLE |
| 0x0100 | COMPOUND_EMBLEM | _inventoryHandler.Handle_COMPOUND_EMBLEM |
| 0x010b | REQUEST_ITEM_LOCK | _inventoryHandler.Handle_ENUM_CMDPACKET_REQUEST_ITEM_LOCK |
| 0x010c | REQUEST_ITEM_UNLOCK | _inventoryHandler.Handle_ENUM_CMDPACKET_REQUEST_ITEM_UNLOCK |
| 0x010d | REQUEST_ITEM_UNLOCK_CANCEL | _inventoryHandler.Handle_ENUM_CMDPACKET_REQUEST_ITEM_UNLOCK_CANCEL |
| 0x010F | UPGRADE_CHRONICLE | _inventoryHandler.Handle_UPGRADE_CHRONICLE |
| 0x0110 | ENCHANT_BY_BEAD | _inventoryHandler.Handle_ENUM_CMDPACKET_ENCHANT_BY_BEAD |
| 0x0131 | CREATE_ACCOUNT_CARGO | _inventoryHandler.Handle_CREATE_ACCOUNT_CARGO |
| 0x0132 | UPGRADE_ACCOUNT_CARGO | _inventoryHandler.Handle_UPGRADE_ACCOUNT_CARGO |
| 0x0133 | DEPOSIT_MONEY | _inventoryHandler.Handle_DEPOSIT_MONEY |
| 0x0134 | WITHDRAW_MONEY | _inventoryHandler.Handle_WITHDRAW_MONEY |
| 0x0172 | ENCHANT_3RD_CHRONICLE_ITEM | _inventoryHandler.Handle_ENCHANT_3RD_CHRONICLE_ITEM |
| 0x0191 | UNSEAL_RANDOM_OPTION | _inventoryHandler.Handle_UNSEAL_RANDOM_OPTION |
| 0x0197 | REGENERATION_RANDOM_OPTION | _inventoryHandler.Handle_REGENERATION_RANDOM_OPTION |
| 0x0198 | UPGRADE_CARGO | _inventoryHandler.Handle_UPGRADE_CARGO |
| 0x019C | TITLE_BOOK_PUT | _inventoryHandler.Handle_TITLE_BOOK |
| 0x019D | TITLE_BOOK_GET | _inventoryHandler.Handle_TITLE_BOOK |
| 0x019e | MONSTERCARD_BIND | _inventoryHandler.Handle_ENUM_CMDPACKET_MONSTERCARD_BIND |
| 0x01A1 | ACHIEVEMENT_TRIGGER | _inventoryHandler.Handle_ACHIEVEMENT_TRIGGER |
| 0x01b6 | CHANGE_RANDOM_OPTION | _inventoryHandler.Handle_CHANGE_RANDOM_OPTION |
| 0x01B7 | UPGRADE_ITEM_SEPARATE | _inventoryHandler.Handle_UPGRADE_ITEM_SEPARATE |
| 0x01cc | AVATAR_OPTION_CHANGE | _inventoryHandler.Handle_AVATAR_OPTION_CHANGE |
| 0x01F3 | USE_DYE | _inventoryHandler.Handle_USE_DYE |
| 0x0207 | OPEN_CERAPACKAGE | _inventoryHandler.Handle_OPEN_AVATAR_PACKAGE |
| 0x0218 | UPDATE_CONTRACT_OF_CUBE_INFO | _inventoryHandler.Handle_USE_BOOSTER_ITEM |
| 0x0239 | SET_CLONE_TITLE | _inventoryHandler.Handle_SET_CLONE_TITLE |
| 0x025c | UPGRADE_CARD | _inventoryHandler.Handle_UPGRADE_CARD |
| 0x0292 | CHANGE_DECK_INFO | _knightShieldHandler.HandleChangeDeckInfo |
| 0x02ca | REQUEST_ITEM_SORT_LOCK | _inventoryHandler.Handle_ENUM_CMDPACKET_TOGGLE_SORT_ITEM_LOCK |
| 0x02cb | REQUEST_ITEM_SORT_UNLOCK | _inventoryHandler.Handle_ENUM_CMDPACKET_UNLOCK_SORT_ITEM_LOCK |
| 0x02cc | SHOP_PURCHASE_COUNT | _inventoryHandler.Handle_ENUM_CMDPACKET_SHOP_PURCHASE_COUNT |
| 0x033A | USE_GEM | _inventoryHandler.Handle_USE_GEM |
| 0x0379 | USE_EMBLEM_FOR_EQUIPMENT | _inventoryHandler.Handle_EQUIPMENT_EMBLEM_ATTACH |
| 0x037A | ADD_EQUIPMENT_SOCKET | _inventoryHandler.Handle_EQUIPMENT_SOCKET_OPEN |
| 0x039F | ADD_EQUIPMENT_EFFECT | _inventoryHandler.Handle_ADD_EQUIPMENT_EFFECT |
| 0x0447 | BIND_PLUS | _inventoryHandler.Handle_COMPOUND_AVATAR_SET |
| 0x0450 | USE_RANDOMBOX_ITEM_EXPAND | _inventoryHandler.Handle_OPEN_MAGIC_BOX |
| CHANGE_DECK_INFO=0x0292 | KnightShieldHandler.HandleChangeDeckInfo | KnightShieldDeckBodyBuilder.cs:8 |

### 副本(分册 04、05)

| CMD | 枚举名 | 处理入口 |
|---:|---|---|
| 0x000f | ENTER_SELECT_DUNGEON | _dungeonHandler.Handle_ENUM_CMDPACKET_ENTER_SELECT_DUNGEON |
| 0x0010 | SELECT_DUNGEON | _dungeonHandler.Handle_ENUM_CMDPACKET_SELECT_DUNGEON |
| 0x0027 | DIE_MONSTER | _dungeonHandler.Handle_ENUM_CMDPACKET_DIE_MONSTER |
| 0x0028 | DIE_CHARACTER | HandleRaidAwareCharacterDeath |
| 0x0029 | USE_COIN | HandleRaidAwareUseCoin |
| 0x002B | GET_ITEM | _dungeonHandler.Handle_ENUM_CMDPACKET_GET_ITEM |
| 0x002d | MOVE_MAP | _dungeonHandler.Handle_ENUM_CMDPACKET_MOVE_MAP |
| 0x002e | SET_PLAY_RESULT | HandleRaidAwareSetPlayResult |
| 0x002f | DROP_ITEM | _dungeonHandler.Handle_ENUM_CMDPACKET_DROP_ITEM |
| 0x0045 | SCORE_SCROLL_STATE | _dungeonHandler.Handle_CARD_START_REQUEST |
| 0x0047 | SELECT_CARD | _dungeonHandler.Handle_ENUM_CMDPACKET_SELECT_CARD |
| 0x0048 | EPLP_COMMAND | HandleRaidAwareDungeonExit(s, h, b, _dungeonHandler.Handle_ENUM_CMD... |
| 0x0075 | BOSS_DIE_CHECK | _dungeonHandler.Handle_BOSS_DIE_CHECK |
| 0x007b | CHARACTER_STATISTIC | HandleRaidAwareDungeonExit(s, h, b, _dungeonHandler.Handle_ENUM_CMD... |
| 0x008f | CHANGE_TUTORIAL_FLAG | _dungeonHandler.Handle_ENUM_CMDPACKET_CHANGE_TUTORIAL_FLAG |
| 0x009f | DEATH_TOWER_STAGE_CMD | _dungeonHandler.Handle_ENUM_CMDPACKET_DEATH_TOWER_STAGE_CMD |
| 0x00bf | DUNGEON_EVENT_STORY_PAUSE | _dungeonHandler.Handle_ENUM_CMDPACKET_DUNGEON_EVENT_STORY_PAUSE |
| 0x0128 | SECRET_SHOP_BUY_ITEM | _secretShopHandler.HandleBuyRequest |
| 0x0129 | SECRET_SHOP_OPEN_CLOSE | _secretShopHandler.HandleOpenClose |
| 0x013c | OPERATE_RIDABLE_OBJECT | _dungeonHandler.HandleDungeonMechanismCommand |
| 0x01de | INFORM_NOTICE | _dungeonHandler.HandleDungeonSceneUniqueIdReport |
| 0x01e4 | TUTORIAL_LEVEL_UP | _dungeonHandler.Handle_ENUM_CMDPACKET_TUTORIAL_LEVEL_UP |
| 0x0211 | SUMMON_MONSTER | _dungeonHandler.HandleDungeonMechanismCommand |
| 0x0253 | EVENT_NPC_DROP_ITEM_ | _dungeonHandler.HandleDungeonMechanismCommand |
| 0x026b | TIMER_MODIFY_INFO | _dungeonHandler.HandleDungeonMechanismCommand |
| 0x026d | SEA_CHASE_MINI_GAME_RESULT | _dungeonHandler.HandleDungeonMechanismCommand |
| 0x0270 | GAIN_EAT_OBJECT | _dungeonHandler.HandleDungeonMechanismCommand |
| 0x02E0 | REJOIN_DUNGEON | _dungeonRejoin.HandleRejoinAsync |
| 0x02E1 | CANCEL_REJOIN_DUNGEON | _dungeonRejoin.HandleCancelAsync |
| 0x0408 | BREAK_TRAP_RESULT | _dungeonHandler.HandleDungeonMechanismCommand |
| 0x0413 | VERY_DIFFICULT_HELL_PARTY | _dungeonHandler.Handle_ENUM_CMDPACKET_GORGEOUS_CHALLENGE_TOGGLE |

### 城镇/技能/任务(分册 06)

| CMD | 枚举名 | 处理入口 |
|---:|---|---|
| 0x001c | CHANGE_SKILLSLOT | _skillHandler.Handle_CHANGE_SKILLSLOT |
| 0x001d | BUY_SKILL | _skillHandler.Handle_BUY_SKILL |
| 0x0023 | SET_USER_POSITION | _townHandler.Handle_ENUM_CMDPACKET_SET_USER_POSITION |
| 0x002a | GIVEUP_GAME | _dungeonLoading.HandleGiveupGameAsync |
| 0x0084 | BACK_2_VILLAGE | HandleRaidAwareDungeonExit(s, h, b, _townHandler.Handle_ENUM_CMDPAC... |
| 0x00ed | TELEPORT | _townHandler.Handle_ENUM_CMDPACKET_TELEPORT |
| 0x0104 | CHANGE_ANOTHER_SKILL_TREE | _skillHandler.Handle_CHANGE_ANOTHER_SKILL_TREE |
| 0x014b | SKILL_COMMAND_CUSTOMIZING | _skillHandler.Handle_CHANGE_SKILL_COMMAND |
| 0x014c | SKILL_COMMAND_ALL_DEFAULT | _skillHandler.Handle_RESET_ALL_SKILL_COMMANDS |
| 0x01ec | SKILL_INIT | _skillHandler.Handle_SKILL_INIT |
| 0x01fd | COMBO_SKILL_INFO | _skillHandler.Handle_COMBO_SKILL_INFO |
| 0x01ff | COMBO_SKILL_EXTENSION_QUICK_SLOT_RESET | _skillHandler.Handle_COMBO_SKILL_EXTENSION_QUICK_SLOT_RESET |

### 组队/聊天/交易/好友/黑名单/公会(分册 07)

| CMD | 枚举名 | 处理入口 |
|---:|---|---|
| 0x0002 | SET_UDP_IP_PORT | _partyHandler.Handle_SET_UDP_IP_PORT + Pvp 回调 |
| 0x000c | SET_PARTY_INFO | _partyHandler.Handle_SET_PARTY_INFO |
| 0x000d | LEAVE_PARTY | _partyHandler.Handle_LEAVE_PARTY |
| 0x000e | WALKOUT_PARTY_MEMBER | _partyHandler.Handle_WALKOUT_PARTY_MEMBER |
| 0x0011 | SEND_MESSAGE | ChatHandler.Handle_SEND_MESSAGE |
| 0x0018 | SET_ITEMTRADE_STATE | _socialHandlers.Trade.State |
| 0x0031 | REPORT_BAD_P2P_USER | (空实现,收下即弃) |
| 0x0043 | GUILD_MEMER_LIST | _gsocialHandlers.GuildMembers.Handle |
| 0x0044 | CALL_GUILD_CREATE_RIGHT | _socialHandlers.GuildCreation.Handle |
| 0x0061 | PEER_CONNECT_RESULT | (空实现,收下即弃) |
| 0x0076 | REGISITER_TO_BLACKLIST | BlacklistHandler.Handle |
| 0x0077 | DELETE_TO_BLACKLIST | BlacklistHandler.Handle |
| 0x0078 | REQUEST_BLACKLIST | BlacklistHandler.Handle |
| 0x0079 | CHANGE_HOST | _partyHandler.Handle_CHANGE_HOST |
| 0x007e | SET_SUB_GUILD_MASTER | _socialHandlers.GuildManagement.Handle |
| 0x0099 | REQ_GUILD_SECEDE | _socialHandlers.GuildManagement.Handle |
| 0x009a | NOTIFY_MESSAGE_TO_GUILD | _socialHandlers.GuildManagement.Handle |
| 0x009c | CHECK_GUILD_NAME_DOUBLE | _socialHandlers.GuildCreation.Handle |
| 0x00a6 | CALL_PARTY_MEMBER_REALTIME_INFO | _partyHandler.Handle_CALL_PARTY_MEMBER_REALTIME_INFO |
| 0x00e0 | WRITE_GUILD_MEMBER_MEMO | _socialHandlers.GuildManagement.Handle |
| 0x0122 | ADD_UNITED_SERVER_FRIEND | UnitedFriendSystem.HandleAddUnitedServerFriend |
| 0x0123 | DELETE_UNITED_SERVER_FRIEND | UnitedFriendSystem.HandleDeleteUnitedServerFriend |
| 0x012f | BREAK_GUILD | _socialHandlers.GuildManagement.Handle |
| 0x0149 | ONE_TO_ONE_CHAT_STATE | ChatHandler.Handle_ONE_TO_ONE_CHAT_STATE |
| 0x0153 | REFRESH_GUILD_INFO | _socialHandlers.GuildJoin.Handle |
| 0x015c | REQUEST_JOIN_GUILD | _socialHandlers.GuildJoin.Handle |
| 0x015d | CANCEL_JOIN_GUILD | _socialHandlers.GuildManagement.Handle |
| 0x015e | APPROVE_JOIN_GUILD | _socialHandlers.GuildJoin.Handle |
| 0x015f | DENY_JOIN_GUILD | _socialHandlers.GuildManagement.Handle |
| 0x0160 | GUILD_JOIN_LIST | _socialHandlers.GuildJoin.Handle |
| 0x016d | JOIN_GUILD_INFO | _socialHandlers.GuildManagement.Handle |
| 0x01a3 | CREATE_GROUP | ChatHandler.Handle_CREATE_GROUP |
| 0x01a5 | LEAVE_FROM_GROUP | ChatHandler.Handle_LEAVE_FROM_GROUP |
| 0x01a9 | ITEM_HYPERLINK_MESSAGE | ChatHandler.Handle_ITEM_HYPERLINK_MESSAGE |
| 0x01df | P2P_STATISTICS | (空实现,收下即弃) |
| 0x02e2 | CHECK_GUILD_CREATE_PROMOTE_MSG | _socialHandlers.GuildCreation.Handle |
| 0x02e3 | MODIFY_GUILD_PROMOTE_MSG | _socialHandlers.GuildManagement.Handle |
| 0x02e4 | REQUEST_GUILD_CREATE_PERMIT | _socialHandlers.GuildCreation.Handle |
| 0x02e6 | CANCEL_GUILD_CREATE | _socialHandlers.GuildCreation.Handle |
| 0x02e8 | REQ_GUILD_SERCH_FOR_JOIN | _socialHandlers.GuildJoin.Handle |
| 0x0323 | REQUEST_USER_CHANNEL | UserChannelHandler.Handle |
| 0x03AE | P2P_HOLE_PUNCHING_SUCCESS_RATE | (空实现,收下即弃) |

### Raid/PVP(分册 08)

| CMD | 枚举名 | 处理入口 |
|---:|---|---|
| 0x0032 | MAKE_PVP_ROOM | PvpRoomHandler.HandleMakeRoom |
| 0x0033 | ENTER_PVP_ROOM | PvpRoomHandler.HandleEnterRoom |
| 0x0034 | SET_PVP_SEAT_STATE | PvpRoomHandler.HandleSetSeatState |
| 0x0035 | SET_PVP_READY_STATE | PvpRoomHandler.HandleSetReadyState |
| 0x0036 | SET_PVP_TEAM_MODE | PvpRoomHandler.HandleSetTeamMode |
| 0x0037 | DIE_PVP_CHARACTER | PvpRoomHandler.HandleDiePvpCharacter |
| 0x0038 | PVP_TIME_OUT | PvpRoomHandler.HandlePvpTimeOut |
| 0x0039 | END_PVP_RESULT | PvpRoomHandler.HandleEndPvpResult |
| 0x003a | RES_PVP_RANK | PvpRoomHandler.HandlePvpRankResponse |
| 0x0070 | PVP_REQUEST_FIGHT | PvpRoomHandler.HandlePvpRequestFight |
| 0x00c3 | PVP_CHANNEL_INFO | PvpChannelInfoHandler.HandlePvpChannelInfo |
| 0x012a | COMPLETE_LOAD_PVP | PvpRoomHandler.HandleCompleteLoadPvp |
| 0x012b | CONNECT_P2P_PVP | PvpRoomHandler.HandleConnectP2pPvp |
| 0x0293 | RAID_ENTRY_COST_INFO | _raidHandler.HandleEntryCostInfo |
| 0x0294 | RAID_MOVIE_SKIP | _raidHandler.HandleRaidMovieSkip |
| 0x0295 | SELECT_RAID_REWARD_CARD | _raidHandler.HandleSelectRaidRewardCard |
| 0x0296 | RAID_DO_BEHAVIOR | _raidHandler.HandleRaidDoBehavior |
| 0x0297 | RAID_SET_SYMBOL | _raidHandler.HandleRaidSetSymbol |
| 0x0299 | CREATE_RAID | _raidHandler.HandleCreateRaid |
| 0x029A | LEAVE_RAID | _raidHandler.HandleLeaveRaid |
| 0x029B | START_RAID | _raidHandler.HandleStartRaid |
| 0x029C | SET_RAID_WAITING | _raidHandler.HandleSetRaidWaiting |
| 0x029D | REJOIN_RAID | _raidHandler.HandleRejoinRaid |
| 0x029E | RAID_MANAGER_WORK | _raidHandler.HandleRaidManagerWork |
| 0x029F | MODIFY_RAID_INFO | _raidHandler.HandleModifyRaidInfo |
| 0x02B7 | RAID_BUFF_SYSTEM | _raidHandler.HandleRaidBuffSystem |
| 0x02B8 | RAID_MONSTER_HP | _raidHandler.HandleRaidMonsterHp |
| 0x0334 | RAID_RECENT_FRIEND_LIST | _raidHandler.HandleRaidWaitingListRequest |
| 0x033C | RAID_OTHER_CHANNEL_LIST | _raidHandler.HandleRaidOtherChannelList |
| 0x0363 | RAID_REQUEST_RAID_MEMBERS | _raidHandler.HandleRaidRequestMembers |
| 0x0364 | RAID_CHECK_RAID_USER | _raidHandler.HandleRaidJoinRequest |

### 商城/活动/邮箱/服务(分册 09)

| CMD | 枚举名 | 处理入口 |
|---:|---|---|
| 0x0040 | BUY_CERASHOP_ITEM | _ceraShopHandler.HandleCeraShopPurchase |
| 0x0041 | GEN_CERATICKET | _ceraShopHandler.HandleGenCeraTicket |
| 0x005e | MAILBOX_SEND | _mailboxHandler.HandleSendMailbox |
| 0x005f | MAILBOX_EXTRACT_ITEM | _mailboxHandler.HandleClaimMailbox |
| 0x0060 | MAILBOX_OPEN | _mailboxHandler.HandleOpenMailbox |
| 0x0086 | CHANGE_LETTER_STAT | _mailboxHandler.HandleChangeLetterStatMailbox |
| 0x00cf | SHOP_COIN_EVENT(0x00CF 字面量) | _shopCoinEventHandler.HandleShopCoinEvent |
| 0x013b | MULTI_MAILBOX_SEND | _mailboxHandler.HandleSendMultiMailbox |
| 0x0144 | QUERY_CHARAC_INFO_MAILBOX | _mailboxHandler.HandleQueryCharacterInfoMailbox |
| 0x01E6 | CRANE_START_USE | _craneMiniGameHandler.HandleStartUse |
| 0x01E7 | CRANE_PICKUP | _craneMiniGameHandler.HandlePickup |
| 0x025B | GET_EXPAND_EXP_GAGE_REWARD | _growthCapsuleHandler.HandleClaimAsync |
| 0x03BA | UPGRADE_CARRY_GOLD | _goldLimitHandler.HandleUpgradeAsync |
| 0x03E5 | SELECT_COLLECTBOX | _collectionBoxHandler.HandleQueryCollectionBox |
| 0x03E6 | ADD_COLLECTBOX_ITEM | _collectionBoxHandler.HandleInsertCollectBoxItem |
| 0x03E7 | REMOVE_COLLECTBOX_ITEM | _collectionBoxHandler.HandleRemoveCollectBoxItem |
| RENT_EQUIPMENT_ITEM=0x03cf | RentHandler.HandleRentWeapon | RentalHandler.cs:16 |

### 宠物/佣兵/专家副职(分册 10)

| CMD | 枚举名 | 处理入口 |
|---:|---|---|
| 0x002c | USE_STACKABLE | useStackable |
| 0x0064 | RENAME_CREATURE | _petCreatureHandler.HandleRenameCreature |
| 0x0066 | HATCH_CREATURE | _petCreatureHandler.HandleHatchCreatureEgg |
| 0x007a | CREATURE_SCRIPT_MESSAGE | _petCreatureHandler.HandleCreatureScriptMessage |
| 0x00ad | HATCH_CREATURE_EGG | _petCreatureHandler.HandleHatchCreatureEgg |
| 0x00ae | REQUEST_HATCHED_CREATURE | _petCreatureHandler.HandleRequestHatchedCreature |
| 0x00EB | REQUEST_DISJOINT_ITEM | HandleSharedDisjointOrHellParty |
| 0x00EC | REPAIR_DISJOINT_MACHINE | _expertJobStoreHandler.HandleRepair |
| 0x00EF | GIVEUP_EXPERT_JOB | _expertJobGiveupHandler.Handle |
| 0x00F0 | UPGRADE_DISJOINT_MACHINE | _expertJobStoreHandler.HandleUpgrade |
| 0x01A0 | EXPERT_EXTRACTION | _expertJobExtractionHandler.Handle |
| 0x01B9 | MERCENARY_RETURN | _mercenaryExpeditionHandler.HandleReturn |
| 0x01BA | MERCENARY_INFO | _mercenaryExpeditionHandler.HandleInfo |
| 0x01BB | MERCENARY_COMPETITION | _mercenaryExpeditionHandler.HandleCompetition |
| 0x01e0 | VERIFY_CREATURE_QUEST | _petCreatureHandler.HandleVerifyCreatureQuest |
| 0x01E5 | REQUEST_CHARAC_SKILL_INFO | _mercenaryHandler.HandleMercenaryRequest |
| 0x01E8 | SELECT_STRIKER | _mercenaryHandler.HandleMercenaryRequest |
| 0x0204 | USE_STACKABLE_ACTION | useStackable |
| 0x0257 | CREATE_EXPERT_JOB_STORE | _expertJobStoreHandler.HandleCreate |
| 0x0258 | ENTER_EXPERT_JOB_STORE | _expertJobStoreHandler.HandleEnter |
| 0x0259 | CLOSE_EXPERT_JOB_STORE | _expertJobStoreHandler.HandleClose |
| 0x025A | USE_ENCHANT_STORE | _expertJobStoreHandler.HandleEnchant |
| 0x0264 | REPAIR_EXPERT_JOB_STORE | _enchanterHandler.HandleRepair |

## 5. 分册导航

| 分册 | 域 |
|---|---|
| [servers4a21-01-login-character.md](servers4a21-01-login-character.md) | 频道层/登录/选角/角色/设置/体力/初始化序列 |
| [servers4a21-02-inventory-base.md](servers4a21-02-inventory-base.md) | 背包基础操作/商店买卖/金库仓库/消耗品 |
| [servers4a21-03-item-enchant-avatar.md](servers4a21-03-item-enchant-avatar.md) | 强化/合成/分解/随机选项/徽章/时装/卡片/称号/锁 |
| [servers4a21-04-dungeon-flow.md](servers4a21-04-dungeon-flow.md) | 副本主流程(入场→推进→结算→翻牌→重连) |
| [servers4a21-05-dungeon-special.md](servers4a21-05-dungeon-special.md) | 特殊副本(死亡塔/地狱派对/许可副本/神秘商店/机制) |
| [servers4a21-06-town-skill-quest.md](servers4a21-06-town-skill-quest.md) | 城镇同步/技能/任务 |
| [servers4a21-07-social-party-chat-trade-friend-guild.md](servers4a21-07-social-party-chat-trade-friend-guild.md) | 组队/聊天/双人交易/好友/黑名单/公会 |
| [servers4a21-08-raid-pvp.md](servers4a21-08-raid-pvp.md) | 团本 Raid / PVP |
| [servers4a21-09-shop-event-mail-service.md](servers4a21-09-shop-event-mail-service.md) | 商城代币/活动/彩票/邮箱/收集箱/成长服务 |
| [servers4a21-10-pet-mercenary-expertjob.md](servers4a21-10-pet-mercenary-expertjob.md) | 宠物生物/佣兵/专家副职 |
