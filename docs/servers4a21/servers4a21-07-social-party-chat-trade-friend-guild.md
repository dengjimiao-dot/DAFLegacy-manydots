# ServerS4A21(86 级版本)已实现协议处理参考 —— 组队 / 聊天 / 双人交易 / 好友·黑名单 / 公会 / 频道查询

> - **本册导航**:总览/帧结构/信封/分发架构见 [servers4a21-00-overview.md](servers4a21-00-overview.md);全部 CMD 注册总表也在总览。
> - 仅写 A21(`ServerS4A21\Server\DfoServer`)侧实现,供 DFLegacy 回填参考。路径相对 `DfoServer` 根。
> 帧前提:客户端→服务端 14B 头(cmd u8 恒 1 + type u16 + length u32 + checksum u32 + seq u16 + extra u8,`Network/Core/Packet.cs:67`),body 从第一个载荷字段开始;服务端→客户端 15B 信封 `GamePacketEnvelopeBuilder.Build(type,body)`,type=0x01 应答、0x00 推送。dstr = u32 长度 + GBK 字节(`WriteClientDstr`/`WriteRawDstr`)。
> 所有协议由 `Network/Protocol/GameProtocolHandler.cs` 的 `GameCommandRegistry` 按 type 分发:每包先过 `CharacterSessionLifecycleCoordinator.CanDispatch`,再对 cmd==1 的包先调 `Trade.CancelBeforeTransition` 后查表(`GameProtocolHandler.cs:339-346`),查不到仅记 Unhandled 日志。

## 域综述

- **组队**:城镇与副本共享同一 `Game.Party.PartyManager` 实例(`GameProtocolHandler.cs:64-66`),副本入场 cohort 与跟随退出都看到同一份队伍状态;队伍上限 4 人(`Game/Party/Party.cs:9-12`),成员槽位 0..3 加入序分配、离队不重排(`Game/Party/Party.cs:28-29`)。所有广播基于 `GetPartySnapshot` 返回的**不可变代际快照**,以 per-party 广播闸门(`AcquireBroadcastGateAsync`,`Network/Handlers/PartyHandler.cs:2688`)串行化,推送相位固定为:用户上下文 → 实时信息(0x99)→ P2P 端点(0x0B)→ 花名册(0x09 type0)→ 端点重放 → 实时信息重放(`PartyHandler.cs:2467-2570`)。
- **聊天**:`ChatHandler`(三个 partial 文件)是"请求解析 + 收件人路由 + 谓词发送"模型:`ResolveRecipients` 按 mode 决定广播范围,每人一个带身份/黑名单谓词的 `TrySendPacketAsync`(`Network/Handlers/ChatHandler.cs:89-124`);同一频道的判定是会话 `ListenerPort` 相等(`ChatHandler.cs:495-499`)。mode 6(公会)与 43(1:1 会话)走专用持久化通道,不进通用路由(`ChatHandler.cs:79-88`)。
- **双人交易**:`ItemTradeHandler` 以 SessionId→Binding 字典维护两端绑定,五态谈判状态机(0 可改 →5 登记锁定 →1 确认 →3 最终确认→结算)收在 `ItemTradeSession`,资产仍归 `InventoryLease`(`Game/Inventory/Services/ItemTradeSession.cs:9-24`);最终交换由 `InventoryExchangeCommitService.TryCommit` 在**单 SQLite 事务**内原子完成,失败用事前加载的双方基线快照整体回滚(`Game/Inventory/Services/InventoryExchangeCommitService.cs:59-97`)。入口三处劫持:REQUEST/RESPONSE_PEER 按 `body[2]==1` 分流(`GameProtocolHandler.cs:389-394`)、MOVE_ITEMSPACE 先问交易(`GameProtocolHandler.cs:465-468`)。
- **好友聚合**:`UnitedFriendSystem` 静态单例持有"角色名→好友名集合"单向关系图(SQLite `united_friend_relations` 持久化,`Game/Friends/UnitedFriendSystem.cs:30-35,110-128`);ADD/DELETE 两个 CMD 之外,上下线/状态/升级/删除/改名全部通过静态 hook 聚合推送(重推 `UNITED_SERVER_FRIEND_INFO` subcmd=0 全量列表),在线图标由同频道 `USERINFO`/`USER_LEAVE` 场景实体驱动(`UnitedFriendSystem.cs:15-26`)。
- **黑名单**:`BlacklistRepository`(按 characterId 持有关系,容量 64)+ `BlacklistProjection`(客户端已发布名字投影)双层;拦截不靠主动校验,而是作为**发送谓词**嵌进聊天、组队邀请、1:1 会话、交易资格各处(`Network/Handlers/ChatHandler.cs:37`、`Network/Handlers/ItemTradeHandler.cs:54-55` 等)。
- **公会**:四组 CMD 全部汇入 `Guild*Handler.Handle(session,header,body)` 统一入口后按 `header.type` switch 分派(`Network/Handlers/GuildJoinHandler.cs:31-81` 等);改动提交后由 `GuildStatePublisher.RefreshAsync` 对每个在线受影响者在自己的 transition gate 内**重读持久层**再推送 Rank/GUILD_INFO/成员表,绝不重放旧快照(`Network/Handlers/GuildStatePublisher.cs:12-51`)。
- **并发纪律(全域通用)**:涉及玩家状态的推送/变更一律先捕获身份快照(cid/uid/名字),经 `CharacterTransitionCoordinator.RunIfCurrent/RunIfBothCurrent` 执行,发送前谓词复核——防止换角、顶号、断线后旧包落在新角色上(如 `Network/Handlers/BlacklistHandler.cs:34-41`、`ChatHandler.cs:108-111`)。

---

## 一、组队(CMD)

组队组注册:`Network/Protocol/GameProtocolHandler.cs:383-403`(party-chat 组,与 ChatHandler 共组)。

### CMD 0x000C (12) `SET_PARTY_INFO` — 创建/修改队伍
- **请求**:两种实机形态归一化(`Network/Parsers/Party/SetPartyInfoRequest.cs:19-56`):创建=12B 直设块(`[1]=TitleIndex, [2]=UserMax, 其余 opaque`);编辑=`[0] 设置字节(≤2) + [1]=0 + u32 名长 + 标题原文 + 10B 尾块`,归一为同一 12B settings 块(`req.Raw`)。
- **应答/推送**:解析/人数非法 → 应答 `[0x00,0x04]`(`PartyHandler.cs:792-807`);新建成功 → 推 NOTI 9 type0 全量(`PartyHandler.cs:870-879`)+ NOTI 153 实时信息;已存在 → 仅广播 NOTI 9 type1(设置刷新,`BroadcastPartySettings`,`PartyHandler.cs:2238-2277`)。
- **行为**:`UserMax` 必须 1..4(`PartyConstants.IsSupportedCapacity`,`Game/Party/Party.cs:11-12`);`PartyManager.SetPartyInfo` 校验 12B 块 `[2]==UserMax`,无队则先 `CreateParty` 再 `UpdateSettings`,全程持 `_lock`(`Game/Party/PartyManager.cs:54-108`);入队 cohort 的 TransitionGate 串行(`RunWithEntryPartyTransitionGatesAsync`,`PartyHandler.cs:726`);raid 花名册提交时强制 userMax=4 并覆写 settings 块(`PartyManager.cs:70-93`);若队长原本在别的队伍,先给原队发清屏+名册刷新(`NotifyPriorPartyAsync`,`PartyHandler.cs:433-464`)。
- **备注**:标题为空时用队长名(`PartyHandler.cs:822-826`)。

