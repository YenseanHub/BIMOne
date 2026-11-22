using RevitModeler.Core.Models;
using RevitModeler.Core.Serialization;
using Xunit;

namespace RevitModeler.Tests.Serialization;

public class CommandSerializerTests
{
    [Fact]
    public void ToFanmoDataJson_ShouldSkipAndProduceCamelCase()
    {
        var cmds = new[]
        {
            new ModelCommand { SourceHandle="1", Action=ModelAction.CreateWall, Category="墙", Layer="A", Length=3000,
                Start = new[]{100d,200d,0d}, End = new[]{100d,3200d,0d} },
            new ModelCommand { SourceHandle="2", Action=ModelAction.Skip, Category="标注", Layer="DIM", Length=100 },
            new ModelCommand { SourceHandle="3", Action=ModelAction.CreateWall, Category="墙", Layer="A", Length=3000 }
                // 无坐标 → 应被过滤，防止堆在原点
        };
        string json = CommandSerializer.ToFanmoDataJson(cmds);
        Assert.Contains("\"handle\": \"1\"", json);
        Assert.Contains("\"category\": \"Wall\"", json);
        Assert.Contains("\"start\"", json);
        Assert.DoesNotContain("\"handle\": \"2\"", json);   // Skip 被过滤
        Assert.DoesNotContain("\"handle\": \"3\"", json);   // 无坐标被过滤
    }
}
