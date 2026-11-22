using Autodesk.Revit.UI;
using Newtonsoft.Json;
using RevitMCPSDK.API.Interfaces;
using RevitModeler.Plugin.Configuration;
using RevitModeler.Plugin.Utils;
using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace RevitModeler.Plugin.Core;

/// <summary>
/// 从独立命令目录 &lt;pluginDir&gt;/Commands/RevitModeler/command.json 反射加载命令集。
/// 与 revit-mcp 的 CommandManager 对齐，但注册到本插件自己的 CommandRegistry（不共享 Host 的那张表）。
/// </summary>
public class FanmoCommandLoader
{
    private readonly ICommandRegistry _registry;
    private readonly UIApplication _uiApp;

    public FanmoCommandLoader(ICommandRegistry registry, UIApplication uiApp)
    {
        _registry = registry;
        _uiApp = uiApp;
    }

    public void LoadCommands()
    {
        var dir = PluginConfig.CommandsDirectory;
        var manifestPath = Path.Combine(dir, "command.json");
        if (!File.Exists(manifestPath))
        {
            AppLogger.Error($"未找到命令清单: {manifestPath}");
            return;
        }

        // 私有依赖（Core / SDK / Newtonsoft）从命令目录解析，避免与 revit-mcp Host 的同名程序集串味
        AppDomain.CurrentDomain.AssemblyResolve += ResolveFromCommandsDirectory;

        var manifest = JsonConvert.DeserializeObject<CommandManifest>(File.ReadAllText(manifestPath));
        if (manifest?.Commands == null) return;

        foreach (var descriptor in manifest.Commands.Where(c => c.Enabled))
        {
            try
            {
                var assemblyPath = Path.Combine(dir, descriptor.AssemblyPath);
                if (!File.Exists(assemblyPath))
                {
                    AppLogger.Error($"命令程序集不存在: {assemblyPath}");
                    continue;
                }

                var assembly = Assembly.LoadFrom(assemblyPath);
                foreach (var type in assembly.GetTypes().Where(t =>
                    typeof(IRevitCommand).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract))
                {
                    var command = Instantiate(type);
                    if (command != null && command.CommandName == descriptor.CommandName)
                    {
                        _registry.RegisterCommand(command);
                        AppLogger.Info($"已注册命令: {command.CommandName} ({Path.GetFileName(assemblyPath)})");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error($"加载命令 {descriptor.CommandName} 失败: {ex.Message}");
            }
        }
    }

    private IRevitCommand Instantiate(Type type)
    {
        if (typeof(IRevitCommandInitializable).IsAssignableFrom(type))
        {
            var c = (IRevitCommand)Activator.CreateInstance(type);
            ((IRevitCommandInitializable)c).Initialize(_uiApp);
            return c;
        }
        var ctor = type.GetConstructor(new[] { typeof(UIApplication) });
        return ctor != null
            ? (IRevitCommand)ctor.Invoke(new object[] { _uiApp })
            : (IRevitCommand)Activator.CreateInstance(type);
    }

    private Assembly ResolveFromCommandsDirectory(object sender, ResolveEventArgs args)
    {
        var name = new AssemblyName(args.Name).Name;
        var candidate = Path.Combine(PluginConfig.CommandsDirectory, name + ".dll");
        return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
    }
}