### CMD 0x000D (13) `LEAVE_PARTY` — 退队
- **请求**:无 body(`PartyHandler.cs:911`)。
- **应答/推送**:不在队 → 应答 `[0x00,0x12]`(`PartyHandler.cs:935`);成功 → 本人 NOTI 9 type3 清空组队窗口(客户端不自清,`PartyHandler.cs:942-946`),剩余成员经 `PublishCommittedDepartureAsync` 重发 NOTI 9 type0。
- **行为**:副本入场 TransitionGate 串行(`PartyHandler.cs:894-908`);退队者若仍在副本实例内则清 run + 拉回城镇(`PullBackToTownIfInDungeonAsync`,`PartyHandler.cs:1065`);副本放弃路径走 `HandleDungeonGiveupWithinTransitionAsync`,最后一名成员保留为单人队(`PartyHandler.cs:979-1040`);无论 socket/回城成败,已提交的队伍代际必须发布(`PartyHandler.cs:967-971`)。

### CMD 0x000E (14) `WALKOUT_PARTY_MEMBER` — 踢人
- **请求**:`[0] = 目标槽位 SlotIndex`(1B,`PartyHandler.cs:1216,1253-1267`)。
- **应答/推送**:目标不存在/会话不匹配/非队长失败 → 应答 `[0x00,0x13]`(`PartyHandler.cs:1268-1324`);成功 → 被踢者收 NOTI 9 type3 清屏 + 副本内拉回城,剩余成员广播 NOTI 9 type0;解散时踢人者本人也收清屏(`PartyHandler.cs:1329-1365`)。
- **行为**:仅 `_partyManager.Kick(uid, sessionId, target.UserId, target.SessionId)` 在双方 gate 内提交(`RunCurrentPartyPairMutationAsync`,`PartyHandler.cs:671`);目标 gate 先取再等,防止与被踢者入场竞态(`PartyHandler.cs:1222-1242`)。
- **备注**:队长权限校验在 `PartyManager.Kick` 内部(`Game/Party/PartyManager.cs:765-835`)。

### CMD 0x0079 (121) `CHANGE_HOST` — 委托队长
- **请求**:`[0] = 目标成员槽位`(1B;A21 客户端与 NOTI 0x001A 消费端都把一字节当 host 槽,`PartyHandler.cs:1370-1372`)。
- **应答/推送**:无专用应答;成功后 `PublishPartyHostProjectionAsync` 重投影 + 全队刷新,`PublishTownPartyListsAsync` 刷新城镇招募列表(`PartyHandler.cs:1450-1462`)。
- **行为**:仅当前队长且会话匹配可委托;按 `SlotIndex` 匹配(不是 list 下标);空槽/委托给自己忽略;`TransferLeader` 只切 leader/host 不重排槽位(`PartyHandler.cs:1397-1461`,`Game/Party/PartyManager.cs:888-949`)。

### CMD 0x00A6 (166) `CALL_PARTY_MEMBER_REALTIME_INFO` — 成员实时信息(HP% 轮询)
- **请求**:不解析 body(`PartyHandler.cs:1207-1210`)。
- **应答/推送**:NOTI 153 `PARTY_MEMBER_REALTIME_INFO`:`[memberCount u8] + 每成员 { uid u16, hpPercent u8, isHelpAbuseParty u8, slot u8 }`;hpPercent 恒 100(`Network/Builders/Party/PartyRealtimeInfoBuilder.cs:8-24`)。
- **行为**:不在队不回包(对照 df check_error,`PartyHandler.cs:1211-1212`)。

### CMD 0x000A (10) `REQUEST_PEER` / CMD 0x000B (11) `RESPONSE_PEER` — 组队邀请分支(body[2]==0)
- **注册分流**:`body[2]==1` 走交易(`Network/Parsers/Inventory/ItemTradeRequest.cs:8`),否则进组队(`GameProtocolHandler.cs:389-394`)。
- **请求**:REQUEST=`[0..1] targetUid u16 + [2] type + [3..6] peerInt i32`(type 0=组队、2=PvP 房、10=raid;`PartyHandler.cs:1489-1497`);RESPONSE=`[0..1] inviterUid u16 + [2] type`,type0 接受为精确 7B、拒绝为同前缀 + u16 0x0085 的 9B(`IsAcceptedTypeZeroPeerResponse`,`PartyHandler.cs:1633-1636`)。
- **应答/推送**:邀请转发 NOTI 7 给目标:`inviterUid u16 + 0 + peerInt i32 + 3×u16 0`(11B 弹框,`PartyHandler.cs:1589-1615`);跨区域附加上下文包(`BuildCrossAreaPartyInviteContextPacket`,`PartyHandler.cs:2101`);接受成功后相位:NOTI 9 全量 → NOTI 8 回执给邀请者(`accepterUid u16 + 0 + inviterUid i32`,7B,让 A 客户端认领队伍单例,`PartyHandler.cs:1884-1916`)。
- **行为**:同频道校验(`IsSameGameChannel`,`PartyHandler.cs:1506-1512`);双方黑名单谓词 `CanInvite`(`PartyHandler.cs:1516-1521`);邀请登记 `PartyManager.RecordInvite` 持锁复验双方 SessionId/满员/队长身份,接受时 `AcceptInvite` 同锁匹配 pending 并按登记时 PartyId 复验(`Game/Party/PartyManager.cs:1053-1128,1134`);副本中(`CurrentRun!=null`)接受直接取消邀请(`PartyHandler.cs:1803-1814`);type0 拒绝/异常形态只消费 pending 不进 mutation(`PartyHandler.cs:1731-1758`)。

### CMD 0x0002 (2) `SET_UDP_IP_PORT` — UDP 端点上报
- **请求**(udp-endpoint 组,`GameProtocolHandler.cs:246-249`):`nat u8 + 内网 IPv4 4B + 外网 IPv4 4B + port u16 LE + mtu u32 LE`(最少 15B;后随身份字节忽略),校验 IPv4 可用类/端口非 0/MTU 区间(`Network/Parsers/Party/SetUdpEndpointRequest.cs:24-115`)。
- **应答/推送**:无应答;更新 `Player.ReportedUdpEndpoint` 后若该玩家在队则整队广播(带 NOTI 0x0B 端点表,`PartyHandler.cs:329-370`)。
- **行为**:分发包装 `HandleSetUdpEndpoint` 在之后触发 `PvpRoomHandler.HandleReportedUdpEndpointChanged`(PvP 房同步刷新,`GameProtocolHandler.cs:432-442`);成员 UDP 端口刷新走 `TryUpdateMemberP2pPort`,relay 房仅自端点刷新不轮换(`PartyHandler.cs:352-363`)。

