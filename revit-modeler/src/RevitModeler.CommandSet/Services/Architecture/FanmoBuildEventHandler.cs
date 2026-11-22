using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPSDK.API.Interfaces;
using RevitModeler.CommandSet.Models.Architecture;
using RevitModeler.CommandSet.Models.Common;
using RevitModeler.CommandSet.Utils;

namespace RevitModeler.CommandSet.Services.Architecture;

/// <summary>
/// 翻模建模事件处理器：在 Revit UI 线程执行 IR 输入 → Transaction 内建墙/楼板 → AIResult 返回。
/// 对齐 mcp-servers-for-revit 的 IExternalEventHandler + IWaitableExternalEventHandler 模式。
/// </summary>
public class FanmoBuildEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
{
    private UIApplication _uiApp;
    private UIDocument UiDoc => _uiApp.ActiveUIDocument;
    private Document Doc => UiDoc.Document;

    private readonly ManualResetEvent _resetEvent = new(false);

    public List<FanmoElement> Input { get; private set; }
    public AIResult<List<int>> Result { get; private set; }

    private readonly List<string> _warnings = new();

    public void SetParameters(List<FanmoElement> data)
    {
        Input = data;
        _resetEvent.Reset();
    }

    public void Execute(UIApplication uiapp)
    {
        _uiApp = uiapp;
        var elementIds = new List<int>();
        try
        {
            _warnings.Clear();
            if (Input is null || Input.Count == 0)
                throw new ArgumentException("翻模输入为空");

            Level level = DocumentUtils.FindNearestLevel(Doc, Input[0].BaseLevel);

            // 单个 Transaction 批量建模：全成功或全回滚，是隔离的最小单元
            using var tx = new Transaction(Doc, "CAD翻模-占位建模");
            tx.Start();
            foreach (var item in Input)
            {
                ElementId id = item.Category switch
                {
                    "Wall" or "墙" => CreateWall(item, level),
                    "Floor" or "楼板" => CreateFloor(item, level),
                    _ => CreateFamilyInstance(item, level)
                };
                if (id is not null) elementIds.Add(id.IntegerValue);
            }
            tx.Commit();

            Result = new AIResult<List<int>>
            {
                Success = true,
                Message = BuildMessage(elementIds.Count),
                Response = elementIds
            };
        }
        catch (Exception ex)
        {
            Result = new AIResult<List<int>>
            {
                Success = false,
                Message = $"翻模建模失败: {ex.Message}"
            };
        }
        finally
        {
            _resetEvent.Set();
        }
    }

    private ElementId CreateWall(FanmoElement item, Level level)
    {
        WallType wallType = DocumentUtils.ResolveWallType(Doc, item.TypeId);
        if (wallType is null)
        {
            _warnings.Add($"[{item.Handle}] 无可用墙类型，跳过");
            return null;
        }
        Line line = ToLine(item);
        Wall wall = Wall.Create(Doc, line, wallType.Id, level.Id,
            CadUnits.ToFeet(item.Height), 0, false, false);
        return wall?.Id;
    }

    private ElementId CreateFloor(FanmoElement item, Level level)
    {
        // 用 IR 的真实闭合顶点生成轮廓（mm → ft）；顶点不足时退化为定位线矩形占位
        var loop = new CurveLoop();
        var pts = item.Points;
        if (pts is not null && pts.Length >= 3)
        {
            for (int i = 0; i < pts.Length; i++)
                loop.Append(Line.CreateBound(CadUnits.FromMm(pts[i]), CadUnits.FromMm(pts[(i + 1) % pts.Length])));
        }
        else
        {
            var s = CadUnits.ToXyz(item.Start); var e = CadUnits.ToXyz(item.End);
            var off = new XYZ(0, CadUnits.ToFeet(1000), 0);
            loop.Append(Line.CreateBound(s, e));
            loop.Append(Line.CreateBound(e, e + off));
            loop.Append(Line.CreateBound(e + off, s + off));
            loop.Append(Line.CreateBound(s + off, s));
        }
        var loops = new List<CurveLoop> { loop };

        FloorType floorType = DocumentUtils.ResolveFloorType(Doc);
        if (floorType is null)
        {
            _warnings.Add($"[{item.Handle}] 无可用楼板类型，跳过");
            return null;
        }
        Floor floor = Floor.Create(Doc, loops, floorType.Id, level.Id);
        return floor?.Id;
    }

    private ElementId CreateFamilyInstance(FanmoElement item, Level level)
    {
        // P3 精化：按 item.Category + FamilyName 从族库选 FamilySymbol
        _warnings.Add($"[{item.Handle}] 类别 {item.Category} 族精化待 P3 实现");
        return null;
    }

    private static Line ToLine(FanmoElement item) => Line.CreateBound(
        CadUnits.ToXyz(item.Start), CadUnits.ToXyz(item.End ?? new[] { 1000d, 0d, 0d }));

    private string BuildMessage(int count)
    {
        string msg = $"成功创建 {count} 个构件";
        if (_warnings.Count > 0) msg += "\n⚠ " + string.Join("\n  • ", _warnings);
        return msg;
    }

    public bool WaitForCompletion(int timeoutMilliseconds = 10000)
    {
        _resetEvent.Reset();
        return _resetEvent.WaitOne(timeoutMilliseconds);
    }

    public string GetName() => "CAD翻模建模";
}
