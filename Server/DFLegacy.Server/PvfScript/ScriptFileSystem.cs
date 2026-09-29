using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Numerics;
using System.Text;

namespace DFLegacy.Server;

/// <summary>
/// Provides read-only access to DNF script files. Production uses Script.pvf
/// directly; a directory is retained only as a test/development fallback.
/// </summary>
public sealed class ScriptFileSystem
{
    private static ReadOnlySpan<byte> PvfMagic => "DNF_SCRIPT_PACK"u8;

    private readonly ILogger<ScriptFileSystem> _logger;
    private readonly ServerOptions _options;
    private readonly byte[]? _archive;
    private readonly int _bodyOffset;
    private readonly IReadOnlyDictionary<string, PvfEntry> _entries;
    private readonly string? _directoryRoot;

    // R8：目录回退模式的大小写无关索引（虚拟路径 → 物理路径）。
    // 启动时一次枚举建立；查找先走索引，未命中再回退操作系统路径解析。
    private static readonly IReadOnlyDictionary<string, string> EmptyDirectoryIndex =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private readonly IReadOnlyDictionary<string, string> _directoryIndex = EmptyDirectoryIndex;
    private readonly ConcurrentDictionary<string, byte[]> _contentCache =
        new(StringComparer.OrdinalIgnoreCase);

    public ScriptFileSystem(
        ServerOptions options,
        ILogger<ScriptFileSystem> logger)
    {
        _logger = logger;
        _options = options;
        var pvfPath = ResolveConfiguredPath(options.ScriptPvfPath);
        if (!string.IsNullOrWhiteSpace(pvfPath) && File.Exists(pvfPath))
        {
            (_archive, _bodyOffset, _entries) = OpenPvf(pvfPath);
            SourceDescription = pvfPath;
            IsPvf = true;
            _logger.LogInformation(
                "Indexed {FileCount} script files directly from {PvfPath}.",
                _entries.Count,
                pvfPath);
            return;
        }

        var directory = ResolveConfiguredPath(options.SkillScriptPath);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            _directoryRoot = directory;
            _entries = new Dictionary<string, PvfEntry>();
            _directoryIndex = IndexDirectoryFiles(directory);
            SourceDescription = directory;
            _logger.LogWarning(
                "Script PVF was not found at {PvfPath}; using unpacked script directory {Directory}.",
                pvfPath,
                directory);
            return;
        }

