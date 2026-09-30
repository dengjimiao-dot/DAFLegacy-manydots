# 60A1 协议修复日志

记录协议行为修复与新增处理的**最终结果**：现象、根因结论、最终改动与验证状态。

只收录已完成并已验证的条目；未完成、待真机复测、未定论与存在争议的事项，以及历史修订与多轮验证过程，一律登记在 `PENDING.md`，本日志不保留过程性叙述。日期小节与条目均按最后修改时间倒序排列（最新在前）。

---

## 2026-09-30

### FIX-11 SQLite 种子把物品可空列压成 0：装备耐久恒下发 0（修理不生效的表象）、点击宠物崩溃

- **现象**：以 `act1_schema.sql` + `data_schema.sql` 重建 game.db 后：装备耐久显示恒 0，副本磨损（CMD 51）与修理（CMD 25）在库中真实写入、重登后仍显示 0，表现为「耐久不保存」；背包宠物页点击宠物客户端必崩。
- **根因结论**：种子导出工具 `tools/export_state_sql.py` 把 `CharacterItemRecord` 的九个可空标量经 `num()/b()/or ""` 压成 0/空串落库，破坏「null 原样存取」约定（`act1_schema.sql` character_items 列注释、`SqliteStateMapper` 头注均明示按 0 落库会改变下发字节）。两条下发链路受害：①线路耐久 = `AvatarAbilityIndex ?? Durability`（`CharacterItemWireProjection.Project`），种子把 `avatar_ability_index` 写成 0 后全部装备耐久恒下发 0，修理/磨损的库值被 0 覆盖，表象即「不保存」；②条目 `add_info` = `AvatarRemainingSeconds ?? CountOrValue`，宠物容器条目的实例 uid 被覆盖为 0，客户端按 uid 查宠物状态对象落空即解引用崩溃（与 `CharacterCreatureInventoryPlanner.NormalizeCreatureInstance` 已登记的客户端空指针机理一致）。库内 `durability` 数值本身一直正确，`SqliteStateMapper.BindItem/ReadItem` 读写链路无误，服务端代码零改动。
- **改动**（仅种子工具与产物）：九个可空物品列与邮件附件三个可空列改为 null 透传（JSON null → SQL NULL，bool → 0/1）；回放校验从 6 列扩为全部 16 列（含九个可空列与 instance_id），同类回归无法再通过校验；重新生成 `docs/sql/data_schema.sql`（穿戴时装的合法 0 保留，普通装备/宠物饲料/宠物蛋九列恢复 NULL）；产物头部用法指向现行基线路径。
- **验证**：工具回放逐字段校验通过（341 物品 16 列与 state.json 一致）；以新种子回放内存库模拟 `CharacterItemWireProjection` 投影，装备线路耐久恢复为库值（34/38/30/…）、宠物条目 uid 恢复（宠物蛋 2–5、已装备 1）。**由旧种子重建的存量 game.db 含同样坏数据，需删库后按新种子重建**；真机复测判据见 `PENDING.md` P-28。真机复测（2026-10-01，抓包 + 部署库对账）：判据②③④通过——修理耐久带值且金币扣减与库值一致、宠物列表正常下发且点击不崩、堆叠数量经 `add_info` 正常；①的耐久掉落往返因两轮全程未出现 CMD 51 待补，剩余动作收敛登记于 `PENDING.md` P-28。

### ADD-14 M2 按频道监听：会话频道随所选频道绑定（切频道后右上角/喇叭频道号正确）

