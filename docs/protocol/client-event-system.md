# DNF.exe 60A1 客户端活动(Event)系统支持文档

> 来源：对 `DNF_ACT1_0918/DF/DNF.exe` 的静态逆向（2026-09-28，IDA Professional 9.1 批处理 + 原始 PE 扫描）。
> 用途：确认客户端对"活动"的支持范围与协议入口，指导 `Server/DFLegacy.Server` 活动相关
> NOTI 包的实现；同时为 `Network/Core/PacketTypes.cs` 头注释中引用的
> `docs/protocol/client-packet-enums-60cn.md` 补上 NOTI 名字表实测数据（见附录 A）。
> 仅用于自有程序兼容性研究。
>
> **结论：支持 ACT1 时代的活动。** NOTI 69 `SERVEREVENT`、86 `SERVER_BROADCAST`、
> 120 `EVENT_INFO` 三个包在客户端均有真实的处理 case 与活动 UI；但**没有**后来版本的
> 活动日历/活动列表 UI。活动呈现方式 = 公告弹窗 + 全服特效/事件 + 疲劳活动条皮肤 +
> 商城/时装活动弹窗 + PVF 脚本内容。

## 1. 样本信息

| 项目 | 值 |
|------|----|
| 文件 | `DNF_ACT1_0918/DF/DNF.exe` |
| MD5 | `71f643d7b7339ca58d9bac6ded960c8f` |
| 大小 | 9,846,272 字节 |
| 类型 | PE32 GUI, Intel 80386 |
| 编译时间戳 | 2008-06-17 07:17:54 UTC |
| ImageBase | 0x400000 |
| 壳 | VMProtect（`.vmp0` @ VA 0xB33000，仅 0x5000 字节，**入口点未被重写**，主代码段明文）；`.SCY` @ 0xB39000（TenProtect/TerSafe 组件）。前 8 个节名被混淆为 `nsn\x00*` |
| 分析可行性 | RTTI 完整（1316 个类）、字符串明文（约 2.5 万条）、无加壳加密段，静态分析质量良好 |

**地址约定**：本文地址均为 VA（IDA 基址 0x400000）。注意本样本各节 raw 偏移与
`VA-0x400000` **不**一致（例如 NOTI 名字字符串区 VA 0xA88xxx 的文件偏移为 0x687xxx），
换算请按节表：

| 节 | VA 范围 | 文件偏移 |
|----|---------|----------|
| `nsn\x00t`(.text) | 0x1000–0x598000 | 0x400 |
| `nsn\x00ta`(.rdata) | 0x598000–0x6FD000 | 0x596C00 |
| `nsn\x00a`(.rdata2/数据表) | 0x6FD000–0x91C000 | 0x6FB600 |
| `nsn\x000` | 0x93C000–0x9ED000 | 0x778A00 |
| `nsn\x00oc` | 0x9ED000–0xA8D000 | 0x818000 |
| `nsn`(无名数据段) | 0xA8D000–0xB33000 | 0x8B7C00 |

## 2. NOTI 包名字表（协议 ID 的权威来源）

- 名字指针表 @ **VA 0xB54170**（文件偏移 0x752770），175 项 × 4 字节，**下标即 wire ID**；
  表尾第 154–174 项为 `NOTI_NO_NAME` 占位。紧随其后（VA 0xB54430）是 CMD 名字表，
  与 `Network/Core/PacketTypes.cs` 头注释完全吻合（该注释中的 0xB54170/0xB54430 为 VA）。
- 原 `Protocol/PacketNames.cs`（2026-09-29 删除，报文名反查并入 `Network/Core/PacketTypes.cs`
  的 `PacketTypeNames`，名称即 `NotiPacketType` / `CmdPacketType` 成员名）中
  C# 数组下标 + 10 = wire ID（其数组缺前 10 个 ID 的表项），
  已用 6 个点交叉验证：MESSAGE 12、FATIGUE 36、SERVEREVENT 69、SERVER_BROADCAST 86、
  EVENT_INFO 120、MEGAPHONE_MSG 130（服务端已实现的喇叭 `MegaphoneMessageNotification = 130`）。
