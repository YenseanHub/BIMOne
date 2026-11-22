using System.Text.Json;
using RevitModeler.Core.Models;

namespace RevitModeler.Core.Serialization;

/// <summary>
/// 把建模命令序列化为 Host 调用 fanmo_build 时的 JObject.data 结构。
/// 位于 Core（不依赖 RevitAPI 与 Newtonsoft），Plugin 侧只需 ToObject&lt;FanmoElement&gt;。
/// </summary>
public static class CommandSerializer
{
    /// <summary>ModelCommand 列表 → 可直接塞进 Execute(JObject)["data"] 的 JSON 数组字符串。</summary>
    public static string ToFanmoDataJson(IEnumerable<ModelCommand> commands)
    {
        var items = (commands ?? Enumerable.Empty<ModelCommand>())
            .Where(c => c.Action != ModelAction.Skip)
            .Where(c => HasUsableGeometry(c))   // 无坐标的图元不建，避免全堆在原点
            .Select(c => new
            {
                handle = c.SourceHandle,
                category = ToCategory(c),
                start = c.Start,
                end = c.End,
                points = c.Points,              // 楼板闭合轮廓（Plugin 侧 P3 用）
                z = c.Start?[2] ?? 0d,
                baseLevel = 0d,
                height = c.Action == ModelAction.CreateFloor ? 120d : 3000d,
                typeId = -1,
                layer = c.Layer
            });
        return JsonSerializer.Serialize(items, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
    }

    /// <summary>必须至少有一组真实坐标，否则拒绝建模（防止墙全堆在原点）。</summary>
    private static bool HasUsableGeometry(ModelCommand c)
    {
        if (c.Start is { Length: >= 3 } && c.End is { Length: >= 3 }) return true;
        if (c.Points is { Count: >= 2 }) return true;
        return false;
    }

    private static string ToCategory(ModelCommand c) => c.Action switch
    {
        ModelAction.CreateWall => "Wall",
        ModelAction.CreateFloor => "Floor",
        ModelAction.PlaceFamily => c.Category,
        _ => c.Category
    };
}
