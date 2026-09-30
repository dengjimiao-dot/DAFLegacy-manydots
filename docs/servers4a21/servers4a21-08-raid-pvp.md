# 08 Raid(安图恩团本)/PVP 域 — ServerS4A21 已实现协议处理参考

> - **本册导航**:总览/帧结构/信封/分发架构/已实现 CMD 总表见 [servers4a21-00-overview.md](servers4a21-00-overview.md)。
> - 仅写 A21(`ServerS4A21\Server\DfoServer`)侧实现,供 DAFLegacy 回填参考。路径相对 `DfoServer` 根;行号为整理时点状态。

## 0. 域综述

Raid(安图恩)运行在独立监听端口上(`IsRaidSession` 判定 `GameNetworkConfig.IsRaidListener`,`Network/handlers/RaidHandler.cs:95-102`),raidId 高 16 位即频道号(`raidId >> 16`,`Network/handlers/RaidHandler.Directory.cs:30`),所有 raid 广播按"同频道 + raid 监听"圈定会话(`Network/handlers/RaidHandler.Directory.cs:28-48`)。编队模型:raid 内 `partyIndex 0..10`,预备期由 PartyHandler 普通组队经委托镜像进 raid(`TryCommitPartyJoin/PreparationPartiesReady/PreparationPartyOrder`,`Network/handlers/RaidHandler.Parties.cs:17-41`),进行期改走 `LivePartyAssignment` 委托;队长/编队变更都以 `RAID_MODIFY`(NOTI 0x0250)+`RAID_LIST`(NOTI 0x024F)重发布。Anton 为两阶段符号表驱动:p0 副本 210-216、p1 副本 218-224(`Network/handlers/RaidHandler.AntonDefinitions.cs:139-141`),进度用 symbol(SetSymbol→NOTI 0x0248)与定时器注册表(带 version 竞态防护,`Network/handlers/RaidHandler.Runtime.cs:226-289`)推进。PVP 走 `FreeDuelRoom` 8 席位房间(`Game/Pvp/FreeDuelRoom.cs:8-21`:房间态 1=等待/2=已开始,结算 phase 0..3),注册在 misc 组 `misc-pvp`(`Network/Protocol/GameProtocolHandler.cs:251,1001-1032`),FAIR/INTEGRATE 积分查询注册在选角段(`GameProtocolHandler.cs:370-373`);所有房间发布经全局 `_roomPublicationGate` 信号量串行(`Network/handlers/PvpRoomHandler.cs:111`),入场/建房用"预留 + 直接握手屏障"防慢客户端冻房(`PvpRoomHandler.cs:913-1023`)。UDP 对战面:`PartyUdpRelay` 是 TURN 式 per-ordered-pair 中继(`Network/Core/PartyUdpRelay.cs:29`,`DFO_UDP_RELAY` 门控),`PvpRelayLifecycleCoordinator` 按房间代同步中继(`Network/handlers/Pvp/PvpRelayLifecycleCoordinator.cs:74`),peer 端点名册经 NOTI 0x000B USER_UDP_IP_PORT 按接收者定制下发(`Network/Builders/Pvp/PvpPeerInfoBuilder.cs:9-15`);`UdpRelayServer` 是未接主流程的通用哑中继(`Network/Core/UdpRelayServer.cs:20`)。

## 1. Raid(安图恩团本)

注册:`raid-lottery-overflow` 组(`Network/Protocol/GameProtocolHandler.cs:245,405-431`)。

### 1.0 RaidHandler 部分类组织与副本主流程挂接

| 文件(`Network/handlers/`) | 职责 |
|---|---|
| RaidHandler.cs | 创建/退场/分发辅助、标题解析、member 快照、断线清理 (`RaidHandler.cs:104-550`) |
| RaidHandler.Admission.cs | 入场材料(10096296)检查/扣料/租赁 (`:42-161`) |
| RaidHandler.AntonDefinitions.cs | Anton 副本/符号常量表 (`:7-141`) |
| RaidHandler.Directory.cs | 频道广播、目录/对象发布、USERINFO 上下文、成员列视图 (`:17-213`) |
| RaidHandler.Parties.cs | 编队委托、队长转让广播、普通组队同步 (`:17-125`) |
| RaidHandler.Progression.cs | 开始/进本/通关/阶段推进/黑火山屏障 (`:16-919`) |
| RaidHandler.Recruitment.cs | 等待席、结对邀请、重同步 (`:18-336`) |
| RaidHandler.Rewards.cs | PhaseRewardFlow 状态机、翻牌/小队奖励/发奖 (`:29-833`) |
| RaidHandler.Runtime.cs | 定时器注册表、Anton 判定、运行态清理 (`:16-546`) |
| RaidHandler.Situation.cs | 攻坚 buff、怪血上报、局势分组 (`:28-470`) |

与副本主流程的挂接点(`Network/Protocol/GameProtocolHandler.cs` 的 Raid 感知包装):

| 原 CMD | 包装 | Raid 侧回调 |
|---|---|---|
| 0x002E (46) SET_PLAY_RESULT | `HandleRaidAwareSetPlayResult` (`:618-642`) | 通关后 `HandleDungeonClearedAsync`;Anton 本抑制普通翻牌流程 (`:631-639`) |
| 0x0028 (40) DIE_CHARACTER | `HandleRaidAwareCharacterDeath` (`:661-675`) | `HandleDungeonCharacterDeathAsync`(记死亡、NOTI 0x024E) |
| 0x0029 (41) USE_COIN | `HandleRaidAwareUseCoin` (`:677-697`) | 复活成功时 `HandleDungeonCharacterReviveAsync`(target=body 前 u16) |
| 0x0048/0x007B/0x0084(EPLP/复活/放弃) | `HandleRaidAwareDungeonExit` (`:644-659`) | 退本后 `HandleDungeonAbortedAsync`(回滚参与+怪血,`RaidHandler.cs:171-180`) |
| 副本加载完成 | `DungeonLoadingCoordinator.cs:68` | `HandleDungeonLoadedAsync`(`Progression.cs:97-116`;219 进图先同步屏障状态 `:107-110`) |
| 断线 | `GameProtocolHandler.cs:303` | `ClearSessionAsync`(`RaidHandler.cs:505-517`,按需解散+广播) |
| 进城区域变化 | `GameProtocolHandler.cs:209` | `HandleRaidTownAreaChangedAsync`(`Recruitment.cs:328-336`,重发目录) |

