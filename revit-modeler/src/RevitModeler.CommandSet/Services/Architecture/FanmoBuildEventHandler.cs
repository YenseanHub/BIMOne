using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPSDK.API.Interfaces;
using RevitModeler.CommandSet.Models.Architecture;
using RevitModeler.CommandSet.Models.Common;
using RevitModeler.CommandSet.Utils;

namespace RevitModeler.CommandSet.Services.Architecture;

/// <summary>
/// 翻模建模事件处理器：在 Revit UI 线程执行 IR 输入 → Transaction 内建墙/楼板/族占位 → AIResult 返回。
/// 墙厚/墙类型、楼板厚度、族名均来自 commands.json，不再一律使用默认类型。
/// </summary>
public class FanmoBuildEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
{
    private UIApplication _uiApp;
    private UIDocument UiDoc => _uiApp.ActiveUIDocument;
    private Document Doc => UiDoc.Document;

    private readonly ManualResetEvent _resetEvent = new(false);

    public List<FanmoElement> Input { get; private set; }
    public AIResult<List<int>> Result { get; private set; }

    private readonly List<string> _warnings = new();

    /// <summary>建模后是否把活动视图缩放到模型。CAD 真实坐标常在原点百米之外，不缩放就“看起来什么都没建”。</summary>
    public bool ZoomToModel { get; private set; } = true;

    /// <summary>
    /// 坐标模式：keep = 保留 CAD 真实坐标（验证/对位用）；normalize = 整体平移到项目原点附近（人看模型用）。
    /// 图纸常带大地坐标，模型会落在离原点上百米处，默认视图根本看不到。
    /// </summary>
    public string OriginMode { get; private set; } = "keep";

    public void SetParameters(List<FanmoElement> data, bool zoomToModel = true, string originMode = "keep")
    {
        Input = data;
        ZoomToModel = zoomToModel;
        OriginMode = string.IsNullOrWhiteSpace(originMode) ? "keep" : originMode.Trim().ToLowerInvariant();
        _resetEvent.Reset();
    }

    public void Execute(UIApplication uiapp)
    {
        _uiApp = uiapp;
        var elementIds = new List<int>();
        try
        {
            _warnings.Clear();
            if (Input is null || Input.Count == 0)
                throw new ArgumentException("翻模输入为空");

            using var tx = QuietTransaction.Begin(Doc, "CAD翻模-完整建模");
            foreach (var item in Input)
            {
                var level = ResolveLevel(item);
                if (level is null)
                {
                    _warnings.Add($"[{item.Handle}] 找不到可用标高，跳过");
                    continue;
                }

                ElementId id = item.Category switch
                {
                    "Wall" or "墙" => CreateWall(item, level),
                    "Floor" or "楼板" => CreateFloor(item, level),
                    "Column" or "柱" => CreateColumn(item, level),
                    "Beam" or "梁" => CreateBeam(item, level),
                    "Door" or "Window" or "门" or "窗" => CreateFamilyInstance(item, level),
                    _ => CreateFamilyInstance(item, level)
                };
                if (id is not null) elementIds.Add(id.IntegerValue);
            }
            tx.Commit();

            // 坐标归一化：整体平移（不改建模逻辑，相对关系与厚度全部保留）
            if (OriginMode == "normalize") NormalizeToOrigin(elementIds);

            // 视图操作必须在事务外做，且要放在提交之后，否则缩放的是提交前的旧几何
            if (ZoomToModel)
            {
                var note = ViewNavigator.Focus(Doc, UiDoc, elementIds.Select(id => new ElementId(id)).ToList());
                if (note.StartsWith("定位失败") || note.StartsWith("定位跳过"))
                    _warnings.Add($"[VIEW] {note}");
            }

            Result = new AIResult<List<int>>
            {
                Success = true,
                Message = BuildMessage(elementIds.Count),
                Response = elementIds
            };
        }
        catch (Exception ex)
        {
            Result = new AIResult<List<int>>
            {
                Success = false,
                Message = $"翻模建模失败: {ex.Message}"
            };
        }
        finally
        {
            _resetEvent.Set();
        }
    }

