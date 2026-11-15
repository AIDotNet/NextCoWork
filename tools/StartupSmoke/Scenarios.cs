using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Ncw.Agent.Storage;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.Desktop.Sidecar;

namespace StartupSmoke;

/// <summary>无界面的真实启动流程:时间线、故障注入、2.x 接管。每一项打印 <c>SMOKE 名字 PASS|FAIL 详情</c>。</summary>
internal sealed class Scenarios(string agent, string work)
{
    private int _failures;

    public int Failures => _failures;

    private static string Fmt(double? ms) => ms is { } v ? v.ToString("F0", CultureInfo.InvariantCulture) : "-";

    private void Report(string name, bool pass, string detail)
    {
        if (!pass)
        {
            _failures++;
        }

        Console.WriteLine($"SMOKE {name} {(pass ? "PASS" : "FAIL")} {detail}");
    }

    private string NewRoot(string name)
    {
        var dir = Path.Combine(work, $"{name}-{Guid.NewGuid():N}"[..Math.Min(40, name.Length + 9)]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void SetAgent(string? path) => Environment.SetEnvironmentVariable("NCW_AGENT_PATH", path);

    // ───────────────────────── 时间线 ─────────────────────────

    private static readonly StartupMilestone[] Columns =
    [
        StartupMilestone.AgentSpawned, StartupMilestone.AgentReady, StartupMilestone.Connected, StartupMilestone.DataReady,
        StartupMilestone.TopicsSynced, StartupMilestone.ProjectsLoaded, StartupMilestone.SessionsLoaded,
    ];

    /// <summary>一次完整的启动:返回每个里程碑相对起点的毫秒数,以及「全部阶段完成」(IsAwaitingShell)的时间。</summary>
    private async Task<(IReadOnlyDictionary<StartupMilestone, double> Ms, double? Ready, double? Reveal)> OneStart(string dataRoot, Func<Harness, Task>? beforeStop = null)
    {
        SetAgent(agent);
        await using var h = Harness.Start(dataRoot);
        var ready = h.WaitFor(s => s.IsAwaitingShell, TimeSpan.FromSeconds(60));
        var ms = h.Milestones();
        // 没有窗口:外壳「完成布局」由这里代报(窗口里由视图报告),然后看淡出什么时候开始——最短停留 / 延迟出现的规则照常生效
        h.Run(s => s.Startup.ReportShellLaidOut());
        var reveal = h.WaitFor(s => s.IsRevealing, TimeSpan.FromSeconds(10));
        if (beforeStop is not null)
        {
            await beforeStop(h);
        }

        return (ms, ready, reveal);
    }

    public async Task TimelineAsync(int runs, int projects)
    {
        Console.WriteLine($"SMOKE note agent={agent} runs={runs} projects={projects}");
        var cold = new List<(IReadOnlyDictionary<StartupMilestone, double> Ms, double? Ready, double? Reveal)>();
        var warm = new List<(IReadOnlyDictionary<StartupMilestone, double> Ms, double? Ready, double? Reveal)>();

        // 冷:每次都是全新的数据根(Agent 要建库、迁移 schema),并且没有 Agent 在运行
        for (var i = 0; i < runs; i++)
        {
            cold.Add(await OneStart(NewRoot("cold")));
        }

        // 热:同一个数据根反复启动(库已存在),带上项目让「内容」阶段有真实的活
        var warmRoot = NewRoot("warm");
        await OneStart(warmRoot, async h =>
        {
            for (var p = 0; p < projects; p++)
            {
                var dir = Path.Combine(warmRoot, $"proj-{p}");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "README.md"), "# project\n");
                await h.Devices.Local!.AddProjectAsync(dir);
            }
        });
        for (var i = 0; i < runs; i++)
        {
            warm.Add(await OneStart(warmRoot));
        }

        // 接上:上次「关窗后继续运行」留下的 Agent(NCW_KEEP_AGENT=1,开发用的强制开关):第二次起不用拉起进程,直接握手
        var adopted = new List<(IReadOnlyDictionary<StartupMilestone, double> Ms, double? Ready, double? Reveal)>();
        var adoptRoot = NewRoot("adopt");
        Environment.SetEnvironmentVariable("NCW_KEEP_AGENT", "1");
        try
        {
            await OneStart(adoptRoot);   // 拉起一个脱离的 Agent,桌面端退出后它继续运行
            for (var i = 0; i < runs; i++)
            {
                adopted.Add(await OneStart(adoptRoot));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("NCW_KEEP_AGENT", null);
            if (File.Exists(StateFile(adoptRoot)))
            {
                try
                {
                    using var leftover = Process.GetProcessById(ReadPid(adoptRoot));
                    leftover.Kill();   // 只杀自己留下的那一个(pid 来自这个临时数据根的 sidecar.json)
                    await leftover.WaitForExitAsync();
                }
                catch (ArgumentException)
                {
                }
            }
        }

        PrintTable("cold (fresh data root, no agent running)", cold);
        PrintTable("warm (existing data root with projects, no agent running)", warm);
        PrintTable("adopted (the agent was left running by the previous session: attach, no spawn)", adopted);
        Report("timeline.cold.completes", cold.All(r => r.Ready is not null), $"{cold.Count(r => r.Ready is not null)}/{cold.Count} runs reached all-steps-done");
        Report("timeline.warm.completes", warm.All(r => r.Ready is not null), $"{warm.Count(r => r.Ready is not null)}/{warm.Count} runs reached all-steps-done");
        Report("timeline.adopted.completes", adopted.All(r => r.Ready is not null) && adopted.All(r => !r.Ms.ContainsKey(StartupMilestone.AgentSpawned)), $"{adopted.Count(r => r.Ready is not null)}/{adopted.Count} runs reached all-steps-done, none of them spawned a process");
        Report("timeline.order", cold.Concat(warm).Where(r => r.Ready is not null).All(r => Ordered(r.Ms)), "AgentSpawned <= AgentReady <= Connected <= ProjectsLoaded in every run");
    }

    private static bool Ordered(IReadOnlyDictionary<StartupMilestone, double> m) =>
        m[StartupMilestone.AgentSpawned] <= m[StartupMilestone.AgentReady]
        && m[StartupMilestone.AgentReady] <= m[StartupMilestone.Connected]
        && m[StartupMilestone.Connected] <= m[StartupMilestone.ProjectsLoaded];

    private static void PrintTable(string title, List<(IReadOnlyDictionary<StartupMilestone, double> Ms, double? Ready, double? Reveal)> rows)
    {
        Console.WriteLine($"\nTIMELINE {title}  (ms since startup began; AllDone = every stage done, Reveal = the fade-out would start; no window here: the shell layout report is made by the harness, window / platform / account milestones are at t=0, see the window mode)");
        var header = new StringBuilder("  run ");
        foreach (var c in Columns)
        {
            header.Append($"{c.ToString(),15}");
        }

        header.Append($"{"AllDone",10}{"Reveal",10}");
        Console.WriteLine(header);
        for (var i = 0; i < rows.Count; i++)
        {
            var line = new StringBuilder($"  {i + 1,3} ");
            foreach (var c in Columns)
            {
                line.Append($"{Fmt(rows[i].Ms.TryGetValue(c, out var v) ? v : null),15}");
            }

            line.Append($"{Fmt(rows[i].Ready),10}{Fmt(rows[i].Reveal),10}");
            Console.WriteLine(line);
        }

        foreach (var (label, pick) in new (string, Func<List<double>, double>)[] { ("min", l => l.Min()), ("median", Median), ("p95", P95), ("max", l => l.Max()) })
        {
            var line = new StringBuilder($"  {label,-4}");
            foreach (var c in Columns)
            {
                var values = rows.Where(r => r.Ms.ContainsKey(c)).Select(r => r.Ms[c]).ToList();
                line.Append($"{Fmt(values.Count == 0 ? null : pick(values)),15}");
            }

            var ready = rows.Where(r => r.Ready is not null).Select(r => r.Ready!.Value).ToList();
            var reveal = rows.Where(r => r.Reveal is not null).Select(r => r.Reveal!.Value).ToList();
            line.Append($"{Fmt(ready.Count == 0 ? null : pick(ready)),10}{Fmt(reveal.Count == 0 ? null : pick(reveal)),10}");
            Console.WriteLine(line);
        }
    }

    private static double Median(List<double> l)
    {
        var s = l.Order().ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }

    private static double P95(List<double> l)
    {
        var s = l.Order().ToList();
        return s[Math.Min(s.Count - 1, (int)Math.Ceiling(s.Count * 0.95) - 1)];
    }

    // ───────────────────────── 故障注入 ─────────────────────────

    public async Task FaultsAsync(string? only = null)
    {
        async Task Run(string name, Func<Task> scenario)
        {
            if (only is null || name.Contains(only, StringComparison.OrdinalIgnoreCase))
            {
                await scenario();
            }
        }

        await Run("agent-not-found", AgentNotFound);
        await Run("launch-failed", LaunchFailed);
        await Run("crash-loop", CrashingAgent);
        await Run("client-outdated", () => VersionMismatch("client-outdated", "0.0.1", canRetry: false));
        await Run("agent-outdated", () => VersionMismatch("agent-outdated", "99.0.0", canRetry: false));
        await Run("data-root-occupied", DataRootOccupied);
        await Run("killed-before-reveal", KilledAfterAllDone);
        await Run("killed-after-interactive", KilledAfterInteractive);
    }

    private static string StateFile(string root) => new SidecarPaths(root).StateFile;

    private async Task AgentNotFound()
    {
        var root = NewRoot("f-notfound");
        SetAgent(Path.Combine(work, "does-not-exist", "ncw-agent"));
        await using var h = Harness.Start(root);
        var shown = h.WaitFor(s => s.IsFailed, TimeSpan.FromSeconds(10));
        var reason = h.Read(s => s.FailureReason);
        var spinning = h.Read(s => s.IsIndeterminate);
        Report("fault.agent-not-found.panel", shown is not null && reason == "agent-not-found", $"failure panel after {Fmt(shown)} ms reason={reason} indeterminateSpinner={spinning} (canRetry={h.Read(s => s.CanRetry)})");

        SetAgent(agent);
        h.Run(s => s.Startup.RetryCommand.Execute(null));
        var ok = h.WaitFor(s => s.IsAwaitingShell && !s.IsFailed, TimeSpan.FromSeconds(60));
        Report("fault.agent-not-found.retry-recovers", ok is not null, $"after Retry the startup reached all-steps-done at {Fmt(ok)} ms ({Fmt(ok - shown)} ms after the panel)");
    }

    private async Task LaunchFailed()
    {
        var root = NewRoot("f-launch");
        var bad = Path.Combine(work, "not-executable");
        File.WriteAllText(bad, "this is not a program");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(bad, UnixFileMode.UserRead | UnixFileMode.UserWrite);   // 没有执行权限:Process.Start 抛 Win32Exception
        }
        SetAgent(bad);
        await using var h = Harness.Start(root);
        var shown = h.WaitFor(s => s.IsFailed, TimeSpan.FromSeconds(20));
        var reason = h.Read(s => s.FailureReason);
        Report("fault.launch-failed.panel", shown is not null && reason == "launch-failed", $"failure panel after {Fmt(shown)} ms reason={reason} (sidecar status: {h.Sidecar.Status.Phase}/{h.Sidecar.Status.Issue})");

        SetAgent(agent);
        h.Run(s => s.Startup.RetryCommand.Execute(null));
        var ok = h.WaitFor(s => s.IsAwaitingShell && !s.IsFailed, TimeSpan.FromSeconds(60));
        Report("fault.launch-failed.retry-recovers", ok is not null, $"recovered at {Fmt(ok)} ms ({Fmt(ok - shown)} ms after the panel)");
    }