### P2P 上报类(静默吞掉,合并条)
- **CMD 0x03AE (942) `P2P_HOLE_PUNCHING_SUCCESS_RATE`、CMD 0x0061 (97) `PEER_CONNECT_RESULT`、CMD 0x0031 (49) `REPORT_BAD_P2P_USER`、CMD 0x01DF (479) `P2P_STATISTICS`**:注册为空任务,收下即忽略、不回包不转发,只为消掉 Unhandled 日志(`GameProtocolHandler.cs:398-402`)。

---

## 二、组队推送面(NOTI 概览)

| 推送 | 布局要点 | 构造处 | 触发时机 |
|---|---|---|---|
| NOTI 9 `PARTY_INFO` | `u16 块数` + 每块 `partyId u16 + type u8`;type 0/1=信息块(12B settings,info0==0 时补空 dstr)+ 8 槽 `{uid u16,0,0,0}` + 3 尾字节(尾 2=队长槽);type 3=清屏块;`BuildList` 支持多块+移除块 | `Network/Builders/Party/PartyInfoNotiBuilder.cs:14-110` | type0 名册:建队/入队/踢人/断线广播(相位见域综述);type1 设置;type3 清屏(退队/被踢/换队);城镇招募列表(`PublishTownPartyListsAsync`,`PartyHandler.cs:1936`) |
| NOTI 153 `PARTY_MEMBER_REALTIME_INFO` | 见 CMD 0x00A6 | `Network/Builders/Party/PartyRealtimeInfoBuilder.cs:8-24` | 建队回执(`PartyHandler.cs:876-879`)、0xA6 轮询、广播相位首尾(`PartyHandler.cs:2472-2488,2554-2570`) |
| NOTI 11 `USER_UDP_IP_PORT` | `[count u8] + 每成员 22B:uid u16 + inner 4B + outer 4B + port u16(大端) + accId u32 + nat u8 + mtu u32 + char_attr u8`(86jp 客户端 sub_D31160 读序);relay 模式逐收件人定制(自己=直连端点,他人=relay IP+中继端口) | `Network/Builders/Party/PartyIpInfoBuilder.cs:14-106` | 广播相位 endpoints-before/after-roster(`PartyHandler.cs:2491-2509,2535-2552`);直连/中继二选一,中继矩阵不全则整队回退直连名册(`PartyHandler.cs:2347-2365`) |
| NOTI 8 `RESPONSE_PEER` | 接受回执 `accepterUid u16 + 0 + inviterUid i32`(7B) | `PartyHandler.cs:1884-1893` | 组队接受成功,在 0x99 之后、0x09 之前发给邀请者 |
| NOTI 7 `REQUEST_PEER` | 邀请弹框 `inviterUid u16 + 0 + peerInt i32 + 3×u16 0` | `PartyHandler.cs:1590-1613`;交易邀请 `Network/Builders/Inventory/ItemTradePacketBuilder.cs:11-16` | 组队邀请/PvP/交易协商入口转发 |
| NOTI 73 `REQUEST_MEMBER_ENTER_TO_RESPONSER` / NOTI 74 `MEMBER_ENTER_OK` | 师徒邀请弹框 `u16 X(推断=发起者 uid) + dstr 名`;成功 `a,b,c 三字节 + dstr 对方名`——两包字节语义标注"待真机验证" | `Network/Builders/Party/MemberEnterInviteBuilder.cs:9-18`、`MemberEnterOkBuilder.cs:9-21` | 预留 builder,当前代码库无调用点 |
| NOTI 0x0184(枚举表名 `TOURNAMENT_MAP_INFO`) | `DungeonRoomP2pBuilder`:8B 头(f1,f2,u32 room,globalFlag,mode)+ `roomId u16 + count1 u8(+14B×n 格位)+ count2 u8(+18B×n 端点)+ trailer`;多字段标注"待真机验证" | `Network/Builders/Party/DungeonRoomP2pBuilder.cs:16-52` | 预留 builder,当前代码库无调用点(副本内 P2P 端点表,与城镇 0x0B 不同 wire) |

---

## 三、聊天(ChatHandler.RegisterHandlers,`Network/Handlers/ChatHandler.Conversations.cs:10-17`)

### CMD 0x0011 (17) `SEND_MESSAGE` — 聊天
- **请求**:`mode u8 + targetUid u16 + targetCid u32 + dstr 消息(1..256B,禁 \0)`;私聊 mode(1/7/43)尾部可再跟 `dstr 目标名`(≤30B,mode43 ≤255B 且必须带名+服务器字节,总长自洽)(`TryParseRequest`,`Network/Handlers/ChatHandler.cs:517-595`)。
- **应答/推送**:无应答;按 mode 推 NOTI 12 `MESSAGE`:`mode u8 + senderUid u16 + serverGroup u8(0) + dstr 消息`(`BuildNotificationBody`,`ChatHandler.cs:597-609`)。
- **行为(路由表 `ResolveRecipients`,`ChatHandler.cs:126-188`)**:

| mode | 语义 | 路由 |
|---|---|---|
| 1 / 7 | 私聊/备用私聊 | 按 targetCid→targetUid→名字三级定位(`FindDirectTarget`,`ChatHandler.cs:289-319`),不在线丢弃 |
| 2 / 副本中 | 队伍 | `PartyManager.GetPartyByUser` 全队(同频道,`ChatHandler.cs:149-169`) |
| 3 | 区域 | 同城镇同区域会话(`GetSessionsInArea`,`ChatHandler.cs:171-181`) |
| 6 | 公会 | 专用路径 `SendGuildMessageAsync`,NOTI 65 `MESSAGE_OTHER_CHANNEL`:`6 + 0 + dstr 发送者名 + server 字节 + dstr 文本`(全频道成员含副本内,`ChatHandler.cs:190-225`) |
| 43 | 1:1 会话 | 专用路径 `SendConversationMessageAsync`,NOTI 0x016F(367) `MESSAGE_GROUP_CHAT`:`conversationId u32 + dstr 发送者名 + dstr 文本`,只发对方(见下) |
| 52 / 53 | raid 聊天(53 仅队长,`ChatHandler.cs:227-230`) | raid 成员,先补发编队上下文包再转发(`ChatHandler.cs:232-287`) |
| 其他 | 未知 mode | 仅发件人自身,刻意不做成跨频道广播(`ChatHandler.cs:183-187`) |

- 每个收件包带四重发送谓词:双方身份未变 + transition current + **黑名单未拦截**(`IsBlocked(recipient,sender)`,`ChatHandler.cs:37,108-111`);`AddIfCurrentChannel` 以 ListenerPort 过滤跨频道(`ChatHandler.cs:495-499`)。

