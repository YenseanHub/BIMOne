using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;

namespace CadReader.Core;

/// <summary>翻模命令生成选项：先分图纸、再识别构件尺寸，缺证据时才用默认值。</summary>
public sealed class FanmoBuildOptions
{
    /// <summary>指定图纸（匹配块名或图纸标题，如“一层平面图”）。为空则自动挑选。</summary>
    public string SheetFilter { get; set; } = "";

    /// <summary>开启柱识别（COLUMN 图层块缩放/闭合轮廓）。</summary>
    public bool IncludeColumns { get; set; } = true;

    /// <summary>开启梁生成（无梁图层时按墙中线 + 默认截面）。</summary>
    public bool IncludeBeams { get; set; } = true;

    /// <summary>图纸未标注板厚时的默认值(mm)。</summary>
    public double DefaultFloorThicknessMm { get; set; } = 120;

    /// <summary>梁宽默认值(mm)，图纸无梁标注时使用。</summary>
    public double DefaultBeamWidthMm { get; set; } = 300;

    /// <summary>梁高默认值(mm)。</summary>
    public double DefaultBeamHeightMm { get; set; } = 700;

    /// <summary>层高(mm)：墙高与梁顶标高按此推算。</summary>
    public double StoreyHeightMm { get; set; } = 3600;
}

/// <summary>一次识别结果的可追溯摘要（写入 stderr / 报告，便于人工复核）。</summary>
public sealed class FanmoBuildReport
{
    public string Sheet { get; set; } = "";
    public string SheetTitle { get; set; } = "";
    public int Candidates { get; set; }
    public Dictionary<string, string> Sources { get; } = new();
    public List<string> Notes { get; } = new();
}

/// <summary>
/// CAD IR → 完整翻模命令。独立于 RevitAPI，只输出与 commands.json 契约一致的 DTO。
/// 流程：图纸分区 → 图层语义分类 → 墙(双线)/柱(块截面)/板(标注厚度)/梁(图层或默认)。
/// </summary>
public static class FanmoCommandBuilder
{
    private static readonly HashSet<string> IgnoredLayers = new(StringComparer.OrdinalIgnoreCase)
    {
        "DIM", "标注", "家俱", "家俱1"
    };

    // 明确非结构构件的图层关键词（任何模式下都排除，避免楼梯/标注/窗被误建成墙）
    private static readonly string[] ExcludeTokens =
    {
        "STAIR", "梯", "DIM", "标注", "DOTE", "AXIS", "轴线", "TEXT", "文字",
        "TITLE", "图框", "HATCH", "填充", "WINDOW", "窗", "FURNITURE", "家具", "家俱",
        "STREET", "街", "GROUND", "场地", "说明", "DEFPOINTS", "ROOF"
    };

    private static readonly string[] WallTokens = { "WALL", "墙" };
    private static readonly string[] ColumnTokens = { "COLUMN", "柱" };
    private static readonly string[] BeamTokens = { "BEAM", "梁" };


    static void SetSource(FanmoBuildReport? report, string key, string value)
    {
        if (report is not null) report.Sources[key] = value;
    }

    static void Note(FanmoBuildReport? report, string text) => report?.Notes.Add(text);

    static bool HasAny(string layer, string[] tokens) =>
        !string.IsNullOrEmpty(layer) && tokens.Any(t => layer.Contains(t, StringComparison.OrdinalIgnoreCase));

    static bool IsExcludedLayer(string layer) => HasAny(layer, ExcludeTokens);

    static bool IsWallLayer(string layer) => HasAny(layer, WallTokens) && !IsExcludedLayer(layer);

    /// <summary>柱图层：只认 COLUMN/柱，且排除窗、墙等含 COLUMN 的复合名误判。</summary>
    static bool IsColumnLayer(string layer) =>
        HasAny(layer, ColumnTokens) && !HasAny(layer, new[] { "WINDOW", "窗", "WALL", "墙" });

