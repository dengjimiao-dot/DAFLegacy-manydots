namespace DFLegacy.Server;

public sealed class ServerOptions
{
    // 配置与数据锚点（R5）：由组合根按 --home / DFLEGACY_HOME / BaseDirectory
    // 解析后写入；配置内相对路径一律以它为基准。
    public string HomeDirectory { get; set; } = AppContext.BaseDirectory;

    public AdminOptions Admin { get; set; } = new();
    public McpOptions Mcp { get; set; } = new();
    public ListenerOptions Entrance { get; set; } = new() { Port = 8080 };
    public ListenerOptions CharacterDatagram { get; set; } = new() { Host = "127.0.0.1", Port = 2311 };
    public GameplayDatagramOptions GameplayDatagram { get; set; } = new();
    public ListenerOptions GameProbe { get; set; } = new() { Port = 7001, Enabled = false };
    public string DataPath { get; set; } = "data/state.json";
    public string ScriptPvfPath { get; set; } = "data/Script.pvf";
    public string SkillScriptPath { get; set; } = "";
    public string OriginalSkillScriptPath { get; set; } = "";
    public ChannelOptions Channel { get; set; } = new();
    public DropOptions Drop { get; set; } = new();
    public ExperienceOptions Experience { get; set; } = new();
    public int MaximumPacketLength { get; set; } = 4 * 1024 * 1024;
    public bool RejectBadCrc32 { get; set; } = false;
    public bool EnablePacketTracing { get; set; } = false;
    public int HexDumpLimit { get; set; } = 64;
    // 登录成功后的 entrance 握手在即将获取频道列表（协议 1/3）之前延迟 N 秒
    // 响应，让客户端停留在 loading 加载界面，模拟正式服的加载节奏。0 表示关闭。
    public int LoginHandshakeDelaySeconds { get; set; } = 3;
}

public sealed class GameplayDatagramOptions
{
    public bool Enabled { get; set; } = true;
    public string Host { get; set; } = "127.0.0.1";
    public int PrimaryPort { get; set; } = 7002;
    public int SecondaryPort { get; set; } = 7003;
    public ushort MonsterObjectType { get; set; } = 0x0211;
    public byte SenderPartyIndex { get; set; } = 0;
}

public sealed class DropOptions
{
    public bool Enabled { get; set; } = true;
    public double RatePercent { get; set; } = 100;
    public double EconomicRate { get; set; } = 1;
    public bool ForceDrops { get; set; }
}

public sealed class ExperienceOptions
{
    public double MonsterMultiplier { get; set; } = 1;
    public double ClearMultiplier { get; set; } = 1;
    public double BlackDiamondClearBonusRate { get; set; } = 0.05;
    public double ClearEventBonusRate { get; set; }
    public DateTimeOffset? ClearEventStartsAt { get; set; }
    public DateTimeOffset? ClearEventEndsAt { get; set; }
}

public sealed class ChannelOptions
{
    public int ServerNumber { get; set; } = 1;
    public string Name { get; set; } = "Local Channel";
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 7001;
    public int ChannelNumber { get; set; } = 1;
    public int MaximumUsers { get; set; } = 100;
    public int CurrentUsers { get; set; } = 1;
}

public sealed class AdminOptions
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8081;
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
