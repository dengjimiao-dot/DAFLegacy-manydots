# ServerS4A21 副本主流程协议参考(入场→加载→推进→击杀→拾取→结算→翻牌→退出/复活/重连)

> - **本册导航**:总览/帧结构/信封/分发架构/已实现 CMD 总表见 [servers4a21-00-overview.md](servers4a21-00-overview.md)。
> - 仅写 A21(`ServerS4A21\Server\DfoServer`)侧实现,供 DAFLegacy 回填参考。路径相对 `DfoServer` 根;行号为整理时点状态。

## 域综述

一局副本的**唯一真相载体**是挂在 `PlayerContext.CurrentRun` 上的 `DungeonRun` 对象(`Network/handlers/Dungeon/DungeonRunLifecycle.cs:12-15`):进本 new、结束置 null,单局字段随对象消失。`DungeonRun.Phase` 是显式相位机(`Game/Dungeon/DungeonRunPhase.cs:10-17`):`None→InProgress→Cleared→ResultShown→CardsRevealed`,由 `_runState`(DungeonRunState)+`_settlementState`(DungeonSettlementState)联合推导(`Game/Dungeon/DungeonRun.cs:113-165`;`TryMarkResultShown` :809、`TryMarkCardsRevealed` :822);运行态另有 Created/Selecting/Active/ClearCommitting/Cleared/Ending/Ended(`Game/Dungeon/DungeonLifecycleState.cs:12-22`)与结束原因枚举 :24-34。

进出局的唯一收口是 `DungeonRunLifecycle`(`BeginRun` :18、`EndRunAsync` :402、断线摘除 `DetachRunOnNetworkDisconnect` :686、重连附着 `AttachResumedRun` :738),所有定时器(翻牌自动流、死亡回城)随局取消(:1055-1063)。`DungeonHandler` 只是门面(`Network/handlers/DungeonHandler.cs:112-131`),实际分发到 `DungeonEntryHandler`(入场/选图)、`DungeonMapHandler`(过图/START_MAP)、`DungeonCombatHandler`(击杀/死亡/复活/拾取/丢弃)、`DungeonSettlementHandler`(通关/结算/翻牌)、`DungeonTutorialHandler`(教程);共享依赖集中在 `DungeonSharedServices` 组合根(`Network/handlers/Dungeon/DungeonSharedServices.cs:19-247`:CardRewardCoordinator、DungeonAdmissionRejectSender、InstanceRegistry、TownReturn、ProgressNotifications 等)。组队 fan-out 以 `DungeonInstanceRegistry` 名册为准:击杀/通关先冻结参与者名册再逐员 relay(`DungeonKillApplicationService.RelayToFrozenRosterAsync` :1389-1447、`DungeonSettlementHandler.RelayClearToFrozenRosterAsync` :2922),每步以 `DungeonEffectId` 幂等台账防重放。加载栅栏由 `DungeonLoadingCoordinator` 统一持有(45s 超时,`Network/handlers/Dungeon/DungeonLoadingCoordinator.cs:16-17`),翻牌自动流由 `CardRewardCoordinator` 定时驱动。Raid(安图恩)通过 `GameProtocolHandler` 内的 wrapper 在这些 CMD 前后挂接(`:618-697`),本文只在相应条目一句带过。

---

## 入场与选图

### CMD 0x000F (15) `ENTER_SELECT_DUNGEON` — 进入选副本界面
- **请求**: `u32 dungeonId`(LE,>int.MaxValue 拒绝),body 最少 4B;尾部字节仅做非零诊断 `Network/Parsers/Dungeon/EnterSelectDungeonRequest.cs:24-51`。
- **应答/推送**:
  - 成功前缀多包:各 USERINFO + `NOTI 0x0003 USER_STATE`(u8 count + count×{u16 uid, u8 state},`Network/Builders/Dungeon/EnterSelectDungeonStateBuilder.cs:12-26`)+ 成长胶囊进度;
  - 随后 `NOTI 0x001B ENTER_SELECT_DUNGEON`(推送):`i32 0, i32 0, u8 blockedCount, blocked×u16, u8 0, u8 userCount, user×{u16 uid, u8 0}, i32 0, u16 1, u16 0, i32 0, u8 0, u8 0, i32 0, i32 0, u8 0`(`EnterSelectDungeonStateBuilder.cs:64-96`,发送处 `Network/handlers/Dungeon/DungeonEntryHandler.cs:793-799`;教程首次进本推迟到 CHANGE_TUTORIAL_FLAG 再发,:801-806);
  - 拒绝:CMD 0x000F 应答 3B `[u8 0, errCode u8, context u8]`(`Network/Builders/Dungeon/DungeonAdmissionRejectBuilder.cs:12-23`;errCode 映射 :25-55,如 InvalidSelectionState=0x13、MissingRequiredItem=0x11、DailyEntryLimitReached=0xF6;context=成员槽位 0-7,`Game/Dungeon/DungeonAdmissionReject.cs:11-24`),经 `DungeonAdmissionRejectSender` 发送(`Network/handlers/Dungeon/DungeonAdmissionRejectSender.cs:19-35`)。
- **行为**:
  - 双重门锁:先 `Player.DungeonRunTransitionGate` 再 `PartyCohort.TransitionGate`,防重复 ENTER_SELECT 并发冻结出两份 cohort(`DungeonEntryHandler.cs:482-484, 543-544`);重传复用已冻结 selection/cohort(:520-529),stale retry 拒绝(:550-566)。
  - 校验链:raid 状态拦截(:619-627)、雇佣兵限制(:628-639)、活跃 run/return 状态(:643-661)、队伍人数变更后未重绑 selection 拒绝(:499-519)、host 槽位解析失败拒绝(:751-772)。
  - 状态迁移:`Player.UserState=0x01` 并广播好友状态(:777-781);组队时队长为每个成员构建 USERINFO 包(:689-718),follower 自己不发 0x000F,由队长投影、应答统一用语义类型 0x000F(:1104-1111)。
  - 结束调用 `selection.TryCompletePartyProjection()` 落地 selection(:812-820)。

