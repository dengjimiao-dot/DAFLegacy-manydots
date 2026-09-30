# DFLegacy 未实现功能回填实现方案(参考 ServerS4A21)

> - **目的**:实现文档先行,评审通过后再动代码。回答三个问题:当前项目哪些功能未实现/桩实现;这些功能能否从 `ServerS4A21\Server\DfoServer` 借鉴;借鉴哪些部分、怎么落地。
> - **现状依据**:对当前代码逐项核实(下称 DFL,根目录 `Server/DFLegacy.Server`,关键文件 `Protocol/GameProtocolEngine.cs`=GPE、`Network/EntranceService.cs`=ES、`Persistence/JsonGameStore.cs`、`Hosting/CharacterSessionRegistry.cs`),证据均给出 file:line。
> - **参考依据**:`ServerS4A21` 的 `Server/DfoServer`(下称 A21,Handlers/Parsers/Builders/Game 分层)。
> - **协议对照**:继承本目录 [comparison-vs-servers4a21.md](comparison-vs-servers4a21.md) 的结论(两版协议号漂移、载荷布局换代),60A1 侧格式以 [client-packet-enums-60cn.md](../protocol/client-packet-enums-60cn.md) 为准。

## 0. 总结论

1. **借鉴原则:搬逻辑,不搬格式。** A21 的状态机、校验顺序、错误码语义、计价公式、定时器/门锁模式、发送谓词并发纪律可直接移植;所有线上字节布局(14B/15B 帧、itemId i32、101B 物品条目、NOTI 12 多 serverGroup 字节、A21 新增 opcode 如 0x013B 多附件)必须按 60A1 客户端重写或放弃。
2. **确认未实现/桩实现共 6 组**(§1):黑名单(含 CMD 130 错配)、双人交易、邮件四项增强、聊天频道路由、GET_USERINFO mode 鉴权、公会全套;另有大量 60A1 枚举存在但无抓包样本的域(PVP/CHAOS/公会/私人商店等)。
3. **一个架构前置项**:DFL 目前没有跨会话的"区域在线名单"(AREA_USERS 只装本机玩家,ES:9855),聊天广播/双人交易/黑名单拦截都依赖它,需先补(§3.0)。
4. **分期**:P0 低风险高价值(130 错配修复、黑名单、邮件增强、体力计价、聊天路由),P1 大功能(双人交易、错误码细分),P2 评估后缓做(公会、查看他人、私人商店等)。

## 1. 现状盘点(逐项核实于当前代码)

### 1.1 A 组:确认未实现,且 60A1 客户端枚举存在(回填候选)

| 协议(60A1 号) | 现状 | 证据 |
|---|---|---|
| CMD 128/129/130 黑名单 | **未实现**;全仓库无数据结构/存储/handler;128/129 掉入兜底只打日志(ES:12249-12254) | 60A1 名表 `CmdPacketType.REGISITER_TO_BLACKLIST`(128)/`DELETE_TO_BLACKLIST`(129)/`REQUEST_BLACKLIST`(130) |
| CMD 10/11 REQUEST/RESPONSE_PEER(双人交易)+ NOTI 7/8/15/16/17/18 | **未实现**:无常量、无 handler、无 builder | 枚举表标注 ❌;CMD 26 处注明"此版交易 NOTI 15-18 未实现" |
| 邮件手续费 | **无**:CMD 97 只扣信内 gold(ES:3814),无费用概念 | ES:3695-3896、14050-14103 |
| 邮件多附件 | **不支持**:单附件模型(ES:3724;JsonGameStore.cs:2370-2377) | — |
| 邮件分页 | **未用**:`CreateMailboxList` 有 `notLoadedCount` 参数(GPE:949)但恒 0,一包全发(ES:482-509) | 容量上限 255(JsonGameStore.cs:2364) |
| 邮件发送幂等键 | **无**:请求不含客户端邮件 id,重复请求重复投递(仅提取侧幂等) | mail.Id 现分配 JsonGameStore.cs:5531 |
| NOTI 98 删除补推 | **builder 存在但零调用**:删除邮件后只回 CMD 144 ack(ES:3653,3686-3691) | GPE:1051-1067;60A1 布局 4+4n 已实现 |
| 聊天频道路由 | **只处理 type=3**,其余记日志丢弃(ES:3348-3357);且**只回显给自己**(ES:338-369 直接写本会话 stream,无广播) | — |
| 公会全套(CMD 70/71/76-81 等)+ NOTI 62 公会聊天 | **零实现**:全仓库无公会逻辑 | 仅 userinfo 里公会占位零字段 GPE:3037-3038 |
| GET_USERINFO mode / NOTI 2 subtype 3/6 | **不解析请求体**(ES:2543-2551);subtype 只产 0/1/2(GPE:2996/3097/3257) | — |

