# ServerS4A21 已实现协议处理参考(九):商城代币 / 租赁 / 小游戏 / 活动 / 彩票 / 邮箱 / 收集箱 / 成长服务

> - **本册导航**:总览/帧结构/信封/分发架构/已实现 CMD 总表见 [servers4a21-00-overview.md](servers4a21-00-overview.md)。
> - 仅写 A21(`ServerS4A21\Server\DfoServer`)侧实现,供 DFLegacy 回填参考。路径相对 `DfoServer` 根;行号为整理时点状态。

---

## 1. 商城 / 代币

### CMD 0x0040 (64) `BUY_CERASHOP_ITEM` — 商城购买

- **请求**: 固定头 4B:`body[2]`=商品条数(u8,0 视为 1)、`body[3]`=是否用优惠券(u8)、`body[4]`=支付模式(u8,0=点券/金币、1=装扮兑换券);其后每条 15B 步进,条目内 `+1`=attr u8、`+3`=commodityNo i32;单件购买时条目后接可变包选项:avatarCount u8 + n×(itemId i32 + attr u8),再 selectionCount u8 + n×(packageItemId i32 + groupIndex u16 + selectionIndex u16);勾选优惠券时包尾 6B 固定为 couponItemId i32 + couponSlot i16。解析 `Network/Parsers/CeraShop/CeraShopPurchaseRequest.cs:33-85`(选项 `:87-140`)。
- **应答/推送**:
  - 成功 ACK(每件一条,body 24B):`[0]=1 成功、[1]=0、[2..5]=category=-1(全类别)、[6..9]=commodityNo、[10..23]=0(count=0 无额外项)`。构造 `Network/Builders/CeraShop/CeraShopPurchaseAckBuilder.cs:36-65`。
  - 失败 ACK(22B):`[0]=0、[1]=错误码、[2..5]=-1、[6..9]=-1、[10..21]=0`(`:72-87`;短包会被客户端当索引越界,故补足)。
  - 成功末尾推 NOTI 0x0035 `CERA`:`u8 1 + cera i32 + tokenCera i32 + happyTokenCera i32`(`Network/Builders/CeraUpdateBuilder.cs:5-13`,发送 `Network/Handlers/CeraShopHandler.cs:305-308`)。
  - 金库/仓库扩容:成功 ACK 前先发 CMD 0x0132/0x0198 成功 ACK + ITEM_LIST 整表刷新(`Network/Handlers/CeraShopHandler.cs:239-255`)。
- **行为**:
  - 解析失败/无在线背包租约直接回失败包(`Network/Handlers/CeraShopHandler.cs:66-88`)。
  - 单件非契约商品走快速路径 `CeraShopRuntimePurchaseCommitService.TryPurchase`(纯读预检 NoEffect 拒绝 + 事务内 `TryBuyCeraShopItem`,`Game/Inventory/Services/CeraShopRuntimePurchaseCommitService.cs:34-75`);支付模式 1 仅限时装且换算为装扮兑换券、点券价清零(`Game/Inventory/Services/InventoryCeraShopRuntimeService.cs:154-164`);优惠券仅模式 0 可用,需槽内持有同 ID 可堆叠 coupon(`:166-178`)。
  - 多件逐条购买,失败记录 InsufficientCera 优先上报(`Network/Handlers/CeraShopHandler.cs:139-196`);决斗契约商品先试 `PremiumService.TryBuyDevastkContract` 路径(`:145-157`)。
  - 错误码映射:InsufficientCera→11、NoEffect(档次已达成)→113、其余→4(`:329-342`);完整客户端码表见 `Network/Builders/CeraShop/CeraShopPurchaseAckBuilder.cs:12-28`(10 金币不足/13 LV 不足/20 无法购买/26 月限购/108 兑换券不足/162 优惠券不可用/163 日赠上限等)。
  - 溢出奖励转邮件并推 NOTI 0x0063 `MAILBOX_ALARM`(1)(`Network/Handlers/CeraShopHandler.cs:267-274`);名牌购买触发外观刷新(`:276-281`),技能树扩容栏触发 USERINFO subtype0 重发(`:290-303`)。
- **备注**: 消耗型商品(金库扩容工具)会同步在线背包 ListParam16 容量(`:417-441`);商品目录(金币价/点券价/时限)来自 PVF `Game/Shop/CeraShopProductCatalog.cs:22-25`。

