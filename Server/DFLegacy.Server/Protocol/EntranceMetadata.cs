using System.Buffers.Binary;
using System.Text;

namespace DFLegacy.Protocol;

public sealed record EntranceChannel(
    string Name,
    string Host,
    int Port,
    int ChannelNumber = 1,
    int MaximumUsers = 100,
    int CurrentUsers = 1);

/// <summary>
/// Builds the decompressed payload consumed by CNEntranceChannel::applyChannelInfo.
/// The DFLegacy 1.0.1.9 client requires all nine server-group records, followed by each
/// group's fixed-width channel records.
/// </summary>
public static class EntranceMetadata
{
    public static readonly string[] ServerGroups =
    [
        "cain", "diregie", "siroco", "prey",
        "casillas", "hilder", "ruke", "seria", "anton"
    ];

    public static byte[] BuildSingleLocalChannel(EntranceChannel channel)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channel.Port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channel.Port, 65535);
        ArgumentOutOfRangeException.ThrowIfLessThan(channel.ChannelNumber, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(channel.MaximumUsers);
        ArgumentOutOfRangeException.ThrowIfNegative(channel.CurrentUsers);
        if (channel.Name.Any(char.IsDigit))
        {
            throw new ArgumentException("The display-name prefix must not contain digits.", nameof(channel));
        }

        using var stream = new MemoryStream();
        Span<byte> integer = stackalloc byte[4];
        WriteInt32(stream, ServerGroups.Length, integer);

        for (var index = 0; index < ServerGroups.Length; index++)
        {
            WriteFixedAscii(stream, ServerGroups[index], 20);
            WriteInt32(stream, 1, integer);

            var displayName = $"{channel.Name} {channel.ChannelNumber}";
            WriteFixedAscii(stream, displayName, 20);
            WriteInt32(stream, channel.MaximumUsers, integer);
            WriteInt32(stream, channel.CurrentUsers, integer);
            WriteFixedAscii(stream, channel.Host, 16);
            WriteInt32(stream, channel.Port, integer);
        }

        return stream.ToArray();
    }

    private static void WriteInt32(Stream stream, int value, Span<byte> buffer)
    {
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteFixedAscii(Stream stream, string value, int width)
    {
        var encoded = Encoding.ASCII.GetBytes(value);
        if (encoded.Length >= width)
        {
            throw new ArgumentException($"ASCII text must be shorter than {width} bytes: {value}");
        }

        Span<byte> field = stackalloc byte[width];
        field.Clear();
        encoded.CopyTo(field);
        stream.Write(field);
    }
}
