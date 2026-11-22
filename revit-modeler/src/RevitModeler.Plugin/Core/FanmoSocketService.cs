using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Models;
using RevitModeler.Plugin.Utils;
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Autodesk.Revit.UI;

namespace RevitModeler.Plugin.Core;

/// <summary>
/// 翻模 JSON-RPC 服务：TCP 监听（默认 8982），协议与 revit-mcp 一致（JSON-RPC 2.0 over TCP）。
/// 独立单例，不与 revit-mcp Host 的 SocketService 共享端口或注册表。
/// </summary>
public class FanmoSocketService
{
    private static readonly FanmoSocketService _instance = new();
    public static FanmoSocketService Instance => _instance;

    private TcpListener _listener;
    private Thread _listenerThread;
    private bool _isRunning;
    private UIApplication _uiApp;
    private CommandRegistry _registry = new();
    private FanmoCommandLoader _loader;
    private ReloadRequestHandler _reloadHandler;
    private ExternalEvent _reloadEvent;

    /// <summary>
    /// 命令执行与热重载的互斥闸门：执行命令持读锁（可并发），重载持写锁（等待在跑的命令结束）。
    /// 没有它的话，重载可能把正在执行的命令程序集卸掉。
    /// </summary>
    private readonly ReaderWriterLockSlim _gate = new(LockRecursionPolicy.NoRecursion);

    public bool IsRunning => _isRunning;
    public int Port => PluginConfig.Port;

    private FanmoSocketService() { }

    public void Initialize(UIApplication uiApp)
    {
        _uiApp = uiApp;

        // 常驻的线程切换器：热重载必须在 API 上下文执行，Initialize 本身就在主线程（按钮点击），
        // 所以这里创建 ExternalEvent 是安全的；只建一次，重复开关服务不重复创建。
        if (_reloadEvent is null)
        {
            _reloadHandler = new ReloadRequestHandler();
            _reloadEvent = ExternalEvent.Create(_reloadHandler);
        }

        _registry = new CommandRegistry();

        // 复用同一个加载器：重复开关服务时才能把上一版上下文交给它回收，
        // 否则每次 new 一个会把旧的可回收上下文变成孤儿（永不卸载）。
        _loader ??= new FanmoCommandLoader(uiApp);
        try
        {
            var outcome = _loader.Load();
            _registry = new CommandRegistry(outcome.Commands);
            foreach (var warning in outcome.Warnings)
                AppLogger.Error($"[命令加载告警] {warning}");
        }
        catch (Exception ex)
        {
            // 命令加载失败不阻断服务：ping 仍可用，便于用户看到错误原因后热修复重载
            AppLogger.Error($"命令集加载失败: {ex.Message}");
        }

        AppLogger.Info($"翻模服务初始化完成，端口 {Port}，已注册命令 {string.Join(", ", _registry.GetRegisteredCommands())}");
    }

    public void Start()
    {
        if (_isRunning) return;
        try
        {
            _isRunning = true;
            _listener = new TcpListener(IPAddress.Loopback, Port); // 仅本机，收敛暴露面
            _listener.Start();
            _listenerThread = new Thread(ListenLoop) { IsBackground = true };
            _listenerThread.Start();
            AppLogger.Info($"翻模服务已启动，监听 127.0.0.1:{Port}");
        }
        catch (Exception ex)
        {
            _isRunning = false;
            AppLogger.Error($"翻模服务启动失败: {ex.Message}");
            throw;
        }
    }

    public void Stop()
    {
        if (!_isRunning) return;
        _isRunning = false;
        try { _listener?.Stop(); } catch { }
        _listener = null;
        AppLogger.Info("翻模服务已停止");
    }

    private void ListenLoop()
    {
        while (_isRunning)
        {
            try
            {
                var client = _listener.AcceptTcpClient();
                new Thread(() => HandleClient(client)) { IsBackground = true }.Start();
            }
            catch (SocketException) { if (!_isRunning) break; }
            catch (Exception ex) { AppLogger.Error($"接受连接失败: {ex.Message}"); }
        }
    }

