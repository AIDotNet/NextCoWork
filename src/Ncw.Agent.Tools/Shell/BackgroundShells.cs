using System.Text.RegularExpressions;
using Ncw.Agent.Environments;
using Ncw.Agent.Kernel;

namespace Ncw.Agent.Tools;

/// <summary>
/// Agent 开着的 shell —— 前台那条能被单独掐掉 / 转去后台、后台那些能被回读(移植自 2.x <c>agent-shells.ts</c>)。两件事一张表,
/// 因为它们回答的是同一个问题:「这一刻 agent 手上还开着哪些 shell」。拆成两个注册表之后,「全部停掉」(退出、删会话)就得记得调两次,
/// 而漏掉的那一次留下的是用户看不见、也杀不掉的进程。
/// <para>
/// 进程由项目的执行环境起(本机和 SSH 走同一个 <c>OpenProcessAsync</c>),注册表只收编已经起好的进程。后台 shell <b>不随 Run 结束而结束</b>,
/// 这正是它的用途;终点只有三个:自己退出、<c>KillShell</c>、<see cref="ShutdownAsync"/>。所以数量必须有上限(<see cref="ShellLimits.MaxRunning"/>)。
/// </para>
/// </summary>
public sealed class BackgroundShells(TimeProvider? clock = null) : IShellRegistry, IAsyncDisposable
{
    private sealed class Entry(BackgroundShellInfo info, IEnvProcess process, ShellCapture capture)
    {
        public BackgroundShellInfo Info { get; set; } = info;

        public IEnvProcess Process { get; } = process;

        public ShellCapture Capture { get; } = capture;
    }

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (ForegroundCall Call, Action Stop, Action? Detach)> _foreground = new(StringComparer.Ordinal);

    // 只增不复用,哪怕那条 shell 已经被淘汰出表
    private int _sequence;

    /// <summary>
    /// 后台列表变了(新开、退出、被杀、被淘汰)。只说「变了」不带增量:监听方每次发全量,丢过一次通知也能自己收敛。
    /// 输出增长<b>不</b>算变化:一个刷日志的 dev server 每秒几十次,全量广播会被它淹没。
    /// </summary>
    public event Action? Changed;

    public int RunningCount
    {
        get
        {
            lock (_gate)
            {
                return _entries.Values.Count(e => e.Info.Status == BackgroundShellStatus.Running);
            }
        }
    }

    // ─────────────────────────── 前台 ───────────────────────────

    public IDisposable Hold(ForegroundCall call, Action stop, Action? detach)
    {
        var key = Key(call.RunId, call.CallId);
        lock (_gate)
        {
            _foreground[key] = (call, stop, detach);
        }

        return new Release(() =>
        {
            lock (_gate)
            {
                _foreground.Remove(key);
            }
        });
    }

    /// <summary>用户点了那张卡片上的停止:只停那一条。返回「真的停了一条」(false = 它已经不在跑了)。</summary>
    public bool StopCall(RunId run, string callId)
    {
        Action? stop;
        lock (_gate)
        {
            stop = _foreground.TryGetValue(Key(run, callId), out var held) ? held.Stop : null;
        }

        stop?.Invoke();
        return stop is not null;
    }

    /// <summary>
    /// 把一条前台命令转去后台。返回「请求已发出」。后台名额满了就<b>当场拒绝</b>,而不是先转走再在收编时失败:
    /// 那时进程已经脱离了前台,收编失败只能杀掉它 —— 用户想要的是「别等了,让它跑着」,拿到的却是「命令没了」。拒绝时前台照旧在等,什么都没坏。
    /// </summary>
    public bool DetachCall(RunId run, string callId)
    {
        Action? detach;
        lock (_gate)
        {
            detach = _foreground.TryGetValue(Key(run, callId), out var held) ? held.Detach : null;
            if (detach is not null && _entries.Values.Count(e => e.Info.Status == BackgroundShellStatus.Running) >= ShellLimits.MaxRunning)
            {
                return false;
            }
        }

        detach?.Invoke();
        return detach is not null;
    }

    // ─────────────────────────── 后台 ───────────────────────────