### 1.2 B 组:桩实现,需修复

| 协议 | 现状 | 证据 |
|---|---|---|
| **CMD 130 错配**(最高优先) | 60A1 名表 130=REQUEST_BLACKLIST,却被 `ExchangeServerCharacterInfoCommand = 130` 占用并回桩 `[1,0]`;EXCHANGE_SERVER_CHARAC_INFO 实际是 139/140(`CmdPacketType.EXCHANGE_SERVER_CHARAC_INFO` / `_RET`) | GPE:370、457-458、496-500 |
| CMD 82 CALL_MEMER_LIST | 被注册为 `PrivateStorePollCommand=82`,回 18B 空商店固定桩,不解析请求;真正的私人商店 CMD 85-89 全未注册 | GPE:362、456、482-494;`CmdPacketType.CALL_MEMER_LIST`(82) |
| CMD 41 USE_SKILL | 只回成功字节,材料扣减全靠 CMD 18 | ES:10475-10524 |
| CMD 148 ALLDIE_MONSTER | 纯日志 | ES:10754-10762 |
| CMD 26 SET_ITEMTRADE_STATE | 实现为"把锁定物品卖给 NPC"(算价加币+回 SELL 包),非玩家交易 | ES:9483-9568 |

### 1.3 C 组:已实现但有行为增强空间

- **RECOVER_STAMINA 计价**:`GetCost(level)` 只按等级取价(ES:9934-9935;StaminaRecoveryCatalog.cs:45-55),无按当前虚弱/疲劳档位的浮动。
- **建/删角色错误码**:统一回 `[0,2]`(ES:12218-12220、12187-12189),客户端无法弹细分提示。
- **死 builder**(协议定义了但从不发):NOTI 22 USER_POSITION(GPE:2199)、NOTI 21/29 空包(GPE:2090/2446)、NOTI 98(见上)。

### 1.4 D 组:暂不回填(60A1 枚举存在但无抓包样本/依赖缺失)

好友(63/64)、PVP(53-62/120-122)(组队已雏形落地、复测进行中)、CHAOS(106-118)、战争房间(92-96)、私人商店(85-89)、时装租赁(22/23)、EXCHANGE_SERVER(137-140)、154-162。这些域要么需要成套逆向,要么依赖组队/公会等前置系统,本期不动。

## 2. 借鉴边界

### 2.1 可直接移植的逻辑层(与协议版本无关)

| 模式 | A21 出处 | 说明 |
|---|---|---|
| 黑名单单事务 Add | Game/Friends/BlacklistRepository.cs:32-52 | "按名找目标→自查重复→容量检查→插入"一次完成;上限 64;错误码 74=重复/76=不存在/78=满/75=删除失败 |
| 发送谓词拦截 | ChatHandler.cs:108-111、204、213 | 把 `IsBlocked` 塞进发送谓词而非先过滤列表,防竞态 |
| 交易五态协商机 | Game/Inventory/Services/ItemTradeSession.cs:119-151 | 0→5(锁定)→1(确认)→3(最终确认),双方对称、先到先等;双方都 3 才提交 |
| 交易提交原子性 | InventoryExchangeCommitService.TryCommit | "先消费协商再结算",失败即整单 Canceled,不落半边 |
| 交易资格谓词集中 | ItemTradeHandler.cs:47-55 | 同频道、空闲态、城镇就绪、非副本、双向非黑名单,一处判定 |
| 同 CMD 双语义分流 | Parsers/Inventory/ItemTradeRequest.cs:8 | `body[2]==1` 区分交易 peer 与组队 peer |
| 邮件手续费公式 | MailboxRepository.cs:2102-2111 | 附件费=件数×1000;金币费=min(10000, 金币×5%) |
| 邮件校验顺序 | MailboxHandler.cs:187-274 | 收件人存在→未删→金额非负→附件约束→非自发→…,fail-closed |
| 删除邮件组合动作 | MailboxHandler.cs:499-520 | ACK + 补推 NOTI 98 + 满 20 条整页刷新 |
| 分页+过期哨兵 | MailboxRepository.cs:179-269、MailboxHandler.cs:55/58-110 | 20/页、分钟级过期维护、9999/永久哨兵 |
| 错误码集中映射 | MailboxHandler.cs:686-763、CommonPacketBodyBuilder.cs:7-15 | 策略枚举→客户端码的每域一张映射表;"[1]=成功、[0,code]=失败"语义 |
| 体力按档计价 | StaminaHandler.cs:153-163、RecoverStaminaPriceProvider.cs:13-48 | `base × (100-当前疲劳百分比)/90`;基价外置数据表;事务内"先折扣判定→后扣费→后重置" |
| 翻牌 4s 自动翻+门锁 | Dungeon/CardRewardCoordinator.cs:99-121、219-245 | 一次性定时器+代际(generation)校验+失败重排;信号量门锁 |
| 未知聊天 mode 静默收敛 | ChatHandler.cs:183-187 | 未知模式永远只发给自己,不因形状相同放行为广播 |
| 代际防竞态纪律 | CharacterTransitionCoordinator.RunIfCurrentAsync 系列 | 构建前/每包发送前重查双方身份;DFL 对应物是 CharacterSessionRegistry 租约(Hosting/CharacterSessionRegistry.cs) |

