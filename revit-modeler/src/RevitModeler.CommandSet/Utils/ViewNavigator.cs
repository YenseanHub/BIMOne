using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitModeler.CommandSet.Utils;

/// <summary>
/// 视图定位：把活动视图对到给定构件上。
///
/// 为什么需要这个：翻模用 CAD 真实坐标，模型常偏离项目原点上百米（本例 118m）。
/// 三维视图的相机停在原点附近且朝 (-X,+Y) 看，模型在 (+X) 背后 —— 屏幕上就是空的。
/// 实测 `UIView.ZoomAndCenterRectangle` 对三维视图**不会移动相机**（EyePosition 前后完全一致），
/// 因此三维视图必须用 `View3D.SetOrientation` 重设相机，再叠加 ZoomAndCenterRectangle 适配视野。
/// </summary>
public static class ViewNavigator
{
    /// <summary>
    /// 把活动视图定位到给定构件集合。返回可读的结果描述（含失败原因），便于回传给调用方。
    /// </summary>
    public static string Focus(Document doc, UIDocument uidoc, IList<ElementId> ids, string viewKind = "auto")
    {
        if (doc is null || uidoc is null) return "定位跳过：缺少文档";
        if (ids is null || ids.Count == 0) return "定位跳过：没有构件";

        var view = doc.ActiveView;
        if (view is null) return "定位跳过：没有活动视图";

        if (!TryGetBounds(doc, ids, out var min, out var max, out int withBox, out string boxError))
            return $"定位失败：{boxError}";

        string switchNote = "";

        // plan：切到模型所在标高的平面视图（看墙布局本来就该用平面，三维只是辅助）
        if (string.Equals(viewKind, "plan", StringComparison.OrdinalIgnoreCase))
        {
            var plan = FindPlanForElevation(doc, min.Z);
            if (plan is not null)
            {
                uidoc.ActiveView = plan;
                view = plan;
                switchNote = $"已切到平面视图「{plan.Name}」；";
            }
            else
            {
                switchNote = "找不到对应的平面视图，沿用当前视图；";
            }
        }

        // 结构规程会隐藏非承重建筑墙（实测：149 面墙 bbox/几何全正常、结构规程里导出图空白，
        // 切到协调后立即可见）。定位模型时把结构规程切到协调，平面/三维视图都受影响。

        // 平面视图的视图范围常比构件矮（本例顶部仅 600mm，墙高 3000）：不调范围，墙就是不出现
        string rangeNote = "";
        if (view is ViewPlan viewPlan)
            rangeNote = FixViewRange(doc, viewPlan, min, max);

        // auto/threeD 且当前视图画不出模型时，开一个干净的三维视图，别和坏配置较劲。
        // 判定必须用 bbox-in-view：`FilteredElementCollector(doc, viewId)` 在部分上下文
        // 只返回符号类元素（实测 149 面墙、149 个 bbox 都非空，收集器却返回 0），不可信。
        if (!switchNote.StartsWith("已切到") && !ViewCanShowModel(doc, view))
        {
            var fresh = CreateFreshThreeD(doc, uidoc);
            if (fresh is not null)
            {
                switchNote = $"原视图「{view.Name}」不收录模型，已切到新建的「{fresh.Name}」；";
                view = fresh;
            }
        }

        // 视图确定后再修规程：新建的三维视图会继承样板默认的「结构」规程，同样要切
        string disciplineNote = FixDiscipline(doc, view);

        var center = (min + max) * 0.5;
        double diag = (max - min).GetLength();
        if (diag <= 0) diag = 1000;

        // 留 10% 边距；退化尺寸给 1ft 兜底，避免零尺寸矩形
        double padX = System.Math.Max((max.X - min.X) * 0.1, 1.0);
        double padY = System.Math.Max((max.Y - min.Y) * 0.1, 1.0);
        double padZ = System.Math.Max((max.Z - min.Z) * 0.1, 1.0);
        var lo = new XYZ(min.X - padX, min.Y - padY, min.Z - padZ);
        var hi = new XYZ(max.X + padX, max.Y + padY, max.Z + padZ);

        string cameraNote = "";
        if (view is View3D view3d)
        {
            cameraNote = MoveCamera(doc, view3d, center, diag);
        }

        // 相机到位后再适配视野（平面视图完全依赖这一步）
        string zoomNote = "未缩放";
        try
        {
            var uiView = uidoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == view.Id);
            if (uiView is null) zoomNote = "活动视图没有打开的 UIView";
            else
            {
                uiView.ZoomAndCenterRectangle(lo, hi);
                uidoc.RefreshActiveView();
                zoomNote = "已适配视野";
            }
        }
        catch (Exception ex)
        {
            zoomNote = $"适配视野失败 {ex.GetType().Name}: {ex.Message}";
        }

