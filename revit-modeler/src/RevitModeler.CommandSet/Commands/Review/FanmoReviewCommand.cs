using Newtonsoft.Json.Linq;
using Autodesk.Revit.UI;
using RevitMCPSDK.API.Base;
using RevitModeler.CommandSet.Models.Common;
using RevitModeler.CommandSet.Services.Review;

namespace RevitModeler.CommandSet.Commands.Review;

/// <summary>
/// 翻模复查命令：扫描当前模型的墙/板并输出问题清单。与建模命令同构，只读。
/// </summary>
public class FanmoReviewCommand : ExternalEventCommandBase
{
    private FanmoReviewEventHandler Handler => (FanmoReviewEventHandler)base.Handler;

    public override string CommandName => "fanmo_review";

    public FanmoReviewCommand(UIApplication uiApp)
        : base(new FanmoReviewEventHandler(), uiApp)
    {
    }

    public override object Execute(JObject parameters, string requestId)
    {
        try
        {
            double minLen = parameters["minWallLengthMm"]?.ToObject<double>() ?? 10;
            Handler.SetParameters(minLen);

            if (RaiseAndWaitForCompletion(10000))
                return Handler.Result;

            throw new TimeoutException("翻模复查超时");
        }
        catch (Exception ex)
        {
            return new AIResult<Models.Review.ReviewReport>
            {
                Success = false,
                Message = $"复查命令失败: {ex.Message}"
            };
        }
    }
}
