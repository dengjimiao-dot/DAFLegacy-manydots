using DAFLegacy.Server;

namespace DAFLegacy.Protocol;

/// <summary>
/// 协议级固定值：帧类型标记，以及各玩法域在线上出现的定值（疲劳上限、会员服务号、
/// 教学关标识、翻牌列数与冠军序号、拒绝错误码）。这些值同时被构建器、handler、
/// 会话发送器与存储层引用，故集中一处；协议号本身见 <see cref="CmdPacketType"/> /
/// <see cref="NotiPacketType"/>（Network/Core/PacketTypes.cs）。
/// </summary>
public static class GameProtocolConstants
{
    // 帧类型标记：传输头首字节，0=服务端推送、1=客户端命令。两向共用同一字节，
    // 值本身不标识协议（07 §4.8.3 规则 5），名称不带方向后缀以示区别。
    public const byte NotificationPacketType = 0;
    public const byte CommandPacketType = 1;

    public const int ClearRewardCardColumnCount = 4;
    public const sbyte NoChampionRewardOrdinal = -1;
    public const sbyte MaximumChampionRewardOrdinal = 11;
    public const byte DungeonItemActionCommand = (byte)CmdPacketType.USE_STACKABLE;
    public const ushort TutorialDungeonId = 10_000;
    public const ushort TutorialMapId = 61_000;
    public const uint TutorialCompletionModule = 31;
    public const ushort DefaultMaximumFatigue = 156;
    public const ushort BlackDiamondBonusFatigue = 32;
    public const ushort BlackDiamondMaximumFatigue =
        DefaultMaximumFatigue + BlackDiamondBonusFatigue;

    // DF2008 CMD 15 reads error 0x16 followed by the exhausted party-member index.
    public const byte EnterDungeonSelectionInsufficientFatigueError = 0x16;
    // DF2008 在 0x410A3C 读取错误码 17 与队伍槽位字节，随后以所选副本的入场道具
    // 与数量格式化 2100 号字符串。
    public const byte SelectDungeonMissingRequiredItemError = 17;
    // MOVE_MAP 错误 0x13 拒绝无效的副本生命周期状态；DF2008 的 0x00414331 分支
    // 只读一个错误字节并清除 actor +0x244 的移动标记。
    public const byte MoveMapInvalidStateError = 0x13;
    // 13339 check_boss_map 对未满足 [boss room entrance condition] 返回 7；
    // DF2008 按同一个单字节 MOVE_MAP 错误消费，无附加字段。
    public const byte MoveMapBossEntranceConditionError = 7;
    // DF2008 starts ground-item pickup optimistically. CMD 46 error 0x15 stays
    // silent but enters the common rollback at 0x00414198, which clears the
    // pending pickup flag at item-object + 0x7A5 so the same item falls back.
    public const byte GetItemSilentRejectionError = 0x15;
    // 错误码 4 在 0x00414170 格式化 793 号字符串，并走同一回滚：
    // 弹出背包操作、解除客户端对地面物品的乐观拾取锁定。
    public const byte GetItemInventoryFullError = 0x04;
    public const uint DefaultCompletedTutorialFlags = uint.MaxValue;
    public const byte BlackDiamondServiceType = 16;
    public const byte OverlordContractServiceType = 22;
    public const byte MasterContractServiceType = 27;
    public const ushort PremiumServiceUpdateAction = 2;
}
