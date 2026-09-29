# 宠物(Creature)包格式(60A1)

> 状态索引见 [client-packet-enums-60cn.md](client-packet-enums-60cn.md);帧头/加解密约定见 [packet-frame-crypto-60cn.md](packet-frame-crypto-60cn.md)。
> 字段表来源:服务端 `Network/Builders/Pets/PetsPacketBuilders.cs` 序列化器与 `EntranceService.cs` 解析器;字节示例约定见帧文档。

## CMD 105 `ENUM_CMDPACKET_HATCH_CREATURE` — 孵化宠物 ✅

`TryReadHatchCreature`:

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | seq | |
| 2 | u8 | itemSpace | 背包列表 |
| 3 | u16 | slot | 宠物蛋槽位 |

**应答**:成功 1B `[1]`;错误 2B `[0, errorCode]`;成功后推 NOTI 105。

发包字节(示例):`01 69 0C 00 00 00 BE 23 C2 58 01 00` *

## NOTI 105 `ENUM_NOTIPACKET_CREATURE_ITEM_LIST` — 宠物列表 ✅

⚠ 此版枚举名 `CREATURE_ITEM_LIST`,=服务端 `CreatureInfoNotification`。

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u8 | count | 宠物数 n |
| 1 | 记录×n | creature | 见下 |

宠物记录(15+len):

| 宽度 | 字段 | 说明 |
|---:|---|---|
| u32 | uid | 宠物 uid(非 0) |
| u8 | stomach | 饱食度 |
| u32 | experience | 累计经验 |
| u8 | level | 等级 |
| dstr | name | 名字(<30B) |
| u8 | noCharge | 1=免消耗 |

回包字节(示例):`00 69 19 00 00 00 D2 CA D6 D6 D6 1E 34 FA D6 D6 C2 DA D6 D6 D6 97 43 07 D6`

## CMD 103 `ENUM_CMDPACKET_RENAME_CREATURE` — 宠物改名 ✅

`TryReadRenameCreature`(body 9+len):

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | seq | |
| 2 | u16 | cardSlot | 宠物卡槽 |
| 4 | u8 | itemSpace | 背包列表(服务端默认 7) |
| 5 | u32 + n | name | dstr 新名(1..12B,不含 0) |

**应答**:成功 4B `[1, cardSlot u16, itemSpace u8]`;错误 2B `[0, errorCode]`;改名广播推 NOTI 101。

发包字节(示例):`01 67 0C 00 00 00 BE 23 C2 58 01 00` *

## NOTI 101 `ENUM_NOTIPACKET_RENAME_CREATURE` — 改名广播 ✅

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | uid | 区域用户 id |
| 2 | dstr | name | 新名(1..19B) |

总长 = 6 + len。回包字节(示例):`00 65 0F 00 00 00 D2 D6 DA D6 D6 D6 97 43 07`

## CMD 104 `ENUM_CMDPACKET_RESPONSE_CREATURE` — 召唤/收回 ✅

正文布局未单独确认。**应答**:无直接回包,推 NOTI 104。

发包字节(示例):`01 68 0C 00 00 00 BE 23 C2 58 01 00` *

## NOTI 104 `ENUM_NOTIPACKET_RESPONSE_CREATURE` — 召唤/收回响应 ✅

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | uid | 区域用户 id |

总长 2B。回包字节(示例):`00 68 08 00 00 00 D2 D6`

## NOTI 100 `ENUM_NOTIPACKET_DIED_CREATURE` — 宠物死亡 ✅

⚠ 按**区域用户 id**寻址,不是宠物 uid:客户端先解析该 id,再把对应玩家的宠物对象标死。

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | uid | 区域用户 id |

总长 2B。回包字节(示例):`00 64 08 00 00 00 D2 D6`

## NOTI 107 `ENUM_NOTIPACKET_REVIVAL_CREATURE` — 宠物复活 ✅

布局与 NOTI 100 相同(区域用户 id),清除宠物死亡标志。总长 2B。

回包字节(示例):`00 6B 08 00 00 00 D2 D6`

## NOTI 102 `ENUM_NOTIPACKET_GAIN_EXP_CREATURE` — 宠物经验 ✅

无宠物 uid、无普通/成长判别:客户端把记录直接应用到当前出战的宠物。

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u8 | level | 等级(非 0) |
| 1 | u32 | experience | 累计经验 |

总长 5B。回包字节(示例):`00 66 0B 00 00 00 C2 34 FA D6 D6`

## NOTI 103 `ENUM_NOTIPACKET_CREATURE_STATE` — 宠物状态 ✅

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u32 | creatureUid | 宠物 uid(非 0) |
| 4 | u32 | state | 状态值 |

总长 8B。回包字节(示例):`00 67 0E 00 00 00 CA D6 D6 D6 D2 D6 D6 D6`

## NOTI 106 `ENUM_NOTIPACKET_EVOLUTE_CREATURE` — 宠物进化 ✅

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u8 | evolutionCreatureId | 进化后物种 id(非 0) |
| 1 | u16 | uid | 区域用户 id |

总长 3B。回包字节(示例):`00 6A 09 00 00 00 DE D2 D6`
