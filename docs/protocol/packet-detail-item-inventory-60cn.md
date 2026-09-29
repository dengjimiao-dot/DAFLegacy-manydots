# 物品/背包包格式(60A1)

> 状态索引见 [client-packet-enums-60cn.md](client-packet-enums-60cn.md);帧头/加解密约定见 [packet-frame-crypto-60cn.md](packet-frame-crypto-60cn.md)。
> 字段表来源:服务端 `Network/Builders/Inventory/InventoryPacketBuilders.cs` 序列化器与 `EntranceService.cs` 解析器;字节示例约定见帧文档。
> 大多数物品操作的最终背包变化都以 NOTI 14 增量更新呈现;全量列表走 NOTI 13。

## NOTI 13 `ENUM_NOTIPACKET_ITEM_LIST` — 全量物品列表 ✅

`CreateItemList`;登录、拉取列表及整理物品等全量刷新回本包。整理必须回全量列表:客户端 NOTI 14 处理器对 listType≠3 只做"忽略 0xFFFF + 新增播报"(IDA 0x41D34F/0x41D769),无法表达整排搬迁(见根目录 [PENDING.md](../../PENDING.md) P-21)。

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u8 | listType | 背包列表类型(0=主背包…) |
| 1 | u16 | param | 仅 listType=2 时存在(type2 布局 5+12n) |
| — | u16 | count | 条目数 n |
| — | 记录×n | item | 每条 12B,见下 |

物品条目(12B,NOTI 13/14 共用):

| 宽度 | 字段 | 说明 |
|---:|---|---|
| u16 | slot | 槽位 |
| u16 | itemId | 物品 id |
| u32 | countOrValue | 堆叠数量/装备数值 |
| u8 | state | 状态 |
| u16 | durability | 耐久 |
| u8 | seal | 封印状态 |

主背包全量(`CreateMainItemList`)无独立钱包头:槽 0/物品 0=金币、槽 2/物品 2=胜利点,由服务端在每次全量刷新时合成。

回包字节(示例):`00 0D 15 00 00 00 D6 D2 D6 D2 D6 71 DA C2 D6 D6 D6 D6 FE D6 D6`

## NOTI 14 `ENUM_NOTIPACKET_UPDATE_ITEM_LIST` — 增量物品更新 ✅

`CreateUpdateItemList`;购买/出售/修理/删除/移动/分解/抽奖/消耗品/耐久/任务奖励等统一回本包。

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u8 | listType | |
| 1 | u16 | count | 条目数 n |
| 3 | 记录×n | item | 同 NOTI 13 的 12B 条目 |

总长 = 3 + 12n;堆叠耗尽用清槽条目 `{slot, 0xFFFF, 0}`。

回包字节(示例):`00 0E 15 00 00 00 D6 D2 D6 D2 D6 71 DA C2 D6 D6 D6 D6 FE D6 D6`

## CMD 18 `ENUM_CMDPACKET_DELETE_ITEM` — 删除物品 ✅

正文:`seq u16 + listType u8 + count u8 + n×{operation u16, slot u16, itemId u16, requestedCount u32}`,末尾附 4B 命令令牌(TryReadConsumeItems,单包最多 32 条)。

**应答**:
- 成功:`[1, listType, count u8] + n×{slot u16, consumedCount u32}` = 3+6n
- 错误 3B:`[0, errorCode, listType]`

随后推 NOTI 14。发包字节(示例):`01 12 0C 00 00 00 BE 23 C2 58 01 00` *

## CMD 19 `ENUM_CMDPACKET_MOVE_ITEMSPACE` — 跨背包/仓库移动 ✅

`TryReadInventoryMove`(body ≥ 20B):

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | seq | |
| 2 | u8 | srcList | 源列表类型 |
| 3 | i16 | srcSlot | 源槽位 |
| 5 | 2B | 保留 | 解析器跳过 |
| 7 | i32 | count | 移动数量 |
| 11 | u8 | dstList | 目标列表类型 |
| 12 | i16 | dstSlot | 目标槽位(负数按 0xFFFF) |
| 14 | ≥6B | 尾部 | 不读取 |

