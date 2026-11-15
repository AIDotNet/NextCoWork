namespace Ncw.Core.Domain;

/// <summary>可视化小组件卡片的阶段(桌面的 WebView 外壳、手机的静态快照、收件箱共用同一套判定)。</summary>
public enum WidgetPhase
{
    /// <summary>工具还在跑、卡片还没出来(模型正在写)。</summary>
    Loading,

    /// <summary>有可显示的卡片。</summary>
    Ready,

    /// <summary>交互卡片已经不再等待提交(提交窗口关了:Run 结束、别的控制端先答了、会话被中断)而又没有提交值;内容还在,但不能再提交。</summary>
    Expired,

    /// <summary>调用失败(消毒拒绝、参数不合法……)或结束了却没有卡片。</summary>
    Failed,

    /// <summary>设备断开,而这张卡的状态本来就靠设备推进(还在生成、还在等提交):<b>未知</b>,不判成结束,也不继续显示加载动画(AGENTS.md §3 规则 7)。</summary>
    Unknown,
}

/// <summary><c>visualize_*</c> 这一次工具调用现在的状态(与转录里的工具卡片状态同源)。</summary>
public enum WidgetCall
{
    Running,
    Done,
    Failed,

    /// <summary>调用没有回执而 Run 已经不在了。</summary>
    Interrupted,
}

/// <param name="HasCard">已经拿到卡片(实时的 <c>tool_progress</c> / <c>tool_end</c> 的 Details 或落库的结果)。</param>
/// <param name="Interactive">交互模式的卡片(等用户 <c>widgets.submit</c>)。</param>
/// <param name="Submitted">卡片 Details 里已经有提交值(终态)。</param>
/// <param name="AwaitingSubmit">设备上还挂着这张卡的 <c>widget_submit</c> 交互。</param>
/// <param name="DeviceUnknown">设备断开,状态未知。</param>
public readonly record struct WidgetFacts(WidgetCall Call, bool HasCard, bool Interactive, bool Submitted, bool AwaitingSubmit, bool DeviceUnknown);

/// <summary>
/// 「这张 widget 卡现在是什么状态、还能不能提交」的唯一派生函数(AGENTS.md §3 规则 3):桌面卡片、手机快照卡、将来的收件箱都调它,不各自再判一遍。
/// 规则 6:终态压过运行中——已经有提交值,或调用已经结束,就不会因为重放的旧事件退回「加载中」。
/// </summary>
public static class WidgetView
{
    public static WidgetPhase Phase(WidgetFacts f)
    {
        if (!f.HasCard)
        {
            return f.Call switch
            {
                WidgetCall.Running => f.DeviceUnknown ? WidgetPhase.Unknown : WidgetPhase.Loading,
                WidgetCall.Interrupted => WidgetPhase.Expired,
                _ => WidgetPhase.Failed,
            };
        }

        if (f.Submitted)
        {
            return WidgetPhase.Ready;
        }

        if (f.DeviceUnknown && (f.Call == WidgetCall.Running || f.AwaitingSubmit))
        {
            return WidgetPhase.Unknown;
        }

        // 交互卡片:提交窗口已关(调用结束了而没有提交值)→ 过期;调用还在跑、交互还没登记或刚被答掉的瞬间不算(避免闪一下)
        if (f.Interactive && !f.AwaitingSubmit && f.Call != WidgetCall.Running)
        {
            return WidgetPhase.Expired;
        }

        return WidgetPhase.Ready;
    }

    /// <summary>用户现在能不能提交:交互卡片、设备上还在等、没有提交过、设备在线。</summary>
    public static bool CanSubmit(WidgetFacts f) => f.HasCard && f.Interactive && !f.Submitted && f.AwaitingSubmit && !f.DeviceUnknown;

    /// <summary>
    /// 卡片的桥能不能调设备(<c>setState</c> / <c>runAction</c>):设备在线、卡片已经出来、调用没有失败。
    /// 提交单独看 <see cref="CanSubmit"/>;过期的卡片仍可以读写状态、跑动作(动作的定义在转录里,和 Run 是否还活着无关)。
    /// </summary>
    public static bool CanCallDevice(WidgetFacts f) => f.HasCard && !f.DeviceUnknown && f.Call != WidgetCall.Failed;
}