- **Anton 判定**:`IsAntonRaidDungeon` = 210..216 ∪ 218..224(`RaidHandler.Runtime.cs:475-486`);`IsAntonDungeonForPhase` 按阶段分桶(`:497-508`);所需通关次数 210:4、213/215:各 2、220:5、其余 1(`:510-524`);HP symbol = dungeonId−160(二阶段 −161)(`:526-533`)。
- **运行态清理**:raid 解散/终局按实例清定时器(键前缀 `raid:{instanceId}:`)、symbol、buff、怪血缓存(`RaidHandler.Runtime.cs:372-412`)。
- **定时器竞态防护**:`ScheduleRaidTimer` 每 (instance,phase,type,dungeon,purpose) 一键、每次调度生成新 version,回调触发时校验 `TimerCurrent` 才执行(`RaidHandler.Runtime.cs:226-289,321-340`);同 key 重排自动取消旧句柄(`:284-287`)。

### 1.1 协议条目

### CMD 0x0299 (665) `CREATE_RAID` — 创建攻坚队
- **请求**: `u8(不校验) + i32 标题长 + u8[len] 标题(GBK)`;解析 `Network/handlers/RaidHandler.cs:447-462`,空标题→默认 `Raid: {cid}`(`:464-467`)。
- **应答**: type1 `[0x01, u32 raidKey]`(`Network/Builders/Raid/RaidPacketBuilder.cs:88-94`);随后向同频道推 NOTI 0x0250 (592) RAID_MODIFY op0 完整对象+成员(接收者支持列协议且非特定态时带列尾,`Network/handlers/RaidHandler.Directory.cs:17-21`),最后 NOTI 0x024F (591) RAID_LIST 目录广播(`:65-69`)。
- **行为**: 仅 Raid 监听 (`RaidHandler.cs:106`);`_raids.Create(title, member, channelId)`(`:116`);同频道他人先补 USERINFO(0x0002)上下文再收对象(`:119-125`);`_objectSent[session]=0` 标记已收对象(`:127`)。

### CMD 0x0363 (867) `RAID_REQUEST_RAID_MEMBERS` — 请求攻坚队成员
- **请求**: 双形态。① `len==4`:成员重同步;② `len==12` 且 `u32 raidId@0 + u32 827343698@4 + u32 1@8`:成员列视图请求(`Network/Builders/Raid/RaidPacketBuilder.cs:59-74`)。
- **应答**: ① `RebindSession` 后首次补发完整对象(RAID_MODIFY op0)+状态重发包组(`Network/handlers/RaidHandler.Recruitment.cs:242-252`);② `TryPrepareRaidMemberColumnView`(`Network/handlers/RaidHandler.Directory.cs:120-143`:校验同频道、phase≤1、成员≤20,置会话列协议版本)批量回 RAID_MODIFY op2+op3(带列尾),经 `SendPreparedPacketBatchAsync` 事务发送(`Recruitment.cs:232-240`)。

### CMD 0x0364 (868) `RAID_CHECK_RAID_USER` — 检查/邀请入队
- **请求**: `u8 + u16 targetCharacterId`(body==3,`Network/handlers/RaidHandler.Recruitment.cs:163-166`)。
- **应答**: 无直接回包;经 `RaidPeerRequestAsync` 委托触发结对邀请弹窗流程(`:172-175`);接受侧 `HandleRaidPeerAcceptAsync` 完成入队(`:104-140`)。
- **行为**: 目标须同 Raid 监听在线(`:167`);发起者未入队则登记等待席(`:179-184`);入队确认时拒绝同账号多角色(`:196-213`),确认后置 `_objectSent`、单发对象+状态并全量广播(`:122-127`)。

### CMD 0x033C (828) `RAID_OTHER_CHANNEL_LIST` — 攻坚队目录/等待席列表
- **请求**: `body[0]==1` → 请求等待席列表;否则请求 raid 目录(`Network/handlers/RaidHandler.Recruitment.cs:299`)。
- **应答**: 等待席 → NOTI 0x02EB (747) RAID_OTHER_CHANNEL_WAITING_LIST:`u32 count + count×{u16 uid, u8 channelId, u8 serverIndex, u16 level, u8 growType, u8 job, u8 0, u8[30] name 补零}`(`Network/Builders/Raid/RaidWaitingPacketBuilder.cs:8-32`);目录 → 逐 raid 补发对象/状态 + NOTI 0x024F 目录(`Network/handlers/RaidHandler.Directory.cs:155-170`)。
- **行为**: 等待席行过滤已入队/跨频道/会话漂移者(`Recruitment.cs:47`)。

### CMD 0x0334 (820) `RAID_RECENT_FRIEND_LIST` — 近期好友列表(占位)
- **请求**: 任意 body(仅日志)。
- **应答**: type1 `u32 0`(`Network/handlers/RaidHandler.Recruitment.cs:31-40`)。

