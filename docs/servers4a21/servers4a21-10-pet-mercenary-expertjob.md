# 域十:宠物生物 / 佣兵 / 专家副职(ServerS4A21 实现参考)

> - **本册导航**:总览/帧结构/信封/分发架构/已实现 CMD 总表见 [servers4a21-00-overview.md](servers4a21-00-overview.md)。
> - 仅写 A21(`ServerS4A21\Server\DfoServer`)侧实现,供 DAFLegacy 回填参考。路径相对 `DfoServer` 根;行号为整理时点状态。

注册位置:宠物组 `Network/Protocol/GameProtocolHandler.cs:562-582`,佣兵组 `:953-959`,副职组 `:1036-1050`。

---

## 1. 宠物 / 生物(pet 组)

### CMD 0x0064 (100) `RENAME_CREATURE` — 宠物改名
- **请求**: `slot i16@0, listType u8@2, nameLen i32@3(0..13), name bytes@7`;handler 内实读 `Network/handlers/Pets/PetCreatureHandler.cs:267-290`。
- **应答**: 失败 `[0x00]`(:177,:206);名字非法回 `BuildCmdError(InvalidNameErrorCode)`(:184-187);成功 ack `[1, slot i16, listType u8]`(:300-307)。推送 NOTI 101 (0x0065) RENAME_CREATURE:`uid u16 + dstr 新名`,构造 :292-298。
- **行为**: 名字经 `NameInputValidator.TryValidateRawName` 上限 13B(:181);改前先结算副本饱食度 elapsed(:192-197);持久化 `PetCreatureMutationCommitService.TryCommitRename`(:198-201);成功后补发 Pet 列表槽更新(:215)。

### CMD 0x0066 (102) `HATCH_CREATURE` / CMD 0x00AD (173) `HATCH_CREATURE_EGG` — 孵蛋(同 handler 复用)
- **注册**: 两号均指向 `HandleHatchCreatureEgg`(GameProtocolHandler.cs:578,580)。
- **请求**: `TryParseCreatureEggHatchRequest`(`PetCreatureHandler.cs:231-265`)自适应两种布局:`listType u8@0 + slot i16@1`(listType=Pet)或 `slot i16@0 + listType u8@2`;蛋模板 ID 在 offset 7/2/3 处探测 i32,须能经 `CreatureEggResolver` 解出孵化产物(`Game/Inventory/Pets/CreatureEggResolver.cs:8-26`:PVF equipment 为 `[creature]` 且 `OutputIndex>0` 且 ≠ 自身)。
- **应答**: 失败 `[0x00]`(:103,:124);成功 `[1]`(`CommonPacketBodyBuilder.BuildSuccessAck`,:128)。
- **行为**: 先结算副本 elapsed(:108-113);`PetCreatureMutationCommitService.TryCommitHatch` 原子完成蛋→宠物替换(:114-119);成功后 SendUpdateItemList(Pet, slot)+ 全量 NOTI 105 生物列表刷新(:129-130)。

### CMD 0x00AE (174) `REQUEST_HATCHED_CREATURE` — 孵化结果确认
- **请求**: 忽略 body(`PetCreatureHandler.cs:135-140`)。
- **应答**: `[1]`(:137)+ 全量 NOTI 105 CREATURE_ITEM_LIST 刷新(:138)。

### CMD 0x007A (122) `CREATURE_SCRIPT_MESSAGE` — 宠物聊天/脚本消息
- **请求**: `mode u8@0, targetUniqueId u16@1, characterId u32@3, msgLen i32@7(≤256), msg bytes@11`;mode=1/7 时其后还有 `nameLen i32 + name`(校验长度但解析层不消费);`Game/Inventory/Pets/PetCreatureScript.cs:33-67`。
- **推送**: NOTI 119 (0x0077) CREATURE_SCRIPT_MESSAGE:`mode u8, senderUid u16, serverGroup u8, dstr 消息`(`PetCreatureScript.cs:23-31`);handler 用 0x00 信封回发给发送者本人(`PetCreatureHandler.cs:158-165`,注释:旧服为同屏广播,单机服无广播集合)。
- **备注**: 进号欢迎语复用 NOTI 119:`Network/Builders/Init/PetCreatureWelcomeMessageBodyBuilder.cs:9-46` 取出战宠物,从 PVF `creature/script/*.wrd` 的 `[ambient][room list]` 段抽反引号台词,按 cid×397⊕item⊕creatureId 哈希选一条(`PetCreatureScript.cs:69-115,221-235`)。

