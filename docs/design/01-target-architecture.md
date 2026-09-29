# 01 · 目标架构：Server 层与 Client 层

- 状态：**已实施**（2026-09-24 标注，重构已落地）
- 上级文档：[00 开档总纲](00-kickoff.md)

---

## 1. 目标结构

### 1.1 项目结构图

层级形式参照 `ServerS4A21`：**层目录 / 项目目录 / 模块目录** 三级，项目内部的模块用目录分组（[参考依据](#9-为什么采用这个形式参考项目的对照)）。

标注：**【S】Server 层 / 【C】Client 层**；`保留` / `新增` / `微调` / `解耦` / `移` 表示本次重构对它的改动程度。

```text
DFLegacy/                                        ← 仓库根
│
├── DFLegacy.Emulator.slnx                      唯一解决方案（全部 5 个项目；原生项目仅 IDE 可见，构建走 patch.bat）
├── publish.bat                                 Windows 一键发布脚本（保留）
├── stop.bat                                    停止服务端脚本（保留）
├── publish.sh                                  [新] M5 · Linux/macOS 脚本（自探测 RID）
├── README.md / ADMIN_API.md                    保留 · 本次不改动
├── .gitignore                                  /docs 整体忽略（docs 仅本地维护，不随仓库分发）
│
├── .github/workflows/                          build.yml / release.yml（改造）
├── docs/design/                                本设计文档集
│
├── Server/                                     ── Server 层：可跨平台的服务端核心 ──
│   │
│   ├── DFLegacy.Server/                        服务端主项目（改后零 ProjectReference）
│   │   ├── DFLegacy.Server.csproj
│   │   ├── DFLegacy.Server.ico                 ApplicationIcon
│   │   ├── Program.cs                          组合根：六个平台决策的唯一汇聚点（第 7 节）
│   │   ├── Properties/                         原地不动：AssemblyInfo.cs、launchSettings.json
│   │   │
│   │   ├── Protocol/                           [移 M0] 线路协议与数据格式     11 个 .cs / 4,470 行
│   │   │   │                                    原独立项目 DFLegacy.Protocol 整体并入
│   │   │   ├── GameProtocolConstants.cs  FallbackCommandReplies.cs   帧类型/域常量 + 兜底应答表
│   │   │   ├── EntranceHandshakeReplies.cs  EntranceMetadata.cs      入口握手应答 + 频道元数据
│   │   │   ├── GameClientPayloadCipher.cs  GameDatagramProtocol.cs  LegacyDatagramProtocol.cs
│   │   │   ├── PacketFrame.cs  GameServerPacket.cs  Crc32.cs
│   │   │   └── PvfArchiveReader.cs              PVF 归档二进制解析
│   │   │
│   │   ├── PvfScript/                          [新 M3] 脚本数据访问           1 个 .cs + 新增
│   │   │   └── ScriptFileSystem.cs              迁自项目根（315 行）；编码契约与路径大小写索引
│   │   │                                        的归属地（CP949 / CP936 / Latin1，R4 / R8）
│   │   ├── Random/                             [新 M2] 随机源（纯托管）       1 个 .cs + 新增
│   │   │   └── GameRandomSource.cs              迁自项目根；改为纯托管单一实现（Random.Shared），
│   │   │                                        接口保留 IDropRandomSource（06）
│   │   ├── Network/                            网络服务                        4 个 .cs
│   │   │   ├── EntranceService.cs               登录与角色主循环（14,264 行，不重写）
│   │   │   ├── CharacterDatagramService.cs  GameplayDatagramService.cs
│   │   │   └── ProbeService.cs
│   │   ├── Persistence/                        状态持久化                      3 个 .cs
│   │   │   ├── JsonGameStore.cs                 5,630 行
│   │   │   └── RuntimeState.cs  SerializedWriteStream.cs
│   │   ├── Hosting/                            宿主配置与后台接口模型           3 个 .cs
│   │   │   └── ServerOptions.cs  AdminApiModels.cs  CharacterSessionRegistry.cs
│   │   ├── Services/                           周期性后台服务（IHostedService）  2 个 .cs
│   │   │   └── WeaknessRecoveryService.cs
│   │   │
│   │   ├── GameWorld/                          脚本静态定义与目录（平铺）       25 个 .cs
│   │   │   │                                    *Catalog / *Definition：从 PVF 解析出的
│   │   │   │                                    只读定义，不含运行时行为
│   │   │   └── ItemCatalog · DungeonCatalog · SkillCatalog · QuestCatalog · DropCatalog
│   │   │       WorldDropCatalog · GoldDropCatalog · MonsterDropCatalog
│   │   │       MonsterChampionDropCatalog · HellDropCatalog · ObjectDropCatalog
│   │   │       ClearRewardCatalog · HiddenDungeonUnlockCatalog · WorldMapCatalog
│   │   │       CharacterStatCatalog · CharacterExperienceCatalog · CharacterSpCatalog
│   │   │       CreatureExperienceCatalog · DungeonExperienceCatalog · PvpExperienceCatalog
│   │   │       StaminaRecoveryCatalog · CompoundCatalog · AvatarCompoundCatalog
│   │   │       EquipmentReinforcementCatalog · ResealCatalog · DisjointCatalog
│   │   │       CeraShopCatalog · PremiumBenefitCatalog …
│   │   │
│   │   ├── Game/                               运行时业务规则，按业务域分       39 个 .cs
│   │   │   ├── Dungeon/                  [11]  DungeonRunState · DungeonGroundItemState
│   │   │   │                                    DungeonClearScore · DungeonClearBonusContext
│   │   │   │                                    DungeonDifficultyProgression · DungeonFatigueRules
│   │   │   │                                    DungeonRoomClearRules · DungeonWeaknessPolicy
│   │   │   │                                    DungeonItemDropPlanner · DungeonPickupPlanner
│   │   │   │                                    ClearRewardCardPlanner
│   │   │   ├── Inventory/                 [9]  CharacterInventoryPlanner · CharacterInventorySorter
│   │   │   │                                    CharacterAvatarInventoryPlanner
│   │   │   │                                    CharacterCreatureInventoryPlanner
│   │   │   │                                    CharacterWarehouseProgression
│   │   │   │                                    CharacterItemIdentity · CharacterItemSealing
│   │   │   │                                    CharacterItemWireProjection · MaximumTransferRoster
│   │   │   ├── Quest/                     [8]  QuestAcceptancePlanner · QuestRewardPlanner
│   │   │   │                                    QuestItemDropPlanner · QuestItemRequirementPlanner
│   │   │   │                                    QuestDungeonClearRequirement · QuestMapRequirement
│   │   │   │                                    QuestMeetNpcRequirement · QuestPvpRankRequirement
│   │   │   ├── Compound/                  [3]  CompoundItemPlanner · AvatarCompoundPlanner
│   │   │   │                                    DisjointItemPlanner
│   │   │   ├── Shop/                      [3]  CeraShopContentPlanner
│   │   │   │                                    CeraBoosterOpeningPlanner · LotteryItemPlanner
│   │   │   ├── ItemUpgrade/               [2]  EquipmentReinforcementPlanner
│   │   │   │                                    EquipmentQualityAdjustmentPlanner
│   │   │   ├── Account/                   [1]  AccountIdentity
│   │   │   ├── Creature/                  [1]  CreatureHungerPlanner
│   │   │   └── Drop/                      [1]  WeightedItemTable
│   │   │
│   │   ├── server.json                         [移 M0] 服务端运行时配置（从仓库根移入）
│   │   └── wwwroot/                            静态资源（随 Web SDK 发布）
│   │       ├── admin/                          admin.css · admin.js · index.html（后台界面）
│   │       └── client/                         旧版充值页面（5 个 .htm）：
│   │                                            client_pay.htm · client_pay_bank.htm
│   │                                            client_pay_netbar.htm · client_diamond.htm
│   │                                            client_diamond_netbar.htm
│   │                                            + css/commen.css + images/{cz.png,hz.gif,pic.png}
│   │
│   ├── DFLegacy.Server.SelfTests/              自测项目（独立 exe，自建 runner）
│       ├── DFLegacy.Server.SelfTests.csproj    引用 ../DFLegacy.Server/DFLegacy.Server.csproj
│       ├── Program.cs                          用例注册与退出码
│       ├── Protocol/                           协议用例
│       ├── PvfScript/  Random/                 [新 M2 / M3] 见第 4.3 节的用例清单
│       ├── GameWorld/                          WorldMapSmokeTests
│       └── Game/                               与主项目同构的业务域子目录：
│           ├── Dungeon/                        DungeonClearExperience · HiddenDungeonQuest
│           ├── Inventory/                      ItemIdentity · ItemSealing
│           ├── Quest/                          QuestMap · QuestPvpRank
│           ├── Compound/                       AvatarCompound · CompoundItem
│           ├── ItemUpgrade/                    EquipmentQuality · EquipmentReinforcement
│           └── （原 tests/DFLegacy.SmokeTests 的 11 个用例 + Program.cs 迁入）
│   │
├── Client/                                     ── Client 层：Windows 客户端交付物 ──
│   │
│   ├── DFLegacy.Launcher/                      Windows 启动器 [解耦 M0]
│   │   ├── DFLegacy.Launcher.csproj            三个原生 Target 改为可选内容项
│   │   ├── launcher.json                       [移 M0] 启动器运行时配置（从仓库根移入）
│   │   ├── Program.cs                          入口：读 launcher.json、按 exe 名启动服务端
│   │   ├── D3D9DeviceProbe.cs                  WinForms UI + d3d9.dll / user32.dll P/Invoke
│   │   ├── Ijl15ReplacementInstaller.cs        校验并安装 ijl15.dll / Config.ini 到客户端目录
│   │   ├── ClientProcessLauncher.cs            启动游戏客户端进程
│   │   └── DFLegacy.Launcher.ico               ApplicationIcon
│   │
│   ├── DFLegacy.Ijl15/                         ijl15.dll 替身 [保留不变]
│   │   │                                       C++ / CMake / Win32 x86
│   │   ├── CMakeLists.txt
│   │   ├── DFLegacy.Ijl15.vcxproj              在解决方案内（仅可见，不参与构建）
│   │   ├── DFLegacy.Ijl15.vcxproj.filters
│   │   ├── build-native.ps1                    产物 → artifacts/native/ijl15/<config>/win-x86/
│   │   ├── Config.ini                          运行时配置，由 Launcher 复制到客户端目录
│   │   ├── include/ijl15.h
│   │   ├── src/                                ijl15.cpp · d3d8_scaler · dnf_patches
│   │   │                                       memory_patch · window_scaling · mouse_wheel
│   │   │                                       message_audio · performance_overlay
│   │   │                                       external_string_table · output_paths
│   │   │                                       ijl15_config · ijl15.def
│   │   └── tests/ijl15_tests.cpp               CTest: ijl15_compatibility
│   │
│   ├── DFLegacy.RandomNative/                  RDSEED 硬件随机桥 [移 M2]（原 src/ 下；服务端解耦后保留至此）
│   │   │                                       C++ / CMake / x64
│   │   ├── CMakeLists.txt
│   │   ├── DFLegacy.RandomNative.vcxproj       在解决方案内（仅可见，不参与构建）
│   │   ├── DFLegacy.RandomNative.vcxproj.filters
│   │   ├── build-native.ps1                    产物 → artifacts/native/random/<config>/
│   │   ├── include/dflegacy_random.h
│   │   ├── src/                                dflegacy_random.cpp · dflegacy_random.def
│   │   └── tests/random_native_tests.cpp       CTest: rdseed_native
│   │
│   └── Patch/                                  预编译补丁资产 [移 M0]（原 third_party/d3d8to9/）
│       ├── d3d8.dll                            d3d8to9 v1.15.1 预编译二进制
│       ├── LICENSE-d3d8to9.md                  BSD 式许可；随二进制分发时须保留
│       └── README-d3d8to9.md                   来源与版本说明
```

**关于 `Server/` 下的两个项目**：`DFLegacy.Server`（主项目，含全部服务端代码）与 `DFLegacy.Server.SelfTests`（测试）。两者都是托管项目，同一 TFM、同一编译图——**Server 层没有原生项目**：原有的原生资产 `DFLegacy.RandomNative` 已按 [06 · 随机源降级为纯托管实现](06-random-source-managed.md) 与服务端解耦，保留至 `Client/DFLegacy.RandomNative/`。测试独立成项目而不是像参照项目那样放进主项目的 `SelfTests/` 目录，理由见 [9.3](#93-与参照项目的两处有意偏离)。

**关于 `artifacts/`**：原生产物目录，已在 `.gitignore` 中。约定为 `artifacts/native/<module>/<configuration>/<rid>/`（[03 · 4.1](03-build-release-ci.md#41-产物落地约定)）。

### 1.2 分层关系

```text
┌──────────────────────────────────────────────────────────────────────┐
│  Server/                    Server 层 —— 可跨平台的服务端核心           │
│  交付物：一个可移植的服务端发布包（win / linux / osx）                    │
│                                                                      │
│   DFLegacy.Server/          服务端主项目（零 ProjectReference）          │
│     Protocol/               线路协议、封包编解码、加密、PVF 二进制解析      │
│     PvfScript/              脚本数据访问：PVF 索引 + 目录回退 + 编码契约    │
│     Random/                 随机源（纯托管单一实现）                    │
│     Network/                登录与 UDP 服务                            │
│     Persistence/ Hosting/   状态持久化 / 宿主配置与后台接口               │
│     Services/               周期性后台服务（IHostedService）           │
│     GameWorld/              脚本静态定义与目录（*Catalog，平铺）           │
│     Game/<业务域>/          运行时业务规则，10 个域，域内平铺              │
│   DFLegacy.Server.SelfTests/ 自测（独立 exe）                          │
└──────────────────────────────────────────────────────────────────────┘
                              ╎
                    ── 没有编译期依赖 ──
                              ╎
┌──────────────────────────────────────────────────────────────────────┐
│  Client/                    Client 层 —— Windows 客户端交付物            │
│  交付物：Windows 客户端包（解到 DNF.exe 同级目录）                         │
│                                                                      │
│   DFLegacy.Launcher/        Windows 桌面程序：配置登录、检查环境、         │
│                             安装 IJL15 模块、启动客户端与服务端            │
│   DFLegacy.Ijl15/           注入型模块：32 位游戏进程内的 ijl15.dll 替身  │
│   Patch/                   预编译补丁资产：d3d8.dll（M5 加复制步骤，见 2.4）      │
└──────────────────────────────────────────────────────────────────────┘
                              │
                              └──→ Launcher 运行期按文件名启动 DFLegacy.Server.exe
```

**两层之间没有任何编译期依赖，两个方向都是零。** 原先设想的"Client 层引用 Server 层的 `DFLegacy.Protocol`"经实测**并不存在**（见 [第 3.0 节](#30-先修正一处事实launcher-对-protocol-的引用是死引用)），已删除。唯一残留的联系是 `Launcher` 运行期按文件名启动服务端可执行文件——那是部署约定，不是依赖。

## 2. Client 层的依赖关系

### 2.1 宿主关系（三者的性质完全不同）

Client 层的三个成员彼此性质不同，但它们与服务端的关系是一致的：**没有关系**——唯一例外是 `Launcher` 在运行期启动服务端进程。**宿主决定归属**：`DFLegacy.Ijl15` 的宿主是游戏客户端进程，因此归 Client 层。**混淆宿主会直接导致错误的构建假设或加载假设。**

| 成员 | 宿主 | 与 Server 层的关系 | 缺失时的行为 |
| --- | --- | --- | --- |
| `DFLegacy.Launcher` | **用户桌面** | 与 Server 层**无编译期关系**；但运行期按 `DFLegacy.Server.exe` 启动它（见 2.3） | 用户需手工配置与启动 |
| `DFLegacy.Ijl15` | **游戏客户端进程** | 与 Server 层**无任何关系** | 游戏按原版运行，缩放与补丁能力缺失 |
| `Client/Patch/`（`d3d8.dll`） | **游戏客户端进程** | 与 Server 层**无任何关系** | 使用游戏自带 D3D8 路径 |

### 2.2 Client 层内部的依赖

同层之内并不互不相干。`Launcher` 与 `Ij15` 之间有 **4 条依赖**，其中 3 条在构建图上：

```text
                     ┌────────────────────────────────────┐
   构建期（4 条）      │  Client/DFLegacy.Launcher/         │
                     └──────┬───────────┬─────────┬───────┘
                            │           │         │
   ① ProjectReference       │           │         │  ③ Copy 任务
      ReferenceOutputAssembly=false      │         │     $(Ijl15Output)
      （仅为构建顺序）       │           │         │     → $(TargetDir) / $(PublishDir)
                            ▼           │         ▼
            Client/DFLegacy.Ijl15/      │   artifacts/native/ijl15/
              DFLegacy.Ijl15.vcxproj     │     <config>/win-x86/ijl15.dll
              （C++ / CMake / Win32）    │
                            ▲           │
   ② Content Include        │           │
      ../DFLegacy.Ijl15/Config.ini ─────┘
      （跨项目取文件，非引用）

   运行期（3 条）
   Launcher ──读同目录 ijl15.dll──→ 校验 PE 头与 x86 架构 ──安装到──→ 客户端目录
   Launcher ──读同目录 Config.ini──→ 复制到客户端目录（客户端已有的优先）
   游戏进程 ──加载──→ 客户端目录的 ijl15.dll

   零依赖
   Client/Patch/d3d8.dll             零依赖（预编译二进制）；M5 加复制步骤后随包分发（见 2.4）
```

三点必须记住的结论：

1. **`Launcher` 依赖 `Ij15` 的三种方式各不相同**：`ProjectReference`（仅保证构建顺序，`ReferenceOutputAssembly="false"`，不引用其程序集）、`Content`（直接从对方项目目录取 `Config.ini`）、`Copy`（从 `artifacts/` 取 `ijl15.dll` 产物）。**删掉其中任何一条都会破坏不同环节**，拆除时必须逐条确认。
2. **`Launcher` 对 `Ij15` 的构建依赖是 M0 要拆的那一条**（[M0 任务](04-migration-plan.md)）：三个原生 Target 改为 `Condition="Exists(...)"` 的可选内容项，`ProjectReference` 删除。

### 2.3 与 Server 层的跨层联系

只有 1 条，是运行期约定：

| # | 方向 | 形式 | 性质 |
| --- | --- | --- | --- |
| 1 | `Launcher` → `Server.exe` | 运行期按字面量 `"DFLegacy.Server.exe"` 在 `AppContext.BaseDirectory` 启动进程 | ⚠ 跨层运行期耦合，靠**同目录约定**而非引用 |

**这张表在早期版本里还有两行，都已消失**：`Server` 项目对 `RandomNative.vcxproj` 的 `ProjectReference`（违反分层，M2 删除），以及"Server 进程运行期探测 Client 层资产"（该探测随 [06 · 随机源降级为纯托管实现](06-random-source-managed.md) 的服务端解耦整体作废——`DFLegacy.RandomNative` 保留至 Client 层，**Server 层没有任何原生项目**）。编译期依赖是**两个方向都零**。

**剩下这一条是"合并包必须存在"的技术原因**：`Launcher` 在 `StartServer=true`（默认值）时按文件名在同目录找服务端，找不到就报错。若只发布纯客户端包，这个默认行为必然失败——所以 [8.2](#82-两层独立打包) 把 Windows 合并包列为主产物，而不是可选项。

它同时也说明一件事：**`Launcher` 与 `Server` 之间没有编译期依赖，却有不可忽略的部署耦合**。两者必须一起分发、且服务端可执行文件名不能改（这也是 [8.3](#83-项目命名保持现有名称) 拒绝改名的原因之一）。

### 2.4 补丁资产 `Client/Patch/d3d8.dll`

**当前状态：仓库里的 `d3d8.dll`（124 KB）没有任何东西引用它，也从未进入过任何发布包。** 实测核对：

| 检查 | 结果 |
| --- | --- |
| 代码（`.cs` / `.cpp` / `.h`）引用 | 零。`Ij15` 里的 `d3d8_scaler` 是对 D3D8 **API 的钩子**，与这个 `d3d8.dll` 二进制无关 |
| 构建配置（`.csproj` / `.vcxproj` / `CMakeLists.txt`） | 零 |
| 发布脚本（`publish.bat` / `stop.bat`） | 零 |
| CI（`build.yml` / `release.yml`） | 零。`release.yml` 只压缩 `dist/DFLegacy.Server/*`，而 `d3d8.dll` 不在其中 |
| `README.md` / `ADMIN_API.md` | 零提及，没有指导用户手工放置 |

**结论：需要分发**（已确认）。因此 M5 必须在发布流程里显式加一步，把 `Client/Patch/d3d8.dll` 复制进客户端包与合并包。实现方式二选一：

| 做法 | 位置 | 说明 |
| --- | --- | --- |
| MSBuild `Content` 项 | `Client/DFLegacy.Launcher/DFLegacy.Launcher.csproj` | 与 `Config.ini` 同样的写法，`Link="d3d8.dll"` + `CopyToPublishDirectory`；跟着 Launcher 的 `dotnet publish` 一起落盘，无需额外 CI 步骤 |
| CI 复制步骤 | `.github/workflows/release.yml` | 在打包前 `Copy-Item`。**不推荐**——它把"包内容"从项目定义里挪到了 CI 脚本里，本地 `publish.bat` 就得不到同样的结果 |

**推荐第一种**：包内容由项目定义，本地发布与 CI 发布结果一致（与 `ijl15.dll`、`Config.ini` 的处理方式一致）。这也顺带解释了它为什么会变成孤儿——`Config.ini` 有明确的项目归属（在 `Ij15` 项目目录里、由 `Launcher` 引用），而 `d3d8.dll` 原先放在 `third_party/` 下、没有任何项目引用它，于是谁也想不起来复制它。**本次改名 `third_party/d3d8to9/v1.15.1/` → `Client/Patch/`，正是为了给它一个表达用途的位置**——它不是「第三方源码依赖」，而是「要复制进客户端目录的补丁资产」，`Patch` 这个名字让归属与用途一眼可辨。

**验收**：M5 之后，客户端包与合并包中**实际存在** `d3d8.dll`（用 `ls` 检查，不靠构建成功推断）。

#### 分发带来的一项义务：许可声明

`d3d8to9` 采用的是 BSD 式条款（`Copyright (C) 2015 Patrick Mours`），其中明确要求：

> Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following disclaimer in the documentation and/or other materials provided with the distribution.

一旦按上面的决定分发 `d3d8.dll`，就构成**二进制再分发**，必须随包提供该版权声明与免责声明。当前发布包**不含任何许可文件**（`release.yml` 只压缩 `dist/DFLegacy.Server/*`），而 `ijl15.dll` 是我们自建、无此义务——**所以这项义务是随本次决定新产生的**。

处理方式：把 `Client/Patch/LICENSE-d3d8to9.md` 与 `Client/Patch/README-d3d8to9.md` 与 `d3d8.dll` **一并**复制进客户端包与合并包（同一步骤完成，成本为零）。这既是合规要求，也让包内可追溯二进制来源与版本。

## 3. 依赖方向规则

### 3.0 先修正一处事实：Launcher 对 Protocol 的引用是死引用

原设计（以及本文档的早期版本）声称跨层交互是"Client 层 ──编译期──→ Server 层的 `DFLegacy.Protocol`"，依据是 `DFLegacy.Launcher.csproj` 里的那句：

```xml
<ProjectReference Include="../DFLegacy.Protocol/DFLegacy.Protocol.csproj" />
```

**实测证明这个引用是死引用**，Launcher 从未使用它：

| 检查项 | 结果 |
| --- | --- |
| `DFLegacy.Protocol` 的 public 类型总数 | 43 |
| 逐个在 `src/DFLegacy.Launcher/*.cs` 中检索 | **0 命中** |
| Launcher 源码的 `using` 指令 | 只有 `System.Diagnostics`、`System.Runtime.InteropServices`、`System.Security.Cryptography`、`System.Net.Sockets`、`System.Text.Json` |
| 是否有全限定名用法 `DFLegacy.Protocol.` | 无 |
| Launcher 自己的配置模型 | `LauncherOptions` / `LauncherAccountProfile`，**定义在 `Program.cs` 内部**（378、398 行） |
| 对照检查（验证检索方法有效） | 同一方法在 `tests/DFLegacy.SmokeTests` 上命中 6 个 Protocol 类型，说明检索能识别真实引用 |

**结论：两层之间零编译期依赖。** 这带来两个直接后果：

1. 跨层规则简化为"**互不引用**"——不再需要为"唯一的跨层引用"开例外。
2. `DFLegacy.Protocol` 不再需要是独立程序集，**可以并入服务端主项目**，这正是 [1.1](#11-项目结构图) 中 `Server/DFLegacy.Server/Protocol/` 成立的前提。

M0 应删除该 `ProjectReference`（连同 Launcher 的原生 Target 一起处理）。

### 3.1 依赖方向

```text
 Server 层内部：
   DFLegacy.Server           零 ProjectReference（Protocol 已并入）
   DFLegacy.Server.SelfTests ──→ DFLegacy.Server（同层引用，允许）

 Client 层内部：
   DFLegacy.Launcher ──→ 无（M0 删除对 Protocol 的死引用）
   DFLegacy.Ijl15            原生项目，独立构建

 跨层：
   Client ──编译期──→ Server      ✗ 禁止（当前为零）
   Server ──编译期──→ Client      ✗ 禁止（当前为零）
   Client ──运行期──→ Server      Launcher 按文件名启动 DFLegacy.Server.exe（部署约定）
```

### 3.2 禁止清单

| 编号 | 禁止项 | 理由 |
| --- | --- | --- |
| Z1 | 任一层任何项目 `ProjectReference` 另一层任何项目 | 分层的全部意义。一旦出现，Server 层的构建图就被 Windows 污染（当前两个方向都为零） |
| Z2 | Server 层任何项目使用 `net*-windows` TFM、`UseWindowsForms`、`UseWPF` | 平台专属能力不得留在 Server 层 |
| Z3 | Server 层任何托管项目 `ProjectReference` 任一 `*.vcxproj`——**包括 `Client/DFLegacy.Ijl15/DFLegacy.Ijl15.vcxproj`** | 原生资产只能作为运行期探测的可选内容，不得进入托管构建图 |
| Z4 | Server 层任何**编译期**依赖原生产物的构建顺序（含以 `Content` 项隐式引入的 `ProjectReference`、`DependsOnTargets`、`Exec` 调用原生构建脚本） | Z1 的等价形式。`Content` 复制允许，但 `Condition="Exists(...)"` 必须成立——产物不存在时构建仍须成功 |
| Z5 | Server 层任何项目 `DllImport` **Windows 系统库**（`d3d9.dll`、`user32.dll`、`kernel32.dll`…） | 系统库没有可降级的替代路径。Server 层现在不存在任何 `DllImport` |
| Z6 | 直接 `Encoding.GetEncoding(<代码页>)` 而不先注册 `CodePagesEncodingProvider` | 见 [02 · R4](02-portability-contracts.md#r4--编码契约) |
| Z7 | 依赖平台 ANSI 代码页语义（假设 `Encoding.Default` 随平台变化） | 同上 |

Client 层不受 Z2、Z5 约束——`DFLegacy.Launcher` 的 WinForms 与 `D3D9DeviceProbe` 的 `d3d9.dll` / `user32.dll` P/Invoke，正是它属于 Client 层的原因。

**Z1–Z4 因项目合并而变得更容易满足**：Server 层只有 2 个托管项目、**没有任何原生项目**，其中主项目**没有任何 ProjectReference**，判定"它是否纯净"只需检查它的 TFM 与项列表。

**注意旧的"目录判据"已被替换。** 早期版本把不变量写成"`Server/` 树里的东西都能在三平台构建"。现在的不变量是**托管构建图纯净**：它同样被编译器无条件守卫（见下），且与"文件放在哪个目录"无关——护栏本来就只拦引用与构建顺序，从没拦过目录位置。

### 3.3 强制手段：编译器承担绝大部分，只补一处源码扫描

**不引入 `Directory.Build.props` / `Directory.Build.targets`，也不写构建期校验目标。** 实测发现 Z1–Z3 已经全部被编译器无条件覆盖，自建机制只会重复它并引入一个可被写错的组件。

#### 实测得到的拦截矩阵

| 违规 | 拦截机制 | 强度 |
| --- | --- | --- |
| **Z1** 任一层引用另一层项目 | **`NU1201`**——`net10.0` 项目引用 `net10.0-windows` 项目，**还原期直接拒绝，且与 `EnableWindowsTargeting` 无关** | 编译器，无条件 ★ |
| **Z2** Server 层项目改 `net10.0-windows` | `DFLegacy.Server.SelfTests`（`net10.0`）引用它 → **`NU1201`** | 编译器，间接 |
| **Z2** Server 层项目加 `UseWindowsForms` / `UseWPF` | **`NETSDK1100`** | 编译器，**有条件**（见下） |
| **Z3** Server 层引用 `.vcxproj` | **`MSB4278`**——`dotnet` CLI 无法加载 `.vcxproj` | 编译器，无条件 |
| **Z5** Server 层 `DllImport` Windows 系统库 | **无**——实测 `DllImport("user32.dll")` 在 macOS 上构建**通过** | ✗ 需要 CI 源码扫描 |
| **Z4** 条件化的构建顺序依赖 | 无直接拦截，但症状（发布包缺文件）会落到包内容校验（[03 · 3.0](03-build-release-ci.md#30-包内容来源与复制责任)） | 间接 |
| **Z6 / Z7** 编码契约 | 自测 + Linux 实跑 | 测试 |

实测细节（macOS 26.6.2 / .NET SDK 10.0.301）：

```text
net10.0                                       → 构建通过
net10.0-windows（仅 TFM）                      → 构建通过          ← 单靠 TFM 不拦
net10.0-windows + UseWindowsForms              → error NETSDK1100  ← 拦
net10.0-windows + UseWPF                       → error NETSDK1100  ← 拦
net10.0 + DllImport("user32.dll")              → 构建通过          ← 不拦
net10.0 引用 net10.0-windows 项目（无 EWT）      → error NETSDK1100
net10.0 引用 net10.0-windows 项目（有 EWT）      → error NU1201     ← 无条件拦
```

#### 一条必须遵守的约束：`EnableWindowsTargeting` 不能放到全局

**`EnableWindowsTargeting` 只写在 `Client/DFLegacy.Launcher/DFLegacy.Launcher.csproj` 里**，绝不放仓库根或共享 props。

理由：它是 `NETSDK1100` 那道门禁的开关。一旦在全局打开，Server 层项目加 `UseWindowsForms` / `UseWPF` 就能在 Linux 上**静默构建通过**——护栏消失。放在 Launcher 项目内则只影响它自己，Server 层的违规仍然会在 Linux 构建时暴露。

> 这是本设计的一处自我修正：早期版本把 `EnableWindowsTargeting` 放进仓库根共享 props，那实际上**削弱**了护栏。单解决方案并没有像当时判断的那样"丢掉全部自动判定"——编译器仍然守住了 Z1–Z3，前提是不去关掉它的开关。

#### 唯一需要新增的机制：CI 源码扫描 Z5

Z5（Windows 系统库 `DllImport`）是唯一"不被编译器覆盖、又完全可静态检测"的一条。用几行 grep 即可，**不需要 MSBuild 目标**：

```yaml
- name: 校验 Server 层无 Windows 系统库 P/Invoke
  shell: bash
  run: |
    if grep -rnE 'DllImport\(\s*"(d3d9|user32|kernel32|gdi32|shell32|advapi32|ole32|psapi|shlwapi|windowscodecs)' \
         Server/ ; then
      echo "::error::Server 层出现 Windows 系统库 P/Invoke（违反 Z5）"
      exit 1
    fi
```

#### 为什么不写构建期校验目标

早期设计提出过 `DFL0001`–`DFL0003` 三个 MSBuild 校验目标。实测后取消，原因有三：

1. **它想覆盖的 Z1/Z2/Z3 编译器已经无条件覆盖**，重复没有增量价值。
2. **它是我们自己的代码**——可靠性低于编译器，而它替换掉的正是编译器保证。
3. **它需要维护一份 Client 层项目清单**（`DFL0003` 用来比对跨层引用）。而 `NU1201` 不需要任何清单：只要 TFM 不同就拒绝。清单会漂移，TFM 不会。

**残余缺口**：只有"有人同时把 Server 与 SelfTests 都改成 `-windows` 且都开着 `EnableWindowsTargeting`"这类刻意为之的改动不会被拦。这不在"防意外"的范围内。

## 4. Server 层各项目职责

### 4.1 `DFLegacy.Server`（主项目）

服务端全部代码，按模块目录组织。项目文件**零 `ProjectReference`**。

项目共有 **80 个 `.cs`**：其中 **79 个位于项目根、需按模块归位**（`GameWorld/` 25 + `Game/` 39 + `Network/` 4 + `Persistence/` 3 + `Hosting/` 3 + `Services/` 2 + `PvfScript/` 1 + `Random/` 1 + 项目根的 `Program.cs` = 79），另 1 个是 `Properties/AssemblyInfo.cs`，**不参与模块归属**、原地不动。

**注意 `Protocol/` 的 11 个 `.cs` 不在这个 79 里**：它来自原独立项目 `DFLegacy.Protocol`，M0 并入后才成为本项目的一部分。所以合并后的项目共有 91 个 `.cs`（79 + 11 + AssemblyInfo）。

模块分**两大类**：非游戏模块（技术职责）与游戏模块（`GameWorld/` + `Game/`）。后者的划分方式直接取自参照项目（[9.1](#91-照搬的部分)）。

| 模块目录 | 职责 | 文件数 |
| --- | --- | --- |
| `Protocol/` | 线路协议、封包帧、CRC32、加密、PVF 归档二进制解析 | 11（4,470 行） |
| `PvfScript/` | PVF 索引与按需解密读取、目录回退、虚拟路径归一、编码契约 | 1 + 新增 |
| `Random/` | 随机源：纯托管的单一裁决实现 + 确定性测试替身（三类随机的边界见第 6 节） | 1 + 新增 |
| `Network/` | 登录主循环与 UDP 服务 | 4 |
| `Persistence/` | 状态持久化 | 3 |
| `Hosting/` | 宿主配置与后台接口模型 | 3 |

> 2026-09-29 修订:`Hosting/` 已并入 `Infrastructure/`,`CharacterSessionRegistry` 移 `Game/Session/`;`Protocol/` 迁 `Network/{Core,Protocol}`;`RuntimeState` 与随机源三文件移 `Infrastructure/`,`SerializedWriteStream` 移 `Network/Core/`;`Services/`(两个周期服务)并入 `Infrastructure/`;当前目录形态以 07 §11 实施记录为准。
| `Services/` | 周期性后台服务（`IHostedService`） | 2 |
| **`GameWorld/`** | **从 PVF 解析出的只读定义与目录**（`*Catalog` / `*Definition`），**平铺**，不含运行时行为 | 25 |
| **`Game/<业务域>/`** | **运行时业务规则**，按业务域分目录，域内平铺 | 39（9 个域） |
| 项目根 | `Program.cs`（组合根）、`server.json`、`wwwroot/` | — |

#### 游戏规则的两个模型

参照项目的 `Game/` 与 `GameWorld/` 是**一对**，各管一半，本项目照搬：

| | `GameWorld/` | `Game/<域>/` |
| --- | --- | --- |
| 内容 | 从 `Script.pvf` 解析出的**只读定义与目录** | 运行时的**行为、状态与规则** |
| 命名特征 | `*Catalog`、`*Definition`、`*Provider`、`*Resolver`、`*Index` | `*Planner`、`*State`、`*Rules`、`*Service`、`*Generator` |
| 是否随运行改变 | 否——启动时加载一次，之后只读 | 是——按会话与副本进度演进 |
| 组织方式 | **平铺**（参照项目 57 个文件全平铺，无子目录） | **按业务域分目录** |
| 依赖方向 | 不依赖 `Game/` | 可依赖 `GameWorld/` |

**为什么这样分而不是把 catalog 也塞进业务域**：一个 catalog 常被多个域共用（`ItemCatalog` 被 Inventory / Compound / Shop / ItemUpgrade 同时用到），放进任一域都会制造跨域反向引用。把它们整体放在 `GameWorld/` 并保持平铺，符合参照项目的选择，也让"改数据目录"与"改玩法逻辑"是两件在目录上一眼可辨的事。

#### `Game/` 的业务域清单

| 业务域 | 文件数 | 文件 |
| --- | --- | --- |
| `Dungeon/` | 11 | `DungeonRunState` `DungeonGroundItemState` `DungeonClearScore` `DungeonClearBonusContext` `DungeonDifficultyProgression` `DungeonFatigueRules` `DungeonRoomClearRules` `DungeonWeaknessPolicy` `DungeonItemDropPlanner` `DungeonPickupPlanner` `ClearRewardCardPlanner` |
| `Inventory/` | 9 | `CharacterInventoryPlanner` `CharacterInventorySorter` `CharacterAvatarInventoryPlanner` `CharacterCreatureInventoryPlanner` `CharacterWarehouseProgression` `CharacterItemIdentity` `CharacterItemSealing` `CharacterItemWireProjection` `MaximumTransferRoster` |
| `Quest/` | 8 | `QuestAcceptancePlanner` `QuestRewardPlanner` `QuestItemDropPlanner` `QuestItemRequirementPlanner` `QuestDungeonClearRequirement` `QuestMapRequirement` `QuestMeetNpcRequirement` `QuestPvpRankRequirement` |
| `Compound/` | 3 | `CompoundItemPlanner` `AvatarCompoundPlanner` `DisjointItemPlanner` |
| `Shop/` | 3 | `CeraShopContentPlanner` `CeraBoosterOpeningPlanner` `LotteryItemPlanner` |
| `ItemUpgrade/` | 2 | `EquipmentReinforcementPlanner` `EquipmentQualityAdjustmentPlanner` |
| `Account/` | 1 | `AccountIdentity` |
| `Creature/` | 1 | `CreatureHungerPlanner` |
| `Drop/` | 1 | `WeightedItemTable` |

**域的数量少、有的域只有 1 个文件是正常的**——参照项目同样有 `ReviveCoin/`、`Appearance/` 这样的单文件域。域是按**业务概念**分的，不是按文件数凑的；后续新增玩法时按概念落位即可。

#### 文件归类的判定规则

M0 实施时按此执行，**归类不再逐个讨论**：

1. 后缀是 `*Catalog` 或 `*Definition` → **`GameWorld/`**（共 25 个）
2. 否则按业务域关键词判入 `Game/<域>/`（共 39 个，映射见上表）
3. `Program.cs` → 项目根；`ServerOptions` / `AdminApiModels` / `CharacterSessionRegistry` → `Hosting/`
4. `EntranceService` 与三个 `*DatagramService` / `ProbeService` → `Network/`
5. `JsonGameStore` / `RuntimeState` / `SerializedWriteStream` → `Persistence/`
6. `ScriptFileSystem` → `PvfScript/`；`GameRandomSource` → `Random/`
7. 两个 `IHostedService` 周期服务（`FatigueResetService`、`WeaknessRecoveryService`）→ `Services/`。判据是**它们是否为某个玩法域的规则**：实测两者都只依赖 `CharacterSessionRegistry` + `JsonGameStore`，`FatigueResetService` **零玩法域依赖**（原来的 `Game/Stamina/` 因此取消），`WeaknessRecoveryService` 额外依赖 `DungeonWeaknessPolicy`，但它的性质是定时任务而非玩法规则

按此规则 79 个 `.cs` **全部有确定归属，无需人工再判**。

### 4.2 模块职责要点

**`PvfScript/`** — 从 `DFLegacy.Server` 抽出的脚本数据访问层。职责：PVF 归档的内存索引与按需解密读取（`DNF_SCRIPT_PACK` 格式）；目录回退模式（`SkillScriptPath` 指向解包目录的开发/测试路径）；虚拟路径归一化；**编码契约的唯一归属地**（CP949 用于 PVF 路径表、CP936 用于客户端文本、`Latin1` 用于无语义字节透传）。

它是全部跨平台风险中最难靠读代码确认的一处（代码页可用性、大小写敏感性、路径分隔符、归档偏移对齐），因此**必须配一组跨平台测试**——见 [4.3](#43-dflegacyserverselftests自测项目)。语义不变项：

- 路径比较始终 `OrdinalIgnoreCase`（PVF 内部路径是全小写的，但外部输入可能不是）。
- 归档条目读取后经 `DecryptInPlace` 做 dword 对齐的循环右移 6 位解密。
- `ReadAllText` 默认编码为 `Encoding.Default`（三平台均为 UTF-8）。
- `FileExists` 在 PVF 模式下是纯字典查找，不触发解密。

**`Random/`** — 随机源层。**纯托管的单一实现**，Server 层没有任何原生资产——原 `Server/DFLegacy.RandomNative/`（RDSEED 硬件熵源桥）已与服务端解耦并保留至 `Client/DFLegacy.RandomNative/`，理由见 [06 · 随机源降级为纯托管实现](06-random-source-managed.md)。模块文件构成、"三类随机不得混用"的边界与实现归属统一登记在 [第 6 节](#6-随机源模块)，此处不再重复。

**`Network/` 与各模块的依赖关系** — 这层是服务端的入口适配层，它的依赖方向决定了整个 Server 层的形状。实测（按类型引用统计）：

| 方向 | 对象 | 数量 |
| --- | --- | --- |
| **出边**（Network 依赖谁） | `Game/<域>` | **36 个类型** |
| | `GameWorld` | 22 个类型 |
| | `Protocol` | 7 个类型 |
| | `Persistence` | 3 个类型 |
| | `Hosting` | 2 个类型（`ServerOptions`、`CharacterSessionRegistry`） |
| | `Random` / `PvfScript` | 各 1 个类型 |
| | `Services` | **0** |
| **入边**（谁依赖 Network） | `Program`（组合根） | 4 处，即注册 4 个 `IHostedService` |
| | 其余全部模块 | **0** |

四条结论：

1. **Network 在依赖图的末端，是纯叶子。** 出边 7 个模块、入边只有组合根——没有任何模块引用它。这符合"入口层"的定位：它消费下层，不被下层复用。**改 Network 不会波及其他模块**（只要不动它调用的那些 API）。

2. **`Protocol` 不是 Network 专属。** 它的消费者分散在 `Network`（7）、`Persistence`（3）、`GameWorld`（1）、`PvfScript`（1）、`Program`（1）。它是被多模块共享的底层，不是"网络层的私有工具"——这一点在评估 Protocol 的改动范围时很重要。

3. **Network 的复杂度 99% 集中在一个文件里。** `EntranceService` 14,264 行、触及 **58 / 65 个玩法类型**；另外三个合计 793 行、每个只引用 2 个 Server 内类型（`ServerOptions` 加各自的一个协作对象）。Network 内部的边也只有一条：`EntranceService → GameplayDatagramService`。

4. **`EntranceService` 是唯一的"全业务域单体"**，这一点值得明确记录：`Game/` 里放的是纯规则与状态（`*Planner` 是近乎纯函数的计算、`*State` 是数据结构），而**协议细节与业务编排都在 `EntranceService` 里**。这与参照项目不同——它的 `Game/<域>/` 每域有 `*Service.cs` / `*ApplicationService.cs` / `*Runtime.cs` 承担编排，`Network/` 只做协议。

   **这是一个可选的后续重构方向（把编排下沉到各业务域），但不在本次范围**：它与可移植性正交，`EntranceService` 本身不引用任何 Windows 特有 API，天然可移植。它的 14k 行是**可维护性**问题，不是可移植性问题（[00 · N2](00-kickoff.md)）。

**共享状态枢纽：`Hosting/CharacterSessionRegistry`** — 这是全项目唯一一处"网络层与玩法层共享的可变状态"。它的引用者：`EntranceService`（Network）、`FatigueResetService` 与 `WeaknessRecoveryService`（`Infrastructure/`，原 `Services/`）、`Program`（注册）。意味着 Network 与两个周期服务之间存在**隐式的时序耦合**（谁在何时修改会话状态），改任一方都需要同时看另外两方。

**`Program.cs`（组合根）** — 所有平台相关决策的唯一汇聚点，六个动作各执行一次，顺序见 [第 7 节](#7-组合根契约)。

**明确不做**：不重写 `EntranceService`（14,264 行）；不拆分 `Persistence` 与 `Catalogs` 的进一步结构。目录重组只做一层分组，不做多级嵌套。

### 4.3 `DFLegacy.Server.SelfTests`（自测项目）

独立 exe，自建 runner（`Program.cs` + 退出码，无测试框架——与仓库现状一致，见 [03 · 5.2](03-build-release-ci.md#52-测试运行器风格的取舍)）。

**目录与主项目同构**，便于定位：

```text
DFLegacy.Server.SelfTests/
├── DFLegacy.Server.SelfTests.csproj
├── Program.cs                                   用例注册 + 退出码
├── Protocol/                                    协议用例
├── PvfScript/                                   [新 M3] 合成 PVF 夹具、CP936/CP949 往返、路径归一、
│                                                         目录回退大小写、归档边界用例
├── Random/                                      [新 M2] 三类随机边界（密码学一类不得被替换）
├── GameWorld/                                   WorldMapSmokeTests
└── Game/<业务域>/                               与主项目同构的域子目录
```

#### 现有 11 个用例的域归属

M0 迁移时**按下表直接归位，不需要重新分析**。依据是每个用例主要引用的类型（`*Catalog` 归该域的定义侧，`*Planner`／`*State` 归行为侧）：

| 现有用例 | 主要引用的类型 | 迁往 |
| --- | --- | --- |
| `AvatarCompoundSmokeTests` | `AvatarCompoundCatalog`、`AvatarCompoundPlanner` | `Game/Compound/` |
| `CompoundItemSmokeTests` | `CompoundCatalog`、`CompoundItemPlanner` | `Game/Compound/` |
| `DungeonClearExperienceSmokeTests` | `DungeonExperienceCatalog` | `Game/Dungeon/` |
| `HiddenDungeonQuestSmokeTests` | `DungeonDifficultyProgression`、`HiddenDungeonUnlockCatalog`、`QuestCatalog` | `Game/Dungeon/` |
| `ItemIdentitySmokeTests` | `Character*InventoryPlanner`、`DungeonGroundItemState`、`DungeonPickupPlanner` | `Game/Inventory/` |
| `ItemSealingSmokeTests` | `SealState`（封印状态） | `Game/Inventory/` |
| `QuestMapSmokeTests` | `DungeonRunState`、`QuestMapRequirement`、各掉落目录 | `Game/Quest/` |
| `QuestPvpRankSmokeTests` | `QuestPvpRankRequirement` | `Game/Quest/` |
| `EquipmentQualitySmokeTests` | `ResealCatalog`、`ItemAttributeModificationPlanner` | `Game/ItemUpgrade/` |
| `EquipmentReinforcementSmokeTests` | `EquipmentReinforcementCatalog`、`EquipmentReinforcementPlanner` | `Game/ItemUpgrade/` |
| `WorldMapSmokeTests` | `WorldMapCatalog` | `GameWorld/`（它只测目录，`Game/` 下无 WorldMap 域） |

两点说明：

- 两个用例含多个域的类型时（`HiddenDungeonQuest`、`ItemIdentity`、`QuestMap`），**按被测的主动能归位**，而不是按引用到的每个类型拆散。测试是围绕一个业务场景写的，拆散会使它失去归属。
- `WorldMapSmokeTests` 归 `GameWorld/` 而不是 `Game/`：它验证的是 `WorldMapCatalog` 的解析结果，属定义侧。这也说明 `GameWorld/` 下确实需要测试目录，不能假定那里只有数据。

#### 与主项目目录的对应关系

| 主项目 | 自测项目 |
| --- | --- |
| `GameWorld/`（25 个静态定义） | `GameWorld/` |
| `Game/<域>/`（10 个域，运行时规则） | `Game/<域>/`，只建有用例的域 |
| `Protocol/`、`PvfScript/`、`Random/` | 同名目录 |
| `Network/`、`Persistence/`、`Hosting/`、`Services/` | 无独立用例；相关覆盖在主项目自测入口内 |
| 其余 30 个目录 | 自测不必与主项目一一对应，**只建有用例的目录** |

**为什么测试独立成项目而不是放进主项目**（与参照项目的有意偏离）：[9.3](#93-与参照项目的两处有意偏离) 说明。

> 原 §4.4「`DFLegacy.RandomNative`（服务端可选原生资产，可选跨平台化）」随服务端与该模块的解耦一并移除（模块保留至 Client 层），故小节编号跳过（见 [06 · 随机源降级为纯托管实现](06-random-source-managed.md)）。

## 5. Client 层各项目职责

### 5.1 `DFLegacy.Launcher`（仅构建解耦）

WinForms 启动器。**功能不跨平台，本设计只做构建解耦。**

**变化**：

- 删除 `BuildIjl15` / `CopyIjl15ToOutput` / `CopyIjl15ToPublish` 三个隐式触发原生构建的 Target。改为：`ijl15.dll` 作为 `Condition="Exists(...)"` 的可选内容项；原生构建改为显式 `-p:BuildIjl15Native=true` 才触发。
- **删除对 `DFLegacy.Protocol` 的 `ProjectReference`**（死引用，见 [3.0](#30-先修正一处事实launcher-对-protocol-的引用是死引用)）。
- `launcher.json` 从仓库根移入本项目目录（M0）。
- 添加 `<EnableWindowsTargeting>true</EnableWindowsTargeting>`，使 Launcher 能在非 Windows 上交叉编译。

**保持不变**：`PlatformTarget` 语义、`ApplicationIcon`、`Config.ini` 内容项、WinForms UI、`D3D9DeviceProbe` 的 P/Invoke、`LauncherOptions` 配置模型（定义在 `Program.cs` 内）。

### 5.2 `DFLegacy.Ijl15`（不变）

32 位游戏进程注入模块。导出 stdcall 修饰名（`_ijlInit@4` 形式），做 D3D8 缩放、PE 内存补丁、窗口子类化、消息音效，链接 `ole32` / `psapi` / `shlwapi` / `windowscodecs`。

**架构上不做任何变更。** 它归入 Client 层的意义在于：不出现在 Server 层的任何依赖图里，也不是构建 Server 层的隐含前置条件。

## 6. 随机源模块

**随机源是纯托管的单一实现，Server 层没有任何原生资产**——原 `Server/DFLegacy.RandomNative/`（RDSEED 硬件熵源桥）已与服务端解耦并保留至 `Client/DFLegacy.RandomNative/`，理由与全部细节见 [06 · 随机源降级为纯托管实现](06-random-source-managed.md)。原 §6.1（`Random/` 与 `RandomNative` 谁是主体、谁是插件）与原 §6.2（RID 探测 + 失败降级）随服务端解耦一并移除，故小节编号跳过。

模块落在 `Server/DFLegacy.Server/Random/`，三个文件（职责见 [4.1](#41-dflegacyserver主项目)）：

| 文件 | 角色 |
| --- | --- |
| `IDropRandomSource.cs` | 对外缝，仅 1 个成员 `int Next(int exclusiveMaximum)`；**保留现名**，42 处调用点零改动 |
| `GameRandomSource.cs` | 生产实现，基于 `Random.Shared`；`GameRandomSource.Shared` 是唯一实例 |
| `SeededRandomSource.cs` | 确定性实现，自测与问题复现用 |

**三类随机不得混用**：

| 类别 | 实现 | DFLegacy 实例 |
| --- | --- | --- |
| **密码学随机** | `RandomNumberGenerator`，**必须保留** | `EntranceHandshakeReplies.cs` 的会话密钥材料（`EntranceSessionState.KeyMaterial`） |
| **服务端裁决随机** | 本模块的单一托管实现（`Random.Shared`） | 42 处调用点 / 16 个文件 |
| **客户端可复算随机** | 客户端 LCG 的服务端复刻 | **当前无实现**（是否引入见 [06 · Q1](06-random-source-managed.md#7-待确认问题)） |

判定走哪一类不再依赖任何探测：三个平台走完全相同的托管代码路径，不存在"原生库在不在"这一状态。

### 6.3 将来若需要托管扩展

如果出现**必须用托管程序集承载**的 Windows 专属能力（例如 Windows 事件日志、性能计数器），届时才评估是否引入托管扩展契约。**那是一个待决问题，不是现在就该建的基础设施**——见 [04 · 待决问题](04-migration-plan.md#5-待决问题)。

## 7. 组合根契约

`Server/DFLegacy.Server/Program.cs` 是所有平台相关决策的**唯一汇聚点**。以下动作必须在此按固定顺序发生，且各仅一次：

1. **日志提供程序选择**。当前已 `ClearProviders()` 以避免缺管理员权限时 Windows 事件日志源创建失败，该决定保留，并在日志中显式说明降级原因。
2. **代码页提供程序注册**。`Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)` 一次，在读取任何配置或脚本之前。
3. **路径解析器构造**。确定配置锚点与数据目录（`DFLEGACY_HOME` 覆盖优先），据此解析 `server.json`、`Script.pvf`、`data/state.json`。
4. **随机源选定**。构造纯托管的单一实现（`Random/` 模块的 `GameRandomSource.Shared`）并打印提供程序名，**不做任何原生探测**。
5. **配置绑定与校验**。绑定后立即校验路径存在性与端口可用性，缺失项以**汇总清单**报告，而不是在后续初始化中逐个抛出。
6. **服务注册与启动**。

**反例（当前存在的问题）**：`Encoding.RegisterProvider` 目前散落在 15 处调用点、15 个文件。重复注册幂等，功能上无害，但这种分布使编码契约没有单一归属地，也让 `PvfScript/` 无法独立测试。目标收敛到两处：组合根与 `PvfScript/` 各一次。

## 8. 解决方案文件与独立打包

### 8.1 唯一的解决方案

| 解决方案文件 | 成员 | 用途 | 可用平台 |
| --- | --- | --- | --- |
| `DFLegacy.Emulator.slnx` | 全部 5 个项目（服务端主项目、自测、Launcher、Ijl15、RandomNative） | 全量 IDE 可见与开发；托管项目经 MSBuild 构建 | win（MSBuild；`dotnet` CLI 因含 `.vcxproj` 报 `MSB4278`，服务端跨平台按项目构建） |

两个 `.vcxproj`（Ijl15、RandomNative）在解决方案内以 `<Build Project="false" />` 标注为**不参与解决方案构建**（2026-09-23 修订，IDE 全量可见），原生构建统一由 `patch.bat` 驱动各自的 `build-native.ps1`（CMake），产物落 `artifacts/native/<module>/<config>/…`。这一标注同时消除平台矩阵冲突——Ijl15 仅 Win32、RandomNative 仅 x64，否则 `msbuild` 会以 `MSB8013` 请求不存在的 `Release|x64`。

含 `.vcxproj` 的解决方案在 `dotnet` CLI 上是**硬错误**（`MSB4278`），不是"跳过该平台的项目"——这个实测结论（[00 · 3.6](00-kickoff.md#36-全仓库能否只用一个解决方案)）在放回原生项目后重新生效。服务端跨平台验证与构建因此按项目进行：`dotnet build` 直接指定两个服务端 `.csproj`。

**命名**：文件名保持不变。Windows 用户既有的 `msbuild .\DFLegacy.Emulator.slnx` 命令继续可用（仅构建托管项目，原生成员被显式排除）；Linux/macOS 上使用 `dotnet build <csproj>` 按项目构建。

### 8.2 两层独立打包

两层的构建与打包互相独立，产物互不依赖。

```text
服务端包（Server 层）
  dotnet publish Server/DFLegacy.Server/DFLegacy.Server.csproj \
    -c Release -r <win-x64|linux-x64> -o dist/server-<rid>
  → 依赖：无项目依赖（全部托管代码在同一项目内）
  → 携带：无原生产物（Server/ 下没有原生项目，随机源是纯托管实现）

客户端包（Client 层）
  dotnet publish Client/DFLegacy.Launcher/DFLegacy.Launcher.csproj \
    -c Release -r win-x64 -o dist/client-win-x64
  → 依赖：无（M0 删除对 Protocol 的死引用后，Client 层零项目依赖）
  → 携带：可选的原生模块 ijl15.dll（存在则复制）
```

**两层的构建图完全不相交**：**服务端包不含任何原生产物**（`Server/` 下没有原生项目，随机源已降级为纯托管实现，见 [06 · 随机源降级为纯托管实现](06-random-source-managed.md)），客户端包带上 `Client/DFLegacy.Ijl15/` 的产物。`Ij15` 不在任何 `.csproj` 的引用链上，靠 `Condition="Exists(...)"` 的可选内容项进入产物。

**客户端包完全自足**：不再需要编译 Server 层的任何代码。原先实测中"客户端打包会编译 Protocol"这一条，在 M0 删除死引用后不再成立。

**三种交付形态、四个发布包**（取舍见 [03 · 6](03-build-release-ci.md#6-发布流程releaseyml)）：

> 2026-09-23 修订：CI 发布只产出两个**服务端**包；客户端产物由本地 `patch.bat` 发布到 `artifacts\native\launcher\`。下表保留作为形态基准。

| 包 | 内容 | 面向 |
| --- | --- | --- |
| 服务端包 `<tag>-{win-x64,linux-x64}` | 服务端可执行 + 配置 + `wwwroot` | 部署服务端 |
| 客户端包 `<tag>-client-win-x64` | Launcher + `ijl15.dll` + `d3d8.dll`\* + `launcher.json` + `Config.ini` | 只需要启动器 |
| Windows 合并包 `<tag>-win-x64` | 上面两者之和，同目录 | 现有用户习惯的部署形态（2026-09-23 修订后由本地 `patch.bat` 产出，不再由 CI 产出） |

\* `d3d8.dll` 需在 M5 加一个复制步骤才进包，见 [2.4](#24-补丁资产-clientpatchd3d8dll) 与 [04 · Q13](04-migration-plan.md#5-待决问题)。

**为什么保留合并包**：现有用法是"把发布目录放进客户端目录内（与 `DNF.exe` 同级），运行 Launcher 由它按需拉起服务端"。如果分层后只出两个独立包，这个用法就失效了。合并包保证 [00 · G4](00-kickoff.md)。

### 8.3 项目命名：保持现有名称

目录名 = 项目文件名 = 程序集名，沿用现有命名，**不重命名**：

| 目录 | 项目 / 程序集 | 说明 |
| --- | --- | --- |
| `Server/DFLegacy.Server/` | `DFLegacy.Server` → `DFLegacy.Server.exe` / `.dll` | 服务端主项目 |
| `Server/DFLegacy.Server.SelfTests/` | `DFLegacy.Server.SelfTests` | 自测 |
| `Client/DFLegacy.Launcher/` | `DFLegacy.Launcher` | 启动器 |
| `Client/DFLegacy.Ijl15/` | `DFLegacy.Ijl15` | 原生 |

**为什么不改成不带点的 `DFLegacyServer`**：`DFLegacy.Server.exe` 这个名字是**用户可见的交付物标识**，被多处硬依赖——

| 位置 | 内容 |
| --- | --- |
| `Client/DFLegacy.Launcher/Program.cs:281` | `Path.Combine(AppContext.BaseDirectory, "DFLegacy.Server.exe")`——Launcher 靠它启动服务端 |
| `stop.bat:8` | `set "SERVER_EXE=DFLegacy.Server.exe"`，并用它 `taskkill` |
| `dist/DFLegacy.Server` | 发布输出目录名，`publish.bat` 与两个 workflow 共 6 处 |

重命名会连带修改 Launcher 的启动逻辑（跨层耦合）、`stop.bat`、发布目录名，并使**现有用户的脚本与快捷方式失效**——与 [00 · G4](00-kickoff.md)（现有 Windows 交付方式不变）冲突。收益只是名字里少一个点。

**若确实要改**，应当作为独立变更评估，并把上表四处一起处理；不要与本次可移植性重构混在一起。

### 8.4 两个运行时配置文件归位

`server.json` 与 `launcher.json` 从仓库根移进各自的项目（M0）。

| 文件 | 现在 | 目标 | 依据 |
| --- | --- | --- | --- |
| `server.json` | 仓库根 | `Server/DFLegacy.Server/server.json` | Server 层的运行时配置，归 Server 层项目 |
| `launcher.json` | 仓库根 | `Client/DFLegacy.Launcher/launcher.json` | Client 层的运行时配置，归 Client 层项目 |

这是分层规则的自然推论：配置文件是**某一层的资产**，放在仓库根等于让两层的东西混在一个不属于任何层的公共位置——与"位置跟着交付面走"是同一条原则。`Config.ini` 已经符合这条规则（在 `Client/DFLegacy.Ijl15/` 下），另两个只是补齐。

**只改位置，不改内容。** 两个文件的**全部配置键与默认值保持原样**，消费端也不需要改：

- `server.json` 的 `DFLegacy` 节：`Admin`、`Entrance`、`CharacterDatagram`、`GameplayDatagram`、`GameProbe`、`Channel`、`Drop`、`Experience`、`DataPath`、`ScriptPvfPath`、`SkillScriptPath`、`MaximumPacketLength`、`RejectBadCrc32`、`EnablePacketTracing`、`HexDumpLimit` —— 由 `ServerOptions` 消费，不动。
- `launcher.json`：`ClientPath`、`EntranceHost`、`EntrancePort`、`Accounts`、`AccountIndex`、`StartServer`、`D3D9Adapter`、`D3D9TimeoutSeconds`、`D3D9RetryIntervalSeconds` —— 由 `LauncherOptions` 消费，不动。

唯一的值变化是 `ScriptPvfPath` 会在 M4 由反斜杠改为正斜杠（[R3](02-portability-contracts.md#r3--路径契约)），那是独立的一项改动，与本次移动无关。

**发布产物布局不变**：两个文件仍被复制到可执行文件旁边（`server.json` 在服务端包根目录、`launcher.json` 在客户端包根目录），用户可见的位置与现在完全一致（[03 · 3](03-build-release-ci.md#3-发布布局)）。运行时的 `Path.Combine(AppContext.BaseDirectory, "server.json")` 也不必改——它读的是输出目录，不是源码目录。

#### 实现要点：两个项目的项类型不同，别照抄

移动本身只是一次 `git mv` 加两行 csproj 调整，但有一个**会静默失败**的坑，已实测确认：

| 项目 | SDK | `*.json` 的默认项类型 | 正确写法 |
| --- | --- | --- | --- |
| `DFLegacy.Server` | `Microsoft.NET.Sdk.Web` | **`Content`**（Web SDK 通配 `**/*.json`） | `<Content Update="server.json">` + 复制元数据 |
| `DFLegacy.Launcher` | `Microsoft.NET.Sdk` | **`None`**（基础 SDK 把剩余文件通配为 `None`） | `<None Update="launcher.json">` + 复制元数据 |

**两个项目的写法必须不同。** 实测中若对 Launcher 照抄 Server 的 `Content Update="launcher.json"`，MSBuild **不报错也不警告**，但 `launcher.json` 不出现在构建输出目录——因为 `Update` 只修改已存在的项，而基础 SDK 从未把它创建为 `Content`。文件丢了，构建却显示成功。

验收方式是唯一的可靠判据（4 条 `ls`，见 [10.3](#103-验收)）：

```bash
ls Server/DFLegacy.Server/bin/Release/net10.0/server.json
ls Client/DFLegacy.Launcher/bin/Release/net10.0-windows/launcher.json
```

## 9. 为什么采用这个形式：参考项目的对照

参照项目 `ServerS4A21` 采用的是**层目录 / 项目目录 / 模块目录**三级结构，本项目照搬其形式；其中游戏规则的划分方式（`GameWorld/` + `Game/<业务域>/`）是最主要的一条照搬。以下说明照搬了什么、以及两处有意偏离。

### 9.1 照搬的部分

| 参照项目 | 本项目 | 依据 |
| --- | --- | --- |
| `Server/DfoServer/` 单项目承载全部服务端代码 | `Server/DFLegacy.Server/` | 服务端是**一个可执行交付物**，内部按模块分目录比按项目切分更贴近它的真实边界 |
| `Server/DfoServer/GameWorld/`（57 个，平铺） | `Server/DFLegacy.Server/GameWorld/`（25 个，平铺） | **脚本静态定义与目录**；两边都保持平铺，因为 catalog 常被多个业务域共用，放进任一域都会造成跨域反向引用（[4.1](#游戏规则的两个模型)） |
| `Server/DfoServer/Game/<业务域>/`（34 个域，`Game/` 本身无直属 `.cs`） | `Server/DFLegacy.Server/Game/<业务域>/`（9 个域，39 个 `.cs`） | **运行时业务规则按业务域分目录**；域内平铺，域按业务概念分而非按文件数凑 |
| `Server/DfoServer/Network/`、`Infrastructure/`、`Sqlite/` | `Network/`、`Hosting/` + `Services/`、`Persistence/` | 技术职责模块，与游戏模块并列 |
| 共享库 `Tool/PvfLib` 独立成项目 | 无共享库需要独立 | 参照项目需要独立是因为 `DfoServer` 与 `Tool/PvfProxy` 都引用它。本项目实测**零跨层引用**，因此 Protocol 可以并入主项目（[3.0](#30-先修正一处事实launcher-对-protocol-的引用是死引用)） |
| 目录即命名空间（`Network/` → `DfoServer.Network.Parsers`） | 见 [9.4](#94-命名空间策略分两步) | 同上 |
| `Server/DfoServer.sln` 与项目同层 | `DFLegacy.Emulator.slnx` 放仓库根 | 参照项目的解决方案只覆盖 `Server/` 与 `Tool/`，本项目要覆盖 `Server/` 与 `Client/` 两层，放根更自然 |

### 9.2 原生项目为什么不并进托管项目

Server 层把 4 个托管项目收成了 1 个项目 + 模块目录（[D3](00-kickoff.md)）。但**原生项目并不进托管项目**——Client 层的 `Ij15` 如此。Client 层因此收不成一个项目。Server 层已经没有原生项目（原 `DFLegacy.RandomNative` 已按 [06 · 随机源降级为纯托管实现](06-random-source-managed.md) 移至 Client 层），"原生项目与主项目平级"这个问题在该层不再存在。

| | Server 层 | Client 层 |
| --- | --- | --- |
| 成员性质 | 2 个 `net10.0` **托管**程序集（同一编译图） | 1 个托管 Exe + 1 个 **C++ 原生** DLL |
| 目标框架 | 托管：同一个（`net10.0`） | 托管：`net10.0-windows`；原生：无 TFM |
| 目标架构 | 托管：与架构无关（AnyCPU） | Launcher：AnyCPU；`Ij15`：**x86** |
| 编译工具链 | `dotnet` / MSBuild（只有托管），单一套 | `dotnet` / MSBuild（托管）**与** MSVC + CMake（原生），两套互不通 |
| 产物宿主 | 服务端进程 | Launcher → 用户桌面；`Ij15` → 32 位游戏进程 |
| 合并可行性 | ✅ **已合并**：该层没有原生项目可并 | ❌ **技术上不成立** |

**四条硬约束，任一条都足以否决"把原生项目合并进托管项目"**：

1. **`.NET SDK 不能编译 C++。** `Ij15` 是 `LANGUAGES CXX` 的 CMake 项目，靠 MSVC 编译。塞进 `.csproj` 只能靠自定义 target 调 CMake——那是把裸命令藏进构建文件，比现在的显式脚本更糟。这一条同时说明原生项目成不了"模块目录"的一员。
2. **架构不同。** `Ij15` 必须是 **x86**（`CMAKE_SIZEOF_VOID_P EQUAL 4`、`-A Win32`，导出名是 32 位 stdcall 修饰形式 `_ijlInit@4`）。一个 `.csproj` 无法产出指定架构的原生 DLL，而它还要被 32 位的游戏进程加载。
3. **架构约束在运行期被强制执行。** `Launcher/Program.cs` 的 `Ijl15ReplacementInstaller` 会校验 `IMAGE_FILE_MACHINE_I386 (0x014C)`，**主动拒绝**任何非 32 位 x86 的 `ijl15.dll`。所以"把 Ijl15 做成 64 位再统一"这条路也走不通。
4. **连唯一的退路 C++/CLI 也被堵死。** 把 C++ 与托管代码放进同一个项目，技术上只有一条路——让 C++ 项目用 `/clr`（C++/CLI 混编）。但 `ijl15.dll` 是**注入 32 位游戏进程**的模块，一旦引入 CLR 依赖，游戏进程就必须能加载 .NET 运行时；而它现在只链 `ole32` / `psapi` / `shlwapi` / `windowscodecs`，是**纯原生**（`CMakeLists.txt` 里没有任何 CLR 开关）。为"项目数少一个"而给被注入的游戏模块加上 CLR 依赖，代价完全不成比例

**托管侧其实已经合并到底了**：Client 层只有 `DFLegacy.Launcher` 一个托管项目，没有第二个可以并进来的同类。再往下合并只能跨工具链，做不到。

**关于"一个解决方案"**：这个词有两个含义，两个都要澄清——

| 含义 | 现状与建议 |
| --- | --- |
| **一个解决方案文件（`.slnx`）** | **已经是唯一一个，且覆盖全部两层**（[8.1](#81-唯一的解决方案)）：成员是全部 5 个项目（2026-09-23 修订放回 Launcher 与两个原生项目，IDE 全量可见）；两个 `.vcxproj` 标注为不参与解决方案构建，原生构建统一走 `patch.bat`（见 [03 · 4.5](03-build-release-ci.md)）。**不建议为某一层另建解决方案**：全仓库项目已尽数入列，再建任何解决方案都会造成成员重叠、违反"单一构建入口"，而且会让层边界校验（[3.3](#33-强制手段编译器承担绝大部分只补一处源码扫描)）因成员分裂而失效 |
| **一个编译项目（`.csproj`）** | **技术上不成立**，理由见上面 4 条硬约束 |

**因此"把一层里的项目合并成一个"这件事，无论按哪种含义都得不到更简的结构**：解决方案层面本来就只有一个；项目层面被工具链与架构挡住。

**结论：Client 层的项目结构是 3 个项目（Launcher / Ijl15 / RandomNative）+ 1 个资产目录（Patch）；Server 层没有原生项目。** Ijl15 的编译工具链、目标架构与宿主进程都由技术约束决定，它进不了任何托管构建图，这不是风格选择。可以优化的只有编排方式——即 M0 要做的"让 Launcher 的构建不再隐式驱动原生构建"。

### 9.3 与参照项目的两处有意偏离

**一、自测独立成项目，不放进主项目的 `SelfTests/` 目录。**

参照项目把 `SelfTests/` 放进 `DfoServer/`，用 `--selftest-*` 参数分发。本项目不这么做，原因是**宿主启动门槛不同**：`DfoServer` 是普通 exe，自测可以直接跑；而 `DFLegacy.Server` 是 Web 宿主，`Program.cs` 以 `optional: false` 读取 `server.json`，把自测塞进宿主意味着"跑测试前必须先有一份合法配置"，且测试代码会随发布包分发。

因此保留独立 exe 的形态，只把它移到 `Server/` 下并与主项目目录同构。这样既拿到目录层级的清晰度，又不把测试与宿主启动绑在一起。**代价**：多一个项目，且测试项目引用主项目（依赖面比"独立测试小项目"大）——但这是可接受的，因为主项目已无任何项目依赖。

**二、不引入 `Tool/` 目录。**

参照项目有 `Tool/PvfLib`、`Tool/PvfProxy`、`Tool/Sqlite`。本项目没有对应的独立工具，`PvfArchiveReader` 是服务端运行期的一部分而非离线工具，因此并入主项目的 `Protocol/`。将来若出现真正的离线工具（例如 PVF 解包器），再按参照项目加 `Tool/`。

### 9.4 命名空间策略：分两步

参照项目的实质之一是"目录即命名空间"。本项目分两步落地，避免把 79 个文件的命名空间改动与结构重组混在一起：

| 步骤 | 内容 | 风险 |
| --- | --- | --- |
| **第一步（M0）** | 只移动文件到模块目录，**命名空间不改**（`namespace DFLegacy.Server` 保持扁平） | 零风险：纯 `git mv`，编译结果不变 |
| **第二步（可选，独立提交）** | 按目录追加命名空间段（`DFLegacy.Server.Network` 等），并相应调整 `using` | 79 个文件的机械改动；只做移动不做重命名，便于二分定位 |

**为什么拆两步**：目录分组带来的结构清晰度在第一步就已全部拿到；命名空间是**可选的进一步一致性**，而它一次触及 79 个文件，若与结构重组同一提交，一旦出问题无法区分是"移动错了"还是"命名空间改错了"。第一步的验收很简单——**编译产物与移动前逐项一致**。

## 10. 目录重组的实施要点

这是本次重构中改动面最大的一步（移动 79 + 11 个 `.cs`，涉及 3 个项目的目录位置）。**必须作为纯 `git mv` 的独立提交**，不与任何语义改动混在一起。

### 10.1 完整影响清单

**一条关键的好消息**：`Server/` 与 `Client/` 与原来的 `src/` **深度相同**，所以所有以 `../..` 上溯仓库根的相对路径**全部不用改**——包括两个 `build-native.ps1` 的 `Split-Path ... -Parent` 上溯、`Launcher.csproj` 的 `Ijl15Output`（`../../artifacts/native/ijl15/...`）。同样，`Client/DFLegacy.Launcher/` → `Client/DFLegacy.Ijl15/` 仍是 `../DFLegacy.Ijl15/`，跨项目兄弟引用也不变。这一条把影响面显著缩小：**真正要改的只有绝对定位到旧 `src/` 名字的地方**。

| 位置 | 需要调整的内容 |
| --- | --- |
| `DFLegacy.Emulator.slnx` | 全部 `<Project Path>` 改写（`src/*` → `Server/*`、`Client/*`），移出 2 个 `.vcxproj`，并**移除 `DFLegacy.Launcher`**（解决方案只保留 Server 层成员） |
| `DFLegacy.Server.csproj` | 项目文件随目录移动（内容基本不变）；删除对 Protocol 的 `ProjectReference`；`server.json` 的项路径（该文件同时移入项目目录） |
| `DFLegacy.Protocol.csproj` | **项目文件消失**（并入主项目） |
| `DFLegacy.Launcher.csproj` | 项目文件随目录移动；删除对 Protocol 的死引用；三个原生 Target 改造（M0 的另一项任务）。`Config.ini` 与 `Ijl15Output` 的相对路径**不变** |
| `DFLegacy.Ijl15/` | 目录整体移入 `Client/`；`CMakeLists.txt` 内部为项目相对路径，无需改；`build-native.ps1` 上溯层数不变 |
| `DFLegacy.Server.SelfTests.csproj` | 新位置与新的项目引用路径（引用 `../DFLegacy.Server/DFLegacy.Server.csproj`） |
| `.github/workflows/build.yml`、`release.yml` | 全部 `.\src\...` 路径改为 `.\Server\...` / `.\Client\...`；解决方案成员变化 |
| `publish.bat` | `src\DFLegacy.Server`、`src\DFLegacy.Launcher` 路径；增加对 `build-native.ps1` 的显式调用 |
| `stop.bat` | 无（只依赖 exe 名，而 exe 名不变） |
| `Server/DFLegacy.Server/Properties/` | **原地不动**（随项目目录移动）：`AssemblyInfo.cs` 无路径依赖；`launchSettings.json` 只被 Visual Studio 调试读取，不参与发布 |
| `Server/DFLegacy.Server/DFLegacy.Server.ico`、`Client/DFLegacy.Launcher/DFLegacy.Launcher.ico` | **原地不动**：`ApplicationIcon` 用的是项目相对路径 |
| `Server/DFLegacy.Server/wwwroot/**`（12 个文件） | **原地不动**：`Content Update="wwwroot/**"` 是项目相对通配，随项目目录移动 |
| `Client/DFLegacy.Launcher/Program.cs` | 无（不依赖被移动的任何路径；`DFLegacy.Server.exe` 名不变） |
| `Client/DFLegacy.Launcher/launcher.json`、`Server/DFLegacy.Server/server.json` | 从仓库根移入，串行在两层的项目目录下 |
| `docs/design/**` | 本文档集中的路径示例 |

### 10.2 覆盖率核查

重组的前提是"每个现有文件都有确定去向"。本设计对此做过一次完整核查，方法与结论如下，**实施后应重跑一次确认**：

```bash
# 权威清单
git ls-files > /tmp/tracked.txt

# 逐文件核对其名字是否出现在 docs/design/ 中（按文件名主干比对，忽略扩展名）
# 期望：除下面“合理排除”的项外，全部命中
```

| 范围 | 数量 | 覆盖情况 |
| --- | --- | --- |
| 仓库受跟踪文件总数 | 177 | — |
| `Server/DFLegacy.Server/` 项目根 `.cs` | 79 | ✅ 全部有模块归属（`GameWorld/` 25 + `Game/` 39 + 技术模块 14 + `Program.cs`） |
| `Properties/`（2 个） | 2 | ✅ 原地不动（§10.1） |
| `wwwroot/`（12 个） | 12 | ✅ 原地不动（§10.1） |
| `DFLegacy.Protocol/`（11 个 `.cs`） | 11 | ✅ 全部并入 `Protocol/` |
| `Client/DFLegacy.Launcher/`（4 个 `.cs` + `.ico`） | 5 | ✅ 全部列举 |
| `Client/DFLegacy.Ijl15/`（30 个） | 30 | ✅ `include/`、`src/`（12 组）、`tests/` 全部列举 |
| `tests/DFLegacy.SmokeTests/`（12 个 `.cs`） | 12 | ✅ 11 个用例逐个给出域归属（§4.3）+ `Program.cs` |
| `Client/Patch/`（3 个） | 3 | ✅ 列举 |
| `.github/workflows/`（2 个） | 2 | ✅ 在影响清单中 |

**唯一的结构性遗漏在核查中被发现并已修正**：`server.json` 与 `wwwroot/` 曾被误画为 `DFLegacy.Server/` 的**兄弟项**（即 `Server/server.json`），实际应在项目目录内。这类层级错误不会导致构建失败，但会让实施者放错位置，**因此结构图的每次改动都值得用 `git ls-files` 复核一遍**。

### 10.3 验收

| 检查 | 判据 |
| --- | --- |
| 服务端三平台构建 | `dotnet build Server/DFLegacy.Server/DFLegacy.Server.csproj` 与 `dotnet build Server/DFLegacy.Server.SelfTests/DFLegacy.Server.SelfTests.csproj` 在 win / linux / osx 均成功（解决方案含 `.vcxproj`，仅 MSBuild 可构建） |
| 层边界不变量 | 托管项目（3 个 `.csproj`）的项列表里没有指向 `*.vcxproj` 的 `ProjectReference`；两个 `.vcxproj` 在 `.slnx` 中标注为不参与构建（`<Build Project="false" />`）；**Server 层无原生项目**，判据见 [06 · 8](06-random-source-managed.md#8-验收标准) 的四条（源码无原生痕迹 / 构建图无原生节点 / 包内无 `runtimes/*/native/*` / 三平台 provider 字段为托管实现） |
| 移动无副作用 | 编译产物与移动前**逐项一致**（同一份文件清单、同一批次类型） |
| 自测通过 | `DFLegacy.Server.SelfTests` 在三平台通过 |
| 配置文件位置 | `Server/DFLegacy.Server/bin/**/server.json` 与 `Client/DFLegacy.Launcher/bin/**/launcher.json` **实际存在**（项类型差异会静默丢文件，见 [00 · 4](00-kickoff.md)） |
| Windows 交付不变 | `publish.bat` 产出的 `dist/DFLegacy.Server` 与基线逐项一致，含 `ijl15.dll` |
| 服务端可运行 | 服务端在 Linux 上启动，日志中的随机源 `provider` 字段为纯托管实现，且不再出现 `cpuRdSeed` / `nativeBridge` / `rdSeedEnabled` 这类原生能力字段 |
