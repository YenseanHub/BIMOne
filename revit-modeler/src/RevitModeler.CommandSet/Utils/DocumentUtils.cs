using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitModeler.CommandSet.Utils;

/// <summary>
/// 文档查询工具：标高、墙/楼板类型、柱梁族符号查找与参数注入。
///
/// 设计要点（精度优先）：
/// 1) 所有“能写多厚”的判断都要求参数真实存在且可写，并对写入结果做回读校验；
/// 2) 类型库可能被历史错误数据污染（例如从幕墙类型复制来的墙类型没有“宽度”参数），
///    因此复用类型前必须验证，不可验证就新建，绝不静默复用；
/// 3) 无法达成目标尺寸时返回 null 或明确 warning，由调用方写入报告，避免“看起来建好了”。
/// </summary>
public static class DocumentUtils
{
    /// <summary>
    /// 厚度匹配/校验容差（约 1mm）。
    /// 必须收紧：原先 6mm 容差会让「目标 120mm」直接匹配到类型库里的 124mm 墙，
    /// 造成 4mm 系统性厚度误差（精度优先，宁可复制一个精确类型）。
    /// </summary>
    private const double WidthToleranceFt = 0.0033;

    /// <summary>找与给定标高(mm)最近的 Level。</summary>
    public static Level FindNearestLevel(Document doc, double baseLevelMm)
    {
        double targetFt = CadUnits.ToFeet(baseLevelMm);
        return new FilteredElementCollector(doc)
            .OfClass(typeof(Level)).Cast<Level>()
            .OrderBy(l => Math.Abs(l.Elevation - targetFt))
            .FirstOrDefault();
    }

    // ---------------------------------------------------------------- 墙类型

    /// <summary>兼容旧调用：仅按 TypeId 或默认类型解析。</summary>
    public static WallType ResolveWallType(Document doc, int typeId) =>
        ResolveWallType(doc, typeId, "", -1, out _);

    /// <summary>兼容旧调用：按名称/厚度解析。</summary>
    public static WallType ResolveWallType(Document doc, int typeId, string wallTypeName, double thicknessMm) =>
        ResolveWallType(doc, typeId, wallTypeName, thicknessMm, out _);