        _entries = new Dictionary<string, PvfEntry>();
        SourceDescription = !string.IsNullOrWhiteSpace(pvfPath)
            ? pvfPath
            : "(not configured)";
        _logger.LogWarning(
            "Neither Script.pvf nor an unpacked script directory is available. Expected PVF at {PvfPath}.",
            pvfPath);
    }

    public string SourceDescription { get; }

    public bool IsPvf { get; }

    public int FileCount => IsPvf ? _entries.Count : 0;

    public bool FileExists(string virtualPath)
    {
        var normalized = NormalizeVirtualPath(virtualPath);
        if (IsPvf)
        {
            return _entries.ContainsKey(normalized);
        }

        if (_directoryIndex.ContainsKey(normalized))
        {
            return true;
        }

        return TryResolveDirectoryPath(normalized, out var path) && File.Exists(path);
    }

    public byte[] ReadAllBytes(string virtualPath)
    {
        var normalized = NormalizeVirtualPath(virtualPath);
        if (IsPvf)
        {
            if (!_entries.TryGetValue(normalized, out var entry))
            {
                throw new FileNotFoundException(
                    $"Script file '{normalized}' was not found in {SourceDescription}.",
                    normalized);
            }

            return _contentCache.GetOrAdd(normalized, _ => ReadPvfEntry(entry)).ToArray();
        }

        if (!TryResolveReadableDirectoryPath(normalized, out var path))
        {
            throw new FileNotFoundException(
                $"Script file '{normalized}' was not found below {SourceDescription}.",
                normalized);
        }

        return File.ReadAllBytes(path);
    }

    public string ReadAllText(string virtualPath, Encoding? encoding = null) =>
        (encoding ?? Encoding.Default).GetString(ReadAllBytes(virtualPath));

    public string ReadAllTextUncached(string virtualPath, Encoding? encoding = null)
    {
        var normalized = NormalizeVirtualPath(virtualPath);
        byte[] bytes;
        if (IsPvf)
        {
            if (!_entries.TryGetValue(normalized, out var entry))
            {
                throw new FileNotFoundException(
                    $"Script file '{normalized}' was not found in {SourceDescription}.",
                    normalized);
            }

            bytes = ReadPvfEntry(entry);
        }
        else
        {
            if (!TryResolveReadableDirectoryPath(normalized, out var path))
            {
                throw new FileNotFoundException(
                    $"Script file '{normalized}' was not found below {SourceDescription}.",
                    normalized);
            }

            bytes = File.ReadAllBytes(path);
        }

        return (encoding ?? Encoding.Default).GetString(bytes);
    }

    public string[] ReadAllLines(string virtualPath, Encoding? encoding = null) =>
        ReadAllText(virtualPath, encoding)
            .Split(["\r\n", "\n", "\r"], StringSplitOptions.None);

    public IEnumerable<string> ReadLines(string virtualPath, Encoding? encoding = null) =>
        ReadAllLines(virtualPath, encoding);

    public IEnumerable<string> EnumeratePaths(string virtualPrefix = "")
    {
        var normalizedPrefix = NormalizeVirtualPath(virtualPrefix);
        if (IsPvf)
        {
            return _entries.Keys
                .Where(path => path.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        if (_directoryRoot is null)
        {
            return [];
        }

        var searchRoot = _directoryRoot;
        if (normalizedPrefix.Length > 0)
        {
            var directoryPrefix = normalizedPrefix.TrimEnd('/');
            if (!TryResolveDirectoryPath(directoryPrefix, out searchRoot)
                || !Directory.Exists(searchRoot))
            {
                return [];
            }
        }

        return Directory.EnumerateFiles(searchRoot, "*", SearchOption.AllDirectories)
            .Select(path => NormalizeVirtualPath(Path.GetRelativePath(_directoryRoot, path)))
            .Where(path => path.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private byte[] ReadPvfEntry(PvfEntry entry)
    {
        var paddedLength = checked((entry.Length + 3) & ~3);
        var absoluteOffset = checked(_bodyOffset + (int)entry.Offset);
        if (_archive is null
            || absoluteOffset < _bodyOffset
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

    private static IReadOnlyDictionary<string, string> IndexDirectoryFiles(string directoryRoot) =>
        Directory.EnumerateFiles(directoryRoot, "*", SearchOption.AllDirectories)
            .Select(path => (
                    Virtual: NormalizeVirtualPath(Path.GetRelativePath(directoryRoot, path)),
                    Physical: path))
            .GroupBy(entry => entry.Virtual, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First().Physical,
                StringComparer.OrdinalIgnoreCase);

    private bool TryResolveReadableDirectoryPath(string normalized, out string path)
    {
        if (_directoryIndex.TryGetValue(normalized, out var indexedPath)
            && File.Exists(indexedPath))
        {
            path = indexedPath;
            return true;
        }

        if (TryResolveDirectoryPath(normalized, out path) && File.Exists(path))
        {
            return true;
        }

        return false;
    }

    private bool TryResolveDirectoryPath(string normalized, out string path)
    {
        path = string.Empty;
        if (_directoryRoot is null)
        {
            return false;
        }

        var candidate = Path.GetFullPath(
            normalized.Replace('/', Path.DirectorySeparatorChar),
            _directoryRoot);
        var relative = Path.GetRelativePath(_directoryRoot, candidate);
        if (relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            return false;
        }

        path = candidate;
        return true;
    }

    private static (byte[] Archive, int BodyOffset, IReadOnlyDictionary<string, PvfEntry> Entries)
        OpenPvf(string path)
    {
        var archive = File.ReadAllBytes(path);
        var headerLength = checked(PvfMagic.Length + 12);
        if (archive.Length < headerLength
            || !archive.AsSpan(0, PvfMagic.Length).SequenceEqual(PvfMagic))
        {
            throw new InvalidDataException($"{path} is not a DNF_SCRIPT_PACK archive.");
        }

        var header = archive.AsSpan(PvfMagic.Length, 12);
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
        var entries = new Dictionary<string, PvfEntry>(
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

            var fileNumber = BinaryPrimitives.ReadUInt32LittleEndian(tree.AsSpan(cursor, 4));
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

            var virtualPath = NormalizeVirtualPath(
                pathEncoding.GetString(tree, cursor, pathLength).TrimEnd('\0'));
            cursor += pathLength;
            entries[virtualPath] = new PvfEntry(
                fileNumber,
                offset,
                length,
                crc32,
                virtualPath);
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
            var plaintext = BitOperations.RotateRight(ciphertext ^ key, 6);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.Slice(offset, 4), plaintext);
        }
    }

    private static string NormalizeVirtualPath(string path) =>
        path.Replace('\\', '/').TrimStart('/').ToLowerInvariant();

    private string? ResolveConfiguredPath(string configuredPath) =>
        string.IsNullOrWhiteSpace(configuredPath)
            ? null
            : PathResolver.Resolve(configuredPath, _options.HomeDirectory);

    private sealed record PvfEntry(
        uint FileNumber,
        uint Offset,
        int Length,
        uint Crc32,
        string Path);
}