### CMD 0x029C (668) `SET_RAID_WAITING` — 登记等待席
- **请求**: 任意 body(仅日志)。
- **应答**: type1 `[1, 5×u32 0]`(`Network/Builders/Raid/RaidPacketBuilder.cs:308-317`)。
- **行为**: 已在任一 raid 中则 ack=0(`Network/handlers/RaidHandler.Recruitment.cs:20`);`TryAddWaiting(channelId, member)`(`:26`)。

### CMD 0x029D (669) `REJOIN_RAID` — 重入攻坚队
- **请求**: 空/任意。
- **应答**: 无独立 ack;`RebindSession` 后重发完整对象+状态包组(`Network/handlers/RaidHandler.Recruitment.cs:255-268`;状态组=`ResendRaidStateToSessionAsync` `:312-326`:RAID_MODIFY op2/op3、NOTI 0x0257、定时器快照、非待机态再补通关数/0x027A/0x027B)。

### CMD 0x0293 (659) `RAID_ENTRY_COST_INFO` — 入场材料状态
- **请求**: `u8 stage`(缺 body 按 0,`Network/handlers/RaidHandler.Admission.cs:54`)。
- **应答**: type1 ack;stage==0 先补 buff 状态+怪血(`:66-70`);随后单发/广播 NOTI 0x0257 (599):`u32 count + count×{u16 uid, u8 ready, u16 owned}`(`Network/Builders/Raid/RaidPacketBuilder.cs:496-515`;广播 `Admission.cs:153-161`)。
- **行为**: 先 `EnsureRaidDungeonParticipationAsync` 补记迟入副本(`Network/handlers/RaidHandler.Situation.cs:406-416`);材料=item 10096296 每人≥1(`Admission.cs:87,115`);首次请求补发完整对象(`:60-64`);全员备齐才可开团(`HasAllEntryCosts` `:101-122`),开团扣料走租赁原子提交(`TryConsumeEntryCosts` `:124-151`)。

### CMD 0x02B7 (695) `RAID_BUFF_SYSTEM` — 攻坚 buff 激活
- **请求**: `u8 buffType[, u16 partyIndex + n×u16 memberIds]`(len==1 合法,`Network/handlers/RaidHandler.Situation.cs:257-279`)。
- **应答**: type1 ack(=是否激活);状态推 NOTI 0x027A (634):`u8 groupCount + {u8 buffType, u8 entryCount, entries{u16 party, u16 uid, u32 activeUntil, u32 cooldownUntil}}`(`Network/Builders/Raid/RaidPacketBuilder.cs:517-543`;单发/广播 `Situation.cs:319-327`)。
- **行为**: 仅 state==2(`:43`);冷却字典 `_raidBuffActivations` + 实例锁互斥(`:80-108`);目标须在局势分组成员内(`TryResolveRequestedBuffTarget` `:281-312`);INCREASE TIME 延长阶段时限并重设攻击超时计时(`:116-121`),INCREASE COIN 追加复活币并刷怪状态(`:123-127`);客户端类型映射 {1→0,2→1,3→2,0→3,4→4}(`:193-205`)。

### CMD 0x02B8 (696) `RAID_MONSTER_HP` — 怪物血量上报/查询
- **请求**: `u8 n(0..5) + n×u32`(`Network/handlers/RaidHandler.Situation.cs:171-189`)。
- **应答/推送**: n==0 为查询:补参与状态+buff+怪血后单发(`:144-159`);n>0 为上报:按局势组键存 `_raidMonsterRuntimeValues` 后广播 NOTI 0x027B (635):`u8 count + {u16 situationIndex, u8 memberCount, count×u16 uid, u32 usedCoin, u8 valueCount, valueCount×u32}`(`Network/Builders/Raid/RaidPacketBuilder.cs:545-569`;广播 `Situation.cs:395-443`)。
- **行为**: usedCoin = used−granted 饱和差(`Situation.cs:363-370`);键含 dungeonId,进/退本时重置(`:372-382`)。

### CMD 0x029A (666) `LEAVE_RAID` — 离开攻坚队
- **请求**: 空(不读 body)。
- **应答**: type1 ack;NOTI 0x0250 RAID_MODIFY op1 remove `u32 raidId + u32 1`(`Network/Builders/Raid/RaidPacketBuilder.cs:239-246`)。
- **行为**: `_raids.Leave(userId)` 判定解散(`Network/handlers/RaidHandler.cs:147`);解散时 `ClearDisbandedRaidState` 清运行态(`:156-157,519-531`);finally 向频道广播成员刷新+目录(`:533-549`)。

### CMD 0x029B (667) `START_RAID` — 开团/开启二阶段
- **请求**: 空 body(`Network/handlers/RaidHandler.Progression.cs:16-18`)。
- **应答**: type1 ack;准备期广播 NOTI 0x0255 (597) RAID_SET_TIMER `u8 key0 + u32 key1 + u32 endTimestamp + u32 duration`(`Network/Builders/Raid/RaidPacketBuilder.cs:319-337`)、NOTI 0x0256 (598) RAID_REMAIN_TIME `u8 type + u32 remain`(`:339-345`)、NOTI 0x0253 (595) PREPARE_START_RAID 空 body(`Progression.cs:40-42`)。
- **行为**: state==5&&arg==0&&phase==0 → 二阶段开始分支(`:19-23,54-67`);一阶段:全员材料校验,失败 ack0+NOTI 0x01F7 SERVER_NOTICE_MESSAGE 提示(`:24-30`);`TryBeginStart` 防重入(`:31`);准备计时到期 `CompleteRaidPreparationAsync`(`:785-839`):编队齐+原子扣料(`:809-813`),逐会话刷背包(`:824-827`),广播 NOTI 0x024A (586) 初始副本状态、NOTI 0x0248 (584) 初始符号、阶段限时并启动攻击超时(`:831-837`);准备未完成则回滚+提示(`:796-821`)。
- **备注**: 攻击超时 `RunAttackTimeoutAsync` → `TryFailAndDisband` + 逐人失败结算 NOTI 0x025A (602) resultType=1(`Network/handlers/RaidHandler.Runtime.cs:162-224`;包构造 `Progression.cs:912-919`)。

