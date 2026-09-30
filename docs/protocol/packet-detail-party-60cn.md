# 组队包格式(60A1)

> **实现状态**:服务端组队已有雏形实现(实现代码 `Game/Party/`、`Network/PartyCoordinator.cs`,自测见 `PartySmokeTests`);真机复测尚未完成。本文档是客户端协议逆向记录,各 CMD/NOTI 的 ✅ 仅表示字段布局已逐 case 在 IDA 验证,不代表服务端功能复测通过;下文涉及服务端行为的描述以实现代码与 CHANGELOG 为准。功能完成度以 README「已知缺口」为准。
>
> 状态索引见 [client-packet-enums-60cn.md](client-packet-enums-60cn.md);帧头/加解密约定见 [packet-frame-crypto-60cn.md](packet-frame-crypto-60cn.md)。
> 全部字段布局于 客户端主程序 IDA 库(ImageBase 0x400000)内逐 case 验证,服务端实现见
> `Game/Party/PartyState.cs`、`Game/Party/PartyManager.cs`、`Network/PartyCoordinator.cs` 与
> `Network/Builders/Party/PartyPacketBuilders.cs`(CMD 10-14 解析、NOTI 6-10 构建)与
> `Network/Protocol/PartyWireModels.cs`(报文模型)。
>
> 与 A21(ServerS4A21)实现的差异以【差异】标注——照搬 A21 布局会在本客户端上错位。

## 流程概览(参考 ServerS4A21,按本客户端验证收敛)

```
建队/改设置   C→S CMD 12 ──────────────► S→C NOTI 9 type=0(建队, 仅创建者)/type=1(改设置, 全队)
                                  └────► S→C NOTI 11 端点表(全队, 含建队时的单人表)
邀请/申请     C→S CMD 10 (u16 目标,0,u32) ► S→C NOTI 7 (11B 弹窗, 发给目标)
接受          C→S CMD 11 (u16 邀请人,0,0) ► S→C NOTI 8 (7B 回执, 发给邀请人) + NOTI 2 mode=3 互发(对端上下文)
                                          + NOTI 9 type=0(全队) + NOTI 11(全队) + NOTI 26
拒绝          C→S CMD 11 (type/值非 0)   ► (服务端撤销邀请, 无封包)
退队          C→S CMD 13 (空)            ► 自己 NOTI 9 type=3; 幸存者 NOTI 10 + NOTI 9 type=3(换代时)+type=0 + NOTI 11
踢人          C→S CMD 14 (u8 槽位)       ► 被踢者 NOTI 9 type=3; 幸存者 NOTI 10 + NOTI 9 type=0 + NOTI 11
UDP 上报      C→S CMD 2  (NAT 上报)      ► 若已在队: 全队 NOTI 11 刷新
房主指定      (成立队伍时)               ► S→C NOTI 26 (u8 队长槽位; 没有它客户端不启动对端数据报路径)
```

- **NOTI 11 与"连接中"**:队员名条上的"连接中"只有在客户端拿到队友 UDP 端点(NOTI 11)并完成连接后才会清除。漏发即全员长期"连接中",组队实时位置走不了 P2P 通道,只剩服务端 1 Hz 的 NOTI 22 镜像(表现为位置卡顿)。
- **服务器中继**:客户端之间的直连 UDP 常被对方防火墙拦截(client→server 的 7002/7003 是通的)。因此队员记录统一通告**服务器的 gameplay 监听端点**(`GameplayDatagram.Host:PrimaryPort`,inner/outer 同值),`GameplayDatagramService` 收到成员的 MT-UDP 数据报后按 `_sessionsByEndpoint` 定位发送者、经 `PartyCoordinator.GetPartyPeerSessionIds` 转发给同队其他成员——客户端拨号的对端与收到数据的来源端点一致,握手可完成。
- **USERINFO mode 3(对端上下文,消除"连接中"的根因)**:NOTI 2 的 mode 3 分支(0x41A7DD)为任意用户创建 user+0x110 对端连接对象——没有它,客户端不会对该成员发起任何 P2P 握手,"连接中"直到超时。服务端在每次队伍成员集变化时,把每个成员的 mode-3 详情快照(mode 字节=3,body 与 mode 1 相同,含经验/技能/战斗属性/穿戴/宠物)发给其他成员;快照由各会话经 `CharacterSessionRegistry.AttachDetailsProvider` 现场构建(只有本连接掌握实时技能/属性/背包)。注意 A21 文档"60CN 无 subtype 3"的说法不成立。
- **对端握手只在副本入口发起(实测+反汇双重确证)**:组队成立后客户端在城镇**不会**朝任何端点发 UDP 数据报(2026-09-28 双客户端实测:组队后零数据报)。触发点是副本门口流程(~0x89F560):全员过场景检查 → 发 CMD 15 → 对每成员 `user+0x110` 对端对象调 `vtable+0x188(0)`。因此城镇里的"连接中"属该客户端的常态展示,消除它需要组队进副本 cohort 流程(队长 CMD 15/选图拉队员、同房间 P2P),见回填计划。

