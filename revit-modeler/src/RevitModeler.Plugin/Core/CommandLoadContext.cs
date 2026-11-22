using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace RevitModeler.Plugin.Core;

/// <summary>
/// 命令程序集的可回收加载上下文（collectible AssemblyLoadContext）。
///
/// 为什么需要它：<c>Assembly.LoadFrom</c> 把程序集装进默认上下文，.NET 中默认上下文永不卸载，
/// 因此每次改 CommandSet 都必须重启 Revit。改为 collectible ALC 后，可在进程内卸载旧程序集并加载新版本。
///
/// 两个关键设计：
/// 1) <b>影子拷贝</b>：先把命令目录复制到 %TEMP%\BIMOne.Commands\&lt;时间戳&gt;，从副本加载。
///    否则会被加载中的 dll 锁住原文件，热更新时无法覆盖。
/// 2) <b>共享程序集隔离</b>：RevitAPI / RevitMCPSDK / Newtonsoft 等必须回落到默认上下文解析，
///    否则子上下文会加载出第二份类型，导致 <c>IRevitCommand</c> 强转失败（同名不同源）。
/// </summary>
internal sealed class CommandLoadContext : AssemblyLoadContext
{
    /// <summary>必须与 Revit 进程默认上下文共享的程序集（按精确名匹配）。</summary>
    private static readonly HashSet<string> SharedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "RevitAPI", "RevitAPIUI", "AdWindows", "Utility", "RevitMCPSDK",
        "Newtonsoft.Json", "WinRT.Runtime", "RevitModeler.Plugin"
    };

    /// <summary>框架与平台程序集前缀，同样必须共享。</summary>
    private static readonly string[] SharedPrefixes =
    {
        "System", "Microsoft", "netstandard", "mscorlib", "WindowsBase",
        "PresentationCore", "PresentationFramework"
    };

    /// <summary>需要拷进影子目录的文件类型。</summary>
    private static readonly HashSet<string> CopiedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".pdb", ".json", ".config"
    };

    public string ShadowDirectory { get; }

    public CommandLoadContext(string sourceDir, string shadowDir)
        : base("BIMOne.Commands." + Guid.NewGuid().ToString("N")[..8], isCollectible: true)
    {
        ShadowDirectory = shadowDir;
        Directory.CreateDirectory(shadowDir);

        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            if (!CopiedExtensions.Contains(Path.GetExtension(file))) continue;

            var relative = Path.GetRelativePath(sourceDir, file);
            var destination = Path.Combine(shadowDir, relative);
            var parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

            File.Copy(file, destination, overwrite: true);
        }
    }

    protected override Assembly Load(AssemblyName assemblyName)
    {
        var name = assemblyName.Name;
        if (string.IsNullOrEmpty(name)) return null;

        // 默认上下文已加载的同名程序集：直接复用，保证类型同一性
        var alreadyLoaded = Default.Assemblies.FirstOrDefault(a =>
            string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
        if (alreadyLoaded is not null) return alreadyLoaded;

        if (IsShared(name))
        {
            try { return Default.LoadFromAssemblyName(assemblyName); }
            catch { return null; }
        }

        // 其余（CommandSet / RevitModeler.Core / 私有依赖）从影子目录隔离加载
        var path = Path.Combine(ShadowDirectory, name + ".dll");
        return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
    }

    private static bool IsShared(string name)
    {
        if (SharedNames.Contains(name)) return true;
        return SharedPrefixes.Any(prefix =>
            name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
}
