using RevitModeler.Core.Models;
using RevitModeler.Core.Semantics;

namespace RevitModeler.Core.Mapping;

/// <summary>
/// IR → 建模命令 的映射器。纯逻辑，可单测，不依赖 RevitAPI。
/// 「BIM翻模大师」专家的研判规则在此固化：显式规则优先，无规则走 LayerSemantics 自动推断。
/// </summary>
public sealed class ModelMapper
{
    private readonly IReadOnlyList<FamilyMappingRule> _rules;

    public ModelMapper(IReadOnlyList<FamilyMappingRule> rules)
    {
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
    }

    public IReadOnlyList<ModelCommand> Map(IEnumerable<CadEntity> entities)
    {
        var all = (entities ?? Enumerable.Empty<CadEntity>()).ToList();
        var result = new List<ModelCommand>(all.Count);
        foreach (var e in all)
            result.Add(MapOne(e, all));
        return result;
    }

    public ModelCommand MapOne(CadEntity e) => MapOne(e, new[] { e });

    private ModelCommand MapOne(CadEntity e, IReadOnlyList<CadEntity> all)
    {
        // 1) 显式规则（用户提供或默认覆盖层）优先
        var rule = _rules.FirstOrDefault(r =>
            r.LayerPattern == e.Layer ||
            (r.LayerPattern.EndsWith("*") && e.Layer.StartsWith(r.LayerPattern.TrimEnd('*'))));

        if (rule is not null)
        {
            if (rule.Action == ModelAction.CreateFloor && !e.Closed)
                return Skip(e, "规则要求闭合图元，但该图元未闭合");
            if (e.Length > 0 && e.Length < rule.MinLength)
                return Skip(e, $"长度 {e.Length:F0}mm 小于规则最小 {rule.MinLength:F0}mm");

            return new ModelCommand
            {
                SourceHandle = e.Handle, Action = rule.Action, Layer = e.Layer,
                Category = rule.Category, FamilyName = rule.FamilyName, Length = e.Length,
                Start = e.Start, End = e.End, Points = e.Points,
                Reason = $"命中图层规则 {rule.LayerPattern}"
            };
        }

        // 2) 无规则命中 → 自动语义推断（图层名 + 几何特征），低置信也建模但标记待复核
        var inf = LayerSemantics.Infer(e, all);
        if (!inf.Actionable)
            return Skip(e, $"语义推断置信度过低({inf.Confidence:F2})：{inf.Reason}");

        var action = LayerSemantics.ToAction(inf.Category);
        string cn = ToChineseCategory(inf.Category);
        return new ModelCommand
        {
            SourceHandle = e.Handle, Action = action, Layer = e.Layer,
            Category = cn, Length = e.Length,
            Start = e.Start, End = e.End, Points = e.Points,
            Confidence = inf.Confidence, NeedsReview = inf.NeedsReview,
            Reason = $"语义推断：{inf.Reason}(置信{inf.Confidence:F2})"
        };
    }

    private static string ToChineseCategory(SemanticCategory c) => c switch
    {
        SemanticCategory.Wall => "墙",
        SemanticCategory.Floor => "楼板",
        SemanticCategory.Door => "门",
        SemanticCategory.Window => "窗",
        SemanticCategory.Column => "柱",
        SemanticCategory.Finish => "地面",
        SemanticCategory.Furniture => "家具",
        SemanticCategory.Annotation => "标注",
        _ => "未知"
    };

    /// <summary>默认规则表：住宅户型常见图层命名。项目可整体替换。</summary>
    public static IReadOnlyList<FamilyMappingRule> DefaultRules() => new[]
    {
        new FamilyMappingRule { LayerPattern = "FLOOR-*", Action = ModelAction.CreateFloor, Category = "楼板", MinLength = 100 },
        new FamilyMappingRule { LayerPattern = "地面材料", Action = ModelAction.CreateFloor, Category = "楼板" },
        new FamilyMappingRule { LayerPattern = "DIM", Action = ModelAction.Skip, Category = "标注" },
        new FamilyMappingRule { LayerPattern = "标注", Action = ModelAction.Skip, Category = "标注" },
        new FamilyMappingRule { LayerPattern = "家俱*", Action = ModelAction.Skip, Category = "家具" }
        // 注意：不再无脑跳过图层 "0"。真实户型图大量墙体线在 0 层，改由 LayerSemantics 几何推断接管。
    };

    private static ModelCommand Skip(CadEntity e, string reason) => new()
    {
        SourceHandle = e.Handle, Action = ModelAction.Skip, Layer = e.Layer, Reason = reason
    };
}
