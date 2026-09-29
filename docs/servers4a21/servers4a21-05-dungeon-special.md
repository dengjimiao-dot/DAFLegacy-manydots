# 特殊副本与副本机制(ServerS4A21)

> - **本册导航**:总览/帧结构/信封/分发架构/已实现 CMD 总表见 [servers4a21-00-overview.md](servers4a21-00-overview.md)。
> - 仅写 A21(`ServerS4A21\Server\DfoServer`)侧实现,供 DFLegacy 回填参考。路径相对 `DfoServer` 根;行号为整理时点状态。
本域覆盖死亡塔、地狱派对、许可副本(许可证地下城)、神秘商店、副本机制小命令(血祭祭坛/锦标赛/时空螺旋/海战小游戏等)与特殊入场(异次元裂缝/圆环/连续副本)。主流程(入场选本、START_MAP、过图、翻牌结算)见主流程册,交叉处仅一句带过。

## 域综述:特殊副本如何挂接主流程

A21 侧特殊副本不另立协议栈,全部寄生在通用 `DungeonRun` 生命周期之上,形成三种挂接模式:

1. **入场复用**:`CRACK_OF_DIMENSION` 在 handler 内把 8B 请求改写成 9B 的合成 `SELECT_DUNGEON` body 后直接调用 `HandleSelectDungeonCore`(Network/handlers/Dungeon/DungeonEntryHandler.cs:1696-1719),入场、过图、结算零改动;`REQUEST_CIRCLE_ENTER` 则只做"选择态握手",不触发入场。
2. **Run 附加运行时**:死亡塔把 `DeathTowerSession` 挂在 `run.Tower` 上(Network/handlers/Dungeon/DeathTowerCoordinator.cs:2075-2083),血祭祭坛/锦标赛由 `DungeonHandler` 构造期注入的 coordinator 持有(Network/handlers/DungeonHandler.cs:115-131);塔的过图拦截在主流程 `HandleMoveMap` 开头 `TryHandleMoveMap` 返回 true 即短路(Network/handlers/Dungeon/DungeonMapHandler.cs:109)。
3. **机制命令统一入口**:`0x013C/0x0211/0x0253/0x026B/0x026D/0x0270/0x01CA/0x01CB/0x0102/0x00FF/0x013D/0x0408` 共 12 个 CMD 全部注册到 `HandleDungeonMechanismCommand`(Network/Protocol/GameProtocolHandler.cs:728-752),由 `DungeonCommandParser.TryParse` 按 wireType 解析成强类型 `DungeonCommand`(Network/Parsers/Dungeon/DungeonCommandParser.cs:8-137),再经 `DungeonMechanismCoordinator.OnCommandReceivedAsync` → `DungeonCommandReceivedDispatcher.DispatchAsync` 按 command 类型分派到 SpecialDungeonNotifier / TournamentDungeonCoordinator / BloodAltarDungeonCoordinator / TimeSpiralDungeonCoordinator / DungeonNpcItemDropCoordinator(Network/handlers/Dungeon/DungeonCommandReceivedDispatcher.cs:29-105)。信封约定:所有应答用 `GamePacketEnvelopeBuilder.Build(0x01, type, body)`,推送用 `0x00`(15B 信封,见主流程册)。

解析失败的统一行为:`DungeonCommandParser` 返回 false 时仅记 `[DungeonCommand] parse rejected` 日志、不回包(Network/handlers/DungeonHandler.cs:275-286);分派器收到无主命令也仅记日志(Network/handlers/Dungeon/DungeonCommandReceivedDispatcher.cs:99-104)。

断言中路径均相对 `DfoServer` 根;CMD/NOTI 编号同时给出十六进制与十进制。

---

## 一、死亡塔(Death Tower)

死亡塔会话在选本时由 `TryCreateSession` 创建(Network/handlers/Dungeon/DeathTowerCoordinator.cs:69-81),入场推送 NOTI 142(0x008E TOWER_INFO,dungeonId u32 + endStage u16 + mode u8 + randomBuff u8;Network/Builders/Dungeon/DeathTowerPacketBuilder.cs:17-29,发送 :89-92)与 NOTI 143(0x008F STAGE_MAP,11B 头 + 14B/怪 + 1B 数 + 18B/掉落;DeathTowerPacketBuilder.cs:34-76,发送 SendStageMap :1584-1606),再补 NOTI 0x001E FINISH_LOADING(:97-99)。塔的结算三段推送(0x0090 ranking → 定时 → 0x0091 reward → 0x0092 eplp)由 `SendSettlement` 状态机驱动(:239-475,含断线恢复 :1316 起),EPLP 命令复用主流程 0x0048,在 `DungeonSettlementHandler.cs:2245` 先问塔。