- ⚠️ 名字字符串在文件里有两份拷贝：被表引用的一份在 VA 0xA88230–0xA89600；
  另一份（RVA 0x688xxx，文件偏移 0x687xxx）**无任何引用**，做 xref 时勿用错份。

三个活动包的名字表项（字符串 VA）：

| wire ID | 表项名 | 字符串 VA |
|---------|--------|-----------|
| 69 | `ENUM_NOTIPACKET_SERVEREVENT` | 0xA88DA4 |
| 86 | `ENUM_NOTIPACKET_SERVER_BROADCAST` | 0xA88B2C |
| 120 | `ENUM_NOTIPACKET_EVENT_INFO` | 0xA88680 |

## 3. 分发函数与活动包处理逻辑

NOTI 分发函数：**`sub_419420`（0x419420–0x42A081，约 68 KB）**，
由调试串 `CNGameCore::NetworkProcNOTIFY`（0x999924）的引用锚定；
跳转表 @ 0x419474，case 0–153（**注**：[client-function-register-60cn.md](client-function-register-60cn.md) §1 记同一分发器跳转表 `0x42A081` 为 152 case（id 0..151），两处 case 数不一致，待 IDA 逐项复核后统一）。落入 default（无专属处理）的 ID 仅：
25, 52, 62, 63, 67, 108–119（chaos 系列为主）, 129, 133, 135, 146, 147。
**三个活动包都有专属 case。** 包内读取原语：`sub_8F5120`/`sub_8F5140` 读 1 字节、
`sub_8F5160` 读 4 字节；`sub_401030` 返回游戏主对象 `dword_B5D6F0`；
`sub_8AA5A0(id)` 按字符串表 ID 取串；`sub_845B60` 由串构建 UI/特效对象。

### 3.1 NOTI 69 SERVEREVENT（全服事件/特效开关）

payload：`{u8 flag, u8 type, u8 ?}`（第三字节读入但处理中未使用——静态观察）。

```
0x425FCE  case 69:
  读 flag → test flag,flag → setnz      ; bool on = flag != 0
  读 type
  读 ?（未使用）
  sub_42E3E0(gameObj, type)   ; obj[90] = type          —— 设置全服事件类型
  sub_42FBF0(gameObj, on)     ; obj[364] = on，变化时调 sub_42EB00(on)
```

`sub_42EB00`：`type == 1` 时按 `on` 选择字符串表 ID **75 / 76** 加载特效对象并挂到
城镇对象管理器 `dword_B5DCB8`。关联的客户端资源/逻辑（均有活跃代码引用）：

| 字符串 | 引用处 | 含义 |
|--------|--------|------|
| `snow event x` / `snow event y` | `sub_873920`（4 处引用） | 雪花活动粒子坐标 |
| `Temp/2006WorldCupEvent/soccerballui.img` | `sub_5D99B0` | 世界杯活动足球 UI |
| `[event]`（PVF 标签） | `sub_768670` | PVF 脚本活动标记 |

### 3.2 NOTI 86 SERVER_BROADCAST（全服广播公告）

payload：`{u8 type}`，随后按 type 分支（静态推断，字段语义待包追踪实测）：

- **type = 1**（0x427141）：`{u8 a, u16 charId, u16 b, u8 c}` →
  `sub_4FDDB0(dword_B5DCB8, charId)` 按 ID 查找角色记录 → `sub_45C060(b)` 取名字串 →
  组装后以字符串表 ID **0x689** 为格式串弹出公告。
- **type = 2**（0x4272A5）：`{u8 a, u16 charId, u16 b, u8 c}`，a==0 或查无此人则忽略；
  c==0 时用字符串 ID **0x104B** + 缓冲 0x200 `sprintf` 构建广播文本（`sub_8B3DB0`），
  c≥1 走重复拼接分支。

公告展示层为 `CNRDNoticeMessageWindow`（见 §4）。

### 3.3 NOTI 120 EVENT_INFO（疲劳活动条特性位）

payload：`{u32 featureFlags}`（0x4286A8 case 120）。

```
读 u32 flags
循环 i = 1..4：
  P = sub_402030(gameObj + 368)          ; 经 sub_4182D0 取 UI 管理对象
  sub_430340(gameObj, P, flags, i, 0xBFE+i-1, 0xC06+i-1)
```

