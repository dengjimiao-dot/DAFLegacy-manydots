# ServerS4A21 已实现协议处理参考 —— 登录 / 选角 / 角色 / 设置 / 体力 / 初始化序列

> - **本册导航**:总览/帧结构/信封/分发架构/已实现 CMD 总表见 [servers4a21-00-overview.md](servers4a21-00-overview.md)。
> - 仅写 A21(`ServerS4A21\Server\DfoServer`)侧实现,供 DAFLegacy 回填参考。路径相对 `DfoServer` 根;行号为整理时点状态。

## 0. 域综述

1. 连接串行:客户端先走**频道层**(11B ChannelPacketHeader,`Network/Protocol/ChannelProtocolHandler.cs`)拿 AES 密钥 / 脚本 / 频道表,再连游戏端口;游戏端口连上后服务端**立即主动推 NOTI 1 CHANNELINFO**(`Network/handlers/LoginHandler.cs:36-46`),没有 60A1 的 NOTI 0 挑战回显环节。
2. 登录 CMD 1 **不验密码**,账号不存在自动建号(`LoginHandler.cs:81-87`);若账号有已保存设置则先推 NOTI 0x00AD 选人界面设置并进入"等 2×CMD 0x00C5"门控,攒满 2 次才回登录成功(`LoginHandler.cs:99-143`、`Network/handlers/SettingsHandler.cs:110`)。
3. 选角 CMD 4 由 `CharacterSessionLifecycleCoordinator.HandleSelectCharacterAsync` 统一编排:频道准入检查 → 顶号(目录替换)→ 转移锁内清旧角色 → `HandleResolvedSelectCharacterAsync` 水合玩家并**下发整条 init 包流**(`Game/SelectCharacter/NewCharacterInitSequence.cs` 静态模板序列 × `Network/Builders/Init/InitPacketBuilderRegistry.cs` 逐包构建),随后 raid 欢迎/重绑补挂(`Network/Protocol/GameProtocolHandler.cs:359-364`)。
4. 所有游戏包先过 `CanDispatch` 门禁:角色已注册时只有"目录中该 cid 的当前会话"可发包,防顶号后的僵尸会话(`Network/handlers/CharacterSessionLifecycleCoordinator.cs:318-330`、`:724-735`)。
5. 角色态生命周期(选角/返回选人/断线)全部由 Coordinator 在 `_characterTransitions` 锁内完成:背包 lease 保存、专家店铺关闭、城镇离开、位置落库、副本局终止、好友下线推送、账号登出记录,顺序敏感(先通知后注销,`CharacterSessionLifecycleCoordinator.cs:109-316`、`:779-872`)。
6. 设置组是登录门控的一环(0x00C5 同时是"选人界面设置保存"与"可见性位应用"载体,后者要求先来过 CMD 0x01C0);体力恢复是选角后唯一带金币扣费事务的域内协议。

## 1. 帧结构(前置)

- 客户端→服务端 Game 帧 14B 头 `GamePacketHeader`:cmd u8(游戏协议恒 1)+type u16+length u32(总长)+checksum u32+seq u16+extra u8(`Network/Core/Packet.cs:68-75`);handler 收到的 body 从第一个载荷字段开始。
- 服务端→客户端信封 15B:`cmd u8 + type u16 + length u32(=body+15) + 8B 零`;cmd 0x01=CMD 应答、0x00=主动推送(`Network/Core/GamePacketEnvelopeBuilder.cs:10-30`)。
- dstr = u32 字节长 + 原始字节,GBK/CP936(`Infrastructure/ClientTextEncoding.cs`)。
- 错误应答通用格式:`[0x00, errCode u8]`(`Network/Builders/CommonPacketBodyBuilder.cs:12-15`);成功 ack `[0x01]`(`:7-10`)。

## 2. 频道层协议(Network/Protocol/ChannelProtocolHandler.cs,非 Game 协议)

11B 头 `ChannelPacketHeader`:classification u8(应答恒 0x7C)+msg_no u8+sLength u32(=头+体)+check_sum u32(置 0)+ack u8=1(`Network/Core/Packet.cs:15-21`;应答构造 `ChannelProtocolHandler.cs:114-127`)。msg_no 枚举 `:38-58`。AES 密钥=当天 `yyyyMMdd`+`"000006"`(`:20`)。

| 请求 | 应答 | 内容 |
|---|---|---|
| CS_CONNECT 0x0B | SC_CONNECT 0x0C | `[4B 0][AES 密钥 ASCII 补零至 32B]`(`:129-138`) |
| CS_GET_SCRIPT 0x09 | SC_GET_SCRIPT 0x0A | 运行目录 `channel_info.etc` 文件整包 AES 加密回传(`:140-146`) |
| CS_CHECK_SCRIPT_VERSION 0x05 | SC_CHECK_SCRIPT_VERSION 0x06 | `[4B 0]["66" 补零至 16B]`,版本号硬编码 `"66"`,AES 无填充(`:18`、`:475-483`) |
| CS_ASK_CHANNEL_INFO_NEW 0x11 | SC_ASK_CHANNEL_INFO_NEW 0x12 | 明文 `[组号 u16=1][条目数 i32] + N×[名称 20B][容量 i32][在线 i32=0][IP 16B][端口 i32]`(48B/条)后整体 AES 加密(`:28-33`、`:158-186`) |