### CMD 0x0294 (660) `RAID_MOVIE_SKIP` — 阶段结算动画跳过
- **请求**: `u8 finished`(==1 表示已播完/确认,`Network/handlers/RaidHandler.Rewards.cs:291-298`)。
- **应答**: type1 ack;finished → 开始翻牌阶段 + 单发 RAID_STATE(4,arg)(`Network/handlers/RaidHandler.cs:356-359`)+本队奖励列表(`Rewards.cs:277-287,352-367`)。
- **推送**: 服务端 2s 后主动提示 NOTI 0x0258 (600):`u32 movieId + u32 option`(`Rewards.cs:325-336`;构造 `RaidPacketBuilder.cs:362-368`)。
- **行为**: 需 state==3 且 `flow.ResultStarted`(`:270`)。

### CMD 0x0295 (661) `SELECT_RAID_REWARD_CARD` — 翻牌选奖励
- **请求**: `u8 rewardType(0..2) + u8 cardIndex(0..3)`(`Network/handlers/RaidHandler.Rewards.cs:504-519`)。
- **应答**: type1 ack;首次记录(rewardType≤1)向同 party 广播翻牌 NOTI 0x0259 (601) RAID_REWARD_LIST(`:454-457,521-536`),随后发奖(`:459-466`)。
- **行为**: `PhaseRewardFlow` 单飞状态机(`:29-263`):卡位 per-party 互斥分配(`GetOrCreateAvailableCardIndex` `:93-128`;`TryRecordCardOperation` `:196-226`);全员选完触发小队奖励(`:467-470`);10s 未选自动选(`:389-424`);小队奖励 NOTI 0x0259 type=3 广播后延迟 5s 落包(`ShowPhaseOneSquadRewardsAsync` `:601-651`);收尾 `TryCompletePhase`:phase0 → 幕间(REMAIN_TIME(1)+PhaseBreak 计时,`FinishPhaseOneRewardsAfterDelayAsync` `:653-682`),phase1 → 终局清理(`:663-669`)。
- **布局**: RAID_REWARD_LIST = `u8 rewardType + u8 count + {u16 uid, u8 cardType, u8 flags, u32 itemId, u16 quantity}`(`RaidPacketBuilder.cs:370-395`);RAID_RESULT = `u8 resultType + u8 phase + u32 clearTime + u16 deaths + u8 rank + u8 rewardOption`(`:347-360`)。

### CMD 0x0296 (662) `RAID_DO_BEHAVIOR` — 行为中继
- **请求**: `u32 targetUid + u32 behavior`(8B,`Network/handlers/RaidHandler.cs:197-204,194`)。
- **应答**: type1 ack;原样 body 以 NOTI 0x0247 (583) 广播全队(`:193`)。
- **行为**: 仅 state==2(`:186`)。

### CMD 0x0297 (663) `RAID_SET_SYMBOL` — 客户端符号请求(黑火山屏障)
- **请求**: `u32 symbolId + u32 operand + u8 op(0=set/1=add/2=sub)`(9B,`Network/handlers/RaidHandler.cs:221-234`)。
- **应答**: type1 ack;符号变化经 NOTI 0x0248 下发(`Network/handlers/RaidHandler.Progression.cs:498-552`)。
- **行为**: 客户端仅可操作 symbol 110(屏障),需 phase==1+state==2+符号已存在(`RaidHandler.cs:210`);运算带 u32 饱和(`:236-253`);实例锁互斥(`Progression.cs:503`);减到 0(破盾)推 [127=0,110=0,111=1,126=1] 并启动恢复计时(`:519-531`);回满 10000 推 [126=0,110=10000,111=0,127=1](`:532-547`);中间值单发 110(`:534`);进 219 时按破盾态单补 4 符号(`SyncBlackVolcanoBarrierStateAsync` `:561-576`)。

### CMD 0x029E (670) `RAID_MANAGER_WORK` — 队长管理操作
- **请求**: `u32 op + u32 targetUserId + u32 partyIndex`(12B;op 限 0/1,partyIndex≤10 仅 op0,`Network/handlers/RaidHandler.Parties.cs:43-61`)。
- **应答**: type1 ack。
- **行为**: op1=转让队长:目标会话活性三重校验后 `TryTransferLeadership`(`Network/handlers/RaidHandler.cs:267-289`),成功广播 USERINFO 上下文+RAID_MODIFY op2+RAID_LIST(`Parties.cs:63-82`);op0=编队分配:state 0 预备期 `TryAssignParty`(`RaidHandler.cs:300-303`),state 2/5 进行期经 `LivePartyAssignment` 委托(`:295-299`);成功广播对象+成员+怪血(`:309-313`)。

### CMD 0x029F (671) `MODIFY_RAID_INFO` — 修改攻坚队标题
- **请求**: 同 CREATE_RAID 标题布局(`RaidHandler.cs:447-462`)。
- **应答**: type1 ack;NOTI 0x0250 RAID_MODIFY op2:`u32 raidId + u32 2 + raidObject(raidId+dstr title+u8 0+u8 state+u8 arg+u32 0+leader)`(`RaidHandler.cs:326`;构造 `RaidPacketBuilder.cs:153-166,211-228`)。