- **现象**：M1 单监听形态下，客户端切换到任意频道后右上角恒显示「01 新手-洛兰」，喇叭横幅恒渲染「1频道」。取证确认客户端不在任何包里上报所选频道（三次不同频道状态下 LOGIN 解码正文逐字节一致，尾部仅启动器机器标识；NOTI 130 报文本身无频道字段，「频道N」由客户端以 NOTI 1 给定的频道号渲染）——真服以「每频道独立端口」让服务端凭连接端口识别频道，M1 把全部频道端点写成同一监听后服务端无从区分，NOTI 1 恒下发目录首条目（频道 1）。
- **改动**（设计见 `docs/servers4a21/feature-backfill-plan-vs-s4a21.md` §4.3 M2，用户确认后实施）：`ServerOptions.ChannelOptions` 新增 `PortBase`（默认 20000），频道 N 监听 `PortBase + N`；`Network/EntranceService.cs` 为目录每条频道起独立 TcpListener，accept 时携带绑定频道，`Channel.Port`（7001）恒保留为遗留频道端口（绑定目录首条目身份，承载其余八组回退记录端点与旧配置直连），入口端口表排除该端口避免双绑；`ChannelSessionState` 新增 `SessionChannel` 落位连接绑定频道；NOTI 1（`CheckConnectionHandler`）、喇叭 NOTI 130 flag（`SendMessageHandler`，跨频道收件人按客户端原生分流走仅横幅渲染）、频道通关经验加成（`DungeonExperienceCatalog` 改为按 etc「额外经验比例」列逐频道懒加载，`SetPlayResultHandler` 传会话频道号）全部改读会话绑定频道；`EntranceMetadata.BuildChannels` 组 0 目录频道记录写各自端点与各自在线人数（`CharacterSessionRegistry` 逐频道连接计数：频道监听 accept 时 +1、读循环收尾 -1，元数据 CurrentUsers 改按频道取值——解决「进游戏后全部频道显示良好」的总数误报），其余八组回退记录保持遗留端口与其绑定频道计数；PVF 无目录时回退单频道监听，行为字节不变。server.json 模板 `Channel` 节新增 `PortBase`；README、`docs/design/08-sqlite-storage.md`、`packet-detail-connection-60cn.md` 同步。
- **验证**：服务端与 SelfTests 构建 0 警告，`entrance-channel-catalog`、`dungeon-clear-experience`、`megaphone` 套件通过（含逐频道端口字节断言 20001/20006/20010/20011/20200、逐频道在线人数差异化断言（0/4/回退值）、遗留端点 7001 保留断言、频道 15/16 差异化经验加成断言；5 项存量 SQLite 临时库文件锁失败与本次无关，干净树复现一致）；真机复测见 `PENDING.md` P-31（2026-10-01 补：双客户端会话均落 `channel:20001`，按频道监听真机生效；同频道双开的到位/摆摊定位/退出跨会话广播正常，UI 判据待目视）。

### FIX-10 切频道 ~10.7 秒停顿的「NOTI 1 重置 NAT 模块」补丁：两轮真机证伪，已整体撤销

- **最终状态**：路线 A 补丁（重定向 NOTI 1 处理分支 0x419647 的 `call sub_78C3B0`，在探测列表/端口填充后清除模块跨会话残留字段）经两轮真机实验全部证伪并整体撤销，代码已还原：第一版桩漏转发 `port1/port2` 参数且未清参（该调用为 `__thiscall(this, port1, port2)`、被调方 `ret 8` 自清），探测列表端口错乱，`ResetChannelNatDiscoveryOnChannelInfo=1` 时**首次选频挂死**（0/1 对照实验抓出）；修正转发后，**客户端在 NOTI 1 处理中即静默挂死**（AppHangB1，收到 NOTI 1 后连 LOGIN 都不发出），静态层看钩子仅四个字段写+参数透传、行为应良性——运行态交互超出静态可证范围。Config.ini 键与 README 键名一并移除。
- **取证存档**（对后续有效，全部地址登记 `client-function-register-60cn.md` §10）：①模块 `dword_B5DCE4` 的每次会话再初始化其实大部分已由 NOTI 1 处理内建——`sub_78CE00` 清探测列表/使用位图并置 `this+436=-1`、`sub_78CEB0` 重填主机、`sub_78C3B0`（仅两次赋值 +496/+500）重设端口对；**跨会话残留只有状态 +0x40、上报类型 +0x44、重试计数 +0x1AC、等待标志 +0x1B0 四字段**；②清除这四字段的时机/方式仍不对——挂死点在 NOTI 1 处理内部，提示状态机存在静态未见的隐蔽不变量（或 VMP 运行态差异）；③挂起形态为 AppHangB1 主线程停止响应（事件日志 22:34），非内存违例。
- **后续路线**（按优先级）：a. 补齐 `sub_78C730` 尾部、`this+504`/`this+436` 生命周期与 state 0 路径的完整契约后再定新钩点；b. 构建带逐 tick 模块状态日志的诊断版 DLL，一次真机挂起复现即可拿到状态轨迹；c. 有真服环境时抓「切频道」服务端字节做对照（最快定谳是否服务端缺信号）。

### FIX-09 跨频道悄悄话不显示发送人：投递前补送发送方城镇外观

- **现象**：M2 后跨频道悄悄话可达但对端聊天行**不显示发送人**（只有文本）；同频道正常。
- **根因结论**：悄悄话下行 = NOTI 12 type 1（`msgType + 发送者区域 uid + 文本`），报文无名字字段，接收端 case 12（sub_849280 mode 1/7 分支）按 uid 在**本机城镇用户表**解析说话人名字与气泡。FIX-08 名册按频道隔离后，跨频道收发双方不再交换外观，接收方城镇用户表（NOTI 2 mode 0 的 find-or-create @0x4FDF60 建立）没有发送方记录，名字解析落空。与既存登记「跨区域/跨城镇队伍行只显示文本」同源（packet-detail-chat-60cn.md type 2/11 遗留项）。
- **改动**：`PartyCoordinator.TrySendToName` 增加发送方上下文前置——解析目标后，若发送方与目标不同城/同区域/频道三者任一不满足，先向目标投递一份发送方的 mode-0 城镇外观（`BuildTownAppearancePacket`，仅外观包、无进城副作用序列），为城镇用户表建立可解析记录后再投聊天行；同城同频道维持原单包路径。首登流 CMD 8 列表与既有外观交换不受影响。
- **验证**：构建 0 警告，`party`/`megaphone` 套件通过；真机复测判据并入 `PENDING.md` P-31。遗留观察项：mode-0 记录是否在接收端渲染静止城镇角色（登记于 P-31；若出现，改走 mode 3 或收缩为用户表注入）。

