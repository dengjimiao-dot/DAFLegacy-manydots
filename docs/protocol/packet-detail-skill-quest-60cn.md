# 技能/任务包格式(60A1)

> 状态索引见 [client-packet-enums-60cn.md](client-packet-enums-60cn.md);帧头/加解密约定见 [packet-frame-crypto-60cn.md](packet-frame-crypto-60cn.md)。
> 字段表来源:服务端分组构建器 `Network/Builders/<组>/<组>PacketBuilders.cs` 序列化器与 `EntranceService.cs` 解析器;字节示例约定见帧文档。

## CMD 30 `ENUM_CMDPACKET_CHANGE_SKILLSLOT` — 技能栏变更 ✅

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | seq | |
| 2 | u8 | src | 源槽 |
| 3 | u8 | dst | 目标槽 |

**应答**:3B `[1, src, dst]`;随后推 NOTI 19。

发包字节(示例):`01 1E 0C 00 00 00 BE 23 C2 58 01 00` *

## CMD 31 `ENUM_CMDPACKET_BUY_SKILL` — 学习技能 ✅

**应答**:6B `[1, remainingSP u16, slot u8, skillId u8, level u8]`;随后推 NOTI 19。

发包字节(示例):`01 1F 0C 00 00 00 BE 23 C2 58 01 00` *

## NOTI 19 `ENUM_NOTIPACKET_SKILLINFO` — 技能列表 ✅

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | sp | 剩余技能点 |
| 2 | u8 | count | 技能数 n |
| 3 | 记录×n | skill | 每条 3B:{slot u8, skillId u8, level u8};无稳定槽位时按包序紧凑编号 |
| — | u8 | tail | =0(无逐技能辅助值) |

总长 = 4 + 3n。回包字节(示例):`00 13 0D 00 00 00 C2 D6 D2 D6 D2 D2 D6`

## CMD 32 `ENUM_CMDPACKET_INCREASE_STATUS` — 属性点分配 ✅

**应答**(`CreateIncreaseStatusReply`):12B `[1, srcSlot u16, statusType u8, amount i32, aux1 u16, aux2 u16]`(回包自带,无独立 NOTI)。

发包字节(示例):`01 20 0C 00 00 00 BE 23 C2 58 01 00` *

## CMD 33 `ENUM_CMDPACKET_ACCEPT_QUEST` — 接受任务 ✅

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | seq | |
| 2 | u16 | quest | 任务 id |

**应答**(`CreateAcceptQuestReply`,8+8n):`[1, questId u16, trigger u32, grants u8] + n×{slot u16, itemId u16, countOrSeed u32}`。
trigger≠0 时客户端(0x4EC960)隐藏脚本 [delete npc index];CMD 35 trigger=0 经 0x4EBB60 恢复。

发包字节(示例):`01 21 0C 00 00 00 BE 23 C2 58 01 00` *

## CMD 34 `ENUM_CMDPACKET_GIVEUP_QUEST` — 放弃任务 ✅

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | seq | |
| 2 | u16 | quest | 任务 id |

**应答**:3B `[1, questId u16]`。

发包字节(示例):`01 22 0C 00 00 00 BE 23 C2 58 01 00` *

## CMD 35 `ENUM_CMDPACKET_SET_QUEST_TRIGGER` — 任务触发器 ✅

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | seq | |
| 2 | u16 | quest | 任务 id |
| 4 | u32 | trigger | 触发器值 |

**应答**:7B `[1, questId u16, trigger u32]`。

发包字节(示例):`01 23 0C 00 00 00 BE 23 C2 58 01 00` *

## CMD 36 `ENUM_CMDPACKET_FINISH_QUEST` — 完成任务 ✅

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | seq | |
| 2 | u16 | quest | 任务 id |
| 4 | 变长 | extras | 任务脚本附加字节(服务端按任务解析) |

**应答**(`CreateFinishQuestReply`,变长):

| 宽度 | 字段 | 说明 |
|---:|---|---|
| u8 | result | =1 |
| u16 | questId | |
| u8 | completionType | =0 |
| u8 + 记录 | consumed | 消耗数 + ×{0 u8, slot u16, remainingCount u32} |
| u8 | chainType | 0=插入物品链,1/2=成长技能链 |
| chainType=0 | inserted | 数量 + ×{slot u16, itemId u16, countOrSeed u32} |
| chainType=1/2 | growth | growNumber u8 + 数量 + ×{skillClass u8(<5), skillId u8, level u8} |

奖励入包以 NOTI 14 呈现。

发包字节(示例):`01 24 0C 00 00 00 BE 23 C2 58 01 00` *

## NOTI 21 `ENUM_NOTIPACKET_ACCEPTABLE_QUEST_LIST` — 可接任务列表 ✅

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u8 | count | 任务数 n |
| 1 | u16×n | questId | |

总长 = 1 + 2n。接任务/放弃任务/触发器变更后均重推本列表。

回包字节(示例):`00 15 0B 00 00 00 DE 71 DA 7D DA`
