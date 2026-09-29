# 副本流程包格式(60A1)

> 状态索引见 [client-packet-enums-60cn.md](client-packet-enums-60cn.md);帧头/加解密约定见 [packet-frame-crypto-60cn.md](packet-frame-crypto-60cn.md)。
> 字段表来源:服务端 `GameProtocolEngine.cs` 序列化器与 `EntranceService.cs` 解析器;字节示例约定见帧文档。
> 疲劳同步(NOTI 36)格式见 [packet-detail-character-state-60cn.md](packet-detail-character-state-60cn.md)。

## CMD 15 `ENUM_CMDPACKET_START_GAME` — 进图资格 ✅

正文布局未单独确认(服务端按疲劳/资格判定)。

**应答**:
- 疲劳不足 3B:`[0, 0x16, partyMemberIndex]`(DF2008 读 0x16 + 耗尽队员序号)
- 成功:异步推 NOTI 27(=服务端 `EnterSelectDungeon`)

发包字节(示例):`01 0F 0C 00 00 00 BE 23 C2 58 01 00` *

## NOTI 27 `ENUM_NOTIPACKET_START_GAME` — 选图资格 ✅

⚠ 此版枚举名 `START_GAME`,语义为进入选图界面,=服务端 `EnterSelectDungeonNotification`。
勿用新版服务器的 NOTI 27 布局(0x41F1CF/0x41F27A/0x41F2BE 实测)。

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u8 | hellQuestsOk | 前置任务完成标志;0 触发字符串 5431 |
| 1 | u8 | missingCount | 缺地狱票队员数 m(≤4) |
| 2 | u8×m | partySlots | **队内槽位**(触发字符串 5430),不是用户 id |

回包字节(示例):`00 1B 08 00 00 00 D2 D6`

## CMD 16 `ENUM_CMDPACKET_SELECT_DUNGEON` — 选择副本 ✅

正文布局未单独确认。**应答**:依次推 NOTI 28(副本信息)与 NOTI 29(起始地图)。

发包字节(示例):`01 10 0C 00 00 00 BE 23 C2 58 01 00` *

## NOTI 28 `ENUM_NOTIPACKET_DUNGEON_INFO` — 副本信息 ✅

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | dungeonId | 副本 id(新手副本=10000) |
| 2 | u8 | difficulty | 难度 |
| 3 | u8 | mazeIndex | 迷宫索引 |
| 4 | u8 | bossX | Boss 房 X(0xFF=未定) |
| 5 | u8 | bossY | Boss 房 Y |
| 6 | u8 | flagA | 标志 A |
| 7 | u8 | flagB | 标志 B |

总长 7B。回包字节(示例):`00 1C 0E 00 00 00 71 DA D2 D6 29 29 D6 D6`

## NOTI 29 `ENUM_NOTIPACKET_START_MAP` — 起始地图快照 ✅

过图(MOVE_MAP)复用本包推送新地图。两种形态:

完整布局(12+9n+13k):

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u8 | roomX | 房间坐标 |
| 1 | u8 | roomY | |
| 2 | u32 | randomSeed | 随机种子 |
| 6 | u8 | roomState | 1=完整,2=完整(保留),0=缓存形态 |
| 7 | u16 | mapId | 地图 id(新手引导经 map.lst 映射 61000→tutorial.map) |
| 9 | u8 | monsterCount | 怪物数 n |
| 10 | 记录×n | monster | 每条 9B:{mapListIndex u8, uniqueId u16, monsterIndex u16, level u8, type u8(0普通/1冠军/2超冠军/3Boss), isBoxMonster u8, boxIndex u8} |
| — | u8 | passiveCount | 被动物品数 k |
| — | 记录×k | passiveDrop | 每条 13B:{assocSlot u8, groundId u16, itemId u16, addInfo u32, ownerUid u32}(与 NOTI 38 掉落记录不同:以关联槽开头、无 state 字段) |
| — | u8 | hellPartyMode | 地狱队模式 |

缓存布局(7B,`CreateCachedStartMap`):`roomX u8 + roomY u8 + seed u32 + state u8=0` —— 客户端跳过地图/怪物/物品节,激活坐标域快照。

回包字节(示例):`00 1D 1B 00 00 00 D6 D6 E1 78 D6 D6 D2 F7 6D D2 D6 D2 D6 D2 D6 D2 D6 D6 D6 D6 D6`

