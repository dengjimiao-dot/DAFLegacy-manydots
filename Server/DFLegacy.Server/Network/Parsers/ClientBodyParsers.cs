using System.Buffers.Binary;

namespace DFLegacy.Server;

/// <summary>
/// Shared reader for the client's length-prefixed body fields (07 §4.7:
/// request parsing lives outside the handlers). The wire layout is a
/// uint32 little-endian byte count followed by that many bytes.
/// </summary>
internal static class ClientBodyParsers
{
    internal static bool TryReadLengthPrefixedBytes(
        byte[] source,
        int offset,
        out byte[] value,
        out int nextOffset)
    {
        value = [];
        nextOffset = offset;
        if (offset < 0 || source.Length - offset < sizeof(uint))
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(offset, sizeof(uint)));
        offset += sizeof(uint);
        if (length > int.MaxValue || source.Length - offset < (int)length)
        {
            return false;
        }

        value = source.AsSpan(offset, (int)length).ToArray();
        nextOffset = offset + (int)length;
        return true;
    }
}