### CMD 0x009F (159) `DEATH_TOWER_STAGE_CMD` — 塔层阶段命令

- **请求**: 1B commandType(后随字节忽略);无独立 parser,handler 实读 body[0]。注册 Network/Protocol/GameProtocolHandler.cs:753。
- **应答/推送**: 无直接应答;阶段推进的可见效果是下一层的 NOTI 0x008F(STAGE_MAP)与结算推送(见上)。
- **行为**:
  - `session.Player.DeathTowerState == null`(不在塔内)时直接忽略(Network/handlers/Dungeon/DeathTowerCoordinator.cs:187-192)。
  - commandType=1 → `tower.SetFighting()`(战斗开始);commandType=2 → `tower.SetCleared()`,并同步当前层通关地图任务进度(:200-215)。
  - commandType=2 且为最后一层 → 立即进入结算 `SendSettlement("final-stage")`(:210-214)。
  - 其他值仅记日志(:216-218)。86JP 客户端可能跳过 0x009F(2) 直接过图,过图路径容忍该缺失(:123-124)。
- **备注**: 过图(`TryHandleMoveMap`)要求 state>=1 且非末层才 `TryAdvanceStage` 推下一层,末层通关后由 0x009F(2) 或死亡触发结算(:102-156,158-183)。

### 塔内背包拦截:CMD 0x0012 (18) DELETE_ITEM / 0x0013 (19) MOVE_ITEMSPACE / 0x0014 (20) SORT_ITEM / 0x002C (44) USE_STACKABLE

四个背包命令在注册层做成"拦截链":先试塔,塔不认领再落回常规背包 handler(0x0012/0x0014:Network/Protocol/GameProtocolHandler.cs:459-479;0x0013 在交易/骑士盾之后:465-472;0x002C 在宠物/副本使用链之前:561-575)。塔侧入口 `TryHandle*` 是 DungeonHandler 的转发(Network/handlers/DungeonHandler.cs:258-268)。

- **请求**(复用普通背包 body 布局,塔专用 parser Network/Parsers/Dungeon/DeathTowerInventoryCommandParser.cs):
  - USE_STACKABLE: ≥7B = slot i16 + listType u8 + instance i32(+ 期望 itemId i32,可选 :47-61);
  - MOVE: ≥14B = srcList u8 + srcSlot i16 + srcInstance i32 + count i32 + dstList u8 + dstSlot i16(+ dstInstance i32,:63-84);
  - SORT: 1-3B = listType u8(+ category u8,:86-99);
  - DELETE: ≥2B = listType u8 + count u8(1..100) + count×12B 条目(:101-137)。
- **应答**: 成功/失败 ACK 复用普通命令号 —— USE_STACKABLE 0x01/0x002C(Network/handlers/Dungeon/DeathTowerCoordinator.cs:1822-1841)、MOVE 0x01/0x0013(:1884-1914,成功时经 `DeathTowerInventoryProjectionBuilder.BuildMoveAckBody` 把 QuickSlot 归一成 Main :11-28)、SORT 0x01/0x0014(:1966-1969)、DELETE 0x01/0x0012(逐 mutation 一包 :2008-2024)。
- **拦截条件与行为**(四个命令同构):
  1. `TryCaptureTowerCommandContext`:`CurrentRun.Tower` 非空且 run 身份匹配,否则返回 false 放行给常规背包(:2066-2084);
  2. body 解析失败返回 true(吃掉包但不动状态),防止污染真实背包(:1780-1783 等);
  3. 持有该角色 inventory lease 且上下文仍当前(:2053-2101);
  4. 委托 `_transientInventory.Try*` 在塔临时背包上结算;返回 `handled=false`(如目标格不是塔格)则放行主流程(如 :1809-1818);
  5. 每次成功/失败后额外推送 **0x00/0x000D 全量背包快照** `SendInventorySnapshotAsync`(:2031-2051);USE/DELETE 后按塔内物品计数重校准"寻找物品"任务进度(:2103-2114)。
- **投影背包机制**(`DeathTowerInventoryProjectionBuilder`,Network/Builders/Dungeon/DeathTowerInventoryProjectionBuilder.cs):A21 客户端把 Main 与 QuickSlot 解析到同一个塔容器。因此:
  - MOVE ACK 里 QuickSlot 坐标被归一成 Main(:133-137),保证客户端本地应用移动成功;
  - 全量快照只产出一份权威 Main 视图:虚拟货币/虚空格计数条目 + 普通背包条目(跳过塔临时占位格)+ 塔临时物品(含 QuickSlot 投影,后者优先)按槽位序写出(:39-118,投影资格判定 :120-131);避免第二个全量包把第一个视图清空。
