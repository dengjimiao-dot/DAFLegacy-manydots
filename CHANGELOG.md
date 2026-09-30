# 60A1 协议修复日志

记录协议行为修复与新增处理的根因、权威验证过程（IDA + 抓包）与改动。

只收录已完成并已验证的条目；未完成、待真机复测、未定论与存在争议的事项一律登记在 `PENDING.md`，本日志不留其正文。日期小节与条目均按最后修改时间倒序排列（最新在前）。

---

## 2026-09-30

### FIX-05 自定义队名下行通道定谳:设置段 kind==0 原生读 dstr,撤销 ADD-11 扩展块方案

- **现象**:「设置段 + type 4 扩展块」组合在真机上自定义队名始终不可见。经四轮真机插桩定位,最终由双端字节对照定谳——这不是服务端/补丁的实现缺陷,而是 **ADD-11 的设计前提(「下行不承载队名文本」)系对客户端读取端的误读**,扩展块方案在任何客户端上都不可能工作。
- **根因(误读点)**:NOTI 9 设置段读取端(0x41B8F9)在 kind==0 分支调用 `sub_8B3250`(0x41B955,cap=0x40,fallback=本地串 0x272),该函数**无条件从游标消费 `u32 长度`**,长度在 1..0x3F 时按长度读入 CP936 文本直接写入队伍名(0x41B998),0/≥0x40 时回退本地串且不消费载荷。旧文档把 0x41B955 误读为固定拷贝本地串,得出「下行不承载队名文本」并据此发明 type 4 扩展块;实际上**原生协议本就承载自定义队名文本,且与上行 CMD 12 的 dstr 形态对称**。
- **权威验证过程(IDA + 双端抓包)**:① IDA 反汇编 `sub_8B3250` 全函数(0x8B3250..0x8B32E0)与设置段读取器(0x41B8F9..0x41B9AF),确认调用形态与回退条件;② 2026-09-30 02:24 真机会话双端对照——服务端日志发包 body `02 00 | 01 00 01 | 00 04 | 01 00 04 | 04 00 00 00 | 37 37 38 38`(2 字节设置段 + type 4 块),客户端插桩(块头钩子 + 游标后 8 字节转储)实测迭代 0 后游标停在 [10]、迭代 1 从 `00 00 00` 读起、`next=37 37 38 38 …`,与「kind==0 无条件读 u32=[6..9] → 回退,再读 userMax=[10]」逐字节吻合;预设名(kind≠0)不走 dstr 分支,故一直正常。
- **改动**:方案经用户确认按「原生 dstr 形态,撤销扩展」执行——服务端 `PartyPacketBuilders.WritePartySettings` 改为:kind≠0 发 `kind + userMax`,kind==0 发 `kind + u32 长度 + 文本 + userMax`(空自定义名发零长度,客户端显示『无队伍名』;长度 ≥0x40 抛 `ArgumentOutOfRangeException`,与上行解析一致);删除 `CustomTitleBlockType`/`HasCustomTitle`/`WriteCustomTitle` 与追加循环,`MaximumCustomTitleBytes` 更名 `CustomTitleLengthCap`(互斥上限语义);`PartyInfoWireBlock.TitleBytes` 保留。SelfTests `party` 套件断言重写(kind==0 三形态字节断言 + 端到端改名设置块 14 字节断言),`protocol-golden` 不含组队样本不受影响。客户端 `Client/DAFLegacy.Ijl15` 无需改动:接管代码对 type 1 仍走原生设置段读取,文本由原生路径直接显示;`UsePartyCustomTitle` 与全套观测钩子保留但不再有 type 4 块可命中,后续可随客户端构建退役。
- **验证(离线)**:服务端全量重建 0 警告,SelfTests 21 套件全绿。
- **真机复测(2026-09-30,用户确认)**:部署新服务端后建队/改自定义队名均直接显示所输文本,修复生效(P-07 结项)。
- **客户端退役(2026-09-30)**:`Client/DAFLegacy.Ijl15` 移除 `UsePartyCustomTitle` 全部实现与配置——`dnf_patches.cpp` 的类型分派接管(`PartyCustomTitleHook`)、五处诊断钩子(循环头/块头/两静默出口/查找调用)、常量与 `DAFLegacy.Ijl15.party-title.log` 日志辅助,`ijl15_config` 的开关及 Config.ini/README 键,`ijl15_tests` 对应断言;原生 Release 重建 0 警告、CTest 通过。自定义队名自此完全由原生协议承载,与补丁无关。
- **定位反思(详见 `docs/protocol/ida-call-chain-convention.md` §6)**:误读存续三日的直接原因,是设置段分析在调用点层面按实参形态把 `sub_8B3250`(带上限与回退的 dstr 读取,无条件先消费 u32 长度)当成了"固定拷贝本地串",从未反汇编进被调函数本体;而本规范 §2 与函数寄存册当时均已把 `sub_8B3250` 登记为 dstr 原语——证据在册却未与调用点表象调和。据此把「原语必须读到叶子、登记册与调用点表象冲突必须先调和、上/下行形态默认对称、【差异】标记即待验证项、静默无效果优先按游标错位排查(游标转储为首轮手段)、承接为设计前提的既有结论须重验」六条写入规范。
- **遗留项**:真机复测待用户执行(判据见 `PENDING.md` P-07):部署本服务端构建后,任意客户端(含未打补丁)建队/改自定义名应直接显示所输文本。
### ADD-13 客户端镜头拉近（命中画面放大）逆向登记：无协议面

- **背景与结论**：副本内攻击命中（含暴击）时整个画面会按倍率放大。经客户端主程序 IDA 库静态逆向确认该表现为**纯客户端实现、无协议面**：开关是客户端选项下标 3（INI 段 `DNF COMMON` 键 `ZOOM EFFECT`，默认 1=开，取值范围 0..1，读取函数 `sub_8A9570`）；缩放对象是整画面而非角色，落点为全局缩放因子 `n1065353216` @0xB0EF38 与两个方向偏移 `n480`/`n640`；由一个"屏幕缩放控制器"逐帧推进三段状态机（放大 → 保持 → 还原，推进函数 `sub_4E6140`）。主路径参数为 2.0 倍 / 300 ms / 保持 1000 ms，锚点取命中目标包围盒中心，启动点 `sub_4A97D0`（命中处理，0x4A9937）→ `sub_4E8AB0`，启动条件含"攻击者为本机玩家"（`攻击者->vtbl[0x25C]() == tm_min`）。关闭开关不只是不再启动，还会立即复位在播效果。
- **权威验证过程（IDA 静态，ida-pro-mcp）**：选项表三件套（键名 `lpKeyName` @0xB53F68、缺省值 `nDefault` @0xA86518、上下界 @0xA86580+@0xA86584）按下标 3 对齐并逐处反汇编核对；状态机三段与全局量写点逐处反编译核对（0x4E646C 写缩放、0x4E6471/0x4E6477 写偏移、`sub_4E3550` 复位）；启动点、其它启动点（左轮「暴击致死」`sub_5F8FD0`→`sub_4AA510`、HP 变化 `sub_4A1E40`、技能「GPUNCH」`sub_5A3530`、热键 `sub_494DA0`）与时间线原语（`sub_8F4FE0` 插值、`sub_8F3E80`/`sub_8F3F60`/`sub_431170`/`sub_405310`）逐函数反编译。**无抓包环节**：本功能不在协议面，预期无包可抓。
- **改动**：仅新增与回填文档，无代码改动（服务端与 `Client/DAFLegacy.Ijl15` 补丁均未改）。
- **文档回写**：新增 `docs/protocol/client-camera-zoom-60cn.md`（§2 选项系统含键表全表、§3 控制器字段、§4 状态机与时间线、§5 触发点、§6 与协议面关系、§7 遗留项、§8 调用链）；`client-function-register-60cn.md` 新增 §9；`docs/protocol/README.md` 索引挂链接。
- **遗留项**：见 `PENDING.md` P-15、P-16、P-17（启动条件语义、0x5E37CE 处 1.5 倍调用的协议面复核、命中计数器与档位语音语义）与 P-09（真机画面复测）。
- **续报（2026-09-30 抓包对照，否证「击杀包触发」）**：按 DAF-MCP 抓包网关当日一次频道会话存档核对副本内连续 25 次击杀，每只怪包序固定为 RX CMD 160 `SDC_DAMAGE_CHECK`（伤害记录批量上报，发送器 `sub_4AB300` @0x4AB344）→ RX CMD 42 `DIE_MONSTER`（击杀上报，发送器 `sub_843AC0` @0x843B39）→ TX NOTI 38 `DIE_MONSTER`（掉落）→ CMD 46/NOTI 39/NOTI 13（拾取）。对 NOTI 38 的客户端 case（0x4217CE..0x421EA7）逐指令核对：其时间线对象取自 `[[dword_B5DCE8]+0x54]+0x5C4`，与缩放控制器（字段 +0x8D4..、时间线 +0x8F4）不是同一对象。另做**全 152 个 NOTI case 逐 case 扫描**（匹配「调用 5 个缩放入口 / 访问控制器字段 / 引用输出全局 `n1065353216` @0xB0EF38」三类特征），**命中 0 处**——没有任何包驱动该缩放，日志中的击杀包只是缩放时刻的伴生记录，服务端不参与。同批新增静态证据：命中路径的目标判定槽 `vtbl[0x8C]` 实为转发桩 0x407480（`push 2; call [eax+80h]`，等价 `vtbl[0x80](2)`）；`vtbl[0x390]` 不是全类统一的无参分类取值（逐类实现不同：0x42C9A0 带参比较、0x568540 为写入器，且多类 vtable 长度不足 0x390 致槽值撞车），遗留项 ① 收窄为「候选读法：命中结果分类（暴击/致死）或对象类别」。真机画面复测与遗留项 ①/② 的区分见 `PENDING.md` P-09、P-15、P-16。
### ADD-11 自定义队名可见：NOTI 9 type 4 扩展块 + 客户端补丁（`UsePartyCustomTitle`）

