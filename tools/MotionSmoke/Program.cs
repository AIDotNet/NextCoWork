using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.UI;
using Ncw.UI.Chrome;
using Ncw.UI.Controls;
using Ncw.UI.Views.Desktop;
using Ncw.UI.Views.Desktop.Conversation;
using static Ncw.UI.Tests.ConversationFixtures;

return AppBuilder.Configure<SmokeApp>().UsePlatformDetect().UseNcw().StartWithClassicDesktopLifetime(args);

internal sealed class SmokeApp : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            NcwMotion.Apply(UiMotionLevel.Full);
            var stores = Scenario();
            var vm = new ShellViewModel(stores);
            var shell = new DesktopShell { DataContext = vm, Chrome = new WindowChrome(78, false) };
            var window = new Window { Width = 1500, Height = 860, Title = "motion smoke", Content = shell };
            desktop.MainWindow = window;
            window.Opened += async (_, _) =>
            {
                var failures = 0;
                try
                {
                    failures = await new Run(window, shell, vm).RunAsync();
                }
                catch (Exception e)
                {
                    Console.WriteLine($"SMOKE harness FAIL {e}");
                    failures++;
                }

                Console.WriteLine($"SMOKE done failures={failures}");
                desktop.Shutdown(failures);
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static ClientStores Scenario()
    {
        var stores = Base("s-ncw-2");
        Session(stores, "s-ncw-2",
        [
            User("u1", "把 ProjectPanelViewModel 的会话过滤抽成纯函数,并跑一遍测试。"),
            Assistant("a1", 8,
                Text("我先读文件,然后改。"),
                Call("c1", "Read", "{\"file_path\":\"/Users/token/code/NextCoWork/src/Ncw.Client/ViewModels/ProjectPanelViewModel.cs\"}"),
                Call("c2", "Edit", "{\"file_path\":\"/Users/token/code/NextCoWork/src/Ncw.Client/ViewModels/SessionListQuery.cs\",\"old_string\":\"return sessions;\",\"new_string\":\"return sessions\\n    .Where(s => !s.Archived)\\n    .OrderByDescending(s => s.UpdatedAt);\"}"),
                Call("c3", "Bash", "{\"command\":\"dotnet test tests/Ncw.Client.Tests --no-build\"}")),
            ToolResults("r1", 7,
                Result("c1", "(file contents)"),
                Result("c2", "Edited 1 location"),
                Result("c3", "Passed!  - Failed: 0, Passed: 93, Skipped: 0, Total: 93\nDuration: 195 ms\nRun finished.")),
        ]);
        return stores;
    }
}

/// <summary>一轮真窗口检查。每一项断言「终态精确」,并把动画期间每一帧的间隔统计出来。</summary>
internal sealed class Run(Window window, DesktopShell shell, ShellViewModel vm)
{
    private int _failures;
    private readonly string _out = Environment.GetEnvironmentVariable("NCW_MOTION_SMOKE_OUT") is { Length: > 0 } custom ? custom : Path.Combine(AppContext.BaseDirectory, "motion-smoke");

    private void Report(string name, bool pass, string detail)
    {
        if (!pass)
        {
            _failures++;
        }

        Console.WriteLine($"SMOKE {name} {(pass ? "PASS" : "FAIL")} {detail}");
    }

    private static Task Delay(int ms) => Task.Delay(ms);

    /// <summary>录 <paramref name="ms"/> 毫秒的帧时间(真实的动画帧回调)。<paramref name="at"/> 里的时刻(毫秒)离屏截一张图。</summary>
    private async Task<FrameStats> RecordAsync(int ms, string? shot = null, params int[] at)
    {
        var times = new List<double>();
        var done = new TaskCompletionSource();
        var start = (TimeSpan?)null;
        var shots = new Queue<int>(at.OrderBy(x => x));
        void Frame(TimeSpan now)
        {
            start ??= now;
            var t = (now - start.Value).TotalMilliseconds;
            times.Add(t);
            if (shot is not null && shots.Count > 0 && t >= shots.Peek())
            {
                Capture($"{shot}-{shots.Dequeue()}ms");
            }

            if (t < ms)
            {
                window.RequestAnimationFrame(Frame);
            }
            else
            {
                done.TrySetResult();
            }
        }

        window.RequestAnimationFrame(Frame);
        await done.Task;
        var gaps = times.Zip(times.Skip(1), (a, b) => b - a).OrderBy(x => x).ToList();
        return new FrameStats(times.Count, gaps.Count == 0 ? 0 : gaps[gaps.Count / 2], gaps.Count == 0 ? 0 : gaps[(int)(gaps.Count * 0.95)], gaps.Count == 0 ? 0 : gaps[^1], gaps.Count(g => g > 33));
    }