### CMD 0x01E0 (480) `VERIFY_CREATURE_QUEST` — 宠物进化任务校验
- **请求**: 不消费包体(注释引旧服 `Dispatcher_VerifyCreatureQuest::read()`)。
- **应答/行为**: `PetCreatureRuntimeService.VerifyCreatureEvolutionQuestAsync`(:344-366):仅当当前装备宠物存在待做进化任务时回发任务列表 `SendAcceptableQuestListAsync`,否则静默不回包。

### CMD 0x002C (44) `USE_STACKABLE` 宠物分支(主文档归背包域)
- 分发链为 死亡之塔→副本消耗品→宠物→普通(`GameProtocolHandler.cs:564-575`);宠物分支 `TryHandleUseStackable`(`PetCreatureHandler.cs:29-96`)仅接管宠物消耗品槽:先结算副本 elapsed 再扣减,成功后若饱食度变化→喂食救活判死宠物时补发 NOTI 107 REVIVAL(:69-81)+ NOTI 103 STATE(:83-86)+ NOTI 105 刷新(:88);ack 复用 `UseStackableAckBuilder`(:55-58)。

### 宠物背包移动(PetInventoryMoveCoordinator,一句话)
- `Network/handlers/Pets/PetInventoryMoveCoordinator.cs:11-33` 挂接物品移动流程:宠物槽×装备槽互移前结算 elapsed,完成后按需补 0x0069+NOTI2 mode0+名誉+婚礼回放(:40-56)与 Pet/Equipment 槽刷新(:58-81)。

### 初始化与 NOTI 100-107 推送概述
- **NOTI 105 (0x0069) CREATURE_ITEM_LIST**(init):`count u8 + n×entry`;entry = `creatureKey i32, field04 u8, modeFlag u8, progress i32, (modeFlag==1 时 2×u8), fieldAfter u8, dstr 文本, tailFlag u8`(`Network/Builders/Init/CreatureListBodyBuilder.cs:8-36`)。

| NOTI | 枚举 | body | 构造处 |
|---|---|---|---|
| 100 (0x0064) | DIED_CREATURE | `uid u16` | `PetCreatureRuntimeService.cs:861-878`(经一次性 timer 异步派发 :809-845,防重复) |
| 101 (0x0065) | RENAME_CREATURE | `uid u16 + dstr 名` | `PetCreatureHandler.cs:292-298` |
| 102 (0x0066) | GAIN_EXP_CREATURE | `level u8 + 0 u8 + exp i32` | `PetCreatureRuntimeService.cs:403-407`(通关按疲劳给经验 :368-428;升级/进化后补任务列表或 NOTI 106) |
| 103 (0x0067) | CREATURE_STATE | `creatureKey i32 + stomach i32` | `PetCreatureRuntimeService.cs:1051-1060`;副本分钟巡检同名 timer 节流同步(:886-926)。注释明令:不得用 0x0069 富格式 entry,否则客户端 stomach 被高位字节钳到 100(实机验证) |
| 106 (0x006A) | EVOLUTE_CREATURE | `param u16 + eventUid u16` | `PetCreatureRuntimeService.cs:993-1049`(eventUid=副本场景 uid,城镇回退 uid;进化后刷装备槽) |
| 107 (0x006B) | REVIVAL_CREATURE | `uid u16` | `PetCreatureRuntimeService.cs:1064-1069`(回城复活与副本喂食救活共用) |

- **运行时要点**: 副本进图锚定饱食度并排死亡 timer(`delay=Before×60/消耗倍率`,:486-538);分钟巡检结算(:656-710);城镇每 360s 回复饱食度(:29,:712-752);回城自动复活并连发 NOTI 107+103(:928-991);死亡后 `PetCreatureLastDeathCreatureKey` 登记,喂食救活据此判断需补 NOTI 107(:286-311)。

---

## 2. 佣兵(mercenary 组)