**应答**:11B `[1, srcList, srcSlot u16, movedCount u32, dstList, dstSlot u16]`;随后推 NOTI 14。

发包字节(示例):`01 13 0C 00 00 00 BE 23 C2 58 01 00` *

## CMD 20 `ENUM_CMDPACKET_SORT_ITEM` — 整理物品 ✅

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | seq | |
| 2 | u8 | space | 背包空间类型 |

**应答**:成功 2B `[1, space]`;错误 3B `[0, errorCode, space]`。随后推 NOTI 13 全量列表(客户端增量通道无法表达整排搬迁,见根目录 [PENDING.md](../../PENDING.md) P-21)。

发包字节(示例):`01 14 0C 00 00 00 BE 23 C2 58 01 00` *

## CMD 21 `ENUM_CMDPACKET_BUY_ITEM` — NPC 商店购买 ✅

`TryReadBuyItem` 兼容多种宽度:

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | seq | |
| 2 | u16 或 i32 | itemId | ≥6B 时按 i32 读(≤0xFFFF 有效),否则 u16 |
| — | u8/u16/i32 | countOrQualitySeed | 堆叠品=数量(1..999);装备=客户端品质种子(服务端 06:00 权威重算) |

**应答**:25B —— DF2008 成功处理器依次读四个完整钱包值再读物品牌,缺字节会把接收缓冲 ASCII '0' 填充读成 0x30303030:

| 宽度 | 字段 |
|---:|---|
| u8 | result=1 |
| u32 | updatedGold |
| u32 | updatedVictoryPoints |
| u32 | auxiliaryItemSpaceValue |
| u32 | updatedCera |
| u16 | slot |
| u16 | itemId |
| u32 | countOrValue |

随后推 NOTI 14。发包字节(示例):`01 15 0C 00 00 00 BE 23 C2 58 01 00` *

## CMD 24 `ENUM_CMDPACKET_SELL_ITEM` — 出售 ✅

`TryReadSellItem` 三种形态:`{list u8, slot u8, count u8}`(5B)/ `{list u8, slot i16, count i16}`(≥7B)/ 兼容旧版 `{slot i16, count i16}`。

**应答**:10B `[1, updatedGold u32, itemSpace, slot u16, soldCount u16]`;随后推 NOTI 14。

发包字节(示例):`01 18 0C 00 00 00 BE 23 C2 58 01 00` *

## CMD 25 `ENUM_CMDPACKET_REPAIR_EQUIPMENT` — 修理装备 ✅

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | seq | |
| 2 | u8 | itemSpace | 0 或 3 |
| 3 | u16 | slot | 0xFFFF=全部修理 |

**应答**:8B `[1, updatedGold u32, itemSpace, slot u16]`;随后推 NOTI 14。

发包字节(示例):`01 19 0C 00 00 00 BE 23 C2 58 01 00` *

## CMD 26 `ENUM_CMDPACKET_SET_ITEMTRADE_STATE` — 交易锁定 ✅

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | seq | |
| 2 | u8 | listType | 0 或 3 |
| 3 | u16 | slot | |
| 5 | u16 | state | 锁定状态 |

**应答**:5B `[1, itemId u16, listType, 0]`。此版交易 NOTI 15-18 未实现,交易流程不完整。

发包字节(示例):`01 1A 0C 00 00 00 BE 23 C2 58 01 00` *

## CMD 28 `ENUM_CMDPACKET_DISJOINT_ITEM` — 分解 ✅

**应答**(`CreateDisjointItemReply`):`[1, srcSlot u16, itemSpace u8, count u8] + k×{dstSlot u16, itemId u16, count u32}` = 5+8k;随后推 NOTI 14。

发包字节(示例):`01 1C 0C 00 00 00 BE 23 C2 58 01 00` *

## CMD 29 `ENUM_CMDPACKET_USE_LOTTERY_ITEM` — 使用抽奖券 ✅

**应答**:14B `[1, srcSlot u16, dstSlot u16, itemId u16, countOrValue u32, instanceValue u16, itemAttr u8]`;随后推 NOTI 14。

发包字节(示例):`01 1D 0C 00 00 00 BE 23 C2 58 01 00` *

