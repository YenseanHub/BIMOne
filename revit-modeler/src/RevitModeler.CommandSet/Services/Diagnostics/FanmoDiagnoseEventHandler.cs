using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPSDK.API.Interfaces;
using RevitModeler.CommandSet.Models.Common;
using RevitModeler.CommandSet.Utils;

namespace RevitModeler.CommandSet.Services.Diagnostics;

/// <summary>
/// 只读诊断：导出文档类型库的真实能力（墙宽/板厚参数是否可写、柱梁族是否具备 b/h 截面参数），
/// 用于定位“厚度写不进去/族缺失”这类只能靠运行时确认的问题。
/// </summary>
public class FanmoDiagnoseEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
{
    private UIApplication _uiApp;
    private Document Doc => _uiApp.ActiveUIDocument.Document;
    private readonly ManualResetEvent _resetEvent = new(false);

    public AIResult<object> Result { get; private set; }

    private string _probeFloorType;
    private double _probeThicknessMm = 100;
    private bool _createTestWall;
    private bool _forceRefresh;
    private int _setDiscipline = -1;

    public void SetParameters(string probeFloorType = null, double probeThicknessMm = 100, bool createTestWall = false, bool forceRefresh = false, int setDiscipline = -1)
    {
        _probeFloorType = probeFloorType;
        if (probeThicknessMm > 0) _probeThicknessMm = probeThicknessMm;
        _createTestWall = createTestWall;
        _forceRefresh = forceRefresh;
        _setDiscipline = setDiscipline;
        _resetEvent.Reset();
    }

