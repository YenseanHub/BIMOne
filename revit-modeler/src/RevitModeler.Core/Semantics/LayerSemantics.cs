using RevitModeler.Core.Models;
using RevitModeler.Core.Utils;

namespace RevitModeler.Core.Semantics;

/// <summary>
/// 无项目规则时的自动语义推断。两路证据：①通用图层名关键词（中英文）；②几何特征
/// （闭合面积、平行双线墙、圆弧门洞、文字块）。这是「BIM翻模大师」在"输入只有 DWG"场景下的主路脑子。
/// </summary>
public static class LayerSemantics
{
    // 通用图层命名惯例（跨项目），命中即高置信。顺序：特殊 → 一般。
    private static readonly (string Keyword, SemanticCategory Cat, double Conf)[] NameRules =
    {
        ("柱",        SemanticCategory.Column,      0.92), ("column",   SemanticCategory.Column,     0.92),
        ("门",        SemanticCategory.Door,        0.90), ("door",     SemanticCategory.Door,       0.90),
        ("窗",        SemanticCategory.Window,      0.90), ("window",   SemanticCategory.Window,     0.90),
        ("墙",        SemanticCategory.Wall,        0.90), ("wall",     SemanticCategory.Wall,       0.90),
        ("楼板",      SemanticCategory.Floor,       0.92), ("floor",    SemanticCategory.Floor,      0.88),
        ("slab",      SemanticCategory.Floor,       0.88), ("地面",     SemanticCategory.Finish,     0.80),
        ("天花",      SemanticCategory.Finish,      0.80), ("ceiling",  SemanticCategory.Finish,     0.80),
        ("家具",      SemanticCategory.Furniture,   0.90), ("家俱",     SemanticCategory.Furniture,  0.90),
        ("furniture", SemanticCategory.Furniture,   0.90),
        ("标注",      SemanticCategory.Annotation,  0.95), ("dim",      SemanticCategory.Annotation, 0.92),
        ("text",      SemanticCategory.Annotation,  0.92), ("文字",     SemanticCategory.Annotation, 0.92),
        ("轴线",      SemanticCategory.Annotation,  0.85), ("axis",     SemanticCategory.Annotation, 0.85),
    };

    /// <summary>对单条图元推断语义。all 用于几何特征（平行双线墙），可选。</summary>
    public static LayerInference Infer(CadEntity e, IReadOnlyList<CadEntity>? all = null)
    {
        if (e is null) return new LayerInference { Reason = "空图元" };

        // 1) 图层名关键词（最强证据）
        string layer = (e.Layer ?? "").Trim();
        foreach (var (kw, cat, conf) in NameRules)
        {
            if (layer.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return new LayerInference
                {
                    Category = cat, Confidence = conf, Reason = $"图层名含“{kw}”"
                };
        }

        // 2) 文字图元 → 标注
        if (e.Type is "TextEntity" or "MText")
            return new LayerInference
            {
                Category = SemanticCategory.Annotation, Confidence = 0.95, Reason = "文字图元"
            };

        // 3) 块引用的块名（Geometry 形如 insert(block=xxx @(...))）
        if (e.Type == "Insert")
        {
            string block = ExtractBlockName(e.Geometry);
            if (!string.IsNullOrEmpty(block))
            {
                foreach (var (kw, cat, conf) in NameRules)
                {
                    if (block.Contains(kw, StringComparison.OrdinalIgnoreCase))
                        return new LayerInference
                        {
                            Category = cat, Confidence = Math.Max(0.55, conf - 0.2),
                            Reason = $"块名“{block}”含“{kw}”"
                        };
                }
            }
        }

        // 4) 几何特征：闭合多段线 → 楼板/面域
        bool isPolyline = e.Type is "LwPolyline" or "Polyline2D" or "Polyline3D";
        if (isPolyline && e.Closed && e.Points is { Count: >= 3 })
        {
            double area = GeometryUtils.PolygonArea(e.Points);
            double conf = area >= 1_000_000 ? 0.72 : 0.55; // ≥1㎡ 高置信
            return new LayerInference
            {
                Category = SemanticCategory.Floor, Confidence = conf,
                Reason = $"闭合多段线({e.Points.Count}点,面积{area / 1_000_000:F2}㎡) → 楼板"
            };
        }

        // 5) 圆弧：半径在常见门洞范围 → 门（低置信，需复核）
        // 注：Arc 无 Length，Infer 靠 Center/Radius 由调用方补；此处保持原判定语义
        if (e.Type == "Arc" && e.Length < 1)
            return new LayerInference { Category = SemanticCategory.Door, Confidence = 0.45, Reason = "圆弧(疑似门开启线)" };

        // 6) 线段：长度足够 + 存在平行近邻 → 墙（双线墙特征）
        if (e.Type is "Line" or "Polyline2D" or "Polyline3D" && e.Length >= 600)
        {
            bool parallel = all is not null && HasParallelNeighbor(e, all);
            return new LayerInference
            {
                Category = SemanticCategory.Wall,
                Confidence = parallel ? 0.72 : 0.45,
                Reason = parallel ? "线段有平行近邻(双线墙特征)" : "长线段(单线，需复核是否为墙)"
            };
        }

        // 7) 圆：小半径可能是柱，但证据弱
        if (e.Type == "Circle")
            return new LayerInference { Category = SemanticCategory.Column, Confidence = 0.40, Reason = "圆(疑似柱，需复核)" };

        return new LayerInference { Category = SemanticCategory.Unknown, Confidence = 0.1, Reason = "无可用证据" };
    }

    /// <summary>语义类别 → 建模动作（占位阶段）。</summary>
    public static ModelAction ToAction(SemanticCategory cat) => cat switch
    {
        SemanticCategory.Wall => ModelAction.CreateWall,
        SemanticCategory.Floor or SemanticCategory.Finish => ModelAction.CreateFloor,
        SemanticCategory.Door or SemanticCategory.Window or SemanticCategory.Column => ModelAction.PlaceFamily,
        _ => ModelAction.Skip
    };

    private static bool HasParallelNeighbor(CadEntity e, IReadOnlyList<CadEntity> all)
    {
        if (e.Start is not { Length: >= 2 } || e.End is not { Length: >= 2 }) return false;
        double dx = e.End[0] - e.Start[0], dy = e.End[1] - e.Start[1];
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1) return false;
        double ux = dx / len, uy = dy / len;

        foreach (var o in all)
        {
            if (ReferenceEquals(o, e) || o.Start is not { Length: >= 2 } || o.End is not { Length: >= 2 }) continue;
            double ox = o.End[0] - o.Start[0], oy = o.End[1] - o.Start[1];
            double ol = Math.Sqrt(ox * ox + oy * oy);
            if (ol < 100) continue;
            double cos = Math.Abs((ux * ox + uy * oy) / ol);
            if (cos < 0.985) continue; // 不平行
            double vx = o.Start[0] - e.Start[0], vy = o.Start[1] - e.Start[1];
            double dist = Math.Abs(vx * uy - vy * ux); // 点到平行线距离
            if (dist is > 20 and < 400) return true;   // 20~400mm ≈ 双线墙厚度
        }
        return false;
    }

    private static string ExtractBlockName(string geometry)
    {
        if (string.IsNullOrEmpty(geometry) || !geometry.StartsWith("insert(block=", StringComparison.OrdinalIgnoreCase))
            return "";
        int start = "insert(block=".Length;
        int end = geometry.IndexOf(' ', start);
        return end > start ? geometry.Substring(start, end - start) : "";
    }
}
