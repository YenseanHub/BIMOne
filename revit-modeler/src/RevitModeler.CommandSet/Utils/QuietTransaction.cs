using Autodesk.Revit.DB;

namespace RevitModeler.CommandSet.Utils;

/// <summary>
/// 静默事务：把 Revit 的警告就地吞掉，不再弹模态警告框。
///
/// 为什么必须有它：翻模一次提交上百个图元，墙重叠、同位置图元这类警告几乎必然出现。
/// 这类警告一旦以模态框形式弹出，Revit 的图形管线就停在那一刻——元素在文档里、
/// 几何正常、API 全部判定"可见"，但屏幕上什么都画不出来，导出图片也是空白。
/// 现场症状就是「建好了但场景里什么都没有」，查起来极容易误判成坐标/视图/可见性问题。
/// </summary>
public static class QuietTransaction
{
    public static Transaction Begin(Document doc, string name)
    {
        var tx = new Transaction(doc, name);
        var options = tx.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(new WarningSuppressor());
        options.SetClearAfterRollback(true);
        tx.SetFailureHandlingOptions(options);
        tx.Start();
        return tx;
    }

    private sealed class WarningSuppressor : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            bool hasError = false;
            foreach (var failure in accessor.GetFailureMessages())
            {
                if (failure.GetSeverity() == FailureSeverity.Warning)
                    accessor.DeleteWarning(failure);
                else
                    hasError = true;
            }

            // 警告删掉继续；真错误仍然回滚，但不能变成弹窗卡住界面
            return hasError
                ? FailureProcessingResult.ProceedWithRollBack
                : FailureProcessingResult.Continue;
        }
    }
}