### CMD 0x0041 (65) `GEN_CERATICKET` — 生成点券票根

- **请求**: 不解析 body。
- **应答**: `[0]=u8 1 + dstr("1234"+Unix 秒 10 位+"00000") + u32 123456`。构造内联 `Network/Handlers/CeraShopHandler.cs:48-60`。
- **行为**: 纯防卡包,仅让客户端停止等待;无持久化。
- **备注**: 代码注释指明该包副作用是鼠标消失,移动到变指针处即恢复(`:50-51`)。

### CMD 0x00CF (207) `SHOP_COIN_EVENT` — 商城金币活动(每日复活币)

- **请求**: 不解析 body。
- **应答/推送**: ACK `u8 ok + i32 grantCount`(`Network/Handlers/ShopCoinEventHandler.cs:42-48`);领取成功先推 NOTI 0x00B1 `SHOP_COIN_EVENT` `[1]`(与初始化流 shop_coin_event_flag 对应,`:35`),再 ITEM_LIST 单槽刷新,最后 ACK。
- **行为**: 校验在线租约后 `ReviveCoinService.TryGrantDaily`:身上已有复活币或当日已领则失败(`Game/ReviveCoin/ReviveCoinService.cs:41-80`,SQLite 日重置 flag + 钱包槽写入 1 枚)。失败仅回 ACK(ok=0, count=0)。

### CMD 0x03D0 (976) `CHARGE_RENTPOINT` — 金币购买幸运星

- **请求**: 固定 22B:`@14`=mode i32(须为 1)、`@18`=buyCount i32(1..999)。解析 `Game/Inventory/RentalCatalogCodec.cs:14-25`(`:8-12` 常量:请求 22B、上限 999、单价 100,000 金币/星)。
- **应答/推送**:
  - 成功 ACK 13B:`[0]=1 + mode i32(回显请求值) + resultOrQuantity i32(=buyCount) + totalLuckyStar i32`(`Network/Handlers/LuckyStarClientNotifier.cs:44-68`);失败 ACK 仅 `[0x00]`(`Network/Handlers/LuckyStarHandler.cs:125-131`)。
  - 成功后推 NOTI 0x03C1 `EQUIPMENT_RENTAL_LIST` 整面板(幸运星+在租列表,见 §2 构造),再金币刷新(`Network/Handlers/LuckyStarHandler.cs:114-122`)。
- **行为**: 单事务校验 星数上限 999 → 金币充足(背包虚拟槽计数)→ 扣金币 + `CurrencyService.GrantLuckyStar` 加星(`Network/Handlers/LuckyStarHandler.cs:68-102`)。

## 2. 租赁

### CMD 0x03CF (975) `RENT_EQUIPMENT_ITEM` — 租赁武器

- **请求**: 固定 22B;`@14`=inventoryTemplateId u32(背包模板 ID,非对齐)、`@18`=clientContext u32,前 14B 为未初始化缓冲不可解析。`Game/Inventory/RentalWeaponRequestCodec.cs:10-36`;星价从 `RentalWeaponInventoryMapper` 目录查,非法模板拒绝。
- **应答/推送**:
  - 成功 ACK 5B:`[0]=1 + result u32(0=成功,1=在租过多,2=背包满)`;失败 ACK 单字节 `[0]`(解析失败)。构造 `Network/Builders/RentalWeaponPacketBuilder.cs:8-23`。
  - 成功后推 NOTI 0x03C1 `EQUIPMENT_RENTAL_LIST` 面板:body=`luckyStar u32 + 条目数 u32 + n×(inventoryTemplateId u32 + 绝对到期 Unix 秒 u32)`(仅未过期条目),构造 `Network/Builders/Init/RentalInfoBodyBuilder.cs:33-61`;在线聚合 Equipment 全表 + Main 租赁槽段(`Network/Handlers/RentalInfoPanelNotifier.cs:23-84`)。再 ITEM_LIST 单槽刷新(`Network/Handlers/RentalHandler.cs:118-119`)。
