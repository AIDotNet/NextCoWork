using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Ncw.Client.Mock;
using Ncw.Client.Stores;
using Ncw.UI.Controls;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Motion;

namespace Ncw.UI.Tests;

/// <summary>
/// 统一动效系统(docs/design/03 §9)的单测。<b>Headless 下能推进动画时钟</b>:Arc 的 <see cref="ManualFrameClock"/> 让每一帧只在 <c>Advance</c> 时才跑,
/// 所以可以确定性地停在「动画进行到一半」去断言高度 / 透明度 / 变换,而不靠 sleep。每个测试结束恢复到 Off(测试默认关闭动效,见 <see cref="TestApp"/>)。
/// </summary>
public class MotionTests : IDisposable
{
    private readonly ManualFrameClock _clock = new();
    private readonly IDisposable _install;

    public MotionTests()
    {
        NcwMotion.Apply(UiMotionLevel.Full);
        _install = _clock.Install();
    }

    public void Dispose()
    {
        _install.Dispose();
        NcwMotion.Apply(UiMotionLevel.Off);
        NcwMotion.SystemReducesMotionProbe = static () => null;
    }

    private static void Pump()
    {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }

    /// <summary>推进动画时钟 <paramref name="ms"/> 毫秒(每 16 ms 一帧),每帧后跑一遍布局。</summary>
    private void Advance(int ms)
    {
        for (var elapsed = 0; elapsed < ms; elapsed += 16)
        {
            _clock.Advance(TimeSpan.FromMilliseconds(Math.Min(16, ms - elapsed)));
            Pump();
        }
    }

    private static Window Show(Control content, double width = 400, double height = 500)
    {
        var window = new Window { Width = width, Height = height, Content = content };
        window.Show();
        Pump();
        return window;
    }

    private static (Window Window, NcwCollapsible Collapsible, Border Content, StackPanel Stack) CollapsibleFixture(bool open = false, double contentHeight = 100)
    {
        var content = new Border { Height = contentHeight, Width = 200, Background = Brushes.Red };
        var collapsible = new NcwCollapsible { Child = content, IsOpen = open };
        var stack = new StackPanel { Children = { new TextBlock { Text = "head" }, collapsible, new TextBlock { Text = "tail" } } };
        return (Show(stack), collapsible, content, stack);
    }

    // ───────────────────────── 时长 / 缓动 / 级别:来自 Arc,不另造 ─────────────────────────

    [AvaloniaFact]
    public void Tiers_are_arcs_duration_tokens_not_a_parallel_set()
    {
        Show(new Border());
        var app = Application.Current!;
        foreach (var (tier, kind) in new[]
                 {
                     (NcwMotionTier.Instant, ArcDesignTokenKind.DurationInstant), (NcwMotionTier.Fast, ArcDesignTokenKind.DurationFast),
                     (NcwMotionTier.Base, ArcDesignTokenKind.DurationStandard), (NcwMotionTier.Slow, ArcDesignTokenKind.DurationConsidered),
                 })
        {
            Assert.True(app.TryFindResource(ArcDesignTokens.Key(kind), out var token));
            Assert.Equal((TimeSpan)token!, NcwMotion.Duration(tier));   // 数值就是 Arc 主题里的 token
        }

        Assert.True(app.TryFindResource(ArcDesignTokens.Key(ArcDesignTokenKind.DurationExit), out var exit));
        Assert.Equal((TimeSpan)exit!, NcwMotion.ExitDuration);

        // 档位递增,退出比进入快(Arc 的约定)
        Assert.True(NcwMotion.Duration(NcwMotionTier.Instant) < NcwMotion.Duration(NcwMotionTier.Fast));
        Assert.True(NcwMotion.Duration(NcwMotionTier.Fast) < NcwMotion.Duration(NcwMotionTier.Base));
        Assert.True(NcwMotion.Duration(NcwMotionTier.Base) < NcwMotion.Duration(NcwMotionTier.Slow));
        Assert.True(NcwMotion.ExitDuration < NcwMotion.Duration(NcwMotionTier.Base));
        Assert.Same(ArcEasings.Enter, NcwMotion.EnterEasing);   // 进入减速
        Assert.Same(ArcEasings.Exit, NcwMotion.ExitEasing);     // 退出加速
        Assert.Equal(ArcSpring.Smooth, NcwMotion.ExpandSpring); // 展开收起用 Arc 的 Smooth 弹簧
        Assert.Equal(6, NcwMotion.RiseOffset);
    }

