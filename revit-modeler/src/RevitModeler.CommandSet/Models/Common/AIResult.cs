// 对齐 mcp-servers-for-revit 的 Models.Common.AIResult<T>：
// Host 通过 Execute() 返回此结果，序列化后经 MCP 回传给 agent。
namespace RevitModeler.CommandSet.Models.Common;

public class AIResult<T>
{
    public bool Success { get; set; }
    public string Message { get; set; }
    public T Response { get; set; }
}
