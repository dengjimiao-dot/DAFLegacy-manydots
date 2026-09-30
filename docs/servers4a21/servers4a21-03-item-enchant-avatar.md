# ServerS4A21 已实现协议参考 — 装备强化/合成/分解/随机选项/徽章/时装/卡片/称号/锁

> - **本册导航**:总览/帧结构/信封/分发架构/已实现 CMD 总表见 [servers4a21-00-overview.md](servers4a21-00-overview.md)。
> - 仅写 A21(`ServerS4A21\Server\DfoServer`)侧实现,供 DAFLegacy 回填参考。路径相对 `DfoServer` 根;行号为整理时点状态。

## 域综述

- 本域全部业务在 `Network/Handlers/InventoryHandler.*.cs` **分部类**(`sealed partial class InventoryHandler`)中,按功能拆 20+ 文件;纯网络层只做 解析→租约校验→服务调用→ACK 构造→补刷新,规则全部下沉到 `Game/Inventory/Services/*` 与 `Game/ItemUpgrade/*`。
- **公共校验链**:先 handler 内/Parser 静态解析布局(长度硬校验),再 `ResolveOwner`+`TryGetOwnedInventoryLease` 取本会话持有的背包租约(`InventoryHandler.Move.cs:436-443`),绝大多数写操作经 `OnlineInventoryMutationCommitCoordinator.TryCommit`(事务内执行内存变更+SQLite 持久化,`Game/Inventory/Services/OnlineInventoryMutationCommitCoordinator.cs`)。
- **结果包模式**:ACK 首字节 0x01/0x00 表成败,失败几乎统一 `[0, errorCode u8]`(`Network/Builders/CommonPacketBodyBuilder.cs:12-15`);成功 ACK 只带"客户端无法自算"的最小字段,槽位内容权威值随后用 NOTI 0x000E 定点刷新(部分协议刻意先刷新后 ACK 或先发空条目再发终态,见 0x010F/0x0197)。
- **概率统一套路**:PVF 配置表→权重(万分比或十万分比)→`ServerRandom.Next(denominator)` 掷点→命中/失败分支;失败补偿(破坏奖励/保护券)在同一事务内计划+发放。
- 广播类强化成功走 NOTI 0x0056 SERVER_BROADCAST(`InventoryHandler.cs:96-119`),锁定类走 NOTI 0x00FB/0x00FC。
- 本域**无独立 parser 文件**的协议(0x0019/0x0191/0x01B6/0x02CA/0x02CB/0x00CE 等)直接在 handler 内实读布局,下文逐条标注。

---

## 一、合成 / 分解 / 净化 / 增幅投资

### CMD 0x0019 (25) `COMPOUND_ITEM` — 设计图合成
- **请求**: `[sourceValue i32@0][isItemId u8@4][count u16@5]`(≥7B;sourceValue 按 isItemId 解释为材料 itemId 或 recipeId)。handler 内解析 `Network/Handlers/InventoryHandler.CompoundItem.cs:79-96`。
- **应答**: 成功 `[1][删除数 u8]×{kind u8, slot i16, 剩余/数量 i32}[产出数 u8]×{kind u8, slot i16, itemId i32, count i32, attr u8, dur u16, seal u8, amplifyValue u16, amplifyType u8, marker i32, genuineUpgrade u8, emancipateLevel u8, tradeRestriction u8, tail u16+3B, remainUse u8, sortLock u8, equipLockId u8}[12B 0]`(`Network/Builders/Inventory/CompoundItemAckBuilder.cs:12-72`);失败 `[0, code]`(`:25-28`,默认 17)。
- **行为**: 提交 `InventoryRecipeSeparateUpgradeCommitService.TryCommitCompoundItem`(`InventoryHandler.CompoundItem.cs:36-40`);替换型产出先发空 0x000E 再发终态(`:61-66`);扣金币后补金币刷新(`:68-69`);kind 字节规则(非堆叠剩余 0 → 1,否则 listType)`CompoundItemAckBuilder.cs:74-102`。
- **备注**: 日志带 PVF recipe 路径与 recipeType(`InventoryHandler.CompoundItem.cs:71-76`),可作 PVF 字段排查入口。

### CMD 0x001A (26) `DISJOINT_ITEM` — 系统分解
- **请求**: `[targetSlot i16@0][itemSpace u8@2][disjointSlot i16@3][ctx i32@5(≥9B 才读)]`。`Network/Parsers/Inventory/DisjointItemRequestParser.cs:14-23`。
- **应答**: 成功 `[1][targetSlot i16][itemSpace u8][数量 u8]×{slot i16, itemId i32, count i32}`(`Network/Builders/Inventory/DisjointItemAckBuilder.cs:8-25`);失败 `[0, code]`(`:27-33`)。错误码:0x02 invalid request / invalid target / inv full 等(枚举在 `Game/Inventory/DisjointModels.cs`)。
- **行为**: 先查材料奖励空位不足直接拒(`InventoryHandler.Disjoint.cs:36-50`,`Game/Inventory/Services/InventorySpaceCheckService.cs`);产物由 `Game/Inventory/DisjointResultCalculator.cs:13-24` 按 PVF system disjoint 配置算(无色 cubes + additional/expand 结果);ACK 自带产物行,客户端不依赖补发 0x000E(`Disjoint.cs:71` 注释);成功后同步寻物任务进度(`:72-78`)。