    [AvaloniaTheory]
    [InlineData(UiMotionPreference.Full, null, ArcMotionLevel.Full)]
    [InlineData(UiMotionPreference.Reduced, null, ArcMotionLevel.Reduced)]
    [InlineData(UiMotionPreference.Off, null, ArcMotionLevel.Off)]
    [InlineData(UiMotionPreference.System, true, ArcMotionLevel.Reduced)]
    [InlineData(UiMotionPreference.System, false, ArcMotionLevel.Full)]
    [InlineData(UiMotionPreference.System, null, ArcMotionLevel.Full)]
    public void Applying_a_preference_sets_the_arc_theme_level_for_everything_below_it(UiMotionPreference preference, bool? system, ArcMotionLevel expected)
    {
        var probe = new Border();
        Show(probe);
        NcwMotion.SystemReducesMotionProbe = () => system;

        NcwMotion.Apply(preference);

        Assert.Equal(expected, NcwMotion.EffectiveLevel(probe));
        Assert.Equal(expected, ArcMotion.GetEffectiveLevel(probe));
    }

    [AvaloniaFact]
    public void Applying_the_motion_level_does_not_reset_the_appearance_or_the_accent()
    {
        Show(new Border());
        ArcTheme.Apply(t => t.Appearance(ArcAppearance.Light).Accent(ArcAccentPreset.Green));
        object? Token(ArcDesignTokenKind kind) => Application.Current!.TryFindResource(ArcDesignTokens.Key(kind), out var v) ? v : null;
        var background = Token(ArcDesignTokenKind.Background);
        var accent = Token(ArcDesignTokenKind.Accent);

        NcwMotion.Apply(UiMotionLevel.Reduced);
        NcwMotion.Apply(UiMotionPreference.Off);

        Assert.Equal(background, Token(ArcDesignTokenKind.Background));   // 改动效级别只改 MotionMode,不动浅色 / 深色和强调色
        Assert.Equal(accent, Token(ArcDesignTokenKind.Accent));
        ArcTheme.Apply(t => t.Appearance(ArcAppearance.Dark).Accent(ArcAccentPreset.Blue));
    }

    [AvaloniaFact]
    public void Binding_the_prefs_store_applies_now_and_follows_later_changes_until_disposed()
    {
        var probe = new Border();
        Show(probe);
        var stores = MockWorkspace.Create(DateTimeOffset.Now);
        stores.Prefs.Update(p => p with { Motion = UiMotionPreference.Reduced });

        using (NcwMotion.Bind(stores))
        {
            Assert.Equal(ArcMotionLevel.Reduced, NcwMotion.EffectiveLevel(probe));
            stores.Prefs.Update(p => p with { Motion = UiMotionPreference.Off });
            Pump();
            Assert.Equal(ArcMotionLevel.Off, NcwMotion.EffectiveLevel(probe));
        }

        stores.Prefs.Update(p => p with { Motion = UiMotionPreference.Full });
        Pump();
        Assert.Equal(ArcMotionLevel.Off, NcwMotion.EffectiveLevel(probe));   // 释放之后不再跟随
    }

    // ───────────────────────── NcwCollapsible 状态机 ─────────────────────────

    [AvaloniaFact]
    public void An_initially_open_collapsible_is_open_at_once_without_any_motion()
    {
        var (_, c, content, _) = CollapsibleFixture(open: true);

        Assert.Equal(1, c.Progress);
        Assert.False(c.IsMoving);
        Assert.True(c.IsVisible);
        Assert.Equal(100, c.Bounds.Height);
        Assert.Equal(1, c.Opacity);
        Assert.False(c.ClipToBounds);   // 到站后不裁剪,阴影 / 弹出内容照常溢出
        Assert.NotNull(content.Parent);
    }