- **频道表来源**:优先解析 `channel_info.etc` 的 `[server]` 组 1(每行 `频道ID 频道名\`...\` 类型 组名\`...\``),`[raid]` 组名但类型非攻坚类型的坏行与客户端一致地丢弃(`:300-382`,坏行判定 `:356-374`);容量按抓包标定 ch20/21=150、攻坚=250、其余 100(`:394-407`);端口 `GameNetworkConfig.PortForChannel`;无 etc 时回落 `GameNetworkConfig.BuildGameChannels` 默认表(`:278-286`)。
- 未知 msg_no 仅记日志(`:108-110`)。

## 3. 会话生命周期(Network/handlers/CharacterSessionLifecycleCoordinator.cs)

- **CanDispatch 门禁**(`:318-330`):`Player.CharacterId<=0`(未进号)放行;否则要求 `_sessionDirectory` 中该 cid 的当前会话就是本会话(`OwnsRegisteredGeneration`,`:724-735`)。顶号后旧会话的一切 CMD 都被丢弃并记日志。分发器在解析头之后第一件事就调它(`Network/Protocol/GameProtocolHandler.cs:331`)。
- **HandleConnectedAsync**(`:99-107`):注册宠物运行时,然后 `LoginHandler.Handle_ClientFirstConnected` 推 **NOTI 1 CHANNELINFO**(`LoginHandler.cs:36-46`)。
- **HandleDisconnectedAsync**(`:109-316`):先清专家店铺(无锁)→ 拿 cid 转移锁 → PC 房结算结束、好友下线推送 → `UnregisterAsync`(返回是否本代 owns)→ owns 时:城镇离开通知、副本局终止、位置强制落库、背包保存(失败则从库重载回滚 `:888-925`)→ finally:背包 lease 注销、rejoin/抽奖/抓娃娃/宠物会话清理、账号登出记录(`:948-980`)。非 owns 代只跳过共享清理并记日志(`:229-235`)。
- **顶号(同角色二次 SELECT_CHARACTER)**:注册前先保存被顶会话的脏背包,失败则本会话收错误码 19(`:407-425`);`RegisterReplacingAsync` 返回被顶会话后 `CleanupDisplacedSessionAsync` 做全套清理并关闭其连接(`:982-1073`)。
- **频道准入拒绝**:仅"100 频道最低 70 级"一条策略(`Network/Core/GameChannelAdmissionPolicy.cs:20-41`,错误码 0xFD+中文公告)。流程(`:352-401`):回拒绝 ack(`SelectCharacterAckBodyBuilder.BuildRejected`)→ NOTI 0x01F7 SERVER_NOTICE_MESSAGE(`Network/Builders/ServerNoticeMessageBuilder.cs:29-34`)→ 延时 3s(`:26-27`)→ 推 **NOTI 0x0371 CHANNEL_MOVE_FOR_MATCHING** 换频道包 `[channelId u8][0 i32][4B IPv4][port i32][0 i32]`(`:583-609`)。回落端口优先"被顶会话所在监听",其次本角色上次成功准入监听,最后普通端口(`:529-563`)。
- **选角准备完成判定**:`session.Player.CharacterId==cid` 且目录当前会话是本会话,否则抛异常走回滚(`:463-474`、回滚 `:1075-1168`)。

## 4. login 组

### CMD 0x0001 (1) `LOGIN` — 登录
- **请求**: `dstr 账号(ASCII,≤256B) + dstr 密码(MD5)`;解析失败回退默认账号 "10038"(`Network/Parsers/LoginRequestParser.cs:20-61`;回退 `LoginHandler.cs:68-79`)。
- **应答**: 成功包约 50B:`[u8 1][u8 20][u8 0][u8 环境][u8 0][i32 频道端口][dstr 广告 IP][i32×2 未知端口][24B 0]`(`Network/Builders/Login/LoginPacketBuilder.cs:35-53`;发送 `LoginHandler.cs:118-126`)。
- **行为**: 监听准入(pvp 监听未启用自由决斗时直接断开,`LoginHandler.cs:145-167`)→ 查账号,**不存在自动建号**(`:81-87`)→ 绑定 session.Account、更新最近登录 IP/时间(`:88-91`)→ 有已保存设置:推 **NOTI 0x00AD** 选人界面设置并置 `A21LoginSuccessPending`,等 2×CMD 0x00C5 后才回成功(`:99-143`);无设置:立即回成功(`:102-108`)。账号查询异常时静默返回(无应答,`:93-97`)。
- **NOTI 0x00AD(173) 登录期推送**: `[i32 len][主设置 blob][i32 len][快捷喊话 bank0][i32 len][bank1]`(`Network/Builders/AccountSettingsPacketBuilder.cs:11-35`);构建 `LoginHandler.cs:169-199`,应用选人界面角色默认并回写主设置(`:187-188`)。抓包证据:客户端的 2×00C5 上行由该包触发(`:102-104`)。
- **备注**: 不验密码+自动建号是单机取向,移植公网服务端必须改。

### NOTI 1 (1) `CHANNELINFO` — 频道信息(纯推送)
- **推送**: `[u8 1][dstr 频道名][i32 0][i32 0][u8 serverIndex][u8 channelId][u8 0][i32 epoch 秒][i32 1][dstr 广告 IP][i32 udpPort1][i32 udpPort2][i32 0][u8 '0'][u8 '0'][i32 CMD 枚举数][i32 NOTI 枚举数][i32 0]`(`Network/Builders/Login/LoginPacketBuilder.cs:7-33`;发送点 `LoginHandler.cs:41-45`,由 `HandleConnectedAsync` 触发)。
- **备注**: 尾部 CMD/NOTI 枚举计数用于客户端协议表校验,漏发会导致后续解析异常;连接即推,无心跳挑战。

