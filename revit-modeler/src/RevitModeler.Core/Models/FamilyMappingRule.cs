namespace RevitModeler.Core.Models;

/// <summary>族库映射规则：图层 → Revit 类别/族。项目级可覆盖，不写死代码。</summary>
public sealed class FamilyMappingRule
{
    public string LayerPattern { get; set; } = "";
    public ModelAction Action { get; set; }
    public string Category { get; set; } = "";
    public string FamilyName { get; set; } = "";
    public double MinLength { get; set; } = 0;
}