- **背景/根因**：60A1 客户端的下行设置段只有 `titleKind + userMax` 两字节，队名由客户端本地渲染（6 条预设文案 / 本地串 0x272『无队伍名』），玩家输入的自定义队名（kind=0）在任何标准客户端上都无法显示。FIX-04 已修掉"容量越界导致改名被拒"，但"显示不出来"属客户端协议面限制；用户确认按 `packet-detail-party-60cn.md` CMD 12 节「扩展块」方案实现逐队自定义名可见。
- **协议面（本服扩展，兼容标准客户端）**：NOTI 9 在同一包内追加**块类型 4**（`u16 partyId + u8 type + u32 长度 + CP936 文本`），固定置于全部标准块之后，仅 `titleKind==0` 且文本非空时发出。标准客户端对该类型的处理路径已逐条核实：块循环读块头后按代号查队伍（`sub_4FEF70`），类型 4 不匹配设置段（0/1）、名册段（0/2）、清空段（3）任何分支，**不读载荷**即进入公共块尾 0x41BB35，块计数用尽结束；通知分发器**无游标消费校验**（全局游标 `src` 仅被读取原语 `0x8F5116..0x8F51D0` 触碰，收尾 0x41C2C3 无比较），故尾部未读文本字节被忽略——标准客户端行为与改动前完全一致。
- **改动**：
  - 服务端：`Network/Builders/Party/PartyPacketBuilders.CreatePartyInfo` 追加 type 4 块（新增 `CustomTitleBlockType`＝4、`MaximumCustomTitleBytes`＝0x40，块计数含扩展块；文本超限抛 `ArgumentOutOfRangeException`）；`Network/Protocol/PartyWireModels.cs` 的 `PartyInfoWireBlock` 增 `TitleBytes`；`Network/PartyCoordinator.cs` 的 `BuildBlock` 携带 `party.TitleBytes`，建队与改设置两条发布路径同时生效。
  - 客户端：`Client/DAFLegacy.Ijl15` 新增开关 `UsePartyCustomTitle`；`dnf_patches.cpp` 把类型分派分支 0x41B8F3 接管到 `PartyCustomTitleHook`（naked 汇编）：复刻原分支语义（type 1 → 0x41B8F9 设置段、其余 → 0x41B9B2），type 4 时按 0x40 上限截断读取 dstr（`sub_8F5160` 读长度、`sub_8F5180` 读字节并推进游标）、写入 NUL 终止符后经 `sub_44D280`（队伍记录 +4）定位名字串（+8）并以 `sub_402AD0` 写入，最后跳公共块尾 0x41BB35（其后的 UI 刷新随之更新显示）。字节校验沿用 `Expected`/`Replacement` 形态；客户端字节不匹配时**只跳过本补丁**并输出调试信息，不拖垮其余补丁。
  - SelfTests：`party` 套件新增扩展块字节断言（标准块字节不变、扩展块在末位且块计数为 2、预设 kind 不发出扩展块、超上限抛异常）与协调器端到端断言（修正容量后的改名设置块＝7 字节标准块＋10 字节扩展块，文本逐字节一致）；`protocol-golden` 71 协议字节守护不变。
  - 客户端测试：`ijl15_tests.cpp` 新增 `UsePartyCustomTitle` 开关断言。
  - 客户端（诊断）：安装与命中写入客户端目录下 `Logs\DAFLegacy.Ijl15.party-title.log`（`installed` / `install skipped: …` / `install failed: …` / 钩子每次进入记录 `entered block_type=… loop=下标/块数`（类型分派分支实际读到的类型字节与循环状态，与入口寄存器无关）/ `handled type4 len=… text_hex=…`（文本十六进制记录）/ 查找调用点接管后每次查找记录 `lookup id=… block_type=…`、块循环头接管后每次迭代记录 `loop index=… count=…`、块循环内两处静默中止（presence-check 出口 / 查找未命中）记录 `abort: …`，逐行追加、1 MB 上限、纯 ASCII），用于免调试器核实部署、命中与块循环状态。
- **验证（离线）**：服务端全量重建 0 错误 0 告警、SelfTests 21 套件全绿；原生 `build-native.ps1 -Configuration Release -RunTests` 编译 0 警告（C4819 由源码非 ASCII 字符触发，已改为纯 ASCII）、`ijl15` 兼容测试通过（直接运行 `ijl15_tests.exe` 输出 `ijl15 compatibility tests passed`；CTest 驱动在沙箱内报 operation not permitted，属执行环境限制）。
- **遗留项**：真机复测待用户执行，三项判据与 2026-09-30 客户端崩溃修复后的复核见 `PENDING.md` P-07。
- **顺带更正**：`packet-detail-party-60cn.md` NOTI 9 节的设置段旧表述（"kind==0 时 u32 长度+队名"及随之推导的长度公式）与读取端反汇编及实测均不符，已按两字节形态更正（type0＝20、type1＝7、type2＝18、type3＝5，扩展块另加 7+L）。
- **真机崩溃修复（2026-09-30，客户端构建 00:08 后复测）**：预设名改名直接崩溃（无转储）。诊断日志钉死位置——`installed` ✓、`lookup id=1 block_type=65537` ✓、`entered` 缺失、崩溃：查找调用点接管钩子调用 C++ 日志辅助函数时**破坏了 ECX**（C++ 调用约定下 ECX 易失），而 ECX 保存的队伍管理器指针正是尾跳的 `sub_4FEF70` 所需的 `this`；修复为钩子内 `push ecx / push edx` 保护、日志后恢复（`PartyInfoLookupLogHook`），块类型同时改为按字节记录（修复前按 DWORD 读会把相邻局部一并带进日志，出现 `block_type=65537`）。原生编译 0 警告、CTest 通过，钩子机器码逐字节复核（`push ecx/edx`、`movzx eax, byte ptr [ebp-0x622]`、`push [esp+16]` 取实参、`pop edx/pop ecx`、尾跳 0x4FEF70）。
- **复测定位补充（2026-09-30 01:0x 轮）**：入口日志的 cdecl 参数压序与形参相反，打印字段整体错位——`entered block_type=2 loop=0/1` 实际含义是「块数=2、下标=0、块类型=1（设置块）」，即客户端对双块包的解析完全正确（计数、块 1 分派均对），失败点在**迭代 1（type 4 块）开始后、查找之前被块循环内的静默出口中止**。两处静默出口（presence-check `0x41B85C`、查找未命中 `0x41B8B3`）的接管常量首字节误写（E8 应为 E9）导致字节校验跳过安装、中止原因未记录，已改为 E9 并重建；同轮还把入口日志扩展为记录循环下标与块数（`loop=下标/块数`）。
- **复测定位补充（2026-09-30 02:0x 轮，IDA 全循环静态验证 + 插桩修正）**：01:25:58 轮日志（`lookup id=1 block_type=1` → `entered block_type=1 loop=0/2` 后无任何后续行）经逐条核对为「双块包块 0（设置块）解析正确、迭代 1 全程无声」。IDA 复核 NOTI 9 读取端全循环：块循环回边唯一（`0x41C2BE jmp 0x41B7C5`），尾部各分支（`0x41BD3C`/`0x41BDA8`，按「块所属队伍是否为当前选中队伍」选路）与清除段均汇流 `0x41C29F`（type≠3 一律回边）；设置段读取器（`0x41B8F9..0x41B9AF`）直线落入名册判定，无提前出口——**当前源码的客户端在该包型上必然迭代到块 1 并留下日志**。唯一全程无声的路径是迭代 1 读到的块类型字节为 0：type 0 走 find-or-create `sub_4FF040`（`0x41B884`，无观测点）且分派 `0x41B8E7` 直跳设置段（绕过接管点 `0x41B8F3`），读原语 `sub_8F5120`/`sub_8F5140` 无边界防护，越界后继续读缓冲区后续字节。该形态只在**游标错位**（服务端实际写出的块 0 字节数与读取端不一致，或计数含扩展块而未追加块体）时出现，而当前仓库源码的服务端构造器经逐字节核对（`CreatePartyInfo`：u16 计数、设置块恰好 5 字节、type 4 块 7+L 置尾）不可能产生——据此判断 **01:25 轮部署的服务端二进制不是当前源码**（P-07 判据本要求「部署新服务端构建」，该轮疑似仅替换了客户端 DLL）。同轮修正插桩三处：① 观测点字节校验移入 `WritePartyCustomTitleHook` 写入之前（查找调用点、两处静默出口、循环头、块头共 5 处逐一对原始字节校验，不匹配记 `install skipped: NOTI 9 observation bytes do not match` 并整体跳过）——原安装函数在写入**之后**才对原始字节做比较，`install skipped: NOTI 9 abort bytes do not match` 为必现假报警；② 原循环头钩子的写入位于函数第二个 `return` 之后属死代码、从未安装（日志因此缺 `loop index=` 行），现并入返回链并补 `pushfd/popfd`；③ 新增块头观测钩子（接管 `0x41B815` 7 字节比较为 `jmp+nop,nop`，`pushfd/eax/ecx/edx` 保护，复刻 `cmp dword ptr ds:[B5DCB8h],0` 后回到 `0x41B81C` 未动的条件跳转），每次迭代记 `header idx=下标 id=队伍代号 type=块类型`——**type 0 块唯一无法绕过的观测点**，游标错位时将直接打出异常 id/type。移除安装函数内与写入函数重复的校验块。原生 Release 0 警告、CTest 通过、两钩子机器码逐字节复核（尾部 `add esp,12/pop edx/pop ecx/pop eax/popfd/cmp/push 0041B81Ch/ret` 与 `popfd/mov [ebp-622h],0/push 0041B7EEh/ret`）。**复测判据**：重部署当前服务端构建与新 `ijl15.dll` 后，改自定义名的双块包应出现 `header idx=1 id=1 type=4 → lookup id=1 block_type=4 → entered block_type=4 loop=1/2 → handled type4 len=… text_hex=…` 且名字生效；若 `header idx=1` 的 id/type 仍异常，即为服务端包体错位的直接证据（按 id/type 实际值定位错位字节数）。
- **复测定谳（2026-09-30 02:1x 轮，游标错位直接证据）**：块头观测点上线首轮即命中——02:10:04 改名双块包日志为 `loop index=1 count=2` 后 `header`（压序修正判读）**id=0、type=0**：客户端消费块 0 的 5 字节（`01 00 01 00 04`）后游标落在连续零字节上，迭代 1 循 type 0 盲区路径（find-or-create `sub_4FF040` + 分派直跳设置段）全程无声，名册段越界读 13 字节后计数用尽退出，扩展块从未分派。该字节排布与「设置块内嵌 `u32 长度+文本` 的旧构造」逐字节吻合（5 字节后恰为长度字段高 3 字节 `00 00 00`）；当前源码 `WritePartySettings` 只写 2 字节、游标应落在块 1 头 `01 00 04`（读出 id=1/type=4）——**运行中的服务端为旧构建实锤**。同轮修正块头钩子自身的 cdecl 压序错位（idx/type 打印互换，与 01:0x 轮入口钩子同类问题：首参数须最后压栈），重建后机器码复核（压序 `movzx byte [ebp-622h]/push → movzx word [ebp-620h]/push → mov [ebp-614h]/push → call` 唯一命中，尾部 `add esp,0Ch/pop edx/pop ecx/pop eax/popfd/cmp/push 0041B81Ch/ret` 不变）。待办**：重部署当前源码服务端构建 + 本轮 `ijl15.dll` 后按 P-07 判据复测，预期 `header idx=1 id=1 type=4 → lookup → entered → handled` 且名字生效。
- **观测增强（2026-09-30 02:2x 轮）**：用户确认已更换服务端后 02:15 轮线上形态依旧（迭代 1 仍 id=0/type=0），而当前源码唯一写入点（`CreatePartyInfo`）不可能产生该字节——为免继续盲猜部署状态，块头日志扩展 `next=` 字段：经全局游标 `src`（@0xBF29E0，机器码 `A1 E0 29 BF 00` 佐证）转储其后 8 个原始字节，下一轮日志直接呈现线上真实字节形态。部署侧排查方向改为监听进程本体（游戏 TCP 监听端口 2311：`netstat -ano | findstr :2311` 取 PID 后核对该 PID 映像路径与文件时间戳；本机存在多个服务端部署副本，实际监听者未必是被更换的那份）。构建 02:22:31，CTest 通过。
- **文档回写**：`packet-detail-party-60cn.md`「扩展块」节草案→已实现（含协议面、实现落点、验收标准与状态）、「队名显示与 titleKind 语义」结论更新、NOTI 9 节更正并登记 type 4；`client-function-register-60cn.md` §8 增扩展块接管点、dstr 读取原语与队伍名写入原语；`client-packet-enums-60cn.md` 组队表 CMD 12/NOTI 9 行备注；README「已知缺口」组队条目更新为"需打补丁客户端"。
### ADD-12 IJL15 功能开关改为 Config.ini 运行时配置