### CMD 0x01A9 (425) `ITEM_HYPERLINK_MESSAGE` — 装备超链接聊天
- **请求**:头部与 SEND_MESSAGE 同构;文本之后到包尾为装备 tooltip 二进制块(原样转发不解析,总长 ≤1024B);私聊 mode1/7 在文本后先剥 `dstr 目标名(≤30B) + 可选 1B 私聊标志(0x01)`,数据块固定以 `01 FF 00 FF FF` 开头(`TryParseHyperlinkRequest`,`Network/Handlers/ChatHandler.Hyperlink.cs:86-156`)。
- **应答/推送**:普通/raid → NOTI 0x0173(371) `MESSAGE_HYPER_LINK`(`mode+uid+server+dstr 文本+数据块原文`);公会 mode6 → NOTI 0x0174(372) `MESSAGE_OTHER_CHANNEL_HYPER_LINK`(同构追加数据块);后两者布局代码自注"按同构推断,待实机验证"(`ChatHandler.Hyperlink.cs:230-260`)。
- **行为**:mode6 复用 SEND_MESSAGE 的成员遍历/黑名单/owned-lease 校验(`ChatHandler.Hyperlink.cs:161-193`)。

### CMD 0x01A3 (419) `CREATE_GROUP` / CMD 0x01A5 (421) `LEAVE_FROM_GROUP` / CMD 0x0149 (329) `ONE_TO_ONE_CHAT_STATE` — 1:1 会话(私聊窗口)
- **请求**:CREATE=`dstr 对方名(1..255B)`;LEAVE=`conversationId u32`;ONE_TO_ONE_CHAT_STATE=空实现(`ChatHandler.cs:375-381`)。
- **应答/推送**:CREATE 成功推 NOTI 0x016A(362) `CREATE_GROUP`:`0 + conversationId u32 + 2 + dstr 己名 + dstr 对方名`,双方各一份(幂等,`Published` 标志防重发,`ChatHandler.cs:383-399`);对方拉黑或被拉黑 → 推 `[77]` 错误(`ChatHandler.cs:352-357`)。LEAVE 推 NOTI 0x016E(366) `LEAVE_USER_FROM_GROUP`:`conversationId u32 + dstr 退出者名` 给对方(`ChatHandler.Conversations.cs:57-66`)。
- **行为**:会话表 `_activeConversations` 以 (min,max) 双方 cid 为键、服务端分配自增 id,锁内创建/查找(`ChatHandler.cs:425-442`);收发消息前 `IsActiveConversation` 复核会话仍存活且**双向未拉黑**(`ChatHandler.Conversations.cs:68-75`);断线钩子移除相关会话并通知对端(`ChatHandler.cs:410-423`)。组队邀请不走此组(0x01A3 属会话流,真组队邀请用 0x000A/0x000B,`PartyHandler.cs:1465-1469`)。

---

## 四、双人交易(本域重点)

入口注册:`GameProtocolHandler.cs:389-395`(REQUEST/RESPONSE 按 `body[2]==1` 分流,`Network/Parsers/Inventory/ItemTradeRequest.cs:8`)+ MOVE_ITEMSPACE 劫持 `GameProtocolHandler.cs:465-474`。处理类 `Network/Handlers/ItemTradeHandler.cs`,谈判状态 `Game/Inventory/Services/ItemTradeSession.cs`,结算 `Game/Inventory/Services/InventoryExchangeCommitService.cs`,组包 `Network/Builders/Inventory/ItemTradePacketBuilder.cs`。

### 资格谓词与绑定模型
- `Eligible(a,b)`:不同会话/角色、**同频道**(ListenerPort 相等)、双方 uid≠0、`UserState==0`、城镇就绪、无副本选择/无进行中副本、双向无黑名单(`ItemTradeHandler.cs:47-55`)。
- 绑定 `Binding{Sessions[2], Uids[2], Trade}` 存 SessionId→Binding 字典;每次操作前 `Current(b,i)` 复核:角色/uid 未换 + 会话目录仍指向该会话 + `InventoryLease` 仍被该会话持有(`ItemTradeHandler.cs:36-43`)。
- 邀请 1 分钟过期(`InvitationDeadline`,`ItemTradeSession.cs:20`);每次新请求前先 `Expire` 双方陈旧绑定(`ItemTradeHandler.cs:75-76`)。

### CMD 0x000A (10) `REQUEST_PEER` — 交易邀请(body[2]==1)
- **请求**:`targetUid u16 + type u8(=1) + peerInt i32`,严格 7/8/16B 且尾零(`ItemTradeRequest.TryPeer`,`ItemTradeRequest.cs:10-23`)。
- **应答/推送**:目标不在线 → 应答 `[0,3,1]`(`ItemTradeHandler.cs:72`);不合格/任一方已在别的绑定 → `[0,19,1]`(`ItemTradeHandler.cs:78,89`);成功 → 发起者收 CMD 应答 `[1]`,目标收 NOTI 7 邀请弹框 `uid u16 + 1 + peer i32 + 0 i32`(`ItemTradePacketBuilder.Invite`,`ItemTradePacketBuilder.cs:11-16`)。
- **行为**:双方 gate 内建绑定,字典锁保证一人只进一个绑定(`ItemTradeHandler.cs:80-89`);同账号不可交易(`a.AccountId==b.AccountId` 拒绝,`ItemTradeHandler.cs:77`)。

### CMD 0x000B (11) `RESPONSE_PEER` — 交易应答
- **请求**:同前缀;拒绝形态为同前缀 + u16 0x0085(9/16B 尾零,`ItemTradeRequest.cs:17-22`)。
- **应答/推送**:拒绝 → 发起者收 CMD 应答 `[0,0x85,1]`,绑定拆除(`ItemTradeHandler.cs:104-108`);接受 → 应答方收 CMD `RESPONSE_PEER` ack(`[1, uid u16, 1, 1]`),发起者收 NOTI 8 `RESPONSE_PEER`(`uid u16 + 1 + peer i32 + 1 + 0 i32`,使客户端打开交易窗口,`ItemTradePacketBuilder.Accepted`,`ItemTradePacketBuilder.cs:18-28`)。
- **行为**:仅应答方(index 1)可响应且 uid 必须匹配发起者(`ItemTradeHandler.cs:100`);`Trade.Accept` 要求未激活、1 分钟内(`ItemTradeSession.cs:30-40`)。