### FIX-08 城镇可见性跨频道串人：名册按频道隔离（M2 补全）

- **现象**：M2 后不同频道的玩家在同一城镇同区域互相可见（12 频道能看到 14 频道的人），外观/位置/离开通知跨频道串发。
- **根因结论**：`PartyCoordinator` 城镇名册只按「城镇 + 区域」匹配（`SameAreaSnapshotLocked` 无频道维度）。M1 单频道时代等价于按频道隔离；M2 多频道后同城区跨频道的居民互相进入对方的同区域快照，到达/移动/离开三类广播全部串频道。
- **改动**：`PartyPresenceUpdate` 与 `PresenceEntry` 携带频道号（取会话绑定频道，`ChannelSessionState.SessionChannel`）；`SameAreaSnapshotLocked` 增加频道相等过滤，到达/位置/离开/区域聊天（`SendToSameArea`）四条路径一并隔离。私聊（按名字定向 `TrySendToName`）保持跨频道可用；组队名册暂未隔离频道（见 `PENDING.md` P-31 备注）。
- **验证**：构建 0 警告，`party` 套件通过；真机复测判据并入 `PENDING.md` P-31（双客户端不同频道同城应互不可见，同频道互见不变）。

### FIX-07 切频道 ~10.7 秒停顿的「补推角色列表」假设：真机证伪，已撤销

- **最终状态**：该修复（LOGIN 应答后 500ms 未见 CMD 8 则补推角色列表）经真机复测**无效并撤销**——补推确实送达（抓包确认切换会话 LOGIN 后 ~0.5s 出现服务端 NOTI 2），但客户端仍在 LOGIN 后静默 10.7/10.9s 才同毫秒爆发 CMD 2 + CMD 4。由此定谳：**停顿与角色列表无关**，客户端在重连回归流（EXIT + 重连新频道端口）里等待的是其他未知门控。代码已完整回滚（`LoginHandler`/`ChannelSessionState`/`GetUserInfoHandler`），未定论事项与全部取证见 `PENDING.md` P-32。
- **取证存档**（对后续排查有效）：①停顿时长高度一致（10703/10800/10714ms），是客户端固定超时；②停顿窗口内客户端无任何网络动作（无 CMD 8、无 UDP NAT 探测、无 keepalive、无其他 TCP 连接尝试，netstat 300ms 采样证实）；③新旧连接均正常关闭（netstat 逐连接时间线：旧连接在新连接建立后 ~1s 内消失，无泄漏）；④该停顿先于 M2 存在——M2 前的 EXIT 重连流即有 9.8s/10.8s 静默（旧抓包），M2 使每次切频道都走该路径而全面暴露；⑤同连接的 CMD 7 回归流（不重连）无停顿且客户端正常发 CMD 8。

### FIX-06 多频道改造后选频列表为空：入口元数据记录名未携带频道号

- **现象**：入口频道目录化（回填方案 §4.3 M1）后，客户端登录后选频界面频道列表为空；多频道改造前的单频道回退形态正常。
- **根因结论**：60A1 客户端应用入口元数据时，从每条记录的显示名提取全部 ASCII 数字字符拼接后 `atol` 作为频道号（`sub_8929C0`，0x892A24 逐字节判定 0x30–0x39），再以（组下标+1, 频道号）查客户端频道表（`sub_71FE80`），未命中则该记录静默跳过不注册（`sub_894B10` 0x894D36–0x894D93，`if (buf[23])` 分支），不抛错也不触发重试。`EntranceMetadata.BuildChannels` 此前把 etc.chn.str 解析出的显示名原样写入记录名，而现网频道名（「格兰之森 A」「新手 - 洛兰」等）不含任何数字——凯恩组 38 条记录全部推出频道号 0、全部脱靶，列表为空。单频道回退名 `Local Channel 1` 自带号数，故旧形态正常。抓包互证：解密本次登录的协议 3 元数据（密钥取自同会话协议 12 明文回包）确认下发记录名确无数字，且协议 1 仅一次交互无重试，与静默跳过吻合。
- **改动**：`EntranceMetadata.BuildChannels` 记录名组合为「显示名 + 空格 + 频道号」后 CP936 写入，并拒绝含数字的显示名（构造期防御）；`ChannelCatalog.TryParseChannelInfoEtc` 将显示名含 ASCII 数字的行按坏行丢弃（回填方案 §4.3 风险④要求的目录校验落地）；SelfTests `entrance-channel-catalog` 同步组合名断言，新增数字名行丢弃与构造拒绝用例。
- **验证**：服务端与 SelfTests 全量构建 0 警告，`entrance-channel-catalog` 套件通过（5 项存量 SQLite 临时库文件锁失败与本次无关，干净树复现一致）；静态 IDA 与抓包解密互证如上；真机复测见 `PENDING.md` P-29。