- **内容**：`dnf_patches.h` 的 11 个编译期功能开关（反调试探测、多开、启动不最小化、装扮期数合并、[etc] 堆叠自定义使用、弹窗公告去标题、外部串表、邮件归档刷新、点券门控总开关、自定义队名、日志明文）与 `output_paths.h` 的日志路径重定向开关、type-16 提示音屏蔽开关全部迁入 `ijl15_config` 运行时配置：`Config.ini`（客户端可执行文件旁）的 `[Features]` 节逐键开关（键名与 `Settings` 字段同名），`[CeraPurchaseGate]` 节逐门控开关（8 项，随总开关生效）；缺失的键回落到 `ijl15_config.cpp` 的默认值，默认值保持此前硬编码行为（既有客户端配置无需修改即行为不变）。
- **结构性改动**：`dnf_patches.cpp` 的补丁表从 `constexpr` 改为 `Apply()` 内配置加载后的 `BuildPatches()` 运行时构建，逐项范围校验自 `IsDnfImage` 移入新的 `ValidatePatchRanges`（表项、URL 指针、三个钩子点、自定义队名接管点），`IsPatchEnabled` 简化为读取表项运行时 `Enabled`（原先 overlay 地址特例由构建期配置吸收）；`ShouldBypassCeraPurchaseGate` 改为读配置（总开关与分项开关同时启用才生效），`CeraPurchaseGate` 枚举移入 `ijl15_config.h`。
- **兼容性**：NOTI 9 标准块字节、钩子机器码与安装点不变；服务端未改动，`protocol-golden` 与 SelfTests 不受影响。
- **验证（离线）**：原生 Release 编译 0 警告、CTest `ijl15_compatibility` 通过；测试工程补入 `ijl15_config.cpp` 以链接 `Get()`，开关断言改为读取配置默认值；配置模板 `Config.ini` 全键默认值化并改为中文说明。
- **文档回写**：README「启动器」Config.ini 示例（全键 + `[CeraPurchaseGate]`）、`packet-detail-party-60cn.md`「扩展块」节开关名标注 Config.ini 键。

---

## 2026-09-29

### FIX-04 改队伍名被拒 / 自定义队名不显示：CMD 12 容量字节越界与客户端队名本地重建

- **现象**：单人队伍改队名不生效。真机 2026-09-29 两轮复现：预设名改动成功，改用自定义名后队伍名不变成所输文本；其中 5 次改名请求被服务端直接拒绝，客户端弹『FAIL : 您不是房主,不能要求变更房间环境！』。
- **根因（两条独立原因）**：
  1. **自定义队名在 60A1 客户端没有下行显示通道**。NOTI 9 设置块读取端（0x41B8F9）只读 `u8 titleKind + u8 userMax`；`titleKind == 0` 时客户端把队伍名替换为本地串 0x272『无队伍名』（0x41B93E 取串 → 0x41B955 拷贝 → 0x41B998 写入队伍记录 +0xC 字符串对象），`titleKind != 0` 时置空串并由预设表渲染（`dword_B75A64[0..5]`，串表 0x1AA..0x1AF，装入点 0x993120..0x99317D）。显示判定 `sub_7D69E0`（同规则内联于 0x7D73AB..0x7D73BB）：`kind ? 预设[(kind-1)%6] : 记录名字串`。故服务端存下的 `Party.TitleBytes` 无任何客户端可显示，kind=0 一律显示『无队伍名』；此前 `packet-detail-party-60cn.md` 记的"1..5=预设名、0 号为空串"不准确，预设实为 **6** 条（titleKind 1..6）。
  2. **改名请求的 userMax 字节越界**，服务端按 2..4 校验后整体拒绝。创建窗口打开时容量取自 `*(队员记录 + 0xFC) + 0x3C`（`sub_7D6240` @0x7D6327；+0xFC 是队员记录指向所属队伍记录的反向指针，写入点 `sub_4FE690` @0x4FE715），对已存在队伍重开窗口时该处读到 **0x30 = 48**（容量单选只能产生 2..4，0x7D60E9 存 index+2；无队伍时窗口默认 4，0x7D635E）→ `IsSupportedCapacity(2..4)` 不通过 → `CreatePartyCommandError` code=8 → 客户端 case 0x410799 弹串 0x2CD 文案，同一请求内的预设名改动一并失效。