- **备注**: 塔内拾取(GET_ITEM)与掉落也由塔 coordinator 拦截(`TryHandleGetItem` :1725 起、`TryGenerateDropsForMonster` :1707 起),属拾取主流程,详见主流程册交叉引用。

---

## 二、地狱派对(Hell Party)

地狱派对房间在 START_MAP 投影时由房间信息标记:`state.IsHellPartyRoom = true`、初始 `HellPartyPhase = WaitingStart`,并记录柱子对象码/出生点/波次与 `HellPartyVeryDifficult`(Network/handlers/Dungeon/DungeonMapHandler.cs:677-691;难度来源 `run.VeryDifficultHell = HellParty.Mode == 1`,Game/Dungeon/DungeonEntryAdmissionApplicationService.cs:426)。杀怪计数在 `DungeonKillApplicationService` 中检查 `HellPartyPhase == Started` 后推进至 Complete(Network/handlers/Dungeon/DungeonKillApplicationService.cs:593-596)。

### CMD 0x0413 (1043) `VERY_DIFFICULT_HELL_PARTY` — 华丽挑战(GORGEOUS_CHALLENGE)开关

- **请求**: ≥14B,仅消费 body[13]==0 视为开启(推断:其余字节为界面状态,证据仅覆盖该判定);无独立 parser,实读。注册 Network/Protocol/GameProtocolHandler.cs:751。
- **应答/推送**: 无应答、无推送。
- **行为**: 解析结果写入 `session.Player.HellPartyGorgeousChallengeEnabled` 并记日志(Network/handlers/Dungeon/DungeonEntryHandler.cs:3633-3642;解析函数 :3672-3677)。该标志为会话侧状态,入场时的"超困难"判定走 `HellParty.Mode`(:426 处),二者并存。
- **备注**: 名为 TOGGLE 实为一次性上报,客户端在选本界面勾选时发送。

### CMD 0x00EB (235) `REQUEST_DISJOINT_ITEM` — 双重身份:拆机请求 / 地狱派对开始

- **请求**: body 由下游各自解析(拆机店分支用 `DisjointMachineRequest`,塔外主流程不在此域)。
- **应答/推送**: 分支内部各自处理;HELLPARTY_START 分支无应答。
- **行为**: 注册为分流器 `HandleSharedDisjointOrHellParty`(Network/Protocol/GameProtocolHandler.cs:1044,1052-1060):
  - 已进入拆机店(`ExpertJobStoreHandler.HasEnteredStore`,Network/Handlers/ExpertJobStoreHandler.cs:225-230)→ 走拆机 `HandleDisjoint`;
  - 否则 → 走 `Handle_ENUM_CMDPACKET_HELLPARTY_START` → `HandleHellPartyStart`(Network/handlers/DungeonHandler.cs:186-187)。
  - `HandleHellPartyStart`:非地狱房间直接忽略(Network/handlers/Dungeon/DungeonMapHandler.cs:1316-1320);`WaitingStart → Started` 只允许一次,重复/迟到包按 phase 忽略(:1322-1330)。置为 Started 后杀怪计数才开始推进(:593-596),房间通关判定要求 Started 且未 Clear(DungeonMapHandler.cs:1306-1311)。
- **备注**: 该 CMD 与拆机店共享命令号是 A21 重要的"上下文多路复用"案例;未进店时包必然是派对开始语义。

---

## 三、许可副本(Licensed Dungeon)

许可证副本的结算有专用状态机,复用 run 的 `SettlementRuntime.LicensedDungeon` 与 `LicensedPlayResultGate`;目录定义在 `LicensedDungeonCatalog`(GameWorld)。普通副本的 `SET_PLAY_RESULT`(0x002E)若发现当前 run 是许可副本,会路由到同一状态机(`validateLicensedWireBody: false` 兼容路径,Network/handlers/Dungeon/DungeonSettlementHandler.cs:96-110)。通关前置推送 NOTI 0x0073 BOSS_DIE_CHECK(result=1,state=1,bossSeq u16)+ NOTI 0x001F ENABLE_CLEAR_DUNGEON(:151-206,builder Network/Builders/Dungeon/DungeonNotificationBuilder.cs:361-371,354-357)。目录信息包(NOTI 0x02F7 许可列表 = u16 记录数 × {dungeonId i32 + licenseLevel i32 + field3 i32};0x02F8 dayIndex u8;0x02FA shotCount u8;0x02FB 剩余次数 u8;0x02F9 CLEAR_INFO 21B 版式 = groupBoss u8 + 用时 u32 + 每日奖励 itemId/count + 通关奖励 itemId/count,注意线上顺序"每日在前")见 Network/Builders/Dungeon/LicensedDungeonPacketBuilder.cs:10-64。

