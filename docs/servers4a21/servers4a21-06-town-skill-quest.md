# 86 级版本服务端(ServerS4A21)已实现协议处理参考 — 城镇同步/技能/任务

> - **本册导航**:总览/帧结构/信封/分发架构/已实现 CMD 总表见 [servers4a21-00-overview.md](servers4a21-00-overview.md)。
> - 仅写 A21(`ServerS4A21\Server\DfoServer`)侧实现,供 DFLegacy 回填参考。路径相对 `DfoServer` 根;行号为整理时点状态。
## 域综述

城镇同步采用"客户端上报、服务端投影"模型:客户端每次移动发 `SET_USER_POSITION`,服务端只更新内存坐标(`PlayerContext.CurPosX/Y`)并异步落库,再把 `USER_POSITION(0x0016)` 推回本人并广播同区域;进区域走 `SET_USER_AREA`,由 `SetUserAreaCoreAsync` 统一处理(鉴权→旧区域离开投影→本人 `USER_AREA`→名册同屏),`PARTY_TELEPORT`/`SOLO_TELEPOART` 最终都复用该核心函数。技能侧没有独立 parser,`SkillHandler` 直接按偏移读 body;技能树页(0/1)、指令自定义、连招页均持久化到 SQLite(`SqliteCharacterProgressRepository`/`SqliteSubtype1Repository`,PvP 频道写 `SqlitePvpSkillRepository`),`SKILL_INIT` 与 `COMBO_SKILL_EXTENSION_QUICK_SLOT_RESET` 用 `SKILLINFO(0x0013)` 推送全量技能页。任务管理器入口是 `Game/Quests/QuestManager.cs`(挂于 `GameSession`,经 `GameProtocolHandler.RegisterQuestHandlers` 分发);四个任务命令的请求体都先经 `StripEcho` 去掉前 2 字节回显(`QuestManager.cs:139-145`)再由 `QuestCommandParser` 解析,应答字节统一由 `QuestAckBuilder` 冻结序列化;完成后的状态投影(EXP/升级/可接列表/完成列表)由 `QuestNotificationProjector` 统一推送。

约定:客户端→服务端帧 14B 头(cmd u8+type u16+length u32+checksum u32+seq u16+extra u8),body 从第一个载荷字段起;服务端→客户端 15B 信封 `GamePacketEnvelopeBuilder.Build(type,body)`(`Network/Core/GamePacketEnvelopeBuilder.cs:6`),0x01=应答、0x00=推送;dstr=u32 长度+GBK 字节。

---

## 一、城镇(town 组)

### CMD 0x0023 (35) `SET_USER_POSITION` — 城镇位置上报
- **请求**: >=7B: i16 x@0, i16 y@2, u8 direction@4, u16 motionState@5;解析处 `Network/Handlers/TownHandler.cs:215-220`(无独立 parser)
- **应答/推送**: 推 NOTI 0x0016(22) `USER_POSITION`,body=u16 userId+i16 x+i16 y+u8 dir+u16 motionState;构造处 `Network/Builders/Town/TownAreaNotificationBuilder.cs:81-94`,先发本人 `Network/Handlers/TownHandler.cs:226-234`
- **行为**:
  - 更新内存坐标/朝向后异步落库(source=set_user_position)`Network/Handlers/TownHandler.cs:221-224`
  - 本人必收自己的 USER_POSITION(单机也只有这一个目标)`Network/Handlers/TownHandler.cs:232-234`
  - 每角色首次移动(movementSequence==1 且城镇到达态)触发一次性进成名册投影 `BroadcastAreaRosterAsync`,`Network/Handlers/TownHandler.cs:241-246`、`Network/Handlers/TownHandler.cs:1573-1578`
  - 其余移动只对同区域其它玩家做轻量广播(排除自己)`Network/Handlers/TownHandler.cs:247-253`

