# DNF.exe 60A1 函数地址登记表(全项目共享)

> 来源:客户端主程序 IDA 库(ImageBase 0x400000)。全项目已恢复的函数/数据统一登记于此;新逆向文档只登记增量并回填本表。
> 取证方式:**反编**=Hex-Rays 反编译确认;**反汇**=仅反汇编;**边界**=跳转表 case 边界推算;**引用**=从他处文档/调用关系间接确认。
> 详细调用链按功能见各 `packet-detail-*.md`(组队:[packet-detail-party-60cn.md](packet-detail-party-60cn.md))。

## 1. 分发与传输层

| 函数/数据 | 地址 | 取证 | 功能 |
|---|---|---|---|
| `sub_40D6D0`(尾部) | 0x40D6D0 | 反汇 | type 分发:`packet[0]=0`→`sub_419420`(NOTI);`=1`→`sub_40F290`(CMD 应答) |
| `sub_419420` | 0x419420 | 反汇 | NOTI 分发器,switch(`packet[1]`),跳转表 `0x42A081`(4B/项,152 case,id 0..151);函数约 68KB,Hex-Rays 失败 |
| 跳转表 | 0x42A081 | 边界 | NOTI id → case 入口(4B/项);case 间有穿插,边界须逐项扫 `get_switch_info` |
| `sub_40F290` | 0x40F290 | 反编 | CMD 应答分发器:protocolId 经 LUT `byte_417638[id-1]` 映射跳转表 `0x417480`(159 槽,id 1..159);`p_tm_min`=应答首字节,`0`=失败进错误码读取 |
| `sub_8B2650`(sock, id) | 0x8B2650 | 反编 | C→S begin:写 `[0]=type(1) [1]=id`,占位 12B 头 |
| `sub_8B2450`(buf, len) | 0x8B2450 | 反编 | 向当前包追加载荷 |
| `sub_8B24C0` | 0x8B24C0 | 反编 | flush:`[2..5]`=u32 总长(载荷+12)、`[6..9]`=`sub_8B23B0` 校验和(覆盖序号+载荷)、`[10..11]`=u16 序号;`[1]==3` 有特殊分支 |
| `sub_8B23B0` | 0x8B23B0 | 反编 | 出站校验和 |
| `sub_8CAFD0` | 0x8CAFD0 | 反编 | 发送环形缓冲出队(缓冲 0xAF000=716800B) |
| `sub_8CACC0`(buf, len) | 0x8CACC0 | 反编 | 原始 `ws2_32 send`(套接字在 `this+0x1B4`) |
| `sub_8B26E0` | 0x8B26E0 | 反编 | `CNGameSocket` 构造(收发状态、加解密键初始化) |

## 2. 读取原语(NOTI 解析共用全局游标 `src`)

| 函数 | 地址 | 取证 | 功能 |
|---|---|---|---|
| `sub_8F5110`(p) | 0x8F5110 | 反汇 | 设置游标 |
| `sub_8F5120`(out) | 0x8F5120 | 反汇 | 读 u8,游标 +1 |
| `sub_8F5140`(out) | 0x8F5140 | 反汇 | 读 u16(LE),游标 +2 |
| `sub_8F5160`(out) | 0x8F5160 | 反汇 | 读 u32(LE),游标 +4 |
| `sub_8B3250`(dst, cap, 默认串) | 0x8B3250 | 反编 | 读 dstr:u32 长度后复制;长度 ≥cap 或 0 时内容不消费,调用方须保证 <cap。**注**:len==0/≥cap 时"长度字段是否仍前进"两处记录不一致(本表原记"仍前进";喇叭验证记录([CHANGELOG](../../CHANGELOG.md) 喇叭条目)与 [confirm-reply](confirm-reply-packet-format-60cn.md) §2.1 记"游标整体不前进"),待 IDA 逐指令复核;服务端构包一律按 1..cap-1 规避 |
| `sub_8F5180`(dst, n) | 0x8F5180 | 反汇 | 从游标复制 n 字节 |
| `sub_8AA5A0`(id) | 0x8AA5A0 | 反编 | 按串表 id 取界面字符串 |

## 3. 用户表(城镇用户记录,dword_B5DCB8)

