using System.Buffers.Binary;
using System.Text;
using DFLegacy.Server;

namespace DFLegacy.Protocol;

public sealed record EntranceChannel(
    string Host,
    int Port,
    int MaximumUsers,
    int CurrentUsers);

/// <summary>
/// Builds the decompressed payload consumed by CNEntranceChannel::applyChannelInfo.
/// The DFLegacy 1.0.1.9 client requires all nine server-group records, followed by each
/// group's fixed-width channel records (docs/protocol/60a4-handshake-protocol.md §11.3).
/// Channel display names come from the catalog (resolved against etc/etc.chn.str);
/// the game endpoint, capacity and live user count are server-wide values shared by
/// every record.
/// </summary>
public static class EntranceMetadata
{
    public static readonly string[] ServerGroups =
    [
        "cain", "diregie", "siroco", "prey",
        "casillas", "hilder", "ruke", "seria", "anton"
    ];

    /// <summary>
    /// 单频道回退的显示名（沿用真机验证过的默认字节；PVF 内无有效频道目录时使用。
    /// 显示名与频道号不再来自 server.json，回退名收敛为本常量）。
    /// </summary>
    public const string SingleChannelDisplayName = "Local Channel 1";

    public static byte[] BuildSingleLocalChannel(EntranceChannel channel) =>
        BuildChannels(channel, []);

    /// <summary>
    /// Builds the entrance metadata from a channel catalog: catalog channels land
    /// in their group (etc group 1 parses to group index 0) and every group
    /// without entries keeps the single default record, preserving the nine-group
    /// layout the 60A1 client requires.
    /// </summary>
    public static byte[] BuildChannels(
        EntranceChannel channel,
        IReadOnlyList<ChannelCatalogEntry> entries)
    {
        ValidateChannelDefaults(channel);
        ArgumentNullException.ThrowIfNull(entries);

        var groups = new List<ChannelCatalogEntry>[ServerGroups.Length];
        for (var index = 0; index < groups.Length; index++)
        {
            groups[index] = [];
        }

        var seen = new HashSet<(int GroupIndex, int ChannelNumber)>();
        foreach (var entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            if (string.IsNullOrEmpty(entry.DisplayName))
            {
                throw new ArgumentException(
                    "Catalog entries must carry a resolved display name.", nameof(entries));
            }

            ArgumentOutOfRangeException.ThrowIfLessThan(entry.GroupIndex, 0);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(entry.GroupIndex, ServerGroups.Length - 1);
            ArgumentOutOfRangeException.ThrowIfLessThan(entry.ChannelNumber, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(entry.ChannelNumber, 255);
            if (!seen.Add((entry.GroupIndex, entry.ChannelNumber)))
            {
                throw new ArgumentException(
                    $"Duplicate channel entry: group {entry.GroupIndex} number {entry.ChannelNumber}.",
                    nameof(entries));
            }

            groups[entry.GroupIndex].Add(entry);
        }

        foreach (var group in groups)
        {
            group.Sort(static (left, right) => left.ChannelNumber.CompareTo(right.ChannelNumber));
        }

        using var stream = new MemoryStream();
        Span<byte> integer = stackalloc byte[4];
        WriteInt32(stream, ServerGroups.Length, integer);

        for (var index = 0; index < ServerGroups.Length; index++)
        {
            WriteFixedAscii(stream, ServerGroups[index], 20);
            if (groups[index].Count == 0)
            {
                WriteInt32(stream, 1, integer);
                WriteChannelRecord(stream, SingleChannelDisplayName, channel, integer);
                continue;
            }

            WriteInt32(stream, groups[index].Count, integer);
            foreach (var entry in groups[index])
            {
                WriteChannelRecord(stream, entry.DisplayName, channel, integer);
            }
        }

        return stream.ToArray();
    }

    private static void ValidateChannelDefaults(EntranceChannel channel)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channel.Port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channel.Port, 65535);
        ArgumentOutOfRangeException.ThrowIfNegative(channel.MaximumUsers);
        ArgumentOutOfRangeException.ThrowIfNegative(channel.CurrentUsers);
    }

    private static void WriteChannelRecord(
        Stream stream,
        string displayName,
        EntranceChannel channel,
        Span<byte> integer)
    {
        WriteFixedClientText(stream, displayName, 20);
        WriteInt32(stream, channel.MaximumUsers, integer);
        WriteInt32(stream, channel.CurrentUsers, integer);
        WriteFixedAscii(stream, channel.Host, 16);
        WriteInt32(stream, channel.Port, integer);
    }

    /// <summary>
    /// Writes a fixed-width client-text field (CP936): values must fit with the
    /// trailing NUL the client's strlen-based readers rely on; oversize is a
    /// broken catalog and fails fast like the ASCII writer.
    /// </summary>
    private static void WriteFixedClientText(Stream stream, string value, int width)
    {
        var encoded = PvfEncodings.Cp936Lossy().GetBytes(value);
        if (encoded.Length >= width)
        {
            throw new ArgumentException(
                $"Client text must be shorter than {width} bytes: {value}");
        }

        Span<byte> field = stackalloc byte[width];
        field.Clear();
        encoded.CopyTo(field);
        stream.Write(field);
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