### CMD 0x00D9 (217) — Raid 弹窗关闭分支
- **请求**: `u8 1 + u16 665`(3B)判为"创建攻坚队弹窗关闭"(`Network/handlers/RaidHandler.cs:330-347`)。
- **应答**: 无;Raid 分支仅吞包防落空。
- **行为**: 注册处先试 Raid 分支,未命中才落入彩票 `LotteryItemHandler.HandleOverflowInfo`(`Network/Protocol/GameProtocolHandler.cs:425-430`;彩票 OverflowInfo 归商城域文档)。

### 1.2 Raid 主要推送布局速查(`Network/Builders/Raid/RaidPacketBuilder.cs`)

| NOTI | 布局 | 构造 |
|---|---|---|
| 0x0250 (592) RAID_MODIFY | `u32 raidId + u32 op(0 对象/1 移除/2 信息/3 成员) + [raidObject] + [成员表] + [列尾]`;raidObject=`u32 raidId + dstr title + u8 0 + u8 state + u8 arg + u32 0 + leader`;member=`u16 uid + u8 1 + dstr name + u8 job + u8 growType + u8 party + u8 0 + u32 cid + u8 0 + u8 0`;列尾=`u32 826491730 + u8 phase + u8 count + {u16 uid, u32 cid, u32 phaseClearCount}` | `:109-121,153-184,186-237` |
| 0x024F (591) RAID_LIST | `u32 count + {raidObject + u8 memberCount}`(无 op 字段) | `:133-150,248-266` |
| 0x024C (588) RAID_STATE | `u8 state + u8 arg` | `:292-306` |
| 0x024A (586) RAID_DUNGEON_STATE | `u8 0 + u8 count + {u32 dungeonId, u8 state} + u32 infectionDungeonId` | `:418-452` |
| 0x024B (587) RAID_CHANGE_DUNGEON_STATE | `u32 dungeonId + u8 0 + u8 state` | `:454-465` |
| 0x0248 (584) RAID_SET_SYMBOL | `u8 count + {u32 id, u32 value}` | `:397-416` |
| 0x0249 (585) RAID_DUNGEON_PARTICIPATION_INFO | `u8 1 + u32 targetId + u8 op + u8 count + count×u16 uid` | `:467-486` |
| 0x024E (590) RAID_MEMBER_STATE | `u16 uid + u8 state`(0 死亡/1 复活) | `:488-494` |
| 0x025A (602) RAID_RESULT | `u8 resultType + u8 phase + u32 clearTime + u16 deaths + u8 rank + u8 rewardOption` | `:347-360` |
| 0x027A (634) / 0x027B (635) / 0x0257 (599) / 0x02EB (747) | 见上文各协议条目 | `:517-543 / :545-569 / :496-515 / RaidWaitingPacketBuilder.cs:8-32` |

成员表推送前会先逐接收者补 NOTI 0x0002 USERINFO 上下文(`Network/handlers/RaidHandler.Directory.cs:80-118,145-153`)。

## 2. PVP

### 2.0 房间生命周期/协调器/中继概述

建房(无 ack)→ 等待房(房态 1)内席位/准备/编队模式/地图互改 → 全员准备(房主除外)即开战(房态 2,NOTI 0x002D START_PVP)→ 逐人上报死亡,最后一死进 rank 结算(NOTI 0x0031)→ 逐人 RES_PVP_RANK 确认,全员确认(或 5s 超时强推)后广播 NOTI 0x002F END_PVP 结算 → 逐人 END_PVP_RESULT 确认(或 5s 超时)回房态 1。房间注册表按 listener+generation 隔离;加入采用"gate 内预留(`_roomAdmission.TryReservePendingJoin`)→ 中继预分配 → 直接握手期间不持 gate → `TryCommitPreparedJoin` 原子提交"(`Network/handlers/PvpRoomHandler.cs:913-1268`)。席位座位状态:0xFE=关闭(退出/踢人)、0xFF=空(`Game/Pvp/FreeDuelRoom.cs:8-21`)。`PvpRoomAdmissionCoordinator` 管 pending join/邀请(`Network/handlers/Pvp/PvpRoomAdmissionCoordinator.cs:12-60`);`PvpRelayLifecycleCoordinator` 在加入/退出/开战/关房时同步 `PartyUdpRelay` 房间代(`Network/handlers/Pvp/PvpRelayLifecycleCoordinator.cs:74-228`);`SET_UDP_IP_PORT` 上报端点变化后重发 peer 名册(`PvpRoomHandler.cs:1576-1680`;包装 `GameProtocolHandler.cs:432-442`)。

### 2.1 协议条目

### CMD 0x00C3 (195) `PVP_CHANNEL_INFO` — PVP 渠道信息
- **请求**: 空 body(`Network/handlers/PvpChannelInfoHandler.cs:49-50`)。
- **应答**: type1 成功 `[1, i32 0, u8 0]`(`:69-77`);失败 `[0, 0]`(`:79-86`)。
- **行为**: 未认证静默丢弃(`:41-47`);可用性=`GameNetworkConfig.FreeDuelListenerEnabled`(`:88-91`)。