| 函数 | 地址 | 取证 | 功能 |
|---|---|---|---|
| `sub_4FDDB0`(mgr, uid) | 0x4FDDB0 | 反编 | 按 uid 查找,未命中返回 0 |
| `sub_4FDF60`(mgr, uid) | 0x4FDF60 | 反编 | 查找或创建记录(0x114 字节);用户记录唯一创建点(USERINFO case @0x419B3E) |
| `sub_4FDDF0`(mgr, uid) | 0x4FDDF0 | 反编 | 移除记录(NOTI 6 @0x41B467) |
| `sub_4FDD30`(mgr, name) | 0x4FDD30 | 反编 | 按名查找(strcmp 遍历) |
| `sub_404300`(obj) | 0x404300 | 反汇 | 取记录数据区指针;关键偏移:member +0x1A 名册 u8、+0x1C/+0x20 状态字;party +4 titleKind、+8 队名、+0x34 队长槽、+0x38 userMax;用户 +0 自检记录的 party 句柄 +0x14 |
| `sub_4FD490`(user, 0/1) | 0x4FD490 | 反编 | 格式化 inner/outer IP `"%d.%d.%d.%d"`(仅显示) |
| `sub_417A80` / `sub_417A90` | 0x417A80 / 0x417A90 | 反编 | 用户 +8 accId / +0xC mtu 写入器(NOTI 11 使用) |

## 4. 组队管理器(dword_B5DCC8)与 UDP 管理器(0xD01D00)

| 函数 | 地址 | 取证 | 功能 |
|---|---|---|---|
| `sub_4FF040`(mgr, partyId) | 0x4FF040 | 反编 | 查找或创建队伍槽(NOTI 9 type=0) |
| `sub_4FEF70`(mgr, partyId) | 0x4FEF70 | 反编 | 按代号查找队伍 |
| `sub_4FE690`(party, slot, uid) | 0x4FE690 | 反编 | 设槽位 uid(0xFFFF 清;type=3 清 4 槽 @0x41BAF8) |
| `sub_4FE660`(party, slot) | 0x4FE660 | 反编 | 取槽位成员对象 |
| `sub_404320`(mgr, slot) | 0x404320 | 反汇 | 取槽位成员 uid(踢人反查 @0x8520D0) |
| `sub_4FE730`(party) | 0x4FE730 | 引用 | 当前人数(容量校验 @0x4FE9B0、0x7D5EEF) |
| `sub_44D280`(party) | 0x44D280 | 反汇 | 队伍设置数据区(PARTY_INFO 设置段) |
| `sub_4052D0`(dword_B5DCB8) | 0x4052D0 | 反汇 | 取本机用户记录(NOTI 9 判"是否我的队伍") |
| `sub_9054C0` | 0x9054C0 | 反编 | 取 UDP 管理器单例 `0xD01D00` |
| `sub_404360`(mgr, slot) | 0x404360 | 反编 | 写房主槽位到管理器 +0x28(NOTI 26 @0x41F162) |

## 5. 已确认的 NOTI case 区间(sub_419420)

| id | 名称 | case 区间 | 取证 |
|---:|---|---|---|
| 2 | USERINFO | 0x419A57..0x41B0D3 | 边界 |
| 3 | USER_STATE | 0x41B0D3..0x41B1CE | 反汇 |
| 6 | USER_LEAVE | 0x41B3C8..0x41B4C8 | 反汇 |
| 7 | REQUEST_PEER | 0x41B4C8..0x41B5F1 | 反汇 |
| 8 | RESPONSE_PEER | 0x41B5F1..0x41B7A1 | 反汇 |
| 9 | PARTY_INFO | 0x41B7A1..0x41C2F0 | 反汇(被 NOTI 128 穿插,边界非相邻) |
| 10 | WALKOUT | 0x41C41A..0x41C7E7 | 反汇 |
| 11 | USER_UDP_IP_PORT | 0x41C7E7..0x41C994 | 反汇 |
| 23 | USER_AREA | 0x41E9C7..0x41EC7C | 反汇 |
| 24 | AREA_USERS | 0x41EC7C..(见城镇文档) | 反汇 |
| 26 | UDP_HOST | 0x41F144..0x41F16C | 反编 |
| 128 | (穿插用例) | 0x41C2F0..0x41C41A | 边界 |
| 130 | MEGAPHONE_MSG | 0x427414..0x427626 | 引用(packet-detail-chat-60cn.md) |

## 6. 组队 CMD 发送点 / 应答 case