- **行为**: 单事务:`CanRentWeapon`(背包满/非法)→ `CurrencyService.TrySpendLuckyStar`(星不足)→ `TryRentWeapon` 写背包并带 `now+86400s` 到期(`Network/Handlers/RentalHandler.cs:69-119`,时长常量 `RentalWeaponRequestCodec.cs:13`)。
- **备注**: 分发号取类内常量 `RentalHandler.CommandType = CmdPacketTypeA21.RENT_EQUIPMENT_ITEM`(`Network/Handlers/RentalHandler.cs:16`,注册 `Network/Protocol/GameProtocolHandler.cs:996`)。

## 3. 小游戏(智能娃娃机)

### CMD 0x01E6 (486) `CRANE_START_USE` — 娃娃机开局

- **请求**: 固定 2B:machineId u16。校验 `Network/Handlers/CraneMiniGameHandler.cs:36-42`。
- **应答**: 成功 ACK:`u8 1 + machineId u16 + 材料剩余 u32 + 6×catalogIndex u32`(必须恰好 6 个展示位);失败 ACK `[0, 0x04]`。构造 `Network/Builders/CraneMiniGame/CraneMiniGameStartAckBuilder.cs:8-23`。
- **行为**: 按目录权重抽 6 个展示品并消耗材料道具(数量来自 PVF 目录 `Game/CraneMiniGame/CraneMiniGameCatalog.cs:24-28`,扣料 `Game/CraneMiniGame/CraneMiniGameStartService.cs:28-52`);会话结果存 `CraneMiniGameSessionCoordinator`(`Network/Handlers/CraneMiniGameHandler.cs:63`)。

### CMD 0x01E7 (487) `CRANE_PICKUP` — 娃娃机抓取

- **请求**: 固定 6B:displaySlot u16 + itemId i32。校验 `Network/Handlers/CraneMiniGameHandler.cs:79-86`。
- **应答**: 成功 ACK:`u8 1 + itemId u32 + count i16`;失败 ACK `[0, 0x04]`。构造 `Network/Builders/CraneMiniGame/CraneMiniGamePickupAckBuilder.cs:7-18`。
- **行为**: 会话协调器按展示品 PickChance 概率判定中签(`Game/CraneMiniGame/CraneMiniGamePickupService.cs:34-41`);未中直接失败回包;中签提交入包,背包溢出转邮件(标题"智能娃娃机(1期)")并推 NOTI 0x0063(`Network/Handlers/CraneMiniGameHandler.cs:121-157`);普通入包则 ITEM_LIST 单槽刷新(`:158-162`)。

## 4. 活动

### CMD 0x04E6 (1254) `JOUST_INFO` — 竞速赛马信息

- **请求**: 不解析 body。
- **应答/推送**: 活动未开回 5B 关闭 ACK `[1, 6, 0, 0, 0]`(`Network/Builders/Events/JoustPacketBuilder.cs:114-117`);开启则按序推 NOTI:0x04BA `JOUST_BETTING_INFO`(46B:`roundNo u16 + 本人总注 i32 + 8×(horseId u8 + 该马注额 i32)`,`:56-81`)→ 0x04B9 `JOUST_INFO`(90B:`roundNo u16 + 8×(knightIndex u8 + condition u8 + 赔率 f32 + 胜 u16 + 负 u16 + 黑马隐藏 u8)`,`:23-54`)→ 赛跑/复盘阶段追加 0x04BB `JOUST_MATCH_RESULT`(31B:`u16 0 + stageIndex u8 + 14×bracketSlot u16`,`:83-93`)。发送序列 `Network/Handlers/EventJoustHandler.cs:300-338`。
- **行为**: 状态由 `JoustService` 快照驱动;分钟 tick 在阶段切换(Betting/StopBetting/Racing)向全服推 0x04B8 `JOUST_STATE`(`roundNo u16 + phase u8`,`:15-21`)与赛果(`Network/Handlers/EventJoustHandler.cs:56-150`);登录时也推(`:152-162`)。

### CMD 0x04E7 (1255) `JOUST_BETTING` — 赛马下注

- **请求**: 双布局兼容:优先 `@14=horseId u8、@15=slot i16、@19=amount i32`,退回 `@13/@14/@18`;horseId≤11,amount 1..1,000,000。解析 `Network/Parsers/Events/JoustBettingRequestParser.cs:8-48`。
- **应答**: ACK 5B:`[1, 0, 0, 0, 0]` 成功 / `[1, 6, 0, 0, 0]` 失败(`Network/Builders/Events/JoustPacketBuilder.cs:119-122`)。
- **行为**: `JoustService.PlaceBet` 消耗道具并记账(`Network/Handlers/EventJoustHandler.cs:200`);成功后刷新被消耗的 Main 槽并回推快照(`:211-226`)。