### CMD 0x0024 (36) `SET_USER_AREA` — 城镇切区
- **请求**: >=6B: u8 townId@0, u8 areaId@1, i16 x@2, i16 y@4;解析处 `Network/Handlers/TownHandler.cs:295-301`(无独立 parser)
- **应答/推送**: 推 NOTI 0x0017(23) `USER_AREA` 给本人(body=u16 uid+u8 town+u8 area+i16 x+i16 y+u8 dir+u8 state,`TownAreaNotificationBuilder.cs:39-53`);旧区域广播同格式"远程离开"包(`TownHandler.cs:345-365`);随后 `BroadcastAreaRosterAsync` 名册面(`TownHandler.cs:372-388`)
- **行为**:
  - 注册处包装:进区前关专家副职商店会话、进区后推该区域商店 `Network/Protocol/GameProtocolHandler.cs:773-779`
  - Raid 区进入校验,拒绝时回发当前区域 `Network/Handlers/TownHandler.cs:303-312`;频道出生点策略拒绝 `Network/Handlers/TownHandler.cs:314-325`
  - 区域状态固定写 0(A21 客户端 body 里的 state 字节不作数)`Network/Handlers/TownHandler.cs:335-338`
  - 旧区域离开投影故意用 USER_AREA 远程分支而非 USER_LEAVE/AREA_USERS(防清队伍槽/防切模块)`Network/Handlers/TownHandler.cs:342-344`
  - 强制立即落库 `Network/Handlers/TownHandler.cs:392`

### CMD 0x00ED (237) `TELEPORT` — 传送道具使用(ItemTeleportRequest)
- **请求**: >=13B: i16 物品槽@0, i32 itemId@2, u8 保留@6, u16 目标城镇@7, i16 x@9, i16 y@11;解析处 `Network/Parsers/Town/ItemTeleportRequest.cs:44-76`
- **应答/推送**: 推 NOTI 0x00AC(172) `LOAD_COOLTIME_ITEM_INFO` body={0x01, i32 itemId, i32 9}(`Network/Builders/Town/TeleportPacketBuilder.cs:9-17`,发送 `TownHandler.cs:754-758`);随后依次推本人 NOTI 0x0017 USER_AREA(`TownHandler.cs:764-767`)、NOTI 0x0018 AREA_USERS(`TownHandler.cs:768-771`)、0x00CA {0x00}(`TownHandler.cs:772-775`);道具栏刷新 UpdateItemList(`TownHandler.cs:759-763`)
- **行为**:
  - 校验背包租约持有该道具 `TownHandler.cs:611-637`;必须是 TownSelection 类消耗品 `TownHandler.cs:639-651`
  - 频道准入拒绝 `TownHandler.cs:663-676`;目标城镇须有 cera 门 `TownHandler.cs:678-698`
  - `TeleportConsumableCommitService.TryCommit` 原子扣道具并写坐标(areaState=3)`TownHandler.cs:705-731`
  - 向旧区域广播离开投影(普通离开语义)`TownHandler.cs:733-744`

### CMD 0x01B3 (435) `PARTY_TELEPORT` — 组队全员传送(PartyTeleportRequest)
- **请求**: >=7B: u8 townId@0, u8 areaId@1, i16 x@2, i16 y@4, u8 direction@6(尾部还有 1B flag 不读);解析处 `Network/Parsers/Town/PartyTeleportRequest.cs:41-57`
- **应答/推送**: 无独立应答;拒绝场景推 NOTI `SERVER_NOTICE_MESSAGE` 文本(异房间 `TownHandler.cs:884-889`、有队员未解锁 `TownHandler.cs:912-917`),频道不符走 `ChannelTownRestrictionSender`(`TownHandler.cs:804`)
- **行为**:
  - 仅队长可发起 `TownHandler.cs:824-832`;本人不可在副本中 `TownHandler.cs:808-817`
  - 逐队员筛条件:同 listener、非副本、频道准入 `TownHandler.cs:836-865`
  - 全有或全无:任一队员与队长不同房间即整队不传 `TownHandler.cs:867-890`;任一队员未解锁目标区域(等级/任务门槛,`MeetsTownAreaPermission` `TownHandler.cs:939-963`)整队不传 `TownHandler.cs:892-918`
  - 通过后对每个合格队员复用 `SetUserAreaCoreAsync` 搬运 `TownHandler.cs:920-928`

### CMD 0x01D7 (471) `SOLO_TELEPOART` — 单人区域传送(SoloTeleportRequest)
- **请求**: 15B: 前 8B 全 0xFF 占位不读,u8 townId@8, u8 areaId@9, i16 x@10, i16 y@12, u8 direction@14;解析处 `Network/Parsers/Town/SoloTeleportRequest.cs:40-59`
- **应答/推送**: 无独立应答,复用 SET_USER_AREA 全套推送(经 `SetUserAreaCoreAsync`)
- **行为**: 频道策略拒绝 `TownHandler.cs:988-1001`;副本中拒绝 `TownHandler.cs:1003-1010`;把目标改写成 6B area body 后调 `SetUserAreaCoreAsync` `TownHandler.cs:1012-1020`

