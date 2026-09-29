using System.Buffers.Binary;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// Pets packet builders (07 §4.8.3 三层镜像: Builders/Pets).
/// Moved verbatim from GameProtocolEngine (R7); byte output unchanged.
/// </summary>
internal static class PetsPacketBuilders
{
    public static GameServerPacket CreateCreatureInfo(
        IReadOnlyList<GameCreatureInfoEntry> creatures)
    {
        if (creatures.Count > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(creatures));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(checked((byte)creatures.Count));
        foreach (var creature in creatures)
        {
            if (creature.Uid == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(creatures));
            }

            if (creature.NameBytes.Length >= 30)
            {
                throw new ArgumentOutOfRangeException(nameof(creatures));
            }

            writer.Write(creature.Uid);
            writer.Write(creature.Stomach);
            writer.Write(creature.Experience);
            writer.Write(creature.Level);
            PacketBodyWriter.WriteLengthPrefixedBytes(writer, creature.NameBytes);
            writer.Write((byte)(creature.NoCharge ? 1 : 0));
        }

        return new GameServerPacket(
            GameProtocolEngine.NotificationPacketType,
            GameProtocolEngine.CreatureInfoNotification,
            payload.ToArray());
    }
    public static GameServerPacket CreateCreatureState(
        uint creatureUid,
        uint state)
    {
        if (creatureUid == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(creatureUid));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(creatureUid);
        writer.Write(state);
        return new GameServerPacket(
            GameProtocolEngine.NotificationPacketType,
            GameProtocolEngine.CreatureStateNotification,
            payload.ToArray());
    }
    public static GameServerPacket CreateDiedCreature(ushort userId)
    {
        if (userId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(userId));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        // NOTI 100 is not keyed by the Creature uid.  The old client first
        // resolves this area-user id and then marks that user's Creature map
        // object as dead.
        writer.Write(userId);
        return new GameServerPacket(
            GameProtocolEngine.NotificationPacketType,
            GameProtocolEngine.DiedCreatureNotification,
            payload.ToArray());
    }
    public static GameServerPacket CreateRevivalCreature(ushort userId)
    {
        if (userId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(userId));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        // NOTI 107 has the same one-field layout as NOTI 100.  It clears the
        // death flag on the resolved Creature object.
        writer.Write(userId);
        return new GameServerPacket(
            GameProtocolEngine.NotificationPacketType,
            GameProtocolEngine.RevivalCreatureNotification,
            payload.ToArray());
    }
    public static GameServerPacket CreateCreatureGainExperience(
        byte level,
        uint cumulativeExperience)
    {
        if (level == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(level));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        // DF2008 NOTI 102 has no Creature uid or normal/grow discriminator.
        // The client applies this fixed record to the currently equipped pet.
        writer.Write(level);
        writer.Write(cumulativeExperience);
        return new GameServerPacket(
            GameProtocolEngine.NotificationPacketType,
            GameProtocolEngine.CreatureGainExperienceNotification,
            payload.ToArray());
    }
    public static GameServerPacket CreateCreatureEvolution(
        byte evolutionCreatureId,
        ushort userId)
    {
        if (evolutionCreatureId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(evolutionCreatureId));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(evolutionCreatureId);
        writer.Write(userId);
        return new GameServerPacket(
            GameProtocolEngine.NotificationPacketType,
            GameProtocolEngine.CreatureEvolutionNotification,
            payload.ToArray());
    }
    public static GameServerPacket CreateCreatureRename(
        ushort userId,
        byte[] nameBytes)
    {
        if (userId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(userId));
        }

        if (nameBytes.Length is 0 or >= 20)
        {
            throw new ArgumentOutOfRangeException(nameof(nameBytes));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(userId);
        PacketBodyWriter.WriteLengthPrefixedBytes(writer, nameBytes);
        return new GameServerPacket(
            GameProtocolEngine.NotificationPacketType,
            GameProtocolEngine.CreatureRenameNotification,
            payload.ToArray());
    }
    public static GameServerPacket CreateCreatureResponse(ushort userId)
    {
        if (userId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(userId));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(userId);
        return new GameServerPacket(
            GameProtocolEngine.NotificationPacketType,
            GameProtocolEngine.CreatureResponseNotification,
            payload.ToArray());
    }
    public static GameServerPacket CreateHatchCreatureReply(
        bool success,
        byte errorCode = 1) =>
        new(
            GameProtocolEngine.CommandPacketType,
            GameProtocolEngine.HatchCreatureCommand,
            success ? [1] : [0, errorCode]);
    public static GameServerPacket CreateRenameCreatureReply(
        bool success,
        ushort cardSlot = 0,
        byte itemSpace = 7,
        byte errorCode = 1)
    {
        if (!success)
        {
            return new(
                GameProtocolEngine.CommandPacketType,
                GameProtocolEngine.RenameCreatureCommand,
                [0, errorCode]);
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write(cardSlot);
        writer.Write(itemSpace);
        return new GameServerPacket(
            GameProtocolEngine.CommandPacketType,
            GameProtocolEngine.RenameCreatureCommand,
            payload.ToArray());
    }
}
