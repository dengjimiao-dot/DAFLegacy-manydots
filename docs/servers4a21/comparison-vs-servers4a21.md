# DAFLegacy (60A1) × ServerS4A21 共有协议实现对比

> - **对比对象**:`DAFLegacy`(服务端,目标客户端 60A1)× `ServerS4A21\Server\DfoServer`(服务端,目标客户端 S4A21,枚举表 NOTI 1219 条)。
> - **对齐方式**:两边客户端版本不同、协议号有漂移,统一按**枚举名**对齐后再比。
> - **方法**:逐协议阅读双方处理代码(DAFLegacy `Server/DAFLegacy.Server/Protocol/GameProtocolEngine.cs` + `Network/EntranceService.cs`;S4A21 `Network/Protocol/GameProtocolHandler.cs` 分发 + `Network/Handlers|Parsers|Builders/*`),比对请求解析、应答/推送布局与行为逻辑。关键断言(如 SELL 10B、MOVE 11B)已做抽样核实。
> - **定性图例**:【一致】= 载荷字段级相同;【版本性差异】= 因客户端版本演进导致的合理布局/语义变化;【实现分歧】= 同语义但逻辑/做法实质不同(互参价值最高);【DAFLegacy 简化实现】= DAFLegacy 为 60A1 做的简化。
> - 代码位置(file:line)为对比时点状态,仅供参考。
> - 配套文档:60A1 侧枚举全表见 `client-packet-enums-60cn.md`;S4A21 侧各协议实现详参(帧结构/逐 CMD 请求应答布局/行为)见 `servers4a21-00-overview.md` 及其分册。

## 0. 总结论

1. 共有协议中**约十分之一字段级一致**(全部是小包,见 §2);副本/物品核心包全部因版本换代不兼容(§5、§4)。
2. 差异分三层:**协议号漂移**(同名不同号)、**版本性布局差异**(不可也没必要跨版本移植)、**实现分歧**(同语义不同做法,是拿成熟实现校验 DAFLegacy 的重点)。
3. 最值得优先处理的实现分歧:CMD 130 黑名单错配、DELETE_CHARACTER 成套行为、错误码粒度、邮件手续费/分页等(§7)。

## 1. 前提性差异(影响所有包)

### 1.1 帧结构与 seq 位置

- DAFLegacy:2 字节序列号在 body 开头,业务载荷从 `body[2]` 开始(`GameProtocolEngine.cs:236` 等)。
- S4A21:seq 在 14B 帧头 `GamePacketHeader` 内(cmd u8 + type u16 + length u32 + checksum u32 + seq u16 + extra u8,合计 14B,`Network/Core/Packet.cs:67-79`),handler 拿到的 body 从第一个载荷字段开始。
- 因此同一字段在两边的 body 偏移系统性相差 2 字节。下文各协议的偏移均已按各自 body 折算。
- NOTI 帧:两边均 type=0 推送、type=1 命令应答(DAFLegacy `GameProtocolEngine.cs:244/308`;S4A21 `GamePacketEnvelopeBuilder.Build(0x00/0x01, ...)`)。
- 变长字符串(dstr)两边同格式:u32 长度 + 原始字节;编码两边都是 **GBK/CP936**(DAFLegacy `EntranceService.cs:14130` Cp936Lossy;S4A21 `Infrastructure/ClientTextEncoding.cs:6-11`)。

### 1.2 CMD 协议号漂移(同名不同号)

S4A21 删除了 60A1 的 22/23(RENT_AVATAR/EXTEND_AVATAR),其后 CMD 整体 -2;更后区段另有个别漂移。常见对照:

| 协议 | 60A1 | A21 |
|---|---:|---:|
| SELL_ITEM | 24 | 22 |
| REPAIR_EQUIPMENT | 25 | 23 |
| SET_ITEMTRADE_STATE | 26 | 24 |
| COMPOUND_ITEM | 27 | 25 |
| DISJOINT_ITEM | 28 | 26 |
| USE_LOTTERY_ITEM | 29 | 27 |
| CHANGE_SKILLSLOT / BUY_SKILL | 30 / 31 | 28 / 29 |
| ACCEPT/GIVEUP/TRIGGER/FINISH_QUEST | 33-36 | 31-34 |
| SET_USER_POSITION / SET_USER_AREA | 37 / 38 | 35 / 36 |
| USE_SKILL | 41 | 38 |
| DIE_CHARACTER / USE_COIN | 43 / 44 | 40 / 41 |
| GIVEUP_GAME | 45 | 42 |
| GET_ITEM | 46 | 43 |
| USE_STACKABLE | 47 | 44 |
| MOVE_MAP / SET_PLAY_RESULT / DROP_ITEM | 48/49/50 | 45/46/47 |
| DECREASE_DURABILITY | 51 | 48 |
| BUY_CERASHOP_ITEM / GEN_CERATICKET | 67 / 68 | 64 / 65 |
| GUILD/CALL_MEMER_LIST 段 | 82 | 67/79 |
| UPGRADE_ITEM / RESET_ITEM_ATTR | 83 / 84 | 80 / 81 |
| COMPOUND_AVATAR | 102 | 99 |
| RENAME/RESPONSE/HATCH_CREATURE | 103/104/105 | 100/101/102 |
| MAILBOX_SEND/EXTRACT/OPEN | 97/98/99 | 94/95/96 |
| CHANGE_LETTER_STAT | 144 | 134 |
| CREATURE_SEND/SCRIPT_MESSAGE | 124 / 132 | 114 / 122 |
| BACK_2_VILLAGE | 142 | 132 |
| BOSS_DIE_CHECK | 127 | 117 |
| SCORE_SCROLL_STATE / SELECT_CARD | 72 / 74 | 69 / 71 |
| CHANGE_TUTORIAL_FLAG | 153 | 143 |

- CMD 0-21 与 NOTI 0-53 低段两边同名同值(CMD 15 在 60A1 客户端名为 `START_GAME`,A21 名为 `ENTER_SELECT_DUNGEON`,同一语义;NOTI 27 同理)。
- NOTI 侧漂移点:宠物聊天 NOTI 126/131 → A21 114/119;EVENT_INFO 120 → A21 108;其余 0-144 段基本同名同值(邮件 97/98/99、宠物 100-107、技能 19、任务 21 均未漂移)。