- **权威验证过程（IDA + 抓包）**：
  - 抓包（DAF-MCP 只读工具 `packet_watch` / `packet_get` / `tap_status`，单会话内对照；当前运行实例 `Logging.Enabled=false`，文件日志未落盘，字节证据取自 MCP 抓包通道）：21:27:58 建队 `31 00 04 04`（预设 4 / 上限 4）→ `[1]` + NOTI 9 建队块；21:28:48/52/56 预设改名 `3B 00 02 04` / `3C 00 06 04` / `3D 00 02 04` 均接受；21:28:59 自定义 `3F 00 00 08 000000 3434343434343434 04` → `[1]` + NOTI 9(kind=0)；21:29:04/07/12 自定义 `40/41/42 00 00 03 000000 313131/323232/333333 30` → `00 08 00 00`；21:37:40/45 预设 `4C 00 02 30` / `4D 00 05 30` → 同被拒；21:38:33 自定义 `5F 00 00 03 000000 313131 04` → `[1]`。同一文本 "111" 在两段会话中分别带 `30`（被拒）与 `04`（接受），据此判定末字节是容量而非队名内容。
  - IDA 静态：CMD 12 写包端 `0x7D5D20`（`CNRDPartyCreateWindow` vtable 0xA7E614，RTTI 0xA7E610）@0x7D5F86 逐段 push；预设判定 0x7D5E3A..0x7D5E85；本地校验串 0x461/0x462/0x463 与建队聊天行 0x464（0x7D5E9E..0x7D5F01、0x7D5FFE）；"无变化不发包" 0x7D5F50..0x7D5F6C；NOTI 9 读取端 0x41B7C3..0x41B9AF（块类型 0 走 find-or-create `sub_4FF040`，否则按代号 `sub_4FEF70`）；失败 case 0x410799。串表文案取自 `Plugin/dnf.str`（`id>文本` 文本表；`Client/DAFLegacy.Ijl15` 的 external_string_table 钩子 0x8AA5FA 即其装载入口）。
- **改动**：
  - `Game/Party/PartyState.cs`：新增 `PartyConstants.DefaultCapacity = 4`；`IsSupportedCapacity` 改为"修正判据"语义并注明客户端回读自存容量的成因；`Party.TitleKind` 注释更正为 1..6 预设 + kind 0 语义。
  - `Game/Party/PartyManager.cs`：`SetPartyInfo` 不再因 userMax 越界拒绝——建队取 `DefaultCapacity`，改设置保留队伍现有容量，修正值经 `PartyOpResult.RepairedUserMax` 回报；有效容量但小于当前人数仍拒绝（正常客户端已被串 0x463 前置拦截）。
  - `Network/PartyCoordinator.cs`：`SetPartyInfo` 对修正记一条信息日志（请求值与实际应用值），便于线上判读该客户端行为。
  - SelfTests `Game/Party/PartySmokeTests.cs`：`party` 套件新增"改设置越界保留原容量""建队越界回落默认值"两条断言，`RunCoordinatorFlow` 新增端到端断言（修正后设置块 blockType=1、kind=0、userMax=4 照常下发），原"rejects capacities the client cannot send"断言按新语义改写，末位退队 NOTI 9 计数 4→5。
- **验证（离线）**：全量重建 0 错误 0 告警；SelfTests 21 套件全绿（含 `party` 与 `protocol-golden` 71 协议字节等价守护）。
- **真机复测（2026-09-29 22:04–22:06，新构建已部署，抓包在同一会话内核对）**：
  - **容量修正通过**：本会话 8 次 CMD 12 全部收到成功应答 `[1]`，无一条 `00 08 00 00`；其中 seq 83（22:05:16）`11 00 00 04 000000 38353535 30` 携带越界容量字节 `0x30`，服务端按原容量（2）处理并照常下发设置块——修复前该请求会回错误码 8 并弹 0x2CD 提示框。服务端控制台应可见 `SET_PARTY_INFO from character … carried capacity 48 outside 2..4; applied 2`（Information 级；本次实例 `Logging.Enabled=false`，不落文件）。
  - **改名链路通过**：建队（seq 67 `0D 00 06 04`）、预设改名（seq 73/76 `…02/04 02`、seq 100/134 `…02 04`）、自定义名改名（seq 80 `…998`、83 `…8555`、87 `…0000`、95 `…7777777`、153 `…1111`）均被接受；退队（seq 91）后重建队伍换代号正常（seq 95 建队 partyId=2、seq 134 新角色建队 partyId=3）。
  - **队名显示符合本节结论（客户端侧限制，非服务端缺陷）**：自定义名（kind=0）在客户端显示为『无队伍名』，与「队名显示与 titleKind 语义」一致。自定义名要在客户端可见需客户端补丁，该方案后由 ADD-11 落地（见 `packet-detail-party-60cn.md`「扩展块」节）。
- **遗留项**：客户端开窗处为何读到 48 未定论（按登记偏移应为同一字段，实测读数不一致，该处记录/反向指针另有来源），见 `PENDING.md` P-14；自定义队名不可见一项后由 ADD-11 实施（服务端扩展块 + 客户端补丁），不再列为遗留项。
- **文档回写**：`packet-detail-party-60cn.md` CMD 12 节（titleKind 1..6、预设 6 条、应答 code 8 文案）与新增「队名显示与 titleKind 语义」「userMax 越界」两小节、流程概览补一条队名条目；`client-function-register-60cn.md` §6 CMD 12 行补注 + 新增 §8 建队窗口与队名显示（含相关串表 id 表）；`client-packet-enums-60cn.md` CMD 12/NOTI 9 行备注；README「已知缺口」组队条目补自定义队名限制。
### ADD-10 队伍聊天：CMD 17 type 2/11 按队伍名册投递

- **现象/根因**：队伍内发送聊天消息后本机无任何显示。抓包与日志互证——同一次会话里客户端先发 CMD 12 `SET_PARTY_INFO`（seq 81，建队成功），11 秒后发 CMD 17 `13 00 | 02 | 01 00 | 00 00 00 00 | 04 00 00 00 | "1111"`（seq 86，type=2），服务端记录 `Unsupported SEND_MESSAGE type 2` 后丢弃（`SendMessageHandler` 此前只认 type 3/1/7/18/13/14/15），客户端因无 NOTI 12 而无行可显示。
- **权威验证过程（IDA + 抓包）**：
  - **取值来源**：聊天输入提交 `sub_849E80` 由聊天窗频道下拉 `*(chatWindow+432)` 的选择项决定默认 type——选择项 1（队伍）在城镇取 **2**、副本场景取 **11**；选择项 2=公会取 6，4/5/6=喇叭 13/14/15，默认城镇 3。
  - **语义确认**：通用发送 `sub_8495B0` 对 type 2 用字符串表 282『组队聊天只能在队伍中才可使用』做前置拦截（type 6 对应用 283『公会聊天只能在加入公会后才可使用』）；显示侧 type 2/11 在 `sub_8431E0` 同色（0xFFEDD568）、在 `sub_845580` 同页签开关，两值属同一频道（城镇/副本两个场景取值）。
  - **下行形态**：接收端 NOTI 12 case 12（`0x41C994`）读 `mode u8 + uid u16 + dstr 文本` 后交 `sub_849280`，按 uid 查城镇用户表（`sub_4FDDB0`）取名字渲染聊天行并走气泡路径（`sub_846A10`），故服务端发 `type 2/11 + 发送者区域 uid + 文本`（A21 参考实现同形，仅少其 serverGroup 字节）。
  - **发送者必须回显**：`sub_8495B0` 的本地自渲染块整体位于其 type 1/7（悄悄话）分支内，其余类型发送者看不到自己的行；type 2/3 的下行副本含发送者本人是显示的必要条件。
- **改动**：
  - `Network/PartyCoordinator.cs` 新增 `SendToParty(sessionId, packet)`：按队伍名册投递（含发送者本人），返回收件会话数，发送者不在队伍时返回 0；
  - `Network/Handlers/Chat/SendMessageHandler.cs` 新增 type 2/11 分支：`ChatPacketBuilders.CreateMessageNotification(type, 发送者 uid, 文本)` 投递全体队员，type 原样透传（与喇叭一致）；无队伍时告警丢弃（正常客户端已被串 282 拦截，到不了这里）；
  - SelfTests：`party` 套件新增名册投递断言（两名队员各收一份、载荷 uid=队长 uid、队外投递收件 0）；`program-inline` 新增实测报文解析断言（`1300 0201 0000 0000 04000000 31313131`）。
- **验证**：全量重建 0 错误 0 告警；SelfTests 21 套件全绿。真机复测见 `PENDING.md` P-06。
- **遗留项**：跨区域/跨城镇队友聊天行只显示文本、无"名字 :"前缀（补齐需 NOTI 2 mode 3 快照），见 `PENDING.md` P-12；公会聊天（type 6）与其余场景频道（5/9/10/8）未支持，见 P-13；队伍内 P2P/城镇用户 uid 唯一性沿用 `LocalUserId = CharacterNo & 0xFFFF`（不同账号同位角色同号）可能错位，见 P-20。
- **文档回写**：`packet-detail-chat-60cn.md` CMD 17 表 msgType 行与新增「type 2 / type 11 队伍聊天」小节；`client-function-register-60cn.md` 新增 §7 聊天函数与字符串表登记；`client-packet-enums-60cn.md` 聊天表 17 行；README「已知缺口」组队条目补队伍聊天。
### ADD-09 新增 CMD 66 点券刷新查询（点券商城「刷新点券」按钮）