`sub_430340` 按 `flags` 第 i 位在两套字符串表 ID（`0xBFE..0xC01` / `0xC06..0xC09`）
中二选一，经 `sub_8AA5A0(id)` + `sub_845B60(串, -6567706, 16, 0, 0)` 构建 UI 资源；
`i == 15`（bit 15）另有特殊分支。语义与 `Network/EntranceService.cs` 现有注释一致：
**"EVENT_INFO then selects and initializes its normal/premium fatigue-bar resources"**，
即燃烧疲劳/普通疲劳条皮肤选择。PVF 侧对应标签 `[burning fatigue event param]`
（解析于 `sub_76ACF0`）。服务端已实现：`StaminaPacketBuilders.CreateEventInfo(featureFlags)`
（`EventInfoNotification = 120`）。

## 4. 活动 UI 类与资源（均非死数据）

UI 窗口工厂 `sub_7BDC60`（0x7BDC60）按窗口 ID `new` 并构造各窗口，以下活动窗口的
vftable 均被工厂链路引用（vftable VA / 构造引用点）：

| RTTI 类 | vftable | 构造引用 |
|---------|---------|----------|
| `CNRDEventConfirmWindow`（活动确认窗） | 0xA7F650, 0xA7F664 | 0x7C4841, 0x7C48CE |
| `CNRDCashShopDoubleEventWindow`（商城双倍活动窗） | 0xA7EB04 | 0x7C1AAD, 0x7C1B22 |
| `CNRDSummerEventAvatarPopupWindow`（夏日活动时装弹窗） | 0xA7F604 | 0x7C46FD, 0x7C4782 |
| `CNRDNoticeMessageWindow`（公告窗，86 的展示层） | 0xA7E394 | 0x7C03ED, 0x7C0472 |
| `CNRDGoldOverNoticePopupWindow`（金币上限公告弹窗） | 0xA7F514 | 0x7C429D, 0x7C4322 |

其他活动资源引用：`Interface/SelectDungeonEvent.img`（`sub_8A4690`）、
`Interface/CashShop/SummerEventAvatar.img`（`sub_825BA0`）。

⚠️ **勿误判**：RTTI 里的 `IEvent / IEventObserver / IEventDeliverer / ConfirmEvent /
IDConfirmEvent` 是 UI 消息/观察者模式的基类（"窗口事件"），与"活动"无关；
`CNEventEquipment / CNEventBarrier` 是副本内机关/触发器类，同样与活动无关。

## 5. 服务端对接现状与建议

| NOTI | 名称 | 服务端现状 | 建议 |
|------|------|------------|------|
| 120 | EVENT_INFO | ✅ 已实现（`CreateEventInfo`，进城镇 AREA_USERS 后发送） | `featureFlags` 位序静态推断为 bit1..bit4 选疲劳条皮肤，可用包追踪+客户端截图验证；bit15 特殊分支待查 |
| 86 | SERVER_BROADCAST | ❌ 未实现 | payload 按本文 §3.2 布局发；type=1/2 的字段含义与字符串表 0x689/0x104B 的格式串内容需实测核对 |
| 69 | SERVEREVENT | ❌ 未实现 | payload `{flag, type, ?}`；type=1 + flag 可触发全服特效（雪花类，字符串表 75/76） |

验证方法：项目已有包追踪输出报文名能力（`Network/Core/PacketTypes.cs` 的两张枚举经
`PacketTypeNames.TryGetName` 反查）；字段级解码器 `PacketPlainText` 当前保留未接入，
可按 §3 的 payload 布局重新接入后补 69/86 的解码分支，再用真实客户端连服观察。

## 6. 边界（这版客户端"不支持"什么）

- 无活动日历 / 活动列表 / 活动任务追踪等后来版本的系统 UI；
- exe 内无中文字符串（GBK 扫描 0 命中），活动文本全部在 `Script.pvf` 与客户端
  字符串表（按 ID 引用：75/76、0x689、0x104B、0xBFE..、0xC06..）；
- `Script.pvf` 目录为加密结构，本文未验证 PVF 内活动脚本内容，仅确认客户端
  解析器支持 `[event]`、`[burning fatigue event param]` 标签。

## 附录 A：NOTI 名字表全量（wire ID → 名称）

