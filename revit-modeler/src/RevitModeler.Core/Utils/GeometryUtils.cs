namespace RevitModeler.Core.Utils;

/// <summary>纯几何计算：无 RevitAPI / CAD 库依赖，Core 与测试共用。</summary>
public static class GeometryUtils
{
    /// <summary>从 start/end 计算欧氏长度(mm)。</summary>
    public static double MeasureLength(double[] start, double[] end)
    {
        if (start is not { Length: >= 2 } || end is not { Length: >= 2 }) return 0;
        double dx = end[0] - start[0], dy = end[1] - start[1];
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>平面多段线面积(mm²)，鞋带公式。</summary>
    public static double PolygonArea(IReadOnlyList<double[]> pts)
    {
        double s = 0;
        for (int i = 0; i < pts.Count; i++)
        {
            var a = pts[i]; var b = pts[(i + 1) % pts.Count];
            if (a is not { Length: >= 2 } || b is not { Length: >= 2 }) continue;
            s += a[0] * b[1] - b[0] * a[1];
        }
        return Math.Abs(s) / 2;
    }

    /// <summary>闭合多段线周长(mm)。</summary>
    public static double PolygonPerimeter(IReadOnlyList<double[]> pts)
    {
        double total = 0;
        for (int i = 0; i < pts.Count; i++)
            total += MeasureLength(pts[i], pts[(i + 1) % pts.Count]);
        return total;
    }
}
