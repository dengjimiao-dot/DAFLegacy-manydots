using System.Net;

namespace DAFLegacy.Server;

/// <summary>
/// 监听端点强校验（docs/design/02 R6）：全部端口无代码默认值，必须在 server.json
/// 显式配置。缺失、越界、主机名（非 IP 字面量）以及同一主机同一传输上的重复占用
/// 都在启动期拒绝——组合根记录每条错误并以非零码退出，避免以隐式端口启动或静默
/// 丢弃一条监听。返回空列表表示配置可用。
/// </summary>
public static class ServerOptionsValidator
{
    private const int MinimumPort = 1;
    private const int MaximumPort = 65535;

    public static IReadOnlyList<string> ValidateListenerEndpoints(ServerOptions options)
    {
        var errors = new List<string>();
        var tcpEndpoints = new Dictionary<(IPAddress Address, int Port), string>();
        var udpEndpoints = new Dictionary<(IPAddress Address, int Port), string>();

        // 管理 HTTP 面没有 Enabled 开关：其端口恒为必需项。频道面的监听端口是
        // PortBase + 频道号（目录驱动），这里只校验基值与绑定主机。
        ClaimEndpoint("Admin", "TCP", options.Admin.Host, options.Admin.Port, tcpEndpoints, errors);
        ValidateHost("Channel", options.Channel.Host, errors, out _);
        ValidatePortRange("Channel.PortBase", options.Channel.PortBase, errors);
        if (options.Channel.PortBase >= MaximumPort)
        {
            errors.Add(
                $"Channel.PortBase must stay below {MaximumPort} so channel 1 fits "
                + $"(PortBase + channel number <= {MaximumPort}).");
        }

        if (options.Mcp.Enabled)
        {
            ClaimEndpoint("Mcp", "TCP", options.Mcp.Host, options.Mcp.Port, tcpEndpoints, errors);
        }

        if (options.Entrance.Enabled)
        {
            ClaimEndpoint("Entrance", "TCP", options.Entrance.Host, options.Entrance.Port, tcpEndpoints, errors);
            for (var index = 0; index < options.Entrance.AdditionalPorts.Length; index++)
            {
                ClaimEndpoint(
                    $"Entrance.AdditionalPorts[{index}]",
                    "TCP",
                    options.Entrance.Host,
                    options.Entrance.AdditionalPorts[index],
                    tcpEndpoints,
                    errors);
            }
        }

        if (options.CharacterDatagram.Enabled)
        {
            ClaimEndpoint(
                "CharacterDatagram",
                "UDP",
                options.CharacterDatagram.Host,
                options.CharacterDatagram.Port,
                udpEndpoints,
                errors);

            // 角色屏 UDP 与入口 TCP 必须同号：客户端用启动器注入的同一端点做角色屏
            // 的 UDP 交换，两者是一个端点在两种传输上的两份配置（docs/design/02 R6）。
            if (options.Entrance.Enabled
                && options.Entrance.Port is >= MinimumPort and <= MaximumPort
                && options.CharacterDatagram.Port is >= MinimumPort and <= MaximumPort
                && options.Entrance.Port != options.CharacterDatagram.Port)
            {
                errors.Add(
                    $"CharacterDatagram.Port ({options.CharacterDatagram.Port}) must equal "
                    + $"Entrance.Port ({options.Entrance.Port}): the client runs its character-screen "
                    + "UDP exchange on the launcher-injected entrance endpoint.");
            }
        }

        if (options.GameplayDatagram.Enabled)
        {
            ClaimEndpoint(
                "GameplayDatagram.PrimaryPort",
                "UDP",
                options.GameplayDatagram.Host,
                options.GameplayDatagram.PrimaryPort,
                udpEndpoints,
                errors);
            ClaimEndpoint(
                "GameplayDatagram.SecondaryPort",
                "UDP",
                options.GameplayDatagram.Host,
                options.GameplayDatagram.SecondaryPort,
                udpEndpoints,
                errors);
        }

        return errors;
    }

    private static void ClaimEndpoint(
        string key,
        string transport,
        string host,
        int port,
        Dictionary<(IPAddress Address, int Port), string> claimed,
        List<string> errors)
    {
        if (!ValidatePortRange(key, port, errors)
            || !ValidateHost(key, host, errors, out var address))
        {
            return;
        }

        if (claimed.TryGetValue((address, port), out var owner))
        {
            errors.Add($"{key} ({transport} {address}:{port}) is already used by {owner}.");
            return;
        }

        claimed[(address, port)] = key;
    }