    [AvaloniaFact]
    public void Opening_passes_through_a_partial_height_then_lands_exactly_open()
    {
        var (_, c, _, stack) = CollapsibleFixture();
        Assert.False(c.IsVisible);
        Assert.Equal(0, c.Bounds.Height);

        c.IsOpen = true;
        Pump();
        Assert.True(c.IsVisible);
        Advance(80);

        Assert.InRange(c.Progress, 0.05, 0.95);        // 动画进行到一半:能抓到中间帧
        Assert.InRange(c.Bounds.Height, 5, 95);
        Assert.True(c.ClipToBounds);                    // 中间帧裁剪内容
        Assert.True(c.IsMoving);
        var tail = stack.Children[2];
        Assert.True(tail.Bounds.Y < 20 + 100);          // 后面的内容跟着往下推,但还没到终点

        Advance(1200);

        Assert.Equal(1, c.Progress);
        Assert.Equal(100, c.Bounds.Height);
        Assert.False(c.IsMoving);
        Assert.False(c.ClipToBounds);
        Assert.Equal(1, c.Opacity);
    }

    [AvaloniaFact]
    public void Closing_ends_hidden_and_out_of_layout_and_the_height_never_goes_negative()
    {
        var (_, c, _, _) = CollapsibleFixture(open: true);

        c.IsOpen = false;
        var seen = new List<double>();
        for (var i = 0; i < 80; i++)
        {
            Advance(16);
            seen.Add(c.Bounds.Height);
        }

        Assert.All(seen, h => Assert.InRange(h, 0, 100.0001));
        Assert.Contains(seen, h => h is > 1 and < 99);   // 中间帧确实存在
        Assert.Equal(0, c.Progress);
        Assert.False(c.IsVisible);                        // 内容不参与布局、焦点、命中测试
        Assert.False(c.IsMoving);
    }

    [AvaloniaFact]
    public void Rapid_toggling_continues_from_the_current_position_and_ends_in_the_last_requested_state()
    {
        var (_, c, _, _) = CollapsibleFixture();
        var progress = new List<double>();

        for (var i = 0; i < 9; i++)
        {
            c.IsOpen = !c.IsOpen;     // 开、关、开……每 40 ms 连点一次
            Advance(40);
            progress.Add(c.Progress);
        }

        Assert.True(c.IsOpen);        // 奇数次:最后是开
        // 连续:相邻采样之间没有「回到起点」式的跳变
        for (var i = 1; i < progress.Count; i++)
        {
            Assert.InRange(Math.Abs(progress[i] - progress[i - 1]), 0, 0.6);
        }

        Advance(1500);
        Assert.Equal(1, c.Progress);
        Assert.Equal(100, c.Bounds.Height);
        Assert.False(c.IsMoving);

        // 偶数次连点:最后是关,同样不会停在半截
        c.IsOpen = false; Advance(30); c.IsOpen = true; Advance(30); c.IsOpen = false;
        Advance(1500);
        Assert.Equal(0, c.Progress);
        Assert.False(c.IsVisible);
    }

    [AvaloniaFact]
    public void Content_that_changes_height_mid_animation_still_lands_on_the_new_height()
    {
        var (_, c, content, _) = CollapsibleFixture();

        c.IsOpen = true;
        Advance(60);
        content.Height = 240;          // 动画途中数据更新,内容变高(流式输出、预览多了几行)
        Advance(40);
        content.Height = 160;          // 又变矮
        Advance(1500);

        Assert.Equal(1, c.Progress);
        Assert.Equal(160, c.Bounds.Height);
        Assert.False(c.IsMoving);
    }

    [AvaloniaFact]
    public void Reduced_motion_jumps_the_size_at_once_and_only_fades_in_and_closes_instantly()
    {
        var (_, c, _, _) = CollapsibleFixture();
        NcwMotion.Apply(UiMotionLevel.Reduced);

        c.IsOpen = true;
        Pump();

        Assert.Equal(1, c.Progress);                  // 尺寸直接跳到终态
        Assert.Equal(100, c.Bounds.Height);
        Assert.True(c.Opacity < 1);                    // 只有短淡入
        Advance(60);
        Assert.InRange(c.Opacity, 0.01, 0.99);
        Advance(400);
        Assert.Equal(1, c.Opacity);

        c.IsOpen = false;
        Pump();
        Assert.Equal(0, c.Progress);                  // 收起没有时长
        Assert.False(c.IsVisible);
    }

    [AvaloniaFact]
    public void Off_applies_every_change_immediately_with_the_same_end_state()
    {
        var (_, c, _, _) = CollapsibleFixture();
        NcwMotion.Apply(UiMotionLevel.Off);

        c.IsOpen = true;
        Pump();
        Assert.Equal((1d, 100d, 1d, false), (c.Progress, c.Bounds.Height, c.Opacity, c.IsMoving));

        c.IsOpen = false;
        Pump();
        Assert.Equal((0d, false), (c.Progress, c.IsVisible));
    }