### CMD 0x0010 (16) `SELECT_DUNGEON` — 入场请求与准入/拒绝
- **请求**: `u32 dungeonId, u8 difficulty, u8 flag1(HellPartyRequest), u8 flag2(HellPartyDifficulty), u16 a21Sentinel(通常 0xFFFF)`,body 最少 9B `Network/Parsers/Dungeon/SelectDungeonRequest.cs:35-62`。
- **应答/推送**: 准入是一条服务器驱动包链(`SendDungeonSelectPacketsTo`,`DungeonEntryHandler.cs:3406-3509`):
  1. 异界裂缝时先 `NOTI CRACK_OF_DIMENSION`(:3422-3431);
  2. `NOTI 0x019F TAG_CHARACTER_INFO` 支援兵(:3437-3449);
  3. 血祭场发 `NOTI 0x00D4 BLOOD_INFO`,否则 `NOTI 0x001C DUNGEON_INFO`:`i32 dungeonId, u8 difficulty, u8 mazeIndex, u8 bossX, u8 bossY, u8 hellX, u8 hellY, u8 0, minimapGroups(u8 count + 组×{u8 n, n×(x,y)}), u8 1, 5×u8 0, u32 0xFFFFFFFF, 10×u8 0`(`Network/Builders/Dungeon/DungeonNotificationBuilder.cs:56-108`);
  4. `NOTI 0x001D START_MAP`(见"副本内主要推送")。
  - 拒绝:同 0x000F 的 `DungeonAdmissionReject` 应答(`RejectEntryAdmissionAsync` :4606-4627、`RejectSelectionAsync` :4629-4683)。
- **行为**:
  - 门锁:owner gate + cohort gate 串行化"队长消费 selection 或队员回城"的单一迁移(`HandleSelectDungeonCore` :2027-2066)。
  - 校验:必须在当前 selection 内发起(:2150-2171)、队伍变更未重绑拒绝(:2177-2196)、cohort 校验(:2197-2216)、已通关安图恩跟随者的重复 SELECT 忽略(:2121-2149)。
  - 组队准入:队长先构建全员 plan(每个成员 selection 一致 + 门票/疲劳/次数校验,`TryBuildPartyEntryAdmissionPlans` :3528-3628,任一失败按成员槽位发定向拒绝),再逐员 `BeginRun`/附着共享 instance、按槽位排序 fan-out。
  - 加载投影:每次入场/过图分配自增 `loadingProjectionId` 并 `run.TryClaimLoadingProjection` 抢占(`:3418-3421`;`Network/handlers/Dungeon/DungeonMapHandler.cs:58-68`),后续每包发送前都复核 run/room/projection 三重代际,过期即中止。

### CMD 0x0025 (37) `FINISH_LOADING` — 加载完成(加载栅栏)— 注册在 town 组
- **注册**:`GameProtocolHandler.cs:780-781` → `DungeonLoadingCoordinator.HandleFinishLoadingAsync`。
- **请求**:无独立 parser;body 不解读(转发 town 时也不校验)。
- **应答/推送**:不回 CMD 应答——A21 客户端以 `NOTI 0x001E FINISH_LOADING` 为完成通知,body 固定 `i32 0 + u8 0`(`Network/Builders/Town/FinishLoadingBuilder.cs:7-14`),发送处 `Network/handlers/TownHandler.cs:592-597`;无 run 时才补 CMD 0x0025 成功 ack(`TownHandler.cs:580-590`)。
- **行为**:三分流——塔(`TryConsumeStageLoadingRelease` 一次性放行,:71-100)/无 run 走 town 常规/普通副本走栅栏(:122-169):房间内 `MarkLoadingReady` 记账,全员就绪才向房内所有在线成员群发 `NOTI 0x001E` 并启动电梯定时器(:187-236);START_MAP 投影启动时挂 **45s 超时**(`:16-17, 256-271`),超时强制放行已就绪者、未就绪者代打 0x002A 放弃回城(:297-393);stale/duplicate FINISH_LOADING 静默丢弃(:146-154)。

### CMD 0x002A (42) `GIVEUP_GAME` — 加载中放弃(通用放弃)— 注册在 town 组
- **注册**:`GameProtocolHandler.cs:782` → `DungeonLoadingCoordinator.HandleGiveupGameAsync`(:102-120)→ `TownHandler.Handle_ENUM_CMDPACKET_GIVEUP_GAME`;放弃后若 run 已摘除则向 Raid 通报 abort(:113-119)。
- **请求**:无独立 parser。
- **应答/推送**:见 CMD 0x0084(同一处理函数,回城投影链)。
- **行为**:与 0x0084 共用 `TownHandler.Handle_ENUM_CMDPACKET_GIVEUP_GAME`(`Network/handlers/TownHandler.cs:1029-1144`);无 run 但有 selection 时走"selection 返还城镇"分支(cohort 门锁 + `TryBeginReturn`,:1032-1115);有 run 时走 run 放弃(:1117-1143)。加载超时器也用此入口清理未就绪成员(`DungeonLoadingCoordinator.cs:359-392`)。

