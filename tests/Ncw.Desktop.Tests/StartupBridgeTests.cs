using Ncw.Client.Devices;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Desktop.Sidecar;
using Ncw.UI.Localization;

namespace Ncw.Desktop.Tests;

/// <summary>本机 Agent 守护 → 启动事实(docs/design/03 §10):守护的状态机是真源,桥只做结构化投影。守护逻辑用假进程 + 假握手 + 假时钟。</summary>
public class StartupBridgeTests
{
    private static ClientStores Tracked()
    {
        var stores = new ClientStores();
        stores.Startup.Begin(StartupProfile.Desktop);
        return stores;
    }

    [Fact]
    public void Every_phase_and_issue_maps_and_every_issue_key_has_a_title_and_a_hint_in_the_ui()
    {
        foreach (var phase in Enum.GetValues<SidecarPhase>())
        {
            Assert.True(Enum.IsDefined(StartupBridge.Map(new SidecarStatus(phase)).Phase));
        }

        Assert.Equal(StartupSidecarPhase.Failed, StartupBridge.Map(new SidecarStatus(SidecarPhase.Failed)).Phase);
        Assert.Equal(StartupSidecarPhase.Restarting, StartupBridge.Map(new SidecarStatus(SidecarPhase.Restarting)).Phase);
        Assert.Equal(StartupSidecarPhase.Running, StartupBridge.Map(new SidecarStatus(SidecarPhase.Running)).Phase);
        Assert.Null(StartupBridge.IssueKey(SidecarIssue.None));

        foreach (var issue in Enum.GetValues<SidecarIssue>().Where(i => i != SidecarIssue.None))
        {
            var key = StartupBridge.IssueKey(issue);
            Assert.False(string.IsNullOrEmpty(key), $"{issue} has no key");
            Assert.True(Enum.IsDefined(Enum.Parse<NcwTextKind>("StartupFailTitle" + KeyText.Pascal(key))), $"{issue}: no title text");
            Assert.True(Enum.IsDefined(Enum.Parse<NcwTextKind>("StartupFailHint" + KeyText.Pascal(key))), $"{issue}: no hint text");
        }
    }

    [Fact]
    public void The_detail_retry_delay_and_restart_count_are_carried_over()
    {
        var mapped = StartupBridge.Map(new SidecarStatus(SidecarPhase.Restarting, SidecarIssue.LaunchFailed, null, 3, TimeSpan.FromSeconds(4), "boom"));

        Assert.Equal(new StartupSidecar(StartupSidecarPhase.Restarting, "launch-failed", 3, TimeSpan.FromSeconds(4), "boom"), mapped);
    }

    [Fact]
    public async Task A_spawn_and_the_handshake_become_two_separate_milestones_and_the_status_is_mirrored()
    {
        await using var rig = new Rig();
        var stores = Tracked();
        StartupBridge.Attach(rig.Sidecar, new StartupSignals(stores, InlineDispatcher.Instance));

        rig.Sidecar.Start();
        await Rig.Until(() => stores.Startup.State.Facts!.Has(StartupMilestone.AgentReady), "ready milestone");   // 状态先变,处理器随后写事实

        var facts = stores.Startup.State.Facts!;
        Assert.True(facts.Has(StartupMilestone.AgentSpawned));
        Assert.True(facts.Has(StartupMilestone.AgentReady));
        Assert.True(facts.Reached[StartupMilestone.AgentSpawned] <= facts.Reached[StartupMilestone.AgentReady]);
        Assert.Equal(StartupSidecarPhase.Running, facts.Sidecar!.Phase);
    }

    [Fact]
    public async Task Attaching_to_an_agent_that_is_already_running_is_ready_without_a_spawn_milestone()
    {
        await using var first = new Rig(keep: true);
        first.Sidecar.Start();
        await first.UntilPhase(SidecarPhase.Running);
        var stores = Tracked();

        // 同一个数据根、上次留下的 Agent:接上,不拉起
        var second = first.Make();
        StartupBridge.Attach(second, new StartupSignals(stores, InlineDispatcher.Instance));
        second.Start();
        await Rig.Until(() => stores.Startup.State.Facts!.Has(StartupMilestone.AgentReady), "ready milestone");

        var facts = stores.Startup.State.Facts!;
        Assert.True(facts.Has(StartupMilestone.AgentReady));
        Assert.False(facts.Has(StartupMilestone.AgentSpawned));
        await second.DisposeAsync();
    }

    [Fact]
    public async Task A_missing_agent_becomes_a_failed_fact_that_a_retry_clears_immediately()
    {
        await using var rig = new Rig();
        rig.AgentPath = null;
        var stores = Tracked();
        StartupBridge.Attach(rig.Sidecar, new StartupSignals(stores, InlineDispatcher.Instance));
        rig.Sidecar.Start();
        await Rig.Until(() => stores.Startup.State.Facts!.Sidecar is { Phase: StartupSidecarPhase.Failed, Issue: "agent-not-found" }, "failed fact");

        rig.AgentPath = "/fake/ncw-agent";      // 装好了 / 改了 NCW_AGENT_PATH
        new SidecarStartupHost(rig.Sidecar).Retry();   // 不推进假时钟:不等 5 秒的「重新找」间隔

        await Rig.Until(() => stores.Startup.State.Facts!.Sidecar is { Phase: StartupSidecarPhase.Running }, "running right after the retry");
        Assert.Single(rig.Launcher.Starts);
    }

    [Fact]
    public async Task Retry_does_nothing_while_the_agent_is_running_so_it_cannot_leave_a_stray_signal_behind()
    {
        await using var rig = new Rig();
        rig.Sidecar.Start();
        await rig.UntilPhase(SidecarPhase.Running);

        new SidecarStartupHost(rig.Sidecar).Retry();
        rig.Launcher.Last.Exit(1);   // 之后一次真正的崩溃:要按退避重启,不被刚才那一下「重试」抢先放行
        await rig.UntilPhase(SidecarPhase.Restarting);
        Assert.Single(rig.Launcher.Starts);

        await rig.AdvanceUntil(() => rig.Sidecar.Status.Phase == SidecarPhase.Running, "restart after the backoff");
        Assert.Equal(2, rig.Launcher.Starts.Count);
    }

    [Fact]
    public async Task Diagnostics_carry_the_status_and_the_tail_of_the_agent_log()
    {
        await using var rig = new Rig();
        Directory.CreateDirectory(rig.Paths.LogsDir);
        await File.WriteAllLinesAsync(rig.Paths.LogFile, Enumerable.Range(1, 50).Select(i => $"line {i}"));

        var text = new SidecarStartupHost(rig.Sidecar, logTailLines: 5).Diagnostics()!;

        Assert.Contains("sidecar status: Idle", text);
        Assert.Contains("line 50", text);
        Assert.Contains("line 46", text);
        Assert.DoesNotContain("line 45", text);
    }
}
