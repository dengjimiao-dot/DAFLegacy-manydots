namespace DAFLegacy.Server;

public sealed class ServerOptions
{
    // 配置与数据锚点（R5）：由组合根按 --home / DAFLEGACY_HOME / BaseDirectory
    // 解析后写入；配置内相对路径一律以它为基准。
    public string HomeDirectory { get; set; } = AppContext.BaseDirectory;

    public AdminOptions Admin { get; set; } = new();
    public LoggingOptions Logging { get; set; } = new();
    public PacketCaptureOptions PacketCapture { get; set; } = new();
    public McpOptions Mcp { get; set; } = new();
    public AccountOptions Accounts { get; set; } = new();
    public ListenerOptions Entrance { get; set; } = new();
    public ListenerOptions CharacterDatagram { get; set; } = new();
    public GameplayDatagramOptions GameplayDatagram { get; set; } = new();

    // 持久化库文件（R5 数据锚点）：相对路径以 HomeDirectory 为基准，默认落在
    // Data/game.sqlite，与基线 schema（Sqlite/act1_schema.sql）分属「运行期数据」与
    // 「代码资产」两个目录。库不存在时按基线建空库，已存在时直接使用、不重建
    // （docs/design/08-sqlite-storage.md D1/D2）。
    public string DatabasePath { get; set; } = "Data/game.sqlite";

    public string ScriptPvfPath { get; set; } = "data/Script.pvf";
    public string SkillScriptPath { get; set; } = "";
    public string OriginalSkillScriptPath { get; set; } = "";
    public ChannelOptions Channel { get; set; } = new();

    // 强化毁坏补偿覆写（docs/design/02 R5）：节点缺省或 ItemId=0 时循 PVF
    // etc/upgrade.etc 的可选 [disjoint] 段；ItemId>0 时整体取代该段。
    public ReinforcementOptions Reinforcement { get; set; } = new();

    public int MaximumPacketLength { get; set; } = 4 * 1024 * 1024;
    public bool RejectBadCrc32 { get; set; } = false;
    public bool EnablePacketTracing { get; set; } = false;
    public int HexDumpLimit { get; set; } = 64;
}

/// <summary>
/// 强化系统配置。当前仅承载毁坏补偿（CMD 83 resultCode=3 随应答下发的补偿物品，
/// 语义循 PVF [disjoint] 段，见 docs/design/02 R5）。<see cref="Enabled"/> 为总开关：
/// false 时毁坏补偿整体关闭，DestroyBonus 节点与 PVF [disjoint] 段一并失效。
/// </summary>
public sealed class ReinforcementOptions
{
    public bool Enabled { get; set; } = true;

    public DestroyBonusOptions DestroyBonus { get; set; } = new();
}

