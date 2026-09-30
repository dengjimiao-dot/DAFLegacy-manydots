# 背包基础操作 / 商店买卖 / 金库仓库 / 消耗品(ServerS4A21)

> - **本册导航**:总览/帧结构/信封/分发架构/已实现 CMD 总表见 [servers4a21-00-overview.md](servers4a21-00-overview.md)。
> - 仅写 A21(`ServerS4A21\Server\DfoServer`)侧实现,供 DAFLegacy 回填参考。路径相对 `DfoServer` 根;行号为整理时点状态。

## 域综述

背包域 handler 全部挂在 `InventoryHandler` sealed partial class 上,按子域拆成 30 余个分部文件(`Network/handlers/InventoryHandler.*.cs`:Trade=买卖/金库、Move=移动/整理、Package=消耗品/礼包、Experience=经验道具、Durability/Repair=耐久、LimitedCube、CargoTransport 等),主文件只留构造与共享辅助(`Network/handlers/InventoryHandler.cs:18-78`)。所有变更都走"在线背包租约(InventoryLease)+ `TryGetOwnedInventoryLease` 会话归属校验"(`Network/handlers/InventoryHandler.Move.cs:425-432`),持久化经 `OnlineInventoryMutationCommitCoordinator.TryCommit` 在共享 Sqlite 连接/事务里先校验 lease 仍是当前会话再落库(`Game/Inventory/Services/OnlineInventoryMutationCommitCoordinator.cs:8-36`)。物品条目有两种线上形态:普通 101B(84B Entry84 + 17B 扩展)、时装 131B(再加 4+18+4+4B 宝珠/尾部),统一由 `Network/Builders/Inventory/ItemListProtocolWriter.cs:8-17` 写出,单条目/全表刷新共用。刷新纪律:多数操作只回 CMD ACK 由客户端本地记账,服务端仅在"服务端权威改写了客户端未知状态"时补发 NOTI 14(0x000E)定点条目或 NOTI 13(0x000D)整表重建(见下节)。金库/仓库等跨空间系统共用同一 InventoryService,空间枚举见 `Game/Inventory/InventoryListType.cs:3-21`(Main=0/Avatar=1/PersonalCargo=2/Equipment=3/Pet=7/AccountCargo=12/QuickSlot=29/KnightShieldEquipped=33/KnightShieldCatalog=34/GuildMedal=38)。

## InventoryRefreshSender — 背包刷新纪律(NOTI 13 / NOTI 14)

- **NOTI 13 (13) `ITEM_LIST` 整表重建**:`SendItemListRefresh(session, listTypes...)` 按 `ItemListPacketBuilder.BuildBody` 重发整个空间(`Network/handlers/InventoryRefreshSender.cs:173-182`)。触发点:整理后(`InventoryHandler.Move.cs:349`)、开通/扩容金库后(`InventoryHandler.Trade.cs:784,814`)、扩容个人仓库后(`InventoryHandler.Trade.cs:866`)、受限变更箱成功前(`InventoryHandler.LimitedCube.cs:86`,该界面不消费定点刷新)。
- **NOTI 14 (14) `UPDATE_ITEM_LIST` 定点更新**:`SendUpdateItemList(session, itemSpace, slot|slots)` → `SendOnlineUpdateItemList`,body=`[listType u8][count u16][count × 条目]`,条目为完整 101B/131B ItemCore 快照,槽位为空时写 `[slot i16][-1 i32]+零填充`(`Network/handlers/InventoryRefreshSender.cs:289-292,356-373`;`Network/Builders/Inventory/ItemListUpdateBuilder.cs:30-52,54-83,133-143`);`SendEmptyUpdateItemList` 直接发空条目形态(`InventoryRefreshSender.cs:294-303`)。仅支持 8 个空间(Main/PersonalCargo/AccountCargo/QuickSlot/Avatar/Equipment/Pet/GuildMedal,`InventoryRefreshSender.cs:417-427`);Equipment 发包前映射为 Avatar(`MapToNotiListType`,`InventoryRefreshSender.cs:400-405`)。
- **金币**即 Main 空间 0 号虚拟槽:`SendGoldUpdate` = 对 slot 0 发 NOTI 14(`InventoryRefreshSender.cs:269-272`;`Game/Inventory/Services/InventoryService.cs:28`),虚拟槽条目由 `ItemListPacketBuilder.WriteMainVirtualCountEntry` 写计数条目(`Network/Builders/Init/ItemListPacketBuilder.cs:256-274`)。
- **触发点速查**:BUY_ITEM(材料槽+金币+所得槽,`InventoryHandler.Trade.cs:273-306`)、SELL_ITEM 只回 ACK 不补 NOTI(客户端按 ACK 记账,`InventoryHandler.Trade.cs:413`)、DELETE_ITEM 只回 ACK(同上,`InventoryHandler.Trade.cs:115,164`)、INCREASE_STATUS 提交后刷来源槽(`InventoryHandler.Experience.cs:113-117`)、存取金库刷金币(`InventoryHandler.Trade.cs:748`)、魔法箱/礼包刷来源/材料/奖励槽(`InventoryHandler.Package.cs:1511-1514,1705-1747`)、搬运石按结果刷多空间(`InventoryHandler.CargoTransport.cs:81-126`)。

