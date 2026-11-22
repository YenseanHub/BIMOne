using System;
using System.IO;

namespace RevitModeler.Plugin.Utils;

/// <summary>轻量日志，写 &lt;pluginDir&gt;/Logs/bimone_yyyyMMdd.log，与 revit-mcp 的 mcp_*.log 分离。</summary>
public static class AppLogger
{
    private static string LogFile => Path.Combine(PluginConfig.LogsDirectory, $"bimone_{DateTime.Now:yyyyMMdd}.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Warning(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        try
        {
            File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不影响插件主流程
        }
        System.Diagnostics.Debug.WriteLine($"[BIMOne:{level}] {message}");
    }
}