## 二、城镇广播面纪律(NOTI 22/23/24/6 等)

| NOTI | 谁收 | 何时发 |
|---|---|---|
| 0x0016(22) USER_POSITION | 本人 + 同区域其它玩家(排除发送者) | 每次 SET_USER_POSITION;首次移动改为整套名册投影 `TownHandler.cs:226-253` |
| 0x0017(23) USER_AREA | 本人;旧区域同伴(远程离开投影);同屏插入包 | SET_USER_AREA/传送/组队单人传送;同屏插入复用同格式(`BuildCoPresenceInsert` `TownHandler.cs:423-427`) `TownHandler.cs:345-388` |
| 0x0018(24) AREA_USERS | **只发进区的本人**(全员名册,自己排第一) | `BroadcastAreaRosterAsync` 内 `TownHandler.cs:480-490`;发给已在场玩家会触发 setDrawLoadingMode 切 TOWN 模块,严禁 `TownHandler.cs:398-400,549-550` |
| 0x0002(2) USERINFO | 本人外观 → 逐个他人外观+插入包;他人只收到达者 USERINFO+USER_AREA | 名册投影尾段 `TownHandler.cs:494-515` |
| 0x0006(6) USER_LEAVE | 同区域其它玩家,body=u16 userId(`TownAreaNotificationBuilder.cs:104-113`) | **仅会话真正离开**(`NotifyLeaveAsync` `TownHandler.cs:564-578`);普通切图禁用——客户端会同时清队伍槽(`TownHandler.cs:343,566`) |

副本/切区离开由 `TownAreaRosterDepartureNotifier` 按模式广播(模式 1=USER_AREA 远程快照)`Network/Handlers/TownAreaRosterDepartureNotifier.cs:24-60`;USERINFO 广播统一实现在 `Network/Handlers/UserInfoBroadcastService.cs:13-31`。

## 三、技能(skill 组)

技能应答无独立 builder 的均由 `SkillHandler` 内联构字节数组;全量技能页推送 body 由 `SkillInfoBodyBuilder.BuildFrom` 构造:每页{u16 header, u8 count, 每{u8 slot, u16 skillId, u8 level, u8 extraCount+extras}},尾 u16+u16(`Network/Builders/Init/SkillInfoBodyBuilder.cs:18-40`);SP 槽由 `SkillPointSlotBodyBuilder`(NOTI 0x015F)构造:u8 count+每{u8 skillType, u16 points}(`Network/Builders/Init/SkillPointSlotBodyBuilder.cs:17-27`)。

### CMD 0x001C (28) `CHANGE_SKILLSLOT` — 技能槽位交换
- **请求**: >=3B: u8 page 标志(1→页1,否则页0)@0, u8 槽A@1, u8 槽B@2;解析处 `Network/Handlers/SkillHandler.cs:115-126`
- **应答**: {0x01, b0, b1, b2} 回显,`SkillHandler.cs:117-119`
- **行为**: PvP 频道写 `SqlitePvpSkillRepository.SwapSkillSlot`(`SkillHandler.cs:127-134`);职业 9(黑暗骑士)走 `DarkKnightComboSkillService`(`SkillHandler.cs:137-142`);其余 `SqliteCharacterProgressRepository.SwapSkillSlot`(`SkillHandler.cs:144-148`)

### CMD 0x001D (29) `BUY_SKILL` — 学/洗技能
- **请求**: >=6B: u8 技能树@0, u8 条目数@1, 之后每 4B {u16 skillIndex(LE), u8 isRefund, u8 level};解析处 `Network/Handlers/SkillHandler.cs:264-283`(技能编号必须读双字节,否则 >255 的技能截断学错,见注释 PR589)
- **应答**: `Network/Builders/BuySkillAckBuilder.cs:12-52`:失败{0x00, err};成功{0x01, u8 tree, u16 remainSp, u16 remainTp, u8 count, 每{u8 slot, u16 skillId, u8 level, u8 hasCmd [+u8 cmdLen+cmdBytes]}}
- **行为**:
  - 二觉树未解锁时请求 tree=1 直接拒绝 err=3 `SkillHandler.cs:295-310`
  - PvP 频道 `BuySkillService.ExecutePvp` `SkillHandler.cs:319-332`;普通频道凭在线背包租约走 `ExecuteWithRefundConsumable`(可消耗遗忘河水) `SkillHandler.cs:335-363`
  - 消耗遗忘河水成功后补发道具栏刷新 `SkillHandler.cs:368-377`

