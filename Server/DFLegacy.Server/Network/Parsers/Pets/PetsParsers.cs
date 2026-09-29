using System.Buffers.Binary;
using System.Text;

namespace DFLegacy.Server;

/// <summary>
/// Creature request parsing (07 §4.7): RENAME_CREATURE (CMD 103) and
/// HATCH_CREATURE (CMD 105) bodies.
/// </summary>
internal static class PetsParsers
{
    private static readonly Encoding ClientEncoding = PvfEncodings.Cp936Lossy();

    internal static bool TryReadRenameCreature(
        byte[] body,
        out ushort cardSlot,
        out byte itemSpace,
        out byte[] nameBytes)
    {
        cardSlot = 0;
        itemSpace = 0;
        nameBytes = [];
        const int fixedLength = 9;
        if (body.Length < fixedLength)
        {
            return false;
        }

        cardSlot = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(2, 2));
        itemSpace = body[4];
        var nameLength = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(5, 4));
        if (nameLength is 0 or > 12
            || nameLength > int.MaxValue
            || body.Length != fixedLength + (int)nameLength)
        {
            return false;
        }

        nameBytes = body.AsSpan(fixedLength, (int)nameLength).ToArray();
        return !nameBytes.Contains((byte)0);
    }

    internal static bool TryReadHatchCreature(
        byte[] body,
        out byte itemSpace,
        out ushort slot)
    {
        itemSpace = 0;
        slot = 0;
        if (body.Length < 5)
        {
            return false;
        }

        itemSpace = body[2];
        slot = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(3, 2));
        return true;
    }
}