        string range = $"X {min.X * 304.8:F0}~{max.X * 304.8:F0} / Y {min.Y * 304.8:F0}~{max.Y * 304.8:F0} / Z {min.Z * 304.8:F0}~{max.Z * 304.8:F0} mm";
        return $"{switchNote}视图「{view.Name}」已定位到 {withBox} 个构件，范围 {range}；{disciplineNote}{rangeNote}{cameraNote}{zoomNote}";
    }

    /// <summary>
    /// 结构规程隐藏非承重建筑墙：实测 Revit 2025 中规程=结构的视图（平面/三维一样）
    /// 不绘制 structuralUsage=非承重 的建筑墙——bbox/几何/阶段/可见性全部正常，
    /// 就是不画，连 ImageExportOptions 导出也空白。定位模型时把结构规程切到协调并回报原值。
    /// </summary>
    private static string FixDiscipline(Document doc, View view)
    {
        try
        {
            var p = view.get_Parameter(BuiltInParameter.VIEW_DISCIPLINE);
            if (p is null || p.IsReadOnly) return "";
            string oldName = p.AsValueString();
            if (string.IsNullOrEmpty(oldName) || !oldName.Contains("结构")) return "";

            using var tx = QuietTransaction.Begin(doc, "CAD翻模-视图规程");
            p.Set(5);  // 5 = 协调（Coordination），显示全部专业
            tx.Commit();
            return $"视图规程「{oldName}」→ 协调（结构规程隐藏非承重建筑墙）；";
        }
        catch (Exception ex)
        {
            return $"规程调整失败：{ex.Message}；";
        }
    }

    /// <summary>
    /// 把平面视图的顶部裁剪面和剖切面调到能装下模型。
    /// 视图范围默认只到 600mm 左右，墙高 3000 时整面墙都在裁剪面之上，屏幕上什么都不画。
    /// </summary>
    private static string FixViewRange(Document doc, ViewPlan plan, XYZ min, XYZ max)
    {
        try
        {
            var range = plan.GetViewRange();
            double levelElev = plan.GenLevel?.Elevation ?? 0;
            double modelTop = max.Z - levelElev;

            double top = modelTop + CadUnits.ToFeet(200);          // 顶部高出模型 200mm
            double cut = modelTop * 0.5;                            // 剖切面落在墙高中部
            if (cut < CadUnits.ToFeet(1200)) cut = CadUnits.ToFeet(1200);

            var currentTop = range.GetOffset(PlanViewPlane.TopClipPlane);
            if (currentTop >= top) return "";                       // 已经够高，不折腾

            using var tx = QuietTransaction.Begin(doc, "CAD翻模-调整视图范围");
            range.SetOffset(PlanViewPlane.TopClipPlane, top);
            range.SetOffset(PlanViewPlane.CutPlane, cut);
            plan.SetViewRange(range);
            tx.Commit();

            return $"视图范围已调高（顶部 {top * 304.8:F0}mm、剖切 {cut * 304.8:F0}mm）；";
        }
        catch (Exception ex)
        {
            return $"视图范围调整失败：{ex.Message}；";
        }
    }

    /// <summary>按标高高度找可用的平面视图（优先视图名含该标高名的平面）。</summary>
    private static ViewPlan FindPlanForElevation(Document doc, double elevationFt)
    {
        try
        {
            var plans = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(v => !v.IsTemplate && v.GenLevel is not null)
                .OrderBy(v => System.Math.Abs(v.GenLevel.Elevation - elevationFt))
                .ToList();
            return plans.FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>
    /// 视图能否画出模型构件：取一面墙问它在该视图里有没有包围盒。
    /// 返回 null 说明 Revit 认为它在这个视图里没有图形——比收集器可信。
    /// </summary>
    private static bool ViewCanShowModel(Document doc, View view)
    {
        try
        {
            var any = new FilteredElementCollector(doc).OfClass(typeof(Wall)).FirstElement()
                ?? new FilteredElementCollector(doc).OfClass(typeof(Floor)).FirstElement();
            if (any is null) return true;   // 文档里没有模型构件，无从谈起
            return any.get_BoundingBox(view) is not null;
        }
        catch { return true; }              // 判定失败时保守起见复用当前视图
    }

    /// <summary>
    /// 新建一个三维视图并设为活动视图。用于当前三维视图因裁剪/可见性配置看不到模型的情况。
    /// </summary>
    private static View3D CreateFreshThreeD(Document doc, UIDocument uidoc)
    {
        try
        {
            var familyType = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(t => t.ViewFamily == ViewFamily.ThreeDimensional);
            if (familyType is null) return null;

            View3D fresh;
            using (var tx = QuietTransaction.Begin(doc, "CAD翻模-新建三维视图"))
            {
                fresh = View3D.CreateIsometric(doc, familyType.Id);
                fresh.Name = $"BIMOne 三维 {DateTime.Now:HHmmss}";
                tx.Commit();
            }

            uidoc.ActiveView = fresh;
            return fresh;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>三维视图：把相机放到模型斜上方，朝模型中心看。</summary>
    private static string MoveCamera(Document doc, View3D view3d, XYZ center, double diag)
    {
        try
        {
            if (view3d.IsLocked)
            {
                try { view3d.Unlock(); } catch { /* 锁不住就算了 */ }
            }

            // 东南方向的等轴测视角：从 (+X, -Y, +Z) 一侧俯视
            var forward = new XYZ(-1, 1, -0.8).Normalize();
            // SetOrientation 要求 up ⊥ forward：把世界 Z 投影到垂直于视线的平面上再归一化
            var up = (XYZ.BasisZ - forward * forward.DotProduct(XYZ.BasisZ)).Normalize();
            var eye = center - forward * (diag * 1.1);

            // ⚠ 实测（fanmo_diagnose 读回相机参数逐位吻合）：Revit 对 ViewOrientation3D
            // 的 forward/up 存储顺序与文档签名 (eye, forward, up) 相反——按文档传参后
            // GetOrientation().ForwardDirection 返回的是我们传入的 up，视图朝天上方向看，屏幕全空。
            // 因此这里交换传参，让存储的 ForwardDirection 真正指向模型中心。
            var orientation = new ViewOrientation3D(eye, up, forward);

            using var tx = QuietTransaction.Begin(doc, "CAD翻模-定位三维视图");
            if (view3d.IsSectionBoxActive) view3d.IsSectionBoxActive = false;  // 剖面框会把模型裁没
            view3d.SetOrientation(orientation);
            tx.Commit();

            return $"相机移至 ({eye.X * 304.8:F0},{eye.Y * 304.8:F0},{eye.Z * 304.8:F0}) mm";
        }
        catch (Exception ex)
        {
            return $"相机定位失败 {ex.GetType().Name}: {ex.Message}；";
        }
    }

    private static bool TryGetBounds(Document doc, IList<ElementId> ids,
        out XYZ min, out XYZ max, out int withBox, out string error)
    {
        min = null; max = null; withBox = 0; error = null;
        foreach (var id in ids)
        {
            var bb = doc.GetElement(id)?.get_BoundingBox(null);
            if (bb is null) continue;
            withBox++;
            min = min is null
                ? bb.Min
                : new XYZ(System.Math.Min(min.X, bb.Min.X), System.Math.Min(min.Y, bb.Min.Y), System.Math.Min(min.Z, bb.Min.Z));
            max = max is null
                ? bb.Max
                : new XYZ(System.Math.Max(max.X, bb.Max.X), System.Math.Max(max.Y, bb.Max.Y), System.Math.Max(max.Z, bb.Max.Z));
        }
        if (min is null || max is null)
        {
            error = $"{ids.Count} 个构件都没有包围盒";
            return false;
        }
        return true;
    }
}
