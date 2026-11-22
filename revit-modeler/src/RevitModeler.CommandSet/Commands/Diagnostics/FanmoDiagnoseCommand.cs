using Newtonsoft.Json.Linq;
using Autodesk.Revit.UI;
using RevitMCPSDK.API.Base;
using RevitModeler.CommandSet.Models.Common;
using RevitModeler.CommandSet.Services.Diagnostics;

namespace RevitModeler.CommandSet.Commands.Diagnostics;

/// <summary>诊断命令入口：只读导出文档类型库能力，供精度排查使用。</summary>
public class FanmoDiagnoseCommand : ExternalEventCommandBase
{
    private FanmoDiagnoseEventHandler Handler => (FanmoDiagnoseEventHandler)base.Handler;

    public override string CommandName => "fanmo_diagnose";

    public FanmoDiagnoseCommand(UIApplication uiApp)
        : base(new FanmoDiagnoseEventHandler(), uiApp)
    {
    }

    public override object Execute(JObject parameters, string requestId)
    {
        try
        {
            // 可选探针：对指定楼板类型实测「层缩放」与「单层重建」两条写厚路径是否可用。
            // 这两条路径平时只在特定样板下才会触发，靠猜不可靠，必须能单独验证。
            var probeType = parameters["probeFloorType"]?.Value<string>();
            var probeThk = parameters["probeThicknessMm"]?.Value<double>() ?? 100;
            var testWall = parameters["createTestWall"]?.Value<bool>() ?? false;
            var refresh = parameters["refresh"]?.Value<bool>() ?? false;
            var setDiscipline = parameters["setDiscipline"]?.Value<int>() ?? -1;

            Handler.SetParameters(probeType, probeThk, testWall, refresh, setDiscipline);
            if (RaiseAndWaitForCompletion(120000)) return Handler.Result;
            throw new TimeoutException("诊断超时");
        }
        catch (Exception ex)
        {
            return new AIResult<object> { Success = false, Message = $"诊断命令失败: {ex.Message}" };
        }
    }
}