### CMD 0x04DD (1245) `CHECK_USER_CONNECTION` — 连接握手确认
- **请求**: 不解析(仅记 body 长度)。
- **应答**: `[0x01]` 成功 ack(`LoginHandler.cs:48-59`)。

## 5. character 组

### CMD 0x0004 (4) `SELECT_CHARACTER` — 选角进号
- **请求**: `[slot u16]`(<2B 按 slot=0);按账号角色列表取对应记录,**slot 越界静默回落 0**;列表为空回落种子角色(`Network/handlers/CharacterSessionLifecycleCoordinator.cs:749-777`)。
- **分发链**: Coordinator 编排 → 成功后再挂 raid 频道欢迎(攻坚频道推 raid 目录,`Network/handlers/RaidHandler.Recruitment.cs:285-292`)与重绑补同步(`:270-283`)(`GameProtocolHandler.cs:359-364`)。
- **应答(CMD ack,选角回包)** — `Network/Builders/Init/SelectCharacterAckBodyBuilder.cs:22-144`,完整字段表:

| 偏移 | 类型 | 内容 |
|---|---|---|
| 0 | u8 | resultCode,成功恒 1 |
| 1 | u32 | 账号注册时间(恒 0) |
| 5 | u32 | 建角时间(record.CreatedAt Unix 秒) |
| 9 | u16 | uniqueId(=CharacterId 低 16 位) |
| 11 | i16 | 总疲劳(0) |
| 13 | i16 | 最大疲劳(恒 188) |
| 15 | i16 | 已用疲劳(0) |
| 17 | u8 + N×9B | 会员项数量;每项 `u8 type + u8[8] 到期时间` |
| 变长 | u32 | 点券 cera |
| 变长 | 30×6B | 固定激活任务槽 `u16 questId + u32 triggerValue`,空槽 `0xFFFF/0`(`:73-103`) |
| 变长 | 4×i32 | QUEST_NOTIFY 槽(CMD 0x01FB 存档恢复,`:105-111`) |
| 变长 | u8 | charSlot(=record.TownId,`Network/Builders/Init/SelectCharacterPacketBuilder.cs:64-65`) |
| 变长 | u8+u8(+1B) | 教程标记:1 级且不可跳过 `[0,0]`,否则 `[0,1,0x4E]`(`:115-125`) |
| 变长 | u16,u16,u8,u16 | 疲劳电池 / 成长 buff / 交易惩罚 / extra(`:127-130`) |
| 变长 | 8B | 保留零(客户端读取边界 264B 之后,`:131-134`) |
| 变长 | u8,u16,22B | tutorialSkipable / 0 / 尾部零(`:135-140`) |

  拒绝回包 = `[0x00, errCode u8]`(19=顶号背包保存失败 `CharacterSessionLifecycleCoordinator.cs:419-424`;0xFD=频道准入,见 §3)。
- **行为**: 解析→频道准入→背包脏保存→旧角色清理(同 RETURN_SELECT 全套,`:779-872`)→ 转移锁内注册目录(顶号替换)→ `HandleResolvedSelectCharacterAsync` 水合(`Network/handlers/CharacterSelectHandler.cs:322-598`):副本效果恢复、背包 lease 注册+过期道具清理、subtype0 尾字段加载(名誉/技能树/攻坚频道显示位)、外观从在线背包重建(`:409-415`)、账号每日重置失败即断开(`:447-461`)→ 发 init 包流(§8)→ 每日补充道具入包并推背包更新(`:541-577`)→ **NOTI 0x0165 CHARAC_INVISIBLE_FALGS** `[uid u16][bits u8]`(`:579-583`;builder `Network/Builders/CharacterVisibilityBodyBuilder.cs:5-12`)→ CMD 0x0239 克隆称号 ack(`:585-592`)→ **NOTI 0x0289 HONOR_LEVEL_INFO** 与 **NOTI 0x0025 EXP** 成长胶囊(`:594-597`;`Game/Accounts/HonorLevelSyncService.cs:63`、`Game/Accounts/GrowthCapsuleSyncService.cs:71-73`)。异常整体回滚并断开(`CharacterSessionLifecycleCoordinator.cs:515-525`)。

### CMD 0x0005 (5) `CREATE_CHARACTER` — 创建角色
- **请求**: `[job u8][nameLen i32][name 原始字节]`,≥6B;job≤13(`CharacterSelectHandler.cs:31-34,962-967`);名 2-18B 且过 `NameInputValidator`(`:969-986`)。
- **应答**: 成功 `[0x01]` + **NOTI 2 名册刷新**(`:1033-1038`);错误:`0x04`=短包/非法 job/无空槽/异常(`:958,965,995,1057`)、`0x12`=名字长度(`:972`)、校验码=非法字符(`:981-985`)、`24`=重名(`:999-1007`)。
- **行为**: 槽位上限按模板 GateOrCount 推导(`:990-997`);默认落点艾尔文防线(TownId 1, 474,234,`:1012-1025`);`InitializeNewCharacter` 初始化结构化数据(`:1030`);失败时软删回滚(`:1040-1058`)。

