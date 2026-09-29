# 09 · DAF-MCP 总网关（一）：协议包实时监听（Packet Tap）

- 状态：**已实现**（2026-09-29 核定：G1 协议包监听随 commit `7e12da7` 落地，代码见 `Server/DFLegacy.Mcp/`、`Server/DFLegacy.Server/Tap/IPacketTapSink.cs` 与 `Infrastructure/McpModuleLoader.cs`，§6 全部 7 个工具均已提供；G2 PVF 工具组尚未实施，见 §15.2）

---

## 1. 目标与设计原则

在服务端内置一个**标准 MCP（Model Context Protocol）服务器**，作为面向智能体的统一能力网关（DAF-MCP）。本档设计第一个工具组 **G1 · 协议包实时监听**：把客户端 ↔ 服务端的全部协议收发包实时暴露给智能体，供其边抓边分析——定位协议字段、验证新包实现、排查客户端交互问题。G2（PVF 解密读取）、G3+（数据查询等）见 §15。

| # | 原则 | 落实方式 |
| --- | --- | --- |
| P1 | **标准 MCP 协议** | 官方 C# SDK（`ModelContextProtocol`），Streamable HTTP 标准传输；工具带 JSON Schema 与描述，智能体自动发现 |
| P2 | **默认关闭** | 唯一配置开关 `DFLegacy.Mcp.Enabled`，默认 `false`；关闭时不开端口、不建线程、零内存驻留 |
| P3 | **独立启动配置** | `server.json` 新增独立配置节 `DFLegacy.Mcp`：`Enabled`（**默认 false**）、`Host`（默认 `127.0.0.1`）、`Port`（默认 `12222`），与 `DFLegacy.Admin` 的 Host/Port 同构；全部工具组共用该端点 |
| P4 | **模块独立、单一项目、可插拔** | MCP 全部代码（入口、hub、宿主、全部工具组）收在唯一项目 `DFLegacy.Mcp`；运行期按开关 + DLL 存在性加载（§3.1），拔除 = 删 DLL / 关开关，业务代码无感 |
| P5 | **不过多侵入业务代码** | 业务文件（`Network/*.cs`）只做删除（去掉 `EnablePacketTracing` 门卫）；抓包分发收敛在 `RuntimeState` 单一漏斗；侵入清单见 §5.2 |
| P6 | **只读观测** | 全部工具只读；写操作类工具须先补鉴权再立项（§14） |

与已有设施的关系：管理平台 `GET /api/packets` **不改动、不依赖**（那是管理界面的 400 条快照查询）；`EnablePacketTracing` 开关继续只管快照与日志，**开 MCP 不需要开包追踪**；`PacketPlainText` / `PacketNames` 在富化阶段复用（§4.2）；G2 复用 `ScriptFileSystem` / `PvfArchiveReader`（§15.2）。

非目标：不做包注入/重放/篡改；不做 Web UI；不做跨机暴露（默认回环绑定，Host 改非回环等同主动暴露，见 §9）；不改 `RuntimeState` 快照队列语义。

### 1.5 术语约定

| 术语 | 含义 |
| --- | --- |
| 包 / 帧 / 数据报 | "包"泛指一次协议收发；"帧"指 TCP 通道的定长头帧（`PacketFrame`/`GameServerPacket`）；"数据报"指 UDP（`GameDatagramProtocol`） |
| 报文名 | `PacketNames.TryGet` 给出的枚举名（如 `ENUM_NOTIPACKET_CHANNELINFO`），源自 60CN 客户端逆向；**不是**"报名" |
| 包号 | `ProtocolId` 字节，报文名对应的数字编号 |
| 明文（PlainText） | `PacketPlainText` 对 body 的字段级解码文本；未知/失败为 null，不是整包 hex |
| wire / 线路原始帧 | 含帧头与 CRC 的线上字节；RX 方向含客户端密文，与"解码后 body"相对 |
| 漏斗 | `RuntimeState` 的 `TracePacket`/`TraceDatagram`——全部包事件唯一的汇聚分发点（§5.1） |
| 游标（Seq） | 跨全部服务的全局单调事件序号，`packet_watch` 的续读位置 |
| ring / 环形缓冲 | hub 内固定容量（4096）的近期事件存档，供回放与游标落后时兜底 |
| 订阅通道 | 每个 MCP 调用方一条有界 `Channel`（1024，DropOldest），实时事件流 |

---

## 2. 标准协议遵从（P1）

### 2.1 SDK 引入

| 项 | 说明 |
| --- | --- |
| NuGet 包 | `ModelContextProtocol`（核心：协议、Schema 生成）+ `ModelContextProtocol.AspNetCore`（ASP.NET Core 托管、Streamable HTTP） |
| 引用位置 | **仅 `DFLegacy.Mcp.csproj`**；`DFLegacy.Server` 零引用（§3.1 依赖方向） |
| 运行环境 | .NET 10 完全兼容（核心库面向 netstandard2.0，集成包面向 net8.0+），无需任何额外运行时 |
| 版本 | 当前稳定版 **2.2.0**（M2 时锁定）；API 变更以该版本文档为准 |
| 打包体积 | **实测 +2.1 MB**（framework-dependent 发布，2026-09，SDK 2.2.0）：`ModelContextProtocol.Core.dll` 1.24 MB、`Microsoft.Extensions.AI.Abstractions.dll` 0.64 MB（传递依赖）、`ModelContextProtocol.AspNetCore.dll` 0.14 MB、`ModelContextProtocol.dll` 0.09 MB；与共享框架零重复，现有输出文件一字节不变 |
| 不引 SDK 的替代 | 自行实现 JSON-RPC 握手、`tools/list`、Schema 生成——无必要 |

