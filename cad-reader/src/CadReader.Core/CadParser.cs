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
}

public sealed class IrStats
{
    public int Total { get; set; }
    public Dictionary<string, int> ByType { get; set; } = new();
    public List<string> Layers { get; set; } = new();
    public int NonZeroZ { get; set; }
    public double ZMin { get; set; }
    public double ZMax { get; set; }
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

        foreach (Entity e in doc.Entities)
            ir.Entities.Add(ToIr(e));

        ir.Stats = ComputeStats(ir);
        return ir;
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
                geometry = $"insert(block={ins.Block?.Name} @({ins.InsertPoint.X:F2},{ins.InsertPoint.Y:F2}) scale={ins.XScale})";
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
            Start = start, End = end, Points = points, Center = center, Radius = radius
        };
    }

    private static double[] XYZ(CSMath.XYZ v) => new[] { v.X, v.Y, v.Z };

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
