namespace RevitModeler.CommandSet.Models.Review;

/// <summary>翻模复查结果：构件统计 + 问题清单（对齐方案 §3 自动复查环节）。</summary>
public class ReviewReport
{
    public int WallCount { get; set; }
    public int FloorCount { get; set; }
    public List<ReviewIssue> Issues { get; set; } = new();
}

/// <summary>单条复查问题：定位到 ElementId + 可审计描述。</summary>
public class ReviewIssue
{
    public int ElementId { get; set; }
    public string Severity { get; set; } = "Warning"; // Warning / Error
    public string Message { get; set; } = "";
}