### 2.2 必须按 60A1 重写或放弃的格式层

- 信封:DFL 2B seq 在 body 开头,业务载荷从 body[2] 起;A21 是 14B(C→S)/15B(S→C) 帧头。所有偏移按各自 body 折算。
- 物品 ID:60A1 全线 u16(物品条目 13B 紧凑式),A21 i32(101B/131B)。交易/邮件涉及物品的一律用 60A1 条目。
- NOTI 编号漂移:两版包号存在多处错位(如 CMD 97 在 A21 为 PEER_CONNECT_RESULT、60A1 为 MAILBOX_SEND;60A1 名表 130=REQUEST_BLACKLIST 而 A21 同族功能在 139/140)。凡 A21 报告里的包号必须先过 [client-packet-enums-60cn.md](../protocol/client-packet-enums-60cn.md) 的映射,严禁按号直译。
- NOTI 12 聊天:60A1 无 serverGroup 字节,布局 `[type, uid u16, len, bytes]`(7+len)。
- A21 独有 opcode(60A1 客户端没有,直接放弃):0x013B 多附件、0x144 按名查角色、CHECK_DOUBLE_CHARACTER_NAME 0x02B5、NOTI 503/CHANNEL_MOVE_FOR_MATCHING、USERINFO subtype 3/6(见 §5)。

### 2.3 结构性差异(移植时要替换的底座)

| 维度 | A21 | DFL | 对回填的影响 |
|---|---|---|---|
| 存储 | SQLite 事务+在线租约 | JsonGameStore 整包写库+文件锁 | 黑名单/交易记录建模成 JSON 记录类型;事务性靠"先内存后单次写盘"保证 |
| 跨会话寻址 | SessionDirectory+频道表 | CharacterSessionRegistry 按角色租约(有,但**无 town/area 投影**);AREA_USERS 只装本机玩家(ES:9849-9858) | 需补区域在线名单(§3.0) |
| 组队 | PartyManager 完整 | 雏形已落地(`Game/Party` + `Network/PartyCoordinator`),真机复测进行中 | CMD 10-14/NOTI 6-10 布局已按 60A1 逆向,格式见 [packet-detail-party-60cn.md](../protocol/packet-detail-party-60cn.md);交易若复用 CMD 10/11 需走 body[2]==1 分流 |
| 并发 | RunIfCurrentAsync 代际 | 每连接顺序循环+Registry 租约回调 | 跨会话推送复用 Registry 的回调机制(mailReceived 等已有先例,CharacterSessionRegistry.cs:23-30) |

## 3. P0 实现方案

### 3.0 前置基建:区域在线会话名单