- **现象**：点券商城界面点「刷新点券」后余额不更新。抓包（会话 `1b34f634`）显示客户端以约 150ms 间隔连发 5～7 条 `CMD 66`(`body` 仅 seq、无载荷)即放弃，服务端每号一条 `No channel handler for packet type 1, protocol 66` 警告，全程无下行。
- **权威验证过程（IDA + 抓包互证）**：
  - 客户端发送器 `sub_85E1E0`：`BeginPacket(66)`(sub_8B2650) → `EndPacket`(sub_8B24C0)，无载荷写入；唯一调用点 `sub_864E10`(0x864F88，点券商城按钮处理)，与抓包 body 恒 2B 吻合。
  - 客户端应答侧：NOTI 53 case 位于 `0x4255BA`，按 `[u8 flag][u32 cera]` 读取(sub_8F5120 读 u8、sub_8F5160 读 u32)，写入全局 `dword_B75A44`；`flag == 0` 时另走 `sub_799EF0(dword_B5DCE8, 0x23, …, sub_8AA5A0(0x297))` 提示行。
  - 该布局与服务端既有 `ShopPacketBuilders.CreateCeraUpdate(bool success, uint value)` 完全一致（`success`→flag），即与余额变更推送同报文；抓包实例：`01 C4 A8 01 00`(flag=1、108,740)。
  - 客户端的 NOTI 66 位已被 `ENUM_NOTIPACKET_CERA_SPECIALITEM`（服务端用作会员服务通知）占用，故本查询的应答只能走 NOTI 53。
- **改动**：新增 `Network/Handlers/Shop/CeraQueryHandler.cs`（`economy.shop`）：请求体仅 seq，选角后推 NOTI 53 回推 `Currency.Cash`，未选角静默忽略（客户端只在游戏内商城界面发送）。`EntranceService` 注册一行；注册表 74 → 75 项。
- **验证**：Server / SelfTests / Mcp 全量重建 0 错误 0 告警；SelfTests 21 套件全绿，`protocol-golden` 新增三条断言（未选角不回包、选角后回包、回包字节与 `CreateCeraUpdate(true, 108740)` 逐字节相同）。真机复测见 `PENDING.md` P-05。
- **文档回写**：`client-packet-enums-60cn.md`（CMD 66 行 ❌→✅、未实现表同步）；`packet-detail-shop-currency-60cn.md`（新增 CMD 66 小节）；`client-function-register-60cn.md` §1 登记 `sub_85E1E0`/调用点、§5 登记 NOTI 53 case 区间；07 §5 组 10 行与 §11.4 计数。
- **关联**：ANTIBOT（CMD 161 → NOTI 152）**不随本次实现**：其对应 id `0x98` 在客户端类型分派之前被传输层拦截（收包入口 `sub_40D6D0` → `sub_8C7300`，`0x98` 交 `dword_BF1634+4` 对象的 `vtable+8`、`0x99` 作压缩信封），既无游戏层消费者，也无真服样本可定字节，按 D1「不猜测应答字节」维持未实现（登记见 `client-packet-enums-60cn.md` NOTI 152/153 行；见 `PENDING.md` P-13）。
### ADD-08 统一文件日志改为开关控制（默认关闭）

- **背景**：`Logging` 配置节此前无开关，`BoundedQueueFileLoggerProvider` 在组合根无条件注册，任何部署都会在 `logs/server.log` 落盘（含中文诊断与协议交互摘要）；默认落盘会让运行目录持续增长，并留下可被误收集的交互记录。
- **改动**：`LoggingOptions.Enabled` 默认 `true` → `false`（`Infrastructure/ServerOptions.cs`，类头注明关闭语义）；`Program.cs` 改为按开关条件注册——关闭时不注册提供器、不创建日志目录、不建队列与后台消费任务。`McpOptions` 的类头注释此前误挂在 `LoggingOptions` 上，随本轮移回原属类。
- **验证**：Server / SelfTests / Mcp 三项目全量重建 0 错误 0 告警；SelfTests 21 套件全绿，`file-logger` 新增两条默认值断言（`LoggingOptions.Enabled` 与 `PacketCaptureOptions.Enabled` 默认均为 `false`）。真机侧未做：默认关闭即不产生文件，开启路径由既有 `file-logger` 套件守护。
- **关联**：README「常用配置」补 `Logging.Enabled` / `Logging.Directory` / `Logging.FileName` / `Logging.MaximumFileBytes` 四行。
### FIX-03 选角后无法进入城镇：装备归位循环 `continue` 被误写为 `return`（迁移期控制流缺陷）

- **症状**：真机选择角色后客户端停在载入画面，无法进入城镇；同一会话内服务端无任何下行。
- **根因**：R1 把 CMD 4 分支自 `Network/EntranceService.cs` 迁入 `SelectCharacterHandler` 时，持久化装备归位循环内 4 处"本件处理完毕、继续下一件"被写成 `return;`（旧实现为 `continue;`，见提交 `95f94db^` 的 `Server/DAFLegacy.Server/Network/EntranceService.cs` L2783/L2790/L2800/L2819）。处理到第一件装备即整条 handler 提前返回，角色详情包与城镇初始化包族（`CharacterPacketBuilders.CreateTownInitializationPackets`）全部未发出——故既无应答也无异常日志。
- **权威验证过程**：
  - 抓包（DAF-MCP 只读工具 `packet_watch` / `packet_get` / `tap_status`）：会话 `389cf9c4` 自入口握手（2311：11/12、5/6、9/10、1/3）到频道面登录（`CMD 1 LOGIN` → `NOTI 1 CHANNELINFO`）、`CMD 8 GET_USERINFO` → `NOTI 2 USERINFO`、`CMD 2 SET_UDP_IP_PORT` → 回包皆正常；断点在 `RX CMD 4 SELECT_CHARACTER`（seq 26，body `05 00 00`，crc 有效）之后——全程再无 TX，客户端只维持 ANTIBOT（`CMD 161`，每分钟一条）与 UDP NAT 保活；服务端日志同一时段仅登记该入包，无错误行。
  - 静态（迁移前旧实现为权威基线）：`return` 落在 `foreach` 体内属可疑形态——全仓扫描"循环内 `return;`"命中 11 处，逐段与旧实现做规范化对拍（剥离机械改名后逐行比对）确认为 `continue;` 误写。反向论证：`continue;` 只能出现在循环内，故该类改写必表现为"循环内 `return;`"；全仓修毕后仅余 2 处且均合法——`Client/DAFLegacy.Launcher/Program.cs`（端口就绪探测的成功返回）与 `Server/DAFLegacy.Server/Network/GameplayDatagramService.cs` L463（畸形数据报丢弃后函数已无后续语句，`return` 与 `break` 等价，属初始导入基线）。
  - 本缺陷只涉及控制流被机械改写，字节布局未变，故不涉 IDA 逐 case 复核。
- **改动**：
  - `Network/Handlers/Characters/SelectCharacterHandler.cs`（L251/L258/L268/L287）：`return;` → `continue;`，恢复旧实现语义。
  - `Network/Handlers/Shop/CeraShopBuyHandler.cs`（L166/L183/L201/L306/L333/L355/L370）：同源误写 7 处 `return;` → `continue;`（对齐旧实现 L8261/L8278/L8296/L8401/L8428/L8450/L8465）。修复前多商品点券购买只规划首个商品便整体退出：应答不发、物品不入账。单商品购买的"主背包落位"分支已于 2026-09-29 真机复测证实修复生效（见下「真机复测」）；其余分支的覆盖情况见 `PENDING.md` P-02。
  - 两文件与旧实现整段规范化对拍复核：除 `Safe` 后缀、事件入队、`cancellationToken` 等既定机械差异外，无其它语义偏差。
- **验证（离线）**：全量重建 0 错误 0 告警；SelfTests 21 套件全绿（含 `protocol-golden` 71 协议字节等价守护）。
- **真机复测（2026-09-29，修复构建 md5 `8c524475b66cb2af8adc3d9b9528cb08`，两次独立登录）**：
  - **选角 → 进城：通过**。`RX CMD 4`（seq 26 body `05 00 00` / seq 109）之后服务端立即回 `CMD 4` 应答（90B / 85B）并连发城镇初始化包族——`NOTI 2 USERINFO`×2、`NOTI 5 DUNGEON_PERMISSION`、`NOTI 13 ITEM_LIST`×4、`NOTI 105 CREATURE_ITEM_LIST`、`NOTI 19 SKILLINFO`、`NOTI 21 ACCEPTABLE_QUEST_LIST`、`NOTI 48 PVP_RECORD`、`NOTI 97 MAILBOX_MAIL_LIST`×2；客户端随后上报 `CMD 37 SET_USER_POSITION`，服务端回 `NOTI 24 AREA_USERS`、`120 EVENT_INFO`、`36 FATIGUE`、`103 CREATURE_STATE`、`2 USERINFO`、`4 STAMINA`、`136 ENTER_GAMEWORLD_COMPLETE`（seq 51 / 132），角色进入城镇。
  - 进城后实际交互全部正常：移动（`CMD 37` 周期上报）、聊天与喇叭（`CMD 17` → `NOTI 12 MESSAGE` / `NOTI 130 MEGAPHONE_MSG`，并扣掉喇叭道具）、组队（`CMD 10/11 REQUEST_PEER`/`RESPONSE_PEER` → `NOTI 8/9/11/26`，两会话同收 `PARTY_INFO`）、点券购买、`CMD 18 DELETE_ITEM` 删除物品。
  - **点券商城（同批 7 处）：通过（1/7 分支）**。购买商品 100000（`item 1`×10，点券价 880）：`CMD 67` 应答 34B + 主背包全量刷新（`item 1` 计数 179→189，金币 2144161 与胜点 200 未动）+ `NOTI 53 CERA` 余额 490（1370−880），命中"主背包落位"分支。其余 6 分支（仓库升级 / 会员契约 / 直发货币 / 礼包容器 / 装扮落位 / 精灵落位）未覆盖，见 `PENDING.md` P-02。
  - 抓包全环（seq 1–400）无 CRC 失效、无线长截断；仅 `NOTI 10 WALKOUT`（14183B 元数据）正文按 4KB 上限截断，属抓包工具限制。
  - 首次选角到应答间隔 421ms，系 `DungeonCatalog` 首次懒加载（日志 `Loaded 43 DAFLegacy dungeons and 1025 map definitions`）所致的一次性开销；第二次登录同路径为 4ms，不属阻塞缺陷。
  - 两项既有现象（非本次改动引入，登记供后续判读）：①入口会话在切频道后由客户端 RST 关闭，服务端按 WRN 记录 `Entrance session … ended with an error`（IOException 10054），自初始导入 `513d0f6` 起即如此，属正常切换的日志噪声；②`CMD 161 ANTIBOT` 每 60s 上报一次、服务端不应答（`client-packet-enums-60cn.md` 登记 `NOTI 152` 未实现），连续在线 5 分钟以上未掉线。
