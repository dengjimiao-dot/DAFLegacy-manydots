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

        // 强化毁坏补偿覆写（docs/design/02 R5）：
        // 缺省节点（ItemId=0，不覆写）不报错，其余字段即使误填也不拦截启用路径。
        check(ServerOptionsValidator.ValidateReinforcementDestroyBonus(new ServerOptions()).Count == 0,
            "an absent destroy bonus override validates");
        check(ServerOptionsValidator.ValidateReinforcementDestroyBonus(new ServerOptions
        {
            Reinforcement = new ReinforcementOptions
            {
                DestroyBonus = new DestroyBonusOptions { ItemId = 3037 }
            }
        }).Any(error => error.Contains("every rarity weight is zero", StringComparison.Ordinal)),
            "an enabled destroy bonus without any positive rarity weight is rejected");

        var validBonus = new ServerOptions
        {
            Reinforcement = new ReinforcementOptions
            {
                DestroyBonus = new DestroyBonusOptions
                {
                    ItemId = 3037,
                    EquipLevelConst = 40,
                    BonusCountLevelBase = 10,
                    BonusWeightByRarity = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["rare"] = 1,
                        ["UNIQUE"] = 1.2,
                        ["epic"] = 1.5
                    },
                    CorrectionGradeByRarity = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["common"] = 1,
                        ["uncommon"] = 1,
                        ["rare"] = 2,
                        ["unique"] = 4,
                        ["epic"] = 5
                    }
                }
            }
        };
        check(ServerOptionsValidator.ValidateReinforcementDestroyBonus(validBonus).Count == 0,
            "a fully configured destroy bonus override validates (rarity keys are case-insensitive)");

        var switchedOff = CloneBonus(validBonus);
        switchedOff.Reinforcement.Enabled = false;
        check(ServerOptionsValidator.ValidateReinforcementDestroyBonus(switchedOff).Count == 0,
            "a disabled reinforcement switch makes the destroy bonus node inert (no validation errors)");

        var oversizedItemId = CloneBonus(validBonus);
        oversizedItemId.Reinforcement.DestroyBonus.ItemId = ushort.MaxValue + 1;
        check(ServerOptionsValidator.ValidateReinforcementDestroyBonus(oversizedItemId)
                .Any(error => error.Contains("DestroyBonus.ItemId", StringComparison.Ordinal)),
            "an item id above the packet width is rejected");

        var negativeConst = CloneBonus(validBonus);
        negativeConst.Reinforcement.DestroyBonus.EquipLevelConst = -1;
        negativeConst.Reinforcement.DestroyBonus.BonusCountLevelBase = -1;
        var negativeConstErrors = ServerOptionsValidator.ValidateReinforcementDestroyBonus(negativeConst);
        check(negativeConstErrors.Any(error => error.Contains("EquipLevelConst", StringComparison.Ordinal))
                && negativeConstErrors.Any(error => error.Contains("BonusCountLevelBase", StringComparison.Ordinal)),
            "negative gate constants are rejected");

        var unknownRarity = CloneBonus(validBonus);
        unknownRarity.Reinforcement.DestroyBonus.BonusWeightByRarity["chronicle"] = 1;
        check(ServerOptionsValidator.ValidateReinforcementDestroyBonus(unknownRarity)
                .Any(error => error.Contains("is not a rarity name", StringComparison.Ordinal)),
            "weight keys outside the rarity name list are rejected");

        var negativeValues = CloneBonus(validBonus);
        negativeValues.Reinforcement.DestroyBonus.BonusWeightByRarity["rare"] = -0.5;
        negativeValues.Reinforcement.DestroyBonus.CorrectionGradeByRarity["epic"] = -1;
        var negativeValueErrors = ServerOptionsValidator.ValidateReinforcementDestroyBonus(negativeValues);
        check(negativeValueErrors.Any(error => error.Contains("BonusWeightByRarity.rare", StringComparison.Ordinal))
                && negativeValueErrors.Any(error => error.Contains("CorrectionGradeByRarity.epic", StringComparison.Ordinal)),
            "negative weights and correction grades are rejected");
    }

    private static ServerOptions CloneBonus(ServerOptions source) => new()
    {
        Reinforcement = new ReinforcementOptions
        {
            DestroyBonus = new DestroyBonusOptions
            {
                ItemId = source.Reinforcement.DestroyBonus.ItemId,
                EquipLevelConst = source.Reinforcement.DestroyBonus.EquipLevelConst,
                BonusCountLevelBase = source.Reinforcement.DestroyBonus.BonusCountLevelBase,
                BonusWeightByRarity = new Dictionary<string, double>(
                    source.Reinforcement.DestroyBonus.BonusWeightByRarity,
                    StringComparer.OrdinalIgnoreCase),
                CorrectionGradeByRarity = new Dictionary<string, int>(
                    source.Reinforcement.DestroyBonus.CorrectionGradeByRarity,
                    StringComparer.OrdinalIgnoreCase)
            }
        }
    };

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
