using Ncw.Client.ViewModels;

namespace Ncw.Client.Tests.Mobile;

/// <summary>「长按确认」手势的状态机(审批卡片的「长按允许」防误触):时间由测试推进,不用真等。</summary>
public class HoldGestureTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);

    private static (HoldGesture Gesture, ManualClock Clock, List<string> Log) Make(double seconds = 1.0, double maxTravel = 24)
    {
        var clock = new ManualClock(T0);
        var gesture = new HoldGesture(TimeSpan.FromSeconds(seconds), clock, maxTravel);
        var log = new List<string>();
        gesture.Completed += () => log.Add("completed");
        return (gesture, clock, log);
    }

    [Fact]
    public void Holding_for_the_full_duration_completes_exactly_once()
    {
        var (gesture, clock, log) = Make();

        gesture.Press();
        Assert.Equal(HoldPhase.Holding, gesture.Phase);
        clock.Advance(TimeSpan.FromMilliseconds(400));
        gesture.Tick();
        Assert.InRange(gesture.Progress, 0.39, 0.41);
        clock.Advance(TimeSpan.FromMilliseconds(700));
        gesture.Tick();
        gesture.Tick();
        gesture.Release();

        Assert.Equal(HoldPhase.Completed, gesture.Phase);
        Assert.Equal(1.0, gesture.Progress);
        Assert.Equal(["completed"], log);
    }

    [Fact]
    public void Releasing_early_cancels_without_triggering_and_the_progress_rewinds_to_zero()
    {
        var (gesture, clock, log) = Make();

        gesture.Press();
        clock.Advance(TimeSpan.FromMilliseconds(600));
        gesture.Tick();
        gesture.Release();

        Assert.Equal(HoldPhase.Cancelled, gesture.Phase);
        Assert.Equal(HoldCancelReason.Released, gesture.CancelReason);
        Assert.Equal(0, gesture.Progress);
        Assert.Empty(log);
    }

    [Fact]
    public void A_release_after_the_duration_counts_even_if_no_frame_tick_arrived_first()
    {
        var (gesture, clock, log) = Make();

        gesture.Press();
        clock.Advance(TimeSpan.FromSeconds(1.2)); // 定时器卡了一下,没来得及 tick

        gesture.Release();

        Assert.Equal(HoldPhase.Completed, gesture.Phase);
        Assert.Single(log);
    }

    [Fact]
    public void Dragging_the_finger_past_the_travel_tolerance_cancels_because_it_is_a_scroll_not_a_press()
    {
        var (gesture, clock, log) = Make(maxTravel: 24);

        gesture.Press();
        clock.Advance(TimeSpan.FromMilliseconds(300));
        gesture.Move(10); // 容差内:继续
        Assert.True(gesture.IsHolding);
        gesture.Move(30);

        Assert.Equal(HoldPhase.Cancelled, gesture.Phase);
        Assert.Equal(HoldCancelReason.MovedAway, gesture.CancelReason);
        clock.Advance(TimeSpan.FromSeconds(5));
        gesture.Tick();
        gesture.Release();
        Assert.Empty(log); // 取消之后不会再完成
    }

    [Fact]
    public void An_outside_interruption_cancels_but_cannot_undo_a_completed_hold()
    {
        var (gesture, clock, log) = Make();
        gesture.Press();
        gesture.Cancel(HoldCancelReason.Interrupted);
        Assert.Equal(HoldPhase.Cancelled, gesture.Phase);

        gesture.Press();
        clock.Advance(TimeSpan.FromSeconds(1));
        gesture.Tick();
        gesture.Cancel(HoldCancelReason.Interrupted); // 已经完成:收不回来

        Assert.Equal(HoldPhase.Completed, gesture.Phase);
        Assert.Single(log);
    }

    [Fact]
    public void After_completion_the_gesture_is_locked_until_reset_so_a_second_press_cannot_fire_again()
    {
        var (gesture, clock, log) = Make();
        gesture.Press();
        clock.Advance(TimeSpan.FromSeconds(1));
        gesture.Tick();

        gesture.Press(); // 锁定:忽略
        clock.Advance(TimeSpan.FromSeconds(2));
        gesture.Tick();
        Assert.Single(log);

        gesture.Reset(); // 回答失败后解锁,可以再按
        gesture.Press();
        clock.Advance(TimeSpan.FromSeconds(1));
        gesture.Tick();
        Assert.Equal(2, log.Count);
    }

    [Fact]
    public void A_second_press_while_holding_does_not_restart_the_fill()
    {
        var (gesture, clock, _) = Make();
        gesture.Press();
        clock.Advance(TimeSpan.FromMilliseconds(500));
        gesture.Tick();

        gesture.Press(); // 键盘自动重复 / 多指
        gesture.Tick();

        Assert.InRange(gesture.Progress, 0.49, 0.51);
    }

    [Fact]
    public void Events_without_a_press_do_nothing()
    {
        var (gesture, clock, log) = Make();

        gesture.Move(100);
        gesture.Tick();
        gesture.Release();
        gesture.Cancel(HoldCancelReason.Interrupted);
        clock.Advance(TimeSpan.FromSeconds(10));
        gesture.Tick();

        Assert.Equal(HoldPhase.Idle, gesture.Phase);
        Assert.Empty(log);
    }

    [Fact]
    public void A_dangerous_approval_needs_a_longer_hold_than_a_normal_one()
    {
        Assert.True(InteractionRowViewModel.DangerousHoldDuration > InteractionRowViewModel.HoldDuration);
        var (gesture, clock, log) = Make(seconds: InteractionRowViewModel.DangerousHoldDuration.TotalSeconds);
        gesture.Press();
        clock.Advance(InteractionRowViewModel.HoldDuration + TimeSpan.FromMilliseconds(100)); // 普通审批够了,危险的还不够
        gesture.Tick();

        Assert.Empty(log);
        Assert.True(gesture.IsHolding);
    }

    [Fact]
    public void Changed_fires_on_every_phase_or_progress_change_so_the_view_can_redraw()
    {
        var (gesture, clock, _) = Make();
        var changes = 0;
        gesture.Changed += () => changes++;

        gesture.Press();
        clock.Advance(TimeSpan.FromMilliseconds(200));
        gesture.Tick();
        gesture.Release();

        Assert.Equal(3, changes); // 按下、进度、取消
    }
}
