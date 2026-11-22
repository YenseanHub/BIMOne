using CadReader.Core;

// CadReader.Cli：命令行入口，复用 Core。用于人工验证、批处理与完整翻模命令生成。
if (args.Length == 0)
{
    Console.Error.WriteLine("用法: CadReader.Cli <dwg/dxf路径> [输出json路径] [--summary] [--commands <commands.json路径>] [--sheet <图纸名>] [--no-beams] [--storey <mm>]");
    return 1;
}

string path = args[0];
string outPath = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : Path.ChangeExtension(path, ".ir.json");
bool summaryOnly = args.Contains("--summary");
string? commandsPath = null;
var options = new FanmoBuildOptions();
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--commands" when i + 1 < args.Length:
            commandsPath = args[++i];
            break;
        case "--sheet" when i + 1 < args.Length:
            options.SheetFilter = args[++i];
            break;
        case "--no-beams":
            options.IncludeBeams = false;
            break;
        case "--storey" when i + 1 < args.Length && double.TryParse(args[i + 1], out var h):
            options.StoreyHeightMm = h;
            i++;
            break;
    }
}

try
{
    var ir = CadParser.Parse(path, msg => Console.Error.WriteLine(msg));
    string json = summaryOnly
        ? System.Text.Json.JsonSerializer.Serialize(new
        {
            source = ir.Source, version = ir.Version, units = ir.Units,
            total = ir.Stats.Total, byType = ir.Stats.ByType, layers = ir.Stats.Layers,
            nonZeroZ = ir.Stats.NonZeroZ, zRange = $"{ir.Stats.ZMin:F3}~{ir.Stats.ZMax:F3}",
            sheets = ir.Stats.Sheets,
            sheetTitles = ir.Entities.Where(e => !string.IsNullOrEmpty(e.SheetTitle))
                .GroupBy(e => e.Sheet).ToDictionary(g => g.Key, g => g.First().SheetTitle)
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })
        : CadParser.ToJson(ir);

    string? irDir = Path.GetDirectoryName(Path.GetFullPath(outPath));
    if (!string.IsNullOrEmpty(irDir)) Directory.CreateDirectory(irDir);
    File.WriteAllText(outPath, json);
    Console.WriteLine($"解析完成: {ir.Source} [{ir.Version}, {ir.Units}] 共 {ir.Stats.Total} 图元");
    Console.WriteLine($"图层({ir.Stats.Layers.Count}): {string.Join(", ", ir.Stats.Layers)}");
    Console.WriteLine($"图纸单元: {ir.Stats.Sheets.Count}（已展开块 {ir.Stats.ExplodedSheets}）");
    Console.WriteLine($"非零 Z: {ir.Stats.NonZeroZ}  输出: {outPath}");

    if (!string.IsNullOrWhiteSpace(commandsPath))
    {
        var report = new FanmoBuildReport();
        var commands = FanmoCommandBuilder.Build(ir, options, report);
        string? cmdDir = Path.GetDirectoryName(Path.GetFullPath(commandsPath));
        if (!string.IsNullOrEmpty(cmdDir)) Directory.CreateDirectory(cmdDir);
        File.WriteAllText(commandsPath, FanmoCommandBuilder.ToJson(commands));

        int walls = commands.Count(c => c.Category == "Wall");
        int floors = commands.Count(c => c.Category == "Floor");
        int columns = commands.Count(c => c.Category == "Column");
        int beams = commands.Count(c => c.Category == "Beam");
        int families = commands.Count(c => c.Category is "Door" or "Window");
        Console.WriteLine($"选定图纸: {report.SheetTitle} ({report.Sheet}) (候选 {report.Candidates} 张)");
        foreach (var s in report.Sources) Console.WriteLine($"  · {s.Key}: {s.Value}");
        foreach (var n in report.Notes) Console.WriteLine($"  ! {n}");
        Console.WriteLine($"命令生成: 墙 {walls} / 柱 {columns} / 梁 {beams} / 楼板 {floors} / 门窗 {families}  输出: {commandsPath}");
    }

    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"失败: {ex.GetType().Name}: {ex.Message}");
    return 3;
}
