# 03 · 构建、发布与 CI

- 状态：**已实施**（2026-09-24 标注，重构已落地）
- 上级文档：[00 开档总纲](00-kickoff.md) · [01 目标架构](01-target-architecture.md)

---

## 1. 现状与目标

| 入口 | 现状 | 目标 |
| --- | --- | --- |
| `DFLegacy.Emulator.slnx` | 全部 6 个项目，含 2 个 `.vcxproj`，只能 MSBuild | **唯一解决方案，5 个成员**（2026-09-23 修订：放回 Launcher 与 2 个原生项目，IDE 全量可见；原生标注 `<Build Project="false" />` 不参与构建）；解决方案仅 MSBuild 可构建，服务端跨平台按项目构建 |
| `publish.bat` | 批处理，vswhere 找 MSBuild，产出 `dist/DFLegacy.Server` | **保留**，Windows 发布入口 |
| `publish.sh` | 不存在 | **新增**，Linux/macOS 发布入口，产出同一目录布局 |
| `stop.bat` | `taskkill` 结束 `DFLegacy.Server.exe` | **保留**；服务端须支持 `SIGTERM` 优雅退出（R7） |
| `build-native.ps1` × 2 | PowerShell + 必须 Visual Studio CMake 生成器 | **保留 2 个**（`DFLegacy.Ijl15` 与 `DFLegacy.RandomNative` 的 Windows 原生构建），均由 `patch.bat` 调用；`random` 那份随模块保留至 `Client/DFLegacy.RandomNative/` |
| `.github/workflows/build.yml` | `runs-on: windows-latest` | **单 job**：护栏扫描 + 服务端自测 + `publish.bat` + 服务端包校验（2026-09-23 修订，见 [4.5](#45-运维模型变更2026-09-23-修订流水线只出服务端包)；修订前为 portable 矩阵 + windows-full 双 job） |
| `.github/workflows/release.yml` | `runs-on: windows-latest`，单平台产物 | **服务端双包**：`server-win-x64` 与 `linux-x64`（2026-09-23 修订：产物只含服务端） |

**核心原则**：Windows 现有工作流不删除、不改变行为。跨平台能力是**新增入口**，不是替换。

## 2. 不引入共享构建配置

**不新增 `Directory.Build.props` / `Directory.Build.targets`。** 早期设计曾计划用它们承载共享属性与层边界校验，实测后取消（[01 · 3.3](01-target-architecture.md#33-强制手段编译器承担绝大部分只补一处源码扫描)）：层边界已由编译器无条件覆盖，共享属性则只剩一条真正需要的。

### 唯一需要跨项目注意的属性：`EnableWindowsTargeting`

它**只写在** `Client/DFLegacy.Launcher/DFLegacy.Launcher.csproj` 内：

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

包标签：**合并** = Windows 合并包、**客户端** = 纯客户端包、**服务端** = Windows 纯服务端包、**Linux** = Linux 服务端包。

> **2026-09-23 修订**：发布流水线只产出**服务端**与 **Linux** 两包；**合并**、**客户端**两包不再由 CI 产出，客户端产物由本地 `patch.bat` 发布到 `artifacts\native\launcher\`。下表保留作为各包内容基准。

| 文件 | 构建期来源 | 复制机制（谁负责） | 合并 | 客户端 | 服务端 | Linux | 生效 |
| --- | --- | --- | :-: | :-: | :-: | :-: | --- |
| `DFLegacy.Server.exe` / `DFLegacy.Server` | Server 项目输出 | `dotnet publish` 本体 | ✅ | — | ✅ | ✅ | 现状 |
| `DFLegacy.Launcher.exe` | Launcher 项目输出 | `dotnet publish` 本体 | ✅ | ✅ | — | — | 现状 |
| `server.json` | `Server/DFLegacy.Server/`（项目内） | `Server.csproj` 的 `<Content Update>` | ✅ | — | ✅ | ✅ | M0 归位 |
| `launcher.json` | `Client/DFLegacy.Launcher/`（项目内） | `Launcher.csproj` 的 `<None Update>` | ✅ | ✅ | — | — | M0 归位 |
| `wwwroot/**`（12 个） | `Server/DFLegacy.Server/wwwroot/` | `Server.csproj` 的 `<Content Update="wwwroot/**">` | ✅ | — | ✅ | ✅ | 现状 |
| `Config.ini` | `Client/DFLegacy.Ijl15/`（跨项目取文件） | `Launcher.csproj` 的 `<Content Include>` + `Link` | ✅ | ✅ | — | — | 现状 |
| `ijl15.dll` | `artifacts/native/ijl15/<config>/win-x86/` | `Launcher.csproj` 的可选 `<Content Condition="Exists(...)">` | ✅ | ✅ | — | — | M0 改为可选内容项 |
| `d3d8.dll` | `Client/Patch/` | `Launcher.csproj` 新增 `<Content>` | ✅ | ✅ | — | — | **M5 新增** |
| `LICENSE-d3d8to9.md` | `Client/Patch/` | 同上（许可义务，必须与 `d3d8.dll` 同步） | ✅ | ✅ | — | — | **M5 新增** |
| `data/state.json` | **运行时生成**（非构建产物） | 服务端首次启动时写入 | ✅ | — | ✅ | ✅ | 现状 |

四点必须记住的结论：

1. **客户端侧资产全部由 `Launcher.csproj` 拥有**（`Config.ini`、`ijl15.dll`、`d3d8.dll`、`LICENSE-d3d8to9.md`），服务端侧资产由 `Server.csproj` 拥有。**包内容由项目定义，不写在 CI 里**——这样本地 `publish.bat` 与 CI 产出的包内容一致。`d3d8.dll` 的复制步骤必须落在 `Launcher.csproj`，不要写成 CI 步骤。
2. **唯一的可选 `Content` 项（`ijl15.dll`）以 `Condition="Exists(...)"` 为前提**：原生产物不存在时构建仍须成功。这是 [02 · R2](02-portability-contracts.md#r2--原生能力的运行期发现)"原生能力是可选的"在包内容层面的体现。
3. **`Config.ini` 与 `ijl15.dll` 的来源是 `Ij15` 项目，而复制责任在 `Launcher` 项目**——这是一条跨项目文件依赖（[01 · 2.2](01-target-architecture.md#22-client-层内部的依赖)）。拆 Launcher 的原生 Target 时不要连它一起删掉。
4. **Linux 包不含任何客户端侧资产**：`launcher.json`、`Config.ini`、`ijl15.dll`、`d3d8.dll`、`LICENSE-d3d8to9.md` 都没有 Linux 形态。这是刻意的，不是遗漏。

**建议在 CI 加一步"包内容校验"**：按上表**按包分列**检查每个包里该有的文件**实际存在**，缺失即失败（注意不能把四个包的期望混成一张清单——例如无扩展名的 `DFLegacy.Server` apphost 只存在于 Linux 包，`ijl15.dll` 只存在于含客户端资产的包）。

```yaml
- name: 校验包内容（按包分列）
  shell: pwsh
  run: |
    $expected = @{
      'dist/DFLegacy.Server'  = @('DFLegacy.Server.exe','server.json','launcher.json','wwwroot',
                                  'Config.ini','ijl15.dll','d3d8.dll','LICENSE-d3d8to9.md')
      'dist/server-linux-x64' = @('DFLegacy.Server','server.json','wwwroot')
      'dist/client-win-x64'   = @('DFLegacy.Launcher.exe','launcher.json','Config.ini',
                                  'ijl15.dll','d3d8.dll','LICENSE-d3d8to9.md')
    }
    $failed = $false
    foreach ($dir in $expected.Keys) {
      $missing = $expected[$dir] | Where-Object { -not (Test-Path (Join-Path $dir $_)) }
      if ($missing) { $failed = $true
        Write-Host "::error::$dir 缺失：$($missing -join ', ')" }
    }
    if ($failed) { throw "包内容缺失" }
```

它拦的正是本文档反复强调的那类失败：**`Content` / `None` 项类型写错时，MSBuild 不报错、构建成功、文件却不在包里**（[01 · 8.4](01-target-architecture.md#84-两个运行时配置文件归位)）。只靠"构建通过"无法发现，只能靠实际检查文件存在。

### 3.1 Windows（行为不变）

> **2026-09-23 修订**：发布流水线只产出服务端两包，客户端产物由本地 `patch.bat` 发布（见 [4.5](#45-运维模型变更2026-09-23-修订流水线只出服务端包)）。本节的合并包布局、发布命令与 `-p:BuildIjl15Native=true` 说明为修订前基线，其中"`-p:BuildIjl15Native=true` 是 M0 之后的必需参数"一段已作废。

```text
dist/DFLegacy.Server/
  DFLegacy.Server.exe
  DFLegacy.Launcher.exe          ← 与 Server 同目录，便于 Launcher 按 exe 名拉起服务端（01 · 2.3）
  ijl15.dll                      ← 由 Launcher 安装到客户端目录
  Config.ini                     ← 同上，随 ijl15.dll 一起安装
  d3d8.dll                       ← M5 新增；随包分发（01 · 2.4）
  LICENSE-d3d8to9.md             ← M5 新增；BSD 式条款要求二进制再分发随附版权声明
  server.json
  launcher.json
  wwwroot/
  data/state.json                ← 运行时生成
```

发布命令（与现在等价，仅补上显式 RID 与 M0 后的 `-p:BuildIjl15Native=true`）：

```powershell
dotnet publish Server/DFLegacy.Server/DFLegacy.Server.csproj -c Release -r win-x64 -o dist/DFLegacy.Server
dotnet publish Client/DFLegacy.Launcher/DFLegacy.Launcher.csproj -c Release -r win-x64 -o dist/DFLegacy.Server -p:BuildIjl15Native=true
```

**`-p:BuildIjl15Native=true` 是 M0 之后的必需参数**：Launcher 的原生 Target 解耦为显式 opts-in 后，不传这个属性就不会复制 `ijl15.dll`。M0 之前该步骤由 `Build` 目标自动触发，所以旧命令里没有它——**升级时漏掉这个参数，包会静默缺少 `ijl15.dll`**。

### 3.2 Linux（新增）

```text
dist/server-linux-x64/
  DFLegacy.Server               ← 无扩展名，apphost 可执行
  server.json
  wwwroot/
  data/state.json
```

发布命令：

```bash
dotnet publish Server/DFLegacy.Server/DFLegacy.Server.csproj -c Release -r linux-x64 -o dist/server-linux-x64
```

**这个包不含任何客户端侧资产**（`launcher.json`、`Config.ini`、`ijl15.dll`、`d3d8.dll`、`LICENSE-d3d8to9.md`）——它们没有 Linux 形态，是刻意的排除而非遗漏（[3.0](#30-包内容来源与复制责任)）。

**为什么显式指定 RID**：不指定 RID 的框架依赖发布会产出与平台无关的程序集，构建期无法确定 apphost 该生成哪个平台的可执行文件。显式 RID 让 apphost 生成正确的可执行文件，也让发布布局在构建期即可判定。**发布一律带 RID。**

### 3.3 可移植性：`wwwroot` 与静态资源

`wwwroot/` 通过 `<Content Update="wwwroot/**">` 复制（含 `admin/` 后台界面与 `client/` 的旧版充值页面，共 12 个文件）。**注意现有 csproj 里写的是反斜杠 `wwwroot\**`**——它在 Windows 上可用，但按 [§2 的路径分隔符约定](#路径分隔符约定)不应依赖，M0 顺手改为正斜杠。这部分是纯静态资源，无平台依赖，但需要在两个 RID 的发布中都存在——由于走内容文件机制，无需额外配置。

## 4. 原生项目的构建与消费

原生产物与托管项目的耦合是本次重构的核心。`DFLegacy.RandomNative` 已与服务端解耦并保留至 `Client/DFLegacy.RandomNative/`（服务端随机源降级为纯托管实现，见 [06 · 随机源降级为纯托管实现](06-random-source-managed.md)），它不参与解决方案构建、不进任何流水线交付，本节只展开 `DFLegacy.Ijl15`；原 §4.2（`RandomNative` 的消费方式）与原 §4.4（跨平台化）随服务端解耦一并移除，故小节编号跳过。

### 4.1 产物落地约定

所有原生产物统一落在 `artifacts/native/<module>/<configuration>/<rid>/`：

```text
artifacts/native/
  ijl15/Release/win-x86/ijl15.dll
```

`artifacts/` 已在 `.gitignore` 中，产物不入库。**RID 作为目录层级显式体现**，取代现在"同目录下靠文件名区分"的做法（当前 `artifacts/native/ijl15/$(Configuration)/ijl15.dll` 没有 RID 维度，一旦多平台就会冲突）。

### 4.3 `DFLegacy.Ijl15` 的构建与 Launcher 解耦（M0 改造）

当前 `DFLegacy.Launcher.csproj` 含三个依赖 PowerShell 与 CMake 的 Target，由 `Build` 自动触发：

```text
BuildIjl15           BeforeTargets=Build     → Exec powershell build-native.ps1
CopyIjl15ToOutput    AfterTargets=Build      → 复制到 $(TargetDir)
CopyIjl15ToPublish   AfterTargets=Publish    → 复制到 $(PublishDir)
```

目标改为**消费预构建产物**：

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

<!-- 显式 opts-in，不再由 Build 自动触发 -->
<Target Name="BuildIjl15Native" Condition="'$(BuildIjl15Native)' == 'true'">
  <Exec Command="pwsh -NoLogo -NoProfile -NonInteractive -File &quot;$(Ijl15BuildScript)&quot; -Configuration &quot;$(Configuration)&quot;" />
</Target>
```

**关键点**：隐藏的隐式前置条件（构建 Launcher ⇒ 必须有 PowerShell + CMake + VS）变成显式的、可选的步骤。`publish.bat` 会显式传入 `-p:BuildIjl15Native=true` 以保持一键发布的体验不变；单独构建 Launcher 时则不再需要原生工具链。

> **2026-09-23 修订**：该显式开关随运维模型修订删除（见 [4.5](#45-运维模型变更2026-09-23-修订流水线只出服务端包)），发布客户端产物改为先运行 `patch.bat`。本节保留 M0 改造记录。

`Ijl15ReplacementInstaller` 在 `ijl15.dll` 不存在时的行为需确认为"报告模块不可用"而不是抛未处理异常——该项列入 M5 验收。

## 4.5 运维模型变更（2026-09-23 修订）：流水线只出服务端包

实施后的运营决定取代本文早先"一条流水线全包"的设想：

| 交付物 | 构建方式 | 工具链 |
| --- | --- | --- |
| 服务端包 | `publish.bat`（流水线与本地同一入口），仅 `dotnet publish` 服务端项目 | 仅 .NET SDK |
| 原生模块 `ijl15.dll` / `DFLegacy.RandomNative.dll` | `patch.bat` 手动构建（包装各自的 `build-native.ps1`）；RandomNative 的产物随发布一并复制进客户端包目录 | CMake + MSVC |
| 启动器 / 客户端包 | `patch.bat` 一并 `dotnet publish` Launcher（输出 `dist\DFLegacy.Launcher\`）；产物存在即自动带上插件 | 仅 .NET SDK |

由此落地的结论：**拆分项目后不需要任何"混合编译模式"。** Server.csproj 零
ProjectReference、零原生耦合；Launcher.csproj 零 ProjectReference；解决方案
的两个 `.vcxproj` 成员不参与构建（`<Build Project="false" />`）；`ijl15.dll`
是 `Condition="Exists(...)"` 的可选内容项。
原 M0 引入的 `-p:BuildIjl15Native=true` 混合编译开关随本决定删除
（[§4.3](#43-dflegacyijl15-的构建与-launcher-解耦m0-改造) 中的开关写法已失效），
§3.0/§3.1 中"`-p:BuildIjl15Native=true` 是必需参数"的说明一并作废——
发布客户端包前先跑一次 `patch.bat` 即可。`build.yml` 收敛为单 job：护栏扫描 +
服务端自测 + `publish.bat` + 服务端包内容校验；`release.yml` 只产出服务端双包
（`linux-x64` / `server-win-x64`，只单独构建服务端项目），客户端产物由本地
`patch.bat` 发布到 `artifacts\native\launcher\`。

## 5. CI 矩阵

### 5.1 `build.yml` 目标结构

> **2026-09-23 修订**：`build.yml` 最终收敛为**单 job**（护栏扫描 + 服务端自测 + `publish.bat` + 服务端包校验）。以下 `portable` / `windows-full` 双 job 结构为修订前基线，保留备查。

```yaml
name: build
on:
  push: { branches: [ master, main ] }
  pull_request: { branches: [ master, main ] }
  workflow_dispatch:

jobs:
  portable:
    # 三平台构建 + 自测。注意：它已不再是 R1 的强制手段（单解决方案含 Client 层），
    # 层边界由编译器强制（NU1201 / MSB4278 / NETSDK1100），另有一处源码扫描，见 [01 · 3.3]。
    strategy:
      fail-fast: false
      matrix:
        os: [ ubuntu-latest, windows-latest, macos-latest ]
    runs-on: ${{ matrix.os }}
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: 10.0.x }
      - name: 构建服务端项目（解决方案含 .vcxproj，dotnet CLI 报 MSB4278）
        run: |
          dotnet build Server/DFLegacy.Server/DFLegacy.Server.csproj -c Release
          dotnet build Server/DFLegacy.Server.SelfTests/DFLegacy.Server.SelfTests.csproj -c Release
      - name: 自测
        # 协议 / PvfScript / Random / 业务域用例都在同一个自测项目里，一次执行
        run: dotnet Server/DFLegacy.Server.SelfTests/bin/Release/net10.0/DFLegacy.Server.SelfTests.dll
      - name: 发布 Linux 服务端
        if: matrix.os == 'ubuntu-latest'
        run: dotnet publish Server/DFLegacy.Server/DFLegacy.Server.csproj -c Release -r linux-x64 -o dist/server-linux-x64
      - uses: actions/upload-artifact@v4
        if: matrix.os == 'ubuntu-latest'
        with:
          name: DFLegacy-server-linux-x64
          path: dist/server-linux-x64
          retention-days: 7

  windows-full:
    # 全量构建，含 DFLegacy.Ijl15 原生项目 —— 保持现有覆盖
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: 10.0.x }
      - uses: microsoft/setup-msbuild@v2
      - name: 构建原生项目
        # 原生成员在解决方案中不参与构建（<Build Project="false" />），原生构建是独立步骤。
        # DFLegacy.Ijl15 注册了 CTest 用例（ijl15_compatibility），
        # 现有 CI 并未运行它；调用仓库既有的 build-native.ps1 的 -RunTests 开关一并补齐。
        run: |
          .\Client\DFLegacy.Ijl15\build-native.ps1 -Configuration Release -RunTests
      - name: 构建托管解决方案
        run: msbuild .\DFLegacy.Emulator.slnx /m /restore /p:Configuration=Release
      - name: 冒烟测试
        run: .\Server\DFLegacy.Server.SelfTests\bin\Release\net10.0\DFLegacy.Server.SelfTests.exe
      - name: 发布 Windows 服务端与启动器
        # Launcher 的原生 Target 已在 M0 解耦为显式 opts-in，
        # 因此这里必须传 -p:BuildIjl15Native=true 才会复制 ijl15.dll。
        run: |
          dotnet publish .\Server\DFLegacy.Server\DFLegacy.Server.csproj -c Release -r win-x64 -o .\dist\DFLegacy.Server
          dotnet publish .\Client\DFLegacy.Launcher\DFLegacy.Launcher.csproj -c Release -r win-x64 -o .\dist\DFLegacy.Server -p:BuildIjl15Native=true
      - uses: actions/upload-artifact@v4
        with:
          name: DFLegacy-dist
          path: dist/DFLegacy.Server
          retention-days: 7
```

两点说明：

- **`portable` job 的价值是回归防护**：服务端在 Linux 上确实能构建、能自测、能出包。**层边界另由编译器强制**（`NU1201` / `MSB4278` / `NETSDK1100`），加一处源码扫描覆盖 Z5——所以它不承担护栏职责，也不必承担。
- **`windows-full` job 保持现状覆盖面**，额外补上原生项目的 CTest（当前 workflow 并未运行 `RUN_TESTS`，`build-native.ps1` 里虽有 `-RunTests` 开关但未被 CI 使用）。

### 5.2 测试运行器风格的取舍

仓库现有自测项目（M0 后为 `Server/DFLegacy.Server.SelfTests/`）是自建 runner（`Program.cs` + 退出码，输出 `All DFLegacy protocol smoke tests passed.`），全仓库**没有任何测试框架包引用**。

**决定：自测项目 `Server/DFLegacy.Server.SelfTests/` 沿用同样的自建 runner 风格**，作为 Exe 由 CI 直接执行，不引入 xunit/nunit/MSTest。新增的 PvfScript / Random 用例放在该项目的对应目录下，不另建项目。

- **理由**：与现有约定一致；避免为三个平台的 CI 引入测试框架的还原与适配成本；这些测试本身就是"跑一串用例、失败即非零退出"的形态，不需要框架的发现与并行能力。
- **可选项**：若后续测试量增大需要参数化、夹具生命周期或覆盖率集成，再整体迁移到 xunit 会比现在部分迁移更省事。这是一次性决定，记录在 [04 · 待决问题](04-migration-plan.md#5-待决问题)。

## 6. 发布流程（`release.yml`）

目标结构：按标签触发，产出**分层独立的包**并各自计算校验和。

> **2026-09-23 修订**：`release.yml` 只产出两个**服务端**包（`DFLegacy-<tag>-linux-x64.zip` 与 `DFLegacy-<tag>-server-win-x64.zip`，后者升为必需），只单独构建 `Server/DFLegacy.Server/DFLegacy.Server.csproj`，产物只含服务端；合并包与纯客户端包不再由 CI 产出。下表为修订前的基线设计，保留备查。

| 产物 | 内容 | 构建平台 | 必需 |
| --- | --- | --- | --- |
| `DFLegacy-<tag>-win-x64.zip` | **合并包**：服务端 + 启动器 + `ijl15.dll` + `d3d8.dll`\* + `LICENSE-d3d8to9.md`\* + 配置 + `wwwroot`，同目录 | `windows-latest` | ✅ |
| `DFLegacy-<tag>-linux-x64.zip` | **服务端包**：服务端 + 配置 + `wwwroot` | `ubuntu-latest` | ✅ |
| `DFLegacy-<tag>-client-win-x64.zip` | **纯客户端包**：启动器 + `ijl15.dll` + `d3d8.dll`\* + `LICENSE-d3d8to9.md`\* + `launcher.json` + `Config.ini` | `windows-latest` | ⭕ |
| `DFLegacy-<tag>-server-win-x64.zip` | **纯服务端包**（Windows 形态）：服务端 + 配置 + `wwwroot` | `windows-latest` | ⭕ |
| `*.zip.sha256` | 各包的校验和，格式 `<hash>  <filename>` | 同上 | ✅ |

**两个必需包对应两层**：合并包保持 G4（现有 Windows 用法不变），Linux 服务端包是本次重构的目标产物。另两个可选包在"只需要启动器"或"服务端单独部署"的场景下有用，成本只是多一次 `dotnet publish`——两层已经独立（[01 · 8.2](01-target-architecture.md#82-两层独立打包)、[00 · 3.7](00-kickoff.md#37-两层能否独立打包实测)），出这四个包不需要任何额外解耦工作。

**为什么保留合并包为主产物**：现有用法是"把发布目录放进客户端目录内（与 `DNF.exe` 同级），运行 Launcher 由它按需拉起服务端"。分层后若只出两个独立包，这个用法就失效了。合并包同时保证按标签发布的既有链接与文档描述继续成立。

保持不变的部分：标签解析逻辑（`v*` 前缀校验）、`softprops/action-gh-release@v2` 创建/更新 release、校验和文件格式。

新增注意点：

- 各平台的产物在同一个 release 中发布，但**构建 job 相互独立**，任一失败不影响另一个已产出的包——使用 `fail-fast: false`。
- **\* `d3d8.dll` 与 `LICENSE-d3d8to9.md` 由 M5 新增的复制步骤放入包中**：它们来自 `Client/Patch/`，**当前 `publish.bat` 与两个 workflow 都不复制**，所以基线里没有。复制点在 `Launcher.csproj` 的 `Content` 项（不要写在 CI 里），每个文件的具体来源与责任见 [3.0](#30-包内容来源与复制责任) 表。
- **每个包的完整文件清单以 [3.0](#30-包内容来源与复制责任) 表为准**，本节表格只列包的用途与必需性。

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

- **是否 `--self-contained`**：参照项目用 `--self-contained true -p:PublishSingleFile=true`，产出单文件、目标机无需 .NET 运行时。DFLegacy 现在的发布是**框架依赖**（需要 .NET 10 运行时）。**建议保持框架依赖**——改变部署形态是独立决定，不应混在可移植性重构里。
- **`PUBLISH_ALL=1`**：本次只在 CI 里用到（矩阵各自发布自己的 RID），保留该开关是为了本地一次性产出全部包。
- **覆盖 `osx-*`**：`detect_rid` 覆盖 macOS 是为了让 `publish.sh` 在开发机上可用，**不意味着发布 macOS 产物**——macOS 在本次设计中只是开发平台（[04 · Q3](04-migration-plan.md#5-待决问题)）。

## 7. 迁移期的构建兼容性

M0 完成**单解决方案改造**（`.vcxproj` 移出 + Launcher 解耦）之前与之后，构建入口不同：

| 阶段 | 场景 | 使用入口 |
| --- | --- | --- |
| M0 之前 | Windows 全量构建（含原生） | `msbuild DFLegacy.Emulator.slnx`（`DFLegacy.Ijl15` 经 `.vcxproj` 一并构建） |
| M0 之前 | Linux/macOS 验证 | 逐个项目 `dotnet build` |
| **M0 之后** | Windows 全量构建 | `msbuild DFLegacy.Emulator.slnx`（仅托管，原生成员 `<Build Project="false" />`）+ `patch.bat`（原生与 Launcher，2026-09-23 修订） |
| **M0 之后** | 三平台验证与发布 | 按项目 `dotnet build`/`publish` 服务端项目（产物只含服务端；2026-09-23 修订取代原"解决方案 + 分层 publish"方案） |
| **M0 之后** | Windows 一键发布 | 服务端包走 `publish.bat`（仅 .NET SDK）；客户端产物走 `patch.bat`（2026-09-23 修订，见 [4.5](#45-运维模型变更2026-09-23-修订流水线只出服务端包)） |

**M0 是关键分界点**，因为它同时改变"解决方案含哪些项目"与"原生如何被构建"两件事。这带来一个必须交代清楚的行为变化：

> `.vcxproj` 移出后，`msbuild .\DFLegacy.Emulator.slnx` **不再构建原生项目**。习惯用该命令的开发者会以为原生已经构建好了。原生与客户端产物由 `patch.bat` 显式补齐（2026-09-23 修订后 `publish.bat` 收敛为仅服务端发布），直接用 msbuild 的场景需要在文档中说明——这是本次重构中唯一的行为退化，已记录在 [00 · 风险摘要](00-kickoff.md)。

**不做的**：不改名任何现有文件（`DFLegacy.Emulator.slnx` 保持原名），不调整 `dist/` 的输出布局（合并包仍落在 `dist/DFLegacy.Server`），不改动 `README.md`。