### CMD 0x0104 (260) `CHANGE_ANOTHER_SKILL_TREE` — 二觉技能树切换
- **请求**: 1B(树号)或 2B(第 2 字节为树号);解析处 `Network/Handlers/SkillHandler.cs:215,462-480`
- **应答**: 有 body 时 = {0x01}+body 回显且第 1 字节改写为最终树号;无 body 时 {0x01, tree},`SkillHandler.cs:450-460,258-259`
- **行为**: 库内树号未解锁则保持 LockedWireValue 原样回包(等效拒绝)`SkillHandler.cs:223-236`;已解锁则在 0/1 间翻转并 `SqliteSubtype1Repository.UpdateSkillTreeIndex` 持久化,同步内存 `Subtype0Tail.SkillTreeIndex` `SkillHandler.cs:238-245`

### CMD 0x014B (331) `SKILL_COMMAND_CUSTOMIZING` — 技能指令自定义
- **请求**: 记录链:首记录 4B 头{u8 page, u8 skillId, u16 cmdLen(大端)},后续每记录 3B 头{u8 skillId, u16 cmdLen}+cmdLen 字节指令;解析处 `SkillHandler.ParseSkillCommandRecords` `Network/Handlers/SkillHandler.cs:418-448`
- **应答**: {0x01}+整个 body 原样回显 `SkillHandler.cs:50-53`
- **行为**: 逐条 `UpdateSkillCommand` 持久化(PvP 频道/普通库分流)`SkillHandler.cs:63-81`

### CMD 0x014C (332) `SKILL_COMMAND_ALL_DEFAULT` — 全部指令还原默认
- **请求**: 无有效载荷
- **应答**: {0x01} `SkillHandler.cs:91`
- **行为**: `ClearAllSkillCommands`(PvP/普通分流)`SkillHandler.cs:99-107`

### CMD 0x01EC (492) `SKILL_INIT` — 技能全量刷新
- **请求**: 无载荷
- **应答**: 先回 {0x01, 0x00}(`SkillHandler.cs:390`),再推 NOTI 0x0013(19) `SKILLINFO`(`SkillHandler.cs:407-411`)
- **行为**: 重载选角数据源并取 InitializationSnapshot.SkillInfo(`SkillHandler.cs:393-397`);PvP 频道改从 PvP 技能库加载(`SkillHandler.cs:398-406`)

### CMD 0x01FD (509) `COMBO_SKILL_INFO` — 黑暗骑士连招页保存
- **请求**: 仅职业 9 受理,其余回失败(`SkillHandler.cs:164-169`);body 由 `DarkKnightComboSkillService.SaveComboSkillInfo/SaveAutoComboSkillInfo` 解析入库(`SkillHandler.cs:171-179`)
- **应答**: 成功 `CommonPacketBodyBuilder.BuildSuccessAck`(`SkillHandler.cs:180`);失败/异常 {0x00, 0x04}(`SkillHandler.cs:160,167,191`)
- **行为**: 若 10 秒内收到过 0x01FF 则按"自动连招提交"语义保存(`ConsumePendingDarkKnightAutoCombo` `SkillHandler.cs:173-179,540-556`)

### CMD 0x01FF (511) `COMBO_SKILL_EXTENSION_QUICK_SLOT_RESET` — 扩展快捷栏重置(自动连招前奏)
- **请求**: 可选 u8 page(>0 为页1)`SkillHandler.cs:518`
- **应答**: `BuildSuccessAck` `SkillHandler.cs:201-204`
- **行为**: 职业 9 先清理重复快捷槽,有清理则推 NOTI 0x0013 SKILLINFO(`SkillHandler.cs:511-538`);随后登记 10 秒自动连招提交标记(`SkillHandler.cs:206-210`)

## 四、任务(quest 组)

请求体前 2 字节为客户端回显,统一剥离后解析(`QuestManager.StripEcho` `Game/Quests/QuestManager.cs:139-145`);四个命令应答字节唯一序列化点 `Network/Builders/Quest/QuestAckBuilder.cs`,失败一律 {0x00, errorCode}(`QuestAckBuilder.cs:181-184`)。

