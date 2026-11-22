using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Base;
using RevitModeler.CommandSet.Models.Common;
using RevitModeler.CommandSet.Services.Review;

namespace RevitModeler.CommandSet.Commands.Review;

/// <summary>导出当前模型墙/板真实几何，用于与 CAD 命令做精度对比。</summary>
public class FanmoExportCommand : ExternalEventCommandBase
{
    private FanmoExportEventHandler ExportHandler => (FanmoExportEventHandler)base.Handler;

    public override string CommandName => "fanmo_export";

    public FanmoExportCommand(UIApplication uiApp)
        : base(new FanmoExportEventHandler(), uiApp)
    {
    }

    public override object Execute(JObject parameters, string requestId)
    {
        try
        {
            ExportHandler.SetParameters();
            if (RaiseAndWaitForCompletion(600000))
                return ExportHandler.Result;
            throw new TimeoutException("几何导出超时");
        }
        catch (Exception ex)
        {
            return new AIResult<object> { Success = false, Message = $"几何导出失败: {ex.Message}" };
        }
    }
}