/// <summary>
/// 毁坏补偿覆写参数。ItemId=0 表示不覆写（循 PVF）。数量 =（目标强化等级 −
/// BonusCountLevelBase)² × 品级权重（向下取整）；EquipLevelConst 为按品级修正后的
/// 装备等级门槛（0 = 不设门槛）。键限定 common/uncommon/rare/unique/epic，
/// 由 ServerOptionsValidator 在启动期校验。
/// </summary>
public sealed class DestroyBonusOptions
{
    public int ItemId { get; set; }
    public int EquipLevelConst { get; set; }
    public int BonusCountLevelBase { get; set; }
    public Dictionary<string, double> BonusWeightByRarity { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> CorrectionGradeByRarity { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class GameplayDatagramOptions
{
    public bool Enabled { get; set; } = true;
    public string Host { get; set; } = "127.0.0.1";
    public int PrimaryPort { get; set; }
    public int SecondaryPort { get; set; }
    public ushort MonsterObjectType { get; set; } = 0x0211;
    public byte SenderPartyIndex { get; set; } = 0;

    /// <summary>
    /// NAT 探测按源 IP 固定窗口限流（次/秒/IP，双端口监听共用同一限流器；
    /// 合法客户端探测节奏约 2 次/秒）。&lt;= 0 表示不限流（docs/design/10 §4.3）。
    /// </summary>
    public int NatProbeRateLimit { get; set; } = 32;
}

/// <summary>
/// 频道监听端点（协议面身份，留在 server.json）。频道目录不在本节——唯一真源是
/// PVF 内 etc/channel_info.etc（显示名经 etc/etc.chn.str 解析，见
/// EntranceChannelDirectory）；<c>CurrentUsers</c> 为在线会话数动态值，
/// <c>MaximumUsers</c> 是容量上报值（客户端以在线/容量 &gt;95% 判拥挤）。
/// <para>M2 按频道监听：目录里每条频道占 <c>PortBase + 频道号</c> 一个端口；目录
/// 缺失时空组回退记录与单频道会话共用 <c>PortBase + 会话频道号</c> 的端点，不再有
/// 独立的遗留频道端口（端口分配见 docs/design/02 R6）。</para>
/// </summary>
public sealed class ChannelOptions
{
    public int ServerNumber { get; set; } = 1;
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>按频道监听的端口基值；频道 N 的端口 = PortBase + N（无默认值，必须显式配置）。</summary>
    public int PortBase { get; set; }

    /// <summary>频道容量上报值（元数据 max 字段）。</summary>
    public int MaximumUsers { get; set; } = 10;

    /// <summary>
    /// PVF 频道目录（etc/channel_info.etc）的 [server] 组号（1 基；默认 1）。
    /// 仅收录该组的频道行，元数据 GroupIndex = ServerGroup − 1（对齐
    /// EntranceMetadata.ServerGroups 的 0 基下标）。此前组号写死 1。
    /// </summary>
    public int ServerGroup { get; set; } = 1;

    /// <summary>频道 <paramref name="channelNumber"/> 的监听端口（PortBase + 频道号）。</summary>
    public int PortFor(int channelNumber) => checked(PortBase + channelNumber);
}

/// <summary>
/// 账号策略（登录期行为）。协议面不新增字段，仅决定 CMD 1 收到不存在的账号时
/// 服务端如何应答（见 docs/protocol/packet-detail-login-character-60cn.md CMD 1）。
/// </summary>
public sealed class AccountOptions
{
    /// <summary>
    /// 登录时账号不存在则用提交的账号/口令直接建号（默认开启）。关闭后按原行为回
    /// 登录失败错误码。建号沿用管理接口的账号名规则（4-32 位）但不要求 QQ 码，口令
    /// 只要求非空（登录表单没有单独的开号步骤，见 GameStoreCore.TryCreateLoginAccountAsync）。
    /// </summary>
    public bool CreateMissingAccountsOnLogin { get; set; } = true;
}

public sealed class AdminOptions
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; }
}

/// <summary>
/// 统一文件日志（docs/design/07 §11 日志统一）。默认关闭：关闭时不注册提供器，
/// 不建队列、不建后台任务、不落盘。Directory 相对 HomeDirectory 解析；
/// MaximumFileBytes 为 0 表示不轮转（参考项目 server.log 语义）。
/// </summary>
public sealed class LoggingOptions
{
    /// <summary>仅门控文件日志器（logs/server.log），默认关闭；控制台日志无开关、始终输出，不受本项影响。</summary>
    public bool Enabled { get; set; }

    public string Directory { get; set; } = "logs";

    public string FileName { get; set; } = "server.log";

    public int QueueCapacity { get; set; } = 8192;

    public long MaximumFileBytes { get; set; }
}

/// <summary>
/// 逐包十六进制捕获（自参考项目 PacketFileLogger 迁移，默认关闭；
/// 暴露协议流，开启前确认部署边界）。Directory 缺省时落 logs/。
/// </summary>
public sealed class PacketCaptureOptions
{
    public bool Enabled { get; set; }

    public string Directory { get; set; } = string.Empty;

    public string FileName { get; set; } = "packet_log.txt";

    public int QueueCapacity { get; set; } = 4096;

    public long MaximumFileBytes { get; set; } = 64L * 1024 * 1024;

    public int HexDumpLimit { get; set; } = 256;
}

/// <summary>
/// DAF-MCP 总网关（docs/design/09-mcp-packet-tap.md §8）。默认关闭：关闭时
/// 不开端口、不建线程、零内存驻留。Host 默认回环，改非回环等同主动暴露
/// 协议流与游戏数据。
/// </summary>
public sealed class McpOptions
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; }
}

/// <summary>
/// 监听端点。端口无代码默认值：必须在 server.json 显式配置，缺失、越界或与
/// 同主机同传输的其他端口重复时由 <see cref="ServerOptionsValidator"/> 在
/// 启动期拒绝（docs/design/02 R6）。
/// </summary>
public sealed class ListenerOptions
{
    public bool Enabled { get; set; } = true;
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; }
    public int[] AdditionalPorts { get; set; } = [];
}
