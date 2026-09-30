# 04 · 迁移路线、验收与风险

- 状态：**已实施**（2026-09-24 标注，重构已落地）
- 上级文档：[00 开档总纲](00-kickoff.md) · [01 目标架构](01-target-architecture.md) · [02 可移植性契约](02-portability-contracts.md) · [03 构建与发布](03-build-release-ci.md)

---

## 1. 执行原则

1. **每一步都可独立发布、独立回滚。** 每个里程碑是一个独立 PR，合入后 `main` 必须处于可发布状态。
2. **层边界靠编译器，唯一需要自己守的是 `EnableWindowsTargeting` 的位置。** Z1–Z3（跨层引用、`.vcxproj` 引用、WinForms/WPF）由 `NU1201` / `MSB4278` / `NETSDK1100` 无条件覆盖，M0 完成后立刻生效、不需要补机制。但**一旦有人把 `EnableWindowsTargeting` 提到仓库根，Z2 的 WinForms/WPF 分支就静默失效**——M0 验收里有一项专门确认它只出现在 `Client/DAFLegacy.Launcher/DAFLegacy.Launcher.csproj` 内。
3. **搬代码与改行为严格分开。** M3 是纯搬运 + 测试补齐，M2 是随机源的实现替换（对外契约与调用点不动），**不允许顺手修改逻辑**。任何行为变更单独提交，便于二分定位。
4. **Windows 行为是回归基准。** 每个里程碑都要在 Windows 上验证"改动前后一致"，而不是只看 Linux 上"能跑起来"。
5. **降级优于崩溃，但不静默。** 缺数据时服务端必须继续启动，同时必须把缺失项以 `Error` 级别和汇总清单暴露出来。

## 2. 阶段总览

| 里程碑 | 主题 | 交付物 | 依赖 | 承诺 |
| --- | --- | --- | --- | --- |
| **M0** | 结构重组与构建收敛 | 目录三极重组（`Server/`/`Client/`）、Protocol 并入主项目、单解决方案、Launcher 解耦、配置文件归位 | — | ✅ 承诺 |
| **M1** | 层边界护栏 | CI 源码扫描（Z5）、三平台矩阵、功能面与产物基线 | M0 | ✅ 承诺 |
| **M2** | 随机源收拢 | 服务端随机源改为纯托管单一实现（`Server/DAFLegacy.Server/Random/`）；服务端与 `DAFLegacy.RandomNative` 解耦（模块保留至 `Client/`），删除 csproj 的编译期耦合 | M1 | ✅ 承诺 |
| **M3** | PVF 脚本模块收拢 | `Server/DAFLegacy.Server/PvfScript/` 补齐编码契约与路径归一；自测项目对应目录 | M2 | ✅ 承诺 |
| **M4** | 路径与数据契约 | `PathResolver`、`DAFLEGACY_HOME`、降级一致性、SIGTERM | M3 | ✅ 承诺 |
| **M5** | 独立打包与发布 | `publish.sh`（RID 探测）、CI 发布 Linux 服务端包 | M4 | ✅ 承诺 |

**M0 是本计划中最重的一步**，它把四件互相牵连的事捆在一起（目录重组、解决方案成员变化、Launcher 解耦、配置文件归位）。四件事必须在同一提交内完成——只做一半会落到半旧半新的路径上，任何构建都失败。后续里程碑则都是小范围的语义改动。

## 3. 各阶段任务与验收

### M0 · 结构重组与构建收敛

**目标**：完成三级目录重组（层目录 / 项目目录 / 模块目录），并把全仓库收敛为一个三平台可构建的解决方案。**这一步不改任何业务逻辑、不改命名空间**——只移动文件与修改构建配置。

**任务**

