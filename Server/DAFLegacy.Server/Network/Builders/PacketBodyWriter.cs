using System.Buffers.Binary;

namespace DAFLegacy.Server;

/// <summary>
/// Shared packet-body writers (07 §4.7): length-prefixed bytes and IPv4
/// address encoding. Moved verbatim from the R7 engine split.
/// </summary>
internal static class PacketBodyWriter
{
    internal static void WriteLengthPrefixedBytes(BinaryWriter writer, byte[] value)
    {
        writer.Write((uint)value.Length);
        writer.Write(value);
    }

    internal static void WriteIpv4Address(BinaryWriter writer, System.Net.IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var bytes = address.MapToIPv4().GetAddressBytes();
        writer.Write(bytes);
    }
}
