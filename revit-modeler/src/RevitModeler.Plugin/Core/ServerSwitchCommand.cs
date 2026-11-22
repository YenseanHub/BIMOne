using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;

namespace RevitModeler.Plugin.Core;

/// <summary>Ribbon「翻模服务 开/关」按钮：与 revit-mcp 的 MCPServiceConnection 对齐。</summary>
[Transaction(TransactionMode.Manual)]
public class ServerSwitchCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            var service = FanmoSocketService.Instance;
            if (service.IsRunning)
            {
                service.Stop();
                TaskDialog.Show("BIMOne 翻模", "翻模服务已关闭");
            }
            else
            {
                service.Initialize(commandData.Application);
                service.Start();
                TaskDialog.Show("BIMOne 翻模", $"翻模服务已开启，监听端口 {service.Port}");
            }
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return Result.Failed;
        }
    }
}
