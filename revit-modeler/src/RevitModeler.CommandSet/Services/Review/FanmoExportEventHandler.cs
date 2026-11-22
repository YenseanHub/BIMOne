using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPSDK.API.Interfaces;
using RevitModeler.CommandSet.Models.Common;
using RevitModeler.CommandSet.Utils;

namespace RevitModeler.CommandSet.Services.Review;

/// <summary>导出当前文档中 BIMOne 墙/楼板的真实几何，供精度校验使用。</summary>
public class FanmoExportEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
{
    private UIApplication _uiApp;
    private Document Doc => _uiApp.ActiveUIDocument.Document;
    private readonly ManualResetEvent _resetEvent = new(false);

    public AIResult<object> Result { get; private set; }

    public void SetParameters() => _resetEvent.Reset();

    public void Execute(UIApplication uiapp)
    {
        _uiApp = uiapp;
        try
        {
            var walls = new FilteredElementCollector(Doc)
                .OfClass(typeof(Wall)).Cast<Wall>()
                .Select(w => new
                {
                    id = w.Id.Value,
                    typeName = w.WallType?.Name ?? "",
                    thicknessMm = ReadWallWidthMm(w),
                    start = ToMm((w.Location as LocationCurve)?.Curve?.GetEndPoint(0)),
                    end = ToMm((w.Location as LocationCurve)?.Curve?.GetEndPoint(1)),
                    // 墙高：bbox 是几何实测值（顶层约束/连接后仍然可信），参数仅在可读时给参考
                    heightMm = ReadWallHeightMm(w),
                    baseElevMm = ReadWallBaseMm(w),
                    unconnectedHeightMm = ReadWallUnconnectedHeightMm(w)
                })
                .Where(w => w.start is not null && w.end is not null)
                .ToList();

            var floors = new FilteredElementCollector(Doc)
                .OfClass(typeof(Floor)).Cast<Floor>()
                .Select(f => new
                {
                    id = f.Id.Value,
                    typeName = f.FloorType?.Name ?? "",
                    thicknessMm = f.FloorType is null ? -1 : DocumentUtils.GetFloorThicknessMm(f.FloorType),
                    bbox = ToMm(f.get_BoundingBox(null))
                })
                .Where(f => f.bbox is not null)
                .ToList();

            // 结构柱/梁：只导出实际建成的实例（族名含 BIMOne 或类别命中），用于截面精度校验
            var columns = new FilteredElementCollector(Doc)
                .OfCategory(BuiltInCategory.OST_StructuralColumns)
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>()
                .Select(c => new
                {
                    id = c.Id.Value,
                    typeName = c.Symbol?.Name ?? "",
                    familyName = c.Symbol?.FamilyName ?? "",
                    bMm = ReadSectionMm(c.Symbol, true),
                    hMm = ReadSectionMm(c.Symbol, false),
                    bbox = ToMm(c.get_BoundingBox(null))
                })
                .Where(c => c.bbox is not null)
                .ToList();

            var beams = new FilteredElementCollector(Doc)
                .OfCategory(BuiltInCategory.OST_StructuralFraming)
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>()
                .Select(b => new
                {
                    id = b.Id.Value,
                    typeName = b.Symbol?.Name ?? "",
                    bMm = ReadSectionMm(b.Symbol, true),
                    hMm = ReadSectionMm(b.Symbol, false),
                    start = ToMm((b.Location as LocationCurve)?.Curve?.GetEndPoint(0)),
                    end = ToMm((b.Location as LocationCurve)?.Curve?.GetEndPoint(1)),
                    bbox = ToMm(b.get_BoundingBox(null))
                })
                .Where(b => b.bbox is not null)
                .ToList();

            Result = new AIResult<object>
            {
                Success = true,
                Message = $"几何导出完成: 墙 {walls.Count} / 楼板 {floors.Count} / 柱 {columns.Count} / 梁 {beams.Count}",
                Response = new { walls, floors, columns, beams }
            };
        }
        catch (Exception ex)
        {
            Result = new AIResult<object> { Success = false, Message = $"几何导出失败: {ex.Message}" };
        }
        finally
        {
            _resetEvent.Set();
        }
    }

    private static double ReadWallWidthMm(Wall wall)
    {
        try
        {
            // 中文版 Revit 内置参数名为“宽度”，必须走 BuiltInParameter 才能读到
            var p = wall.WallType?.get_Parameter(BuiltInParameter.WALL_ATTR_WIDTH_PARAM);
            return p is not null && p.HasValue ? CadUnits.ToMillimeters(p.AsDouble()) : -1;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>墙高(mm)：用包围盒 Z 跨度做几何实测，不受顶部约束方式影响。</summary>
    private static double ReadWallHeightMm(Wall wall)
    {
        try
        {
            var bb = wall.get_BoundingBox(null);
            return bb is null ? -1 : CadUnits.ToMillimeters(bb.Max.Z - bb.Min.Z);
        }
        catch { return -1; }
    }

    /// <summary>墙底标高(mm)。</summary>
    private static double ReadWallBaseMm(Wall wall)
    {
        try
        {
            var bb = wall.get_BoundingBox(null);
            return bb is null ? -1 : CadUnits.ToMillimeters(bb.Min.Z);
        }
        catch { return -1; }
    }

    /// <summary>“无连接高度”参数值(mm)，仅作参考；顶部约束到标高时可能读不到。</summary>
    private static double ReadWallUnconnectedHeightMm(Wall wall)
    {
        try
        {
            var p = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM);
            return p is not null && p.HasValue ? CadUnits.ToMillimeters(p.AsDouble()) : -1;
        }
        catch { return -1; }
    }

    private static double[] ToMm(XYZ p) =>
        p is null ? null : new[] { CadUnits.ToMillimeters(p.X), CadUnits.ToMillimeters(p.Y), CadUnits.ToMillimeters(p.Z) };

    private static double[] ToMm(BoundingBoxXYZ bb) =>
        bb is null ? null : new[]
        {
            CadUnits.ToMillimeters(bb.Min.X), CadUnits.ToMillimeters(bb.Min.Y), CadUnits.ToMillimeters(bb.Min.Z),
            CadUnits.ToMillimeters(bb.Max.X), CadUnits.ToMillimeters(bb.Max.Y), CadUnits.ToMillimeters(bb.Max.Z)
        };

    /// <summary>读取族类型的截面 b/h(mm)；width=true 取 b，否则取 h。</summary>
    private static double ReadSectionMm(FamilySymbol symbol, bool width)
    {
        if (symbol is null) return -1;
        string[] names = width
            ? new[] { "b", "B", "宽", "宽度", "截面宽", "截面宽度" }
            : new[] { "h", "H", "高", "高度", "截面高", "截面高度", "深度" };
        var p = names.Select(n => symbol.LookupParameter(n)).FirstOrDefault(x => x is not null);
        return p is not null && p.HasValue ? CadUnits.ToMillimeters(p.AsDouble()) : -1;
    }

    public bool WaitForCompletion(int timeoutMilliseconds = 600000) =>
        _resetEvent.WaitOne(timeoutMilliseconds);

    public string GetName() => "CAD翻模几何导出";
}
