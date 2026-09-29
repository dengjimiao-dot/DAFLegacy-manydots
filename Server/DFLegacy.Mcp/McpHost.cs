using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using DFLegacy.Server;

namespace DFLegacy.Mcp;

/// <summary>
/// DAF-MCP 宿主（docs/design/09-mcp-packet-tap.md §2.2/§5.3）：自持一个只有
/// /mcp 路由的独立最小 WebApplication（独立 Kestrel），以 IHostedService 挂在
/// 主进程。端口被占等故障只降级 MCP，游戏服务不受影响；日志接主进程的
/// ILoggerFactory（§17），不产生第二份日志。
/// </summary>
public sealed class McpHost : IHostedService
{
    private readonly RuntimeState _runtime;
    private readonly PacketTapHub _tapHub;
    private readonly ServerOptions _options;
    private readonly IServiceProvider _services;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<McpHost> _logger;
    private WebApplication? _app;

    public McpHost(
        RuntimeState runtime,
        PacketTapHub tapHub,
        ServerOptions options,
        IServiceProvider services,
        ILoggerFactory loggerFactory,
        ILogger<McpHost> logger)
    {
        _runtime = runtime;
        _tapHub = tapHub;
        _options = options;
        _services = services;
        _loggerFactory = loggerFactory;
        _logger = logger;

        // 构造即接入漏斗：宿主在全部 hosted service 构造完成、任何 StartAsync
        // 之前被创建，保证网络监听器的第一个包起 TapSink 就已在位。
        _runtime.TapSink = _tapHub;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var mcp = _options.Mcp;
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                // 锚定程序目录，避免吃进程工作目录下的 appsettings.json。
                ContentRootPath = AppContext.BaseDirectory,
            });
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton<ILoggerFactory>(_loggerFactory);
            builder.WebHost.ConfigureKestrel(kestrel =>
                kestrel.Listen(IPAddress.Parse(mcp.Host), mcp.Port));

            // 与主进程共享的单例直接登记进内部容器；新工具组需要的 Server
            // 服务同样从这里解析（§15.1）。
            builder.Services.AddSingleton(_tapHub);
            builder.Services.AddSingleton(_runtime);
            builder.Services.AddMcpServer(mcpServer =>
                {
                    mcpServer.ServerInfo = new Implementation
                    {
                        Name = "DFLegacy",
                        Title = "DAF-MCP",
                        Version = "0.1.0",
                    };
                })
                .WithHttpTransport()
                // 扫描本模块程序集：新工具组加文件即自动入列（§2.2）。
                .WithToolsFromAssembly(typeof(McpModule).Assembly)
                .WithResourcesFromAssembly(typeof(McpModule).Assembly);

            var app = builder.Build();
            app.MapMcp("/mcp");
            _app = app;
            await app.StartAsync(cancellationToken);
            _logger.LogInformation(
                "DAF-MCP ready at http://{Host}:{Port}/mcp (tools: packet tap G1).",
                mcp.Host,
                mcp.Port);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "DAF-MCP failed to start at http://{Host}:{Port}/mcp; the module stays disabled and the game services are unaffected.",
                mcp.Host,
                mcp.Port);
            _runtime.TapSink = null;
            _app = null;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _runtime.TapSink = null;
        if (_app is null)
        {
            return;
        }

        try
        {
            await _app.StopAsync(cancellationToken);
        }
        catch
        {
            // 停机时尽力而为。
        }

        await _app.DisposeAsync();
        _app = null;
    }
}
