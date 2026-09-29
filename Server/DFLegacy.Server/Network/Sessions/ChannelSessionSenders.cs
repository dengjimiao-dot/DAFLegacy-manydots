using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// Session-state senders and coarse state readers shared across handler
/// groups (07 §4.7/§4.8.2: cross-domain flows keep one canonical
/// implementation; sends go through <see cref="IOutboundQueue"/> so every
/// frame passes the single-writer pump, 07 §4.4). Split into partial files
/// by functional block: Social, Mailbox, Quest, Skill, Inventory, Creature,
/// Town, Dungeon, DungeonRewards; this file keeps the shared state readers
/// and the entrance content cache.
/// </summary>
public sealed partial class ChannelSessionSenders(
    JsonGameStore store,
    ItemCatalog itemCatalog,
    ScriptFileSystem scripts,
    ServerOptions options,
    CharacterStatCatalog characterStatCatalog,
    CharacterSpCatalog spCatalog,
    CreatureExperienceCatalog creatureExperienceCatalog,
    CharacterSessionRegistry characterSessions,
    PartyCoordinator partyCoordinator,
    QuestCatalog questCatalog,
    DungeonCatalog dungeonCatalog,
    PvpExperienceCatalog pvpExperienceCatalog,
    CharacterPacketBuilders builders,
    ILogger<ChannelSessionSenders> logger)
{
    // Entrance content cache (migrated with LoadContent; single instance
    // shared with the pump's entrance-port path).
    private readonly object _contentGate = new();
    private EntranceContent? _content;

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
        if (_content is not null)
        {
            return _content;
        }

        lock (_contentGate)
        {
            if (_content is not null)
            {
                return _content;
            }

            byte[]? channelScript = null;
            const string virtualPath = "etc/channel_info.etc";
            if (scripts.FileExists(virtualPath))
            {
                channelScript = scripts.ReadAllBytes(virtualPath);
            }
            else
            {
                logger.LogWarning(
                    "{VirtualPath} is missing from {Source}; protocol 9 download is disabled.",
                    virtualPath,
                    scripts.SourceDescription);
            }

            var channel = options.Channel;
            var metadata = EntranceMetadata.BuildSingleLocalChannel(new EntranceChannel(
                channel.Name,
                channel.Host,
                channel.Port,
                channel.ChannelNumber,
                channel.MaximumUsers,
                channel.CurrentUsers));
            _content = new EntranceContent(channelScript, metadata);
            return _content;
        }
    }
}