## 骑士盾(KnightShield)机制概述

守护者(Guardian)专属副武器系统,仅该职业可用(`Network/handlers/Dungeon/…/KnightShieldDataProvider.IsEligibleCharacter:job==Guardian`)。5 个 deck 槽:0=主盾、1-4=备用(`Game/KnightShield/KnightShieldDeckSnapshot.cs:8-9`),走两个专用空间:listType 33 KnightShieldEquipped / 34 KnightShieldCatalog(`Game/Inventory/InventoryListType.cs:17-18`)。入口有二:① MOVE_ITEMSPACE 前置分流(命中 33/34 空间即拦截,`Network/handlers/KnightShieldHandler.cs:30-116,240-253`),按方向分发到 TryEquipSlot/TryUnequipSlot/TryMoveDeckSlot,成功回 MOVE ack + NOTI 0x0245 `SEND_DECK_INFO`(deck 5×i32,无数量前缀,`Network/Builders/KnightShieldDeckBodyBuilder.cs:7-26`)+ 主盾变化时 NOTI 2 外观刷新(`KnightShieldHandler.cs:153-187`);② 普通背包 MOVE 触碰 SupportWeapon 槽后服务端主动同步 deck(`InventoryHandler.Move.cs:255-314`)。卸下请求不带 item id,权威状态从持久化 deck 读取(`KnightShieldHandler.cs:69-71`)。

### CMD 0x0292 (658) `CHANGE_DECK_INFO` — 骑士盾卡组整组保存

- **请求**:deck 5×i32(恰 20B,`Network/handlers/KnightShieldHandler.cs:15,124-132`;注册 `Network/Protocol/GameProtocolHandler.cs:559`)。
- **应答**:成功/拒绝均回 `[01][00][deck 5×i32]` 22B,拒绝时回权威 deck(`Network/Builders/KnightShieldDeckBodyBuilder.cs:28-38`;`KnightShieldHandler.cs:144-151,189-204`);随后推 NOTI 0x0245 `SEND_DECK_INFO` = deck 5×i32(`KnightShieldHandler.cs:206-214`)。
- **行为**:长度不符或 `TrySaveDeck` 拒绝 → 回权威 deck 快照(`KnightShieldHandler.cs:124-141`);主盾与当前外观不一致时重建 NOTI 2 外观(`KnightShieldHandler.cs:153-187`)。
- **备注**:本 CMD 常量取自 builder 的 `ChangeDeckCommandType = (ushort)CmdPacketTypeA21.CHANGE_DECK_INFO`(`KnightShieldDeckBodyBuilder.cs:8`)。

## 背包基础操作

### CMD 0x0012 (18) `DELETE_ITEM` — 丢弃/删除物品

- **注册**:先经 `_dungeonHandler.TryHandleDeathTowerDeleteItem` 分流(死亡塔临时背包,命中塔上下文即拦截;未命中返回 false 落回普通处理,`Network/Protocol/GameProtocolHandler.cs:459-464`;`Network/handlers/DungeonHandler.cs:267-269`;`Network/handlers/Dungeon/DeathTowerCoordinator.cs:1974`)。
- **请求**:双形态。批量形态 `body>=15 且 1<=body[1]<=100`:`[listType u8][count u8][count × (opType u16, slot i16, itemId i32, deleteCount i32)]` 每条 12B(`Network/handlers/InventoryHandler.Trade.cs:40-56`);简单形态经 `TryParseDeleteOrSellRequest`:`[listType u8][slot i16][count i16]` 5B,兼容旧 4B `[slot i16][count i16]`(`Network/handlers/InventoryHandler.cs:121-141`)。
- **应答**:成功 11B `[01][listType u8][01][slot i16][appliedCount i32][0 i16]`;失败 3B `[00][0x17][listType]`(`Network/Builders/Inventory/DeleteItemAckBuilder.cs:10-30`;发送 `InventoryHandler.Trade.cs:109-115,157-164`)。
- **行为**:① 副本训练场禁止丢弃时(RewardPolicy 不允许 discard)直接回错误 ACK(`InventoryHandler.Trade.cs:19-34,174-201`);② 批量形态逐条提交 `InventoryDeleteCommitService.TryCommit`,opType>1 视为技能材料删除并触发"使用物品"成就(`InventoryHandler.Trade.cs:60-104,203-204`);单条失败回错误 ACK 并 continue 不中断批次(`InventoryHandler.Trade.cs:106-118`);③ 批次完成后重校准任务寻物进度(`InventoryHandler.Trade.cs:120-128`)并补发成就进度包(见 ACHIEVEMENT_TRIGGER,`InventoryHandler.Trade.cs:130-137`);④ 简单形态提交成功后同样重校准任务(`InventoryHandler.Trade.cs:165-171`)。不发 NOTI 14,客户端按 ACK 本地扣减。

### CMD 0x0013 (19) `MOVE_ITEMSPACE` — 物品移动/堆叠/穿戴

