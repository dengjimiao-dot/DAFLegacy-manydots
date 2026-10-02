using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

public sealed record EntranceSessionChannel(int Number, string DisplayName);

/// <summary>
/// 入口频道目录的一次性解析快照（启动期构建）。频道目录唯一真源是 PVF 内
/// etc/channel_info.etc（[server] 组号由 Channel.ServerGroup 配置，默认 1），显示名经 etc/etc.chn.str 的名称引用解析；
/// server.json 的 Channel 节只承载协议面身份（Host/Port/容量），不提供频道目录。
/// 目录缺失或无有效行时回退单频道默认记录
/// （<see cref="EntranceMetadata.SingleChannelDisplayName"/>），行为与旧单频道一致。
/// </summary>
public sealed class EntranceChannelDirectory
{
    public EntranceChannelDirectory(
        ScriptFileSystem scripts,
        ServerOptions options,
        ILogger<EntranceChannelDirectory> logger)
    {
        byte[]? channelScript = null;
        string? channelInfoEtc = null;
        if (scripts.FileExists(ChannelCatalog.ChannelInfoEtcPath))
        {
            channelScript = scripts.ReadAllBytes(ChannelCatalog.ChannelInfoEtcPath);
            channelInfoEtc = scripts.ReadAllText(
                ChannelCatalog.ChannelInfoEtcPath,
                PvfEncodings.Cp936Lossy());
        }
        else
        {
            logger.LogWarning(
                "{ChannelInfoPath} is missing from {Source}; the entrance serves the single local channel and protocol 9 download is disabled.",
                ChannelCatalog.ChannelInfoEtcPath,
                scripts.SourceDescription);
        }

        IReadOnlyDictionary<string, string>? displayNames = null;
        if (scripts.FileExists(ChannelCatalog.ChannelStringTablePath))
        {
            displayNames = ChannelCatalog.TryParseChannelStringTable(
                scripts.ReadAllText(
                    ChannelCatalog.ChannelStringTablePath,
                    PvfEncodings.Cp936Lossy()));
        }
        else if (channelScript is not null)
        {
            logger.LogWarning(
                "{StringTablePath} is missing from {Source}; channel rows without a resolvable display name are dropped.",
                ChannelCatalog.ChannelStringTablePath,
                scripts.SourceDescription);
        }

        Catalog = ChannelCatalog.TryParseChannelInfoEtc(
            channelInfoEtc,
            displayNames,
            options.Channel.ServerGroup);
        ChannelScript = channelScript;
        Session = Catalog is { Count: > 0 } catalog
            ? new EntranceSessionChannel(catalog[0].ChannelNumber, catalog[0].DisplayName)
            : new EntranceSessionChannel(1, EntranceMetadata.SingleChannelDisplayName);

        // 启动即打印频道列表（回退单频道时同样提示），便于部署后目视核对目录真源。
        if (Catalog is { Count: > 0 } entries)
        {
            logger.LogInformation(
                "Entrance channel catalog: {ChannelCount} channels from {Source}; session channel {SessionNumber} \"{SessionDisplayName}\"; endpoint {Host}:{Port}, capacity {MaximumUsers}.",
                entries.Count,
                scripts.SourceDescription,
                Session.Number,
                Session.DisplayName,
                options.Channel.Host,
                options.Channel.Port,
                options.Channel.MaximumUsers);
            foreach (var entry in entries)
            {
                logger.LogInformation(
                    "  channel {ChannelNumber}: {DisplayName} (type {Type}, tag {Tag})",
                    entry.ChannelNumber,
                    entry.DisplayName,
                    entry.Type,
                    entry.Tag ?? "-");
            }
        }
        else
        {
            logger.LogInformation(
                "Entrance channel catalog unavailable; serving the single local channel \"{DisplayName}\" at {Host}:{Port}.",
                EntranceMetadata.SingleChannelDisplayName,
                options.Channel.Host,
                options.Channel.Port);
        }
    }

    /// <summary>协议 9 下发的频道脚本字节（etc/channel_info.etc 原文）；缺失时为 null。</summary>
    public byte[]? ChannelScript { get; }

    /// <summary>解析出的频道目录（组 1，按频道号升序）；无有效行时为 null。</summary>
    public IReadOnlyList<ChannelCatalogEntry>? Catalog { get; }

    /// <summary>
    /// 遗留频道端口（<c>Channel.Port</c>）与无目录回退时的会话频道（NOTI 1
    /// displayName/channelNumber、喇叭 flag 与经验加成键的频道号来源）：
    /// 目录首条目。M2 后常规会话频道由 accept 该连接的频道监听器决定
    /// （<see cref="ChannelSessionState.SessionChannel"/>），本值仅作其回退。
    /// </summary>
    public EntranceSessionChannel Session { get; }
}
