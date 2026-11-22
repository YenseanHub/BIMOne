using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPSDK.API.Interfaces;
using RevitModeler.CommandSet.Models.Common;
using RevitModeler.CommandSet.Utils;

namespace RevitModeler.CommandSet.Services.Architecture;

/// <summary>按 ElementId 删除 BIMOne 测试构件，用于翻模重跑前回滚。</summary>
public class FanmoCleanupEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
{
    private UIApplication _uiApp;
    private Document Doc => _uiApp.ActiveUIDocument.Document;

    private readonly ManualResetEvent _resetEvent = new(false);

    public List<long> ElementIds { get; private set; } = new();
    public bool PruneTypes { get; private set; }
    public List<int> CategoryIds { get; private set; } = new();
    public AIResult<int> Result { get; private set; }

    public void SetParameters(IEnumerable<long> elementIds, bool pruneTypes = false, IEnumerable<int> categoryIds = null)
    {
        ElementIds = (elementIds ?? Enumerable.Empty<long>()).ToList();
        PruneTypes = pruneTypes;
        CategoryIds = (categoryIds ?? Enumerable.Empty<int>()).ToList();
        _resetEvent.Reset();
    }

    public void Execute(UIApplication uiapp)
    {
        _uiApp = uiapp;
        int deleted = 0;
        try
        {
            if (ElementIds.Count == 0 && !PruneTypes && CategoryIds.Count == 0)
                throw new ArgumentException("elementIds 为空");

            using var tx = QuietTransaction.Begin(Doc, "CAD翻模-清理测试构件");
            foreach (long id in ElementIds)
            {
                if (Doc.GetElement(new ElementId(id)) is null) continue;
                Doc.Delete(new ElementId(id));
                deleted++;
            }

            // 按类别整批删除：诊断用的模型线、对照墙这类"不是翻模产出"的东西，
            // 调用方通常拿不到 id，只能按类别清。
            foreach (var catId in CategoryIds)
            {
                var ids = new FilteredElementCollector(Doc)
                    .OfCategory((BuiltInCategory)catId)
                    .WhereElementIsNotElementType()
                    .ToElementIds();
                foreach (var id in ids)
                {
                    if (Doc.GetElement(id) is null) continue;
                    Doc.Delete(id);
                    deleted++;
                }
            }

            int pruned = PruneTypes ? PruneOrphanTypes() : 0;
            tx.Commit();
            Result = new AIResult<int>
            {
                Success = true,
                Message = $"已清理 {deleted} 个构件"
                          + (PruneTypes ? $"、{pruned} 个无引用的 BIMOne 类型" : ""),
                Response = deleted
            };
        }
        catch (Exception ex)
        {
            Result = new AIResult<int> { Success = false, Message = $"清理失败: {ex.Message}" };
        }
        finally
        {
            _resetEvent.Set();
        }
    }

    /// <summary>
    /// 删除没有任何实例引用的 BIMOne_* 墙/板类型。多轮翻模里失败的类型复制会留下孤儿，
    /// 不清掉会让同名类型被占用，于是每轮都新建一个，文档越堆越乱。
    /// </summary>
    private int PruneOrphanTypes()
    {
        int removed = 0;
        var orphanIds = new List<ElementId>();

        var usedWallTypes = new HashSet<ElementId>(
            new FilteredElementCollector(Doc).OfClass(typeof(Wall)).Cast<Wall>().Select(w => w.GetTypeId()));
        foreach (var t in new FilteredElementCollector(Doc).OfClass(typeof(WallType)).Cast<WallType>())
        {
            if (!t.Name.StartsWith("BIMOne_", StringComparison.OrdinalIgnoreCase)) continue;
            if (!usedWallTypes.Contains(t.Id)) orphanIds.Add(t.Id);
        }

        var usedFloorTypes = new HashSet<ElementId>(
            new FilteredElementCollector(Doc).OfClass(typeof(Floor)).Cast<Floor>().Select(f => f.GetTypeId()));
        foreach (var t in new FilteredElementCollector(Doc).OfClass(typeof(FloorType)).Cast<FloorType>())
        {
            if (!t.Name.StartsWith("BIMOne_", StringComparison.OrdinalIgnoreCase)) continue;
            if (!usedFloorTypes.Contains(t.Id)) orphanIds.Add(t.Id);
        }

        foreach (var id in orphanIds)
        {
            try { Doc.Delete(id); removed++; } catch { /* 有引用或受保护，跳过 */ }
        }
        return removed;
    }

    public bool WaitForCompletion(int timeoutMilliseconds = 600000)
    {
        // SetParameters already reset the event; do not reset again here.
        return _resetEvent.WaitOne(timeoutMilliseconds);
    }

    public string GetName() => "CAD翻模清理";
}