- **注册**:四级分流(`Network/Protocol/GameProtocolHandler.cs:465-474`):① 交易 `Trade.TryMove`——body>=12 且 `body[0]==4 || body[11]==4`(交易栏空间 4)时拦截,放入/取回交易窗口并给双方回 MOVE ack + 物品 NOTI(`Network/Parsers/Inventory/ItemTradeRequest.cs:35`;`Network/handlers/ItemTradeHandler.cs:117-160`);② 死亡塔 `TryHandleDeathTowerMoveItem`(`Network/handlers/DungeonHandler.cs:261-263`;`DeathTowerCoordinator.cs:1848`);③ 骑士盾 `TryHandleMoveItemSpace`(源/目标空间为 33/34,见概述);④ 普通背包。
- **请求**:最少 14B `[srcList u8][srcSlot i16][srcInstanceValue i32][moveCount i32][dstList u8 @11][dstSlot i16 @12][dstInstanceValue i32 @14(>=18B 时)]`(`Network/handlers/InventoryHandler.Move.cs:27-36`)。
- **应答**:成功 11B `[01][srcList u8][srcSlot i16][moveValue i32][dstList u8][dstSlot i16]`;失败 4B `[00][err][srcList][dstList]`,err:0x02=无效操作(目标槽/不可堆叠/不可换位/客户端回显)、0x04=空间无效或背包缺失、0xB4=佣兵外观封锁(`Network/Builders/Inventory/MoveItemSpaceAckBuilder.cs:11-35`;`InventoryHandler.Move.cs:22-23,50-55,96-113,197-213,15`)。
- **行为**:装扮槽↔穿戴栏变动先过佣兵限制(`InventoryHandler.Move.cs:45-56,130-139`);宠物运行时预提交失败即回 0x04(`InventoryHandler.Move.cs:66-84`);`InventoryMoveCommitService.TryCommit` 带 Job/GrowType 做职业校验(`InventoryHandler.Move.cs:86-92`);成功后依次:宠物槽联动刷新(`InventoryHandler.Move.cs:118-123`)、触碰副武器槽时同步骑士盾 deck(`InventoryHandler.Move.cs:123,255-314`)、外观槽(装扮/武器/称号/宠物/名牌,副本内武器称号除外)补 NOTI 2(`InventoryHandler.Move.cs:125-127,215-253`)。普通成功不补 NOTI 14。

### CMD 0x0014 (20) `SORT_ITEM` — 背包整理

- **注册**:先经死亡塔分流(`Network/Protocol/GameProtocolHandler.cs:475-480`;`DeathTowerCoordinator.cs:1919`)。
- **请求**:`[listType u8][category u8][condition u8(可选)]`(`Network/handlers/InventoryHandler.Move.cs:318-323`)。
- **应答**:2B `[01][listType]`(`Network/Builders/Inventory/SortItemAckBuilder.cs:7-10`;`InventoryHandler.Move.cs:348`);随后推 NOTI 13 整表 + NOTI 0x00FB 装备锁定列表(`Network/handlers/InventoryHandler.Move.cs:349-350`;`InventoryRefreshSender.cs:310-320`)。
- **行为**:`InventorySortCommitService.TryCommit` 就地排序并落库,算法/受影响槽数仅记日志(`InventoryHandler.Move.cs:332-344`);失败静默返回(无错误 ACK)。排序锁定(CMD 0x02CA/0x02CB)不在本域展开。

## 商店买卖

### CMD 0x0015 (21) `BUY_ITEM` — NPC 商店购买

- **请求**:`[itemId i32][count i32][shopId i32][npcId i32]`(后三个字段按包长宽容缺失,`Network/handlers/InventoryHandler.Trade.cs:232-235`)。
- **应答**:成功 = `[01][gold i32][sp i32][0 i32][coin i32][物品条目 84B 头+扩展或旧式摘要][count u8][count × (itemId i32, requestedCount i32)]`;失败 2B `[00][0x00]`(`Network/Builders/Inventory/BuyItemAckBuilder.cs:12-95`;发送 `InventoryHandler.Trade.cs:268,293-294`)。
- **行为**:计价 `totalGoldCost=BuyGold×count`、`totalCeraCost=BuyCoin×count`, Crane 币兑换可覆盖材料需求(`Game/Inventory/Services/InventoryShopRuntimeService.cs:43-56`);顺序:规格化请求→空间检查→限购记录 `ItemPurchaseLimitService.TryRecordPurchase`→扣费(金币/点券/材料,任一失败整体回滚)→`InventoryRewardGrantService.TryCreateAndInsert` 入包(`InventoryShopRuntimeService.cs:31-113`)。成功后推送:代币材料槽 NOTI 14(耗尽时发空条目,`InventoryHandler.Trade.cs:273-282`)→ ACK → 金币 NOTI 14 → 所得槽 NOTI 14(`InventoryHandler.Trade.cs:293-306`);任务寻物进度同步(`InventoryHandler.Trade.cs:308-314`)。
- **备注**:注册处在 handler 返回后追加 `RaidHandler.RefreshEntryCostsAsync`(买后刷新团本入场费,`Network/Protocol/GameProtocolHandler.cs:481-488`)。

