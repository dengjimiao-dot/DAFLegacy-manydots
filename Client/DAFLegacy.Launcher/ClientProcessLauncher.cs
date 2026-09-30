using System.Diagnostics;

namespace DAFLegacy.Launcher;

internal static class ClientProcessLauncher
{
    internal static Process Start(string clientPath, string launcherData)
    {
        var startInfo = new ProcessStartInfo(clientPath)
        {
            WorkingDirectory = Path.GetDirectoryName(clientPath)!,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(launcherData);

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                $"无法启动 DAFLegacy 客户端：{clientPath}");
    }
}