- 队长没有独立字段:客户端从花名册尾字节读队长槽位(NOTI 9 @0x41BAD7 存入 `party+0x34`)。
- **队名只有预设可显示**:下行设置块只带 `titleKind + userMax`,队名由客户端本地渲染(预设表 / 本地串『无队伍名』),自定义队名无下行通道;改名请求的 userMax 字节还可能越界(客户端回读自存容量)。两点详见 CMD 12 节下「队名显示与 titleKind 语义」「userMax 越界」。
- 邀请既可以是"邀请无队玩家"也可以是"向队长的队伍申请加入",由服务端 pending 邀请的两个 PartyId 决定归宿(同 A21 三分支)。
- 队长退队时队伍代号(PartyId)必须换代:本客户端把 type=0 当作按槽位 0..3 的有序差分应用,复用旧代号会损坏名册;换代后先对幸存者发 type=3(旧 id)再发 type=0(新 id)。
- 断线清理与副本回城保留代号,最低占用槽位继任队长。

## CMD 10 `ENUM_CMDPACKET_REQUEST_PEER` ✅

| 偏移* | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 2 | u16 | targetUid | 目标(城镇名单中的 uid) |
| 4 | u8 | type | 0=组队邀请/申请;1=交易、2=PvP 房间(本服未实现,静默拒绝) |
| 5 | u32 | peerId | 客户端生成,NOTI 7 原样回显 |

客户端写入点 `sub_4FE9B0`(0x4FE9B0:begin(10) + 2+1+4 字节)。
**应答**:成功 `CreateCommandReply`(`[1]`);失败 `CreatePeerCommandError`(`[0,0,code,0]`,code=5 时客户端弹提示,case 0x4102F6)。

## CMD 11 `ENUM_CMDPACKET_RESPONSE_PEER` ✅

| 偏移* | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 2 | u16 | peerUid | 邀请人 uid |
| 4 | u8 | type | 0 且值非零判断见下 |
| 5 | u32 | value | 接受=0;拒绝=非零(type 或 value) |

接受写入点 `sub_4FEBC0`(0x4FEBC0,固定 7 字节载荷);拒绝写入点 0x821E46(同 7 字节,type/value 非零)。
**应答**:成功 `[1]`;失败 `[0,0,1,0]`(code=1 弹提示,case 0x4104FF;该 case 在失败时还读 u16+u8,故填充 4 字节)。

## CMD 12 `ENUM_CMDPACKET_SET_PARTY_INFO` ✅【差异:A21 是 12 字节设置块,本客户端不是】

| 偏移* | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 2 | u8 | titleKind | 0=自定义队名(后随 dstr);1..6=预设名。预设表 `dword_B75A64[0..5]` 共 **6** 条,文案取客户端串表 id 0x1AA..0x1AF(装入点 0x993120..0x99317D) |
| 3 | u32+bytes | title | 仅 titleKind==0:u32 长度 + CP936 字节,容量 0x40 |
| — | u8 | userMax | 人数上限,界面单选 = 槽位+2,即 2/3/4;对已存在队伍重开创建窗口时该字节取自客户端自存队伍记录,可越界(实测 0x30),见下「userMax 越界」 |

写入点 0x7D5F86(begin(12) 之后逐段 push;自定义名经 `sub_42DAD0` 打包为 u32 长度 + CP936 字节)。titleKind 由编辑框文本与预设表逐条比对得出(0x7D5E3A..0x7D5E85):命中某条预设即用该索引,否则为 0 并随行携带文本。建队与改设置同型(同一个窗口,串表 0x468『变更队伍名或修改组队的限制人数』);改设置要求请求者是队长且 memberCount ≤ userMax。
**应答**:成功 `[1]`;失败 `CreatePartyCommandError`([0,code,0,0])。失败 case 0x410799 对 **code=8** 弹串表 0x2CD『FAIL : 您不是房主,不能要求变更房间环境！』——该文案与"改名"无关,线上出现此弹窗即服务端拒绝了 SET_PARTY_INFO。

### 队名显示与 titleKind 语义(2026-09-29)

- **显示判定**(`sub_7D69E0`;同规则内联于 0x7D73AB..0x7D73BB):`titleKind != 0` → `dword_B75A64[(kind-1) % 6]`(六条预设队名);`titleKind == 0` → 队伍记录 +0x10 的名字串。
- **下行承载队名文本(2026-09-30 更正,推翻本节旧结论)**:NOTI 9 设置段读取端(0x41B8F9)先读 `u8 titleKind`;`titleKind == 0` 时经 `sub_8B3250`(0x41B955,cap=0x40,fallback=本地串 0x272)**无条件再读 `u32 长度`**,长度在 1..0x3F 时继续按长度读入 CP936 文本并写入队伍记录 +0xC 字符串对象(0x41B998),长度为 0 或 ≥0x40 时回退本地串 0x272『无队伍名』且不消费载荷字节;随后读 `u8 userMax`(0x41B964)。`titleKind != 0` 时跳过 dstr 直接读 userMax。旧结论把 0x41B955 误读为固定拷贝本地串,得出「下行不承载队名文本」,系误读;下行 dstr 与上行 CMD 12 的文本形态对称。
- 结论(更正后):**原版客户端原生支持 kind=0 的下行自定义队名**——设置段 kind=0 携带 `u32 长度 + CP936 文本`(1..0x3F 字节)即可在所有客户端直接显示,无需任何补丁;长度 0 表示无自定义名(显示『无队伍名』);预设名(1..6)仍由预设表渲染。2026-09-30 之前的「自定义名不可见」实为服务端未按此形态发送文本所致,据此撤销同日的 type 4 扩展块方案(见下节)。
- 客户端本地校验(0x7D5E9E..0x7D5F01):空名弹 0x461『请输入队伍名』、文本含 `%` 弹 0x462『含有无法使用的特殊字符』、容量小于当前人数弹 0x463『人员数过少』;建队成功后在聊天窗插一行 0x464『因创建了新队伍,系统将自动解除[拒绝组队]设置』(0x7D5FFE)。文本等于当前预设且容量未变时**不发包**直接关窗(0x7D5F50..0x7D5F6C)——复测时点重复预设看不到 CMD 12 属正常。