### 1.3 字段宽度换代

物品 ID 全线 **u16 → i32**,是所有"物品条目类"载荷差异的共同根源:60A1 物品条目 13B 紧凑式(slot u16, itemId u16, count u32, state u8, dur u16, seal u8,`GameProtocolEngine.cs:16-22`);A21 普通条目 101B / 时装 131B 完整 ItemCore(附魔/徽章/随机选项/编年史/守护珠等,`Network/Builders/Init/ItemListProtocolWriter.cs:159-193`)。dungeonId、怪物 code 等同理 u16→u32。

## 2. 字段级一致的包(可直接互认)

| 协议 | 共同布局 |
|---|---|
| CMD 3 EXIT | ack `[1]` |
| CMD 19 MOVE_ITEMSPACE 成功包 | 11B `[1, srcList u8, srcSlot i16, movedCount u32, dstList u8, dstSlot i16]`(DFL `GameProtocolEngine.cs:1650`;A21 `MoveItemSpaceAckBuilder.cs:19-27`) |
| CMD 20 SORT_ITEM ack | `[1, listType]` |
| CMD 24 SELL_ITEM 回包 | 10B `[1, gold u32, listType u8, slot u16, soldCount u16]`(DFL `:1803`;A21 `SellItemBuilder.cs:11-18`) |
| CMD 51/48 DECREASE_DURABILITY ack | `[1, slot u8]` |
| NOTI 22 USER_POSITION | 9B `uid, x, y, dir, motion`(两边默认 motion=100) |
| NOTI 23 USER_AREA / NOTI 24 AREA_USERS | 9B / `town, area, count, n×7B` 同构 |
| CMD 44/41 USE_COIN 成功 | 先 NOTI 32(活态)再 `[1, uid u16]` |
| CMD 75/72 EPLP_COMMAND ack | `[1, state, option]` |
| NOTI 100/107 宠物死亡/复活 | `uid u16` |
| CMD 5/7 CREATE/RETURN_CHARACTER 成功 | `[1]` + NOTI 2 名册刷新 |
| CMD 144/134 CHANGE_LETTER_STAT | 请求 `mailId u32 + action u16`、ack `[1, id, action]` 同形 |
| 错误码(部分)| RECOVER_STAMINA 4/18/22、邮件 4/7/10/17/22/90 语义对齐 |

## 3. 域一:连接 / 登录 / 角色 / 疲劳

### 3.1 CMD 0 CHECK_CONNECTION ↔ NOTI 0(心跳)
- DAFLegacy:连接后主动推 16B 挑战 NOTI 0(`EntranceService.cs:2211`),客户端 CMD 0 回显,服务端校验 16B 后回 **NOTI 1 CHANNELINFO**(`GameProtocolEngine.cs:2967`,`EntranceService.cs:12236`)。
- S4A21:**无此环节**——分发处 CMD 0 是空块(`GameProtocolHandler.cs:334-337`),连接即推 NOTI 1(`LoginHandler.cs:41-45`)。
- 结论:【版本性差异】(A21 客户端无挑战回显)。

### 3.2 NOTI 1 CHANNELINFO(频道信息)
- DAFLegacy:`dstr 名称, u32 channelType, u32 result, u8 serverId, u8 channelNumber, u32 seed, u32 hostCount + dstr×N, u32 端口1, u32 端口2`(`GameProtocolEngine.cs:3315`)。
- A21:`u8 1, dstr 频道名, u32 0, u32 0, u8 serverIndex, u8 channelId, u8 0, u32 epoch, u32 1, dstr IP, u32 udpPort1, u32 udpPort2, u32 0, u8'0', u8'0', u32 CMD枚举数, u32 NOTI枚举数, u32 0`(`Builders/Login/LoginPacketBuilder.cs:7-33`)。
- 差异:DFL 有 host 列表;A21 单 IP + **尾部 CMD/NOTI 枚举计数**(客户端协议表校验,DFL 没有)。【版本性差异】

### 3.3 CMD 1 LOGIN ↔ 登录应答
- 请求两边同为 `dstr 账号 + dstr 密码`(DFL `EntranceService.cs:13955`;A21 `Parsers/LoginRequestParser.cs:20-61`)。
- DAFLegacy 成功回 5B `[1, flag×3, clientState=0]`(`GameProtocolEngine.cs:421`);A21 回约 50B(环境/端口/IP/24B 0,`LoginPacketBuilder.cs:35-53`),且有 00AD+2×00C5 门控状态机(`LoginHandler.cs:99-143`)。
- 行为:**A21 不验密码、账号不存在自动建号**(`LoginHandler.cs:81-87`);DAFLegacy 校验密码、失败回 `[0,2]`。A21 自动建号属安全问题,不应照抄。
- 结论:请求【一致】;应答/流程【版本性差异】+【实现分歧】。

### 3.4 CMD 8 GET_USERINFO
- DAFLegacy:不解析请求,恒回 NOTI 2 名册(`EntranceService.cs:2543`)。
- A21:解析 `[uid u16][mode u8]`,mode 0/1/3/5=查看他人(带请求者鉴权、同频道校验、代际防竞态,`CharacterSelectHandler.cs:600-729`),mode 2 回名册并追加 CMD 0x0286 与成长胶囊包。
- 结论:【版本性差异】(mode 语义为 A21 新增);A21 的鉴权/竞态防护可借鉴。

### 3.5 CMD 4 SELECT_CHARACTER
- 请求:DFL slot=u8(`EntranceService.cs:2670`);A21 slot=u16 且**越界静默回落 slot 0**(`CharacterSessionLifecycleCoordinator.cs:749-777`,宽松实现)。
- ACK 结构完全不同:
  - DFL:`[result, charId u32, serverId u16, usedFatigue u16, maxFatigue u16, premiumFatigue u16, 会员疲劳 N×(type+remainSec), cash u32, 3×任务占位, 完成任务位图组, townId u8, tutorial u32]`(`GameProtocolEngine.cs:1136-1230`)。
  - A21:`[result, 注册时间 u32, 建角时间 u32, uniqueId, 疲劳 0/188/0, 会员疲劳(type+8B 到期), cera, 30×任务槽, 4×QUEST_NOTIFY 槽, charSlot, 教程 flag, fatigueBattery, grownUpBuff, tradePunish, …22B 0]`(`Builders/Characters/SelectCharacterAckBodyBuilder.cs:36-141`)。
