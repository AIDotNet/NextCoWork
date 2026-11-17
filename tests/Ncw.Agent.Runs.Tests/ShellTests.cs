using Ncw.Agent.Environments;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Runs;
using Ncw.Agent.Runs.Tests.Support;
using Ncw.Agent.Tools;

namespace Ncw.Agent.Runs.Tests;

/// <summary>后台 Shell 与 Run 的关系(05 §8,2.x <c>agent-shells.ts</c> + <c>shutdownRuns</c>):Run 结束 / 中止不杀后台 Shell;应用退出和会话删除才清理。用真进程(本机执行环境)。</summary>
public sealed class ShellTests
{
    private static string Sleeper => OperatingSystem.IsWindows() ? "ping -n 60 127.0.0.1 > nul" : "sleep 60";

    private static async Task<BackgroundShellInfo> StartShell(Rig rig, string runId, string callId = "c1") =>
        await rig.Shells!.StartAsync(new ShellStartRequest(Sleeper, LocalEnvironment.PathOf(rig.WorkRoot), null, new RunId(runId), callId), rig.Env, CancellationToken.None);

    [Fact]
    public async Task A_background_shell_outlives_the_run_that_started_it_both_when_it_ends_and_when_it_is_stopped()
    {
        using var rig = new Rig(o =>
        {
            o.Shells = true;
            o.Driver = r => new ScriptedDriver(async (handle, request, sink, ct) =>
            {
                await sink.EmitAsync(new RunStartEvent(handle.RunId, handle.SessionId, request.Model), CancellationToken.None);
                if (handle.RunId.Value == "stopped")
                {
                    await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                }

                await sink.EmitAsync(new RunEndEvent(RunStatus.Done), CancellationToken.None);
                return ScriptedDriver.Done(handle);
            });
        });
        rig.NewSessionRow("s1");
        rig.NewSessionRow("s2");
        try
        {
            var shellA = await StartShell(rig, "ended");
            var shellB = await StartShell(rig, "stopped");

            await rig.RunAsync("s1", "x", runId: "ended");
            var stopped = rig.Start("s2", "x", runId: "stopped");
            await Wait.UntilAsync(() => stopped.HasStarted);
            rig.Registry.Abort(stopped.RunId);
            await Rig.Settled(stopped);

            Assert.Equal(RunState.Aborted, stopped.State);
            Assert.All(rig.Shells!.List(), s => Assert.Equal(BackgroundShellStatus.Running, s.Status));
            Assert.Equal(2, rig.Shells.RunningCount);
            _ = (shellA, shellB);
        }
        finally
        {
            await rig.Shells!.ShutdownAsync();
        }
    }

    [Fact]
    public async Task Shutdown_stops_the_runs_and_then_kills_every_background_shell_they_do_not_belong_to_any_run_so_abort_all_cannot_take_them()
    {
        using var rig = new Rig(o => o.Shells = true);
        rig.NewSessionRow();
        rig.Llm.Turn(Up.Hang());
        var run = rig.Start("s1", "x", runId: "r1");
        await Wait.UntilAsync(() => run.HasStarted);
        await StartShell(rig, "r1");
        Assert.Equal(1, rig.Shells!.RunningCount);

        await rig.Registry.ShutdownAsync();
        Assert.Equal(RunState.Aborted, run.State);
        Assert.Equal(0, rig.Shells.RunningCount);
        Assert.Empty(rig.Shells.List());
    }

    [Fact]
    public async Task Forgetting_a_session_stops_its_runs_and_kills_only_the_shells_that_belong_to_its_runs()
    {
        using var rig = new Rig(o => o.Shells = true);
        rig.NewSessionRow("doomed");
        rig.NewSessionRow("other");
        try
        {
            await rig.RunAsync("doomed", "x", runId: "rd");
            await rig.RunAsync("other", "x", runId: "ro");
            var doomed = await StartShell(rig, "rd");
            var other = await StartShell(rig, "ro");

            await rig.Registry.ForgetSessionsAsync(["doomed"]);

            var after = rig.Shells!.List().ToDictionary(s => s.Id);
            Assert.Equal(BackgroundShellStatus.Killed, after[doomed.Id].Status);
            Assert.Equal(BackgroundShellStatus.Running, after[other.Id].Status);
        }
        finally
        {
            await rig.Shells!.ShutdownAsync();
        }
    }

    [Fact]
    public async Task Forgetting_a_session_with_a_live_run_aborts_it_first_and_waits_for_it()
    {
        using var rig = new Rig(o => o.Shells = true);
        rig.NewSessionRow();
        rig.Llm.Turn(Up.Hang());
        var run = rig.Start("s1", "x", runId: "r1");
        await Wait.UntilAsync(() => run.HasStarted);
        await StartShell(rig, "r1");
        try
        {
            await rig.Registry.ForgetSessionsAsync(["s1"]);
            Assert.Equal(RunState.Aborted, run.State);
            Assert.True(run.IsSettled);
            Assert.Equal(0, rig.Shells!.RunningCount);
        }
        finally
        {
            await rig.Shells!.ShutdownAsync();
        }
    }
}
