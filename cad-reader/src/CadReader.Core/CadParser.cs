using System.Text.Json;
using System.Text.Encodings.Web;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;

namespace CadReader.Core;

/// <summary>CAD 中间表示(IR)：解耦 CAD 解析与 Revit 建模的合同。</summary>
public sealed class IrDocument
{
    public string Source { get; set; } = "";
    public string Version { get; set; } = "";
    public string Units { get; set; } = "";
    public List<IrEntity> Entities { get; set; } = new();
    public IrStats Stats { get; set; } = new();
}

public sealed class IrEntity
{
    public string Handle { get; set; } = "";
    public string Type { get; set; } = "";
    public string Layer { get; set; } = "";
    public string Geometry { get; set; } = "";   // 可读摘要（调试用）
    public string Text { get; set; } = "";
    public bool Closed { get; set; }
    public double Z { get; set; }
    public int PointCount { get; set; }

    // 结构化坐标（建模真正使用；单位 mm，与 CAD 源一致）
    public double[] Start { get; set; }          // 线/弧起点：[x,y,z]
    public double[] End { get; set; }            // 线/弧终点：[x,y,z]
    public List<double[]> Points { get; set; }   // 多段线顶点：[[x,y,z],...]（楼板成面用）
    public double[] Center { get; set; }         // 圆/弧/块插入点
    public double Radius { get; set; }           // 圆/弧半径
    public string BlockName { get; set; } = "";  // Insert 块名（结构化，供门窗/家具识别）
    public string LineWeight { get; set; } = ""; // CAD 线宽（辅助墙厚判断）
    public string Color { get; set; } = "";      // CAD 颜色（辅助图层语义）

    /// <summary>所属图纸单元（顶层大块的块名）。整套施工图靠它分区，单图 DWG 为空。</summary>
    public string Sheet { get; set; } = "";

    /// <summary>图纸可读标题（如“一层平面图”），由块内文字推断。</summary>
    public string SheetTitle { get; set; } = "";

    /// <summary>块引用 X/Y 缩放（天正单位块用它表达截面，如 _FZH 柱块 600×600）。</summary>
    public double ScaleX { get; set; } = 1;
    public double ScaleY { get; set; } = 1;

    /// <summary>块引用旋转角（度）。</summary>
    public double Rotation { get; set; }
}

public sealed class IrStats
{
    public int Total { get; set; }
    public Dictionary<string, int> ByType { get; set; } = new();
    public List<string> Layers { get; set; } = new();
    public int NonZeroZ { get; set; }
    public double ZMin { get; set; }
    public double ZMax { get; set; }
    /// <summary>坐标含 NaN/Infinity 而被清洗归零的图元数（数据质量指标）。</summary>
    public int NonFiniteCoords { get; set; }
    /// <summary>图纸单元 → 展开后图元数（整套施工图分区结果）。</summary>
    public Dictionary<string, int> Sheets { get; set; } = new();
    /// <summary>被展开的顶层大块数。</summary>
    public int ExplodedSheets { get; set; }
}

