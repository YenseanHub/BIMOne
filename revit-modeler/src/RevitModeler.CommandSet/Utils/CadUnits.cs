using Autodesk.Revit.DB;

namespace RevitModeler.CommandSet.Utils;

/// <summary>单位与坐标转换：CAD 侧统一 mm，Revit API 用 ft。单点收口避免散落 /304.8。</summary>
/// <remarks>命名为 CadUnits 而非 UnitUtils，避免与 Autodesk.Revit.DB.UnitUtils 撞名。</remarks>
public static class CadUnits
{
    public const double MmPerFoot = 304.8;

    /// <summary>mm 三元组 → Revit XYZ（ft）。</summary>
    public static XYZ FromMm(double[] p) =>
        new(p[0] / MmPerFoot, p[1] / MmPerFoot, p.Length > 2 ? p[2] / MmPerFoot : 0);

    /// <summary>可空 mm 三元组 → XYZ，空值兜底到原点。</summary>
    public static XYZ ToXyz(double[] p) => FromMm(p ?? new[] { 0d, 0d, 0d });

    /// <summary>mm → ft。</summary>
    public static double ToFeet(double mm) => mm / MmPerFoot;

    /// <summary>ft → mm。</summary>
    public static double ToMillimeters(double ft) => ft * MmPerFoot;
}