### 2.2 传输与能力面

- 传输：**Streamable HTTP**，端点 `http://{Mcp.Host}:{Mcp.Port}/mcp`（默认 `http://127.0.0.1:12222/mcp`），ZCode / Claude Code 等客户端原生支持。
- 宿主：MCP 模块**自持一个独立最小 WebApplication**（独立 Kestrel，只有 `/mcp` 路由），以 `IHostedService` 挂在主进程；与 Admin 应用零耦合，配置端口被占时模块记错误日志并保持关闭，游戏服务不受影响。stdio 适配进程为可选项（§14）。

| MCP 能力 | 提供内容 |
| --- | --- |
| Tools | `packet_watch`、`packet_get`、`packet_wait`、`session_list`、`packet_names_search`、`protocol_decode`、`tap_status`（§6） |
| Resources | `packet-names://noti`、`packet-names://cmd`（报文名注册表全文） |
| Prompts | 可选：`analyze-window` |
| 工具发现 | **协议内建，零自研**：`tools/list` / `resources/list` / `prompts/list` 由官方 SDK 自动实现——`[McpServerToolType]` 工具类自动入列，JSON Schema 由参数类型 + `[Description]` 生成；新工具组加文件即自动入列 |
| Notifications | 不依赖。实时性由游标长轮询承载（§7）；工具集随进程启动固定，无需 `notifications/tools/list_changed` |

---

## 3. 总体架构

```text
┌─ DFLegacy.Server 进程 ───────────────────────────────────────────────────────┐
│                                                                              │
│  Network 层业务代码（侵入 = 仅删除 EnablePacketTracing 门卫，§5.2）              │
│  EntranceService / CharacterDatagramService / GameplayDatagramService        │
│        │  无条件调用（门卫移入漏斗）                                            │
│        ▼                                                                     │
│  RuntimeState（Persistence，唯一漏斗）                                         │
│  TracePacket / TraceDatagram：                                               │
│    ├─ tracingEnabled ──► 400 条快照 + 日志（现状，管理界面用，不动）              │
│    └─ TapSink.Active ─► Publish（原始参数直传，不拷贝不富化）                   │
│                            │  IPacketTapSink ▼                              │
│  ┌── DFLegacy.Mcp（独立模块项目，默认关闭，运行期加载）──────────────────────────┐ │
│  │  McpModule（入口）: Enabled && DLL 存在 → 激活，否则静默                    │ │
│  │  ├─ G1 PacketTapHub：富化（报文名/明文）+ seq / ring / 订阅者通道池            │ │
│  │  ├─ G2 PvfTools（后续）: DI 注入 ScriptFileSystem                          │ │
│  │  └─ G3+ …（后续工具组，各一个静态工具类文件）                                 │ │
│  │  自持 WebApplication → {Mcp.Host}:{Mcp.Port}/mcp（§8 配置）                 │ │
│  └───────────────────────────┬────────────────────────────────────────────┘ │
│                              │                                              │
│  管理平台 /api/packets（不动）──┘                                              │
└──────────────────────────────┼───────────────────────────────────────────────┘
                               ▼  Streamable HTTP（标准 MCP）
                  智能体（ZCode / Claude 等）：packet_watch 游标长轮询 + packet_get 取详情
```

### 3.1 模块加载与依赖方向

```text
DFLegacy.Mcp ──引用──► DFLegacy.Server   （工具组可自由 DI 服务端服务）
DFLegacy.Server ──✗──► DFLegacy.Mcp      （编译期零引用，靠运行期加载）
```

- **编译期**：`DFLegacy.Server.csproj` 不引用 `DFLegacy.Mcp`，无循环。Server 侧唯一新增是约 20 行加载器 `Infrastructure/McpModuleLoader.cs`（反射 `Assembly.Load("DFLegacy.Mcp")`，找到 `IMcpModule` 入口调用 `Attach`），Program.cs 对它只加 1 行调用。该加载器是通用机制，不含 MCP 业务语义。
- **运行期**：`Enabled=true` 且输出目录存在 `DFLegacy.Mcp.dll` → 激活；DLL 缺失、加载抛错、初始化异常 → 记一条警告后继续运行，游戏服务永远不受影响。`Enabled=false`（默认）→ 加载器短路。
- **契约归属规则**：共享类型必须住在**被引用的一侧**（`Mcp → Server` ⇒ 两边都要用的类型只能放 Server，否则循环引用）。据此 Server 侧压到理论最小：`Tap/` 只留 1 个文件的遥测接缝 `IPacketTapSink`（原始参数直传，不含 MCP 词汇，§4.1）；事件模型、富化、hub 全在 `DFLegacy.Mcp`——给事件加字段不改 Server。

---

## 4. 抓包契约与事件模型

### 4.1 Server 侧：遥测接缝（`Server/DFLegacy.Server/Tap/IPacketTapSink.cs`，唯一 1 个文件）

```csharp
// 服务端声明"我能对外提供协议流原始数据"的端口；不含任何 MCP 概念
public enum TapDecodeMode { None, ClientCmd, Server }  // 富化提示，按现有 LogPacket 两个重载的选择逻辑传入

public interface IPacketTapSink
{
    bool Active { get; }            // 漏斗热路径上唯一的读取项（一次布尔判断）
    void Publish(                   // 只传原始参数：不拷贝、不查名、不解码
        Guid sessionId, string service, string direction,
        byte type, byte protocolId, int totalLength,
        uint crc32, bool crcValid,
        TapDecodeMode decodeMode,
        ReadOnlySpan<byte> body, ReadOnlySpan<byte>? wire);
}
```