- **问题**:聊天广播(区域)、双人交易(寻找/校验对方)、黑名单聊天拦截都需要"同 town/area 在线玩家→会话"的实时投影。当前 townId/areaId 是每连接的局部变量(ES:9876-9877),AREA_USERS 回包只含自己。
- **方案**:给 `CharacterSessionRegistry` 的租约增加 `TownId/AreaId` 可更新字段(进城 SET_USER_AREA 时更新,断连时移除);新增查询 `GetSessionsInArea(town, area)`。跨会话发送沿用现有回调注入模式(参照 mailReceived 的做法)。
- **验收**:两个客户端进同一 town/area,各自 AREA_USERS/NOTI 23 能看到对方(此项同时是 60A1 客户端多人同屏的既有缺口,顺手补齐需单独验证客户端表现,可先只建名单不改编排)。

### 3.1 CMD 130 错配修复(半小时级,先行)

1. 删除 GPE:370 的 `ExchangeServerCharacterInfoCommand = 130` 常量与 GPE:457-458/496-500 的桩分支;EXCHANGE 家族整体属 D 组不实现(若要保留占位,移到 139 并标注"未经验证")。
2. 同步更新 SelfTests 引用(SelfTests/Program.cs:489、504 断言了该桩)。
3. 130 号让给 §3.2 黑名单 REQUEST。

### 3.2 黑名单系统(CMD 128/129/130)

- **60A1 协议面**:客户端枚举齐全;130 有抓包样本但仅帧头(请求应为空体);128/129 无样本,请求载荷按同族协议推断为"u32 长度 + GBK 名字"(与 A21 及 DFL 其余 dstr 一致),**实现前需用抓包工具对客户端社交界面取样确认**;ACK/列表布局未知,需逆向。
- **借鉴**:§2.1 前两行(单事务 Add、错误码语义、发送谓词、容量 64)。
- **设计**:
  1. 存储模型:`JsonGameStore` 新增 `CharacterBlacklistRecord { OwnerId, TargetId, CreatedAt }`,唯一约束 (Owner,Target);名字→角色解析复用现有按名查询。
  2. 内存层:`BlacklistService`(每 Owner 一个 HashSet 的并发字典),写路径经 JsonGameStore 持久化,读路径纯内存。
  3. 三个 handler 挂进 ES 主循环(参照现有 CMD 分支写法):128 ADD、129 DELETE、130 REQUEST(返回全量列表)。结果码语义:成功 `[1]`+名字、74 重复、76 不存在、78 满;具体字节布局封装在单一 builder `CreateBlacklistAck/CreateBlacklistList`,先用 A21 形状(dstr 名;列表含生日则日期编码待定)落地,抓包后校正——**布局集中在两个方法里,校正成本最低**。
  4. 拦截联动:聊天广播(§3.5)发送谓词查 `IsBlocked`;邮件发送回错误码 90(对齐 A21 MailboxRepository.cs:1784-1786 语义,60A1 邮件错误码 4/7/10/17/22/90 已确认语义一致,见对比文档 §2)。
- **风险**:60A1 ACK/列表布局是唯一的逆向不确定点;其余纯增量。
- **验收**:SelfTests 覆盖 Add(重复/不存在/满)/Delete/Request 全分支;双人在线时 A 屏蔽 B 后区域聊天互相不可见。

### 3.3 邮件增强(手续费、NOTI 98 补推、分页)

| 子项 | 借鉴 | DFL 落点 | 备注 |
|---|---|---|---|
| 手续费 | 公式 附件×1000 + min(10000, gold×5%)(MailboxRepository.cs:2102-2111) | ES CMD 97 handler(3695-3896):在现有余额检查后增加费用计算与扣减;`plannedGold` 改为 `currentGold - gold - fee` | **开放问题**:60A1 客户端发送界面金额语义(信内 gold 是否含费)需抓包核对;费用率做成 server.json 配置,默认按 A21 公式,核对后可调 |
| 删除补推 NOTI 98 | MailboxHandler.cs:499-520 的"ACK+Remove+满页刷新" | CMD 144 `mailState==0` 分支(ES:3653)删除成功后调用已有 `CreateMailboxRemoveMail`(GPE:1051,60A1 布局 4+4n);80 封以上再触发整页 NOTI 97 刷新 | builder 已有且格式已验证,纯接线,最低风险 |
| 分页 | 20/页 + notLoaded 语义(两端骨架一致,对比文档 §6.3) | `SendMailboxStateAsync`(ES:482-509)按 20 条切片,`CreateMailboxList` 的 `notLoadedCount`(GPE:949)传余数,`reset=false` 续包 | 容量 255 上限同步降到与分页策略匹配 |
| 多附件/幂等键 | — | **不做**:0x013B 是 A21 新增 opcode,60A1 客户端没有;幂等键依赖客户端请求携带 session/seq/checksum,60A1 CMD 97 请求体无此字段(TryReadMailboxSend,ES:14050-14103) | 提取侧已幂等(Claim 为 null 回错误 10),重发重复投递风险接受 |

