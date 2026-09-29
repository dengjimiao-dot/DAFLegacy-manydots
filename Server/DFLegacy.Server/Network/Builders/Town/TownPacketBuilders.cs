using System.Buffers.Binary;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// Town packet builders (07 §4.8.3 三层镜像: Builders/Town).
/// Moved verbatim from GameProtocolEngine (R7); byte output unchanged.
/// </summary>
internal static class TownPacketBuilders
{
    public static GameServerPacket CreatePrivateStorePollReply() =>
        new(
            GameProtocolEngine.CommandPacketType,
            (byte)CmdPacketType.CALL_MEMER_LIST,
            [
                1,             // result
                0, 0,          // local store state
                0, 0, 0, 0,   // empty store name
                0, 0,          // store flags
                0, 0, 0, 0,   // first store value
                0, 0, 0, 0,   // second store value
                0              // additional store count
            ]);
    public static GameServerPacket CreateUserPosition(
        GameTownUser user,
        ushort movementValue = 100)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(user.UserId);
        writer.Write(user.X);
        writer.Write(user.Y);
        writer.Write(user.Direction);
        writer.Write(movementValue);
        return new GameServerPacket(
            GameProtocolEngine.NotificationPacketType,
            (byte)NotiPacketType.USER_POSITION,
            payload.ToArray());
    }
    public static GameServerPacket CreateUserArea(
        byte townId,
        byte areaId,
        GameTownUser user)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(user.UserId);
        writer.Write(townId);
        writer.Write(areaId);
        writer.Write(user.X);
        writer.Write(user.Y);
        writer.Write(user.Direction);
        writer.Write((byte)1); // active town actor
        return new GameServerPacket(
            GameProtocolEngine.NotificationPacketType,
            (byte)NotiPacketType.USER_AREA,
            payload.ToArray());
    }
    public static GameServerPacket CreateAreaUsers(
        byte townId,
        byte areaId,
        IReadOnlyList<GameTownUser> users)
    {
        if (users.Count > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(users));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(townId);
        writer.Write(areaId);
        writer.Write((ushort)users.Count);
        foreach (var user in users)
        {
            writer.Write(user.UserId);
            writer.Write(user.X);
            writer.Write(user.Y);
            writer.Write(user.Direction);
            writer.Write((byte)1); // active town actor
        }

        return new GameServerPacket(
            GameProtocolEngine.NotificationPacketType,
            (byte)NotiPacketType.AREA_USERS,
            payload.ToArray());
    }
    /// <summary>NOTI 6 removes the town user record for the uid (reader at
    /// 0x41B3C8 drops the map entry at 0x41B467 and cleans the scene).</summary>
    public static GameServerPacket CreateUserLeave(ushort userId)
    {
        var payload = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, userId);
        return new GameServerPacket(
            GameProtocolEngine.NotificationPacketType,
            (byte)NotiPacketType.USER_LEAVE,
            payload);
    }
}
