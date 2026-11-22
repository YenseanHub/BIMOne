using RevitModeler.Core.Models;
using RevitModeler.Core.Mapping;
using Xunit;

namespace RevitModeler.Tests.Mapping;

public class ModelMapperTests
{
    private readonly ModelMapper _mapper = new(ModelMapper.DefaultRules());

    [Fact]
    public void ClosedFloorLayer_ShouldCreateFloor()
    {
        var entity = new CadEntity
        {
            Handle = "1", Type = "LwPolyline", Layer = "FLOOR-01", Closed = true, Length = 5000
        };
        var cmd = _mapper.MapOne(entity);
        Assert.Equal(ModelAction.CreateFloor, cmd.Action);
        Assert.Equal("楼板", cmd.Category);
    }

    [Fact]
    public void OpenPolylineOnFloorLayer_ShouldBeSkipped()
    {
        var entity = new CadEntity
        {
            Handle = "2", Type = "LwPolyline", Layer = "FLOOR-01", Closed = false, Length = 5000
        };
        var cmd = _mapper.MapOne(entity);
        Assert.Equal(ModelAction.Skip, cmd.Action);
        Assert.Contains("未闭合", cmd.Reason);
    }

    [Fact]
    public void DimensionLayer_ShouldBeSkipped()
    {
        var entity = new CadEntity { Handle = "3", Type = "Line", Layer = "DIM", Length = 3000 };
        Assert.Equal(ModelAction.Skip, _mapper.MapOne(entity).Action);
    }

    [Fact]
    public void UnknownLineLayer_ShouldInferWallWithReviewFlag()
    {
        var entity = new CadEntity
        {
            Handle = "4", Type = "Line", Layer = "未知层", Length = 3000,
            Start = new[] { 100d, 200d, 0d }, End = new[] { 3100d, 200d, 0d }
        };
        var cmd = _mapper.MapOne(entity);
        Assert.Equal(ModelAction.CreateWall, cmd.Action);
        Assert.Equal("墙", cmd.Category);
        Assert.Contains("语义推断", cmd.Reason);
        Assert.True(cmd.NeedsReview, "单线无平行近邻应为低置信，标记待图像复核");
    }

    [Fact]
    public void WildcardRule_ShouldMatchLayerPrefix()
    {
        var entity = new CadEntity
        {
            Handle = "5", Type = "LwPolyline", Layer = "家俱1", Closed = true, Length = 800
        };
        Assert.Equal(ModelAction.Skip, _mapper.MapOne(entity).Action);
    }

    [Fact]
    public void Map_ShouldPreserveOrderAndCount()
    {
        var entities = new[]
        {
            new CadEntity { Handle = "a", Type = "Line", Layer = "0", Length = 100 },
            new CadEntity { Handle = "b", Type = "Line", Layer = "墙线", Length = 200 },
            new CadEntity { Handle = "c", Type = "Circle", Layer = "X", Length = 300 }
        };
        var cmds = _mapper.Map(entities);
        Assert.Equal(3, cmds.Count);
        Assert.Equal("a", cmds[0].SourceHandle);
        Assert.Equal("b", cmds[1].SourceHandle);
        Assert.Equal("c", cmds[2].SourceHandle);
    }

    [Fact]
    public void MinLengthRule_ShouldSkipTooShort()
    {
        var mapper = new ModelMapper(new[]
        {
            new FamilyMappingRule { LayerPattern = "WALL", Action = ModelAction.CreateWall, Category = "墙", MinLength = 500 }
        });
        var cmd = mapper.MapOne(new CadEntity { Handle = "s", Type = "Line", Layer = "WALL", Length = 100 });
        Assert.Equal(ModelAction.Skip, cmd.Action);
        Assert.Contains("最小", cmd.Reason);
    }
}