- **验收**:SelfTests:手续费金额三例(纯文本、带附件、巨额封顶);删除后 NOTI 98 帧内容断言;>20 封时分包序列与 notLoadedCount 断言。

### 3.4 RECOVER_STAMINA 按档计价

- **借鉴**:`cost = base(level) × (100 - official)/90`(StaminaHandler.cs:153-163),official=剩余疲劳百分比;事务内"先折扣/免单判定→余额检查(22)→扣费→重置"顺序。
- **适配**:DFL 有真实疲劳/虚弱系统(60A1 必需,不可像 A21 那样省掉)。取 DFL 的虚弱字节(0=正常)映射 A21 的缺口比例:`cost = GetCost(level) × weakness / 90`,下限 `GetCost(level)`、上限 ×10/9,系数进常量。**口径开放问题**:DFL weakness 的确切数值域以 `weaknessRecovery` 回调与 NOTI 4(1B)现行代码为准,落地时先读该处代码校准(ES 现行 RECOVER 分支 ~9934)。
- **风险**:零协议风险——请求/应答布局不变,只改价格;玩家可感知的是"越虚弱越贵",与官方体感一致。
- **验收**:SelfTests 三档虚弱值的价格断言;金币不足仍回 22。

### 3.5 CMD 17 聊天频道路由扩展

- **借鉴**:集中路由函数(ResolveRecipients,ChatHandler.cs:126-188)、未知 mode 只发给自己(183-187)、黑名单挂发送谓词。
- **60A1 协议面**:请求 `mode u8 + target u16 + u32 + len + 字符串`(与 A21 同族,私聊目标字段 60A1 为保留位);NOTI 12 布局 `[type, uid u16, len, bytes]`,无 serverGroup。60A1 客户端实际会发的 mode 集合需抓包确认(预期至少 1/7 私聊、2 队伍、3 区域)。
- **设计**:
  1. 把 type 过滤(ES:3348)改为路由表:`3` → §3.0 区域名单广播(替换现在的"只回显自己",ES:3359);`1/7` → 按 target uid/名字经 §3.0 名单定向私聊(60A1 私聊 NOTI 12 是否需附加目标名字段,抓包定);`2/6`(队伍/公会)→ 系统未实现,回明确忽略并记日志(不回显、不广播);其余 → 只回显自己(安全默认)。
  2. 所有投递点过黑名单发送谓词(§3.2)。
  3. `SendTypedMessageNotificationAsync`(ES:338)改造为可指定目标会话(复用 Registry 回调通道),保持帧构造 `CreateMessageNotification` 不变。
- **验收**:双人同区互见区域聊天;私聊仅双方可见;被屏蔽者消息不达;未知 mode 无广播副作用。

## 4. P1 实现方案

### 4.1 双人面对面交易(本计划最大增量)

- **前置**:§3.0 区域名单;物品快照读写(现有背包模型)。
- **借鉴(逻辑层全拿)**:五态协商机(ItemTradeSession.cs:119-151:0→5→1→3,双方都 5 才进确认、都 3 才提交)、邀请 1 分钟超时(L20)、资格谓词(Eligible:47-55)、`MOVE_ITEMSPACE` 劫持进交易栏(TryMove:117-156,金币=slot0 虚拟条目、物品 slot3..11 的映射思路)、断线/换角/进副本强制 `Close(false)` 回滚(CancelBeforeTransition:195-210)、提交成功后逐边发结束包+补发 NOTI 13 主背包全量(Close:225-257)、`body[2]==1` 分流(ItemTradeRequest.cs:8)。
- **60A1 协议面(全部需逆向,是本项主要工作量)**:CMD 10/11 请求、CMD 26 状态包、NOTI 7/8(邀请/应答)、15/16/17/18(变动/取消/状态/完成)。物品条目用 60A1 13B 紧凑式;**NOTI 号按 60A1 表换算,严禁照抄 A21 号**(A21 的取消 NOTI 22 在 60A1 是 USER_POSITION)。60A1 客户端是否完整实现了交易 NOTI 15-18 的处理逻辑,以逆向为准(枚举表已注明"此版未实现"指的是服务端,客户端 UI 资源存在)。
- **DFL 侧事务模型**:无 A21 的 InventoryLease,用"双方背包内存快照 → TryCommit 校验(物品仍各自在交易栏原位)→ 一次 JsonGameStore 双角色写盘"模拟原子交换;提交失败整单取消。
- **改动面**:新 `Game/Trading/`(状态机+资格)+ ES 增加 CMD 10/11 分支与 MOVE 劫持 + GPE 增加 NOTI 15-18 builder(13B 条目版)+ CMD 26 从"卖 NPC"改回状态语义(**行为变更:现"卖 NPC"路径废止,销毁物品走 CMD 18**)。
- **风险**:逆向量最大;60A1 客户端交易窗对 NOTI 时序敏感(参照 A21 注释:body[0]≠1 时客户端把 body[1] 当错误码崩溃一类硬约束,60A1 侧同类约束要逐个摸)。建议先交付"邀请→放物品→锁定→确认→提交"主链路,取消/超时分支次之。
- **验收**:SelfTests 全状态机矩阵(含单方锁定、双方确认、余额不足、断线回滚、黑名单拒绝);双人实测交换金币+物品各一。