### CMD 0x032C (812) `LICENSE_DUNGEON_PLAY_RESULT` — 通关结果(分两次请求)

- **请求**: 11B(业务)或 16B(官方抓包,末 5B 保留为不透明传输数据);Network/Parsers/Dungeon/LicensedDungeonPlayResultRequest.cs:20-41。注册 Network/Protocol/GameProtocolHandler.cs:714-715。
- **应答/推送**: 第 1 次请求不回 ACK,改为推送 NOTI 0x02F9 CLEAR_INFO;第 2 次起回 0x01/0x032C 成功 ACK(Network/CommonPacketBodyBuilder.BuildSuccessAck),随后补推经验通知。
- **行为**(Network/handlers/Dungeon/DungeonSettlementHandler.cs:139-149,208-375):
  - 门禁:当前 run 必须在 `LicensedDungeonCatalog` 且已 Cleared(`ClearedFact` 非空)、`SettlementRuntime.LicensedDungeon` 就绪(:214-260);
  - 在 `LicensedPlayResultGate` 内按请求计数分叉:第 1 次 → 发 CLEAR_INFO 并置 `LicensedClearInfoSent`(:277-305);第 2 次 → 标记 ResultShown、回成功 ACK、一次性补发许可投影 `SendLicensedDungeonLicenseProjectionAsync` 与经验授予通知(:307-369);
  - 兼容路径(0x002E 进入)跳过 body 校验(:104-108)。
- **备注**: 客户端对同一 CMD 发两次是该协议的固定节奏;"next=LICENSE_DUNGEON_REQUEST_REWARD"(:369)。

### CMD 0x032D (813) `LICENSE_DUNGEON_REQUEST_REWARD` — 领取奖励(落库)

- **请求**: 空 body;Network/Parsers/Dungeon/LicensedDungeonRequestRewardRequest.cs:11-27。注册 Network/Protocol/GameProtocolHandler.cs:716-717。
- **应答**: 0x01/0x032D 成功 ACK。
- **行为**(Network/handlers/Dungeon/DungeonSettlementHandler.cs:377-487):
  - 门禁:许可副本 run、已 Cleared、`ResultShown/Completed` 阶段(即 0x032C 第二步已完成)且持有 inventory lease(:382-437);
  - 以幂等效果 ID(`DungeonPersistentEffectKinds.LicensedDungeonRewardCommit`)调用 `PersistentEffects.TryApplyLicensedDungeonReward` 提交奖励(带 licenseLevel/groupBoss/奖励表)(:439-461);
  - 按变更分组逐背包发 `SendUpdateItemList`(:463-475),`TryCompleteSettlement` 收尾后回 ACK(:477-481)。

---

## 四、神秘商店(Secret Shop)

通关普通副本时若产出神秘商人,结算前置会生成 `run.SecretShopOffer` 并推送两包:NOTI 0x0117 SECRET_SHOP_NPC(4B npcId)+ NOTI 0x0118 SECRET_SHOP_ITEM_LIST(商品行,必须先 0x0117 存 NPC 模板再 0x0118 生成)(Network/handlers/Dungeon/DungeonSettlementHandler.cs:2342 附近注释,发送 :2570-2600;组合器 Network/Builders/SecretShopClearPacketBuilder.cs:13-31;商品行 Network/Builders/SecretShopItemListBodyBuilder.cs:7)。

### CMD 0x0128 (296) `SECRET_SHOP_BUY_ITEM` — 神秘商店购买

- **请求**: 8B = itemId i32 + count i32(itemId>0,count>=0);Network/Parsers/SecretShopBuyRequest.cs:16-29。注册 Network/Protocol/GameProtocolHandler.cs:726。
- **应答**: 0x01/0x0128。成功 26B 版式 = flag u8(1) + updatedGold i32 + slot u16 + itemId i32 + itemValue i32 + extData0 u8 + durability u16 + requiredItemId i32(无则 -1)+ costRemain i32 + offerRemain i32(Network/Builders/SecretShopBuyAckBuilder.cs:8-25);失败 2B = {0, 0x04}(:27-28)。
- **行为**(Network/Handlers/SecretShopHandler.cs:34-95):
  - `run.SecretShopOffer` 缺失或非神秘商店 → 失败 ACK(:36-47);
  - 持有 inventory lease 时 `SecretShopPurchaseService.TryPurchase` 原子结算(金币+代币成本、入库槽位)(:52-65),失败 → 失败 ACK(:67-75);
  - 成功后:成功 ACK → 金币更新 → 主背包入库槽更新 → 代币槽更新(:77-89)。

