using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CadReader.Core;

// CadReader.McpServer：精简 MCP stdio server（JSON-RPC 2.0 over stdin/stdout）
// 暴露工具：cad__parse_cad（读 DWG/DXF → 返回 IR JSON）、cad__verify_image（内嵌预览图交叉验证）
// 日志一律走 stderr，stdout 只输出协议帧——这是 stdio MCP 的铁律。

var server = new McpServer();
await server.RunAsync();

sealed class McpServer
{
    private const string ProtocolVersion = "2024-11-05";
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public async Task RunAsync()
    {
        using var stdin = Console.OpenStandardInput();
        using var reader = new StreamReader(stdin, Encoding.UTF8);
        using var writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };

        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonNode? response = null;
            try
            {
                var req = JsonNode.Parse(line)!.AsObject();
                string method = req["method"]?.GetValue<string>() ?? "";
                bool isNotification = !req.ContainsKey("id");
                response = Handle(req, method, isNotification);
            }
            catch (Exception ex)
            {
                Log($"处理请求异常: {ex.Message}");
                response = Error(-1, -32603, $"Internal error: {ex.Message}");
            }
            if (response is not null)
                writer.WriteLine(response.ToJsonString(JsonOpts));
        }
    }

    private JsonNode? Handle(JsonObject req, string method, bool isNotification)
    {
        JsonNode? id = req["id"]?.DeepClone();

        switch (method)
        {
            case "initialize":
                return Result(id, new JsonObject
                {
                    ["protocolVersion"] = ProtocolVersion,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "cad-reader", ["version"] = "0.1.0" }
                });

            case "notifications/initialized":
                return null; // 通知，无需响应

            case "tools/list":
                return Result(id, new JsonObject
                {
                    ["tools"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["name"] = "cad__parse_cad",
                            ["description"] = "读取 CAD 文件(DWG/DXF)，提取图元的几何、图层、文字、闭合与 Z 值，返回结构化 IR JSON。用于 CAD 翻模的数据读取阶段。",
                            ["inputSchema"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["path"] = new JsonObject
                                    {
                                        ["type"] = "string",
                                        ["description"] = "CAD 文件绝对路径（.dwg 或 .dxf）"
                                    },
                                    ["summaryOnly"] = new JsonObject
                                    {
                                        ["type"] = "boolean",
                                        ["description"] = "true 时只返回统计摘要（类型分布/图层/Z值），不返回全部图元。默认 false。",
                                        ["default"] = false
                                    },
                                    ["outPath"] = new JsonObject
                                    {
                                        ["type"] = "string",
                                        ["description"] = "可选。指定后把完整 IR 写入该文件（JSON），返回文件路径+摘要；大模型可直接 Read/Edit。不传则保持内联返回完整 IR。建议 work/<task>.ir.json"
                                    }
                                },
                                ["required"] = new JsonArray("path")
                            }
                        },
                        new JsonObject
                        {
                            ["name"] = "cad__verify_image",
                            ["description"] = "图像交叉验证：用 DWG 内嵌预览图（AutoCAD 渲染，独立于矢量解析）核对解析结果，输出全局一致度与低一致区域，供专家定向复核低置信图元。",
                            ["inputSchema"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["path"] = new JsonObject
                                    {
                                        ["type"] = "string",
                                        ["description"] = "DWG 文件绝对路径（必须含内嵌预览图；DXF 无此能力）"
                                    },
                                    ["irPath"] = new JsonObject
                                    {
                                        ["type"] = "string",
                                        ["description"] = "已解析的 IR JSON 路径；不传则现场解析 path"
                                    },
                                    ["outPath"] = new JsonObject
                                    {
                                        ["type"] = "string",
                                        ["description"] = "可选。指定后把验证报告写入该文件（JSON），返回路径+摘要。建议 work/<task>.verify.json"
                                    }
                                },
                                ["required"] = new JsonArray("path")
                            }
                        }
                    }
                });

            case "tools/call":
            {
                string name = req["params"]?["name"]?.GetValue<string>() ?? "";
                var args = req["params"]?["arguments"]?.AsObject();
                if (name != "cad__parse_cad" && name != "cad__verify_image")
                    return Error(id, -32602, $"未知工具: {name}");

                string? path = args?["path"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(path))
                    return ToolError(id, "缺少必填参数 path");

                try
                {
                    if (name == "cad__verify_image")
                    {
                        string? irPath = args?["irPath"]?.GetValue<string>();
                        IrDocument verifyIr;
                        if (!string.IsNullOrWhiteSpace(irPath) && File.Exists(irPath!))
                            verifyIr = JsonSerializer.Deserialize<IrDocument>(File.ReadAllText(irPath!),
                                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                        else
                            verifyIr = CadParser.Parse(path!, msg => Log(msg));
                        var report = ImageVerifier.Verify(path!, verifyIr);
                        var reportJson = ImageVerifier.ToJson(report);
                        string? verifyOut = args?["outPath"]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(verifyOut))
                        {
                            WriteJsonFile(verifyOut!, reportJson);
                            return Result(id, new JsonObject
                            {
                                ["content"] = new JsonArray
                                {
                                    new JsonObject { ["type"] = "text", ["text"] = $"验证报告已写入: {Path.GetFullPath(verifyOut!)}\n{reportJson}" }
                                }
                            });
                        }
                        return Result(id, new JsonObject
                        {
                            ["content"] = new JsonArray
                            {
                                new JsonObject { ["type"] = "text", ["text"] = reportJson }
                            }
                        });
                    }

                    bool summaryOnly = args?["summaryOnly"]?.GetValue<bool>() ?? false;
                    var ir = CadParser.Parse(path!, msg => Log(msg));
                    string payload = summaryOnly ? StatsJson(ir) : CadParser.ToJson(ir);

                    string? outPath = args?["outPath"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(outPath))
                    {
                        // 落盘完整 IR（即使 summaryOnly=true 也写完整 IR，摘要只影响返回文本）
                        WriteJsonFile(outPath!, CadParser.ToJson(ir));
                        payload = $"IR 已写入: {Path.GetFullPath(outPath!)}\n" +
                                  $"图元 {ir.Stats.Total} / 图层 {ir.Stats.Layers.Count} / Z非零 {ir.Stats.NonZeroZ}\n" +
                                  "下一步：Read 该文件做语义研判，写出 commands.json，再调用 revit__fanmo_build(dataPath=...)。";
                    }

                    return Result(id, new JsonObject
                    {
                        ["content"] = new JsonArray
                        {
                            new JsonObject { ["type"] = "text", ["text"] = payload }
                        }
                    });
                }
                catch (Exception ex)
                {
                    return ToolError(id, $"CAD 解析失败: {ex.GetType().Name}: {ex.Message}");
                }
            }

            case "ping":
                return Result(id, new JsonObject());

            default:
                return isNotification ? null : Error(id, -32601, $"未知方法: {method}");
        }
    }

    /// <summary>把已是合法 JSON 的文本写入文件，自动创建父目录。</summary>
    private static void WriteJsonFile(string path, string json)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(full, json, new UTF8Encoding(false));
        Log($"已写出: {full}");
    }

    private static string StatsJson(IrDocument ir)
    {
        var s = ir.Stats;
        return JsonSerializer.Serialize(new
        {
            source = ir.Source, version = ir.Version, units = ir.Units,
            total = s.Total, byType = s.ByType, layers = s.Layers,
            nonZeroZ = s.NonZeroZ, zRange = $"{s.ZMin:F3}~{s.ZMax:F3}"
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject Result(JsonNode? id, JsonNode result) => new()
    {
        ["jsonrpc"] = "2.0", ["id"] = id ?? JsonValue.Create(0)!, ["result"] = result
    };

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0", ["id"] = id ?? JsonValue.Create(0)!,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
    };

    private static JsonObject ToolError(JsonNode? id, string message) => Result(id, new JsonObject
    {
        ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = message } },
        ["isError"] = true
    });

    private static void Log(string msg) => Console.Error.WriteLine($"[cad-reader-mcp] {msg}");
}
