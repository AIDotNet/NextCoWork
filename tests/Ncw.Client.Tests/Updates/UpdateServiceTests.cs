using Ncw.Client.Actions;
using Ncw.Client.Updates;
using Ncw.Core.Domain;

namespace Ncw.Client.Tests.Updates;

/// <summary>自更新状态机(假引擎 + 假宿主 + 手动时钟):阶段转换、单飞、单调、回滚不降级、失败不覆盖好状态、重启顺序、后台检查。</summary>
public class UpdateServiceTests
{
    private static readonly ReleaseManifest V301 = UpdateKit.Manifest("3.0.1", notes: "fixes");

    private static async Task<UpdateRig> Initialized(string current = "3.0.0", UpdateOptions? options = null, bool installed = true)
    {
        var rig = new UpdateRig(options, current, installed: installed);
        await rig.Service.InitializeAsync();
        return rig;
    }

    [Fact]
    public async Task An_app_that_is_not_installed_is_disabled_and_never_touches_the_engine()
    {
        var rig = await Initialized(installed: false);
        await rig.Service.CheckAsync();
        await rig.Service.DownloadAsync();
        Assert.Equal(UpdatePhase.Disabled, rig.State.Phase);
        Assert.Empty(rig.Engine.Checks);
        Assert.False(await rig.Service.SetChannelAsync(UpdateChannel.Beta));
        Assert.False(await rig.Service.RestartAndApplyAsync());
    }

    [Fact]
    public async Task Initialize_loads_the_saved_channel_and_last_check_time_and_shows_the_running_version()
    {
        var rig = new UpdateRig(current: "3.0.0");
        var saved = UpdateRig.Start.AddDays(-2);
        rig.Meta.Stored = new UpdateMeta("beta", saved);
        await rig.Service.InitializeAsync();
        Assert.Equal(UpdateChannel.Beta, rig.State.Channel);
        Assert.Equal(saved, rig.State.LastCheckedAt);
        Assert.Equal("3.0.0", rig.State.CurrentVersion);
        Assert.Equal(UpdatePhase.Idle, rig.State.Phase);
    }

    [Fact]
    public async Task A_newer_signed_release_becomes_available_with_its_policy_and_notes()
    {
        var rig = await Initialized();
        var grace = UpdateRig.Start.AddDays(7);
        rig.Engine.Offering(UpdateKit.Manifest("3.0.1", mandatory: true, minimum: "3.0.1", grace: grace, notes: "fixes"));

        await rig.Service.CheckAsync();

        Assert.Equal(UpdatePhase.Available, rig.State.Phase);
        Assert.Equal(new UpdateRelease("3.0.1", true, "3.0.1", grace), rig.State.Release);
        Assert.Equal("fixes", rig.State.Notes);
        Assert.Equal(UpdateRig.Start, rig.State.LastCheckedAt);
        Assert.Null(rig.State.Failure);
        // 上次检查时间和通道写进了记录
        Assert.Equal(new UpdateMeta("stable", UpdateRig.Start), rig.Meta.Stored);
    }

    [Fact]
    public async Task Up_to_date_when_the_engine_offers_nothing()
    {
        var rig = await Initialized("3.0.1");
        rig.Engine.Offering(V301, offer: false);
        await rig.Service.CheckAsync();
        Assert.Equal(UpdatePhase.UpToDate, rig.State.Phase);
        Assert.Null(rig.State.Release);
        Assert.NotNull(rig.State.LastCheckedAt);
    }

