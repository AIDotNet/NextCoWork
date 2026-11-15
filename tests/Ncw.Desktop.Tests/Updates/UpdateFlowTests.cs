using Ncw.Client.Tests.Updates;
using Ncw.Client.Updates;
using Ncw.Desktop.Sidecar;
using Ncw.Desktop.Updates;

namespace Ncw.Desktop.Tests.Updates;

/// <summary>
/// 桌面端更新重启的集成:真实的 <see cref="UpdateService"/> 状态机 + <see cref="DesktopUpdateHost"/> + 真实的 <see cref="LocalAgentSidecar"/>(假进程 + 假握手 + 假时钟)。
/// 验证 04 §3 的顺序:更新器就位 → sidecar 优雅收尾(不管「关窗后继续运行」)→ 才请求退出;更新器交接失败时 sidecar 一点没动。
/// </summary>
public class UpdateFlowTests
{
    private static readonly ReleaseManifest V301 = UpdateKit.Manifest("3.0.1");

    private sealed class Flow : IAsyncDisposable
    {
        public readonly Rig Rig;
        public readonly UpdateRig Update;
        public readonly DesktopUpdateHost Host;
        public readonly List<int> HardExits = [];
        public int Exits;

        public Flow(bool keepAgent)
        {
            Rig = new Rig(keep: keepAgent);
            Update = new UpdateRig();
            Host = new DesktopUpdateHost(() => Rig.Sidecar, () => { Exits++; Journal.Add("exit"); }, Rig.Clock, TimeSpan.FromSeconds(20), HardExits.Add);
        }

        public List<string> Journal => Update.Journal;

        public UpdateService Service() =>
            new(Update.Stores, new RecordingEngine(Update.Engine, Journal), Host, new MemoryUpdateMetaStore(), null, Update.Clock, new UpdateOptions { AutoCheck = false });

        public async ValueTask DisposeAsync() => await Rig.DisposeAsync();
    }

    /// <summary>把引擎的「就位」记进同一份日志,看顺序。</summary>
    private sealed class RecordingEngine(FakeUpdateEngine inner, List<string> journal) : IUpdateEngine
    {
        public bool IsInstalled => inner.IsInstalled;

        public string CurrentVersion => inner.CurrentVersion;

        public Task<UpdateCheckResult> CheckAsync(UpdateChannel channel, CancellationToken ct) => inner.CheckAsync(channel, ct);

        public Task DownloadAsync(UpdateOffer offer, Action<int> progress, CancellationToken ct) => inner.DownloadAsync(offer, progress, ct);

        public void StageRestart(UpdateOffer offer)
        {
            journal.Add("stage");
            inner.StageRestart(offer);
        }
    }