### CMD 0x00CA (202) `DISJOINT_AVATAR` — 时装分解
- **请求**: `[slot i16@0]`,JP 客户端可追加 `[expectedItemId i32@2]`(≥6B 才读)。`Network/Parsers/Inventory/AvatarDisjointRequestParser.cs:14-24`。
- **应答**: 成功 `[1][slot i16][数量 u16]×{slot i16, itemId i32, count i32}`(`Network/Builders/Inventory/AvatarDisjointAckBuilder.cs:9-23`);失败 `[0, code]`(`:25-31`)。
- **行为**: 徽章槽空位检查(`InventoryHandler.AvatarDisjoint.cs:34-48`);提交 `InventoryDisjointCommitService.TryCommitAvatar`(`:50-54`);产物按品级查 `Game/Inventory/AvatarDisjointConfigProvider.cs`(`Game/Inventory/Services/InventoryAvatarDisjointService.cs:95`);ACK 后补时装槽 + 主背包产物槽刷新(`AvatarDisjoint.cs:70-75`)。

### CMD 0x00CC (204) `PURIFY_ITEM` — 净化(异次元气息去除)
- **请求**: `[targetSlot i16@0][targetId i32@2][matSlot i16@6][matId i32@8]` 恰 12B。`Network/Parsers/Inventory/PurifyItemRequestParser.cs:11-22`。
- **应答**: 成功 `[1][matSlot i16][matRemain i32][targetSlot i16][amplifyType u8][amplifyValue u16]`(`Network/Builders/Inventory/PurifyItemAckBuilder.cs:7-17`);失败 **4 字节全 0**(客户端只看首个 dword,不消费错误码,`PurifyItemAckBuilder.cs:19-23`)。
- **行为**: `InventoryEquipmentAmplifyOptionCommitService.TryCommitPurify`(`Game/Inventory/Services/InventoryEquipmentAmplifyOptionCommitService.cs:5-33`)→ `InventoryEquipmentMutationService.TryPurifyItem`(`Game/Inventory/Services/InventoryEquipmentMutationService.cs:602`);成功后对 target/material 两槽补 0x000E(`InventoryHandler.Purify.cs:63`)。

### CMD 0x00CD (205) `INVEST_ITEM_AMPLIFY_OPTION` — 增幅/扭转/纯金投资
- **请求**: `[action u8@0][targetSlot i16@1][targetId i32@3][matSlot i16@7][matId i32@9][selectedOption u8@13]` 恰 14B。action:0=invest/1=twist/2=pure gold(`Network/Parsers/Inventory/InvestItemAmplifyOptionRequestParser.cs:14-29`;枚举 `Game/Inventory/InventoryModels.cs:593-598`)。
- **应答**: 成功 `[1][action u8][matSlot i16][matRemain i32][targetSlot i16][amplifyType u8][amplifyValue u16][amplifyLevel u8(仅 pure gold)]`(`Network/Builders/Inventory/InvestItemAmplifyOptionAckBuilder.cs:8-20`);失败 `[0, code]`(`:23-27`)。
- **行为**: `TryCommitInvest`(`InventoryEquipmentAmplifyOptionCommitService.cs:35-58`)→ `TryInvestItemAmplifyOption`(`InventoryEquipmentMutationService.cs:698`);刷新走 `SendSortItemLockSlotRefresh`(带排序锁状态,`InventoryHandler.InvestAmplifyOption.cs:63`;`InventoryRefreshSender.cs:305-327`)。

## 二、强化 / 品级 / 编年史

