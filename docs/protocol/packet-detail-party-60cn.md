# 组队包格式(60A1)

> **实现状态**:服务端组队已有雏形实现(实现代码 `Game/Party/`、`Network/PartyCoordinator.cs`,自测见 `PartySmokeTests`);真机复测尚未完成。本文档是客户端协议逆向记录,各 CMD/NOTI 的 ✅ 仅表示字段布局已逐 case 在 IDA 验证,不代表服务端功能复测通过;下文涉及服务端行为的描述以实现代码与 CHANGELOG 为准。功能完成度以 README「已知缺口」为准。
>
> 状态索引见 [client-packet-enums-60cn.md](client-packet-enums-60cn.md);帧头/加解密约定见 [packet-frame-crypto-60cn.md](packet-frame-crypto-60cn.md)。
> 全部字段布局于 客户端主程序 IDA 库(ImageBase 0x400000)内逐 case 验证,服务端实现见
> `Game/Party/PartyState.cs`、`Game/Party/PartyManager.cs`、`Network/PartyCoordinator.cs` 与
> `Protocol/GameProtocolEngine.cs`(常量/构建器/解析器)。
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
| 2 | u8 | titleKind | 0=自定义队名(后随 dstr);1..5=预设名(dword_B75A64[0..5],0 号为空串) |
| 3 | u32+bytes | title | 仅 titleKind==0:u32 长度 + CP936 字节,容量 0x40 |
| — | u8 | userMax | 人数上限,界面单选 = 槽位+2,即 2/3/4 |

写入点 0x7D5F86(begin(12) 之后逐段 push)。建队与改设置同型;改设置要求请求者是队长且 memberCount ≤ userMax。
**应答**:成功 `[1]`;失败 `CreatePartyCommandError`([0,code,0,0],code=8 弹提示,case 0x410799)。

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
| 4 | u8 | type | 0=完整编制 1=仅设置 2=仅名册 3=清除 |
| — | — | 设置段(type 0/1) | u8 titleKind;`titleKind==0` 时 u32 长度+队名(容量 0x40,`sub_8B3250` @0x41B94C);u8 userMax(`party+0x38`) |
| — | — | 名册段(type 0/2) | 4 槽 × {u16 uid, u8 state(存 `member+0x1A`)};空槽 uid=0xFFFF(0x41BB1C 清槽写法);尾字节 u8 队长槽位 |
| — | — | type 3 | 无载荷,客户端把 4 槽全部置 0xFFFF(0x41BAF8) |

【差异】type=0/1 设置段与 CMD 12 同构(kind, [dstr], userMax),不是 A21 的 12 字节块;【差异】type=3 只有 5 字节块头,无 A21 的额外空 dstr;【差异】名册每槽 3 字节而非 5 字节。
单块 body 长度:kind=0 队名 L → type0 = 24+L、type1 = 9+L、type2 = 18、type3 = 5(均含 2 字节 blockCount)。

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
- `Network/PartyCoordinator.cs` — 会话投递、城镇存在表、封包编排
- `Protocol/GameProtocolEngine.cs` — CMD 10-14 解析、NOTI 6/7/8/9/10 构建、`CreateCurrentCharacterDetails(mode:3)` 对端上下文构建
- `Network/CharacterSessionRegistry.cs` — 跨会话封包通道(`AttachPacketChannel`)
- 自测:`DFLegacy.Server.SelfTests/Game/Party/PartySmokeTests.cs`
