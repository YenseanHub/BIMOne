using Newtonsoft.Json.Linq;
using Autodesk.Revit.UI;
using RevitMCPSDK.API.Base;
using RevitModeler.CommandSet.Models.Common;
using RevitModeler.CommandSet.Services.Architecture;

namespace RevitModeler.CommandSet.Commands.Architecture;

/// <summary>把活动视图缩放到模型范围；不传 elementIds 时自动收集翻模构件。</summary>
public class FanmoZoomCommand : ExternalEventCommandBase
{
    private FanmoZoomEventHandler Handler => (FanmoZoomEventHandler)base.Handler;

    public override string CommandName => "fanmo_zoom";

    public FanmoZoomCommand(UIApplication uiApp)
        : base(new FanmoZoomEventHandler(), uiApp)
    {
    }

    public override object Execute(JObject parameters, string requestId)
    {
        try
        {
            var ids = parameters["elementIds"]?.ToObject<List<long>>() ?? new List<long>();
            var viewKind = parameters["viewKind"]?.Value<string>() ?? "auto";
            Handler.SetParameters(ids, viewKind);
            if (RaiseAndWaitForCompletion(120000)) return Handler.Result;
            throw new TimeoutException("缩放超时");
        }
        catch (Exception ex)
        {
            return new AIResult<string> { Success = false, Message = $"缩放到模型失败: {ex.Message}" };
        }
    }
}