### userMax 越界:客户端回读自存容量,服务端按修正处理(2026-09-29)

- **成因**:创建窗口打开时容量取自 `*(队员记录 + 0xFC) + 0x3C`(`sub_7D6240` @0x7D6327;队员记录的 +0xFC 是所属队伍记录的反向指针,写入点 `sub_4FE690` @0x4FE715)。容量单选框本身只能产生 2..4(0x7D60E9 存 index+2),无队伍时窗口默认 4(0x7D635E)。
- **实测**(2026-09-29 抓包,单会话内对照):新建(无队伍)开窗发出的 userMax=4,服务端接受;对已存在队伍重开窗口发出的 userMax=**0x30 = 48**。修复前服务端按 2..4 校验拒绝,客户端弹上述 0x2CD 文案、改名整体失败(同一请求内的预设名改动也一并被拒);修复后按修正处理(见下条),该请求照常接受。
- **服务端处理**(`Game/Party/PartyManager.cs` → `SetPartyInfo`):越界不再作为拒绝理由——**建队按创建窗口默认 `PartyConstants.DefaultCapacity` = 4**,**改设置保留队伍现有容量**,修正值经 `PartyOpResult.RepairedUserMax` 回报并由 `PartyCoordinator` 记一条信息日志。有效容量但小于当前人数仍拒绝(正常客户端已被串 0x463 拦截)。
- **遗留项**:按本分册与 `client-function-register-60cn.md` §3 登记的队伍记录偏移(NOTI 9 settings 把 userMax 写入记录 +0x3C,0x41B9AF),开窗读取处本应读到同一字段;实测读到 48 说明该处读到的记录/反向指针另有来源,需动态验证(客户端断点或临时日志)后方可定论。

### 扩展块:自定义队名下行的承载(2026-09-29 实现,2026-09-30 撤销)

**撤销结论(2026-09-30)**:本方案建立在「下行不承载队名文本」的误读上——设置段 kind==0 分支的 `sub_8B3250`(0x41B955)本会无条件消费 u32 长度并按长度读入文本(更正见「队名显示与 titleKind 语义」),原生协议本就承载自定义队名。type 4 块在真实包里永远不会被分派:2 字节设置段 + type 4 块的组合下,kind==0 的 dstr 读取先吃掉后续 4 字节造成游标错位,扩展块从未到达类型分派点——该组合在**任何客户端(打不打补丁)上都不可用**,已由 2026-09-30 双端抓包与客户端插桩日志逐字节证实。服务端已改为在设置段内直接携带 dstr 文本(见 NOTI 9 节),`CustomTitleBlockType`/`WriteCustomTitle` 等扩展代码与对应 SelfTests 断言一并移除;客户端 `UsePartyCustomTitle` 接管与观测钩子、Config.ini 键及 `DAFLegacy.Ijl15.party-title.log` 诊断日志已随退役整体移除(2026-09-30),自定义队名由原生设置段路径承载,任何客户端无需补丁。以下为原方案历史记录,前提作废:

**目标**:让玩家在改设置窗口输入的任意自定义队名在客户端可见。改动前 kind=0 一律显示本地串『无队伍名』(见上「队名显示与 titleKind 语义」),服务端无任何下行字段可承载该文本。

**协议面**——NOTI 9 在同一包内追加一个**新块类型 4**,固定置于全部标准块之后:

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | partyId | 队伍代号,与既有块头同形 |
| 2 | u8 | blockType | 4 = 自定义队名块 |
| 3 | u32+bytes | title | u32 长度 + CP936 文本,与上行 CMD 12 的 dstr 同形(上限 0x40 字节) |

- **标准(未打补丁)客户端的降级行为**:块循环(0x41B7C3..0x41BB35)读 `u16 队伍号 + u8 块类型` 后按代号查队伍(`sub_4FEF70`);类型 4 不匹配设置段(0/1)、名册段(0/2)、清空段(3)三处分支,**不读任何载荷**即进入下一轮(公共块尾 0x41BB35),块计数用尽即结束。通知分发器**不做游标消费校验**(全局游标 `src` 仅被读取原语 `sub_8F5116..0x8F51D0` 触碰,分发器收尾 0x41C2C3 无比较),故尾部未读文本字节被忽略,标准客户端照常应用前序标准块,无名册错位风险。
- **补丁客户端的处理**:`Client/DAFLegacy.Ijl15` 把类型分派分支 0x41B8F3 接管到 `PartyCustomTitleHook`(`UsePartyCustomTitle` 开关):读块内 dstr(`sub_8F5160` 读 u32 长度 + `sub_8F5180` 读字节,按 0x40 上限截断保护缓冲区),再经 `sub_44D280`(队伍记录 +4)取到名字串(+8,即记录 +0xC)并以 `sub_402AD0`(std::string assign)写入——与 settings 段写本地串 0x272 同址;接管代码复刻原分支语义(类型 1 落设置段、其余跳设置段)并在处理后跳到公共块尾,UI 随之刷新。

