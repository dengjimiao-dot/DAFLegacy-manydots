using System.Reflection;
using System.Runtime.Loader;

namespace DFLegacy.Server;

/// <summary>
/// 运行期加载 DFLegary.Mcp 模块（docs/design/09-mcp-packet-tap.md §3.1/§5.3）。
/// 编译期 DFLegacy.Server 不引用 DFLegacy.Mcp；本加载器是通用机制，不含任何
/// MCP 业务语义：Enabled 且输出目录存在模块 DLL 时反射调用模块入口
/// <c>McpModule.Attach(IServiceCollection, ServerOptions)</c>，任何失败只降级
/// MCP，游戏服务照常。
/// </summary>
public static class McpModuleLoader
{
    private const string ModuleAssemblyName = "DFLegacy.Mcp";
    private const string ModuleEntryTypeName = "DFLegacy.Mcp.McpModule";
    private const string ModuleEntryMethodName = "Attach";

    private static int _resolvingHooked;

    public static void TryAttach(IServiceCollection services, ServerOptions options, ILogger logger)
    {
        if (!options.Mcp.Enabled)
        {
            logger.LogDebug(
                "DFLegacy.Mcp is disabled by configuration; the module loader short-circuits.");
            return;
        }

        try
        {
            HookDependencyResolving(logger);
            var module = Assembly.Load(ModuleAssemblyName);
            var entryType = module.GetType(ModuleEntryTypeName, throwOnError: true)!;
            var attach = entryType.GetMethod(
                ModuleEntryMethodName,
                BindingFlags.Public | BindingFlags.Static,
                [typeof(IServiceCollection), typeof(ServerOptions)]);
            if (attach is null)
            {
                throw new MissingMethodException(
                    ModuleEntryTypeName,
                    $"{ModuleEntryMethodName}(IServiceCollection, ServerOptions)");
            }

            attach.Invoke(null, [services, options]);
            logger.LogInformation(
                "DFLegacy.Mcp module attached; tools become available once the hosted endpoint starts.");
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "DFLegacy.Mcp module could not be loaded; MCP stays disabled and the game services are unaffected.");
        }
    }

    /// <summary>
    /// 模块按"发布目录摆 DLL"部署且不携带 deps.json（§5.3 构建产物精简），
    /// 其 SDK 依赖靠默认加载上下文的 Resolving 事件按文件名探测兜底。
    /// 事件只在常规探测失败时触发，对未装模块的进程无影响。
    /// </summary>
    private static void HookDependencyResolving(ILogger logger)
    {
        if (Interlocked.Exchange(ref _resolvingHooked, 1) == 1)
        {
            return;
        }

        AssemblyLoadContext.Default.Resolving += (context, assemblyName) =>
        {
            var path = Path.Combine(AppContext.BaseDirectory, $"{assemblyName.Name}.dll");
            if (!File.Exists(path))
            {
                return null;
            }

            logger.LogDebug("Resolving {AssemblyName} from {Path}.", assemblyName.Name, path);
            return context.LoadFromAssemblyPath(path);
        };
    }
}
