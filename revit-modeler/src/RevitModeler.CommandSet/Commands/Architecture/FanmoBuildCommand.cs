using Newtonsoft.Json.Linq;
using Autodesk.Revit.UI;
using RevitMCPSDK.API.Base;
using RevitModeler.CommandSet.Models.Common;
using RevitModeler.CommandSet.Services.Architecture;

namespace RevitModeler.CommandSet.Commands.Architecture;

/// <summary>
/// 翻模建模命令入口：由 Host kernel 按 command.json 反射调用 Execute(JObject, requestId)。
/// 对齐 mcp-servers-for-revit 的 ExternalEventCommandBase 模式。
/// </summary>
public class FanmoBuildCommand : ExternalEventCommandBase
{
    private FanmoBuildEventHandler Handler => (FanmoBuildEventHandler)base.Handler;

    public override string CommandName => "fanmo_build";

    public FanmoBuildCommand(UIApplication uiApp)
        : base(new FanmoBuildEventHandler(), uiApp)
    {
    }

    public override object Execute(JObject parameters, string requestId)
    {
        try
        {
            var data = parameters["data"]?.ToObject<List<Models.Architecture.FanmoElement>>();
            if (data is null || data.Count == 0)
                throw new ArgumentNullException(nameof(data), "翻模输入为空");

            Handler.SetParameters(data);

            if (RaiseAndWaitForCompletion(10000))
                return Handler.Result;

            throw new TimeoutException("翻模建模超时");
        }
        catch (Exception ex)
        {
            return new AIResult<List<int>> { Success = false, Message = $"翻模命令失败: {ex.Message}" };
        }
    }
}
