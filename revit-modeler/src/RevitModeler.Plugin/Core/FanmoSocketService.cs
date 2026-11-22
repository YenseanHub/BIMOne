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
/// 翻模 JSON-RPC 服务：TCP 监听（默认 8091），协议与 revit-mcp 一致（JSON-RPC 2.0 over TCP）。
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

    public bool IsRunning => _isRunning;
    public int Port => PluginConfig.Port;

    private FanmoSocketService() { }

    public void Initialize(UIApplication uiApp)
    {
        _uiApp = uiApp;
        _registry = new CommandRegistry();
        new FanmoCommandLoader(_registry, uiApp).LoadCommands();
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
                while (_isRunning && client.Connected)
                {
                    int read;
                    try { read = stream.Read(buffer, 0, buffer.Length); }
                    catch (IOException) { break; }
                    if (read == 0) break;

                    var raw = Encoding.UTF8.GetString(buffer, 0, read);
                    var response = ProcessRequest(raw);
                    var bytes = Encoding.UTF8.GetBytes(response);
                    stream.Write(bytes, 0, bytes.Length);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"处理客户端失败: {ex.Message}");
        }
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
                ["commands"] = new JArray(_registry.GetRegisteredCommands())
            };
            return Success(request.Id, pong);
        }

        if (!_registry.TryGetCommand(request.Method, out var command))
            return Error(request.Id, JsonRPCErrorCodes.MethodNotFound, $"Method '{request.Method}' not found");

        try
        {
            var result = command.Execute(request.GetParamsObject(), request.Id);
            return Success(request.Id, result);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"命令 {request.Method} 执行失败: {ex.Message}");
            return Error(request.Id, JsonRPCErrorCodes.InternalError, ex.Message);
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