    [Fact]
    public async Task A_feed_that_went_backwards_never_downgrades_even_if_the_engine_offers_it()
    {
        // 当前 3.0.1,feed 回滚到 3.0.0 且(假设引擎出错地)给了 offer:服务按版本比较判「已是最新」
        var rig = await Initialized("3.0.1");
        rig.Engine.Offering(UpdateKit.Manifest("3.0.0"), offer: true);
        await rig.Service.CheckAsync();
        Assert.Equal(UpdatePhase.UpToDate, rig.State.Phase);
        Assert.Null(rig.State.Release);
        await rig.Service.DownloadAsync();
        Assert.Equal(0, rig.Engine.Downloads);
        Assert.Contains(rig.Logs, l => l.Contains("feed offers 3.0.0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_same_version_is_not_an_update_and_a_prerelease_is_older_than_its_final()
    {
        var rig = await Initialized("3.0.0");
        rig.Engine.Offering(UpdateKit.Manifest("3.0.0"), offer: true);
        await rig.Service.CheckAsync();
        Assert.Equal(UpdatePhase.UpToDate, rig.State.Phase);

        var beta = await Initialized("3.0.0-beta.3");
        beta.Engine.Offering(UpdateKit.Manifest("3.0.0"));
        await beta.Service.CheckAsync();
        Assert.Equal(UpdatePhase.Available, beta.State.Phase);
    }

    [Theory]
    [InlineData(UpdateFailureKind.SignatureInvalid)]
    [InlineData(UpdateFailureKind.SignatureMissing)]
    [InlineData(UpdateFailureKind.UnknownSigningKey)]
    [InlineData(UpdateFailureKind.NoTrustedKeys)]
    [InlineData(UpdateFailureKind.Network)]
    [InlineData(UpdateFailureKind.FeedInvalid)]
    public async Task A_failed_check_is_failed_with_the_reason_and_offers_nothing_to_download(UpdateFailureKind kind)
    {
        var rig = await Initialized();
        rig.Engine.CheckFailure = new UpdateException(kind, "detail with /secret/path");
        await rig.Service.CheckAsync();

        Assert.Equal(UpdatePhase.Failed, rig.State.Phase);
        Assert.Equal(kind, rig.State.Failure);
        Assert.Null(rig.State.Release);
        Assert.Null(rig.State.LastCheckedAt); // 没读到验过签名的清单:不更新「上次检查时间」
        await rig.Service.DownloadAsync();
        Assert.Equal(0, rig.Engine.Downloads);
        Assert.Contains(rig.Logs, l => l.Contains(kind.ToString(), StringComparison.Ordinal)); // 细节只进日志
    }

    [Fact]
    public async Task A_failed_check_does_not_erase_a_release_we_already_know()
    {
        var rig = await Initialized();
        rig.Engine.Offering(V301);
        await rig.Service.CheckAsync();
        var checkedAt = rig.State.LastCheckedAt;

        rig.Clock.Advance(TimeSpan.FromHours(1));
        rig.Engine.CheckFailure = new UpdateException(UpdateFailureKind.SignatureInvalid, "tampered");
        await rig.Service.CheckAsync();

        Assert.Equal(UpdatePhase.Available, rig.State.Phase);
        Assert.Equal("3.0.1", rig.State.Release!.Version);
        Assert.Equal(UpdateFailureKind.SignatureInvalid, rig.State.Failure);
        Assert.Equal(checkedAt, rig.State.LastCheckedAt);

        // 恢复之后失败标记清掉
        rig.Engine.CheckFailure = null;
        await rig.Service.CheckAsync();
        Assert.Null(rig.State.Failure);
    }

    [Fact]
    public async Task Download_reports_monotonic_progress_and_ends_ready()
    {
        var rig = await Initialized();
        rig.Engine.Offering(V301);
        await rig.Service.CheckAsync();
        var seen = new List<int>();
        rig.Stores.Updates.Changed += () => { if (rig.State.Phase == UpdatePhase.Downloading) { seen.Add(rig.State.Progress); } };

        await rig.Service.DownloadAsync();

        Assert.Equal(UpdatePhase.Ready, rig.State.Phase);
        Assert.Equal(100, rig.State.Progress);
        Assert.Equal([0, 10, 50, 100], seen.Distinct()); // 50 的重复值和倒退的 30 都没有出现
        Assert.Equal(1, rig.Engine.Downloads);
    }

    [Theory]
    [InlineData(UpdateFailureKind.ChecksumMismatch)]
    [InlineData(UpdateFailureKind.DownloadFailed)]
    [InlineData(UpdateFailureKind.Network)]
    public async Task A_failed_download_goes_back_to_available_so_it_can_be_retried(UpdateFailureKind kind)
    {
        var rig = await Initialized();
        rig.Engine.Offering(V301);
        await rig.Service.CheckAsync();
        rig.Engine.DownloadFailure = new UpdateException(kind, "x");

        await rig.Service.DownloadAsync();

        Assert.Equal(UpdatePhase.Available, rig.State.Phase);
        Assert.Equal(kind, rig.State.Failure);
        Assert.Equal(0, rig.State.Progress);

        rig.Engine.DownloadFailure = null;
        await rig.Service.DownloadAsync();
        Assert.Equal(UpdatePhase.Ready, rig.State.Phase);
        Assert.Null(rig.State.Failure);
    }

    [Fact]
    public async Task Overlapping_checks_are_single_flight()
    {
        var rig = await Initialized();
        rig.Engine.Offering(V301);
        rig.Engine.CheckGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = rig.Service.CheckAsync();
        var second = rig.Service.CheckAsync();
        Assert.Equal(UpdatePhase.Checking, rig.State.Phase);
        Assert.Same(first, second);

        rig.Engine.CheckGate.SetResult();
        await Task.WhenAll(first, second);
        Assert.Single(rig.Engine.Checks);
        Assert.Equal(UpdatePhase.Available, rig.State.Phase);
    }

    [Fact]
    public async Task A_download_click_while_checking_does_nothing_and_a_check_while_downloading_does_nothing()
    {
        var rig = await Initialized();
        rig.Engine.Offering(V301);
        await rig.Service.CheckAsync();
        rig.Engine.DownloadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var download = rig.Service.DownloadAsync();
        Assert.Equal(UpdatePhase.Downloading, rig.State.Phase);

        await rig.Service.CheckAsync();           // 下载中:不检查,立刻返回
        Assert.Same(download, rig.Service.DownloadAsync()); // 重复点击:同一个任务
        Assert.Single(rig.Engine.Checks);
        Assert.Equal(1, rig.Engine.Downloads);

        rig.Engine.DownloadGate.SetResult();
        await download;
        Assert.Equal(UpdatePhase.Ready, rig.State.Phase);
    }

    [Fact]
    public async Task A_ready_update_is_not_downgraded_by_a_later_check()
    {
        var rig = await Initialized();
        rig.Engine.Offering(V301);
        await rig.Service.CheckAsync();
        await rig.Service.DownloadAsync();
        Assert.Equal(UpdatePhase.Ready, rig.State.Phase);

        rig.Engine.Offering(V301, offer: false); // 迟到的「没有更新」
        await rig.Service.CheckAsync();

        Assert.Equal(UpdatePhase.Ready, rig.State.Phase);
        Assert.Equal("3.0.1", rig.State.Release!.Version);
        Assert.Single(rig.Engine.Checks);
    }

    [Fact]
    public async Task Restart_stages_the_updater_first_then_stops_background_services_then_exits()
    {
        var rig = await Initialized();
        rig.Engine.Offering(V301);
        await rig.Service.CheckAsync();
        await rig.Service.DownloadAsync();

        var started = await rig.Service.RestartAndApplyAsync();

        Assert.True(started);
        Assert.Equal(UpdatePhase.Installing, rig.State.Phase);
        Assert.Equal(["prepare", "exit"], rig.Journal);
        Assert.Equal("3.0.1", Assert.Single(rig.Engine.Staged).Version);
        Assert.Equal(1, rig.Host.ExitRequests);
    }

    [Fact]
    public async Task If_the_updater_cannot_be_staged_nothing_is_stopped_and_the_app_stays_usable()
    {
        var rig = await Initialized();
        rig.Engine.Offering(V301);
        await rig.Service.CheckAsync();
        await rig.Service.DownloadAsync();
        rig.Engine.StageFailure = new UpdateException(UpdateFailureKind.ApplyFailed, "no updater");

        var started = await rig.Service.RestartAndApplyAsync();

        Assert.False(started);
        Assert.Equal(UpdatePhase.Ready, rig.State.Phase);          // 还能再点
        Assert.Equal(UpdateFailureKind.ApplyFailed, rig.State.Failure);
        Assert.Empty(rig.Journal);                                  // sidecar 没被动过,应用没退
        rig.Engine.StageFailure = null;
        Assert.True(await rig.Service.RestartAndApplyAsync());
    }

    [Fact]
    public async Task A_sidecar_that_fails_to_stop_does_not_stop_the_restart()
    {
        var rig = await Initialized();
        rig.Engine.Offering(V301);
        await rig.Service.CheckAsync();
        await rig.Service.DownloadAsync();
        rig.Host.PrepareFails = true;

        Assert.True(await rig.Service.RestartAndApplyAsync());

        Assert.Equal(["prepare", "exit"], rig.Journal);
        Assert.Contains(rig.Logs, l => l.Contains("sidecar did not stop", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Restart_only_works_from_ready_and_a_second_click_is_ignored()
    {
        var rig = await Initialized();
        Assert.False(await rig.Service.RestartAndApplyAsync()); // Idle
        rig.Engine.Offering(V301);
        await rig.Service.CheckAsync();
        Assert.False(await rig.Service.RestartAndApplyAsync()); // Available,没下载
        await rig.Service.DownloadAsync();
        Assert.True(await rig.Service.RestartAndApplyAsync());
        Assert.False(await rig.Service.RestartAndApplyAsync()); // Installing
        Assert.Single(rig.Engine.Staged);
        Assert.Equal(1, rig.Host.ExitRequests);
    }

    [Fact]
    public async Task Changing_the_channel_resets_the_pending_release_saves_it_and_checks_the_new_channel()
    {
        var rig = await Initialized();
        rig.Engine.OnCheck = channel => channel == UpdateChannel.Beta
            ? new UpdateCheckResult(UpdateKit.Manifest("3.1.0-beta.1", channel: "beta"), new UpdateOffer("3.1.0-beta.1", UpdateKit.Manifest("3.1.0-beta.1", channel: "beta"), "h"))
            : new UpdateCheckResult(V301, new UpdateOffer("3.0.1", V301, "h"));
        await rig.Service.CheckAsync();
        Assert.Equal("3.0.1", rig.State.Release!.Version);

        Assert.True(await rig.Service.SetChannelAsync(UpdateChannel.Beta));

        Assert.Equal(UpdateChannel.Beta, rig.State.Channel);
        Assert.Equal("3.1.0-beta.1", rig.State.Release!.Version);
        Assert.Equal([UpdateChannel.Stable, UpdateChannel.Beta], rig.Engine.Checks);
        Assert.Equal("beta", rig.Meta.Stored!.Channel);
        Assert.True(await rig.Service.SetChannelAsync(UpdateChannel.Beta)); // 同一个通道:不重复检查
        Assert.Equal(2, rig.Engine.Checks.Count);
    }

    [Fact]
    public async Task The_channel_cannot_be_changed_while_downloading_or_when_an_update_is_ready()
    {
        var rig = await Initialized();
        rig.Engine.Offering(V301);
        await rig.Service.CheckAsync();
        await rig.Service.DownloadAsync();
        Assert.False(await rig.Service.SetChannelAsync(UpdateChannel.Beta));
        Assert.Equal(UpdateChannel.Stable, rig.State.Channel);
    }

    [Fact]
    public async Task A_check_result_that_arrives_after_a_channel_change_is_dropped()
    {
        var rig = await Initialized();
        rig.Engine.Offering(V301);
        rig.Engine.CheckGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stale = rig.Service.CheckAsync();
        Assert.False(await rig.Service.SetChannelAsync(UpdateChannel.Beta)); // 检查进行中不换通道
        rig.Engine.CheckGate.SetResult();
        await stale;
        Assert.Equal(UpdateChannel.Stable, rig.State.Channel);
    }

    [Fact]
    public async Task Background_check_runs_after_the_first_delay_and_then_on_the_interval()
    {
        var rig = await Initialized(options: new UpdateOptions { FirstCheckDelay = TimeSpan.FromSeconds(30), CheckInterval = TimeSpan.FromHours(6), AutoCheck = true });
        rig.Engine.Offering(V301, offer: false);
        rig.Engine.CurrentVersion = "3.0.1";

        rig.Clock.Advance(TimeSpan.FromSeconds(29));
        await Task.Delay(50);
        Assert.Empty(rig.Engine.Checks);

        rig.Clock.Advance(TimeSpan.FromSeconds(2));
        await WaitUntil(() => rig.Engine.Checks.Count == 1);
        rig.Clock.Advance(TimeSpan.FromHours(6));
        await WaitUntil(() => rig.Engine.Checks.Count == 2);
    }

    [Fact]
    public async Task Background_check_downloads_a_mandatory_update_by_itself_but_not_an_optional_one()
    {
        var optional = await Initialized(options: new UpdateOptions { FirstCheckDelay = TimeSpan.FromSeconds(1), AutoCheck = true });
        optional.Engine.Offering(V301);
        optional.Clock.Advance(TimeSpan.FromSeconds(2));
        await WaitUntil(() => optional.State.Phase == UpdatePhase.Available);
        await Task.Delay(100);
        Assert.Equal(UpdatePhase.Available, optional.State.Phase);
        Assert.Equal(0, optional.Engine.Downloads);

        var mandatory = await Initialized(options: new UpdateOptions { FirstCheckDelay = TimeSpan.FromSeconds(1), AutoCheck = true });
        mandatory.Engine.Offering(UpdateKit.Manifest("3.0.1", mandatory: true, minimum: "3.0.1"));
        mandatory.Clock.Advance(TimeSpan.FromSeconds(2));
        await WaitUntil(() => mandatory.State.Phase == UpdatePhase.Ready);
        Assert.Equal(1, mandatory.Engine.Downloads);
    }

    [Fact]
    public async Task Auto_check_can_be_turned_off()
    {
        var rig = await Initialized(options: new UpdateOptions { FirstCheckDelay = TimeSpan.FromSeconds(1), AutoCheck = false });
        rig.Clock.Advance(TimeSpan.FromHours(12));
        await Task.Delay(50);
        Assert.Empty(rig.Engine.Checks);
    }

    [Fact]
    public async Task The_grace_deadline_gives_the_state_a_tick_so_time_based_views_can_re_derive()
    {
        var rig = await Initialized();
        var grace = UpdateRig.Start.AddHours(2);
        rig.Engine.Offering(UpdateKit.Manifest("3.0.1", mandatory: true, minimum: "3.0.1", grace: grace));
        await rig.Service.CheckAsync();
        var before = rig.State.Tick;
        var changes = 0;
        rig.Stores.Updates.Changed += () => changes++;

        rig.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(before, rig.State.Tick);
        rig.Clock.Advance(TimeSpan.FromHours(1.1));

        Assert.Equal(before + 1, rig.State.Tick);
        Assert.Equal(1, changes);
        Assert.Equal(UpdateEnforcement.Blocking, UpdatePolicy.EnforcementOf(rig.State.CurrentVersion, rig.State.Release, rig.Clock.GetUtcNow()));
    }

    [Fact]
    public async Task Actions_without_a_service_are_no_ops_and_with_one_delegate_to_it()
    {
        var none = new UpdateActions(null);
        Assert.False(none.IsAvailable);
        await none.CheckAsync();
        await none.DownloadAsync();
        Assert.False(await none.RestartAndApplyAsync());
        Assert.False(await none.SetChannelAsync(UpdateChannel.Beta));

        var rig = await Initialized();
        rig.Engine.Offering(V301);
        var actions = new UpdateActions(rig.Service);
        await actions.CheckAsync();
        await actions.DownloadAsync();
        Assert.True(await actions.RestartAndApplyAsync());
        Assert.Equal(UpdatePhase.Installing, rig.State.Phase);
    }

    [Fact]
    public async Task Disposing_stops_the_background_timers()
    {
        var rig = await Initialized(options: new UpdateOptions { FirstCheckDelay = TimeSpan.FromSeconds(1), AutoCheck = true });
        rig.Engine.Offering(V301, offer: false);
        await rig.Service.DisposeAsync();
        rig.Clock.Advance(TimeSpan.FromHours(1));
        await Task.Delay(50);
        Assert.Empty(rig.Engine.Checks);
        await rig.Service.DisposeAsync(); // 重复释放不抛
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException("condition not reached");
            }

            await Task.Delay(10);
        }
    }
}
