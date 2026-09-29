# 60A1 协议修复日志

记录协议行为修复与新增处理的根因、权威验证过程（IDA + 抓包）、改动与遗留项。

---

## 2026-09-29

### ADD-05 新增 DAF-MCP 抓包网关（<http://127.0.0.1:12222/mcp>）

- **功能**：服务端内置标准 MCP 服务器（默认关闭），供智能体（ZCode / Claude Code 等）实时抓包分析协议——报文名 + 明文富化的收发包流、单包完整字节、离线解码、包号↔报文名检索。
- **开启步骤**：
  1. 服务端发布目录需存在 `DFLegacy.Mcp.dll`：由 `publish-mcp.bat` / `publish-mcp.sh` 发布到 `dist\DFLegacy.Mcp` 后手动拷入；未拷贝时服务端照常运行，仅不加载 MCP。
  2. `server.json` 的 `DFLegacy.Mcp.Enabled` 改为 `true` 并重启服务端（`Mcp.Host` / `Mcp.Port` 默认 `127.0.0.1:12222`）。
  3. 在 MCP 客户端配置中加入：

  ```json
  {
    "mcpServers": {
      "dflegacy": {
        "type": "http",
        "url": "http://127.0.0.1:12222/mcp"
      }
    }
  }
  ```

- **工具清单（均为只读）**：
  - `packet_watch`：实时跟踪收发包（`tail -f` 语义），`afterSeq` 游标续读，可按方向/协议号/报文名/会话过滤；
  - `packet_get`：按 seq 取单包完整详情（含明文 bodyHex 与 RX 密文 wireHex），供逆向拷字节；
  - `packet_wait`：挂起等待第一条匹配事件，用于"做了操作等回包"式往返验证；
  - `packet_names_search`：在 60CN 报文名注册表中按名字片段或包号检索；
  - `protocol_decode`：离线复用服务端字段级解码器解码提交的 bodyHex；
  - `session_list`：列出当前网络会话，定位要盯的 sessionId；
  - `tap_status`：抓包通道状态自检（当前 seq、环形存档占用）。
- **典型用法**：`session_list` 找到目标会话 → `packet_names_search` 查包号定过滤 → `packet_watch` 续读跟踪；需要等特定回包时用 `packet_wait`，深挖字段用 `packet_get` + `protocol_decode`。
- **安全**：默认仅回环监听；改为非回环等同主动暴露协议流与游戏数据。
- **关联**：`docs/design/09-mcp-packet-tap.md`（设计全档）；README「常用配置 → 服务端 → MCP」。

### ADD-06 协议层重构管线落地：分发注册表 + 出站单写者泵（R0，组 1-2-3 部分协议走新链路）

- **内容**：按 `docs/design/07-protocol-layer-refactor.md` 落地 R0 基础设施并接入部分协议（分支 `refactor/protocol-layer`）：
  - `GameCommandRegistry` 字典注册表：协议号 → handler 一对一挂载，重复注册启动期失败并带来源组名；启动时按组打印清单；
  - `GameCommandDispatcher` 通用管线：端口/阶段门槛统一校验（U3），迁移期门槛不合格回落旧分支按原拒绝路径处理；
  - `ChannelSessionState` 十二域会话状态对象（07 §4.3 字段一一对应），`HandleClientAsync` 约 78 个闭包变量整体迁入，旧链路经别名、新 handler 经 `ctx.State` 访问同一存储；
  - `OutboundQueue` 有界 Channel（容量 256）+ 每连接单写者泵：背压阻塞发送方、不丢包不乱序，发送计数与 `LogPacket` 收进单点（U4）；`SendAsync` 等待帧真正写完，保持旧直写的时序语义；
  - `ConnectionTimerScheduler` 单调一次性定时器（回调仅允许入队 `ConnectionEvent`，R4/R5 收编时启用）；
  - 会话状态分域对象化后，`HandleClientAsync` 全链路字节行为经 SelfTests 653 项内联检查与 golden 套件回归为零变化。
