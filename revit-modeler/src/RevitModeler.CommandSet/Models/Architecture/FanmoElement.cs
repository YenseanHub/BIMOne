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

    /// <summary>期望墙类型名；为空时按墙厚查找或创建</summary>
    public string WallTypeName { get; set; }

    /// <summary>墙厚 (mm)；<=0 表示使用默认墙类型</summary>
    public double WallThicknessMm { get; set; } = -1;

    /// <summary>期望楼板类型名；为空时使用文档默认楼板类型</summary>
    public string FloorTypeName { get; set; }

    /// <summary>板厚 (mm)；<=0 表示不覆盖类型默认厚度</summary>
    public double FloorThicknessMm { get; set; } = -1;

    /// <summary>族名（门/窗/柱占位用）</summary>
    public string FamilyName { get; set; }

    /// <summary>族实例宽度 (mm)，门/窗占位用；<=0 表示不设置</summary>
    public double WidthMm { get; set; } = -1;

    /// <summary>截面宽 b (mm)：柱/梁用，来自 CAD 柱块缩放或默认值</summary>
    public double SectionBMm { get; set; } = -1;

    /// <summary>截面高 h (mm)：柱/梁用</summary>
    public double SectionHMm { get; set; } = -1;

    /// <summary>构件底标高 (mm)：梁底相对本层标高，用于实例 Z 偏移</summary>
    public double ElevationMm { get; set; }

    /// <summary>来源标记：cad:* 图纸识别 / default:* 默认值推断，写入报告便于复核</summary>
    public string Source { get; set; } = "";

    /// <summary>族实例旋转角（度）</summary>
    public double RotationDeg { get; set; }
}
