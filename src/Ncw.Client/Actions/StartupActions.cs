using Ncw.Client.Devices;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Core.Domain;

namespace Ncw.Client.Actions;

/// <summary>
/// 启动加载界面上的用户意图(一个意图一个方法,AGENTS.md §3 规则 4):重试、复制诊断信息、退出;
/// 以及界面向 Store 报告的两个事实(外壳完成首次布局、加载界面已淡出)。必须在 UI 线程上调(Store 只在 UI 线程上读写)。
/// </summary>
public sealed class StartupActions(ClientStores stores, DeviceRegistry devices, Func<IStartupHost?> host, Func<ILocalFiles> files, Func<Action?> exit)
{
    /// <summary>
    /// 「重试」:让本机 Agent 守护立刻再来一次(从失败或退避等待中),并让设备连接立刻重连(不等退避)。结果经 Store 回来:
    /// 失败是派生状态,恢复后错误面板自己消失,没有「重试之后的状态」要在这里另存。
    /// </summary>
    public void Retry()
    {
        host()?.Retry();
        devices.Local?.RetryNow();
    }

    /// <summary>「复制诊断信息」:把诊断文本放进系统剪贴板(客户端本机的动作)。</summary>
    public Task CopyDiagnosticsAsync(string text) => files().CopyTextAsync(text);

    /// <summary>「退出」:走应用自己的退出流程(让 Agent 收尾)。</summary>
    public void Quit() => exit()?.Invoke();

    /// <summary>加载界面下面的外壳在拿到数据后完成了一次完整布局(界面在所有阶段都完成后的第二个动画帧报告)。</summary>
    public void ReportShellLaidOut() => stores.Startup.Reach(StartupMilestone.ShellLaidOut);

    /// <summary>加载界面淡出完毕、已经释放:应用可交互。之后启动视图永远是 Done。</summary>
    public void ReportInteractive() => stores.Startup.Reach(StartupMilestone.Interactive);
}
