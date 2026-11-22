using System;
using System.IO;
using System.Reflection;

namespace RevitModeler.Plugin.Utils;

/// <summary>插件级配置：集中声明隔离参数（端口、命令目录），避免散落魔法值。</summary>
public static class PluginConfig
{
    /// <summary>独立 TCP 端口。revit-mcp Host 固定 8080，这里用 8091 避免冲突。</summary>
    public static int Port => ReadPortFromEnvOrDefault(8091);

    /// <summary>插件安装目录（本 DLL 所在目录）。</summary>
    public static string PluginDirectory => Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

    /// <summary>命令目录：&lt;pluginDir&gt;/Commands/RevitModeler/，与 revit-mcp 的 Commands/ 物理隔离。</summary>
    public static string CommandsDirectory
    {
        get
        {
            var dir = Path.Combine(PluginDirectory, "Commands", "RevitModeler");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>日志目录：&lt;pluginDir&gt;/Logs/。</summary>
    public static string LogsDirectory
    {
        get
        {
            var dir = Path.Combine(PluginDirectory, "Logs");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static int ReadPortFromEnvOrDefault(int fallback)
    {
        var raw = Environment.GetEnvironmentVariable("BIMONE_REVIT_PORT");
        return int.TryParse(raw, out var port) && port > 0 && port < 65536 ? port : fallback;
    }
}