### CMD 0x02E0 (736) `REJOIN_DUNGEON` / CMD 0x02E1 (737) `CANCEL_REJOIN_DUNGEON` — 断线重连
- **请求**:rejoin `i32 partyId + i32 targetParticipantUserId`(8B),cancel `i32 partyId`(4B),`Network/Parsers/Dungeon/DungeonRejoinRequestParser.cs:31-86`。
- **应答/推送**:成功 ack(`CommonPacketBodyBuilder.BuildSuccessAck`)+ `NOTI 0x02AB REJOINABLE_DUNGEON`;拒绝 ack `[0x04]`;cancel 成功另推 `NOTI 0x02AA CANCEL_REJOIN_DUNGEON`(u16 uid)。断线期间登录时投影候选:`NOTI 0x02A8 DISCONN_DUNGEON_INFO` + 每队员一条 `NOTI 0x02A9 REJOIN_DUNGEON`(u16 uid)(`Network/handlers/Dungeon/DungeonRejoinCoordinator.cs:70-128, 258-263, 339-344, 446-463`;builder `Network/Builders/Dungeon/DungeonRejoinNotificationBuilder.cs`)。
- **行为**:
  - 断线侧:`DetachRunOnNetworkDisconnect` 把 run 从会话摘除但**保留在 InstanceRegistry**(`DungeonRunLifecycle.cs:686-736`):定时器挂起(`SuspendForNetworkDetach` :719)、会话经验落库(:720-723)、玩家 UserState/selection 清空。
  - 重连侧:角色迁移 gate → 会话内 offer 表(按 partyId/账号/角色校验)→ `registry.TryResume`(以 `AttachmentGeneration` 防并发)→ 恢复队伍投影 → 离开城镇投影 → `AttachResumedRun` 把 run 挂回 `Player.CurrentRun`(`DungeonRunLifecycle.cs:738-758`,UserState=0x01)→ 重放挂起的参与者效果/结算(`RecoverDungeonParticipantEffectsAsync`,`Network/handlers/DungeonHandler.cs:306-321`:击杀效果、结算呈现、翻牌定时器 `CardRewardCoordinator.RecoverTimer` :123-184、死亡复活定时器 :629-657)。任一步失败逐级回滚 registry/party(:210-240)。offer 表每会话一份、操作串行化(:156-176)。
  - cancel:`registry.TryCancel` 后释放候选(:323-337)。

---

## 推进与战斗

### CMD 0x002D (45) `MOVE_MAP` — 过图
- **请求**:固定 64B:`u8 nextX, u8 nextY, u32 pathX, u32 pathY, u8 moveMode, u16 trapBits, 8×u16 memberMapClear, 8×u32 memberMapElapsed, u16 clientTimingToken, u8 clientStateFlag` `Network/Parsers/Dungeon/MoveMapRequest.cs:46-85`。
- **应答/推送**:无 CMD 应答;服务端向本人(及被带动的队员)发 `NOTI 0x001D START_MAP`(首次)或 `BuildStartMapRevisit`(重访,actorCount=0,`DungeonNotificationBuilder.cs:259-280`)。
- **行为**(`Network/handlers/Dungeon/DungeonMapHandler.cs:92-253`):
  - 前置拒绝:死亡参与者(:97-104)、塔内改走 `DeathTower.TryHandleMoveMap`(:107-113)、`Phase>=Cleared`(:125-129)、血祭场未完图(:131-141)、Hell 派对进行中(:143-147)、电梯出口校验(:149-156)、迷宫拓扑外移动(`DungeonRoomTopology.TryResolveMoveTarget` :158-170,目标点会被归一化)。
  - 竞态防护:先抢占 loadingProjectionId(:183-185)再从名册捕获同房间队员并让他们也 claim 同一投影 id(:186-196, :331-384);`lock(run.SyncRoot)` 内完成房间态读改写、**await 一律在锁外**(:492-495, :723)。
  - `moveMode==1` 触发分层地图推进(:200-216);机制地图覆盖经 `DungeonMechanismCoordinator`(:175-181, :218-222)。
  - 组队:队长换图后逐队员重放 START_MAP(`BroadcastMoveMapToPartyAsync` :256-329),队员是 follower 不自发 MOVE_MAP;每一步都复核 leader/member 的 run-room 代际。

