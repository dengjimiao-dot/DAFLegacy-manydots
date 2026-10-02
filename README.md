# DAFLegacy

<a href="#运行环境"><img src="https://img.shields.io/badge/Windows-0078D4" alt="Windows" /></a>
<a href="#构建与启动"><img src="https://img.shields.io/badge/.NET-10-512BD4" alt=".NET 10" /></a>
<a href="#模块说明"><img src="https://img.shields.io/badge/UI-Web-7A3E9D" alt="Web UI" /></a>
<br />
<a href="#运行环境"><img src="https://img.shields.io/badge/architecture-x86%20%2F%20x64-455A64" alt="x86 / x64" /></a>
<a href="#许可证"><img src="https://img.shields.io/badge/license-0BSD-2E7D32" alt="0BSD License" /></a>

面向赛里斯服公测 ACT1 客户端的本地模拟服务端、启动器与客户端补丁。

项目从 `Script.pvf` 读取客户端配置数据，并将用户数据在本地持久化。

项目不包含客户端、官方服务端文件或 PVF，相关资源的权利归原权利人所有，请自行确保使用资源的合法性。

本项目用于个人学习、研究及技术交流使用，任何项目副本均与本项目作者无关。

本项目所有资源均来源于互联网公开信息，仅用于学习测试网络连通性与技术实现方式。

本项目不对资源的合法性、准确性、完整性作任何保证，也不承担任何直接或间接责任。

如本项目内容侵犯了您的合法权益，请及时联系删除，作者将在第一时间进行处理。

使用本项目即代表您已阅读并同意以上声明内容。

## 模块说明

| 模块 | 作用 |
| --- | --- |
| `DAFLegacy.Server` | 主业务、PVF 数据读取、账号角色持久化、TCP/UDP 服务及 HTTP 管理接口。 |
| `Server/DAFLegacy.Server/Protocol` | 登录与通信协议的编解码、请求解析、响应与通知包构建。 |
| `DAFLegacy.Launcher` | Windows 启动器，管理登录配置、检查运行环境、安装 IJL15 模块并启动客户端，可按需启动本地服务端。 |
| `DAFLegacy.Ijl15` | 32 位 C++ `ijl15.dll` 兼容模块，提供客户端所需的 JPEG 功能及进程内兼容补丁、窗口缩放等功能。 |
| `DAFLegacy.RandomNative` | 64 位 C++ 硬件随机种子桥接，为玩法随机提供 RDSEED 支持；不支持该指令时自动回退，不是运行必需项。 |
| `wwwroot/admin` | 独立的中文后台管理页面，使用现代浏览器访问，不依赖前端构建工具。 |
| `wwwroot/client` | 充值及会员服务页面，与后台管理页面分离。 |
| `Server/DAFLegacy.Server.SelfTests` | 协议、数据解析和主要业务规则的冒烟测试。 |

## 当前实现

| 系统 | 已实现内容 |
| --- | --- |
| 账号与角色 | 注册、登录、改密与找回密码；角色创建、选择及状态保存；支持多账号；点券、契约及黑钻等服务按账号管理。 |
| 角色成长 | 怪物经验、通关经验及结算字段；升级、SP 和额外 SP 保存；经验书、升级券、属性类道具；技能学习、重置、转职与觉醒相关处理。 |
| 副本 | 进入条件、疲劳消耗与恢复、虚弱及定时恢复、难度解锁；房间切换与重访状态保存；精英怪、深渊入口与相关掉落处理。 |
| 掉落与奖励 | PVF 掉落表、怪物独立掉落、精英专属掉落及任务掉落；地面物品、拾取、负重检查；翻牌结算处理。 |
| 任务 | 接取、进度、完成与奖励；接取时给予任务物品；击杀、收集、普通及带条件通关等已适配条件；隐藏副本与职业相关任务处理。 |
| 物品与装备 | 背包、快捷栏、仓库、丢弃、拾取、商店买卖；非堆叠装备 UUID、强化、品级、封装状态、再封装、分解与配方制作；商店每日统一品级种子。 |
| 商城与装扮 | 商城购买、账号点券扣除、契约及兑换券直接兑现；礼包购买时打开、随机礼包抽选及手动开启；装扮发放与合成。 |
| 宠物 | 宠物与宠物装备、经验升级、饱食度变化与自动喂食、进化任务相关处理、死亡与复活、脚本消息及喊话请求处理。 |
| 邮件与通知 | 邮件列表、附件领取及保管箱相关处理；普通物品、装备、装扮和宠物蛋附件；指定角色或在线广播的消息通知与自定义弹窗。 |
| 决斗数据 | 决斗经验表读取、决斗经验书、等级计算及记录同步。 |