- 原始参数直传（风格与现有 `TraceDatagram(…, ReadOnlySpan<byte> datagram)` 一致），Server 不拥有任何 MCP 形状的数据类型。
- 该接缝是**通用遥测端口**：SelfTests 直接实现它测漏斗（§12）；未来 pcap 导出等非 MCP 消费者同走这里。

### 4.2 Mcp 侧：事件模型与 hub（`DFLegacy.Mcp`）

```csharp
// PacketTapEvent.cs —— MCP 面向智能体的富事件模型（字段演进不动 Server）
public sealed record PacketTapEvent(
    long Seq, DateTimeOffset Timestamp, Guid SessionId, string Service, string Direction,
    byte Type, byte ProtocolId, string? PacketName,        // PacketNames.TryGet 结果
    int TotalLength, byte[] Body, bool BodyTruncated,      // 解密后 body，截断到 MaxBodyBytes=4096
    byte[]? Wire, bool WireTruncated,                      // 线路原始帧（仅 RX 有：含头、含客户端密文）
    string? PlainText);                                    // PacketPlainText 解码结果，未知/失败为 null

// PacketTapHub : IPacketTapSink —— 实现接缝
//   Publish() 内：字节拷贝截断 → PacketNames.TryGet / PacketPlainText.Decode(Server)（按 decodeMode）
//   → 记全局 seq、入 ring、按订阅者谓词预筛后扇出
```

- `Type` 沿用现有 Trace 语义：0=NOTI 或 entrance 协议帧（两者同为 0，按 `Service` 区分）、1=CMD、2=UDP 数据报；`Direction` 沿用 `RX`/`TX`/`RX-UDP`/`TX-UDP`/`RX-UDP-NAT`/`RX-UDP-NAT-KEEPALIVE`；`Service` 沿用 `entrance:{port}` / `channel:{port}` / `character-datagram:{port}`。
- hex 字符串不在事件里：ring 存 `byte[]`，工具返回时才格式化。
- `Wire` 仅 RX TCP 帧路径有（客户端密文，逆向有价值）；TX 线路帧可由 body + 帧头推导，不占内存。
- 富化在 hub 做：每包一次报文名查询 + 一次明文解码；try/catch 包裹，追踪绝不影响会话。

---

## 5. 抓包接入：单一漏斗与最小侵入（P5）

### 5.1 RuntimeState 改造

现状：所有包追踪调用点都以 `if (options.EnablePacketTracing)` 为门卫，然后调用 `runtime.TracePacket / TraceDatagram`。把**门卫移进漏斗**：

```csharp
public sealed class RuntimeState
{
    private bool _tracingEnabled;                 // 组合根启动时置位（= options.EnablePacketTracing）
    public IPacketTapSink? TapSink { get; set; }  // MCP 模块激活时由加载器注入
    private bool TapActive => TapSink is { Active: true };

    public void TracePacket(RuntimeSession session, string direction,
        PacketFrame frame, int dumpLimit, PacketFrame? rawFrame = null)   // 新增 rawFrame 可选参
    {
        if (TapActive)                            // 只传原始参数（try/catch 包裹，绝不影响会话）
            TapSink.Publish(session.Id, session.Service, direction,
                frame.Type, frame.ProtocolId, frame.TotalLength,
                frame.DeclaredCrc32, frame.HasValidCrc32,
                decodeMode: TapDecodeMode.ClientCmd,
                body: frame.Body, wire: rawFrame?.Encode());
        if (!_tracingEnabled)                     // 原快照/日志路径，行为与现状完全一致
            return;
        ...现有实现原样...
    }

    public void TraceDatagram(...)                // 同样结构：Publish（decodeMode: None）+ _tracingEnabled 门卫
}
```

行为保持：`_tracingEnabled=false` 且无 TapSink 时，`/api/packets` 输出与今天逐字节一致；`EnablePacketTracing=true` 时管理快照与日志照旧。

### 5.2 业务文件侵入清单（完整盘点）

业务文件里**没有新增代码**，只有门卫删除/简化——净行数为负：

| 文件 | 现状 | 改后 |
| --- | --- | --- |
| `Network/EntranceService.cs` `LogPacket(frame)`（~L14421） | `if (!tracing) return;` 挡住全部 | `TracePacket(..., rawFrame)` 移到门卫外**无条件调用**；门卫只挡控制台 hex/日志部分（2 个 helper 各 1 处） |
| `Network/EntranceService.cs` `LogPacket(GameServerPacket)`（~L14462） | 同上 | 同上 |
| `Network/CharacterDatagramService.cs`（~L77、L231、L276） | `if (tracing) { Trace... }` ×3 | 去掉 if，直接调用（-3 行） |
| `Network/GameplayDatagramService.cs`（~L151、L244、L257、L265、L359） | `if (tracing) { TraceDatagram }` ×5 | 去掉 if，直接调用（-5 行） |
| `Network/ProbeService.cs` | 自行拼 hex 写日志，不走 Trace | **不动**（旁路采集器，默认禁用，非真实客户端链路） |

### 5.3 组合根与工程结构

