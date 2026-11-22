using RevitModeler.Core.Models;
using RevitModeler.Core.Semantics;
using Xunit;

namespace RevitModeler.Tests.Semantics;

public class LayerSemanticsTests
{
    [Fact]
    public void ChineseWallLayer_ShouldBeHighConfidence()
    {
        var inf = LayerSemantics.Infer(new CadEntity { Type = "Line", Layer = "墙体线", Length = 3000 });
        Assert.Equal(SemanticCategory.Wall, inf.Category);
        Assert.True(inf.Confidence >= 0.9);
        Assert.False(inf.NeedsReview);
    }

    [Fact]
    public void NumberLayer_ParallelLines_ShouldInferWallWithHighConfidence()
    {
        var e = new CadEntity { Type = "Line", Layer = "0", Length = 3000,
            Start = new[] { 0d, 0d, 0d }, End = new[] { 3000d, 0d, 0d } };
        var neighbor = new CadEntity { Type = "Line", Layer = "0", Length = 3000,
            Start = new[] { 0d, 200d, 0d }, End = new[] { 3000d, 200d, 0d } };
        var inf = LayerSemantics.Infer(e, new[] { e, neighbor });
        Assert.Equal(SemanticCategory.Wall, inf.Category);
        Assert.True(inf.Confidence >= 0.7, "平行双线应提升为高置信");
    }

    [Fact]
    public void ClosedLargePolyline_ShouldInferFloor()
    {
        var e = new CadEntity { Type = "LwPolyline", Layer = "3", Closed = true,
            Points = new List<double[]> {
                new[]{0d,0d,0d}, new[]{5000d,0d,0d}, new[]{5000d,4000d,0d}, new[]{0d,4000d,0d} } };
        var inf = LayerSemantics.Infer(e);
        Assert.Equal(SemanticCategory.Floor, inf.Category);
        Assert.True(inf.Confidence >= 0.7);
    }
}