- 两边都有顶号处理;A21 另有频道准入拒绝流(拒绝 ack+公告+换频道包)。
- 结论:【版本性差异】为主;slot 回落属【实现分歧】。选角 ACK 是对齐工作量最大的单包。

### 3.6 CMD 5 CREATE_CHARACTER
- 请求:DFL `[job u8, dstr name]`(名 1-30B);A21 `[job u8, nameLen u32, name]`(job≤13,名 2-18B)。
- 成功两边都 `[1]` + 补发 NOTI 2 名册。**错误码分歧**:DFL 统一 `[0,2]`;A21 细分 0x04/0x12/24(重名)/名字校验码,并有槽位策略、重名预检、失败回滚(`CharacterSelectHandler.cs:954-1059`)。
- 结论:成功路径【一致】;错误粒度【实现分歧】(DAFLegacy 简化)。

### 3.7 CMD 6 DELETE_CHARACTER(成套不兼容,最危险)
- 请求:DFL 只要 slot u8;A21 是 `[slot u16][名 dstr]` 且**强校验名字与槽位角色一致**(不符回 0x15,`CharacterSelectHandler.cs:1069-1098`)。
- ACK:DFL `[1, slot]` 2B;A21 `[1][flag=0][slot u16]` 4B(注释:body[0]≠1 时客户端把 body[1] 当错误码崩溃,`:1129-1135`)。
- 行为:DFL 删后**补发 NOTI 2 名册**(`EntranceService.cs:12194`);A21 **明确不补发**(注释:客户端本地自管理,补发会崩溃/对账冲突,`:1139-1141`),且原子前移后续槽位、佣兵远征中禁删(0x28)、清理好友关系。
- 结论:【版本性差异】成套硬约束;DFL 的"2B ACK+补名册"不可单移植。

### 3.8 CMD 7 RETURN_SELECT_CHARACTER
- 请求两边无载荷;都是 `[1]` ack + NOTI 2 名册。【一致】
- 行为差异:DFL 有"教程未完成禁止返回"(error 4);A21 有守护者卡组清空推送、副本局结束、库存保存失败回 `[0,19]` 等。【实现分歧】(各自玩法栈)

### 3.9 CMD 3 EXIT
- 两边都是 1B 成功 ack、无副作用(持久化走断连清理)。【一致】

### 3.10 CMD 9 RECOVER_STAMINA ↔ NOTI 4 STAMINA
- 错误码语义两边对齐:4=状态非法、18=未虚弱、22=金币不足。
- 成功路径完全不同:DFL 5B ack `[1, 剩余金币 u32]` + NOTI 4(1B 虚弱恢复值)(`GameProtocolEngine.cs:1958-2004`);A21 **无 CMD ack、全工程不发 NOTI 4**,只发 NOTI 33 `[100]` + 金币刷新,错误包 3B `[0, code, 0]`(`StaminaHandler.cs:133-171`)。
- 费用模型:DFL 只按等级取价;A21 = 基价×(100-当前疲劳)/90 按档计价,另有恶魔契约免费恢复。
- 结论:错误码【一致】;成功应答/费用【版本性差异】+【DAFLegacy 简化实现】。

### 3.11 NOTI 2 USERINFO 各子类型(全部重写)
- DFL 实现 subtype 0/1/2(`GameProtocolEngine.cs:2985-3292`);A21 另有 subtype 3/6(查看他人/进号通知)。
- subtype2(名册):DFL 头 `[2][count u16]`,角色条目含 6B 紧凑外观;A21 头多 7 个字段,外观条目 20B(slot,item i32,expLen,exp,state,linkItem,enchant,flag),尾 30B(cloneTitle 等)。觉醒 60A1 打包进 growth 位,A21 拆独立字段。
- subtype0(城镇实体):A21 在 uid 前多 **38B 固定头**,尾部有 Stamina u8(虚弱值改存这里)、64B 常量 blob;DFL 是紧凑布局+宠物 item u16。
- subtype1(详情):DFL `[1][1][uid][exp][len=81][81B 战斗块][装备 5B 条目×n][技能 (id u8,lv u8)×n]`;A21 前缀加成长胶囊/名誉等级/名誉经验/光环皮肤,战斗块 88B,装备为完整 ItemCore,技能 ID **2B×两页**。
- 结论:【版本性差异】——同名 NOTI 2 下所有子类型互不兼容,且 A21 的 NOTI 4 语义被并入 subtype0 尾部 Stamina 字段。

### 3.12 NOTI 3 USER_STATE
- DFL:`[uid u16][state u8]` 固定 3B 无前缀(`GameProtocolEngine.cs:2062`);A21:`[count u8] × ([uid][state])` 列表(`Builders/Dungeon/EnterSelectDungeonStateBuilder.cs:12-26`)。
- state 语义一致(0=空闲/1=选副本)。【版本性差异】(布局)、语义【一致】

### 域一 Top 差异
1. DELETE_CHARACTER 成套不兼容(请求带名+4B ACK+禁补名册)。
2. SELECT_CHARACTER ACK 两套结构,选角根基,对齐工作量最大。
3. USERINFO 各子类型全部重写;虚弱值迁移到 subtype0 尾部。
4. RECOVER_STAMINA 成功路径无 CMD ack、无 NOTI 4。
5. 握手模型不同(无 NOTI 0 挑战);CHANNELINFO 尾部枚举计数必须补。
6. 建/删角色错误码粒度(0x02 一律 vs A21 细分)。
7. GET_USERINFO 的 mode/鉴权框架 DFL 完全没有。
8. A21 登录不验密自动建号(安全取舍,勿照抄)。
9. USER_STATE 多 count 前缀;CMD4 slot u16 越界回落 0(偏移错一字节即崩)。
10. 频道准入/顶号编排(A21 多频道运营能力,DFL 单频道顶号)。

## 4. 域二:物品 / 背包 / 商店 / 货币

### 4.1 CMD 18 DELETE_ITEM
- 请求:DFL 条目 10B `op u16, slot u16, itemId u16, count u32`,仅 listType=0(`EntranceService.cs:13673`);A21 条目 12B(itemId i32),双格式兼容。
- 应答:DFL `[1, listType, count, {slot, consumed u32}×n]` / 失败 `[0, 0x11, listType]`;A21 每条目一包 `[1, listType, 1, slot, applied i32, 0]` / 失败 `[0, 0x17, listType]`。
- 行为:DFL 整包原子事务(任一失败全拒);A21 逐条提交、失败 continue,并联动成就/任务/副本奖励策略。
- 结论:【版本性差异】(条目加宽、错误码)+【实现分歧】(事务模型)。