### CMD 0x0129 (297) `SECRET_SHOP_OPEN_CLOSE` — 商店界面开关

- **请求**: 1B,0=关 1=开(>1 拒绝);Network/Parsers/SecretShopOpenCloseRequest.cs:3-14。注册 Network/Protocol/GameProtocolHandler.cs:727。
- **应答/推送**: 无。
- **行为**: 仅记日志(open/close + npcId),服务端无界面状态机(Network/Handlers/SecretShopHandler.cs:21-32)。
- **备注**: 与 NOTI 0x0129 `ULTIMATE_DIFFICULTY_UI`(血祭终极难度弹窗)撞号但方向相反(CMD vs NOTI),互不影响。

---

## 五、副本机制统一入口(DungeonMechanismCoordinator)

12 个 CMD → `HandleDungeonMechanismCommand`(Network/Protocol/GameProtocolHandler.cs:728-752)→ `DungeonCommandParser`(:8-137)→ dispatcher(Network/handlers/Dungeon/DungeonCommandReceivedDispatcher.cs:29-105)。分派总表:

| CMD | 枚举 | 命令对象(parser 行) | 分派目标(dispatcher 行) |
|---|---|---|---|
| 0x0211 (529) | SUMMON_MONSTER | SummonMonsterDungeonCommand(:139-176) | SpecialDungeonNotifier.HandleBossSummonRequestAsync(:31-35) |
| 0x0253 (595) | EVENT_NPC_DROP_ITEM_ | NpcItemDropDungeonCommand(:46-48) | DungeonNpcItemDropCoordinator.HandleCommandAsync(:55-61) |
| 0x026B (619) | TIMER_MODIFY_INFO | TimerModifyInfoDungeonCommand(:26-30) | SpecialDungeonNotifier.HandleGentInfiltrateTimerModifyInfoAsync(:37-41) |
| 0x026D (621) | SEA_CHASE_MINI_GAME_RESULT | SeaChaseResultDungeonCommand(:32-44) | SpecialDungeonNotifier.HandleSeaChaseMiniGameResultAsync(:43-47) |
| 0x013C (316) | OPERATE_RIDABLE_OBJECT | SeaChaseObservedDungeonCommand(:126-131) | SpecialDungeonNotifier.ObserveSeaChasePacketAsync(:49-53) |
| 0x0270 (624) | GAIN_EAT_OBJECT | SeaChaseObservedDungeonCommand(:126-131) | 同上(仅观察) |
| 0x01CA (458) | TOURNAMENT_REWARD_SELECT_STATE | TournamentRewardSelectStateDungeonCommand(:54-64,要求空 body) | TournamentDungeonCoordinator.HandleRewardSelectStateAsync(:69-73) |
| 0x01CB (459) | TOURNAMENT_REWARD_SELECT | TournamentRewardSelectDungeonCommand(:66-85,2B 且各 <2) | TournamentDungeonCoordinator.HandleRewardSelectAsync(:75-79) |
| 0x0102 (258) | BLOOD_ROUND_UI_PREPARE_FINISH_ | BloodAltarPrepareFinishedDungeonCommand(:87-97,空 body) | BloodAltarDungeonCoordinator.HandlePrepareFinishedAsync(:81-85) |
| 0x00FF (255) | DIE_BLOOD_MONSTER | BloodAltarMonsterDeathsDungeonCommand(:99-104,178-220) | BloodAltarDungeonCoordinator.HandleMonsterDeathsAsync(:87-91) |
| 0x013D (317) | SELECT_ULTIMATE_DIFFICULTY | BloodAltarSelectDifficultyDungeonCommand(:106-124,1B 且 ∈{1,2}) | BloodAltarDungeonCoordinator.HandleSelectDifficultyAsync(:93-97) |
| 0x0408 (1032) | BREAK_TRAP_RESULT | BreakTrapResultDungeonCommand(:50-52) | TimeSpiralDungeonCoordinator.HandleBreakTrapResultAsync(:63-67) |