### FIX-05 自定义队名下行通道定谳：设置段 kind==0 原生读 dstr，撤销 ADD-11 扩展块方案

- **现象**：「设置段 + type 4 扩展块」组合在真机上自定义队名始终不可见。
- **根因结论**：NOTI 9 设置段读取端（0x41B8F9）在 kind==0 分支调用的 `sub_8B3250` 无条件从游标消费 `u32 长度`：长度在 1..0x3F 时按长度读入 CP936 文本直接写入队伍名，0/≥0x40 时回退本地串且不消费载荷——**原生协议本就承载自定义队名文本，且与上行 CMD 12 的 dstr 形态对称**。ADD-11 的设计前提「下行不承载队名文本」系把该 dstr 原语误读为固定拷贝本地串，扩展块方案在任何客户端上都不可能工作。字段级结论见 [packet-detail-party-60cn.md](docs/protocol/packet-detail-party-60cn.md)；由本事件固化的六条强制核对项见 [ida-call-chain-convention.md](docs/protocol/ida-call-chain-convention.md) §6。
- **改动**：方案经用户确认按「原生 dstr 形态，撤销扩展」执行。服务端 `PartyPacketBuilders.WritePartySettings` 改为：kind≠0 发 `kind + userMax`，kind==0 发 `kind + u32 长度 + 文本 + userMax`（空自定义名发零长度，客户端显示『无队伍名』；长度 ≥0x40 抛 `ArgumentOutOfRangeException`，与上行解析一致）；删除 `CustomTitleBlockType`/`HasCustomTitle`/`WriteCustomTitle` 与追加循环，`MaximumCustomTitleBytes` 更名 `CustomTitleLengthCap`（互斥上限语义），`PartyInfoWireBlock.TitleBytes` 保留；SelfTests `party` 套件断言重写（kind==0 三形态字节断言 + 端到端改名设置块断言）。客户端 `Client/DAFLegacy.Ijl15` 移除 `UsePartyCustomTitle` 全部实现与配置——类型分派接管钩子、五处诊断钩子、日志辅助、`ijl15_config` 开关及 Config.ini 键、`ijl15_tests` 对应断言；自定义队名自此完全由原生协议承载，与补丁无关。
- **验证**：服务端与原生全量重建 0 警告，SelfTests 全绿、CTest 通过；真机复测通过（部署新服务端后，任意客户端建队/改自定义队名均直接显示所输文本）。

### ADD-13 客户端镜头拉近（命中画面放大）逆向登记：纯客户端实现，无协议面

- **结论**：副本内攻击命中（含暴击）时的整画面放大为**纯客户端实现、无协议面**。开关是客户端选项下标 3（INI 段 `DNF COMMON` 键 `ZOOM EFFECT`，默认 1=开）；由屏幕缩放控制器逐帧推进三段状态机（放大 → 保持 → 还原），主路径参数 2.0 倍 / 300 ms / 保持 1000 ms，锚点取命中目标包围盒中心，关闭开关会立即复位在播效果。对全部 152 个 NOTI case 按「调用缩放入口 / 访问控制器字段 / 引用输出全局量」三类特征逐 case 扫描，命中 0 处——没有任何包驱动该缩放，日志中的击杀包只是缩放时刻的伴生记录，服务端不参与。
- **改动**：仅新增与回填文档，无代码改动。选项系统、控制器字段、状态机与时间线、触发点与调用链见 [client-camera-zoom-60cn.md](docs/protocol/client-camera-zoom-60cn.md)；函数登记回填 `client-function-register-60cn.md` §9。
- **遗留项**：`PENDING.md` P-09（真机画面复测）、P-15（启动条件语义）、P-16（0x5E37CE 处 1.5 倍调用是否由网络侧驱动）、P-17（命中计数器与档位语音语义）。

### ADD-11 自定义队名可见：NOTI 9 type 4 扩展块 + 客户端补丁（已由 FIX-05 撤销）