### CMD 0x0013 (19) `MOVE_ITEMSPACE` — 交易窗物品放取(劫持)
- **劫持判定** `IsTradeMove`:源列表或目的列表为 4(交易窗)(`ItemTradeRequest.cs:34`);分发层先问交易再问死亡塔/骑士盾/普通移动物品栏(`GameProtocolHandler.cs:465-474`)。
- **请求布局** `TryMove`(实机 28B,兼容 32B 采样):`srcList u8 + srcSlot i16 + srcInstance i32 + count i32 + dstList u8 + dstSlot i16 + dstInstance i32`(自 `[28]` 起必须全零);目的字段被忽略——以服务端分配为准(`ItemTradeRequest.cs:36-64`)。
- **应答/推送**:无绑定却发交易移动 → 普通 MOVE 错误 ack;非法请求/放置被拒 → `MoveItemSpaceAckBuilder.BuildError(InvalidOperationErrorCode, srcList, dstList)`(`ItemTradeHandler.cs:121-128,144`);成功 → 放置者收 CMD MOVE 应答 11B `[1, srcList, srcSlot i16, count u32, dstList, dstSlot i16]`,对端收 NOTI 15 `CHANGE_ITEMTRADE_ITEM`(`ItemTradeHandler.cs:145-153`)。
- **行为(服务端权威格位)**:放入 `Offer`:仅状态 0 且对方未确认(状态≠1)时可改;`srcSlot==0 && itemId==0` 解释为**金币**(slot0 映射,上限=主背包虚拟计数 0 余额−已放金币);普通物品按源槽快照校验 itemId/可交易性/不重复源槽,落入本方交易窗 **slot 3..11**(共 9 格),可堆叠物投影 count=放入数(`ItemTradeSession.cs:42-78`);取回 `Withdraw`:金币直接扣减;物品按 tradeSlot 找回源槽,当前实例字节必须与放入快照一致,支持部分取回(`ItemTradeSession.cs:82-108`);金币条目 NOTI 15 用虚拟计数 84 式编码(`WriteVirtualCountEntry84(w,0,0,gold)`,`ItemTradePacketBuilder.cs:30-40`)。晶体仓(列表 36)拖入仅允许真实水晶槽且 itemId↔slot 双向可解(`ItemTradeRequest.cs:56-62`)。

### CMD 0x0018 (24) `SET_ITEMTRADE_STATE` — 状态推进(五态状态机)
- **请求**:1/4/8B 且 `[0]=state≤5` 尾零(`ItemTradeRequest.TryState`,`ItemTradeRequest.cs:25-32`);服务端只接受 **5(登记锁定)→ 1(确认)→ 3(最终确认)**(`ItemTradeHandler.cs:167`)。
- **应答/推送**:state≠3 时双向广播 NOTI 17 `STATE_ITEMTRADE`:`uid u16 + state u8`(`ItemTradePacketBuilder.cs:42-46`);双方都到 5 → 双向推 NOTI 0x028F(655) `ITEMTRADE_REG_ITEM_FINISH`(空 body,`ItemTradePacketBuilder.cs:48-49`);双方都到 3 → 结算后走统一 Close 推送(见下)。
- **行为(状态机 `ItemTradeSession.Advance`,`ItemTradeSession.cs:119-151`)**:5 仅从 0 进入;1 仅从 5(对方 5 或 1);3 仅从 1(对方 1 或 3);任一不满足 Rejected(静默);进入 1/3 后 `Offer/Withdraw` 一律拒绝(`ItemTradeSession.cs:48,87`);**先置 `Closed=true` 再结算**,防结算期间再进出物品(`ItemTradeSession.cs:143`)。

### 提交原子性(InventoryExchangeCommitService.TryCommit)
- 前置:双方同库不同角色不同账号、各 ≤9 项、金币非负(`InventoryExchangeCommitService.cs:39-44`);按 characterId 升序拿双方 lease 锁(`:45-48`);终验:金币≤余额、每项 `ReadSource` 现值与快照 `ToBytes()` 全等、`CanOffer` 复验(`Validate`,`:117-130`)。
- `CanOffer`:禁带锁(EquipmentLockId)、禁 TradeRestriction、禁时装/宠物/EPIC 片;可堆叠 count≤持有数否则必须 =1;已过期拒绝;再过 `MailboxSendPolicy.ValidateTransferability`(`:15-33`)。
- 事务:先 `SaveDirty` 保留既有脏改动 → 开连接加载**双方基线快照** → 单事务内做金币携带上限校验(`CharacterGoldLimitRepository`)→ 双方 Remove(普通槽按槽消耗,水晶槽走虚拟计数)→ 交叉 Insert(水晶槽严格守恒;`IsTradeLimitItem` 扣 `StackTradeCount`;可堆叠合并前逐实例字节比对防丢绑定/时效)→ 双方 SetMainVirtualCount 写金币 → 事务提交并清脏(`:56-84`)。
- 失败回滚:任何一步抛异常则用提交前基线 `InventoryContext.TryReplaceCurrentLease` 整体替换双方内存态(`:86-97`)→ 状态机返回 Canceled。

### 断线 / 切场景强制回滚;提交成功补发
- **切场景**:`OnPacketReceived_86JP` 对每个 cmd==1 包先调 `Trade.CancelBeforeTransition`,命中 `SELECT_CHARACTER(4)/RETURN_SELECT_CHARACTER(7)/ENTER_SELECT_DUNGEON(15)/SET_USER_AREA(36)/TELEPORT(0x00ED)/SOLO_TELEPOART(0x01D7)/PARTY_TELEPORT(0x01B3)/PARTY_TELEPORT_CONFIRM(0x01B4)/ENTER_PVP_ROOM(0x0033)` 即关闭绑定(`GameProtocolHandler.cs:341`、`ItemTradeHandler.cs:195-210`)。
- **断线**:SessionEnding 钩子 `Ending` → `Close(b,false)`(`ItemTradeHandler.cs:268-272`);Close 内以 `b.Trade.Committed` 为准重读结果,**断线竞态不会把已提交交换改成取消**(`ItemTradeHandler.cs:225-231`)。
- **Close 推送序列**:存活一方先收 NOTI 18 `FINISH_ITEMTRADE` / NOTI 16 `CANCEL_ITEMTRADE`(`[0,0]` 2B,客户端据此清临时交易背包,`ItemTradePacketBuilder.cs:53-55`)→ 再收 **NOTI 13 `ITEM_LIST` 主背包全量补发**(`ItemListPacketBuilder.BuildItemSpaceListBody(lease.Inventory,Main)`,`ItemTradeHandler.cs:244-255`);提交成功还会补任务寻物进度(`SyncItemSeekingQuestProgressAfterInventoryMutationsAsync`,`ItemTradeHandler.cs:235-243`)。
- 所有下行包谓词:`Registered && Current(0) && Current(1) && Trade.IsCurrent`(`Send`,`ItemTradeHandler.cs:259-266`)。

---

## 五、好友 / 黑名单 / 频道查询

注册:`Network/Protocol/GameProtocolHandler.cs:964-973`(friend 组)。好友 CMD 由 `UnitedFriendSystem.RegisterHandlers` 注册(`Game/Friends/UnitedFriendSystem.Requests.cs:15-22`)。