    public void Execute(UIApplication uiapp)
    {
        _uiApp = uiapp;
        try
        {
            var levels = new FilteredElementCollector(Doc).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.Elevation)
                .Select(l => new { name = l.Name, elevationMm = CadUnits.ToMillimeters(l.Elevation) })
                .ToList();

            var wallTypes = new FilteredElementCollector(Doc).OfClass(typeof(WallType)).Cast<WallType>()
                .Select(t =>
                {
                    var p = t.get_Parameter(BuiltInParameter.WALL_ATTR_WIDTH_PARAM);
                    return new
                    {
                        name = t.Name,
                        kind = t.Kind.ToString(),
                        widthParamExists = p is not null,
                        widthParamReadOnly = p?.IsReadOnly ?? true,
                        widthMm = p is not null && p.HasValue ? CadUnits.ToMillimeters(p.AsDouble()) : -1,
                        usable = DocumentUtils.IsUsableWallType(t)
                    };
                })
                .ToList();

            var floorTypes = new FilteredElementCollector(Doc).OfClass(typeof(FloorType)).Cast<FloorType>()
                .Select(t =>
                {
                    var p = t.get_Parameter(BuiltInParameter.FLOOR_ATTR_THICKNESS_PARAM);
                    return new
                    {
                        name = t.Name,
                        thicknessParamExists = p is not null,
                        thicknessParamReadOnly = p?.IsReadOnly ?? true,
                        thicknessMm = DocumentUtils.GetFloorThicknessMm(t)
                    };
                })
                .ToList();

            var columnSymbols = ReadSymbols(BuiltInCategory.OST_StructuralColumns);
            var beamSymbols = ReadSymbols(BuiltInCategory.OST_StructuralFraming);

            Result = new AIResult<object>
            {
                Success = true,
                Message = $"诊断完成: 墙类型 {wallTypes.Count}(可用 {wallTypes.Count(w => w.usable)}) / 板类型 {floorTypes.Count} / 柱族 {columnSymbols.Count} / 梁族 {beamSymbols.Count}",
                Response = new
                {
                    document = Doc.Title,
                    levels,
                    counts = new
                    {
                        walls = new FilteredElementCollector(Doc).OfClass(typeof(Wall)).GetElementCount(),
                        floors = new FilteredElementCollector(Doc).OfClass(typeof(Floor)).GetElementCount(),
                        columns = new FilteredElementCollector(Doc).OfCategory(BuiltInCategory.OST_StructuralColumns)
                            .WhereElementIsNotElementType().GetElementCount(),
                        beams = new FilteredElementCollector(Doc).OfCategory(BuiltInCategory.OST_StructuralFraming)
                            .WhereElementIsNotElementType().GetElementCount()
                    },
                    wallTypes,
                    floorTypes,
                    columnSymbols,
                    beamSymbols,
                    view = ReadActiveView(),
                    wallExtent = ReadWallExtent(),
                    wallPhases = ReadWallPhases(),
                    wallGeometry = ReadWallGeometry(),
                    visibilityProbe = ReadVisibilityProbe(),
                    floorProbe = string.IsNullOrWhiteSpace(_probeFloorType)
                        ? null
                        : ProbeFloorThickness(_probeFloorType, _probeThicknessMm),
                    testWall = _createTestWall ? CreateTestWall() : null,
                    testLine = _createTestWall ? CreateControlLines() : null,
                    refresh = _forceRefresh ? ForceRefreshGraphics() : null,
                    setDiscipline = _setDiscipline >= 0 ? SetDisciplineProbe(_setDiscipline) : null,
                    wallTypeInfo = ReadWallTypeInfo(),
                    docInventory = ReadDocInventory()
                }
            };
        }
        catch (Exception ex)
        {
            Result = new AIResult<object> { Success = false, Message = $"诊断失败: {ex.Message}" };
        }
        finally
        {
            _resetEvent.Set();
        }
    }

    /// <summary>
    /// 活动视图状态：视图类型/裁剪/剖面框/墙类别可见性/视图坐标系。
    /// 「模型建好了但屏幕上空的」几乎都出在这一层，必须能一眼看到原因。
    /// </summary>
    private object ReadActiveView()
    {
        try
        {
            var av = Doc.ActiveView;
            if (av is null) return new { error = "没有活动视图" };
            var uidoc = _uiApp.ActiveUIDocument;
            var wallsCategory = new ElementId(BuiltInCategory.OST_Walls);

            object sectionBox = null;
            // 剖面框只存在于三维视图
            var view3d = av as View3D;
            if (view3d is not null && view3d.IsSectionBoxActive)
            {
                var sb = view3d.GetSectionBox();
                var t = sb.Transform;
                sectionBox = new
                {
                    min = new[] { CadUnits.ToMillimeters(sb.Min.X), CadUnits.ToMillimeters(sb.Min.Y), CadUnits.ToMillimeters(sb.Min.Z) },
                    max = new[] { CadUnits.ToMillimeters(sb.Max.X), CadUnits.ToMillimeters(sb.Max.Y), CadUnits.ToMillimeters(sb.Max.Z) },
                    origin = new[] { CadUnits.ToMillimeters(t.Origin.X), CadUnits.ToMillimeters(t.Origin.Y), CadUnits.ToMillimeters(t.Origin.Z) }
                };
            }

            return new
            {
                id = av.Id.Value,
                name = av.Name,
                viewType = av.ViewType.ToString(),
                scale = av.Scale,
                isTemplate = av.IsTemplate,
                detailLevel = av.DetailLevel.ToString(),
                displayStyle = av.DisplayStyle.ToString(),
                planInfo = ReadPlanInfo(av as ViewPlan),
                cropActive = av.CropBoxActive,
                cropVisible = av.CropBoxVisible,
                wallsHiddenInView = av.GetCategoryHidden(wallsCategory),
                sectionBoxActive = view3d?.IsSectionBoxActive ?? false,
                sectionBox,
                isPerspective = view3d?.IsPerspective,
                isLocked = view3d?.IsLocked,
                orientation = ReadOrientation(view3d),
                // 模型建了却不显示，问题常在下面这几项：视图是否收录墙、阶段是否匹配、是否有过滤器
                wallsInThisView = new FilteredElementCollector(Doc, av.Id).OfClass(typeof(Wall)).GetElementCount(),
                farClipEnabled = ReadParamInt(av, BuiltInParameter.VIEWER_BOUND_FAR_CLIPPING),
                farClipOffsetMm = ReadParamMm(av, BuiltInParameter.VIEWER_BOUND_OFFSET_FAR),
                designOption = ReadPhaseName(av.get_Parameter(BuiltInParameter.VIEWER_OPTION_VISIBILITY)?.AsElementId()),
                viewPhase = ReadPhaseName(av.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId()),
                viewPhaseFilter = ReadPhaseName(av.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER)?.AsElementId()),
                filters = ReadViewFilters(),
                originMm = new[] { CadUnits.ToMillimeters(av.Origin.X), CadUnits.ToMillimeters(av.Origin.Y), CadUnits.ToMillimeters(av.Origin.Z) },
                openUIViews = uidoc?.GetOpenUIViews().Select(v => v.ViewId.Value).ToList()
            };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    /// <summary>文档中墙的整体范围，以及有多少墙在当前视图被隐藏（隔离/隐藏命令）。</summary>
    private object ReadWallExtent()
    {
        try
        {
            var walls = new FilteredElementCollector(Doc).OfClass(typeof(Wall)).Cast<Wall>().ToList();
            if (walls.Count == 0) return new { count = 0 };

            var view = Doc.ActiveView;
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            int noBox = 0, hidden = 0;

            foreach (var w in walls)
            {
                var bb = w.get_BoundingBox(null);
                if (bb is null) { noBox++; continue; }
                minX = System.Math.Min(minX, bb.Min.X); minY = System.Math.Min(minY, bb.Min.Y); minZ = System.Math.Min(minZ, bb.Min.Z);
                maxX = System.Math.Max(maxX, bb.Max.X); maxY = System.Math.Max(maxY, bb.Max.Y); maxZ = System.Math.Max(maxZ, bb.Max.Z);
                if (view is not null && w.IsHidden(view)) hidden++;
            }

            return new
            {
                count = walls.Count,
                noBoundingBox = noBox,
                hiddenInActiveView = hidden,
                minMm = new[] { minX * 304.8, minY * 304.8, minZ * 304.8 },
                maxMm = new[] { maxX * 304.8, maxY * 304.8, maxZ * 304.8 }
            };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    /// <summary>三维视图相机：眼点、朝向、上方向（mm）。相机停在原点附近而模型在百米外，就是空屏。</summary>
    /// <summary>
    /// 抽 5 面墙检查真实几何：Solid 数量与体积。包围盒有值不代表有实体——
    /// 退化墙（高度/厚度为 0）能算出 bbox 却画不出东西，这正是「屏幕空白」的典型成因。
    /// </summary>
    private object ReadWallGeometry()
    {
        try
        {
            var walls = new FilteredElementCollector(Doc).OfClass(typeof(Wall)).Cast<Wall>().Take(5).ToList();
            if (walls.Count == 0) return new { count = 0 };

            var options = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine };
            var details = walls.Select(w =>
            {
                int solids = 0;
                double volume = 0;
                try
                {
                    var geo = w.get_Geometry(options);
                    if (geo is not null)
                    {
                        foreach (var obj in geo)
                        {
                            if (obj is Solid s && s.Faces.Size > 0)
                            {
                                solids++;
                                volume += s.Volume;
                            }
                        }
                    }
                }
                catch (Exception ex) { return (object)new { id = w.Id.Value, error = ex.Message }; }

                var loc = (w.Location as LocationCurve)?.Curve as Line;
                return (object)new
                {
                    id = w.Id.Value,
                    typeName = w.WallType?.Name ?? "",
                    kind = w.WallType?.Kind.ToString() ?? "",
                    solids,
                    volumeM3 = volume * 0.0283168,
                    lengthMm = loc is null ? -1 : CadUnits.ToMillimeters(loc.Length)
                };
            }).ToList();

            return new { sampled = walls.Count, details };
        }
        catch (Exception ex) { return new { error = ex.Message }; }
    }

    /// <summary>
    /// 「建了但看不到」的全字段探针：把能挡住元素显示的机制逐个读出来
    /// （工作集可见性、设计选项、元素级覆盖、类别隐藏、所属视图）。
    /// </summary>
    private object ReadVisibilityProbe()
    {
        try
        {
            var wall = new FilteredElementCollector(Doc).OfClass(typeof(Wall)).FirstElement() as Wall;
            if (wall is null) return new { error = "文档里没有墙" };
            var view = Doc.ActiveView;

            string worksetName = null;
            string worksetVisibility = null;
            if (Doc.IsWorkshared)
            {
                try
                {
                    var ws = Doc.GetWorksetTable().GetWorkset(wall.WorksetId);
                    worksetName = ws?.Name;
                    worksetVisibility = view.GetWorksetVisibility(wall.WorksetId).ToString();
                }
                catch (Exception ex) { worksetName = "读取失败: " + ex.Message; }
            }

            return new
            {
                docTitle = Doc.Title,
                docPath = string.IsNullOrEmpty(Doc.PathName) ? "(未保存)" : Doc.PathName,
                isWorkshared = Doc.IsWorkshared,
                viewName = view.Name,
                wallId = wall.Id.Value,
                category = wall.Category?.Name,
                categoryId = wall.Category?.Id.Value,
                worksetId = wall.WorksetId.IntegerValue,
                worksetName,
                worksetVisibility,
                designOption = wall.DesignOption?.Name ?? "(主模型)",
                isHiddenInView = wall.IsHidden(view),
                canBeHidden = wall.CanBeHidden(view),
                // 材质：墙若被赋予白色/无材质，在着色模式下与背景同色就"看不见"
                wallTypeMaterials = ReadWallTypeMaterials(wall),
                ownerViewId = wall.OwnerViewId.Value,
                isElementType = wall.IsValidObject,
                inViewCollector = new FilteredElementCollector(Doc, view.Id).ToElementIds().Count,
                levelsInViewCollector = new FilteredElementCollector(Doc, view.Id).OfClass(typeof(Level)).GetElementCount(),

                // ---- 全局可见性开关（构件在文档里、几何正常，却在所有视图都画不出来时的嫌疑项）----
                viewTemplate = ReadTemplateName(view),
                categoryHidden = view.GetCategoryHidden(new ElementId(BuiltInCategory.OST_Walls)),
                discipline = view.get_Parameter(BuiltInParameter.VIEW_DISCIPLINE)?.AsValueString(),
                temporaryHideIsolate = view.IsInTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate),
                temporaryViewProperties = view.IsInTemporaryViewMode(TemporaryViewMode.TemporaryViewProperties),

                // 直接问 Revit：这面墙在这个视图里到底有没有图形
                bboxInView = ReadBBoxInView(wall, view),
                facesInView = ReadFacesInView(wall, view),
                inViewCollectorByCategory = new FilteredElementCollector(Doc, view.Id)
                    .OfCategory(BuiltInCategory.OST_Walls).GetElementCount(),

                // 全量统计：有多少面墙在活动视图里真的有图形（bbox 非空）
                wallsWithGeometryInView = CountWallsVisibleInView(view),

                // 逐元素图形覆盖：被设成"不可见"的覆盖不会让 IsHidden 变 true，但就是不画
                elementOverride = ReadElementOverride(wall, view),
                categoryOverride = ReadCategoryOverride(view, BuiltInCategory.OST_Walls),
                objectStyleWalls = ReadObjectStyle(BuiltInCategory.OST_Walls),
                objectStyleLevels = ReadObjectStyle(BuiltInCategory.OST_Levels),
                elementsInView = ReadElementsInView(view)
            };
        }
        catch (Exception ex) { return new { error = ex.Message }; }
    }

    /// <summary>文档里所有实例元素按类别清点，用于发现"盖在上面的大块填充"之类意外元素。</summary>
    private object ReadDocInventory()
    {
        try
        {
            var all = new FilteredElementCollector(Doc).WhereElementIsNotElementType().ToElements();
            var byCat = all
                .GroupBy(e => e.Category?.Name ?? "(无类别)")
                .Select(g => new { category = g.Key, count = g.Count() })
                .OrderByDescending(g => g.count)
                .Take(25)
                .ToList();
            return new { total = all.Count, byCategory = byCat };
        }
        catch (Exception ex) { return new { error = ex.Message }; }
    }

    /// <summary>
    /// 强制重生成几何 + 刷新视图。用于验证「元素在库里但屏幕不画」是否只是图形未刷新。
    /// </summary>
    private object ForceRefreshGraphics()
    {
        try
        {
            // Regenerate 本身被 Revit 视为文档修改，必须在事务内
            using (var tx = QuietTransaction.Begin(Doc, "CAD翻模-强制重生成"))
            {
                Doc.Regenerate();
                tx.Commit();
            }
            var uidoc = _uiApp?.ActiveUIDocument;
            uidoc?.RefreshActiveView();
            return new { ok = true, activeView = uidoc?.ActiveView?.Name };
        }
        catch (Exception ex) { return new { ok = false, error = $"{ex.GetType().Name}: {ex.Message}" }; }
    }

    /// <summary>
    /// 规程探针：把活动视图规程切到指定值（0=建筑 1=结构 5=协调）。用于验证
    /// 「规程=结构时非承重建筑墙不画」这类只有实测才能确认的显示规则。
    /// </summary>
    private object SetDisciplineProbe(int discipline)
    {
        try
        {
            var view = Doc.ActiveView;
            var p = view.get_Parameter(BuiltInParameter.VIEW_DISCIPLINE);
            if (p is null || p.IsReadOnly) return new { ok = false, error = "规程参数不存在或只读" };
            int old = p.AsInteger();
            using var tx = QuietTransaction.Begin(Doc, "CAD翻模-规程探针");
            p.Set(discipline);
            tx.Commit();
            return new { ok = true, oldDiscipline = old, newDiscipline = discipline };
        }
        catch (Exception ex) { return new { ok = false, error = $"{ex.GetType().Name}: {ex.Message}" }; }
    }

    /// <summary>第一面墙的类型/族/实心几何厚度——墙体若退化成零厚度，平面里就没有可画的截面。</summary>
    private object ReadWallTypeInfo()
    {
        try
        {
            var wall = new FilteredElementCollector(Doc).OfClass(typeof(Wall)).Cast<Wall>().FirstOrDefault();
            if (wall is null) return new { error = "无墙" };
            var bb = wall.get_BoundingBox(null);
            var dims = bb is null
                ? null
                : new[]
                  {
                      CadUnits.ToMillimeters(bb.Max.X - bb.Min.X),
                      CadUnits.ToMillimeters(bb.Max.Y - bb.Min.Y),
                      CadUnits.ToMillimeters(bb.Max.Z - bb.Min.Z)
                  };
            return new
            {
                id = wall.Id.Value,
                typeName = wall.WallType?.Name,
                kind = wall.WallType?.Kind.ToString(),
                function = wall.WallType?.Function.ToString(),
                structuralUsage = wall.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_USAGE_PARAM)?.AsValueString(),
                widthParamMm = ReadParamMm(wall, BuiltInParameter.WALL_ATTR_WIDTH_PARAM),
                bboxDimsMm = dims,
                unconnectedHeightMm = ReadParamMm(wall, BuiltInParameter.WALL_USER_HEIGHT_PARAM)
            };
        }
        catch (Exception ex) { return new { error = ex.Message }; }
    }

    private static double ReadParamMm(Element e, BuiltInParameter bip)
    {
        var p = e.get_Parameter(bip);
        return p is not null && p.HasValue ? CadUnits.ToMillimeters(p.AsDouble()) : -1;
    }

    private string ReadTemplateName(View view)
    {
        try
        {
            var id = view.ViewTemplateId;
            if (id is null || id == ElementId.InvalidElementId) return "(无)";
            return Doc.GetElement(id)?.Name ?? "(未知模板)";
        }
        catch (Exception ex) { return "读取失败: " + ex.Message; }
    }

    /// <summary>
    /// 构件在指定视图里的包围盒。返回 null 说明 Revit 认为它在这个视图里没有图形——
    /// 这比 `FilteredElementCollector(doc, viewId)` 更直接，不受收集器语义干扰。
    /// </summary>
    private object ReadBBoxInView(Element e, View view)
    {
        try
        {
            var bb = e.get_BoundingBox(view);
            if (bb is null) return null;
            return new
            {
                minMm = new[] { CadUnits.ToMillimeters(bb.Min.X), CadUnits.ToMillimeters(bb.Min.Y), CadUnits.ToMillimeters(bb.Min.Z) },
                maxMm = new[] { CadUnits.ToMillimeters(bb.Max.X), CadUnits.ToMillimeters(bb.Max.Y), CadUnits.ToMillimeters(bb.Max.Z) }
            };
        }
        catch (Exception ex) { return "读取失败: " + ex.Message; }
    }

    /// <summary>按视图取图形：视图模式下 Revit 会裁掉不可见部分，返回面数可直接判断可见性。</summary>
    private object ReadFacesInView(Element e, View view)
    {
        try
        {
            var geo = e.get_Geometry(new Options { View = view, ComputeReferences = false });
            if (geo is null) return new { note = "几何为 null" };
            int solids = 0, faces = 0;
            foreach (var obj in geo)
                if (obj is Solid s && s.Faces.Size > 0) { solids++; faces += s.Faces.Size; }
            return new { solids, faces };
        }
        catch (Exception ex) { return new { error = ex.Message }; }
    }

    private static string Hex(Color c) => c is null || !c.IsValid
        ? null
        : $"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}";

    /// <summary>视图里该类别的 V/G 覆盖：线色/填充色若为白色，在 HLR 白底上就是"消失"。</summary>
    private object ReadCategoryOverride(View view, BuiltInCategory bic)
    {
        try
        {
            var o = view.GetCategoryOverrides(new ElementId(bic));
            return new
            {
                cutLineColor = Hex(o.CutLineColor),
                projectionLineColor = Hex(o.ProjectionLineColor),
                cutForegroundPatternColor = Hex(o.CutForegroundPatternColor),
                cutBackgroundPatternColor = Hex(o.CutBackgroundPatternColor),
                surfaceForegroundPatternColor = Hex(o.SurfaceForegroundPatternColor),
                cutForegroundPatternVisible = o.IsCutForegroundPatternVisible,
                surfaceForegroundPatternVisible = o.IsSurfaceForegroundPatternVisible,
                transparency = o.Transparency,
                halftone = o.Halftone,
                cutLineWeight = o.CutLineWeight,
                projectionLineWeight = o.ProjectionLineWeight
            };
        }
        catch (Exception ex) { return new { error = ex.Message }; }
    }

    /// <summary>文档级对象样式（影响所有视图，包括新建的视图）。</summary>
    private object ReadObjectStyle(BuiltInCategory bic)
    {
        try
        {
            var cat = Category.GetCategory(Doc, bic);
            if (cat is null) return new { error = "类别不存在" };
            return new
            {
                name = cat.Name,
                lineColor = Hex(cat.LineColor),
                cutLineWeight = cat.GetLineWeight(GraphicsStyleType.Cut),
                projectionLineWeight = cat.GetLineWeight(GraphicsStyleType.Projection),
                materialName = cat.Material?.Name
            };
        }
        catch (Exception ex) { return new { error = ex.Message }; }
    }

    /// <summary>逐元素的图形覆盖设置。`IsVisible=false` 时构件在文档里、几何正常，但就是不画。</summary>
    private object ReadElementOverride(Element e, View view)
    {
        try
        {
            var o = view.GetElementOverrides(e.Id);
            return new
            {
                halftone = o.Halftone,
                transparency = o.Transparency,
                cutLineWeight = o.CutLineWeight,
                projectionLineWeight = o.ProjectionLineWeight,
                cutLineColor = o.CutLineColor?.IsValid == true
                    ? $"#{o.CutLineColor.Red:X2}{o.CutLineColor.Green:X2}{o.CutLineColor.Blue:X2}"
                    : null,
                surfaceForegroundPatternVisible = o.IsSurfaceForegroundPatternVisible,
                cutForegroundPatternVisible = o.IsCutForegroundPatternVisible
            };
        }
        catch (Exception ex) { return new { error = ex.Message }; }
    }

    /// <summary>列出视图收集器真正收到的元素，用来看它到底收了什么（收集器语义在不同视图类型上不一致）。</summary>
    private object ReadElementsInView(View view)
    {
        try
        {
            var items = new FilteredElementCollector(Doc, view.Id)
                .WhereElementIsNotElementType()
                .Take(30)
                .Select(e => $"{e.Category?.Name ?? "(no cat)"}:{e.Name}")
                .ToList();
            return new { count = items.Count, sample = items };
        }
        catch (Exception ex) { return new { error = ex.Message }; }
    }

    /// <summary>
    /// 全部墙在活动视图里的真实可见性统计 + 世界坐标分布。
    /// 坐标分布用来抓「有一批墙落在别处」这类问题——模型中心离原点百米时，
    /// 光看总数会以为一切正常。
    /// </summary>
    private object CountWallsVisibleInView(View view)
    {
        try
        {
            var walls = new FilteredElementCollector(Doc).OfClass(typeof(Wall)).Cast<Wall>().ToList();
            int visible = 0;
            var centersX = new List<double>();
            var centersY = new List<double>();
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;

            foreach (var w in walls)
            {
                if (w.get_BoundingBox(view) is not null) visible++;
                var bb = w.get_BoundingBox(null);
                if (bb is null) continue;
                minX = System.Math.Min(minX, bb.Min.X); maxX = System.Math.Max(maxX, bb.Max.X);
                minY = System.Math.Min(minY, bb.Min.Y); maxY = System.Math.Max(maxY, bb.Max.Y);
                centersX.Add(CadUnits.ToMillimeters((bb.Min.X + bb.Max.X) / 2));
                centersY.Add(CadUnits.ToMillimeters((bb.Min.Y + bb.Max.Y) / 2));
            }

            // 按 10m 网格聚类，看墙是「一坨」还是「两坨」
            var buckets = centersX
                .Select(x => System.Math.Round(x / 10000) * 10)
                .GroupBy(v => v).OrderByDescending(g => g.Count())
                .Take(6)
                .Select(g => $"{g.Key:F0}m×{g.Count()}");

            return new
            {
                total = walls.Count,
                visibleInView = visible,
                worldMinMm = walls.Count == 0 ? null : new[] { minX * 304.8, minY * 304.8 },
                worldMaxMm = walls.Count == 0 ? null : new[] { maxX * 304.8, maxY * 304.8 },
                centerXBuckets = buckets,
                centerXMedianMm = centersX.Count == 0 ? (double?)null : centersX.OrderBy(v => v).ElementAt(centersX.Count / 2),
                centerYMedianMm = centersY.Count == 0 ? (double?)null : centersY.OrderBy(v => v).ElementAt(centersY.Count / 2)
            };
        }
        catch (Exception ex) { return new { error = ex.Message }; }
    }

    /// <summary>
    /// 对照实验：用最简参数在原点旁建一面标准墙（3m 长 / 2.8m 高）。
    /// 它若显示而翻模墙不显示，问题就在翻模墙本身；若它也不显示，就是视图/显示系统的问题。
    /// </summary>
    private object CreateTestWall()
    {
        try
        {
            // 用样板自带的墙类型（不是翻模新建的 BIMOne_*），排除"我们的类型被改坏"这条嫌疑
            var wallType = new FilteredElementCollector(Doc).OfClass(typeof(WallType)).Cast<WallType>()
                .FirstOrDefault(t => t.Kind == WallKind.Basic && !t.Name.StartsWith("BIMOne", StringComparison.OrdinalIgnoreCase));
            var level = new FilteredElementCollector(Doc).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => System.Math.Abs(l.Elevation)).FirstOrDefault();
            if (wallType is null || level is null) return new { error = "缺少墙类型或标高" };

            var c = ModelCenterFt();
            using var tx = QuietTransaction.Begin(Doc, "CAD翻模-对照墙");
            var curve = Line.CreateBound(
                new XYZ(c.X, c.Y, level.Elevation),
                new XYZ(c.X + CadUnits.ToFeet(6000), c.Y, level.Elevation));
            var wall = Wall.Create(Doc, curve, wallType.Id, level.Id, CadUnits.ToFeet(2800), 0, false, false);
            tx.Commit();

            int solidCount = 0;
            var geo = wall?.get_Geometry(new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine });
            if (geo is not null)
                foreach (var obj in geo)
                    if (obj is Solid s && s.Faces.Size > 0) solidCount++;

            var view = Doc.ActiveView;
            return new
            {
                id = wall?.Id.Value,
                wallType = wallType.Name,
                level = level.Name,
                solids = solidCount,
                atMm = new[] { CadUnits.ToMillimeters(c.X), CadUnits.ToMillimeters(c.Y) },
                isHiddenInView = wall?.IsHidden(view) ?? true,
                bboxInView = wall is null ? null : ReadBBoxInView(wall, view)
            };
        }
        catch (Exception ex) { return new { error = $"{ex.GetType().Name}: {ex.Message}" }; }
    }

    /// <summary>
    /// 对照实验之二：在模型中心画 4 条模型线围成 20m 方框。
    /// 模型线是最简单的可绘制几何——它若显示而墙不显示，问题就锁定在墙的绘制表示上。
    /// </summary>
    private object CreateControlLines()
    {
        try
        {
            var level = new FilteredElementCollector(Doc).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => System.Math.Abs(l.Elevation)).FirstOrDefault();
            var c = ModelCenterFt();
            double half = CadUnits.ToFeet(10000);

            using var tx = QuietTransaction.Begin(Doc, "CAD翻模-对照线");
            var plane = SketchPlane.Create(Doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(c.X, c.Y, level?.Elevation ?? 0)));
            var pts = new[]
            {
                new XYZ(c.X - half, c.Y - half, level?.Elevation ?? 0),
                new XYZ(c.X + half, c.Y - half, level?.Elevation ?? 0),
                new XYZ(c.X + half, c.Y + half, level?.Elevation ?? 0),
                new XYZ(c.X - half, c.Y + half, level?.Elevation ?? 0)
            };
            int created = 0;
            for (int i = 0; i < 4; i++)
            {
                var line = Line.CreateBound(pts[i], pts[(i + 1) % 4]);
                Doc.Create.NewModelCurve(line, plane);
                created++;
            }
            tx.Commit();
            return new { created, atMm = new[] { CadUnits.ToMillimeters(c.X), CadUnits.ToMillimeters(c.Y) }, halfSideMm = 10000 };
        }
        catch (Exception ex) { return new { error = $"{ex.GetType().Name}: {ex.Message}" }; }
    }

    /// <summary>现有墙的包围盒中心（英尺），用于把对照实验放进当前视图的可见区域。</summary>
    private XYZ ModelCenterFt()
    {
        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        foreach (var w in new FilteredElementCollector(Doc).OfClass(typeof(Wall)))
        {
            var bb = w.get_BoundingBox(null);
            if (bb is null) continue;
            minX = System.Math.Min(minX, bb.Min.X); maxX = System.Math.Max(maxX, bb.Max.X);
            minY = System.Math.Min(minY, bb.Min.Y); maxY = System.Math.Max(maxY, bb.Max.Y);
        }
        if (minX > maxX) return XYZ.Zero;
        return new XYZ((minX + maxX) / 2, (minY + maxY) / 2, 0);
    }

    /// <summary>
    /// 平面视图的关联标高与视图范围。剖切面不在构件高度范围内，构件就是不画——
    /// 名字叫「标高 1」的视图也可能绑在别的标高上。
    /// </summary>
    private object ReadPlanInfo(ViewPlan plan)
    {
        if (plan is null) return null;
        try
        {
            var range = plan.GetViewRange();
            double OffsetMm(PlanViewPlane plane) =>
                CadUnits.ToMillimeters(range.GetOffset(plane));

            return new
            {
                genLevel = plan.GenLevel?.Name,
                genLevelElevMm = plan.GenLevel is null ? (double?)null : CadUnits.ToMillimeters(plan.GenLevel.Elevation),
                cutPlaneMm = OffsetMm(PlanViewPlane.CutPlane),
                topMm = OffsetMm(PlanViewPlane.TopClipPlane),
                bottomMm = OffsetMm(PlanViewPlane.BottomClipPlane),
                viewDepthMm = OffsetMm(PlanViewPlane.ViewDepthPlane)
            };
        }
        catch (Exception ex) { return new { error = ex.Message }; }
    }

    /// <summary>墙类型用到的材质与其表面颜色（判断"白墙白底看不见"）。</summary>
    private List<object> ReadWallTypeMaterials(Wall wall)
    {
        var result = new List<object>();
        try
        {
            var type = wall.WallType;
            if (type is null) return result;

            foreach (var materialId in type.GetMaterialIds(false))
            {
                var m = Doc.GetElement(materialId) as Material;
                if (m is null) continue;
                var c = m.Color;
                result.Add(new
                {
                    name = m.Name,
                    colorArgb = c is null ? "(无颜色)" : $"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}"
                });
            }

            // 复合结构各层材质（结构层通常决定显示外观）
            var cs = type.GetCompoundStructure();
            if (cs is not null)
            {
                foreach (var layer in cs.GetLayers())
                {
                    var m = Doc.GetElement(layer.MaterialId) as Material;
                    result.Add(new
                    {
                        name = $"层[{layer.Function}] {m?.Name ?? "(无材质)"}",
                        colorArgb = m?.Color is null ? "(无颜色)" : $"#{m.Color.Red:X2}{m.Color.Green:X2}{m.Color.Blue:X2}"
                    });
                }
            }
        }
        catch (Exception ex) { result.Add(new { error = ex.Message }); }
        return result;
    }

    private static int ReadParamInt(View view, BuiltInParameter bip)
    {
        try
        {
            var p = view.get_Parameter(bip);
            return p is null ? -1 : p.AsInteger();
        }
        catch { return -1; }
    }

    private static double ReadParamMm(View view, BuiltInParameter bip)
    {
        try
        {
            var p = view.get_Parameter(bip);
            return p is not null && p.HasValue ? CadUnits.ToMillimeters(p.AsDouble()) : -1;
        }
        catch { return -1; }
    }

    /// <summary>阶段名（视图阶段 / 构件创建阶段）；两者不一致时构件就是不显示。</summary>
    private string ReadPhaseName(ElementId phaseId)
    {
        try
        {
            if (phaseId is null || phaseId == ElementId.InvalidElementId) return "(未设置)";
            return Doc.GetElement(phaseId)?.Name ?? phaseId.Value.ToString();
        }
        catch { return "(读取失败)"; }
    }

    /// <summary>文档里墙的创建/拆除阶段，用于和视图阶段比对。</summary>
    private object ReadWallPhases()
    {
        try
        {
            var wall = new FilteredElementCollector(Doc).OfClass(typeof(Wall)).FirstElement() as Wall;
            if (wall is null) return new { count = 0 };
            return new
            {
                count = new FilteredElementCollector(Doc).OfClass(typeof(Wall)).GetElementCount(),
                createdPhase = ReadPhaseName(wall.get_Parameter(BuiltInParameter.PHASE_CREATED)?.AsElementId()),
                demolishedPhase = ReadPhaseName(wall.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED)?.AsElementId())
            };
        }
        catch (Exception ex) { return new { error = ex.Message }; }
    }

    /// <summary>视图过滤器名称（过滤器可以按规则隐藏元素，GetCategoryHidden 查不到）。</summary>
    private List<string> ReadViewFilters()
    {
        try
        {
            return Doc.ActiveView.GetFilters()
                .Select(id => Doc.GetElement(id)?.Name ?? id.Value.ToString())
                .ToList();
        }
        catch { return new List<string>(); }
    }

    private object ReadOrientation(View3D view3d)
    {
        if (view3d is null) return null;
        try
        {
            var o = view3d.GetOrientation();
            if (o is null) return null;
            return new
            {
                eyeMm = new[] { CadUnits.ToMillimeters(o.EyePosition.X), CadUnits.ToMillimeters(o.EyePosition.Y), CadUnits.ToMillimeters(o.EyePosition.Z) },
                forward = new[] { o.ForwardDirection.X, o.ForwardDirection.Y, o.ForwardDirection.Z },
                up = new[] { o.UpDirection.X, o.UpDirection.Y, o.UpDirection.Z }
            };
        }
        catch (Exception ex) { return new { error = ex.Message }; }
    }

    private List<object> ReadSymbols(BuiltInCategory category)
    {
        return new FilteredElementCollector(Doc).OfCategory(category)
            .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
            .Select(s =>
            {
                bool hasSection = DocumentUtils.ReadSection(s, out var b, out var h);
                return (object)new
                {
                    family = s.FamilyName,
                    type = s.Name,
                    hasSectionParams = hasSection,
                    bMm = b,
                    hMm = h
                };
            })
            .ToList();
    }

    /// <summary>
    /// 写厚探针：对指定楼板类型各复制一份，分别走「层缩放」和「单层重建」两条路径，
    /// 回读实际厚度后立即删除副本。用于验证兜底路径在真实类型上是否可用（不改动文档）。
    /// </summary>
    private object ProbeFloorThickness(string typeName, double thicknessMm)
    {
        var baseType = new FilteredElementCollector(Doc).OfClass(typeof(FloorType)).Cast<FloorType>()
            .FirstOrDefault(t => t.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
        if (baseType is null) return new { typeName, ok = false, error = $"类型不存在: {typeName}" };

        double targetFt = CadUnits.ToFeet(thicknessMm);
        string tag = "BIMOne_Probe_" + Guid.NewGuid().ToString("N")[..6];

        using var tx = QuietTransaction.Begin(Doc, "CAD翻模-厚度写入探针");
        try
        {
            var dupA = (FloorType)baseType.Duplicate(tag + "_scale");
            bool scaleOk = DocumentUtils.TryScaleLayersToThickness(dupA, targetFt, out var scaleDetail);
            double scaleRead = DocumentUtils.GetFloorThicknessMm(dupA);

            var dupB = (FloorType)baseType.Duplicate(tag + "_single");
            bool singleOk = DocumentUtils.TryRebuildAsSingleLayer(dupB, targetFt, out var singleDetail);
            double singleRead = DocumentUtils.GetFloorThicknessMm(dupB);

            Doc.Delete(dupA.Id);
            Doc.Delete(dupB.Id);
            tx.Commit();

            return new
            {
                typeName,
                targetMm = thicknessMm,
                ok = scaleOk || singleOk,
                scale = new { ok = scaleOk, readbackMm = scaleRead, detail = scaleDetail },
                singleLayer = new { ok = singleOk, readbackMm = singleRead, detail = singleDetail }
            };
        }
        catch (Exception ex)
        {
            tx.RollBack();
            return new { typeName, ok = false, error = ex.Message };
        }
    }

    public bool WaitForCompletion(int timeoutMilliseconds = 600000) => _resetEvent.WaitOne(timeoutMilliseconds);

    public string GetName() => "CAD翻模诊断";
}