    /// <summary>
    /// 按 TypeId → 类型名 → 墙厚 → 复制基本墙类型并设宽度的顺序解析墙类型。
    /// 只有“基本墙 + 宽度参数可写”的类型才会被采用，避免幕墙/叠层墙类型导致的静默失败。
    /// </summary>
    public static WallType ResolveWallType(Document doc, int typeId, string wallTypeName, double thicknessMm,
        out string warning)
    {
        warning = null;
        if (typeId > 0 && doc.GetElement(new ElementId(typeId)) is WallType byId && IsUsableWallType(byId))
            return byId;

        var usable = new FilteredElementCollector(doc)
            .OfClass(typeof(WallType)).Cast<WallType>()
            .Where(IsUsableWallType).ToList();

        if (usable.Count == 0)
        {
            warning = "文档中没有可设置厚度的基本墙类型";
            return null;
        }

        double targetFt = thicknessMm > 0 ? CadUnits.ToFeet(thicknessMm) : -1;

        // 1) 同名类型：宽度对得上才复用；对不上则尝试校正，校正失败就放弃复用（可能被污染）
        if (!string.IsNullOrWhiteSpace(wallTypeName))
        {
            var byName = usable.FirstOrDefault(t =>
                t.Name.Equals(wallTypeName, StringComparison.OrdinalIgnoreCase));
            if (byName is not null)
            {
                if (targetFt <= 0 || Math.Abs(GetWallWidthFt(byName) - targetFt) <= WidthToleranceFt)
                    return byName;
                if (TrySetWallWidth(byName, targetFt)) return byName;
            }
        }

        if (targetFt > 0)
        {
            // 2) 已有同厚度类型
            var byWidth = usable.FirstOrDefault(t => Math.Abs(GetWallWidthFt(t) - targetFt) <= WidthToleranceFt);
            if (byWidth is not null) return byWidth;

            // 3) 复制最接近的基本墙类型并设宽度，回读校验
            var baseType = usable
                .OrderBy(t => Math.Abs(GetWallWidthFt(t) - targetFt))
                .First();
            try
            {
                string name = $"BIMOne_Wall_{Math.Round(thicknessMm)}mm";
                if (usable.Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    name = name + "_" + Guid.NewGuid().ToString("N")[..6];

                var duplicate = (WallType)baseType.Duplicate(name);
                if (TrySetWallWidth(duplicate, targetFt)) return duplicate;

                warning = $"墙厚 {thicknessMm:F0}mm 写入失败（类型参数只读），已退回基础类型";
                return baseType;
            }
            catch (Exception ex)
            {
                warning = $"复制墙类型失败: {ex.Message}";
            }
        }

        return usable.FirstOrDefault();
    }

    /// <summary>
    /// 可复用的墙类型：必须是基本墙，且厚度「可被改动」。
    /// 注意：基本墙的 WALL_ATTR_WIDTH_PARAM 由复合结构（CompoundStructure）决定，**永远是只读的**，
    /// 因此不能用「参数可写」作为判据，否则会把所有基本墙类型误判为不可用。
    /// 判据改为：参数可写，或复合结构存在且至少有一层（可改层厚）。
    /// </summary>
    public static bool IsUsableWallType(WallType t)
    {
        if (t is null) return false;
        try
        {
            if (t.Kind != WallKind.Basic) return false;

            var p = t.get_Parameter(BuiltInParameter.WALL_ATTR_WIDTH_PARAM);
            if (p is not null && !p.IsReadOnly) return true;

            var cs = t.GetCompoundStructure();
            return cs is not null && cs.LayerCount > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>墙类型总厚度(ft)：参数优先，参数不可读时退回复合结构层厚累加。</summary>
    private static double GetWallWidthFt(WallType wallType)
    {
        try
        {
            // 必须用内置参数枚举：中文版 Revit 的显示名是“宽度”，LookupParameter("Width") 查不到
            var p = wallType.get_Parameter(BuiltInParameter.WALL_ATTR_WIDTH_PARAM);
            if (p is not null && p.HasValue)
            {
                var value = p.AsDouble();
                if (value > 0) return value;
            }
        }
        catch { /* 退回复合结构 */ }

        return GetCompoundStructureThicknessFt(wallType);
    }

    /// <summary>
    /// 设墙厚：参数可写就用参数；否则改复合结构层厚。
    /// 基本墙走后者——这就是之前“墙厚永远写不进去”的真正原因。
    /// </summary>
    private static bool TrySetWallWidth(WallType wallType, double widthFt)
    {
        try
        {
            var p = wallType.get_Parameter(BuiltInParameter.WALL_ATTR_WIDTH_PARAM);
            if (p is not null && !p.IsReadOnly)
            {
                p.Set(widthFt);
                if (Math.Abs(GetWallWidthFt(wallType) - widthFt) <= WidthToleranceFt) return true;
            }
        }
        catch { /* 继续走复合结构 */ }

        return TrySetThicknessByCompoundStructure(wallType, widthFt);
    }

    // ---------------------------------------------------------------- 复合结构

    /// <summary>复合结构总厚度(ft)；无法读取返回 0。</summary>
    private static double GetCompoundStructureThicknessFt(HostObjAttributes type)
    {
        try
        {
            var cs = type.GetCompoundStructure();
            return cs is null ? 0 : cs.GetLayers().Sum(l => l.Width);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 通过复合结构把类型总厚度改到目标值（墙、楼板共用，二者都继承 HostObjAttributes）。
    /// 单层：直接把该层设为目标厚度；多层：等比缩放各层保证构造比例，再把残差补到最厚层，
    /// 使总厚度精确命中目标——翻模的验收指标是总厚度，不是各层配比。
    /// </summary>
    private static bool TrySetThicknessByCompoundStructure(HostObjAttributes type, double targetFt) =>
        TrySetThicknessByCompoundStructure(type, targetFt, out _);

    /// <summary>同上，额外回报失败细节（层数/异常消息），用于把静默失败暴露到诊断里。</summary>
    private static bool TrySetThicknessByCompoundStructure(HostObjAttributes type, double targetFt, out string detail)
    {
        // ① 优先按层等比缩放，保留原构造比例（墙最关键：多层墙要留住面层/保温层）
        if (TryScaleLayersToThickness(type, targetFt, out var scaleDetail))
        {
            detail = scaleDetail;
            return true;
        }

        // ② 缩放失败时退化为单层结构。典型场景：金属压型板楼板层宽度由压型轮廓决定，
        //    SetLayerWidth 直接抛 "The width of the layer is not valid"，此时构造比例
        //    已无意义，验收指标只有总厚度，用单层结构精确命中。
        if (TryRebuildAsSingleLayer(type, targetFt, out var rebuildDetail))
        {
            detail = $"{scaleDetail} → 改用单层结构({rebuildDetail})";
            return true;
        }

        detail = $"{scaleDetail}; {rebuildDetail}";
        return false;
    }

    /// <summary>按层缩放把总厚度改到目标值（单层直接设，多层等比缩放+残差补最厚层）。</summary>
    internal static bool TryScaleLayersToThickness(HostObjAttributes type, double targetFt, out string detail)
    {
        detail = null;
        try
        {
            var cs = type.GetCompoundStructure();
            if (cs is null) { detail = "复合结构为 null"; return false; }
            if (cs.LayerCount == 0) { detail = "复合结构层数为 0"; return false; }
            detail = $"层数={cs.LayerCount}";

            if (cs.LayerCount == 1)
            {
                cs.SetLayerWidth(0, targetFt);
            }
            else
            {
                var widths = cs.GetLayers().Select(l => l.Width).ToArray();
                var total = widths.Sum();
                if (total <= 0) { detail = "各层厚度均为 0"; return false; }

                var scale = targetFt / total;
                for (var i = 0; i < widths.Length; i++)
                    cs.SetLayerWidth(i, Math.Max(widths[i] * scale, CadUnits.ToFeet(1)));

                // 最小层厚截断会带来残差，补到最厚的一层，保证总厚精确
                var current = cs.GetLayers().Select(l => l.Width).ToArray();
                var residual = targetFt - current.Sum();
                if (Math.Abs(residual) > 1e-9)
                {
                    var thickest = 0;
                    for (var i = 1; i < current.Length; i++)
                        if (current[i] > current[thickest]) thickest = i;

                    var adjusted = current[thickest] + residual;
                    if (adjusted > 0) cs.SetLayerWidth(thickest, adjusted);
                }
            }

            type.SetCompoundStructure(cs);

            var actual = GetCompoundStructureThicknessFt(type);
            var ok = actual > 0 && Math.Abs(actual - targetFt) <= WidthToleranceFt;
            detail = ok
                ? $"{detail} 写入成功={actual * 304.8:F2}mm"
                : $"{detail} 写入后回读={actual * 304.8:F2}mm 目标={targetFt * 304.8:F2}mm";
            return ok;
        }
        catch (Exception ex)
        {
            detail = $"{detail ?? ""} 缩放异常={ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>把类型整体替换成单层结构（沿用原结构层材质），总厚度精确命中目标。</summary>
    internal static bool TryRebuildAsSingleLayer(HostObjAttributes type, double targetFt, out string detail)
    {
        detail = null;
        try
        {
            var material = ElementId.InvalidElementId;
            // 端部条件必须从原复合结构继承：CreateSingleLayerCompoundStructure 造出来的是
            // EndCap=None 之外的条件，直接 SetCompoundStructure 会报
            // "Input compound structure has wrong EndCap condition for this element type"，
            // 楼板尤其挑剔。同族继承一定合法。
            var endCap = EndCapCondition.None;
            try
            {
                var cs0 = type.GetCompoundStructure();
                if (cs0 is not null)
                {
                    endCap = cs0.EndCap;
                    if (cs0.LayerCount > 0)
                    {
                        var layers = cs0.GetLayers();
                        var donor = layers.FirstOrDefault(l => l.Function == MaterialFunctionAssignment.Structure)
                                    ?? layers[0];
                        if (donor.MaterialId != ElementId.InvalidElementId) material = donor.MaterialId;
                    }
                }
            }
            catch { /* 材质/端部条件沿用失败不影响厚度 */ }

            var single = CompoundStructure.CreateSingleLayerCompoundStructure(
                MaterialFunctionAssignment.Structure, targetFt, material);
            if (single is null) { detail = "单层结构创建失败"; return false; }

            single.EndCap = endCap;
            single.StructuralMaterialIndex = 0;
            type.SetCompoundStructure(single);

            var actual = GetCompoundStructureThicknessFt(type);
            var ok = actual > 0 && Math.Abs(actual - targetFt) <= WidthToleranceFt;
            detail = ok
                ? $"单层={actual * 304.8:F2}mm(EndCap={endCap})"
                : $"单层回读={actual * 304.8:F2}mm 目标={targetFt * 304.8:F2}mm";
            return ok;
        }
        catch (Exception ex)
        {
            detail = $"单层重建异常={ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    // ---------------------------------------------------------------- 楼板类型

    /// <summary>兼容旧调用：使用默认楼板类型。</summary>
    public static FloorType ResolveFloorType(Document doc) => ResolveFloorType(doc, "", -1, out _);

    /// <summary>兼容旧调用：仅按名称查找。</summary>
    public static FloorType ResolveFloorType(Document doc, string floorTypeName) =>
        ResolveFloorType(doc, floorTypeName, -1, out _);

    /// <summary>
    /// 楼板厚度只能落在 FloorType 上（实例级无效）。解析顺序：名称 → 同厚度 →
    /// 复制类型并设厚 → 复合结构强制替换（参数只读时的兜底），每一步都回读校验。
    /// </summary>
    public static FloorType ResolveFloorType(Document doc, string floorTypeName, double thicknessMm, out string warning)
    {
        warning = null;
        var types = new FilteredElementCollector(doc)
            .OfClass(typeof(FloorType)).Cast<FloorType>()
            .Where(t => !t.IsFoundationSlab).ToList();
        if (types.Count == 0)
        {
            warning = "文档中没有可用的楼板类型";
            return null;
        }

        double targetFt = thicknessMm > 0 ? CadUnits.ToFeet(thicknessMm) : -1;

        if (!string.IsNullOrWhiteSpace(floorTypeName))
        {
            var byName = types.FirstOrDefault(t =>
                t.Name.Equals(floorTypeName, StringComparison.OrdinalIgnoreCase));
            if (byName is not null && (targetFt <= 0 || EnsureFloorThickness(byName, thicknessMm)))
                return byName;
        }

        if (targetFt > 0)
        {
            var byThk = types.FirstOrDefault(t => Math.Abs(GetFloorThicknessFt(t) - targetFt) <= WidthToleranceFt);
            if (byThk is not null) return byThk;

            string name = $"BIMOne_Floor_{Math.Round(thicknessMm)}mm";

            // 幂等：同名类型已存在就直接把它修正到目标厚度，不再新建一个带随机后缀的。
            // 否则多轮翻模会让文档里的 BIMOne_Floor_* 无限膨胀。
            var sameName = types.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (sameName is not null && EnsureFloorThickness(sameName, thicknessMm, out _))
                return sameName;

            // 不能只按"厚度最接近"挑基准类型：样板里最接近 100mm 的可能是
            // 「75mm 金属屋顶板」，它的压型层宽度由轮廓决定，改不动。
            // 按可编辑性排序后逐个试，命中即用，全失败才报错并汇总原因。
            if (sameName is not null)
                name = name + "_" + Guid.NewGuid().ToString("N")[..6];

            var failures = new List<string>();
            foreach (var baseType in RankFloorBaseTypes(types, targetFt))
            {
                FloorType dup = null;
                try
                {
                    dup = (FloorType)baseType.Duplicate(name);
                    if (EnsureFloorThickness(dup, thicknessMm, out var detail)) return dup;

                    failures.Add($"{baseType.Name}({detail})");
                }
                catch (Exception ex)
                {
                    failures.Add($"{baseType.Name}({ex.Message})");
                }

                // 失败的类型立刻删掉，避免文档里堆积一堆厚度不对的 BIMOne_Floor_* 类型
                if (dup is not null)
                {
                    try { doc.Delete(dup.Id); } catch { /* 有引用时删不掉，留着无害 */ }
                }
            }

            // 达不到目标厚度就不交付：静默返回任意类型会得到默认 300mm 的“假精度”
            warning = $"板厚 {thicknessMm:F0}mm 无法写入，已尝试 {failures.Count} 个基准类型："
                      + string.Join(" / ", failures.Take(3));
            return null;
        }

        return types.FirstOrDefault();
    }

    /// <summary>
    /// 楼板基准类型排序：通用类型优先（名字含“常规/Generic”），其次无变截面层、层数少、厚度接近。
    /// 目的是优先拿到「构造简单、层宽可自由改动」的类型做复制基准。
    /// </summary>
    private static IEnumerable<FloorType> RankFloorBaseTypes(IEnumerable<FloorType> types, double targetFt)
    {
        return types
            .Select(t =>
            {
                var generic = t.Name.IndexOf("常规", StringComparison.Ordinal) >= 0
                              || t.Name.IndexOf("Generic", StringComparison.OrdinalIgnoreCase) >= 0;
                var layerCount = 0;
                var hasVariableLayer = false;
                var thicknessFt = 0.0;
                try
                {
                    var cs = t.GetCompoundStructure();
                    if (cs is not null)
                    {
                        layerCount = cs.LayerCount;
                        hasVariableLayer = cs.VariableLayerIndex >= 0;
                        thicknessFt = cs.GetLayers().Sum(l => l.Width);
                    }
                }
                catch { /* 读不到就按最差处理 */ }
                return new { Type = t, Generic = generic, LayerCount = layerCount, HasVariableLayer = hasVariableLayer, ThicknessFt = thicknessFt };
            })
            .OrderBy(x => x.Generic ? 0 : 1)
            .ThenBy(x => x.HasVariableLayer ? 1 : 0)
            .ThenBy(x => x.LayerCount)
            .ThenBy(x => Math.Abs(x.ThicknessFt - targetFt))
            .Select(x => x.Type);
    }

    /// <summary>回读楼板类型厚度(mm)；参数缺失时用复合结构层厚累加。</summary>
    public static double GetFloorThicknessMm(FloorType t)
    {
        double ft = GetFloorThicknessFt(t);
        if (ft <= 0)
        {
            try
            {
                var cs = t.GetCompoundStructure();
                if (cs is not null)
                    ft = cs.GetLayers().Sum(l => l.Width);
            }
            catch { /* 无复合结构则保持 0 */ }
        }
        return ft * 304.8;
    }

    /// <summary>楼板类型总厚度(ft)：参数优先，参数不可读时退回复合结构层厚累加。</summary>
    private static double GetFloorThicknessFt(FloorType t)
    {
        try
        {
            var p = t.get_Parameter(BuiltInParameter.FLOOR_ATTR_THICKNESS_PARAM);
            if (p is not null && p.HasValue)
            {
                var value = p.AsDouble();
                if (value > 0) return value;
            }
        }
        catch { /* 退回复合结构 */ }

        return GetCompoundStructureThicknessFt(t);
    }

    /// <summary>确保楼板类型厚度=目标值；参数只读时改复合结构，回读校验。</summary>
    private static bool EnsureFloorThickness(FloorType t, double thicknessMm) =>
        EnsureFloorThickness(t, thicknessMm, out _);

    private static bool EnsureFloorThickness(FloorType t, double thicknessMm, out string detail)
    {
        detail = null;
        double targetFt = CadUnits.ToFeet(thicknessMm);
        if (Math.Abs(GetFloorThicknessFt(t) - targetFt) <= WidthToleranceFt) return true;

        try
        {
            var p = t.get_Parameter(BuiltInParameter.FLOOR_ATTR_THICKNESS_PARAM);
            if (p is null) detail = "厚度参数不存在";
            else if (p.IsReadOnly) detail = "厚度参数只读";
            else
            {
                p.Set(targetFt);
                if (Math.Abs(GetFloorThicknessFt(t) - targetFt) <= WidthToleranceFt) return true;
                detail = "参数写入后回读不符";
            }
        }
        catch (Exception ex)
        {
            detail = $"参数写入异常 {ex.GetType().Name}";
        }

        return TrySetThicknessByCompoundStructure(t, targetFt, out detail);
    }

    /// <summary>尝试写入楼板实例厚度参数（多数情况下无效，仅兜底）。</summary>
    public static void TrySetFloorThickness(Floor floor, double thicknessMm)
    {
        if (floor is null || thicknessMm <= 0) return;
        try
        {
            var p = floor.get_Parameter(BuiltInParameter.FLOOR_ATTR_THICKNESS_PARAM)
                    ?? floor.LookupParameter("Thickness");
            if (p is not null && !p.IsReadOnly) p.Set(CadUnits.ToFeet(thicknessMm));
        }
        catch
        {
            // 部分楼板类型 thickness 只读，忽略即可
        }
    }

    // ---------------------------------------------------------------- 柱/梁族符号

    private static readonly string[] SectionWidthParams =
        { "b", "B", "宽", "宽度", "截面宽", "截面宽度", "Width" };

    private static readonly string[] SectionHeightParams =
        { "h", "H", "高", "高度", "截面高", "截面高度", "深度", "Depth", "Height" };

    /// <summary>
    /// 解析结构柱/梁的族符号：优先复制现有类型并设置 b/h 截面参数；
    /// 族没有可写截面参数时按原截面使用，并通过 note 告知调用方（不臆造尺寸）。
    /// </summary>
    public static FamilySymbol ResolveSectionSymbol(Document doc, BuiltInCategory category,
        string typePrefix, double bMm, double hMm, out string note)
    {
        note = "";
        var symbols = new FilteredElementCollector(doc)
            .OfCategory(category).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().ToList();
        if (symbols.Count == 0)
        {
            note = $"{DescribeCategory(category)}族缺失";
            return null;
        }

        string wanted = $"{typePrefix}_{Math.Round(bMm)}x{Math.Round(hMm)}";

        // 已有同名类型且截面一致 → 直接复用
        var existing = symbols.FirstOrDefault(s => s.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && ReadSection(existing, out var eb, out var eh) &&
            Math.Abs(eb - bMm) < 2 && Math.Abs(eh - hMm) < 2)
            return existing;

        // 找一个能写截面的族符号作为模板
        foreach (var s in symbols)
        {
            if (!HasSectionParams(s)) continue;
            try
            {
                var dup = existing is not null
                    ? existing
                    : (FamilySymbol)s.Duplicate(UniqueName(symbols, wanted));
                if (TrySetSection(dup, bMm, hMm, out var gotB, out var gotH))
                {
                    note = $"截面 {gotB:F0}×{gotH:F0}mm";
                    return dup;
                }
            }
            catch (Exception ex)
            {
                note = $"复制族类型失败: {ex.Message}";
            }
        }

        var fallback = existing ?? symbols.First();
        ReadSection(fallback, out var fb, out var fh);
        note = $"族 {fallback.FamilyName}/{fallback.Name} 无 b/h 参数，实际截面 {fb:F0}×{fh:F0}mm ≠ 目标 {bMm:F0}×{hMm:F0}mm";
        return fallback;
    }

    private static string UniqueName(List<FamilySymbol> existing, string wanted) =>
        existing.Any(s => s.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            ? wanted + "_" + Guid.NewGuid().ToString("N")[..6]
            : wanted;

    private static string DescribeCategory(BuiltInCategory c) => c switch
    {
        BuiltInCategory.OST_StructuralColumns => "结构柱",
        BuiltInCategory.OST_StructuralFraming => "结构框架(梁)",
        _ => c.ToString()
    };

    private static bool HasSectionParams(FamilySymbol s) =>
        SectionWidthParams.Any(n => s.LookupParameter(n) is not null) &&
        SectionHeightParams.Any(n => s.LookupParameter(n) is not null);

    private static Parameter FindWritable(FamilySymbol s, string[] names) =>
        names.Select(n => s.LookupParameter(n)).FirstOrDefault(p => p is not null && !p.IsReadOnly);

    /// <summary>写入截面参数并回读校验（单位 mm → ft）。</summary>
    private static bool TrySetSection(FamilySymbol s, double bMm, double hMm, out double gotB, out double gotH)
    {
        gotB = gotH = -1;
        try
        {
            var pb = FindWritable(s, SectionWidthParams);
            var ph = FindWritable(s, SectionHeightParams);
            if (pb is null || ph is null) return false;
            pb.Set(CadUnits.ToFeet(bMm));
            ph.Set(CadUnits.ToFeet(hMm));
            gotB = pb.AsDouble() * 304.8;
            gotH = ph.AsDouble() * 304.8;
            return Math.Abs(gotB - bMm) <= 2 && Math.Abs(gotH - hMm) <= 2;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>读取族类型截面尺寸(mm)。</summary>
    public static bool ReadSection(FamilySymbol s, out double bMm, out double hMm)
    {
        bMm = hMm = -1;
        try
        {
            var pb = SectionWidthParams.Select(n => s.LookupParameter(n)).FirstOrDefault(p => p is not null);
            var ph = SectionHeightParams.Select(n => s.LookupParameter(n)).FirstOrDefault(p => p is not null);
            if (pb is null || ph is null) return false;
            bMm = pb.AsDouble() * 304.8;
            hMm = ph.AsDouble() * 304.8;
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- 其它

    /// <summary>墙类型实际厚度(mm)；读不到返回 -1。</summary>
    public static double GetWallWidthMm(WallType t)
    {
        double ft = GetWallWidthFt(t);
        return ft > 0 ? ft * 304.8 : -1;
    }

    /// <summary>把结构柱顶部偏移设到层高，使柱高=Height(mm)。</summary>
    public static bool TrySetTopOffset(FamilyInstance instance, double heightMm)
    {
        try
        {
            var p = instance.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM)
                    ?? instance.LookupParameter("顶部偏移")
                    ?? instance.LookupParameter("长度")
                    ?? instance.LookupParameter("高度");
            if (p is null || p.IsReadOnly) return false;
            p.Set(CadUnits.ToFeet(heightMm));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>取文档第一个族符号（门/窗占位用）。</summary>
    public static FamilySymbol ResolveFamilySymbol(Document doc, string familyName) =>
        new FilteredElementCollector(doc)
            .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
            .FirstOrDefault(fs => string.IsNullOrEmpty(familyName) ||
                                  fs.Name.Equals(familyName, StringComparison.OrdinalIgnoreCase) ||
                                  fs.Name.Contains(familyName, StringComparison.OrdinalIgnoreCase));

    /// <summary>尝试写入族实例宽度参数（门/窗占位用）。</summary>
    public static void TrySetFamilyWidth(FamilyInstance instance, double widthMm)
    {
        if (instance is null || widthMm <= 0) return;
        try
        {
            var p = instance.LookupParameter("Width") ?? instance.LookupParameter("宽度");
            if (p is not null && !p.IsReadOnly) p.Set(CadUnits.ToFeet(widthMm));
        }
        catch
        {
            // 族参数可能不存在或只读，忽略
        }
    }

    /// <summary>给族实例写入 Z 偏移（梁底/柱顶定位用），找不到参数则返回 false。</summary>
    public static bool TrySetVerticalOffset(FamilyInstance instance, double offsetMm)
    {
        try
        {
            var p = instance.get_Parameter(BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM)
                    ?? instance.LookupParameter("z 偏移值")
                    ?? instance.LookupParameter("Z 偏移值")
                    ?? instance.LookupParameter("偏移");
            if (p is null || p.IsReadOnly) return false;
            p.Set(CadUnits.ToFeet(offsetMm));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