- 提交本文档集（`docs/design/00`–`04`）。
- `.gitignore`：`docs/` 不在忽略之列，`docs/design/**` 随仓库跟踪；新增内容一律写入 `docs/design/`（[00 · 两个约定](00-kickoff.md)）。
- **目录三极重组**（纯 `git mv`，[01 · 10.1](01-target-architecture.md#101-完整影响清单) 给出完整影响清单）：
  - `src/DAFLegacy.Server/` → `Server/DAFLegacy.Server/`，其 79 个 `.cs` 按 [01 · 4.1](01-target-architecture.md#41-daflegacyserver主项目) 的机械规则归位：
    - **`GameWorld/`**（25 个）：全部 `*Catalog` / `*Definition`，**平铺**，即从 PVF 解析出的只读定义与目录。
    - **`Game/<业务域>/`**（39 个，9 个域）：运行时业务规则，按业务域分目录——`Dungeon/`(11)、`Inventory/`(9)、`Quest/`(8)、`Compound/`(3)、`Shop/`(3)、`ItemUpgrade/`(2)、`Account/`(1)、`Creature/`(1)、`Drop/`(1)。域内平铺。
    - 技术职责模块：`Network/`（含 `Core/`、`Protocol/`、`Dispatch/`、`Sessions/`、`Timers/` 基建与 `Handlers/<组>/`、`Builders/<组>/`、`Parsers/<组>/` 协议组目录）、`PvfScript/`、`Infrastructure/`（随机源、运行态、路径、宿主选项、后台服务；原 `Random/`、`Hosting/`、`Persistence/`、`Services/` 均已并入）。
    - 79 个文件**全部有确定归属**，归类规则见该节，实施时不再逐个讨论；目录形态的现行登记见 [07 · §11](07-protocol-layer-refactor.md)。
  - `src/DAFLegacy.Protocol/` 的 11 个 `.cs` → `Server/DAFLegacy.Server/Protocol/`，**`DAFLegacy.Protocol.csproj` 删除**（项目并入主项目）。
  - `src/DAFLegacy.Launcher/`、`src/DAFLegacy.Ijl15/` → `Client/` 下同名目录。
  - `third_party/d3d8to9/v1.15.1/` → `Client/Patch/`，并把 `LICENSE.md` / `README.md` 改名为 `LICENSE-d3d8to9.md` / `README-d3d8to9.md`（`Patch/` 将容纳多个补丁资产，通用名会歧义）。**`third_party/` 目录随之消失**。
  - `tests/DAFLegacy.SmokeTests/` → `Server/DAFLegacy.Server.SelfTests/`，项目名改为 `DAFLegacy.Server.SelfTests`，11 个用例**按 [01 · 4.3](01-target-architecture.md#现有-11-个用例的域归属) 的表格直接归位**（`Game/Compound/` 2、`Game/Dungeon/` 2、`Game/Inventory/` 2、`Game/Quest/` 2、`Game/ItemUpgrade/` 2、`GameWorld/` 1），`Program.cs` 留在项目根。
  - **命名空间保持 `DAFLegacy.Server` 扁平不动**（分两步走，见 [01 · 9.4](01-target-architecture.md#94-命名空间策略分两步)）。
- **解决方案成员改造**：两个 `.vcxproj` 标注 `<Build Project="false" />` 不参与解决方案构建（文件名不变，IDE 全量可见，原生构建统一走 `patch.bat`）。
- **两个运行时配置文件归位**：`server.json` → `Server/DAFLegacy.Server/`，`launcher.json` → `Client/DAFLegacy.Launcher/`。两个项目用的 SDK 不同，项类型分别是 `Content` 与 `None`，写成一样会**静默丢失文件**——详见 [01 · 8.4](01-target-architecture.md#84-两个运行时配置文件归位)。
- **Launcher 原生解耦**（原计划的独立里程碑，现因是单解决方案的前置条件而并入 M0）：
  - `DAFLegacy.Launcher.csproj` 的 `BuildIjl15` / `CopyIjl15ToOutput` / `CopyIjl15ToPublish` 三个 Target 删除：`ijl15.dll` 作为 `Condition="Exists(...)"` 的可选内容项，原生产物在位即自动随包（[03 · 4.3](03-build-release-ci.md#43-daflegacyijl15-的构建与-launcher-解耦m0-改造)）；原生与客户端产物统一由 `patch.bat` 构建（[03 · 4.5](03-build-release-ci.md)）。
  - **删除对 `DAFLegacy.Protocol` 的 `ProjectReference`**（死引用，[01 · 3.0](01-target-architecture.md#30-先修正一处事实launcher-对-protocol-的引用是死引用)）。
  - `publish.bat` 保持一键发布入口，仅发布服务端包（仅 .NET SDK）；原生与客户端产物由 `patch.bat` 发布（[03 · 4.5](03-build-release-ci.md)）。
  - 确认 `Ijl15ReplacementInstaller` 在 `ijl15.dll` 缺失时报"模块不可用"而非抛未处理异常。
  - IJL15 原生产物目录改为 `artifacts/native/ijl15/<config>/win-x86/`。
- 新增内容一律写入 `docs/design/`，**不修改仓库根 `README.md`**。

**验收**

```bash
# 三个平台均须成功（这是本阶段的核心验收）
# 解决方案含 .vcxproj、仅 MSBuild 可构建，跨平台验收按项目构建
dotnet build Server/DAFLegacy.Server/DAFLegacy.Server.csproj -c Release
dotnet build Server/DAFLegacy.Server.SelfTests/DAFLegacy.Server.SelfTests.csproj -c Release

# 三个平台均须输出 "All DAFLegacy protocol smoke tests passed."
dotnet Server/DAFLegacy.Server.SelfTests/bin/Release/net10.0/DAFLegacy.Server.SelfTests.dll

# 两层可独立发布（各平台均须成功）
dotnet publish Server/DAFLegacy.Server/DAFLegacy.Server.csproj -c Release -r linux-x64 -o dist/server-linux-x64
dotnet publish Client/DAFLegacy.Launcher/DAFLegacy.Launcher.csproj -c Release -r win-x64 -o dist/client-win-x64

# 配置文件归位后必须仍在输出目录与发布产物中（防静默丢失）
ls Server/DAFLegacy.Server/bin/Release/net10.0/server.json
ls Client/DAFLegacy.Launcher/bin/Release/net10.0-windows/launcher.json
ls dist/server-linux-x64/server.json
ls dist/client-win-x64/launcher.json
```

**功能面复核**：按 [02 · R11](02-portability-contracts.md#r11--功能面不变) 的判据逐项验证运行期可达性——服务端 CLI 仍生效、Launcher 的 6 个参数仍被识别、`/health` 与 `/api/status` 正常、后台界面能调用 `/api/admin/*`、6 个后台服务都注册。**文件覆盖为零不等于功能未丢**，两者是不同的检查。

**覆盖率复核**：重组后重跑 [01 · 10.2](01-target-architecture.md#102-覆盖率核查) 的 `git ls-files` 比对，确认 177 个受跟踪文件在新结构下**全部有确定位置**（含 `Properties/`、`wwwroot/`、两个 `.ico`）。

**反向验收（同样必须通过）**：`windows-latest` 上 `publish.bat` 产出的 `dist/DAFLegacy.Server` **必须仍含 `ijl15.dll`**，与 M0 之前逐项一致——这是"解耦没有弄坏 Windows 交付"的唯一证据。

**已验证**：

- macOS 上单解决方案含全部 4 个 `.csproj`（含 `net10.0-windows` 的 Launcher）**完整构建成功，0 警告 0 错误**（[00 · 3.6](00-kickoff.md#36-全仓库能否只用一个解决方案) 试验 2）。
- macOS 上单独发布 Launcher 到 `win-x64` 成功，只还原 `Protocol` 与 `Launcher`，产出 `PE32+ executable (GUI) x86-64, for MS Windows`（[00 · 3.7](00-kickoff.md#37-两层能否独立打包实测) 试验 4）。
- 含 `.vcxproj` 的解决方案在 `dotnet` CLI 上是硬错误 `MSB4278`（试验 3），这是 `.vcxproj` 必须移出的直接原因。

### M1 · 层边界护栏

**目标**：补上 M0 牺牲掉的判定能力，让 Server 层的纯净性重新可被机器发现。

**任务**

- **新增 CI 源码扫描**（唯一需要自建的一步）：检查 `Server/` 与 `tests/` 下有没有 Windows 系统库 `DllImport`（Z5）。几行 grep，**不写 MSBuild 目标、不新增共享 props**——[01 · 3.3](01-target-architecture.md#33-强制手段编译器承担绝大部分只补一处源码扫描) 说明了为什么其余 Z 规则交给编译器。
- **确认 `EnableWindowsTargeting` 只出现在 `Client/DAFLegacy.Launcher/DAFLegacy.Launcher.csproj` 内**，仓库根与 `Server/` 下不存在 `Directory.Build.props`。这是护栏生效的前提——放到全局会让 Z2 的 WinForms/WPF 分支静默通过。
- `build.yml` 落地护栏扫描与服务端构建自测：单 job（`windows-latest`）只做护栏扫描（Z5 源码扫描 + `EnableWindowsTargeting` 位置 + 禁共享构建配置）与服务端自测构建执行，不产出任何包产物（见 [03 · 5.1](03-build-release-ci.md#51-buildyml-目标结构)）。
- 原生项目的 CTest（`build-native.ps1` 的 `-RunTests` 开关）由本地执行：`patch.bat` 把额外开关转发给两个 `build-native.ps1`；CI 不构建原生项目。
- 新增 CI 源码扫描步骤：Server 层项目的 `.cs` 中出现 Windows 系统库 `DllImport`（`d3d9.dll`、`user32.dll`、`kernel32.dll` 等）即失败。
- 记录基线，作为后续里程碑的回归比对基准：
  - Windows 全量构建与**发布产物清单**（逐文件）。
  - **功能面基线**（[02 · R11](02-portability-contracts.md#r11--功能面不变)）：从 `Program.cs` 提取 31 个 `Map*` 路由、2 个服务端 CLI 开关、6 个 Launcher CLI 参数、6 个 `IHostedService`、2 个 CTest 用例、2 种数据源模式、5 个子系统开关。**这份清单是「迁移没有丢功能」的唯一依据**，不能只看构建是否通过。

**验收**

| 故意违规（反向验证） | 期望的拦截机制 |
| --- | --- |
| 给 `DAFLegacy.Server.csproj` 加 `<UseWindowsForms>true</UseWindowsForms>` | Linux 构建 **`NETSDK1100`** 失败（前提：`EnableWindowsTargeting` 未全局打开） |
| 把 `DAFLegacy.Server.csproj` 的 TFM 改成 `net10.0-windows` | **`NU1201`** 失败——`DAFLegacy.Server.SelfTests`（`net10.0`）无法引用它 |
| 给 `DAFLegacy.Server.csproj` 加指向 `DAFLegacy.Launcher` 的 `ProjectReference` | **`NU1201`** 失败（无条件，与 `EnableWindowsTargeting` 无关） |
| 给 `DAFLegacy.Server.csproj` 加指向任一 `.vcxproj` 的 `ProjectReference` | **`MSB4278`** 失败 |
| 给 `Server/` 下任一 `.cs` 加 `[DllImport("user32.dll")]` | **CI 源码扫描步骤**失败 |

**完成定义**：上述四条"故意违规"的验收全部能被拦下——**护栏必须经过反向验证**，只验证"正常构建能通过"不足以证明它有效。

### M2 · 随机源收拢

**目标**：服务端随机源降级为**纯托管的单一实现**，Server 层不再有任何原生资产（[06 · 随机源降级为纯托管实现](06-random-source-managed.md)，对应 [02 · R2](02-portability-contracts.md#r2--原生能力的运行期发现) 在本层的零实例状态）。

M0 已把 `GameRandomSource.cs` 移入 `Random/`。本阶段**在 `Random/` 内替换实现，并把原生模块整体移至 `Client/DAFLegacy.RandomNative/` 保留**（不删除，模块移至 Client 层），不改其他模块。

**任务**（取自 [06 · 9](06-random-source-managed.md#9-落地步骤)）

- 先确认 [06 · Q1](06-random-source-managed.md#7-待确认问题)（客户端是否从 `ClientRandomSeed` 复算掉落），以决定是否需要移植客户端 LCG。
- 按 [06 · 4.3](06-random-source-managed.md#4-设计提案) 把 `GameRandomSource` 改为 `Random.Shared` 的单一实现（约 249 行 → 约 20 行）。**保留 `IDropRandomSource` 缝与 `GameRandomSource.Shared` 名字**，42 处调用点零改动；新增确定性实现 `SeededRandomSource`（自测与问题复现用）。
- 按 [06 · 4.4](06-random-source-managed.md#4-设计提案) 收缩状态面：`GameRandomStatus` 由 9 个字段降为一个 provider 字段，同步改写 6 处消费点。
- 按 [06 · 5](06-random-source-managed.md#5-原生资产处置清单) 处置原生资产：`src/DAFLegacy.RandomNative/` 整目录（8 个文件）**移至 `Client/DAFLegacy.RandomNative/` 保留**（相对深度不变，内部路径无需改动）；`RandomNative` 在 `DAFLegacy.Emulator.slnx` 的条目保留（原生项目仅 IDE 可见、不参与构建）；删除 `GameRandomSource.cs` 内的 `NativeRdSeed` 与 `NativeMethods`。
- **删除** `Server/DAFLegacy.Server/DAFLegacy.Server.csproj` 中指向 `DAFLegacy.RandomNative.vcxproj` 的 `ProjectReference`、`BuildRandomNativeReference`、`RandomNativeBinary` 属性与硬编码 `Content`（4 处编译期耦合）。
- 自测项目新增 `Random/` 目录，改用 `SeededRandomSource` 与既有替身覆盖接口语义，删除断言三级链与拒绝采样的用例。
- 按 [06 · 6](06-random-source-managed.md#6-需要同步修订的既有设计) 同步修订 00–03 中失去对象的条目，并把 [05](05-randomnative-crossplatform.md) 标注作废。

**验收**：判据统一登记在 [06 · 8 验收标准](06-random-source-managed.md#8-验收标准)，此处不再重复列录。

**完成定义**：42 处调用点零改动且分布行为不变；服务端在三个平台走完全相同的托管代码路径，不存在"原生库在不在"这一状态；`RandomNumberGenerator` 仍只用于会话密钥等密码学用途。

### M3 · PVF 脚本模块收拢

**目标**：把两个真实的可移植性风险点（代码页、路径与大小写语义）集中到 `PvfScript/` 并配上跨平台测试（[02 · R4](02-portability-contracts.md#r4--编码契约)、[R8](02-portability-contracts.md#r8--文件系统语义)）。

M0 已把 `ScriptFileSystem.cs` 移入 `PvfScript/`。本阶段补编码契约、路径归一与测试。

**任务**

- 在 `Server/DAFLegacy.Server/PvfScript/` 内：
  - 编码契约集中定义（CP949 用于 PVF 路径表、CP936 用于客户端文本、`Latin1` 用于无语义字节透传），并提供自足注册。
  - 虚拟路径归一的唯一归属地。
  - 目录回退模式改为启动时一次枚举建立 `OrdinalIgnoreCase` 索引（R8）。
- **行为不变项逐条对照**（见 [01 · 4.2](01-target-architecture.md#42-模块职责要点)）：`OrdinalIgnoreCase` 路径语义、`DecryptInPlace` 的 dword 对齐与循环右移 6 位、`ReadAllText` 默认编码、`FileExists` 在 PVF 模式下不触发解密。
- 编码注册收敛为 2 处（组合根 + 本模块自足），删除其余 13 处。注意 `Protocol/PvfArchiveReader.cs` 也有一处，归入组合根或本模块。
- 自测项目新增 `PvfScript/` 目录，包含：
  - **合成 PVF 夹具生成器**：按 `DNF_SCRIPT_PACK` 格式生成最小归档，路径表用 CP949 编码，数据体 dword 对齐并加密。`Script.pvf` 不入库。
  - 编码往返用例：CP936 / CP949 / CP51949 / UTF-8。
  - 路径归一用例：`\` 与 `/` 输入等价。
  - 大小写用例：Linux 上以小写路径访问大写目录（R8）。
  - 边界用例：条目长度非 4 字节倍数、偏移越界、`pathLength <= 0`、树头长度非法。

**验收**

```bash
dotnet build DAFLegacy.Emulator.slnx -c Release
dotnet Server/DAFLegacy.Server.SelfTests/bin/Release/net10.0/DAFLegacy.Server.SelfTests.dll   # 三平台通过
```

**完成定义**：PvfScript 用例在三个平台通过且不需要 `Script.pvf`；原有自测用例在三个平台仍全绿；Windows 上以真实数据加载的日志输出与改动前逐项一致（脚本文件计数、路径来源描述等）。

**风险提示**：这是本次重构中最容易引入细微行为差异的一步。建议 M3 的 PR 中**把语义调整与测试补齐分开提交**——先补测试证明当前行为，再改语义并让测试证明行为未变。

### M4 · 路径与数据契约

**目标**：修掉已实测确认的 Windows 专属配置问题，并统一降级行为（[02 · R3](02-portability-contracts.md#r3--路径契约)、[R5](02-portability-contracts.md#r5--配置与数据锚点)、[R9](02-portability-contracts.md#r9--降级一致性)）。

**任务**

- 新增 `PathResolver`（`Normalize` / `Resolve`）；替换 `CeraShopCatalog`、`CharacterStatCatalog`、`ItemCatalog`、`CharacterCreatureInventoryPlanner` 等处零散的 `Replace('\\','/')`。
- `server.json` 的 `ScriptPvfPath` 由 `"..\\Script.pvf"` 改为 `"../Script.pvf"`；反斜杠写法继续兼容（`Normalize` 处理）。
- 配置与数据锚点支持 `--home <path>` / `DAFLEGACY_HOME`，默认仍为 `AppContext.BaseDirectory`（R5）。
- `CreatureExperienceCatalog` 的缺失场景由抛异常改为 `Error` 日志 + 功能禁用（R9）。
- 组合根增加启动前的路径校验与**缺失数据汇总清单**输出。
- 验证 `SIGTERM` 优雅退出（R7）：状态文件完整落盘、退出码 0。

**验收**

| 场景 | 期望 |
| --- | --- |
| Linux，`DAFLEGACY_HOME=/tmp/daflegacy-home` 启动 | 配置从该目录的 `server.json` 读取，`state.json` 写入该目录 `data/` |
| Linux，使用旧的 `"..\\Script.pvf"` 配置 | 日志中报出的路径为正常的 `/…/Script.pvf`；文件存在时能正确加载 |
| 删除 `creature/exptable.tbl` | 服务端仍启动，有明确 `Error` 行与缺失清单，生物经验表现为禁用 |
| `kill -TERM <pid>` | 退出码 0，`state.json` 为合法 JSON |
| **完整启动** | 在具备 `Script.pvf` 的环境（或目录回退模式指向最小脚本集）下服务端启动并接受客户端连接 |

**完成定义**：上表全部通过，且**"完整启动 + 接受连接"这一项必须在真实数据环境下验证**——仓库内不含 `Script.pvf`，此项无法在 CI 中自动化，须人工执行并记录结果。

### M5 · 独立打包与发布

**目标**：让分层独立出包成为默认的交付方式，Linux 成为可持续交付的目标（[03](03-build-release-ci.md)）。

**任务**

- 新增 `publish.sh`，自探测 RID（[03 · 6.1](03-build-release-ci.md#61-publishsh-的-rid-处理)），支持 `PUBLISH_ALL=1` 产出全部目标。
- 发布命令显式带 RID（`-r win-x64` / `-r linux-x64`），`publish.bat` 同步更新。
- `release.yml` 按标签触发，唯一交付物为 `DAFLegacy-<tag>-linux-x64.zip`（含 `.sha256`），只单独构建服务端项目；Windows 服务端包与客户端产物分别由本地 `publish.bat` / `patch.bat` 产出，不进 CI（[03 · 4.5](03-build-release-ci.md)、[03 · 6](03-build-release-ci.md#6-发布流程releaseyml)）。
- **把 `d3d8.dll` 真正放进包**（[Q13](#5-待决问题) 已决：需要分发）：在 `Client/DAFLegacy.Launcher/DAFLegacy.Launcher.csproj` 里加 `Content` 项指向 `Client/Patch/` 下的**三个文件**——`d3d8.dll` 以及 `LICENSE-d3d8to9.md` / `README-d3d8to9.md`（[01 · 2.4](01-target-architecture.md#24-补丁资产-clientpatchd3d8dll) 说明了后两者的许可义务：BSD 式条款要求二进制再分发必须随附版权声明），写法与 `Config.ini` 一致。**不要写在 CI 里**——那会让本地 `publish.bat` 与 CI 产出的包内容不一致。今天它零引用、从未进包（[01 · 2.4](01-target-architecture.md#24-补丁资产-clientpatchd3d8dll)），所以这是 M5 的**新增**步骤，基线里没有它。
- **包内容校验**：按 [03 · 3.0](03-build-release-ci.md#30-包内容来源与复制责任) 的表在 CI 校验 Linux 服务端包内容（`DAFLegacy.Server`、`server.json`、`wwwroot`），缺失即失败。它拦的是 `Content`/`None` 项类型写错导致的**静默丢文件**（构建成功但文件不在包里）。
- **更新 [03](03-build-release-ci.md) 的发布布局与发布命令章节**：把 Linux 一条路径写完整，并说明 Linux 包不含 Launcher 与 `ijl15.dll` 及其原因。**不修改仓库根 `README.md`**——面向使用者的 Windows 部署说明保持原样，Linux 相关说明一律落在 `docs/design/` 内。

**验收**

| 场景 | 期望 |
| --- | --- |
| `ubuntu-latest` 发布 Linux 服务端包 | 产物中 apphost 经 `file` 确认为 ELF x86-64 |
| Windows 服务端包 | 由本地 `publish.bat` 产出，包内容与 [03 · 3.0](03-build-release-ci.md#30-包内容来源与复制责任) 的服务端列一致 |
| 包内容校验 | `release.yml` 在打包前校验 Linux 服务端包内容（`DAFLegacy.Server`、`server.json`、`wwwroot`），缺失即失败 |
| 文档 | [03](03-build-release-ci.md) 记录的 Linux 发布步骤可在干净机器上照做即得可用产物 |
| 仓库根文档 | `git diff --stat` 中不出现 `README.md` |

**已验证**：从 macOS 执行 `dotnet publish ... -r linux-x64` 在当前代码上即已成功，产物为 `ELF 64-bit LSB pie executable, x86-64`。**Linux 发布在今天的代码上就已可行**——M5 的价值在于把它纳入 CI 与文档，使之成为受支持且不会静默失效的路径，而不在于让它"变得可能"。同理，两层的独立打包已由 [00 · 3.7](00-kickoff.md#37-两层能否独立打包实测) 验证。

## 4. 回滚策略

| 里程碑 | 回滚方式 | 残留影响 |
| --- | --- | --- |
| M0 | **四件事必须一起还原**：`git mv` 把目录搬回 `src/`（含 `GameWorld/`、`Game/<域>/` 拆分还原为平铺）、把 `.vcxproj` 放回解决方案、还原 `Launcher.csproj` 的三个 Target 与配置文件项、还原 `publish.bat` | 无源码影响。改动全在文件位置与构建配置 |
| M1 | 删除 CI 的源码扫描步骤与矩阵 job | 无源码影响 |
| M2 | `git revert`：还原 `Random/` 内的实现替换与新增文件，恢复 `DAFLegacy.Server.csproj` 与 `Client/DAFLegacy.RandomNative` 的耦合 | 无。改动集中在 `Random/` 一个目录与 csproj 耦合 |
| M3 | 还原 `Server/DAFLegacy.Server/PvfScript/` 内新增的编码与路径代码；自测项目的 `PvfScript/` 目录保留（测试仍有价值） | 无 |
| M4 | `PathResolver` 调用点回退；`server.json` 还原 | 无。R9 的降级修复建议保留（它修的是缺陷） |
| M5 | 还原 CI 与脚本 | 无源码影响 |

**关键点**：M3、M4 是仅有的"改动已有业务逻辑"的阶段，且都是小范围改动——M3 集中在 `PvfScript/` 一个目录、M4 集中在路径解析与降级两处。M0 虽然改动面最大（177 个文件里的 100 余个换位置），但那是**纯文件移动**，不涉及一行逻辑，因此不需要长期并存的双实现。

**M0 的回滚最需要小心**，因为它是四件事的组合：目录重组、解决方案成员变化、Launcher 解耦、配置文件归位。**只还原其中一部分会落到半旧半新的路径上，任何构建都失败**。回滚时应整体 `git revert` 该提交，而不是逐项挑着改。


## 5. 待决问题

以下问题影响投入规模，需要在开工前明确。每项给出建议与理由。

| # | 问题 | 建议 | 理由 |
| --- | --- | --- | --- |
| Q1 | **是否把业务目录（catalogs/planners，约 2 万行）从 `DAFLegacy.Server` 拆出 `DAFLegacy.Domain`？** | **暂不拆** | 对可移植性零收益——它们已经是纯托管代码且已在三平台验证。拆分的收益是分层观感与更快的单测，成本是搬动大量已验证代码。若要拆，应作为独立目标（可测试性）单独立项。参照项目 `ServerS4A21` 用的正是"单项目 + 目录分层"（`Game/`、`Network/` 等），同一思路 |
| Q2 | **Linux 是否作为一等部署目标？** | **是** | M5 已验证 Linux 发布在当前代码上即可行；把它纳入 CI 的成本很低，而不纳入的代价是这条路径会随时静默失效 |
| Q3 | **macOS 是开发机还是部署目标？** | **仅开发机** | 三平台 CI 中的 macOS 用于捕获可移植性回归；发布只做 win-x64 与 linux-x64，避免维护第三套发布产物的成本 |
| Q4 | **测试框架：继续自建 runner 还是迁移 xunit？** | **继续自建 runner** | 与仓库现状一致（全仓库零测试框架引用）。参照项目把测试放在主项目内的 `SelfTests/` 目录，说明"不引入框架"是多数的做法。若未来需要参数化 / 夹具生命周期 / 覆盖率，整体迁移比现在部分迁移更省事 |
| Q5 | **是否需要容器镜像？** | **暂不做** | 单实例 + 文件持久化（`data/state.json`）与容器化的多实例假设冲突（见 Q6）。先做出可用的 Linux 发布包 |
| Q6 | **JSON 文件持久化不支持多实例共享写入**（README"已知缺口"） | **本次不解决** | 与可移植性正交。但它限制 Linux 部署形态（无法水平扩展），在 Q5 决策前需明确。参照项目已改用 SQLite，可作为将来参考 |
| Q7 | **服务端在 Linux 上的性能特征未知** | **M5 后做一次压测** | 14k 行的 `EntranceService` 包处理路径可能有平台相关的性能假设；不设预期指标，只做基线测量 |
| Q8 | **Server 层项目是否开启 `TreatWarningsAsErrors`？** | **暂不开启** | 原生项目已用 `/WX`，托管项目未用。开启会与迁移改动混在一起产生大量噪声，应独立评估 |
| Q9 | **是否需要 systemd unit / 服务安装脚本？** | **M5 后按需** | 与 Q2 联动。参照项目提供了 `start-server.sh` / `cleanup.sh` 等 Unix 脚本双胞胎，DAFLegacy 至少需要 `stop.sh` 对应现有的 `stop.bat`，可先做这一项 |
| Q10 | **是否需要 macOS 发布包？** | **不需要** | 见 Q3。macOS 上只需保证 `dotnet build` 与测试可用 |
| Q11 | **是否引入托管扩展契约（程序集级插件）？** | **现在不引入** | 见 [01 · 6.3](01-target-architecture.md#63-将来若需要托管扩展)。原先的判据是"今天的唯一需求是可选原生资产，用 RID 探测 + 降级即可覆盖"；该需求已随 [06](06-random-source-managed.md) 服务端与 `DAFLegacy.RandomNative` 的解耦消失（模块保留至 Client 层，不再被服务端引用），结论不变但理由更强——今天服务端连可选原生资产都没有了。等到出现必须用托管程序集承载的 Windows 专属能力（事件日志、性能计数器），再评估——那时才有真实需求可供设计 |
| Q12 | **`EnableWindowsTargeting` 让 Client 层能在非 Windows 上交叉编译，是否应纳入常规流程？** | **是，但只用于验证** | 它让 macOS/Linux 开发者能验证 Launcher 的托管代码改动而无需 Windows。**不用于产出正式 Windows 发布包**——正式 Windows 形态由本地 `publish.bat` / `patch.bat` 在 Windows 上构建，以保证 apphost 与资源嵌入与 MSBuild 路径一致 |
| Q13 | ~~**`d3d8.dll` 是否仍需要分发？**~~ | **已决：需要分发** | 实测确认它当前零引用、从未进入任何发布包（[01 · 2.4](01-target-architecture.md#24-补丁资产-clientpatchd3d8dll)）。**决定保留并分发**，因此 M5 增加一个复制步骤，推荐用 `Launcher.csproj` 的 `Content` 项实现（与 `Config.ini` 一致），而不是写在 CI 里 |
| Q14 | **DAFLegacy 自己的 `LICENSE`（0BSD）是否随发布包分发？** | **随包** | `Launcher.csproj` 的 `Content` 项已把仓库根 `LICENSE` 一并复制进客户端产物（与 `d3d8.dll` 的第三方许可同步落地，[01 · 2.4](01-target-architecture.md#24-补丁资产-clientpatchd3d8dll)）。项目自身是 BSD Zero Clause（0BSD），不要求署名；随包分发让用户知悉使用条款，成本是一个 `Content` 项 |

## 6. 风险登记

| # | 风险 | 概率 | 影响 | 应对 | 关联 |
| --- | --- | --- | --- | --- | --- |
| K1 | **M0 之后、M1 之前缺少 Z5 扫描与三平台矩阵** | 高 | 中——Z1–Z3 仍由编译器无条件守卫（写错的跨层引用与 `.vcxproj` 引用当场被拦），但这段时间里 Windows 系统库 P/Invoke 不会被发现，"产物在三平台上是否真的可用"也没人验证 | M0 与 M1 连续完成，不拉开；把「连续」写进执行原则（[第 1 节](#1-执行原则)第 2 条） | [00 · D4](00-kickoff.md) |
| K2 | 把 `EnableWindowsTargeting` 误放到仓库根或共享 props | 中 | 高——Z2 的 WinForms/WPF 违规会在 Linux 上静默通过，护栏失效且无报错 | 它**只写在 `Client/DAFLegacy.Launcher/DAFLegacy.Launcher.csproj`** 内；M0 验收增加一项：仓库根不存在 `Directory.Build.props`，且 `EnableWindowsTargeting` 只出现在该文件 | [01 · 3.3](01-target-architecture.md#33-强制手段编译器承担绝大部分只补一处源码扫描) |
| K3 | `.vcxproj` 移出后 `msbuild DAFLegacy.Emulator.slnx` 不再构建原生项目 | 高 | 中——开发者以为原生已构建 | 原生构建统一走 `patch.bat`（两个 `build-native.ps1`）；在 [03 · 7](03-build-release-ci.md#7-迁移期的构建兼容性) 显式标注为"唯一的行为退化" | [03 · 7](03-build-release-ci.md#7-迁移期的构建兼容性) |
| K4 | 搬运 `Server/DAFLegacy.Server/PvfScript/` 时引入细微行为差异 | 中 | 高——脚本数据读取错误会广泛影响业务 | M3 的 PR 只做移动；行为不变项逐条对照清单；先补齐特征测试再搬运 | [01 · 4.2](01-target-architecture.md#42-模块职责要点) |
| K5 | 降级一致性改动（R9）掩盖真实配置错误 | 中 | 中——服务端"启动成功"但功能缺失 | 缺失数据一律 `Error` 级日志 + 启动完成时汇总缺失清单；不做静默降级 | [02 · R9](02-portability-contracts.md#r9--降级一致性) |
| K6 | Linux 上缺少 `Script.pvf` 导致 M4 无法完整验收 | 高 | 中——里程碑卡在人工验收项 | 用目录回退模式（`SkillScriptPath` 指向最小脚本集）构造验收夹具；真实 PVF 验收单独记录 | [00 · 3.2](00-kickoff.md#32-服务端在-macos-上的真实启动行为) |
| K7 | 目录回退模式的大小写索引改写引入新行为 | 中 | 中——开发/测试路径受影响 | 保留原查询作为回退；新索引只解决大小写，不改变返回路径的大小写形式 | [02 · R8](02-portability-contracts.md#r8--文件系统语义) |
| K9 | `EnableWindowsTargeting` 放在共享 props 中对 Server 层项目产生意外影响 | 低 | 低 | 该属性只影响面向 Windows 的项目还原；Server 层项目 TFM 不含 `-windows`，不触发 | [03 · 2](03-build-release-ci.md#2-不引入共享构建配置) |
| K11 | `EntranceService`（14,264 行）对可移植性改动的耦合面未知 | 中 | 中——M3/M4 可能需要改动该文件 | 优先通过适配层（命名空间 / 扩展方法）避免修改其内部；仅在编码注册与路径解析两处触及 | [01 · 4.1](01-target-architecture.md#41-daflegacyserver主项目) |
| K12 | Windows 服务端包与客户端产物不在 CI 覆盖内，本地形态可能悄悄坏掉 | 中 | 中——用户在需要该形态时才发现不可用 | 两者与 CI 的 Linux 包同源（同一 csproj 的 `dotnet publish`）；客户端产物由 `patch.bat` 一并构建原生与 Launcher，原生产物在位即自动带上插件；发版前按 [03 · 3.0](03-build-release-ci.md#30-包内容来源与复制责任) 的表逐项核对 | [03 · 6](03-build-release-ci.md#6-发布流程releaseyml) |

## 7. 下一步

1. 评审本文档集，确认第 5 节的 Q1、Q2、Q3、Q11（其余为建议项，可延后）。
2. 按 M0 完成构建收敛，**紧接着完成 M1 补上护栏**——这两步之间的窗口是本次迁移风险最高的时段（K1）。
3. 依次执行 M2 → M5，每个里程碑独立 PR、独立验收。