- **真机复测（三轮，同日 20:30–20:48，会话 `1b34f634`，抓包环 seq 1–769 与服务端日志同批核对）**：本轮补覆盖 §7.4 清单，结论如下。
  - **通过**：城镇与系统面——`CMD 38` 切区与 `CMD 37` 位移的整组下行（USER_AREA/AREA_USERS/EVENT_INFO/FATIGUE/CREATURE_STATE/NOTI 33/USERINFO/ENTER_GAMEWORLD_COMPLETE）、`CMD 82` 访客名单、`CMD 130` 黑名单、`CMD 132`→`NOTI 131` 精灵脚本；聊天与喇叭——`CMD 17` type 14/13 各一条 → `NOTI 14` 喇叭道具扣减 + `NOTI 130` 横幅（发言者 flag=频道、target=自身 uid）；背包——`CMD 19` 移动 15 次（全为主背包内重排，逐次应答 + `NOTI 13`）、`CMD 20` 整理 6 次、`CMD 18` 删除、`CMD 47` 使用 2 次、`CMD 51` 耐久 2 次、`CMD 46` 拾取 7 次（→`NOTI 39`+`NOTI 13`）；商店——卖出 8 笔（单价 240/240/120/1800/2400/2100/2400/2380，应答 `gold` 字段与 `ItemCatalog` 计费逐笔一致、累计 +16,880）、买入 2 笔（商品 3037 单价 100、落槽 78，应答四钱包字段与 `NOTI 53 CERA` 交叉一致）；体力——`CMD 9` 虚弱恢复（应答 gold=2,080,484 即扣费后余额，费额取自 `StaminaRecoveryCatalog`，续发 `NOTI 4` 满体力）；技能——`CMD 30` 互换 2 次（应答回显 + 日志 `Changed skill slot from 1 to 0`）；副本——两次进图，其中一次完整通关（`15→16→28/29→40`、`160/42→38`、`41`、`46→39`、`48→29`、`127→148`、`49→34/102/35`、`72→73→74→75` 回城，途中 `MOVE_MAP` 跨图 4 次、拾取 7 次、翻牌 2 次）、一次经 `CMD 45` 放弃回城；邮件——`CMD 99` 打开 3 次（应答 + `NOTI 97` 列表 + `NOTI 99` 提醒）、`CMD 144` 改状态/删除 6 次（应答回显 mailId+state）。
  - 全环无 CRC 失效、无线长截断；除有意静默者（`CMD 66` / `CMD 160` / `CMD 37` / `CMD 161`）外无缺失应答，业务应答均为 result=01。`NOTI 33(0x64)` 出现在进城路径属既定设计（`ChannelSessionSenders.SendSilentTownStaminaStateAsync` 的静默满体力分支）；`DieMonsterHandler` 的 WRN（尾王先死、仍有存活怪）是等待 `BOSS_DIE_CHECK`/`ALLDIE_MONSTER` 级联的既定分支，随后 `CMD 127`/`CMD 148` 如期到达。
  - **未覆盖**：清单与逐项判据见 `PENDING.md` P-01、P-02（与 `docs/design/07-protocol-layer-refactor.md` §11.4 同源）。
  - **旁证**：`CMD 66`（CERA 查询，19 次）与 `CMD 160`（SDC_DAMAGE_CHECK，24 次）走 "No channel handler" 警告；经迁移前快照（`95f94db^`）比对，旧链路对这两号同样无分支、警告文案相同（`GameProtocolEngine.Handle` 返回 null），不属回归（如需降噪，可另立静默消费注册项）。ADD-07 的后台页组合动作在真实服务端留下三次「先发放点券、后创建邮件」的日志（时序差 7–10 ms，金额 10000/10/100000），在线角色收到 `NOTI 53 CERA` 且余额吻合（8,740+100,000=108,740），即"目标角色在线时收到点券刷新"一项获真机证据；页面侧交互仍待用户自测，见 `PENDING.md` P-08。
- **遗留项**：迁移期逐字搬迁的其余片段是否还有同类失真，随 `docs/design/07-protocol-layer-refactor.md` §11.4 的真机全流程冒烟逐协议复核；见 `PENDING.md` P-01。
### ADD-07 后台邮件表单附带点券发放（账号级即时到账，非邮件附件）

- **背景**：邮件协议三个环节——发送（CMD 97）、邮件记录（NOTI 97）与领取提取（CMD 98）——均无点券字段（字段级依据见 `docs/protocol/packet-detail-mailbox-60cn.md`），邮件本身无法携带点券。故"在邮件中发送点券"落在既有账号级点券接口上，由后台页面组合两次调用实现。
- **功能（先发放、后发信）**：`/admin/index.html` 邮件视图新增「点券（发放到账号）」字段，缺省 0。填写大于 0 时，页面先以收件角色的账号名调用 `POST /api/client-pay/cera` 发放点券，再调用 `POST /api/admin/characters/{characterId}/mail` 落邮件。点券为账号级数据、即时到账，与邮件是否被领取无关：收件人删除邮件或邮件过期都不回收已发放部分。
- **校验放宽（行为变更）**：`JsonGameStore.GrantCeraToAccountAsync` 的校验由 `amount <= 0 || amount % 100 != 0` 改为 `amount <= 0`，错误文案由「充值点数必须是正整数，且为 100 的倍数。」改为「充值点数必须是正整数。」，即任意正整数均可发放。放宽依据：100 倍数约束源自早期实现约定，客户端与协议侧均无此要求——点券在 `NOTI 53 CERA` 中为 u32 原值直传，发放值不参与任何取整或对齐运算；后台小额补差（如 137）属常见诉求。
- **非原子语义**：两次调用不构成事务。点券已发放而邮件发送失败时，页面提示「点券已发放 N，邮件发送失败：…」，点券保留、邮件不落库，需人工决定是否重发；附件解析失败（未选中有效物品）在发放之前抛出，不会出现"邮件失败但点券已发"的倒置次序。规则见 `ADMIN_API.md` §5.2.7，§6.1 的 `amount` 规则行同步放宽。
- **改动**：`Server/DAFLegacy.Server/Persistence/JsonGameStore.cs`（校验与错误文案）；`wwwroot/admin/index.html`（点券字段与表头说明）；`wwwroot/admin/admin.js`（邮件提交改为先发放后发送、失败文案区分、收件角色缺失前置校验）；`ADMIN_API.md`（§6.1 规则行、新增 §5.2.7）；`Server/DAFLegacy.Server.SelfTests/Program.cs`（新增两条断言）。
- **验证**：
  - 服务端侧：全量重建 0 错误 0 告警；SelfTests 21 套件全绿，新增断言覆盖非 100 倍数发放（137 后账号余额精确为 1_765_769）与 0／负数拒绝；`admin.js` 通过 `node --check` 语法校验。新增发放改动了账号余额，故其后以旧快照为基准的断言需重取角色余额，已在测试内重取（`reloadedPremiumCharacter` 刷新一处）。
  - 页面侧（桩后端实测，2026-09-29；仅自建桩接口托管仓库内静态页，未接触服务端进程、部署目录与游戏数据）：①点券 137、无附件提交后，先 `POST /api/client-pay/cera {account:"test",amount:137}`、再邮件接口，提示「邮件已发送给 验证角色，并发放 137 点券」；②桩接口返回错误时提示「点券已发放 50，邮件发送失败：<服务端错误文本>」，点券调用仍在邮件调用之前；③点券为 0 时不发起点券调用，提示回落为「邮件已发送给 验证角色」；④负数点券被 `min="0"` 的原生约束校验拦住，表单不提交、不发起任何请求（页面侧 `Number.isInteger` 守卫是绕过约束校验时的兜底）。
  - **待用户手动验证（真实服务端）**：判据见 `PENDING.md` P-08。