### CMD 0x0122 (290) `ADD_UNITED_SERVER_FRIEND` — 添加好友
- **请求**:`targetUid u16 + server u8(=1) + dstr 名(1..255B,严格 GBK)`(`Network/Parsers/Friends/UnitedFriendRequest.cs:11-16`)。UID 只是提示,以名字查库定位(离线角色也可加,`UnitedFriendSystem.Requests.cs:48-49`)。
- **应答/推送**:应答 `[1]` 或 `[0,err]`(`Network/Builders/Friends/UnitedFriendPacketBuilder.cs:6-9`);错误码:0x15 解析失败/角色不存在、0x01 自加/无名/持久化异常、0x04 满 64 人(`MaximumFriends=64`,`UnitedFriendSystem.Requests.cs:13,46-57`);成功后推:subcmd=2 删旧节点(若有)→ `UNITED_SERVER_FRIEND_INFO` subcmd=0 全量列表 → 被加者在线同频道时补推 USERINFO(0x0002) 实体(`PublishFriendList`,`UnitedFriendSystem.Requests.cs:96-107`)。
- **行为**:单向关系(a→b),`RecordFriendship` 持 `Sync` 锁幂等落库(`Game/Friends/UnitedFriendSystem.cs:110-128`);处理全程包在 `RunIfCurrentAsync` + 处理前身份快照复核(防等待闸门时换角,`UnitedFriendSystem.Requests.cs:35-39`)。

### CMD 0x0123 (291) `DELETE_UNITED_SERVER_FRIEND` — 删除好友
- **请求**:`server u8(=1) + dstr 名`(`UnitedFriendRequest.cs:19-24`)。
- **应答/推送**:失败 `[0,5]`(解析失败/关系不存在/持久化异常);成功 `[1]` 后同上加删除节点 + 全量刷新(`UnitedFriendSystem.Requests.cs:65-107`)。
- **行为**:`RemoveFriendship` 只删 a→b 不动 b→a(`UnitedFriendSystem.cs:134-155`)。

### 好友列表与聚合推送(非 CMD,hooks)
- 列表 body `UNITED_SERVER_FRIEND_INFO`(NOTI 0x0111=273) subcmd=0:`u32 0 + u32 count + 每项 { u8 大区=1, u16 频道(同频道在线=0/异频道在线=真实频道/离线=0), dstr 名, u8 Lv, u32 Job, u8 GrowType, u8 黑名单位=0, i32 0 }`,离线项查角色仓储取真实值(`BuildFriendListBody`,`UnitedFriendSystem.cs:739-816`);subcmd=2 删节点 `{u32 2, u32 1, u8 1, dstr 名}`(`SendFriendDeletedAsync`,`UnitedFriendSystem.cs:838-864`)。列表重发后必须重发黑名单投影(原生 reset 会清掉临时标记,`UnitedFriendSystem.cs:722-724`)。
- 上下线:`INOUT_UNITED_SERVER_FRIEND`(NOTI 0x0112=274) `u16 频道(0=退出) + u8 服务器 + dstr 名 + u8 黑名单位`,跨频道也推;同频道再推 USERINFO/USER_LEAVE 实体(`NotifyPlayerEnteredGame/Disconnected`,`UnitedFriendSystem.cs:195-336`)。状态变更(进副本/回城)、升级/转职(等级属节点数据,不分频道)、删角/改名(双向清边+落库+通知受影响 owner)各有 hook(`UnitedFriendSystem.cs:342-438,446-545`);升级触发点也挂在 INCREASE_STATUS 分发上(`GameProtocolHandler.cs:498-507`)。

### CMD 0x0076 (118) `REGISITER_TO_BLACKLIST` — 加入黑名单
- **请求**:`dstr 名`,总长 5..33B、`u32 长度==总长-4`、严格 GBK、禁控制字符(`BlacklistHandler.TryParseName`,`Network/Handlers/BlacklistHandler.cs:88-95`)。
- **应答/推送**:`[1, dstr 名]` 成功;错误码 **74=重复、76=目标不存在、78=满 64**(`MaximumEntries=64`,`Game/Friends/BlacklistRepository.cs:12,32-52`);自加/库异常/owner 无效 → `[0,0]`(`AddAck`,`BlacklistHandler.cs:98-106`)。
- **行为**:目标查找+容量清理(先删已删角色项)+插入在**一个 immediate 事务**内(`BlacklistRepository.Add`,`BlacklistRepository.cs:32-52`);成功回调同步客户端投影 `BlacklistProjection.Names`(`BlacklistHandler.cs:61-69`);按 characterId 持有关系、名字仅是投影(`BlacklistProjection.cs:10-12`)。

### CMD 0x0077 (119) `DELETE_TO_BLACKLIST` — 移出黑名单
- **请求**:同 118 的 dstr 名。
- **应答/推送**:`[1, dstr 名]` 成功;失败 **[0,75]**(`BlacklistHandler.cs:71-77`)。
- **行为**:优先用投影里已发布的 characterId 删,退化为按名删(`BlacklistHandler.cs:73-76`);成功回调清投影(`:76`)。

### CMD 0x0078 (120) `REQUEST_BLACKLIST` — 拉取黑名单
- **请求**:必须空 body;非空回 `[0,0]`(`BlacklistHandler.cs:47-55`)。
- **应答/推送**:`PublishAsync` 先对投影中已消失的永久名逐个补发 DELETE ack(0x0077),再发 LIST ack(0x0078):`[1, count u8, 每项 { dstr 名, u8 年-1900, u8 月-1, u8 日 }]`(`BlacklistPacketBuilder.List`,`Network/Builders/Friends/BlacklistPacketBuilder.cs:16-27`、`BlacklistProjection.PublishAsync`,`BlacklistProjection.cs:43-63`)。
- **行为(发送谓词拦截)**:拦截统一由 `BlacklistRepository.IsBlocked`(SQL EXISTS)在发送谓词处生效——聊天(`ChatHandler.cs:37,111`)、组队邀请/应答(`PartyHandler.cs:1520,1698-1703,1805`)、1:1 会话(`ChatHandler.Conversations.cs:73-74`)、交易资格(`ItemTradeHandler.cs:54-55`)、好友上线通知(黑名单位透传,`UnitedFriendSystem.cs:225-227`)。

### CMD 0x0323 (803) `REQUEST_USER_CHANNEL` — 查询角色所在频道
- **请求**:`server u8(=1) + dstr 名`,总长 <55B(`UnitedFriendRequest.TryUserChannel`,`UnitedFriendRequest.cs:27-28`)。
- **应答/推送**:`[0,err]` 失败(err=0 解析/频道解析失败;100=不在线)或 `[1, ChannelServerIndex u8, dstr 名, ChannelServerIndex u8, channelId u8]`(`Network/Handlers/UserChannelHandler.cs:57-70`)。
- **行为**:按名在全部游戏会话中找在线且 current 的目标,由 `ListenerPort` 反查频道目录(`UserChannelHandler.cs:38-55`);发送谓词同时锁定双方身份与目标名,失败降级回 `[0,100]`(`UserChannelHandler.cs:64-67`)。

