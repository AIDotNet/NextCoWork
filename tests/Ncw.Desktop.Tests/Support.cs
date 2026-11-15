using Ncw.Desktop;
using Ncw.Desktop.Sidecar;
using Ncw.Protocol.Wire;

namespace Ncw.Desktop.Tests;

/// <summary>手动推进的时钟:<see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> 与 <c>WaitAsync(timeout, clock)</c> 都走它。</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _now.Ticks;
        }
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public int PendingTimers
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count(t => t.Due is not null);
            }
        }
    }

    /// <summary>向前推进,按时间顺序触发到期的计时器。</summary>
    public void Advance(TimeSpan amount)
    {
        DateTimeOffset target;
        lock (_gate)
        {
            target = _now + amount;
        }

        while (true)
        {
            ManualTimer? next;
            lock (_gate)
            {
                next = _timers.Where(t => t.Due is not null && t.Due <= target).OrderBy(t => t.Due).FirstOrDefault();
                if (next is null)
                {
                    _now = target;
                    return;
                }

                if (next.Due > _now)
                {
                    _now = next.Due!.Value;
                }

                next.Fire();
            }

            next.Invoke();
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period;

        public DateTimeOffset? Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                _period = period;
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                if (!owner._timers.Contains(this))
                {
                    owner._timers.Add(this);
                }
            }

            return true;
        }

        // 在锁里调用:更新下一次到期
        public void Fire() => Due = _period > TimeSpan.Zero && _period != Timeout.InfiniteTimeSpan ? Due + _period : null;

        // 在锁外调用回调
        public void Invoke() => callback(state);

        public void Dispose()
        {
            lock (owner._gate)
            {
                Due = null;
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class FakeProcess : IAgentProcess
{
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeProcess(int id) => Id = id;

    public int Id { get; }

    public bool HasExited => _exit.Task.IsCompleted;

    public int? ExitCode => _exit.Task.IsCompletedSuccessfully ? _exit.Task.Result : null;

    /// <summary>Agent 是否在监听(握手能成功)。</summary>
    public bool Listening { get; set; } = true;

    public string Version { get; set; } = "";

    public int StdinClosed { get; private set; }

    public int Terminated { get; private set; }

    public int Killed { get; private set; }

    /// <summary>关 stdin 就退出(真实的 Agent 的行为)。</summary>
    public bool ExitOnStdinClose { get; set; } = true;

    public bool ExitOnTerminate { get; set; } = true;

    public bool SupportsTerminate { get; set; } = true;

    public Task<int> WaitForExitAsync(CancellationToken ct) => _exit.Task.WaitAsync(ct);

    public void Exit(int code)
    {
        Listening = false;
        _exit.TrySetResult(code);
    }

    public void CloseStdin()
    {
        StdinClosed++;
        if (ExitOnStdinClose)
        {
            Exit(0);
        }
    }

    public bool RequestTerminate()
    {
        Terminated++;
        if (SupportsTerminate && ExitOnTerminate)
        {
            Exit(0);
        }

        return SupportsTerminate;
    }

    public void Kill()
    {
        Killed++;
        Exit(137);
    }

    public void Dispose()
    {
    }
}

internal sealed class FakeLauncher : IProcessLauncher
{
    private int _nextPid = 1000;

    public List<(AgentLaunchSpec Spec, FakeProcess Process)> Starts { get; } = [];

    /// <summary>pid → 已经在运行的进程(Attach 能接上的)。</summary>
    public Dictionary<int, FakeProcess> Running { get; } = [];

    public Func<AgentLaunchSpec, Exception?>? FailWith { get; set; }

    public Action<FakeProcess>? OnStart { get; set; }

    public string AgentVersion { get; set; } = "3.0.0";

    public FakeProcess Last => Starts[^1].Process;

    public IAgentProcess Start(AgentLaunchSpec spec, Action<string> onOutput)
    {
        if (FailWith?.Invoke(spec) is { } failure)
        {
            throw failure;
        }

        var process = new FakeProcess(_nextPid++) { Version = AgentVersion };
        Starts.Add((spec, process));
        Running[process.Id] = process;
        OnStart?.Invoke(process);
        onOutput("ncw-agent ready dev_fake");
        return process;
    }

    public IAgentProcess? Attach(int pid) => Running.TryGetValue(pid, out var p) && !p.HasExited ? p : null;
}

/// <summary>握手:连到「在监听」的假进程就成功,返回它的版本;没有可连的就抛 IO 异常(和连不上的 socket 一样)。</summary>
internal sealed class FakeHandshake(FakeLauncher launcher) : IAgentHandshake
{
    public int Probes { get; private set; }

    public Func<FakeProcess, Exception?>? Reject { get; set; }

    public Task<AgentHello> ProbeAsync(string endpoint, string token, string clientVersion, CancellationToken ct)
    {
        Probes++;
        var target = launcher.Running.Values.LastOrDefault(p => p.Listening && !p.HasExited);
        if (target is null)
        {
            return Task.FromException<AgentHello>(new IOException("connection refused"));
        }

        return Reject?.Invoke(target) is { } error
            ? Task.FromException<AgentHello>(error)
            : Task.FromResult(new AgentHello("dev_fake", target.Version, 1));
    }
}

internal sealed class FakePolicy : ISidecarPolicy
{
    public bool KeepAgentRunningAfterExit { get; set; }
}

internal sealed class MemoryLog : ISidecarLog
{
    public List<string> Lines { get; } = [];

    public void Write(string line)
    {
        lock (Lines)
        {
            Lines.Add(line);
        }
    }
}

internal sealed class TempDir : IDisposable
{
    public TempDir() => Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ncw-dt-" + Guid.NewGuid().ToString("N")[..8]);

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>装配好的守护 + 全部假依赖。</summary>
internal sealed class Rig : IAsyncDisposable
{
    public const string DesktopVersion = "3.0.0";

    public readonly TempDir Dir = new();
    public readonly ManualTimeProvider Clock = new();
    public readonly FakeLauncher Launcher = new();
    public readonly FakeHandshake Handshake;
    public readonly FakePolicy Policy = new();
    public readonly MemoryLog Log = new();
    public string? AgentPath = "/fake/ncw-agent";
    public readonly List<SidecarStatus> Statuses = [];
    public readonly SidecarTimings Timings;
    public readonly LocalAgentSidecar Sidecar;
    public readonly SidecarPaths Paths;

    public Rig(SidecarTimings? timings = null, bool keep = false)
    {
        Paths = new SidecarPaths(Dir.Path);
        Launcher.AgentVersion = DesktopVersion;
        Handshake = new FakeHandshake(Launcher);
        Policy.KeepAgentRunningAfterExit = keep;
        Timings = timings ?? new SidecarTimings();
        Sidecar = Make();
    }

    public LocalAgentSidecar Make()
    {
        var sidecar = new LocalAgentSidecar(Dir.Path, new SidecarDependencies(Launcher, Handshake, Policy, Clock, Log, () => AgentPath, Timings, DesktopVersion, 4242));
        sidecar.StatusChanged += s =>
        {
            lock (Statuses)
            {
                Statuses.Add(s);
            }
        };
        return sidecar;
    }

    public SidecarStateFile StateFile => new(Paths.StateFile);

    /// <summary>等条件成立;期间不推进假时钟。</summary>
    public static async Task Until(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var end = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > end)
            {
                throw new TimeoutException("timed out waiting for " + what);
            }

            await Task.Delay(2);
        }
    }

    public Task UntilPhase(SidecarPhase phase, LocalAgentSidecar? s = null) =>
        Until(() => (s ?? Sidecar).Status.Phase == phase, $"phase {phase} (now {(s ?? Sidecar).Status})");

    /// <summary>推进假时钟直到条件成立(每步 1 秒,让守护线程有机会消费)。</summary>
    public async Task AdvanceUntil(Func<bool> condition, string what, TimeSpan? step = null, int maxSteps = 200)
    {
        for (var i = 0; i < maxSteps; i++)
        {
            if (condition())
            {
                return;
            }

            Clock.Advance(step ?? TimeSpan.FromSeconds(1));
            await Task.Delay(3);
        }

        if (!condition())
        {
            throw new TimeoutException("timed out advancing for " + what);
        }
    }

    /// <summary>Dispose 里有带时限的等待:推进假时钟帮它走完。</summary>
    public async Task DisposeSidecarAsync(LocalAgentSidecar? s = null)
    {
        var task = (s ?? Sidecar).DisposeAsync().AsTask();
        for (var i = 0; i < 100 && !task.IsCompleted; i++)
        {
            Clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(3);
        }

        await task;
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeSidecarAsync();
        Dir.Dispose();
    }
}
