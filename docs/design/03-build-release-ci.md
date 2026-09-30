# 03 · 构建、发布与 CI

- 状态：**已实施**（2026-09-24 标注，重构已落地）
- 上级文档：[00 开档总纲](00-kickoff.md) · [01 目标架构](01-target-architecture.md)

---

## 1. 现状与目标

| 入口 | 现状 | 目标（即现行形态） |
| --- | --- | --- |
| `DAFLegacy.Emulator.slnx` | 全部 6 个项目，含 2 个 `.vcxproj`，只能 MSBuild | **唯一解决方案**：服务端主项目、MCP 模块、自测、Launcher 与两个原生项目，IDE 全量可见；原生项目标注 `<Build Project="false" />` 不参与构建；解决方案仅 MSBuild 可构建，服务端跨平台按项目构建 |
| `publish.bat` | 批处理，vswhere 找 MSBuild，产出 `dist/DAFLegacy.Server` | **保留**，Windows 服务端发布入口（仅 .NET SDK；客户端产物改走 `patch.bat`） |
| `publish.sh` | 不存在 | **新增**，Linux/macOS 发布入口（自探测 RID，产出 `dist/server-<rid>`） |
| `stop.bat` | `taskkill` 结束 `DAFLegacy.Server.exe` | **保留**；服务端须支持 `SIGTERM` 优雅退出（R7） |
| `build-native.ps1` × 2 | PowerShell + 必须 Visual Studio CMake 生成器 | **保留 2 个**（`DAFLegacy.Ijl15` 与 `DAFLegacy.RandomNative` 的 Windows 原生构建），均由 `patch.bat` 调用；`random` 那份随模块保留至 `Client/DAFLegacy.RandomNative/` |
| `.github/workflows/build.yml` | `runs-on: windows-latest` | **单 job**（`windows-latest`）：护栏扫描 + 服务端自测构建与执行，不做发布、不产出包产物（见 [5.1](#51-buildyml-目标结构) 与 [4.5](#45-运维模型流水线只出服务端包)） |
| `.github/workflows/release.yml` | `runs-on: windows-latest`，单平台产物 | **Linux 服务端单包** `linux-x64`：CI 发布的唯一交付物；Windows 服务端包由本地 `publish.bat` 产出（见 [6](#6-发布流程releaseyml)） |

**核心原则**：Windows 现有工作流不删除、不改变行为。跨平台能力是**新增入口**，不是替换。

## 2. 不引入共享构建配置

**不新增 `Directory.Build.props` / `Directory.Build.targets`。** 层边界已由编译器无条件覆盖（[01 · 3.3](01-target-architecture.md#33-强制手段编译器承担绝大部分只补一处源码扫描)），跨项目共享的属性只剩一条真正需要的（`EnableWindowsTargeting`，见下）；共享配置带来的"改一处影响全部项目"没有对应收益。不引入共享构建配置这一点由 `build.yml` 的护栏扫描显式把守：仓库内出现 `Directory.Build.props` / `Directory.Build.targets` 即失败。

### 唯一需要跨项目注意的属性：`EnableWindowsTargeting`

它**只写在** `Client/DAFLegacy.Launcher/DAFLegacy.Launcher.csproj` 内：

```xml
<PropertyGroup>
  <!-- 让 Launcher（net10.0-windows + WinForms）能在非 Windows 上交叉编译。
       刻意不放到仓库根：全局打开会让 Server 层的 WinForms/WPF 违规在 Linux 上静默通过。 -->
  <EnableWindowsTargeting>true</EnableWindowsTargeting>
</PropertyGroup>
```

**不要把它提升到仓库根**——它是 `NETSDK1100` 门禁的开关，全局打开等于关掉 Z2 的一半护栏。

### 其余属性保持各项目自声明

`Nullable` / `ImplicitUsings` / `LangVersion` 等在 3 个项目里各自重复声明，**不为省这几行引入一个间接层**——共享 props 的代价是"改一处影响全部项目"，在项目数只有 3 个时不划算。`TreatWarningsAsErrors` 暂不开启（[04 · Q8](04-migration-plan.md#5-待决问题)）。

### 路径分隔符约定

MSBuild 属性与 `Include` 中的路径**统一使用 `/`**。两个平台都接受正斜杠，而反斜杠在 Unix 上依赖 MSBuild 的归一化行为——虽然当前实际可用，但不应依赖。这一条同时适用于 `.csproj` 与 `.ps1` 中的 MSBuild 调用参数。**现有 `Server.csproj` 的 `wwwroot\**` 是反斜杠写法，M0 顺手改为正斜杠。**

## 3. 发布布局

### 3.0 包内容来源与复制责任

**这张表是"分发包里每个文件从哪来、由谁复制"的唯一依据。** 缺了它，就会出现"文档说包里有某文件、实际没人复制"的情况——`d3d8.dll` 正是这样变成孤儿的（[01 · 2.4](01-target-architecture.md#24-补丁资产-clientpatchd3d8dll)）。

包标签：**合并** = Windows 合并包、**客户端** = 纯客户端包、**服务端** = Windows 纯服务端包、**Linux** = Linux 服务端包。CI 发布流水线（`release.yml`）只产出 **Linux** 服务端包；**服务端**（Windows）包由本地 `publish.bat`、**合并**与**客户端**形态所需的客户端产物由本地 `patch.bat`（发布到 `artifacts\native\launcher\`）产出，下表是各包内容的统一基准。

| 文件 | 构建期来源 | 复制机制（谁负责） | 合并 | 客户端 | 服务端 | Linux | 生效 |
| --- | --- | --- | :-: | :-: | :-: | :-: | --- |
| `DAFLegacy.Server.exe` / `DAFLegacy.Server` | Server 项目输出 | `dotnet publish` 本体 | ✅ | — | ✅ | ✅ | 现状 |
| `DAFLegacy.Launcher.exe` | Launcher 项目输出 | `dotnet publish` 本体 | ✅ | ✅ | — | — | 现状 |
| `server.json` | `Server/DAFLegacy.Server/`（项目内） | `Server.csproj` 的 `<Content Update>` | ✅ | — | ✅ | ✅ | M0 归位 |
| `launcher.json` | `Client/DAFLegacy.Launcher/`（项目内） | `Launcher.csproj` 的 `<None Update>` | ✅ | ✅ | — | — | M0 归位 |
| `wwwroot/**`（12 个） | `Server/DAFLegacy.Server/wwwroot/` | `Server.csproj` 的 `<Content Update="wwwroot/**">` | ✅ | — | ✅ | ✅ | 现状 |
| `Config.ini` | `Client/DAFLegacy.Ijl15/`（跨项目取文件） | `Launcher.csproj` 的 `<Content Include>` + `Link` | ✅ | ✅ | — | — | 现状 |
| `ijl15.dll` | `artifacts/native/ijl15/<config>/win-x86/` | `Launcher.csproj` 的可选 `<Content Condition="Exists(...)">` | ✅ | ✅ | — | — | M0 改为可选内容项 |
| `d3d8.dll` | `Client/Patch/` | `Launcher.csproj` 新增 `<Content>` | ✅ | ✅ | — | — | **M5 新增** |
| `LICENSE-d3d8to9.md` | `Client/Patch/` | 同上（许可义务，必须与 `d3d8.dll` 同步） | ✅ | ✅ | — | — | **M5 新增** |
| `data/state.json` | **运行时生成**（非构建产物） | 服务端首次启动时写入 | ✅ | — | ✅ | ✅ | 现状 |

四点必须记住的结论：

1. **客户端侧资产全部由 `Launcher.csproj` 拥有**（`Config.ini`、`ijl15.dll`、`d3d8.dll`、`LICENSE-d3d8to9.md`），服务端侧资产由 `Server.csproj` 拥有。**包内容由项目定义，不写在 CI 里**——这样本地 `publish.bat` 与 CI 产出的包内容一致。`d3d8.dll` 的复制步骤必须落在 `Launcher.csproj`，不要写成 CI 步骤。
2. **唯一的可选 `Content` 项（`ijl15.dll`）以 `Condition="Exists(...)"` 为前提**：原生产物不存在时构建仍须成功。这是 [02 · R2](02-portability-contracts.md#r2--原生能力的运行期发现)"原生能力是可选的"在包内容层面的体现。
3. **`Config.ini` 与 `ijl15.dll` 的来源是 `Ij15` 项目，而复制责任在 `Launcher` 项目**——这是一条跨项目文件依赖（[01 · 2.2](01-target-architecture.md#22-client-层内部的依赖)）。拆 Launcher 的原生 Target 时不要连它一起删掉。
4. **Linux 包不含任何客户端侧资产**：`launcher.json`、`Config.ini`、`ijl15.dll`、`d3d8.dll`、`LICENSE-d3d8to9.md` 都没有 Linux 形态。这是刻意的，不是遗漏。

**CI 已落地包内容校验**：`release.yml` 在打包前按上表的 **Linux** 列检查 `DAFLegacy.Server`（apphost）、`server.json`、`wwwroot` 实际存在，缺失即失败。校验必须**按包分列**执行，不能把各包的期望混成一张清单——例如无扩展名的 `DAFLegacy.Server` apphost 只存在于 Linux 包，`ijl15.dll` 只存在于含客户端资产的包。本地 `publish.bat` / `patch.bat` 产出的包按同一张表逐项核对。它拦的正是本文档反复强调的那类失败：**`Content` / `None` 项类型写错时，MSBuild 不报错、构建成功、文件却不在包里**（[01 · 8.4](01-target-architecture.md#84-两个运行时配置文件归位)）。只靠"构建通过"无法发现，只能靠实际检查文件存在。

### 3.1 Windows（本地脚本产出，行为不变）

Windows 用户的部署方式不变（服务端与 Launcher 同目录、解到客户端目录内即可用），两个形态分别由本地脚本产出：

**服务端包**（`publish.bat`，仅 .NET SDK，一键执行同一条命令）：

```text
dist/DAFLegacy.Server/
  DAFLegacy.Server.exe
  server.json
  wwwroot/
  data/state.json                ← 运行时生成
```

```powershell
dotnet publish Server/DAFLegacy.Server/DAFLegacy.Server.csproj -c Release -r win-x64 -o dist/DAFLegacy.Server
```

**客户端产物**（`patch.bat`：CMake + MSVC 先构建 `ijl15.dll` 与 `DAFLegacy.RandomNative.dll`，再发布 Launcher）落在 `artifacts\native\launcher\`，内容为 `DAFLegacy.Launcher.exe`、`ijl15.dll`、`Config.ini`、`d3d8.dll`、`LICENSE-d3d8to9.md`、`README-d3d8to9.md`、`LICENSE`、`launcher.json`。原生产物先于 Launcher 发布构建完成，`Launcher.csproj` 的可选内容项（`Condition="Exists(...)"`）自动把 `ijl15.dll` 带进产物。

**合并部署形态**由两份本地产物组合承载：把服务端发布目录与 `artifacts\native\launcher\` 的内容放进客户端目录内（服务端与 Launcher 同目录），Launcher 即按文件名拉起服务端（[01 · 2.3](01-target-architecture.md#23-与-server-层的跨层联系)）。

### 3.2 Linux（新增）

```text
dist/server-linux-x64/
  DAFLegacy.Server               ← 无扩展名，apphost 可执行
  server.json
  wwwroot/
  data/state.json
```

发布命令：

```bash
dotnet publish Server/DAFLegacy.Server/DAFLegacy.Server.csproj -c Release -r linux-x64 -o dist/server-linux-x64
```

**这个包不含任何客户端侧资产**（`launcher.json`、`Config.ini`、`ijl15.dll`、`d3d8.dll`、`LICENSE-d3d8to9.md`）——它们没有 Linux 形态，是刻意的排除而非遗漏（[3.0](#30-包内容来源与复制责任)）。

**为什么显式指定 RID**：不指定 RID 的框架依赖发布会产出与平台无关的程序集，构建期无法确定 apphost 该生成哪个平台的可执行文件。显式 RID 让 apphost 生成正确的可执行文件，也让发布布局在构建期即可判定。**发布一律带 RID。**

### 3.3 可移植性：`wwwroot` 与静态资源

`wwwroot/` 通过 `<Content Update="wwwroot/**">` 复制（含 `admin/` 后台界面与 `client/` 的旧版充值页面，共 12 个文件）。**注意现有 csproj 里写的是反斜杠 `wwwroot\**`**——它在 Windows 上可用，但按 [§2 的路径分隔符约定](#路径分隔符约定)不应依赖，M0 顺手改为正斜杠。这部分是纯静态资源，无平台依赖，但需要在两个 RID 的发布中都存在——由于走内容文件机制，无需额外配置。

## 4. 原生项目的构建与消费

原生产物与托管项目的耦合是本次重构的核心。`DAFLegacy.RandomNative` 已与服务端解耦并保留至 `Client/DAFLegacy.RandomNative/`（服务端随机源降级为纯托管实现，见 [06 · 随机源降级为纯托管实现](06-random-source-managed.md)），它不参与解决方案构建、不进任何流水线交付，本节只展开 `DAFLegacy.Ijl15`；原 §4.2（`RandomNative` 的消费方式）与原 §4.4（跨平台化）随服务端解耦一并移除，故小节编号跳过。

### 4.1 产物落地约定

所有原生产物统一落在 `artifacts/native/<module>/<configuration>/<rid>/`：

```text
artifacts/native/
  ijl15/Release/win-x86/ijl15.dll
```

`artifacts/` 已在 `.gitignore` 中，产物不入库。**RID 作为目录层级显式体现**，取代现在"同目录下靠文件名区分"的做法（当前 `artifacts/native/ijl15/$(Configuration)/ijl15.dll` 没有 RID 维度，一旦多平台就会冲突）。

### 4.3 `DAFLegacy.Ijl15` 的构建与 Launcher 解耦（M0 改造）

`DAFLegacy.Launcher.csproj` 原有三个依赖 PowerShell 与 CMake、由 `Build` 自动触发的 Target（`BuildIjl15` / `CopyIjl15ToOutput` / `CopyIjl15ToPublish`），M0 改造时整体删除；原生构建成为独立的、可选的前置步骤，对原生产物的消费收敛为一个可选内容项：

```xml
<PropertyGroup>
  <Ijl15Binary>$(MSBuildThisFileDirectory)../../artifacts/native/ijl15/$(Configuration)/win-x86/ijl15.dll</Ijl15Binary>
</PropertyGroup>

<ItemGroup>
  <Content Include="$(Ijl15Binary)" Link="ijl15.dll" Condition="Exists('$(Ijl15Binary)')">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    <CopyToPublishDirectory>PreserveNewest</CopyToPublishDirectory>
  </Content>
</ItemGroup>
```

**关键点**：隐藏的隐式前置条件（构建 Launcher ⇒ 必须有 PowerShell + CMake + VS）被移除，Launcher 的构建只需 .NET SDK。原生产物存在即随输出与发布自动带上 `ijl15.dll`，不存在则构建照常成功、只是包里没有该 DLL。原生构建由 `patch.bat` 承担（调用 `Client/DAFLegacy.Ijl15/build-native.ps1`，CMake）；发布客户端产物前先运行一次 `patch.bat`，Launcher 发布即自动带上插件。

`Ijl15ReplacementInstaller` 在 `ijl15.dll` 不存在时的行为需确认为"报告模块不可用"而不是抛未处理异常——该项列入 M5 验收。

## 4.5 运维模型：流水线只出服务端包

交付物的构建职责划分如下：

| 交付物 | 构建方式 | 工具链 |
| --- | --- | --- |
| 服务端包（Windows 形态） | `publish.bat`，仅 `dotnet publish` 服务端项目 | 仅 .NET SDK |
| 服务端包（Linux 形态） | `release.yml`（CI），只单独构建服务端项目 | 仅 .NET SDK |
| 原生模块 `ijl15.dll` / `DAFLegacy.RandomNative.dll` | `patch.bat` 构建（包装各自的 `build-native.ps1`）；RandomNative 的产物随发布一并复制进客户端产物目录 | CMake + MSVC |
| 启动器 / 客户端产物 | `patch.bat` 一并 `dotnet publish` Launcher（输出 `artifacts\native\launcher\`）；原生产物在位即自动带上插件 | 仅 .NET SDK |

**拆分项目后不需要任何"混合编译模式"。** Server.csproj 零 ProjectReference、零原生耦合；Launcher.csproj 零 ProjectReference；解决方案的两个 `.vcxproj` 成员不参与构建（`<Build Project="false" />`）；`ijl15.dll` 是 `Condition="Exists(...)"` 的可选内容项（见 [§4.3](#43-daflegacyijl15-的构建与-launcher-解耦m0-改造)）。发布客户端产物前先运行一次 `patch.bat`，Launcher 发布即自动带上插件。

流水线的职责面由此收敛：

- `build.yml` 只做护栏扫描 + 服务端自测构建与执行——push/PR 流水线不产出任何包产物，包构建一律由本地 `publish.bat` / `patch.bat` 或 `release.yml` 承担。自测项目经 ProjectReference 传递编译服务端，编译验证不受影响（见 [5.1](#51-buildyml-目标结构)）。
- `release.yml` 是 CI 发布的唯一入口，只产出 Linux 服务端包（`DAFLegacy-<tag>-linux-x64.zip`，见 [6](#6-发布流程releaseyml)）。需要 Windows 形态时在本地执行 `publish.bat`（产物与 [3.0](#30-包内容来源与复制责任) 的服务端列一致）；客户端产物由 `patch.bat` 发布到 `artifacts\native\launcher\`。

## 5. CI 矩阵

### 5.1 `build.yml` 目标结构

`build.yml` 为单 job（`windows-latest`）：护栏扫描 + 服务端自测构建与执行，不做发布、不产出任何包产物——包构建一律由本地 `publish.bat` / `patch.bat` 或 `release.yml` 承担（见 [4.5](#45-运维模型流水线只出服务端包)）。自测项目经 ProjectReference 传递编译服务端，编译验证不受影响。

```yaml
name: build
on:
  push: { branches: [ master, main ] }
  pull_request: { branches: [ master, main ] }
  workflow_dispatch:

jobs:
  server:
    # 流水线只做护栏扫描与服务端自测，不做发布。层边界由编译器强制
    # （NU1201 / MSB4278 / NETSDK1100），另保留两处廉价源码扫描防回归。
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: 10.0.x }
      - name: 层边界护栏检查（Z5 源码扫描 + EnableWindowsTargeting 位置）
        shell: bash
        # ① Server/ 下出现 Windows 系统库 DllImport 即失败（Z5）；
        # ② 仓库内出现 Directory.Build.props / Directory.Build.targets 即失败；
        # ③ EnableWindowsTargeting 只允许出现在
        #    Client/DAFLegacy.Launcher/DAFLegacy.Launcher.csproj。
        run: ...（完整脚本见 .github/workflows/build.yml）
      - name: Build and self-test
        run: |
          dotnet build .\Server\DAFLegacy.Server.SelfTests\DAFLegacy.Server.SelfTests.csproj -c Release
          .\Server\DAFLegacy.Server.SelfTests\bin\Release\net10.0\DAFLegacy.Server.SelfTests.exe
```

两点说明：

- **护栏扫描承担源码级检查**：Z5（Windows 系统库 `DllImport`）与 `EnableWindowsTargeting` 的位置约束由 job 内两处廉价扫描把守并防回归；其余层边界规则（`NU1201` / `MSB4278` / `NETSDK1100`）由编译器无条件强制，job 不承担三平台矩阵与产物交付职责。
- **原生项目的 CTest 不进 CI**：CI 不构建原生项目。`build-native.ps1` 的 `-RunTests` 开关由本地 `patch.bat -RunTests` 转发执行（`patch.bat` 把额外开关转发给两个 `build-native.ps1`）。

### 5.2 测试运行器风格的取舍

仓库现有自测项目（M0 后为 `Server/DAFLegacy.Server.SelfTests/`）是自建 runner（`Program.cs` + 退出码，输出 `All DAFLegacy protocol smoke tests passed.`），全仓库**没有任何测试框架包引用**。

**决定：自测项目 `Server/DAFLegacy.Server.SelfTests/` 沿用同样的自建 runner 风格**，作为 Exe 由 CI 直接执行，不引入 xunit/nunit/MSTest。新增的 PvfScript / Random 用例放在该项目的对应目录下，不另建项目。

- **理由**：与现有约定一致；避免为三个平台的 CI 引入测试框架的还原与适配成本；这些测试本身就是"跑一串用例、失败即非零退出"的形态，不需要框架的发现与并行能力。
- **可选项**：若后续测试量增大需要参数化、夹具生命周期或覆盖率集成，再整体迁移到 xunit 会比现在部分迁移更省事。这是一次性决定，记录在 [04 · 待决问题](04-migration-plan.md#5-待决问题)。

## 6. 发布流程（`release.yml`）

按标签触发（`v*` 前缀校验，`workflow_dispatch` 可手动指定标签），CI 发布的唯一交付物是 **Linux 服务端单包**，附校验和：

| 产物 | 内容 | 构建平台 | 必需 |
| --- | --- | --- | --- |
| `DAFLegacy-<tag>-linux-x64.zip` | **Linux 服务端包**：服务端 apphost + 配置 + `wwwroot` | `ubuntu-latest` | ✅ |
| `*.zip.sha256` | 校验和，格式 `<hash>  <filename>` | 同上 | ✅ |

Windows 服务端包由本地 `publish.bat` 产出（包内容与 [3.0](#30-包内容来源与复制责任) 的服务端列一致），客户端产物由本地 `patch.bat` 产出（`artifacts\native\launcher\`）——两者不进 CI。现有 Windows 合并部署形态（服务端与 Launcher 同目录）由这两份本地产物组合承载（见 [3.1](#31-windows本地脚本产出行为不变)）。

流程要点：

- `linux-server` job 只单独构建 `Server/DAFLegacy.Server/DAFLegacy.Server.csproj`，发布到 `dist/server-linux-x64`（`-r linux-x64`，框架依赖）。
- 打包前做**包内容校验**：`DAFLegacy.Server`、`server.json`、`wwwroot` 缺失即失败。
- 产物压缩为 ZIP 并生成 `.sha256`，经 artifact 上传；`release` job 以下载的产物创建/更新 GitHub Release（`softprops/action-gh-release@v2`）。
- 每个包的完整文件清单以 [3.0](#30-包内容来源与复制责任) 表为准，本节表格只列包的用途与必需性。

### 6.1 `publish.sh` 的 RID 处理

参照 `ServerS4A21/publish.sh` 的做法，`publish.sh` 应当**自己探测当前平台的 RID**，而不是硬编码 `linux-x64`：

```bash
detect_rid() {
  local os arch
  os="$(uname -s)"; arch="$(uname -m)"
  case "$os" in
    Darwin) case "$arch" in arm64) echo osx-arm64 ;; x86_64) echo osx-x64 ;; esac ;;
    Linux)  case "$arch" in x86_64) echo linux-x64 ;; aarch64|arm64) echo linux-arm64 ;; esac ;;
    MINGW*|MSYS*|CYGWIN*) echo win-x64 ;;
  esac
}

# 默认发布当前平台；PUBLISH_ALL=1 时发布全部目标
if [[ "${PUBLISH_ALL:-}" == "1" ]]; then
  for rid in win-x64 linux-x64 linux-arm64; do publish_rid "$rid"; done
else
  publish_rid "$(detect_rid)"
fi
```

三点取舍：

- **是否 `--self-contained`**：参照项目用 `--self-contained true -p:PublishSingleFile=true`，产出单文件、目标机无需 .NET 运行时。DAFLegacy 的发布是**框架依赖**（需要 .NET 10 运行时）。**保持框架依赖**——改变部署形态是独立决定，不应混在可移植性重构里。
- **`PUBLISH_ALL=1`**：本地一次性产出全部目标（`win-x64` / `linux-x64` / `linux-arm64`）。CI 不经 `publish.sh`——`release.yml` 直接执行单一 RID 的 `dotnet publish`。
- **覆盖 `osx-*`**：`detect_rid` 覆盖 macOS 是为了让 `publish.sh` 在开发机上可用，**不意味着发布 macOS 产物**——macOS 在本次设计中只是开发平台（[04 · Q3](04-migration-plan.md#5-待决问题)）。

## 7. 迁移期的构建兼容性

M0 完成**单解决方案改造**（`.vcxproj` 移出 + Launcher 解耦）之前与之后，构建入口不同：

| 阶段 | 场景 | 使用入口 |
| --- | --- | --- |
| M0 之前 | Windows 全量构建（含原生） | `msbuild DAFLegacy.Emulator.slnx`（`DAFLegacy.Ijl15` 经 `.vcxproj` 一并构建） |
| M0 之前 | Linux/macOS 验证 | 逐个项目 `dotnet build` |
| **M0 之后** | Windows 全量构建 | `msbuild DAFLegacy.Emulator.slnx`（仅托管，原生成员 `<Build Project="false" />`）+ `patch.bat`（原生与 Launcher） |
| **M0 之后** | 三平台验证与发布 | 按项目 `dotnet build`/`publish` 服务端项目（产物只含服务端） |
| **M0 之后** | Windows 一键发布 | 服务端包走 `publish.bat`（仅 .NET SDK）；客户端产物走 `patch.bat`（见 [4.5](#45-运维模型流水线只出服务端包)） |

**M0 是关键分界点**，因为它同时改变"解决方案含哪些项目"与"原生如何被构建"两件事。这带来一个必须交代清楚的行为变化：

> `.vcxproj` 移出后，`msbuild .\DAFLegacy.Emulator.slnx` **不再构建原生项目**。习惯用该命令的开发者会以为原生已经构建好了。原生与客户端产物由 `patch.bat` 显式补齐（`publish.bat` 仅发布服务端），直接用 msbuild 的场景需要在文档中说明——这是本次重构中唯一的行为退化，已记录在 [00 · 风险摘要](00-kickoff.md)。

**不做的**：不改名任何现有文件（`DAFLegacy.Emulator.slnx` 保持原名），不调整 `dist/` 的输出布局（Windows 服务端包仍落在 `dist/DAFLegacy.Server`），不改动 `README.md`。