---

## 六、公会(四组注册:`GameProtocolHandler.cs:256-281`)

全部 CMD 汇入 `GuildMemberHandler / GuildJoinHandler / GuildCreationHandler / GuildManagementHandler` 的统一 `Handle(session,header,body)`,按 `header.type` switch 分派;文本类请求统一走 `Network/Parsers/Guilds/GuildTextRequest.cs`(dstr,长度上限按用途:名/宣传/公告/备忘,`GuildTextRequest.cs:21-30`)。所有写操作先过 `InventoryContext.TryGetOwnedLease`(选角就绪)+ transition gate;失败统一应答 `[0,0x22]`(管理类)。

### CMD 0x0043 (67) `GUILD_MEMER_LIST` — 成员名册(guild-members 组)
- **请求**:必须空 body(`Network/Handlers/GuildMemberHandler.cs:27-31`)。
- **应答/推送**:CMD 应答:`[1, guildId i32, dstr 会长名, u32 0(进度), u16 count, u16 count, 每项 { cid i32, dstr 名, dstr 备注, u16 Lv, u8 Job, u8 GrowType, u8 服务器号, u8 频道(0xFF=离线), u8 0, u8 0(隐藏模式), u8 阶级(1 会长..), u32 离线秒数(在线=0), i32 accountId, u8 0, dstr "" }]`;无公会/ body 非空 → Failure `[0,0]`(`Network/Builders/Guilds/GuildMemberPacketBuilder.cs:10-42`)。
- **备注**:无分页游标(区别于 ALL_MEMBER_LIST,`GuildMemberPacketBuilder.cs:8-9`);频道在线判定含 owned lease(`GuildMemberHandler.cs:43-49`)。

### guild-join 组(`Network/Handlers/GuildJoinHandler.cs:31-124`)
- **CMD 0x02E8 (744) `REQ_GUILD_SERCH_FOR_JOIN`**:请求=dstr 公会名;应答 `Search`:`[1, guildId i32, dstr 名, u16 人数, dstr 宣传, u32 0, u8 0]`,查不到 `[0,0x22]`(`Network/Builders/Guilds/GuildJoinPacketBuilder.cs:9-17`)。
- **CMD 0x015C (348) `REQUEST_JOIN_GUILD`**:请求=**两个 dstr**(公会名+申请留言,留言需过宣传校验,`GuildTextRequest.TryParseApplication`,`GuildTextRequest.cs:10-19`);成功应答 `Applied`:`[1, guildId, dstr 公会名, dstr 留言, u32 0]`;失败 `[0,0x9F]`(`GuildJoinHandler.cs:45-63`)。
- **CMD 0x0160 (352) `GUILD_JOIN_LIST`**:请求空 body;应答 `Applications`:`[1, count i32, 每项 { cid i32, dstr 名, u8 Job, u8 GrowType, u8 Lv, u8 0, dstr 留言, u32 离线秒数(客户端按 /3600、/86400 格式化,非时间戳) }](仅会长可见,`GetApplicationsForLeader`)`(`GuildJoinHandler.cs:64-71`、`GuildJoinPacketBuilder.cs:31-41`)。
- **CMD 0x015E (350) `APPROVE_JOIN_GUILD`**:请求=`targetCid i32`;成功应答 `[1, targetId i32]`、失败 `[0,0,targetId]`(`GuildJoinPacketBuilder.Approved`,`GuildJoinPacketBuilder.cs:44-50`);提交在 leader+applicant **按 cid 升序**双 gate 内(与登录协程同序防竞态,`GuildJoinHandler.cs:96-100`);成功后给在线申请人推:身份投影 + NOTI 2 外观更新 + NOTI 127 `GUILD_MEMBER_INFO`(`[rank u8]`,成员=4,`GuildJoinPacketBuilder.cs:52-56`)+ NOTI 70 `GUILD_INFO` 全量;最后对全体成员 `GuildStatePublisher.RefreshAsync`(`GuildJoinHandler.cs:109-123`)。
- **CMD 0x0153 (339) `REFRESH_GUILD_INFO`**:请求空 body;应答两连包:NOTI 127 Rank(自身阶级) + NOTI 70 `GUILD_INFO`(`dstr 名 + u8 1 在会 + u16 人数 + u8 等级 + dstr 公告 + u8 模型等级 + dstr 宣传 + 6×(u32 权限位掩码 + 24B 定长阶级名) + 尾部禁用系统全零`;位 1 控制申请审批,`Network/Builders/Guilds/GuildInfoPacketBuilder.cs:10-40`)。

### guild-creation 组(草稿状态机,`Network/Handlers/GuildCreationHandler.cs:30-120`)
创建资格/文本以 `CreationDraft{Name,Promotion,Approved}` 挂在 `InventoryLease` 上(`ConditionalWeakTable`,`GuildCreationHandler.cs:19,36`)。
- **CMD 0x009C (156) `CHECK_GUILD_NAME_DOUBLE`**:请求=dstr 名;校验失败 `[0,0x6C]`、重名 `[0,0x6B]`、已有公会 `[0,0x6A]`;通过则重置草稿存名并应答 `[1]`(`GuildCreationHandler.cs:39-64`)。
- **CMD 0x02E2 (738) `CHECK_GUILD_CREATE_PROMOTE_MSG`**:请求=dstr 宣传词(须先有名);通过存草稿应答 `[1]`,失败 `[0,0x6C]`(`GuildCreationHandler.cs:65-77`)。
- **CMD 0x02E4 (740) `REQUEST_GUILD_CREATE_PERMIT`**:请求=dstr 目标玩家名(单人模式**不解析不联系**该玩家);名+宣传齐备才 `[1]` + 推 NOTI 0x02AE(686) `REPLY_GUILD_CREATE_PERMIT`(`[1]`,置客户端创建状态=已批准),否则 `[0,3]`(`GuildCreationHandler.cs:78-89`、`Network/Builders/Guilds/GuildCreationPacketBuilder.cs:12-14`)。
- **CMD 0x02E6 (742) `CANCEL_GUILD_CREATE`**:空 body 丢弃草稿,无应答(`GuildCreationHandler.cs:91-93`)。
- **CMD 0x0044 (68) `CALL_GUILD_CREATE_RIGHT`**:请求必须恰为 `[0x01]` 且草稿三要素齐备;`GuildCreationService.Create` 扣费建会;失败 `[0,0]`;成功序:**先提交完再发包**(不持 inventory 锁发包):金币更新 → NOTI 2 外观更新(身份投影)→ NOTI 71 `GUILD_CREATE`(`guildId i32 + dstr 名`,`GuildCreationPacketBuilder.cs:20-26`)→ CMD `[1]`(`GuildCreationHandler.cs:94-118`)。