```text
Server/
├── DFLegacy.Server/
│   ├── DFLegacy.Server.csproj    不变（不引用 Mcp）
│   ├── Program.cs                +1 行：McpModuleLoader.TryAttach(...)
│   ├── Infrastructure/McpModuleLoader.cs   ← 新增，~20 行反射加载器
│   ├── Tap/IPacketTapSink.cs     ← 新增，1 个文件（§4.1 遥测接缝）
│   ├── Persistence/RuntimeState.cs  §5.1 漏斗改造（+~25 行）
│   └── Hosting/ServerOptions.cs  +McpOptions{Enabled,Host,Port}（与 AdminOptions 同构，+3 属性）
└── DFLegacy.Mcp/                 ← 新增模块项目（引用 Server + MCP SDK + AspNetCore FrameworkReference）
    ├── DFLegacy.Mcp.csproj         引 ModelContextProtocol + ModelContextProtocol.AspNetCore
    ├── McpModule.cs                IMcpModule 入口：Attach() 注册各工具组与宿主
    ├── McpHost.cs                  自持 WebApplication + IHostedService，按 McpOptions 绑定
    ├── Tap/                        G1 · 协议抓包（本档）
    │   ├── PacketTapEvent.cs       G1 富事件模型（§4.2）
    │   ├── PacketTapHub.cs         IPacketTapSink 实现：富化、seq、ring、订阅者通道池
    │   ├── PacketTapTools.cs       G1 工具
    │   └── PacketNamesResource.cs  G1 Resources
    └── Pvf/                        G2 · PVF 读取（M4，§15.2）
        └── PvfTools.cs
```

命名约定：项目名取**协议身份** `DFLegacy.Mcp`——它对外的唯一契约就是标准 MCP 端点，且 P4 要求单项目承载全部工具组，故不得按首个工具组命名（`DFLegacy.PacketTap` 之类在 G2 落地时即失效）；`DafMcp` 前缀与 `DFLegacy` 语义重复，也不用。语义化下沉到**组子目录**：新增工具组 = 新增一个子目录（`Pvf/`、`Catalogs/`…），文件归属一目了然。csproj 不做 NuGet 打包（`AssemblyName` 等取默认值即可）。

平台属性：`net10.0`，**不设** `PlatformTarget` / `RuntimeIdentifier`（保持 AnyCPU，64 位兼容）；模块纯托管、无 P/Invoke（MCP SDK 亦然），以 `Assembly.Load` 进入 Server 进程后位数随宿主（默认 64 位，见 `DFLegacy.Server` 的平台属性）。

`publish.bat` / `publish.sh` 各 +1 行：`dotnet publish Server/DFLegacy.Mcp/...` 后**按白名单拷贝**——`DFLegacy.Mcp.dll`、`ModelContextProtocol*.dll`、`Microsoft.Extensions.AI.Abstractions.dll`（排除随 ProjectReference 流入的 `DFLegacy.Server.*` 与内容文件）。不拷 = 该次发布天然无 MCP。Mcp 不是 NuGet 包、不装 GAC：**单独编译的模块 DLL，可增删**。

**构建产物精简（已实测）**：csproj 设 `GenerateDependencyFile=false`、`DebugType=none`、`SatelliteResourceLanguages=en` 后，publish 产物**恰为 5 个 .dll**（自身 + 4 个 SDK DLL，体积见 §2.1），无 deps.json / pdb / xml / 资源子目录。`deps.json` 缺席不影响加载——加载器的 `Resolving` 兜底即为此设计。`PublishSingleFile` 单文件捆绑对"被宿主 `Assembly.Load` 的插件"**不可行**（bundle 由宿主 hostfxr 解包），不做单文件；若将来必须单文件，走"SDK DLL 内嵌为资源 + 加载器 `Resolving` 装载"（§14.7）。

**主程序入口与启动时序**：

```csharp
// 代码入口：Program.cs 组合根，唯一新增的一行（builder 阶段）
McpModuleLoader.TryAttach(builder, options);
```

加载器内部：`Enabled=false` 短路 → `Assembly.Load("DFLegacy.Mcp")`（DLL 在发布目录即可探测；用默认 `AssemblyLoadContext` 的 `Resolving` 事件兜底解析 MCP 及其 SDK 依赖）→ 反射调用 `McpModule.Attach(services, configuration, options)` → 注册 `PacketTapHub`（写入 `RuntimeState.TapSink`）、`McpHost` 宿主与工具组。

```text
网络入口：http://{Mcp.Host}:{Mcp.Port}/mcp   （默认 127.0.0.1:12222/mcp）
时序：进程启动 → TryAttach（Enabled && DLL 在位）→ app.RunAsync
    → McpHost hosted service 起独立 Kestrel → /mcp 可供智能体连接
```

任一步失败（DLL 缺失/损坏、端口被占）只降级 MCP，游戏端口照常启动。与 Admin 的挂法区别：Admin 是主 Kestrel 上的一条路由；MCP 是 hosted service 自带独立 Kestrel——拔除/故障互不牵连的结构基础。

### 5.4 插拔操作手册（P4 的验收标准）

| 操作 | 步骤 | 结果 |
| --- | --- | --- |
| **插上** | 发布目录放入 `DFLegacy.Mcp.dll`（publish 脚本默认做）+ `Enabled: true` + 重启 | 按配置的 Host:Port 起监听，抓包可用 |
| **运行时关闭** | `"Enabled": false`（默认） | 加载器短路：不开端口、无后台线程、内存零驻留 |
| **拔除（免重编译）** | 删除发布目录里的 `DFLegacy.Mcp.dll` | 加载器记一条调试日志，服务端照常 |
| **拔除（源码级）** | 删 `DFLegacy.Mcp/` 项目 + publish 拷贝行 + `McpModuleLoader.cs` | 服务端编译通过、行为与未装过一致（`Tap/IPacketTapSink.cs` 留存为惰性接缝） |
| **故障隔离** | — | DLL 损坏 / 端口被占 / 初始化异常 → 记警告并保持关闭，游戏服务照常 |

---

## 6. G1 工具设计

全部只读（P6）；参数即 JSON Schema，由 SDK 自动生成，智能体可自发现。

### 6.1 `packet_watch` —— 实时跟踪（核心）

`tail -f` 语义：智能体循环调用，每次带上一次返回的 `nextSeq`。

