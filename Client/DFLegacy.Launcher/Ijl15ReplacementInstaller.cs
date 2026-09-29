using System.Security.Cryptography;

namespace DFLegacy.Launcher;

internal static class Ijl15ReplacementInstaller
{
    private const string ReplacementFileName = "ijl15.dll";
    private const string BackupFileName = "ijl15.original.dll";
    private const string ConfigFileName = "Config.ini";

    internal static BundledIjl15 ValidateBundledReplacement()
    {
        var path = Path.Combine(AppContext.BaseDirectory, ReplacementFileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "启动器目录缺少 32 位 ijl15.dll 替换模块。",
                path);
        }

        ValidateX86Dll(path);
        return new BundledIjl15(path, ComputeSha256(path));
    }

    internal static Ijl15InstallResult Install(string clientPath)
    {
        var bundled = ValidateBundledReplacement();
        var clientDirectory = Path.GetDirectoryName(clientPath)
            ?? throw new InvalidDataException("DNF.exe 路径缺少父目录。");
        var targetPath = Path.Combine(clientDirectory, ReplacementFileName);
        InstallBundledConfig(clientDirectory);

        if (Path.GetFullPath(bundled.Path).Equals(
                Path.GetFullPath(targetPath),
                StringComparison.OrdinalIgnoreCase))
        {
            return new Ijl15InstallResult(
                false,
                targetPath,
                null,
                bundled.Sha256);
        }

        if (File.Exists(targetPath)
            && ComputeSha256(targetPath).Equals(
                bundled.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return new Ijl15InstallResult(
                false,
                targetPath,
                null,
                bundled.Sha256);
        }

        string? backupPath = null;
        if (File.Exists(targetPath))
        {
            backupPath = Path.Combine(clientDirectory, BackupFileName);
            if (!File.Exists(backupPath))
            {
                File.Copy(targetPath, backupPath, overwrite: false);
            }
        }

        var temporaryPath = Path.Combine(
            clientDirectory,
            $".{ReplacementFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(bundled.Path, temporaryPath, overwrite: false);
            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        return new Ijl15InstallResult(
            true,
            targetPath,
            backupPath,
            bundled.Sha256);
    }

    private static void ValidateX86Dll(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 256
            || reader.ReadUInt16() != 0x5A4D)
        {
            throw new InvalidDataException(
                $"ijl15 替换模块不是有效的 PE 文件：{path}");
        }

        stream.Position = 0x3C;
        var peOffset = reader.ReadInt32();
        if (peOffset < 0x40 || peOffset > stream.Length - 26)
        {
            throw new InvalidDataException(
                $"ijl15 替换模块的 PE 头无效：{path}");
        }

        stream.Position = peOffset;
        var signature = reader.ReadUInt32();
        var machine = reader.ReadUInt16();
        _ = reader.ReadUInt16();
        _ = reader.ReadUInt32();
        _ = reader.ReadUInt32();
        _ = reader.ReadUInt32();
        _ = reader.ReadUInt16();
        var characteristics = reader.ReadUInt16();
        var optionalHeaderMagic = reader.ReadUInt16();

        const ushort ImageFileMachineI386 = 0x014C;
        const ushort ImageFileDll = 0x2000;
        const ushort Pe32Magic = 0x010B;
        if (signature != 0x00004550
            || machine != ImageFileMachineI386
            || (characteristics & ImageFileDll) == 0
            || optionalHeaderMagic != Pe32Magic)
        {
            throw new InvalidDataException(
                $"ijl15 替换模块必须是 32 位 x86 DLL：{path}");
        }
    }

    private static void InstallBundledConfig(string clientDirectory)
    {
        var bundledPath = Path.Combine(AppContext.BaseDirectory, ConfigFileName);
        if (!File.Exists(bundledPath))
        {
            return;
        }

        var targetPath = Path.Combine(clientDirectory, ConfigFileName);
        // A user-edited client Config.ini is authoritative. The bundled file
        // only supplies defaults on the first installation.
        if (!File.Exists(targetPath))
        {
            File.Copy(bundledPath, targetPath, overwrite: false);
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

internal sealed record BundledIjl15(string Path, string Sha256);

internal sealed record Ijl15InstallResult(
    bool Installed,
    string TargetPath,
    string? BackupPath,
    string Sha256);