### guild-management 组(`Network/Handlers/GuildManagementHandler.cs:22-122`)
进入即按 cid 升序取**全成员 transition gates**,等锁期间有人入会则重取(最多 5 轮,`AcquireAffectedAsync`,`GuildManagementHandler.cs:124-145`)。
- **CMD 0x015D (349) `CANCEL_JOIN_GUILD`**:请求=`guildId i32`(`TryId`,`GuildManagementHandler.cs:147-148`)→ 撤销申请。
- **CMD 0x015F (351) `DENY_JOIN_GUILD`**:请求=`targetCid i32` → 拒绝申请。
- **CMD 0x016D (365) `JOIN_GUILD_INFO`**:请求空 body;应答为推送形 NOTI 0x0134(308) `JOIN_GUILD_INFO`:`present u8;present 时 { guildId i32, dstr 公会名, dstr 留言, u32 0, u32 0 }`(查自己的待审申请,`Network/Builders/Guilds/GuildManagementPacketBuilder.cs:8-17`)。
- **CMD 0x0099 (153) `REQ_GUILD_SECEDE`**:请求=dstr 名(≤62B;**空=自己退会,非空=踢该名成员**,仅限本会);成功应答 `Left`:`[1, 1|2, dstr 公会名, dstr 名]`(2=被踢,`GuildManagementPacketBuilder.cs:21-26`)。
- **CMD 0x007E (126) `SET_SUB_GUILD_MASTER`**:请求=`dstr 名 + 尾字节 rank(2..5)`;成功应答 `[1, 0, dstr 名, rank]`(`RankChanged`,`GuildManagementPacketBuilder.cs:29-34`)。
- **CMD 0x009A (154) `NOTIFY_MESSAGE_TO_GUILD`(公告)/ CMD 0x02E3 (739) `MODIFY_GUILD_PROMOTE_MSG`(宣传)/ CMD 0x00E0 (224) `WRITE_GUILD_MEMBER_MEMO`(成员备注)**:三条同构,请求=dstr 文本(上限按字段),`EditText` 落库,成功应答 `[1]`(`GuildManagementHandler.cs:85-92`)。
- **CMD 0x012F (303) `BREAK_GUILD`**:请求空 body → `Disband`;成功应答 `[1]`,刷新集合并入全部受影响成员(`GuildManagementHandler.cs:93-99`)。
- 应答纪律:发起者断线也必须把已提交结果发完(`SessionDirectory.TrySendBestEffortAsync`,`GuildManagementHandler.cs:109-111`);成功后刷新集合=发起者+目标+全体成员,交 `GuildStatePublisher.RefreshAsync`(BREAK 时 `disbanded=true`,`GuildManagementHandler.cs:112-121`)。

### GuildStatePublisher(状态再传播,`Network/Handlers/GuildStatePublisher.cs:24-51`)
对每个在线受影响者:在自己 gate 内重读 roster → `GuildIdentityProjection.Apply` 更新 `Subtype0Tail` → guildId 变化才推 NOTI 2 外观更新 → 无会:(曾经有会则推 NOTI `GUILD_DISMISS`(0x003D=61,解散)/`GUILD_SECEDE_TO_USER`(0x0097=151,退会)空包清模型) + NOTI 308 Pending;有会:NOTI 127 Rank + NOTI 70 GUILD_INFO + CMD 67 成员表(`Cleared`,`GuildManagementPacketBuilder.cs:37-38`)。

---

## 附:本域 CMD/NOTI 速查

| CMD | 枚举 | NOTI | 枚举 |
|---|---|---|---|
| 0x0002 (2) | SET_UDP_IP_PORT | 0x0007 (7) | REQUEST_PEER |
| 0x000A/0x000B (10/11) | REQUEST_PEER / RESPONSE_PEER | 0x0008 (8) | RESPONSE_PEER |
| 0x000C/0x000D/0x000E (12/13/14) | SET_PARTY_INFO / LEAVE_PARTY / WALKOUT_PARTY_MEMBER | 0x0009 (9) | PARTY_INFO |
| 0x0011 (17) | SEND_MESSAGE | 0x000B (11) | USER_UDP_IP_PORT |
| 0x0013 (19) | MOVE_ITEMSPACE(劫持) | 0x000C (12) | MESSAGE |
| 0x0018 (24) | SET_ITEMTRADE_STATE | 0x000F/0x0010/0x0011/0x0012 (15/16/17/18) | CHANGE_ITEMTRADE_ITEM / CANCEL_ITEMTRADE / STATE_ITEMTRADE / FINISH_ITEMTRADE |
| 0x0079 (121) | CHANGE_HOST | 0x0099 (153) | PARTY_MEMBER_REALTIME_INFO |
| 0x00A6 (166) | CALL_PARTY_MEMBER_REALTIME_INFO | 0x0111/0x0112 (273/274) | UNITED_SERVER_FRIEND_INFO / INOUT_UNITED_SERVER_FRIEND |
| 0x0122/0x0123 (290/291) | ADD/DELETE_UNITED_SERVER_FRIEND | 0x0041 (65) | MESSAGE_OTHER_CHANNEL |
| 0x0076/0x0077/0x0078 (118/119/120) | REGISITER/DELETE/REQUEST_BLACKLIST | 0x0173/0x0174 (371/372) | MESSAGE_HYPER_LINK / MESSAGE_OTHER_CHANNEL_HYPER_LINK |
| 0x0323 (803) | REQUEST_USER_CHANNEL | 0x016A/0x016E/0x016F (362/366/367) | CREATE_GROUP / LEAVE_USER_FROM_GROUP / MESSAGE_GROUP_CHAT |
| 0x0043 (67) | GUILD_MEMER_LIST | 0x0046/0x0047 (70/71) | GUILD_INFO / GUILD_CREATE |
| 0x02E8/0x015C/0x0160/0x015E/0x0153 (744/348/352/350/339) | 搜会/申请/申请列表/批准/刷新 | 0x007F (127) | GUILD_MEMBER_INFO |
| 0x009C/0x02E2/0x02E4/0x02E6/0x0044 (156/738/740/742/68) | 建会五步 | 0x0134 (308) | JOIN_GUILD_INFO |
| 0x015D/0x015F/0x016D/0x0099/0x007E/0x009A/0x02E3/0x00E0/0x012F | 管理/文本/解散 | 0x02AE (686) | REPLY_GUILD_CREATE_PERMIT |
| 0x03AE/0x0061/0x0031/0x01DF (942/97/49/479) | P2P 上报(静默) | 0x003D/0x0097 (61/151) | GUILD_DISMISS / GUILD_SECEDE_TO_USER |
| 0x01A9 (425) | ITEM_HYPERLINK_MESSAGE | 0x028F (655) | ITEMTRADE_REG_ITEM_FINISH |
| 0x01A3/0x01A5/0x0149 (419/421/329) | CREATE_GROUP / LEAVE_FROM_GROUP / ONE_TO_ONE_CHAT_STATE | 0x000D (13) | ITEM_LIST(交易结算后全量补发) |