- **已挂载新链路的协议**（共 9 个，均为字节级等价迁移）：CHECK_CONNECTION 0、EXIT 3、ANTIBOT 161（组 1）；LOGIN 1、SET_UDP_IP_PORT 2（组 2）；GET_USERINFO 8、CREATE_CHARACTER 5、DELETE_CHARACTER 6、RETURN_SELECT_CHARACTER 7（组 3）。未挂载协议仍走旧分支链，行为不变。
- **Parser 外置**：`TryReadLoginCredentials` 等请求解析逐字迁至 `Network/Handlers/<组>/` 与 `Network/Parsers/`（07 §4.7）；USERINFO 名册/初始背包构建收口 `CharacterPacketBuilders`。
- **验证**：SelfTests 全量通过（含新增 `ProtocolGoldenSmokeTests`：EXIT 全字节、CHECK_CONNECTION 同 seed 全载荷对比、门槛拒绝路径、批量 FIFO、计账单点）；样本库 `tools/packet_bytes.json`（66 CMD + 49 NOTI）由 SelfTests 直接消费。**遗留项：真机冒烟复测未做**（登录→选角→建/删角→返回选角需真机确认）。
- **续报（R1 收尾 + R2）**：SELECT_CHARACTER（CMD 4）已随 R1 收尾迁入 `SelectCharacterHandler`（初始化包家族迁入 `CharacterPacketBuilders`，循 §6·R1 InitPacketBuilderRegistry 方向）；R2 组 5-8 共 14 协议挂载新链路（CHAT 3／MAILBOX 4／QUEST 4／SKILL 3），注册组 `social.chat`／`social.mailbox`／`progression.quest`／`progression.skill`；跨会话推送（邮件／会员／点券／喇叭／弹窗／虚弱／副本权限）统一经目标会话出站泵入队，fire-and-forget 直写他连流的 P2 裂缝在已迁移协议面上机制化封口。新增 `ConnectionContext.ReadAdjacentFrame` 泵层能力承载 CHANGE_SKILLSLOT 的紧邻帧预读（解密与计账留在传输层）。EntranceService.cs 14,832 → 10,791 行。
- **续报（R3）**：组 9-11 共 19 协议挂载新链路（INVENTORY 5／SHOP 6／CRAFT 8），注册组 `economy.inventory`／`economy.shop`／`economy.craft`；新增 InventoryParsers 与 ShopParsers（含 ConsumeItemsRequestItem／CeraShopPurchaseRequestItem 请求记录），库存移动发送器、精灵移动规划簇与库存空间校验族归口 `ChannelSessionSenders`。EntranceService.cs 缩至 5,128 行；累计挂载 43/71 协议；组 12-15（精灵／副本／城镇／组队）与 R6 清退仍待推进。目录形态随后对齐参考项目三层镜像：`<组>Parsers` 由 `Network/Handlers/<组>/` 提升至平级 `Network/Parsers/<组>/`（共享 `ClientBodyParsers` 置 Parsers 根）。　另按参考项目 `Network/{Core,Protocol}` 两极分治：`Protocol/` 整体迁入 `Network/`，非核心功能（加解密、名称枚举、日志明文、包基元、PVF 读取、`IPacketTapSink`）统一入 `Network/Core`，协议核心（分帧 + 游戏/入口引擎 + UDP 数据报协议）留 `Network/Protocol`；命名空间不变，字节冻结不受影响。　另将 `Hosting/`（`ServerOptions`/`PathResolver`/`AdminApiModels`）并入 `Infrastructure/`，`CharacterSessionRegistry` 移入 `Game/Session/`（对标参考项目 `SessionDirectory`），空目录移除；`Properties/`（SDK/IDE 约定）与 `Services/`（待 R6 事件入队改造）原地不动。　日志面自参考项目迁移 `FileLogger`/`PacketFileLogger` 功能并统一为 Microsoft.Extensions.Logging 管线：新增有界队列文件日志器（`logs/server.log`，DI 注册、排空退出）与逐包十六进制捕获 sink（`IPacketTapSink`，默认关闭、64MB 轮转），Tap 漏斗扩为多端口并集（MCP hub 与包捕获并存）；十六进制排版工具拆为 `Network/Core/HexDump`，日志层不含加解密逻辑；新增 `file-logger` 自测套件。

---

## 2026-09-28

### FIX-01 返回选角后公告弹窗需要确认两次