### 4.2 建/删角色错误码细分(小)

- **借鉴**:CREATE 的 0x04(参数/槽位)、0x12(名字长度)、24(重名,与预检共用)、名字校验码;DELETE 的 0x15(名槽不符)、0x28(状态禁止)(CharacterSelectHandler.cs:954-1059、1061-1147)。
- **注意**:60A1 客户端错误码表与 A21 是否同值需实测(建角失败弹窗逐码验证);DELETE_CHARACter 的"请求带名+4B ACK+禁补名册"是 A21 客户端约束,**60A1 下保持 DFL 现有 2B ACK+补名册不变**(对比文档 §3.7 结论),只细分错误码数值。
- **验收**:重名/非法名/槽满三 case 客户端弹正确提示。

### 4.3 多频道生成与频道目录(P1)

- **目标**:DFL 现为单频道(server.json `Channel` 节 → `EntranceMetadata.BuildSingleLocalChannel` 固定 9 组×1 频道,`Network/Sessions/ChannelSessionSenders.cs:71` LoadContent);参考 A21 频道生成链路(PVF 频道脚本 → 目录 → 按频道监听 → 列表下发)回填为目录驱动的多频道。对应对比文档差距项 10 的前半(频道目录/生成);频道准入/顶号编排放 M2。
- **A21 参考链路**(出处均为 A21 `Server/DfoServer`):
  1. 真源:PVF 内 `etc/channel_info.etc`(`GameWorld/PvfArchiveAccessor.cs:23-44`,大小写两路径+名称兜底检索),提取为运行目录文件(`Infrastructure/ServerPaths.cs:25`)。
  2. 解析:`Network/Protocol/ChannelProtocolHandler.cs:300-382` ParseScriptChannels——`[server]` 节组 1,频道行=频道号(0..255)+反引号名+类型+第 4 列组名;组名 `[raid]` 但类型非攻坚(0x17)的坏行与 A21 客户端一致丢弃。
  3. 目录:`Network/Core/GameNetworkConfig.cs` ConfigureChannelCatalog 按频道号去重;端口=10000+频道号(:77);类型→环境(普通 0x01/PvP 8,24/自由决斗 0x0D/攻坚 0x17,:24-26);无目录回落基线三频道 11/100/200(:82-138)。
  4. 下发:`Network/Protocol/ChannelProtocolHandler.cs:158-186` 明文=2B 组号+4B 条目数+每条 48B(20B 名+4B 容量+4B 在线+16B IP+4B 端口),AES 密钥=当天日期串+"000006"(:20);容量抓包标定 ch20/21=150、攻坚=250、其余 100(:394-407);下发名 `#ch.{id}`(客户端显示名来自其解析的脚本,非列表)。