    private static async Task<(Flow Flow, UpdateService Service)> ReadyAsync(bool keepAgent)
    {
        var flow = new Flow(keepAgent);
        flow.Update.Engine.Offering(V301);
        var service = flow.Service();
        await service.InitializeAsync();
        flow.Rig.Sidecar.Start();
        await flow.Rig.UntilPhase(SidecarPhase.Running);
        await service.CheckAsync();
        await service.DownloadAsync();
        Assert.Equal(UpdatePhase.Ready, flow.Update.State.Phase);
        return (flow, service);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restart_stages_the_updater_then_stops_the_agent_gracefully_then_exits_regardless_of_keep_running(bool keepAgent)
    {
        var (flow, service) = await ReadyAsync(keepAgent);
        await using (flow)
        {
            var process = flow.Rig.Launcher.Last;
            Assert.False(process.HasExited);

            var restart = service.RestartAndApplyAsync();
            await flow.Rig.AdvanceUntil(() => restart.IsCompleted, "restart");
            Assert.True(await restart);

            Assert.Equal(["stage", "exit"], flow.Journal);   // 就位在前,退出在后
            Assert.True(process.HasExited);                   // sidecar 已经收尾
            Assert.True(process.StdinClosed > 0 || process.Terminated > 0);
            Assert.Equal(0, process.ExitCode);                // 优雅退出,不是强杀
            Assert.Equal(0, process.Killed);
            Assert.Equal(SidecarPhase.Stopped, flow.Rig.Sidecar.Status.Phase);
            Assert.Null(flow.Rig.StateFile.Read());           // 状态文件删掉了:下次启动换新令牌,不会去「接上」旧 Agent
            Assert.Equal(1, flow.Exits);
            Assert.Equal(UpdatePhase.Installing, flow.Update.State.Phase);
        }
    }

    [Fact]
    public async Task The_agent_is_not_restarted_by_the_supervisor_after_a_stop_for_update()
    {
        var (flow, service) = await ReadyAsync(keepAgent: false);
        await using (flow)
        {
            var restart = service.RestartAndApplyAsync();
            await flow.Rig.AdvanceUntil(() => restart.IsCompleted, "restart");
            await restart;
            flow.Rig.Clock.Advance(TimeSpan.FromMinutes(5)); // 退避定时器要是还活着,这里会重启它
            await Task.Delay(50);
            Assert.Single(flow.Rig.Launcher.Starts);
        }
    }

    [Fact]
    public async Task If_the_updater_cannot_be_staged_the_agent_keeps_running_and_the_app_stays_open()
    {
        var (flow, service) = await ReadyAsync(keepAgent: false);
        await using (flow)
        {
            flow.Update.Engine.StageFailure = new UpdateException(UpdateFailureKind.ApplyFailed, "no updater next to the app");
            var process = flow.Rig.Launcher.Last;

            Assert.False(await service.RestartAndApplyAsync());

            Assert.Equal(["stage"], flow.Journal);
            Assert.False(process.HasExited);
            Assert.Equal(SidecarPhase.Running, flow.Rig.Sidecar.Status.Phase);
            Assert.Equal(0, flow.Exits);
            Assert.Equal(UpdatePhase.Ready, flow.Update.State.Phase);
            Assert.Equal(UpdateFailureKind.ApplyFailed, flow.Update.State.Failure);
        }
    }

    [Fact]
    public async Task An_agent_that_ignores_the_stdin_close_is_terminated_and_then_killed_without_blocking_the_restart_forever()
    {
        var (flow, service) = await ReadyAsync(keepAgent: false);
        await using (flow)
        {
            var process = flow.Rig.Launcher.Last;
            process.ExitOnStdinClose = false;
            process.ExitOnTerminate = false;

            var restart = service.RestartAndApplyAsync();
            await flow.Rig.AdvanceUntil(() => restart.IsCompleted, "restart", maxSteps: 60);

            Assert.True(await restart);
            Assert.True(process.HasExited);
            Assert.Equal(1, process.Killed);
            Assert.Equal(["stage", "exit"], flow.Journal);
        }
    }

    [Fact]
    public async Task The_app_exit_has_a_fallback_so_the_updater_that_only_waits_a_minute_is_not_left_hanging()
    {
        var (flow, service) = await ReadyAsync(keepAgent: false);
        await using (flow)
        {
            var restart = service.RestartAndApplyAsync();
            await flow.Rig.AdvanceUntil(() => restart.IsCompleted, "restart");
            await restart;
            Assert.Empty(flow.HardExits);

            flow.Rig.Clock.Advance(TimeSpan.FromSeconds(19));
            Assert.Empty(flow.HardExits);
            flow.Rig.Clock.Advance(TimeSpan.FromSeconds(2));

            Assert.Equal([0], flow.HardExits);
        }
    }

    [Fact]
    public async Task A_stopped_sidecar_can_be_disposed_again_by_the_normal_exit_path_without_error()
    {
        var (flow, service) = await ReadyAsync(keepAgent: true);
        await using (flow)
        {
            var restart = service.RestartAndApplyAsync();
            await flow.Rig.AdvanceUntil(() => restart.IsCompleted, "restart");
            await restart;
            await flow.Rig.DisposeSidecarAsync(); // 应用退出时 LocalBackend.DisposeAsync 还会再收尾一次
            Assert.Equal(SidecarPhase.Stopped, flow.Rig.Sidecar.Status.Phase);
        }
    }
}
