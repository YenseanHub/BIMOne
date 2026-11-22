using CadReader.Core;

// CadReader.Cli：命令行入口，复用 Core。用于人工验证与批处理。
if (args.Length == 0)
{
    Console.Error.WriteLine("用法: CadReader.Cli <dwg/dxf路径> [输出json路径] [--summary]");
    return 1;
}

string path = args[0];
string outPath = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : Path.ChangeExtension(path, ".ir.json");
bool summaryOnly = args.Contains("--summary");

try
{
    var ir = CadParser.Parse(path, msg => Console.Error.WriteLine(msg));
    string json = summaryOnly
        ? System.Text.Json.JsonSerializer.Serialize(new
        {
            source = ir.Source, version = ir.Version, units = ir.Units,
            total = ir.Stats.Total, byType = ir.Stats.ByType, layers = ir.Stats.Layers,
            nonZeroZ = ir.Stats.NonZeroZ, zRange = $"{ir.Stats.ZMin:F3}~{ir.Stats.ZMax:F3}"
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })
        : CadParser.ToJson(ir);

    File.WriteAllText(outPath, json);
    Console.WriteLine($"解析完成: {ir.Source} [{ir.Version}, {ir.Units}] 共 {ir.Stats.Total} 图元");
    Console.WriteLine($"图层({ir.Stats.Layers.Count}): {string.Join(", ", ir.Stats.Layers)}");
    Console.WriteLine($"非零 Z: {ir.Stats.NonZeroZ}  输出: {outPath}");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"失败: {ex.GetType().Name}: {ex.Message}");
    return 3;
}