### CMD 0x0032 (50) `MAKE_PVP_ROOM` — 创建 PVP 房间
- **请求**: `u8 roomNameType`(=0 时 `i32 len + u8[len] 房名(≤29)`)+ `i16 mapIndex` + `u8 hasPassword`(=1 时 `i32 len + u8[len] 密码(≤8)`)+ `u8 specialBattleMode`(仅 1 → battleMode 6,否则 2);解析 `Network/Parsers/Pvp/MakePvpRoomRequest.cs:66-165`(尾部多余字节拒绝)。
- **应答**: **无 ack**;成功序列=创建者 USERINFO(subtype1)→ NOTI 0x0029 (41) PVP_ROOM_INFO → NOTI 0x0017 (23) USER_AREA(area=0xFE),再向就绪监听者排队 NOTI 0x0029 + NOTI 0x0003 (3) USER_STATE(state=2)(`Network/handlers/PvpRoomHandler.cs:609-646,731-775`);失败 type1 `[0, errorCode]`(`:5198-5207`)。
- **行为**: 前置=已 lobby-ready、UserState==0、无进行中副本(`:579-594`);gate 内 `TryCreate` 并预留发布序(`:655-713`);先发创建者 USERINFO 再 gate 内复核房间未被并发改变(`:731-764`);置 `UserState=2`(`:766-767`);任何失败回滚房间并断开(`:782-799`)。
- **布局**: PVP_ROOM_INFO 房间条目=`u16 roomId + u8 nameType + [i32 len+bytes 房名] + u8 roomState + u8 managerSeat + i16 mapIndex + u8 battleMode + 8×{u8 seatState, u16 uid} + u8 hasPassword + i32 matchingType`(`Network/Builders/Pvp/PvpRoomNotificationBuilder.cs:254-288`)。

### CMD 0x0033 (51) `ENTER_PVP_ROOM` — 进入房间
- **请求**: `i16 roomId + u8 hasPassword [+ i32 len + u8[len] 密码]`;解析 `Network/Parsers/Pvp/EnterPvpRoomRequest.cs:35-111`。
- **应答**: 成功 type1 `[1, 8×u8 各席 ready]`(`PvpRoomNotificationBuilder.cs:187-205`;发送 `PvpRoomHandler.cs:1184-1205`);失败 type1 `[0, errorCode]`,邀请路径经 NOTI 0x000B `[0, errorCode, 2]`(`PvpRoomHandler.cs:5225-5240`)。
- **行为**: gate 内仅预留(房 pending/加入 pending → 错误 22,`:913-1023`);先做中继预分配,失败可重试拒绝(`:1044-1096`);直接握手序列:新人对内收 既有成员 USERINFO0 花名册+各成员 USERINFO1+0x000B 名册 → 自身 USERINFO1+自身 peer 记录 → ack → 席位 NOTI 0x002B + USER_AREA(`:1114-1252`);`TryCommitPreparedJoin` 原子提交(`:1259-1268`);房内他人随后收新人席位/peer 增量。

### CMD 0x0034 (52) `SET_PVP_SEAT_STATE` — 席位操作/退房/踢人
- **请求**: `u8 seatIndex + u8 seatState`(2B,seat<8,state 合法;`Network/Parsers/Pvp/SetPvpSeatStateRequest.cs:14-31`)。
- **应答/推送**: 无 ack;NOTI 0x002B (43) PVP_SEAT_STATE `u16 roomId + u8 battleMode + u8 1 + {u8 seat, u8 state, u16 uid}`(`PvpRoomNotificationBuilder.cs:207-222,340-348`)。
- **行为**: 三分支(`Network/handlers/PvpRoomHandler.cs:2202-2480`):①房主对他人席位发 0xFE=踢人(`TryRemoveNonOwnerMember`,被踢者收 USER_STATE(0)+空席位包 `:2222-2273`);②对自己发 0xFE=退房:房主退房有成员则提升继任(`TryRemoveOwnerAndPromote`,`:2305-2321`),无成员则销毁房间推 NOTI 0x002A 摧毁态(`BuildDestroyedRoomStateBody` `:23-34`),普通成员仅腾席位;③其余=换座(`TrySetSeatState`,revision 变化才广播 `:2455-2480`);退房后 UserState=0、中继同步/关房(`:2544-2600`);被踢收尾延后到 transition 释放后执行防锁序死锁(`:2107-2131`)。

### CMD 0x0035 (53) `SET_PVP_READY_STATE` — 准备/开战
- **请求**: `u8 ready`(1B;`Network/Parsers/Pvp/SetPvpReadyStateRequest.cs:14-30`)。
- **应答/推送**: 无 ack;NOTI 0x002C (44) PVP_READY_STATE `[u8 seat, u8 ready]`(`PvpRoomNotificationBuilder.cs:52-66`);全体就绪(房主恒视为就绪)→ 成员收 0x002C + NOTI 0x002D (45) START_PVP `[u8 mapIndex, u8 battleMode]`(`:68-85`) + NOTI 0x002A,大厅旁观者仅收 0x002A(`Network/handlers/PvpRoomHandler.cs:3067-3159`);房主就绪还向全监听发 0x002A(`:3077-3109`)。
- **行为**: battleMode 6(练习)静默吞包不发任何通知(`:3020-3026,3168-3175`);房主不可取消就绪(`:3029-3031`);开战前先解析全部成员目标防消失竞态(`:3037-3050`);mode 3(车轮)开战延时 7s 发轮转(`:3189-3194`;默认延迟 `:61-64`)。

### CMD 0x0036 (54) `SET_PVP_TEAM_MODE` — 修改对战模式
- **请求**: `u8 battleMode(1..6)`(`Network/Parsers/Pvp/SetPvpTeamModeRequest.cs:14-30`)。
- **应答/推送**: 失败 type1 `[0, err]`(`Network/handlers/PvpRoomHandler.cs:3921-3939,4002-4008`);成功无 ack,按 occupied 席位逐个发 NOTI 0x002B 增量(`:3974-3994`)。
- **行为**: 仅房主(`CanMutateOwnedRoom`,`:3919,3948`);`TrySetBattleMode` 提交。

