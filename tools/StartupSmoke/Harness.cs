using System.Collections.Concurrent;
using System.Diagnostics;
using Ncw.Client.Devices;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.Desktop;
using Ncw.Desktop.Sidecar;
using Ncw.UI;

namespace StartupSmoke;

/// <summary>单线程的「UI 线程」:Store 只在它上面读写(和生产里的 UI 线程同一个约束)。</summary>
internal sealed class UiThread : IUiDispatcher, IDisposable
{
    private readonly BlockingCollection<Action> _queue = [];

    public UiThread()
    {
        new Thread(Loop) { IsBackground = true, Name = "smoke-ui" }.Start();
    }

    private void Loop()
    {
        foreach (var action in _queue.GetConsumingEnumerable())
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[ui] {e}");
            }
        }
    }

    public void Post(Action action)
    {
        if (!_queue.IsAddingCompleted)
        {
            _queue.Add(action);
        }
    }

    public T Invoke<T>(Func<T> func)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                done.SetResult(func());
            }
            catch (Exception e)
            {
                done.SetException(e);
            }
        });
        return done.Task.GetAwaiter().GetResult();
    }

    public void Invoke(Action action) => Invoke(() =>
    {
        action();
        return 0;
    });

    public void Dispose() => _queue.CompleteAdding();
}

/// <summary>桌面启动流程的无界面版本:真实的守护 + 连接层 + Store + <see cref="StartupViewModel"/>,没有窗口。</summary>
internal sealed class Harness : IAsyncDisposable
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<(double Ms, string What)> _events = [];

    private Harness()
    {
    }

    public UiThread Ui { get; } = new();

    public ClientStores Stores { get; private set; } = null!;

    public DeviceRegistry Devices { get; private set; } = null!;

    public ShellViewModel Shell { get; private set; } = null!;

    public LocalBackend Backend { get; private set; } = null!;

    public LocalAgentSidecar Sidecar { get; private set; } = null!;

    public DateTimeOffset T0 { get; private set; }

    public double ElapsedMs => _clock.Elapsed.TotalMilliseconds;

    public IReadOnlyList<(double Ms, string What)> Events => _events;

    /// <summary>
    /// 开始一次启动。窗口相关的里程碑(窗口 / 平台 / 账户)这里没有窗口,记在起点(真实的值见 window 模式);
    /// <paramref name="desktopVersion"/> 用来注入版本不匹配。
    /// </summary>
    public static Harness Start(string dataRoot, string? desktopVersion = null, SidecarTimings? timings = null)
    {
        var h = new Harness();
        h.Ui.Invoke(() =>
        {
            h.Stores = new ClientStores();
            h.Devices = new DeviceRegistry();
            h.Stores.Startup.Begin(StartupProfile.Desktop);
            h.T0 = h.Stores.Startup.State.Facts!.StartedAt;
            foreach (var m in new[] { StartupMilestone.InstanceReady, StartupMilestone.WindowShown, StartupMilestone.PlatformReady, StartupMilestone.AccountDecided })
            {
                h.Stores.Startup.Reach(m);
            }

            var deps = SidecarDependencies.Production(dataRoot, NcwApp.ClientDirectory, typeof(LocalAgentSidecar).Assembly);
            if (desktopVersion is not null)
            {
                deps = deps with { DesktopVersion = desktopVersion };
            }

            if (timings is not null)
            {
                deps = deps with { Timings = timings };
            }

            h.Sidecar = new LocalAgentSidecar(dataRoot, deps);
            h.Backend = new LocalBackend(h.Sidecar);
            h.Shell = new ShellViewModel(h.Stores, devices: h.Devices) { StartupHost = h.Backend.StartupHost };
            h.Shell.Startup.PropertyChanged += (_, e) => h.OnStartupChanged(e.PropertyName);
            // 视图的一次性 UI 定时器:ViewModel 给出下一次需要重算的时刻,到点调 Tick(这里没有视图,所以由 Harness 代做,行为和 StartupOverlay 一致)
            h.Shell.Startup.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(StartupViewModel.NextWake) && h.Shell.Startup.UntilWake is { } until)
                {
                    _ = Task.Delay(until).ContinueWith(_ => h.Ui.Post(() => h.Shell.Startup.Tick()), TaskScheduler.Default);
                }
            };
            Arm();   // 构造时给出的第一个 NextWake(延迟出现)在订阅之前就发出了,和 StartupOverlay.Attach 一样先武装一次
            h.Backend.Start(h.Stores, h.Devices, h.Ui, DefaultClientText.Instance);

            void Arm()
            {
                if (h.Shell.Startup.UntilWake is { } until)
                {
                    _ = Task.Delay(until).ContinueWith(_ => h.Ui.Post(() => h.Shell.Startup.Tick()), TaskScheduler.Default);
                }
            }
        });
        return h;
    }

    private string? _lastDetail;
    private string? _lastStep;
    private bool _lastFailed;

    private void OnStartupChanged(string? name)
    {
        var s = Shell.Startup;
        void Note(string what) => _events.Add((ElapsedMs, what));
        switch (name)
        {
            case nameof(StartupViewModel.IsFailed) when s.IsFailed != _lastFailed:
                _lastFailed = s.IsFailed;
                Note(s.IsFailed ? $"failure shown: {s.FailureReason}" : "failure cleared");
                break;
            case nameof(StartupViewModel.Detail) when $"{s.Detail}" != _lastDetail:
                _lastDetail = $"{s.Detail}";
                Note($"detail: {s.Detail}");
                break;
            case nameof(StartupViewModel.DataStep) when s.DataStep != _lastStep:
                _lastStep = s.DataStep;
                Note($"data step: {s.DataStep ?? "(none)"} fraction={s.Fraction:F3}");
                break;
        }
    }

    public T Read<T>(Func<StartupViewModel, T> read) => Ui.Invoke(() => read(Shell.Startup));

    /// <summary>每 5 ms 在 UI 线程上检查一次条件,直到成立(返回已用毫秒)或超时(返回 null)。</summary>
    public double? WaitFor(Func<StartupViewModel, bool> condition, TimeSpan timeout)
    {
        var deadline = _clock.Elapsed + timeout;
        while (_clock.Elapsed < deadline)
        {
            if (Ui.Invoke(() => condition(Shell.Startup)))
            {
                return ElapsedMs;
            }

            Thread.Sleep(5);
        }

        return null;
    }

    public void Run(Action<ShellViewModel> action) => Ui.Invoke(() => action(Shell));

    public StartupFacts Facts() => Ui.Invoke(() => Shell.Stores.Startup.State.Facts!);

    /// <summary>每个里程碑相对起点(<see cref="T0"/>)的毫秒数。</summary>
    public IReadOnlyDictionary<StartupMilestone, double> Milestones() =>
        Facts().Reached.ToDictionary(kv => kv.Key, kv => (kv.Value - T0).TotalMilliseconds);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Backend.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException)
        {
            Console.WriteLine("SMOKE note backend shutdown timed out");
        }

        Ui.Dispose();
    }
}
