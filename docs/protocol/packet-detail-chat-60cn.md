# 聊天包格式(60A1)

> 状态索引见 [client-packet-enums-60cn.md](client-packet-enums-60cn.md);帧头/加解密约定见 [packet-frame-crypto-60cn.md](packet-frame-crypto-60cn.md)。
> 字段表来源:服务端 `Network/Builders/Chat/ChatPacketBuilders.cs` 序列化器与 `EntranceService.cs` 解析器;字节示例约定见帧文档。
> 喇叭(物品 36/37)变体的实测推导、type 13/14 的 IDA 实证与标志字节语义登记于本册 CMD 17 与 NOTI 130 节（原暂存于 new-packet-formats-60cn.md，已收敛至此）。

## CMD 17 `ENUM_CMDPACKET_SEND_MESSAGE` — 发送聊天消息 ✅

`TryReadSendMessage` 解析(通用聊天布局;type 13/14 时 target/slot 字段复用为喇叭槽位):

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u16 | seq | |
| 2 | u8 | msgType | 消息颜色 `GameMessageType`;3=普通地图白字,13=服务器喇叭(物品 36),14/15=频道喇叭(物品 37),18=1:1 聊天 |
| 3 | u16 | targetOrItemSpace | 目标 uid;喇叭场景=背包 listType;type 18=0 |
| 5 | u16 | slotOrReservedLo | 普通聊天=保留;喇叭=槽位(低 16 位) |
| 7 | u16 | slotOrReservedHi | 保留(=0) |
| 9 | u32 | textLen | 1..255 |
| 13 | n | text | CP936 文本,不含 0 终止 |
| — | u32+n | partnerName | **仅 type 18**:聊天对象角色名 dstr(1..29B) |

服务端解析把偏移 5..9 的"槽位+保留"按一个 u32 读入 `SlotOrReserved`，天然兼容；喇叭分支要求 `SlotOrReserved <= 0xFFFF`。

> **type 值的 IDA 实证（.stk 脚本解析 sub_846BA0）**：客户端按 PVF 脚本标签决定行为——
> - `//echophone <槽位>`（=物品 37 的 `stackable/cash/megaphone_echo.stk`，分支 `0x848C39`）：播放音效 0x25 后，固定构造 **CMD 17 type=14**、target=0，写入槽位参数与文本后发送。**频道喇叭=14 是客户端硬编码，服务端透传即可。**
> - `//megaphone`（分支 `0x848BC4`）：GM 本地测试标签，**不发包**，循环 100 次渲染 type=13 横幅（名字固定 "testID"）。
> - 显示侧 type 13/14/15 共用同一横幅样式（sub_845580 同一 case 组、同色 0xFF00F2FF），类型差异只供服务端决定可见范围；单频道架构下两种喇叭显示一致属预期。type 15 未被任何喇叭脚本标签使用，暂按喇叭族透传。
>
> **校验以槽位内容为权威**（实测修正）：不要按 type↔物品 硬映射——客户端为物品 37 发出的具体类型值可能与推断不符，槽上是 36/37 即放行，广播原样透传客户端发来的类型。

**应答**:普通聊天无直接回包(成功以 NOTI 12 呈现);喇叭变体回 NOTI 14(扣减背包)+ NOTI 130(广播),不回 CMD 应答;type 18 无直接回包,服务端向对方与发送者各推一份 NOTI 12 type 18(见下)。

发包字节(示例):`01 11 0C 00 00 00 BE 23 C2 58 01 00` *

type 18 构造点:DNF.exe `sub_811C20`(1:1 聊天窗发送回调)——写 type 18、u16 0、u32 0、文本 dstr(输入框,≤255B)、对象名 dstr(20B 窗口缓冲,即名长 ≤19)。通用聊天发送函数 `sub_8495B0` 证实 type 1/7 也会追加对象名 dstr(本服未启用)。实测抓包(channel:7001):`21 00 12 00 00 00 00 00 00 00 05 00 00 00 "77777" 05 00 00 00 "Fight"`。

### type 18 / type 1/7 的悄悄话路由(服务端行为) ✅(实测修正)

- 上行 type 18 只有名字(uid 恒 0),type 1/7 的 u16 target 为对方区域 uid;服务端按城镇 presence 表按名解析在线角色。
- **下行禁止原样回显 type 18**:客户端 case 12(@0x41C994)只解析 `mode u8 + uid u16 + dstr 文本`,没有名字字段;mode 18 会把 NULL 名字传进 1:1 窗口路径(sub_849280 case 18 → 窗口 vtable+76 = sub_810A90 → sub_810560 对名字做 strlen),**strlen(NULL) 直接崩溃客户端**(实测:双方开窗聊天时服务端发 NOTI 12 type 18,4 秒内两个客户端全部掉线,NOTI 6 USER_LEAVE)。NOTI 分发器内不存在任何携带名字、能安全到达该窗口的 case(逐一排查 sub_419420 全部 case、sub_40F290 全部 18 处渲染调用、窗口 vtable A81474 全部槽位)。
- **实际下行 = NOTI 12 type 1**(浅绿私聊行):`msgType=1 + uid=发送者区域 uid + dstr 文本`。接收方 sub_849280 mode 1/7 分支按 uid 在城镇用户表解析说话者名字、渲染气泡,uid 等于自己时跳过显示;实测安全。A21 参考实现同样以 type 原样(1/7)走 NOTI 12。
- **发送方无需服务端回显**:两条发送路径(sub_811C20 / sub_8495B0)发送后都在本地把文本插入窗口/历史(sub_811A00)。
- 对方不在线/无 presence 时,给发送者回 NOTI 12 type 0(棕色系统行)提示。
- 附:case 65(`MESSAGE_OTHER_CHANNEL` 号位)在 60CN 里是 messenger 状态通知——读单字节码('M'=拉黑类,'Z' 等)后往 messenger 窗口插字符串表 2078/2079/2080 的系统提示,与 A21 的"被拉黑回单字节 77"呼应;不是聊天投递通道。