    [AvaloniaFact]
    public void Leaving_the_visual_tree_mid_animation_lands_on_the_target_and_stops_asking_for_frames()
    {
        var (window, c, _, stack) = CollapsibleFixture();
        c.IsOpen = true;
        Advance(50);
        Assert.True(c.IsMoving);

        stack.Children.Remove(c);       // 离开界面树(虚拟化回收 / 页面被丢弃)
        Pump();

        Assert.False(c.IsMoving);
        Assert.Equal(1, c.Progress);    // 落在目标状态,不是半截
        var requests = _clock.RequestCount;
        Advance(300);
        Assert.Equal(requests, _clock.RequestCount);   // 没有泄漏的帧订阅 / 定时器
        window.Close();
    }

    [AvaloniaFact]
    public void A_recycled_container_whose_data_context_changes_jumps_instead_of_animating_from_the_old_rows_state()
    {
        var collapsible = new NcwCollapsible { Child = new Border { Height = 100, Width = 100 } };
#pragma warning disable IL2026, IL3050 // 测试里用反射绑定去模拟 XAML 里的 {Binding}(产品代码用编译绑定)
        collapsible.Bind(NcwCollapsible.IsOpenProperty, new Avalonia.Data.Binding("Open"));
#pragma warning restore IL2026, IL3050
        var rowA = new Row { Open = false };
        var rowB = new Row { Open = true };
        var host = new ContentControl { DataContext = rowA, Content = collapsible };
        Show(host);

        host.DataContext = rowB;       // 虚拟化列表复用容器:数据上下文整个换掉,IsOpen 随之从 false 变成 true
        Pump();

        Assert.True(collapsible.IsOpen);
        Assert.Equal(1, collapsible.Progress);        // 没有动画,直接就位
        Assert.False(collapsible.IsMoving);

        rowB.Open = false;             // 用户的操作:才有动画
        Pump();
        Advance(40);
        Assert.InRange(collapsible.Progress, 0.01, 0.99);
    }

    private sealed class Row : Avalonia.Controls.Primitives.TemplatedControl
    {
        public static readonly StyledProperty<bool> OpenProperty = AvaloniaProperty.Register<Row, bool>(nameof(Open));

        public bool Open
        {
            get => GetValue(OpenProperty);
            set => SetValue(OpenProperty, value);
        }
    }

    // ───────────────────────── 出现 / 消失 ─────────────────────────

    [AvaloniaFact]
    public void Arrive_rises_and_fades_in_then_restores_the_original_opacity_and_transform()
    {
        var box = new Border { Width = 100, Height = 40, Opacity = 0.8 };
        Show(new StackPanel { Children = { box } });

        NcwMotion.Arrive(box, NcwArrival.Rise);
        Advance(40);

        Assert.True(NcwMotion.IsArriving(box));
        Assert.InRange(box.Opacity, 0.01, 0.79);
        var translate = Assert.IsType<TranslateTransform>(box.RenderTransform);
        Assert.InRange(translate.Y, 0.5, NcwMotion.RiseOffset);   // 轻微上移:最多 6 px,只动 RenderTransform

        Advance(600);

        Assert.False(NcwMotion.IsArriving(box));
        Assert.Equal(0.8, box.Opacity);                              // 恢复成原来的透明度
        Assert.Null(box.RenderTransform);                            // 不留变换
    }

    [AvaloniaFact]
    public void Arrive_does_nothing_when_motion_is_off_and_only_fades_when_reduced()
    {
        var box = new Border { Width = 100, Height = 40 };
        Show(new StackPanel { Children = { box } });

        NcwMotion.Apply(UiMotionLevel.Off);
        NcwMotion.Arrive(box);
        Assert.False(NcwMotion.IsArriving(box));
        Assert.Equal(1, box.Opacity);

        NcwMotion.Apply(UiMotionLevel.Reduced);
        NcwMotion.Arrive(box);
        Advance(40);
        Assert.True(box.Opacity < 1);
        Assert.Null(box.RenderTransform);                            // 减弱:没有位移
        Advance(400);
        Assert.Equal(1, box.Opacity);
    }