- **遗留项**：页面级组合动作的原子性需求与真实服务端页面验证，见 `PENDING.md` P-08；邮件协议侧无点券通道一事不因本条目改变。
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
- **验证**：SelfTests 全量通过（含新增 `ProtocolGoldenSmokeTests`：EXIT 全字节、CHECK_CONNECTION 同 seed 全载荷对比、门槛拒绝路径、批量 FIFO、计账单点）；样本库 `tools/packet_bytes.json`（66 CMD + 49 NOTI）由 SelfTests 直接消费。真机冒烟复测见 `PENDING.md` P-01（登录→选角→建/删角→返回选角）。
- **续报（R1 收尾 + R2）**：SELECT_CHARACTER（CMD 4）已随 R1 收尾迁入 `SelectCharacterHandler`（初始化包家族迁入 `CharacterPacketBuilders`，循 §6·R1 InitPacketBuilderRegistry 方向）；R2 组 5-8 共 14 协议挂载新链路（CHAT 3／MAILBOX 4／QUEST 4／SKILL 3），注册组 `social.chat`／`social.mailbox`／`progression.quest`／`progression.skill`；跨会话推送（邮件／会员／点券／喇叭／弹窗／虚弱／副本权限）统一经目标会话出站泵入队，fire-and-forget 直写他连流的 P2 裂缝在已迁移协议面上机制化封口。新增 `ConnectionContext.ReadAdjacentFrame` 泵层能力承载 CHANGE_SKILLSLOT 的紧邻帧预读（解密与计账留在传输层）。EntranceService.cs 14,832 → 10,791 行。
- **续报（R3）**：组 9-11 共 19 协议挂载新链路（INVENTORY 5／SHOP 6／CRAFT 8），注册组 `economy.inventory`／`economy.shop`／`economy.craft`；新增 InventoryParsers 与 ShopParsers（含 ConsumeItemsRequestItem／CeraShopPurchaseRequestItem 请求记录），库存移动发送器、精灵移动规划簇与库存空间校验族归口 `ChannelSessionSenders`。EntranceService.cs 缩至 5,128 行；累计挂载 43/71 协议；组 12-15（精灵／副本／城镇／组队）与 R6 清退仍待推进。目录形态随后对齐参考项目三层镜像：`<组>Parsers` 由 `Network/Handlers/<组>/` 提升至平级 `Network/Parsers/<组>/`（共享 `ClientBodyParsers` 置 Parsers 根）。　另按参考项目 `Network/{Core,Protocol}` 两极分治：`Protocol/` 整体迁入 `Network/`，非核心功能（加解密、名称枚举、日志明文、包基元、PVF 读取、`IPacketTapSink`）统一入 `Network/Core`，协议核心（分帧 + 游戏/入口引擎 + UDP 数据报协议）留 `Network/Protocol`；命名空间不变，字节冻结不受影响。　另将 `Hosting/`（`ServerOptions`/`PathResolver`/`AdminApiModels`）并入 `Infrastructure/`，`CharacterSessionRegistry` 移入 `Game/Session/`（对标参考项目 `SessionDirectory`），空目录移除；`Properties/`（SDK/IDE 约定）与 `Services/`（待 R6 事件入队改造）原地不动。　日志面自参考项目迁移 `FileLogger`/`PacketFileLogger` 功能并统一为 Microsoft.Extensions.Logging 管线：新增有界队列文件日志器（`logs/server.log`，DI 注册、排空退出）与逐包十六进制捕获 sink（`IPacketTapSink`，默认关闭、64MB 轮转），Tap 漏斗扩为多端口并集（MCP hub 与包捕获并存）；十六进制排版工具拆为 `Network/Core/HexDump`，日志层不含加解密逻辑；新增 `file-logger` 自测套件。　目录再次按职责归位：`RuntimeState` 与随机源三文件移入 `Infrastructure/`，`SerializedWriteStream` 移入 `Network/Core/`，`Persistence/` 仅保留 `JsonGameStore`；`Services/`（待 R6 事件入队）与 `Properties/`（SDK/IDE 约定）不动。　协议层迁移收官：R4 组 12/14/15 十二协议与 R5 组 4/13 十九协议全部挂载（累计 71/71 协议，注册表 74 项含组队族 5 壳），`EntranceService.cs` 收缩至 1,918 行；R6 清退完成死分支清除与 5 个 `Interlocked` 标志事件化，定时器收编（4 处 WhenAny 归并单调度点，实现位置与方案见 07 §11.4）与真机全流程冒烟（71 协议）两项见 `PENDING.md` P-10、P-01。　收尾整理：纯规划器（库存空间校验族、精灵移动规划簇）自 senders 归位 `Game/Inventory/`；`MoveItemSpaceHandler` 按子流程拆为四个 partial（742/155/195/134 行）。　另按参考项目三层形态完成 R7：`GameProtocolEngine.cs` 3,754 → 447 行（常量表 + 兜底），14 个分组构建器落 `Network/Builders/<组>/`，共享报文模型独立为 `PacketModels.cs`、写出器为 `PacketBodyWriter.cs`；993 处调用点机械重命名，全量重建 0 错误 0 告警、SelfTests 21 套件全绿。　协议号枚举按参考项目形态落地：新增 `CmdPacketType`/`NotiPacketType` 方向分表（163/154 项，源 `tools/packet_id_map.json`），引擎 130 个协议号常量迁入并删除，`PacketNames` 反查改为枚举单源。参考项目 `Network/Core` 全量研究结论（采纳/已覆盖/评估登记三分类）见 07 §11；其 TURN 式每有序对 UDP 中继经评估不迁移（2026-09-29 IDA 复核已定论对端按帧内槽位字节归属，per-ordered-pair 无必要；多人副本数据面的前置缺口见 `PENDING.md` P-12），评估记录在 `docs/protocol/packet-detail-party-60cn.md`。
- **续报（诊断工具清理）**：`Network/Core/PacketNames.cs` 删除——报文名反查以 `PacketTypeNames.TryGetName` 并入 `Network/Core/PacketTypes.cs`（枚举成员名即客户端包名去前缀形态，只按客户端习惯补 `ENUM_CMDPACKET_` / `ENUM_NOTIPACKET_` 前缀；包名表地址 .rdata 0xB54170/0xB54430 与对照文档指引随迁该文件头注释，证据链不断），六个调用点（`RuntimeState` ×2、`PacketFileLogSink`、MCP `PacketTapHub`/`PacketTapTools`/`PacketNamesResource`）改接，`packet_names_search` 工具与 `packet-names://cmd|noti` 资源行为不变。字段级明文解码器 `Network/Core/PacketPlainText.cs` 保留但断开接线：`EntranceService.LogPacket` 两个重载去掉 `text=` 与解码调用（控制台追踪回到"原始字节 + crc"）、`PacketTapHub` 停止富化 `PlainText`、`PacketTapEvent.PlainText` 字段与 MCP `protocol_decode` 工具（实现整体依赖该解码器，ADD-05 工具清单中该项随之作废）下线；解码实现本体原样保留（类头注明未接入），备后续离线工具或重新接入。`TapDecodeMode` 口径分类保留，作 body 来源标识与解码器重接接缝。Server/Mcp/SelfTests 全量重建 0 错误 0 告警、SelfTests 21 套件全绿（`protocol-golden`/`packet-tap-funnel`/`file-logger`/`mcp-module-loader` 覆盖本次路径）；真机冒烟见 `PENDING.md` P-01。另记同批处置：`tools/gen_packet_names.py` 删除（其唯一产物即本次删除的字符串表，脚本内写死机器绝对路径，输出目录 `Server/DAFLegacy.Server/Protocol/` 在 R7 目录重组后已不存在——保留即等于留一个会复活已删文件的入口）；`tools/PacketDumper` 保留待重写，它是 `tools/packet_bytes.json` 黄金样本库的生成器（SelfTests 经 csproj 直接消费），价值不可弃，但不在解决方案内、仍调用 R7 前的 `GameProtocolEngine.CreateXxx` 与 `PacketPlainText`，当前不能编译；另 `PacketTypes.cs` 的重生成脚本尚未随枚举落地（当前为一次性生成）。两项见 `PENDING.md` P-11。
- **参考项目 `Network/Core` 复核（第二轮）**：20 个文件逐项复核，三分类结论不变（见 07 §11.5）；`PartyUdpRelay` 的迁移判定收窄为单条 IDA 问询——客户端 P2P 收包分派按数据报内的队伍槽位字节归属，还是按源 ip:port 归属（本项目 MT-UDP 帧头第 7 字节即发送者槽位，若为前者则单口中继已足够、无需 per-ordered-pair；该问询已于 2026-09-29 由 IDA 复核定论为按帧内槽位字节归属，见 `docs/protocol/packet-detail-party-60cn.md`「第二轮复核补记」与 `PENDING.md` P-19）；另发现本项目数据面 `GameplayDatagramService` 的同 IP 多会话端点归属歧义（`ObserveEndpoint` 按 TCP 源 IP 整体学习，同机双开/同一 NAT 出口下互相覆盖端点映射），登记为候选缺陷：修复方向为按会话认领端点，未真机复现前不改（见 `PENDING.md` P-19）。两项均见 `docs/protocol/packet-detail-party-60cn.md`「第二轮复核补记」，遗留项同步登记 07 §11.4。
- **`Services/` 并入 `Infrastructure/`**：两个周期后台服务（`FatigueResetService` 每日 06:00 疲劳重置、`WeaknessRecoveryService` 每分钟推进虚弱恢复）移入 `Infrastructure/`，空目录 `Services/` 移除；命名空间不变、调用点零改动，全量重建 0 错误 0 告警、SelfTests 21 套件全绿。既定前置"R6 事件入队改造"经核实已完成：两者的通知回调在 `SelectCharacterHandler` 领租处即写目标会话 `session.Events`（`ConnectionEventKind.FatigueReset` / `WeaknessRecovered`），服务侧只经 `CharacterSessionRegistry` 转交回调，不再直写他人连接流。项目根目录至此只余 `Program.cs` 与 SDK 约定的 `Properties/`。
- **组队报文模型归位 + `Builders/CharacterPacket/` 目录**：五个组队 TCP 报文模型（`PartyWireSlot`、`PartyInfoWireBlock`、`PartySettingsRequest`、`PeerRequestFrame`、`PeerResponseFrame`）自 `Network/Protocol/GameDatagramProtocol.cs` 逐字移入 `Network/Protocol/PartyWireModels.cs`——它们属频道命令面（CMD 10-14 请求体、NOTI 7-10 块），不含 UDP 数据报结构，与数据报协议同文件属误置；初始化包家族 `CharacterPacketBuilders`（主文件 + 8 个功能块 partial，718 行）自 `Network/Builders/` 根移入 `Network/Builders/CharacterPacket/`，与同名的组构建器 `CharactersPacketBuilders`（Characters 组，R7 产物，527 行静态类）区分。Builders 目录规则随之明确：根只放共享基建（`PacketBodyWriter.cs`），子目录放组构建器与流程家族。命名空间不变、调用点零改动；全量重建 0 错误 0 告警、SelfTests 21 套件全绿。
- **`Network/Protocol/` 两个"引擎"名语义化 + 文档回写**：`GameProtocolEngine` 拆为 `GameProtocolConstants`（帧类型标记与玩法域定值）与 `FallbackCommandReplies`（注册表未命中的兜底应答表），`EntranceProtocolEngine` 改为 `EntranceHandshakeReplies`（含 `EntranceSessionState` / `EntranceContent`）；44 个文件的 360 处引用改写（插入与删除行数相等，纯改名）+ `GameProtocolEngine` 一拆二（101 行 → `GameProtocolConstants` 41 行 + `FallbackCommandReplies` 79 行）+ `EntranceProtocolEngine` 改名；命名空间与字节行为不变。文档回写：07 §11.5 新增旧名→新名对照表、§4.8.3 新增规则 6（Builders 子目录——根只放共享基建，组构建器入同名组目录，流程家族单独登记，当前为 `Builders/CharacterPacket/`）；现行引用同步（07 的 `Network/Protocol/` 文件清单与计划段示例、`packet-detail-*.md` 12 个分册的"字段表来源"行改指分组构建器、`docs/design/01/06/08`、`docs/sql/60A1_schema.sql`）。历史记录（CHANGELOG 日期条目、07 §11 早期各条、`docs/servers4a21/*` 的行号引证）保留旧名不改——按 §11.5 对照表翻译即可。
### ADD-05 新增 DAF-MCP 抓包网关（<http://127.0.0.1:12222/mcp>）