- **现象**：首次登录选角，选角界面的活动公告弹窗确认 1 次；进入城镇房间后再返回选角，同一弹窗需要确认 2 次。
- **根因**：客户端返回选角（CMD 7 `ENUM_CMDPACKET_RETURN_SELECT_CHARACTER`）后会自行发送 GET_USERINFO（CMD 8 `ENUM_CMDPACKET_GET_USERINFO`）拉取角色列表；服务端在 CMD 7 应答后又主动补发一份 NOTI 2（mode=2）列表，同一帧列表到达两次。客户端每次收到列表都会重建选角界面并重放活动公告。
- **权威验证过程**：
  - 抓包（两份独立日志）均显示 `CMD 7 → ack → NOTI 2 mode=2（补发）→ CMD 8 → NOTI 2 mode=2（应答）`，两帧字节完全一致；首次登录路径只有 CMD 8 应答一帧。
  - 实测定量：列表到达次数与弹窗确认次数 1:1 对应（1 帧 → 1 次确认；2 帧 → 2 次确认）。
  - IDA 复核确认 CMD 7/8 与 NOTI 2 的枚举语义与 [client-packet-enums-60cn.md](docs/protocol/client-packet-enums-60cn.md) 一致（CMD 7 行备注：返回选角界面→刷新列表，配对 NOTI 2）。
- **修复**：删除 CMD 7 处理器中应答后的主动补发，以 CMD 8 应答为列表唯一来源（`Server/DFLegacy.Server/Network/EntranceService.cs`）。
- **验证**：冒烟全绿；真客户端复测待确认（返回选角后弹窗应仅 1 次）。
- **关联**：[client-packet-enums-60cn.md](docs/protocol/client-packet-enums-60cn.md) CMD 7/8 行。

### FIX-02 整理物品后背包页签跳到"材料"

> **状态：❌ 修复已回滚（2026-09-28）。** 首版"NOTI 14 增量"方案虽止住页签跳转，但真机复测引入两个更严重症状（旧物品影子 + "获得 xx"公告，根因见下"客户端处理器语义"），遂整体回退：整理恢复推送全量 NOTI 13，增量方案代码已删除。**当前行为**：无影子/无公告，但页签跳转症状随全量列表回归，待按"遗留与风险"所列路径根治。

- **现象**：背包窗口打开时点击整理（CMD 20 `ENUM_CMDPACKET_SORT_ITEM`，space=0），装备/消耗品/材料/任务四个页签的选中态全部跳到"材料"。
- **根因**：整理应答后服务端推送全量背包列表 NOTI 13（listType=0）。全量列表是客户端"整体重建背包窗口"的信号（与 FIX-01 选角列表重放公告同一机制），重建时页签选中状态被重置。协议规范为操作后走增量：NOTI 13 仅用于登录/拉取列表等全量刷新，物品操作以 NOTI 14 增量呈现（CMD 19 移动即明确"随后推 NOTI 14"）。
- **权威验证过程（IDA）**：
  - 收包链：recv 封装 `sub_891520` → 帧读取 `sub_892830`（会话线程 `sub_892AE0`）→ 解密 `sub_788F30` → 经会话对象 `this+0x702C` 加密器 vtable+12 转发分发。
  - 发包封装 `sub_799EF0(mgr, 协议号, body, len, 0)`；内部动作号→协议号映射表构建函数 `sub_8E8A90`（211→CMD 8、212→NOTI 13、228→CMD 20 SORT_ITEM）。
  - 背包窗口类 RTTI：`CNRDItemInventoryWindow`（vtable 0xa7cf44 一带），其虚函数簇位于 0x79xxxx–0x7bxxxx。
  - 排除项：`sub_7BD810`/`sub_7D7AF0`/`sub_8B2880`/`sub_8B37D0` 中的立即数 0xA1 属客户端内嵌 SNMP 栈（SNMP 端口 161 巧合），与游戏协议无关；`"sorting..."` 字符串所在函数（0x8ae5b0）为无引用调试残留。
  - 诚实说明：NOTI 13 处理器内"重置页签"的具体指令未逐条钉死（收包分发为跳表且部分代码未被 IDA 定义为函数）。定性依据是：①全量列表=窗口重建的机制实证（FIX-01 同机制）；②协议文档增量规则；③移动路径已有 NOTI 14 单槽更新的在库先例（`EntranceService.cs` 换位/清槽更新）。