### CMD 0x04E8 (1256) `JOUST_MATCH_HISTORY` — 赛马历史战绩

- **请求**: 不解析 body。
- **应答**: ACK 3505B:`u8 1 + i32 0 + 500×(roundNo u16 + 胜者马号 u8 + 赔率 f32)`,不足补 0。构造 `Network/Builders/Events/JoustPacketBuilder.cs:95-112`(服务端载入最近 500 条,`Network/Handlers/EventJoustHandler.cs:228-238`)。

### CMD 0x0279 (633) `GET_PCROOM_TIME_POINT_ITEM` — 网吧在线积分

- **请求**: 2B:`[0]`=selector、`[1]`=index。`0x00,0xFF`=查询;selector 0x01/0x02/0x04/0x08 + 0xFF = 领日奖励段 1-4;selector 0x10 + index≤3 = 领时段奖励。解析 `Network/Parsers/Events/PcRoomTimePointRequestParser.cs:7-54`。
- **应答/推送**: ACK 6B 全 0(`Network/Builders/Events/PcRoomTimePointPacketBuilder.cs:27-34`);状态推 NOTI 0x0232 `PCROOM_TIME_POINT` 17B:`当日在线秒 u32 + 已完成时段数 u32 + 日领掩码 u8 + 保留 u32 + 时段领掩码 u32`(`:10-25`);领取产生邮件时推 NOTI 0x0063(`Network/Handlers/EventPcRoomTimePointHandler.cs:155-162`)。
- **行为**: 会话登录/结束驱动在线计时(`:56-99`);分钟 tick + 日阈值单次定时器去重推送状态(`:184-311`)。

### CMD 0x04E9 (1257) `AT_DAILY_ATTENDANCE` — 随时签到(累计签到奖励)

- **请求**: 不解析 body(body 长度仅记日志)。
- **应答/推送**: ACK 空 body(`Network/Handlers/EventDailyAttendanceAnytimeHandler.cs:120-132`);状态推 NOTI 0x049D `INTEGRATE_EVENT_DATA` 37B:`eventId u32 + u8 0 + 累计签到数 u32 + accumulateState0-2 u32×3 + 今日推荐图清次数 u32 + 3/4/5 三个固定 u32`,构造 `Network/Builders/Events/DailyAttendanceAnytimePacketBuilder.cs:10-26`;领取入邮件则推 NOTI 0x0063(`Network/Handlers/EventDailyAttendanceAnytimeHandler.cs:64-68`)。
- **行为**: `ClaimAccumulateReward` 以账号+角色判定领取状态,重复领取仅跳过并记日志(`:58-77`);登录主动推状态(`:20-40`)。

### CMD 0x0441 (1089) `EVENT_TOTAL_ATTENDANCE_CHECK_THISWEEK` — 全勤签到周核对

- **请求**: 不解析 body。
- **应答/推送**: ACK 4B:`resultCode u32`(0=成功,1=失败),构造 `Network/Builders/Events/TotalAttendancePacketBuilder.cs:51-62`;状态推 NOTI 0x042D `EVENT_TOTAL_ATTENDANCE` 450B,关键字段:`@0x004` 累计周数 u32、`@0x198` 起 20B 周序号 1..N、`@0x1B0` 本周推荐图清次数 u32、`@0x1BC` 当前活动周 u32、`@0x1C0` 本周可核对按钮 u8、`@0x1C1` 加成模式 u8(`:11-43`);发邮件推 NOTI 0x0063(`Network/Handlers/EventTotalAttendanceHandler.cs:63-67`)。
- **行为**: `CheckThisWeek` 每周首次核对发放奖励,失败记 status 日志(`:56-78`);登录推状态(`:19-39`)。

## 5. 彩票

### CMD 0x001B (27) `USE_LOTTERY_ITEM` — 使用彩票/抽奖盒