### CMD 0x0027 (39) `DIE_MONSTER` — 击杀上报
- **请求**:`u16 localIndex(怪物序号), u16 userId, [u16 0xFFFF=map-owned passive 签名]`;passive 标志从 body[20] 的 atkCount 偏移推导、capture 标志从 body[22] 的 attackRecordCount 偏移推导,`Network/Parsers/Dungeon/DieMonsterRequest.cs:46-79`。
- **应答/推送**:无 CMD 应答;成功后发 `NOTI 0x0026 DIE_MONSTER`:body = `3 + dropCount×48 + 4` 字节——`u16 seq, u8 dropCount, drop×{u16 sceneSlot, u32 itemId, u8 upgrade, u32 value, u16 durability, u8 amplifyType, u16 amplifyValue, u32 dropUnixSeconds, 24×u8 0, u16 ownerActorId, u16 0}`,尾部 `00 00 FF 00`(`Network/Builders/Dungeon/DungeonNotificationBuilder.cs:283-321`;发送处 `Network/handlers/Dungeon/DungeonKillApplicationService.cs:1544-1557`)。
- **行为**(核心在 `DungeonKillApplicationService.ProcessAsync` :70-203 → `ProcessCoreAsync` :305-675):
  - 幂等/分流:instance Ending/Ended 或 `Phase>=Cleared` 时只回 NOTI 不再奖励(:79-91);塔内用 `RoomKilledSeqIds` 去重(:93-106);普通副本先 `TryCanonicalizeSharedDeath` 把本地上报规范化为共享死亡事实(:118-127),再冻结房间名册、按 `SourceEventId` 做参与者效果预留 TryBegin/TryCommit(:130-165, :189-196),重复上报直接回 NOTI。
  - passive-object(符文/机关物)单独分支:记录地图对象死亡 → 任务同步 → 机制回调 → 命中 ClearCondition 时提交通关(`Network/handlers/Dungeon/DungeonCombatHandler.cs:88-163`;判定 `ShouldTreatAsPassiveObject` :181-196)。
  - 奖励:`ApplyParticipantRewardAsync`(:751-1105)计算经验(PVF 标准公式×难度/队友数/剧情加成 + 成长契约 + 快捷栏纹章装备加成,:798-875)、投落(`SequentialLoot` 每日守卫 + `DropService.GenerateAndRegister`,顺序本掉落结果冻结复用 :940-1023)、经验入账并发 `NOTI 0x0025 EXP`(`ProgressNotifications.SendExpGrantNotificationAsync`,tag=`DUNGEON_KILL`,:1090-1102)。
  - 同步链:怪物掉落任务 `QuestDrops.CheckMonsterDrop`(:481-497)→ 击杀计数任务 `DungeonActorQuestSync.SyncAsync`(:499-508)→ 机制(:524-538)→ 房间清空判定 `TryCommitCurrentRoomClear`(:540-554)→ 通关意图(见 SET_PLAY_RESULT 前置)。
  - 组队 fan-out:本地上报在 commit 后 relay 到冻结名册全体(`RelayToFrozenRosterAsync` :1389-1447,Origin=PartyRelay),并完成延迟的通关 fan-out(:1449-1463)。

### CMD 0x0028 (40) `DIE_CHARACTER` — 角色死亡
- **请求**:无独立 parser,handler 仅日志原样 body(`DungeonCombatHandler.cs:272-273`)。
- **应答/推送**:无 CMD 应答;向 instance 名册广播 `NOTI 0x0020 DIE_STATE`:`u16 actorId, u8 state(0=死亡), u8 0`(`BuildParticipantLifeStateBody` :942-951;广播 `BroadcastParticipantLifeStateAsync` :542-596,名册外补发本人)。
- **行为**(:270-344):按 `ResolveCharacterDeathFlow` 分流——剧本死亡/塔(取消复活倒计时)、锦标赛(起 10s 复活倒计时)、普通(进入**全灭计时**:`MarkParticipantDead` + `PartyWipeDelay` 定时器,:374-452,到期全员 `TownReturn.ReturnAsync` 并以 0x007B 作为成功 ack :496-535)。Raid 分流:安图恩副本死亡额外通报 `RaidHandler.HandleDungeonCharacterDeathAsync`(`GameProtocolHandler.cs:661-675`)。

### CMD 0x0029 (41) `USE_COIN` — 复活币
- **请求**:`u16 targetActorId`(body<2B 时回落自身 userId,`DungeonCombatHandler.cs:808-811`)。
- **应答/推送**:成功先 `NOTI 0x0020 DIE_STATE`(state=1 复活,:906-913)再 CMD 0x0029 ack `[u8 1, u16 targetActorId]`(:915-920);失败 ack `[u8 0, u16 targetActorId]`(:878-891)。不补发 0x000E 背包刷新(客户端本地预扣,:916 注释)。
- **行为**(:808-940):锦标赛内只扣币(`TryConsumeReviveCoin` 校验 InventoryLease 归属 + `ReviveCoinService.TryConsume` :925-940);普通副本在 `Instance.ParticipantLifeGate` 下从名册找复活目标、`CanReviveParticipant` 通过才扣币并 `MarkParticipantAlive`(:837-875)。成功后取消死亡回城倒计时(:904)。Raid 分流:复活成功通报 `HandleDungeonCharacterReviveAsync`(`GameProtocolHandler.cs:677-697`)。

### CMD 0x007B (123) `DEATH_RESPAWN` — 死亡回城确认
- **请求**:无独立 parser,body 原样日志(`DungeonCombatHandler.cs:348-349`)。注:cmd 枚举表此处名为 `CHARACTER_STATISTIC`,服务端按 DEATH_RESPAWN 语义处理(`GameProtocolHandler.cs:723`)。
- **应答/推送**:无独立应答;回城投影链完成后由 `TownReturn` 以 CMD 0x007B 补发成功 ack(见 0x0084)。
- **行为**(:346-372, :708-777):必须存在存活的 `CombatDeathRespawn` 定时器 ticket(:357-363);`ResolveDeathRespawnDeadlineAsync` 校验 run/run 状态/ticket 代际(:719-725),未到 `DeathRespawnAvailableAt`(死亡时起 **10s**,`DeathRespawnDelay` :33)的客户端请求被延迟(:727-735);锦标赛按裁决走发奖或忽略(:737-766);普通路径 `TownReturn.ReturnAsync(reason=DeathRespawn, ack=0x007B)`(:768-774)。定时器到期强制回城(:685-706);重连恢复定时器(:629-657)。Raid 分流同 0x0084 wrapper(`GameProtocolHandler.cs:721-723` 中 0x0048/0x007B 共用 `HandleRaidAwareDungeonExit` :644-659)。