- **修复（结论修订，2026-09-28 当晚）**：
  - 首版曾把整理改为 NOTI 14 逐槽增量，真机验证出现两个新症状：**旧物品影子**（搬迁后原槽位图标不消失）与**槽位变更物品弹"获得 xx"公告**。IDA 定位到根因（见下"客户端处理器语义"）：客户端 NOTI 14 对 listType≠3（含主背包 0）走简化路径——`0xFFFF` 墓碑条目被直接忽略、新增物品必走播报路径，**增量通道在协议层面无法表达整排搬迁**。
  - 最终实现：整理（space=0）**恢复推送全量 NOTI 13**（客户端该处理器为静默的容器重建，与 A21 参考服及本文档原始描述一致）；`CharacterInventorySortDelta` 增量方案及其测试随回退移除。仓库（space=2）与宠物背包（space=7）整理保持原行为。
- **客户端处理器语义（IDA，权威）**：
  - NOTI 14 处理器 `0x41D237..0x41D942`（sub_419420 case 14）：入口按 **包内 listType 分流**——`0x41D34F cmp eax,3`：**listType==3（穿戴栏）** 走完整增删改路径（`0x41D39E` push 0xFFFF + vtable+0x168 清槽、`sub_51AB60` 更新）；**listType≠3（主背包 0 等）** 走 `0x41D4CF` 简化路径：`0x41D769 cmp 0xFFFF → 跳过该条目`（墓碑无效→影子）、新增走 `sub_51AB60` + `sub_45E280(…,1)`（→ `sub_7AAAF0` 播报获得）。
  - NOTI 13 处理器 `0x41D01C..0x41D237`：`sub_45E2F0` 清空容器 → 逐条目 `sub_4048C0` 查槽 → `sub_51AB60` 建物品 + `sub_45E280` 插入 → 尾部 `sub_4EC4B0` 通知界面；无获得播报分支，为静默整体重建。
  - NOTI 39（获得物品）= `0x421EA7..0x422CC3`。
- **验证**：冒烟全绿；回退后真机复测待确认——预期无影子、无"获得"公告（全量列表为静默重建），但**页签跳转可能复现**（见顶部状态与遗留项）。
- **遗留与风险**：
  - 页签跳转成因尚未钉死：首例"跳材料"发生在 CMD 19 移动（服务端全量列表带 preferredLastSlot=68，材料区）之后紧接整理，存在**混淆因素**；整理自身回全量列表后是否复现页签跳转，以本次复测为准。若复现，下一步剖 `sub_4EC4B0`（容器变更→界面重绑）与背包窗口（CNRDItemInventoryWindow）的页签选择写入点。
  - CMD 19 移动路径当前仍推全量 NOTI 13；因 listType=0 增量通道无法表达"槽位清空"（0xFFFF 被忽略），移动/出售等操作的清槽表达方式（count=0 更新 vs 全量列表）需真服抓包取证后另行处理。
- **关联**：[packet-detail-item-inventory-60cn.md](docs/protocol/packet-detail-item-inventory-60cn.md) CMD 20 / NOTI 13 / NOTI 14 节（已同步更新）；[client-packet-enums-60cn.md](docs/protocol/client-packet-enums-60cn.md) CMD 20 行。

#### 补充复核（2026-09-28）：整理的排序规则归属

结论：**排序规则 100% 属服务端，客户端没有也不需要排序规则。**