### 4.2 CMD 19 MOVE_ITEMSPACE
- 请求字段序列两边一致(srcList/srcSlot/[srcIV]/count/dstList/dstSlot/[dstIV])。
- **成功回包完全一致 11B**;失败路径分歧:DFL 回"成功格式但 movedCount=0 的 no-op 包"+全量刷新(`EntranceService.cs:1964-1989`);A21 显式 4B 错误包 `[0, code, srcList, dstList]`(code 0x02/0x04/0xB4)。
- 结论:成功【一致】;失败【实现分歧】。

### 4.3 CMD 20 SORT_ITEM
- 请求:DFL 1B(itemSpace 0/2/7);A21 3B(listType, category, condition,新增分类/条件)。
- ack 两边 `[1, listType]`。DFL 有失败错误包;A21 失败静默。A21 另有整理锁 0x02CA/0x02CB(60A1 无)。
- 结论:ack【一致】;其余【版本性差异】。

### 4.4 CMD 21 BUY_ITEM
- 请求:DFL 自适应 itemId u16/i32;A21 固定 `itemId i32, count i32, shopId i32, npcId i32`(新增 shop/npc)。
- 应答:DFL 25B `[1, gold, 胜点, aux, cera, slot u16, itemId u16, count u32]`(四钱包);A21 四钱包槽保留但 2/4 号语义变为 SP/coin,物品段扩为 101B 条目+限购表(`Builders/Inventory/BuyItemAckBuilder.cs:17-77`)。
- 行为:DFL 金币不足 error 4、背包满转邮箱溢出邮件;A21 走 NPC 限购/材料币道具/购买限次(0x02CC)。
- 结论:【版本性差异】+ 行为分歧。

### 4.5 CMD 24/22 SELL_ITEM
- 应答**完全一致 10B**(`GameProtocolEngine.cs:1803` × `SellItemBuilder.cs:11-18`)。
- 行为:A21 code=22 时补发 NOTI 51 MONEY_FULL;DFL 按耐久算售价、checked 加币。
- 结论:应答【一致】;行为【实现分歧】。

### 4.6 CMD 25/23 REPAIR_EQUIPMENT
- 请求:DFL `itemSpace(0|3), slot u16(0xFFFF 全修)`;A21 增加货柜(2)、自动修(魔王契约)、快速修 ×1.5。
- 应答:DFL 8B;A21 9B(尾部多一个 short,台服逆向注释)。全修:DFL 逐个修回一次;A21 回 slot=0xFFFF 让客户端本地拉满。
- 结论:【版本性差异】。

### 4.7 CMD 26/24 SET_ITEMTRADE_STATE —— 同号不同义
- DFL:实现为"把交易锁定的物品卖给 NPC"(算价、加币、发 SELL 回包,`EntranceService.cs:9483-9568`),没有玩家交易。
- A21:state 驱动**双人面对面交易状态机**,配合 REQUEST/RESPONSE_PEER 的 `body[2]==1` 识别、NOTI 交易包、完成后补发 NOTI 13(`ItemTradeHandler.cs:158-186`)。
- 结论:【实现分歧】(不可互相移植)。

### 4.8 CMD 27/25 COMPOUND_ITEM
- 请求:DFL 8B 含魔数校验;A21 7B(itemId 扩 i32)。
- 应答:DFL 产物条目 13B;A21 约 40B 完整物品核心+12B 尾(`CompoundItemAckBuilder.cs:41-77`)。
- 行为:DFL 有**序列号重放缓存**(compoundReplay)并强制 NOTI 13 全量("DF2008 不可靠应用增量");A21 用 NOTI 14 增量。
- 结论:【版本性差异】+【实现分歧】(重放防护)。

### 4.9 CMD 28/26 DISJOINT_ITEM
- 请求同 5B 前缀但**语义错位**:DFL +0 是被分解物槽;A21 +0 是目标装备槽、+3 才是分解物槽,另有 4B 上下文。
- 应答:两边同构 10B/条,itemId 16→32 位。
- 结论:【版本性差异】(语义重排)。

### 4.10 CMD 29/27 USE_LOTTERY_ITEM
- A21 引入两阶段(phase 0/1)开启与会话协调;DFL 一次性 slot u16。
- 应答:DFL 14B 固定;A21 分型长包(普通 ≈35B / 时装 126B / 金币变体 / 13B 错误)。
- 结论:【版本性差异】。

### 4.11 CMD 47/44 USE_STACKABLE
- 请求前缀一致(slot, listType);A21 追加 instanceValue i32 + itemCode i32 用于过期/篡改校验。
- 应答:DFL 4B/错误 3B;A21 11B(错误体也带全字段)。
- 行为:A21 是超大分发器(过期拒绝、使用上限、金库/货柜券、符文、史诗药水 buff、任务校准);DFL 支持 listType 7 宠物喂食饥饿结算。
- 结论:前缀【一致】+【版本性差异】。

### 4.12 CMD 51/48 DECREASE_DURABILITY
- 应答完全一致 `[1, slot]`。行为:DFL 硬编码装备槽 9..22 且仅副本内、固定 -1、整包持久化;A21 走事务服务,"zero_durability" 原因也回成功包。【一致】+【实现分歧】

### 4.13 CMD 46/43 GET_ITEM ↔ NOTI 39
- DFL:无成功 ack(直接 NOTI 39),拒绝发 `[0, 0x15]` 静默回滚包(客户端 0x00414198 回滚);A21 **先发 1B ack** `[1]`,背包满 `[0, 4]`。
- NOTI 39 金币:DFL 24B(4×{gold,调整字节},队尾分配);A21 固定 117B 模板(教程抓包,只回写三处)。
- NOTI 39 物品:DFL 12B;A21 17B(加 flag=1 与保留尾)。
- 结论:【版本性差异】。