    private void Capture(string name)
    {
        try
        {
            Directory.CreateDirectory(_out);
            var size = new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height);
            using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
            bitmap.Render(window);
            bitmap.Save(Path.Combine(_out, $"{name}.png"), new PngBitmapEncoderOptions());
        }
        catch (Exception e)
        {
            Console.WriteLine($"SMOKE capture {name} skipped: {e.Message}");
        }
    }

    private NcwCollapsible Detail(string callId)
    {
        var row = vm.Conversation.Rows.OfType<ToolRowViewModel>().First(t => t.Row.CallId == callId);
        return window.GetVisualDescendants().OfType<ToolCard>().First(c => ReferenceEquals(c.DataContext, row)).GetVisualDescendants().OfType<NcwCollapsible>().First();
    }

    public async Task<int> RunAsync()
    {
        await Delay(1200);   // 窗口、字体、首帧都就绪

        // ───── 预热:第一次展开要创建模板、JIT 布局路径,这一下的卡顿不是稳态(单独记,不算进预算) ─────
        var row = vm.Conversation.Rows.OfType<ToolRowViewModel>().First(t => t.Row.CallId == "c3");
        var warm = RecordAsync(900);
        row.IsExpanded = true;
        var warmStats = await warm;
        Console.WriteLine($"SMOKE note warm-up expand (first time, includes template creation / JIT): {warmStats}");
        row.IsExpanded = false;
        await Delay(900);

        // ───── 完整级别:展开 / 收起一张工具卡(稳态,不截图,截图本身会占帧) ─────
        var detail = Detail("c3");
        var open = RecordAsync(900);
        row.IsExpanded = true;
        var stats = await open;
        Report("expand.frames", stats.Count >= 20, $"{stats}");
        Report("expand.smooth", stats.IsSmooth, $"{stats.Describe()}");
        Report("expand.end-state", detail is { Progress: 1, IsVisible: true, IsMoving: false } && detail.Opacity == 1, $"progress={detail.Progress} h={detail.Bounds.Height:F1} moving={detail.IsMoving}");

        var close = RecordAsync(900);
        row.IsExpanded = false;
        stats = await close;
        detail = Detail("c3");
        Report("collapse.smooth", stats.IsSmooth, $"{stats.Describe()}");
        Report("collapse.end-state", detail is { Progress: 0, IsVisible: false, IsMoving: false }, $"progress={detail.Progress} visible={detail.IsVisible} {stats}");

        // ───── 快速连点:每 40 ms 切换一次,共 11 次(奇数 = 最后是开) ─────
        for (var i = 0; i < 11; i++)
        {
            row.IsExpanded = !row.IsExpanded;
            await Delay(40);
        }

        await Delay(1200);
        detail = Detail("c3");
        Report("rapid-toggle.end-state", detail is { Progress: 1, IsVisible: true, IsMoving: false } && detail.Opacity == 1 && row.IsExpanded, $"progress={detail.Progress} open={row.IsExpanded} moving={detail.IsMoving}");
        row.IsExpanded = false;
        await Delay(1200);

        // ───── 左侧区域滑出 / 滑入(整窗重排只有一次) ─────
        var left = shell.GetVisualDescendants().OfType<Grid>().First(g => g.Name == "LeftArea");
        var slideOut = RecordAsync(900);
        vm.ToggleSidebarCommand.Execute(null);
        stats = await slideOut;
        Report("sidebar.out.end-state", !left.IsVisible && left.RenderTransform is null && !shell.IsSidebarVisible, $"visible={left.IsVisible} {stats}");
        Report("sidebar.out.smooth", stats.IsSmooth, $"{stats.Describe()}");
        var slideIn = RecordAsync(900);
        vm.ToggleSidebarCommand.Execute(null);
        stats = await slideIn;
        Report("sidebar.in.end-state", left.IsVisible && left.RenderTransform is null && left.Opacity == 1 && shell.IsSidebarVisible, $"visible={left.IsVisible} {stats}");
        Report("sidebar.in.smooth", stats.IsSmooth, $"{stats.Describe()}");

        // ───── 中间帧截图(离屏渲染,占帧,所以和上面的帧统计分开跑) ─────
        var shots = RecordAsync(900, "tool-expand", 60, 140, 260);
        row.IsExpanded = true;
        await shots;
        shots = RecordAsync(900, "tool-collapse", 60, 140);
        row.IsExpanded = false;
        await shots;
        shots = RecordAsync(900, "sidebar-out", 80, 160);
        vm.ToggleSidebarCommand.Execute(null);
        await shots;
        shots = RecordAsync(900, "sidebar-in", 80, 160);
        vm.ToggleSidebarCommand.Execute(null);
        await shots;

        // ───── 检查器 ─────
        var column = shell.GetVisualDescendants().OfType<Border>().First(b => b.Name == "InspectorColumn");
        vm.ToggleInspectorCommand.Execute(null);
        await RecordAsync(900);
        Report("inspector.out.end-state", !column.IsVisible && column.RenderTransform is null, $"visible={column.IsVisible}");
        vm.ToggleInspectorCommand.Execute(null);
        await RecordAsync(900);
        Report("inspector.in.end-state", column.IsVisible && column.RenderTransform is null && column.Opacity == 1, $"visible={column.IsVisible}");

        // ───── 设置覆盖层 ─────
        var overlay = window.GetVisualDescendants().OfType<SettingsOverlay>().Single();
        vm.Settings.OpenCommand.Execute(null);
        var settingsOpen = RecordAsync(700, "settings-open", 60, 140);
        await settingsOpen;
        Report("settings.open.end-state", overlay.IsVisible && overlay.Opacity == 1, $"visible={overlay.IsVisible} opacity={overlay.Opacity}");
        vm.Settings.CloseCommand.Execute(null);
        await RecordAsync(700);
        Report("settings.close.end-state", !overlay.IsVisible && overlay.Opacity == 1, $"visible={overlay.IsVisible}");

        // ───── 减弱 / 关闭:终态一致,没有动画 ─────
        foreach (var level in new[] { UiMotionLevel.Reduced, UiMotionLevel.Off })
        {
            NcwMotion.Apply(level);
            await Delay(100);
            detail = Detail("c3");
            var started = DateTime.UtcNow;
            row.IsExpanded = true;
            await Delay(450);
            detail = Detail("c3");
            Report($"{level}.expand.end-state", detail is { Progress: 1, IsVisible: true } && detail.Opacity == 1 && !detail.IsMoving, $"progress={detail.Progress} opacity={detail.Opacity} moving={detail.IsMoving} after {(DateTime.UtcNow - started).TotalMilliseconds:F0} ms");
            row.IsExpanded = false;
            await Delay(100);
            detail = Detail("c3");
            Report($"{level}.collapse.immediate", detail is { Progress: 0, IsVisible: false }, $"progress={detail.Progress} visible={detail.IsVisible}");
        }

        NcwMotion.Apply(UiMotionLevel.Full);
        return _failures;
    }

    private readonly record struct FrameStats(int Count, double P50, double P95, double Max, int Dropped)
    {
        public override string ToString() => $"frames={Count} gap p50={P50:F1} p95={P95:F1} max={Max:F1} ms, gaps over 33 ms={Dropped}";

        /// <summary>流畅 = 帧间隔的中位数贴着刷新率(60 Hz 约 16.7 ms),而且丢帧(间隔超过两帧)的不到一成。机器负载高时偶尔的单次卡顿(max)只记录,不判失败。</summary>
        public bool IsSmooth => P50 < 20 && Dropped <= Math.Max(2, Count / 10);

        public string Describe() => ToString();
    }
}