### CMD 0x0075 (117) `BOSS_DIE_CHECK` — Boss 死亡确认
- **请求**:`u16 userId, u16 bossSequence`(4B)`Network/Parsers/Dungeon/BossDieCheckRequest.cs:14-25`。
- **应答/推送**:无直接 CMD 应答;licensed 副本在通关事实就绪时投影 `NOTI 0x0073 BOSS_DIE_CHECK`(`u8 result=1, u8 state=1, u16 bossSeq`)+ `NOTI 0x001F ENABLE_CLEAR_DUNGEON`(`[0x00]`)前置包(`DungeonCombatHandler.cs:240-249`;builder `DungeonNotificationBuilder.cs:359-371, 354-357`)。
- **行为**(:198-250):`DungeonMechanismCoordinator.OnBossDieCheck` 解析机制性通关 → `ProcessConfirmedBossDeathAsync` 把 boss 死亡规范化进击杀管线(生成真实 seq/code 再走 ProcessAsync :205-242)→ 命中 `ShouldClearDungeon` 时 `SubmitClearIntentAsync`(:230-238)。

---

## 拾取与丢弃

### CMD 0x002B (43) `GET_ITEM` — 拾取
- **请求**:`u16 srcSlot`(地面场景槽)`Network/Parsers/Dungeon/GetItemRequest.cs:14-21`。
- **应答/推送**:
  - CMD 0x002B ack:成功 `[0x01]`(客户端先消费 1B ack 再解析通知,`Network/Builders/Dungeon/DropItemBuilder.cs:57-60`);背包满 `[0x00, 0x04]`(`DungeonCombatHandler.cs:987-993`)。
  - `NOTI 0x0027 GET_ITEM`:物品 = `u16 sceneSlot, u16 pickerActorId, u8 1, i32 0, i32 0, u16 pickerActorId, u16 dstInvSlot, u8 0`(`DropItemBuilder.cs:62-78`);金币 = 固定 117B 模板,8 条 14B 槽位条目,entry i 的 gold 在绝对偏移 `6+i*14`(按入场冻结的队内槽位投放,非 0 槽先清 entry0,`DropItemBuilder.cs:85-107`)。
- **行为**(`DungeonCombatHandler.cs:953-1076`):死亡参与者忽略(:958-966);塔内优先 `DeathTower.TryHandleGetItem`(:971-975);`ItemAcquisition.AcquireGroundDrop` 原子取地面物并入包(:979-982);金币另补主背包 0 号金币槽 `0x000E` 刷新(:1018-1021);epic piece 追加 `SendEpicPieceInfo`(:1040-1046);拾取后按 itemId 校准任务计数(:1047-1054);`SaveDirty` 落库(:997, :1060-1076)。

### CMD 0x002F (47) `DROP_ITEM` — 丢弃物品
- **请求**:固定 12B:`u16 posX, u16 posY, u8 listType, u16 slotIndex, i32 count`;仅接受 Main 列表且 count>0,`Network/Parsers/Dungeon/DropItemRequest.cs:28-49`。
- **应答/推送**:CMD 0x002F ack 成功 `[u8 1, u8 listType, u16 slot, i32 count]`、失败 `[u8 0, u8 errCode=17, u8 listType]`(`DropItemBuilder.cs:38-55`);成功后 `NOTI 0x0028 DROP_ITEM`:`u16 dropperActorId, u16 posX, u16 posY, u16 sceneSlot, 84B 通用物品条目(ItemListProtocolWriter.WriteCommonEntry84), u8 0, u16 ownerActorId`(`DropItemBuilder.cs:17-36`;发送处 `DungeonCombatHandler.cs:1122-1130`)。
- **行为**(:1078-1135):`DropService.TryDropInventoryItem` 扣背包 + 登记地面物(:1096-1101);失败带 errCode=17(:1104-1108)。

---

## 结算与翻牌

### CMD 0x002E (46) `SET_PLAY_RESULT` — 结算
- **请求**:无独立 parser;handler 只读 `body[10]` 作为客户端评分点(`SetPlayResultRankPointOffset=10`,`Network/handlers/Dungeon/DungeonSettlementHandler.cs:34, 1899-1907`)。
- **应答/推送**(三连呈现,`ProjectSettlementPresentationAsync` :489-763):
  1. `NOTI 0x0022 PLAY_RESULT`:`u8 rankIndex(恒 0), i32 clearTimeMs, u8 timeBonusPoint, u8 clientRankPoint, u8 questMaze, u8 memberCount(1), u16 userId, i32 clearTimeMs, u8 newBest`(`DungeonNotificationBuilder.cs:426-449`);
  2. `NOTI 0x0025 EXP`(经 `SendExpGrantNotificationAsync`,tag=`SET_PLAY_RESULT`,:562-574);
  3. `NOTI 0x0023 CLEAR_DUNGEON_REWARD`(`DungeonNotificationBuilder.cs:461-615`):109B 基础块(4×u32 + u8 + 23×i32 奖励槽,槽位映射 :489-501)+ 2×u8 变长列表计数 + 5×u32 + 评分四元组(champion/superChampion/boss,:522-529)+ `u32 count + count×{u32 objectKey, u32 exp}` 对象经验(:531-540)+ 8 个按队伍槽位的免费翻牌座位(count 前缀,:549-597)+ 付费翻牌成本 + 固定尾巴。
  - 另:绝望之塔补 `0x015C` 通关奖励与背包刷新(:624-660);升级追发 `SendInDungeonLevelUpFollowups`(:673-685)。
  - 无 CMD ack(A21 用 NOTI 0x0022 驱动 UI)。