### CMD 0x003B (59) `SET_PVP_MAP_INDEX` — 修改地图
- **请求**: `i16 mapIndex`(2B,`Network/handlers/Pvp/PvpMapHandler.cs:17`)。
- **应答/推送**: **无命令应答**;成功仅广播 NOTI 0x002A 房间状态(`PvpMapHandler.cs:32-36`;布局=`u16 roomId + u8 roomState + u8 managerSeat + i16 mapIndex + u8 battleMode + i32 matchingType`,`PvpRoomNotificationBuilder.cs:36-50`)。
- **行为**: 仅房主、等待房内(`PvpMapHandler.cs:27-31`);错误只记日志(`:42-45`)。

### CMD 0x0037 (55) `DIE_PVP_CHARACTER` — 上报死亡
- **请求**: 兼容两形态:`u16 deadUid`(2B)或 `u16 deadUid + u16 killerUid + u8 bool`(5B,bool≤1)(`Network/Parsers/Pvp/DiePvpCharacterRequest.cs:14-31`)。
- **应答/推送**: 无 ack;房间广播 NOTI 0x002E (46) DIE_PVP_CHARACTER `[u8 deadSeat, u8 killerSeat|0xFF]`(`PvpRoomNotificationBuilder.cs:87-106`;发送 `Network/handlers/PvpRoomHandler.cs:3405-3437`);终局(最后一人死亡)追加广播 NOTI 0x0031 (49) REQ_PVP_RANK 空 body(`:3416-3424`)。
- **行为**: `TryReportDeath` 校验上报者即死者会话、killer 为可选参考(`:3385-3395`);终局后 5s 无全员 rank 确认则强制结算(`ScheduleRankSettlementTimeout` `:3697-3750`;超时默认 `:59-60`);mode 3 非终局死亡 3s 后发车轮轮转(`:3454-3457`)。

### CMD 0x0038 (56) `PVP_TIME_OUT` — 客户端超时报告(fail-closed)
- **请求**: `8×i32`(32B,内容不透明;`Network/Parsers/Pvp/PvpTimeOutRequest.cs:14-24`)。
- **应答**: 无。
- **行为**: 恒忽略——客户端自报值不得单方面结束对局(`Network/handlers/PvpRoomHandler.cs:3599-3620`)。

### CMD 0x0039 (57) `END_PVP_RESULT` — 结算画面对局确认
- **请求**: 空 body(`Network/Parsers/Pvp/EndPvpResultRequest.cs:12-19`)。
- **应答/推送**: 无 ack;全员确认后广播 NOTI 0x002A 房间状态(`Network/handlers/PvpRoomHandler.cs:3576-3589`)。
- **行为**: `TryAcknowledgeEnd` 计数;5s 未齐强制完成(`ScheduleEndSettlementTimeout` `:3752-3808`)。

### CMD 0x003A (58) `RES_PVP_RANK` — rank 结算确认
- **请求**: 70B 不透明块,仅作确认(`Network/Parsers/Pvp/PvpRankResponseRequest.cs:14-24`)。
- **应答/推送**: 无 ack;全员确认(或超时)→ 逐成员推 NOTI 0x002F (47) END_PVP 结算(`QueueEndPvpResult` `Network/handlers/PvpRoomHandler.cs:4764-4790`)。
- **布局**: END_PVP = `u8 winnerSeat + i32 0(胜点) + u8 grade + i32 expChange + u8 combatantCount + 各战斗席位{u16 uid, i32 killCount, u8 grade, i32 exp, i32 rankPoint, i32 peakRankPoint} + u16 0xFFFF(ACE) + u16 0xFFFF(全场最佳) + i32 0(奖励经验) + u8 isDraw + u8 0xFF + i32 0 + u8 0(奖励物品数)`(`Network/Builders/Pvp/PvpRoomNotificationBuilder.cs:113-185`)。
- **行为**: `TryAcknowledgeRank`+`SettleMatch`(`:3497-3516`);结算后 5s end 超时兜底(`:3524-3525`)。

### CMD 0x012A (298) `COMPLETE_LOAD_PVP` / CMD 0x012B (299) `CONNECT_P2P_PVP` — 开战加载信号
- **请求**: 0x012A 空;0x012B `u8 count(0..8) + count×{u8 seat, u8 status}`(`Network/Parsers/Pvp/ConnectP2pPvpRequest.cs:21-45`)。
- **应答**: 均无应答(旧版 CNormalMatch 为 no-op 回调,不伪造 NOTI 0x0119/0x011A)。
- **行为**: 统一 `ConsumeNormalMatchSignalAsync` 消费:需处于已开始的普通匹配(mode 0)房间(`Network/handlers/PvpRoomHandler.cs:3846-3894`)。

### CMD 0x0070 (112) `PVP_REQUEST_FIGHT` — 车轮战请求出战
- **请求**: 空 body(`Network/Parsers/Pvp/PvpRequestFightRequest.cs:12-21`)。
- **应答/推送**: 无 ack;广播 NOTI 0x0071 (113) PVP_REQUEST_FIGHT `[u8 seat]`(`Network/handlers/PvpRoomHandler.cs:3346-3360`;构造 `PvpRoomNotificationBuilder.cs:331-338`)。
- **行为**: 仅 mode 3 已开始房间的非观战者(`:3329-3338`);客户端按通知做可见切换(`:3346-3348` 注释);车轮轮转 NOTI 0x0070 (112) PVP_TURN_PLAYER = `u8 2 + {u8 activeSeat, u8 0}×2 队`(`PvpRoomNotificationBuilder.cs:290-329`;延时广播 `Network/handlers/PvpRoomHandler.cs:3622-3695`)。