### 后台管理

默认入口：<http://127.0.0.1:8081/admin/index.html>。

- 给指定角色发送邮件，可附金币和一项物品附件，区分普通物品、装扮与宠物蛋；另可同时向收件角色的账号发放点券（账号级即时到账，不是邮件附件，规则见 `ADMIN_API.md` §5.2.7）。
- 给在线角色发送消息或弹窗，或向当前在线角色广播；消息支持类型选择。
- 设置角色可进入的副本最高难度。
- 查看角色在线状态，从已加载 PVF 中搜索物品和选择副本。

## 已知缺口

- **多人组队** 已落地雏形（组队创建、邀请、接受、退队、踢人、成员互见与队伍聊天的协议链路，格式与实现见 `docs/protocol/packet-detail-party-60cn.md`、`docs/protocol/packet-detail-chat-60cn.md`）；2026-10-01 补齐队长转让（CMD 131）与组队成员互发 mode 3 对端上下文（副本 P2P 前置）；完整流程的真机复测归 P-36 统一窗口，多人进同一副本的协同编队待该窗口走查。**队伍名**：6 条预设文案由客户端本地渲染；自定义名经 NOTI 9 设置段 kind==0 的 dstr（u32 长度 + CP936 文本）原生下发，所有客户端直接显示、无需补丁；改名请求携带的容量字节可能越界（客户端回读自存队伍记录），服务端按"建队取默认 4、改设置保留原容量"修正。
- **玩家交易** 协议族已定界并落地下行构建器与 CMD 26 双形态状态机（确认/取消/双锁，NPC 出售路径保留）；交易发起流与值域待真机定案后补齐（见 `docs/design/11-social-systems-migration.md` §3.9 T6-T8）。
- **私人商店** 骨架已实现（开摊/挂名/浏览/退出/收摊五 handler 与建摊广播；请求载荷全族取证）；购买应答与上架物品通道待真机值域后接线（§3.9 T9-T11）。
- **社交与公会** 好友与黑名单已实现（单向添加、增删后列表实时刷新、上下线推送；密语拦截待 NOTI 65 语义定案）；公会建会流静态不可定位（71/166-168 零发送点），待真机定案后实现（NOTI 70 信息包布局已备）；战场、公会战等玩法尚未实现和验证。
- **决斗场** 已有决斗经验、等级及记录同步；房间状态机骨架已实现（建房/进房/座位/准备/开战校验/击杀计数），handler 接线、复合下行布局与结算公式待后续（§3.9 T14-T16）。
- **数据持久化** 已切换 SQLite（设计、取舍与结项状态见 `docs/design/08-sqlite-storage.md`；真机回归、便捷备份与 GM 改值入口待补，见 `PENDING.md` P-25/P-26/P-27），不支持多实例共享写入。

上述为尚未实现的功能；已完成但待真机复测、未定论与存在争议的事项登记在 [PENDING.md](PENDING.md)。

上述协议类缺口的迁移组位、命名与归属预留见 [docs/design/07-protocol-layer-refactor.md](docs/design/07-protocol-layer-refactor.md) 的 §4.8.4；存储缺口由 [docs/design/08-sqlite-storage.md](docs/design/08-sqlite-storage.md) 承载；社交系统分阶段实施状态与剩余待办总表见 [docs/design/11-social-systems-migration.md](docs/design/11-social-systems-migration.md)（§3.9）与 [docs/design/12-sql-social-storage.md](docs/design/12-sql-social-storage.md)。

## 运行环境

- Windows；客户端及 IJL15 为 32 位，硬件随机桥接为 64 位，建议使用 64 位 .NET 运行服务端。
- 从源码构建需要 .NET 10 SDK、带桌面 C++ 开发组件的 Visual Studio，以及 CMake。
- 使用下述默认发布方式运行时，需要相应的 .NET 10 运行时，包括 ASP.NET Core 和 Windows Desktop 运行时；安装 SDK 的开发机通常已具备。
- 通过 Launcher 启动需要可用的图形桌面。启动器会检查 Direct3D 9 HAL 设备，远程或无显示环境可能无法通过检查。

## 构建与启动

发布服务端与启动器到同一目录：

```powershell
dotnet publish .\Server\DAFLegacy.Server\DAFLegacy.Server.csproj -c Release -o .\dist\DAFLegacy.Server
dotnet publish .\Client\DAFLegacy.Launcher\DAFLegacy.Launcher.csproj -c Release -o .\dist\DAFLegacy.Server
```

