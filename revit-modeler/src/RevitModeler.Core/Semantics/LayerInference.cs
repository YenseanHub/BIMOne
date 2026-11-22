namespace RevitModeler.Core.Semantics;

/// <summary>单条推断结果：带置信度与依据，供专家复核 / 图像验证定向取样。</summary>
public sealed class LayerInference
{
    public SemanticCategory Category { get; set; } = SemanticCategory.Unknown;
    public double Confidence { get; set; }
    public string Reason { get; set; } = "";

    /// <summary>0.4~0.7 为低置信：可先占位建模，但建议图像交叉验证 / 专家研判。</summary>
    public bool NeedsReview => Confidence >= 0.4 && Confidence < 0.7;
    public bool Actionable => Confidence >= 0.4;
}