    [AvaloniaFact]
    public void Restarting_an_arrival_mid_flight_and_resting_a_recycled_container_leave_no_residue()
    {
        var box = new Border { Width = 100, Height = 40 };
        Show(new StackPanel { Children = { box } });

        NcwMotion.Arrive(box);
        Advance(30);
        NcwMotion.Arrive(box);                                       // 又来一个新行
        Advance(30);
        NcwMotion.Rest(box);                                         // 容器被回收

        Assert.False(NcwMotion.IsArriving(box));
        Assert.Equal(1, box.Opacity);
        Assert.Null(box.RenderTransform);
        var requests = _clock.RequestCount;
        Advance(200);
        Assert.Equal(requests, _clock.RequestCount);
    }

    [AvaloniaFact]
    public void IsPresent_fades_out_before_hiding_and_reverses_from_the_current_position()
    {
        var box = new Border { Width = 100, Height = 40 };
        NcwMotion.SetPresenceKind(box, NcwArrival.Fade);
        Show(new StackPanel { Children = { box } });
        Assert.True(box.IsVisible);

        NcwMotion.SetIsPresent(box, false);
        Advance(110);
        Assert.True(box.IsVisible);                                  // 退出动画还在播,没有立刻消失
        var mid = box.Opacity;
        Assert.InRange(mid, 0.01, 0.99);

        NcwMotion.SetIsPresent(box, true);                           // 退到一半又要出现:从当前位置接着走
        Advance(16);
        Assert.True(box.Opacity > mid - 0.05);
        Advance(600);
        Assert.True(box.IsVisible);
        Assert.Equal(1, box.Opacity);

        NcwMotion.SetIsPresent(box, false);
        Advance(600);
        Assert.False(box.IsVisible);                                 // 退完才隐藏
        Assert.Equal(1, box.Opacity);                                // 隐藏后透明度已恢复,下次出现不会从 0 卡住
        Assert.False(NcwMotion.IsArriving(box));
    }

    [AvaloniaFact]
    public void IsPresent_has_no_transition_when_motion_is_reduced_or_off()
    {
        var box = new Border { Width = 100, Height = 40 };
        Show(new StackPanel { Children = { box } });

        NcwMotion.Apply(UiMotionLevel.Reduced);
        NcwMotion.SetIsPresent(box, false);
        Assert.False(box.IsVisible);                                 // 减弱:没有退出过渡

        NcwMotion.Apply(UiMotionLevel.Off);
        NcwMotion.SetIsPresent(box, true);
        Assert.True(box.IsVisible);
        Assert.Equal(1, box.Opacity);
        Assert.False(NcwMotion.IsArriving(box));
    }

    [AvaloniaFact]
    public void IsPresent_set_before_the_control_is_attached_just_sets_visibility()
    {
        var box = new Border { Width = 100, Height = 40 };
        NcwMotion.SetIsPresent(box, false);
        Assert.False(box.IsVisible);

        Show(new StackPanel { Children = { box } });
        Assert.False(box.IsVisible);
        Assert.Equal(1, box.Opacity);
    }

    [AvaloniaFact]
    public void ShowMotion_plays_when_a_pane_becomes_visible_but_not_on_first_attach()
    {
        var pane = new Border { Width = 100, Height = 40, IsVisible = false };
        NcwMotion.SetShowMotion(pane, NcwArrival.Fade);
        Show(new StackPanel { Children = { pane } });
        Assert.False(NcwMotion.IsArriving(pane));

        pane.IsVisible = true;
        Advance(40);

        Assert.True(NcwMotion.IsArriving(pane));
        Assert.InRange(pane.Opacity, 0.01, 0.99);
        Advance(600);
        Assert.Equal(1, pane.Opacity);
        Assert.False(NcwMotion.IsArriving(pane));
    }

    // ───────────────────────── 运行中指示 ─────────────────────────

    [AvaloniaFact]
    public void Busy_spin_rotates_under_full_breathes_under_reduced_and_rests_under_off()
    {
        var icon = new Border { Width = 16, Height = 16 };
        NcwMotion.SetBusy(icon, NcwBusyKind.Spin);
        Show(new StackPanel { Children = { icon } });

        Advance(250);
        var rotate = Assert.IsType<RotateTransform>(icon.RenderTransform);
        Assert.InRange(rotate.Angle, 10, 350);                       // 完整:在转

        NcwMotion.Apply(UiMotionLevel.Reduced);
        Advance(700);
        Assert.Null(icon.RenderTransform);                           // 减弱:不转
        Assert.InRange(icon.Opacity, ArcBusyPulse.MinOpacity - 0.001, 1);   // 只有 Arc 规定的「忙」呼吸(明暗)

        NcwMotion.Apply(UiMotionLevel.Off);
        Advance(100);
        Assert.Null(icon.RenderTransform);                           // 关闭:静止
        Assert.Equal(1, icon.Opacity);
    }