    static bool IsBeamLayer(string layer) => HasAny(layer, BeamTokens) && !IsExcludedLayer(layer);

    public static List<FanmoCommand> Build(IrDocument ir) => Build(ir, new FanmoBuildOptions());

    public static string ToJson(IEnumerable<FanmoCommand> commands) =>
        JsonSerializer.Serialize(commands, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });

    public static List<FanmoCommand> Build(IrDocument ir, FanmoBuildOptions options, FanmoBuildReport? report = null)
    {
        var scope = SelectSheet(ir, options, report);
        var commands = new List<FanmoCommand>();

        var walls = BuildWalls(scope, options);
        commands.AddRange(walls);

        if (options.IncludeColumns)
            commands.AddRange(BuildColumns(scope, options, report));

        if (options.IncludeBeams)
            commands.AddRange(BuildBeams(scope, walls, options, report));

        var floorThickness = ResolveFloorThickness(ir, options, report);
        var floor = BuildFloor(walls, floorThickness);
        if (floor is not null) commands.Add(floor);

        commands.AddRange(BuildFamilies(scope));
        return commands;
    }

    /// <summary>
    /// 图纸分区选择：整套施工图的平面图/立面图/大样混排，必须只翻一张可建平面，
    /// 否则会把大样、楼梯、总图的线全部误建成墙。未指定时按“墙图层线条数最多且非大样”自动挑选。
    /// </summary>
    private static List<IrEntity> SelectSheet(IrDocument ir, FanmoBuildOptions options, FanmoBuildReport? report)
    {
        var withSheet = ir.Entities.Where(e => !string.IsNullOrEmpty(e.Sheet)).ToList();
        if (withSheet.Count == 0)
        {
            // 单图 DWG（如户型图）：整张就是一张平面
            SetSource(report, "sheet", "single");
            return ir.Entities;
        }

        var groups = withSheet.GroupBy(e => e.Sheet).ToList();

        if (!string.IsNullOrWhiteSpace(options.SheetFilter))
        {
            var hit = groups.FirstOrDefault(g =>
                g.Key.Contains(options.SheetFilter, StringComparison.OrdinalIgnoreCase) ||
                (g.FirstOrDefault()?.SheetTitle ?? "").Contains(options.SheetFilter, StringComparison.OrdinalIgnoreCase));
            if (hit is not null)
            {
                Note(report, $"指定图纸: {hit.Key} ({hit.FirstOrDefault()?.SheetTitle})");
                if (report is not null) report.Sheet = hit.Key;
                if (report is not null) report.SheetTitle = hit.FirstOrDefault()?.SheetTitle ?? "";
                return hit.ToList();
            }
            Note(report, $"未找到图纸“{options.SheetFilter}”，改为自动挑选");
        }

        var scored = groups.Select(g =>
        {
            var title = g.FirstOrDefault()?.SheetTitle ?? "";
            int wallLines = g.Count(e => e.Type == "Line" && IsWallLayer(e.Layer));
            int colEnts = g.Count(e => IsColumnLayer(e.Layer));
            bool detail = Regex.IsMatch(title, "大样|详图|卫生间|楼梯|节点");
            bool elevation = Regex.IsMatch(title, "立面|剖面") || g.Any(e => e.Layer.StartsWith("E_WALL", StringComparison.OrdinalIgnoreCase));
            bool roof = title.Contains("屋顶") || title.Contains("水箱");
            int score = wallLines + colEnts * 3 - (detail ? 2000 : 0) - (elevation ? 3000 : 0) - (roof ? 500 : 0);
            return new { g, title, wallLines, colEnts, score, detail, elevation, roof };
        }).OrderByDescending(x => x.score).ToList();

        var best = scored.First();
        SetSource(report, "sheet", $"{best.g.Key} · {(string.IsNullOrEmpty(best.title) ? "未标注" : best.title)}");
        Note(report, $"共 {groups.Count} 张图纸，自动选用墙线最多的一张：{best.g.Key} / {best.title}（墙线 {best.wallLines}，柱 {best.colEnts}）");
        if (report is not null)
        {
            report.Sheet = best.g.Key;
            report.SheetTitle = best.title;
            report.Candidates = groups.Count;
        }
        return best.g.ToList();
    }

    private static List<FanmoCommand> BuildWalls(IReadOnlyList<IrEntity> scope, FanmoBuildOptions options)
    {
        // 图纸分类：若存在明确“墙”图层（如 WALL / S_WALL / PUB_WALL），只在这些图层里找墙，避免楼梯/总图/详图被误建；
        // 否则（图层名全是 0/数字的图纸）退回全图几何兜底。两种模式都排除楼梯/标注/窗等非结构图层。
        bool hasWallLayer = scope.Any(e => IsWallLayer(e.Layer));
        var lines = scope.Where(e =>
                e.Type == "Line" && e.Start is { Length: >= 3 } && e.End is { Length: >= 3 } &&
                Math.Abs(e.Z) < 1e-6 &&
                !IgnoredLayers.Contains(e.Layer) && !IsExcludedLayer(e.Layer) &&
                (!hasWallLayer || IsWallLayer(e.Layer)))
            .Select(e =>
            {
                double dx = e.End[0] - e.Start[0];
                double dy = e.End[1] - e.Start[1];
                double len = Math.Sqrt(dx * dx + dy * dy);
                return new
                {
                    Entity = e,
                    Length = len,
                    Horizontal = Math.Abs(dy) <= Math.Abs(dx) * 0.02,
                    Vertical = Math.Abs(dx) <= Math.Abs(dy) * 0.02
                };
            })
            .Where(x => x.Length >= 600 && (x.Horizontal || x.Vertical))
            .ToList();

        var candidates = new List<WallCandidate>();
        for (int i = 0; i < lines.Count; i++)
        {
            for (int j = i + 1; j < lines.Count; j++)
            {
                var a = lines[i];
                var b = lines[j];
                if (a.Horizontal != b.Horizontal || a.Vertical != b.Vertical) continue;

                bool horizontal = a.Horizontal && b.Horizontal;
                double ca = horizontal ? a.Entity.Start[1] : a.Entity.Start[0];
                double cb = horizontal ? b.Entity.Start[1] : b.Entity.Start[0];
                double separation = Math.Abs(ca - cb);
                double snappedThickness = SnapThickness(separation);
                if (snappedThickness <= 0) continue;

                double aLo = horizontal
                    ? Math.Min(a.Entity.Start[0], a.Entity.End[0])
                    : Math.Min(a.Entity.Start[1], a.Entity.End[1]);
                double aHi = horizontal
                    ? Math.Max(a.Entity.Start[0], a.Entity.End[0])
                    : Math.Max(a.Entity.Start[1], a.Entity.End[1]);
                double bLo = horizontal
                    ? Math.Min(b.Entity.Start[0], b.Entity.End[0])
                    : Math.Min(b.Entity.Start[1], b.Entity.End[1]);
                double bHi = horizontal
                    ? Math.Max(b.Entity.Start[0], b.Entity.End[0])
                    : Math.Max(b.Entity.Start[1], b.Entity.End[1]);
                // 墙段范围：双线在转角处是「包角」画法，两条边线端点关于中心线端点对称
                // （一条伸到外角、一条止于内角），因此取两条线端点的中点才等于真实中心线端点。
                // 实测：交集端点系统性短半墙厚、并集长半墙厚，中点与 Revit 落位一致。
                // 单端差异超过 1.5 倍墙厚时视为误配对，退回交集保守处理。
                double loMin = Math.Min(aLo, bLo), loMax = Math.Max(aLo, bLo);
                double hiMin = Math.Min(aHi, bHi), hiMax = Math.Max(aHi, bHi);
                double maxEndDelta = snappedThickness * 1.5;
                double lo = (loMax - loMin) <= maxEndDelta ? (loMin + loMax) / 2 : loMax;
                double hi = (hiMax - hiMin) <= maxEndDelta ? (hiMin + hiMax) / 2 : hiMin;
                if (hi - lo < 600) continue;

                double coord = (ca + cb) / 2;
                candidates.Add(new WallCandidate
                {
                    Horizontal = horizontal,
                    Coord = coord,
                    Lo = lo,
                    Hi = hi,
                    Thickness = snappedThickness,
                    Layer = a.Entity.Layer,
                    Handle = $"{a.Entity.Handle}/{b.Entity.Handle}",
                    Confidence = Math.Min(a.Length, b.Length)
                });
            }
        }

        var merged = new List<WallCandidate>();
        var groups = candidates.GroupBy(c => new
        {
            c.Horizontal,
            Bucket = (int)Math.Round(c.Coord / 20.0)
        });
        foreach (var group in groups)
        {
            var ordered = group.OrderBy(c => c.Lo).ToList();
            WallCandidate? current = null;
            foreach (var c in ordered)
            {
                if (current is null)
                {
                    current = Clone(c);
                    continue;
                }
                if (c.Lo <= current.Hi + 150)
                {
                    current.Hi = Math.Max(current.Hi, c.Hi);
                    if (c.Confidence > current.Confidence)
                    {
                        current.Thickness = c.Thickness;
                        current.Layer = c.Layer;
                        current.Handle = c.Handle;
                        current.Confidence = c.Confidence;
                    }
                }
                else
                {
                    merged.Add(current);
                    current = Clone(c);
                }
            }
            if (current is not null) merged.Add(current);
        }

        // 墙端点吸附到相邻墙中心线的交点：Revit 的墙连接会把端点拉到交点，
        // 若命令端点不先对齐，导出后必然出现半墙厚级的系统性偏移。
        SnapWallEndsToIntersections(merged);

        return merged.Where(w => w.Hi - w.Lo >= 800)
            .OrderByDescending(w => w.Hi - w.Lo)
            .Select((w, index) => new FanmoCommand
            {
                Handle = $"W{index + 1:000}_{w.Handle}",
                Category = "Wall",
                Start = w.Horizontal ? new[] { w.Lo, w.Coord, 0d } : new[] { w.Coord, w.Lo, 0d },
                End = w.Horizontal ? new[] { w.Hi, w.Coord, 0d } : new[] { w.Coord, w.Hi, 0d },
                Points = null,
                BaseLevel = 0,
                Height = options.StoreyHeightMm,
                TypeId = -1,
                Layer = w.Layer,
                Z = 0,
                WallThicknessMm = Math.Round(w.Thickness, 1),
                WallTypeName = $"BIMOne_Wall_{Math.Round(w.Thickness)}mm",
                Source = "cad:double-line"
            })
            .ToList();
    }

    /// <summary>
    /// 把墙段端点吸附到相交墙的中心线交点。
    ///
    /// 背景：CAD 墙双线在转角处是包角画法，从中点推出的端点在半个墙厚内浮动；
    /// 而 Revit 的墙连接会把 LocationCurve 端点拉到相邻墙中心线的交点。
    /// 若命令端点在浮动区内不先对齐，导出几何就会出现 ±半墙厚的系统性偏移。
    /// 吸附只在「距离 ≤ 3/4 墙厚 且落在对方墙段范围内」时生效，自由端不受影响。
    /// </summary>
    private static void SnapWallEndsToIntersections(List<WallCandidate> walls)
    {
        var horizontal = walls.Where(w => w.Horizontal).ToList();
        var vertical = walls.Where(w => !w.Horizontal).ToList();

        foreach (var h in horizontal)
        {
            foreach (var v in vertical)
            {
                // 容差随「两墙厚度」变化：包角偏移量与相邻墙的厚度相关，
                // 用固定值会漏掉 120 墙接 240 墙这类跨厚度节点。
                var tolerance = Math.Max(80, (h.Thickness + v.Thickness) / 2 + 60);

                // 垂直墙的 x 落在这面水平墙的端点上，且交点在该垂直墙的 y 范围内
                if (v.Coord < h.Lo - tolerance || v.Coord > h.Hi + tolerance) continue;
                if (h.Coord < v.Lo - tolerance || h.Coord > v.Hi + tolerance) continue;

                if (Math.Abs(v.Coord - h.Lo) <= tolerance) h.Lo = v.Coord;
                if (Math.Abs(v.Coord - h.Hi) <= tolerance) h.Hi = v.Coord;
            }
        }

        foreach (var v in vertical)
        {
            foreach (var h in horizontal)
            {
                var tolerance = Math.Max(80, (h.Thickness + v.Thickness) / 2 + 60);

                if (h.Coord < v.Lo - tolerance || h.Coord > v.Hi + tolerance) continue;
                if (v.Coord < h.Lo - tolerance || v.Coord > h.Hi + tolerance) continue;

                if (Math.Abs(h.Coord - v.Lo) <= tolerance) v.Lo = h.Coord;
                if (Math.Abs(h.Coord - v.Hi) <= tolerance) v.Hi = h.Coord;
            }
        }
    }

    /// <summary>
    /// 柱识别：天正/结构软件用“单位块 + 缩放”表达截面（柱块 _FZH 为 1×1，Insert 缩放即截面尺寸），
    /// 因此直接读块引用的 ScaleX/Y 得到 b×h；也兼容柱图层上的闭合矩形。
    /// </summary>
    private static IEnumerable<FanmoCommand> BuildColumns(IReadOnlyList<IrEntity> scope, FanmoBuildOptions options, FanmoBuildReport? report)
    {
        var columns = new List<FanmoCommand>();
        foreach (var e in scope.Where(e => IsColumnLayer(e.Layer)))
        {
            if (e.Type == "Insert" && e.Center is { Length: >= 3 })
            {
                double b = Math.Abs(e.ScaleX), h = Math.Abs(e.ScaleY);
                if (b < 150 || h < 150 || b > 2000 || h > 2000) continue;
                // 单位块以插入点为中心；旋转角沿用图元
                columns.Add(MakeColumn(e, b, h, e.Center[0], e.Center[1], e.Rotation, "cad:block-scale"));
            }
            else if (e.Points is { Count: 4 } pts && e.Closed)
            {
                double b = Math.Abs(pts[0][0] - pts[2][0]);
                double h = Math.Abs(pts[0][1] - pts[2][1]);
                if (b < 150 || h < 150 || b > 2000 || h > 2000) continue;
                double cx = (pts[0][0] + pts[2][0]) / 2, cy = (pts[0][1] + pts[2][1]) / 2;
                columns.Add(MakeColumn(e, b, h, cx, cy, 0, "cad:closed-outline"));
            }
        }

        if (columns.Count > 0)
        {
            var hist = columns.GroupBy(c => $"{Math.Round(c.SectionBMm)}x{Math.Round(c.SectionHMm)}")
                .OrderByDescending(g => g.Count())
                .Select(g => $"{g.Key}×{g.Count()}");
            SetSource(report, "column", "图纸识别（" + string.Join(", ", hist.Take(5)) + "）");
        }
        else
        {
            Note(report, "未在柱图层识别到柱截面，本次不建柱（避免臆造）");
            SetSource(report, "column", "无（跳过）");
        }
        return columns;
    }

    private static FanmoCommand MakeColumn(IrEntity e, double b, double h, double cx, double cy,
        double rotationDeg, string source)
    {
        var corners = RectCorners(cx, cy, b, h, rotationDeg);
        return new FanmoCommand
        {
            Handle = $"C_{e.Handle}",
            Category = "Column",
            Start = new[] { cx, cy, 0d },
            End = null,
            Points = corners,
            BaseLevel = 0,
            Height = 3600,
            TypeId = -1,
            Layer = e.Layer,
            Z = e.Z,
            FamilyName = $"BIMOne_Column_{Math.Round(b)}x{Math.Round(h)}",
            WidthMm = b,
            SectionBMm = Math.Round(b, 1),
            SectionHMm = Math.Round(h, 1),
            RotationDeg = rotationDeg,
            Source = source
        };
    }

    private static double[][] RectCorners(double cx, double cy, double b, double h, double rotationDeg)
    {
        double r = rotationDeg * Math.PI / 180.0;
        double cos = Math.Cos(r), sin = Math.Sin(r);
        double hb = b / 2, hh = h / 2;
        var local = new[] { new[] { -hb, -hh }, new[] { hb, -hh }, new[] { hb, hh }, new[] { -hb, hh } };
        return local.Select(p => new[]
        {
            cx + p[0] * cos - p[1] * sin,
            cy + p[0] * sin + p[1] * cos,
            0d
        }).ToArray();
    }

    /// <summary>
    /// 梁识别：优先用梁图层线段；整套图常无梁图层（本图即如此），
    /// 此时按墙中线生成等截面梁（默认 300×700），在 Source 标记 default，便于人工复核。
    /// </summary>
    private static IEnumerable<FanmoCommand> BuildBeams(IReadOnlyList<IrEntity> scope, IReadOnlyList<FanmoCommand> walls,
        FanmoBuildOptions options, FanmoBuildReport? report)
    {
        var beams = new List<FanmoCommand>();
        bool hasBeamLayer = scope.Any(e => IsBeamLayer(e.Layer));
        if (hasBeamLayer)
        {
            var lines = scope.Where(e => e.Type == "Line" && IsBeamLayer(e.Layer) &&
                                         e.Start is { Length: >= 3 } && e.End is { Length: >= 3 }).ToList();
            int i = 0;
            foreach (var e in lines)
            {
                double len = Math.Sqrt(Math.Pow(e.End![0] - e.Start![0], 2) + Math.Pow(e.End[1] - e.Start[1], 2));
                if (len < 600) continue;
                beams.Add(new FanmoCommand
                {
                    Handle = $"B{++i:000}_{e.Handle}",
                    Category = "Beam",
                    Start = new[] { e.Start[0], e.Start[1], 0d },
                    End = new[] { e.End[0], e.End[1], 0d },
                    BaseLevel = 0,
                    Height = options.StoreyHeightMm,
                    TypeId = -1,
                    Layer = e.Layer,
                    Z = 0,
                    SectionBMm = options.DefaultBeamWidthMm,
                    SectionHMm = options.DefaultBeamHeightMm,
                    FamilyName = $"BIMOne_Beam_{options.DefaultBeamWidthMm}x{options.DefaultBeamHeightMm}",
                    ElevationMm = options.StoreyHeightMm - options.DefaultBeamHeightMm,
                    Source = "cad:beam-layer"
                });
            }
            SetSource(report, "beam", $"梁图层（{beams.Count} 段）");
            return beams;
        }

        // 无梁图层：沿墙中线生成同截面梁，梁顶对齐层高（板底）
        int k = 0;
        foreach (var w in walls)
        {
            double len = Math.Sqrt(Math.Pow(w.End![0] - w.Start![0], 2) + Math.Pow(w.End[1] - w.Start[1], 2));
            if (len < 1200) continue;
            beams.Add(new FanmoCommand
            {
                Handle = $"B{++k:000}_DEFAULT_{w.Handle}",
                Category = "Beam",
                Start = w.Start,
                End = w.End,
                BaseLevel = 0,
                Height = options.StoreyHeightMm,
                TypeId = -1,
                Layer = "INFERRED",
                Z = 0,
                SectionBMm = options.DefaultBeamWidthMm,
                SectionHMm = options.DefaultBeamHeightMm,
                FamilyName = $"BIMOne_Beam_{options.DefaultBeamWidthMm}x{options.DefaultBeamHeightMm}",
                ElevationMm = options.StoreyHeightMm - options.DefaultBeamHeightMm,
                Source = "default:wall-centerline"
            });
        }
        Note(report, $"无梁图层/梁标注：按墙中线生成 {beams.Count} 段梁，截面默认 {options.DefaultBeamWidthMm}×{options.DefaultBeamHeightMm}mm（标记 default）");
        SetSource(report, "beam", $"默认 {options.DefaultBeamWidthMm}×{options.DefaultBeamHeightMm}（沿墙中线）");
        return beams;
    }

    /// <summary>板厚：优先读图纸“板厚N”标注（全图一致时采信），否则用默认值。</summary>
    private static double ResolveFloorThickness(IrDocument ir, FanmoBuildOptions options, FanmoBuildReport? report)
    {
        var found = new List<double>();
        foreach (var e in ir.Entities)
        {
            if (string.IsNullOrWhiteSpace(e.Text)) continue;
            foreach (Match m in Regex.Matches(e.Text, @"板\s*厚\s*[:：]?\s*(\d{2,3})"))
                if (double.TryParse(m.Groups[1].Value, out var v) && v is >= 60 and <= 400)
                    found.Add(v);
        }
        if (found.Count > 0)
        {
            var mode = found.GroupBy(v => v).OrderByDescending(g => g.Count()).First();
            SetSource(report, "floorThickness", $"图纸标注 板厚{Math.Round(mode.Key)}mm（{found.Count} 处）");
            return mode.Key;
        }
        SetSource(report, "floorThickness", $"默认 {options.DefaultFloorThicknessMm}mm（无标注）");
        Note(report, $"图中无“板厚”标注，按默认 {options.DefaultFloorThicknessMm}mm 建板");
        return options.DefaultFloorThicknessMm;
    }

    private static FanmoCommand? BuildFloor(IReadOnlyList<FanmoCommand> walls, double thicknessMm)
    {
        if (walls.Count == 0) return null;
        var pts = walls.SelectMany(w => new[] { w.Start, w.End })
            .Where(p => p is { Length: >= 3 }).ToList();
        if (pts.Count == 0) return null;

        double minX = pts.Min(p => p[0]);
        double maxX = pts.Max(p => p[0]);
        double minY = pts.Min(p => p[1]);
        double maxY = pts.Max(p => p[1]);
        if (maxX - minX < 1000 || maxY - minY < 1000) return null;

        return new FanmoCommand
        {
            Handle = "FLOOR_OUTLINE",
            Category = "Floor",
            Start = null,
            End = null,
            Points = new[]
            {
                new[] { minX, minY, 0d },
                new[] { maxX, minY, 0d },
                new[] { maxX, maxY, 0d },
                new[] { minX, maxY, 0d }
            },
            BaseLevel = 0,
            Height = thicknessMm,
            TypeId = -1,
            Layer = "INFERRED",
            Z = 0,
            FloorThicknessMm = thicknessMm,
            FloorTypeName = $"BIMOne_Floor_{Math.Round(thicknessMm)}mm",
            Source = "cad:bbox+thickness-text"
        };
    }

    private static IEnumerable<FanmoCommand> BuildFamilies(IReadOnlyList<IrEntity> scope)
    {
        foreach (var e in scope.Where(e =>
                     e.Type == "Insert" && e.Center is { Length: >= 3 }))
        {
            if (IsColumnLayer(e.Layer)) continue; // 柱已按截面单独处理

            string name = string.IsNullOrWhiteSpace(e.BlockName)
                ? ExtractBlockName(e.Geometry)
                : e.BlockName;
            string category;
            double width;
            if (ContainsAny(name, "门", "DOOR"))
            {
                category = "Door";
                width = 900;
            }
            else if (ContainsAny(name, "窗", "WINDOW"))
            {
                category = "Window";
                width = 1200;
            }
            else continue;

            yield return new FanmoCommand
            {
                Handle = $"{category}_{e.Handle}",
                Category = category,
                Start = e.Center,
                End = null,
                Points = null,
                BaseLevel = 0,
                Height = 2100,
                TypeId = -1,
                Layer = e.Layer,
                Z = e.Z,
                FamilyName = name,
                WidthMm = width,
                Source = "cad:block"
            };
        }
    }

    // 覆盖常见框架/剪力墙厚（含 280 剪力墙），避免 280 被误吸收成 300
    private static readonly double[] StandardWallThicknesses =
        { 60, 80, 100, 120, 150, 180, 200, 240, 280, 300 };

    /// <summary>
    /// 墙厚取值：与标准厚度相差 ≤25mm 视为绘图误差并吸附到标准值，否则判定为非墙（返回 -1），
    /// 避免把家具/设备的平行间距误建为异常厚度墙体。
    /// </summary>
    private static double SnapThickness(double measured)
    {
        if (measured < 60 || measured > 400) return -1;
        double best = StandardWallThicknesses
            .OrderBy(t => Math.Abs(t - measured))
            .First();
        return Math.Abs(best - measured) <= 25 ? best : -1;
    }

    private static bool ContainsAny(string value, params string[] tokens) =>
        tokens.Any(t => value.Contains(t, StringComparison.OrdinalIgnoreCase));

    private static string ExtractBlockName(string geometry)
    {
        const string marker = "insert(block=";
        if (!geometry.StartsWith(marker, StringComparison.OrdinalIgnoreCase)) return "";
        int end = geometry.IndexOf(' ', marker.Length);
        return end > marker.Length ? geometry.Substring(marker.Length, end - marker.Length) : "";
    }

    private static WallCandidate Clone(WallCandidate source) => new()
    {
        Horizontal = source.Horizontal,
        Coord = source.Coord,
        Lo = source.Lo,
        Hi = source.Hi,
        Thickness = source.Thickness,
        Layer = source.Layer,
        Handle = source.Handle,
        Confidence = source.Confidence
    };

    private sealed class WallCandidate
    {
        public bool Horizontal { get; set; }
        public double Coord { get; set; }
        public double Lo { get; set; }
        public double Hi { get; set; }
        public double Thickness { get; set; }
        public string Layer { get; set; } = "";
        public string Handle { get; set; } = "";
        public double Confidence { get; set; }
    }
}