- **最终状态**：该方案以「NOTI 9 追加块类型 4 扩展块（服务端）+ `UsePartyCustomTitle` 接管补丁（客户端）」实现，经真机多轮插桩复测证伪并撤销——其设计前提「下行不承载队名文本」系对设置段读取端的误读，扩展块在任何客户端上都不可能工作。自定义队名最终由 FIX-05 的原生 dstr 通道承载；客户端接管钩子、诊断钩子与日志辅助已全部退役，`ijl15_config` 的 Config.ini 运行时配置框架（ADD-12）保留。撤销定谳与最终形态见 FIX-05。

### ADD-12 IJL15 功能开关改为 Config.ini 运行时配置

- **内容**：`dnf_patches.h` 的 11 个编译期功能开关（反调试探测、多开、启动不最小化、装扮期数合并、[etc] 堆叠自定义使用、弹窗公告去标题、外部串表、邮件归档刷新、点券门控总开关、自定义队名、日志明文）与日志路径重定向、type-16 提示音屏蔽开关全部迁入 `ijl15_config` 运行时配置：`Config.ini`（客户端可执行文件旁）的 `[Features]` 节逐键开关，`[CeraPurchaseGate]` 节逐门控开关（8 项，随总开关生效）；缺失的键回落编译期默认值，既有客户端配置无需修改即行为不变。
- **结构性改动**：`dnf_patches.cpp` 的补丁表从 `constexpr` 改为配置加载后的 `BuildPatches()` 运行时构建，逐项范围校验收口 `ValidatePatchRanges`；`ShouldBypassCeraPurchaseGate` 改为读配置。
- **验证**：原生 Release 编译 0 警告、CTest 通过；配置模板 `Config.ini` 全键默认值化并附中文说明。README「启动器」与 `packet-detail-party-60cn.md` 相应键名标注同步。

---

## 2026-09-29

### FIX-04 改队伍名被拒：CMD 12 容量字节越界（自定义队名不显示一项后由 FIX-05 定谳）

- **现象**：单人队伍改队名不生效；部分改名请求被服务端直接拒绝，客户端弹『FAIL : 您不是房主，不能要求变更房间环境！』。
- **根因结论**：改名请求的 userMax 字节越界——创建窗口对已存在队伍重开时从 `*(队员记录 + 0xFC) + 0x3C` 读到 `0x30`（48），超出服务端 `IsSupportedCapacity(2..4)` 校验，请求整体拒绝（错误码 8），同一请求内的队名改动一并失效。该容量字节是客户端回读自存的队伍记录值，非队名内容。
- **改动**：`Game/Party/PartyState.cs` 新增 `PartyConstants.DefaultCapacity = 4`，`IsSupportedCapacity` 改为「修正判据」语义；`Game/Party/PartyManager.cs` 的 `SetPartyInfo` 不再因 userMax 越界拒绝——建队取默认容量、改设置保留队伍现有容量，修正经 `PartyOpResult.RepairedUserMax` 回报；有效容量但小于当前人数仍拒绝；`Network/PartyCoordinator.cs` 对修正记信息日志；SelfTests `party` 套件新增越界修正断言。
- **验证**：全量重建 0 错误 0 告警，SelfTests 全绿；真机复测通过（携带越界容量 `0x30` 的请求按原容量处理并照常下发设置块，无错误应答；预设与自定义改名链路均被接受）。
- **遗留项**：客户端开窗处为何读到 48 未定论（按登记偏移应为同一字段，实测读数不一致），见 `PENDING.md` P-14。本条目同批登记的「自定义队名在客户端无下行显示通道」结论后经 FIX-05 证伪更正，队名显示的最终结论以 FIX-05 与 `packet-detail-party-60cn.md` 为准。

### ADD-10 队伍聊天：CMD 17 type 2/11 按队伍名册投递

- **现象/结论**：队伍内发聊天消息本机无显示，原因是服务端 `SendMessageHandler` 只认 type 3/1/7/18/13/14/15，type 2（城镇）/11（副本）被丢弃。客户端取值与显示语义：聊天窗频道下拉选择项 1（队伍）在城镇取 type 2、副本取 11，两值属同一频道（同色、同页签开关）；下行经 NOTI 12（`mode u8 + uid u16 + dstr 文本`）按发送者 uid 渲染聊天行并走气泡路径；发送者本人的行只能靠下行副本回显（本地自渲染块仅存在于悄悄话分支）。
- **改动**：`Network/PartyCoordinator.cs` 新增 `SendToParty`（按队伍名册投递、含发送者本人，返回收件会话数）；`Network/Handlers/Chat/SendMessageHandler.cs` 新增 type 2/11 分支，以 `ChatPacketBuilders.CreateMessageNotification` 投递全体队员，type 原样透传；无队伍时告警丢弃；SelfTests 新增名册投递与实测报文解析断言。
- **验证**：全量重建 0 错误 0 告警，SelfTests 21 套件全绿；真机复测见 `PENDING.md` P-06。
- **遗留项**：跨区域队友聊天行缺名字前缀（需 NOTI 2 mode 3 快照）见 P-12；公会聊天（type 6）与其余场景频道未支持见 P-13；`LocalUserId = CharacterNo & 0xFFFF` 的跨账号 uid 撞号风险见 P-20。
- **文档回写**：`packet-detail-chat-60cn.md` CMD 17 表与「type 2 / type 11 队伍聊天」节；`client-function-register-60cn.md` §7；`client-packet-enums-60cn.md` 聊天表。