枚举号验证:CmdPacketType 中 DIE_BLOOD_MONSTER=0x00FF、BLOOD_ROUND_UI_PREPARE_FINISH_=0x0102、SELECT_ULTIMATE_DIFFICULTY=0x013D、TOURNAMENT_REWARD_SELECT_STATE/SELECT=0x01CA/0x01CB、SUMMON_MONSTER=0x0211、EVENT_NPC_DROP_ITEM_=0x0253、TIMER_MODIFY_INFO=0x026B、SEA_CHASE_MINI_GAME_RESULT=0x026D、BREAK_TRAP_RESULT=0x0408(Network/Core/PacketTypes.cs:1486-2263)。

### CMD 0x0211 (529) `SUMMON_MONSTER` — 条件 Boss 召唤(特殊副本)

- **请求**: 19B = conditionalType u16 + monsterCode i32 + stateId i32 + mapId i32 + param0 u16 + param1 u16 + matchCount u8;且 monster/state/map/matchCount 必须 >0(DungeonCommandParser.cs:139-176;字段定义 Game/Dungeon/DungeonMechanismCommand.cs:16-45)。
- **应答/推送**: 无固定 ACK;成功后按 `SpecialDungeonEffectRouter.RoutePlanAsync` 投放效果计划(Boss 出场/掉落等,按副本种类路由)。
- **行为**: 仅在当前 run 事件有效时受理;`ApplyBossSummonAndPlan` 生成登记+计划,无计划(不匹配该副本机制)则拒绝并记日志(Network/handlers/Dungeon/SpecialDungeonNotifier.cs:160-198)。召唤登记为实例级事实,后续 `BOSS_DIE_CHECK` 门禁会校验 `ConditionalBossSpawned` 与 bossSeq(Network/handlers/Dungeon/DungeonMechanismCoordinator.cs:522-557)。

### CMD 0x0253 (595) `EVENT_NPC_DROP_ITEM_` — 事件 NPC 掉落(任务道具)

- **请求**: 空 payload(非空即忽略并记日志,DungeonMechanismCommand.cs:92-107)。
- **应答**: 恒回 0x01/0x0253 成功 ACK,即使业务拒绝也回(Network/handlers/Dungeon/DungeonNpcItemDropCoordinator.cs:47-54)。
- **行为**: 门禁链 = 当前 run 事件 → `RewardPolicy.AllowsQuestDrops` → 当前地图命中 `DungeonNpcItemDropData` 场景 → 任务快照恰好 1 个匹配(副本+难度+职业)→ 持有 lease,全过后生成掉落入库(:57-129+,异常吞掉仅记日志 :39-45)。

### CMD 0x026B (619) `TIMER_MODIFY_INFO` — 绅士潜入计时修改(仅观察)

- **请求**: 任意长度 payload 原样留存(DungeonMechanismCommand.cs:47-61)。
- **应答/推送**: 无。
- **行为**: 仅记 `[SpecialDungeonModule] TIMER_MODIFY_INFO observe` 日志(含 body 十六进制),不改状态(Network/handlers/Dungeon/SpecialDungeonNotifier.cs:200-212,352-367)。潜入超时由服务端定时器驱动 `MarkGentInfiltrateTimeoutAsync`(:307-335),与该包无联动。

### CMD 0x026D (621) `SEA_CHASE_MINI_GAME_RESULT` — 海战小游戏结算

- **请求**: 4B = result i32(DungeonCommandParser.cs:32-44)。
- **应答/推送**: 完成 replay 时回 0x01/0x026D CommandSuccessAck;正常路径按效果计划路由(Network/handlers/Dungeon/SpecialDungeonNotifier.cs:251-272)。
- **行为**: 仅 `SpecialDungeonKind.SeaChase` 的 run 受理(:232-242);`ApplySeaChaseResultAndPlan` 登记结果并生成计划;幂等 replay(已创建且已完成)只补 ACK(:251-265)。

### CMD 0x013C (316) `OPERATE_RIDABLE_OBJECT` / CMD 0x0270 (624) `GAIN_EAT_OBJECT` — 观察类包(合并)

- **请求**: 任意 payload 原样留存;两个 CMD 共用 `SeaChaseObservedDungeonCommand`(DungeonCommandParser.cs:126-131)。
- **应答/推送**: 无。
- **行为**: 仅记 observe 日志(Network/handlers/Dungeon/SpecialDungeonNotifier.cs:293-305)。坐骑操作与吃对象的玩法推进由 START_MAP/杀怪路径的 ridable-object 逻辑负责(主流程册),此入口仅留证据。
- **备注**: 0x013C 亦被 `TimeSpiralDungeonCoordinator.ApplyTeleportOverride` 用作过图坐标改写的上下文,但那发生在 MOVE_MAP,不经此 CMD。

