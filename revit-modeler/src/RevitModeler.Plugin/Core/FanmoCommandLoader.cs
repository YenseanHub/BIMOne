using Autodesk.Revit.UI;
using Newtonsoft.Json;
using RevitMCPSDK.API.Interfaces;
using RevitModeler.Plugin.Configuration;
using RevitModeler.Plugin.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

namespace RevitModeler.Plugin.Core;

/// <summary>一次命令集加载的产物。与具体加载上下文解耦，便于「先加载新的、成功后再替换」。</summary>
public sealed class CommandLoadOutcome
{
    /// <summary>commandName → 命令实例。</summary>
    public Dictionary<string, IRevitCommand> Commands { get; } = new(StringComparer.Ordinal);

    public List<string> Warnings { get; } = new();

    /// <summary>本次使用的影子目录（排查「加载的是哪一版」时看这里）。</summary>
    public string ShadowDirectory { get; set; } = "";

    /// <summary>程序集文件名 → 版本指纹（版本号 / 大小 / 写入时间）。</summary>
    public Dictionary<string, string> Assemblies { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 从 &lt;pluginDir&gt;/Commands/RevitModeler/command.json 反射加载命令集，支持进程内热重载。
///
/// 加载语义是<b>原子</b>的：先把新版本完整加载并校验通过，才替换当前版本；
/// 任一命令失败则整体回滚到上一版本，绝不停留在半加载状态（否则又要重启 Revit 才能救回来）。
/// </summary>
public class FanmoCommandLoader
{
    private readonly UIApplication _uiApp;
    private readonly List<WeakReference> _retired = new();
    private CommandLoadContext _active;
    private Dictionary<string, IRevitCommand> _activeCommands = new(StringComparer.Ordinal);

    public FanmoCommandLoader(UIApplication uiApp) => _uiApp = uiApp;

    public bool HasLoaded => _active is not null;

    public string ActiveShadowDirectory => _active?.ShadowDirectory ?? "";

    /// <summary>
    /// 加载（或重载）命令集。失败抛异常且当前版本保持不变。
    /// 成功后旧上下文进入待回收队列，需在旧命令实例被释放后调用 <see cref="CollectRetired"/>。
    /// </summary>
    public CommandLoadOutcome Load()
    {
        var dir = PluginConfig.CommandsDirectory;
        var manifestPath = Path.Combine(dir, "command.json");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException($"未找到命令清单: {manifestPath}");

        var manifest = JsonConvert.DeserializeObject<CommandManifest>(File.ReadAllText(manifestPath));
        if (manifest?.Commands is null || manifest.Commands.Count == 0)
            throw new InvalidOperationException("command.json 中没有命令声明");

        var enabled = manifest.Commands.Where(c => c.Enabled).ToList();
        if (enabled.Count == 0)
            throw new InvalidOperationException("command.json 中没有启用的命令");

        var shadow = Path.Combine(Path.GetTempPath(), "BIMOne.Commands",
            DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        var context = new CommandLoadContext(dir, shadow);
        var outcome = new CommandLoadOutcome { ShadowDirectory = shadow };

        try
        {
            var failures = new List<string>();

            foreach (var descriptor in enabled)
            {
                var assemblyPath = Path.Combine(shadow, descriptor.AssemblyPath);
                if (!File.Exists(assemblyPath))
                {
                    failures.Add($"{descriptor.CommandName}: 程序集不存在 {descriptor.AssemblyPath}");
                    continue;
                }

                var assembly = context.LoadFromAssemblyPath(assemblyPath);
                outcome.Assemblies[Path.GetFileName(assemblyPath)] = Describe(assemblyPath);

                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t is not null).ToArray();
                    var first = ex.LoaderExceptions?.FirstOrDefault()?.Message ?? "未知原因";
                    outcome.Warnings.Add($"{Path.GetFileName(assemblyPath)} 部分类型加载失败: {first}");
                }

                // 先按「类型名 ≈ 命令名」排序，命中后立即停止实例化，
                // 避免为一个命令把同程序集里所有命令都构造一遍（每个都会创建 ExternalEvent）。
                var candidates = types
                    .Where(t => t is not null && !t.IsInterface && !t.IsAbstract
                                && typeof(IRevitCommand).IsAssignableFrom(t))
                    .OrderByDescending(t => NameMatches(t.Name, descriptor.CommandName))
                    .ToList();

                IRevitCommand matched = null;
                foreach (var type in candidates)
                {
                    var instance = Instantiate(type);
                    if (instance is null) continue;
                    if (string.Equals(instance.CommandName, descriptor.CommandName, StringComparison.Ordinal))
                    {
                        matched = instance;
                        break;
                    }
                }

                if (matched is null)
                    failures.Add($"{descriptor.CommandName}: 未找到对应命令类型（{Path.GetFileName(assemblyPath)}）");
                else
                    outcome.Commands[matched.CommandName] = matched;
            }

            if (failures.Count > 0)
                throw new InvalidOperationException("命令加载失败，已回滚到上一版本: " + string.Join("; ", failures));

            // 原子切换：到这里所有命令都已就绪
            ReleaseActiveCommands();

            var previous = _active;
            _active = context;
            _activeCommands = outcome.Commands;
            if (previous is not null) _retired.Add(new WeakReference(previous));

            return outcome;
        }
        catch
        {
            try { context.Unload(); } catch { }
            TryDelete(shadow);
            throw;
        }
    }

    /// <summary>
    /// 释放上一批命令持有的 ExternalEvent。
    ///
    /// ExternalEventCommandBase 只实现 IRevitCommand、没有实现 IDisposable，
    /// 其内部创建的 Revit ExternalEvent 在命令被替换时不会自动释放。
    /// 每轮热重载会泄漏「命令数量」个 ExternalEvent，累积后 Revit 的 ExternalEvent
    /// 机制会整体停止响应（所有 Raise 都不再执行），只能重启 Revit 才能恢复。
    /// 因此必须在切换前显式 Dispose。
    /// </summary>
    private void ReleaseActiveCommands()
    {
        foreach (var command in _activeCommands.Values)
        {
            try
            {
                // Event 属性由基类暴露（可能非 public），反射取一次即可
                var property = command.GetType().GetProperty("Event",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (property?.GetValue(command) is IDisposable disposable)
                    disposable.Dispose();
            }
            catch (Exception ex)
            {
                AppLogger.Error($"释放命令 ExternalEvent 失败: {ex.Message}");
            }
        }

        _activeCommands.Clear();
    }

    /// <summary>
    /// 回收已卸载的旧上下文。必须在注册表换成新实例之后调用，否则命令实例仍持有旧程序集引用。
    /// 返回实际回收数与被存活的（存活说明有引用泄漏，不影响本次运行，但会占用内存）。
    /// </summary>
    public (int collected, int alive) CollectRetired()
    {
        if (_retired.Count == 0) return (0, 0);

        var total = _retired.Count;
        for (var i = 0; i < 10; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (_retired.All(w => !w.IsAlive)) break;
        }

        var alive = _retired.Count(w => w.IsAlive);
        _retired.RemoveAll(w => !w.IsAlive);
        return (total - alive, alive);
    }

    private IRevitCommand Instantiate(Type type)
    {
        try
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
        catch (Exception ex)
        {
            AppLogger.Error($"实例化 {type.FullName} 失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>类型名是否疑似对应某命令名（fanmo_build ↔ FanmoBuildCommand）。</summary>
    private static bool NameMatches(string typeName, string commandName)
    {
        var normalized = commandName.Replace("_", "").Replace("-", "");
        return typeName.Replace("_", "")
            .Contains(normalized, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>程序集版本指纹：版本号 + 大小 + 写入时间，用于确认「加载的到底是哪一版」。</summary>
    private static string Describe(string path)
    {
        try
        {
            var info = new FileInfo(path);
            var version = FileVersionInfo.GetVersionInfo(path).FileVersion ?? "-";
            return $"v{version} {info.Length / 1024}KB {info.LastWriteTime:MM-dd HH:mm:ss}";
        }
        catch
        {
            return "-";
        }
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }
}
