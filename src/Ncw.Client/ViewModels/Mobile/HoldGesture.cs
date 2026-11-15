namespace Ncw.Client.ViewModels;

/// <summary>长按手势的阶段。</summary>
public enum HoldPhase
{
    /// <summary>没在按。</summary>
    Idle,

    /// <summary>按住中,<see cref="HoldGesture.Progress"/> 在往 1 走。</summary>
    Holding,

    /// <summary>按满了时长:动作已触发(只触发一次),手势锁定到 <see cref="HoldGesture.Reset"/>。</summary>
    Completed,

    /// <summary>提前结束:没触发动作,进度回到 0。原因见 <see cref="HoldGesture.CancelReason"/>。</summary>
    Cancelled,
}

public enum HoldCancelReason
{
    /// <summary>时间没到就松手。</summary>
    Released,

    /// <summary>手指移出了容差(在滚动列表,不是在按这个按钮)。</summary>
    MovedAway,

    /// <summary>被外部打断(窗口失焦、卡片更新、设备断开、别处已处理)。</summary>
    Interrupted,

    /// <summary>按钮不可用(正在提交、已经回答过)。</summary>
    Disabled,
}

/// <summary>
/// 「长按确认」的状态机(纯逻辑,时间由 <see cref="TimeProvider"/> 注入,单测不用真等)。审批卡片的「长按允许」用它防误触:
/// 必须连续按满 <see cref="Duration"/>;手指移出 <see cref="MaxTravel"/> 像素(在滚动)就取消;松手、失焦、卡片版本变了都取消,进度回零;
/// 按满只触发一次(<see cref="Completed"/>),之后锁定,直到 <see cref="Reset"/>(例如回答失败后让用户再按一次)。
/// 界面只负责把指针事件和一个每帧的定时器转给它,并把 <see cref="Progress"/> 画成填充条。
/// </summary>
public sealed class HoldGesture(TimeSpan duration, TimeProvider? clock = null, double maxTravel = HoldGesture.DefaultMaxTravel)
{
    /// <summary>手指离开按下点多远(像素)算「在滚动」:和 Arc 的 HoldToConfirm 一致。</summary>
    public const double DefaultMaxTravel = 24;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private long _pressedAt;

    /// <summary>需要按住的时长。按下之后改它不影响这一次。</summary>
    public TimeSpan Duration { get; set; } = duration;

    public double MaxTravel { get; } = maxTravel;

    public HoldPhase Phase { get; private set; }

    public HoldCancelReason? CancelReason { get; private set; }

    /// <summary>0 – 1。按住中随时间增长;取消后回到 0;完成后是 1。</summary>
    public double Progress { get; private set; }

    public bool IsHolding => Phase == HoldPhase.Holding;

    /// <summary>阶段或进度变了(界面据此重画)。</summary>
    public event Action? Changed;

    /// <summary>按满了(只触发一次)。</summary>
    public event Action? Completed;

    /// <summary>按下。已完成(锁定)或已经在按就忽略。</summary>
    public void Press()
    {
        if (Phase is HoldPhase.Completed or HoldPhase.Holding)
        {
            return;
        }

        _pressedAt = _clock.GetTimestamp();
        Phase = HoldPhase.Holding;
        CancelReason = null;
        Progress = 0;
        Changed?.Invoke();
    }

    /// <summary>手指移动:<paramref name="travel"/> 是离按下点(超出按钮边缘)的距离。超过容差就取消。</summary>
    public void Move(double travel)
    {
        if (Phase == HoldPhase.Holding && travel > MaxTravel)
        {
            Cancel(HoldCancelReason.MovedAway);
        }
    }

    /// <summary>每帧调一次(界面的定时器):按住的时间够了就完成。</summary>
    public void Tick()
    {
        if (Phase != HoldPhase.Holding)
        {
            return;
        }

        var elapsed = _clock.GetElapsedTime(_pressedAt);
        if (Duration <= TimeSpan.Zero || elapsed >= Duration)
        {
            Complete();
            return;
        }

        var progress = elapsed / Duration;
        if (progress != Progress)
        {
            Progress = progress;
            Changed?.Invoke();
        }
    }

    /// <summary>松手。已经按够了时间就完成(定时器可能还没来得及 tick),否则取消。</summary>
    public void Release()
    {
        if (Phase != HoldPhase.Holding)
        {
            return;
        }

        if (_clock.GetElapsedTime(_pressedAt) >= Duration)
        {
            Complete();
        }
        else
        {
            Cancel(HoldCancelReason.Released);
        }
    }

    /// <summary>外部打断。没在按、或已经完成时忽略(完成的动作收不回来)。</summary>
    public void Cancel(HoldCancelReason reason)
    {
        if (Phase != HoldPhase.Holding)
        {
            return;
        }

        Phase = HoldPhase.Cancelled;
        CancelReason = reason;
        Progress = 0;
        Changed?.Invoke();
    }

    /// <summary>解除完成后的锁定,回到空闲(回答失败后让用户可以再按一次)。</summary>
    public void Reset()
    {
        if (Phase == HoldPhase.Idle)
        {
            return;
        }

        Phase = HoldPhase.Idle;
        CancelReason = null;
        Progress = 0;
        Changed?.Invoke();
    }

    private void Complete()
    {
        Phase = HoldPhase.Completed;
        Progress = 1;
        Changed?.Invoke();
        Completed?.Invoke();
    }
}