### ADD-09 新增 CMD 66 点券刷新查询（点券商城「刷新点券」按钮）

- **现象/结论**：点券商城点「刷新点券」后余额不更新：客户端以约 150ms 间隔连发数条 CMD 66（body 仅 seq、无载荷）即放弃，服务端无处理器。应答复用既有 `CreateCeraUpdate`（NOTI 53 `[u8 flag][u32 cera]`），与余额变更推送同报文；客户端的 NOTI 66 号位已被 `ENUM_NOTIPACKET_CERA_SPECIALITEM`（会员服务通知）占用，故本查询应答只能走 NOTI 53。发送器 `sub_85E1E0` 由按钮处理 `sub_864E10` 调用，NOTI 53 case 于 `0x4255BA`（登记见 `client-function-register-60cn.md` §1/§5）。
- **改动**：新增 `Network/Handlers/Shop/CeraQueryHandler.cs`（`economy.shop`）：请求体仅 seq，选角后推 NOTI 53 回推 `Currency.Cash`，未选角静默忽略；注册表 74 → 75 项；`protocol-golden` 新增三条断言。
- **验证**：全量重建 0 错误 0 告警，SelfTests 21 套件全绿；真机复测见 `PENDING.md` P-05。
- **关联**：ANTIBOT（CMD 161 → NOTI 152）不随实现——其 id 在客户端类型分派前被传输层拦截、无真服样本，按 D1 维持未实现，见 `PENDING.md` P-13 与 `client-packet-enums-60cn.md` NOTI 152/153 行。

### ADD-08 统一文件日志改为开关控制（默认关闭）

- **背景/改动**：`BoundedQueueFileLoggerProvider` 此前在组合根无条件注册，任何部署都会落盘 `logs/server.log`。现 `LoggingOptions.Enabled` 默认 `false`（`Infrastructure/ServerOptions.cs`）：关闭时不注册提供器、不创建日志目录、不建队列与后台消费任务；控制台日志不受影响。
- **验证**：三项目全量重建 0 错误 0 告警，SelfTests 全绿（`file-logger` 套件含默认值断言）。README「常用配置」补 `Logging.*` 四行。

### FIX-03 选角后无法进入城镇：装备归位循环 `continue` 被误写为 `return`（迁移期控制流缺陷）

- **症状**：真机选择角色后客户端停在载入画面，无法进入城镇；服务端无任何下行。
- **根因结论**：R1/R3 协议迁移把旧实现迁入 `SelectCharacterHandler` 与 `CeraShopBuyHandler` 时，持久化装备归位与购买落位循环内合计 11 处「本件处理完毕、继续下一件」的 `continue;` 被误写为 `return;`——处理到第一件即整条 handler 提前返回，后续应答与初始化包族全部未发出。
- **改动**：`Network/Handlers/Characters/SelectCharacterHandler.cs` 4 处、`Network/Handlers/Shop/CeraShopBuyHandler.cs` 7 处 `return;` → `continue;`，恢复旧实现语义；两文件与旧实现整段规范化对拍复核，无其它语义偏差。
- **验证**：全量重建 0 错误 0 告警，SelfTests 21 套件全绿；真机复测通过——选角 → 进城及进城后移动、聊天与喇叭、组队、点券购买、删物品、商店买卖、体力、技能、副本通关与放弃、邮件打开与删除等路径全部正常，全环无 CRC 失效、无缺失应答；已覆盖与未覆盖清单见 `PENDING.md` P-01、P-02。
- **遗留项**：迁移期其余片段是否还有同类失真，随真机全流程冒烟（`PENDING.md` P-01）逐协议复核。

### ADD-07 后台邮件表单附带点券发放（账号级即时到账，非邮件附件）

