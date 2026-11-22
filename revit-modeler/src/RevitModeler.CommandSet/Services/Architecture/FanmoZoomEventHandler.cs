using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPSDK.API.Interfaces;
using RevitModeler.CommandSet.Models.Common;
using RevitModeler.CommandSet.Utils;

namespace RevitModeler.CommandSet.Services.Architecture;

/// <summary>
/// 把活动视图定位到模型。翻模坐标来自 CAD 真实坐标系，模型常偏离项目原点上百米，
/// 视图不跟随就会出现「建好了但屏幕是空的」。定位逻辑见 <see cref="ViewNavigator"/>。
/// </summary>
public class FanmoZoomEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
{
    private UIApplication _uiApp;
    private readonly ManualResetEvent _resetEvent = new(false);

    public List<long> ElementIds { get; private set; } = new();
    /// <summary>auto / threeD / plan：plan 会切到模型所在标高的平面视图。</summary>
    public string ViewKind { get; private set; } = "auto";
    public AIResult<string> Result { get; private set; }

    public void SetParameters(IEnumerable<long> elementIds, string viewKind = "auto")
    {
        ElementIds = (elementIds ?? Enumerable.Empty<long>()).ToList();
        ViewKind = string.IsNullOrWhiteSpace(viewKind) ? "auto" : viewKind.Trim();
        _resetEvent.Reset();
    }

    public void Execute(UIApplication uiapp)
    {
        _uiApp = uiapp;
        try
        {
            var uidoc = uiapp?.ActiveUIDocument;
            if (uidoc is null) { Fail("没有打开的项目文档"); return; }
            var doc = uidoc.Document;

            var ids = ElementIds.Count > 0
                ? ElementIds.Select(id => new ElementId(id)).ToList()
                : CollectModelElements(doc);

            if (ids.Count == 0) { Fail("文档里没有可缩放的翻模构件"); return; }

            var message = ViewNavigator.Focus(doc, uidoc, ids, ViewKind);
            if (message.StartsWith("定位失败") || message.StartsWith("定位跳过")) Fail(message);
            else Success(message);
        }
        catch (Exception ex)
        {
            Fail($"缩放失败: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _resetEvent.Set();
        }
    }

    /// <summary>收集墙/楼板/结构柱/结构梁实例（翻模四类构件的实际类别）。</summary>
    private static List<ElementId> CollectModelElements(Document doc)
    {
        var ids = new List<ElementId>();
        ids.AddRange(new FilteredElementCollector(doc).OfClass(typeof(Wall)).ToElementIds());
        ids.AddRange(new FilteredElementCollector(doc).OfClass(typeof(Floor)).ToElementIds());
        ids.AddRange(new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_StructuralColumns).WhereElementIsNotElementType().ToElementIds());
        ids.AddRange(new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_StructuralFraming).WhereElementIsNotElementType().ToElementIds());
        return ids;
    }

    private void Success(string message) =>
        Result = new AIResult<string> { Success = true, Message = message, Response = message };

    private void Fail(string message) =>
        Result = new AIResult<string> { Success = false, Message = message };

    public bool WaitForCompletion(int timeoutMilliseconds = 600000) => _resetEvent.WaitOne(timeoutMilliseconds);

    public string GetName() => "CAD翻模-缩放到模型";
}