/// <summary>DWG/DXF 解析器：读取 CAD 文件 → 输出 IR。principal API，CLI/MCP 共用。</summary>
public static class CadParser
{
    public static IrDocument Parse(string path, Action<string>? log = null)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"CAD 文件不存在: {path}", path);

        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".dwg" or ".dxf"))
            throw new NotSupportedException($"不支持的 CAD 格式: {ext}(仅 .dwg / .dxf)");

        void Notify(object? s, NotificationEventArgs e) => log?.Invoke($"[{e.NotificationType}] {e.Message}");

        CadDocument doc = ext == ".dwg"
            ? new DwgReader(path, Notify).Read()
            : new ACadSharp.IO.DxfReader(path, Notify).Read();

        var ir = new IrDocument
        {
            Source = Path.GetFileName(path),
            Version = doc.Header.Version.ToString(),
            Units = doc.Header.InsUnits.ToString()
        };

        int exploded = 0;
        CollectEntities(doc.Entities, "", 0, ir.Entities, ref exploded);
        AssignSheetTitles(ir);

        // 真实图纸偶含 NaN/Infinity 坐标（异常样条、坏块引用等），会直接让 JSON 序列化抛错。
        // 建模前统一清洗归零并计数，保证 IR 始终可落盘、可下游消费。
        int nonFinite = SanitizeNonFinite(ir);
        ir.Stats = ComputeStats(ir);
        ir.Stats.NonFiniteCoords = nonFinite;
        ir.Stats.ExplodedSheets = exploded;
        ir.Stats.Sheets = ir.Entities.Where(e => !string.IsNullOrEmpty(e.Sheet))
            .GroupBy(e => e.Sheet).ToDictionary(g => g.Key, g => g.Count());
        if (exploded > 0)
            log?.Invoke($"[Info] 展开 {exploded} 个图纸大块（整图内容藏在块定义里）");
        if (nonFinite > 0)
            log?.Invoke($"[Warn] {nonFinite} 个图元含非有限坐标，已清洗归零");
        return ir;
    }

    /// <summary>超过该图元数的块视为“整张图纸”，展开成真实几何参与识别。</summary>
    private const int SheetExplodeMinEntities = 200;

    private const int MaxExplodeDepth = 3;

    /// <summary>
    /// 收集图元；遇到内容庞大的块引用（天正/结构软件把整张平面图存进块）时递归展开，
    /// 并把顶层块名记为图纸单元 Sheet，供后续“先分图纸、再识别构件”使用；
    /// 小而语义明确的块（门/窗/柱）保持 Insert 不动，保留 BlockName 与缩放语义。
    /// </summary>
    private static void CollectEntities(IEnumerable<Entity> source, string sheet, int depth,
        List<IrEntity> sink, ref int exploded)
    {
        foreach (Entity e in source)
        {
            if (e is Insert ins && ins.Block is not null)
            {
                int childCount;
                try { childCount = ins.Block.Entities.Count(); }
                catch { childCount = 0; }

                if (childCount > SheetExplodeMinEntities && depth < MaxExplodeDepth)
                {
                    List<Entity> expanded;
                    try { expanded = ins.Explode().ToList(); }
                    catch { expanded = new List<Entity>(); }

                    string name = depth == 0 && !string.IsNullOrWhiteSpace(ins.Block.Name)
                        ? ins.Block.Name
                        : sheet;
                    if (depth == 0) exploded++;
                    CollectEntities(expanded, name, depth + 1, sink, ref exploded);
                    continue;
                }
            }

            var ir = ToIr(e);
            ir.Sheet = sheet;
            sink.Add(ir);
        }
    }

    private static readonly string[] SheetTitleTokens =
        { "平面图", "大样", "立面图", "剖面图", "总平面", "详图", "机房", "屋顶" };

    /// <summary>按块内标题文字回填图纸可读名（“一层平面图 1:100” → “一层平面图”）。</summary>
    private static void AssignSheetTitles(IrDocument ir)
    {
        var titles = new Dictionary<string, string>();
        foreach (var g in ir.Entities.Where(e => !string.IsNullOrEmpty(e.Sheet)).GroupBy(e => e.Sheet))
        {
            var candidate = g.Where(e => !string.IsNullOrWhiteSpace(e.Text))
                .Where(e => SheetTitleTokens.Any(t => e.Text.Contains(t, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(e => e.Text.Length <= 24)
                .FirstOrDefault();
            if (candidate is null) continue;
            var raw = candidate.Text.Trim().Replace("\r", " ").Replace("\n", " ");
            int cut = raw.IndexOf("1:", StringComparison.Ordinal);
            if (cut > 0) raw = raw[..cut];
            titles[g.Key] = raw.Trim();
        }
        foreach (var e in ir.Entities)
            if (!string.IsNullOrEmpty(e.Sheet) && titles.TryGetValue(e.Sheet, out var t))
                e.SheetTitle = t;
    }

    public static string ToJson(IrDocument ir) => JsonSerializer.Serialize(ir, new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, // 与 revit-modeler 的输入契约对齐
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    });

    private static IrEntity ToIr(Entity e)
    {
        string layer = e.Layer?.Name ?? "";
        string type = e.GetType().Name;
        string text = "", geometry = "";
        bool closed = false;
        double z = 0;
        int pointCount = 0;
        double[] start = null, end = null, center = null;
        List<double[]> points = null;
        double radius = 0;
        string blockName = "";
        double scaleX = 1, scaleY = 1, rotation = 0;

        switch (e)
        {
            case Line l:
                start = XYZ(l.StartPoint); end = XYZ(l.EndPoint);
                geometry = $"line({l.StartPoint.X:F2},{l.StartPoint.Y:F2})->({l.EndPoint.X:F2},{l.EndPoint.Y:F2})";
                z = l.StartPoint.Z;
                break;
            case LwPolyline lp:
                pointCount = lp.Vertices.Count;
                closed = lp.IsClosed;
                points = lp.Vertices.Select(v => new[] { v.Location.X, v.Location.Y, (double)lp.Elevation }).ToList();
                start = points.FirstOrDefault(); end = closed ? points.FirstOrDefault() : points.LastOrDefault();
                geometry = $"lwpolyline[{pointCount}pts,closed={closed}]";
                z = lp.Elevation;
                break;
            case Polyline2D p2:
                pointCount = p2.Vertices.Count;
                closed = p2.IsClosed;
                points = p2.Vertices.Select(v => XYZ(v.Location)).ToList();
                start = points.FirstOrDefault(); end = closed ? points.FirstOrDefault() : points.LastOrDefault();
                geometry = $"polyline2d[{pointCount}pts,closed={closed}]";
                break;
            case Polyline3D p3:
                pointCount = p3.Vertices.Count;
                closed = p3.IsClosed;
                points = p3.Vertices.Select(v => XYZ(v.Location)).ToList();
                start = points.FirstOrDefault(); end = closed ? points.FirstOrDefault() : points.LastOrDefault();
                geometry = $"polyline3d[{pointCount}pts,closed={closed}]";
                break;
            case Arc a:
                center = XYZ(a.Center); radius = a.Radius;
                geometry = $"arc(center=({a.Center.X:F2},{a.Center.Y:F2}) r={a.Radius:F2} {a.StartAngle:F2}..{a.EndAngle:F2})";
                z = a.Center.Z;
                break;
            case Circle c:
                center = XYZ(c.Center); radius = c.Radius;
                geometry = $"circle(center=({c.Center.X:F2},{c.Center.Y:F2}) r={c.Radius:F2})";
                z = c.Center.Z;
                break;
            case TextEntity t:
                text = t.Value;
                center = XYZ(t.InsertPoint);
                geometry = $"text(@({t.InsertPoint.X:F2},{t.InsertPoint.Y:F2}))";
                z = t.InsertPoint.Z;
                break;
            case MText mt:
                text = mt.Value;
                center = XYZ(mt.InsertPoint);
                geometry = $"mtext(@({mt.InsertPoint.X:F2},{mt.InsertPoint.Y:F2}))";
                z = mt.InsertPoint.Z;
                break;
            case Insert ins:
                center = XYZ(ins.InsertPoint);
                blockName = ins.Block?.Name ?? "";
                scaleX = ins.XScale; scaleY = ins.YScale;
                rotation = ins.Rotation * 180.0 / Math.PI;
                geometry = $"insert(block={blockName} @({ins.InsertPoint.X:F2},{ins.InsertPoint.Y:F2}) scale={ins.XScale})";
                z = ins.InsertPoint.Z;
                break;
            default:
                geometry = type;
                break;
        }

        return new IrEntity
        {
            Handle = e.Handle.ToString(), Type = type, Layer = layer,
            Geometry = geometry, Text = text, Closed = closed, Z = z, PointCount = pointCount,
            Start = start, End = end, Points = points, Center = center, Radius = radius,
            BlockName = blockName,
            ScaleX = scaleX, ScaleY = scaleY, Rotation = rotation,
            LineWeight = e.LineWeight.ToString(),
            Color = e.Color.ToString()
        };
    }

    private static double[] XYZ(CSMath.XYZ v) => new[] { v.X, v.Y, v.Z };

    /// <summary>把 NaN/±Infinity 坐标清洗为 0，返回受影响图元数。</summary>
    private static int SanitizeNonFinite(IrDocument ir)
    {
        static bool Fix(double[]? arr)
        {
            if (arr is null) return false;
            bool changed = false;
            for (int i = 0; i < arr.Length; i++)
                if (!double.IsFinite(arr[i])) { arr[i] = 0; changed = true; }
            return changed;
        }

        int bad = 0;
        foreach (var e in ir.Entities)
        {
            bool c = false;
            c |= Fix(e.Start);
            c |= Fix(e.End);
            c |= Fix(e.Center);
            if (e.Points is not null)
                foreach (var p in e.Points) c |= Fix(p);
            if (!double.IsFinite(e.Z)) { e.Z = 0; c = true; }
            if (!double.IsFinite(e.Radius)) { e.Radius = 0; c = true; }
            if (c) bad++;
        }
        return bad;
    }

    private static IrStats ComputeStats(IrDocument ir)
    {
        var stats = new IrStats { Total = ir.Entities.Count };
        foreach (var g in ir.Entities.GroupBy(x => x.Type))
            stats.ByType[g.Key] = g.Count();
        stats.Layers = ir.Entities.Select(x => x.Layer)
            .Where(x => !string.IsNullOrEmpty(x)).Distinct().OrderBy(x => x).ToList();
        stats.NonZeroZ = ir.Entities.Count(x => Math.Abs(x.Z) > 1e-6);
        if (ir.Entities.Count > 0)
        {
            stats.ZMin = ir.Entities.Min(x => x.Z);
            stats.ZMax = ir.Entities.Max(x => x.Z);
        }
        return stats;
    }
}