### CMD 0x0050 (80) `UPGRADE_ITEM` — 强化/增幅(凯丽/券/便携)
- **请求**: `[method u16@0][targetSlot i16@2][targetId i32@4][matSlot i16@8][ticketSlot i16@10][nameLen i32@12][name GBK@16]`(≥16B;method 0=强化/1=增幅/2=高级强化,`Network/Parsers/Inventory/ItemUpgradeRequest.cs:23-43`,枚举 `Game/ItemUpgrade/ItemUpgradeModels.cs:14-19`)。
- **应答**: 成功 `[1][method u8][matSlot i16][matRemain i32][ticketSlot i16][0][oldLevel u8][resultCode u8][newLevel u8][0][targetSlot i16][ticketSlot i16]`,resultCode==3(破坏)追加 `[奖励数 u8]×{slot i16, itemId i32, count i32}`(`Network/Builders/Inventory/ItemUpgradeAckBuilder.cs:8-39`);失败 `[0, code]`(`:41-47`)。
- **行为**: 材料奖励空位预检(`InventoryHandler.Equipment.cs:218-236`);权重表由 PVF 券/柜台配置解析(`Game/ItemUpgrade/ItemUpgradeModels.cs:128-314`),**最终成功率 = (基础+附加+称号加成) × (100000+ bonuses)/100000,券场景直接用基础值**(`Game/Inventory/Services/InventoryItemUpgradeService.cs:644-664`),掷点 `ServerRandom.Next(100000)`(`:165`,WeightScale=100000 `:11`);强化上限 clamp 0..31,penaltyType==3 时无保护券即破坏并发破坏补偿(`:171-199`); resultCode 成功=0/失败=1..3(`:198`);金币不足单独错误(`:142-147`);达标等级广播 NOTI 0x0056(`InventoryHandler.Equipment.cs:270-286` → `Network/Builders/Inventory/ItemUpgradeNoticeBuilder.cs:7-23`,body `[1][success u8][uid u16][itemId i32][level u8]+RandomOption 动态块`)。

### CMD 0x01B7 (439) `UPGRADE_ITEM_SEPARATE` — 分离强化(券确认)
- **请求**: `[listType u8@0(Equipment/Main)][targetSlot i16@1][targetId i32@3][matSlot i16@7][…nameLen i32@9][name][variant u8 末字节≤2]`,硬校验 `13+nameLen+1==len`(≥15B)。`Network/Parsers/Inventory/SeparateUpgradeRequest.cs:20-45`。
- **应答**: 成功 `[1][matSlot i16][matRemain i32][oldLevel u8][失败标志 u8(0 成/1 败)][newLevel u8][listType u8][targetSlot i16]`(`Network/Builders/Inventory/SeparateUpgradeAckBuilder.cs:7-19`);失败 `[0, code]`(`:21-22`)。
- **行为**: 概率表 `Game/ItemUpgrade/SeparateUpgradeTableProvider.cs`,成功率按 /10000 记日志(`InventoryHandler.SeparateUpgrade.cs:87`);提交 `InventoryRecipeSeparateUpgradeCommitService.TryCommitSeparateUpgrade`(`:45-51`);装备位成功后补 NOTI 2 外观刷新(`:76-77`);广播 0x0056 变体,首字节 0x0E(`Network/Builders/Inventory/SeparateUpgradeNoticeBuilder.cs:10-17`)。

### CMD 0x0051 (81) `RESET_ITEM_ATTR` — 品级调整箱 / 蜜蜡重封(双路由)
- **请求**: `[targetSlot i16@0][targetId i32@2][matSlot i16@6]` 恰 8B。`Network/Parsers/Inventory/ResetItemQualityRequestParser.cs:10-24`;**材料 itemId==14(蜜蜡)时整包改走蜜蜡路由**(`InventoryHandler.ResetItemQuality.cs:12,17-33`)。
- **应答(品级)**: 成功 `[1][targetId i32][listType u8=Main][targetSlot i32]` 10B(`Network/Builders/Inventory/ResetItemQualityAckBuilder.cs:14-25`);失败 `[0, code]`(`:27-31`)。**应答(蜜蜡)**: `[targetSlot i32][targetItemId i32][resultCode i32]`(`ResetItemQuality.cs:140-147`)。
- **行为**: 品级重掷 `TryResetItemQuality`:目标须装备、锁定即拒、材料须可堆叠非装备/时装/宠物,是否品级调整箱交 PVF policy 裁决(`InventoryEquipmentMutationService.cs:1708-1748`);蜜蜡 `TryWaxReseal`:重封次数 <7、费用=品级+等级+次数函数、SealFlag 置 1(`:1827-1871`);两路由均事务提交后补 target/material 刷新(`ResetItemQuality.cs:84-87,136-137`)。

### CMD 0x010F (271) `UPGRADE_CHRONICLE` — 编年史成长 / 装备解放(双分支)
- **请求**: `[ticketSlot i16@0][ticketId i32@2][targetSlot i16@6][targetId i32@8][matSlot i16@13][matId i32@15]` 恰 19B。`Network/Parsers/Inventory/ChronicleGrowthRequest.cs:10-32`。
- **应答**: 成功 `[1][成长成败 u8][消耗数 u8]×{listType u8, slot i16, 消耗 i32}`(`Network/Builders/Inventory/ChronicleGrowthAckBuilder.cs:8-23`);失败 `[0, code]`(`:25`)。
- **行为**: **解放分支**——券 `[emancipate]` 类型与目标装备匹配时改走 `InventoryEmancipateService.TryConvert`(`InventoryHandler.ChronicleGrowth.cs:35-48`,分支判定 `:83-89`);成长分支成功率按当前等级查 PVF emancipate 概率表,掷 `ServerRandom.Next(100000)`(`Game/Inventory/Services/ChronicleGrowthService.cs:100,219-233`);**刷新顺序敏感**:解放先发空 0x000E 作废旧条目,且 0x000E 必须在 ACK 之后发,否则客户端缓存残留(`ChronicleGrowth.cs:68-78` 注释)。

