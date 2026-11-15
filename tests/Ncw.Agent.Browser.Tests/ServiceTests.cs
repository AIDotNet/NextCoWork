using Ncw.Agent.Environments;

namespace Ncw.Agent.Browser.Tests;

/// <summary>
/// 浏览器运行时:标签归属(移植自 2.x <c>manager.test.ts</c> 的「工作区和 run 隔离」几条)、每工作区一个浏览器进程、闲置回收、崩溃后重启、远程环境拒绝。
/// 2.x 里「认领用户标签 / clientTabId 幂等注册 / Profile 增删」依赖内置浏览器与全局 Profile 注册表,不移植(见设计 03 §10)。
/// </summary>
public sealed class ServiceTests
{
    [Fact]
    public async Task A_run_can_operate_its_own_tabs_and_the_same_session_can_continue_them_in_a_later_run()
    {
        await using var h = new Harness();
        var tab = await h.OpenTab(run: "run-a", session: "s1");

        var sameRun = await h.Run("browser_snapshot", "run-a", "s1", "ws-1", ("tabId", tab));
        var laterRun = await h.Run("browser_snapshot", "run-b", "s1", "ws-1", ("tabId", tab)); // 同会话的新 Run
        var otherSession = await h.Run("browser_snapshot", "run-c", "s2", "ws-1", ("tabId", tab));

        Assert.False(sameRun.IsError);
        Assert.False(laterRun.IsError);
        Assert.True(otherSession.IsError);
        Assert.Equal(BrowserErrorKeys.TabNotOwned, Harness.ErrorKey(otherSession));
        Assert.Contains("opened by this Agent session", Harness.Text(otherSession));
    }

    [Fact]
    public async Task A_tab_never_crosses_workspaces_even_for_the_same_run()
    {
        await using var h = new Harness();
        var tab = await h.OpenTab(run: "run-a", session: "s1", workspace: "ws-1");

        var other = await h.Run("browser_snapshot", "run-a", "s1", "ws-2", ("tabId", tab));

        Assert.True(other.IsError);
        Assert.Equal(BrowserErrorKeys.TabNotOwned, Harness.ErrorKey(other));
        Assert.Contains("another workspace", Harness.Text(other));
    }

    [Fact]
    public async Task Tab_listing_shows_only_the_callers_tabs()
    {
        await using var h = new Harness();
        var mine = await h.OpenTab(run: "run-a", session: "s1");
        await h.OpenTab(run: "run-b", session: "s2");
        await h.OpenTab(run: "run-c", session: "s3", workspace: "ws-2");

        var listing = await h.Run("browser_tabs", "run-a", "s1", "ws-1");

        var text = Harness.Text(listing);
        Assert.Contains(mine, text);
        Assert.Single(text.Split('\n'));
    }

    [Fact]
    public async Task One_browser_process_serves_a_workspace_and_each_workspace_gets_its_own_profile_directory()
    {
        await using var h = new Harness();
        await h.OpenTab(workspace: "ws-1");
        await h.OpenTab(workspace: "ws-1", run: "run-2");
        await h.OpenTab(workspace: "ws-2", run: "run-3", session: "s3");

        Assert.Equal(2, h.Launcher.Launches);
        var dirs = h.Launcher.Requests.Select(r => r.ProfileDirectory.Value).ToList();
        Assert.EndsWith("/ws-1", dirs[0]);
        Assert.EndsWith("/ws-2", dirs[1]);
        Assert.NotEqual(dirs[0], dirs[1]);
    }

    [Theory]
    [InlineData("a/b", "a%002Fb")]
    [InlineData("../x", "%002E%002E%002Fx")]
    [InlineData("ws_1-A", "ws_1-A")]
    [InlineData("", "default")]
    public void Workspace_ids_become_safe_single_path_segments(string id, string expected) =>
        Assert.Equal(expected, BrowserService.SafeName(id));

