using System.Text;
using DAFLegacy.Server;
using Microsoft.Extensions.Logging.Abstractions;

// PVF 脚本提取诊断工具：按 tools/ 规范放在 tools/diagnostics/ 下。
// 用法：
//   ExtractPvfScript <pvf路径> list <虚拟前缀>
//   ExtractPvfScript <pvf路径> dungeonlist
//   ExtractPvfScript <pvf路径> get <虚拟路径> [输出文件] [cp949|gbk]
// pvf 路径由命令行传入，不在源码中写死本机路径。

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: ExtractPvfScript <pvf-path> <list|dungeonlist|get> [args]");
    return 2;
}

var pvfPath = args[0];
var mode = args[1].ToLowerInvariant();
Console.OutputEncoding = Encoding.UTF8;
PvfEncodings.EnsureRegistered();

// CP949 用于路径与韩文脚本；中文可见文本（名称/说明）为 GBK(CP936)。
static Encoding ResolveEncoding(string? name) => name?.ToLowerInvariant() switch
{
    "gbk" or "cp936" => Encoding.GetEncoding(936),
    _ => PvfEncodings.Cp949(),
};

var options = new ServerOptions { ScriptPvfPath = pvfPath };
var scripts = new ScriptFileSystem(options, NullLogger<ScriptFileSystem>.Instance);
if (!scripts.IsPvf && !Directory.Exists(pvfPath))
{
    Console.Error.WriteLine($"PVF was not found at {pvfPath}.");
    return 1;
}

switch (mode)
{
    case "list":
    {
        var prefix = args.Length > 2 ? args[2] : "";
        foreach (var path in scripts.EnumeratePaths(prefix))
        {
            Console.WriteLine(path);
        }
        return 0;
    }
    case "dungeonlist":
    {
        Console.Write(scripts.ReadAllText("dungeon/dungeon.lst", PvfEncodings.Cp949()));
        return 0;
    }
    case "get":
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: ExtractPvfScript <pvf-path> get <virtual-path> [output-file] [cp949|gbk]");
            return 2;
        }
        var virtualPath = args[2];
        var encoding = ResolveEncoding(args.Length > 4 ? args[4] : null);
        var text = scripts.ReadAllText(virtualPath, encoding);
        if (args.Length > 3)
        {
            File.WriteAllText(args[3], text, new UTF8Encoding(false));
            Console.WriteLine($"Wrote {text.Length} chars to {args[3]}");
        }
        else
        {
            Console.Write(text);
        }
        return 0;
    }
    default:
    {
        Console.Error.WriteLine($"Unknown mode {mode}.");
        return 2;
    }
}