- **行为**:
  - 前置:必须已通关(`RunState==Cleared && SettlementState==Preparing`,:501-502)且权威结算效果已 commit(:508-518);提前到达的 0x002E 会被 `TryQueueSettlementPresentation` 挂起待通关提交后继续(:119-131)。
  - 状态迁移:呈现完成 `TryMarkResultShown()`(:740-745);无需翻牌流的副本直接 `TryCompleteSettlement`(:747-748)。
  - 翻牌流调度:重置 4+4 卡位并 `CardRewards.ScheduleAutoFlow(layoutDelayMs=2000, autoFlipDelayMs=4000)`(:701-725)。
  - 幂等:整个呈现包在 `DungeonEffectReservation` 下执行,失败 TryFail 可重放(:520-526, :750-762)。
  - licensed 副本改道专用 0x032C 状态机(:75-110);血祭场忽略(:111-117)。
  - Raid 分流:安图恩副本抑制普通翻牌流(CancelAutoFlip + 置 `CardsRevealed`,`GameProtocolHandler.cs:627-640`)。

### CMD 0x0045 (69) `CARD_START_REQUEST` — 翻牌开始(手动)
- **请求**:无 body 内容要求(handler 不解析,`DungeonSettlementHandler.cs:2316-2337`)。注:cmd 枚举表此处名为 `SCORE_SCROLL_STATE`,服务端按翻牌开始处理(`GameProtocolHandler.cs:719`)。
- **应答/推送**:CMD 0x0045 ack `[0x01]` + CMD 0x0046 ack(翻牌布局)`u8 1 + 8×u16 eligibility(每队伍槽位是否有卡,-1=无)`(`Network/handlers/Dungeon/CardRewardNotificationSender.cs:74-86, 158-171`)。
- **行为**:licensed/非标准呈现副本忽略(`DungeonSettlementHandler.cs:2319-2335`);仅 `SettlementState==ResultShown` 受理,`CardRewardCoordinator.HandleCardStartRequest` → `TryRevealCardsAsync(source=Manual-start)`,成功即迁移 CardsRevealed 并重排 4s 自动翻定时器(`Network/handlers/Dungeon/CardRewardCoordinator.cs:332-349`)。

### CMD 0x0047 (71) `SELECT_CARD` — 翻牌
- **请求**:`u8 cardType(0=免费/1=付费), u8 cardIndex(0-3)`(2B,`CardRewardCoordinator.cs:190-197`)。
- **应答/推送**:CMD 0x0047 ack(翻牌信息)`u8 1` + 8 个队伍槽位:`u8 freeSelectorPartySlot, u8 paidSelectorPartySlot, [已占付费槽: u8 2, u32 0, i32 gold, u32 itemId, i32 count | 未占: u8 0], u8 0`(`CardRewardNotificationSender.cs:120-156`);布局同步广播给房内其他队员(:972-1009);奖励到账后按列表组发 `0x000E` 背包刷新,消耗金卡契约时补 `PREMIUM_SERVICE` 刷新(:473-493)。
- **行为**:
  - **代际门锁**:选卡全程在 `run.Settlement.CardProjectionGate` + `run.Instance.CardRewardSendGate/CardRewardProjectionGate` 三把锁内(:219-245, :760-845),每步前后以 `Player.IsCurrentDungeonRun(runIdentity)`(含 RunGeneration)复核,run 换代立即回滚卡位(:291-298, :847-863)。
  - 状态迁移:`ResultShown` 阶段的 SELECT_CARD 触发翻牌布局揭示(:202-213);`CardsRevealed` 阶段才真正占位,免费侧成功即 `CancelAutoFlip`(:275-276);占位检查含全队名册(`IsCardPositionOccupied` :888-913),失败回推最新布局(:247-260)。
  - **4s 自动翻**:`ScheduleAutoFlow` 两段定时器(2s 布局 + 4s 自动)挂在 `SettlementCardAutoFlow` 键,`RunTimerDetachPolicy.SuspendUntilResume`(:67-97);到期 `AutoFlipFreeCard` 自动翻第一个空闲免费卡位并 deliver(:399-445, :706-737);ticket 代际 + run 代际双校验防旧定时器打新局(:739-747);投影失败时重启 4s 重试(:278-290)。
  - 付费侧:库存校验 `CanPayPaidCard`(:255-261)+ 安图恩觉醒付费选择预留(:231-240);奖励交付 `DeliverCardRewards`(:447-528)。

### CMD 0x0048 (72) `EPLP_COMMAND` — 结算界面状态命令
- **请求**:通用布局 `u8 state, u8 option`(2B)。分流 parser:塔 `DeathTowerEplpCommandParser`(state/option 走 `DeathTowerEplpCommandRules` 白名单校验,`Network/Parsers/Dungeon/DeathTowerEplpCommandParser.cs:7-34`);血祭场 `BloodAltarEplpCommandParser`(仅长度校验,`Network/Parsers/Dungeon/BloodAltarEplpCommandParser.cs:7-23`)。
- **应答/推送**:普通副本出口/继续时 CMD 0x0048 ack `[u8 1, u8 state, u8 option]`(`CardRewardNotificationSender.cs:96-103`);塔/血祭场由各自协调器应答。
- **行为**(`DungeonSettlementHandler.HandleEplpCommand` :2243-2314):分流顺序 = 塔(:2245-2251)→ 血祭场(:2252-2256)→ 普通;普通先 `PrepareEplpCommand`(`CardRewardCoordinator.cs:351-397`):`ResultShown` 阶段先揭示布局,随后在 CardProjectionGate 内 `CancelAutoFlip`——即 **EPLP 命令优先于自动翻牌**;linked 副本的"继续挑战"选项转入 `EnterLinkedDungeonAsync`(:2284-2300);`option<=2` 走标准 EPLP 处理(:2301-2308),否则发出口 ack(:2310-2313)。licensed 副本持锁期间清空卡计划防旧定时器复活布局(:2261-2273)。Raid 分流:0x0048 也经 `HandleRaidAwareDungeonExit` wrapper(`GameProtocolHandler.cs:721`)。