## CMD 44 `ENUM_CMDPACKET_USE_COIN` — 使用复活币 ✅

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | seq | |
| 2 | u16 | targetUid | 目标(复活)用户 id |

**应答**:3B `[1, targetUid u16]`。

发包字节(示例):`01 2C 0E 00 00 00 38 89 E3 80 01 00 5A 7A`

## CMD 46 `ENUM_CMDPACKET_GET_ITEM` — 拾取 ✅

客户端乐观拾取;服务端拒绝时不回显错误(静默),错误码 0x15 进入 0x00414198 公共回滚,清除 item-object+0x7A5 的待拾取标志,物品可再次拾取。

**应答**:
- 静默拒绝 2B:`[0, 0x15]`
- 金币拾取 NOTI 39(24B)
- 物品拾取 NOTI 39(12B)

发包字节(示例):`01 2E 0C 00 00 00 BE 23 C2 58 01 00` *

## NOTI 39 `ENUM_NOTIPACKET_GET_ITEM` — 拾取回执 ✅

金币布局(24B,`CreateGoldItemPickup`):

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | groundId | 地面物 id |
| 2 | u16 | pickerUid | 拾取者 |
| 4 | 4×(u32+u8) | partyGold | 每队位 {分得金币 u32, 经济调控条目数 u8=0};拾取者队位=全额,其余 0 |

物品布局(12B,`CreateInventoryItemPickup`):

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | groundId | |
| 2 | u16 | pickerUid | |
| 4 | 4B | dice | 队伍骰子路由;直接拾取必须全 0(非零被当作骰子结果) |
| 8 | u16 | pickerUid2 | |
| 10 | u16 | dstSlot | 入包槽位 |

回包字节(示例):`00 27 12 00 00 00 DA D6 D2 D6 D6 D6 D6 D6 D2 D6 C2 D6`

## CMD 47 `ENUM_CMDPACKET_USE_STACKABLE` — 使用消耗品 ✅

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | seq | |
| 2 | u16 | slot | ≥3 且 <0xFFFF |
| 4 | u8 | listType | |

**应答**:成功 4B `[1, slotLo, slotHi, listType]`;错误 3B `[0, errorCode, listType]`;随后推 NOTI 14。

发包字节(示例):`01 2F 0C 00 00 00 BE 23 C2 58 01 00` *

## CMD 50 `ENUM_CMDPACKET_DROP_ITEM` — 丢弃物品 ✅

`TryParseDropItemCommand`(body 13B):

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | seq | |
| 2 | i16 | x | 地图坐标 |
| 4 | i16 | y | |
| 6 | u8 | itemSpace | |
| 7 | u16 | slot | 源槽位 |
| 9 | u32 | count | 必须 >0 |

**应答**:成功 8B `[1, itemSpace, slot u16, count u32]`;错误 3B `[0, errorCode, itemSpace]`。
地面掉落广播推 NOTI 40。

发包字节(示例):`01 32 17 00 00 00 9E FE DD 12 01 00 F6 7A 63 7A 7A FA 7A 5A 7A 7A 7A`

## NOTI 40 `ENUM_NOTIPACKET_DROP_ITEM` — 地面掉落广播 ✅

注意与 NOTI 38 的掉落记录不同:attr 在 info 之前、无 owner 尾字段。

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | uid | 丢弃者 |
| 2 | i16 | x | |
| 4 | i16 | y | |
| 6 | u16 | groundId | |
| 8 | u16 | itemId | |
| 10 | u8 | itemAttr | |
| 11 | u32 | addInfo | 堆叠数/强化等级(多态) |
| 15 | u16 | durability | |

总长 17B。回包字节(示例):`00 28 17 00 00 00 D2 D6 47 D6 F5 D6 DA D6 91 CA D6 C2 D6 D6 D6 FE D6`

## CMD 51 `ENUM_CMDPACKET_DECREASE_DURABILITY` — 耐久扣减 ✅

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | seq | |
| 2 | u8 | slot | |

**应答**:2B `[1, slot]`;随后推 NOTI 14。

发包字节(示例):`01 33 0C 00 00 00 BE 23 C2 58 01 00` *
