using Ncw.UI.Views.Desktop.Conversation;

namespace Ncw.UI.Tests;

/// <summary>转录「跟随最新」的状态机:只认用户意图,布局 / 程序造成的偏移变化不改状态。</summary>
public class TranscriptFollowTests
{
    [Fact]
    public void Starts_following_and_pins_whenever_it_is_not_at_the_bottom()
    {
        var follow = new TranscriptFollow();

        Assert.True(follow.IsFollowing);
        Assert.True(follow.ShouldPin(atBottom: false));
        Assert.False(follow.ShouldPin(atBottom: true));
    }

    [Fact]
    public void Offset_changes_outside_a_user_gesture_are_layout_noise_and_change_nothing()
    {
        var follow = new TranscriptFollow();

        // 虚拟化重估高度:偏移往上漂了一大截
        follow.OffsetChanged(now: 10_000, deltaY: -480, atBottom: false);
        Assert.True(follow.IsFollowing);

        follow.Pause();
        // 展开动画 / 重估让偏移漂到了底部:不是用户回到了底部
        follow.OffsetChanged(now: 10_100, deltaY: 300, atBottom: true);
        Assert.False(follow.IsFollowing);
    }

    [Fact]
    public void Scrolling_up_stops_following_only_when_there_is_somewhere_to_go()
    {
        var follow = new TranscriptFollow();

        // 已经在顶上 / 内容不满一屏:往上滚什么也不会发生,不能因此停止跟随
        follow.UserScroll(now: 0, towardTop: true, canMoveUp: false);
        Assert.True(follow.IsFollowing);

        follow.UserScroll(now: 0, towardTop: true, canMoveUp: true);
        Assert.False(follow.IsFollowing);
        Assert.False(follow.ShouldPin(atBottom: false));
    }

    [Fact]
    public void Scrolling_back_down_to_the_bottom_within_the_gesture_resumes_following()
    {
        var follow = new TranscriptFollow();
        follow.UserScroll(now: 0, towardTop: true, canMoveUp: true);

        follow.UserScroll(now: 1_000, towardTop: false, canMoveUp: true);
        follow.OffsetChanged(now: 1_010, deltaY: 120, atBottom: false);
        Assert.False(follow.IsFollowing);
        follow.OffsetChanged(now: 1_020, deltaY: 120, atBottom: true);
        Assert.True(follow.IsFollowing);
    }

    [Fact]
    public void The_gesture_window_expires()
    {
        var follow = new TranscriptFollow();
        follow.Pause();

        follow.UserScroll(now: 0, towardTop: false, canMoveUp: true);
        follow.OffsetChanged(now: TranscriptFollow.UserWindowMs + 1, deltaY: 50, atBottom: true);

        Assert.False(follow.IsFollowing);
    }

    [Fact]
    public void A_direction_less_gesture_is_decided_by_the_offset_it_causes()
    {
        var follow = new TranscriptFollow();

        follow.UserScroll(now: 0, towardTop: false, canMoveUp: true); // 触摸手势:方向未知
        follow.OffsetChanged(now: 5, deltaY: -40, atBottom: false);

        Assert.False(follow.IsFollowing);
    }

    [Fact]
    public void Dragging_the_scrollbar_never_pins_and_releasing_at_the_bottom_resumes()
    {
        var follow = new TranscriptFollow();
        follow.BeginDrag();
        Assert.False(follow.ShouldPin(atBottom: false));

        follow.OffsetChanged(now: 50_000, deltaY: -300, atBottom: false); // 拖动期间不看时间窗口
        Assert.False(follow.IsFollowing);

        follow.EndDrag(atBottom: true);
        Assert.True(follow.IsFollowing);
        Assert.False(follow.IsDragging);
    }

    [Fact]
    public void Pause_and_resume_are_explicit()
    {
        var follow = new TranscriptFollow();
        follow.UserScroll(now: 0, towardTop: false, canMoveUp: true);
        follow.Pause();

        // 刚才那次操作的窗口作废:点击展开不是「滚到底」
        follow.OffsetChanged(now: 1, deltaY: 10, atBottom: true);
        Assert.False(follow.IsFollowing);

        follow.Resume();
        Assert.True(follow.IsFollowing);
        Assert.True(follow.ShouldPin(atBottom: false));
    }
}