| 参数 | 类型 | 默认 | 说明 |
| --- | --- | --- | --- |
| `afterSeq` | long | `-1` | 返回 seq 大于该值的事件。`-1`=从 ring 最旧开始（回放留存事件）；`0`=不回放，以本次调用时刻的 maxSeq 为起点，只返回此后新产生的事件；正数=正常续读 |
| `limit` | int | 50 | 单次返回条数上限，1–200 |
| `waitMs` | int | 10000 | 无新包时最长等待；0=立即返回 |
| `filter` | object | null | 见 §6.2 |

返回（hex 字段为工具侧格式化）：

```json
{
  "events": [{
    "seq": 1042,
    "timestamp": "2026-09-28T21:04:11.482+08:00",
    "sessionId": "5f0c…",
    "service": "channel:7001",
    "direction": "RX",
    "kind": "cmd",
    "type": 1,
    "protocolId": 49,
    "packetName": "ENUM_CMDPACKET_MOVE_ON_MAP",
    "totalLength": 128,
    "bodyLength": 120,
    "bodyHex": "0100AC00…", "bodyTruncated": false,
    "wireHex": "…", "wireTruncated": false,
    "crc32": "0x1A2B3C4D", "crcValid": true,
    "plainText": "pos=(104,212) …"
  }],
  "nextSeq": 1043,
  "ringOldestSeq": 311,
  "cursorLapsed": false,
  "subscriberDropped": 0
}
```

- `cursorLapsed=true`：`afterSeq` 已被 ring 淘汰，返回从 ring 最旧开始——中间有缺口，智能体应知情。
- `subscriberDropped`：该订阅通道积压丢弃计数（DropOldest），>0 说明流量超过消费速度。

### 6.2 filter 对象（`packet_watch` / `packet_wait` 共用）

```json
{
  "kinds":        ["cmd", "noti", "udp"],
  "services":     ["channel"],
  "directions":   ["RX"],
  "protocolId":   49,
  "nameContains": "ITEM",
  "sessionId":    "5f0c…",
  "minBodyLength": 8
}
```

`kind` 与 `Type` 一一对应：`noti`=0（含 entrance 协议帧）、`cmd`=1、`udp`=2。全部条件 AND；数组内 OR；缺省 = 不过滤。`services`/`nameContains` 大小写不敏感包含匹配；`directions` 前缀匹配（`"RX"` 命中 `RX-UDP` 系列）。过滤在 hub 入订阅通道**之前**执行（每订阅者持有编译好的谓词）。

### 6.3 其余工具

| 工具 | 参数 | 返回 | 用途 |
| --- | --- | --- | --- |
| `packet_get` | `seq` | 单事件完整详情 | 看某包全貌、拷字节逆向 |
| `packet_wait` | `filter`, `timeoutMs` | 第一条匹配事件或 `{timedOut:true}` | "点了强化等回包"式往返验证 |
| `session_list` | — | `SessionSnapshot[]`（直接 DI `RuntimeState.Sessions()`） | 找要盯的 sessionId |
| `packet_names_search` | `query`, `limit` | Noti/Cmd 表中匹配的 `{type, protocolId, name}` | 先查"包号↔名字"再过滤 watch |
| `protocol_decode` | `type`, `protocolId`, `bodyHex` | 明文文本或 null | 离线复用服务端解码器 |
| `tap_status` | — | 当前 seq、ring 占用/容量、订阅数、是否已启用 | 确认抓包通道活着 |

### 6.4 Resources

`packet-names://noti` / `packet-names://cmd`：整张报文名注册表 JSON（源自 60A1 客户端逆向，见 `PacketNames.cs` 头注释）。

---

## 7. 实时语义（游标长轮询，而非服务端推送）

工具调用是所有 MCP 客户端都稳定支持的原语，实时性由此承载：

```text
智能体循环： r = packet_watch(afterSeq=r.nextSeq, waitMs=10000, filter=…)
             ├─ 有匹配包 → 立即返回，分析后继续
             └─ 无 → 服务端挂起至多 waitMs 后返回空 events（同样的 nextSeq）
```

- 端到端延迟 = 包产生 → 订阅通道 → 长轮询唤醒，本地毫秒级。
- 两次调用间隙由订阅通道（实时）+ ring（回看）双保险；丢失经 `cursorLapsed` / `subscriberDropped` 如实上报。
- 上下文不爆：`limit` 上限 + `plainText` 提炼字段，默认看摘要、`packet_get` 取全量。
- 挂起用 `Channel.Reader.WaitToReadAsync` + `CancellationTokenSource.CancelAfter(waitMs)`，无轮询风暴。

---

## 8. 配置（P2 / P3）

`server.json` 新增独立配置节 `DFLegacy.Mcp`，与 `DFLegacy.Admin` 同构（组合根解析为 `McpOptions`）：

```json
{
  "DFLegacy": {
    "Mcp": {
      "Enabled": false,
      "Host": "127.0.0.1",
      "Port": 12222
    }
  }
}
```

| 项 | 默认 | 说明 |
| --- | --- | --- |
| `Enabled` | **`false`** | MCP 启动开关，默认关闭；false 时加载器短路，不开端口、零驻留；true 且 `DFLegacy.Mcp.dll` 在位时模块激活 |
| `Host` | `127.0.0.1` | 监听地址（IP）。默认回环；改非回环等同主动暴露协议流，须自行叠加防火墙/认证（§9） |
| `Port` | `12222` | 监听端口。默认值已核对无冲突：本仓库 8080/8081/2311/7001–7003；参考项目区段 10011–10200、12311–12312、17200、30000+（§16.2）。被占时模块记错误日志并保持关闭 |