### CMD 0x02CC (716) `SHOP_PURCHASE_COUNT` — 限购商品已购计数查询

- **请求**:`[npcId i32]`(`Network/handlers/InventoryHandler.Trade.cs:325-327`)。
- **应答**:`[01][count i32][count × (itemId i32, 已购数 i32)][0 i32]`(`Network/Builders/Inventory/ShopPurchaseCountPacketBuilder.cs:11-28`)。
- **行为**:从 ShopCatalog 取该 NPC 限购商品定义,再查持久化计数;无定义或无记录时不回包(`InventoryHandler.Trade.cs:333-337`;`Game/Inventory/ItemPurchaseLimitService.cs:83-108`)。

### CMD 0x0016 (22) `SELL_ITEM` — 出售物品

- **请求**:复用 `TryParseDeleteOrSellRequest`:`[listType u8][slot i16][count i16]`(兼容 4B 旧式)(`Network/handlers/InventoryHandler.cs:121-141`;`Network/handlers/InventoryHandler.Trade.cs:352`)。
- **应答**:成功 10B `[01][updatedGold i32][listType u8][slot i16][soldCount i16]`;失败 2B `[00][err]`(`Network/Builders/Inventory/SellItemBuilder.cs:7-23`;发送 `InventoryHandler.Trade.cs:400,413`)。
- **行为**:计价 `sellGold×appliedCount`,目标金币超携带上限返回 22(`Game/Inventory/Services/InventoryShopRuntimeService.cs:281-288`);物品锁定返回 0xD6(`InventoryShopRuntimeService.cs:272-274`);规格非法返回 1(`InventoryShopRuntimeService.cs:269-279`)。err!=0 回错误 ACK,code==22 追加 NOTI 0x0033 `MONEY_FULL` `[03][0 i32][0 i32]`(`InventoryHandler.Trade.cs:384-409`)。成功只回 ACK(客户端本地扣物品、覆盖金币),再重校准任务进度(`InventoryHandler.Trade.cs:413-420`)。

### CMD 0x0017 (23) `REPAIR_EQUIPMENT` — 装备修理

- **请求**:`[inven_type u8][slot i16][repair_item_slot i16][auto u8 @5][pad u8][quick u8 @7]`;inven_type 0=背包/快捷栏、3=穿戴、2=货柜;slot=0xFFFF(-1) 全部修理;auto=1 魔王契约自动修理(免费);quick=1 侧边栏快速修理(费用×1.5)(`Network/handlers/InventoryHandler.Repair.cs:10-27,99-108`)。
- **应答**:成功 9B `[01][剩余金币 i32][inven_type u8][slot i16][0 i16]`;失败 2B `[00][err]`(0x0A 通用失败、0x11 inven_type 非法)(`Network/Builders/Inventory/RepairEquipmentAckBuilder.cs:19-35`;`Network/handlers/InventoryHandler.Repair.cs:20,36,80,85-87`)。
- **行为**:auto 修理先消耗魔王契约次数 `DevilContractUsagePolicy.TryConsume(AutoRepairSlot)`,失败整体拒绝(`Network/handlers/InventoryHandler.Repair.cs:49-61`);单价 `repairPrice×(grade+5)/10/maxDurability×lostDura×强化费率表`,快速修理再乘 `_quickRepairRate`(pricetable.tbl `[repair cost]`/`[quick repair cost rate]`,`Game/Inventory/EquipmentRepairPriceProvider.cs:18-33,46-60`);全部修理逐槽累加后一次扣费,ACK 只回 slot=0xFFFF,客户端自行拉满全身耐久(`Game/Inventory/Services/InventoryRepairService.cs:83-127`;`Network/handlers/InventoryHandler.Repair.cs:84-86`);免费契约修理后补发会员服务刷新(`Network/handlers/InventoryHandler.Repair.cs:88-94`)。

### CMD 0x0030 (48) `DECREASE_DURABILITY` — 穿戴耐久扣减

- **请求**:恰 1B `[equipmentSlot u8]`(`Network/Parsers/Inventory/DecreaseDurabilityRequest.cs:13-19`)。
- **应答**:成功 2B `[01][slot u8]`;失败 2B `[00][17]`(ErrorInvalidTarget=17)(`Network/Builders/Inventory/DecreaseDurabilityAckBuilder.cs:11-25`;发送 `Network/handlers/InventoryHandler.Durability.cs:71-82`)。
- **行为**:`InventoryDurabilityService.TryDecreaseEquippedDurability` 固定扣 1 点(`DefaultDecreaseAmount=1`),无效槽/空槽/非装备/已归零分别返回 noop reason(`Game/Inventory/Services/InventoryDurabilityService.cs:8-60`);handler 仅在 `Changed` 或 `zero_durability` 时回成功,其余回 17(`Network/handlers/InventoryHandler.Durability.cs:69-82`)。

## 消耗品(USE_STACKABLE 家族 / 经验道具 / 礼包)

