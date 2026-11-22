using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPSDK.API.Interfaces;
using RevitModeler.CommandSet.Models.Common;
using RevitModeler.CommandSet.Models.Review;
using RevitModeler.CommandSet.Utils;

namespace RevitModeler.CommandSet.Services.Review;

/// <summary>
/// 翻模复查事件处理器（方案 §3 自动复查环节）：只读扫描当前模型，输出构件统计 + 问题清单。
/// 与建模 handler 同构（IExternalEventHandler + IWaitable），但只读、不开 Transaction。
/// </summary>
public class FanmoReviewEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
{
    private UIApplication _uiApp;
    private Document Doc => _uiApp.ActiveUIDocument.Document;

    private readonly ManualResetEvent _resetEvent = new(false);

    /// <summary>墙长度下限(mm)，短于此值视为问题图元。</summary>
    public double MinWallLengthMm { get; private set; } = 10;

    public AIResult<ReviewReport> Result { get; private set; }

    public void SetParameters(double minWallLengthMm = 10)
    {
        MinWallLengthMm = minWallLengthMm;
        _resetEvent.Reset();
    }

    public void Execute(UIApplication uiapp)
    {
        _uiApp = uiapp;
        try
        {
            var walls = new FilteredElementCollector(Doc).OfClass(typeof(Wall)).Cast<Wall>().ToList();
            var floors = new FilteredElementCollector(Doc).OfClass(typeof(Floor)).Cast<Floor>().ToList();
            var report = new ReviewReport { WallCount = walls.Count, FloorCount = floors.Count };

            foreach (var wall in walls)
            {
                double lenMm = MeasureWallLengthMm(wall);
                if (lenMm < MinWallLengthMm)
                    report.Issues.Add(new ReviewIssue
                    {
                        ElementId = wall.Id.IntegerValue,
                        Severity = "Warning",
                        Message = $"墙长度异常: {lenMm:F1}mm < {MinWallLengthMm:F1}mm（疑似零长度/坐标缺失）"
                    });
            }

            Result = new AIResult<ReviewReport>
            {
                Success = true,
                Message = $"复查完成: 墙 {report.WallCount} / 楼板 {report.FloorCount} / 问题 {report.Issues.Count}",
                Response = report
            };
        }
        catch (Exception ex)
        {
            Result = new AIResult<ReviewReport> { Success = false, Message = $"复查失败: {ex.Message}" };
        }
        finally
        {
            _resetEvent.Set();
        }
    }

    private static double MeasureWallLengthMm(Wall wall)
    {
        if (wall.Location is not LocationCurve lc || lc.Curve is null) return 0;
        return CadUnits.ToMillimeters(lc.Curve.Length);
    }

    public bool WaitForCompletion(int timeoutMilliseconds = 10000) =>
        _resetEvent.WaitOne(timeoutMilliseconds);

    public string GetName() => "CAD翻模复查";
}