工具组不单独配置（§15.4）；容量参数（ring 4096、单包截断 4096/8192 字节、订阅通道 1024）为代码常量。`README.md` 选项表实现时补三行。

---

## 9. 安全边界

1. 默认回环绑定，与 Admin API 同一防线（ADMIN_API.md §1.2 的警告同样适用：不要 NAT/反代 MCP 端口）。
2. `Host` 可配置（§8）：**改非回环地址等同主动暴露协议流与游戏数据**，v1 无鉴权，跨机场景必须先叠加防火墙/反代认证或等鉴权能力落地（§14.2）。
3. 工具集只读；`protocol_decode` 只作用于智能体提交的字节串。
4. 默认关闭 = 出厂无新攻击面；开启后默认也只多一个回环端口。
5. 单包截断常量同时是内存上界与 DoS 防线。
6. 无鉴权（本机信任模型，与 Admin 现状一致）；跨机需求出现时先补 Bearer Token（§14.2）。

---

## 10. 智能体接入

### 10.1 客户端配置（ZCode / Claude Code 系）

先在 `server.json` 开 `Enabled: true` 并重启服务端，然后：

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

URL 按 `server.json` 的 `DFLegacy.Mcp.Host/Port` 拼接（默认即上例）；新工具组上线自动出现在同一端点，客户端配置不变。

### 10.2 典型分析流程（G1）

1. **验证一个新实现的 TX 包**：`packet_names_search` 确认包号 → `packet_watch(filter={protocolId, kind:"noti"}, waitMs=15000)` 挂起 → 触发客户端操作 → 读 `plainText` 与 `bodyHex` 比对预期布局。
2. **逆向未知 RX 包**：`packet_watch(filter={kind:"cmd"})` 盯客户端上行 → 可疑包 `packet_get` 拿完整 `bodyHex` + `wireHex`（密文）→ 结合报文名注册表给字段假设 → 下次抓包验证。
3. **往返时序排查**：`session_list` 锁定会话 → `packet_watch(filter={sessionId})` 全量跟踪，按 `seq` 还原请求/响应配对（含 CRC 失败、UDP NAT 探测等异常事件）。

---

## 11. 实施步骤

### M1 · 漏斗与遥测接缝（无 MCP，可独立合入）

- 新增 `Server/DFLegacy.Server/Tap/IPacketTapSink.cs`（1 个文件：接口 + `TapDecodeMode` 枚举）
- `RuntimeState`：`_tracingEnabled` + `TapSink` 属性 + 原始参数 Publish；`TracePacket` 增加 `rawFrame` 可选参
- `Network/*`：按 §5.2 清单删门卫
- `Program.cs`：`runtime._tracingEnabled = options.EnablePacketTracing` 装配（1 行）
- SelfTests：自实现 `IPacketTapSink` 直测漏斗（§12），确认 `/api/packets` 行为不变

### M2 · MCP 模块与 G1 工具组（默认关，开了就能用）

- 新增 `Server/DFLegacy.Mcp/`：McpModule、PacketTapEvent、PacketTapHub、McpHost（按 McpOptions 绑定）、核心工具（watch/get/wait/session_list/tap_status）
- Server 侧：`Infrastructure/McpModuleLoader.cs` + Program.cs 1 行；`ServerOptions` + `McpOptions`
- publish 脚本 +1 行拷贝；`server.json` 开关（默认 false）；README 补行；锁定 SDK 版本（2.2.0）

### M3 · G1 辅助能力

- `packet_names_search`、`protocol_decode`、Resources、（可选）stdio 适配进程

### M4 · G2 PVF 工具组（另开档细化，见 §15.2）

---

## 12. 测试与验收

**单元（SelfTests）**

- 漏斗（自实现 `IPacketTapSink`，不依赖 Mcp 项目）：`_tracingEnabled=false` 且无 TapSink → 快照队列保持空（`/api/packets` 回归）；仅 TapSink 激活 → 收到的原始参数正确、快照仍为空；两者同开 → 双通道各自正确。
- PacketTapHub（Mcp 项目）：富化正确（报文名/明文按 `decodeMode`）；seq 单调且跨服务连续；ring 超容淘汰最旧；订阅通道满 DropOldest 且计数正确；无订阅者 `Publish` 零分配（`GC.GetAllocatedBytesForCurrentThread` 前后差断言）。
- filter：各维度匹配与 AND/OR 组合；`afterSeq` 早于 `OldestSeq` 时 `cursorLapsed=true`。
- 加载器：`Enabled=false` → 不尝试加载；`Enabled=true` 无 DLL → 警告 + 继续；DLL 在位但初始化抛错 → 警告 + 继续、`TapSink` 保持 null。

**集成（冒烟）**

- 起服务端（`EnablePacketTracing=false`、`Mcp.Enabled=true`），脚本 TCP 客户端走一次 entrance 握手（60a4，见 `docs/protocol/60a4-handshake-protocol.md`），断言：`packet_watch` 收到 `entrance:*` 的 RX/TX 事件；`/api/packets` 全程为空数组。
- 默认关闭验收：`Enabled=false` 启动 → 配置的 MCP 端口无监听（端口探测断言）。
- 拔插验收：删除 `DFLegacy.Mcp.dll` 后启动正常；源码级移除 Mcp 项目后全解决方案构建通过、冒烟全绿。

**人工验收**

- ZCode 挂 MCP，边操作客户端边让智能体实时复述收发包（登录、选角、进图、捡物品各一轮），核对报文名与明文字段。

---

## 13. 性能预算