### CMD 0x0172 (370) `ENCHANT_3RD_CHRONICLE_ITEM` — 编年史精炼(第 3 期附魔)
- **请求**: `[matSlot i16@0][matId i32@2][pad u8@6(须 0)][targetSlot i16@7][targetId i32@9][optionNo u8@13]` 恰 14B。`Network/Parsers/Inventory/ChronicleRefineRequest.cs:20-41`。
- **应答**(响应用请求 opcode;0x0173 是旧 70 表应答号,`InventoryHandler.Equipment.cs:16-18`): 成功 `[1][matSlot i16][matRemain i16][成败 u8]`,失败分支追加 `[0 u8][targetSlot i16][奖励数 u8]×{slot i16, itemId i32, count i32}`(`Network/Builders/Inventory/ChronicleRefineAckBuilder.cs:7-32`);失败 `[0, code]`(`:34-37`)。
- **行为**: 概率按 job/growType/装备 type/optionNo 查 PVF ThreeChronicleEnchant checks,掷 `ServerRandom.Next(101)`(`Game/Inventory/Services/ChronicleRefineService.cs:17-19,133-137`);**失败销毁目标并发放失败奖励**(`:200-216`);刷新 target/mat + 奖励槽(`InventoryHandler.Equipment.cs:56-63`)。

## 三、附魔 / 守护珠

### CMD 0x0110 (272) `ENCHANT_BY_BEAD` — 宝珠附魔
- **请求**: `[beadSpace u8@0][beadSlot i16@1][targetSpace u8@3][targetSlot i16@4]` 6B。`Network/Parsers/Inventory/EnchantByBeadRequest.cs:23-32`。
- **应答**: 成功 `[1][targetSpace u8][targetSlot i16]`(`Network/Builders/Inventory/EnchantByBeadAckBuilder.cs:8-15`);失败 `[0, code]`(`:17-23`)。
- **行为**: 宠物装备分支单独处理(`InventoryHandler.Equipment.cs:80-84,166-196`);提交 `InventoryEquipmentAugmentationCommitService.TryCommitEnchant`(`Game/Inventory/Services/InventoryEquipmentAugmentationCommitService.cs`);**同空间先批量刷新、异空间分别刷新,且刷新先于 ACK**(`:107-114`)。

### CMD 0x033A (826) `USE_GEM` — 守护珠镶嵌(勋章)
- **请求**: 恰 11B `[medalId u32@0][matSlot u16@4][gemId u32@6][socket u8@10]`,socket<`ItemCore.GuardianGemSlotCount`。`Network/Parsers/Inventory/UseGuardianGemRequest.cs:16-45`。
- **应答**: 成功 `[1]`、失败 `[0, code]`,handler 内联构造(`InventoryHandler.Equipment.cs:124,155,159`)。
- **行为**: `TryCommitGuardianGem`(`InventoryEquipmentAugmentationCommitService.cs:79`);覆盖同孔旧珠(日志记 PreviousGuardianGemItemId,`Equipment.cs:163`);刷新 GuildMedal + Equipment 两槽(`:160-161`)。

## 四、随机选项(魔法封印)

### CMD 0x0191 (401) `UNSEAL_RANDOM_OPTION` — 解封
- **请求**: `[slot i16@0][scrollSlot i16@2]`(≥4B;scrollSlot 不校验,服务端固定传 0)。handler 内解析(`InventoryHandler.RandomOption.cs:10-20`)。
- **应答**: `[1]` / `[0]`(无错误码,`RandomOption.cs:54,16,44`)。
- **行为**: `TryUnsealRandomOption`:目标须主背包装备→`RandomOptionResolver.TryRollOptions` 重掷 3 条→扣 `ResolveBreakSealGoldCost` 金币(`InventoryEquipmentMutationService.cs:801-840`);非穿戴才补 0x000E,金币变动补 NOTI(`RandomOption.cs:48-52`)。