- **请求**: 4B:phase u16(仅 0/1)+ slotIndex i16(≥0)。解析 `Network/Parsers/Lottery/LotteryItemUseRequest.cs:11-24`。
- **应答/推送**(经 `Network/Handlers/LotteryItemResponseSender.cs` 组装):
  - 原生结果 ACK:金币变体 `u8 1 + srcSlot i16 + i16 0 + itemId i32 0 + gold i32 + 4B 空 + 31B 尾`;普通物品变体 `u8 1 + srcSlot i16 + dstSlot i16 + itemId i32 + displayValue i32 + 耐久 u16 + attr u8 + 增幅类型 u8 + 增幅值 u16 + 31B 尾(尾含 expireTime i32)`;时装变体首字节后直接写 126B 时装条目。构造 `Network/Builders/Lottery/LotteryItemAckBuilder.cs:9-84`。
  - 失败 ACK 13B:`u8 0 + i16 -1 + u16 0 + i32 0 + i32 0`(`:67-76`)。
  - 伴随:进度推 NOTI 0x03D8(204B 全量状态:版本 2 i32 + 当前 itemId i32 + 新奖励序 i32 + 8×24B 记录[itemId i32 + ≤20B 已领掩码],`Network/Builders/IncreaseChanceLotteryPacketBuilder.cs:15-56`,发送 `LotteryItemResponseSender.cs:127-135`);可用品上限推 NOTI 0x021E(`Network/Handlers/LotteryItemHandler.cs:361-372`);奖励广播 NOTI 0x0056 `SERVER_BROADCAST`:`u8 0x02 + u8 0x01 + uid u16 + itemId i32 + 强化 u8`(`Network/Builders/Lottery/LotteryItemNoticeBuilder.cs:5-14`,广播 `LotteryItemResponseSender.cs:287-320`);邮件兜底推 NOTI 0x0063(`:108-114`);另有可能的高级服务刷新与奖励槽 ITEM_LIST(`:77-107`)。
- **行为**:
  - phase=0:先校验源道具未过期(过期则删除源并刷槽,`Network/Handlers/LotteryItemHandler.cs:280-330`),再立即开箱(ConfirmedRegular 计划,`:70-89`)。
  - phase=1:有 pending 则沿用其计划,无 pending 视为"快速直接开"(`:91-107`);开箱流程统一走预约→提交→应答(`:216-278`),失败回错误 ACK。
- **备注**: 双倍奖励、显示值归一等表现策略集中在 `LotteryPresentationPolicy`(`LotteryItemResponseSender.cs:42-57`)。

### CMD 0x0453 (1107) `INCREASE_CHANCE_LOTTERY_RESET` — 概率累积彩票重置

- **请求**: 固定 21B:`@13`=slotIndex i16(≥0)、`@17`=itemTemplateId i32(>0),其余字段不消费。解析 `Network/Parsers/Lottery/IncreaseChanceLotteryResetRequest.cs:11-29`。
- **应答/推送**: ACK 成功 `[1]` / 失败 `[0, errorCode≥1]`(`Network/Builders/IncreaseChanceLotteryPacketBuilder.cs:58-65`);成功后推 NOTI 0x03D8 进度全量 + 金币刷新(槽 0)(`Network/Handlers/LotteryItemHandler.cs:172-175`,发送 `Network/Handlers/LotteryItemResponseSender.cs:127-135,181-184`)。
- **行为**: `TryResetProgress` 校验槽内道具与请求一致后清进度并结算已投入金币(`Network/Handlers/LotteryItemHandler.cs:159-176`)。

### CMD 0x00D9 (217) `OVERFLOW_INFO` — 非 Raid 分支(彩票溢出确认)

- **请求**: 仅接受彩票形状的确认包:恰好 3B 且 `[0x01, 0x1B, 0x00]`(`Network/Handlers/LotteryItemHandler.cs:186-193`)。
- **应答**: ACK 3B:`u8 1 + 回显 body[1] + 回显 body[2]`(`Network/Builders/Lottery/LotteryOverflowConfirmAckBuilder.cs:5-18`)。
- **行为**: 0x00D9 先归 Raid 弹窗关闭处理,未消费才落彩票分支(`Network/Protocol/GameProtocolHandler.cs:427-430`);存在 pending phase0 时补齐该次开箱应答(`Network/Handlers/LotteryItemHandler.cs:118-137`),无 pending 忽略。