### CMD 0x002C (44) / 0x0204 (516) `USE_STACKABLE` / `USE_STACKABLE_ACTION` — 使用消耗品

- **注册**:同一 lambda 依次分流(`Network/Protocol/GameProtocolHandler.cs:564-576`):① 死亡塔 `TryHandleDeathTowerUseStackable`(塔上下文命中即拦截,解析不出塔命令时返回 true 吞掉,`DeathTowerCoordinator.cs:1770`);② 副本内 `TryHandleDungeonUseStackable`——RewardPolicy 不允许消耗消耗品的训练/练习副本:只回 ACK 不改库存(`Network/handlers/InventoryHandler.Package.cs:20-82`,练习成功 ACK 首字节为 0 的特殊形态 `Network/Builders/Inventory/UseStackableAckBuilder.cs:13-19`);③ 宠物消耗品 `PetCreatureHandler.TryHandleUseStackable`(listType=Pet 且槽在宠物 consumable 区,喂食饱食度/复活,`Network/handlers/Pets/PetCreatureHandler.cs:29-80`);④ 普通 `Handle_ENUM_CMDPACKET_USE_STACKABLE`。
- **请求**:`[slot i16][listType u8 @2][instanceValue i32][itemCode i32 @7(>=11B 时)]`(`Network/handlers/InventoryHandler.Package.cs:129-135`)。
- **应答**:成功 11B `[01][slot i16][listType u8][instanceValue i32][itemCode i32]`;失败 11B `[00][err][listType][instanceValue i32][itemCode i32]`,默认 err=23(无效物品)(`Network/Builders/Inventory/UseStackableAckBuilder.cs:21-44`)。
- **行为(普通分支的拒绝/分流链)**:① 过期源直接移除并回错误 ACK+NOTI 14(`Network/handlers/InventoryHandler.Package.cs:139-149,342-406`);② 100 频道传送类消耗品按频道策略拒绝并追加城镇限制包(`Network/handlers/InventoryHandler.Package.cs:151-161,408-488`);③ 每日可用次数超限拒绝(`Network/handlers/InventoryHandler.Package.cs:163-173,490-549`);④ 副职配方书学习(`Network/handlers/InventoryHandler.Package.cs:175-177,551-664`,成功推 NOTI 0x00CD 副职信息);⑤ 任务完成券(`Network/handlers/InventoryHandler.Package.cs:179-187,1174+`);⑥ 金库扩容工具/仓库扩容券(分别回 USE_STACKABLE ACK + 0x0132/整表刷新,`Network/handlers/InventoryHandler.Package.cs:189-267,781-839`);⑦ 装备特效符文(`Network/handlers/InventoryHandler.Package.cs:269-289,742-770`);⑧ 普通消耗:`InventoryDeleteCommitService.TryCommitStackableUseDetailed` 扣 1 个(`Network/handlers/InventoryHandler.Package.cs:291-301`),成功回 ACK 后按物品追加:史诗 buff 药剂加/定时移除 buff(322-328)、可用次数状态 NOTI 0x021E `[01][itemId i32][usedCount i32]`(329-330;`Network/Builders/Inventory/UsableCountLimitPacketBuilder.cs:9-19`)、任务重校准(331-334);失败时宠物区陈旧消耗品也回成功 ACK 防客户端卡死(`Network/handlers/InventoryHandler.Package.cs:841-870`)。普通扣减不发 NOTI 14。

### CMD 0x001E (30) `INCREASE_STATUS` — 经验书/SP 书/复活币等经验道具

- **请求**:恰 2B `[slot i16]`,slot<0 拒绝(`Network/Parsers/Inventory/IncreaseStatusRequest.cs:12-24`;`Network/handlers/InventoryHandler.Experience.cs:32-40`)。
- **应答**:成功 12B `[01][uid u16][0xFF 状态类型][0 i32][0 u16][0 u16]`(明确"无状态变更",权威 EXP 走 NOTI);失败 2B `[00][err]`(0x11=来源缺失/已过期/扣减失败,0x01=未知)(`Network/Builders/Inventory/IncreaseStatusAckBuilder.cs:17-38`;错误映射 `Network/handlers/InventoryHandler.Experience.cs:343-355`)。
- **行为**:`ExperienceItemUseService.UseBySlot` 按道具定义发放 EXP/SP 并落库(`Network/handlers/InventoryHandler.Experience.cs:52-73`);成功后:来源槽 NOTI 14(`Experience.cs:113-117`)→ 复活币特殊:只刷钱包槽(`Experience.cs:119-125`)→ 其余走 `ExperienceItemNotificationService.SendAsync`:未升级只发 NOTI 0x0025 `EXP` 绝对快照;升级时城镇先 subtype0/subtype1(NOTI 0x0002)再 EXP,副本内先 EXP 再 subtype1+任务列表(`Network/handlers/ExperienceItemNotificationService.cs:46-100,133-155`);失败且来源可疑时刷新来源槽(`Experience.cs:93-108`)。
- **备注**:注册处在 handler 后比较 Level,升级则向加 self 为好友的玩家重推好友列表(`Network/Protocol/GameProtocolHandler.cs:498-507`)。

