using CommunityToolkit.Mvvm.ComponentModel;

namespace Ncw.Client.ViewModels;

// 手机上的「长按允许」(docs/design/02 B2:危险操作需要长按确认)。手势的状态机是 HoldGesture(纯逻辑、时间可注入);这里把它接到审批卡片上:
// 按满 → 一次 AllowOnce(和桌面的「允许一次」按钮是同一个回答意图);卡片换了版本 / 正在提交 / 设备断开 → 取消这次按压。
public sealed partial class InteractionRowViewModel
{
    /// <summary>普通审批要按住的时长。</summary>
    public static readonly TimeSpan HoldDuration = TimeSpan.FromMilliseconds(800);

    /// <summary>危险审批(命令风险分级 dangerous / 破坏性工具)要按住的时长:更长,多一点反悔的时间。</summary>
    public static readonly TimeSpan DangerousHoldDuration = TimeSpan.FromMilliseconds(1500);

    private HoldGesture? _hold;

    /// <summary>「长按允许」的手势。第一次用到时才创建(桌面不用)。</summary>
    public HoldGesture AllowHold
    {
        get
        {
            if (_hold is null)
            {
                _hold = new HoldGesture(CurrentHoldDuration, _clock);
                _hold.Changed += OnHoldChanged;
                _hold.Completed += OnHoldCompleted;
            }

            return _hold;
        }
    }

    private TimeSpan CurrentHoldDuration => IsDangerous ? DangerousHoldDuration : HoldDuration;

    /// <summary>按住进度 0 – 1(界面画填充条)。</summary>
    [ObservableProperty] public partial double HoldProgress { get; private set; }

    [ObservableProperty] public partial bool IsHolding { get; private set; }

    /// <summary>按满之后等待设备回答的阶段(按钮显示「已允许,提交中」)。</summary>
    [ObservableProperty] public partial bool HoldCompleted { get; private set; }

    /// <summary>从收件箱点进来定位到的卡片:加一圈醒目的边框(纯 UI 状态)。</summary>
    [ObservableProperty] public partial bool IsHighlighted { get; set; }

    /// <summary>按下「长按允许」。正在提交、已经回答过的不接受。</summary>
    public void BeginAllowHold()
    {
        if (IsBusy || !IsToolPermission)
        {
            return;
        }

        AllowHold.Duration = CurrentHoldDuration;
        AllowHold.Press();
    }

    /// <summary>手指移动(离按钮边缘的距离):移出容差取消。</summary>
    public void MoveAllowHold(double travel) => AllowHold.Move(travel);

    /// <summary>松手。</summary>
    public void EndAllowHold() => AllowHold.Release();

    /// <summary>失焦、卡片被盖住等外部打断。</summary>
    public void InterruptAllowHold() => AllowHold.Cancel(HoldCancelReason.Interrupted);

    /// <summary>每帧 tick(界面的定时器,只在按住期间运行)。</summary>
    public void TickAllowHold() => AllowHold.Tick();

    private void OnHoldChanged()
    {
        HoldProgress = AllowHold.Progress;
        IsHolding = AllowHold.IsHolding;
    }

    private void OnHoldCompleted()
    {
        HoldCompleted = true;
        _ = CompleteHoldAsync();
    }

    private async Task CompleteHoldAsync()
    {
        await AllowOnceCommand.ExecuteAsync(null);
        // 回答没成功(设备拒绝、断线):解锁,让用户可以再按一次;成功的话卡片随 Store 消失
        HoldCompleted = false;
        AllowHold.Reset();
    }

    /// <summary>派生行变了(同一个交互换了版本):这次按压作废,不能按旧版本的内容批准。</summary>
    private void CancelHoldOnRefresh()
    {
        if (_hold is { IsHolding: true })
        {
            _hold.Cancel(HoldCancelReason.Interrupted);
        }
    }
}
