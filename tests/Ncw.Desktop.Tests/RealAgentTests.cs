using Ncw.Desktop.Sidecar;

namespace Ncw.Desktop.Tests;

/// <summary>
/// 真实冒烟:真的 ncw-agent 进程 + 真的本机通道握手。用 <c>NCW_AGENT_PATH</c>(例如 AOT 发布产物);没有就找本仓库刚构建出来的
/// <c>src/Ncw.Agent/bin/*/net10.0/ncw-agent</c>。都没有、或设置了 <c>NCW_SKIP_AGENT_SMOKE=1</c> 时跳过(并说明原因)。
/// </summary>
public class RealAgentTests
{
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "NextCoWork.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("repo root not found");
    }

    internal static string FindAgent()
    {
        if (Environment.GetEnvironmentVariable("NCW_SKIP_AGENT_SMOKE") == "1")
        {
            Assert.Skip("NCW_SKIP_AGENT_SMOKE=1");
        }

        if (Environment.GetEnvironmentVariable("NCW_AGENT_PATH") is { Length: > 0 } custom && File.Exists(custom))
        {
            return custom;
        }

        var name = AgentLocator.ExecutableName(OperatingSystem.IsWindows());
        var bin = Path.Combine(RepoRoot(), "src", "Ncw.Agent", "bin");
        var found = Directory.Exists(bin)
            ? Directory.EnumerateFiles(bin, name, SearchOption.AllDirectories)
                // 只要「能直接跑」的产物:publish 目录是发布产物,bin/<配置>/<rid>/native/ 是 NativeAOT 的中间产物(旁边没有 e_sqlite3,启动就 DllNotFoundException)。
                // 之前这里什么都没排除:本机做完一次 AOT 发布,中间产物成了「最新」的,真实 Agent 的用例就全部起不来。
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}native{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    && !f.Contains($"{Path.DirectorySeparatorChar}publish{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;
        if (found is null)
        {
            Assert.Skip("no ncw-agent binary: build src/Ncw.Agent first (dotnet build NextCoWork.slnx) or set NCW_AGENT_PATH");
        }

        return found;
    }

    internal static (LocalAgentSidecar Sidecar, FakePolicy Policy, string Version) Real(string dataRoot, string agent, FakePolicy? policy = null)
    {
        policy ??= new FakePolicy();
        var version = SidecarVersionPolicy.OfAssembly(typeof(LocalAgentSidecar).Assembly);
        var sidecar = new LocalAgentSidecar(dataRoot, new SidecarDependencies(
            new SystemProcessLauncher(),
            new LocalChannelHandshake("desktop-test", TimeSpan.FromSeconds(5)),
            policy,
            TimeProvider.System,
            new FileSidecarLog(new SidecarPaths(dataRoot).LogFile),
            () => agent,
            new SidecarTimings { ProbeInterval = TimeSpan.FromMilliseconds(100), ReadyTimeout = TimeSpan.FromSeconds(60), GracefulStop = TimeSpan.FromSeconds(10) },
            version,
            Environment.ProcessId));
        return (sidecar, policy, version);
    }

    private static bool Alive(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [Fact]
    public async Task Starts_the_real_agent_survives_a_kill_with_a_restart_and_shuts_it_down_gracefully()
    {
        var agent = FindAgent();
        using var dir = new TempDir();
        var (sidecar, _, version) = Real(dir.Path, agent);
        var state = new SidecarStateFile(new SidecarPaths(dir.Path).StateFile);

        sidecar.Start();
        await sidecar.WaitReadyAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(SidecarPhase.Running, sidecar.Status.Phase);
        Assert.Equal(version, sidecar.Status.AgentVersion);
        var first = state.Read()!.Pid;
        Assert.True(Alive(first));

        // 同一个数据根再起一个 Agent:单实例锁拒绝(退出码 75)
        using (var second = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(agent, ["--mode", "local", "--data-root", dir.Path])
        {
            Environment = { ["NCW_LOCAL_TOKEN"] = "x" },
            RedirectStandardError = true,
        })!)
        {
            await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(LocalAgentSidecar.AlreadyRunningExitCode, second.ExitCode);
            Assert.True(Alive(first)); // 第一个不受影响
        }

        // 崩溃:杀掉它,守护应该退避 1 秒后重启并重新握手
        using (var victim = System.Diagnostics.Process.GetProcessById(first))
        {
            victim.Kill();
            await victim.WaitForExitAsync();
        }

        await Rig.Until(() => state.Read() is { } s && s.Pid != first && sidecar.Status is { Phase: SidecarPhase.Running, RestartCount: 1 }, "restart after kill", 60000);
        var second2 = state.Read()!.Pid;

        await sidecar.DisposeAsync();
        await Rig.Until(() => !Alive(second2), "agent exit after dispose", 15000);
        Assert.Null(state.Read());
        Assert.Contains("ncw-agent ready", File.ReadAllText(new SidecarPaths(dir.Path).LogFile)); // stdout 进了日志
    }

    [Fact]
    public async Task Keep_running_leaves_the_real_agent_and_the_next_session_adopts_it_then_stops_it_when_keep_is_off()
    {
        var agent = FindAgent();
        using var dir = new TempDir();
        var state = new SidecarStateFile(new SidecarPaths(dir.Path).StateFile);

        var (first, policy, _) = Real(dir.Path, agent, new FakePolicy { KeepAgentRunningAfterExit = true });
        first.Start();
        await first.WaitReadyAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));
        var pid = state.Read()!.Pid;
        await first.DisposeAsync(); // 关窗:留下
        Assert.True(Alive(pid));

        var (second, _, _) = Real(dir.Path, agent, policy);
        Assert.Equal(first.Token, second.Token);
        second.Start();
        await second.WaitReadyAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(pid, state.Read()!.Pid); // 接上了,没有另起一个
        Assert.Equal(0, second.Status.RestartCount);

        policy.KeepAgentRunningAfterExit = false; // 用户关掉开关
        await second.DisposeAsync();
        await Rig.Until(() => !Alive(pid), "detached agent stopped", 15000);
        Assert.Null(state.Read());
    }
}