**实现落点**:服务端 `Network/Builders/Party/PartyPacketBuilders.CreatePartyInfo`(仅 `TitleKind == 0 && TitleBytes.Length > 0` 时追加扩展块,`CustomTitleBlockType` / `MaximumCustomTitleBytes` 常量)与 `Network/PartyCoordinator.cs` 的建队、改设置两条发布路径(`BuildBlock` 携带 `TitleBytes`);客户端 `Client/DAFLegacy.Ijl15`(`dnf_patches.cpp` 接管与字节校验,开关为 Config.ini `[Features] UsePartyCustomTitle`,见 `ijl15_config`)。

**诊断**:补丁的安装与命中都会追加一行到客户端目录下 `Logs\DAFLegacy.Ijl15.party-title.log`(逐行 `installed` / `install skipped: …` / `entered block_type=… loop=下标/块数`(类型分派分支实际读到的类型字节与循环状态) / `header idx=… id=… type=…`(块头读取点每次迭代的循环下标、队伍代号与块类型;type 0 块走 find-or-create 且分派直跳设置段,绕过查找与分派两个观测点,此点为唯一全量观测位,游标错位时在此直接暴露异常 id/type) / `loop index=… count=…`(块循环头每次迭代的下标与计数) / `handled type4 len=… text_hex=…` / `lookup id=… block_type=…`(NOTI 9 每次按队伍代号查找的实参,含查找调用点接管后的全部调用方) / `abort: …`(块循环内两处静默中止:presence-check 出口与查找未命中),文本按十六进制记录,文件 1 MB 上限),用于免调试器核实部署、命中与块循环状态。观测点字节校验在写入前执行,任一观测点与原始字节不符即整体跳过并记 `install skipped: NOTI 9 observation bytes do not match`。

**验收标准**:① 打补丁客户端上,自定义队名在领队与队员端均显示所输文本,改回预设名后显示预设文案;② 标准客户端上,队伍名仍为预设文案或『无队伍名』,名册/容量/队长槽不受影响(NOTI 9 标准块字节与改动前完全一致);③ 服务端 SelfTests 全绿(含扩展块字节断言与 `protocol-golden` 字节等价守护);④ 真机双客户端复测:改名→双方显示,退队/踢人/断线重连/换代号后名字保持正确。

**状态**:服务端与客户端补丁均已实现,离线验证通过(全量重建 0 错误 0 告警;SelfTests 21 套件全绿;原生编译 0 警告、`ijl15` 兼容测试通过)。**真机复测未做**(需部署新构建与重建的 `ijl15.dll`),复测结论回填 `CHANGELOG.md`。

**备选(未采用,成本递增)**:① 零代码——改 6 条预设队名文案(客户端 `Plugin/dnf.str` 串表 0x1AA..0x1AF,项目已有外部串表注入开关 `UseExternalDnfStringTable`),玩家从 6 个模板中选用、所有客户端可见,但不支持逐队任意文本;② 客户端补丁(仅本机可见)——OK 处理保存所输文本 + 读取端 kind=0 改用该文本,领队本机可见、队友端仍显示『无队伍名』。

## CMD 13 `ENUM_CMDPACKET_LEAVE_PARTY` ✅

载荷为空(begin(13) 后直接 flush,0x7D12AC)。
**应答**:成功 `[1]`;失败 `[0,0x12,0,0]`(case 0x4107D8 对 code 0x12 弹提示)。

## CMD 14 `ENUM_CMDPACKET_WALKOUT_PARTY_MEMBER` ✅

| 偏移* | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 2 | u8 | slot | 目标成员的花名册槽位 0..3(选中 uid 反查槽位,0x8520C8) |

**应答**:成功 `[1]`;失败 `[0,4,0,0]`(case 0x410834 对 code 4 弹提示)。

*\*偏移相对服务端 `PacketFrame.Body`;Body 前 2 字节是传输层序号,业务字段从 offset 2 起。*

## NOTI 9 `ENUM_NOTIPACKET_PARTY_INFO` ✅【差异:4 槽位制,无 A21 的 8×5 名册与尾随 hasExtra】

解析 case `0x41B7A1..0x41C2F0`(全局游标读取器:u8=`sub_8F5120`、u16=`sub_8F5140`、u32=`sub_8F5160`、dstr=`sub_8B3250`)。

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | blockCount | 块数 |
| 2 | u16 | partyId | 块起始:队伍代号 |
| 4 | u8 | type | 0=完整编制 1=仅设置 2=仅名册 3=清除 4=自定义队名扩展块(2026-09-30 已撤销,不再下发) |
| — | — | 设置段(type 0/1) | **u8 titleKind;kind==0 时续接 u32 长度 + CP936 文本(1..0x3F 直接显示,0/≥0x40 回退本地串 0x272『无队伍名』且不消费载荷字节),再 u8 userMax;kind 1..6 直接 u8 userMax**(读取端 0x41B8F9,dstr 读取 `sub_8B3250` @0x41B955;2026-09-30 更正,旧「两字节」表述系误读) |
| — | — | 名册段(type 0/2) | 4 槽 × {u16 uid, u8 state(存 `member+0x1A`)};空槽 uid=0xFFFF(0x41BB1C 清槽写法);尾字节 u8 队长槽位 |
| — | — | type 3 | 无载荷,客户端把 4 槽全部置 0xFFFF(0x41BAF8) |
| — | — | type 4(已撤销) | 2026-09-30 起不再下发;原为本服扩展块(u32 长度 + CP936 文本),因设置段 kind==0 原生读 dstr 的更正而失去前提,见「队名显示与 titleKind 语义」更正与「扩展块」节撤销结论 |

