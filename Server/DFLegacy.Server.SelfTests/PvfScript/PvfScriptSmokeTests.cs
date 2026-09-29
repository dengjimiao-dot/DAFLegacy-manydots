using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using DFLegacy.Server;
using Microsoft.Extensions.Logging.Abstractions;

internal static class PvfScriptSmokeTests
{
    public static void Run(Action<bool, string> check)
    {
        RunArchiveRoundTrip(check);
        RunEncodingContracts(check);
        RunDirectoryFallbackCaseInsensitivity(check);
        RunMalformedArchives(check);
    }

    // —— 合成 PVF：索引、CP949 路径表、dword 对齐解密、非 4 倍数条目长度 ——

    private static void RunArchiveRoundTrip(Action<bool, string> check)
    {
        var entries = new (string Path, byte[] Content)[]
        {
            ("skill/swordman/slash.sin", "PVF-ROUNDTRIP"u8.ToArray()),
            ("Equipment/대검.lst", Encoding.UTF8.GetBytes("스킬=액티브")),
            ("etc/padded.tbl", new byte[] { 1, 2, 3, 4, 5 }), // 5 字节：非 dword 倍数
        };

        using var archive = new TempFile(CreateArchive(entries));
        var scripts = new ScriptFileSystem(
            new ServerOptions { ScriptPvfPath = archive.Path },
            NullLogger<ScriptFileSystem>.Instance);

        check(scripts.IsPvf && scripts.FileCount == entries.Length,
            "synthetic PVF indexes all entries directly from the archive");
        check(scripts.FileExists("SKILL/SWORDMAN/SLASH.SIN")
                && scripts.FileExists("skill\\swordman\\slash.sin"),
            "PVF lookups are case- and separator-insensitive");
        check(Encoding.UTF8.GetString(scripts.ReadAllBytes("skill/swordman/slash.sin"))
                == "PVF-ROUNDTRIP",
            "PVF entry bodies decrypt back to their plaintext");
        check(scripts.ReadAllText("Equipment/대검.lst") == "스킬=액티브",
            "CP949-encoded tree paths resolve and UTF-8 bodies round-trip");
        check(scripts.ReadAllBytes("etc/padded.tbl").SequenceEqual(new byte[] { 1, 2, 3, 4, 5 }),
            "entry lengths that are not dword multiples are trimmed after decryption");
        check(!scripts.FileExists("etc/missing.etc"),
            "missing PVF entries report existence as false");
    }

    // —— 编码契约：CP936 / CP949 / CP51949 / UTF-8 往返 ——

    private static void RunEncodingContracts(Action<bool, string> check)
    {
        PvfEncodings.EnsureRegistered();

        check(PvfEncodings.Cp949().GetString(PvfEncodings.Cp949().GetBytes("스킬마스터")) == "스킬마스터",
            "CP949 round-trips Korean path-table text");
        check(PvfEncodings.Cp936Strict().GetString(PvfEncodings.Cp936Strict().GetBytes("暗黑大声级")) == "暗黑大声级",
            "CP936 strict round-trips Chinese client text");
        check(PvfEncodings.Cp936Lossy().GetBytes("暗黑?") is { Length: > 0 },
            "CP936 lossy mode replaces instead of throwing on unmappable input");
        check(Encoding.GetEncoding(51949).GetString(Encoding.GetEncoding(51949).GetBytes("던파")) == "던파",
            "CP51949 (EUC-KR) resolves and round-trips after module registration");
        check(Encoding.UTF8.GetString(Encoding.UTF8.GetBytes("던파 暗黑")) == "던파 暗黑",
            "UTF-8 (ReadAllText default) round-trips mixed scripts");
    }

    // —— 目录回退模式：启动时建 OrdinalIgnoreCase 索引（R8） ——

    private static void RunDirectoryFallbackCaseInsensitivity(Action<bool, string> check)
    {
        using var directory = new TempDirectory();
        var nested = System.IO.Path.Combine(directory.Path, "Equipment", "Sub");
        Directory.CreateDirectory(nested);
        File.WriteAllText(System.IO.Path.Combine(nested, "List.TBL"), "case-insensitive");

        var scripts = new ScriptFileSystem(
            new ServerOptions { SkillScriptPath = directory.Path },
            NullLogger<ScriptFileSystem>.Instance);

        check(!scripts.IsPvf && scripts.FileExists("equipment/sub/list.tbl")
                && scripts.FileExists("EQUIPMENT\\SUB\\LIST.TBL"),
            "directory fallback resolves lowercase and uppercase queries for capitalised paths");
        check(scripts.ReadAllText("equipment/sub/list.tbl") == "case-insensitive",
            "directory fallback reads content through the case-insensitive index");
        check(!scripts.FileExists("equipment/missing.tbl"),
            "directory fallback reports missing files as absent");
    }

    // —— 边界用例：树头非法、pathLength 非法、条目越界 ——