### CMD 0x01B9 (441) `MERCENARY_RETURN` — 佣兵归队
- **请求**: `purpose u8@0 + characterId i32@1`(最小 5B);`Network/Parsers/Mercenary/MercenaryCommandParser.cs:89-99`。
- **应答**: 成功 `[1, status u8=2, charId i32, itemId i32, itemCount i32, hasReward u8]`(`Network/Builders/MercenaryExpeditionBodyBuilder.cs:35-50`;注释:无奖励归队第二字节固定 2,不是 purpose 回显);失败 `[0, 7]`(:62-63;`Network/Handlers/MercenaryExpeditionHandler.cs:40-48`)。
- **行为**: 按账号锁串行(`Game/Mercenary/MercenaryService.cs:212`);无派出记录→失败(:214-216);`Settle` 结算奖励写 outbox 并尝试即时投递邮件(:296-307, :309-336);`purpose=0xFF`(RedeployReturnPurpose)用于换派出时结算旧任务(:14, :170-185)。

### CMD 0x01BA (442) `MERCENARY_INFO` — 冒险团信息
- **请求**: 无有效载荷(`MercenaryExpeditionHandler.cs:23-38`)。
- **应答**: `[1, manageLevel u8, managePoint i32, count u8, n×(charId i32, dstr 名, state u8, remainingSec i32, area u8, period u8, avatarBonusTier u8)]`(`MercenaryExpeditionBodyBuilder.cs:11-33`)。
- **行为**: 先补投递该账号到期奖励(`MercenaryService.cs:51-58`);列表=账号角色且 Lv≥配置门槛、≤255 条(:68-70);state: `FinishTime>now ? InProgress(1) : Complete(2)`,未派出 Waiting(0)(`Game/Mercenary/MercenaryExpeditionModels.cs:6-12,144-147`);未派出角色 area=0/period=255(:207-208)。
- **备注**: 进号以 cmd=1 MERCENARY_INFO 推同一布局(`Network/Builders/Init/MercenaryInfoCmdBodyBuilder.cs:10-43`)。

### CMD 0x01BB (443) `MERCENARY_COMPETITION` — 派出佣兵
- **请求**: `characterId i32@0 + areaIndex u8@4 + periodIndex u8@5`(最小 6B;`MercenaryCommandParser.cs:101-112`)。
- **应答**: 成功 `[1, charId i32, area u8, period u8]`(`MercenaryExpeditionBodyBuilder.cs:52-60`);失败 `[0, 21]`(错误码 21,`MercenaryExpeditionHandler.cs:14,63-87`)。
- **行为**: 校验角色存在/归属本账号/未删除/非当前在线角色/等级达门槛(`MercenaryService.cs:120-132`);period 合法、area 可见且非随机,随机区按等级可做列表随机解析(:133-151, :338-352);`finishTime=now+period.Hours×BaseTimeUnitSeconds`,溢出拒绝(:153-155);已有派出→替换并先结算旧奖励(:170-185);记录时装加成 tier(:167)。

### CMD 0x01E5 (485) `REQUEST_CHARAC_SKILL_INFO` — 支援兵技能表
- **请求**: `wireSlotEcho u16@0`(客户端填充到固定缓冲,只校验最小 2B;低字节即角色表下标);`MercenaryCommandParser.cs:67-75`。
- **应答**: 成功 `[1, echo u16, job u8, growType u8, count u8, n×(combo u8, skillId u16, displayLevel u8)]`(条目 4B/条,`Network/Builders/StrikerSupportSkillListWriter.cs:12-53`);失败 `[0x00, err]`(:32-33)。
- **行为**: wire 下标直接索引账号角色表(`Game/Mercenary/StrikerSupportRoster.cs:6-21`);技能页按 job/grow/level 从 `StrikerSupportSkillListSource.Load` 加载(`Network/Handlers/MercenaryHandler.cs:61-108`)。