### 通关提交(0x002E/0x0045/0x0047 的前置,无独立 CMD)
- 入口 `DungeonSettlementHandler.SubmitClearIntentAsync`(:2343-2420,boss 击杀/passive object/任务/机制/剧本死亡多源汇入):创建共享 `DungeonClearedFact` → 冻结 instance 名册 → `CullRemainingBlockingActorsOnClearAsync` 清场 → 本参与者 `ProcessClearForParticipantAsync`(:2458-2794,`TryBeginClearCommit` 幂等 + 结算经验预计算 + `TryQueueSettlementPresentation`)→ relay 名册其余成员(:2916-2919)。NOTI 31 `ENABLE_CLEAR_DUNGEON` 由此阶段投出(注释 :2339-2342)。

---

## 退出 / 教程 / 杂项

### CMD 0x0084 (132) `BACK_2_VILLAGE` — 回城(GIVEUP_GAME 语义)— 注册在 town 组
- **注册**:`GameProtocolHandler.cs:783`,经 `HandleRaidAwareDungeonExit` wrapper(:644-659,run 摘除后向 Raid 通报 abort)→ `TownHandler.Handle_ENUM_CMDPACKET_GIVEUP_GAME`。注:cmd 枚举表 0x0084 名为 `BACK_2_VILLAGE`,0x002A 才是 `GIVEUP_GAME`;两者在服务端共用同一处理函数。
- **请求**:无独立 parser。
- **应答/推送**(回城投影链,`Network/handlers/Dungeon/DungeonTownReturnCoordinator.cs:64-151`):`NOTI 0x0003 USER_STATE`(自己,状态回 0)→ 城镇在册投影(失败回落 `NOTI 0x0017 USER_AREA` + `NOTI 0x0018 AREA_USERS`,:109-125)→ [按需] CMD 0x0084/0x007B 成功 ack(:128-136)→ `NOTI 0x00CA VILLAGE_OBJECT_LIST` `[0x00]`(:137-140)→ subtype0 用户信息广播 + 婚礼回放三包(:143-145)。
- **行为**:run 分支在 cohort gate 下重校验 run 代际(`TownHandler.cs:1117-1143`);selection 分支支持组队 fan-out 返还(:1075-1081);回城锚点优先级 = selection 锚 > 旧 run 锚 > 频道临时出生点 > 副本门口配置(`DungeonRunLifecycle.ApplyTownReturnAnchor` :474-522,城镇投影 state 归一为 0)。run 摘除统一走 `EndRunAsync(ReturnToTown)`(取消定时器、经验落库、机制清理、宠物收尾,`DungeonRunLifecycle.cs:549-628`)。

### CMD 0x008F (143) `CHANGE_TUTORIAL_FLAG` — 教程标记
- **请求**:`u8 mode, u32 flagIndex, u8 rewardFlag`(紧凑 6B;完整抓包 15B,后 9B 保留),`Network/Parsers/Dungeon/ChangeTutorialFlagRequest.cs:21-38`。
- **应答/推送**:CMD 0x008F ack `u8 resultCode=1 + u8 count + count×{u16 slot, u32 itemId, u32 count}`(`Network/handlers/Dungeon/DungeonTutorialHandler.cs:177-187`)。
- **行为**(:104-221):`rewardFlag!=0` 时按 PVF `escalade tutorial reward` 发奖(:135-162);`flagIndex==30` 补完 SELECT_DUNGEON 挂起的教程入场投影(:164-175);`flagIndex==31` 教程毕业——持久化 skip 标记并回城(`ReturnToVillage` :268-322,投影序列同 0x0084 但 reason=TutorialExit)。

### CMD 0x00BF (191) `DUNGEON_EVENT_STORY_PAUSE` — 剧情暂停
- **请求**:`u8 pauseFlag, u8 requestType`(最少 2B,`DungeonTutorialHandler.cs:38`)。
- **应答/推送**:副本内推送 `NOTI 0x00AA DUNGEON_EVENT_STORY_PAUSE`:`u16 uid, u8 pauseFlag, u8 requestType`(:65-69);town 态特例:回城后残留的 STORY_PAUSE 改发 `NOTI 0x00CA` + 6B 全零 CMD ack(:40-56)。
- **行为**:pauseFlag 交 `DungeonMechanismCoordinator.OnStoryPause`,命中剧情通关条件时 `SubmitClearIntentAsync`(:71-92)。

### CMD 0x01E4 (484) `TUTORIAL_LEVEL_UP` — 教程直升
- **请求**:body 空(86JP 布局,`DungeonTutorialHandler.cs:223-230`)。
- **应答/推送**:推送 `NOTI 0x0025 EXP`(:244-253);CMD 0x01E4 ack 成功 `[0x01]` / 条件不符 `[0x13]`(:232-236, :259)。
- **行为**:仅 `Level==1` 且在 run 中受理;直升 Lv2(`TutorialTargetLevel=2` :24)+ SP 状态持久化 + `SendInDungeonLevelUpFollowups`(:238-256)。