### CMD 0x0197 (407) `REGENERATION_RANDOM_OPTION` — 装备合成(再生成)
- **请求**: `[srcSlot i16@0][mode u16@2][part u16@4]`(≥6B)。handler 内解析(`InventoryHandler.EquipmentRegeneration.cs:23-28`)。
- **应答**: 成功 `[1][resultSlot u16]`(`:87-90`);失败 `[0, code]`(缺参 17,`:19,62`)。
- **行为**: 候选池/权重来自 `Game/Inventory/EquipmentRegenerationCandidateCatalog.cs` + ConfigProvider(PVF),由 `InventoryEquipmentRegenerationCommitService.TryCommit` 消耗材料产出新装备;**结果落原槽时先发空条目作废客户端模板缓存再发终态**(`:74-83` 注释);ACK 中的 resultSlot 供客户端立即定位弹窗(`:86-90`)。

### CMD 0x01B6 (438) `CHANGE_RANDOM_OPTION` — 变更随机选项
- **请求**: `[slot i16@0][materialSlot i16@2][optionIndex u8@4]`(≥5B)。handler 内解析(`RandomOption.cs:59-70`)。
- **应答**: `[1]` / `[0]`(`:105,65,95`)。
- **行为**: `TryChangeRandomOption`:校验装备在主背包→按 optionIndex 替换→扣 `ResolveOptionModificationGoldCost`(`InventoryEquipmentMutationService.cs:842-880`,扣金币 `:1608-1618`);刷新策略同 0x0191。

## 五、怪物卡片

### CMD 0x019E (414) `MONSTERCARD_BIND` — 卡片合成
- **请求**: 恰 6B `[binderSlot i16@0][firstSlot i16@2][secondSlot i16@4]`。handler 内解析(`InventoryHandler.MonsterCardBind.cs:12-20`)。
- **应答**: 成功恒 19B `[1][binder u16][first u16][second u16][1][grantSlot u16][resultItemId i32][1 i32][0]`(`Network/Builders/MonsterCardBindAckBuilder.cs:7-22`);失败 `[0, code]`(0x04 背包满/0x13 通用,`MonsterCardBind.cs:41-49`)。
- **行为**: 成功率=PVF 合成器 `MonsterCardBind` 值×首/次稀有度对,掷 `Next(ProbabilityDenominator)`(`Game/Inventory/MonsterCardBindService.cs:54-58`);产物槽随机分配,刷新 binder/first/second/grant 4 槽(`MonsterCardBind.cs:53-54`)。

### CMD 0x025C (604) `UPGRADE_CARD` — 卡片升级
- **请求**: 恰 7B `[listType u8@0][targetSlot i16@1][materialCount i16@3][materialSlot i16@5]`。handler 内解析(`InventoryHandler.MonsterCardUpgrade.cs:21-24`)。
- **应答**: 成功恒 7B `[1][成败 u8][targetItemId i32][upgradeCount u8]`(`Network/Builders/MonsterCardUpgradeAckBuilder.cs:7-17`);失败 `[0, code]`(0x0A 金币不足/0x04 满/0x13,`MonsterCardUpgrade.cs:46-55`)。
- **行为**: 概率=PVF `card upgrade` 常数 / 100000,`card upgrade cost` 为金币费(`Game/Inventory/MonsterCardUpgradeService.cs:11,20-29,47`);成功率 100% 直接成功(`:143-144`);成功后**全主背包刷新**(`MonsterCardUpgrade.cs:65`)。

## 六、时装 / 徽章 / 称号

### CMD 0x0063 (99) `COMPOUND_AVATAR` — 时装合成(2 件)
- **请求**: ≥22B `[consumeSlot i16@0][slot1 i16@2][pad 4B@4][slot2 i16@8][pad 4B@10][reqItemId i32@14][abilityNo u16@18]`。handler 内解析(`InventoryHandler.Package.cs:1934-1940,2113-2122`)。
- **应答**: 成功 `[1][03][01][01][slot1 i16][1 i32][01][slot2 i16][1 i32][00][consumeSlot i16][1 i32]` + 2×结果块 `{slot i16, itemId i32, 0 i32, abilityNo u16, 30 i32, 30B0, 4 i32, 4B0}`(`Package.cs:1971-1993`);失败 `[0][0x16][0]`(带尾字节,`:1935-1943,1961-1965`)。
- **行为**: 概率 `Game/Inventory/CompoundAvatarProbabilityService.cs:38-100`:普通+普通=rareRate、高级+高级=rareRate+upperRareRate、混合取中点;黄金合成器(itemId 899)+200、钻石(10008013)+400 万分比;掷 `Next(10000)` 命中稀有池加权抽 1 件**额外**稀有时装,请求的目标件必给(`:97-101`);材料 21 普通/899 黄金/10008013 钻石(`:15-17`)。

