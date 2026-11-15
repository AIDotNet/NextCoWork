using Ncw.Client.Actions;
using Ncw.Integration.Tests.Support;
using Ncw.Protocol.Methods;

namespace Ncw.Integration.Tests;

/// <summary>
/// 设置 / 定时任务 / 用量的控制端链路(真实 Agent + <c>DeviceLink</c> + Store + Actions):Actions 发起、设备推送经主题折进 <c>ClientStores.Settings</c> / <c>Schedules</c>(key 带 deviceId),
/// 定时运行的状态由全量权威广播给出(终态之后不再是「运行中」),断线标为未知(规则 7)、重连后追平。
/// </summary>
public sealed class ServicesLinkTests : IAsyncLifetime
{
    private readonly OrchestrationRig _rig = new();

    public ValueTask InitializeAsync() => _rig.InitializeAsync();

    public ValueTask DisposeAsync() => _rig.DisposeAsync();

    private static string DeviceOf(Controller c) => c.Link.DeviceId;

    private static DeviceSettingsMirrorView Settings(Controller c)
    {
        lock (c.Ui.Gate)
        {
            var m = c.Stores.Settings.State.Of(c.Link.DeviceId);
            return new DeviceSettingsMirrorView(m.Known, m.Rev, m.Settings);
        }
    }

    private sealed record DeviceSettingsMirrorView(bool Known, long Rev, DeviceSettingsDto? Settings);

    private static Ncw.Client.Stores.DeviceSchedules Schedules(Controller c)
    {
        lock (c.Ui.Gate)
        {
            return c.Stores.Schedules.State.Of(c.Link.DeviceId);
        }
    }

    [Fact]
    public async Task Gateway_actions_turn_the_gateway_on_hand_out_a_key_that_works_and_off_again()
    {
        var c = await _rig.StartControllerAsync("ctrl-gateway");
        var gateway = new GatewayActions(c.Devices);
        await OrchestrationRig.WaitUntil(() => Settings(c).Known, "settings mirrored");

        var off = await gateway.GetStatusAsync(DeviceOf(c));
        Assert.False(off.Value!.Enabled);

        var on = await gateway.SetEnabledAsync(DeviceOf(c), true);
        Assert.True(on.IsOk);
        Assert.NotNull(on.Value!.Port);
        await OrchestrationRig.WaitUntil(() => Settings(c).Settings!.Gateway.Enabled, "the enabled switch is the mirrored setting");

        var key = await gateway.GetKeyAsync(DeviceOf(c), "viewer");
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{on.Value.Port}/api/device.info") { Content = new StringContent("{}") };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key.Value!.Key);
        using var response = await http.SendAsync(request);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        var bad = await gateway.GetKeyAsync(DeviceOf(c), "owner");
        Assert.Equal(ActionOutcome.Invalid, bad.Outcome);
        Assert.Equal("gateway.invalid.role", bad.ErrorKey);
        Assert.False((await gateway.SetEnabledAsync(DeviceOf(c), false)).Value!.Enabled);
    }