### CMD 0x0006 (6) `DELETE_CHARACTER` — 删除角色
- **请求**: `[slot u16][nameLen i32][name]`,≥6B(`:1069-1077`,旧 `[slot u8]` 解析会恒拦截,注释明示)。
- **应答**: 成功 `[0x01][0x00][slot u16 回显]`(`:1124-1137`;body[0] 必须非 0 否则客户端按错误崩溃,charId 不能代替 slot);错误:`0x02`=短包/长度/槽越界(`:1065,1076,1088`)、`0x15`=名字不匹配(`:1093-1098`)、`0x28`=远征队出征中或异常(`:1100-1109,1145`)。
- **行为**: 软删+slot 前移压缩同事务(`:1117`,空洞 slot 会令客户端列表越界崩溃);清理好友关系并推删除节点(`:1119-1121`);**删除后不补发名册**(客户端 ACK 分支本地自管理,`:1139-1140`)。

### CMD 0x0007 (7) `RETURN_SELECT_CHARACTER` — 返回选人
- **请求**: 不解析。
- **应答**: `[0x01]` + **NOTI 2 名册**(`CharacterSelectHandler.cs:1149-1172`);守护者职业先推空守护盾 NOTI 0x0245 清客户端目录(`:1151-1163`)。
- **行为**: Coordinator 版本(`CharacterSessionLifecycleCoordinator.cs:611-722`)在转移锁内:目录世代校验→背包保存(失败回错误码 19)→专家店铺/PC 房/城镇/位置/副本清理→目录注销→背包 lease 注销→账号登出→好友下线推送(注销后、清零 UserId 前,`:699-713`)→清零 Player.CharacterId/UserId/PvpGrade(`:737-747`),并记 `PendingReturnSelectCharacterId`(`:697`)。

### CMD 0x0008 (8) `GET_USERINFO` — 查询用户信息
- **请求**: `[targetUid u16][mode u8]`,≥3B(`CharacterSelectHandler.cs:604-617`)。
- **应答/行为**:
  - mode 0/1/3/5=查看他人:请求者鉴权(`:152-164`)+同频道唯一定位目标(`:124-150`,低 16 位歧义即失败)→ `OtherUserInfoResponseBuilder.Build`(`Network/Builders/Init/OtherUserInfoResponseBuilder.cs:42-161`):mode 0 → NOTI 2 外观自包(`:82-87`);mode 5 → **NOTI 0x02DD CHARAC_NAME_CHANGE_LOG** `[0]` 空改名记录(`:60-72`);mode 1 → USERINFO subtype1 全量(`:118-141`);mode 3 → USERINFO subtype3(`:143-153`)。发包全程复核世代,变了即中止(`:666-693`)。
  - mode 2=自己名册:NOTI 2 名册(信封第 7 字节携带 routingByte,`BuildPacketWithRouting` `:898-909`)+ **CMD 0x0286 DAILY_CHALLENGE ack** `[0x00,0x04]`(`:721`)+ 成长胶囊 NOTI EXP(`:722-723`)。
  - 名册体:`[u8 subtype=2][u16 槽上限][u16 Gate2][u8 0][i32 0][u16 unk][i32 unk][u16 角色数] + N×角色项`(`Network/Builders/Init/AccountCharacterListBodyBuilder.cs:32-74`);角色项 `[slot u16][dstr 名][2B][job u8 低4位][growType u8][level u8][0x0B][0x01][2×u32][u8 外观数]+外观项+克隆称号尾`(`:77-108`)。**进号主序列不发 subtype2 名册**,名册只在 GET_USERINFO(mode2)与建/删/换槽后发(`:594`)。

### CMD 0x01A8 (424) `OTHER_USER_TITLE_BOOK_LIST` — 查看他人称号簿
- **请求**: `[targetUid u16]`(0xFFFF 忽略)。
- **应答**: NOTI 0x0166 TITLE_BOOK_LIST 系列包(逐包世代校验,`CharacterSelectHandler.cs:809-887`;构建 `OtherUserInfoResponseBuilder.BuildTitleBookList` `:163-186`)。

### CMD 0x0310 (784) `RE_GROWUP_CHANGE` — 成长类型变更(觉醒分支切换)
- **请求**: body[14] = targetGrowType(前 14B 为客户端上下文,不解析)(`Network/Parsers/Characters/GrowupChangeRequestParser.cs:8-25`)。
- **应答**: `[u8 1][resultCode i32][changeCount u8]`(`Network/Builders/Characters/GrowupChangeAckBuilder.cs:10-18`)。
- **行为**: 须持有本人背包 lease(`Network/handlers/GrowupChangeHandler.cs:40-49`);`GrowupChangeApplicationService.TryChange` 变更 GrowType/变更次数/扣金币/移除受影响任务(`:51-71`);成功更新 Player 字段、金币变化推更新、任务刷新(`:57-67`);非法请求回 InvalidState 应答(`:29-37,97-109`)。

### CMD 0x0009 (9) `RECOVER_STAMINA` — 疲劳虚弱恢复
- **请求**: 不解析(仅日志);需要在线背包 lease,否则错误码 4(`Network/handlers/StaminaHandler.cs:39-49`)。
- **应答**: 成功推送 **NOTI 0x0021** `[100]`(体力回满显示,`:133`)随后金币更新(+契约免费时再推魔王契约刷新,`:134-142`);失败 `[0x00][errCode][0x00]`(`:165-171`)。错误码:**4**=无背包/事务失败/异常,**18**=无虚弱状态(`:53-59`),**22**=金币不足(`:83-89,100-103`)。
- **行为**: 费用 `basePrice(等级) × (100-(100-min(10,虚弱)×9))/90`(`:153-163`);恶魔契约按 slot 免单(`:76-82`);金币扣减与 subtype0 虚弱清零同事务提交(`:71-118`);成功同步内存尾字段 Stamina/FatiguePenalty=0(`:127-131`)。