### CMD 0x0447 (1095) `BIND_PLUS` — 8 件高级装扮+合成器合成稀有装扮
- **请求**: ≥72B,关键偏移:`[consumeStackableSlot u8@13][reqItemId i32@16][option u16@20]` + 8×`{slot i16, itemId i32}@24`。handler 内解析(`Package.cs:2017-2033`)。
- **应答**: 成功 `[1][01 00 03 00][01 00 00 00][newSlot i16][newItemId i32][option u16][1 i16][8×slot i16][24B 0]`(`Package.cs:2078-2090`);槽位重复即 `[0,0x16]`(`:2035-2044`)。
- **行为**: `Game/Inventory/AbsoluteBindCubeService.cs:40-56` 校验合成器 `[absolute bind cube]` 与职业 8 部位池,请求 itemId 必须在池内(`Package.cs:2049-2062`);提交 `TryCommitSet`,成功仅补合成器消耗槽刷新(`:2092`)。

### CMD 0x0100 (256) `COMPOUND_EMBLEM` — 徽章合成
- **请求**: `[count u8@0(2..5)]` + count×`{itemId i32, slot i16}`(长度恒 `1+count*6`)。`Network/Parsers/Inventory/EmblemCompoundRequestParser.cs:14-36`。
- **应答**: 成功 10B `[1][01][rewardItemId i32][grantedCount i32]`(槽位变化用 0x000E,`Network/Builders/Inventory/EmblemCompoundAckBuilder.cs:9-20`);失败 `[0, code]`(`:22-28`)。
- **行为**: 输入须为可堆叠时装徽章、同槽数量足额(`Game/Inventory/Services/InventoryEmblemCompoundService.cs:15-48`);**合成品级=输入最低 grade**,`EmblemCompoundConfigProvider.TryRollReward(grade, 输入数)` 定产出(`:50-56`);刷新全部变更槽(`InventoryHandler.EmblemCompound.cs:50`)。

### CMD 0x039F (927) `ADD_EQUIPMENT_EFFECT` — 武器特效符文
- **请求**: ≥21B,容错解析:slot 优先取 `i32@18`,不合理回落 `i32@8`;targetSlot=`i32@14`;targetListType=`u8@12`(限 Main/PersonalCargo/Equipment)。`Game/Inventory/EquipmentEffectRuneModels.cs:36-72`。
- **应答**: 成功**回显整包请求体**: `[1]+rawBody`(`InventoryHandler.Package.cs:745-752`);失败 `[0]`(`:689`)或 `[0,0x04]`(`:694-696`)。
- **行为**: `InventoryEquipmentMutationService.TryUseEquipmentEffectRune` 校验符文道具并按 PVF `intData` 正则抽 effectId(`EquipmentEffectRuneModels.cs:74-81`)写入目标;成功后刷新符文槽+目标槽(`Package.cs:731-735`)。

### CMD 0x01CC (460) `AVATAR_OPTION_CHANGE` — 时装属性调整箱
- **请求**: ≥13B `[srcSlot i16@0][srcId i32@2][targetSlot i16@6][targetId i32@8][abilityNo u8@12]`。`Network/Parsers/Inventory/AvatarOptionChangeRequestParser.cs:14-23`。
- **应答**: 成功 `[1][targetSlot i16][abilityNo u8]`(`Network/Builders/Inventory/AvatarOptionChangeAckBuilder.cs:7-13`);失败 `[0]`(`:16-19`)。
- **行为**: `InventoryAvatarOptionChangeService.TryChange` 消耗调整箱、给目标时装写入 abilityNo;仅补 source(调整箱)槽刷新(`InventoryHandler.AvatarOptionChange.cs:54`)。

### CMD 0x01F3 (499) `USE_DYE` — 时装染色
- **请求**: ≥4B `[dyeSlot i16@0][avatarSlot i16@2]`。`Network/Parsers/Inventory/UseDyeRequestParser.cs:11-21`。
- **应答**: 成功 `[1][avatarSlot i16][4 u32][color1 u16][color2 u16]`(内嵌染色信息块,`Network/Builders/Inventory/DyeItemAckBuilder.cs:5-17`);失败 `[0]`(`:19-22`)。
- **行为**: `InventoryDyeService.TryUse` 带道具过期时间校验(`InventoryHandler.Dye.cs:34-41`);**ACK 块本身负责刷新目标时装,服务端只补刷新染料槽**(`:99-107`);失败但已提交时也发已发生变化槽(`:51-53`)。

### CMD 0x00CE (206) `AVATAR_SOCKET_OPEN` — 时装打孔
- **请求**: ≥8B `[targetSlot i16@0][targetId i32@2][materialSlot i16@6]`(与装备打孔共用 `TryParseSocketOpenBody`,`InventoryHandler.Equipment.cs:465-477`)。
- **应答**: 成功 `[1][targetSlot i16][targetId i32][materialSlot i16]`(`:551-559`);失败 `[0]`(`:379`)/`[0,0x04]`(`:399`)。
- **行为**: `TryCommitAvatarSocket`;**已开孔时静默修复不耗材料**(`:405-413`);补材料槽+Avatar 槽刷新。