### CMD 0x01E8 (488) `SELECT_STRIKER` — 选定支援兵
- **请求**: `wireSlot u8@0 + skillId u16@1`(最小 3B;`MercenaryCommandParser.cs:77-87`)。
- **应答**: 成功**不回 cmd ack**,只推 NOTI 415 (0x019F) TAG_CHARACTER_INFO;失败才回 `[0x00, err]`(类注释 `MercenaryHandler.cs:13`;:110-192)。
- **行为**: 点自己=取消支援→清持久化并推空 body 0x019F(:194-216);资格=Lv50+(PVF 最低等级)且完成一觉(`growType&0xF0>=0x10`,`StrikerSupportRoster.cs:23-33`);所选技能必须在候选可用表中(`MercenaryHandler.cs:150-158,223-235`);持久化 `MercenarySupportState`(owner+单槽,:160-189)。0x019F record 布局:`uid u16 + dstr 名 + level u8 + job u8 + grow u8 + skillId u16 + u32 长度 + 88B 战斗块 + 装备 count u8 + n×Noti2 装备项(槽 0-28+勋章 31,含默认时装补齐) + cloneTitle u32 + 技能 count u8 + treeIndex u8 + 4B/条技能表 + 0x6F + u32 0`(`Network/Builders/Init/StrikerSupportTagCharacterPacketBuilder.cs:212-261,292-348,377-446`);组队进本投影多 owner 聚合 `count u16 + n×record`(:77-128)。

---

## 3. 专家副职(expert-job 组)

店铺会话状态机与双人锁见 `Network/Handlers/ExpertJobStoreHandler.cs`;解析全在 `Network/Parsers/ExpertJob/**`,构造在 `Network/Builders/ExpertJob/**`。通用错误码:店铺 10 请求非法/19 状态非法(`Game/ExpertJob/ExpertJobStoreRuntimeService.cs:8-10`);落点 190 不可用点/82 商业限制区(`ExpertJobStorePlacementValidator.cs:12-13`)。

### CMD 0x00EF (239) `GIVEUP_EXPERT_JOB` — 放弃副职
- **请求**: 无包体(`Network/Parsers/ExpertJob/GiveupExpertJobRequest.cs:5-7`)。
- **应答**: 成功 `[1, currentGold i32, giveupCount u8]`(`Network/Builders/ExpertJob/ExpertJobGiveupPacketBuilder.cs:69-79`);失败 `[0, err]`(:81-83)。
- **行为**: 副本中/开店中/在店中拒绝(`Network/Handlers/ExpertJobGiveupHandler.cs:39-71`);`ExpertJobGiveupApplicationService.Apply` 扣金/清副职状态;成功投影一串补偿包:Main 槽刷新+NOTI 19 (0x0013) 技能全量+NOTI 205 (0x00CD) EXPERT_JOB_INFO(type=0,清零态)+现役/可接任务列表+USERINFO0(`Network/Handlers/ExpertJobGiveupNotificationProjector.cs:36-117,119-132`)。

### CMD 0x0257 (599) `CREATE_EXPERT_JOB_STORE` — 开店(拆机/附魔)
- **请求**: `kind u8@0 + nameLen i32@1 + name + cost i32 + x i16 + y i16 + dir i16`,总长恒 15+nameLen(`Network/Parsers/ExpertJob/CreateExpertJobStoreRequest.cs:8-37`)。
- **应答**: 成功 `[1]`(`Network/Handlers/ExpertJobStoreHandler.cs:172-175`);失败 `BuildCmdError(code)`(:841-847)。
- **推送**: NOTI 539 (0x021B) CREATE_EXPERT_JOB_STORE:`kind u8, ownerUid u16, dstr 名, town u8, area u8, x i16, y i16, cost i32, mode u8=0(0=店主分身转商店,1 会残留分身,故恒 0), (附魔店追加: count u8 + n×卡片资格等级 u8)`(`Network/Builders/ExpertJob/ExpertJobStorePacketBuilder.cs:9-56`);广播同 area 其他人(:176-183),他人进 area 补发(:796-809)。
- **行为**: 须在城镇/非副本/非组队(`ExpertJobStoreRuntimeService.cs:363-395`);kind 须匹配自身副职(拆机=3:机器 grade>0、耐久>0;附魔=1:耐久>0 且有卡片资格),cost≤各店 `MaximumStoreCharge`(:340-361);落点须在 movable 区内且与 NPC 保持 x±80/y±150 商业限制距离(`Game/ExpertJob/ExpertJobStorePlacementValidator.cs:46-70`);成功后把玩家坐标写为开店点(`ExpertJobStoreHandler.cs:170-171`)。