### CMD 0x02B5 (693) `CHECK_DOUBLE_CHARACTER_NAME` — 重名预检
- **请求**: `[nameLen i32][name]`(≤30B,≥5B)。
- **应答**: 可用 `[0x01]`;`0x02`=短包、`0x14`=长度非法、校验码=非法字符、`24`=已存在(`CharacterSelectHandler.cs:911-952`)。无 DB 写。

### CMD 0x0127 (295) `CHANGE_CHARAC_SLOT` — 角色槽位交换
- **请求**: `[slotA u32][slotB u32]`,≥8B(`:1186-1195`)。
- **应答**: `[0x01]` + **NOTI 2 名册**(`:1201-1202`);`SwapSlotIndexes` 落库(`:1198`)。

## 6. settings 组(注册 `GameProtocolHandler.cs:797-804`)

会话归属统一取 `(CharacterId>0?cid:0, AccountId)`(`Network/handlers/SessionOwnerResolver.cs:8-13`)。

### CMD 0x00C5 (197) `SAVE_GAME_OPTION_1` — 主游戏设置保存
- **请求**: `[len i32][blob]`,len≤body-4(`SettingsHandler.cs:34-41`);按角色/账号两份投影拆分(`:42-43`)。
- **行为**: 若本会话在 CMD 0x01C0 后待应用可见性位(`:20,48-51`):把角色级可见位合入 `UserStateBits` 持久化并同步尾字段(`:53-62`);位变化时推 **NOTI 0x0165** 可见性(自+同区广播,`:66-83`)+ **NOTI 2** 自外观(`:85-88`)+ **NOTI 0x0187 CHARACTER_OPTION** 投影(`:89-95`);否则/同时仅存账号主设置(`:100,106`)。末尾调 `TryCompletePendingLoginSuccessAsync` 驱动登录门控(`:110`;计数逻辑 `LoginHandler.cs:128-143`)。
- **备注**: 角色位只取 01C0 之后的 00C5(`SettingsHandler.cs:66` 注释)。

### CMD 0x00C6 (198) `SAVE_GAME_OPTION_2` — 快捷键设置保存
- **请求**: `[len i32][blob]`。角色在线:存角色 hotkey 配置+账号级 hotkey 槽(`SettingsHandler.cs:113-129`);未选角:仅账号级(`:131-132`)。无应答。

### CMD 0x0170 (368) `SAVE_GAME_OPTION_QUICKCHATTING` — 快捷喊话保存
- **请求**: `[bank u8(≤1)][len i32][blob]`,≥5B。按账号存 bank(`SettingsHandler.cs:135-150`)。无应答。

### CMD 0x00FE (254) `CHANGE_EMOTION` — 心情变更
- **请求**: `[mood u16]`,≥2B 且已选角(`SettingsHandler.cs:152-161`)。
- **应答/推送**: **NOTI 2** 自外观 body(心情在 USERINFO0 64B 尾 +59,`SettingsHandler.cs:164-174`)。落库角色状态(`:162`)。

### CMD 0x01C0 (448) `SAVE_CHARACTER_OPTION` — 角色选项保存
- **请求**: 原始 blob,不解析内部(投影 `AccountSettings.ProjectCharacterOptionBlob` 并合入当前可见位,`SettingsHandler.cs:179-194`)。
- **行为**: 按角色持久化,并给会话打"待应用可见性位"标记,下一个 0x00C5 触发 NOTI 0x0165/NOTI 2/NOTI 0x0187 下发(`:188-193`、`:48-103`)。无直接应答。

## 7. misc(域内注册项,`GameProtocolHandler.cs:986-1034`)

- **CMD 0x0003 (3) `EXIT`**:恒回 `[0x01]`(`:988-989`)。
- **CMD 0x02A8 (680) `LOAD_EXTEND_CHARACS`**:桩,恒回 2B `[0x00,0x00]`(`:994-995`)。
- **CMD 0x0252 (594) `SECURITY_STATUS`**:副本加载期上报,恒回 6B 零体,不改状态(`:1033`,构造 `:444-455`)。

## 8. 选角后初始化包序列

- **驱动**:`CharacterSelectHandler.HandleResolvedSelectCharacterAsync` 调 `SelectCharacterPacketBuilder.BuildPacketStream`(静态模板 `Game/SelectCharacter/NewCharacterInitSequence.cs:29-109` × 注册表 `Network/Builders/Init/InitPacketBuilderRegistry.cs:28-110`),逐包包 15B 信封发出(`SelectCharacterPacketBuilder.cs:532-539` 为调用点、`:46-198` 为流构建)。快照由 `dataSource.Load(cid,aid)` 装载(`:56`)。
- **机制**:模板 cmd=0x01 走 `registry.TryBuildCmd`、cmd=0x00 走 `registry.TryBuild(notiType, snapshot, occurrenceIndex)`(`:127-137`);builder 存在但数据缺失→SKIP(不出包),无 builder→ERROR 日志(`:160-165`)。特例:NOTI 0x0245 守护盾仅合格职业且有 deck 快照才发(`:71-86`;`Network/Builders/KnightShieldDeckBodyBuilder.cs:7,20`,5×u32 槽 0 主盾);NOTI 0x019F 支援兵空态(≤2B)整包跳过且流末尾兜底注入(`:88-118,168-187`);NOTI 0x01C0 黑暗骑士连招仅 job==9,模板缺失时在 0x0013 后内联注入、否则流末补发(`:143-156,189-197,200-210`);NOTI 0x03D8 occ0=增率抽奖空态、occ1=双人房间常量体(`InitPacketBuilderRegistry.cs:118-124`)。`SimpleByteBodyBuilder`=1B 包体,取值为快照状态字段(`Network/Builders/Init/GenericBuilders.cs:10-27`)。
- **序列**(cmd 列:C=CMD 应答 0x01 / N=NOTI 推送 0x00):