服务端发布可由仓库根目录的 `publish.bat` 一键完成：脚本通过 .NET SDK 的 `dotnet publish` 发布到 `dist\DAFLegacy.Server`，发布前会清空该输出目录，任一步失败即停止并返回非零退出码。DAF-MCP 模块由 `publish-mcp.bat` 单独发布到 `dist\DAFLegacy.Mcp`，模块 DLL 需手动拷入 `dist\DAFLegacy.Server` 后 MCP 才生效（未拷贝时服务端照常运行，仅不加载 MCP）。Linux/macOS 下对应使用 `publish.sh` 与 `publish-mcp.sh`。

原生模块由仓库根目录的 `patch.bat` 构建（CMake 与 Visual Studio C++ 工具集编译 IJL15 与 RandomNative，并将启动器发布到 `artifacts\native\launcher`）；启动器发布会复制已构建的 `ijl15.dll`。若只发布服务端而没有 `DAFLegacy.RandomNative.dll` 原生随机模块，则仍使用操作系统伪随机。

1. `server.json` 的 `DAFLegacy.ScriptPvfPath` 默认 `..\Script.pvf`；发布目录放在客户端目录内（与客户端主程序同级）时无需修改，否则改为实际 PVF 路径（相对路径以服务端程序目录为基准）。
2. 同目录的 `launcher.json` 的 `ClientPath` 默认指向客户端主程序，按相同布局解析；账号也在此配置，客户端目录应包含匹配的 `Script.pvf`。
3. 运行 `DAFLegacy.Launcher.exe`。默认 `StartServer: true`，启动器会按需启动本地服务端。

示例配置按"发布目录位于客户端目录内"的布局使用相对路径；布局不同时请自行调整为实际路径。

仅运行服务端时，可在发布目录执行：

```powershell
.\DAFLegacy.Server.exe
```

若端口被残留的服务端进程占用（启动报 `AddressAlreadyInUseException`），可运行仓库根目录的 `stop.bat` 结束 `DAFLegacy.Server.exe` 并确认端口已释放。

更新前备份数据，避免覆盖现有存档。不要同时启动多个实例写入同一份状态文件。

## 常用配置

### 服务端

`server.json` 的设置位于 `DAFLegacy` 节：

| 设置 | 用途 |
| --- | --- |
| `ScriptPvfPath` | PVF 路径；相对路径以服务端程序目录为基准。更新 PVF 后重启服务端重新加载。 |
| `DatabasePath` | SQLite 存档路径，默认 `Data/game.sqlite`（2026-10-03 起由 `game.db` 更名，旧档手动改名即可沿用），相对路径以服务端程序目录为基准。库不存在时按基线 schema 建空库（不播种任何数据，账号与角色经 Admin API 或客户端流程创建），已存在时直接使用、不重建。 |

爆率、经验倍率、深渊掉落次数与配方率、掉落开关、黑钻与活动经验加成等**游戏内容参数不在本文件**——它们以 `server_settings` 表为唯一真源，随库持久化；启动时读一次并打印生效值，改库后重启生效。频道容量（`Channel.MaximumUsers`，默认 10）在本文件 `Channel` 节；频道当前人数为在线会话数动态值；频道目录解析自 PVF（`etc/channel_info.etc` + `etc/etc.chn.str`），启动成功时打印列表；收录的 `[server]` 组号由 `Channel.ServerGroup` 配置（默认 1）。
| `Admin` | HTTP 监听地址和端口，默认 `127.0.0.1:8081`。 |
| `Mcp.Enabled` | DAF-MCP 总网关（标准 MCP 端点，供智能体实时抓包分析协议），默认 `false`。开启需发布目录存在 `DAFLegacy.Mcp.dll`。 |
| `Mcp.Host` / `Mcp.Port` | MCP 监听地址和端口，默认 `127.0.0.1:12222`。仅回环使用；改非回环等同主动暴露协议流与游戏数据。 |
| `Entrance` / `Channel` | 登录入口和频道连接配置。频道目录每条频道监听 `Channel.PortBase + 频道号`（默认 20000 起），`Channel.Port`（默认 7001）为遗留单频道端口。 |
| `CharacterDatagram` / `GameplayDatagram` | 客户端 UDP 监听配置。 |
| `Drop.Enabled` | 服务端掉落总开关。 |
| `Drop.RatePercent` | 普通掉落倍率百分比，`100` 使用 PVF 原始概率。 |
| `Drop.EconomicRate` | 金币倍率，默认 `1.0`。 |
| `Drop.ForceDrops` | 强制掉落测试开关，正常体验应保持 `false`。 |
| `Experience.MonsterMultiplier` | 逐只怪物经验倍率，默认 `1.0`。 |
| `Experience.ClearMultiplier` | 通关基础经验及评分奖励倍率，默认 `1.0`。 |
| `EnablePacketTracing` | 协议包跟踪开关，用于排查客户端交互问题。 |
| `Logging.Enabled` | 统一文件日志（`logs/server.log`）开关，默认 `false`；需留存诊断记录时置 `true`。关闭时不注册日志提供器，连日志目录都不会创建。 |
| `Logging.Directory` / `Logging.FileName` | 日志落盘位置，默认 `logs/server.log`，相对路径以服务端程序目录为基准。 |
| `Logging.MaximumFileBytes` | 单文件字节上限，超过即轮转为同名 `.1` 文件；`0` 表示不轮转。 |