### 4.14 CMD 50/47 DROP_ITEM ↔ NOTI 40
- CMD 层两边同构(`[1, listType, slot, count]`);x/y 符号性不同。
- NOTI 40:DFL 17B 紧凑式(userId,x,y,groundId,itemId u16,attr,addInfo,dur);A21 = 头 + **101B 完整 ItemCore** + owner u16。
- 结论:CMD【一致(近似)】;NOTI【版本性差异】。

### 4.15 CMD 83/80 UPGRADE_ITEM
- A21 强化系统整体改版:请求 ≥16B(method 枚举/高级强化券/物品名),回包含 oldLevel/ticketSlot,破碎(code 3)追加碎片奖励列表;另有世界公告 NOTI 0x56。
- DFL:8B 请求、9-11B 回包、一次一档、消耗炉岩芯。
- 结论:【版本性差异】(DFL 为 60A1 简化版强化)。

### 4.16 CMD 84/81 RESET_ITEM_ATTR
- itemId 扩 i32;A21 按材料 ID==14(蜜蜡)分流重新封印分支(60A1 无);回包字段重排(9B vs 10B)。
- 结论:【版本性差异】。

### 4.17 CMD 102/99 COMPOUND_AVATAR
- 布局完全不同:DFL 17B marker 定界式;A21 ≥22B(consumeSlot/reqItemId i32/abilityNo)。应答 A21 含 2×126B avatar 条目。另有 60A1 没有的 8 件套克隆合成(BIND_PLUS)。
- 结论:【版本性差异】(重写)。

### 4.18 CMD 67/64 BUY_CERASHOP_ITEM ↔ NOTI 53 CERA
- 请求:DFL 条目 7B×n(精确总长校验)+ **礼物模式**;A21 条目 15B 步进+优惠券+支付方式+自选选项块(礼物未实现)。
- 应答:两边都靠 body 中 commodityNo/category 的特殊值防客户端崩溃(A21 category=-1 全类搜索;DFL 中间 commodityNo 作映射键);错误码 A21 全表(4/10/11/…/113/162…)。
- NOTI 53:DFL 5B `[success, cera u32]`;A21 13B 三币种(点券/代币券/欢乐代币券)。
- 结论:【版本性差异】+【实现分歧】(gift vs coupon)。

### 4.19 CMD 68/65 GEN_CERATICKET
- 两边都是防客户端卡死的桩实现,但票据格式不同(DFL 面向 Web 计费 Guid;A21 模仿台服 `1234<unix>00000`)。【实现分歧】

### 4.20 NOTI 13/14 ITEM_LIST / UPDATE_ITEM_LIST
- 头部结构一致(listType[+param]+count);**条目 13B → 101B/131B**(§1.3)。
- Main 列表虚拟槽:DFL slot0=金币/slot2=胜点;A21 slot0-2 货币、slot354-359 魔盒、360-364 灵魂。
- 空槽:DFL 不出现;A21 UPDATE 显式写 `slot + itemId=-1`。
- 结论:头部【一致】;条目【版本性差异】——这是所有物品类回包差异的根源。

### 域二 Top 差异
1. 物品条目整体换代 13B→101/131B(itemId u16→i32 贯穿全部)。
2. SET_ITEMTRADE_STATE 同号不同义(卖 NPC vs 双人交易)。
3. NOTI 39/40 拾取掉落布局(24B→117B 模板;17B→嵌 101B 条目;A21 先发 1B ack)。
4. UPGRADE_ITEM 改版(method/券/破碎奖励)。
5. USE_LOTTERY_ITEM 两阶段化。
6. BUY_ITEM 四钱包保留但语义漂移、物品段换代。
7. CERA 商城条目/错误码/三币种;DFL 独有 gift。
8. 错误应答风格(no-op 成功包 vs 显式错误包)。
9. DELETE_ITEM 原子事务 vs 逐条提交。
10. 持久化模型:整包写库 vs 在线租约+脏保存+任务/成就钩子。

## 5. 域三:副本 / 城镇 / 战斗

### 5.1 CMD 15(START_GAME/ENTER_SELECT_DUNGEON)↔ NOTI 27
- 请求:DFL 无业务载荷(进选择界面即触发);A21 带 `u32 dungeonId`。
- NOTI 27:DFL `u8 地狱票缺失 + u8 count + n×u8 槽`(`GameProtocolEngine.cs:2308-2331`,注释明确勿用新版布局);A21 40B+ 结构(blocked u16 槽列表+userCount+固定尾)。
- 疲劳不足拒绝:DFL 在 CMD 15 阶段检查,ack `0x16+成员序号`;A21 统一 3B `DungeonAdmissionReject`(0x00,0x16,slot)且此阶段不查疲劳。
- DFL 另有 30 秒选择倒计时自动回城、黑钻疲劳 156+32。
- 结论:【版本性差异】+【实现分歧】(A21 组队 cohort 状态机复杂得多)。

### 5.2 CMD 16 SELECT_DUNGEON ↔ NOTI 28 / NOTI 29
- 请求:DFL `u16 dungeonId, difficulty, option` 6B;A21 `u32 id, difficulty, 地狱 flag×2, u16 哨兵` ≥9B。
- NOTI 28:DFL 7B;A21 ~32B+(minimap 组、固定尾、id u32)。
- NOTI 29(怪物快照):DFL 每怪 9B(mapList 索引+u16 怪物 id,客户端查表);A21 每怪 19B(**直接下发 PVF 怪物 code i32**+packetIndex+全局序号),另建筑掉落 19B/个、雾/骑乘/分层字段。重访房间:DFL 7B 缓存包;A21 完整骨架 0 怪。
- 结论:【版本性差异】。

### 5.3 CMD 40 FINISH_LOADING ↔ NOTI 30 / NOTI 136
- DFL:恒回 `[1]` + 空 NOTI 30,NOTI 136 空 body。
- A21:副本内走**房间级加载栅栏**(全员 ready 或 45s 超时才逐个发 NOTI 30,body 5B);城镇分支才回 CMD ack。ENTER_GAMEWORLD_COMPLETE(0x7C)固定 8B 且只出现在选角初始化序列,不在过图链。
- 结论:【版本性差异】+【实现分歧】(多人加载屏障)。

### 5.4 CMD 48 MOVE_MAP
- 请求:DFL 2B 坐标;A21 **64B 固定**(路径/分层/trapBits/8 人清图值与耗时/计时 token)。
- 行为:DFL 成功先回 CMD ack 再发 START_MAP、首访扣疲劳、地面物不跨房;A21 **不发 ack**、队长移动自动带全队换图、死亡/已通关忽略。
- 结论:【版本性差异】+【实现分歧】。