### CMD 0x01DC (476) `IMAGE_COMMUNICATION_EQUIPMENT_USE` — 影像通讯装置使用
- **请求**: 约定空 body(`QuestCommandParser.TryParseImageCommunicationUse` 仅在 body 为空时成功,`Network/Parsers/Quest/QuestCommandParser.cs:10-16`)
- **应答**: i32 npcIndex(无效补 0),`Network/Builders/Quest/ImageCommunicationAckBuilder.cs:9-13`,构造调用 `Game/Quests/QuestManager.cs:196-198`
- **行为**: 解析失败按"无匹配进行中任务"失败(`QuestManager.cs:179-188`);成功交 `ImageCommunicationApplicationService.Apply`(`QuestManager.cs:191-194`);失败记日志(`QuestManager.cs:200-206`)

### CMD 0x001F (31) `ACCEPT_QUEST` — 接任务
- **请求**: 剥回显后 u16 questId;解析处 `Network/Parsers/Quest/QuestCommandParser.cs:18-27`
- **应答**: `QuestAckBuilder.BuildAccept`:成功{0x01, u16 questId, u32 initTrigger, u8 事件道具数, 每{u16 slot, u32 itemId, u32 count}}(`QuestAckBuilder.cs:11-27`)
- **行为**: AnotherArad 任务先行分流(`Network/Protocol/GameProtocolHandler.cs:819-828` → `Network/Handlers/DungeonHandler.cs:165`);接取成功后推接受触发表、刷新可接列表 `QuestManager.cs:166-172`

### CMD 0x0020 (32) `GIVEUP_QUEST` — 放弃任务
- **请求**: 剥回显后 u16 questId;解析处 `QuestCommandParser.cs:29-38`
- **应答**: `BuildGiveup` {0x01, u16 questId}(`QuestAckBuilder.cs:29-37`)
- **行为**: 有背包变更时按列表分组推 UpdateItemList(`QuestManager.cs:226-236`)

### CMD 0x0021 (33) `SET_QUEST_TRIGGER` — 任务触发计数
- **请求**: 剥回显后的触发载荷,由 `QuestService.HandleSetTrigger` 解释(`QuestManager.cs:286`)
- **应答**: `BuildSetTrigger` {0x01, u16 questId, u32 triggerValue}(`QuestAckBuilder.cs:39-48`)
- **行为**:
  - 每日挑战分支:直接推 NOTI 0x0286 DAILY_CHALLENGE 并返回其 ack(`QuestManager.cs:263-269`)
  - 服务端驱动回显/延迟清图分支先行应答(`QuestManager.cs:271-284`)
  - 注册处:AnotherArad 分流后,普通结果交 `DungeonHandler.HandleQuestSetTriggerResultAsync` 做副本侧结算(`Network/Protocol/GameProtocolHandler.cs:837-862` → `Network/Handlers/DungeonHandler.cs:297`)

### CMD 0x0022 (34) `FINISH_QUEST` — 交任务
- **请求**: 固定 8B: u16 questId, u16 rewardSelection(0xFFFF=不选), u16 completionCount, u16 哨兵必须 0xFFFF;解析处 `QuestCommandParser.cs:40-61`
- **应答**: `BuildFinish`(`QuestAckBuilder.cs:50-138`):公共前缀{0x01, u16 questId, u8 finishType, u32 exp, u32 completionCount};Seeking/0x19 类先写{u8 数量+每 7B 消耗条目};chain=title 走 7B 条目+chain5;chain1/2 写 chain+保留 0+两页压缩技能;chain20 写 chain+growNumber+两页技能;chain0 追加奖励物品条目(slot/itemId/count/upgradeLevel/durability/时间戳/2B 尾)
- **行为**:
  - ACK 前置推送:chain1/2 推技能刷新、chain20 推副职变更(`QuestManager.cs:341` → `Game/Quests/QuestNotificationProjector.cs:77-88`)
  - ACK 后投影 EXP/升级/SP/称号/宠物进化/好友列表等(`QuestManager.cs:343` → `QuestNotificationProjector.cs:102-263`;副本内升级不发 subtype0 广播防丢房间态,`QuestNotificationProjector.cs:184-190`)
  - 带每日挑战快照时再推 CLEAR_QUEST_LIST + NOTI 0x0286(`QuestManager.cs:344-354`)
  - AnotherArad 分流 `GameProtocolHandler.cs:863-877` → `DungeonHandler.cs:171`