## 6. 成长 / 金币

### CMD 0x025B (603) `GET_EXPAND_EXP_GAGE_REWARD` — 成长胶囊领取

- **请求**: 不解析 body。
- **应答/推送**: ACK:成功 `[u8 0 + u32 0 + itemId u32 + count u32]`、失败 `[u8 1]`(注意 0=成功)。构造 `Network/Builders/GrowthCapsulePacketBuilder.cs:5-16`。无论成败都推 NOTI 0x0025 `EXP` 经验通知体(胶囊进度并入 displayExp,`Game/Accounts/GrowthCapsuleSyncService.cs:48-81`)。
- **行为**: 仅满级(`ExpTableProvider.MaxLevel`)角色可领取(`Network/Handlers/GrowthCapsuleHandler.cs:38-44`);`GrowthCapsuleClaimService.Claim` 结算累计经验换道具并分配槽位,成功刷 ITEM_LIST 单槽(`:53-67`);失败也要回推服务端进度防客户端本地清零(`:56-60`)。

### CMD 0x03BA (954) `UPGRADE_CARRY_GOLD` — 金币携带上限升级

- **请求**: 固定 15B,内容不消费。解析 `Network/Parsers/Inventory/GoldLimitUpgradeRequest.cs:7-18`。
- **应答/推送**: 无常规 ACK;推 NOTI 0x039B `UPGRADE_CARRY_GOLD`,body 单字节 upgradeLevel(`Network/Handlers/GoldLimitHandler.cs:39-44`);成功后经 InventoryRefreshSender 发金币刷新(`:46-50`)。
- **行为**: `CharacterGoldLimitRepository.TryUpgrade` 决定扣费与新档位(携带/拍卖行上限),失败不推 NOTI(`:31-36`)。

## 7. 邮箱(mailbox 组,注册 `Network/Protocol/GameProtocolHandler.cs:936-943`)

### CMD 0x005E (94) `MAILBOX_SEND` — 发送邮件(单附件)

### CMD 0x013B (315) `MULTI_MAILBOX_SEND` — 发送邮件(多附件,A21 新增)

- **请求**(同一解析器 `Network/Handlers/MailboxHandler.cs:1428-1529`,handler `:165-179`):
  - 共同前段:`dstr 收件人名 + gold i32`。
  - 0x005E:`u8 itemType + u16 slot + itemId i32 + itemCount i32`(1 条)+ `dstr 正文`。
  - 0x013B:`attachmentCount u16`(≤10)后跟 N 条记录——**首条省略 itemType**,第 2 条起每条前置 `u8 itemType`,再 `u16 slot + itemId i32 + itemCount i32`;最后 `dstr 正文`。
  - itemType→背包表映射:1=时装、3/7=宠物、0/2/其他=主背包(`:306-320`)。
- **应答/推送**: 成功 ACK `[u8 1]`(`:528-535`),收件人在线推 NOTI 0x0063 `MAILBOX_ALARM`(u16 计数,会话内合并去重,`:574-610`);失败 ACK `[u8 0, 错误码]` 并**回滚刷新附件槽 + 装备锁列表**(`:276-304`)。
- **行为**(校验与计价顺序,`Game/Mailbox/MailboxRepository.cs:1704-1849`):
  1. 收件人按名查库,已删除→24、不存在→3(`Network/Handlers/MailboxHandler.cs:191-200`);金币<0、附件>10、自己发给自己(7)逐项拒绝(`:202-231`)。
  2. 附件槽实读快照(堆叠物按请求数量,装备强制 1,`Game/Mailbox/MailboxRepository.cs:2160-2214`);ID 不符→17、锁定→214。
  3. 幂等重放:同连接同 seq/checksum 的重试按请求哈希返回原已提交消息(`:1741`,`Network/Handlers/MailboxHandler.cs:250` 幂等键)。
  4. **手续费** `CalculateFeeGold`:附件数>0 时 `附件数×1000`,无附件固定 100;再加 `min(10000, gold×5%)`(`Game/Mailbox/MailboxRepository.cs:2102-2113`);总扣费=随信金币+手续费,余额不足→10(`:1744-1749`)。
  5. 策略链:PVF 交易次数递减(`:1784-1794`)→ 黑名单→90(`:1796-1798`)→ 收件人金币超过其等级携带上限→14、发件人日限额 `10000×level²` 超→70(`:1831-1858`)→ 空内容→22。
  6. 提交:扣金币→消耗附件→落库→插入消息(**15 天过期**,`expireAtUtc=now+15d`,`:1808-1814`)+ 双收件记录(收件人 folder=0、发件人抄送 folder=1)+ 附件行(`:1816-1836`)。
