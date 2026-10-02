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
    TownPresenceService townPresence,
    PartyCoordinator partyCoordinator,
    QuestCatalog questCatalog,
    DungeonCatalog dungeonCatalog,
    PvpExperienceCatalog pvpExperienceCatalog,
    CharacterPacketBuilders builders
)
{
    /// <summary>会话频道回退值（M2 后常规取 <see cref="ChannelSessionState.SessionChannel"/>，
    /// 即 accept 该连接的监听器绑定频道；此属性保留为未绑定会话的兜底）。</summary>
    public EntranceSessionChannel SessionChannel => channelDirectory.Session;

    public GameSkillEntry[] GetCurrentGameSkills(ConnectionContext context) => context.State.Skill.Current
        .OrderBy(skill => skill.Slot)
        .Select(skill => new GameSkillEntry(skill.SkillId, skill.Level, skill.Slot))
        .ToArray();

    /// <summary>会话现场的 mode-3 详情快照（组队成员互发；11 号 P0）。未选角返回 null。</summary>
    public GameServerPacket? BuildMode3Details(ChannelSessionState state)
    {
        var active = state.Character.Active;
        if (active is null)
        {
            return null;
        }

        return CharactersPacketBuilders.CreateCurrentCharacterDetails(
            state.Character.LocalUserId,
            experience: active.Experience,
            learnedSkills: state.Skill.Current
                .OrderBy(skill => skill.Slot)
                .Select(skill => new GameSkillEntry(skill.SkillId, skill.Level, skill.Slot))
                .ToArray(),
            combatStats: state.Character.LastProjectedCombatStats,
            equippedItems: builders.CreateWornEquipmentSnapshot(
                state.Inventory.Equipped.Values,
                state.Inventory.Creature),
            equippedCreatureLevel: CharacterPacketBuilders.GetEquippedCreatureLevel(
                state.Inventory.Creature),
            mode: 3);
    }

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
        // CurrentUsers 按频道取中枢连接注册数（批次 3：TownPresenceService 是人口
        // 唯一真源）：拥挤度（在线/容量 > 95%）逐频道生效，未绑定连接的遗留端点
        // 记录取其绑定频道的计数。
        var channel = options.Channel;
        var defaults = new EntranceChannel(
            channel.Host,
            channel.Port,
            channel.MaximumUsers,
            townPresence.GetChannelPopulation(channelDirectory.Session.Number));
        var metadata = channelDirectory.Catalog is { Count: > 0 } catalog
            ? EntranceMetadata.BuildChannels(
                defaults,
                catalog,
                channel.PortBase,
                townPresence.GetChannelPopulation)
            : EntranceMetadata.BuildSingleLocalChannel(defaults);
        return new EntranceContent(channelDirectory.ChannelScript, metadata);
    }
}
