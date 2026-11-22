using Autodesk.Revit.UI;
using System;
using System.Threading.Tasks;

namespace RevitModeler.Plugin.Core;

/// <summary>
/// 把「重载命令集」这类动作送回 Revit 主线程执行。
///
/// 为什么必须这样：socket 请求在后台线程处理，而重建命令会调用
/// <c>ExternalEvent.Create</c>，Revit 要求它运行在 API 上下文；在主线程之外创建会抛出异常。
/// 这里用常驻 ExternalEvent 做一次线程切换，动作仍是同步返回（后台线程等待结果）。
/// </summary>
public sealed class ReloadRequestHandler : IExternalEventHandler
{
    private readonly object _sync = new();
    private Func<object> _pending;
    private TaskCompletionSource<object> _completion;

    /// <summary>在调用线程等待，实际动作在主线程执行。</summary>
    public object Invoke(ExternalEvent externalEvent, Func<object> action, TimeSpan timeout)
    {
        TaskCompletionSource<object> tcs;
        lock (_sync)
        {
            _pending = action;
            tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            _completion = tcs;
        }

        externalEvent.Raise();

        try
        {
            if (!tcs.Task.Wait(timeout))
                return Failure("等待 Revit 主线程超时（可能有模态对话框或命令正在运行）");
            return tcs.Task.Result;
        }
        catch (AggregateException ex)
        {
            return Failure(ex.InnerException?.Message ?? ex.Message);
        }
    }

    public void Execute(UIApplication app)
    {
        Func<object> action;
        TaskCompletionSource<object> tcs;

        lock (_sync)
        {
            action = _pending;
            tcs = _completion;
            _pending = null;
            _completion = null;
        }

        if (action is null) return;

        try { tcs?.TrySetResult(action()); }
        catch (Exception ex) { tcs?.TrySetException(ex); }
    }

    public string GetName() => "BIMOne.ReloadCommands";

    private static object Failure(string message) => new { ok = false, error = message };
}
