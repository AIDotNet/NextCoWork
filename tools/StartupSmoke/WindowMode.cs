using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.Desktop;
using Ncw.UI;
using Ncw.UI.Controls;
using Ncw.UI.Views.Desktop;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;

namespace StartupSmoke;

/// <summary>
/// 真实窗口里的完整启动:和 <c>Program.Main</c> 同样的装配(真实的 LocalBackend、真实的 ncw-agent、真实的 NcwApp),
/// 再挂上探针记录:原生窗口底色、第一帧、内容出现、淡出开始 / 结束的时间,整段期间的帧间隔,淡出时外壳有没有布局跳动,释放之后有没有残留。
/// 中间帧用 RenderTargetBitmap 离屏截图(不需要系统的「屏幕录制」权限)。
/// </summary>
internal static class WindowMode
{
    public static int Run(string agent, string work, string shots, string[] args)
    {
        Environment.SetEnvironmentVariable("NCW_AGENT_PATH", agent);
        SmokeApp.Out = shots;
        SmokeApp.Started = DateTimeOffset.UtcNow;
        SmokeApp.SkipNativeBackground = args.Contains("--no-native-background");
        NcwApp.BackendFactory = () => new LocalBackend();
        NcwApp.EarlyMilestones =
        [
            (StartupMilestone.ProcessStarted, ProcessStart()),
            (StartupMilestone.MainEntered, SmokeApp.Started),
            (StartupMilestone.InstanceReady, DateTimeOffset.UtcNow),
        ];
        NcwApp.NativeWindowBackground = args.Contains("--no-native-background") ? null : NativeWindowBackground.Apply;
        Console.WriteLine($"SMOKE note shots -> {shots}");
        AppBuilder.Configure<SmokeApp>().UsePlatformDetect().UseNcw().StartWithClassicDesktopLifetime(args);
        Console.WriteLine($"SMOKE done failures={SmokeApp.Failures}");
        return SmokeApp.Failures;
    }

    private static DateTimeOffset ProcessStart()
    {
        using var self = Process.GetCurrentProcess();
        return new DateTimeOffset(self.StartTime.ToUniversalTime());
    }
}

internal sealed class SmokeApp : NcwApp
{
    public static string Out = "";
    public static DateTimeOffset Started;
    public static bool SkipNativeBackground;
    public static int Failures;