【差异】type=0/1 设置段在 kind 1..6 时是两字节(kind, userMax),kind==0 时为 kind+dstr+userMax(与上行 CMD 12 的文本形态对称);不是 A21 的 12 字节块。type=3 只有 5 字节块头,无 A21 的额外空 dstr;名册每槽 3 字节而非 5 字节。
单块 body 长度(含 2 字节 blockCount):type2 = 18、type3 = 5;type0/type1 的设置段随 kind 变化——kind 1..6 时 type0 = 20、type1 = 7,kind 0 时为 20/7 + 4+L(L = 队名 CP936 字节数;空自定义名为固定 4 字节零长度)。

## NOTI 7 `ENUM_NOTIPACKET_REQUEST_PEER` ✅【差异:type 0 共 11 字节,非 A21 的 13 字节】

解析 case 0x41B4C8:`u16 uid, u8 type, u32 peerId`,type==0 追加 `u16 state, u16 state2`(存 `user+0x1C/0x20`,发 0 即可)。uid 必须能在本地城镇用户表命中(0x41B50F),否则弹窗被静默丢弃——因此邀请前双方必须已通过城镇存在交换(USERINFO 模式 0)互认。

## NOTI 8 `ENUM_NOTIPACKET_RESPONSE_PEER` ✅

解析 case 0x41B5F1:`u16 uid, u8 type, u32 value`,type 0 共 7 字节。type 0 渲染"加入"提示(串 0x270);type 1 额外读一个 u8(本服不使用)。服务端在 NOTI 9 编制下发之前把它发给邀请人,让其客户端先认领队伍单例(沿用 A21 顺序)。

## NOTI 10 `ENUM_NOTIPACKET_WALKOUT` ✅【差异:uid 是 u8】

解析 case 0x41C41A:`u8 uid, u8 reason(0..9)`。reason 0/1 渲染离队/被踢聊天条(串 0x277..0x27A)。uid>255 时截断,只影响提示文案查找,不影响结构。

## NOTI 26 `ENUM_NOTIPACKET_UDP_HOST`(房主指定)✅

解析 case 0x41F144..0x41F16C:`u8 槽位` → `sub_9054C0()` 取 UDP 管理器单例(0xD01D00)→ `sub_404360(槽位)` 存到 +0x28。这是客户端启动对端数据报路径的触发器:漏发时所有成员停在"连接中",客户端不会朝 NOTI 11 通告的端点发任何数据(A21 侧 `UdpRelayServer` 的注释同样把 UDP_HOST 列为客户端发包的前提)。服务端在每次队伍编制发布后随 NOTI 11 发送,取队长槽位。

## NOTI 11 `ENUM_NOTIPACKET_USER_UDP_IP_PORT`(组队端点表)✅

复用连接组的 0x0B(布局见 [packet-detail-connection-60cn.md](packet-detail-connection-60cn.md));组队语境下在每次成员变动后按全队重发。case 0x41C7E7 逐记录:
`u16 uid, u32 innerIp(原始 octets), u32 outerIp, u16 port, u32 accId, u8 nat, u32 mtu`,查不到的 uid 静默跳过(0x41C8EB)。
**端口字节序**:线上 u16 按小端读入(`sub_8F5140`),客户端随后 `htons()` 存入记录 +8(0x41C920)——即线上是小端、内存里转网络序,`CreateUdpPeerInfo` 的 LE 写法对本客户端正确(与 CMD 2 上报方向的大端不同,两处格式独立)。

## 前置:城镇存在交换

城镇用户表记录由 NOTI 2 USERINFO 模式 0 创建(0x419B3E 调 `sub_4FDF60` find-or-create,0x114 字节记录),
NOTI 23 USER_AREA / NOTI 24 AREA_USERS 只更新已存在记录(查不到即跳过,0x41EE9C);NOTI 6 USER_LEAVE(`u16 uid`)移除记录(0x41B467)。
因此 `PartyCoordinator` 在每次入城/切区时,把本连接构建好的外观包(USERINFO 模式 0)+ USER_AREA 交换给同区域在线玩家,离城/断线广播 USER_LEAVE。
这也是 CMD 37 SET_USER_POSITION 的镜像广播(NOTI 22)的落点。

## 附:客户端调用链(函数名 / 地址 / 功能)

> 约定见 [ida-call-chain-convention.md](ida-call-chain-convention.md);本章函数已回填全项目登记表 [client-function-register-60cn.md](client-function-register-60cn.md)。地址均指向 客户端主程序 IDA 库(ImageBase 0x400000)。

### 分发与传输层