    [Fact]
    public async Task Concurrent_opens_for_one_workspace_launch_the_browser_once()
    {
        await using var h = new Harness();
        h.Launcher.BeforeLaunch = () => Task.Delay(100);

        var tabs = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => h.OpenTab(run: $"run-{i}", session: $"s{i}")));

        Assert.Equal(4, tabs.Distinct().Count());
        Assert.Equal(1, h.Launcher.Launches);
    }

    [Fact]
    public async Task The_per_workspace_tab_limit_is_enforced_with_a_structured_error()
    {
        await using var h = new Harness(maxTabs: 2);
        await h.OpenTab();
        await h.OpenTab();

        var third = await h.Run("browser_open", "run-1", "session-1", "ws-1", ("url", "https://example.com/"));

        Assert.True(third.IsError);
        Assert.Equal(BrowserErrorKeys.TabLimit, Harness.ErrorKey(third));
        Assert.Equal(2, h.Service.OpenTabCount);
    }

    [Fact]
    public async Task Closing_a_tab_frees_its_slot_and_the_id_stops_resolving()
    {
        await using var h = new Harness(maxTabs: 1);
        var tab = await h.OpenTab();

        var closed = await h.Call("browser_close", ("tabId", tab));
        Assert.False(closed.IsError);
        var after = await h.Call("browser_snapshot", ("tabId", tab));
        Assert.Equal(BrowserErrorKeys.TabNotFound, Harness.ErrorKey(after));
        await h.OpenTab(); // 名额回来了
        Assert.Single(h.Server.Of("Target.closeTarget"));
    }

    [Fact]
    public async Task A_failed_navigation_during_open_leaves_no_tab_behind()
    {
        await using var h = new Harness();
        h.Server.NavigateResult = _ => "{\"errorText\":\"net::ERR_CONNECTION_REFUSED\"}";

        var result = await h.Call("browser_open", ("url", "http://127.0.0.1:1/"));

        Assert.True(result.IsError);
        Assert.Equal(BrowserErrorKeys.NavigationFailed, Harness.ErrorKey(result));
        Assert.Contains("ERR_CONNECTION_REFUSED", Harness.Text(result));
        Assert.Equal(0, h.Service.OpenTabCount);
        Assert.Single(h.Server.Of("Target.closeTarget"));
    }

    [Fact]
    public async Task Idle_tabs_are_reaped_and_an_empty_browser_exits_one_period_later()
    {
        await using var h = new Harness(idle: TimeSpan.FromMinutes(10));
        var tab = await h.OpenTab();

        h.Clock.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal((0, 0), await h.Service.ReapIdleAsync());
        await h.Call("browser_snapshot", ("tabId", tab)); // 使用会刷新闲置计时
        h.Clock.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal((0, 0), await h.Service.ReapIdleAsync());

        h.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal((1, 0), await h.Service.ReapIdleAsync());
        Assert.Equal(0, h.Launcher.Kills);

        h.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal((0, 1), await h.Service.ReapIdleAsync());
        Assert.Equal(1, h.Launcher.Kills);
        Assert.Equal(0, h.Service.RunningBrowserCount);

        await h.OpenTab(); // 再开会重新起
        Assert.Equal(2, h.Launcher.Launches);
    }

    [Fact]
    public async Task When_the_browser_process_exits_its_tabs_vanish_and_the_next_open_relaunches()
    {
        await using var h = new Harness();
        var tab = await h.OpenTab();
        h.Launcher.Crash();
        await h.Server.DropConnectionsAsync();
        await Task.Delay(100);

        var gone = await h.Call("browser_snapshot", ("tabId", tab));
        Assert.True(gone.IsError);
        Assert.Equal(BrowserErrorKeys.TabNotFound, Harness.ErrorKey(gone));
        Assert.Equal(0, h.Service.OpenTabCount);
    }

    [Fact]
    public async Task Disposing_the_service_kills_every_browser()
    {
        var h = new Harness();
        await h.OpenTab(workspace: "ws-1");
        await h.OpenTab(workspace: "ws-2", run: "r2", session: "s2");

        await h.Service.DisposeAsync();

        Assert.Equal(2, h.Launcher.Kills);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => h.Service.OpenAsync(new BrowserCaller("ws-1", "r", "s"), h.Env, "https://example.com/", CancellationToken.None));
        await h.Server.DisposeAsync();
    }

    [Fact]
    public async Task A_remote_environment_is_refused_before_anything_is_launched()
    {
        await using var h = new Harness();
        var ssh = new SshLikeEnvironment(h.Env);
        h.EnvironmentOverride = ssh;

        var result = await h.Call("browser_open", ("url", "https://example.com/"));

        Assert.True(result.IsError);
        Assert.Equal(BrowserErrorKeys.RemoteEnvironment, Harness.ErrorKey(result));
        Assert.Contains("dev-box", Harness.Text(result));
        Assert.Equal(0, h.Launcher.Launches);
        Assert.Equal(0, ssh.OpenProcessCalls);
        // 错误卡片也带执行环境标识
        Assert.Equal("dev-box", result.Details!.Value.GetProperty("environment").GetProperty("environment").GetString());
        Assert.Equal("ssh", result.Details.Value.GetProperty("environment").GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_launch_failure_is_reported_with_its_key_and_does_not_poison_the_next_attempt()
    {
        await using var h = new Harness();
        var calls = 0;
        h.Launcher.BeforeLaunch = () => ++calls == 1 ? throw new BrowserException(BrowserErrorKeys.NotInstalled, "No Chrome found.") : Task.CompletedTask;

        var first = await h.Call("browser_open", ("url", "https://example.com/"));
        Assert.Equal(BrowserErrorKeys.NotInstalled, Harness.ErrorKey(first));

        var second = await h.Call("browser_open", ("url", "https://example.com/"));
        Assert.False(second.IsError);
    }
}