### CMD 0x0315 (789) `SCENARIO_MODE_CLEAR_QUEST` — 剧情模式直接完成
- **请求**: 固定 4B u32 questId(>0 且 ≤0xFFFF);解析处 `QuestCommandParser.cs:63-77`
- **应答**: 无专用 ack;成功后依次推进行中/可接/已完成三张任务列表(`QuestManager.cs:403-411`)
- **行为**: 带等级/职业/转职入库(`QuestManager.cs:381-387`);有道具变更推 UpdateItemList(`QuestManager.cs:391-401`);失败静默(仅日志 `QuestManager.cs:366-373`)

### CMD 0x01FB (507) `SAVE_QUEST_NOTIFY` — 任务追踪栏保存(只存不回)
- **请求**: u8 数量 + 数量×i32 questId(数量≤4、去重、>0);解析处 `QuestCommandParser.cs:79-105`(上限 `Game/Quests/QuestNotifySelection.cs:19` MaxSlots=4)
- **应答**: 无
- **行为**: `QuestNotifySelectionService.TryReplace` 整表替换并落库,失败仅日志(`QuestManager.cs:438-457`)

### CMD 0x02BC (700) `DAILY_CHALLENGE_REWARD` — 每日挑战领奖(注册处内联)
- **请求**: 固定 4B i32 groupIndex;解析处 `Game/Quests/QuestManager.cs:291-321`(长度/租约不符即 InvalidRequest)
- **应答**: `Network/Builders/Quest/DailyChallengeRewardAckBuilder.cs:10-20`:失败{0,0};成功{0x01, i32 groupIndex, i32 0(保留)}
- **推送**: 有快照时推 NOTI 0x0286(646) `DAILY_CHALLENGE`,body=u32 角色等级+u32 组数+每组{u32 groupId,u32 条目数,每条目{u32 trackLikeId,u32 remaining,u32 target}}+u32 标志长度+领奖标志向量(定长 5)`Network/Builders/Init/DailyChallengeBodyBuilder.cs:32-79`
- **行为**: 领奖经 `DailyChallengeService.ClaimReward`;发放道具后按列表分组推 UpdateItemList(`Network/Protocol/GameProtocolHandler.cs:891-933`,内联完整逻辑);(另 `DailyChallengeClearDungeonBodyBuilder` 为副本结算侧单 u32 推送体,`Network/Builders/Quest/DailyChallengeClearDungeonBodyBuilder.cs:10-27`)

## 五、任务推送面(QuestManager/Projector 主动 NOTI)

| NOTI | body(构造处) | 触发时机 |
|---|---|---|
| 0x023F(575) ACCEPTED_QUEST 进行中列表 | u32 数量+每{u16 questId, u32 triggerValue}(`QuestNotificationProjector.cs:562-575`,发送 `:265-270`) | 登录初始化;剧情模式完成后(`QuestManager.cs:403-405`) |
| 0x0015(21) ACCEPTABLE_QUEST_LIST 可接列表 | u8 level+u16 数量+u16[] questId,由 QuestListBodyBuilder.BuildBody 按 level/job/growType/已完成标志计算(`Network/Builders/Init/QuestListBodyBuilder.cs:49-60`,发送 `QuestNotificationProjector.cs:293-334`) | 接任务成功后(`QuestManager.cs:171-172`);交任务投影末尾(`QuestNotificationProjector.cs:261-262`);剧情模式完成后(`QuestManager.cs:406-408`) |
| 0x0164(356) CLEAR_QUEST_LIST 已完成列表 | u32 长度(30000)+30000B 位图(`Network/Builders/Init/ClearQuestListBodyBuilder.cs:23-45`,发送 `QuestNotificationProjector.cs:336-347`) | 交任务且带每日挑战快照时(`QuestManager.cs:350`);剧情模式完成后(`QuestManager.cs:409-411`) |
| 0x0286(646) DAILY_CHALLENGE | 见 DailyChallengeBodyBuilder | SET_QUEST_TRIGGER 每日分支(`QuestManager.cs:263-269`);交任务带快照(`QuestManager.cs:351-353`);领奖确认(`GameProtocolHandler.cs:921-927`) |
| CMD SET_QUEST_TRIGGER 形式的服务端驱动触发回显 | `QuestAckBuilder.BuildSetTrigger` | 任务接取/完成引发的服务端触发变更,经 `SendTriggerChangesAsync` 逐条下发(`QuestNotificationProjector.cs:272-291`) |