| 函数/数据 | 地址 | 功能 |
|---|---|---|
| `sub_40D6D0`(尾部) | 0x40D6D0 | type 分发:`packet[0]=0`→`sub_419420`(NOTI 推送);`=1`→`sub_40F290`(CMD 应答) |
| `sub_419420` | 0x419420 | NOTI 分发器,switch(`packet[1]`),跳转表 `0x42A081`(4B/项,152 case;case 间边界按表项排序推算) |
| `sub_40F290` | 0x40F290 | CMD 应答分发器,protocolId 经 LUT `byte_417638[id-1]` 二次映射到跳转表 `0x417480`(159 槽);`p_tm_min`=应答载荷首字节,`0`=失败并继续读错误码 |
| `sub_8B2650`(sock, id) | 0x8B2650 | 开始构包:写 `[0]=type(1) [1]=protocolId`,12 字节头占位 |
| `sub_8B2450`(buf, len) | 0x8B2450 | 向当前包追加载荷字节 |
| `sub_8B24C0` | 0x8B24C0 | flush:写 `[2..5]`=u32 总长(载荷+12)、`[6..9]`=`sub_8B23B0` 校验和、`[10..11]`=u16 序号,再加密载荷 |
| `sub_8B23B0` | 0x8B23B0 | 出站校验和计算(覆盖序号+载荷) |
| `sub_8CAFD0` / `sub_8CACC0` | 0x8CAFD0 / 0x8CACC0 | 环形发送缓冲出队 → `ws2_32 send` 原始发送 |

### 读取原语(NOTI 解析共用全局游标 `src`)

| 函数 | 地址 | 功能 |
|---|---|---|
| `sub_8F5110` | 0x8F5110 | 设置全局游标(指向当前包载荷) |
| `sub_8F5120` | 0x8F5120 | 读 u8,游标 +1 |
| `sub_8F5140` | 0x8F5140 | 读 u16(LE),游标 +2 |
| `sub_8F5160` | 0x8F5160 | 读 u32(LE),游标 +4 |
| `sub_8B3250`(dst, cap, 默认串) | 0x8B3250 | 读 dstr:`sub_8F5160` 取 u32 长度后复制字节;长度 ≥ 容量或为 0 时内容不消费(长度字段是否仍前进存在两处不一致记录,见 [client-function-register-60cn.md](client-function-register-60cn.md) §2 注),调用方须保证长度 < cap |
| `sub_8AA5A0`(id) | 0x8AA5A0 | 按 id 取界面字符串(组队提示 0x270..0x27A、错误 0x2CD..0x2D0、建队校验 0x461..0x464) |

### 用户表(城镇用户记录,dword_B5DCB8)

| 函数 | 地址 | 功能 |
|---|---|---|
| `sub_4FDDB0`(mgr, uid) | 0x4FDDB0 | 按 uid 查找用户记录,未命中返回 0(NOTI 7/22/23/24 均先查,查不到即静默跳过) |
| `sub_4FDF60`(mgr, uid) | 0x4FDF60 | **查找或创建**用户记录(0x114 字节);USERINFO case 2 @0x419B3E 调用,是用户记录唯一创建点 |
| `sub_4FDDF0`(mgr, uid) | 0x4FDDF0 | 移除用户记录(NOTI 6 case @0x41B467 调用) |
| `sub_404300`(obj) | 0x404300 | 取记录数据区指针(member: +0x1A=名册 u8、+0x1C/+0x20=状态字;party: +4=titleKind、+8=队名、+0x34=队长槽、+0x38=userMax) |
| `sub_4FD490`(user, 0/1) | 0x4FD490 | 格式化 inner/outer IP 为 `"%d.%d.%d.%d"` 字符串(NOTI 11 记录尾部调用,仅显示用) |

### 组队管理器(dword_B5DCC8)

| 函数 | 地址 | 功能 |
|---|---|---|
| `sub_4FF040`(mgr, partyId) | 0x4FF040 | 查找或创建队伍槽(NOTI 9 type=0 路径) |
| `sub_4FEF70`(mgr, partyId) | 0x4FEF70 | 按代号查找队伍 |
| `sub_4FE690`(party, slot, uid) | 0x4FE690 | 设置槽位 uid(0xFFFF=清空;type=3 时循环清 4 槽 @0x41BAF8) |
| `sub_4FE660`(party, slot) | 0x4FE660 | 取槽位成员对象 |
| `sub_404320`(mgr, slot) | 0x404320 | 取槽位成员 uid(踢人反查 @0x8520D0) |
| `sub_4FE730`(party) | 0x4FE730 | 当前人数(容量校验 @0x4FE9B0、0x7D5EEF) |
| `sub_44D280`(party) | 0x44D280 | 取队伍设置数据区(PARTY_INFO 设置段落位) |
| `sub_4052D0`(dword_B5DCB8) | 0x4052D0 | 取本机用户记录(NOTI 9 中与队伍指针比较判"是否我的队伍") |

### 组队封包 case / 发送点