| 项 | 地址 | 取证 | 说明 |
|---|---|---|---|
| CMD 12 发送 | 0x7D5F86 | 反汇 | 建队/改设置;预设名表 `dword_B75A64[0..5]`;校验串 0x461..0x464 |
| CMD 13 发送 | 0x7D12AC / 0x8520AB | 反汇 | 城镇菜单 / 组队窗口 |
| CMD 14 发送 | 0x8520F1 | 反汇 | 槽位反查 0x8520C8(扫 0..3) |
| CMD 10 发送 | 0x4FE9B0(type0)/ 0x852117(type2) | 反编/反汇 | 邀请/申请;PvP 邀请走 type2;错误文案 432..439 |
| CMD 11 发送 | 0x4FEBC0(接受)/ 0x821E46(拒绝) | 反编/反汇 | 接受=7B 全零;拒绝 type/value 非零 |
| 应答 case 10/11/12/13/14 | 0x4102F6 / 0x4104FF / 0x410799 / 0x4107D8 / 0x410834 | 反汇 | 失败分支读取形状与错误码→弹窗(详见组队文档) |
| 应答 case 97 | 0x416988 | 反汇 | 成功→`sub_80DF90` 刷新邮箱;失败读 u8 错误码→`sub_80AA30`(证实 ACT1 97=MAILBOX_SEND,A21 的 97=PEER_CONNECT_RESULT 编号不适用) |
| CMD 100 PEER_CONNECT_RESULT | — | 反汇 | 应答走 default(火忘式);**全二进制无发送点**——本客户端不向服务器汇报对端连接结果,P2P 状态纯 UDP 层 |
| 对端连接对象簇 | vtable 0xA7DFF4 / 0xA81404,构造 `sub_80E2E0`/`sub_80F600`,tick ~0x80E370 | 反汇 | 成员 P2P 链路对象;成员 +0x110 处对象虚表 +1364=连接态检查(`sub_4FE850`);用户记录 +0x100="在我的队伍"标记(`sub_4FE7D0`/`sub_4FED80`) |
| 副本门口检查+P2P 触发 | ~0x89F560..0x89F6FA(CMD 15 发送点 0x89F69E) | 反汇 | 进门时遍历 4 槽:成员不在"我的场景"(actor `+0x10C` 查不到或 `sub_4C5310` 否)且 `sub_4FE850>1` → 串 0x215 阻止进门;全员通过才发 CMD 15,**随后对每个成员 `user+0x110` 对端对象调 `vtable+0x188(0)` 发起 P2P 连接**——对端握手只在副本入口发生,城镇不发起(实测日志佐证:组队后零 UDP 数据报) |
| 对端对象创建 | `sub_4FD880`(this, peerObj) | 反编 | 写 `user+0x110 = peerObj` 并把套接字索引注册进混淆表(dword_CFD458+17/18);经虚表调用,对应的服务端触发包未定 |
| 队长限定 START_GAME | `sub_8779F0`(case 0/1,发送点 0x877A6D/0x877AE5) | 反编 | 选图确认流程:`sub_4FE830`=队长槽(NOTI 9 尾 `sub_4FE970(party+0x34)` 写 mgr+0x20),仅当"该槽成员==自己"(即自己是队长)才发 CMD 15;非队长等服务器拉入 |
| **USERINFO mode 3 = 对端上下文包(组队连接超时根因)** | 创建点 0x41A7DD(mode==3 分支)/ 0x419CEA(自我首建) | 反编/反汇 | NOTI 2 的 mode 3 分支为**任意用户**创建 user+0x110 对端连接对象(`sub_654E50` 建 → `sub_4FD880` 挂接 → vtable+0x498 初始化);没有它,组队成员的对端对象不存在 → 永不发起 P2P 握手 → "连接中"直至超时。mode 分派:0x419BD4(mode==0/2 走角色更新;mode==3 走 0x41A764 分支)。mode-3 载荷 = 完整"他人角色详情"(u8/u16/u32/dstr 数十个字段,读取器 sub_8F5120/5140/5160/8B3220),字段映射待逐项回填。`sub_417AB0`=读 user+0x110;`sub_4FED80` 在"自己首建记录"时同步把自身 NAT 端点(dword_B5DCE4)写入 data+0/+4/+8 |
| `sub_4FE910` | 0x4FE910 | 反编 | 求自己在队伍槽位(0..3 或 -1) |
