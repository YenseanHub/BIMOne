using Autodesk.Revit.UI;
using System;
using System.Reflection;
using RevitModeler.Plugin.Utils;

namespace RevitModeler.Plugin.Core;

/// <summary>
/// BIMOne RevitModeler 插件入口（IExternalApplication）。
/// 与 mcp-servers-for-revit/plugin/Core/Application.cs 对齐：Ribbon 面板 + 开/关 Server 按钮。
///
/// 启动策略：由用户点一次「翻模服务 开/关」启动（不随 Revit 自启）。
/// 服务一旦启动即常驻，之后只更新命令集走 deploy-hot.ps1 热重载，无需重启 Revit。
/// </summary>
public class Application : IExternalApplication
{
    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            var panel = application.CreateRibbonPanel("BIMOne 翻模");

            var data = new PushButtonData(
                "ID_BIMONE_FANMO_SERVER",
                "翻模服务\n开/关",
                Assembly.GetExecutingAssembly().Location,
                "RevitModeler.Plugin.Core.ServerSwitchCommand")
            {
                ToolTip = "开启 / 关闭 BIMOne 翻模 JSON-RPC 服务（端口 " + PluginConfig.Port + "）"
            };

            panel.AddItem(data);
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            AppLogger.Error("BIMOne RevitModeler 启动失败: " + ex.Message);
            return Result.Failed;
        }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        try
        {
            if (FanmoSocketService.Instance.IsRunning)
                FanmoSocketService.Instance.Stop();
        }
        catch (Exception ex)
        {
            AppLogger.Error("BIMOne RevitModeler 关闭服务失败: " + ex.Message);
        }
        return Result.Succeeded;
    }
}
