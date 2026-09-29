# 合成/强化包格式(60A1)

> 状态索引见 [client-packet-enums-60cn.md](client-packet-enums-60cn.md);帧头/加解密约定见 [packet-frame-crypto-60cn.md](packet-frame-crypto-60cn.md)。
> 字段表来源:服务端 `GameProtocolEngine.cs` 序列化器与 `EntranceService.cs` 解析器;字节示例约定见帧文档。
> 本组四条命令成功后统一推 NOTI 14 增量更新(格式见 [packet-detail-item-inventory-60cn.md](packet-detail-item-inventory-60cn.md))。

## CMD 27 `ENUM_CMDPACKET_COMPOUND_ITEM` — 物品合成 ✅

`TryParseCompoundItemCommand`(body 恰 8B):

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | seq | |
| 2 | u16 | source | 配方物品 id 或背包槽位 |
| 4 | u8 | isItemId | source 是否为物品 id(≤1) |
| 5 | u8 | craftCount | 制作数量(非 0) |
| 6 | u8 | reserved | 必须 =0 |
| 7 | u8 | fixed | 必须 =1 |

**应答**:
- 成功(`CreateCompoundItemReply`,变长):`[1, consumedCount u8] + m×{listType u8, slot u16, remainingCount u32} + [resultCount u8] + k×{slot u16, itemId u16, countOrValue u32, state u8, durability u16}`
- 失败 2B:`[0, errorCode]`

发包字节(示例):`01 1B 12 00 00 00 AA 3E FB 68 01 00 70 7A 5A 1A 7A 5A`

## CMD 83 `ENUM_CMDPACKET_UPGRADE_ITEM` — 强化 ✅

`TryParseUpgradeItemCommand`(body 8B;要求 targetItem≠0 且 targetSlot≠materialSlot):

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | seq | |
| 2 | u16 | targetSlot | 目标装备槽 |
| 4 | u16 | targetItemId | 目标物品 id |
| 6 | u16 | materialSlot | 材料槽 |

**应答**(`CreateUpgradeItemReply`):

| 宽度 | 字段 | 说明 |
|---:|---:|---|
| u8 | result | =1 |
| u16 | materialSlot | |
| u32 | materialRemaining | 材料余量 |
| u8 | resultCode | 结果码 |
| u8 | newLevel | 强化后等级 |
| u16 | targetSlot | |
| u8 | 0 | 仅 resultCode>2 时附加(总长 11 或 12B) |

发包字节(示例):`01 53 12 00 00 00 27 0E E6 50 01 00 1A 7A 47 9A 5B 7A`

## CMD 84 `ENUM_CMDPACKET_RESET_ITEM_ATTR` — 属性重置 ✅

`TryParseResetItemAttributeCommand`:入参布局与 CMD 83 完全相同(targetSlot/targetItemId/materialSlot)。

**应答**:
- 成功 9B:`[1, materialSlot u16, materialRemaining u32, operationType u16]`
- 失败 2B:`[0, errorCode]`

发包字节(示例):`01 54 12 00 00 00 27 0E E6 50 01 00 1A 7A 47 9A 5B 7A`

## CMD 102 `ENUM_CMDPACKET_COMPOUND_AVATAR` — 时装合成 ✅

`TryParseCompoundAvatarCommand`(body 恰 15B):

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | seq | |
| 2 | u16 | materialSlot | 材料槽 |
| 4 | u16 | marker1 | 第一物品列表标记 |
| 6 | u16 | firstItemId | 第一件时装 id |
| 8 | u16 | marker2 | 第二物品列表标记 |
| 10 | u16 | secondItemId | 第二件时装 id |
| 12 | u16 | marker3 | 选项列表标记 |
| 14 | u8 | selectedOption | 选中选项行 |
| 15 | u8 | firstSlot | 第一件槽位 |
| 16 | u8 | secondSlot | 第二件槽位 |

**应答**:
- 成功(变长,`CreateCompoundAvatarReply`):`[1, consumedCount u8] + m×{listType u8, slot u16, remainingCount u32} + resultSlot u16 + resultItemId u16 + remainingSeconds u32 + selectedOption u8`
- 失败 2B:`[0, 4]`(默认错误码 4)

发包字节(示例):`01 66 0C 00 00 00 BE 23 C2 58 01 00` *