    private static void RunMalformedArchives(Action<bool, string> check)
    {
        var entries = new (string Path, byte[] Content)[] { ("a/one.txt", new byte[] { 65 }) };

        check(ThrowsInvalidData(() => Open(CreateArchive(entries, treeLengthOverride: 0))),
            "PVF with zero tree length is rejected");
        check(ThrowsInvalidData(() => Open(CreateArchive(entries, treeLengthOverride: 6))),
            "PVF with a tree length that is not a dword multiple is rejected");
        check(ThrowsInvalidData(() => Open(CreateArchive(entries, firstPathLengthOverride: 0))),
            "PVF tree entry with non-positive path length is rejected");
        check(ThrowsInvalidData(() => Open(CreateArchive(entries, firstPathLengthOverride: 4096))),
            "PVF tree entry whose path length exceeds the tree is rejected");

        var oversized = Open(CreateArchive(entries, firstLengthOverride: 1 << 20));
        check(ThrowsInvalidData(() => oversized.ReadAllBytes("a/one.txt")),
            "PVF entry pointing beyond the archive is rejected on read");

        static ScriptFileSystem Open(byte[] archive)
        {
            var file = new TempFile(archive);
            try
            {
                return new ScriptFileSystem(
                    new ServerOptions { ScriptPvfPath = file.Path },
                    NullLogger<ScriptFileSystem>.Instance);
            }
            finally
            {
                file.Dispose();
            }
        }

        static bool ThrowsInvalidData(Action action)
        {
            try
            {
                action();
                return false;
            }
            catch (InvalidDataException)
            {
                return true;
            }
        }
    }

    // —— DNF_SCRIPT_PACK 合成夹具 ——

    internal static byte[] CreateArchive(
        IReadOnlyList<(string Path, byte[] Content)> entries,
        int? treeLengthOverride = null,
        int? firstPathLengthOverride = null,
        int? firstLengthOverride = null)
    {
        const uint treeKey = 0x1EC7BEEFu;
        const uint bodyKey = 0x5A5A5A5Au;

        var body = new List<byte>();
        var tree = new List<byte>();
        for (var index = 0; index < entries.Count; index++)
        {
            var (path, content) = entries[index];
            var offset = body.Count;
            foreach (var cipher in EncryptDwords(Pad(content), bodyKey))
            {
                body.AddRange(cipher);
            }

            var pathBytes = PvfEncodings.Cp949().GetBytes(path);
            var length = index == 0 && firstLengthOverride.HasValue
                ? firstLengthOverride.Value
                : content.Length;
            var pathLength = index == 0 && firstPathLengthOverride.HasValue
                ? firstPathLengthOverride.Value
                : pathBytes.Length;
            tree.AddRange(ToBytes(1u));
            tree.AddRange(ToBytes((uint)offset));
            tree.AddRange(ToBytes(unchecked((uint)length)));
            tree.AddRange(ToBytes(bodyKey));
            tree.AddRange(ToBytes(unchecked((uint)pathLength)));
            tree.AddRange(pathBytes);
        }

        var treePlain = Pad(tree.ToArray());
        var treeLength = treeLengthOverride ?? treePlain.Length;
        var magicLength = "DNF_SCRIPT_PACK"u8.ToArray().Length;
        var archive = new List<byte>(magicLength + 12 + treePlain.Length + body.Count);
        archive.AddRange("DNF_SCRIPT_PACK"u8.ToArray());
        archive.AddRange(ToBytes(unchecked((uint)treeLength)));
        archive.AddRange(ToBytes(treeKey));
        archive.AddRange(ToBytes((uint)entries.Count));
        foreach (var cipher in EncryptDwords(treePlain, treeKey))
        {
            archive.AddRange(cipher);
        }

        archive.AddRange(body);
        return archive.ToArray();
    }

    private static byte[] Pad(byte[] bytes)
    {
        var padded = new byte[(bytes.Length + 3) & ~3];
        bytes.CopyTo(padded, 0);
        return padded;
    }

    // ScriptFileSystem.DecryptInPlace（plain = ROR(cipher ^ key, 6)）的逆：
    // cipher = ROL(plain, 6) ^ key。
    private static IEnumerable<byte[]> EncryptDwords(byte[] padded, uint key)
    {
        for (var offset = 0; offset < padded.Length; offset += 4)
        {
            var plain = BinaryPrimitives.ReadUInt32LittleEndian(padded.AsSpan(offset, 4));
            var cipher = BitOperations.RotateLeft(plain, 6) ^ key;
            var bytes = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, cipher);
            yield return bytes;
        }
    }

    private static byte[] ToBytes(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    private sealed class TempFile : IDisposable
    {
        public TempFile(byte[] bytes)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"dflegacy-pvf-{Guid.NewGuid():N}.pvf");
            File.WriteAllBytes(Path, bytes);
        }

        public string Path { get; }

        public void Dispose() => File.Delete(Path);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"dflegacy-scripts-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
