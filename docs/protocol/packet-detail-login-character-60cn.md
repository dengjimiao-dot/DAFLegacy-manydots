# 登录/角色选择包格式(60A1)

> 状态索引见 [client-packet-enums-60cn.md](client-packet-enums-60cn.md);帧头/加解密约定见 [packet-frame-crypto-60cn.md](packet-frame-crypto-60cn.md)。
> 字段表来源:服务端 `GameProtocolEngine.cs` 序列化器与 `EntranceService.cs` 解析器;字节示例约定见帧文档。
> 本组核心是 NOTI 2 `USERINFO` 的四种变体(选角列表 / 城镇角色 / 当前角色详情 / 对端上下文);本文档详登记前三种,mode 3 见 [packet-detail-party-60cn.md](packet-detail-party-60cn.md)。

## CMD 1 `ENUM_CMDPACKET_LOGIN` — 登录 ✅

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | seq | 命令序号 |
| 2 | dstr | name | 账号名(CP936) |
| — | dstr | password | 口令;之后可携带启动器附加的机器/会话元数据(鉴权只读前两段) |

**应答**:CMD 1 回包 5B `[1, 0, 0, 0, 0]`(result + 3 个账号标志 + 1 个客户端状态尾字节;全 0 走正常角色列表路径)。

发包字节(示例):`01 01 0C 00 00 00 BE 23 C2 58 01 00` *

## CMD 3 `ENUM_CMDPACKET_EXIT` — 退出游戏世界 ✅

**应答**:通用成功 1B `[1]`(客户端 EXIT 处理器消费后清旧会话)。

发包字节(示例):`01 03 0C 00 00 00 BE 23 C2 58 01 00` *

## CMD 4 `ENUM_CMDPACKET_SELECT_CHARACTER` — 选择角色 ✅

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | seq | 命令序号 |
| 2 | u8 | slot | 角色卡槽位 |

**应答**(变长,`CreateSelectCharacterReply`):

| 宽度 | 字段 | 说明 |
|---:|---|---|
| u8 | result | 成功=1 |
| u32 | characterId | 与 NOTI 2 mode0 的 uid 对应 |
| u16 | serverId | |
| u16 | usedFatigue | 已用疲劳 |
| u16 | maximumFatigue | 疲劳上限(默认 156;黑钻 +32) |
| u16 | premiumFatigue | 付费疲劳 |
| u8 + 记录 | premiumServices | 数量 + n×{serviceType u8, remainSeconds u32} |
| u32 | cash | 点券 |
| 3×(u16+u32) | activeQuests | 恰好 3 个 {questId, trigger} 槽;未用=FFFF/0 |
| u8 + 记录 | completedQuestGroups | 组数 + n×{group u8, len u32, bitmap 32B}(经 questmappingtable.tbl 位映射) |
| u8 | townId | 城镇编号 |
| u32 | completedTutorialFlags | 新手引导完成位图(低 32 位) |

发包字节(示例):`01 04 0C 00 00 00 BE 23 C2 58 01 00` *

## CMD 5 `ENUM_CMDPACKET_CREATE_CHARACTER` — 创建角色 ✅

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | seq | |
| 2 | u8 | job | 职业 |
| 3 | dstr | name | 角色名(1..30 编码字节) |

**应答**:通用成功 1B(客户端随后刷新 USERINFO)。

发包字节(示例):`01 05 0C 00 00 00 BE 23 C2 58 01 00` *

## CMD 6 `ENUM_CMDPACKET_DELETE_CHARACTER` — 删除角色 ✅

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | seq | |
| 2 | u8 | slot | 待删槽位 |

**应答**:2B `[result, slot]`(回包含被删槽位,客户端移除对应角色卡)。

发包字节(示例):`01 06 0D 00 00 00 09 D2 8D 10 01 00 3A`

## CMD 7 `ENUM_CMDPACKET_RETURN_SELECT_CHARACTER` — 返回选角界面 ✅

**应答**:通用成功 1B;随后服务端推送新 NOTI 2 角色列表。

发包字节(示例):`01 07 0C 00 00 00 BE 23 C2 58 01 00` *

## CMD 8 `ENUM_CMDPACKET_GET_USERINFO` — 拉取角色列表 ✅

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | seq | |
| 2 | 变长 | filter | 过滤字节(如 `FF FF 02`),服务端不解释 |