| # | cmd | 编号 | 枚举名/含义 | builder(相对 Network/Builders/) |
|---|---|---|---|---|
| 1 | C | CMD 0x0004 (4) | 选角 ack(§5 字段表) | `Init/SelectCharacterAckBodyBuilder.cs:22`(注册表特判 `Init/InitPacketBuilderRegistry.cs:132-147`) |
| 2 | N | NOTI 0x0002 (2) `USERINFO` occ0 | subtype0 角色主体 | `Init/UserInfoBodyBuilder.cs:40-44` |
| 3-7 | N | NOTI 0x0166 (358) `TITLE_BOOK_LIST` occ0-4 | 称号簿(5 次分段) | `Init/TitleBookListBodyBuilder.cs:9` |
| 8 | N | NOTI 0x02BF (703) `STORY_BOOK_INFO` | 故事簿开启回放 | `Init/StoryBookInfoBodyBuilder.cs:23` |
| 9 | N | NOTI 0x0167 (359) `ACHIEVEMENT_LIST` | 成就列表 | `Init/AchievementListBodyBuilder.cs:8` |
| 10 | N | NOTI 0x0015 (21) `ACCEPTABLE_QUEST_LIST` | 可接任务列表 | `Init/QuestListBodyBuilder.cs:14` |
| 11 | N | NOTI 0x0164 (356) `CLEAR_QUEST_LIST` | 已完成任务列表 | `Init/ClearQuestListBodyBuilder.cs:11` |
| 12 | N | NOTI 0x0013 (19) `SKILLINFO` | 技能页快照 | `Init/SkillInfoBodyBuilder.cs:8` |
| 13 | N | NOTI 0x0069 (105) `CREATURE_ITEM_LIST` | 宠物/生物列表 | `Init/CreatureListBodyBuilder.cs:8` |
| 14 | N | NOTI 0x0002 (2) `USERINFO` occ1 | subtype1 装备/技能全量 | `Init/UserInfoBodyBuilder.cs:16-37` |
| 15 | N | NOTI 0x0003 (3) | 城镇用户状态 `[1][cid u16][0]` | `Init/GenericBuilders.cs:46-69` |
| 16 | N | NOTI 0x00CA (202) `VILLAGE_OBJECT_LIST` | 城镇对象(SimpleByte 恒 0) | `Init/InitPacketBuilderRegistry.cs:43` |
| 17 | N | NOTI 0x0005 (5) `DUNGEON_PERMISSION` | 副本进入权限 | `Init/DungeonPermissionBodyBuilder.cs:10` |
| 18-23 | N | NOTI 0x000D (13) `ITEM_LIST` ×6 | 背包:Main/Avatar/PersonalCargo/Pet/AccountCargo/GuildMedal | `Init/ItemListPacketBuilder.cs:9`(特例 `SelectCharacterPacketBuilder.cs:120-125`) |
| 24 | N | NOTI 0x0245 (581) `SEND_DECK_INFO` | 守护盾 5 槽(条件发送) | `Network/Builders/KnightShieldDeckBodyBuilder.cs`(`SelectCharacterPacketBuilder.cs:71-86`) |
| 25 | N | NOTI 0x0465 (1125) | 史诗图鉴碎片 `[0][count u32][n×(id u32,value u32)]` | `Init/GenericBuilders.cs:146-193` |
| 26 | N | NOTI 0x021E (542) `SET_USABLE_COUNT_INFO` | 可用次数道具 `[count u8][n×(id i32,value i32)]` | `Init/GenericBuilders.cs:195-219` |
| 27 | N | NOTI 0x0286 (646) `DAILY_CHALLENGE` | 每日挑战快照 | `Init/DailyChallengeBodyBuilder.cs:8` |
| 28 | N | NOTI 0x00AD (173) `GAME_OPTION` | 主设置+快捷喊话 3 段;无存档跳过 | `Init/GameOptionBodyBuilder.cs:8-33` |
| 29 | N | NOTI 0x0002 (2) `USERINFO` occ3 | HOTKEY 前再推 subtype0 | `Init/UserInfoBodyBuilder.cs:39-44` |
| 30 | N | NOTI 0x01C7 (455) `HOTKEY_OPTION` | 快捷键(恒发,防跨角色键位残留) | `Init/HotkeyConfigBodyBuilder.cs:9` |
| 31 | N | NOTI 0x0030 (48) `PVP_RECORD` | PVP 战绩 | `Network/Builders/Pvp/PvpRecordBodyBuilder.cs` |
| 32 | N | NOTI 0x031B (795) `PVP_TOTAL_MATCH_TEAM_INFO` | 全局匹配战队 | `Network/Builders/Pvp/PvpTotalMatchTeamBodyBuilder.cs` |
| 33 | N | NOTI 0x006C (108) `EVENT_INFO` | 活动信息(DB) | `Init/EventInfoBodyBuilder.cs:25` |
| 34 | N | NOTI 0x0187 (391) `CHARACTER_OPTION` | 角色选项投影 | `Init/CharacterOptionBodyBuilder.cs:9` |
| 35 | N | NOTI 0x01B9 (441) `INFORM_NOTICE_2ND` | 成长武器 | `Init/GrowthWeaponBodyBuilder.cs:7` |
| 36 | N | NOTI 0x015F (351) `TOTAL_ITEM_MAKING_SKILL_POINT` | 制作技能点槽位 | `Init/SkillPointSlotBodyBuilder.cs:9` |
| 37 | N | NOTI 0x00AC (172) `LOAD_COOLTIME_ITEM_INFO` | 冷却道具 `[count u8][n×(id i32,expire i32)]` | `Init/ItemStateListBodyBuilder.cs:14-33` |
| 38 | N | NOTI 0x00AE (174) `LOAD_EFFECT_ITEM_INFO` | 效果道具(同上布局) | 同上 |
| 39 | N | NOTI 0x01E8 (488) `CHARACTER_ADD_BUFF` | 疲劳药 buff | `Init/EpicBuffPotionInitBodyBuilder.cs:8` |
| 40 | N | NOTI 0x017B (379) `AURA_TYPE_COOLTIME` | 固定 `[2,0,0,2,0]` | `Init/ShowEffectBodyBuilder.cs:9` |
| 41-43 | N | NOTI 0x03EB (1003) `COLLECT_BOX` occ0-2 | 收集箱(3 段) | `Init/CollectionBoxBodyBuilder.cs:18` |
| 44 | N | NOTI 0x021F (543) `CARD_BOOK_INFO` | u32 0 占位 | `Init/BossTowerBodyBuilder.cs:12` |
| 45 | N | NOTI 0x03C1 (961) `EQUIPMENT_RENTAL_LIST` | 租赁:幸运星+条目(模板 ID+到期秒) | `Init/RentalInfoBodyBuilder.cs:21,54` |
| 46 | N | NOTI 0x00FB (251) `ITEM_LOCK_LIST` | 物品锁列表 | `Init/ItemLockListBodyBuilder.cs:8` |
| 47 | N | NOTI 0x00CD (205) `EXPERT_JOB_INFO` | 副职业信息 | `Init/ExpertJobInfoBodyBuilder.cs:9` |
| 48 | N | NOTI 0x019F (415) `TAG_CHARACTER_INFO` | 支援兵(空则跳过,末尾兜底) | `Init/StrikerSupportTagCharacterBodyBuilder.cs:21` |
| 49 | N | NOTI 0x00B1 (177) `SHOP_COIN_EVENT` | SimpleByte=复活币当日领取标记 | `Init/InitPacketBuilderRegistry.cs:61` |
| 50 | N | NOTI 0x036A (874) `CONTRACT_OF_CUBE_INFO` | 立方契约信息 | `Init/CubeInfoBodyBuilder.cs:9` |
| 51 | N | NOTI 0x032F (815) `PREMIUM_SERVICE` | 魔王契约(进号必须主动推) | `Init/PremiumServiceInitBodyBuilder.cs:8` |
| 52 | N | NOTI 0x03D8 (984) occ0 | 增率抽奖固定 204B 空态 | `Init/LotteryBufferBodyBuilder.cs:8-28` |
| 53 | N | NOTI 0x025B (603) `SEQUENTIAL_DUNGEON_INFO` | 连锁攻坚系统 | `Init/ChampionBreakSystemBodyBuilder.cs:8` |
| 54 | N | NOTI 0x0331 (817) `LIMIT_IPGNO` | SimpleByte=携带金币扩容等级 | `Init/InitPacketBuilderRegistry.cs:63` |
| 55 | N | NOTI 0x039B (923) `UPGRADE_CARRY_GOLD` | SimpleByte=同上 | `Init/InitPacketBuilderRegistry.cs:64-66` |
| 56 | N | NOTI 0x01EB (491) `USER_FATIGUE_ACCELERATION_STATE` | 疲劳加速状态 | `Init/FatigueAccelBodyBuilder.cs:10` |
| 57 | N | NOTI 0x0061 (97) `MAILBOX_MAIL_LIST` | 邮件列表(空箱 6B 0) | `Init/MailboxBodyBuilder.cs:11,37` |
| 58 | N | NOTI 0x0158 (344) `RESET_ONE_A_DAY_ITEM_SHOP` | 每日商店重置 | `Init/PvpMissionBodyBuilder.cs:8` |
| 59-61 | N | NOTI 0x02D5 (725) `ACCOUNT_TUTORIAL_INFO` occ0-2 | 账号教程信息(3 段) | `Init/DailyScheduleBodyBuilder.cs:8` |
| 62 | N | NOTI 0x01A8 (424) `RESET_CRANE_NEED_MATERIAL` | SimpleByte=PC 房游玩状态 | `Init/InitPacketBuilderRegistry.cs:62` |
| 63 | N | NOTI 0x0009 (9) `PARTY_INFO` | 空队伍 `[0,0]` | `Init/SelectEnterMinimalBodyBuilders.cs:6-16` |
| 64 | N | NOTI 0x0344 (836) `BUY_RESTRICT_ITEM_LIST` | 限购列表 | `Init/BuyRestrictItemListBodyBuilder.cs:8` |
| 65 | N | NOTI 0x007C (124) `ENTER_GAMEWORLD_COMPLETE` | 8B `[1,0,0,0,1,0,0,0]` | `Init/SelectEnterMinimalBodyBuilders.cs:19-29` |
| 66 | N | NOTI 0x0002 (2) `USERINFO` occ2 | subtype6(25B,见下) | `Init/UserInfoBodyBuilder.cs:46-51` |
| 67 | C | CMD 0x01BA (442) `MERCENARY_INFO` | 佣兵列表推送 | `Init/MercenaryInfoCmdBodyBuilder.cs:12` |
| 68 | N | NOTI 0x0035 (53) `CERA` | `[1][cera][tokenCera][happyToken]` i32×3 | `Init/GenericBuilders.cs:225-244` |
| 69 | N | NOTI 0x0111 (273) `UNITED_SERVER_FRIEND_INFO` | 全量好友快照(兜底 8B 空态) | `Init/GenericBuilders.cs:74-119` |
| 70 | N | NOTI 0x0016 (22) `USER_POSITION` | `[cid u16][x][y][dir][100 u16]` | `Init/GenericBuilders.cs:126-143` |
| 71 | N | NOTI 0x0077 (119) `CREATURE_SCRIPT_MESSAGE` | 宠物欢迎语(无则跳过) | `Init/PetCreatureWelcomeMessageBodyBuilder.cs:9` |
| 72 | N | NOTI 0x03D0 (976) `WEDDING_INFO` | 婚姻信息 | `Init/WeddingReplayBodyBuilders.cs:11` |
| 73 | C | CMD 0x03F3 (1011) `WEDDING_CHARAC` | 婚姻常量回放体 | `Init/WeddingReplayBodyBuilders.cs:30` |
| 74 | N | NOTI 0x0326 (806) `DIMENSION_GATE_ENTRANCE_INFO` | 异次元门入口 | `Init/DimensionGateEntranceInfoBodyBuilder.cs:23` |
| 75 | N | NOTI 0x03D8 (984) `COUPLE_ROOM` occ1 | 双人房间常量体 | `Init/InitPacketBuilderRegistry.cs:120-124` |
| 尾 | N | NOTI 0x01C0 (448) `COMBO_SKILL_INFO` | 黑暗骑士连招(仅 job==9) | `Init/DarkKnightComboSkillInfoBodyBuilder.cs:8`(`SelectCharacterPacketBuilder.cs:189-210`) |