- **60A1 协议面**(IDA 逐 case,详见 [60a4-handshake-protocol.md](../protocol/60a4-handshake-protocol.md) §11,函数登记 [client-function-register-60cn.md](../protocol/client-function-register-60cn.md) §10):入口元数据(协议 1/3)布局=u32 组数+每组[20B 组名+u32 频道数+N×48B 记录(20B 显示名+u32 容量+u32 在线+16B 主机+u32 端口)](`sub_894B10`);组名必须命中客户端内置 109 项表(前 9 项与 `EntranceMetadata.ServerGroups` 同序同集);显示名内全部数字字符拼接 atol=频道号(`sub_8929C0`,即现"前缀不含数字"约束的客户端依据);拥挤=100×在线/容量>95;调试文件 `_ChannelTest` 存在时客户端把收到的元数据 dump 到 `ChannelInfo.txt`(真机校准判据)。
- **号位错位警示**:A21 频道层 11/12=频道表请求、5/6=脚本版本、0xB/0xC=CONNECT 密钥;60A1 入口 11/12=密钥交换、5/6=缓存判定、9/10=脚本下载、1/3=元数据。语义族对应、号位不同,严禁按号直译(沿用 §2.2 结论)。
- **设计**(两期):
  - M1 目录+元数据(零监听改动,已实现):频道目录唯一真源=PVF 内 `etc/channel_info.etc`(与协议 9 下发脚本同文件),显示名经 `etc/etc.chn.str` 名称引用解析(`ChannelCatalog.TryParseChannelStringTable`,未解析行丢弃);server.json 不提供频道目录配置,`Channel` 节收缩为协议面身份 Host/Port/ServerNumber + 容量 `MaximumUsers`(默认 10)。`Network/Protocol/ChannelCatalog.cs` 解析 `[server]` 组 1 频道行(只取组 1,与参考项目一致),`GameWorld/EntranceChannelDirectory.cs` 启动期一次解析并打印频道列表,`EntranceMetadata.BuildChannels` 组 0 承载目录频道(记录名 CP936 写入)、其余 8 组回落单频道默认记录(回退名常量 `Local Channel 1`,沿用真机验证字节);各频道 host:port 统一指向现游戏监听,会话频道=目录首条目(NOTI 1 `displayName/channelNumber`、NOTI 130 频道字段与经验加成键同源,既有先例 [packet-detail-connection-60cn.md](../protocol/packet-detail-connection-60cn.md));`CurrentUsers` 改为在线会话数动态值(`CharacterSessionRegistry.SessionCount`),元数据随每次握手现建;PVF 无有效 etc 行时回退现行单频道元数据,字节不变。
  - M2 按频道监听+准入(对齐 A21 完整形态):每频道独立游戏监听(端口规划不必照搬 10000+号,60A1 端口由元数据下发,客户端无固定约定);频道准入/顶号编排接 `CharacterSessionLifecycleCoordinator`(对比文档差距项 10 后半)。
- **改动面**:`Network/Protocol/EntranceMetadata.cs`(BuildChannels)、`Network/Sessions/ChannelSessionSenders.cs`(LoadContent 目录化)、`Infrastructure/ServerOptions.cs`(Channel→Channels)、组合根监听编排(仅 M2)、SelfTests 元数据字节断言。
- **风险**:①组名表 109 项中 DFL 只验证前 9 名,目录引入新组名必须先真机验证命中;②A21 容量标定(150/250/100)为 A21 抓包结论,60A1 无对应标定,容量值不照搬、按配置;③同端口多频道的会话归属(M1 形态)依赖 NOTI 1 `channelNumber` 的会话内一致性,需与 NOTI 130 频道字段联动复核([packet-detail-chat-60cn.md](../protocol/packet-detail-chat-60cn.md));④显示名取全部数字拼接为频道号,组名/显示名含数字即错号(客户端 atol 语义,现构造期约束已覆盖,新目录校验须保留)。
- **验收**:SelfTests 多组多频道元数据字节断言(布局/约束拒绝/单频道回归不变);真机:`_ChannelTest` dump 与元数据逐字节对照、选频 UI 显示 N 频道并按拥挤阈值表现、进游戏后 NOTI 130 频道号与所选一致。

## 5. P2 / 暂不清单(评估结论)