### CMD 0x01CA (458) `TOURNAMENT_REWARD_SELECT_STATE` — 锦标赛奖励翻牌状态查询

- **请求**: 空 body(DungeonCommandParser.cs:54-64)。
- **应答**: 0x01/0x01CA = flag u8(1) + CardTypeCount × PartySlotCount 个槽位使能字节(可用=1,不可用=0xFF)(Network/Builders/Dungeon/TournamentPacketBuilder.cs:107-127)。
- **行为**: 门禁 = 奖励状态存在 + 锦标赛已终结(`IsTournamentTerminated`)+ run Active;发送后启动选牌倒计时(Network/handlers/Dungeon/TournamentDungeonCoordinator.cs:215-242)。断线恢复路径会同时重发 0x01CA+0x01CB 并恢复计时器(:269-301)。

### CMD 0x01CB (459) `TOURNAMENT_REWARD_SELECT` — 锦标赛选牌

- **请求**: 2B = cardType u8 + cardIndex u8,均须 <2(DungeonCommandParser.cs:66-85)。
- **应答/推送**: 选中后由 `ApplyTournamentRewardSelectionAsync` 预留→发奖→投影(成功后推 0x01CB selection 与通关奖励 NOTI,详见 TournamentPacketBuilder.cs:129-144 与 :303-329 起)。
- **行为**: 同样要求锦标赛已终结且 run Active(Network/handlers/Dungeon/TournamentDungeonCoordinator.cs:244-267);`TryReserveReward` 幂等预留防止重复选(:322-328)。

### CMD 0x0102 (258) `BLOOD_ROUND_UI_PREPARE_FINISH_` — 血祭祭坛:回合准备完成

- **请求**: 空 body(DungeonCommandParser.cs:87-97)。
- **应答/推送**: 无 ACK;成功后推 NOTI 0x00D7 BLOOD_ROUND_INTERVAL_TIME = round u8(1-based)+ intervalMs u32(Network/Builders/Dungeon/BloodAltarPacketBuilder.cs:74-84)。
- **行为**: 门禁 = 当前血祭 run 且祭坛地图已绑定当前房间;通过后 `StartNextRoundAsync` 开启下一回合并调度波次(Network/handlers/Dungeon/BloodAltarDungeonCoordinator.cs:46-68,181-205)。

### CMD 0x00FF (255) `DIE_BLOOD_MONSTER` — 血祭祭坛:怪物死亡批量上报

- **请求**: 1B count(>0)+ count × seqId u16(seq 非 0),总长须恰为 1+2×count(DungeonCommandParser.cs:178-220)。
- **应答/推送**: 无固定 ACK;每个 seq 走杀怪处理器,波次推进时推 NOTI 0x00D2 BLOOD_MONSTER_SPAWN 等(BloodAltarDungeonCoordinator.cs:284,1244)。
- **行为**: 门禁 = 当前血祭 run;逐 seq 构造 `KillContext(sourceActorId=seq)` 交给 `ProcessMechanismKillAsync`(由 DungeonHandler 构造期注入,Network/handlers/DungeonHandler.cs:126-127),每步之间校验参与者仍在当前房间(Network/handlers/Dungeon/BloodAltarDungeonCoordinator.cs:70-108)。

### CMD 0x013D (317) `SELECT_ULTIMATE_DIFFICULTY` — 血祭祭坛:终极难度选择

- **请求**: 1B difficulty ∈ {1,2}(DungeonCommandParser.cs:106-124)。
- **应答**: 0x01/0x013D = {0x01, difficulty}(Network/Builders/Dungeon/BloodAltarPacketBuilder.cs:102-103)。
- **行为**: 门禁 = 当前血祭 run;按 `DifficultyPromptVersion` 校验转移合法,取消选牌倒计时后回 ACK 并开下一回合(Network/handlers/Dungeon/BloodAltarDungeonCoordinator.cs:110-143)。弹窗由服务端主动推 NOTI 0x0129 ULTIMATE_DIFFICULTY_UI(BuildUltimateDifficultyPrompt = 0 + round u8 + timeout u16,BloodAltarPacketBuilder.cs:86-97,推送点 BloodAltarDungeonCoordinator.cs:361-362,412,1269-1270)。
- **备注**: 推送侧还有 0x00D0 RANKING/0x00D1 REWARD(BloodAltarDungeonCoordinator.cs:639,751)与 0x00D3 START_BLOOD_MAP(:1208),属祭坛结算/入场投影,主流程册交叉。

### CMD 0x0408 (1032) `BREAK_TRAP_RESULT` — 时空螺旋:陷阱破坏结果