- **备注**: 玩家邮件发件人显示真实角色;系统邮件(mailType≠0)显示 `DNFadmin`/cid 0(`Network/Handlers/MailboxHandler.cs:31,1389-1406`)。

### CMD 0x005F (95) `MAILBOX_EXTRACT_ITEM` — 领取(附件/金币)

- **请求**: 4B:claimObjectId i32(`Network/Handlers/MailboxHandler.cs:322-331`)。**带附件邮件的领取对象是 `0x40000000 + attachment_id`,纯金币/正文邮件用 messageId**(`Game/Mailbox/MailboxRepository.cs:22`,编码于列表 summary 首字段 `Network/Handlers/MailboxHandler.cs:959-968`)。
- **应答**: 成功 ACK `u8 1 + claimObjectId i32`(`:537-551`)。
- **行为**:
  - SQLite CAS:附件行 `claimed_flag 0→2(预留)→1(已领)`,金币行 `received_gold_flag 0→2→1`(`Game/Mailbox/MailboxRepository.cs:1560-1653`),失败即并发冲突回滚。
  - 成功序:ACK → ITEM_LIST 分表槽刷新(领到金币加刷槽 0)→ 若邮件整体领完发 CMD 0x0086 形状 ACK(messageId+action=0)充当"移除"→ 该页删满 20 封时整页 NOTI 0x0061 刷新(`Network/Handlers/MailboxHandler.cs:357-369`)。
  - 失败时部分已发刷新先行,再回错误码(`:343-355`)。
- **错误码**(0x005F 专用映射,`:671-698,765-774`):4 附件放不进(客户端再细分 2057 空间/重量或 42077 携带上限)、10 领金币超携带上限、19 交易受限、21 无此邮件、60 个人商店中、219 交易中、2 服务忙。

### CMD 0x0060 (96) `MAILBOX_OPEN` — 打开邮箱

- **请求**: 不解析 body。
- **应答**: ACK `u8 1 + notLoaded u16`(`Network/Handlers/MailboxHandler.cs:800-806`,发送 `:112-126`)。
- **行为**: 打开仅清会话级新邮件告警状态(`:116`);随后推完整 NOTI 0x0061 `MAILBOX_MAIL_LIST`(可能多包):
  - 包布局(`:823-872`):`summaryCount u8 + isFirstLoad u8 + n×summary + notLoaded u16 + detailCount u16 + n×detail`。
  - summary(附件预览行,`:952-1078`):`claimObjectId i32 + 发件人cid i32 + dstr(≤30) 名字 + gold i32 + itemId i32 + 有无附件 u8 + instance i32(堆叠=数量) + 耐久 u16 + attr u8 + 附魔卡/数量 i32 + 强化 u8 + 增幅类型 u8 + 增幅值 u16 + (宠物: extra i32 + u8) + expireTime i32 + 徽章数组(u8 count + i32×) + rune u16 + 随机选项段(u8 count + n×4B + state 2B [+changed 6B]) + v47/v25/v48 u8×3 + tailWord u16 + tail u8×5 + 时装 blob(u32 len + 宝珠 18B + u32 4 + 颜色 4B;非时装两段 0)+ u8 0 +(时装再两段 u32 0)+ 剩余秒 i32 + summaryKey i32(messageId,纯正文行填 0)+ u8 0`。
  - detail(正文行,`:1356-1371`):`messageId i32 + 发件人cid i32 + dstr(≤30) 名字 + dstr(≤512) 正文(标题\n正文)+ createdAt u32 + letterStat u16 + u8 0`。
  - 分页:每页 20 条 + 收藏箱(saved)10 条(`:25-26`;SQL `Game/Mailbox/MailboxRepository.cs:179-317`),`notLoaded = max(0, total-loaded)`(`Game/Mailbox/MailboxModels.cs:198`);summary 行与正文行**必须分包发送**(客户端把新行插到 0 号位,混包会打乱顺序,分包只在两类交界切,`Network/Handlers/MailboxHandler.cs:874-927`)。