### CMD 0x01A2 (418) `REQUEST_EVENT_SERVER_LEVEL_UP` — 直升券(等级券)

- **请求**:`[slot u16][reserved u8]`(>=3B,旧抓包带全零尾部只消费已证实字段,`Network/Parsers/Inventory/LevelUpTicketRequest.cs:11-27`)。
- **应答**:成功 2B `[00 00]`(短体,等级/经验以后续 NOTI 为准);失败 2B `[00][err]`(0x11 缺失、0x16 等级受限、0x17 道具不支持、0x63 落库失败、0x01 未知)(`Network/Builders/Inventory/LevelUpTicketAckBuilder.cs:11-17`;映射 `Network/handlers/InventoryHandler.Experience.cs:15-19,386-404`)。
- **行为**:`UseLevelUpTicketBySlot` 直接到目标等级(`Experience.cs:175-195`);成功序列:ACK → NOTI 0x0169 `EVENT_SERVER_AUTO_QUEST_CLEAR_REWARD_DATA` 10B 零(`Experience.cs:221-229`)→ EXP 同步(不带任务列表,`Experience.cs:231-234`)→ 可接/进行中任务列表 + NOTI 0x0164 `CLEAR_QUEST_LIST`(`Experience.cs:235,262-295`)→ 来源槽 NOTI 14(`Experience.cs:236-240`)。

### CMD 0x00D0 (208) `OPEN_MAGIC_BOX_SINGLE` — 魔法箱单次开启

- **请求**:`[listType u8][slot i16][materialSlot i16(>=5B 时)]`,listType 仅接受 Main(0)/客户端主背包别名 4/旧式 5(`Network/Parsers/Inventory/MagicBoxOpenRequest.cs:75-97,99-113`)。
- **应答**:成功 `[01][clientType u8][双倍 u8][srcSlot i16][matSlot i16][count u16][count × (slot i16, itemId i32, count i32, dura u16, attr u8, expire i32, 23B 零)]`;材料不足静默关窗 `[01][0xFF][00]`(`Network/Builders/Inventory/MagicBoxOpenAckBuilder.cs:12-36,38-45,230-241`;失败分支 `Network/handlers/InventoryHandler.Package.cs:1598-1604`)。
- **行为**:统一走 `TryUseOnlineBoosterItem`→`InventorySpecialConsumableCommitService.TryCommitBoosterItem`(数量固定 1,`Network/handlers/InventoryHandler.Package.cs:1061-1111,1365-1384`);失败再补 NOTI 0x01F7 `SERVER_NOTICE_MESSAGE` 材料不足公告并刷新过期来源(`Network/handlers/InventoryHandler.Package.cs:1044-1051,1561-1588`)。

### CMD 0x0450 (1104) `USE_RANDOMBOX_ITEM_EXPAND` → `OPEN_MAGIC_BOX` — 魔法箱批量开启

- **请求**:`[listType u8][slot i16][itemId i32][materialSlot i16][materialItemId i32][count u16]`(>=15B,材料二选一给全或全不给,`Network/Parsers/Inventory/MagicBoxOpenRequest.cs:22-73`)。
- **应答**:赛丽亚幸运值道具(原生批量界面)回 `MagicBoxOpenAckBuilder.BuildBatch` = `[01][clientType u8][双倍 u8][消耗数量 u16][srcSlot i16][matSlot i16][主奖励列表 u16+n×行][0 u16][双倍奖励列表 u16+n×行]`;否则降级为 0x00A0 弹窗包(`Network/Builders/Inventory/MagicBoxOpenAckBuilder.cs:12-25`;判定与降级 `Network/handlers/InventoryHandler.Package.cs:1456-1494,1637-1640`)。
- **行为**:与 0x00D0 同一提交服务,数量取请求值(`Network/handlers/InventoryHandler.Package.cs:1009-1058`);成功后统一走 `SendBoosterUseResult`:来源槽耗尽发空条目、来源/材料/奖励槽 NOTI 14、幸运币/点券奖励落库后推 NOTI 0x0035 三币种、宠物奖励补 NOTI 0x0069、会员道具激活(`Network/handlers/InventoryHandler.Package.cs:1456-1538`)。

### CMD 0x00A0 (160) `OPEN_SELECTABLE_PACKAGE` — 自选礼包

- **请求**:`[slot i16][selectionContext i16][selectedItemId i32][flag u8 @8]`;尾部可带时装自选块(4×count 个 id + count u8 + count×(id i32, option u8)),按总长探测(`Game/Inventory/SelectablePackage.cs:25-80`)。
- **应答**:成功 `[01][slot i16][0 i32][0 i32][count u16][count × (itemId i32, count i32)]`;错误 `[00][00][-1 i32][-1 i32][0 i32][0 i32][0 i32]`(经 `CommonPacketBodyBuilder.BuildCmdError` 包装的 invalid 请求走 2B 形态)(`Network/Builders/Inventory/SelectablePackageAckBuilder.cs:11-46`;fallback 错误 `Network/handlers/InventoryHandler.Package.cs:1004-1007,978-995`)。
- **行为**:先走专用 `TryCommitSelectablePackage`,成功后 ACK + 来源/奖励槽刷新 + 会员激活(`Network/handlers/InventoryHandler.Package.cs:941-996`);道具不是自选包时降级为增强剂开启 `TryHandleBoosterOpen`(`Network/handlers/InventoryHandler.Package.cs:989,1113-1172`)。