- CMD 20 请求体仅 `{seq u16, space u8}`（见 [packet-detail-item-inventory-60cn.md](docs/protocol/packet-detail-item-inventory-60cn.md) CMD 20 节，抓包 `3E 00 00` 一致）：客户端不上送任何排序键或排列结果，规则不可能由客户端定义。
- 客户端按服务端返回的槽位布局直接呈现：NOTI 13/14 条目即 `{slot, item, …}`，网格按槽位渲染；FIX-02 修复后实测当前页签保持、物品按服务端排布显示，无本地重排迹象。
- IDA 排查记录（客户端主程序 IDA 库）：
  - 发包封装 `sub_799EF0` 的 49 个调用者逐一反编译，无任何以协议号 20 的直接调用（整理发送经内部映射表 `sub_8E8A90` 槽 228 中转）；
  - 全部字符串注册表中 "sort" 命中仅为 SNMP 排序例程、性能打点（`object sort : %d usec/frame`）与无引用调试残留（`sub_8ae5b0`，内容为字符串/哈希辅助，无物品字段比较）；
  - 未发现任何以品级/类型/物品 ID 为键的背包排序比较器。
- 当前服务端规则（`Game/Inventory/CharacterInventorySorter.cs`，本项目自行定义，非官方还原）：四个分区（装备/消耗品/材料/任务）各自独立排序、不跨区；装备区按品级（PVF `[rarity]`，未知按 9999 殿后）升序 → 物品 ID → 原槽位；其余分区按物品 ID → 原槽位。如需改为"品级从高到低"等其它规则，属服务端策略调整，客户端无需改动。

#### A21 参照（2026-09-28）：ServerS4A21 的整理规则

A21 参考服源码 `Server/DfoServer/Game/Inventory/Services/InventorySortService.cs`（请求解析 `InventoryHandler.Move.cs` `Handle_ENUM_CMDPACKET_SORT_ITEM`）：

- **请求体不同代**：A21 CMD 20 body = `listType u8 + category u8 + condition u8（缺省 0）`——category 指定单页签，condition 指定排序条件；60CN body 只有 `{seq u16, space u8}`，无 condition 字段。
- **条件→算法**（`ResolveSortAlgorithm`）：
  - 装备：槽位（默认）/ 品级 / 等级（最小等级降序）/ 部位（PartRank 升序）；
  - 消耗品：槽位（默认）/ 堆叠类型（`[stackable type]` 字符串升序）/ 有效期（有期限在前，无期限殿后）；
  - 材料/副职业材料：槽位（默认）/ 堆叠类型；
  - 时装：品级（默认）/ 部位；宠物装备与宠物消耗品：仅槽位。
- **品级排序为显式条件且降序**：A21 PVF rarity 0 普通、1 高级、2 稀有、3 神器、4 史诗、5 异界、6 传说，玩家序映射 史诗>传说>神器>异界>稀有>…（`ToRaritySortRank`：4→6、6→5、3→4、5→3）后降序排。
- **默认规则（condition=槽位）**：主键跳过，实际顺序 = 物品类别 → 物品 ID → 原槽位（全部升序）。即 A21 默认**不按品级**。
- **公共次序键**：主键之后一律 类别 → 物品 ID → 原槽位。
- **整理锁**：`SortLockFlag=1` 的槽位固定不参与移动（A21 专属 0x02CA/0x02CB，60CN 无此功能）。
- **应答与刷新**：ack（`0x01 0x0014`，body=listType）+ 全量 ITEM_LIST + 装备锁列表刷新——A21 客户端可接受全量列表；60CN 客户端全量列表会重置页签（FIX-02），故 60CN 侧必须走增量。

**与本服差异**：①本服装备区默认按品级升序，A21 默认无品级（条件才排品级且为降序、史诗优先）；②本服其余分区物品 ID→原槽位，与 A21 默认（类别恒定→物品 ID→原槽位）实质一致；③A21 支持条件参数与整理锁，60CN 协议不带 condition、无整理锁。规则取向属服务端策略，保持现规则或对齐 A21 默认均可，客户端零改动。

### ADD-03 新增 ANTIBOT 上报消费（CMD 161）