    public async ValueTask<BackgroundShellInfo> StartAsync(ShellStartRequest request, IExecutionEnvironment environment, CancellationToken ct)
    {
        EnsureRoom();
        var process = await environment.OpenProcessAsync(new CommandSpec { ShellCommand = request.Command, WorkingDirectory = request.Cwd }, ct).ConfigureAwait(false);
        var capture = new ShellCapture(process);
        // 订阅输出要在起进程之后的同一个同步块里做完:晚一个 tick 就会丢掉它启动时打的头几行,而那几行恰恰是「端口被占用」之类最该被看到的话
        capture.Start();
        try
        {
            return Register(environment.Id, process, capture, request);
        }
        catch
        {
            await process.KillAsync(CancellationToken.None).ConfigureAwait(false);
            await process.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public BackgroundShellInfo Adopt(DetachedShell shell, ShellStartRequest request)
    {
        EnsureRoom();
        return Register(shell.Environment.Id, shell.Process, shell.Capture, request);
    }

    public ShellRead Read(string id, Regex? filter)
    {
        Entry entry;
        lock (_gate)
        {
            entry = Find(id);
        }

        var take = entry.Capture.Take(filter is null ? null : line => filter.IsMatch(line));
        lock (_gate)
        {
            return new ShellRead(entry.Info, take.Stdout, take.Stderr, take.Dropped);
        }
    }

    public async ValueTask<BackgroundShellInfo> KillAsync(string id, CancellationToken ct)
    {
        Entry entry;
        var kill = false;
        lock (_gate)
        {
            entry = Find(id);
            if (entry.Info.Status == BackgroundShellStatus.Running)
            {
                // 先标状态再杀:退出回调可能在同一个 tick 里就到,那时它必须看得出这是一次主动终止,而不是进程自己退的(两者对模型意味着不同的事)
                entry.Info = entry.Info with { Status = BackgroundShellStatus.Killed, EndedAt = _clock.GetUtcNow() };
                kill = true;
            }
        }

        if (kill)
        {
            Changed?.Invoke();
            await entry.Process.KillAsync(ct).ConfigureAwait(false);
        }

        lock (_gate)
        {
            return entry.Info;
        }
    }

    public IReadOnlyList<BackgroundShellInfo> List()
    {
        lock (_gate)
        {
            return _entries.Values.Select(e => e.Info).ToList();
        }
    }

    /// <summary>界面看一眼输出尾巴。<b>不消费</b>模型那份缓冲。找不到时返回 null 而不是抛:界面上那条可能刚好被淘汰,这不是错误。</summary>
    public (BackgroundShellInfo Info, string Output)? Peek(string id)
    {
        Entry? entry;
        lock (_gate)
        {
            entry = _entries.GetValueOrDefault(id);
        }

        if (entry is null)
        {
            return null;
        }

        var output = entry.Capture.Peek();
        lock (_gate)
        {
            return (entry.Info, output);
        }
    }

    /// <summary>应用退出 / 会话删除 / 测试收尾:杀光所有后台进程。不杀的话它们会活过应用本身:重启后端口还占着,而界面上再也找不到是谁占的。</summary>
    public async ValueTask ShutdownAsync()
    {
        List<Entry> running;
        lock (_gate)
        {
            running = _entries.Values.Where(e => e.Info.Status == BackgroundShellStatus.Running).ToList();
            foreach (var entry in running)
            {
                entry.Info = entry.Info with { Status = BackgroundShellStatus.Killed, EndedAt = _clock.GetUtcNow() };
            }

            _entries.Clear();
            _foreground.Clear();
        }

        foreach (var entry in running)
        {
            await entry.Process.KillAsync(CancellationToken.None).ConfigureAwait(false);
        }

        Changed?.Invoke();
    }

    public ValueTask DisposeAsync() => ShutdownAsync();

    private BackgroundShellInfo Register(EnvironmentId environment, IEnvProcess process, ShellCapture capture, ShellStartRequest request)
    {
        Entry entry;
        lock (_gate)
        {
            var id = $"bash_{Interlocked.Increment(ref _sequence)}";
            var info = new BackgroundShellInfo(id, request.Command, request.Description, request.Cwd, BackgroundShellStatus.Running, null, _clock.GetUtcNow(), null, request.RunId, request.CallId, environment);
            entry = new Entry(info, process, capture);
            _entries[id] = entry;
            Evict();
        }

        _ = WatchAsync(entry);
        Changed?.Invoke();
        lock (_gate)
        {
            return entry.Info;
        }
    }

    private async Task WatchAsync(Entry entry)
    {
        ProcessExit exit;
        try
        {
            exit = await entry.Process.Exited.ConfigureAwait(false);
        }
        catch (Exception)
        {
            exit = new ProcessExit(null, null, ProcessExitReason.ConnectionLost);
        }

        // 后台子进程可能还攥着管道:给读取一个宽限期,别让收尾一直挂着
        await entry.Capture.DrainAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        lock (_gate)
        {
            // 已经是 killed 的就不要改回 exited:「是我们杀的」这件事退出码里没有
            entry.Info = entry.Info.Status == BackgroundShellStatus.Running
                ? entry.Info with { Status = BackgroundShellStatus.Exited, ExitCode = exit.ExitCode, EndedAt = _clock.GetUtcNow() }
                : entry.Info with { EndedAt = entry.Info.EndedAt ?? _clock.GetUtcNow() };
            Evict();
        }

        try
        {
            await entry.Process.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 收尾失败不是调用方要处理的错误
        }

        Changed?.Invoke();
    }

    private void EnsureRoom()
    {
        if (RunningCount >= ShellLimits.MaxRunning)
        {
            throw new ShellRegistryException(
                $"Too many background shells are already running ({ShellLimits.MaxRunning}). Use KillShell to stop one you no longer need, or run this command in the foreground.");
        }
    }

    private Entry Find(string id)
    {
        if (_entries.TryGetValue(id, out var entry))
        {
            return entry;
        }

        // 必须把现在有哪些列出来:只说「没有这个 shell」的话,模型会原样重试一次(它认为是自己手抖),而真正的原因通常是那条已经跑完并被淘汰了
        var current = _entries.Values.Select(e => $"{e.Info.Id} ({e.Info.Status.ToString().ToLowerInvariant()})").ToList();
        var listed = current.Count == 0 ? "There are no background shells right now." : $"Currently tracked: {string.Join(", ", current)}.";
        throw new ShellRegistryException($"There is no background shell with id \"{id}\". {listed} Do not retry with the same id.");
    }

    /// <summary>只淘汰<b>已结束</b>的,从最早结束的那条开始。还在跑的一律不动。</summary>
    private void Evict()
    {
        if (_entries.Count <= ShellLimits.MaxTracked)
        {
            return;
        }

        foreach (var entry in _entries.Values
            .Where(e => e.Info.Status != BackgroundShellStatus.Running)
            .OrderBy(e => e.Info.EndedAt ?? DateTimeOffset.MinValue)
            .ToList())
        {
            if (_entries.Count <= ShellLimits.MaxTracked)
            {
                return;
            }

            _entries.Remove(entry.Info.Id);
        }
    }

    private static string Key(RunId run, string callId) => $"{run.Value}\u0000{callId}";

    private sealed class Release(Action action) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                action();
            }
        }
    }
}