### CMD 0x035F (863) `CHECK_PVP_TOTAL_MATCH_TEAM_NAME` / CMD 0x035E (862) `SET_PVP_TOTAL_MATCH_TEAM` — 全服赛战队
- **请求**: `i32 len + u8[len] 战队名(1..24)`,SET 另加 3×u8 名单位(`Network/Parsers/Pvp/PvpTotalMatchTeamRequest.cs:8-21`)。
- **应答**: CHECK 成功 type1 `[1]`、重名 `[0, 3]`(`Network/handlers/Pvp/PvpTotalMatchTeamHandler.cs:57-60`);SET 成功 type1 `[1, slot0, slot1, slot2]` 并先推 NOTI 0x031B (795) PVP_TOTAL_MATCH_TEAM_INFO `i32 len + u8[len] name + 3×u8 slots`(`:62-68`;构造 `Network/Builders/Pvp/PvpTotalMatchTeamBodyBuilder.cs:24-45`)。
- **行为**: 与房间开战共用 `_roomPublicationGate` 串行(`:33-34`);可在城镇或等待房使用(`CanUseTotalMatchTeam` `:71-89`);非法名回 `[0, InvalidNameErrorCode]`(`:44-46`);持久化于 SQLite(`SqlitePvpTotalMatchTeamRepository`)。

### CMD 0x0177 (375) `FAIR_PVP_SCORE` — 公平竞技积分查看
- **请求**: `u16 targetUid + u8 isSelf + u8 viewMode(1..3) + i32 actorIndex(-1..2)`(8B;`Network/Parsers/Pvp/FairPvpScoreRequest.cs:23-33`)。
- **应答**: type1(`Network/handlers/CharacterSelectHandler.cs:731-742,788-806`):基础 `u8 1 + u16 uid + u8 viewMode + u8 hasDetail + u8 pvpGrade + u8 ratingGrade + i32 个人胜 + i32 车轮胜 + u8 近10胜/负/平 + i32 连胜 + i32 0 + i32 0 + i32 峰值连胜`,hasDetail=1 追加详细尾(个人/车轮/队伍胜负平 + 按职业分项,`Network/Builders/Pvp/FairPvpScoreResponseBuilder.cs:18-62`)。
- **行为**: 目标须在线、同频道可查看、isSelf 与实际一致(`:757-786`);发送前再验请求者/目标双方身份未漂移(`:793-802`)。

### CMD 0x0176 (374) `INTEGRATE_MATCH_PVP_SCORE` — 全服赛积分查看
- **请求**: `u16 targetUid`(2B,`Network/Parsers/Pvp/IntegrateMatchPvpScoreRequest.cs:14-27`)。
- **应答**: type1 固定 69B 模板(成功字节后 68B):`u8 1 + u16 uid + 4×{i32 wins,i32 kills,i32 multiKills} + i32 累计胜 + i32/u32×3 地狱模式统计 + i32 末项`全部填 0(`Network/Builders/Pvp/IntegrateMatchPvpScoreResponseBuilder.cs:9-40`;处理 `CharacterSelectHandler.cs:744-754,788-789`)。
- **行为**: 目标校验同 FAIR(`:757-786`);无已验证的一赛季历史数据源,恒回空模板。

### 2.2 对战面:UDP 中继与推送 builder

- **PartyUdpRelay**(`Network/Core/PartyUdpRelay.cs:29`):TURN 式中继,每有序对 (owner→peer) 一个 socket,数据报落在哪条腿即唯一确定方向,对称 NAT 鲁棒;`DFO_UDP_RELAY` 门控,关闭时不实例化(`:19-24` 注释);PVP 以 `pvpUdpRelay` 注入 `PvpRoomHandler`(`Network/Protocol/GameProtocolHandler.cs:88,147`),公网 IP 必须为数值 IPv4(`PvpRoomHandler.cs:256-270`)。
- **PvpRelayLifecycleCoordinator**(`Network/handlers/Pvp/PvpRelayLifecycleCoordinator.cs:39-464`):按房间 generation 在加入/踢人/退房/开战/关房时 `TrySyncGenerationAsync`/`ReconcileGenerationAsync`/`CloseRoomAsync`;同步失败且代仍当前 → fail-closed 断开剩余成员(`PvpRoomHandler.cs:2497-2508`)。
- **peer 名册**:NOTI 0x000B (11) USER_UDP_IP_PORT,`u8 count + count×22B{u16 uid, IPv4 inner, IPv4 outer, u16 port(网络序), u32 accountId, u8 NAT, u32 MTU, u8 attr}`;开中继时按接收者定制:他人记录指向"接收者→该 peer"的中继腿端口、NAT 置 0,自身记录保留上报端点(`Network/Builders/Pvp/PvpPeerInfoBuilder.cs:9-15,63-160`;按收件人发布 `Network/handlers/PvpRoomHandler.cs:1909-1990`)。代理/未知 TCP 对端 fail-closed 为 0(`:134-140`)。
- **UdpRelayServer**(`Network/Core/UdpRelayServer.cs:20`):协议无关哑中继,尚未接主流程,仅作数据面地基。
- **其他 builder**:`PvpRecordBodyBuilder`(选角初始化 51B 战绩:`i32 胜/负/经验/经验下限/上限 + u8 grade + u8 ratingGrade + i32 -1 + i32 rankPoint + i32 peakRankPoint + i32 warmup + u8 0 + i32 0 + i32 全服赛分 + i32 全服赛 warmup`,`Network/Builders/Pvp/PvpRecordBodyBuilder.cs:24-49`);`PvpRoomAdmissionCoordinator`/`PvpRequiredPublicationCoordinator` 分别管加入预留与必达包排队(5s 超时,`Network/handlers/PvpRoomHandler.cs:55-64,205-216`)。
- **大厅就绪**:`HandleLobbyReadyAsync` 在 PVP 监听就绪后发房间快照/身份名册(`Network/handlers/PvpRoomHandler.cs:4020` 起;可发布条件 `:5093`),作为 MAKE_PVP_ROOM 的前置 `_lobbyReadySessions`。