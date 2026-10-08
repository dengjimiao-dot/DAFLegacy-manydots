using DAFLegacy.Server;

internal static class ServerOptionsValidatorSmokeTests
{
    public static void Run(Action<bool, string> check)
    {
        // 模板形态（server.json 取值）：TCP 7001 与 UDP 7001 分属两种传输，不算冲突
        // （且入口与角色屏按约定必须同号）。
        check(ServerOptionsValidator.ValidateListenerEndpoints(CreateTemplateOptions()).Count == 0,
            "template endpoint values pass validation");

        // 端口无代码默认值：整份缺省配置必须逐项报错（Admin、PortBase、Entrance、
        // CharacterDatagram、GameplayDatagram 主/次共 6 项）。
        var missing = ServerOptionsValidator.ValidateListenerEndpoints(new ServerOptions());
        check(missing.Count == 6,
            $"a default-constructed configuration reports every missing port (got {missing.Count})");
        check(missing.Any(error => error.Contains("Entrance", StringComparison.Ordinal))
                && missing.Any(error => error.Contains("Channel.PortBase", StringComparison.Ordinal)),
            "missing-port errors name the offending keys");

        var missingMcp = CreateTemplateOptions();
        missingMcp.Mcp.Enabled = true;
        missingMcp.Mcp.Port = 0;
        check(ServerOptionsValidator.ValidateListenerEndpoints(missingMcp)
                .Any(error => error.Contains("Mcp", StringComparison.Ordinal)),
            "an enabled MCP gateway requires its port");

        var disabledSections = CreateTemplateOptions();
        disabledSections.Mcp.Enabled = true;
        disabledSections.Mcp.Port = 12222;
        disabledSections.Entrance.Enabled = false;
        disabledSections.Entrance.Port = 0;
        disabledSections.CharacterDatagram.Enabled = false;
        disabledSections.CharacterDatagram.Port = 0;
        disabledSections.GameplayDatagram.Enabled = false;
        disabledSections.GameplayDatagram.PrimaryPort = 0;
        disabledSections.GameplayDatagram.SecondaryPort = 0;
        check(ServerOptionsValidator.ValidateListenerEndpoints(disabledSections).Count == 0,
            "disabled sections do not require ports");

        var splitCharacterScreen = CreateTemplateOptions();
        splitCharacterScreen.CharacterDatagram.Port = 7002;
        check(ServerOptionsValidator.ValidateListenerEndpoints(splitCharacterScreen)
                .Any(error => error.Contains("must equal", StringComparison.Ordinal)),
            "the character-screen UDP port must match the entrance port");

        var duplicatedAdditional = CreateTemplateOptions();
        duplicatedAdditional.Entrance.AdditionalPorts = [7100, 7100];
        check(ServerOptionsValidator.ValidateListenerEndpoints(duplicatedAdditional)
                .Any(error => error.Contains("Entrance.AdditionalPorts[1]", StringComparison.Ordinal)),
            "duplicate additional entrance ports are rejected");

        var sharedDatagramPort = CreateTemplateOptions();
        sharedDatagramPort.GameplayDatagram.SecondaryPort = sharedDatagramPort.GameplayDatagram.PrimaryPort;
        check(ServerOptionsValidator.ValidateListenerEndpoints(sharedDatagramPort)
                .Any(error => error.Contains("GameplayDatagram.SecondaryPort", StringComparison.Ordinal)),
            "primary and secondary gameplay datagram ports must differ");

        var hostName = CreateTemplateOptions();
        hostName.Channel.Host = "localhost";
        check(ServerOptionsValidator.ValidateListenerEndpoints(hostName)
                .Any(error => error.Contains("not an IP literal", StringComparison.Ordinal)),
            "host names are rejected (R6 keeps every bind on IP literals)");

        var exhaustedPortBase = CreateTemplateOptions();
        exhaustedPortBase.Channel.PortBase = 65535;
        check(ServerOptionsValidator.ValidateListenerEndpoints(exhaustedPortBase)
                .Any(error => error.Contains("Channel.PortBase", StringComparison.Ordinal)),
            "a port base leaving no room for channel 1 is rejected");
    }

    private static ServerOptions CreateTemplateOptions() => new()
    {
        Admin = new AdminOptions { Host = "127.0.0.1", Port = 8081 },
        Mcp = new McpOptions { Enabled = false, Host = "127.0.0.1", Port = 12222 },
        Entrance = new ListenerOptions { Enabled = true, Host = "127.0.0.1", Port = 7001 },
        CharacterDatagram = new ListenerOptions { Enabled = true, Host = "127.0.0.1", Port = 7001 },
        GameplayDatagram = new GameplayDatagramOptions
        {
            Enabled = true,
            Host = "127.0.0.1",
            PrimaryPort = 12311,
            SecondaryPort = 12312
        },
        Channel = new ChannelOptions { Host = "127.0.0.1", PortBase = 10000 }
    };
}
