using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Numerics;
using System.Text;
using DFLegacy.Server;

namespace DFLegacy.Protocol;

/// <summary>
/// Reads the DNF_SCRIPT_PACK archive used by the DFLegacy client without
/// extracting it to a directory.
/// </summary>
public sealed class PvfArchiveReader
{
    private static ReadOnlySpan<byte> Magic => "DNF_SCRIPT_PACK"u8;

    private readonly byte[] _archive;
    private readonly int _bodyOffset;
    private readonly IReadOnlyDictionary<string, Entry> _entries;
    private readonly ConcurrentDictionary<string, byte[]> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public PvfArchiveReader(string path)
    {
        ArchivePath = Path.GetFullPath(path);
        (_archive, _bodyOffset, _entries) = Open(ArchivePath);
    }

    public string ArchivePath { get; }

    public int FileCount => _entries.Count;

    public bool FileExists(string virtualPath) =>
        _entries.ContainsKey(NormalizePath(virtualPath));

    public byte[] ReadAllBytes(string virtualPath)
    {
        var normalized = NormalizePath(virtualPath);
        if (!_entries.TryGetValue(normalized, out var entry))
        {
            throw new FileNotFoundException(
                $"PVF file '{normalized}' was not found in {ArchivePath}.",
                normalized);
        }

        return _cache.GetOrAdd(normalized, _ => ReadEntry(entry)).ToArray();
    }

    private byte[] ReadEntry(Entry entry)
    {
        var paddedLength = checked((entry.Length + 3) & ~3);
        var absoluteOffset = checked(_bodyOffset + (int)entry.Offset);
        if (absoluteOffset < _bodyOffset
            || paddedLength < 0
            || absoluteOffset > _archive.Length - paddedLength)
        {
            throw new InvalidDataException(
                $"PVF entry '{entry.Path}' points outside the archive.");
        }

        var content = _archive.AsSpan(absoluteOffset, paddedLength).ToArray();
        DecryptInPlace(content, entry.Crc32);
        return content.AsSpan(0, entry.Length).ToArray();
    }

    private static (byte[] Archive, int BodyOffset, IReadOnlyDictionary<string, Entry> Entries)
        Open(string path)
    {
        var archive = File.ReadAllBytes(path);
        var headerLength = checked(Magic.Length + 12);
        if (archive.Length < headerLength
            || !archive.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new InvalidDataException($"{path} is not a DNF_SCRIPT_PACK archive.");
        }

        var header = archive.AsSpan(Magic.Length, 12);
        var treeLength = BinaryPrimitives.ReadInt32LittleEndian(header[..4]);
        var treeCrc32 = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(4, 4));
        var fileCount = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(8, 4));
        if (treeLength <= 0
            || treeLength % 4 != 0
            || treeLength > archive.Length - headerLength
            || fileCount > int.MaxValue)
        {
            throw new InvalidDataException($"{path} has an invalid PVF tree header.");
        }

        var tree = archive.AsSpan(headerLength, treeLength).ToArray();
        DecryptInPlace(tree, treeCrc32);
        var pathEncoding = PvfEncodings.Cp949();
        var entries = new Dictionary<string, Entry>(
            checked((int)fileCount),
            StringComparer.OrdinalIgnoreCase);
        var cursor = 0;
        for (var index = 0u; index < fileCount; index++)
        {
            if (cursor > tree.Length - 20)
            {
                throw new InvalidDataException(
                    $"{path} ended inside PVF tree entry {index}.");
            }

            var offset = BinaryPrimitives.ReadUInt32LittleEndian(tree.AsSpan(cursor + 4, 4));
            var length = BinaryPrimitives.ReadInt32LittleEndian(tree.AsSpan(cursor + 8, 4));
            var crc32 = BinaryPrimitives.ReadUInt32LittleEndian(tree.AsSpan(cursor + 12, 4));
            var pathLength = BinaryPrimitives.ReadInt32LittleEndian(tree.AsSpan(cursor + 16, 4));
            cursor += 20;
            if (length < 0 || pathLength <= 0 || pathLength > tree.Length - cursor)
            {
                throw new InvalidDataException(
                    $"{path} contains an invalid PVF tree entry {index}.");
            }

            var virtualPath = NormalizePath(
                pathEncoding.GetString(tree, cursor, pathLength).TrimEnd('\0'));
            cursor += pathLength;
            entries[virtualPath] = new Entry(offset, length, crc32, virtualPath);
        }

        return (archive, checked(headerLength + treeLength), entries);
    }

    private static void DecryptInPlace(Span<byte> bytes, uint key)
    {
        if (bytes.Length % 4 != 0)
        {
            throw new InvalidDataException("PVF encrypted data is not dword aligned.");
        }

        for (var offset = 0; offset < bytes.Length; offset += 4)
        {
            var ciphertext = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.Slice(offset, 4),
                BitOperations.RotateRight(ciphertext ^ key, 6));
        }
    }

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/').TrimStart('/').ToLowerInvariant();

    private sealed record Entry(uint Offset, int Length, uint Crc32, string Path);
}