### CMD 0x0207 (519) `OPEN_AVATAR_PACKAGE` — 时装礼包

- **请求**:`[slot i16][count u8 @2][count × (itemId i32, option u8)]`(1<=count<=32,恰 3+5×count B,`Game/Inventory/AvatarPackage.cs:25-56`)。
- **应答**:先回 `[01][slot i16]`(0x0207),有可见奖励再回 0x00A0 弹窗包;持久化失败回单字节 `[00]`(`Network/Builders/Inventory/AvatarPackageAckBuilder.cs:9-15`;发送 `Network/handlers/InventoryHandler.Package.cs:904-929`)。
- **行为**:礼包定义从 PVF `stackable/…[usable cera package]` 解析,奖励按 equipment 类型拆时装/普通/宠物并解析到期时间(`Game/Inventory/AvatarPackage.cs:117-156,182-207`);时装自选路径失败降级为通用 0x0207 包(`TryHandleOpenPackage0207`,请求按 `[slot i16][count u8][count×5B]` 探测,`Network/handlers/InventoryHandler.Package.cs:932-936,1338-1362,1886-1902`);最终失败回 `[00]`(`Network/handlers/InventoryHandler.Package.cs:938`)。

### CMD 0x0218 (536) `USE_BOOSTER_ITEM` — 增强剂/通用开启

- **请求**:`[slot i16][后续为候选 itemId 列表(多形态探测)]`(`Network/handlers/InventoryHandler.Package.cs:1116-1123,1870-1884`)。
- **应答**:无专用 ACK 形态——按 result 走 `SendBoosterUseResult` 默认分支:通用成功 ACK + 0x00A0 弹窗;完全失败回 `[00][err]`(err 0x16=材料不足映射,`Network/handlers/InventoryHandler.Package.cs:998-1002,1590-1596`)。
- **行为**:slot==0 是客户端"确认使用"回执,直接回成功 ACK(`Network/handlers/InventoryHandler.Package.cs:1139-1144`);body 匹配水晶契约(UPDATE_CONTRACT_OF_CUBE_INFO)形态时转为保存契约选择(`Network/handlers/InventoryHandler.Package.cs:1125-1137`);其余同 `TryHandleBoosterOpen` 共用提交服务。

### CMD 0x0152 (338) `USE_LIMIT_CUBE` — 受限变更箱(物品变换)

- **请求**:`[targetSlot i16][targetItemId i32][cubeSlot i16]`(8B,cubeSlot!=targetSlot,`Network/Parsers/Inventory/LimitedCubeUseRequestParser.cs:21-38`)。
- **应答**:成功 10B `[01][resultItemId i32][resultValue i16][isEquipment u8]`(装备结果置 1,客户端写入新物品 +0x5C);失败 `[00][0x11]`(`Network/Builders/Inventory/LimitedCubeAckBuilder.cs:8-27`;发送 `Network/handlers/InventoryHandler.LimitedCube.cs:87-90,99-107`)。
- **行为**:锁内读源/目标实测 itemId,与请求不一致或规则不匹配即拒绝(`Network/handlers/InventoryHandler.LimitedCube.cs:33-57`);`InventoryTitleChangeCommitService.TryCommitLimitedCube` 提交(`Network/handlers/InventoryHandler.LimitedCube.cs:67-72`);成功先 NOTI 13 整表重建主背包再回 ACK(该界面不消费定点刷新,`Network/handlers/InventoryHandler.LimitedCube.cs:84-90`)。

## 金库 / 仓库

### CMD 0x0133 (307) `DEPOSIT_MONEY` / CMD 0x0134 (308) `WITHDRAW_MONEY` — 金库存/取金币

- **请求**:`[amount i32]`(共用 `HandleCargoGold`,`Network/handlers/InventoryHandler.Trade.cs:691-714`)。
- **应答**:成功 `[01][金库余额 i32]`;失败 2B `[00][0x0A]`(含 body 过短/金额<=0,`Network/handlers/InventoryHandler.Trade.cs:703-746`)。
- **行为**:存入=扣 Main 金币+加 AccountCargo.Money(防 i32 溢出,`Game/Inventory/Services/InventoryCargoRuntimeService.cs:20-43`);取出相反且校验余额(`InventoryCargoRuntimeService.cs:45-68`);经事务协调器落库后发金币 NOTI 14 + 任务同步(`Network/handlers/InventoryHandler.Trade.cs:720-755`)。

### CMD 0x0131 (305) `CREATE_ACCOUNT_CARGO` — 开通账号金库