- **MCP 关闭（默认）**：热路径新增 = 一次 `TraceX` 调用 + `TapActive` 空判断 + `_tracingEnabled` 判断。纳秒级。
- **仅 Tap 激活**：每包一次字节拷贝（受截断常量约束）+ 一次报文名查询 + 一次明文解码（未知包立即 null）+ 入队一次——与现有 `LogPacket` 同类工作量。
- **订阅前预筛**：hub 出队侧按订阅者谓词过滤，UDP 热流量不冲击只订 `cmd` 的订阅者。
- **关闭时模块零驻留**：不开端口、无线程、无定时器。

---

## 14. 开放问题与后续方向

1. **包注入/重放工具**（远期）：能改写会话状态，需白名单 + 显式开关 + 仅调试构建 + **鉴权先行**，单独开档。
2. **鉴权**：跨机或多人共用开发机时补 Bearer Token（`McpHost` 中间件，一处改动）；任何写操作类工具组立项的前置条件。
3. **pcap 导出**：ring 落 `.pcap`（走 §4.1 遥测接缝），供 Wireshark 交叉分析。
4. **双端对拍**：同屏订阅两个会话做 diff。
5. **stdio 适配进程**：个别客户端只支持 stdio 时提供薄适配（stdio ↔ 本机 HTTP）。
6. **SDK 版本升级**：跟随 2.x 稳定线，升级记录落在 PR 描述里。
7. **单文件插件形态**（与多文件的取舍，启用前必读）：把 4 个 SDK DLL 内嵌为 `DFLegacy.Mcp.dll` 资源、加载器经 `Resolving` 从资源装载——发布目录只增 1 个文件。注意 `PublishSingleFile` 对"被宿主 `Assembly.Load` 的插件"不可行（bundle 由宿主 hostfxr 解包），内嵌是唯一可行路径。两形态影响对比：

   | 维度 | 多文件（5 个 DLL，现行） | 内嵌资源单文件 |
   | --- | --- | --- |
   | 部署物 | 5 个 .dll，文件名即版本 | 1 个 .dll，内嵌版本不可见 |
   | 升级粒度 | 换哪个缺哪个 | 整包重编 |
   | 调试 | 栈帧与文件直接对应 | 栈帧仅类型名（内存模块） |
   | 版本漂移 | 肉眼可见 | **隐蔽**：目录残留同名旧 DLL 会静默遮蔽内嵌版 |
   | 类型身份 | 正常绑定共享框架 | 内嵌依赖私有化——不得与宿主共享同名程序集（升级时核对） |
   | 失败模式 | 缺文件 → 明确 FileNotFound | 遮蔽/漂移 → 静默跑旧版 |

   决策：多文件为默认；单文件化仅在"分发只允许投递一个文件"成为真实需求时启用。

---

## 15. 工具组扩展框架（DAF-MCP 路线）

### 15.1 新增一个工具组 = 什么

工具组是 `DFLegacy.Mcp` 内部的一个组织单元：**一个子目录（`Tap/`、`Pvf/`、`Catalogs/`…）内的一组静态 `[McpServerToolType]` 工具类 + 可选 Resources/Prompts**，所需服务端服务通过构造函数 DI 注入。新增流程：在 `DFLegacy.Mcp` 新增 `<Group>Tools.cs` →（如需）在 `McpModule.Attach` 注册 → 冒烟 + 文档。**服务端代码零改动**；开关与端点不变；客户端不换配置即可发现新工具。

### 15.2 G2 · PVF 解密读取（下一个实现，届时另开新档细化）

让智能体直接查 `Script.pvf`——列结构、搜路径、读并解码文件内容，用于逆向比对、配置核对（掉落/强化/技能数值）与脚本调试。服务端已有全部底层能力，工具组只是包一层 MCP 面：

| 拟定工具 | 映射的现有实现 | 说明 |
| --- | --- | --- |
| `pvf_stat` | `ScriptFileSystem.IsPvf / FileCount / SourceDescription` | PVF 是否加载、文件数、来源 |
| `pvf_list` | `ScriptFileSystem.EnumeratePaths(prefix)` | 列目录/按前缀检索虚拟路径 |
| `pvf_search` | `EnumeratePaths` + 内存过滤 | 按子串/扩展名全局搜索 |
| `pvf_read_text` | `ScriptFileSystem.ReadAllText / ReadAllLines` | 返回解码后脚本文本 |
| `pvf_read_bytes` | `ScriptFileSystem.ReadAllBytes` | 原始字节 + hex/长度，供格式分析 |
| `pvf_binary_info` | `PvfArchiveReader` 条目表 | 归档条目：偏移、压缩后大小（衔接 exe-unpacking-guide.md） |
| `pvf_parse_script` | 参考 ServerS4A21 `PvfLib.ScriptParser`（Parse → 节点树）思路自研 | 脚本解析为结构化键值/节点树，比纯文本更利于智能体比对字段（§16.1） |

约束：只读；单次返回文本/字节设上限，超长分页。

### 15.3 G3+ · 候选池（不承诺，按需立项）

- **游戏数据查询**：`ItemCatalog` / `SkillCatalog` / `DungeonCatalog` / 各 DropCatalog / 经验表——纯内存只读。
- **存档查询**：`JsonGameStore` 的账号/角色/邮件读取面。
- **运行时状态**：在线会话、疲劳、副本运行态（与 G1 的 `session_list` 分工：G1 管网络会话，这里管游戏态）。

### 15.4 分组开关？

现阶段不做：单一 `Enabled` 开关 + 全组只读，配置面最小。出现"只想要 PVF 不想抓包"的真实需求后，再在模块内加 `EnabledGroups` 数组——仍是模块内部改动，服务端无感。

---

## 16. 与参考项目 ServerS4A21 的对标与选型结论