提取自 VA 0xB54170 表（下标即 wire ID）。0–153 有名，154–174 为 `NOTI_NO_NAME`。
与客户端名字表（现物化为 `Network/Core/PacketTypes.cs` 的 `NotiPacketType`，下标即 wire ID）的映射一致。

```
  0 ENUM_NOTIPACKET_CHECK_CONNECTION      1 ENUM_NOTIPACKET_CHANNELINFO
  2 ENUM_NOTIPACKET_USERINFO              3 ENUM_NOTIPACKET_USER_STATE
  4 ENUM_NOTIPACKET_STAMINA               5 ENUM_NOTIPACKET_DUNGEON_PERMISSION
  6 ENUM_NOTIPACKET_USER_LEAVE            7 ENUM_NOTIPACKET_REQUEST_PEER
  8 ENUM_NOTIPACKET_RESPONSE_PEER         9 ENUM_NOTIPACKET_PARTY_INFO
 10 ENUM_NOTIPACKET_WALKOUT              11 ENUM_NOTIPACKET_USER_UDP_IP_PORT
 12 ENUM_NOTIPACKET_MESSAGE              13 ENUM_NOTIPACKET_ITEM_LIST
 14 ENUM_NOTIPACKET_UPDATE_ITEM_LIST     15 ENUM_NOTIPACKET_CHANGE_ITEMTRADE_ITEM
 16 ENUM_NOTIPACKET_CANCEL_ITEMTRADE     17 ENUM_NOTIPACKET_STATE_ITEMTRADE
 18 ENUM_NOTIPACKET_FINISH_ITEMTRADE     19 ENUM_NOTIPACKET_SKILLINFO
 20 ENUM_NOTIPACKET_CHANGE_BONUSSP       21 ENUM_NOTIPACKET_ACCEPTABLE_QUEST_LIST
 22 ENUM_NOTIPACKET_USER_POSITION        23 ENUM_NOTIPACKET_USER_AREA
 24 ENUM_NOTIPACKET_AREA_USERS           25 ENUM_NOTIPACKET_FISHING
 26 ENUM_NOTIPACKET_UDP_HOST             27 ENUM_NOTIPACKET_START_GAME
 28 ENUM_NOTIPACKET_DUNGEON_INFO         29 ENUM_NOTIPACKET_START_MAP
 30 ENUM_NOTIPACKET_FINISH_LOADING       31 ENUM_NOTIPACKET_ENABLE_CLEAR_DUNGEON
 32 ENUM_NOTIPACKET_DIE_STATE            33 ENUM_NOTIPACKET_FAIL_CLEAR_DUNGEON
 34 ENUM_NOTIPACKET_PLAY_RESULT          35 ENUM_NOTIPACKET_CLEAR_DUNGEON_REWARD
 36 ENUM_NOTIPACKET_FATIGUE              37 ENUM_NOTIPACKET_EXP
 38 ENUM_NOTIPACKET_DIE_MONSTER          39 ENUM_NOTIPACKET_GET_ITEM
 40 ENUM_NOTIPACKET_DROP_ITEM            41 ENUM_NOTIPACKET_PVP_ROOM_INFO
 42 ENUM_NOTIPACKET_PVP_ROOM_STATE       43 ENUM_NOTIPACKET_PVP_SEAT_STATE
 44 ENUM_NOTIPACKET_PVP_READY_STATE      45 ENUM_NOTIPACKET_START_PVP
 46 ENUM_NOTIPACKET_DIE_PVP_CHARACTER    47 ENUM_NOTIPACKET_END_PVP
 48 ENUM_NOTIPACKET_PVP_RECORD           49 ENUM_NOTIPACKET_REQ_PVP_RANK
 50 ENUM_NOTIPACKET_KEY_COUNT            51 ENUM_NOTIPACKET_MONEY_FULL
 52 ENUM_NOTIPACKET_FRIEND_INFO          53 ENUM_NOTIPACKET_CERA
 54 ENUM_NOTIPACKET_BUDDY_IN             55 ENUM_NOTIPACKET_BUDDY_OUT
 56 ENUM_NOTIPACKET_BUDDY_CONN_LIST      57 ENUM_NOTIPACKET_PVPEXP_OF_WEEK
 58 ENUM_NOTIPACKET_GUILD_ENTER          59 ENUM_NOTIPACKET_GUILD_SECEDE
 60 ENUM_NOTIPACKET_GUILD_MARK_CHANGE    61 ENUM_NOTIPACKET_GUILD_DISMISS
 62 ENUM_NOTIPACKET_GUILD_CHAT_MSG       63 ENUM_NOTIPACKET_GUILD_CHAR_NAMES
 64 ENUM_NOTIPACKET_GUILD_MEMBER_LOGIN   65 ENUM_NOTIPACKET_MESSAGE_OTHER_CHANNEL
 66 ENUM_NOTIPACKET_CERA_SPECIALITEM     67 ENUM_NOTIPACKET_CARD_SELECT_COMPLETE_STATE
 68 ENUM_NOTIPACKET_CERA_GROUP_HISTORY   69 ENUM_NOTIPACKET_SERVEREVENT
 70 ENUM_NOTIPACKET_GUILD_INFO           71 ENUM_NOTIPACKET_GUILD_CREATE
 72 ENUM_NOTIPACKET_MEMBER_MEMBER_LOGIN_OUT
 73 ENUM_NOTIPACKET_REQUEST_MEMBER_ENTER_TO_RESPONSER
 74 ENUM_NOTIPACKET_MEMBER_ENTER_OK      75 ENUM_NOTIPACKET_DELETE_MEMBER_ID
 76 ENUM_NOTIPACKET_PAY_TAX_TO_UPPER     77 ENUM_NOTIPACKET_MEMBER_SECEDE
 78 ENUM_NOTIPACKET_CREATE_PRIVATE_STORE 79 ENUM_NOTIPACKET_REMOVE_PRIVATE_STORE
 80 ENUM_NOTIPACKET_FROCE_REMOVE_PRIVATE_STORE
 81 ENUM_NOTIPACKET_COMPLETE_DISPLAY     82 ENUM_NOTIPACKET_BUY_PRIVATE_STORE_ITEM
 83 ENUM_NOTIPACKET_PRIVATE_STORE_ITEM   84 ENUM_NOTIPACKET_PRIVATE_STORE_LIST
 85 ENUM_NOTIPACKET_PRIVATE_STORE_INFO   86 ENUM_NOTIPACKET_SERVER_BROADCAST
 87 ENUM_NOTIPACKET_WARROOM_INFO         88 ENUM_NOTIPACKET_WARROOM_STATE
 89 ENUM_NOTIPACKET_WARROOM_SEAT_STATE   90 ENUM_NOTIPACKET_START_WARROOM
 91 ENUM_NOTIPACKET_WARROOM_MONSTER_SPAWN
 92 ENUM_NOTIPACKET_CLEAR_WARAREA_REWARD 93 ENUM_NOTIPACKET_WARROOM_GET_ITEM
 94 ENUM_NOTIPACKET_ADD_KP               95 ENUM_NOTIPACKET_WARROOM_DIE_STATE
 96 ENUM_NOTIPACKET_CLEAR_WARAREA_FP     97 ENUM_NOTIPACKET_MAILBOX_MAIL_LIST
 98 ENUM_NOTIPACKET_MAILBOX_REMOVE_MAIL  99 ENUM_NOTIPACKET_MAILBOX_ALARM
100 ENUM_NOTIPACKET_DIED_CREATURE        101 ENUM_NOTIPACKET_RENAME_CREATURE
102 ENUM_NOTIPACKET_GAIN_EXP_CREATURE    103 ENUM_NOTIPACKET_CREATURE_STATE
104 ENUM_NOTIPACKET_RESPONSE_CREATURE    105 ENUM_NOTIPACKET_CREATURE_ITEM_LIST
106 ENUM_NOTIPACKET_EVOLUTE_CREATURE     107 ENUM_NOTIPACKET_REVIVAL_CREATURE
108 enum_NOTIPACKET_CHAOS_BUY_FP_ITEM    109 enum_NOTIPACKET_CHAOS_ZONE_OWNERSHIP
110 enum_NOTIPACKET_CHAOSROOM_INFO       111 enum_NOTIPACKET_CHAOSROOM_START
112 enum_NOTIPACKET_DIE_CHAOSROOM_CHARACTER
113 enum_NOTIPACKET_CHAOSROOM_DIE_STATE  114 enum_NOTIPACKET_CHAOS_GAME_CLEAR
115 enum_NOTIPACKET_CHAOSROOM_SEAT_STATE 116 enum_NOTIPACKET_CHAOSROOM_READY_STATE
117 enum_NOTIPACKET_CHAOSROOM_STATE      118 ENUM_NOTIPACKET_CHAOSROOM_END_REWARD
119 ENUM_NOTIPACKET_CHAOSROOM_MONSTER_SPAWN
120 ENUM_NOTIPACKET_EVENT_INFO           121 ENUM_NOTIPACKET_GUILD_WAR_END
122 ENUM_NOTIPACKET_GUILD_WAR_RESULT     123 ENUM_NOTIPACKET_CODE_CHECKSUM_RANGE
124 ENUM_NOTIPACKET_PVP_TURN_PLAYER      125 ENUM_NOTIPACKET_PVP_REQUEST_FIGHT
126 ENUM_NOTIPACKET_CREATURE_MESSAGE     127 ENUM_NOTIPACKET_BOSS_DIE_CHECK
128 ENUM_NOTIPACKET_SEAL_DOOR_ITEMS      129 ENUM_NOTIPACKET_GUILD_WAR_START
130 ENUM_NOTIPACKET_MEGAPHONE_MSG        131 ENUM_NOTIPACKET_CREATURE_SCRIPT_MESSAGE
132 ENUM_NOTIPACKET_PVP_REVIVE           133 ENUM_NOTIPACKET_TIME_CHECK
134 ENUM_NOTIPACKET_SP_RETURN_INFO       135 ENUM_NOTIPACKET_CHECK_FORCED_DISCONNECT
136 ENUM_NOTIPACKET_ENTER_GAMEWORLD_COMPLETE
137 ENUM_NOTIPACKET_REQ_HUMAN_CERTIFY    138 ENUM_NOTIPACKET_GM_REQUEST_MID
139 ENUM_NOTIPACKET_GUILD_MEMBER_INFO    140 ENUM_NOTIPACKET_MOVE_ASSAULT_PLACE
141 ENUM_NOTIPACKET_START_ASSAULT        142 ENUM_NOTIPACKET_DIE_ASSAULT_PLAYER
143 ENUM_NOTIPACKET_END_ASSAULT          144 ENUM_NOTIPACKET_REVIVAL_ASSAULT_PLAYER
145 ENUM_NOTIPACKET_VIEW_ASSAULT_ICON    146 ENUM_NOTIPACKET_LEAVE_ASSAULT_PLACE
147 ENUM_NOTIPACKET_CHANGE_HP            148 ENUM_NOTIPACKET_CLOSE_ASSAULT
149 ENUM_NOTIPACKET_TELEPORT             150 ENUM_NOTIPACKET_AA_STARTINFO
151 ENUM_NOTIPACKET_AA_TIME              152 ENUM_NOTIPACKET_ANTIBOT
153 ENUM_NOTIPACKET_DPROTO
154–174 NOTI_NO_NAME（占位）
```

## 附录 B：复现方法

1. IDA 批处理：`idat -A -S<脚本> DNF.exe`（脚本内 `idaapi.auto_wait()` 后用
   `idautils.Strings()` 定位锚串，`DataRefsTo/XrefsTo` 追引用）。
2. 关键锚点：`CNGameCore::NetworkProcNOTIFY`（0x999924）→ 分发函数 `sub_419420`；
   NOTI 表 VA 0xB54170 直接按下标读指针、解引用取名字符串。
3. RTTI 链：`.?AV<类名>@@` 串 VA − 8 = TypeDescriptor；全文件搜 TD 指针得 COL
   （命中处 − 0xC）；搜 COL 指针得 vftable 槽（**vftable 首址 = COL 槽 + 4**，
   MSVC 布局）；搜 vftable 指针得构造函数引用点。
4. 已分析 IDA 库为客户端主程序 IDA 库的本地留存（`DNF.exe.i64`，含四次探针输出
   `dnf_probe*_out.txt`，分发函数完整反汇编在 probe2 输出内）。
