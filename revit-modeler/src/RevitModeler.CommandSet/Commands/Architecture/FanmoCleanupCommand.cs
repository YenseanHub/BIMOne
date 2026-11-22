using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Base;
using RevitModeler.CommandSet.Models.Common;
using RevitModeler.CommandSet.Services.Architecture;

namespace RevitModeler.CommandSet.Commands.Architecture;

/// <summary>翻模回滚命令：按 ElementId 删除上一轮生成的构件。</summary>
public class FanmoCleanupCommand : ExternalEventCommandBase
{
    private FanmoCleanupEventHandler Handler => (FanmoCleanupEventHandler)base.Handler;

    public override string CommandName => "fanmo_cleanup";

    public FanmoCleanupCommand(UIApplication uiApp)
        : base(new FanmoCleanupEventHandler(), uiApp)
    {
    }

    public override object Execute(JObject parameters, string requestId)
    {
        try
        {
            var ids = parameters["elementIds"]?.ToObject<List<long>>() ?? new List<long>();
            // pruneTypes: 顺手删掉无实例引用的 BIMOne_* 类型，
            // 避免多轮翻模在文档里堆一堆孤儿类型（失败尝试会留下旧类型）
            var prune = parameters["pruneTypes"]?.Value<bool>() ?? false;
            // categoryIds: 按 BuiltInCategory 整批删除（诊断留下的模型线/对照墙拿不到 id）
            var categoryIds = parameters["categoryIds"]?.ToObject<List<int>>() ?? new List<int>();
            if (ids.Count == 0 && !prune && categoryIds.Count == 0)
                throw new ArgumentNullException(nameof(ids), "elementIds 为空（如需清理孤儿类型请传 pruneTypes=true）");

            Handler.SetParameters(ids, prune, categoryIds);
            if (RaiseAndWaitForCompletion(600000))
                return Handler.Result;
            throw new TimeoutException("翻模清理超时");
        }
        catch (Exception ex)
        {
            return new AIResult<int> { Success = false, Message = $"翻模清理失败: {ex.Message}" };
        }
    }
}