### CMD 0x0086 (134) `CHANGE_LETTER_STAT` — 邮件状态变更

- **请求**: 6B:messageId i32 + action u16(`Network/Handlers/MailboxHandler.cs:442-453`)。
- **应答**: ACK `u8 1 + messageId i32 + action u16`(`:553-560`)。
- **行为**:
  - action=2 标记已读;action=3 存入收藏箱(超 10 封→22);其他非 0→3(`:455-497`)。
  - action=0 删除:**组合动作** ACK → NOTI 0x0062 `MAILBOX_REMOVE_MAIL`(`u32 count + n×messageId i32`,`:808-821`)→ 会话内同一页累计删满 20 封则再推整页 NOTI 0x0061 刷新(`:499-520`,页计数 `:645-662`)。
  - 邮件已不存在(MailNotFound)也回成功 ACK + 补 0x0062/整页刷新,保证客户端状态收敛(`:502-509`)。
  - 过期清理:服务端每分钟扫描过期邮件,批量(≤200)对在线收件人推 0x0062 + 满页刷新(`:55-110`)。

### CMD 0x0144 (324) `QUERY_CHARAC_INFO_MAILBOX` — 按名查询角色(写信收件人)

- **请求**: `dstr 角色名`(u32 长度+GBK,≤20 字节缓冲)。解析 `Network/Handlers/MailboxHandler.cs:1416-1426`。
- **应答**: 成功 ACK `u8 1 + 名字长度 i32 + 名字字节 + level u16 + job u8 + growType u8 + u8 0`(客户端 sub_CD2A00 无条件再读三字节,`:782-798`)。
- **错误码**: 7=发给自己、21=名字空/角色不存在或已删(`:128-163`,常量 `:35`)。
- **备注**: 失败 ACK 走通用 `[0, code]`(`:528-535,776-780`)。

## 8. 收集箱(注册 `Network/Protocol/GameProtocolHandler.cs:948-950`)

### CMD 0x03E5 (997) `SELECT_COLLECTBOX` — 选择收集箱

- **请求**: body 末字节 = boxIndex(`Network/Handlers/CollectionBoxHandler.cs:19`)。
- **应答**: ACK 2B:`选中 boxIndex u8 ×2`(`:24-28`)。无效索引回落 0。

### CMD 0x03E6 (998) `ADD_COLLECTBOX_ITEM` — 放入收集箱

- **请求**: 8B:boxIndex u16 + 源槽 u16 + itemId u32(`:37-41`)。
- **应答/推送**: 成功先发 CMD 0x0012 `DELETE_ITEM` ACK(原背包槽删除,`:69-72`),再推 NOTI 0x03EB `COLLECT_BOX`;最后本协议 ACK `u8 结果(1=成功)` / 失败 `u8 0 + u8 错误码(默认 0x12)`(`:153-164`)。
- **行为**: `CollectBoxRuntimeService.TryPutItem` 在线内存态转移 + 事务提交;提交失败回滚为 0x12(`:43-65`);NOTI 0x03EB body:`boxIndex u8 + u8 1 + 剩余秒 u32 + 状态 u8(1=无期限;有期限时 0)+ 数量 u8 + n×itemId u32`,过期写 0xFFFFFFFF(`Network/Builders/Init/CollectionBoxBodyBuilder.cs:67-101`)。

### CMD 0x03E7 (999) `REMOVE_COLLECTBOX_ITEM` — 取出收集箱

- **请求**: ≥5B:`[0]` 未消费,itemId u32 @1(`:91-93`)。
- **应答/推送**: 成功 ITEM_LIST 单槽刷新 + NOTI 0x03EB(该箱全量),最后 ACK(格式同上,`:117-132`)。
- **行为**: `TryTakeItem` 按 itemId 定位槽并回填背包,提交失败同 0x12。
- **备注**: `EXTEND_COLLECTBOX_EXPIRY_DATE` 0x03E8 未注册处理;进度槽位经 `CollectBoxProgressRepository` 持久化并在登录 init 复用同一 builder(`Network/Builders/Init/CollectionBoxBodyBuilder.cs:20-49`)。