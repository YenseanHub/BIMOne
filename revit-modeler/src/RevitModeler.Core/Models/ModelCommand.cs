namespace RevitModeler.Core.Models;

/// <summary>建模命令：纯数据，交给 Revit 插件层执行。</summary>
public sealed class ModelCommand
{
    public string SourceHandle { get; set; } = "";
    public ModelAction Action { get; set; }
    public string Layer { get; set; } = "";
    public string Category { get; set; } = "";   // 墙/楼板/门/窗/柱...
    public string FamilyName { get; set; } = ""; // 族名（LOD200 精化）
    public double Length { get; set; }
    public string Reason { get; set; } = "";     // 决策依据（可审计）
    public double Confidence { get; set; } = 1;  // 语义置信度（低置信→图像复核）
    public bool NeedsReview { get; set; }        // 是否建议定向图像验证

    // 真实坐标：必须贯穿到 plugin，否则翻模全堆在原点
    public double[] Start { get; set; }
    public double[] End { get; set; }
    public List<double[]> Points { get; set; }

    // 完整翻模扩展字段：墙厚/墙类型、板厚/板类型、族占位
    public double WallThicknessMm { get; set; } = -1;
    public string WallTypeName { get; set; } = "";
    public double FloorThicknessMm { get; set; } = -1;
    public string FloorTypeName { get; set; } = "";
    public double WidthMm { get; set; } = -1;
    public double RotationDeg { get; set; }
}
