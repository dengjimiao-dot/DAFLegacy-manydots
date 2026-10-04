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
    public ListenerOptions Entrance { get; set; } = new() { Port = 8080 };
    public ListenerOptions CharacterDatagram { get; set; } = new() { Host = "127.0.0.1", Port = 2311 };
    public GameplayDatagramOptions GameplayDatagram { get; set; } = new();
    public ListenerOptions GameProbe { get; set; } = new() { Port = 7001, Enabled = false };

    // 持久化库文件（R5 数据锚点）：相对路径以 HomeDirectory 为基准，默认落在
    // Data/game.sqlite，与基线 schema（Sqlite/act1_schema.sql）分属「运行期数据」与
    // 「代码资产」两个目录。库不存在时按基线建空库，已存在时直接使用、不重建
    // （docs/design/08-sqlite-storage.md D1/D2）。
    public string DatabasePath { get; set; } = "Data/game.sqlite";

    public string ScriptPvfPath { get; set; } = "data/Script.pvf";
    public string SkillScriptPath { get; set; } = "";
    public string OriginalSkillScriptPath { get; set; } = "";
    public ChannelOptions Channel { get; set; } = new();
    public int MaximumPacketLength { get; set; } = 4 * 1024 * 1024;
    public bool RejectBadCrc32 { get; set; } = false;
    public bool EnablePacketTracing { get; set; } = false;
    public int HexDumpLimit { get; set; } = 64;
}

public sealed class GameplayDatagramOptions
{
    public bool Enabled { get; set; } = true;
    public string Host { get; set; } = "127.0.0.1";
    public int PrimaryPort { get; set; } = 7002;
    public int SecondaryPort { get; set; } = 7003;
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
/// <para>M2 按频道监听：目录里每条频道占 <c>PortBase + 频道号</c> 一个端口；
/// <c>Port</c> 是遗留单频道端口，恒绑定目录首条目身份（无目录时即单频道回退监听）。</para>
/// </summary>
public sealed class ChannelOptions
{
    public int ServerNumber { get; set; } = 1;
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 7001;

    /// <summary>按频道监听的端口基值；频道 N 的端口 = PortBase + N（默认 10000）。</summary>
    public int PortBase { get; set; } = 10000;

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

public sealed class AdminOptions
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8081;
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
    public int Port { get; set; } = 12222;
}

public sealed class ListenerOptions
{
    public bool Enabled { get; set; } = true;
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; }
    public int[] AdditionalPorts { get; set; } = [];
}