## CMD 40 `ENUM_CMDPACKET_FINISH_LOADING` — 加载完成 ✅

**应答**:异步推 NOTI 30 + NOTI 136,均无载荷(0B)。

发包字节(示例):`01 28 0C 00 00 00 BE 23 C2 58 01 00` *

## NOTI 30 `ENUM_NOTIPACKET_FINISH_LOADING` — 加载回执 ✅

无载荷。回包字节(示例):`00 1E 06 00 00 00`

## NOTI 136 `ENUM_NOTIPACKET_ENTER_GAMEWORLD_COMPLETE` — 入场完成 ✅

无载荷。回包字节(示例):`00 88 06 00 00 00`

## CMD 42 `ENUM_CMDPACKET_DIE_MONSTER` — 击杀怪物 ✅

捕获的客户端 body 为 23B:第 2..4B=怪物 entityId;第 22B(偏移)=被动对象判别(1=passive object,普通怪=0);中间字节不解析。

**应答**:无直接回包;服务端推 NOTI 38(死亡掉落),清完推 NOTI 31。

发包字节(示例):`01 2A 21 00 00 00 C8 48 34 80 01 00 9A 7A 7A 7A 7A 7A 7A 7A 7A 7A 7A 7A 7A 7A 7A 7A 7A 7A 7A 7A 7A 7A`

## NOTI 38 `ENUM_NOTIPACKET_DIE_MONSTER` — 怪物死亡/掉落 ✅

全灭怪物(ALLDIE_MONSTER)批量复用本包。

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | uniqueId | 怪物实体 id |
| 2 | u8 | dropCount | 掉落数 n |
| 3 | 记录×n | drop | 每条 12B:{groundId u16, itemId u16, addInfo u32(多态:堆叠数/强化等级), durability u16, ownerUid u16} |
| — | 3B | trailer | {0 u8, championOrdinal sbyte, 0 u8} |

trailer 中间字节是冠军奖励序号,不是 owner:冠军/超冠军击杀带 1..11 触发通关奖励动画;Boss 与普通怪=255(无奖励)。

总长 = 6 + 12n。回包字节(示例):`00 26 18 00 00 00 CA D6 D2 D2 D6 91 CA C2 D6 D6 D6 D6 D6 D6 D6 D6 29 D6`

## NOTI 31 `ENUM_NOTIPACKET_ENABLE_CLEAR_DUNGEON` — 允许通关 ✅

无载荷。回包字节(示例):`00 1F 06 00 00 00`

## CMD 49 `ENUM_CMDPACKET_SET_PLAY_RESULT` — 提交成绩 ✅

**应答**:异步推 NOTI 34。

发包字节(示例):`01 31 0C 00 00 00 BE 23 C2 58 01 00` *

## NOTI 34 `ENUM_NOTIPACKET_PLAY_RESULT` — 结算回执 ✅

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u8 | uid | 队伍用户 id |
| 1 | 3B | flags | 三个计分标志(当前全 0) |
| 4 | u8 | reserved | 保留结果字节 |

总长 5B。回包字节(示例):`00 22 0B 00 00 00 D2 D6 D6 D6 D6`

## NOTI 35 `ENUM_NOTIPACKET_CLEAR_DUNGEON_REWARD` — 通关翻牌奖励 ✅

变长(~86+),结构:

| 宽度 | 字段 | 说明 |
|---:|---|---|
| u8 | uid | |
| u32×9 | exp 九元组 | wire[0]=base+party(客户端显示基值=wire[0]-wire[2]),依次 rank/party/avatar/event/blackDiamond(+0x130)/channel(+0x134)/mentor(+0x120)/creature 加成 |
| u8 | attachCount | =0(无即时背包奖励) |
| 4×记录 | freeCards | 免费卡四列,每列 {type u8(0=空,1=金币,2=金币+物品), (0 u16 + gold u32)[, itemId u16 + 0 u32]};全四列必须在场(计数按非空列递增) |
| u32 | goldCardCost | 金卡价格(未启用=0) |
| 4×u8 | paidColumns | 付费卡结果占位(0);实际内容由 SELECT_CARD 应答揭示 |
| 4×记录 | premiumCards | 高级卡列,仅本地队位有内容,结构同免费卡 |

