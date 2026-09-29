using DFLegacy.Server;
using Microsoft.Extensions.DependencyInjection;

namespace DFLegacy.Mcp;

/// <summary>
/// DAF-MCP 模块入口（docs/design/09-mcp-packet-tap.md §5.3）。只被
/// DFLegacy.Server 的 McpModuleLoader 反射调用：向主进程容器注册抓包枢纽
/// （PacketTapHub）与自持 Kestrel 的宿主（McpHost）。工具组扩展时在此追加
/// 注册即可，服务端代码零改动（§15.1）。
/// </summary>
public static class McpModule
{
    public static void Attach(IServiceCollection services, ServerOptions options)
    {
        services.AddSingleton<PacketTapHub>();
        services.AddHostedService<McpHost>();
    }
}