### 5.5 CMD 37/35 SET_USER_POSITION ↔ NOTI 22
- 请求字段一致(仅 seq 位置差);**NOTI 22 布局一致 9B**(默认 motion 都=100)。
- 行为:DFL 首次 seed 后不再回发(防回弹);A21 回显给自己+按需广播。【一致】+【实现分歧】

### 5.6 CMD 38/36 SET_USER_AREA ↔ NOTI 23/24
- NOTI 23/24 **布局一致**(9B / 4+7n);state 字节 DFL 恒 1,A21 到达=0。
- 行为:A21 有旧区域离开投影(伪区域 0xFF)、"已在场玩家不收 0x18"等同屏规则、频道准入;DFL 固定序列 USER_AREA→AREA_USERS→疲劳→外观→NOTI33→NOTI136。
- 结论:布局【一致】;行为【实现分歧】。

### 5.7 CMD 42/39 DIE_MONSTER ↔ NOTI 38;CMD 148 ALLDIE_MONSTER
- 请求:DFL `u16 entityId`+被动判定 body[22];A21 `localIndex+userId`,被动/抓捕标志在变长攻击记录之后(偏移动态)。
- NOTI 38:DFL 掉落条 12B(ground,item u16,addInfo,dur,owner)+ 3B 尾含**冠军奖励序号 1-11/255**;A21 掉落条 **48B**(itemId i32、强化、品级、掉落时间戳、owner)+ 固定尾 `[0,0,0xFF,0]`,冠军奖励移到 NOTI 35 分项。
- 行为:DFL 每杀即时发 NOTI 37(15B)、掉落由目录掷点;A21 每杀发 83B NOTI 37、经验/掉落进台账、房间清空驱动结算。A21 **未注册 ALLDIE_MONSTER(0x8A)**(DFL 仅记日志)。
- 结论:【版本性差异】+【实现分歧】。

### 5.8 CMD 43/40 DIE_CHARACTER ↔ NOTI 32
- NOTI 32:DFL 3B `uid+alive`;A21 4B(多 1 个 flag 字节)。
- 行为:DFL 10 秒死亡超时、重复死亡 error 19;A21 团灭计时器、四种死亡流分支、广播副本全体名册(DFL 只发给自己)。
- 结论:【版本性差异】+【实现分歧】。

### 5.9 CMD 44/41 USE_COIN
- 成功应答**完全一致**(先 NOTI 32 再 `[1, uid]`);A21 支持复活同实例任意参与者+金币契约扣减;DFL 只查自己背包固定槽。
- 结论:应答【一致】;目标选择【实现分歧】。

### 5.10 CMD 45/42 GIVEUP_GAME ↔ NOTI 33;CMD 142/132 BACK_2_VILLAGE
- DFL:GIVEUP **不发 ack**,直接回城链(USER_STATE(0)→USER_AREA→AREA_USERS→疲劳→NOTI 33 复用(1B 满体力值)→NOTI 136);教程中 error 4。
- A21:回城**先发 CMD ack [1]**(注明在 USER_STATE 之前),无 NOTI 33,无回城疲劳惩罚;0x2A 与 0x84 服务端等价。
- 结论:【版本性差异】+【实现分歧】。

### 5.11 CMD 49/46 SET_PLAY_RESULT ↔ NOTI 34
- 请求:DFL 24B 计分布并服务端重算评级(阈值 105/95/85/70/55/45/40/35);A21 只取 body[10] 的 rank 点,权威结算靠服务端台账。
- NOTI 34:DFL 5B;A21 15B(clearTime/timeBonus/newBest 等)。
- 结论:【版本性差异】。

### 5.12 NOTI 35 CLEAR_DUNGEON_REWARD
- DFL(~86B+):经验九元组(base/队伍/时装/活动/黑钻/频道/师徒/宠物)+ 4 列卡(免费/装机)+ 金卡价。
- A21(300B+):109B 基础块(23 个固定奖励槽:秘药/黑钻/宠物/冒险团/成长契约/杀怪契约/装备加成…)+ 16B 计分四元组(保留/冠军/超级冠军/领主)+ 对象经验列表 + 卡区(1-2 免费条目+7 个固定卡座)。
- 行为:A21 有 2s 布局/4s 自动翻牌定时器。
- 结论:【版本性差异】(两边差异最大的包,互不兼容)。

### 5.13 CMD 72/69 计分卷、73/70 翻牌权、74/71 SELECT_CARD
- 计分卷:两边都回一次性 `[1]`;A21 紧随 0x46 布局包且可服务器主动推。
- 翻牌权:DFL `1+4×u16`(自己槽 1/0,他人 0xFFFF);A21 `1+8×u16`(8 队位)。
- SELECT_CARD:DFL 请求 `cardType+index`(4 列,金卡扣费);A21 `type+index`(≤3,8 槽),应答重排,含服务器 4s 自动翻免费卡与并发门锁、失败回滚重选。
- 结论:【版本性差异】(4→8 槽)+【实现分歧】(自动翻牌)。

### 5.14 CMD 75/72 EPLP_COMMAND
- 请求 `state+option`(仅 seq 位置差)、应答 `[1, state, option]` **完全一致**。
- 行为:DFL state=1 时兜底自动选免费卡再回城;A21 支持连锁副本继续挑战、死亡塔/血色分流、授权副本抑制普通卡流。
- 结论:ack【一致】;后续【实现分歧】。

### 5.15 CMD 127/117 BOSS_DIE_CHECK ↔ NOTI 127
- 请求:DFL 10B(participant,bossId,skillChecksum,校验 participant==自己);A21 4B(uid,bossSeq)。
- 应答:DFL 必答 NOTI 127(`[0, err]` / `[1, allReported, bossId]`)并驱动级联清场;A21 普通副本**不回 0x73**(BOSS 确认走 DIE_MONSTER+NOTI 31 通路),仅授权副本回 `[1,1,seq]`。
- 结论:【版本性差异】(语义收窄)。

