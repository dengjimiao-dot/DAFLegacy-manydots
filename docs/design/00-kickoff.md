# 00 · 设计开档总纲：跨平台可移植性重构

- 状态：01–06 **已实施**（2026-09-24 标注，重构已落地）；07 **待实施**。05 为已作废提案，仅作技术记录，其后继方案 06 已实施
- 日期：2026-09-20
- 分支基线：`fix/build-and-publish`（开档时工作区干净；本分支仅新增本文档集与 `.gitignore` 调整，未改源码）
- 相关文档：[01 目标架构](01-target-architecture.md) · [02 可移植性契约](02-portability-contracts.md) · [03 构建与发布](03-build-release-ci.md) · [04 迁移路线](04-migration-plan.md) · [05 RandomNative 跨平台化（已作废）](05-randomnative-crossplatform.md) · [06 随机源降级为纯托管实现](06-random-source-managed.md)

---

## 阅读顺序

本文档集位于 `docs/design/`。建议按以下顺序阅读：

| 文档 | 内容 | 读者 |
| --- | --- | --- |
| **00-kickoff.md**（本文） | 开档总纲：问题陈述、现状实测证据、Server/Client 两层架构、关键决策、里程碑 | 所有人，先读这篇 |
| [01-target-architecture.md](01-target-architecture.md) | 目标项目结构、依赖方向规则、各模块职责、参考项目对照、目录重组影响清单与覆盖率核查 | 架构与实现 |
| [02-portability-contracts.md](02-portability-contracts.md) | 可移植性契约 R1–R11：分层、路径、编码、随机源、数据目录、降级 | 实现 |
| [03-build-release-ci.md](03-build-release-ci.md) | 构建入口、包内容责任表、发布布局与 CI 矩阵（2026-09-23 修订：流水线只出服务端两包） | 构建与发布 |
| [04-migration-plan.md](04-migration-plan.md) | M0–M5 分阶段任务、每阶段验收标准、回滚策略、风险与待决问题 | 执行者 |
| [05-randomnative-crossplatform.md](05-randomnative-crossplatform.md) | **已作废**：`DFLegacy.RandomNative` 跨平台化提案（被 06 取代） | — |
| [06-random-source-managed.md](06-random-source-managed.md) | 随机源降级为纯托管实现：服务端与 `DFLegacy.RandomNative` 解耦的决策（模块保留至 Client 层）、参考项目设计摘录、三类随机、待确认问题 | 架构与实现 |

### 两个约定

**关于 `.gitignore`**：仓库根 `.gitignore` 保留 `/docs` 规则，整体忽略 `docs/` 目录——本设计文档集仅本地维护，不随仓库分发。开档时曾计划改为 `/docs/*` 加 `!/docs/design/` 使 `docs/design/**` 可被跟踪，该调整最终未落地。

**关于仓库现有文档**：仓库根 `README.md` 是面向使用者的运行与配置说明，`ADMIN_API.md` 是后台 HTTP 接口参考。本设计**不修改 `README.md`**——Linux 部署方式等新增内容一律在本目录内描述（见 [03](03-build-release-ci.md)），不需要改动仓库根文档。

## 1. 背景

DFLegacy 是一个 60A1 客户端旧版协议的模拟器实现，共 6 个子项目：2 个原生 C++ 模块（`DFLegacy.Ijl15`、`DFLegacy.RandomNative`）与 4 个 .NET 项目（`DFLegacy.Launcher`、`DFLegacy.Protocol`、`DFLegacy.Server`、`DFLegacy.SmokeTests`），全部混在同一个 `DFLegacy.Emulator.slnx` 里。其中 `DFLegacy.RandomNative` 原定随服务端随机源托管化一并删除，后修订为**保留并移至 Client 层**（`Client/DFLegacy.RandomNative`，[06 · 随机源降级为纯托管实现](06-random-source-managed.md)），仍为 6 个子项目。

当前构建链路整体绑定 Windows：解决方案文件混入 `.vcxproj`，因此只能用 MSBuild 构建；两个 CI workflow 固定 `runs-on: windows-latest`；`publish.bat` 是批处理并依赖 vswhere 定位 MSBuild；`build-native.ps1` 还要求 CMake 提供 Visual Studio 生成器；`README.md` 的"运行环境"明确写了 Windows + Visual Studio 桌面 C++ 组件。

**但代码本身的耦合度比构建链路低得多。** 经实测（见第 3 节），三个 .NET 项目在 macOS 上不仅能编译，服务端进程还能实际启动运行，原生随机模块的缺失被设计好的降级路径消化掉了（该模块随后按 [06 · 随机源降级为纯托管实现](06-random-source-managed.md) 与服务端解耦，保留至 Client 层）。真正阻碍跨平台的不是代码，而是**项目边界把可移植的核心和 Windows 专属的部分捆在了一起**。

本文档的目标是：在不改变服务端行为的前提下，重新划定项目边界，把 Windows 专属能力推到叶子节点，让可移植的核心可以用 `dotnet build` 在 Windows / Linux / macOS 上直接构建、测试、发布。

## 2. 目标与非目标

### 目标