    /// <summary>
    /// 把新建构件整体平移到项目原点附近（对齐包围盒中心到原点，Z 不动）。
    /// 用 ElementTransformUtils 一次性平移，墙/柱/梁/板的相对位置和截面都不受影响。
    /// </summary>
    private void NormalizeToOrigin(List<int> elementIds)
    {
        try
        {
            var elements = elementIds.Select(id => Doc.GetElement(new ElementId(id)))
                .Where(e => e is not null).ToList();
            if (elements.Count == 0) return;

            XYZ min = null, max = null;
            foreach (var e in elements)
            {
                var bb = e.get_BoundingBox(null);
                if (bb is null) continue;
                min = min is null
                    ? bb.Min
                    : new XYZ(System.Math.Min(min.X, bb.Min.X), System.Math.Min(min.Y, bb.Min.Y), System.Math.Min(min.Z, bb.Min.Z));
                max = max is null
                    ? bb.Max
                    : new XYZ(System.Math.Max(max.X, bb.Max.X), System.Math.Max(max.Y, bb.Max.Y), System.Math.Max(max.Z, bb.Max.Z));
            }
            if (min is null || max is null) return;

            // 只平移 XY：Z 保持原标高关系，避免楼层跑位
            var dx = -(min.X + max.X) * 0.5;
            var dy = -(min.Y + max.Y) * 0.5;
            if (System.Math.Abs(dx) < 1e-6 && System.Math.Abs(dy) < 1e-6) return;

            using var tx = QuietTransaction.Begin(Doc, "CAD翻模-对齐到项目原点");
            ElementTransformUtils.MoveElements(Doc, elements.Select(e => e.Id).ToList(), new XYZ(dx, dy, 0));
            tx.Commit();

            _warnings.Add($"[ORIGIN] 已平移到项目原点附近：Δ({dx * 304.8:F0}, {dy * 304.8:F0}) mm");
        }
        catch (Exception ex)
        {
            _warnings.Add($"[ORIGIN] 平移失败: {ex.Message}");
        }
    }

    private Level ResolveLevel(FanmoElement item)
    {
        var nearest = DocumentUtils.FindNearestLevel(Doc, item.BaseLevel);
        if (nearest is not null) return nearest;
        return new FilteredElementCollector(Doc)
            .OfClass(typeof(Level)).Cast<Level>().FirstOrDefault();
    }

    private ElementId CreateWall(FanmoElement item, Level level)
    {
        WallType wallType = DocumentUtils.ResolveWallType(
            Doc, item.TypeId, item.WallTypeName, item.WallThicknessMm, out var warning);
        if (wallType is null)
        {
            _warnings.Add($"[{item.Handle}] 无可用墙类型，跳过" + (warning is null ? "" : $"（{warning}）"));
            return null;
        }
        if (warning is not null) _warnings.Add($"[{item.Handle}] {warning}");

        Wall wall = Wall.Create(Doc, ToLine(item), wallType.Id, level.Id,
            CadUnits.ToFeet(item.Height), 0, false, false);
        if (wall is not null && item.WallThicknessMm > 0)
            WarnIfThicknessMismatch(item, wallType.Name, item.WallThicknessMm, wall.WallType);
        return wall?.Id;
    }

    private void WarnIfThicknessMismatch(FanmoElement item, string typeName, double targetMm, WallType actualType)
    {
        double actualMm = DocumentUtils.GetWallWidthMm(actualType);
        if (actualMm > 0 && Math.Abs(actualMm - targetMm) > 2)
            _warnings.Add($"[{item.Handle}] 墙厚目标 {targetMm:F0}mm，实际类型 {typeName} 为 {actualMm:F0}mm");
    }

    private ElementId CreateFloor(FanmoElement item, Level level)
    {
        var loop = new CurveLoop();
        var pts = item.Points;
        if (pts is not null && pts.Length >= 3)
        {
            for (int i = 0; i < pts.Length; i++)
                loop.Append(Line.CreateBound(CadUnits.FromMm(pts[i]), CadUnits.FromMm(pts[(i + 1) % pts.Length])));
        }
        else
        {
            var s = CadUnits.ToXyz(item.Start);
            var e = CadUnits.ToXyz(item.End ?? new[] { item.Start[0] + 1000, item.Start[1], item.Start[2] });
            var off = new XYZ(0, CadUnits.ToFeet(1000), 0);
            loop.Append(Line.CreateBound(s, e));
            loop.Append(Line.CreateBound(e, e + off));
            loop.Append(Line.CreateBound(e + off, s + off));
            loop.Append(Line.CreateBound(s + off, s));
        }
        var loops = new List<CurveLoop> { loop };

        FloorType floorType = DocumentUtils.ResolveFloorType(Doc, item.FloorTypeName, item.FloorThicknessMm, out var warning);
        if (floorType is null)
        {
            _warnings.Add($"[{item.Handle}] 无可用楼板类型，跳过" + (warning is null ? "" : $"（{warning}）"));
            return null;
        }
        if (warning is not null) _warnings.Add($"[{item.Handle}] {warning}");

        Floor floor = Floor.Create(Doc, loops, floorType.Id, level.Id);
        DocumentUtils.TrySetFloorThickness(floor, item.FloorThicknessMm);

        double actualMm = DocumentUtils.GetFloorThicknessMm(floorType);
        if (item.FloorThicknessMm > 0 && Math.Abs(actualMm - item.FloorThicknessMm) > 2)
            _warnings.Add($"[{item.Handle}] 板厚目标 {item.FloorThicknessMm:F0}mm，实际 {actualMm:F0}mm（{floorType.Name}）");
        return floor?.Id;
    }