- **背景/功能**：邮件协议三个环节（发送 CMD 97、记录 NOTI 97、领取 CMD 98）均无点券字段，邮件本身无法携带点券。`/admin/index.html` 邮件视图新增「点券（发放到账号）」字段：填写大于 0 时先调用 `POST /api/client-pay/cera` 发放点券、再调用邮件接口落信；点券为账号级数据、即时到账，与邮件是否被领取无关。
- **校验放宽**：`JsonGameStore.GrantCeraToAccountAsync` 的校验由「正整数且为 100 的倍数」放宽为「正整数」——点券在 NOTI 53 中为 u32 原值直传，协议侧无取整要求；`ADMIN_API.md` §5.2.7 与 §6.1 同步。
- **非原子语义**：两次调用不构成事务；点券已发放而邮件发送失败时页面明确提示，点券保留、邮件不落库。
- **验证**：全量重建 0 错误 0 告警，SelfTests 全绿（含非 100 倍数发放与 0/负数拒绝断言）；页面组合动作经桩后端实测（先发放后发信、失败文案区分、0 值不发起调用、负值被原生约束拦截）。真实服务端的页面验证由用户手动执行，判据见 `PENDING.md` P-08。

### ADD-06 协议层重构管线落地（R0–R7 收官：71/71 协议挂载新链路）

- **内容**：按 `docs/design/07-protocol-layer-refactor.md` 整体落地（分支 `refactor/protocol-layer`）：`GameCommandRegistry` 字典注册表 + `GameCommandDispatcher` 通用管线（端口/阶段门槛统一校验）；`ChannelSessionState` 十二域会话状态对象化；`ConnectionQueue` 有界出站队列 + 每连接单写者泵（背压阻塞、不丢包不乱序、发送计账单点）；`ConnectionTimerScheduler` 单调一次性定时器基建；71/71 协议全部挂载新链路，注册表 75 项（含 CMD 66 与组队族薄壳）；Parser/Handlers/Parsers/Builders 按 `Network/{Core,Protocol}` 两极与三层镜像归位，引擎按域拆分为 14 个分组构建器；协议号枚举 `CmdPacketType`/`NotiPacketType` 方向分表（163/154 项，源 `tools/packet_id_map.json`）。诊断工具随枚举化收敛：`PacketNames.cs` 删除（反查并入 `PacketTypeNames`）、`PacketPlainText.cs` 保留但断开接线（`protocol_decode` 工具随之下线）、`tools/gen_packet_names.py` 删除。最终目录形态、登记偏差与工具处置见 07 §11；跨会话推送统一经目标会话出站泵入队，fire-and-forget 直写他连流的裂缝在已迁移协议面上机制化封口。
- **验证**：SelfTests 全量通过（含 `protocol-golden` 71 协议字节等价守护，样本库 `tools/packet_bytes.json`；`HandleClientAsync` 全链路字节行为经内联检查与 golden 回归为零变化）；真机冒烟状态见 `PENDING.md` P-01。
- **遗留项**：真机全流程冒烟见 P-01；R6 定时器收编（唯一未完成的机制项）见 P-10；PacketDumper 重写与枚举重生成脚本见 P-11；多人副本数据面前置缺口与端点归属歧义见 P-12、P-19；uid 唯一性见 P-20。

### ADD-05 新增 DAF-MCP 抓包网关（<http://127.0.0.1:12222/mcp>）

- **功能**：服务端内置标准 MCP 服务器（默认关闭），供智能体实时抓包分析协议——报文名富化的收发包流、单包完整字节、包号↔报文名检索。
- **开启步骤**：
  1. 服务端发布目录需存在 `DAFLegacy.Mcp.dll`：由 `publish-mcp.bat` / `publish-mcp.sh` 发布到 `dist\DAFLegacy.Mcp` 后手动拷入；未拷贝时服务端照常运行，仅不加载 MCP。
  2. `server.json` 的 `DAFLegacy.Mcp.Enabled` 改为 `true` 并重启服务端（`Mcp.Host` / `Mcp.Port` 默认 `127.0.0.1:12222`）。
  3. 在 MCP 客户端配置中加入：

  ```json
  {
    "mcpServers": {
      "daflegacy": {
        "type": "http",
        "url": "http://127.0.0.1:12222/mcp"
      }
    }
  }
  ```

- **工具清单（均为只读）**：
  - `packet_watch`：实时跟踪收发包（`tail -f` 语义），`afterSeq` 游标续读，可按方向/协议号/报文名/会话过滤；
  - `packet_get`：按 seq 取单包完整详情（含 body 十六进制与 RX 密文 wireHex），供逆向拷字节；
  - `packet_wait`：挂起等待第一条匹配事件，用于"做了操作等回包"式往返验证；
  - `packet_names_search`：在 60CN 报文名注册表中按名字片段或包号检索；
  - `session_list`：列出当前网络会话，定位要盯的 sessionId；
  - `tap_status`：抓包通道状态自检（当前 seq、环形存档占用）。