- **包流之后**(`CharacterSelectHandler.cs:541-597`):每日补充道具入包并逐槽推背包更新;**NOTI 0x0165** 可见性 `[uid u16][bits u8]`;CMD 0x0239 克隆称号 ack;**NOTI 0x0289** 名誉等级;**NOTI 0x0025** 成长胶囊。

### USERINFO(NOTI 0x0002)各 subtype 差异

- **分发**:`UserInfoBodyBuilder` 按 occurrence 出包:occ0/3→subtype0,occ1→subtype1(带 17B 前缀),occ2→subtype6(`Network/Builders/Init/UserInfoBodyBuilder.cs:11-56`)。
- **subtype0**(角色主体,选角/城镇实体):`[u8 0][u16 1][38B 固定头][uid u16][dstr 名]` + `BuildRemainingBytes`:`job/growType/level/pvpGrade/pvpRatingGrade/userState` 6B + 外观列表 `[count u8]+n×(slot u8, itemId i32, expLen i32, exp 4B, state u8, linkId i32, enchant u32, flag20 u8)` + 尾字段(克隆称号/锻造/宠物三件套等)+ **A21 专有 64B 无行会 blob**(副职业类型/经验 @23/24、名誉等级/经验 @39/43、UserStateBits @47、心情 @59、技能树 @61;有行会时 6B 后接行会名 dstr+等级 u32,`Network/Builders/Init/UserInfoSubtype0Builder.cs:21-92,104-174`;外观项 `:230-241`)。在线背包动态字段(称号/宠物)实时投影(`:94-101`)。
- **subtype1**(本人装备/技能全量):前缀 `[u8 1][u16 1][u32 成长胶囊经验][u16 0][u32 名誉等级][u32 名誉经验][u8 光环皮肤][u16 cid]`(`UserInfoBodyBuilder.cs:58-75`)+ `[exp i32][statBlobLen i32=CombatStatBlobWriter.BlobLength][战斗 stat blob][exEquipSlotStat u8][装备数 u8]+n×101B/131B 完整 ItemCore 条目 + 克隆称号/名称卡/到期 + 技能树 + 2 技能页(count u8 + n×(skillId u16, level u8)) + 宠物等级 + 特殊副本入场次数尾 + 5B 常量 + 特殊奖励任务 + 经营等级/点数`(`Network/Builders/Init/UserInfoSubtype1Builder.cs:19-144`)。
- **subtype3**(城镇查看他人,GET_USERINFO mode=3):`[u8 3][u16 1][u16 经营等级][5B 0][uid u16]` + 与 subtype1 同构的 exp/stat blob/装备/称号/技能页/宠物等级,之后是 `6×u32+3B` 检视上下文 + 行会名 dstr+等级+`0x6F` 标记 + 特殊奖励任务 + 1B(`Network/Builders/Init/UserInfoSubtype3Builder.cs:20-82`)。与 subtype1 的差异:无 subtype1 前缀(改为 manageLevel 头)、无特殊副本尾、多检视上下文段。
- **subtype6**(进号 25B 心跳态,occ2):`[u8 6][u16 1][u16 cid][u32 0xFFFFFFFF][u32 0x68624229][u32 0xFF][u32 0xFF][u32 0]`(`Network/Builders/Init/UserInfoSubtype6Builder.cs:7-35`)。