using System.Diagnostics;

namespace DFLegacy.Launcher;

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
                $"无法启动 DFLegacy 客户端：{clientPath}");
    }
}