参考项目 `ServerS4A21` 中没有 MCP，但有三件同题的前代设计，DAF-MCP 是它们的正升级而非另起炉灶：

### 16.1 三件套对照

| 能力 | ServerS4A21（前代） | DAF-MCP（本档） | 结论 |
| --- | --- | --- | --- |
| 服务端包捕获 | `PacketFileLogger`：`--packet-capture` 开关（默认关）→ 4096 有界异步队列 → 落盘 `packet_log.txt`（64MB 上限，满则丢 + 计数告警） | `PacketTapHub`：`Enabled`（默认关）→ ring(4096) + 订阅通道(1024, DropOldest) → MCP 长轮询 | **继承全部工程纪律**（有界队列、丢弃计数、默认关闭、截断上限）；消费端从"人 tail 文件"升级为"智能体游标实时拉取"，且多出报文名/明文富化 |
| 带外抓包 | `PvfProxy` 独立进程：TCP 中间人（7001/10011 → 7002/10012），逐行日志，bat 编排拉起全家桶 | 不需要：tap 在进程内协议层（解密后帧 + RX wire 双份） | 中间人方案保留为 fallback 认知：当需要抓"不经服务端代码路径"的流量时它仍是正解 |
| PVF 读取 | `Tool/PvfLib` 独立库：`PvfDecryptor`（Decrypt/Guard + zlib）、`PvfUnpacker/PvfPacker`、`ScriptParser`（Parse → 节点树） | G2（§15.2）：现有 `ScriptFileSystem/PvfArchiveReader` 覆盖读路径；`pvf_parse_script` 参考 `ScriptParser` 思路 | PvfLib 的能力面是 G2 的验收参照；但其"独立成库"的组织方式**不采纳**——PVF 工具归入 `DFLegacy.Mcp` 单一项目（P4） |

### 16.2 端口规划互认

参考项目 1 万位段被游戏协议密集占用（10011/10012/10100/10161/10162/10068/10200/10128，UDP 12311/12312，17200，UDP relay 30000+）。DAF-MCP **默认端口取 12222**：避开上述全部区段，保留"1 万位段 = 工具/辅助服务"的语感；端口可在 `server.json` 修改（§8）。

### 16.3 选型结论：两套设计不是竞争关系

两套方案为**不同的消费者和失败模式**优化：

| 维度 | 文件日志 + 中间人（前代） | 进程内 tap + 标准 MCP（本档） |
| --- | --- | --- |
| 目标消费者 | 人：事后翻日志、grep、离线脚本 | 智能体：实时游标跟踪、按字段过滤、结构化返回 |
| 看到的数据 | 线路字节（代理侧是密文；服务端侧按固定头长解帧） | 解密后的帧 + 报文名 + 明文字段 + RX wire 双份 |
| 崩溃时 | **文件已落盘，可事后验尸** | ring 在内存，随进程消失 |
| 侵入性 | 代理方案对服务端零侵入 | 需要漏斗改造（§5.2，净行数为负） |
| 消费门槛 | 任何工具都能读文件 | 需要 MCP 客户端 |

**面向"智能体实时分析"，DAF-MCP 明显更合理**：文件日志对智能体意味着轮询 + 正则 + 上下文不可控，中间人拿到的密文对字段分析几乎无用，双进程端口编排脆弱。前代方案的优势场景（崩溃验尸、线路层真相）依然成立，处置是**并存分层**：现有 `EnablePacketTracing` 日志通道原样保留作持久验尸层；排查崩溃类问题时两者同开，MCP 管实时定位、文件日志管事后复盘。pcap 导出（§14）落地后补齐"线路层真相"一档，供 Wireshark 而非智能体使用。

---

## 17. 日志

MCP 的日志走**主进程的 ILoggerFactory**（`Attach` 时注入，`McpHost` 自持 WebApplication 的日志也接同一工厂）——输出与游戏服务同一渠道（控制台/文件按现有配置），**不产生第二份日志文件**，也**不写 `packet_log.txt`**（那仍是 `EnablePacketTracing` 通道的领地）。类别名约定 `DFLegacy.Mcp.*`。

### 17.1 自身运行日志（有）

| 阶段 | 事件 | 级别 | 处置 |
| --- | --- | --- | --- |
| 装配 | `Enabled=false`（默认）短路 | Debug | 一行说明后返回，默认 Info 级别下不可见 |
| 装配 | `Enabled=true` 但 `DFLegacy.Mcp.dll` 不存在 | Warning | 继续，模块不激活 |
| 装配 | 加载/初始化抛异常 | Warning + 异常栈 | 继续，`TapSink` 保持 null |
| 装配 | 激活成功 | Information | 端点 URL、工具组数 |
| 宿主 | 监听启动 / 停止 | Information | `http://{Host}:{Port}/mcp` |
| 宿主 | 端口被占 / 绑定失败 | Error | 模块保持关闭，进程正常 |
| 工具 | 工具执行抛异常 | Warning | 工具名 + 参数摘要（不含包体） |
| Tap | `Publish` 内部捕获到异常 | Warning | 低频；追踪绝不影响会话 |

### 17.2 明确不打印的（设计红线）

1. **包内容不进日志**：body/wire hex、报文名、明文只经 MCP 工具流返回给订阅的智能体，不写日志——避免双份记录、避免协议数据落盘、避免热路径字符串开销。
2. **逐次工具调用不打日志**：`packet_watch` 是 10 秒级长轮询，逐调用记 Info 会淹没真正的问题；调用频次与丢弃统计由 `tap_status` 工具按需提供，`subscriberDropped` 计数随事件返回。
3. **ring/订阅通道丢弃不逐条告警**：仅计数上报；若需告警，未来可加"丢弃率超阈值"一条 Warning（§14 候选）。