- **请求**: 任意 payload 原样留存(DungeonMechanismCommand.cs:109-123)。
- **应答**: 无条件回 0x01/0x0408 成功 ACK(即使 run 无效)(Network/handlers/Dungeon/TimeSpiralDungeonCoordinator.cs:37-40)。
- **行为**: 后续仅时空螺旋副本(`TimeSpiralData.IsDungeon`)处理:解析当前地图后 `TryMarkTeleportPending` 置传送挂起,并推条件通过门通知(:42-73);真正改写过图目标发生在下一次 MOVE_MAP 的 `ApplyTeleportOverride`(清除隐藏 Boss、替换目标房、掷陷阱 buff,:88-129)。

---

## 六、特殊入场

### CMD 0x02B6 (694) `CRACK_OF_DIMENSION` — 异次元裂缝(异界历史副本)入场

- **请求**: ≥8B = historicalDungeonId u32 + crackQuestId u32(+ 尾部任意字节,记录非零尾标);Network/Parsers/Dungeon/CrackOfDimensionRequest.cs:26-58。注册 Network/Protocol/GameProtocolHandler.cs:702-703。
- **应答/推送**: 无独立应答;成功即走 `SELECT_DUNGEON` 主流程的正常入场推送(START_MAP/PERMISSION 等),失败回入场拒绝包。
- **行为**(Network/handlers/Dungeon/DungeonEntryHandler.cs:1643-1720):
  - 解析失败 → `DungeonAdmissionReject.InvalidSelectionState`(:1648-1661);
  - `AnotherAradSelectionResolver.TryResolve` 按(历史副本, 裂缝任务)解析包装副本失败 → 拒绝 `DungeonUnavailable`(:1663-1680);
  - 无当前选择态或已在 run 中 → 拒绝 `InvalidSelectionState`(:1682-1694);
  - 成功:合成 9B body = historicalDungeonId i32 + 最大难度 u8(offset4)+ 0xFF 0xFF(offset7-8),连同 selection 调 `HandleSelectDungeonCore`(:1696-1719)。
- **备注**: 入场后裂缝任务在会话内接受(`TryHandleAnotherAradQuestAcceptAsync`,按 run 上挂的 `AnotherAradCrackQuestId` 拦截普通任务接受命令,:1722-1768),属任务域交叉。

### CMD 0x0308 (776) `REQUEST_CIRCLE_ENTER` — 公会圆环入场握手

- **请求**: 恰 8B = dungeonId u32 + circleQuestId u32;Network/Parsers/Dungeon/CircleDungeonEntryRequest.cs:20-32。注册 Network/Protocol/GameProtocolHandler.cs:704-705。
- **应答**: 0x01/0x0308。成功 9B = flag u8(1) + gate u32(常量 1,`SuccessGateCandidate`,客户端仅验证非零)+ circleQuestId u32;失败 1B = {0}(Network/Builders/Dungeon/CircleDungeonEntryResponseBuilder.cs:7-21)。
- **行为**(Network/handlers/Dungeon/DungeonEntryHandler.cs:333-401):四级门禁 = body 长度 → 活跃游戏会话 → 存在当前副本选择态 → `CircleDungeonEntryPolicy.Evaluate` 且 `activeSelection.TryBindCircleEntry` 绑定成功;任一失败回 Rejected 并记拒绝原因(:341-395)。
- **备注**: 该包只完成"选择态绑定 + 任务握手",不直接触发入场;gate 字段值尚待成功抓包证实(builder 注释 :5-7)。

### CMD 0x035D (861) `SEQUENTIAL_DUNGEON_INFO` — 连续副本序列进度询问

- **请求**: ≥4B = configKey i32(客户端会反复重发并卡界面,必须始终应答);无独立 parser,实读(Network/handlers/Dungeon/DungeonEntryHandler.cs:402-429)。注册 Network/Protocol/GameProtocolHandler.cs:706-707。
- **应答/推送**: 推送(0x00)NOTI 0x025B SEQUENTIAL_DUNGEON_INFO = configKey i32 + progress u8 + routeMask i32(Network/Builders/Dungeon/DungeonNotificationBuilder.cs:414-424,发送 :448-454);若该序列有权限条目,先补推 NOTI DUNGEON_PERMISSION(与主动推送路径布局一致,:440-447)。
- **行为**: `PersistentMechanisms.TryResolveSequentialState` 解析角色在该 configKey 的进度;无记录按 progress=0(未开始)应答(:429-435)。注释明确:不回权限包时暴走安徒恩(序列 41)在组队下会被客户端判"与组队模式不符"(:436-439)。