- **请求**:handler 不消费 body(`Network/handlers/InventoryHandler.Trade.cs:760-762`)。
- **应答**:成功 `[01]`;失败 `[00][err]`(0x14=金币不足/已开通,`Network/handlers/InventoryHandler.Trade.cs:766-782`)。
- **行为**:固定消耗 100,000 金币(`Game/Inventory/Services/InventoryCargoRuntimeService.cs:11,71-99`);成功后按扣费类型刷新(金币 NOTI 14 / 材料 NOTI 14 / NOTI 0x0035 三币种,`Network/handlers/InventoryHandler.Trade.cs:783,818-840`)再推 AccountCargo 整表 NOTI 13(`Network/handlers/InventoryHandler.Trade.cs:784`)。初始容量 1 格(`InventoryCargoRuntimeService.cs:8`)。

### CMD 0x0132 (306) `UPGRADE_ACCOUNT_CARGO` — 扩容账号金库

- **请求**:handler 不消费 body(`Network/handlers/InventoryHandler.Trade.cs:788-790`)。
- **应答**:成功 `[01]`;失败 `[00][err]`(0x15 默认、0x13 已满级/非法、0x14 扣费失败,`Network/handlers/InventoryHandler.Trade.cs:793-812`)。
- **行为**:容量档位 1/8/16…120(`Game/Inventory/Services/InventoryCargoRuntimeService.cs:14`);按当前 selectionKey 取计价:selectionKey==1 为 2,000,000 金币,否则点券,否则虚空魔石(itemId 3299)×250(`InventoryCargoRuntimeService.cs:11-13,330-384,490-496`);成功刷新同开通(`Network/handlers/InventoryHandler.Trade.cs:813-815`)。

### CMD 0x0198 (408) `UPGRADE_CARGO` — 扩容个人仓库

- **请求**:handler 不消费 body(仅日志,`Network/handlers/InventoryHandler.Trade.cs:842-845,861,867`)。
- **应答**:成功 `[01]`;失败 2B `[00][0x13]`(`Network/handlers/InventoryHandler.Trade.cs:858-865`)。
- **行为**:容量档位 24/40/56…200(`Game/Inventory/Services/InventoryCargoRuntimeService.cs:15-17`);`TryUpgradePersonalCargo` 推进档位后推 PersonalCargo 整表 NOTI 13(`Network/handlers/InventoryHandler.Trade.cs:849-866`);另有仓库扩容券道具走 USE_STACKABLE 分支(`Network/handlers/InventoryHandler.Package.cs:229-267,781-808`)。

### CMD 0x022E (558) `CARGO_TRANSPORT_ITEM` — 搬运石(跨角色/宠物搬运)

- **请求**:`[stoneSlot u16][targetSlot u16][isCreature u8 @4][targetCharacterSlot u32 @5]`(>=9B,偏移 3 字节未消费,`Network/Parsers/Inventory/CargoTransportStoneRequestParser.cs:12-34`)。
- **应答**:成功 10B `[01][stoneSlot u16][targetSlot u16][stoneRemain u16][param u16][mode u8]`;失败经 `CommonPacketBodyBuilder.BuildCmdError`:0x04 金库满、0xD6 目标锁定、0x16 来源缺失/变动/次数超限、0x11 目标缺失、0x07 通用(`Network/Builders/Inventory/CargoTransportItemAckBuilder.cs:12-76`)。
- **行为**:宠物搬运石按账号角色槽解析目标角色(未删、按 slot 排序)并经 `InventoryCargoTransportStoneCommitService` 提交,目标角色道具走邮件(`Network/handlers/InventoryHandler.CargoTransport.cs:37-46,158-174`);落库失败强制置 MutationFailed(`Network/handlers/InventoryHandler.CargoTransport.cs:47-51`);刷新:成功只刷 Pet 槽+任务重校准,失败刷 Main/AccountCargo/Pet 槽 + NOTI 0x0069 宠物列表 + 可用次数 NOTI 0x021E(`Network/handlers/InventoryHandler.CargoTransport.cs:81-126`)。

## 成就

### CMD 0x01A1 (417) `ACHIEVEMENT_TRIGGER` — 成就计数触发

- **请求**:`[questId i32][delta1 u16][delta2 u16][delta3 u16]`(10B,`Network/handlers/InventoryHandler.Trade.cs:539-545`)。
- **应答**:成功 10B `[01][questId i32][remain1 u16][remain2 u16][remain3 u16]`;失败 6B `[00][err][questId i32]`(默认 0x0A)(`Network/handlers/InventoryHandler.Trade.cs:588-641`)。
- **行为**:租约校验→`TryTriggerAchievement` 计数并落库→事务提交→回 ACK(`Network/handlers/InventoryHandler.Trade.cs:546-586`);完成且奖励称号时追加 NOTI 0x0168 `ACHIEVEMENT_COMPLETE` = `[questId i32][category i32][bookIndex i32][titleItemId i32][bookIndex u16]` + 称号簿分类整表 NOTI 0x0166(`Network/handlers/InventoryHandler.Trade.cs:608-624,664-674`)。DELETE_ITEM 批量删除技能材料时会合并产生本协议进度包(`Network/handlers/InventoryHandler.Trade.cs:130-137,206-225`)。