/// <summary>与 RevitModeler.CommandSet.Models.Architecture.FanmoElement 对齐的命令 DTO。</summary>
public sealed class FanmoCommand
{
    public string Handle { get; set; } = "";
    public string Category { get; set; } = "";
    public double[] Start { get; set; }
    public double[] End { get; set; }
    public double[][] Points { get; set; }
    public double BaseLevel { get; set; }
    public double Height { get; set; } = 3000;
    public int TypeId { get; set; } = -1;
    public string Layer { get; set; } = "";
    public double Z { get; set; }
    public string WallTypeName { get; set; }
    public double WallThicknessMm { get; set; } = -1;
    public string FloorTypeName { get; set; }
    public double FloorThicknessMm { get; set; } = -1;
    public string FamilyName { get; set; }
    public double WidthMm { get; set; } = -1;

    /// <summary>截面宽/高 (mm)：柱、梁用。</summary>
    public double SectionBMm { get; set; } = -1;
    public double SectionHMm { get; set; } = -1;

    /// <summary>构件底标高 (mm)：梁底=板底-梁高，用于实例 Z 偏移。</summary>
    public double ElevationMm { get; set; }

    /// <summary>来源标记：cad:* 为图纸识别，default:* 为默认值推断，供人工复核。</summary>
    public string Source { get; set; } = "";

    public double RotationDeg { get; set; }
}
