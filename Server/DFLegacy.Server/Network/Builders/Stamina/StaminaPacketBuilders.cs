using System.Buffers.Binary;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// Stamina packet builders (07 §4.8.3 三层镜像: Builders/Stamina).
/// Moved verbatim from GameProtocolEngine (R7); byte output unchanged.
/// </summary>
internal static class StaminaPacketBuilders
{
    public static GameServerPacket CreateEnterDungeonSelectionInsufficientFatigueReply(
        byte partyMemberIndex = 0) =>
        new(
            GameProtocolEngine.CommandPacketType,
            (byte)CmdPacketType.START_GAME,
            [0, GameProtocolEngine.EnterDungeonSelectionInsufficientFatigueError, partyMemberIndex]);
    public static GameServerPacket CreatePremiumServiceNotification(
        ushort action,
        byte serviceType,
        uint remainSeconds)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(action);
        writer.Write(serviceType);
        writer.Write(remainSeconds);
        return new GameServerPacket(
            GameProtocolEngine.NotificationPacketType,
            (byte)NotiPacketType.CERA_SPECIALITEM,
            payload.ToArray());
    }
    public static GameServerPacket CreateStamina(byte value) =>
        new(GameProtocolEngine.NotificationPacketType, (byte)NotiPacketType.STAMINA, [value]);
    public static GameServerPacket CreateRecoverStaminaReply(int remainingGold)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write((uint)Math.Max(0, remainingGold));
        return new GameServerPacket(
            GameProtocolEngine.CommandPacketType,
            (byte)CmdPacketType.RECOVER_STAMINA,
            payload.ToArray());
    }
    public static GameServerPacket CreateRecoverStaminaError(byte errorCode) =>
        new(
            GameProtocolEngine.CommandPacketType,
            (byte)CmdPacketType.RECOVER_STAMINA,
            [0, errorCode]);
    public static GameServerPacket CreateFatigue(
        ushort usedFatigue = 0,
        ushort maximumFatigue = GameProtocolEngine.DefaultMaximumFatigue,
        ushort premiumFatigue = 0)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(usedFatigue);
        writer.Write(maximumFatigue);
        writer.Write(premiumFatigue);
        return new GameServerPacket(
            GameProtocolEngine.NotificationPacketType,
            (byte)NotiPacketType.FATIGUE,
            payload.ToArray());
    }
    public static GameServerPacket CreateEventInfo(uint featureFlags = 0)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(featureFlags);
        return new GameServerPacket(
            GameProtocolEngine.NotificationPacketType,
            (byte)NotiPacketType.EVENT_INFO,
            payload.ToArray());
    }
}
