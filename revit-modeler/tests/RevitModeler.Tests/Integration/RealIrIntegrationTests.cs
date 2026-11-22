using System.Text.Json;
using RevitModeler.Core.Models;
using RevitModeler.Core.Mapping;
using RevitModeler.Core.Serialization;
using RevitModeler.Core.Utils;
using Xunit.Abstractions;

namespace RevitModeler.Tests.Integration;

/// <summary>端到端：用 cad-reader 产出的真实 IR，验证坐标贯穿到 fanmo_build data。</summary>
public class RealIrIntegrationTests
{
    private readonly ITestOutputHelper _output;
    public RealIrIntegrationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void RealApartmentIr_CoordinatesShouldReachFanmoData()
    {
        string irPath = @"D:/RIAMB/Code/BIMOne/outputs/一居室公寓.ir.json";
        if (!File.Exists(irPath)) return; // 无真实数据时跳过，不阻塞 CI

        using var doc = JsonDocument.Parse(File.ReadAllText(irPath));
        var entities = new List<CadEntity>();
        foreach (var el in doc.RootElement.GetProperty("entities").EnumerateArray())
        {
            entities.Add(new CadEntity
            {
                Handle = el.GetProperty("handle").GetString() ?? "",
                Type = el.GetProperty("type").GetString() ?? "",
                Layer = el.GetProperty("layer").GetString() ?? "",
                Closed = el.GetProperty("closed").GetBoolean(),
                Geometry = el.GetProperty("geometry").GetString() ?? "",
                Start = ReadVec3(el, "start"),
                End = ReadVec3(el, "end"),
                Points = ReadPoints(el, "points"),
                Length = MeasureEntityLength(el)
            });
        }

        var mapper = new ModelMapper(ModelMapper.DefaultRules());
        var cmds = mapper.Map(entities);

        Assert.Equal(entities.Count, cmds.Count);
        int actionable = cmds.Count(c => c.Action != ModelAction.Skip);
        Assert.True(actionable > 0, "真实数据应至少映射出一条可执行建模命令");

        // 关键断言：fanmo_build 的 data 必须带真实坐标，不能是 [0,0,0]
        string dataJson = CommandSerializer.ToFanmoDataJson(cmds);
        using var payload = JsonDocument.Parse(dataJson);
        var array = payload.RootElement;
        Assert.True(array.GetArrayLength() > 0);

        int withCoords = 0;
        foreach (var item in array.EnumerateArray())
        {
            var start = item.GetProperty("start");
            double sx = start[0].GetDouble(), sy = start[1].GetDouble();
            if (Math.Abs(sx) > 1e-6 || Math.Abs(sy) > 1e-6) withCoords++;

            string category = item.GetProperty("category").GetString();
            if (category == "Wall")
            {
                var end = item.GetProperty("end");
                double ex = end[0].GetDouble(), ey = end[1].GetDouble();
                Assert.True(Math.Abs(ex - sx) > 1e-6 || Math.Abs(ey - sy) > 1e-6,
                    $"墙图元 {item.GetProperty("handle").GetString()} 的 start/end 不应重合");
            }
            else if (category == "Floor")
            {
                var pts = item.GetProperty("points");
                Assert.True(pts.GetArrayLength() >= 3,
                    $"楼板 {item.GetProperty("handle").GetString()} 应带至少 3 个轮廓顶点");
            }
        }
        Assert.True(withCoords > 0, "必须至少有一条命令带非零真实坐标（否则墙全堆原点）");

        var walls = cmds.Count(c => c.Action == ModelAction.CreateWall);
        var floors = cmds.Count(c => c.Action == ModelAction.CreateFloor);
        int skipped = cmds.Count(c => c.Action == ModelAction.Skip);
        _output.WriteLine($"真实 IR 端到端: {entities.Count} 图元 → 可序列化 {array.GetArrayLength()} / 墙 {walls} / 楼板 {floors} / 跳过 {skipped} / 带真实坐标 {withCoords}");
    }

    private static double MeasureEntityLength(JsonElement el)
    {
        var pts = ReadPoints(el, "points");
        if (pts.Count >= 2) return GeometryUtils.PolygonPerimeter(pts);
        return GeometryUtils.MeasureLength(ReadVec3(el, "start"), ReadVec3(el, "end"));
    }

    private static double[] ReadVec3(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return null;
        return v.EnumerateArray().Select(x => x.GetDouble()).ToArray();
    }

    private static List<double[]> ReadPoints(JsonElement el, string name)
    {
        var result = new List<double[]>();
        if (!el.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return result;
        foreach (var p in arr.EnumerateArray())
            result.Add(p.EnumerateArray().Select(x => x.GetDouble()).ToArray());
        return result;
    }
}