- **现象**：客户端登录时发一条 92 字节、此后每 60 秒一条 18 字节的 CMD 161；服务端无处理器，日志每分钟告警 `No channel handler for packet type 1, protocol 161`。
- **权威验证过程（IDA）**：
  - 发送器 `sub_8C74F0`：`BeginPacket(161)`（`sub_8B2650`）→ 写 4 字节长度前缀（`sub_8B2450(&len, 4)`）→ 写记录负载 → `EndPacket`（`sub_8B24C0`）。该函数经函数表 0x998C50 登记，周围为 `unknown owner MAC` 等反作弊检测字符串，确认属 ANTIBOT 模块。
  - 排除项：`sub_8B2880`/`sub_8B37D0` 等处的立即数 161 属客户端内嵌 SNMP 栈（SNMP 端口 161 巧合），与游戏协议无关。
  - 线上格式（与抓包逐字节吻合）：`body = seq u16 + u32 声明长度 + 记录`；记录以大端 u16 kind 开头。已观测两种形态：登录环境上报 kind 0x0003（86 字节），周期心跳 kind 0x000A（12 字节，含 4 字节校验值与大端 u16 计数器）。
  - **fire-and-forget 契约**：无应答时客户端心跳计数器持续递增（实测 4→23 跨越多次心跳），不重试、不断线、无惩罚路径；发送路径未见 pending 状态或超时登记。服务端不应答即正确行为，不猜测应答字节（NOTI 161 无真服样本，保持未实现）。
- **新增内容**：
  - `Protocol/GameProtocolEngine.cs`：常量 `AntibotCommand = 161`（60CN `ENUM_CMDPACKET_ANTIBOT`，见 [client-packet-enums-60cn.md](docs/protocol/client-packet-enums-60cn.md) CMD 161 行）。
  - `Game/Antibot/AntibotReportParser.cs`：`TryParse` 解析 seq、声明长度一致性校验、记录 kind。
  - `Network/EntranceService.cs`：未知协议兜底前新增显式分支，静默消费并以 Debug 级日志记录（seq/kind/声明长度）。
  - `Server/DFLegacy.Server.SelfTests/Game/Antibot/AntibotReportSmokeTests.cs`：以真实抓包帧断言解析与拒绝路径（短包、声明长度造假）。
- **验证**：冒烟全绿；真客户端复测待确认（登录挂机 ≥2 分钟，无 protocol 161 警告，游戏行为无变化）。
- **关联**：[client-packet-enums-60cn.md](docs/protocol/client-packet-enums-60cn.md) CMD 161 行（`ENUM_CMDPACKET_ANTIBOT`；其同名配对 NOTI 152 `ENUM_NOTIPACKET_ANTIBOT` 无真服样本，保持未实现）。

### ADD-04 新增喇叭功能（CMD 17 type 13/14 + NOTI 130 全服广播）

- **功能**：使用服务器喇叭（PVF 物品 36，type 13）/ 频道喇叭（PVF 物品 37，type 14）发言，服务端校验并扣减道具后向全服在线会话广播 NOTI 130 大横幅消息。
- **权威验证过程（IDA + 抓包互证）**：
  - 使用喇叭不走物品使用命令：真实会话抓包证明喇叭走聊天命令 CMD 17 `SEND_MESSAGE`，请求体在普通聊天布局之外以 slot 字段指明喇叭所在背包槽（实测输入 "hello"：type=13、slot=8）。
  - 显示包为 NOTI 130 `ENUM_NOTIPACKET_MEGAPHONE_MSG`：客户端处理器 `sub_419420` case 130（`0x427414..0x427626`），以全局游标 dstr 读取器 `sub_8B3250` 解析 `u8 type + u8 flag + u16 target + dstr 发送者名(cap 0x1E) + dstr 消息(cap 0x200)`；长度为 0 或达到容量会导致游标不前进、后续字段全部错位，因此构包器对两个 dstr 长度做硬性上限校验。
  - 显示分支：flag 与本地玩家单字节值（`0x404A60` 处 `dword_B5B434`）匹配 → 写入普通聊天窗（`sub_849280`，发送者自己）；不匹配且 type∈{13,14,15} → 大横幅渲染（`sub_845B60`，颜色 0xFF00F2FF）。故向全部会话广播同一帧 NOTI 130 即可，客户端自行区分形态。
  - PVF 佐证：物品 36 = 服务器喇叭、37 = 频道喇叭（堆叠消耗品，主背包 listType 0）。