**应答**:无同步回包,异步推 NOTI 2(空列表或完整列表)。

发包字节(示例):`01 08 0C 00 00 00 BE 23 C2 58 01 00` *

## NOTI 2 `ENUM_NOTIPACKET_USERINFO` — 角色信息(4 种模式) ✅

### mode 2 — 选角界面角色卡列表(`CreateUserInfo`)

| 宽度 | 字段 | 说明 |
|---:|---|---|
| u8 | mode | =2,直接构建可见角色卡 |
| u16 | count | 角色数 n |
| 记录×n | character | 见下 |

角色卡记录:

| 宽度 | 字段 | 说明 |
|---:|---|---|
| u16 | slot | 槽位 |
| dstr | name | 角色名(1..30B) |
| u8 | job | 职业 |
| u8 | growth | bit0-3=转职分支,bit4-6=觉醒阶段 |
| u8 | level | 等级 |
| u8 | pvpGrade | PVP 段位(0..30,觉醒位不在本字段) |
| u8 | state | =0 |
| u8 | appearanceCount | 外观数量 |
| 记录 | appearance | ×{slot u8, itemId u16, state u8}(slot≤9) |
| u8 | extState | =0 |
| u32 | guildId | =0 |
| u8 | guildFlag | =0 |
| u8 | state | =0 |
| u8 | option | =0 |
| u8 | visible | =1(可见/可选卡) |

### mode 0 — 城镇角色实体更新(`CreateCurrentCharacterInfo`)

开头 `mode u8=0, count u16=1`,角色基础字段同上;后续为城镇实体字段:

| 宽度 | 字段 | 说明 |
|---:|---|---|
| u16 | uid | 必须=SELECT_CHARACTER 返回的 characterId |
| … | 基础字段 | name/job/growth/level/pvpGrade/state/entityState + 紧凑外观×{slot u8, itemId u16, state u8}(slot≤10,10=称号) |
| u16 | creatureItemId | 出战宠物外观 id(经 sub_50D8D0 转 [creature species]) |
| dstr | creatureName | 宠物名 |
| u8 | creatureState | 客户端存 !state,0 才入场景 |
| u8 | pcRoomEffect | PC 房名字特效(record+0xB0) |
| u8 | blackDiamond | 1=金名+黑钻图标(record+0xB8) |
| u32 | reputation | 名誉分(≥100 触发 GradeB/C/D 称号特效) |
| u8 + dstr | guildInfo | 公会信息标志 + 公会名(空也必须存在) |
| u8 + u32×3 | entity | active=1 + actor state A/B/C |

### subtype 1 — 当前角色详情(`CreateCurrentCharacterDetails`)

| 宽度 | 字段 | 说明 |
|---:|---|---|
| u8 | subtype | =1 |
| u16 | count | =1 |
| u16 | uid | |
| u32 | experience | 经验 |
| u32 + 81B | statBlock | 战斗属性块,恰好 81B:maxHp/maxMp u32、物攻/物防/魔攻/魔防/四属性抗 i16×8、17×u16 状态抗性、负重上限 u32、HP/MP回复、移动/攻击/施速、受身、跳跃 u16×7、体重 u32,末字节=虚弱/疲劳恢复百分比 |
| u8 + 记录 | equipment | 数量 + ×{slot u8, itemId u16, value u32, state u8, durability u16}(23 个穿戴槽权威快照,缺失槽被卸下) |
| u8 + 记录 | skills | 数量 + ×{skillId u8, level u8}(紧凑对;第三状态字节走 NOTI 19) |
| u8 | creatureLevel | 出战宠物等级 |

### mode 3 — 对端上下文(组队连接前置)

为任意用户创建 user+0x110 对端连接对象,是消除组队"连接中"的根因;body 为完整"他人角色详情"(与 subtype 1 同构的字段族)。字段布局、触发时机与实现说明见 [packet-detail-party-60cn.md](packet-detail-party-60cn.md) 流程概览与 [client-function-register-60cn.md](client-function-register-60cn.md)(字段映射待逐项回填)。

回包字节(示例,mode2):`00 02 21 00 00 00 DE D2 D6 D6 D6 DA D6 D6 D6 C7 CF E7 D6 D2 FE D6 D6 D6 D6 D6 D6 D6 D6 D6 D6 D6 D2`