默认 TCP 端口为 `2311`、`7001`、`8080`，管理 HTTP 为 `8081`；默认 UDP 端口为 `2311`、`7002`、`7003`。更改网络配置时，应同步检查启动器与频道地址。

智能体（ZCode / Claude Code 等）接入 MCP 抓包：`server.json` 里把 `Mcp.Enabled` 改为 `true` 并重启服务端，然后在 MCP 客户端配置中加入：

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

模块 DLL 与 Server 同进程、直接引用服务端类型，因此**服务端类型面有改动后须重发模块**（`publish-mcp.sh` / `publish-mcp.bat`）再启用；跨版本混用不会启动报错，而是抓包静默失效（详见 `docs/design/09-mcp-packet-tap.md` §8）。

工具均为只读（`packet_watch` 实时跟踪、`packet_get` 取单包详情、`packet_wait` 等回包、`packet_names_search` 查包号↔报文名、`session_list`、`tap_status`）。详见 `docs/design/09-mcp-packet-tap.md`。

### 启动器

`launcher.json` 支持 `Accounts` 账号列表及 `AccountIndex`；也可以使用 `--account-index N` 或 `--account NAME` 临时选择已配置账号。默认测试凭据为 `test/test`，请勿将包含真实密码的配置公开分发。

启动器会安装发布目录中的 `ijl15.dll`，首次替换已有模块时保留 `ijl15.original.dll`，缺少客户端 `Config.ini` 时复制默认配置。IJL15 补丁只修改当前进程内存，不改写磁盘上的 PE；Launcher 启动器本身不读写客户端进程内存。

客户端目录中的 `Config.ini` 提供以下常用设置（`[Features]` 每个键对应一个功能开关，`[CeraPurchaseGate]` 对应点券商城门控分项；缺失的键使用默认值，功能清单见 `Client/DAFLegacy.Ijl15/src/ijl15_config.cpp` 的默认值表）：

```ini
[Resolution]
Width=800
Height=600

[Rendering]
ScaleFilter=native

[Features]
EnablePerformanceOverlay=1
EnableMouseWheel=1
DisableDebuggerDetection=1
AllowMultipleClientInstances=1
DisableStartupMinimize=1
SinglePermanentAvatarPeriodOption=1
EnableCustomStackableUseMode1=1
RemoveCustomPopupAnnouncementTitle=1
UseExternalDnfStringTable=1
FixMailboxArchiveRefresh=1
BypassCeraPurchaseRestrictions=1
ForcePlainTextLogs=1
DisableType16ChatMessageSound=1
RedirectClientOutputPaths=0

[CeraPurchaseGate]
LegacySaleWindow=1
PerCharacterLimit=1
CharacterLevel=1
ClientInventoryCapacity=0
JobCompatibility=0
CurrencyBalance=0
RequiredOption=0
GiftEligibility=0
```

窗口大小只改变外层缩放，内部渲染保持 `640×480`。`ScaleFilter` 支持 `native`、`point`/`nearest`、`linear` 和 `anisotropic`。两个功能开关分别控制性能信息覆盖层与鼠标滚轮支持，设为 `0` 关闭。修改后重启客户端生效。

## 测试

运行主要业务冒烟测试：

```powershell
dotnet run --project .\Server\DAFLegacy.Server.SelfTests\DAFLegacy.Server.SelfTests.csproj -c Release
```

全部通过时末尾输出 `All DAFLegacy protocol smoke tests passed.`，退出码为 0。

## 许可证

源码使用 **BSD Zero Clause（0BSD）许可证**，详见仓库根目录的 `LICENSE`。该许可证不授予客户端或其他第三方资源的使用与分发权利。