    [AvaloniaFact]
    public void Busy_stops_requesting_frames_when_hidden_or_detached()
    {
        var icon = new Border { Width = 16, Height = 16 };
        NcwMotion.SetBusy(icon, NcwBusyKind.Breathe);
        var stack = new StackPanel { Children = { icon } };
        Show(stack);
        Advance(100);
        Assert.NotNull(icon.RenderTransform);                        // 呼吸:轻微缩放

        icon.IsVisible = false;                                      // 看不见的指示不跑
        Pump();
        var requests = _clock.RequestCount;
        Advance(200);
        Assert.Equal(requests, _clock.RequestCount);

        icon.IsVisible = true;
        stack.Children.Remove(icon);                                 // 离开界面
        Pump();
        requests = _clock.RequestCount;
        Advance(200);
        Assert.Equal(requests, _clock.RequestCount);
        Assert.Null(icon.RenderTransform);
        Assert.Equal(1, icon.Opacity);
    }

    [AvaloniaFact]
    public void ArriveOnAttach_plays_each_time_a_small_list_item_is_attached()
    {
        var host = new StackPanel();
        Show(host);
        var item = new Border { Width = 100, Height = 40 };
        NcwMotion.SetArriveOnAttach(item, NcwArrival.Rise);

        host.Children.Add(item);                                     // 例如「已在另一台设备上处理」的提示出现
        Pump();
        Assert.True(NcwMotion.IsArriving(item));
        Advance(600);
        Assert.False(NcwMotion.IsArriving(item));
        Assert.Equal(1, item.Opacity);
        Assert.Null(item.RenderTransform);
    }

    [AvaloniaFact]
    public void The_hold_fill_follows_the_gesture_linearly_and_only_the_rewind_is_eased()
    {
        var hold = new NcwHoldButton { Width = 200, Height = 44 };
        Show(new StackPanel { Children = { hold } });

        hold.Progress = 0.3;                                         // 按住:画面 = 手势进度,不加缓动(它就是手势的时钟)
        Assert.Equal(0.3, hold.DisplayProgress);
        hold.Progress = 0.62;
        Assert.Equal(0.62, hold.DisplayProgress);

        hold.Progress = 0;                                           // 松手早了:回退走统一的退出缓动,不是瞬间归零
        Assert.Equal(0.62, hold.DisplayProgress, 3);
        Advance(80);
        Assert.InRange(hold.DisplayProgress, 0.01, 0.61);
        Advance(400);
        Assert.Equal(0, hold.DisplayProgress);

        hold.Progress = 0.5;                                         // 回退途中又按下:从画面当前位置接着,不跳变
        hold.Progress = 0.1;
        Advance(40);
        hold.Progress = 0.45;
        Assert.Equal(0.45, hold.DisplayProgress);

        NcwMotion.Apply(UiMotionLevel.Off);                          // 关闭 / 减弱:回退也是立即的
        hold.Progress = 0;
        Assert.Equal(0, hold.DisplayProgress);
    }

    [AvaloniaFact]
    public void The_shared_spin_style_drives_the_busy_behavior_instead_of_an_endless_style_animation()
    {
        var spinner = new TKCode.Arc.Icons.Lucide.LucideIcon { Kind = TKCode.Arc.Icons.Lucide.LucideIconKind.LoaderCircle };
        spinner.Classes.Add("spin");
        var host = new StackPanel { Children = { spinner } };
        host.Styles.Add(new Ncw.UI.Themes.NcwStyles());
        Show(host);

        Assert.Equal(NcwBusyKind.Spin, NcwMotion.GetBusy(spinner));
        Advance(250);
        Assert.IsType<RotateTransform>(spinner.RenderTransform);

        NcwMotion.Apply(UiMotionLevel.Off);                          // 关闭级别:同一个图标静止(以前的样式动画不看级别)
        Advance(100);
        Assert.Null(spinner.RenderTransform);
    }
}