| 封包 | case / 发送函数 | 地址 | 说明 |
|---|---|---|---|
| NOTI 9 PARTY_INFO | case | 0x41B7A1..0x41C2F0 | 解析见上文;类型判定 0x41B8DE(0/1 设置段)、0x41B9B2(0/2 名册段)、0x41BAEC(3 清除) |
| NOTI 7 REQUEST_PEER | case | 0x41B4C8..0x41B5F1 | 弹窗经 `sub_799EF0(mgr, 0x25, user, type, 0)` @0x41B5BF |
| NOTI 8 RESPONSE_PEER | case | 0x41B5F1..0x41B7A1 | type0 渲染串 0x270;type1 读附加 u8 |
| NOTI 10 WALKOUT | case | 0x41C41A..0x41C7E7 | reason 0..9 子开关 @0x41C453 |
| NOTI 11 端点表 | case | 0x41C7E7..0x41C994 | 记录落位 inner+0/outer+4/端口+8(htons @0x41C920)/accId(`sub_417A80`)/nat+0xA/mtu(`sub_417A90`) |
| NOTI 6 USER_LEAVE | case | 0x41B3C8..0x41B4C8 | u16 uid |
| NOTI 23/24 | case | 0x41E9C7 / 0x41EC7C | 仅更新已存在用户(0x41EE9C 查不到跳过) |
| NOTI 26 UDP_HOST | case | 0x41F144..0x41F16C | u8 槽位 → `sub_9054C0`(=全局 0xD01D00 单例)→ `sub_404360` 存 +0x28 房主槽位 |
| CMD 12 发送 | 建队/改设置对话框 | 0x7D5F86(begin 后逐段 push) | 预设名表 `dword_B75A64[0..5]`;空名/含%校验 0x461/0x462;容量不足 0x463 |
| CMD 13 发送 | 城镇菜单退队 | 0x7D12AC | 空载荷 |
| CMD 13/14/10 发送 | 组队窗口 | 0x8520AB / 0x8520F1 / 0x852117 | case 2=退队、case 3=踢人(槽位反查 0x8520C8)、case 5=PvP 邀请(type 2) |
| CMD 10 邀请 | `sub_4FE9B0` | 0x4FE9B0 | 校验目标存在/未满员,错误文案 432..439;body=u16+u8(0)+u32 |
| CMD 11 接受 | `sub_4FEBC0` | 0x4FEBC0 | 7 字节 (u16,u8 0,u32 0);拒绝路径 @0x821E46(type/value 非零) |

### CMD 应答消费(`sub_40F290` case)

| case | 地址 | 失败分支读取 | 弹提示的错误码 |
|---|---|---|---|
| CMD 10 | 0x4102F6 | u8 + u8 | 5(payload[2]) |
| CMD 11 | 0x4104FF | u16 + u8 | 1(payload[3]) |
| CMD 12 | 0x410799 | u8(payload[1]) | 8 → 串 0x2CD |
| CMD 13 | 0x4107D8 | u8(payload[1]) | 0x12 → 串 0x2CE |
| CMD 14 | 0x410834 | u8(payload[1]) | 4 → 串 0x2D0 |

### NOTI 11 accId 字段

记录中的 u32 accId 经 `sub_417A80` 存入用户记录,未见参与连接校验(连接键为 uid+端点)。服务端按登录账号的真实 `AccountUid` 下发(`PartyMember.AccountUid`),不再使用占位值。

## 实现文件

- `Game/Party/PartyState.cs` — 常量(4 槽、0x40 队名上限)、成员与队伍快照
- `Game/Party/PartyManager.cs` — 建队/编辑/邀请/接受/退队/踢人/断线(线程安全)
- `Network/PartyCoordinator.cs` — 会话投递、城镇存在表、封包编排;队伍聊天按名册投递 `SendToParty`(格式与路由见 [packet-detail-chat-60cn.md](packet-detail-chat-60cn.md) CMD 17 type 2/11 节)
- `Network/Builders/Party/PartyPacketBuilders.cs` — CMD 10-14 解析、NOTI 6/7/8/9/10 构建;`CreateCurrentCharacterDetails(mode:3)` 对端上下文构建在 `Network/Builders/Characters/CharactersPacketBuilders.cs`
- `Game/Session/CharacterSessionRegistry.cs` — 跨会话封包通道(`AttachPacketChannel`)
- 自测:`DAFLegacy.Server.SelfTests/Game/Party/PartySmokeTests.cs`

### 参考项目 TURN 式中继(per-ordered-pair)评估(2026-09-29)

对照参考项目 `Network/Core/PartyUdpRelay.cs`(1,014 行,env `DFO_UDP_RELAY` 门控、默认关)+ `UdpRelayServer.cs`:

| 维度 | 本项目(现行) | 参考项目 |
| --- | --- | --- |
| 通告端点 | 所有队员统一通告服务器 gameplay 监听端点(单口,inner/outer 同值) | 每**有序对**(成员→队友)一个专属端口(固定端口段) |
| 转发归属 | 按 `_sessionsByEndpoint` 定位发送者,经 `PartyCoordinator.GetPartyPeerSessionIds` 广播给同队 | 按**到达 socket** 唯一确定 (owner→peer),无需端点匹配 |
| 设计动机 | 客户端拨号对端与回包来源端点一致,握手可完成(已记录于本分册上文) | ① 单口下客户端无法按源 ip:port 区分队友;② 对称 NAT 下同客户端对不同目标端口映射不同,按源端点匹配会错乱 |
| 验证状态 | 双客户端实测:城镇零数据报、握手在副本入口触发 | 其自注释"⚠️ 需真机验证(服务端绿≠客户端认)" |

**结论:暂不迁移,登记为候选**。理由与前置条件:

1. 本项目的单口中继已满足**握手**路径的端点一致性(上文实测结论);差异只在**多人副本内 P2P 数据报的归属**:若 60CN 客户端按源 ip:port 区分队友,则单口下多队友流量不可区分,需 per-ordered-pair;若其按帧内槽位归属,则单口已足够。该判定所需的客户端 IDA 复核**已于 2026-09-29 完成,结论为按帧内槽位归属、单口足够**(证据见文末「第二轮复核补记」第 1 条),本条判据不再支持迁移。
2. 迁移会改变 NOTI 11 通告的端点形态(单口 → 每对端口),属客户端可见行为;按 AGENTS D1/D2 与 07 §7.5 需 IDA 静态复核 + 双客户端副本内抓包实测(≥3 人、含对称 NAT 场景)后方可落地,未复测前不实施。
3. 参考项目自身亦默认关闭、env 门控,印证其结论仍处验证阶段;若后续确需迁移,建议同样以配置门控(默认关)形态引入,复用其"每有序对一 socket + 每腿独立学习"的形态与本项目的端点学习/中继解析器并存。

### 第二轮复核补记(2026-09-29):判定问题收窄 + 服务端侧归属歧义

对参考项目 `Network/Core/` 20 个文件逐项复核(分类结论见 07 §11.5,不变),与本分册相关的三点补充:

1. **IDA 复核结论(2026-09-29 已定论:按帧内槽位归属)**。收包线程 `sub_908520`(`NUDPCore::ThreadProc`,select + `WSARecvFrom`)收到数据报后调 `sub_907EC0`(`NUDPCore::ProcessIncomingData`),后者按**块类型字节**分派,**归属键只取自帧内字节**:

   - 类型 1(可靠)/ 2(不可靠):槽位 = 帧内偏移 7(与 `GameDatagramProtocol.CreateUnreliableFrame` 写出的 `senderPartyIndex` 为同一字节);槽位 `>= 8` 或该槽位**无对端对象**即整包丢弃(类型 2 另打印调试串 `"can't define peer"`)。
   - 类型 0(NAT 保活):槽位 = 帧内偏移 1。
   - 源 ip:port 不参与选对端:类型 1 完全不比对地址;类型 2 在该源地址与对端记录的 IP(`+28`)/端口(`+32`)不一致时,以到包源地址**更新**该槽位对端记录(`sub_909B00` / `sub_909AB0`)——地址是学习量,不是归属键。

   故单口中继的归属语义成立,**per-ordered-pair 迁移在本项目无必要**(上文判据①不成立);判据②(对称 NAT 下按源端点匹配会错乱)的解决路径本就落在服务端中继,与本节第 3 条同源。函数与地址登记见 `client-function-register-60cn.md` §4。

2. **多人副本 P2P 的前置缺口在 mode 3,不在中继拓扑**。`sub_907EC0` 的槽位查表要求该槽位**已存在对端对象**(`manager + slot*4 + 261`),否则丢弃;而对端对象(`user+0x110`)的创建点全二进制仅三处——本机自身记录首建流(`sub_4086F0`,写默认内网端点)、NOTI 2 处理器的 mode==0 且处理对象为本机自己(`0x419CA0..0x419CEA`)、以及 mode 1/3 分支(`0x41A780`:`sub_654E50` 建 → `sub_4FD880` 挂接 → `vtable+0x498` 初始化)。队友的对端对象只能由 **NOTI 2 mode 3** 建立(本分册"USERINFO mode 3"条);`PartyCoordinator` 当前按"城镇外观记录已覆盖检视窗"明确不交换详情快照,故多人同副本时客户端会丢弃服务器中继来的队友数据报。启用多人副本前需先补 mode-3 快照,并与第 3 条的双客户端实测一并复验。

3. **同 IP 多会话的端点学习歧义(服务端侧,待真机复现确认)**。`GameplayDatagramService.ObserveEndpoint` 按 TCP 源 IP 整体学习 UDP 端点:任一探测/保活数据报到达时,该 IP 下的**所有**会话都被赋予同一个观测端点。单会话独占一个 IP 时行为正确;同机双开或同一 NAT 出口下该 IP 承载多个会话时:

   - `_observedByAddress` 只保留最后一个观测端点,`_sessionsByEndpoint` 随之被覆盖为最后一个会话,另一会话的真实源端点失去映射,`RelayDatagram` 落到 "could not be attributed to a session; not relaying";
   - 被覆盖的会话,其端点字段指向他人端口,转发目标错位。

   对端握手(探测→应答)不经归属判定,故上文双客户端实测的握手结论不受影响;该歧义只在**多人副本内的中继转发**上显现,与本节第 1 条是同一问题的服务端侧一半。

   修复方向(纯服务端,不改线上字节,不触及字节冻结):端点学习改为**按会话认领**——先在各会话自报的 `GameNatReport` 端点中精确匹配观测端点(地址 + 端口),命中即只更新该会话;无命中且该 IP 下仅一个会话时沿用现行整体学习;无命中且多会话时不学习,保留各自自报端点。`SelectDatagramEndpoint` 的同源判断一并收紧(共享 IP 时不用共享观测值)。

   验证要求:双客户端同机进入同一副本,确认是否出现 "could not be attributed" 与错位转发;未复现前不改——现行实现是单会话场景的已知良好基线(AGENTS D1:未复测不得记为已验证)。