    /// <summary>结构柱：截面来自 CAD 柱块缩放（b×h），落在族类型参数上并回读校验。</summary>
    private ElementId CreateColumn(FanmoElement item, Level level)
    {
        var symbol = DocumentUtils.ResolveSectionSymbol(Doc, BuiltInCategory.OST_StructuralColumns,
            "BIMOne_Column", item.SectionBMm, item.SectionHMm, out var note);
        if (symbol is null)
        {
            _warnings.Add($"[{item.Handle}] {note}，跳过柱");
            return null;
        }

        double[] p = item.Start ?? new double[] { 0, 0, 0 };
        try
        {
            var column = Doc.Create.NewFamilyInstance(
                CadUnits.ToXyz(p), symbol, level,
                Autodesk.Revit.DB.Structure.StructuralType.Column);
            if (column is not null && item.Height > 0)
                DocumentUtils.TrySetTopOffset(column, item.Height);
            return column?.Id;
        }
        catch (Exception ex)
        {
            _warnings.Add($"[{item.Handle}] 建柱失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>结构梁：沿墙中线布置，截面默认 300×700，梁顶对齐层高（板底）。</summary>
    private ElementId CreateBeam(FanmoElement item, Level level)
    {
        var symbol = DocumentUtils.ResolveSectionSymbol(Doc, BuiltInCategory.OST_StructuralFraming,
            "BIMOne_Beam", item.SectionBMm, item.SectionHMm, out var note);
        if (symbol is null)
        {
            _warnings.Add($"[{item.Handle}] {note}，跳过梁");
            return null;
        }

        try
        {
            var curve = ToLine(item);
            var beam = Doc.Create.NewFamilyInstance(curve, symbol, level,
                Autodesk.Revit.DB.Structure.StructuralType.Beam);
            if (beam is not null && item.ElevationMm > 0)
                DocumentUtils.TrySetVerticalOffset(beam, item.ElevationMm);
            return beam?.Id;
        }
        catch (Exception ex)
        {
            _warnings.Add($"[{item.Handle}] 建梁失败: {ex.Message}");
            return null;
        }
    }

    private ElementId CreateFamilyInstance(FanmoElement item, Level level)
    {
        var symbol = DocumentUtils.ResolveFamilySymbol(Doc, item.FamilyName);
        if (symbol is null)
        {
            _warnings.Add($"[{item.Handle}] 未找到族类型 {item.FamilyName}，跳过");
            return null;
        }

        double[] p = item.Start ?? item.Points?.FirstOrDefault() ?? new double[] { 0, 0, 0 };
        try
        {
            var instance = Doc.Create.NewFamilyInstance(
                CadUnits.ToXyz(p), symbol, level,
                Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
            DocumentUtils.TrySetFamilyWidth(instance, item.WidthMm);
            return instance?.Id;
        }
        catch (Exception ex)
        {
            _warnings.Add($"[{item.Handle}] 族实例 {item.FamilyName} 放置失败: {ex.Message}");
            return null;
        }
    }

    private static Line ToLine(FanmoElement item) => Line.CreateBound(
        CadUnits.ToXyz(item.Start),
        CadUnits.ToXyz(item.End ?? new[] { item.Start[0] + 1000, item.Start[1], item.Start[2] }));

    private string BuildMessage(int count)
    {
        string msg = $"成功创建 {count} 个构件";
        if (_warnings.Count > 0) msg += "\n⚠ " + string.Join("\n  • ", _warnings);
        return msg;
    }

    public bool WaitForCompletion(int timeoutMilliseconds = 600000)
    {
        // SetParameters has already reset the event. Resetting here would race with
        // a fast Execute() and lose the completion signal.
        return _resetEvent.WaitOne(timeoutMilliseconds);
    }

    public string GetName() => "CAD翻模完整建模";
}
