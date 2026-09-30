using System.Threading.Channels;

namespace DAFLegacy.Server;

/// <summary>
/// Per-connection session state split into domains (07 §4.3). Fields map one
/// to one to the former HandleClientAsync closure variables; a domain may
/// only be touched by the handler groups its owning large category names in
/// 07 §4.8.2. <see cref="Events"/> is the single cross-thread intake: other
/// threads enqueue events, only this connection's read loop dequeues them.
/// </summary>
public sealed class ChannelSessionState
{
    public LoginState Login { get; } = new();
    public RosterState Roster { get; } = new();
    public CharacterState Character { get; } = new();
    public InventoryState Inventory { get; } = new();
    public SkillState Skill { get; } = new();
    public QuestState Quest { get; } = new();
    public DungeonState Dungeon { get; } = new();
    public CurrencyState Currency { get; } = new();
    public MailboxState Mailbox { get; } = new();
    public PremiumState Premium { get; } = new();
    public WeaknessState Weakness { get; } = new();

    /// <summary>
    /// 本连接绑定的频道（M2：由 accept 该连接的频道监听器决定；入口握手连接为 null）。
    /// NOTI 1 的 channelNumber/displayName、喇叭 NOTI 130 的 flag 字节与频道经验加成
    /// 均以它为源。
    /// </summary>
    public EntranceSessionChannel? SessionChannel { get; set; }

    public Channel<ConnectionEvent> Events { get; } =
        Channel.CreateUnbounded<ConnectionEvent>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>
    /// Coarse session phase derived from the state domains; finer scene gates
    /// stay inside handlers (07 §4.8.1).
    /// </summary>
    public CommandPhase CurrentPhase => Dungeon.CurrentDungeonId != 0 ? CommandPhase.InDungeon
        : Character.Active is not null ? CommandPhase.CharacterSelected
        : Login.AccountUid != AccountUidRules.InvalidUid ? CommandPhase.Authenticated
        : CommandPhase.Any;
}