- **实现**：
  - `Protocol/GameProtocolEngine.cs`：常量 `MegaphoneMessageNotification = 130`；构包器 `CreateMegaphoneNotification`（dstr 长度硬校验：名字 1..0x1D、消息 1..0x1FF）。
  - `Network/EntranceService.cs`：CMD 17 分支放行 type 13/14——以请求内 slot 权威校验槽上物品（36/37、listType=0、数量>0，校验失败拒绝且不广播），数量 -1（归零移除槽位）并持久化，回自己 NOTI 14 单条目更新（清槽用 `0xFFFF`），再经会话注册表向全部在线会话广播 NOTI 130。
  - `Hosting/CharacterSessionRegistry.cs`：新增 `megaphoneNotification` 回调通道与 `NotifyMegaphoneAll` 全服遍历。
- **验证**：冒烟全绿（`MegaphoneSmokeTests`：真实抓包帧解析 type 13/slot 8/"hello"、NOTI 130 字节逐字节断言、名字 0x1E 容量与空消息拒绝路径）。
- **待确认**：type 14 ↔ 物品 37 映射为推断（未单独抓频道喇叭）；flag 字节语义（疑似 localUserId 低字节，错仅影响自己那条的显示形态）；type 15 喇叭变体未处理。
- **关联**：[confirm-reply-packet-format-60cn.md](docs/protocol/confirm-reply-packet-format-60cn.md) §5/§6（本条的 IDA+抓包全记录）；[new-packet-formats-60cn.md](docs/protocol/new-packet-formats-60cn.md) §1；[client-packet-enums-60cn.md](docs/protocol/client-packet-enums-60cn.md) NOTI 130 行。

#### 修订（2026-09-28）：单人使用喇叭时头顶气泡不显示

- **现象**：仅自己一个角色在线时使用喇叭，角色头顶不出现消息气泡。
- **根因（case 130 逐指令复核）**：服务端此前 flag=频道号，而所有客户端的本机频道号都与之相等，于是**人人走聊天窗路径**（仅聊天行一行字，无横幅）；且 target 硬编码 0，不指向任何人 → 气泡无从生成。
- **客户端精确语义（IDA）**：
  - `0x427518 mov ecx, offset dword_B5B434; call sub_404A60` 取**本机频道号**，与包内 flag（`var_348E`）比较（`0x427522 cmp eax, ecx; 0x42752B jnz`）。
  - **相等** → `0x42752D..0x427562`：仅 `sub_849280` 写聊天窗——包内 target 等于本机 uid 时附带头顶自己的气泡；**无横幅、直接返回**。（旧注释"equal → chat line + speaker banner"有误，横幅只在 unequal 分支。）
  - **不等** → 按 type 13/14/15（`0x42756C/0x4275A9/0x4275E6`）调 `sub_845B60` 渲染屏幕横幅（0xFF00F2FF）；`sub_845B60` 为纯文本横幅（无角色查找、无气泡）。
  - 两条路径互斥：聊天行+气泡 与 横幅不可兼得，由 flag 是否等于本机频道号决定。
- **修复（终版，按接收者差异化，2026-09-28）**：
  - 演进：①首版"双帧"（广播 flag=不匹配 + 发言者直发拷贝）→ 发言者双显示、横幅按 flag 拼出"频道0"；②改"单播 target=说话者 uid" → 因所有会话本机 uid 恒为 1，**每个接收者自己头顶都冒泡**。③终版：**按接收者差异化发包**——`NotifyMegaphoneAll(messageType, speakerSessionId, speakerFlag, speakerTarget, otherFlag, name, msg)`：发言者会话的帧 `flag=频道号 + target=自己 uid`（聊天行"频道N 名字 : 消息" + 头顶气泡），其他会话的帧 `flag=不匹配值 + target=0`（仅横幅，无气泡、无聊天行）。
  - 发言者标识采用**连接级 SessionId**（租约按连接独占：`ClaimAsync` 收 `runtimeSession.Id`，同一角色重复登录会踢掉旧连接，见注册表 "Character session moved" 日志）——路由问题本质是"哪个连接是发言者的"，账号+角色组合非必需；显示用的名字由包内 name 字段独立承载（与他人看到的文案无关路由）。
- **验证**：冒烟全绿；真客户端复测通过标准——发言者：聊天行"频道1 名字 : 消息"+ 头顶气泡，各一次；其他在线者：仅横幅，无气泡、无聊天行、无双显示。
