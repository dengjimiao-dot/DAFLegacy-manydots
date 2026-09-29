using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;

namespace DFLegacy.Launcher;

internal static class Program
{
    internal const string DefaultClientPath = @"..\DNF.exe";
    private const int ServerStartupAttempts = 40;
    private static readonly TimeSpan PortProbeTimeout = TimeSpan.FromMilliseconds(250);

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        var noDialog = args.Contains("--no-dialog", StringComparer.OrdinalIgnoreCase)
            || args.Contains("--self-test", StringComparer.OrdinalIgnoreCase)
            || args.Contains("--install-ijl15", StringComparer.OrdinalIgnoreCase)
            || args.Contains("--check-d3d9", StringComparer.OrdinalIgnoreCase);

        try
        {
            var options = LoadOptions();
            ApplyCommandLineAccountSelection(options, args);
            ValidateOptions(options);

            if (args.Contains("--check-d3d9", StringComparer.OrdinalIgnoreCase))
            {
                await EnsureD3D9AvailableAsync(options);
                return 0;
            }

            var clientPath = ResolveClientPath(options.ClientPath);
            ValidateClient(clientPath);
            ValidateScriptPvf(clientPath);
            var bundledIjl15 =
                Ijl15ReplacementInstaller.ValidateBundledReplacement();
            var launcherData = BuildLauncherData(options);

            if (args.Contains("--install-ijl15", StringComparer.OrdinalIgnoreCase))
            {
                var install = Ijl15ReplacementInstaller.Install(clientPath);
                WriteLauncherLog(
                    install.Installed
                        ? $"Installed x86 ijl15 replacement at {install.TargetPath}; " +
                          $"sha256={install.Sha256}; " +
                          $"backup={install.BackupPath ?? "(none)"}."
                        : $"x86 ijl15 replacement is already installed at " +
                          $"{install.TargetPath}; sha256={install.Sha256}.");
                return 0;
            }

            if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
            {
                WriteLauncherLog(
                    $"Self-test passed for DFLegacy client {clientPath} " +
                    $"(file version check disabled); ijl15={bundledIjl15.Sha256}.");
                return 0;
            }

            await EnsureD3D9AvailableAsync(options);
            var ijl15Install = Ijl15ReplacementInstaller.Install(clientPath);
            if (ijl15Install.Installed)
            {
                WriteLauncherLog(
                    $"Installed x86 ijl15 replacement at {ijl15Install.TargetPath}; " +
                    $"sha256={ijl15Install.Sha256}; " +
                    $"backup={ijl15Install.BackupPath ?? "(none)"}.");
            }
            await EnsureServerReadyAsync(options);
            var client = ClientProcessLauncher.Start(clientPath, launcherData);
            TryRaisePriority(client);
            WriteLauncherLog(
                $"Started DFLegacy client process {client.Id}; entrance={options.EntranceHost}:{options.EntrancePort}.");
            return 0;
        }
        catch (Exception exception)
        {
            WriteLauncherLog($"Launcher failed: {exception}");
            if (!noDialog)
            {
                ApplicationConfiguration.Initialize();
                MessageBox.Show(
                    exception.Message,
                    "DFLegacy 启动失败",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }

            return 1;
        }
    }

    internal static string BuildLauncherData(LauncherOptions options)
    {
        ValidateOptions(options);
        var account = options.GetSelectedAccount();
        return string.Join('?',
        [
            "99",
            options.EntranceHost,
            options.EntrancePort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            account.Account,
            account.Password,
            "0", "0", "0", "0", "0", "0", "0", "0"
        ]);
    }