### 5.16 CMD 153/143 CHANGE_TUTORIAL_FLAG
- DFL:单模块完成判定(body[2..6]==31)+完成回城;无奖励。
- A21:通用旗标系统(0-77),**ACK 携带奖励发放记录** `[1, count, n×(slot,itemId,count)]`,flag 31 触发跳过并回城。
- 结论:【版本性差异】(整套协议语义不同)。

### 5.17 CMD 41/38 USE_SKILL
- DFL:回 `[1]` ack+日志(材料由 CMD 18 权威扣减);**A21 未注册该 opcode**(枚举存在但无 handler)。
- 结论:【S4A21 未实现】。

### 5.18 NOTI 5 DUNGEON_PERMISSION
- DFL:2+3n(dungeonId u16),增量单条推送解锁;A21:2+5n(id i32),全量列表。
- 结论:【版本性差异】。

### 5.19 NOTI 36 FATIGUE / NOTI 37 EXP
- FATIGUE:DFL 6B(used/max/premium)、逐房扣减;**A21 从不发送**(无疲劳扣减逻辑,仅保留 0x16 拒绝码)。
- EXP:DFL 15B(level,累计经验,extraA,extraB,SP);A21 83B(队伍/SP/TP 双页/PvP/14+ 加成分项)。
- 结论:疲劳属【DAFLegacy 独有】;EXP【版本性差异】。

### 域三 Top 差异
1. NOTI 35 完全重排(结算 UI 核心包)。
2. NOTI 29 怪物记录体系(查表索引 → PVF code 直发)。
3. NOTI 38 掉落条 12B→48B、冠军位迁移。
4. 疲劳系统 DFL 独有(A21 无)。
5. MOVE_MAP 64B 载荷/无 ack/带队换图。
6. CMD 15/27 请求带 id、NOTI 27 布局不同。
7. SET_PLAY_RESULT:客户端计分布权威 vs 服务端台账权威。
8. BOSS_DIE_CHECK 语义收窄。
9. USE_SKILL/ALLDIE 在 A21 未注册。
10. 翻牌 4→8 槽+自动翻牌。
11. 加载栅栏/回城 ack 有无。
12. NOTI 5 条目 3B→5B。
13. 城镇广播模型(布局兼容,行为分歧)。
14. 教程协议重定义。

## 6. 域四:聊天 / 邮件 / 宠物 / 技能 / 任务 / 杂项

### 6.1 CMD 17 SEND_MESSAGE ↔ NOTI 12
- 请求字段序列一致(mode u8+target u16+u32+len 字符串);A21 的 u32 是私聊目标 CharacterId,DFL 是保留字段。私聊 A21 尾部可再跟目标名 dstr。
- NOTI 12:DFL `type, uid u16, len, bytes`(7+len);A21 `mode, uid, serverGroup u8, dstr`(**多一个 serverGroup 字节**)。
- 行为:DFL 只接受 type=3(地图聊天)且**只回显给自己**、无 ack、无广播;A21 完整频道路由(1/7 私聊、2 队伍、3 同区域、43 会话、52/53 攻坚队),公会聊天改走 NOTI 65 独立载荷,发送前黑名单过滤。60A1 另有 NOTI 126 弹窗变体(`CreatePopupNotification`),A21 无。
- 结论:【版本性差异】+【实现分歧】。

### 6.2 CMD 124/132 → A21 114/122 宠物聊天 ↔ NOTI 126/131 → A21 114/119
- 请求布局同 CMD 17;DFL 有"装备 Bobo 宠物/城镇内"校验;A21 不校验,且因单机无广播集合只回发给自己,另有 PVF `creature/script` 驱动的服务端迎宾语推送。
- **A21 未注册 CMD 114(CREATURE_SEND_MESSAGE)**。
- NOTI 0x77(119):`mode, uid, serverGroup, dstr`(多 serverGroup 字节)。
- 结论:opcode 三重漂移【版本性差异】+ 互有缺失【实现分歧】。

### 6.3 邮箱:CMD 97/98/99/144 → A21 94/95/96/134 ↔ NOTI 97/98/99(同值)
- open:两边无载荷,ack `result+notLoaded u16` 同形。
- extract:DFL 按 mailId u32;A21 用 claimObjectId i32(带附件邮件是附件合成 id,不是邮件 id)。
- CHANGE_LETTER_STAT:**布局完全同形**;行为差异——A21 action=0 删除成功后补推 NOTI 98+整页刷新,DFL 只回 ack。
- send:DFL 单附件(itemId u16);A21 itemId i32,另有 0x013B 多附件(≤10)。**手续费:A21 每附件 1000 金+金币 5%(上限 1 万),DFL 零手续费**;参考资料分册另载无附件固定 100 金(见 [s4a21/servers4a21-09](servers4a21-09-shop-event-mail-service.md))。
- NOTI 97 快照:两段式骨架一致(汇总段+notLoaded+正文段+首字节 reset 标记);汇总行内容完全不同——A21 把完整 A21 物品结构塞进汇总行,纯文本邮件也占行;DFL 纯文本不进汇总段、归档邮件逐封分包。
- NOTI 99 ALARM:两边 `count u16`;触发时机不同(DFL 进游戏/open 时;A21 发送时对在线收件人推、每连接一次)。
- A21 还有分页(20/页)、过期时间、官方邮件、防诈骗正文、成体系错误码、发送幂等键;DFL 全量快照、有仓库升级附件特判。
- 结论:骨架【一致】;字段与行为【版本性差异】+【实现分歧】。

### 6.4 技能:CMD 30/31 → A21 28/29 ↔ NOTI 19
- CHANGE_SKILLSLOT:DFL `src u8+dst u8`,ack `[1,src,dst]`;A21 多一个**技能页字节**,ack 4B 回显。DFL 有"双连发交换"等待逻辑(5ms 读下一包);A21 逐条持久化。
- BUY_SKILL:DFL 单个 skillIndex u8;A21 批量 `count + N×(skillId u16, isRefund, level)`(16 位技能号+遗忘退款),ack 含 remainTP/技能指令。
- NOTI 19:DFL 单页 `sp u16+count+3B×n`;A21 多页(page u16 头+每条 extras+双尾)。
- 结论:【版本性差异】。