    private static void Report(string name, bool pass, string detail)
    {
        if (!pass)
        {
            Failures++;
        }

        Console.WriteLine($"SMOKE {name} {(pass ? "PASS" : "FAIL")} {detail}");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: MainWindow window } desktop)
        {
            _ = new Probe(window, (ShellViewModel)window.DataContext!, desktop).RunAsync();
        }
    }

    private sealed class Probe(MainWindow window, ShellViewModel vm, IClassicDesktopStyleApplicationLifetime desktop)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<(double T, double Gap)> _frames = [];
        private double _lastFrame = -1;
        private bool _stop;

        private double Now => _clock.Elapsed.TotalMilliseconds;

        private DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

        private void Capture(string name)
        {
            try
            {
                Directory.CreateDirectory(Out);
                var size = new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height);
                using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
                bitmap.Render(window);
                bitmap.Save(Path.Combine(Out, name + ".png"), new PngBitmapEncoderOptions());
            }
            catch (Exception e)
            {
                Console.WriteLine($"SMOKE note capture {name} skipped: {e.Message}");
            }
        }

        private (byte R, byte G, byte B)? PixelAt(RenderTargetBitmap bitmap, int x, int y)
        {
            // 读一个像素:把位图保存成 PNG 再读太重;用 CopyPixels
            var buffer = new byte[4];
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(buffer, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                bitmap.CopyPixels(new PixelRect(x, y, 1, 1), handle.AddrOfPinnedObject(), 4, 4);
                return (buffer[2], buffer[1], buffer[0]);   // BGRA
            }
            finally
            {
                handle.Free();
            }
        }

        public async Task RunAsync()
        {
            // 窗口刚创建、还没显示:原生窗口底色(NcwApp 已经按 Arc 的 Background token 设过)
            var token = ArcTokens.GetColor(window, ArcDesignTokenKind.Background);
            var native = Ncw.Desktop.NativeWindowBackground.TryRead(window);
            if (SkipNativeBackground)
            {
                Console.WriteLine($"SMOKE note native window background WITHOUT our fix (system default): {(native is { } d ? $"#{d.R:X2}{d.G:X2}{d.B:X2}" : "unreadable")}; Arc Background token is #{token.R:X2}{token.G:X2}{token.B:X2}");
            }
            else
            {
                Report("window.native-background", native is { } n && Math.Abs(n.R - token.R) <= 1 && Math.Abs(n.G - token.G) <= 1 && Math.Abs(n.B - token.B) <= 1,
                    $"NSWindow.backgroundColor read back = {(native is { } v ? $"#{v.R:X2}{v.G:X2}{v.B:X2}" : "unreadable (not macOS / handle unavailable)")}; Arc Background token = #{token.R:X2}{token.G:X2}{token.B:X2}");
            }

            var opened = new TaskCompletionSource<double>();
            window.Opened += (_, _) => opened.TrySetResult(Now);
            var timeline = new List<(double T, string What)>();
            void Note(string what) => timeline.Add((Now, what));

            var openedAt = await opened.Task;
            var sinceLaunch = (UtcNow - Started).TotalMilliseconds;
            Note("window opened");
            Console.WriteLine($"SMOKE note window opened {openedAt:F0} ms after the probe started ({sinceLaunch:F0} ms after Main)");

            // 第一帧:覆盖层的底色已经在,内容还没出现(延迟);用离屏渲染读中心像素和一个角上的像素
            var firstFrame = new TaskCompletionSource<double>();
            window.RequestAnimationFrame(_ => firstFrame.TrySetResult(Now));
            Report("window.first-frame", await Task.WhenAny(firstFrame.Task, Task.Delay(5000)) == firstFrame.Task, $"first animation frame {firstFrame.Task.GetAwaiter().GetResult():F0} ms after the probe started");
            var overlay = window.GetVisualDescendants().OfType<StartupOverlay>().SingleOrDefault();
            var size = new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height);
            using (var first = new RenderTargetBitmap(size, new Vector(96, 96)))
            {
                first.Render(window);
                var corner = PixelAt(first, size.Width / 2, size.Height - 40);
                var center = PixelAt(first, size.Width / 2, size.Height / 2);
                bool IsToken((byte R, byte G, byte B)? c) => c is { } p && Math.Abs(p.R - token.R) <= 1 && Math.Abs(p.G - token.G) <= 1 && Math.Abs(p.B - token.B) <= 1;
                Report("window.first-frame-is-loading-backdrop", overlay is { IsVisible: true } && IsToken(corner) && IsToken(center),
                    $"overlay visible={overlay?.IsVisible}; offscreen render of the first frame: pixel(center)={center}, pixel(bottom-middle)={corner} vs token #{token.R:X2}{token.G:X2}{token.B:X2}; content visible={vm.Startup.IsContentVisible}");
            }

            Capture("01-first-frame");

            // 整段期间记录每一帧
            void Frame(TimeSpan _)
            {
                var t = Now;
                if (_lastFrame >= 0)
                {
                    _frames.Add((t, t - _lastFrame));
                }

                _lastFrame = t;
                if (!_stop)
                {
                    window.RequestAnimationFrame(Frame);
                }
            }

            window.RequestAnimationFrame(Frame);

            // 盯着 ViewModel 的几个时刻
            double? contentAt = null, revealAt = null, releasedAt = null, awaitingAt = null;
            var shot2 = false;
            var shot3 = false;
            var shotReveal = false;
            var seen = new HashSet<string>();
            string? layoutBefore = null;
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromMinutes(2) && releasedAt is null)
            {
                await Task.Delay(8);
                var s = vm.Startup;
                var stage = $"{s.Current}/{s.Detail}";
                if (seen.Add(stage))
                {
                    Note($"stage {stage} fraction={s.Fraction:F2} steps={s.StepsDone}/{s.StepsTotal}");
                }

                if (contentAt is null && s.IsContentVisible)
                {
                    contentAt = Now;
                    Note("content visible");
                }

                if (!shot2 && s.IsContentVisible && Now - (contentAt ?? Now) > 120)
                {
                    shot2 = true;
                    Capture("02-content");
                }

                if (!shot3 && s.IsContentVisible && s.StepsDone >= 3)
                {
                    shot3 = true;
                    Capture("03-mid-load");
                }

                if (awaitingAt is null && s.IsAwaitingShell)
                {
                    awaitingAt = Now;
                    Note("all steps done, awaiting the shell layout");
                }

                if (revealAt is null && s.IsRevealing)
                {
                    revealAt = Now;
                    Note("reveal (fade-out) started");
                    layoutBefore = ShellBounds();
                }

                if (!shotReveal && revealAt is not null && Now - revealAt > 70)
                {
                    shotReveal = true;
                    Capture("04-reveal-mid");
                }

                if (revealAt is not null && !window.GetVisualDescendants().OfType<StartupOverlay>().Any())
                {
                    releasedAt = Now;
                    Note("overlay released (fade-out finished, removed from the tree)");
                }
            }

            await Task.Delay(500);
            Capture("05-after");
            _stop = true;
            var layoutAfter = ShellBounds();

            // ───── 结果 ─────
            Console.WriteLine("\nWINDOW TIMELINE (ms since the probe started; the first row is the window opening)");
            foreach (var (t, what) in timeline)
            {
                Console.WriteLine($"  {t,8:F0}  {what}");
            }

            var facts = vm.Stores.Startup.State.Facts!;
            Console.WriteLine("\nSTARTUP MILESTONES (ms since process start)");
            foreach (var (m, ms) in StartupProgress.Timeline(facts))
            {
                Console.WriteLine($"  {ms,8:F0}  {m}");
            }

            Report("window.completes", releasedAt is not null && facts.Has(StartupMilestone.Interactive), $"content visible at {contentAt?.ToString("F0") ?? "never"} ms, reveal started {revealAt?.ToString("F0") ?? "never"} ms, released {releasedAt?.ToString("F0") ?? "never"} ms");
            if (contentAt is not null && revealAt is not null && releasedAt is not null)
            {
                Report("window.fade-duration", releasedAt - revealAt is > 0 and < 600, $"fade-out ran {releasedAt - revealAt:F0} ms (NcwMotion ExitDuration is 180 ms; the offset includes the 8 ms polling and two animation frames)");
            }

            Report("window.no-layout-jump", layoutBefore is not null && layoutAfter == layoutBefore, $"shell bounds at reveal start {layoutBefore} vs after release {layoutAfter}");

            // 帧间隔:加载期间 / 淡出期间
            double[] Gaps(double from, double to) => _frames.Where(f => f.T >= from && f.T <= to).Select(f => f.Gap).Order().ToArray();
            string Describe(double[] g) => g.Length == 0 ? "no frames" : $"frames={g.Length} median={g[g.Length / 2]:F1} p95={g[(int)(g.Length * 0.95)]:F1} max={g[^1]:F1} ms, gaps over 33 ms={g.Count(x => x > 33)}";
            Console.WriteLine($"SMOKE note frames while loading ({openedAt:F0}..{revealAt - 1:F0}): {Describe(Gaps(openedAt, (revealAt ?? Now) - 1))}");
            if (revealAt is not null && releasedAt is not null)
            {
                var fade = Gaps(revealAt.Value, releasedAt.Value + 50);
                Console.WriteLine($"SMOKE note frames during the fade-out ({revealAt:F0}..{releasedAt:F0}): {Describe(fade)}");
                Report("window.fade-frames", fade.Length >= 3 && fade[fade.Length / 2] < 25, Describe(fade));
            }

            // 释放之后:没有残留
            var left = window.GetVisualDescendants().OfType<StartupOverlay>().Count();
            var arcs = window.GetVisualDescendants().OfType<NcwProgressArc>().Count();
            Report("window.released-everything", left == 0 && arcs == 0 && !vm.Startup.OwnsScreen && !vm.Startup.IsPresented && vm.Startup.NextWake is null,
                $"StartupOverlay in the tree={left}, NcwProgressArc in the tree={arcs}, vm.OwnsScreen={vm.Startup.OwnsScreen}, vm.NextWake={vm.Startup.NextWake}");

            // 空闲 CPU:没有残留的动画循环 / 定时器 → 几乎不用 CPU。释放之后外壳自己还在收尾(会话列表、git 状态……),所以分两个 2 秒窗口量:
            // 前一个是「释放后立刻」,后一个是「外壳稳定之后」;判据看后一个(真正的空闲),前一个只记录
            using var self = Process.GetCurrentProcess();
            async Task<double> Cpu()
            {
                self.Refresh();
                var before = self.TotalProcessorTime;
                await Task.Delay(2000);
                self.Refresh();
                return (self.TotalProcessorTime - before).TotalMilliseconds / 2000 * 100;
            }

            var settling = await Cpu();
            await Task.Delay(1500);
            var idle = await Cpu();
            Console.WriteLine($"SMOKE note cpu right after the release (shell still settling): {settling:F1}% of one core");
            Report("window.idle-cpu", idle < 5, $"{idle:F1}% of one core over 2 s of idle once the shell has settled (a leftover animation loop would show up here; the first 2 s after the release were {settling:F1}%)");

            // 和 ViewModel 的接线:宿主能力有没有真的接上(INcwBackend.StartupHost 经默认接口成员实现)
            Report("window.host-capabilities-wired", vm.StartupHost is not null && vm.RequestExit is not null, $"StartupHost={(vm.StartupHost is null ? "null" : vm.StartupHost.GetType().Name)}, RequestExit={(vm.RequestExit is null ? "null" : "set")}");

            desktop.Shutdown(0);
        }

        /// <summary>外壳几个关键区域在窗口里的位置和尺寸(取整):淡出前后必须一样。</summary>
        private string ShellBounds()
        {
            var shell = window.GetVisualDescendants().OfType<DesktopShell>().First();
            string Box(Control c)
            {
                var p = c.TranslatePoint(default, window) ?? default;
                return $"({Math.Round(p.X)},{Math.Round(p.Y)} {Math.Round(c.Bounds.Width)}x{Math.Round(c.Bounds.Height)})";
            }

            return $"workspace={Box(shell.GetVisualDescendants().OfType<WorkspaceArea>().First())} left={Box(shell.GetVisualDescendants().OfType<Grid>().First(g => g.Name == "LeftArea"))} main={Box(shell.GetVisualDescendants().OfType<Grid>().First(g => g.Name == "Main"))}";
        }
    }
}