### CMD 0x0258 (600) `ENTER_EXPERT_JOB_STORE` — 进店
- **请求**: `ownerUserId u16`(≠0,恒 2B;`Network/Parsers/ExpertJob/EnterExpertJobStoreRequest.cs:9-20`)。
- **应答**: 成功拆机店 `[1, kind u8, machineGrade u8, cost i32, endurance i32]` / 附魔店 `[1, kind u8, ownerUid u16, endurance i32]`(`ExpertJobStorePacketBuilder.cs:66-92`);失败 error 19(`ExpertJobStoreHandler.cs:190-223`)。
- **行为**: 非副本;店铺须在同 town/area(:201-214)。

### CMD 0x0259 (601) `CLOSE_EXPERT_JOB_STORE` — 关店/离店
- **请求**: 允许空包体(`Network/Parsers/ExpertJob/CloseExpertJobStoreRequest.cs:5-7`)。
- **推送**: NOTI 540 (0x021C) CLOSE_EXPERT_JOB_STORE:`ownerUid u16 + ownerCid u16`(`ExpertJobStorePacketBuilder.cs:58-64`);店主侧还补 USERINFO0(注释:CLOSE 与 USERINFO0 必须发店主会话而非访客会话,`ExpertJobStoreHandler.cs:812-839`)。
- **行为**: 本处理一身三用:持有店=关店并广播;仅是访客=离店静默;两者皆非回 close 包(:723-762);断线清理 `CloseSessionAsync`(:764-794,分发在区域切换/掉线钩子 `GameProtocolHandler.cs:776-778`)。

### CMD 0x00EB (235) `REQUEST_DISJOINT_ITEM` — 拆机(在店)/地狱派对入口(不在店)
- **分流**: `HandleSharedDisjointOrHellParty`(`Network/Protocol/GameProtocolHandler.cs:1052-1060`):`HasEnteredStore(session)` 为真走拆机 `ExpertJobStoreHandler.HandleDisjoint`,否则走 `_dungeonHandler.Handle_ENUM_CMDPACKET_HELLPARTY_START`。
- **请求**: `ownerUserId u16@0(≠0) + targetSlot i16@2 + itemSpace u8@4(必须=Main)`,恒 5B(`Network/Parsers/ExpertJob/DisjointMachineRequest.cs:12-33`)。
- **应答**: 成功 `[1, slot i16, itemSpace u8, matCount u8, n×(slot i16, itemId i32, count i32), requesterGold i32, endurance i32]`(`ExpertJobStorePacketBuilder.cs:94-112`);失败 `[0, err]`(:114-120):4 背包满/21 金币不足/19 物品不可拆/0xD4 机器等级不足/189 无耐久(`Game/ExpertJob/DisjointMachineService.cs:10-14`)。
- **推送**: 店主(非本人拆时)NOTI 203 (0x00CB) REQUEST_DISJOINT_ITEM:`[1, ownerGold i32, endurance i32]`(`ExpertJobStorePacketBuilder.cs:137-146`;`ExpertJobStoreHandler.cs:336-344`)。
- **行为**: 须已进该店且 owner/town/area 匹配、店主会话在线、双方 InventoryLease 归属校验(`ExpertJobStoreHandler.cs:243-262`);自助拆免费,跨人拆收 `store.Cost` 且店主金受携带上限,双 lease 按 cid 排序加锁(:264-307);拆机产出按机器 grade 放大(`DisjointMachineService.cs:63-91`);耐久随机减 `[EnduranceReduceMin..Max]`、经验随机 `[GainExpMin..Max]`(:108-122);持久化(库存+机器状态+经验)失败则回滚内存耐久(`ExpertJobStoreHandler.cs:315-330`);经验>0 补推 USERINFO0(:345-353);耐久耗尽自动关店广播(:360-367);同步任务寻物进度(:368-374)。

### CMD 0x00EC (236) `REPAIR_DISJOINT_MACHINE` — 拆机店机器修理
- **请求**: 无包体(`Network/Parsers/ExpertJob/RepairExpertJobStoreRequest.cs:5-7`)。
- **应答**: 成功 `[1, gold i32, endurance i32]`(`ExpertJobStorePacketBuilder.cs:158-165`);失败 `[0, err]`(:167-173)。
- **行为**: 仅拆机师(type=3)、非副本、未开店(`ExpertJobStoreHandler.cs:570-592`);`DisjointMachineRepairService.TryRepair` 扣金回耐久(:594-601);同事务持久化失败回错误(:609-623)。