    private static LauncherOptions LoadOptions()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "launcher.json");
        if (!File.Exists(path))
        {
            return new LauncherOptions();
        }

        var options = JsonSerializer.Deserialize<LauncherOptions>(
            File.ReadAllText(path),
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip
            });
        return options ?? throw new InvalidDataException("launcher.json 不能为空。");
    }

    private static string ResolveClientPath(string configuredPath)
    {
        var path = string.IsNullOrWhiteSpace(configuredPath)
            ? DefaultClientPath
            : Environment.ExpandEnvironmentVariables(configuredPath);
        return Path.GetFullPath(
            Path.IsPathRooted(path)
                ? path
                : Path.Combine(AppContext.BaseDirectory, path));
    }

    private static void ValidateOptions(LauncherOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.EntranceHost))
        {
            throw new InvalidDataException("EntranceHost 不能为空。");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(options.EntrancePort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.EntrancePort, 65535);
        ValidateLauncherField(options.EntranceHost, nameof(options.EntranceHost));
        if (options.Accounts.Count > 0)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(options.AccountIndex);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
                options.AccountIndex,
                options.Accounts.Count);
            for (var index = 0; index < options.Accounts.Count; index++)
            {
                ValidateLauncherField(options.Accounts[index].Account, $"Accounts[{index}].Account");
                ValidateLauncherField(options.Accounts[index].Password, $"Accounts[{index}].Password");
            }
        }
        else
        {
            ValidateLauncherField(options.Account, nameof(options.Account));
            ValidateLauncherField(options.Password, nameof(options.Password));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(options.D3D9Adapter);
        ArgumentOutOfRangeException.ThrowIfNegative(options.D3D9TimeoutSeconds);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            options.D3D9RetryIntervalSeconds,
            0.1);
    }

    private static void ApplyCommandLineAccountSelection(
        LauncherOptions options,
        IReadOnlyList<string> args)
    {
        var indexArgument = Array.FindIndex(
            args.ToArray(),
            argument => string.Equals(argument, "--account-index", StringComparison.OrdinalIgnoreCase));
        if (indexArgument >= 0)
        {
            if (indexArgument + 1 >= args.Count
                || !int.TryParse(args[indexArgument + 1], out var accountIndex))
            {
                throw new InvalidDataException("--account-index 需要一个非负整数。");
            }

            options.AccountIndex = accountIndex;
        }

        var accountArgument = Array.FindIndex(
            args.ToArray(),
            argument => string.Equals(argument, "--account", StringComparison.OrdinalIgnoreCase));
        if (accountArgument < 0)
        {
            return;
        }

        if (accountArgument + 1 >= args.Count
            || string.IsNullOrWhiteSpace(args[accountArgument + 1]))
        {
            throw new InvalidDataException("--account 需要一个账号名。");
        }

        if (options.Accounts.Count == 0)
        {
            options.Account = args[accountArgument + 1];
            return;
        }

        var selectedIndex = options.Accounts.FindIndex(profile =>
            string.Equals(
                profile.Account,
                args[accountArgument + 1],
                StringComparison.OrdinalIgnoreCase));
        if (selectedIndex < 0)
        {
            throw new InvalidDataException(
                $"Accounts 中不存在账号 {args[accountArgument + 1]}。");
        }

        options.AccountIndex = selectedIndex;
    }

    private static void ValidateLauncherField(string value, string name)
    {
        if (string.IsNullOrEmpty(value)
            || value.Contains('?')
            || value.Contains('"')
            || value.Any(char.IsWhiteSpace))
        {
            throw new InvalidDataException(
                $"{name} 不能为空，也不能包含空白、问号或双引号。");
        }
    }

    private static void ValidateClient(string clientPath)
    {
        if (!File.Exists(clientPath))
        {
            throw new FileNotFoundException(
                $"找不到 DFLegacy 客户端：{clientPath}",
                clientPath);
        }

        // The PE file version is metadata only. The IJL15/native patches and
        // protocol implementation target the known 2008DF layout, but the
        // launcher must not reject a locally rebuilt or relabeled executable.
    }

    private static void ValidateScriptPvf(string clientPath)
    {
        var scriptPvfPath = Path.Combine(Path.GetDirectoryName(clientPath)!, "Script.pvf");
        if (!File.Exists(scriptPvfPath))
        {
            throw new FileNotFoundException(
                "目标客户端目录缺少 Script.pvf。",
                scriptPvfPath);
        }
    }

    private static async Task EnsureServerReadyAsync(LauncherOptions options)
    {
        if (await IsPortOpenAsync(options.EntranceHost, options.EntrancePort))
        {
            return;
        }

        if (!options.StartServer)
        {
            throw new InvalidOperationException(
                $"入口服务 {options.EntranceHost}:{options.EntrancePort} 未监听，且 StartServer=false。");
        }

        if (!IsLoopbackHost(options.EntranceHost))
        {
            throw new InvalidOperationException(
                "StartServer 只能自动启动本机入口服务；远程地址请先手动启动服务端。");
        }

        var serverPath = Path.Combine(AppContext.BaseDirectory, "DFLegacy.Server.exe");
        if (!File.Exists(serverPath))
        {
            throw new FileNotFoundException(
                "找不到模拟服务端，请保留发布目录内的全部文件。",
                serverPath);
        }

        Process.Start(new ProcessStartInfo(serverPath)
        {
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        });

        for (var attempt = 0; attempt < ServerStartupAttempts; attempt++)
        {
            await Task.Delay(250);
            if (await IsPortOpenAsync(options.EntranceHost, options.EntrancePort))
            {
                return;
            }
        }

        throw new InvalidOperationException(
            $"模拟服务端启动失败，{options.EntranceHost}:{options.EntrancePort} 没有开始监听。");
    }

    private static async Task EnsureD3D9AvailableAsync(LauncherOptions options)
    {
        var result = await D3D9DeviceProbe.WaitForDeviceAsync(
            options.D3D9Adapter,
            TimeSpan.FromSeconds(options.D3D9TimeoutSeconds),
            TimeSpan.FromSeconds(options.D3D9RetryIntervalSeconds));
        if (!result.Available)
        {
            WriteLauncherLog(
                $"D3D9 preflight failed after {result.Attempt} attempt(s): {result.Summary}");
            throw new InvalidOperationException(
                "当前桌面没有可用的 Direct3D 9 显示设备，已拒绝启动 DNF.exe。" +
                $"{Environment.NewLine}{result.Summary}" +
                $"{Environment.NewLine}请恢复显示输出或图形设备后再试。");
        }

        WriteLauncherLog(
            $"D3D9 preflight passed after {result.Attempt} attempt(s): {result.Summary}.");
    }

    private static async Task<bool> IsPortOpenAsync(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = new CancellationTokenSource(PortProbeTimeout);
            await client.ConnectAsync(host, port, timeout.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsLoopbackHost(string host) =>
        string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase);

    private static void TryRaisePriority(Process client)
    {
        try
        {
            client.PriorityClass = ProcessPriorityClass.AboveNormal;
            client.PriorityBoostEnabled = true;
        }
        catch (Exception exception)
        {
            WriteLauncherLog(
                $"Could not raise client process {client.Id} priority: {exception.Message}");
        }
    }

    private static void WriteLauncherLog(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "launcher.log"),
                $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never prevent the target process from starting.
        }
    }
}

internal sealed class LauncherOptions
{
    public string ClientPath { get; set; } = Program.DefaultClientPath;
    public string EntranceHost { get; set; } = "127.0.0.1";
    public int EntrancePort { get; set; } = 2311;
    public string Account { get; set; } = "test";
    public string Password { get; set; } = "test";
    public List<LauncherAccountProfile> Accounts { get; set; } = [];
    public int AccountIndex { get; set; }
    public bool StartServer { get; set; } = true;
    public int D3D9Adapter { get; set; }
    public double D3D9TimeoutSeconds { get; set; } = 30;
    public double D3D9RetryIntervalSeconds { get; set; } = 1;

    public LauncherAccountProfile GetSelectedAccount() =>
        Accounts.Count == 0
            ? new LauncherAccountProfile { Account = Account, Password = Password }
            : Accounts[AccountIndex];
}

internal sealed class LauncherAccountProfile
{
    public string Account { get; set; } = "test";
    public string Password { get; set; } = "test";
}