    private static bool ValidateHost(
        string key,
        string host,
        List<string> errors,
        out IPAddress address)
    {
        if (IPAddress.TryParse(host, out var parsed))
        {
            address = parsed;
            return true;
        }

        address = IPAddress.None;
        errors.Add(
            $"{key}.Host \"{host}\" is not an IP literal; host names are forbidden (docs/design/02 R6).");
        return false;
    }

    /// <summary>
    /// 强化毁坏补偿覆写（docs/design/02 R5）启动期校验：键名白名单、取值范围与
    /// 「启用即至少一个品级权重为正」的防呆规则。返回空列表表示配置可用。
    /// </summary>
    public static IReadOnlyList<string> ValidateReinforcementDestroyBonus(
        ServerOptions options)
    {
        var errors = new List<string>();
        if (!options.Reinforcement.Enabled)
        {
            // 总开关关闭时节点整体不生效，不校验取值，避免惰性配置阻塞启动。
            return errors;
        }

        var bonus = options.Reinforcement.DestroyBonus;
        if (bonus.ItemId < 0 || bonus.ItemId > ushort.MaxValue)
        {
            errors.Add(
                $"Reinforcement.DestroyBonus.ItemId must stay within 0..{ushort.MaxValue} "
                + $"(0 disables the override and falls back to the PVF [disjoint] section); got {bonus.ItemId}.");
        }

        if (bonus.EquipLevelConst < 0)
        {
            errors.Add(
                $"Reinforcement.DestroyBonus.EquipLevelConst must not be negative; got {bonus.EquipLevelConst}.");
        }

        if (bonus.BonusCountLevelBase < 0)
        {
            errors.Add(
                $"Reinforcement.DestroyBonus.BonusCountLevelBase must not be negative; got {bonus.BonusCountLevelBase}.");
        }

        var positiveWeight = false;
        foreach (var pair in bonus.BonusWeightByRarity)
        {
            if (!IsKnownRarity(pair.Key, "BonusWeightByRarity", errors))
            {
                continue;
            }

            if (double.IsNaN(pair.Value) || double.IsInfinity(pair.Value))
            {
                errors.Add(
                    $"Reinforcement.DestroyBonus.BonusWeightByRarity.{pair.Key} must be a finite number; got {pair.Value}.");
                continue;
            }

            if (pair.Value < 0)
            {
                errors.Add(
                    $"Reinforcement.DestroyBonus.BonusWeightByRarity.{pair.Key} must not be negative; got {pair.Value}.");
                continue;
            }

            positiveWeight |= pair.Value > 0;
        }

        foreach (var pair in bonus.CorrectionGradeByRarity)
        {
            if (IsKnownRarity(pair.Key, "CorrectionGradeByRarity", errors)
                && pair.Value < 0)
            {
                errors.Add(
                    $"Reinforcement.DestroyBonus.CorrectionGradeByRarity.{pair.Key} must not be negative; got {pair.Value}.");
            }
        }

        if (bonus.ItemId > 0 && !positiveWeight)
        {
            errors.Add(
                "Reinforcement.DestroyBonus is enabled (ItemId > 0) but every rarity weight is zero; "
                + "set at least one positive BonusWeightByRarity value or remove the node to fall back "
                + "to the PVF [disjoint] section.");
        }

        return errors;
    }

    private static bool IsKnownRarity(
        string key,
        string nodeName,
        List<string> errors)
    {
        if (ReinforcementDisjointConfig.RarityNames.Contains(
                key,
                StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        errors.Add(
            $"{nodeName} key \"{key}\" is not a rarity name; expected one of: "
            + string.Join(", ", ReinforcementDisjointConfig.RarityNames) + ".");
        return false;
    }

    private static bool ValidatePortRange(string key, int port, List<string> errors)
    {
        if (port is >= MinimumPort and <= MaximumPort)
        {
            return true;
        }

        errors.Add(
            $"{key} must be {MinimumPort}..{MaximumPort}; port values are required in server.json "
            + "and have no defaults.");
        return false;
    }
}