### CMD 0x00F0 (240) `UPGRADE_DISJOINT_MACHINE` — 拆机店机器升级
- **请求**: 无包体(`Network/Parsers/ExpertJob/UpgradeDisjointMachineRequest.cs:5-7`)。
- **应答**: 成功 `[1, gold i32, grade i32, endurance i32]`(`ExpertJobStorePacketBuilder.cs:175-186`);失败 `[0, err]`(:188-194)。
- **行为**: 仅拆机师、未开店(`ExpertJobStoreHandler.cs:647-669`);按(持久化的)副职经验+角色等级判定可升等级(`DisjointMachineUpgradeService.TryUpgrade`,:671-681);持久化后回新 grade(:689-711)。

### CMD 0x01A0 (416) `EXPERT_EXTRACTION` — 抽取(提炼机)
- **请求**: `extractorType u8@0 + extractorSlot i16@1 + targetList u8@3 + targetSlot i16@4`,恒 6B(`Network/Parsers/ExpertJob/ExpertJobExtractionRequest.cs:9-24`)。
- **应答**: 成功 `[1, targetList u8, targetSlot i16, matCount u8, n×(slot i16, itemId i32, count i32)]`(`Network/Builders/ExpertJob/ExpertJobExtractionPacketBuilder.cs:41-59`);失败 `BuildCmdError`(`Network/Handlers/ExpertJobExtractionHandler.cs:147-151`)。
- **行为**: extractorType 须等于自身副职类型且配置存在(:50-62);抽取产出材料+副职经验,升级学习的新配方随机器状态同事务保存(:68-98);成功后刷 Main 目标槽+材料槽(:105-108)与 USERINFO0(:109-114);跨级时补 NOTI 205 (0x00CD) EXPERT_JOB_INFO(:115-127);同步任务寻物进度(:128-134)。

### CMD 0x0264 (612) `REPAIR_EXPERT_JOB_STORE` — 附魔店修理(EnchanterHandler)
- **请求**: 无包体(`Network/Parsers/ExpertJob/RepairExpertJobStoreRequest.cs:5-7`,与拆机修理共用校验类)。
- **应答**: 成功 `[1, gold i32, endurance i32]`;失败 `[0, err]`(复用 `ExpertJobStorePacketBuilder.cs:158-173`;发送 `Network/Handlers/EnchanterHandler.cs:93-98,109-113`)。
- **行为**: 仅附魔师(type=1)、非副本、未开店(`EnchanterHandler.cs:38-57`);`EnchanterMachineRepairService.TryRepair` 按副职经验计价修复(:59-69);`SaveEnchanterInTransaction` 持久化(:76-91)。

### CMD 0x025A (602) `USE_ENCHANT_STORE` — 附魔店附魔
- **请求**: `ownerUid u16@0 + recipeItemId i32@2 + mode u8@6(必须=2) + targetList u8@7 + targetSlot i16@8 + cardList u8@10 + cardSlot i16@11`,恒 13B;target/card 均须 Main 且两槽不同(`Network/Parsers/ExpertJob/EnchanterStoreUseRequest.cs:9-45`)。
- **应答**: 成功 `[1, enchantResult u8(1=成功), finalExp u32, legacy u8=0(客户端消费但不投影), endurance i32]`(`ExpertJobStorePacketBuilder.cs:122-135`);失败 `BuildCmdError`(`ExpertJobStoreHandler.cs:887-891`)。
- **推送**: 店主(非自助)NOTI 534 (0x0216) USE_ENCHANT_STORE:`ownerGold i32 + endurance i32`(`ExpertJobStorePacketBuilder.cs:148-156`;`ExpertJobStoreHandler.cs:507-517`,同时双方刷金币);附魔师跨级补 NOTI 205(:524-537)。
- **行为**: 进店/归属/店主须附魔师校验同拆机且更严(:393-413);获锁后二次复核店会话引用未变(:415-429);成功率=`experienceRule.SuccessRates[recipe.Qualification]`(`Game/ExpertJob/EnchanterStoreUseService.cs:98`),耐久扣减高等级店主减半(:42-57),金币/经验规则同拆机(:81-88,:125-142);持久化时按经验差自动学新配方(`ExpertJobStoreHandler.cs:476-490`);卡片资格等级随最终经验刷新并留在店会话(:492-494);耐久耗尽自动关店(:545-550)。

