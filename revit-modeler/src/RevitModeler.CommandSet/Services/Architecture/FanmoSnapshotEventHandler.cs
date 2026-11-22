using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPSDK.API.Interfaces;
using RevitModeler.CommandSet.Models.Common;
using RevitModeler.CommandSet.Utils;

namespace RevitModeler.CommandSet.Services.Architecture;

/// <summary>
/// 导出活动视图为 PNG。用于「模型明明建了但屏幕上什么都没有」这类只能靠眼睛确认的问题——
/// 与其让用户反复描述，不如把视图本身截下来当证据。
/// </summary>
public class FanmoSnapshotEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
{
    private UIApplication _uiApp;
    private readonly ManualResetEvent _resetEvent = new(false);

    /// <summary>输出目录；为空时写到临时目录。</summary>
    public string OutputDir { get; private set; }
    /// <summary>像素宽度，越大越清晰。</summary>
    public int PixelSize { get; private set; } = 1600;

    /// <summary>
    /// 按模型包围盒临时开裁剪再导出。
    /// 不开裁剪时导出框取的是"视图范围 + 所有注释"的并集，立面标记常把范围撑到几百米，
    /// 模型被挤到画面边角甚至框外——导出的图看着像空的，其实是取景错了。
    /// </summary>
    public bool FitModel { get; private set; } = true;

    public AIResult<string> Result { get; private set; }

    public void SetParameters(string outputDir, int pixelSize = 1600, bool fitModel = true)
    {
        OutputDir = outputDir;
        PixelSize = pixelSize > 0 ? pixelSize : 1600;
        FitModel = fitModel;
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
            var view = doc.ActiveView;
            if (view is null) { Fail("没有活动视图"); return; }

            var dir = string.IsNullOrWhiteSpace(OutputDir)
                ? System.IO.Path.GetTempPath().TrimEnd('\\')
                : OutputDir;
            System.IO.Directory.CreateDirectory(dir);

            var baseName = $"BIMOne_Snapshot_{DateTime.Now:HHmmss}";

            // 平面/剖面视图：临时把裁剪框收到模型范围，再按裁剪区导出，
            // 这样导出的图一定包含模型，不受立面标记撑大范围的影响。
            bool cropped = false;
            BoundingBoxXYZ savedBox = null;
            bool savedActive = false;
            try
            {
                if (FitModel && view is not View3D)
                {
                    var bounds = ModelBounds(doc);
                    if (bounds is not null)
                    {
                        using (var tx = QuietTransaction.Begin(doc, "CAD翻模-截图取景"))
                        {
                            savedBox = view.CropBox;
                            savedActive = view.CropBoxActive;
                            var crop = new BoundingBoxXYZ
                            {
                                Min = new XYZ(bounds.Min.X, bounds.Min.Y, savedBox?.Min.Z ?? bounds.Min.Z),
                                Max = new XYZ(bounds.Max.X, bounds.Max.Y, savedBox?.Max.Z ?? bounds.Max.Z),
                                Transform = savedBox?.Transform ?? Transform.Identity
                            };
                            view.CropBox = crop;
                            view.CropBoxActive = true;
                            tx.Commit();
                        }
                        cropped = true;
                    }
                }
            }
            catch { /* 取景失败就退回原样导出，至少还能给出一张图 */ }

            var options = new ImageExportOptions
            {
                FilePath = System.IO.Path.Combine(dir, baseName),
                ZoomType = cropped ? ZoomFitType.FitToPage : ZoomFitType.Zoom,
                PixelSize = PixelSize,
                HLRandWFViewsFileType = ImageFileType.PNG,
                ShadowViewsFileType = ImageFileType.PNG,
                ImageResolution = ImageResolution.DPI_150,
                ShouldCreateWebSite = false,
                ExportRange = ExportRange.SetOfViews
            };
            options.SetViewsAndSheets(new List<ElementId> { view.Id });

            try
            {
                doc.ExportImage(options);
            }
            finally
            {
                if (cropped) RestoreCrop(doc, view, savedBox, savedActive);
            }

            // Revit 会在文件名后追加 " - <视图名>"，这里把真实产物找出来
            var produced = System.IO.Directory.GetFiles(dir, baseName + "*.png");
            if (produced.Length == 0) { Fail($"导出未产生文件（目录 {dir}，前缀 {baseName}）"); return; }

            var file = produced[0];
            var size = new System.IO.FileInfo(file).Length;
            Success($"已导出视图「{view.Name}」({view.ViewType}) 到 {file}（{size / 1024}KB）");
        }
        catch (Exception ex)
        {
            Fail($"截图失败: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _resetEvent.Set();
        }
    }

    /// <summary>翻模构件的整体包围盒（不含注释），留 5% 边距。</summary>
    private static BoundingBoxXYZ ModelBounds(Document doc)
    {
        double[] r = { double.MaxValue, double.MaxValue, double.MaxValue, double.MinValue, double.MinValue, double.MinValue };
        var found = false;

        var cats = new[]
        {
            BuiltInCategory.OST_Walls,
            BuiltInCategory.OST_Floors,
            BuiltInCategory.OST_StructuralColumns,
            BuiltInCategory.OST_StructuralFraming
        };

        foreach (var bic in cats)
        {
            foreach (var e in new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType())
            {
                var bb = e.get_BoundingBox(null);
                if (bb is null) continue;
                found = true;
                r[0] = System.Math.Min(r[0], bb.Min.X); r[1] = System.Math.Min(r[1], bb.Min.Y); r[2] = System.Math.Min(r[2], bb.Min.Z);
                r[3] = System.Math.Max(r[3], bb.Max.X); r[4] = System.Math.Max(r[4], bb.Max.Y); r[5] = System.Math.Max(r[5], bb.Max.Z);
            }
        }

        if (!found) return null;

        double padX = System.Math.Max((r[3] - r[0]) * 0.05, 0.5);
        double padY = System.Math.Max((r[4] - r[1]) * 0.05, 0.5);
        return new BoundingBoxXYZ
        {
            Min = new XYZ(r[0] - padX, r[1] - padY, r[2]),
            Max = new XYZ(r[3] + padX, r[4] + padY, r[5])
        };
    }

    private static void RestoreCrop(Document doc, View view, BoundingBoxXYZ box, bool active)
    {
        try
        {
            using var tx = QuietTransaction.Begin(doc, "CAD翻模-还原截图取景");
            if (box is not null) view.CropBox = box;
            view.CropBoxActive = active;
            tx.Commit();
        }
        catch { /* 还原失败不影响导出结果 */ }
    }

    private void Success(string message) =>
        Result = new AIResult<string> { Success = true, Message = message, Response = message };

    private void Fail(string message) =>
        Result = new AIResult<string> { Success = false, Message = message };

    public bool WaitForCompletion(int timeoutMilliseconds = 120000) => _resetEvent.WaitOne(timeoutMilliseconds);

    public string GetName() => "CAD翻模-视图截图";
}