回包字节(示例):`00 23 6C 00 00 00 D2 75 DA D6 D6 47 D6 D6 D6 D6 D6 D6 D6 D6 D6 D6 D6 D6 D6 D6 D6 D6 D6 D6 D6 D6 D6 D6 D6 D6 D6 D6 D6 D6 D6 D6 DE D6 D6 75 DA D6 D6 91 CA D6 D6 D6 D6 DE D6 D6 75 DA D6 D6 91 CA D6 D6 D6 D6 DE D6 D6 75 DA D6 D6 91 CA D6 D6 D6 D6 DE D6 D6 75 DA D6 D6 91 CA D6 D6 D6 D6 DE D6 D6 75 DA D6 D6 91 CA D6 D6 D6 D6 F4 9A D6 D6 D6 D6 D6 D6 D6 D6 D6 D6`

## CMD 73 `ENUM_CMDPACKET_CARD_SELECT_RIGHT_STATE` — 翻牌权查询 ✅

**应答**(`CreateCardSelectRightState`,9B):`[1] + 4×u16`(本队位=可翻牌?1:0,其余=0xFFFF)。

发包字节(示例):`01 49 0C 00 00 00 BE 23 C2 58 01 00` *

## CMD 74 `ENUM_CMDPACKET_SELECT_CARD` — 翻牌选牌 ✅

**应答**(`CreateSelectCardReply`,变长):`[1] + 4×{freeOwner u8, goldOwner u8, goldRewardType u8[, 金线(0 u16 + 0 u32)[, 金卡物品(itemId u16 + 0 u32)]]}`。
客户端把每列第一条记录当金币线,第二条记录才渲染装备;付费卡无金币也必须有零金币记录。

发包字节(示例):`01 4A 0C 00 00 00 BE 23 C2 58 01 00` *

## CMD 45 `ENUM_CMDPACKET_GIVEUP_GAME` — 放弃游戏 ✅

**应答**:异步推 NOTI 33。

发包字节(示例):`01 2D 0C 00 00 00 BE 23 C2 58 01 00` *

## NOTI 33 `ENUM_NOTIPACKET_FAIL_CLEAR_DUNGEON` — 通关失败 ✅

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u8 | stamina | 剩余疲劳 |

回包字节(示例):`00 21 07 00 00 00 97`

## CMD 127 `ENUM_CMDPACKET_BOSS_DIE_CHECK` — Boss 死亡校验 ✅

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | seq | |
| 2 | u16 | participant | 参与者 id |
| 4 | u16 | bossId | Boss id |
| 6 | u32 | skillChecksum | 技能校验和 |

**应答**:异步推 NOTI 127。

发包字节(示例):`01 7F 14 00 00 00 D0 0A C4 69 01 00 5A 7A 5B 7A 75 B0 FC 38`

## NOTI 127 `ENUM_NOTIPACKET_BOSS_DIE_CHECK` — Boss 死亡校验回执 ✅

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u8 | ok | 0=失败,1=成功 |
| ok=0 | u8 | errorCode | 失败码(总长 2B) |
| ok=1 | u8 + u16 | allReported, bossId | 是否全员上报 + Boss id(总长 4B) |

回包字节(示例):`00 7F 0A 00 00 00 D2 D2 F2 D6`

## CMD 148 `ENUM_CMDPACKET_ALLDIE_MONSTER` — 全灭怪物 ✅

**应答**:无直接回包;批量走 NOTI 38。

发包字节(示例):`01 94 0C 00 00 00 BE 23 C2 58 01 00` *

## CMD 142 `ENUM_CMDPACKET_BACK_2_VILLAGE` — 回城 ✅

**应答**:无直接回包;区域同步走 NOTI 23(格式见 [packet-detail-town-sync-60cn.md](packet-detail-town-sync-60cn.md)),随后疲劳走 NOTI 36。

发包字节(示例):`01 8E 0C 00 00 00 BE 23 C2 58 01 00` *

## CMD 72 `ENUM_CMDPACKET_SCORE_SCROLL_STATE` — 计分卷状态 ✅

**应答**:1B `[1]`。

发包字节(示例):`01 48 0C 00 00 00 BE 23 C2 58 01 00` *

## CMD 75 `ENUM_CMDPACKET_EPLP_COMMAND` — EPLP 命令 ✅

**应答**:3B `[1, state, option]`。

发包字节(示例):`01 4B 0C 00 00 00 BE 23 C2 58 01 00` *