## NOTI 12 `ENUM_NOTIPACKET_MESSAGE` — 聊天回显/广播 ✅

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u8 | msgType | 消息颜色(决定字体颜色/路由) |
| 1 | u16 | targetUid | 目标区域用户 id |
| 3 | u32 + n | text | dstr 文本(1..255B) |

总长 = 7 + len。回包字节(示例):`00 0C 12 00 00 00 DA D2 D6 C2 D6 D6 D6 77 43 67 67 6B`

## CMD 124 `ENUM_CMDPACKET_CREATURE_SEND_MESSAGE` — 宠物聊天 ✅

正文布局与 CMD 17 相同(共用解析)。**应答**:无直接回包,推 NOTI 126。

发包字节(示例):`01 7C 0C 00 00 00 BE 23 C2 58 01 00` *

## NOTI 126 `ENUM_NOTIPACKET_CREATURE_MESSAGE` — 宠物聊天(兼弹窗) ✅

载荷布局与 NOTI 12 相同(type u8 + targetUid u16 + dstr 文本),notification id 仅决定最终显示模式。

- 广播变体:msgType 原样,targetUid=收包者。
- 弹窗变体(`CreatePopupNotification`):`msgType=0 + targetUid=0 + dstr 文本` —— 客户端用字符串 640 格式化文本并打开弹窗 44。

回包字节(示例):`00 7E 11 00 00 00 D6 D2 D6 C6 D6 D6 D6 0B 6B 6B 4F`

## CMD 132 `ENUM_CMDPACKET_CREATURE_SCRIPT_MESSAGE` — 宠物脚本消息 ✅

正文布局与 CMD 17 相同。**应答**:无直接回包,推 NOTI 131。

发包字节(示例):`01 84 0C 00 00 00 BE 23 C2 58 01 00` *

## NOTI 131 `ENUM_NOTIPACKET_CREATURE_SCRIPT_MESSAGE` — 宠物脚本消息回显 ✅

载荷布局与 NOTI 12 相同。总长 = 7 + len。

回包字节(示例):`00 83 13 00 00 00 DA D2 D6 CE D6 D6 D6 1B 5B 1F 73 17 07`

## NOTI 130 `ENUM_NOTIPACKET_MEGAPHONE_MSG` — 大喇叭广播 ✅

客户端 case 130(`sub_419420` @0x427414)经全局游标 dstr 读取;`CreateMegaphoneNotification`:

| 偏移 | 宽度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | u8 | msgType | 13/14/15,与 CMD 17 发来的类型一致(实测:物品 36=13,物品 37=14),原样透传 |
| 1 | u8 | channelNo | **消息所属频道号**:客户端与自身频道号(CHANNELINFO 第 5 字节)比较,相等→正常路径(聊天行 "Ch.N 名字 : 文本"+喇叭横幅),不等→降级仅横幅(红×、前缀错乱);服务端发配置的 channelNumber |
| 2 | u16 | speakerUid | **发言者用户 id**:客户端与自身 id 比较,相等→头顶气泡;服务端发 0(所有客户端 id=channelNumber,0 不匹配任何人→无人冒气泡) |
| 4 | dstr | senderName | 发送者角色名,**长度必须 1..29**(cap=0x1E) |
| — | dstr | text | 消息文本,**长度必须 1..511**(cap=0x200) |

- 越界会让客户端 dstr 游标不推进导致后续全部错位,上述长度是硬约束。
- 正确组合 = channelNo=配置频道号、speakerUid=0(实测:两者都发 1 会让全员冒气泡;channelNo 发 0xFF 会让全员走异频道降级分支)。
- 广播范围:13=全服,14=本频道(单频道架构下同为全服);发送者自己也收(speakerUid=0 统一横幅呈现)。

**两字段比对语义(实测 + IDA)**:客户端在 NOTI 1 CHANNELINFO(case 1,`0x4194C8..0x419A57`)把第 4/5 字节存为 `dword_B5B430/B5B434`(0x419697,服务端分别写 serverId=1 与 channelNumber)。NOTI 130 的 channelNo 与 B5B434(频道)比较决定走哪条显示路径;speakerUid 经 sub_849280 再与 B5B434 比较决定是否头顶气泡(0x419697/0x849462)。历史两次误判:两者都发 1 → 全员头顶气泡;channelNo 发 0xFF → 全员走异频道降级分支(红×、前缀错乱显示为文本长度)。将来实现每会话唯一 userId 后,speakerUid 可改发发言者真实 id(发送者见气泡、他人见横幅)。

**服务端处理流程(type 13/14/15)**:校验槽位(物品 ∈ {36, 37}、count>0、保留 u16=0;失败→弹窗+warning,不广播)→ count-1(为 0 清槽)并 `SaveCharacterInventoryAsync` 持久化 → 发 NOTI 14 单条目刷新自己背包 → `NotifyMegaphoneAll` 广播 NOTI 130 给全部在线会话(含发送者),type 原样透传客户端所选值 → 不回 CMD 17 应答(成功以 NOTI 呈现)。NOTI 14 条目格式见 [packet-detail-item-inventory-60cn.md](packet-detail-item-inventory-60cn.md)。