### CMD 0x01DE (478) `DUNGEON_SCENE_UNIQUE_ID_REPORT` — 场景唯一 ID 回报 — 注册在 misc 组
- **请求**:原样回报值:`body>=4B` 读 `u32`(否则 `u16`),取低 16 位为 sceneUniqueId;注册处注记该 CMD 枚举表名为 `INFORM_NOTICE`,服务端按副本场景回报处理(`GameProtocolHandler.cs:993`)。
- **应答/推送**:CMD 0x01DE ack 成功(`CommonPacketBodyBuilder.BuildSuccessAck`)。
- **行为**:非零写入 `Player.DungeonSceneUniqueId`(场景实体归属键),零值忽略仅日志(`Network/handlers/DungeonHandler.cs:323-344`);run 摘除/回城时清零(`DungeonRunLifecycle.cs:714, 886`)。

---

## 副本内主要推送(服务端→客户端,信封 type=0x00)

| NOTI | 枚举名 | 触发时机 | 布局要点 | 构造处 |
|---|---|---|---|---|
| 0x0003 | USER_STATE | 进入选图/回城/重连(自己或全体) | `u8 count + count×{u16 uid, u8 state}` | `EnterSelectDungeonStateBuilder.cs:12-26` |
| 0x001B | ENTER_SELECT_DUNGEON | 0x000F 受理后(教程延后) | 见 CMD 0x000F | `EnterSelectDungeonStateBuilder.cs:64-96` |
| 0x001C | DUNGEON_INFO | SELECT_DUNGEON 准入链 | dungeonId/难度/迷宫/boss 坐标/minimap 组,基础 32B | `DungeonNotificationBuilder.cs:56-108` |
| 0x001D | START_MAP | 入场与每次 MOVE_MAP(含队员带动) | `u8 x, u8 y, u8 layeredFlag, i32 seed, u8 hellMode, u8 0, i32 roomStateValue, u8 roomStateFlag, i32 mapId, u8 actorCount, actor×20B{u16 templateOrder, i32 packetIndex, u16 seq, i32 code, u8 lv, u8 type, u8 flag0, u8 flag1, i32 extra, u8 0}, u8 extraCount, extra×19B, u8 fogFlag, ridable 分组, u8 partyMemberIndex`;重访 actorCount=0 | `DungeonNotificationBuilder.cs:161-257, 259-280`;发送 `DungeonMapHandler.cs:870-884` |
| 0x001E | FINISH_LOADING | 加载栅栏全员就绪(或超时/塔放行/无 run) | `i32 0 + u8 0` | `FinishLoadingBuilder.cs:7-14` |
| 0x001F | ENABLE_CLEAR_DUNGEON | 通关提交/boss check 前置 | `[0x00]` | `DungeonNotificationBuilder.cs:354-357` |
| 0x0020 | DIE_STATE | 死亡/复活广播 | `u16 actorId, u8 state, u8 0` | `DungeonCombatHandler.cs:942-951` |
| 0x0022 | PLAY_RESULT | SET_PLAY_RESULT 呈现 | 16B,见 CMD 0x002E | `DungeonNotificationBuilder.cs:426-449` |
| 0x0023 | CLEAR_DUNGEON_REWARD | SET_PLAY_RESULT 呈现 | 23 槽奖励 + 8 翻牌座位,见 CMD 0x002E | `DungeonNotificationBuilder.cs:461-615` |
| 0x0025 | EXP | 击杀经验、结算经验、教程直升(固定 83B) | `u8 level, u32 totalExp, u32 partyExp, SP/TP 4×u16, u32 pvpVP, 各加成 u32 ×n, u8 varCount, 荣誉/胶囊…`(A21 无 A12 成员奖励字段) | `ExpNotificationBuilder.cs:23-70`;投递 `DungeonProgressNotificationProjector.cs:146+` |
| 0x0026 | DIE_MONSTER | 每次击杀(含 relay) | `3 + dropCount×48 + 4` | `DungeonNotificationBuilder.cs:283-321` |
| 0x0027 | GET_ITEM | 拾取成功(物品/金币两形) | 见 CMD 0x002B | `DropItemBuilder.cs:62-107` |
| 0x0028 | DROP_ITEM | 丢弃成功 | 见 CMD 0x002F | `DropItemBuilder.cs:17-36` |
| 0x0073 | BOSS_DIE_CHECK | licensed 通关前置 | `u8 result, u8 state, u16 bossSeq` | `DungeonNotificationBuilder.cs:359-371` |
| 0x00AA | DUNGEON_EVENT_STORY_PAUSE | 剧情暂停回显 | `u16 uid, u8 pause, u8 reqType` | `DungeonTutorialHandler.cs:65-69` |
| 0x015B | USER_APC_INFO_TOD | 绝望之塔 START_MAP 后 APC 基础/当前两层 | (塔专用) | `DungeonMapHandler.cs:892-921` |
| 0x02A8/0x02A9/0x02AB | DISCONN_DUNGEON_INFO / REJOIN_DUNGEON / REJOINABLE_DUNGEON | 断线候选投影、重连受理 | partyId/uid/UiState | `DungeonRejoinCoordinator.cs:70-128, 197-207, 258-263` |

特殊副本(电梯/气槽/minimap 图标/buff/召唤)公共推送由 `SpecialDungeonNotificationSender` 统一分发:`NOTI 0x00FE ELEVATOR_CLEAR_TIME_CHECK`、`NOTI 0x022D GAUGE_OBJECT_BAR_DATA`、`NOTI 0x022F MINIMAP_ICON_INFO`、buff 增删、`CMD 0x026B TIMER_MODIFY_INFO` ack、`CMD 0x0211 SUMMON_MONSTER` 应答(`Network/handlers/Dungeon/SpecialDungeonNotificationSender.cs:29-178`);START_MAP 投影期间这些推送统一经 `TrySendLoadingProjectionPacketAsync` 带 cancellation 发送,投影被抢占即静默丢弃(`DungeonMapHandler.cs:70-90`)。