- **典型用法**：`session_list` 找到目标会话 → `packet_names_search` 查包号定过滤 → `packet_watch` 续读跟踪；需要等特定回包时用 `packet_wait`，深挖字段用 `packet_get`。
- **安全**：默认仅回环监听；改为非回环等同主动暴露协议流与游戏数据。
- **关联**：`docs/design/09-mcp-packet-tap.md`（设计全档）；README「常用配置 → 服务端 → MCP」。

---

## 2026-09-28

### ADD-04 新增喇叭功能（CMD 17 type 13/14 + NOTI 130 全服广播）

- **功能/结论**：使用服务器喇叭（PVF 物品 36，type 13）/ 频道喇叭（PVF 物品 37，type 14）发言：喇叭不走物品使用命令，走聊天命令 CMD 17，请求体在普通聊天布局外以 slot 字段指明喇叭所在背包槽（服务端凭此校验并扣减道具）；随后向全部在线会话广播 NOTI 130 大横幅消息（`u8 msgType + u8 channelNo + u16 speakerUid + dstr 发送者名(cap 0x1E) + dstr 消息(cap 0x200)`），构包器对两个 dstr 长度做硬性上限校验，type 原样透传。客户端按 channelNo/speakerUid 与本机值的比对结果区分显示形态，字段语义与显示分支详见 [packet-detail-chat-60cn.md](docs/protocol/packet-detail-chat-60cn.md) NOTI 130 节。
- **实现**：`Protocol/`（现 `Network/Core/PacketTypes.cs`）常量 `MegaphoneMessageNotification = 130` 与构包器 `CreateMegaphoneNotification`；`Network/EntranceService.cs` CMD 17 分支放行 type 13/14（槽位权威校验、扣减持久化、回 NOTI 14 单条目刷新）；`CharacterSessionRegistry` 新增 `NotifyMegaphoneAll` 全服遍历。
- **验证**：冒烟全绿（真实抓包帧解析、NOTI 130 字节逐字节断言、容量与空消息拒绝路径）；真客户端复测通过（发言者与他人显示形态符合分册语义）。
- **待确认**：见 `PENDING.md` P-18（type 14 ↔ 物品 37 映射、flag 字节语义细节、type 15 喇叭变体未处理）。
- **关联**：[confirm-reply-packet-format-60cn.md](docs/protocol/confirm-reply-packet-format-60cn.md) §5（NOTI 130 布局登记）；[new-packet-formats-60cn.md](docs/protocol/new-packet-formats-60cn.md) §1（已收敛至聊天分册）；[client-packet-enums-60cn.md](docs/protocol/client-packet-enums-60cn.md) NOTI 130 行。

### ADD-03 新增 ANTIBOT 上报消费（CMD 161）

- **现象/结论**：客户端登录时发一条 92 字节、此后每 60 秒一条 18 字节的 CMD 161，服务端原每分钟告警。线上格式：`body = seq u16 + u32 声明长度 + 记录`，记录以大端 u16 kind 开头（已观测登录环境上报 kind 0x0003 与周期心跳 kind 0x000A）。契约特性：**fire-and-forget**——无应答时客户端心跳计数器持续递增，不重试、不断线、无惩罚路径，服务端不应答即正确行为（发送器 `sub_8C74F0` 属 ANTIBOT 模块；客户端内嵌 SNMP 栈同含立即数 161，与游戏协议无关）。
- **改动**：协议号常量 `AntibotCommand = 161`；`Game/Antibot/AntibotReportParser.cs` 解析 seq、声明长度校验与记录 kind；`Network/EntranceService.cs` 显式分支静默消费并以 Debug 级日志记录；SelfTests 以真实抓包帧断言解析与拒绝路径。
- **验证**：冒烟全绿；真机复测见 `PENDING.md` P-04。
- **关联**：[client-packet-enums-60cn.md](docs/protocol/client-packet-enums-60cn.md) CMD 161 行；配对 NOTI 152 无真服样本，保持未实现，见 `PENDING.md` P-13。

### FIX-01 返回选角后公告弹窗需要确认两次

- **现象**：首次登录选角，活动公告弹窗确认 1 次；进入城镇后返回选角，同一弹窗需确认 2 次。
- **根因结论**：客户端返回选角（CMD 7）后自行发 GET_USERINFO（CMD 8）拉取角色列表，而服务端在 CMD 7 应答后又主动补发一份 NOTI 2（mode=2），同一帧列表到达两次；客户端每次收到列表都重建选角界面并重放活动公告，弹窗次数与列表帧数 1:1 对应。
- **修复**：删除 CMD 7 处理器中应答后的主动补发，以 CMD 8 应答为列表唯一来源（`Network/EntranceService.cs`）。
- **验证**：冒烟全绿；真机复测见 `PENDING.md` P-03（返回选角后弹窗应仅 1 次）。
- **关联**：[client-packet-enums-60cn.md](docs/protocol/client-packet-enums-60cn.md) CMD 7/8 行。
