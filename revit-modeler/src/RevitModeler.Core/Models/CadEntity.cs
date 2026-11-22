namespace RevitModeler.Core.Models;

/// <summary>与 CAD 侧 IR 对齐的输入契约（不引用 CAD 程序集，避免耦合）。</summary>
public sealed class CadEntity
{
    public string Handle { get; set; } = "";
    public string Type { get; set; } = "";
    public string Layer { get; set; } = "";
    public string Text { get; set; } = "";
    public bool Closed { get; set; }
    public double Length { get; set; }     // 解析出的长度（墙长/跨度），mm
    public string Geometry { get; set; } = "";

    // 结构化坐标（从 IR 传入，mm）
    public double[] Start { get; set; }
    public double[] End { get; set; }
    public List<double[]> Points { get; set; }
    public double[] Center { get; set; }
    public double Radius { get; set; }
    public string BlockName { get; set; } = "";
    public string LineWeight { get; set; } = "";
    public string Color { get; set; } = "";
}
