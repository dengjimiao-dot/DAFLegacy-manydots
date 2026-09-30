using DAFLegacy.Protocol;
using DAFLegacy.Server.Sqlite;

namespace DAFLegacy.Server;

/// <summary>
/// Session-state senders and coarse state readers shared across handler
/// groups (07 §4.7/§4.8.2: cross-domain flows keep one canonical
/// implementation; sends go through <see cref="IConnectionQueue"/> so every
/// frame passes the single-writer pump, 07 §4.4). Split into partial files
/// by functional block: Social, Mailbox, Quest, Skill, Inventory, Creature,
/// Town, Dungeon, DungeonRewards; this file keeps the shared state readers
/// and the entrance content cache.
/// </summary>
public sealed partial class ChannelSessionSenders(
    IGameStore store,
    ItemCatalog itemCatalog,
    EntranceChannelDirectory channelDirectory,
    ServerOptions options,
    CharacterStatCatalog characterStatCatalog,
    CharacterSpCatalog spCatalog,
    CreatureExperienceCatalog creatureExperienceCatalog,
    CharacterSessionRegistry characterSessions,
    PartyCoordinator partyCoordinator,
    QuestCatalog questCatalog,
    DungeonCatalog dungeonCatalog,
    PvpExperienceCatalog pvpExperienceCatalog,
    CharacterPacketBuilders builders
)
{
    /// <summary>会话频道（NOTI 1/130 频道号来源）：目录首条目或单频道回退，
    /// M1 单监听形态下所有会话共享。</summary>
    public EntranceSessionChannel SessionChannel => channelDirectory.Session;

    public GameSkillEntry[] GetCurrentGameSkills(ConnectionContext context) => context.State.Skill.Current
        .OrderBy(skill => skill.Slot)
        .Select(skill => new GameSkillEntry(skill.SkillId, skill.Level, skill.Slot))
        .ToArray();

    public bool HasActivePremiumService(ConnectionContext context, byte serviceType) =>
        context.State.Premium.ExpiryUnix.TryGetValue(serviceType, out var expiryUnix)
        && expiryUnix > DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public byte[] GetActivePremiumServiceTypes(ConnectionContext context)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return context.State.Premium.ExpiryUnix
            .Where(entry => entry.Value > now)
            .Select(entry => entry.Key)
            .ToArray();
    }

    public byte GetProjectedStaminaRecovery(ConnectionContext context)
    {
        if (context.State.Character.Active is null
            || !context.State.Character.Active.IsWeakened
            || HasActivePremiumService(context,
                GameProtocolConstants.BlackDiamondServiceType))
        {
            return DungeonWeaknessPolicy.FullStamina;
        }

        return (byte)Math.Clamp(
            context.State.Character.Active.WeaknessRecovery,
            DungeonWeaknessPolicy.MinimumRecovery,
            DungeonWeaknessPolicy.FullStamina);
    }

    internal EntranceContent LoadContent()
    {
        // CurrentUsers 取在线会话数动态值：入口元数据随每次握手现建，
        // 拥挤度（在线/容量 > 95%）随之实时变化。
        var channel = options.Channel;
        var defaults = new EntranceChannel(
            channel.Host,
            channel.Port,
            channel.MaximumUsers,
            characterSessions.SessionCount);
        var metadata = channelDirectory.Catalog is { Count: > 0 } catalog
            ? EntranceMetadata.BuildChannels(defaults, catalog)
            : EntranceMetadata.BuildSingleLocalChannel(defaults);
        return new EntranceContent(channelDirectory.ChannelScript, metadata);
    }
}