### CMD 0x00EE (238) `COMPOUND_ITEM_BY_EXPERT_JOB` — 副职合成
- **请求**: `recipeItemId i32@0 + requestedCount u16@4 + cardSlotIndex i16@6`,恒 8B(`Network/Parsers/ExpertJob/ExpertJobCompoundRequest.cs:8-28`);`cardSlotIndex==-1` 为产品合成,`>=0` 为卡片(宝珠)合成(`Game/ExpertJob/ExpertJobCompoundCommand.cs:92-93`)。
- **应答**: 成功 `[1, outCount u8, n×(itemId i32, count i32), successCount i32, failureCount i32, 0 u8]`(`Network/Builders/ExpertJob/ExpertJobCompoundPacketBuilder.cs:8-28`);失败 `[0, err]`(:30-31)。
- **行为**: 未开店(`Network/Handlers/ExpertJobCompoundHandler.cs:71-75`);产品合成走 `ExpertJobCompoundService.TryCraftProduct`(需载入副职状态),附魔师的卡片合成走 `EnchanterCompoundService.TryCraftBead`(:84-103);经验/新配方同事务持久化(:111-123);抽取器背包变动时全量刷 Main 列表、否则按槽刷新+刷金币(:131-145);经验>0 补 USERINFO0(:146-154);需要时补 NOTI 205 (0x00CD)(:155-166)。

### NOTI 205 (0x00CD) EXPERT_JOB_INFO(副职信息推送,兼 init)
- body 由 `ExpertJobInfoBodyBuilder.BuildProjectedBody(type, state, exp)` 构建,进号、抽取/合成/附魔跨级、放弃副职(全零态)时推送(上文各处)。

---

## 附:本域协议号速查

| CMD | 枚举 | 走向要点 |
|---|---|---|
| 0x002C (44) | USE_STACKABLE | 宠物分支仅接管宠物消耗品槽 |
| 0x0064 (100) | RENAME_CREATURE | ack `[1,slot,list]` + NOTI 101 |
| 0x0066 (102) / 0x00AD (173) | HATCH_CREATURE / HATCH_CREATURE_EGG | 同 handler;蛋→宠物原子替换 |
| 0x007A (122) | CREATURE_SCRIPT_MESSAGE | 解析 mode/target/cid/dstr;NOTI 119 回发 |
| 0x00AE (174) | REQUEST_HATCHED_CREATURE | `[1]` + NOTI 105 全量 |
| 0x01E0 (480) | VERIFY_CREATURE_QUEST | 有进化任务才回任务列表 |
| 0x01B9/01BA/01BB (441/442/443) | MERCENARY_RETURN/INFO/COMPETITION | 账号级远征;错误 7/21 |
| 0x01E5 (485) / 0x01E8 (488) | REQUEST_CHARAC_SKILL_INFO / SELECT_STRIKER | 成功选兵只推 NOTI 415 (0x019F) |
| 0x00EB (235) | REQUEST_DISJOINT_ITEM | 在店拆机,否则地狱派对入口;NOTI 203 通知店主 |
| 0x00EC/0x00F0 (236/240) | REPAIR/UPGRADE_DISJOINT_MACHINE | 空包体;拆机师专属 |
| 0x00EF (239) | GIVEUP_EXPERT_JOB | 空包体;补偿包一串 |
| 0x01A0 (416) | EXPERT_EXTRACTION | 6B;type 须=自身副职 |
| 0x0257/0258/0259/025A (599-602) | CREATE/ENTER/CLOSE_EXPERT_JOB_STORE / USE_ENCHANT_STORE | NOTI 539/540/534 |
| 0x0264 (612) | REPAIR_EXPERT_JOB_STORE | 附魔师专属 → EnchanterHandler |
| 0x00EE (238) | COMPOUND_ITEM_BY_EXPERT_JOB | 8B;cardSlot=-1 产品 / >=0 卡片 |