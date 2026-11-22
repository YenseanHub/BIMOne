using Newtonsoft.Json.Linq;
using Autodesk.Revit.UI;
using RevitMCPSDK.API.Base;
using RevitModeler.CommandSet.Models.Common;
using RevitModeler.CommandSet.Services.Architecture;

namespace RevitModeler.CommandSet.Commands.Architecture;

/// <summary>把活动视图导出为 PNG，用于确认屏幕上到底有没有东西。</summary>
public class FanmoSnapshotCommand : ExternalEventCommandBase
{
    private FanmoSnapshotEventHandler Handler => (FanmoSnapshotEventHandler)base.Handler;

    public override string CommandName => "fanmo_snapshot";

    public FanmoSnapshotCommand(UIApplication uiApp)
        : base(new FanmoSnapshotEventHandler(), uiApp)
    {
    }

    public override object Execute(JObject parameters, string requestId)
    {
        try
        {
            var dir = parameters["outputDir"]?.Value<string>();
            var pixel = parameters["pixelSize"]?.Value<int>() ?? 1600;
            // fitModel: 默认 true。临时把裁剪框收到模型范围再导出，
            // 否则注释元素会把取景范围撑大，模型被挤到框外，图看着是空的。
            var fit = parameters["fitModel"]?.Value<bool>() ?? true;
            Handler.SetParameters(dir, pixel, fit);
            if (RaiseAndWaitForCompletion(120000)) return Handler.Result;
            throw new TimeoutException("视图截图超时");
        }
        catch (Exception ex)
        {
            return new AIResult<string> { Success = false, Message = $"视图截图失败: {ex.Message}" };
        }
    }
}