1. **G1** 服务端（协议 + 脚本读取 + 业务规则 + HTTP 后台 + 游戏服务）成为一等可移植目标，可在 Linux 上构建、测试、发布、运行。
2. **G2** 服务端不再依赖原生能力（RDSEED 硬件随机桥）：服务端裁决随机改为纯托管的单一实现，与 `DFLegacy.RandomNative` 解耦——模块本身保留至 Client 层（`Client/DFLegacy.RandomNative`），不再被服务端引用（[06 · 随机源降级为纯托管实现](06-random-source-managed.md)）。
3. **G3** Server 层与 Client 层的边界由构建系统在**每次构建时**强制，而不是靠约定和自觉。
4. **G4** 现有 Windows 交付方式（Launcher + IJL15 注入 + 服务端与 Launcher 同目录的部署布局）行为保持不变。
5. **G5** 两层可以**独立构建与打包**，产物互不依赖；全仓库收敛为**一个解决方案**（2026-09-23 修订：解决方案含原生项目、仅 MSBuild 可构建，服务端跨平台构建按项目进行，见 [D4](#d4--全仓库一个解决方案层边界由编译器强制)）。

### 非目标

- **N1** 不让 Launcher 和 IJL15 跨平台。它们的定位就是 Windows 桌面启动器与 32 位 Windows 游戏模块的替身，改造等于重写另一个产品。本次只做**构建解耦**，让"构建 Launcher"不再强依赖 Windows 工具链。
- **N2** 不重写 `EntranceService`（14,264 行）。它的规模是可维护性问题，与可移植性正交，不在本次范围。
- **N3** 不改业务逻辑，也不重写 catalogs/planners 的内容。它们在 M0 会**移入模块目录**（`*Catalog` 进 `GameWorld/`，`*Planner` 与规则类按业务域进 `Game/<域>/`），但那只是文件位置变化，代码与命名空间都不动（[01 · 9.4](01-target-architecture.md#94-命名空间策略分两步)）。不进一步嵌套子目录，不按业务域细分。
- **N4** 不引入数据库、容器编排或新的部署形态。本次只把"能不能在 Linux 上跑"这一步做完。

## 3. 现状实测

在 macOS 26.6.2 / arm64、.NET SDK 10.0.301 上逐一实测，结论如下。

### 3.1 编译与运行

| 子项目 | 类型 / TFM | 非 Windows 编译 | 非 Windows 运行 | 阻塞原因 |
| --- | --- | --- | --- | --- |
| `DFLegacy.Protocol` | net10.0 类库（4,470 行） | ✅ 通过 | ✅ | 无。纯托管，无 P/Invoke、无平台分支 |
| `DFLegacy.Server` | net10.0 Web（80 文件 / 41,926 行） | ✅ 通过 | ✅ 可启动（降级） | 见 3.2 |
| `DFLegacy.SmokeTests` | net10.0 控制台 | ✅ 通过 | ✅ 全部通过 | 无 |
| `DFLegacy.Launcher` | net10.0-**windows** / WinForms | ❌ `NETSDK1100` | ❌ | 目标框架 + WinForms + `d3d9.dll`/`user32.dll` P/Invoke |
| `DFLegacy.Ijl15` | C++ Win32 x86 DLL（5,555 行） | ❌ 设计上拒绝 | — | CMake 对非 `WIN32` 直接 `FATAL_ERROR`；且本质是 Windows 游戏模块 |

实测命令与结果：

```text
dotnet build src/DFLegacy.Protocol          → 0 warning, 0 error
dotnet build src/DFLegacy.Server            → 0 warning, 0 error
dotnet build tests/DFLegacy.SmokeTests      → 0 warning, 0 error
dotnet tests/.../DFLegacy.SmokeTests.dll    → "All DFLegacy protocol smoke tests passed."（退出码 0）
dotnet build src/DFLegacy.Launcher          → error NETSDK1100
```

### 3.2 服务端在 macOS 上的真实启动行为

直接运行已构建的 `DFLegacy.Server.dll`，实际输出：

```text
info: Game random source initialized: provider=OperatingSystemCSPRNG,
      cpuRdSeed=False, nativeBridge=False, rdSeedEnabled=False.
info: Saved emulator state to .../bin/Release/net10.0/data/state.json
```

**这说明降级路径是有效的**：`GameRandomSource` 在非 Windows 上根本不尝试加载原生桥（`CpuAdvertisesRdSeed()` 先返回 false），因此不会抛 `DllNotFoundException`，而是安静地退回操作系统 CSPRNG，状态持久化也正常工作。

随后进程因缺少 `Script.pvf` 而终止：

```text
Unhandled exception. System.IO.FileNotFoundException:
  Creature experience table 'creature/exptable.tbl' was not found in ...
```

这是**数据缺失问题，与操作系统无关**——Windows 上缺同一份 PVF 会同样终止。但它暴露了一个健壮性缺口：绝大多数 catalog 在脚本缺失时只记录警告并禁用对应功能（日志中 `item production is disabled`、`equipment reinforcement is disabled` 等），而 `CreatureExperienceCatalog.LoadThresholds()` 直接抛异常杀进程。新平台上首次部署更容易碰到数据布局不一致，这个不一致必须修掉（见 [02 · R9](02-portability-contracts.md)）。

> 复验说明：完整启动（能实际接受客户端连接）尚未验证，因为仓库内不含 `Script.pvf`。该项列入 [04 阶段 3 验收](04-migration-plan.md)。

### 3.3 三个已排查并排除的可移植性疑虑

| 疑虑 | 实测结论 |
| --- | --- |
| **代码页 936/949 在非 Windows 上是否可用？** | **可用。** `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)` 后，CP936(gb2312)、CP949(ks_c_5601-1987)、CP51949(euc-kr) 在 macOS 上全部可解析，往返转换正常。PVF 路径表用 CP949 解码这一点在 Linux 上不会失效 |
| **`Encoding.Default` 跨平台是否一致？** | **一致，均为 UTF-8。** .NET Core 及以后 `Encoding.Default` 恒为 UTF-8，不再依赖平台 ANSI 代码页。`ScriptFileSystem.ReadAllText` 的默认编码行为在各平台相同 |
| **网络绑定是否依赖主机名解析？** | **不依赖。** `Program.cs`、`EntranceService`、`GameplayDatagramService`、`CharacterDatagramService`、`ProbeService` 全部使用 `IPAddress.Parse(...)`，无 `Dns.*` 调用 |

### 3.4 已确认存在的 Windows 专属配置

`server.json` 的 `ScriptPvfPath` 为 `"..\\Script.pvf"`。实测在 Unix 上：

```text
Path.GetFullPath("..\\Script.pvf", base) → "/…/net10.0/..\Script.pvf"
File.Exists(该路径)                       → false
```

反斜杠在 Unix 上不是路径分隔符而是普通文件名字符，因此该配置在 Linux 上必然解析失败。日志中已经能看到这个畸形路径（`Expected PVF at /…/net10.0/..\Script.pvf`）。这是配置层的 Windows 印记，代码层的 `Path.Combine` / `Path.GetFullPath` 用法本身是正确的。

### 3.5 Linux 发布在今天的代码上就已可行

从 macOS 直接交叉发布 Linux 目标，当前代码无需任何改动即可成功：

```bash
dotnet publish src/DFLegacy.Server/DFLegacy.Server.csproj -c Release -r linux-x64 --self-contained false -o <out>
# → 成功。产物 DFLEGACY.Server 经 file(1) 确认为：
#   ELF 64-bit LSB pie executable, x86-64, dynamically linked,
#   interpreter /lib64/ld-linux-x86-64.so.2
```

同样地，把 3 个 Server 层项目写入一个只含 `.csproj` 的解决方案文件后，`dotnet build` 在该解决方案上成功（0 警告 0 错误）。

**这两条实测结果决定了本次重构的定位。** Linux 部署不是"做不到"，而是"没人管"——它今天就能跑通，只是：没有 CI 覆盖（随时可能被无意的改动破坏）、存在两处真实缺陷（R3 路径、R9 降级）、以及构建图里挂着一个脆弱的 Windows-only 节点（R2）。因此本次工作的价值在于**把这条路径变成受支持、被验证、不会静默失效的**，而不是把它"从不可能变为可能"。这也意味着投入规模可控：M0 几乎零源码改动，M1–M3 都是小范围改动。

### 3.6 全仓库能否只用一个解决方案

参考项目 `ServerS4A21` 的做法是**一个解决方案 + 单项目 + 目录分层**（`Server/DfoServer.sln` 只含 `DfoServer` 与 `PvfLib` 两个项目，业务分层体现为 `Network/`、`Game/`、`GameWorld/`、`Infrastructure/`、`Data/`、`Sqlite/`、`SelfTests/` 等目录）。它之所以只需要一个解决方案，是因为**它不含原生项目**。DFLegacy 有平台专属的原生构建对象（`DFLegacy.Ijl15`），这是"是否必须多个解决方案"的根因。

就此做了三组实测：

| # | 试验 | 结果 |
| --- | --- | --- |
| 1 | 单个 `.slnx` 含全部 4 个 `.csproj`（含 `net10.0-windows` 的 Launcher），加 `-p:EnableWindowsTargeting=true` | Launcher **编译成功**（产出 `net10.0-windows/DFLegacy.Launcher.dll`），仅被 `BuildIjl15` 目标打断——`powershell.exe` 不存在，`MSB3073` 退出码 127 |
| 2 | 同上，跳过 `BuildIjl15` / `CopyIjl15ToOutput` / `CopyIjl15ToPublish` 三个目标（即完成 Launcher 解耦后的状态） | **完整成功，0 警告 0 错误** |
| 3 | 现有 `DFLegacy.Emulator.slnx`（含 2 个 `.vcxproj`） | **硬错误**：两个 `.vcxproj` 报 `MSB4278`——`$(VCTargetsPath)/Microsoft.Cpp.Default.props` 不存在，需要 `MSBuild.exe`。不是"跳过"，是失败 |

由此得到三条确定结论：

1. **`.vcxproj` 必须移出解决方案**，否则任何解决方案都无法用 `dotnet` CLI 在非 Windows 上构建。原生项目改由 `build-native.ps1`（CMake）独立构建——这本来就是产出 `artifacts/native/...` 的既有路径。（2026-09-23 修订重新权衡了这条结论：`MSB4278` 的技术事实不变，但改为把原生项目以 `<Build Project="false" />` 放回解决方案换取 IDE 全量可见，服务端跨平台构建按项目进行，见 [D4](#d4--全仓库一个解决方案层边界由编译器强制)。）
2. **Launcher 可以交叉编译**，只需 `EnableWindowsTargeting=true`。阻塞它的唯一一件事就是那个隐式调用 PowerShell 的 `BuildIjl15` 目标——也正是 [D7](#d7--launcher-只做构建解耦不做跨平台改造) 要拆掉的东西。
3. **因此全仓库可以只有一个解决方案**。这条判定原以"解决方案在三平台 `dotnet build` 成功"作为"服务端托管构建图里没有 Windows 专属节点"的判定手段（D4）——2026-09-23 修订后，该不变量改为由"按项目构建服务端"等价实现（见 [D4](#d4--全仓库一个解决方案层边界由编译器强制)）。

这条结论把原计划里"可选的 M5（Launcher 解耦）"提升为**必需的前置步骤**——它是单解决方案成立的条件，不再是增值项。

### 3.7 两层能否独立打包实测

分层的价值之一是两层可以各自独立出包。就此做了第四组实测：

| # | 试验 | 结果 |
| --- | --- | --- |
| 4 | 从 macOS 单独 `dotnet publish` Client 层的 Launcher（`-r win-x64`，并跳过 M0 将拆掉的原生目标） | **成功**。整个还原过程只涉及 `DFLegacy.Protocol`（Launcher 里那条死引用，M0 删除）与 `DFLegacy.Launcher` 两个项目，完全未触碰 Server 层的服务端项目。产物 `DFLegacy.Launcher.exe` 经 `file(1)` 确认为 `PE32+ executable (GUI) x86-64, for MS Windows` |

**结论：两层可以独立分离打包构建。** 依赖关系是零，两个方向都是——试验 4 里那次对 `DFLegacy.Protocol` 的还原来自一条已确认的死引用，M0 删掉后 Client 层打包只涉及 `DFLegacy.Launcher` 自身；Server 打包则完全不触碰 Client 层。

两点说明：

- 试验中用 `-p:DesignTimeBuild=true` 跳过 Launcher 现有那三个原生相关 Target，这是对"完成 Launcher 解耦后状态"的模拟。它同时跳过了 `ijl15.dll` 的复制，所以真实产物在原生构建已完成时还会**多出** `ijl15.dll`——那正是期望行为，由 `Condition="Exists(...)"` 的可选内容项实现，与 `DesignTimeBuild` 无关。
- Server 层的独立发布此前已实测（[3.5](#35-linux-发布在今天的代码上就已可行)）：`dotnet publish -r linux-x64` 在当前代码上即产出合法 ELF 可执行文件。

## 4. 架构分层：Server 层与 Client 层

**项目结构图**（完整带注释版见 [01 · 1.1](01-target-architecture.md#11-项目结构图)）：

```text
DFLegacy/
├── DFLegacy.Emulator.slnx  唯一解决方案（全部 5 个项目；原生项目仅 IDE 可见，构建走 patch.bat）
├── publish.bat             stop.bat  Windows 构建 / 发布 / 停止脚本（保留）
├── publish.sh              [新] M5 · Linux/macOS 脚本（自探测 RID）
├── .github/workflows/      build.yml / release.yml（改造）
├── docs/design/            本设计文档集
│
├── Server/                 ── Server 层：服务端侧交付物来源 ──
│   ├── DFLegacy.Server/    服务端主项目（改后零 ProjectReference）
│   │   ├── Program.cs    组合根
│   │   ├── Protocol/     线路协议（原独立项目并入）             11
│   │   ├── PvfScript/    PVF/脚本访问 + 编码契约              1+新
│   │   ├── Random/       随机源（纯托管单一实现）              1+新
│   │   ├── Network/      登录主循环与 UDP 服务                 4
│   │   ├── Persistence/  状态持久化                            3
│   │   ├── Hosting/      宿主配置与后台接口模型                 3
│   │   ├── Services/     跨业务域后台服务                       1
│   │   ├── GameWorld/    脚本静态定义与目录（*Catalog，平铺）    25
│   │   ├── Game/         运行时业务规则，按业务域分目录          39
│   │   │   ├── Dungeon/[11]  Inventory/[9]  Quest/[8]
│   │   │   ├── Compound/[3]  Shop/[3]  ItemUpgrade/[2]
│   │   │   └── Account/[1]  Creature/[1]  Drop/[1]
│   │   ├── server.json   [移] M0 · 从仓库根移入
│   │   └── wwwroot/       admin/ 后台界面 + client/ 旧版充值页面
│   ├── DFLegacy.Server.SelfTests/  自测（独立 exe，目录与主项目同构）
│
├── Client/                 ── Client 层：Windows 客户端交付物 ──
│   ├── DFLegacy.Launcher/  Windows 启动器                       net10.0-windows
│   │   └── launcher.json  [移] M0 · 从仓库根移入
│   ├── DFLegacy.Ijl15/     ijl15.dll 替身  C++ · Win32 x86
│   ├── DFLegacy.RandomNative/  RDSEED 硬件随机桥  C++ · x64  [移] M2 · 服务端解耦后保留至此
│   └── Patch/ [移] M0 · 预编译补丁资产：d3d8.dll（原 third_party/d3d8to9/）
```

整个仓库只分两层。归属判定按顺序问三个问题，第一个命中的即为答案：

1. **它随客户端包分发到玩家桌面或游戏进程吗？** → **Client 层**
2. **它随服务端包分发吗？** → **Server 层**。若它同时是 Windows 专属二进制，则必须满足：**可选、失败即降级、不进托管构建图**
3. **都不是？** → 按"随谁分发"归

| 层 | 定义 | 成员 | 平台 |
| --- | --- | --- | --- |
| **Server 层** | 服务端侧交付物来源，全部是纯可移植的托管代码 | `Server/DFLegacy.Server/`（含 `Protocol/`、`PvfScript/`、`Random/`、`Network/`、`Persistence/`、`Hosting/`、`Services/` 七个技术模块，以及 `GameWorld/` + `Game/<业务域>/` 两类游戏模块）、`Server/DFLegacy.Server.SelfTests/` | win / linux / osx |
| **Client 层** | 全部 Windows 客户端交付物 | `Client/DFLegacy.Launcher/`、`Client/DFLegacy.Ijl15/`、`Client/Patch/` | win（`Ijl15` 为 x86） |

**每层一条不变量**，它取代原来那句"Server/ 树里的东西都能在三平台构建"：

| 层 | 不变量 | 谁守 |
| --- | --- | --- |
| `Server/` | **服务端托管构建图**（服务端两个 `.csproj` 及其全部 `ProjectReference`）里不得出现 Windows 专属节点 | 编译器：`NU1201` / `MSB4278` / `NETSDK1100` |
| `Client/` | 无（Windows 专属之地）；`EnableWindowsTargeting` 只允许写在 Launcher 项目内 | 同上——放到全局会削弱 Z2 那道护栏 |

这条不变量与旧说法的守卫力完全相同：护栏本来就只拦引用与构建顺序，从没拦过目录位置，现在只是把规则写成它真实的形状。

关于分层的三个要点：

1. **`DFLegacy.Server` 属于 Server 层。** 这是本次架构判断的核心。服务端曾经唯一的原生耦合（`GameRandomSource` 里的两个 `DllImport`）已随服务端与 `DFLegacy.RandomNative` 的解耦消失——模块保留在 Client 层（[06 · 随机源降级为纯托管实现](06-random-source-managed.md)）。它现在是纯可移植代码，用一个 `dotnet build` 就能覆盖 Linux 部署场景。

2. **配置文件跟着所属层走，不放仓库根。** `server.json` 是 Server 层的资产、`launcher.json` 是 Client 层的资产，两者都要从仓库根移进各自的项目（M0）；`Config.ini` 已在 `Client/DFLegacy.Ijl15/` 下，符合这条规则。这与第 1 点是同一条原则的另一面：仓库根不该躺着只属于某一层的文件。发布产物布局不受影响——文件仍被复制到可执行文件旁边。

### 跨层交互只有一条

```text
Client 层 ──运行期──→ Server 层
  Launcher 按文件名启动 DFLegacy.Server.exe（部署约定，无编译期依赖）
```

**编译期引用是两个方向都零。** 早期版本设想的第二条运行期联系（Server 层运行期探测可选原生资产）随服务端与 `DFLegacy.RandomNative` 的解耦一并消失。

**剩下这一条不可避免**：`Launcher` 在 `StartServer=true`（默认）时按字面量 `"DFLegacy.Server.exe"` 在同目录找服务端。它没有编译期依赖，却有不可忽略的部署耦合——这是 Windows 合并包必须存在的原因（[01 · 2.3](01-target-architecture.md#23-与-server-层的跨层联系)）。

**"编译期零引用"这件事本身值得单说。** 早期的设计版本声称存在一条合法的跨层引用（Client → `DFLegacy.Protocol`），依据是 `DFLegacy.Launcher.csproj` 里的 `ProjectReference`。经实测该引用**是死引用**——Launcher 的源码没有使用 Protocol 的任何一个 public 类型（43 个类型逐个检索，零命中），它只 `using System.*`，配置模型也定义在自己内部。详见 [01 · 3.0](01-target-architecture.md#30-先修正一处事实launcher-对-protocol-的引用是死引用)。

## 5. 关键设计决策

### D1 · 按"交付面"分两层，而不是按技术分层

不从 DDD 或 Clean Architecture 的角度重排项目，而是**只问一个问题：这东西随哪个包分发**。随客户端包走的进 `Client/`，随服务端包走的进 `Server/`，两层之间只剩一条运行期联系（上节）。

**位置判据与"服务端可移植"是两件事，分工必须说清。** 位置由交付面决定；"服务端能在三个平台构建"这条性质由一个**不变量**守住：服务端**托管**构建图里不得出现 Windows 专属节点。前者是命名与归属的规则，后者是可以被机器验证的规则——服务端与 `DFLegacy.RandomNative` 解耦后（模块保留在 Client 层），`Server/` 下没有任何 Windows 专属成员（[Z1–Z4](01-target-architecture.md#32-禁止清单)）。

- **理由**：分层的价值必须能被机器验证，否则会退化回"约定"。"服务端托管构建图是否纯净"可以用一个 Linux 构建直接判定（`dotnet build` 两个服务端 `.csproj` 必须成功；2026-09-23 修订把原生项目放回解决方案后，跨平台验证改为按项目构建）；而"领域层是否干净"这种断言今天无法验证（没有替换需求）。把可验证的属性当分层依据。
- **取舍**：`DFLegacy.Server` 会同时包含 HTTP 后台、游戏服务、业务目录三件事，不够"内部干净"。接受这一点，换取不搬动 2 万多行已验证代码。
- **收益**：层名与位置名实相符（`Client/` 只剩面向客户端的东西），归属规则简单到可以口头传递——"随哪个包分发就放哪层，跨层引用一律禁止"。新增代码时几乎不会归属错。

### D2 · 原生随机模块与服务端解耦（模块保留至 Client 层），随机源降级为纯托管单一实现

`DFLegacy.Server.csproj` 曾有一个指向 `DFLegacy.RandomNative.vcxproj` 的 `ProjectReference`，以及一个硬编码到 `artifacts/native/random/$(Configuration)/` 的 `Content` 项，使**服务端的构建图里存在一个只有 Windows 才能求值的节点**。本设计将服务端与 `DFLegacy.RandomNative` 项目整体解耦（模块保留至 `Client/DFLegacy.RandomNative`，不再被任何托管项目引用），服务端裁决随机改为纯托管的**单一实现**：`Random/` 模块目录里的 `GameRandomSource`（基于 `Random.Shared`），对外缝保留 `IDropRandomSource`，42 处调用点零改动。

**边界**：协议层会话密钥等密码学用途必须继续走 `RandomNumberGenerator`，不得改走伪随机。

- **理由**：这个原生模块的全部作用是把 CPU 的 RDSEED 硬件熵源接进服务端，而它对玩法结果是零影响——为这一点收益维持一个跨平台原生工程（RID 探测、`DllImportResolver`、独立 CMake 工具链、CTest）成本与收益不成比例。
- **细节**：[02 · R2](02-portability-contracts.md) 保留为零实例的前置约束；完整决策见 [06 · 随机源降级为纯托管实现](06-random-source-managed.md)。

### D3 · 服务端收成一个项目，模块用目录分组

服务端全部代码收进**一个项目** `Server/DFLegacy.Server/`，内部用模块目录分组：技术模块（`Protocol/`、`PvfScript/`、`Random/`、`Network/`、`Persistence/`、`Hosting/`、`Services/`）与游戏模块（`GameWorld/` 放脚本静态定义，`Game/<业务域>/` 放运行时规则，按参照项目的 `Game/*` 模型）。原先计划的独立项目 `DFLegacy.Script`（现名 `PvfScript`）与 `DFLegacy.Random` 改为**目录**，原独立项目 `DFLegacy.Protocol` **并入**同一项目。

- **理由**：目录层级形式参照 `ServerS4A21`（`Server/DfoServer/Network/` 这种层/项目/模块三级），它的服务端也是一个项目加模块目录。Protocol 之所以能并入，是因为实测证明它不需要是独立程序集（[01 · 3.0](01-target-architecture.md#30-先修正一处事实launcher-对-protocol-的引用是死引用)）——它没有被 Client 层引用。
- **收益**：服务端主项目**零 `ProjectReference`**，层边界的判定从"4 个项目是否各自纯净"简化为"服务端托管项目是否引用了另一层的项目或任何 `.vcxproj`"，机械且不易出错。
- **代价**：`PvfScript/` 与 `Random/` 失去独立项目带来的测试隔离——测试项目要引用整个服务端项目（含 ASP.NET 依赖）。这是真实损失，但与原设计相比只是测试依赖面变大，不影响正确性，且与参照项目的做法一致。
- **明确不做的**：不进一步嵌套（不做多级子目录），不改命名空间（第一步只移文件，见 [01 · 9.4](01-target-architecture.md#94-命名空间策略分两步)）。

### D4 · 全仓库一个解决方案；层边界由编译器强制

`DFLegacy.Emulator.slnx` 是仓库中唯一的解决方案文件，成员是全部 5 个项目（服务端主项目、自测、Launcher、Ijl15、RandomNative），IDE 内全量可见（2026-09-23 修订）。两个 `.vcxproj` 以 `<Build Project="false" />` 标注为**不参与解决方案构建**——原生构建统一走 `patch.bat`（[03 · 4.5](03-build-release-ci.md)）；含 `.vcxproj` 的解决方案在 `dotnet` CLI 上是硬错误（`MSB4278`），服务端跨平台构建按项目进行（`dotnet build` 直接指定 `.csproj`）。

- **理由**：实测（[3.6](#36-全仓库能否只用一个解决方案)）表明不含 `.vcxproj` 的单解决方案可在三个平台 `dotnet build`，而含 `.vcxproj` 的在 `dotnet` CLI 上是硬错误（`MSB4278`）。2026-09-23 修订权衡后把原生项目放回解决方案：IDE 全项目可见、单一构建入口的价值高于"解决方案本身可跨平台"——后者由"按项目构建服务端"等价实现。`<Build Project="false" />` 同时消除原生项目在解决方案平台矩阵下的配置冲突（Ijl15 仅 Win32、RandomNative 仅 x64，否则 `msbuild` 会以 `MSB8013` 请求不存在的 `Release|x64`）。
- **层边界靠什么守**：编译器，不是自建机制。实测证明 Z1–Z3 全部被无条件覆盖——跨层引用是 `NU1201`（`net10.0` 项目引用 `net10.0-windows` 项目，还原期拒绝，与 `EnableWindowsTargeting` 无关）、`.vcxproj` 引用是 `MSB4278`、`UseWindowsForms` / `UseWPF` 是 `NETSDK1100`。只有 Z5（Windows 系统库 `DllImport`）需要补一处 CI 源码扫描。**不引入 `Directory.Build.props` / `Directory.Build.targets`，也不写 MSBuild 校验目标**——完整实测矩阵与取消理由见 [01 · 3.3](01-target-architecture.md)。
- **一条必须遵守的约束**：`EnableWindowsTargeting` **只写在 `Client/DFLegacy.Launcher/DFLegacy.Launcher.csproj` 内**，绝不放仓库根。它是 `NETSDK1100` 那道门禁的开关，全局打开会让 Server 层的 WinForms/WPF 违规在 Linux 上静默通过。
- **保留**：`DFLegacy.Emulator.slnx` 的文件名不变，Windows 用户既有的命令与 Visual Studio 工作流不受影响——只是它不再构建原生项目；原生与客户端产物由 `patch.bat` 承担（见 [03 · 4.5](03-build-release-ci.md) 的运维模型修订）。
### D5 · 路径与数据目录锚点显式化、可覆盖

引入统一路径解析：配置中的路径统一用 `/`，读取时把 `\` 归一为 `/`；锚点默认仍是 `AppContext.BaseDirectory`（保持现有 Windows 部署布局不变），但允许 `DFLEGACY_HOME` 环境变量覆盖，以便 Linux systemd / 容器把可写数据放到 `/var/lib/dflegacy`。

- **理由**：`AppContext.BaseDirectory` 在 Linux 上通常是只读的安装目录，把 `data/state.json` 写在那里不符合平台习惯；同时现有 Windows 用户依赖"发布目录内的相对路径"，不能破坏。

### D6 · 降级必须是一致行为，不能有的降级有的崩

所有 catalog 在数据缺失时的行为统一为：记录 `Error`/`Warning` 日志 + 把对应功能标记为不可用 + 服务端继续启动。当前 `CreatureExperienceCatalog` 抛异常终止进程的行为要改掉。

- **理由**：既是健壮性问题，也是可移植性问题——新平台的首次部署最容易出现数据布局不一致，此时"能启动并打印清晰的缺失清单"远比"崩在第一条缺文件上"有用。

### D7 · Launcher 只做构建解耦，不做跨平台改造

把 `DFLegacy.Launcher.csproj` 里的 `BuildIjl15` / `CopyIjl15ToOutput` / `CopyIjl15ToPublish` 三个 MSBuild Target 改成**消费预构建产物**，原生构建变成一个独立的、可选的前置步骤。同时加 `EnableWindowsTargeting=true` 使 Launcher 可以在非 Windows 上交叉编译出 Windows 二进制。

- **理由**：现在"构建 Launcher"隐含"必须有 PowerShell + CMake + Visual Studio"；解耦后 Launcher 的构建只需 .NET SDK。这让 macOS/Linux 开发者可以验证 Launcher 的托管代码改动而无需 Windows 环境。
- **不做的**：不移植 WinForms、不移植 `D3D9DeviceProbe`。

### D8 · 两层独立打包，产物互不依赖

**两层各自可以独立构建与打包**，互不需要对方的产物。这是分层带来的直接收益，也是它必须经受的检验——如果两层仍要一起构建才能出包，分层就只是目录上的装饰。

实测（见 [3.7](#37-两层能否独立打包实测)）：从 macOS 单独发布 Client 层的 Launcher，产出合法的 Windows GUI 可执行文件，全程不触碰 Server 层的服务端项目。当时那次还原涉及 `DFLegacy.Protocol`（即 Launcher 里那条死引用，M0 删除），删掉之后 Client 层打包只还原 `DFLegacy.Launcher` 自身。

- **依赖关系是零，两个方向都是**：Client 打包只涉及 Client 层的项目，不编译 Server 的任何代码、不需要 Server 的任何产物；Server 打包则完全不触碰 Client 层。
- **发布产物分开**：服务端包按 RID 出 `win-x64` / `linux-x64`；客户端包只出 `win-x64`。
- **另出一个 Windows 合并包**，保留现有"服务端与 Launcher 同目录、解到客户端目录内即可用"的部署习惯——现有用户的用法不能因为分层而失效。三种交付形态的取舍见 [03 · 6](03-build-release-ci.md#6-发布流程releaseyml)；**2026-09-23 修订**：发布流水线只产出服务端两包（`linux-x64` / `server-win-x64`），合并包与纯客户端包改由本地 `patch.bat` 发布（[03 · 4.5](03-build-release-ci.md)）。

## 6. 里程碑

| 里程碑 | 内容 | 产出 | 验收摘要 |
| --- | --- | --- | --- |
| **M0** 结构重组与构建收敛 | 目录三极重组（`Server/`/`Client/`）+ Protocol 并入 + 单解决方案 + Launcher 解耦 + 配置文件归位 | `Server/DFLegacy.Server/{Protocol,Script,Random,Network,…}/`、`Client/…` | 三平台按项目 `dotnet build` 服务端两项目成功（2026-09-23 修订；解决方案含 `.vcxproj`，仅 MSBuild 可构建）；产物与移动前逐项一致 |
| **M1** 层边界护栏 | CI 源码扫描（Z5）+ 三平台矩阵 + 功能面与产物基线 | 可执行的层边界 | 故意给 Server 层项目加 Windows 依赖能被拦下 |
| **M2** 随机源收拢 | 服务端与 `DFLegacy.RandomNative` 解耦（模块保留至 Client 层），`Random/` 模块换成纯托管的单一实现（保留 `IDropRandomSource`） | 服务端随机源无任何原生依赖 | 42 处调用点零改动；三平台随机源行为一致 |
| **M3** PVF 脚本模块收拢 | `PvfScript/` 模块补齐编码契约与路径归一；集中编码注册 | PVF 读取三平台测试 | 含 CP936/CP949 路径表的 PVF 用例在 3 平台通过 |
| **M4** 路径与数据契约 | `PathResolver`、`DFLEGACY_HOME`；修 R9 降级一致性 | 配置与路径契约落地 | Linux 上服务端用真实 `Script.pvf` 完整启动并接受连接 |
| **M5** 独立打包与发布 | `publish.sh`（RID 探测）、CI 发布矩阵 | CI 绿 | `ubuntu-latest` 产出 `linux-x64` 服务端包；`windows-latest` 产出 `server-win-x64` 包（2026-09-23 修订后 CI 只出服务端两包） |

M0–M5 是本次重构的全部承诺范围。详细任务分解与每阶段验收命令见 [04 迁移路线](04-migration-plan.md)。

**M0 比其他里程碑重得多。** 它同时包含三件互相牵连的事：目录重组（移动 79 + 11 个 `.cs`、3 个项目换位置、1 个项目消失）、解决方案成员变化（移出 `.vcxproj`）、Launcher 解耦与配置文件归位。这四件事**必须在同一个提交里完成**——只做一半会得到半旧半新的路径，任何构建都会失败。因此 M0 的验收标准是"与移动前的产物逐项一致"，而不是"能构建"。

**注意 M0 与 M1 的次序**：M0 之后、M1 之前，Z1–Z3 仍由编译器无条件守着（[01 · 3.3](01-target-architecture.md#33-强制手段编译器承担绝大部分只补一处源码扫描)），缺的是 Z5 的源码扫描与三平台 CI 矩阵——也就是说，这段时间里**写错的跨层引用仍会被编译期拦下，但"产物在各平台上是否真的可用"没人验证**。**两者应连续完成，不要把 M1 推后**——这是本次迁移风险最高的窗口。

**已验证的关键假设**：试验 2 证明不含 `.vcxproj` 的单解决方案在三平台可构建；试验 4 证明两层可独立打包；试验 3 证明含 `.vcxproj` 的解决方案无法用 `dotnet` CLI 构建（2026-09-23 修订据此把原生项目以不参与构建的方式放回解决方案）；Launcher 对 Protocol 的死引用经 43 个类型逐个检索确认。M0 涉及的**业务源码改动为零**——只有文件移动与构建配置修改。

## 7. 风险摘要

| 风险 | 影响 | 应对 |
| --- | --- | --- |
| **M0 的目录重组是本次改动面最大的一步**（移动 79 + 11 个 `.cs`、3 个项目换位置、1 个项目消失，并牵连 `.slnx`、CI、`publish.bat`） | 漏改一处路径即构建失败；若与语义改动混在一起则无法二分定位 | 作为**纯 `git mv` 的独立提交**，验收标准是"产物与移动前逐项一致"而非"能构建"；四件事必须在同一提交内完成，只做一半会落到半旧半新的路径上 |
| Protocol 并入主项目后，`DFLegacy.Protocol.csproj` 消失 | 若有外部脚本引用该 csproj 会失效 | 全仓库检索确认只有 `DFLegacy.Launcher.csproj` 引用它，且是死引用；CI 与 `publish.bat` 均未引用该 csproj 路径 |
| 搬运 `ScriptFileSystem` 时改变行为 | PVF 路径大小写、缓存语义、目录回退路径可能出现细微差异 | M3 前先补齐已存在行为的特征测试；`StringComparer.OrdinalIgnoreCase` 等语义逐条对照。注意 M0 **只移动文件不改逻辑**，M3 才调整语义 |
| **把 `EnableWindowsTargeting` 放到仓库根或共享 props** | Z2 的 WinForms/WPF 违规会在 Linux 上静默通过——护栏失效且无任何报错 | 它只写在 `Client/DFLegacy.Launcher/DFLegacy.Launcher.csproj` 内；M0 验收含"仓库根不存在 `Directory.Build.props`"一项（[01 · 3.3](01-target-architecture.md)）。**Z1–Z3 本身由编译器无条件守住，不需要额外机制** |
| `.vcxproj` 移出解决方案后，Windows 上 `msbuild DFLegacy.Emulator.slnx` 不再构建原生项目 | 习惯用该命令的开发者会以为原生已构建 | `publish.bat` 显式调用 `build-native.ps1`；`README.md` 不改，改动说明落在 [03](03-build-release-ci.md) |
| **迁移可能丢掉功能入口而无人察觉** | 文件一个不少、构建也通过，但某个 CLI 开关或 Admin API 端点失效——**文件覆盖检查发现不了这类问题** | M0 提取功能面基线（[02 · R11](02-portability-contracts.md#r11--功能面不变)），并明确：服务端 CLI、Launcher 的 6 个 CLI 模式、31 个路由、6 个后台服务都必须逐项验证**运行期可达**，不能只看构建 |
| 改名 `server.json` / `launcher.json` 的项类型写错 | 文件静默不进输出目录，构建却显示成功 | 两个项目 SDK 不同（`Content` vs `None`）；验收要求实际 `ls` 输出目录（[01 · 10.3](01-target-architecture.md#103-验收)） |
| Linux 上缺少 `Script.pvf` 导致 M4 无法验收 | 里程碑卡住 | 用现有目录回退模式（`SkillScriptPath`）构造最小脚本集作为验收夹具，真实 PVF 验收另计 |
| 降级一致性改动（R9）触发行为变化 | 原本"崩掉"的场景变成"启动但功能禁用"，可能掩盖真实配置错误 | 缺失数据一律以 `Error` 级别日志 + 启动完成时汇总缺失清单呈现，不做静默降级 |
| 服务端在 Linux 上的性能特征未知 | 14k 行的包处理循环可能有平台相关的性能假设 | 非本次承诺；记录为待决事项 |

完整风险与待决问题清单见 [04 · 风险与待决](04-migration-plan.md#6-风险登记)。
