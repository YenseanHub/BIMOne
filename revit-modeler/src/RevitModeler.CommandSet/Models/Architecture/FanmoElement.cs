namespace RevitModeler.CommandSet.Models.Architecture;

/// <summary>
/// 翻模输入图元（对齐 mcp-servers-for-revit 的 Models.Common 风格）。
/// 由 cad-reader 的 IR 映射而来，交给 EventHandler 在 Revit UI 线程执行。
/// 长度单位统一 mm，进入 Revit 时除以 304.8 转 ft。
/// </summary>
public class FanmoElement
{
    /// <summary>CAD 句柄，全流程追溯锚点</summary>
    public string Handle { get; set; }

    /// <summary>目标类别：Wall / Floor / Door / Window / Column...</summary>
    public string Category { get; set; }

    /// <summary>墙/板定位线起点 (mm)</summary>
    public double[] Start { get; set; }

    /// <summary>墙/板定位线终点 (mm)</summary>
    public double[] End { get; set; }

    /// <summary>标高 (mm)，相对项目基准</summary>
    public double BaseLevel { get; set; }

    /// <summary>高度 (mm)：墙高 / 板厚</summary>
    public double Height { get; set; } = 3000;

    /// <summary>族类型 Id（-1 表示用默认类型）</summary>
    public int TypeId { get; set; } = -1;

    /// <summary>来源图层（可审计）</summary>
    public string Layer { get; set; }

    /// <summary>闭合轮廓顶点（楼板用，mm）：[[x,y,z],...]</summary>
    public double[][] Points { get; set; }

    /// <summary>原图 Z (mm)，用于标高判定</summary>
    public double Z { get; set; }
}
