# 02 · 可移植性契约 R1–R11

- 状态：**已实施**（2026-09-24 标注，重构已落地）
- 上级文档：[00 开档总纲](00-kickoff.md) · [01 目标架构](01-target-architecture.md)

这十条是"可移植"在本项目中的**可判定定义**。违反其中任何一条的代码，即使能在 Windows 上工作，也不算满足本设计。每条都给出当前状态、目标状态与判定方式。

---

## R1 · 分层与依赖方向

**契约**：每个项目属于 Server 层或 Client 层之一，**归属按交付面判定**（随哪个包分发）。**两层互不引用**——Server 层项目不得引用 Client 层项目，也不得使用 `-windows` TFM、WinForms/WPF、`.vcxproj` 引用、无降级分支的 Windows 系统库 P/Invoke；Client 层项目也不得引用 Server 层的任何项目。**跨层引用是两个方向都零。**

**同层不等于可以引用**：原生项目不得被任何托管项目 `ProjectReference`，**位置与构建图成员是两件事**（见 [01 · 3.2](01-target-architecture.md#32-禁止清单)）。这条规则原先的实例是 Server 层的 `Server/DAFLegacy.RandomNative`；该模块已按 [06 · 随机源降级为纯托管实现](06-random-source-managed.md) 与服务端解耦并保留至 `Client/DAFLegacy.RandomNative/`，**Server 层现在没有任何原生项目**。规则在 Client 层同样生效：`Client/DAFLegacy.Ijl15/DAFLegacy.Ijl15.vcxproj` 与 `Client/DAFLegacy.RandomNative/` 都不被任何托管项目引用（前一引用由 M0 删除）。

**关于"Client 可引用 Server"**：早期版本曾允许一条例外（Client 层引用 `DAFLegacy.Protocol`，因为 `DAFLegacy.Launcher.csproj` 里确有该 `ProjectReference`）。实测证明**那是死引用**——Launcher 从未使用 Protocol 的任何 public 类型（43 个类型逐个检索，零命中），详见 [01 · 3.0](01-target-architecture.md#30-先修正一处事实launcher-对-protocol-的引用是死引用)。因此例外取消，规则简化为"互不引用"；也正是这个修正使 Protocol 不再需要是独立程序集、可以并入服务端主项目。

**当前**：无分层概念。`.slnx` 把 `.vcxproj` 与 `.csproj` 混在一起，`DAFLegacy.Launcher`（Client 层）与 `DAFLegacy.Server`（Server 层）在同一个构建图里，且 Launcher 通过 `BuildIjl15` 隐式驱动原生构建。

**目标**：见 [01 · 依赖方向规则](01-target-architecture.md#3-依赖方向规则)。

**判定**：**编译器承担绝大部分，只需补一处源码扫描**（Z5）。Z1–Z7 逐条的拦截机制、强度与实测输出统一登记在 [01 · 3.3](01-target-architecture.md#33-强制手段编译器承担绝大部分只补一处源码扫描) 的实测拦截矩阵，此处不重复列录。

**前置约束**：`EnableWindowsTargeting` 不得出现在仓库根或任何共享 props 中，否则 Z2 的 WinForms/WPF 分支会在 Linux 上静默通过。它只属于 `Client/DAFLegacy.Launcher/DAFLegacy.Launcher.csproj`。

**早期设计中的 `DFL0001`–`DFL0003` 构建期校验目标已取消**——实测证明编译器已无条件覆盖 Z1/Z2/Z3，自建目标只会重复它、并引入一个可被写错的组件与一份会漂移的 Client 项目清单。

**残余缺口**：同时把 Server 与 SelfTests 改成 `-windows` 并全局打开 `EnableWindowsTargeting` 的刻意改动不会被拦。这不在"防意外"的范围内。

---

## R2 · 原生能力的运行期发现

**契约**：Server 层**当前不存在原生资产**，因此这一条是**零实例的规则**：保留它，是为了将来若引入原生二进制时先有可判定的前置约束。

规则本身：Server 层项目对原生二进制的依赖必须是**运行期的、可选失败的**。编译期零 `ProjectReference`（同层也不例外）；库名使用**逻辑名**并由 `SetDllImportResolver` 按 RID 解析，不写死某个平台的文件名；加载失败转换为能力标记，不抛出、不终止进程。

**当前与目标**：这条规则历史上的服务端实例是 `Server/DAFLegacy.RandomNative`（RDSEED 硬件熵源桥）。**该模块已与服务端解耦并保留至 `Client/DAFLegacy.RandomNative/`**——随机源降级为纯托管的单一实现，理由、备选方案与全部细节见 [06 · 随机源降级为纯托管实现](06-random-source-managed.md)。解耦后：

- `DAFLegacy.Server.csproj` 没有任何指向 `.vcxproj` 的 `ProjectReference`，也没有原生内容项；
- 服务端程序集内不存在 `DllImport`；
- 发布产物中不存在 `runtimes/*/native/*`；
- 三个平台走完全相同的托管代码路径，不存在"原生库在不在"这一状态。

**判定**：

| 检查 | 判据 |
| --- | --- |
| 服务端无原生调用 | `git grep "DllImport" Server/DAFLegacy.Server/` 无结果 |
| 构建图无原生节点 | `DAFLegacy.Server.csproj` 的 `ProjectReference` 全部指向托管项目 |
| 包内无原生库 | 任一发布产物中不存在 `runtimes/` 目录 |
| 三平台一致 | `dotnet build` 两个服务端 `.csproj` 在三平台均成功（解决方案含 `.vcxproj`，仅 MSBuild 可构建），且启动日志中的随机源字段不随平台变化 |

**若将来引入原生资产**（例如确实需要某项只有原生才能取到的硬件能力），必须同时满足三件事：Z3（不被任何托管项目 `ProjectReference`）、逻辑库名 + 按 RID 解析、加载失败降级为能力标记。三者缺一即违反本条。

---

## R3 · 路径契约

**契约**：配置文件中出现的路径可以使用任意分隔符；解析时统一归一。归一规则集中在一处，不允许各模块自行 `Replace`。

**当前**：`server.json` 的 `ScriptPvfPath` 为 `"..\\Script.pvf"`。实测在 Unix 上 `Path.GetFullPath` 会产出含字面反斜杠的畸形路径（见 [00 · 3.4](00-kickoff.md#34-已确认存在的-windows-专属配置)），PVF 必然找不到。此外 `CeraShopCatalog`、`CharacterStatCatalog`、`ItemCatalog`、`CharacterCreatureInventoryPlanner` 等多处各自做 `Replace('\\','/')`，语义不统一。

**目标**：

```csharp
public static class PathResolver
{
    /// 归一为当前平台的分隔符。接受两种输入分隔符。
    public static string Normalize(string configured) =>
        configured.Replace('\\', '/');

    /// 归一后按 anchor 解析为绝对路径。
    public static string Resolve(string configured, string anchor) =>
        Path.GetFullPath(
            Normalize(configured).Replace('/', Path.DirectorySeparatorChar),
            anchor);
}
```

使用规则：

- **配置文件层面**：文档与示例统一改用 `/`（`"../Script.pvf"`）。旧的反斜杠写法仍然可用，因为 `Normalize` 会处理——**不破坏现有用户的配置**。
- **代码层面**：所有读取配置路径的地方改调 `PathResolver.Resolve`；删除各处零散的 `Replace`。
- **虚拟路径层面**（PVF 内部的路径、`ScriptFileSystem` 的 `NormalizeVirtualPath`）保持现状：始终 `\` → `/` + 小写。这与 OS 无关，不属于本契约范围，不要混用。

**已知取舍**：`Normalize` 会把文件名中合法的反斜杠也当作分隔符。Unix 上反斜杠是合法文件名字符，所以理论上存在正常文件被误解析的可能。本项目的数据文件命名不包含反斜杠，接受该取舍并在文档中说明。

**判定**：在 Linux 上用 `"..\\Script.pvf"` 的旧配置启动，日志中报出的路径必须是正常的 `/path/to/Script.pvf`（而不是含字面反斜杠的路径），且文件存在时能正确加载。

---

## R4 · 编码契约

**契约**：代码页提供程序在**组合根注册一次**；任何 `Encoding.GetEncoding(<代码页>)` 调用点必须在注册之后执行。禁止依赖平台 ANSI 代码页语义。

**当前**：`Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)` 出现在 **15 处调用点、15 个文件**：

```text
Server/DAFLegacy.Server/Protocol/PvfArchiveReader.cs
Server/DAFLegacy.Server/Program.cs
Server/DAFLegacy.Server/PvfScript/ScriptFileSystem.cs
Server/DAFLegacy.Server/EntranceService.cs
Server/DAFLegacy.Server/ItemCatalog.cs
Server/DAFLegacy.Server/DungeonCatalog.cs
Server/DAFLegacy.Server/DungeonExperienceCatalog.cs
Server/DAFLegacy.Server/ObjectDropCatalog.cs
Server/DAFLegacy.Server/ClearRewardCatalog.cs
Server/DAFLegacy.Server/AvatarCompoundCatalog.cs
Server/DAFLegacy.Server/CompoundCatalog.cs
Server/DAFLegacy.Server/EquipmentReinforcementCatalog.cs
Server/DAFLegacy.Server/MonsterChampionDropCatalog.cs
Server/DAFLegacy.Server/StaminaRecoveryCatalog.cs
Server/DAFLegacy.Server/WorldMapCatalog.cs
```

重复注册是幂等的，功能上无害，但"编码契约"因此没有单一归属地，且使 `Server/DAFLegacy.Server/PvfScript/` / `Server/DAFLegacy.Server/Protocol/` 无法脱离 `Program.cs` 独立使用。

**目标**：收敛到两处——

1. 组合根（`Program.cs`）注册一次，在读取任何配置与脚本之前。
2. `Server/DAFLegacy.Server/PvfScript/` 在被独立使用（测试、工具）时自行注册一次，使该层自足。

**编码用途清单**（搬运时保持不变）：CP949 用于 PVF 路径表；CP936 用于客户端可见文本（`Program.cs` 中以 `ExceptionFallback` 严格模式构造）；`Latin1` 用于无语义字节透传（`ObjectDropCatalog`、`ClearRewardCatalog`、`CharacterStatCatalog`）；`Encoding.Default` 作为 `ReadAllText` 的默认（.NET Core 起全平台为 UTF-8）。

**已验证事实**：CP936 / CP949 / CP51949 在 macOS 上经 `CodePagesEncodingProvider` 全部可用且往返正确；`Encoding.Default` 在 macOS 上为 `utf-8`。因此本契约的迁移不涉及编码能力缺失风险。

**判定**：自测项目的 `PvfScript/` 目录中构造含中文（CP936）与韩文（CP949）路径表的 PVF 夹具，在三个平台上解码结果一致。

---

## R5 · 配置与数据锚点

**契约**：配置与数据的锚点默认保持 `AppContext.BaseDirectory`（不改变现有 Windows 部署布局），但必须可被环境变量覆盖。

**当前**：`Program.cs` 以 `Path.Combine(AppContext.BaseDirectory, "server.json")` 读取配置且 `optional: false`；`JsonGameStore.StorePath` 为 `Path.GetFullPath(options.DataPath, AppContext.BaseDirectory)`。锚点不可配置。Linux 上可执行文件所在目录通常只读，把 `data/state.json` 写在安装目录不符合平台习惯。

**目标**：引入锚点解析，优先级从高到低：

```text
1. 命令行参数  --home <path>
2. 环境变量    DAFLEGACY_HOME
3. 默认        AppContext.BaseDirectory
```

锚点确定后：

- 配置文件路径 = `<home>/server.json`
- 配置内相对路径的解析基准 = `<home>`
- 默认数据路径 = `<home>/data/state.json`

**兼容性**：Windows 上不设环境变量时行为与现在完全一致。

**判定**：`DAFLEGACY_HOME=/tmp/daflegacy-linux dotnet DAFLegacy.Server.dll` 时，配置从 `/tmp/daflegacy-linux/server.json` 读取、`state.json` 写入 `/tmp/daflegacy-linux/data/`。

---

## R6 · 网络绑定

**契约**：所有监听地址必须来自 `IPAddress.Parse`，禁止 DNS 名称解析；端口与地址全部来自配置。

**当前**：**已满足。** `Program.cs`（Kestrel 后台）、`EntranceService`（TCP，含 `AdditionalPorts`）、`GameplayDatagramService`（UDP 主/次端口）、`CharacterDatagramService`（UDP）、`ProbeService`（TCP 探测）全部使用 `IPAddress.Parse(options.*.Host)`，全仓库无 `Dns.*` 调用。

**目标**：保持不变，作为回归契约记录，避免后续引入主机名。

**判定**：代码检索无 `Dns.` / `Dns.GetHost*` 调用。

---

## R7 · 外部进程与系统集成

**契约**：Server 层项目不得依赖 Windows 专属的系统集成点。

**当前与目标**：

| 集成点 | 当前状态 | 目标 |
| --- | --- | --- |
| Windows 事件日志 | 已在 `Program.cs` 通过 `builder.Logging.ClearProviders()` 规避（注释说明"缺管理员权限时事件源创建会失败"） | 保留该决定，并在日志中说明降级原因 |
| 进程终止 | `stop.bat` 使用 `taskkill` 结束 `DAFLegacy.Server.exe` | 服务端须正确响应 `SIGTERM` 优雅退出（`IHostedService` + `stoppingToken` 已具备机制，需实测确认）；`stop.bat` 保留，另提供 Unix 用法说明 |
| 子进程启动 | `DAFLegacy.Launcher/ClientProcessLauncher.cs` 启动客户端 | Client 层，不在契约范围 |
| 注册表 / 服务注册 | 无使用 | 保持无使用 |
| 文件权限 / ACL | 无使用 | 保持无使用 |

**判定**：`kill -TERM <server pid>` 后进程在一个日志刷盘周期内正常退出，退出码为 0，且状态文件已落盘（不能损坏 JSON）。

---

## R8 · 文件系统语义

**契约**：不得假设文件系统大小写不敏感。

**风险分析**：这是本项目最容易被忽略的可移植性陷阱，因为开发环境是 Windows。

| 场景 | Windows | Linux | 结论 |
| --- | --- | --- | --- |
| PVF 内部路径查找 | 字典 + `StringComparer.OrdinalIgnoreCase` | 同左 | **安全**，与 OS 无关 |
| PVF 归档条目名归一 | `NormalizeVirtualPath` 统一小写 | 同左 | **安全** |
| 目录回退模式的路径查找 | `File.Exists` 大小写不敏感 | **大小写敏感** | **有风险** |
| 目录回退模式的枚举 | `Directory.EnumerateFiles` | 同左，但排序与大小写行为不同 | 已用 `OrderBy(OrdinalIgnoreCase)`，**安全** |
| 换行处理 | `\r\n` | `\n` | `ReadAllLines` 已同时处理 `\r\n` / `\n` / `\r`，**安全** |

**目标**：目录回退模式（`SkillScriptPath` 指向解包目录）改为**启动时一次枚举建立 `OrdinalIgnoreCase` 索引**，后续查找走索引而不是 OS 路径查找。这使目录模式的语义与 PVF 模式一致，也让从 Windows 解包出来、大小写可能不一致的脚本目录在 Linux 上可用。

**判定**：在 Linux 上准备一个含 `Equipment/Equipment.lst`（大写目录）的脚本目录，用小写的 `equipment/equipment.lst` 访问必须成功。

---

## R9 · 降级一致性

**契约**：脚本数据缺失时，所有消费方的行为必须一致——记录 `Error` 级日志、把对应功能标记为不可用、服务端继续启动。任何"缺文件即终止进程"的初始化路径都是缺陷。

**当前**：绝大多数 catalog 的行为已经符合契约——它们在缺失时记录警告并禁用功能。实测日志可见：

```text
warn: CompoundCatalog  … item production is disabled.
warn: AvatarCompoundCatalog … avatar compound is disabled.
warn: EquipmentReinforcementCatalog … equipment reinforcement is disabled.
warn: ResealCatalog … equipment resealing is disabled.
warn: CharacterSpCatalog … level-ups will grant no SP.
warn: CharacterStatCatalog … built-in base attributes will be used.
```

**唯一不符合的**是 `CreatureExperienceCatalog.LoadThresholds()`（`CreatureExperienceCatalog.cs:144` 附近），它在 `!_scripts.FileExists(...)` 时抛 `FileNotFoundException`，导致进程终止：

```text
Unhandled exception. System.IO.FileNotFoundException:
  Creature experience table 'creature/exptable.tbl' was not found in …
  at DAFLegacy.Server.CreatureExperienceCatalog.LoadThresholds()
  at Program.<Main>$(String[] args) in Program.cs:line 112
```

**范围界定**：`ScriptFileSystem` 自身的 4 处 `FileNotFoundException` **不属于本契约的违反**——访问层对外抛异常是正确契约，责任在调用方"先 `FileExists` 再读取"。需要改的只有 `CreatureExperienceCatalog` 这一处调用方。

**目标**：

1. `CreatureExperienceCatalog` 改为与同类一致：缺失时 `Error` 日志 + 禁用生物经验功能。
2. 组合根在启动完成前**汇总输出缺失数据清单**，使"降级"不会掩盖配置错误。

**判定**：删除或改名 `creature/exptable.tbl` 后服务端仍能启动，日志中有明确的错误行与缺失清单，且生物经验相关行为表现为禁用而非崩溃。

---

## R10 · 跨平台测试契约

**契约**：Server 层的测试必须在 `win-x64`、`linux-x64`、`osx-arm64` 三个平台上全部通过；测试不得依赖仓库外的数据文件。

**当前**：自测项目（现为 `tests/DAFLegacy.SmokeTests`，12 个文件；M0 后迁至 `Server/DAFLegacy.Server.SelfTests/`）实测在 macOS 上通过（`All DAFLegacy protocol smoke tests passed.`），但不覆盖 PVF 读取、编码解码、路径归一、随机源降级这四类跨平台风险点。`Script.pvf` 不在仓库内，且体积与版权都不适合入库。

**目标**：

1. 在自测项目的 `PvfScript/` 目录新增用例，覆盖：
   - **合成 PVF 夹具**：按 `DNF_SCRIPT_PACK` 格式生成最小归档（含 CP949 编码的路径表、dword 对齐的加密体），验证索引、解密、路径查找。
   - **编码用例**：CP936 / CP949 / CP51949 / UTF-8 往返。
   - **路径归一用例**：`\` 与 `/` 两种输入的等价性。
   - **目录回退的大小写用例**（R8）。
   - **边界用例**：条目长度非 4 字节倍数、偏移越界、路径表长度非法、`pathLength <= 0`。
2. 在自测项目中新增 `Random/` 目录：覆盖 [06 · 三类随机](06-random-source-managed.md) 的边界——裁决随机的分布与确定性替身（`SeededRandomSource`），以及**密码学随机不得被替换**（`EntranceHandshakeReplies` 的会话密钥必须仍走 `RandomNumberGenerator`）。
3. 自测项目的现有覆盖保持，加入三平台 CI 矩阵。

**判定**：自测项目在三个平台均通过——沿用仓库现有的**自建 runner**（Exe + 退出码，无测试框架，见 [03 · 5.2](03-build-release-ci.md#52-测试运行器风格的取舍)），不使用 `dotnet test`；测试运行时不需要 `Script.pvf`，也不写仓库目录之外的路径。

---

## R11 · 功能面不变

**契约**：迁移不得减少任何现存功能入口。本次重构改变的是**文件位置、项目边界与构建方式**，不改变服务端对外暴露的能力。

**当前**：没有任何清单记录"哪些功能入口必须保持"，因此迁移后无法核对是否丢过东西。

**目标**：下面七类功能面逐项保留。M0 从代码提取基线，每个里程碑后可比对。

| 类别 | 内容 | 数量 | 迁移风险 |
| --- | --- | --- | --- |
| **服务端 CLI** | `--add-cash-all <正数>`（给全部账号加点券）、`--reset-maximum-transfer-roster` | 2 | M4 在组合根加 `--home` 时要动同一段 `args` 解析，可能顺手丢掉既有参数 |
| **Launcher CLI** | `--no-dialog`、`--self-test`、`--install-ijl15`、`--check-d3d9`、`--account-index <N>`、`--account <名>` | 6 | M0 解耦 ijl15 时，容易以为"和 ijl15 相关"的无界面模式可以一并删——`--install-ijl15` 是**无 GUI 安装 ijl15 的唯一入口** |
| **Admin API** | 25 个 API 端点（含 8 个 `/api/admin/*`）+ 6 个静态页面路由 | 31 | 分层不涉及路由，但组合根改动必须保证注册集合不变 |
| **后台服务** | 6 个 `IHostedService`：`EntranceService`、`CharacterDatagramService`、`GameplayDatagramService`、`ProbeService`、`FatigueResetService`、`WeaknessRecoveryService` | 6 | 已在本设计中逐项覆盖（[01 · 4.2](01-target-architecture.md#42-模块职责要点)） |
| **原生测试** | CTest 用例 `ijl15_compatibility` | 1 | 已覆盖（[03 · 5.1](03-build-release-ci.md#51-buildyml-目标结构) 补上 `RUN_TESTS`）；原 `rdseed_native` 随 `DAFLegacy.RandomNative` 移至 Client 层（不进流水线） |
| **数据源模式** | `Script.pvf` 直读 + `SkillScriptPath` 目录回退 | 2 | 已覆盖（`PvfScript/` 的目录回退） |
| **子系统开关** | `server.json` 中 `Entrance` / `CharacterDatagram` / `GameplayDatagram` / `GameProbe` / `Drop` 的 `Enabled` | 5 | 键名与语义不变（[01 · 8.4](01-target-architecture.md#84-两个运行时配置文件归位)） |

### 一处项目现状的文档缺口（非本设计引入）

`ADMIN_API.md` 的接口总览记录 **17 个端点**，而代码注册 **25 个 API 端点**——**8 个 `/api/admin/*` 端点没有被记录**，而它们正是后台界面 `wwwroot/admin/admin.js` 所依赖的：

```text
GET   /api/admin/characters                   GET   /api/admin/dungeons
GET   /api/admin/items                        GET   /api/admin/message-types
POST  /api/admin/broadcast/message            POST  /api/admin/broadcast/popup
POST  /api/admin/characters/{id}/mail         PUT   /api/admin/characters/{id}/dungeons/{dungeonId}
```

（比对时已把路由约束 `{characterId:guid}` 归一为 `{characterId}`；文档侧没有多出的端点。）

**因此不能把 `ADMIN_API.md` 当作功能面的权威清单**——它是不完整的。权威清单是 `Server/DAFLegacy.Server/Program.cs` 里的 31 个 `Map*` 注册。本设计不修改 `ADMIN_API.md`，但建议单独补齐那 8 个端点。

**判定**：不能只看静态计数，要看**运行期可达性**——

| 检查 | 判据 |
| --- | --- |
| 服务端 CLI | `--add-cash-all` 与 `--reset-maximum-transfer-roster` 仍生效（用一个临时数据目录验证） |
| Launcher CLI | 6 个参数仍被识别（`--self-test`、`--check-d3d9` 应能无 GUI 运行并给出结果） |
| Admin API | 服务端启动后 `/health` 与 `/api/status` 正常；后台界面能加载并成功调用至少一个 `/api/admin/*` 端点 |
| 后台服务 | 启动日志中出现 6 个服务的注册（或按各自日志确认已运行） |
| 原生测试 | CTest 用例 `ijl15_compatibility` 在 Windows CI 中实际执行 |
| 数据源模式 | 两种模式各启动一次服务端，`/api/status` 显示的脚本来源不同 |

---

## 契约速查表

| 编号 | 主题 | 当前 | 需要改动 |
| --- | --- | --- | --- |
| R1 | 分层与依赖方向 | 无分层，两层混在一个构建图 | ✅ 新增分层；边界由编译器强制 + 一处 CI 源码扫描 |
| R2 | 原生能力运行期发现 | 编译期引用 + 硬编码路径 | ✅ 原生模块与服务端解耦（保留至 Client 层），规则保留为零实例约束 |
| R3 | 路径契约 | 配置含 `\`，多处散乱 Replace | ✅ 引入 `PathResolver`，配置改 `/` |
| R4 | 编码契约 | 15 处注册散落 15 个文件 | ✅ 收敛到 2 处 |
| R5 | 配置与数据锚点 | 固定 `AppContext.BaseDirectory` | ✅ 支持 `DAFLEGACY_HOME` / `--home` |
| R6 | 网络绑定 | 已全部 `IPAddress.Parse` | ➖ 保持，作为回归契约 |
| R7 | 外部进程与系统集成 | 已规避事件日志；SIGTERM 未验证 | ⚠️ 验证优雅退出 |
| R8 | 文件系统大小写 | 目录回退模式大小写敏感 | ✅ 改启动时建索引 |
| R9 | 降级一致性 | 1 处 catalog 抛异常终止进程 | ✅ 改为降级 + 缺失清单汇总 |
| R10 | 跨平台测试 | 仅协议级冒烟测试 | ✅ 新增 PvfScript/Random 测试 |
| R11 | 功能面不变 | 无功能入口清单，迁移后无法核对 | ✅ 提取基线 + 运行期可达性校验 |