    [Fact]
    public async Task A_settings_action_reaches_the_store_through_the_topic_and_a_cut_connection_makes_it_unknown_until_it_catches_up()
    {
        var c = await _rig.StartControllerAsync("ctrl-settings");
        var settings = new SettingsActions(c.Devices);
        await OrchestrationRig.WaitUntil(() => Settings(c).Known, "settings mirrored");
        Assert.Equal(32_000, Settings(c).Settings!.MaxOutputTokens); // 出厂值
        var rev0 = Settings(c).Rev;

        var result = await settings.UpdateAsync(DeviceOf(c), new DeviceSettingsPatchDto(MaxOutputTokens: 9000, Gateway: new GatewaySettingsPatchDto(Enabled: true)));
        Assert.True(result.IsOk);
        await OrchestrationRig.WaitUntil(() => Settings(c).Settings!.MaxOutputTokens == 9000, "store follows the write");
        Assert.True(Settings(c).Rev > rev0);
        Assert.True(Settings(c).Settings!.Gateway.Enabled);

        // 深合并:紧跟着改同一块里的另一项,上一项不被覆盖
        await settings.UpdateAsync(DeviceOf(c), new DeviceSettingsPatchDto(Gateway: new GatewaySettingsPatchDto(AllowRemote: true)));
        await OrchestrationRig.WaitUntil(() => Settings(c).Settings!.Gateway.AllowRemote, "second write");
        Assert.True(Settings(c).Settings!.Gateway.Enabled);

        // 校验失败:结构化结局(界面按 key 翻译),Store 没有被乐观改动
        var bad = await settings.UpdateAsync(DeviceOf(c), new DeviceSettingsPatchDto(UpstreamIdleTimeoutSeconds: 5));
        Assert.Equal((ActionOutcome.Invalid, "settings.invalid.range"), (bad.Outcome, bad.ErrorKey));
        Assert.Equal(600, Settings(c).Settings!.UpstreamIdleTimeoutSeconds);

        // 断线:未知(规则 7),重连之后追平
        c.Current!.Cut();
        await OrchestrationRig.WaitUntil(() => !Settings(c).Known, "settings become unknown on disconnect");
        await OrchestrationRig.WaitUntil(() => Settings(c).Known && Settings(c).Settings!.MaxOutputTokens == 9000, "settings re-mirrored after reconnect");
    }

    [Fact]
    public async Task A_manual_scheduled_run_reaches_a_terminal_state_in_the_store_and_the_tasks_follow_the_device()
    {
        var c = await _rig.StartControllerAsync("ctrl-schedules");
        var schedules = new ScheduleActions(c.Devices);
        var project = await c.Link.AddProjectAsync(_rig.Workspace);
        await OrchestrationRig.WaitUntil(() => Schedules(c).Known, "schedules mirrored");

        var created = await schedules.CreateAsync(DeviceOf(c), new ScheduledTaskInputDto(
            "Nightly", "check things", project.WorkspaceId, "sonnet", new ScheduleRuleDto("daily", Time: "03:00"), Timezone: "UTC"));
        Assert.True(created.IsOk);
        await OrchestrationRig.WaitUntil(() => Schedules(c).Tasks.Any(t => t.Id == created.Value!.Id), "task in the store");

        var queued = await schedules.RunNowAsync(DeviceOf(c), created.Value!.Id);
        Assert.True(queued.IsOk);
        await OrchestrationRig.WaitUntil(() => Schedules(c).Runs.Any(r => r.Id == queued.Value!.Id && r.Status == "success"), "the run reaches a terminal state in the store");
        Assert.False(Schedules(c).IsRunning(created.Value.Id)); // 「运行中」只由权威广播回答,终态之后不再是

        // 删任务:Store 跟着设备
        Assert.True((await schedules.DeleteAsync(DeviceOf(c), created.Value.Id)).IsOk);
        await OrchestrationRig.WaitUntil(() => Schedules(c).Tasks.IsEmpty, "task removed from the store");
        // 运行记录留着(历史),可按任务翻页
        var page = await schedules.ListRunsAsync(DeviceOf(c), new ScheduledRunsQuery(created.Value.Id));
        Assert.Single(page.Value!.Runs);
    }

    [Fact]
    public async Task Usage_actions_read_through_the_link_and_a_disconnected_device_is_unavailable_not_an_exception()
    {
        var c = await _rig.StartControllerAsync("ctrl-usage");
        var usage = new UsageActions(c.Devices);

        var summary = await usage.GetSummaryAsync(DeviceOf(c), new UsageWindowDto(null, 1));
        Assert.True(summary.IsOk);
        Assert.Equal(0, summary.Value!.RequestCount);
        Assert.Empty((await usage.GetDailySeriesAsync(DeviceOf(c), new UsageWindowDto(null, 1))).Value!.Buckets);

        var unknown = await usage.GetSummaryAsync("no-such-device", new UsageWindowDto(null, 1));
        Assert.Equal(ActionOutcome.Unavailable, unknown.Outcome);
    }
}