- **功能**：服务端内置标准 MCP 服务器（默认关闭），供智能体（ZCode / Claude Code 等）实时抓包分析协议——报文名 + 明文富化的收发包流、单包完整字节、离线解码、包号↔报文名检索。
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
  - `protocol_decode`：离线复用服务端字段级解码器解码提交的 bodyHex（该工具已于 ADD-06「诊断工具清理」中下线）；
  - `session_list`：列出当前网络会话，定位要盯的 sessionId；
  - `tap_status`：抓包通道状态自检（当前 seq、环形存档占用）。
- **典型用法**：`session_list` 找到目标会话 → `packet_names_search` 查包号定过滤 → `packet_watch` 续读跟踪；需要等特定回包时用 `packet_wait`，深挖字段用 `packet_get`。
- **安全**：默认仅回环监听；改为非回环等同主动暴露协议流与游戏数据。
- **关联**：`docs/design/09-mcp-packet-tap.md`（设计全档）；README「常用配置 → 服务端 → MCP」。

---

## 2026-09-28

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
- **待确认**：见 `PENDING.md` P-18（type 14 ↔ 物品 37 映射为推断、flag 字节语义、type 15 喇叭变体未处理）。
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
### ADD-03 新增 ANTIBOT 上报消费（CMD 161）

- **现象**：客户端登录时发一条 92 字节、此后每 60 秒一条 18 字节的 CMD 161；服务端无处理器，日志每分钟告警 `No channel handler for packet type 1, protocol 161`。
- **权威验证过程（IDA）**：
  - 发送器 `sub_8C74F0`：`BeginPacket(161)`（`sub_8B2650`）→ 写 4 字节长度前缀（`sub_8B2450(&len, 4)`）→ 写记录负载 → `EndPacket`（`sub_8B24C0`）。该函数经函数表 0x998C50 登记，周围为 `unknown owner MAC` 等反作弊检测字符串，确认属 ANTIBOT 模块。
  - 排除项：`sub_8B2880`/`sub_8B37D0` 等处的立即数 161 属客户端内嵌 SNMP 栈（SNMP 端口 161 巧合），与游戏协议无关。
  - 线上格式（与抓包逐字节吻合）：`body = seq u16 + u32 声明长度 + 记录`；记录以大端 u16 kind 开头。已观测两种形态：登录环境上报 kind 0x0003（86 字节），周期心跳 kind 0x000A（12 字节，含 4 字节校验值与大端 u16 计数器）。
  - **fire-and-forget 契约**：无应答时客户端心跳计数器持续递增（实测 4→23 跨越多次心跳），不重试、不断线、无惩罚路径；发送路径未见 pending 状态或超时登记。服务端不应答即正确行为，不猜测应答字节（NOTI 161 无真服样本，保持未实现，见 `PENDING.md` P-13）。
- **新增内容**：
  - `Protocol/GameProtocolEngine.cs`：常量 `AntibotCommand = 161`（60CN `ENUM_CMDPACKET_ANTIBOT`，见 [client-packet-enums-60cn.md](docs/protocol/client-packet-enums-60cn.md) CMD 161 行）。
  - `Game/Antibot/AntibotReportParser.cs`：`TryParse` 解析 seq、声明长度一致性校验、记录 kind。
  - `Network/EntranceService.cs`：未知协议兜底前新增显式分支，静默消费并以 Debug 级日志记录（seq/kind/声明长度）。
  - `Server/DAFLegacy.Server.SelfTests/Game/Antibot/AntibotReportSmokeTests.cs`：以真实抓包帧断言解析与拒绝路径（短包、声明长度造假）。
- **验证**：冒烟全绿；真客户端复测见 `PENDING.md` P-04（登录挂机 ≥2 分钟，无 protocol 161 警告，游戏行为无变化）。
- **关联**：[client-packet-enums-60cn.md](docs/protocol/client-packet-enums-60cn.md) CMD 161 行（`ENUM_CMDPACKET_ANTIBOT`；其同名配对 NOTI 152 `ENUM_NOTIPACKET_ANTIBOT` 无真服样本，保持未实现，见 `PENDING.md` P-13）。
### FIX-01 返回选角后公告弹窗需要确认两次

- **现象**：首次登录选角，选角界面的活动公告弹窗确认 1 次；进入城镇房间后再返回选角，同一弹窗需要确认 2 次。
- **根因**：客户端返回选角（CMD 7 `ENUM_CMDPACKET_RETURN_SELECT_CHARACTER`）后会自行发送 GET_USERINFO（CMD 8 `ENUM_CMDPACKET_GET_USERINFO`）拉取角色列表；服务端在 CMD 7 应答后又主动补发一份 NOTI 2（mode=2）列表，同一帧列表到达两次。客户端每次收到列表都会重建选角界面并重放活动公告。
- **权威验证过程**：
  - 抓包（两份独立日志）均显示 `CMD 7 → ack → NOTI 2 mode=2（补发）→ CMD 8 → NOTI 2 mode=2（应答）`，两帧字节完全一致；首次登录路径只有 CMD 8 应答一帧。
  - 实测定量：列表到达次数与弹窗确认次数 1:1 对应（1 帧 → 1 次确认；2 帧 → 2 次确认）。
  - IDA 复核确认 CMD 7/8 与 NOTI 2 的枚举语义与 [client-packet-enums-60cn.md](docs/protocol/client-packet-enums-60cn.md) 一致（CMD 7 行备注：返回选角界面→刷新列表，配对 NOTI 2）。
- **修复**：删除 CMD 7 处理器中应答后的主动补发，以 CMD 8 应答为列表唯一来源（`Server/DAFLegacy.Server/Network/EntranceService.cs`）。
- **验证**：冒烟全绿；真客户端复测见 `PENDING.md` P-03（返回选角后弹窗应仅 1 次）。
- **关联**：[client-packet-enums-60cn.md](docs/protocol/client-packet-enums-60cn.md) CMD 7/8 行。