    private async Task CrashingAgent()
    {
        var root = NewRoot("f-crash");
        var script = Path.Combine(work, "crash-agent.sh");
        File.WriteAllText(script, "#!/bin/sh\necho 'boom' >&2\nexit 3\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        SetAgent(script);
        await using var h = Harness.Start(root);
        var shown = h.WaitFor(s => s.IsFailed, TimeSpan.FromSeconds(30));
        var reason = h.Read(s => s.FailureReason);
        var restarts = h.Sidecar.Status.RestartCount;
        Report("fault.crash-loop.panel", shown is not null && reason == "crash-loop", $"failure panel after {Fmt(shown)} ms reason={reason} (agent restarted {restarts}x; the guard itself only gives up after 8 consecutive crashes)");

        SetAgent(agent);
        h.Run(s => s.Startup.RetryCommand.Execute(null));
        var ok = h.WaitFor(s => s.IsAwaitingShell && !s.IsFailed, TimeSpan.FromSeconds(60));
        Report("fault.crash-loop.retry-recovers", ok is not null, $"recovered at {Fmt(ok)} ms ({Fmt(ok - shown)} ms after the panel)");
    }

    private async Task VersionMismatch(string expected, string desktopVersion, bool canRetry)
    {
        var root = NewRoot("f-version");
        SetAgent(agent);
        await using var h = Harness.Start(root, desktopVersion);
        var shown = h.WaitFor(s => s.IsFailed, TimeSpan.FromSeconds(30));
        var reason = h.Read(s => s.FailureReason);
        var retry = h.Read(s => s.CanRetry);
        Report($"fault.{expected}.panel", shown is not null && reason == expected && retry == canRetry, $"failure panel after {Fmt(shown)} ms reason={reason} canRetry={retry} (desktop version injected as {desktopVersion})");
    }

    /// <summary>另一个本应用无法通信的 Agent 占着数据目录:单实例锁让第二个退出码 75。</summary>
    private async Task DataRootOccupied()
    {
        var root = NewRoot("f-occupied");
        SetAgent(agent);
        using var occupier = Process.Start(new ProcessStartInfo(agent, ["--mode", "local", "--data-root", root])
        {
            Environment = { ["NCW_LOCAL_TOKEN"] = "someone-elses-token" },
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        })!;
        try
        {
            await Task.Delay(3000);   // 让它拿到锁
            await using var h = Harness.Start(root);
            var shown = h.WaitFor(s => s.IsFailed, TimeSpan.FromSeconds(30));
            var reason = h.Read(s => s.FailureReason);
            Report("fault.data-root-occupied.panel", shown is not null && reason == "already-running-elsewhere", $"failure panel after {Fmt(shown)} ms reason={reason} (sidecar status {h.Sidecar.Status.Phase}/{h.Sidecar.Status.Issue}; the guard alone would give up after 8 tries, ~45 s)");

            occupier.Kill();
            await occupier.WaitForExitAsync();
            h.Run(s => s.Startup.RetryCommand.Execute(null));
            var ok = h.WaitFor(s => s.IsAwaitingShell && !s.IsFailed, TimeSpan.FromSeconds(60));
            Report("fault.data-root-occupied.retry-recovers", ok is not null, $"after the other agent quit and Retry: all-steps-done at {Fmt(ok)} ms");
        }
        finally
        {
            if (!occupier.HasExited)
            {
                occupier.Kill();
            }
        }
    }

    private int ReadPid(string root) => System.Text.Json.JsonDocument.Parse(File.ReadAllText(StateFile(root))).RootElement.GetProperty("pid").GetInt32();

    private async Task KillAgentAsync(string root)
    {
        using var victim = Process.GetProcessById(ReadPid(root));
        victim.Kill();
        await victim.WaitForExitAsync();
    }

    /// <summary>所有阶段都完成、还没淡出时 Agent 被杀:已完成的阶段不退回,揭幕照常,之后 Agent 被守护拉起、设备重连。</summary>
    private async Task KilledAfterAllDone()
    {
        var root = NewRoot("f-kill1");
        SetAgent(agent);
        await using var h = Harness.Start(root);
        h.WaitFor(s => s.IsAwaitingShell, TimeSpan.FromSeconds(60));
        var before = h.Read(s => (s.StepsDone, s.Fraction));
        await KillAgentAsync(root);
        await Task.Delay(300);
        var during = h.Read(s => (s.StepsDone, s.Fraction, s.Phase, s.IsFailed, s.IsIndeterminate));
        h.Run(s => s.Startup.ReportShellLaidOut());
        var revealing = h.WaitFor(s => s.IsRevealing, TimeSpan.FromSeconds(5));
        h.Run(s => s.Startup.ReportRevealed());
        var done = h.Read(s => (s.Phase, s.OwnsScreen));
        var reconnect = h.WaitFor(_ => h.Devices.Local!.IsConnected, TimeSpan.FromSeconds(60));   // 条件本身就在 UI 线程上求值,里面不能再 Invoke(会死锁)
        Report("fault.killed-before-reveal", during.StepsDone == before.StepsDone && during.Fraction >= before.Fraction && !during.IsFailed && revealing is not null && done.Phase == StartupPhase.Done && !done.OwnsScreen && reconnect is not null,
            $"steps {before.StepsDone}->{during.StepsDone} fraction {before.Fraction:F2}->{during.Fraction:F2} phase={during.Phase}; reveal ran, final phase={done.Phase}; device reconnected at {Fmt(reconnect)} ms");
    }

    /// <summary>进入可交互之后 Agent 被杀:不再弹全屏加载(用已有的断线条),设备重连后恢复。</summary>
    private async Task KilledAfterInteractive()
    {
        var root = NewRoot("f-kill2");
        SetAgent(agent);
        await using var h = Harness.Start(root);
        var awaiting = h.WaitFor(s => s.IsAwaitingShell, TimeSpan.FromSeconds(60));
        h.Run(s => s.Startup.ReportShellLaidOut());
        var revealing = h.WaitFor(s => s.IsRevealing, TimeSpan.FromSeconds(5));
        h.Run(s => s.Startup.ReportRevealed());
        var beforeKill = h.Read(s => (s.Phase, s.IsRevealing));
        await KillAgentAsync(root);
        var seenBack = false;
        var wentDown = false;
        var deadline = Stopwatch.StartNew();
        // 先等设备真的断了(杀进程到连接层察觉有一小段时间),再等它重新连上;整个过程中加载层都不能回来
        while (deadline.Elapsed < TimeSpan.FromSeconds(60))
        {
            var (connected, running) = h.Ui.Invoke(() => (h.Devices.Local!.IsConnected, h.Sidecar.Status.Phase == SidecarPhase.Running));
            wentDown |= !connected;
            if (wentDown && connected && running)
            {
                break;
            }

            seenBack |= h.Read(s => s.OwnsScreen || s.IsPresented || s.IsFailed);
            await Task.Delay(20);
        }

        var phase = h.Read(s => s.Phase);
        Report("fault.killed-after-interactive", !seenBack && wentDown && phase == StartupPhase.Done && deadline.Elapsed < TimeSpan.FromSeconds(60), $"the full-screen loading layer never came back while the agent was killed and restarted (device went down={wentDown}, back up after {Fmt(deadline.Elapsed.TotalMilliseconds)} ms); phase={phase} (awaiting at {Fmt(awaiting)} ms, revealing at {Fmt(revealing)} ms, before the kill: {beforeKill})");
    }

    // ───────────────────────── 2.x 接管 ─────────────────────────

    public async Task TakeoverAsync(string? legacyDbCopy)
    {
        await BlockedThenTakenOver();
        if (legacyDbCopy is not null)
        {
            await RealTakeover(legacyDbCopy);
        }
        else
        {
            Console.WriteLine("SMOKE note takeover.real skipped: pass --legacy-db <a read-only COPY of a 2.x nextcowork.db>");
        }
    }

    /// <summary>2.x 形状的小库 + 一个指向活进程的 SingletonLock:被挡住 → 数据页接管整窗 → 放开后「重试」→ 接管 → 摘要 → 继续。</summary>
    private async Task BlockedThenTakenOver()
    {
        var root = NewRoot("t-blocked");
        using (var db = AgentDatabase.Open(root))
        {
            using var command = db.Connection.CreateCommand();
            command.CommandText = "INSERT OR REPLACE INTO kv (key, json) VALUES ('seed', '{}')";
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        var lockPath = Path.Combine(root, "SingletonLock");
        File.CreateSymbolicLink(lockPath, $"{Environment.MachineName}-{Environment.ProcessId}");   // 指向本进程:「2.x 还在运行」
        SetAgent(agent);
        await using var h = Harness.Start(root);
        var handed = h.WaitFor(s => s.Phase == StartupPhase.NeedsUser, TimeSpan.FromSeconds(60));
        var snap = h.Ui.Invoke(() => (StartupOwns: h.Shell.Startup.OwnsScreen, GatePresented: h.Shell.DataGate.IsPresented, h.Shell.DataGate.Mode, StartupPresented: h.Shell.Startup.IsPresented));
        Report("takeover.blocked.handoff", handed is not null && !snap.StartupOwns && !snap.StartupPresented && snap.GatePresented && snap.Mode == DataGateMode.Blocked,
            $"blocked at {Fmt(handed)} ms: startup layer yields (owns={snap.StartupOwns}), data page presented={snap.GatePresented} mode={snap.Mode}");

        File.Delete(lockPath);   // 用户退出了 2.x
        h.Run(s => s.DataGate.RetryCommand.Execute(null));
        var tookOver = h.WaitFor(s => s.Phase == StartupPhase.NeedsUser && h.Shell.DataGate.Mode == DataGateMode.Completed, TimeSpan.FromSeconds(60));
        var sawBusyOwned = h.Events.Any(e => e.What.StartsWith("data step", StringComparison.Ordinal));
        Report("takeover.blocked.retry-then-summary", tookOver is not null, $"after the lock was removed and the data page's Retry: takeover completed at {Fmt(tookOver)} ms; data steps seen by the startup layer: {string.Join(" | ", h.Events.Where(e => e.What.StartsWith("data step", StringComparison.Ordinal)).Select(e => $"{e.Ms:F0}ms {e.What}"))}");

        h.Run(s => s.DataGate.ContinueCommand.Execute(null));
        var settled = h.WaitFor(s => s.IsAwaitingShell, TimeSpan.FromSeconds(60));
        Report("takeover.continue-resumes-startup", settled is not null, $"after Continue the startup layer took the screen back and finished the content stage at {Fmt(settled)} ms; sawBusyOwnedByStartup={sawBusyOwned}");
    }

    private static void WriteFakeMasterKey(string root)
    {
        var path = Path.Combine(root, "credential.key");
        File.WriteAllBytes(path, System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private async Task RealTakeover(string legacyDbCopy)
    {
        var root = NewRoot("t-real");
        var sw = Stopwatch.StartNew();
        var target = Path.Combine(root, AgentDatabase.FileName);
        File.Copy(legacyDbCopy, target);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);   // 副本本身是只读的(防止误改);接管要写它的这份拷贝
        }

        Console.WriteLine($"SMOKE note copied the 2.x database copy ({new FileInfo(legacyDbCopy).Length / 1_000_000} MB) in {sw.ElapsedMilliseconds} ms");
        SetAgent(agent);
        await using var h = Harness.Start(root);

        // 第一段(真实发现):只有库、没有凭据主密钥(2.x 的数据根里本来有 credential.key;这里不拷贝真实的密钥文件)。
        // 真实的 Agent 在构造阶段抛未处理的 FileNotFoundException 退出,守护按崩溃处理——用户该看到错误面板,不是无限转圈
        var shown = h.WaitFor(v => v.IsFailed, TimeSpan.FromSeconds(60));
        var reason = h.Read(v => v.FailureReason);
        Report("fault.agent-crashes-on-startup.panel", shown is not null && reason == "crash-loop", $"legacy 2.x database without credential.key: failure panel after {Fmt(shown)} ms reason={reason} (sidecar: {h.Sidecar.Status.Phase}, restarts={h.Sidecar.Status.RestartCount}, detail={h.Sidecar.Status.Detail})");

        // 第二段:补上一份随机的主密钥(旧凭据解不开会计入「需要重新输入」),点「重试」,继续走真实接管
        WriteFakeMasterKey(root);
        var retried = h.ElapsedMs;
        h.Run(v => v.Startup.RetryCommand.Execute(null));
        var fractions = new List<double>();
        var owned = true;
        var gatePresentedDuringBusy = false;
        var deadline = Stopwatch.StartNew();
        double? completed = null;
        while (deadline.Elapsed < TimeSpan.FromMinutes(10))
        {
            var s = h.Ui.Invoke(() => (h.Shell.Startup.Fraction, h.Shell.Startup.OwnsScreen, GatePresented: h.Shell.DataGate.IsPresented, h.Shell.DataGate.Mode));
            if (fractions.Count == 0 || fractions[^1] != s.Fraction)
            {
                fractions.Add(s.Fraction);
            }

            if (s.Mode is DataGateMode.Preparing or DataGateMode.TakingOver)
            {
                owned &= s.OwnsScreen;
                gatePresentedDuringBusy |= s.GatePresented;
            }

            if (s.Mode == DataGateMode.Completed)
            {
                completed = h.ElapsedMs;
                break;
            }

            if (s.Mode is DataGateMode.Blocked or DataGateMode.Failed)
            {
                Report("takeover.real.completes", false, $"data page is {s.Mode} (reason {h.Ui.Invoke(() => h.Shell.DataGate.Reason)})");
                return;
            }

            if (h.Read(v => v.IsFailed))
            {
                Report("takeover.real.completes", false, $"startup failure panel: {h.Read(v => v.FailureReason)}\n{h.Ui.Invoke(() => h.Shell.Startup.ToggleDiagnosticsCommand.CanExecute(null) ? h.Shell.Startup.DiagnosticsText : "")}");
                return;
            }

            await Task.Delay(25);
        }

        var steps = h.Events.Where(e => e.What.StartsWith("data step", StringComparison.Ordinal)).ToList();
        Console.WriteLine($"  (Retry was pressed at {retried:F0} ms; step times below are ms since the harness started)");
        foreach (var e in steps)
        {
            Console.WriteLine($"  {e.Ms,8:F0} ms  {e.What}");
        }

        var monotonic = fractions.Zip(fractions.Skip(1), (a, b) => b >= a).All(x => x);
        var distinct = steps.Select(e => e.What.Split(' ')[2]).Distinct().Where(x => x != "(none)").ToList();
        Report("takeover.real.completes", completed is not null, $"2.x takeover completed at {Fmt(completed)} ms; summary={h.Ui.Invoke(() => h.Shell.DataGate.Summary)}");
        Report("takeover.real.steps-are-real", distinct.Contains("backing-up") && distinct.Contains("migrating") && distinct.Contains("committing"), $"the startup layer displayed the agent's real steps: {string.Join(" -> ", distinct)}");
        Report("takeover.real.progress-monotonic", monotonic, $"{fractions.Count} distinct progress values, never decreasing: {string.Join(", ", fractions.Select(f => f.ToString("F3", CultureInfo.InvariantCulture)))}");
        Report("takeover.real.single-fullscreen-layer", owned && !gatePresentedDuringBusy, $"during preparing / taking over the startup layer owned the screen={owned} and the data page was never presented at the same time={!gatePresentedDuringBusy}");
    }
}