### CMD 0x00C9 (201) `AVATAR_EMBLEM_ATTACH` — 时装徽章镶嵌
- **请求**: 兼容两式:body[0]==Avatar(0x0A)且 ≥8B 时带前导空间字节,否则直接 `[targetSlot i16][targetId i32][count u8]` + count×`{emblemSlot i16, emblemId i32, socket u8}`(每条 7B)。`InventoryHandler.Equipment.cs:508-549`。
- **应答**: 成功 `[1][targetSlot i16][targetId i32][count u8]`(`:561-569`);失败 `[0]`(`:422`)/`[0,0x04]`(`:428`)。
- **行为**: `TryCommitAvatarEmblems`;**该共享实现(`TryHandleAvatarEmblemAttach`,`:431-455`)同时被 0x0379 当目标不在装备区时的回退路径复用**(`:357-361`);非穿戴才补 0x000E。

### CMD 0x037A (890) `ADD_EQUIPMENT_SOCKET` — 装备打孔
- 同 0x00CE 的请求/应答布局(`Equipment.cs:288-329`);差异:成功后补 **Main** 目标槽刷新(`:321`)与材料刷新(`:322-323`);同样支持已开孔免材料修复(`:325-328`)。

### CMD 0x0379 (889) `USE_EMBLEM_FOR_EQUIPMENT` — 装备徽章镶嵌
- 请求同 0x00C9 的无前导式(`TryParseEmblemAttachBody`,`Equipment.cs:479-506`);应答 `[1][targetSlot i16][targetId i32][count u8]`(`:367`),失败 `[0]`/`[0,0x04]`(`:338,363`)。
- 行为:`TryCommitEquipmentEmblems`;目标不是装备区物品时**回退走时装徽章流程**(`:357-361`);非穿戴才补刷新(`:368-369`)。

### CMD 0x019C (412) / 0x019D (413) `TITLE_BOOK_PUT` / `TITLE_BOOK_GET` — 称号簿存/取(共用 handler)
- **请求**: ≥20B `[itemSpace i32@0][slot i32@4][itemId i32@8][category i32@12][index i32@16]`。handler 内解析(`InventoryHandler.Trade.cs:444-452`)。
- **应答**: 成功 `[1][itemSpace i32][slot i32][category i32][index i32]`(`:643-652`);失败 `[0][code][itemSpace i32][category i32]`(默认 0x0A,`:654-662`)。
- **行为**: SQLite 称号簿 `TryPutTitleBook/TryGetTitleBook`(`:480-494`)→事务提交(`:509-522`);装备区变动时重载外观快照并推 NOTI 2(`:524-528`);锁状态变化则全量重推 NOTI 0x00FB(`:534-535`);称号簿列表推送走 NOTI 0x0166 TITLE_BOOK_LIST(`:664-674`)。

### CMD 0x0239 (569) `SET_CLONE_TITLE` — 克隆称号设置
- **请求**: ≥4B `[cloneTitleItemId i32@0]`(无租约校验)。`InventoryHandler.Trade.cs:423-426`。
- **应答**: `[1][cloneTitleItemId i32]` 5B(`Game/Appearance/AppearanceService.cs:113-119`)。
- **行为**: `AppearanceService.SaveCloneTitleItemId` 落库 + 更新会话外观尾快照(`Trade.cs:427-434`);0 值即清除。

### CMD 0x03B0 (944) `USE_TITLE_CHANGE_ITEM` — 称号变换券
- **请求**: ≥4B `[srcSlot i16@0][targetSlot i16@2]`,两槽不得相同。`Network/Parsers/Inventory/TitleChangeRequestParser.cs:8-23`。
- **应答**: 成功 `[1][isSuccessBranch u8][resultItemId i32][sourceItemId i32]`(对应客户端 sub_CDB120 读取序,`Network/Builders/Inventory/TitleChangeAckBuilder.cs:9-22`);失败 `[0, 0x11]`(`:24-27`)。
- **行为**: 券→称号规则由 PVF `InventoryTitleChangeRuleResolver.TryResolveTitleChange` 裁决(`InventoryHandler.TitleChange.cs:41-54`);**先发 NOTI 0x000D 全主背包重建再发 ACK**(称号变换界面不消费定点 0x000E,`:73-75` 注释)。

## 七、锁定