    private void HandleClient(TcpClient client)
    {
        try
        {
            using (client)
            using (var stream = client.GetStream())
            {
                var buffer = new byte[8192];
                var pending = new StringBuilder();
                // 持久 Decoder：避免多字节 UTF-8 字符被 socket 分包截断
                var decoder = Encoding.UTF8.GetDecoder();
                var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];

                while (_isRunning && client.Connected)
                {
                    int read;
                    try { read = stream.Read(buffer, 0, buffer.Length); }
                    catch (IOException) { break; }
                    if (read == 0) break;

                    int charCount = decoder.GetChars(buffer, 0, read, chars, 0);
                    pending.Append(chars, 0, charCount);

                    // 一条 TCP 流里可能是半条 / 一条 / 多条 JSON-RPC 请求：
                    // 按括号深度切出所有完整对象，逐个处理并回写，剩余不完整部分留在 pending。
                    string rest = pending.ToString();
                    int consumed;
                    while (TryExtractFrame(rest, out var frame, out consumed))
                    {
                        var response = ProcessRequest(frame);
                        var bytes = Encoding.UTF8.GetBytes(response);
                        stream.Write(bytes, 0, bytes.Length);
                        rest = rest.Substring(consumed);
                    }
                    pending.Clear();
                    pending.Append(rest);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"处理客户端失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 从累积文本中切出第一个完整 JSON 对象（考虑字符串与转义），返回其消费长度。
    /// 与客户端 FanmoRevitClient.drain 的括号深度扫描保持对称，支持大 payload 跨包与粘包。
    /// </summary>
    private static bool TryExtractFrame(string s, out string frame, out int consumed)
    {
        frame = null;
        consumed = 0;
        int depth = 0, start = -1;
        bool inString = false, escape = false;

        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            if (inString)
            {
                if (escape) escape = false;
                else if (ch == '\\') escape = true;
                else if (ch == '"') inString = false;
                continue;
            }
            if (ch == '"') { inString = true; continue; }
            if (ch == '{') { if (depth == 0) start = i; depth++; }
            else if (ch == '}')
            {
                depth--;
                if (depth == 0 && start >= 0)
                {
                    frame = s.Substring(start, i - start + 1);
                    consumed = i + 1;
                    return true;
                }
                if (depth < 0) { depth = 0; start = -1; } // 容错：多余的右括号
            }
        }
        return false;
    }


    private string ProcessRequest(string raw)
    {
        JsonRPCRequest request;
        try
        {
            request = JsonConvert.DeserializeObject<JsonRPCRequest>(raw);
            if (request == null || !request.IsValid())
                return Error(null, JsonRPCErrorCodes.InvalidRequest, "Invalid JSON-RPC request");
        }
        catch (JsonException)
        {
            return Error(null, JsonRPCErrorCodes.ParseError, "Invalid JSON");
        }

        // 内置自检：不依赖 commandset 是否加载成功，用于验证 server↔plugin 链路
        if (request.Method == "revit__ping")
        {
            var pong = new JObject
            {
                ["ok"] = true,
                ["service"] = "BIMOne.RevitModeler",
                ["port"] = Port,
                ["commands"] = new JArray(_registry.GetRegisteredCommands()),
                ["hotReload"] = true,
                ["shadowDirectory"] = _loader?.ActiveShadowDirectory ?? ""
            };
            return Success(request.Id, pong);
        }

        // 内置热重载：不经过 commandset，因此命令集正在被替换时也能调用
        if (request.Method is "fanmo_reload" or "system__reload_commands")
            return Success(request.Id, ReloadCommands());

        if (!_registry.TryGetCommand(request.Method, out var command))
            return Error(request.Id, JsonRPCErrorCodes.MethodNotFound, $"Method '{request.Method}' not found");

        try
        {
            // 读锁：允许命令并发执行，但阻止「边执行边卸载」
            _gate.EnterReadLock();
            try
            {
                var result = command.Execute(request.GetParamsObject(), request.Id);
                return Success(request.Id, result);
            }
            finally
            {
                _gate.ExitReadLock();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"命令 {request.Method} 执行失败: {ex.Message}");
            return Error(request.Id, JsonRPCErrorCodes.InternalError, ex.Message);
        }
    }

    /// <summary>
    /// 就地重载命令集：编译好的 dll 覆盖到命令目录后调用即可，无需重启 Revit。
    /// 新版本加载失败时旧版本保持可用（返回 ok=false 说明原因）。
    /// </summary>
    private object ReloadCommands()
    {
        if (_loader is null)
            return new JObject { ["ok"] = false, ["error"] = "加载器未初始化" };

        if (_reloadEvent is null)
            return new JObject { ["ok"] = false, ["error"] = "重载事件未初始化，请重启 Revit 一次以启用热重载" };

        // 等待正在执行的命令结束；超时则拒绝重载，避免长时间阻塞调用方
        if (!_gate.TryEnterWriteLock(TimeSpan.FromSeconds(20)))
            return new JObject { ["ok"] = false, ["error"] = "有命令正在执行，请稍后重试重载" };

        try
        {
            // 加载动作必须回到 Revit 主线程（重建命令会创建 ExternalEvent）
            return _reloadHandler.Invoke(_reloadEvent, DoReload, TimeSpan.FromSeconds(90));
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    /// <summary>在 Revit 主线程执行：加载新版本 → 原子替换注册表 → 回收旧程序集。</summary>
    private object DoReload()
    {
        try
        {
            var outcome = _loader.Load();
            _registry = new CommandRegistry(outcome.Commands);   // 原子替换
            var (collected, alive) = _loader.CollectRetired();   // 旧实例已无引用，回收旧程序集

            AppLogger.Info($"命令集热重载成功: {string.Join(", ", outcome.Commands.Keys)}（回收 {collected} 个旧版本，存活 {alive}）");

            var assemblies = new JObject();
            foreach (var pair in outcome.Assemblies) assemblies[pair.Key] = pair.Value;

            return new JObject
            {
                ["ok"] = true,
                ["commands"] = new JArray(outcome.Commands.Keys),
                ["assemblies"] = assemblies,
                ["shadowDirectory"] = outcome.ShadowDirectory,
                ["retiredContexts"] = collected,
                ["liveContexts"] = alive,
                ["warnings"] = new JArray(outcome.Warnings)
            };
        }
        catch (Exception ex)
        {
            AppLogger.Error($"命令集热重载失败（已保留旧版本）: {ex.Message}");
            return new JObject { ["ok"] = false, ["error"] = ex.Message };
        }
    }

    private static string Success(string id, object result)
    {
        var response = new JsonRPCSuccessResponse
        {
            Id = id,
            Result = result is JToken token ? token : JToken.FromObject(result)
        };
        return response.ToJson();
    }

    private static string Error(string id, int code, string message)
    {
        var response = new JsonRPCErrorResponse
        {
            Id = id,
            Error = new JsonRPCError { Code = code, Message = message }
        };
        return response.ToJson();
    }
}