| 功能 | 结论 | 理由 |
|---|---|---|
| GET_USERINFO mode 鉴权 + NOTI 2 subtype 3/6 | **不做** | subtype 3/6 是 A21 新增,60A1 客户端协议域没有;mode 语义同为 A21 扩展(对比文档 §3.4)。鉴权/防竞态思路留档,未来做"查看他人"时按 60A1 格式重新设计 |
| 公会全套 + NOTI 62 公会聊天 | **暂不** | 无任何数据底座;依赖好友/成员表;独立立项 |
| 翻牌 4s 自动翻、加载栅栏 | **暂不** | DFL 现单人副本流程可用;组队系统落地后再借 A21 定时器模式(CardRewardCoordinator/DungeonLoadingCoordinator) |
| 好友 63/64、私人商店 85-89 | **暂不** | D 组;依赖区域名单与交易先落地。组队雏形已落地、复测进行中(见 [packet-detail-party-60cn.md](../protocol/packet-detail-party-60cn.md)),副本协同编队(cohort/定时器)仍待副本组队化时再借 A21 模式 |
| RECOVER_STAMINA 成功路径改 A21 式(无 ack) | **不做** | 60A1 客户端需要 CMD ack+NOTI 4,版本性差异不可倒置 |
| A21 自动建号、slot 越界回落 0 | **不采纳** | 安全/正确性劣化,对比文档已定性 |
| DFL 独有保留项:NOTI 0 挑战、疲劳系统、COMPOUND 重放缓存、CMD0 ack | **保留** | 60A1 必需或 DFL 特有优势(对比文档 §7.5/7.6) |

## 6. 改动落点汇总(文件级)

| 工作项 | 新增 | 修改 |
|---|---|---|
| 3.0 区域名单 | — | Hosting/CharacterSessionRegistry.cs(租约加 town/area+查询);ES 进城/SET_USER_AREA 两处回写 |
| 3.1 CMD 130 | — | GPE:370/457-458/496-500;SelfTests/Program.cs:489,504 |
| 3.2 黑名单 | Game/Friends/BlacklistService.cs;GPE 两个 builder;JsonGameStore 记录类型 | ES 主循环 3 个分支;聊天/邮件拦截点 |
| 3.3 邮件 | — | ES:3695-3896(费用)、3653 后(NOTI 98)、482-509(分页);server.json 配置项 |
| 3.4 体力计价 | — | GameWorld/StaminaRecoveryCatalog.cs 或 ES:9934 处;常量表 |
| 3.5 聊天路由 | Network/ChatRouter.cs(集中路由) | ES:3331-3370、338-369 |
| 4.1 交易 | Game/Trading/(Session/Eligibility/Commit);GPE NOTI 15-18 builder(13B 条目) | ES CMD 10/11 新分支、MOVE 劫持、CMD 26 重写;SelfTests |
| 4.2 错误码 | — | ES:12187-12220 |
| 4.3 多频道 | Network/Protocol/ChannelCatalog.cs(etc 组 1 + etc.chn.str 解析);EntranceMetadata.BuildChannels;GameWorld/EntranceChannelDirectory.cs;SelfTests | ChannelSessionSenders.LoadContent(元数据现建,去缓存);ServerSettings(移除频道容量两字段);组合根监听编排(仅 M2) |

## 7. 风险清单

1. **60A1 载荷逆向缺口**(黑名单 ACK/列表、聊天私聊模式号、交易全家):统一对策——tools/ 下抓包工具先行,布局全部封装在单一 builder 后校正;实现顺序"逻辑+存储先行,格式后定"。
2. **客户端崩溃类硬约束**:A21 经验表明错误字节错位会直接崩客户端(DELETE ack body[0]、NOTI 27 布局注释等)。60A1 侧同类约束逐包实测,先在 SelfTests 断言帧字节,再上真机。
3. **手续费/体力的客户端口径**:两处都有"服务端算法对,客户端显示语义未证实"的风险,均做成配置/常量可调,实测校准。
4. **并发回归**:区域名单与交易引入跨会话写,必须沿用"租约校验后再动对方状态"的纪律,防顶号/断线竞态(A21 RunIfCurrentAsync 思路的 DFL 化)。

## 8. 里程碑建议

| 阶段 | 内容 | 出口标准 |
|---|---|---|
| M0 | §3.1 + §3.0 + 抓包采样(黑名单/聊天/交易) | 130 归位;名单可查询;三组样本入库 docs/protocol |
| M1 | §3.2 + §3.5(黑名单+聊天) | 双人互聊/屏蔽实测通过 |
| M2 | §3.3 + §3.4(邮件+计价) | SelfTests 全绿;真机弹窗/扣费核对 |
| M3 | §4.2 + 交易状态机(无格式层) | 状态机矩阵 SelfTests 全绿 |
| M4 | 交易格式层接线 + 真机联调 | 双人完整交易实测 |