### 6.5 任务:CMD 33-36 → A21 31-34 ↔ NOTI 21
- ACCEPT/GIVEUP 请求同为 questId u16;SET_TRIGGER:A21 多一个 increment 字节;FINISH:DFL 可变长(奖励选择),A21 定长 8B 带 sentinel 0xFFFF。
- ack:ACCEPT 条目 DFL 8B(itemId u16)vs A21 10B(itemId i32);FINISH 后段大分歧(DFL 金币伪装 slot=0 条目、转职/觉醒链重置技能树;A21 exp/完成数前缀+16B 奖励条目+称号/觉醒等 chain 分支)。A21 解析前先 StripEcho 去掉前 2 字节回显。
- **NOTI 21:DFL `count u8 + n×questId u16`;A21 `level u8 + count u16 + n×questId u16`**——最易忽略的初始化包错位点。
- 结论:小命令【一致→版本性差异】;FINISH/NOTI 21【版本性差异】。

### 6.6 CMD 2 SET_UDP_IP_PORT ↔ NOTI 11/26
- 请求:两边 `nat+内网 IP+外网 IP+port+附加`;**端口字节序相反**(DFL 大端,A21 小端+mtu 校验)。
- ack:DFL 回 success/error;**A21 不回 ack**(只更新 Player 并广播 PARTY_INFO)。
- NOTI 11:布局同族(count+{uid,ip,ip,port,…});A21 仅在 PvP 房间流推送且可替换为中继地址;DFL 进城选图时推送。
- NOTI 26 UDP_HOST:DFL 发 1 字节;**A21 未实现**(注释:格式未逆向)。
- 结论:【版本性差异】+【实现分歧】。

### 6.7 CMD 130 REQUEST_BLACKLIST(60A1)vs A21 黑名单 118/119/120
- DAFLegacy:**黑名单未实现**,且 CMD 130 被 `ExchangeServerCharacterInfoCommand` 占用(`GameProtocolEngine.cs:370`)——按 60A1 名称表 130 实为 REQUEST_BLACKLIST,即把黑名单 opcode 当成了跨服角色信息应答(md 已标注的错配风险)。
- A21:ADD/DELETE/REQUEST 完整实现(dstr 名字 5-33B、ack `[1+name]`/错误码 74 重复/76 不存在/78 满、列表含生日、SQLite 持久化、聊天拦截联动)。
- 结论:【DAFLegacy 未实现/错配】——若两服共享客户端逻辑这是隐患点。

### 6.8 CMD 82 CALL_MEMER_LIST(个人商店访客轮询)
- DFL:固定回 18B 空商店。A21:对应 opcode 79 **未注册**;67(GUILD_MEMER_LIST)实现的是真公会名册,语义无关。
- 结论:【实现分歧】(DFL 桩,A21 缺失)。

### 6.9 NOTI 120 EVENT_INFO(→ A21 108)
- DFL:单 `featureFlags u32`;A21:`count + n×(eventId, 开始/结束公告 dstr, detail…)`。
- 结论:【版本性差异】。

### 6.10 宠物:CMD 103/104/105 → A21 100/101/102 ↔ NOTI 100-107(同值)
- RENAME:字段序一致(listType,slot,nameLen,name),名字上限 12 vs 13;A21 有名字校验器与专用错误码,追加宠物栏刷新。NOTI 101 同形 `uid+dstr`。【一致(骨架)】+【版本性差异】
- RESPONSE_CREATURE:DFL 回 NOTI 104 `uid`;**A21 未注册 CMD 101**。【DFL 有 / A21 无】
- HATCH:DFL `itemSpace=7+slot`;A21 启发式解析(模板 id 试探),成功后 NOTI 0x69 刷新。【实现分歧】
- NOTI 100/107:`uid u16` 同形【一致】(DFL 由饥饿系统触发,A21 由饱食度运行时触发)。
- NOTI 102 GAIN_EXP:DFL 5B `level+exp`;A21 6B(中间多 1 保留字节)。【版本性差异】
- NOTI 103 STATE:DFL `uid u32+state u32`;A21 是**整条宠物条目**。【版本性差异】
- NOTI 105 CREATURE_ITEM_LIST:DFL `uid,stomach,exp,level,dstr,noCharge`;A21 `key,f04,modeFlag,progress,…,dstr,tail`(字段组完全不同)。【版本性差异】
- NOTI 106 EVOLUTE:DFL `creatureId u8+uid u16`(任务进化);A21 `param+eventUniqueId`(饱食进化)。【版本性差异】

### 域四 Top 差异
1. NOTI 载荷整体风格分野(紧凑 u16 元组 vs 32 位+完整物品结构),移植必须重写 builder。
2. NOTI 21 头部差异(level+count u16)——初始化包最易错位。
3. 聊天 NOTI 多 serverGroup 字节;A21 频道路由/黑名单,DFL 单回显。
4. 邮件手续费(1000/附件+5%)DFL 没有。
5. 宠物聊天 opcode 三重漂移且互有缺失(CMD114/101)。
6. 黑名单:DFL 未实现且 CMD 130 被错配占用。
7. CHANGE_LETTER_STAT 删除后补推 NOTI 98 与否;claimObjectId vs mailId。
8. 宠物状态推送布局(102/103/105/106)全部不兼容。
9. UDP 域:端口字节序相反、A21 无 ack、NOTI 26 未实现。
10. 技能:页字节/16 位技能号/批量+退款 vs 单条+双连发交换等待。

## 7. 综合建议

行动方案(优先级、借鉴清单、逐项落点与里程碑)统一登记在 [feature-backfill-plan-vs-s4a21.md](feature-backfill-plan-vs-s4a21.md)(该档继承本档结论,文档先行),此处不再重复;上文各域结论中的【实现分歧】条目即其输入。

## 8. 单侧未实现清单(共有域之外)

- **A21 未实现**(DFL 有):CMD 0 CHECK_CONNECTION 应答、NOTI 0 挑战、NOTI 4 STAMINA、NOTI 26 UDP_HOST、NOTI 36 FATIGUE、USE_SKILL、ALLDIE_MONSTER、RESPONSE_CREATURE(CMD 101)、CREATURE_SEND_MESSAGE(CMD 114)、CALL_MEMER_LIST(79)、疲劳系统整体。
- **DAFLegacy 未实现**(A21 有):逐项清单与回填分期见 [feature-backfill-plan-vs-s4a21.md](feature-backfill-plan-vs-s4a21.md) §1 与 §3–§5(黑名单、双人交易、邮件增强、聊天频道路由、公会等),此处不再重复。
