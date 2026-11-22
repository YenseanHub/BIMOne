using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitModeler.CommandSet.Utils;

/// <summary>文档查询工具：标高、墙/楼板类型查找。对齐 revit-mcp 的 Utils/ProjectUtils 职责。</summary>
public static class DocumentUtils
{
    /// <summary>找与给定标高(mm)最近的 Level。</summary>
    public static Level FindNearestLevel(Document doc, double baseLevelMm)
    {
        double targetFt = CadUnits.ToFeet(baseLevelMm);
        return new FilteredElementCollector(doc)
            .OfClass(typeof(Level)).Cast<Level>()
            .OrderBy(l => Math.Abs(l.Elevation - targetFt))
            .FirstOrDefault();
    }

    /// <summary>按 Id 取墙类型，找不到则取文档第一个墙类型。</summary>
    public static WallType ResolveWallType(Document doc, int typeId)
    {
        if (typeId > 0 && doc.GetElement(new ElementId(typeId)) is WallType wt)
            return wt;
        return new FilteredElementCollector(doc)
            .OfClass(typeof(WallType)).Cast<WallType>().FirstOrDefault();
    }

    /// <summary>取文档第一个楼板类型。</summary>
    public static FloorType ResolveFloorType(Document doc) =>
        new FilteredElementCollector(doc)
            .OfClass(typeof(FloorType)).Cast<FloorType>().FirstOrDefault();

    /// <summary>取文档第一个族符号（LOD200 精化用）。</summary>
    public static FamilySymbol ResolveFamilySymbol(Document doc, string familyName) =>
        new FilteredElementCollector(doc)
            .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
            .FirstOrDefault(fs => string.IsNullOrEmpty(familyName) ||
                                  fs.Name.Equals(familyName, StringComparison.OrdinalIgnoreCase));
}