### CMD 0x010B (267) `REQUEST_ITEM_LOCK` / 0x010C (268) `REQUEST_ITEM_UNLOCK` / 0x010D (269) `REQUEST_ITEM_UNLOCK_CANCEL` — 装备锁
- **请求**(三者同): ≥3B `[listType u8@0][slot i16@1]`。`InventoryHandler.ItemLock.cs:111-121`。
- **应答**: 0x010B 成功 `[listType u8][slot i16]`、失败 `[code u8]`;0x010C 成功 `[listType u8][slot i16][remainSec i32]`、失败 `[code][remainSec]`;0x010D 成功 `[listType u8][slot i16]`、失败 `[code]`(`Network/Builders/Inventory/EquipmentItemLockBuilder.cs:8-46`)。
- **推送**: NOTI 0x00FB ITEM_LOCK_LIST `[count u16]×{listType u8, slot i16, state u8, (state==2 时 remainSec i32)}`(`EquipmentItemLockBuilder.cs:56-73`;锁定/取消后发单条 delta,`ItemLock.cs:93-109`);解锁成功另推 NOTI 0x00FC ITEM_UNLOCK `[listType u8][slot i16]`(`:62-63`)。
- **行为**: 锁定分配 equipmentLockId、state=1 持久化(`Game/Inventory/Services/EquipmentItemLockCommitService.cs:14-58`);**解锁为立即生效直接删锁行(无 24h 延迟,A21 简化)**(`:61-95`);取消恢复 state=1(`:97-140`)。

### CMD 0x02CA (714) `SORT_ITEM_LOCK`(切换) / 0x02CB (715) `UNLOCK_SORT_ITEM_LOCK` — 排序锁
- **请求**(两者同): ≥3B `[listType u8@0][slot i16@1]`。`InventoryHandler.Move.cs:360-364,398-402`。
- **应答**: 0x02CA 锁定 `[state u8][listType u8][slot i16]`(`Network/Builders/Inventory/SortItemLockBuilder.cs:7-14`;state==0 即解锁时改发 0x02CB 应答,`Move.cs:380-386`);0x02CB `[1][映射后 listType u8][slot i16]`(listType 经 `MapToSortLockListType` 映射,`Move.cs:427-433` + `InventoryRefreshSender.cs`)。
- **行为**: `SortItemLockCommitService.TryCommitToggle/TryCommitUnlock` 持久化排序锁条目(`Game/Inventory/SortItemLockEntry.cs`);失败仅记日志不发错包(`Move.cs:373-381,404-412`)。

## 八、其它

### CMD 0x0313 (787) `EPIC_BOOK_MAKE_ITEM` — 史诗图鉴制作
- **请求**: ≥4B `[outputEquipmentId i32@0]`(>0)。`InventoryHandler.EpicBook.cs:90-100`。
- **应答**: 成功 `[1]`;失败 `[0, code]`(默认 4,`EpicBook.cs:102-112`;`Game/Inventory/Services/EpicPieceCraftService.cs:33`)。
- **行为**: 先锁背包 `CanCraft` 查史诗碎片余额(`:31-45`),再事务内 `TryCraft` 扣碎片产装备(可改邮件投递,`:47-71`);成功后推碎片余额专包 + 主背包变更槽刷新(`:73-81`)。

### CMD 0x0357 (855) `OPEN_AURA_SKIN_SLOT` — 光环皮肤槽开通
- **请求**: ≥4B `[materialSlot i32@0]`(按 i32 读,范围收窄到 i16,超界报 0x02)。`InventoryHandler.AuraSkin.cs:107-121,22-27`。
- **应答**: 成功 `[1]`(`CommonPacketBodyBuilder.BuildSuccessAck`);失败 `[0, code]`(0x02 范围/0x04 通用,`:123-135`)。
- **行为**: 已开通直接短路成功不耗材料(`:47-51`);否则事务内删 1 个材料 + 写 aura_skin 标志列 + 内存置位(`:53-74`);成功后补材料槽刷新与 subtype1 用户信息刷新(`:91-99`)。

### CMD 0x01F7 (503) `USE_RIGHT_OF_CHANGE_GROW_TYPE` — 转职(一类觉醒)券 / ClassChange
- **请求**: ≥3B `[itemSlot i16@0][targetGrowType u8@2]`。`Network/Parsers/Characters/ClassChangeItemRequestParser.cs:8-23`。
- **应答**: 成功 `[1]`;失败 `[0, code]`,code 映射:0xEB 券过期 / 0xD9 冷却 / 0x1E 等级不足 / 0x13 状态无效或目标未变 / 0x17 来源缺失等(`Network/Builders/Characters/ClassChangeItemAckBuilder.cs:8-45`)。
- **行为**: `ClassChangeItemApplicationService.TryUse` 校验券、冷却、等级并扣道具(`InventoryHandler.ClassChange.cs:26-42`);成功更新 `session.Player.GrowType`(`:54-58`)、补主背包刷新与可用次数限制包、重校准寻物任务(`:96-121`)并触发成长任务刷新(`:60-64`);**本域唯一由 ClassChangeItemRequestParser/ClassChangeItemAckBuilder 承担的 CMD,即 0x01F7**(GPH 注册 `GameProtocolHandler.cs:534-535`,无独立 ClassChange CMD)。