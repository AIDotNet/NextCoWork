using Ncw.Client.Actions;
using Ncw.Client.Services;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Tests.Management;

public sealed class UsageViewTests
{
    [Fact]
    public void The_window_is_the_local_days_ending_tomorrow_midnight_and_All_has_no_lower_bound()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
        var now = new DateTimeOffset(2026, 11, 14, 10, 0, 0, TimeSpan.FromHours(8));

        var seven = UsageView.Window(UsageRange.Days7, now, zone);
        Assert.Equal(new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.FromHours(8)).ToUnixTimeMilliseconds(), seven.To);
        Assert.Equal(new DateTimeOffset(2026, 11, 8, 0, 0, 0, TimeSpan.FromHours(8)).ToUnixTimeMilliseconds(), seven.From);
        Assert.Null(UsageView.Window(UsageRange.All, now, zone).From);
    }

    [Fact]
    public void Daily_series_fills_missing_days_with_zero_and_never_adds_currencies_or_counts_unknown_cost_as_money()
    {
        var buckets = new[]
        {
            FakeUsage.Bucket("2026-11-12", requests: 4, input: 10, output: 5, currency: "USD", cost: 1_000_000),
            FakeUsage.Bucket("2026-11-12", requests: 1, currency: "CNY", cost: 7_000_000),
            FakeUsage.Bucket("2026-11-13", requests: 2, currency: "", cost: null, priced: 0),
        };
        var today = new DateOnly(2026, 11, 14);

        var requests = UsageView.Daily(buckets, UsageMetric.Requests, null, UsageRange.Days7, today);
        Assert.Equal(7, requests.Count);
        Assert.Equal(5, requests.Single(p => p.Day == new DateOnly(2026, 11, 12)).Value);
        Assert.Equal(0, requests.Single(p => p.Day == new DateOnly(2026, 11, 14)).Value);

        var usd = UsageView.Daily(buckets, UsageMetric.Cost, "USD", UsageRange.Days7, today);
        Assert.Equal(1.0, usd.Single(p => p.Day == new DateOnly(2026, 11, 12)).Value);
        Assert.Equal(0, usd.Single(p => p.Day == new DateOnly(2026, 11, 13)).Value);
        Assert.Equal(["CNY", "USD"], UsageView.Currencies(buckets));
        Assert.True(UsageView.HasUnpriced(buckets));

        Assert.Empty(UsageView.Daily([], UsageMetric.Tokens, null, UsageRange.All, today));
    }

    [Fact]
    public void Money_and_costs_keep_currencies_apart_and_unknown_is_null_not_zero()
    {
        Assert.Equal("$1.50", UsageView.Money("USD", 1_500_000));
        Assert.Equal("¥8.00", UsageView.Money("CNY", 8_000_000));
        Assert.Null(UsageView.Costs([]));
        Assert.Equal("¥8.00 · $1.50", UsageView.Costs([new UsageCostTotalDto("USD", 1_500_000), new UsageCostTotalDto("CNY", 8_000_000)]));
        Assert.Equal("1.2K", UsageView.Compact(1234));
        Assert.Null(UsageView.SuccessRate(0, 0));
    }
}

public sealed class UsagePageTests
{
    private static ManagementKit Seeded(string role = "viewer")
    {
        var kit = new ManagementKit(role);
        kit.Usage.Buckets.AddRange([FakeUsage.Bucket("2026-11-13"), FakeUsage.Bucket("2026-11-14", requests: 5)]);
        kit.Usage.Providers.Add(new UsageDimensionStatDto("anthropic", "Anthropic", 10, 9, 1000, 500, 0, 0, 0, 800, 300, 0, 0, [new UsageCostTotalDto("USD", 1_500_000)]));
        kit.Usage.Models.Add(new UsageDimensionStatDto("sonnet", "sonnet", 10, 9, 1000, 500, 0, 0, 0, 800, 300, 0, 0, []));
        return kit;
    }

    [Fact]
    public async Task A_viewer_can_read_summary_providers_models_and_series()
    {
        var kit = Seeded();
        await kit.Pages.Usage.ActivateAsync();
        var page = kit.Pages.Usage;
        await Task.Delay(30);

        Assert.Equal(PageState.Ready, page.State);
        Assert.Equal(10, page.Summary!.RequestCount);
        Assert.Equal("$1.50", page.TotalCost);
        Assert.Single(page.Providers);
        Assert.Equal("$1.50", page.Providers[0].Cost);
        Assert.Null(page.Models[0].Cost); // 查不到价格 = 未知,不是 $0
        Assert.Equal(30, page.Series.Count);
        Assert.True(page.IsReadOnly == false);
    }

    [Fact]
    public async Task Changing_the_range_reloads_and_a_late_response_for_the_old_range_is_dropped()
    {
        var kit = Seeded();
        var gate = new TaskCompletionSource();
        kit.Usage.Gate = w => w.From is null ? Task.CompletedTask : (kit.Usage.Calls.Count == 1 ? gate.Task : Task.CompletedTask);

        var first = kit.Pages.Usage.ActivateAsync();
        kit.Pages.Usage.Range = UsageRange.All; // 触发第二次加载
        await Task.Delay(30);
        gate.SetResult();
        await first;

        Assert.Equal(UsageRange.All, kit.Pages.Usage.Range);
        Assert.Null(kit.Usage.Calls.Last().Window.From);
        Assert.Equal(PageState.Ready, kit.Pages.Usage.State);
        Assert.Equal(PageState.Ready, kit.Pages.Usage.State);
    }

    [Fact]
    public async Task No_requests_is_an_empty_state_and_a_failure_is_an_error_state_and_offline_is_unknown()
    {
        var empty = Seeded();
        empty.Usage.Summary = empty.Usage.Summary with { RequestCount = 0 };
        await empty.Pages.Usage.ActivateAsync();
        Assert.True(empty.Pages.Usage.IsEmptyData);

        var failing = Seeded();
        failing.Usage.SummaryFailure = R.Fail<UsageSummaryDto>(ActionOutcome.Failed, "usage.invalid.window");
        await failing.Pages.Usage.ActivateAsync();
        Assert.Equal(PageState.Error, failing.Pages.Usage.State);

        var offline = Seeded();
        await offline.Pages.Usage.ActivateAsync();
        offline.Rig.SetConnection(DeviceConnection.Offline);
        Assert.Equal(PageState.Unknown, offline.Pages.Usage.State);
        Assert.Null(offline.Pages.Usage.Summary);
    }
}

public sealed class StoragePageTests
{
    private static async Task<ManagementKit> Open(string role = "owner", bool remote = false)
    {
        var kit = new ManagementKit(role);
        if (remote)
        {
            kit.Rig.MakeRemote();
        }

        await kit.Pages.Storage.ActivateAsync();
        return kit;
    }

    [Fact]
    public async Task Stats_and_backup_status_load_and_a_failure_is_an_error_state()
    {
        var kit = await Open();
        Assert.Equal(PageState.Ready, kit.Pages.Storage.State);
        Assert.Equal(42, kit.Pages.Storage.Stats!.ConversationCount);
        Assert.Equal("/Backups", kit.Pages.Storage.Backup!.Directory);

        var failing = new ManagementKit();
        failing.Storage.StatsFailure = R.Fail<StorageStatsDto>(ActionOutcome.Failed);
        await failing.Pages.Storage.ActivateAsync();
        Assert.Equal(PageState.Error, failing.Pages.Storage.State);
    }

    [Fact]
    public async Task Danger_operations_are_local_owner_only_and_a_remote_owner_can_still_do_the_safe_ones()
    {
        var remote = await Open(remote: true);
        Assert.Equal(GateReason.LocalOnly, remote.Pages.Storage.DangerGate.Reason);
        await remote.Pages.Storage.PreviewCleanupCommand.ExecuteAsync(DangerKind.CleanupHistory);
        Assert.DoesNotContain(remote.Storage.Calls, c => c.StartsWith("preview", StringComparison.Ordinal));
        Assert.Equal(DangerStage.Idle, remote.Pages.Storage.Stage);

        await remote.Pages.Storage.VacuumCommand.ExecuteAsync(null);
        await remote.Pages.Storage.CreateBackupCommand.ExecuteAsync(null);
        Assert.Contains("vacuum", remote.Storage.Calls);
        Assert.Contains("backup", remote.Storage.Calls);

        var member = await Open("member");
        Assert.Equal(GateReason.NeedsOwner, member.Pages.Storage.OwnerGate.Reason);
        await member.Pages.Storage.VacuumCommand.ExecuteAsync(null);
        Assert.Empty(member.Storage.Calls);
    }

    [Fact]
    public async Task Cleanup_by_age_previews_then_asks_for_confirmation_then_executes_with_the_token()
    {
        var kit = await Open();
        var page = kit.Pages.Storage;
        page.Age = "6";

        await page.PreviewCleanupCommand.ExecuteAsync(DangerKind.CleanupAge);
        Assert.Equal(DangerStage.Confirming, page.Stage);
        Assert.Equal(5, page.CleanupPreview!.SessionCount);
        Assert.Equal("preview:age:6", kit.Storage.Calls.Last());
        Assert.Empty(kit.Storage.Executed); // 预览不执行

        Assert.False(page.NeedsAcknowledge); // 按年龄清理只需要这次确认
        await page.ConfirmCommand.ExecuteAsync(null);
        var executed = Assert.Single(kit.Storage.Executed);
        Assert.Equal(("age", "6", "tok-1"), executed);
        Assert.Equal(DangerStage.Done, page.Stage);
        Assert.Equal(5, page.CleanupResult!.Deleted);

        page.DismissDoneCommand.Execute(null);
        Assert.Equal(DangerStage.Idle, page.Stage);
    }

    [Fact]
    public async Task Clearing_history_needs_the_acknowledgement_and_cancel_forgets_the_token()
    {
        var kit = await Open();
        var page = kit.Pages.Storage;
        await page.PreviewCleanupCommand.ExecuteAsync(DangerKind.CleanupHistory);
        Assert.True(page.NeedsAcknowledge);
        Assert.False(page.CanConfirm);
        await page.ConfirmCommand.ExecuteAsync(null);
        Assert.Empty(kit.Storage.Executed);

        page.Acknowledged = true;
        Assert.True(page.CanConfirm);
        page.CancelDangerCommand.Execute(null);
        Assert.Equal(DangerStage.Idle, page.Stage);
        Assert.False(page.Acknowledged);
        await page.ConfirmCommand.ExecuteAsync(null);
        Assert.Empty(kit.Storage.Executed);
    }

    [Fact]
    public async Task An_expired_confirmation_is_never_sent_and_asks_for_a_fresh_preview()
    {
        var kit = await Open();
        var page = kit.Pages.Storage;
        await page.PreviewCleanupCommand.ExecuteAsync(DangerKind.CleanupAge);
        kit.Clock.Now += StorageViewModel.TokenLifetime + TimeSpan.FromSeconds(1);

        await page.ConfirmCommand.ExecuteAsync(null);
        Assert.Empty(kit.Storage.Executed);
        Assert.Equal("storage.confirmationRequired", page.DangerFailure?.Key);
        Assert.Equal(DangerStage.Idle, page.Stage);
    }

    [Fact]
    public async Task A_busy_device_fails_the_execution_back_to_idle_with_the_key()
    {
        var kit = await Open();
        kit.Storage.ExecuteFailure = R.Fail<CleanupResultDto>(ActionOutcome.Unavailable, "storage.busy");
        await kit.Pages.Storage.PreviewCleanupCommand.ExecuteAsync(DangerKind.CleanupAge);
        await kit.Pages.Storage.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal("storage.busy", kit.Pages.Storage.DangerFailure?.Key);
        Assert.Equal(DangerStage.Idle, kit.Pages.Storage.Stage);
        await kit.Pages.Storage.ConfirmCommand.ExecuteAsync(null); // 令牌一次性,已经用掉
        Assert.Single(kit.Storage.Executed);
    }

    [Fact]
    public async Task Restore_and_import_follow_the_same_preview_confirm_flow_and_cancelling_discards_the_upload()
    {
        var kit = await Open();
        var page = kit.Pages.Storage;

        kit.Dialogs.NextFile = ScriptedDialogs.File("backup.bak", 10, "/Backups/x.bak");
        await page.PreviewCleanupCommand.ExecuteAsync(DangerKind.Restore);
        Assert.Equal(DangerStage.Confirming, page.Stage);
        Assert.Equal("/Backups/x.bak", page.RestorePreview!.Path);
        page.Acknowledged = true;
        await page.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(["/Backups/x.bak|tok-restore"], kit.Storage.Restored);
        page.DismissDoneCommand.Execute(null);

        kit.Dialogs.NextFile = ScriptedDialogs.File("data.json", 10);
        await page.PreviewCleanupCommand.ExecuteAsync(DangerKind.Import);
        Assert.Equal(2, page.ImportPreview!.OverwriteCount);
        page.CancelDangerCommand.Execute(null);
        Assert.Equal(["up-1"], kit.Storage.Discarded);
        Assert.Empty(kit.Storage.Applied);

        await page.PreviewCleanupCommand.ExecuteAsync(DangerKind.Import);
        page.Acknowledged = true;
        await page.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(["up-1|tok-import"], kit.Storage.Applied);
        Assert.Equal(8, page.ImportResult!.Imported);
    }

    [Fact]
    public async Task Choosing_no_file_cancels_quietly_and_export_writes_to_the_chosen_stream()
    {
        var kit = await Open();
        kit.Dialogs.NextFile = null;
        await kit.Pages.Storage.PreviewCleanupCommand.ExecuteAsync(DangerKind.Restore);
        Assert.Equal(DangerStage.Idle, kit.Pages.Storage.Stage);
        Assert.Null(kit.Pages.Storage.DangerFailure);

        kit.Dialogs.NextSave = new MemoryStream();
        await kit.Pages.Storage.ExportCommand.ExecuteAsync(null);
        Assert.True(kit.Pages.Storage.ExportDone);
        Assert.Contains("export", kit.Storage.Calls);
    }

    [Fact]
    public async Task Backup_directory_and_frequency_are_written_as_a_data_patch_by_the_owner()
    {
        var kit = new ManagementKit();
        kit.Settings.Push(FakeSettings.Defaults());
        await kit.Pages.Storage.ActivateAsync();
        kit.Pages.Storage.BackupDirectory = "/Volumes/Backups";
        kit.Pages.Storage.BackupFrequency = "daily";
        Assert.True(kit.Pages.Storage.BackupDirty);
        await kit.Pages.Storage.ApplyBackupSettingsCommand.ExecuteAsync(null);
        var data = kit.Settings.Patches.Single().Data!;
        Assert.Equal(("/Volumes/Backups", "daily"), (data.BackupDirectory, data.BackupFrequency));
    }
}

public sealed class GatewayPageTests
{
    private static async Task<ManagementKit> Open(string role = "owner", bool remote = false, bool enabled = true)
    {
        var kit = new ManagementKit(role);
        kit.Settings.Push(FakeSettings.Defaults());
        kit.Gateway.Status = new GatewayStatusDto(enabled, enabled ? 19836 : null, "127.0.0.1", false, false, [new GatewayHealthDto("anthropic", true, 1, 0, null, 220, null, 0)], null);
        if (remote)
        {
            kit.Rig.MakeRemote();
        }

        await kit.Pages.Gateway.ActivateAsync();
        return kit;
    }

    [Fact]
    public async Task Reading_the_gateway_needs_the_owner()
    {
        Assert.Equal(PageState.Forbidden, (await Open("member")).Pages.Gateway.State);
        Assert.Equal(PageState.Forbidden, (await Open("viewer")).Pages.Gateway.State);
        var owner = await Open();
        Assert.Equal(PageState.Ready, owner.Pages.Gateway.State);
        Assert.Equal(["http://127.0.0.1:19836"], owner.Pages.Gateway.Addresses);
        Assert.Equal("http://127.0.0.1:19836/api/openapi.json", owner.Pages.Gateway.OpenApiUrl);
        await owner.Pages.Gateway.OpenApiCommand.ExecuteAsync(null);
        Assert.Equal(["http://127.0.0.1:19836/api/openapi.json"], owner.Launcher.Opened);
    }

    [Fact]
    public async Task Keys_are_revealed_only_by_the_local_owner_and_cleared_when_hidden_or_the_page_is_left()
    {
        var kit = await Open();
        var key = kit.Pages.Gateway.Keys.Single(k => k.Role == "member");
        Assert.False(key.IsRevealed);
        await key.RevealCommand.ExecuteAsync(null);
        Assert.Equal("ncw_member_secret", key.Key);

        await kit.Pages.ActivateAsync(SettingsSection.Hooks); // 离开网关页
        Assert.False(key.IsRevealed);

        var remote = await Open(remote: true);
        Assert.Equal(GateReason.LocalOnly, remote.Pages.Gateway.LocalGate.Reason);
        await remote.Pages.Gateway.Keys[0].RevealCommand.ExecuteAsync(null);
        Assert.Empty(remote.Gateway.Revealed);
        Assert.False(remote.Pages.Gateway.Keys[0].IsRevealed);
    }

    [Fact]
    public async Task Rotating_asks_first_and_only_the_confirmed_rotation_replaces_the_key()
    {
        var kit = await Open();
        var key = kit.Pages.Gateway.Keys.Single(k => k.Role == "viewer");
        key.AskRotateCommand.Execute(null);
        Assert.True(kit.Pages.Gateway.HasPendingRotate);
        kit.Pages.Gateway.CancelRotateCommand.Execute(null);
        Assert.Empty(kit.Gateway.Rotated);

        key.AskRotateCommand.Execute(null);
        await kit.Pages.Gateway.ConfirmRotateCommand.ExecuteAsync(null);
        Assert.Equal(["viewer"], kit.Gateway.Rotated);
        Assert.Equal("ncw_viewer_rotated", key.Key);
    }

    [Fact]
    public async Task The_switch_is_local_owner_only_and_allowing_the_lan_needs_a_risk_confirmation_first()
    {
        var remote = await Open(remote: true, enabled: false);
        await remote.Pages.Gateway.SetEnabledCommand.ExecuteAsync(true);
        Assert.Empty(remote.Gateway.Enabled);

        var kit = await Open(enabled: false);
        await kit.Pages.Gateway.SetEnabledCommand.ExecuteAsync(true);
        Assert.Equal([true], kit.Gateway.Enabled);
        Assert.True(kit.Pages.Gateway.IsEnabled);

        await kit.Pages.Gateway.ToggleAllowRemoteCommand.ExecuteAsync(null);
        Assert.True(kit.Pages.Gateway.ConfirmingAllowRemote);
        Assert.Empty(kit.Settings.Patches); // 还没确认,没有写

        kit.Pages.Gateway.CancelAllowRemoteCommand.Execute(null);
        Assert.Empty(kit.Settings.Patches);

        await kit.Pages.Gateway.ToggleAllowRemoteCommand.ExecuteAsync(null);
        await kit.Pages.Gateway.ConfirmAllowRemoteCommand.ExecuteAsync(null);
        Assert.True(kit.Settings.Patches.Single().Gateway!.AllowRemote);
    }

    [Fact]
    public async Task A_disconnected_device_shows_unknown_and_drops_any_revealed_key()
    {
        var kit = await Open();
        await kit.Pages.Gateway.Keys[0].RevealCommand.ExecuteAsync(null);
        kit.Rig.SetConnection(DeviceConnection.Offline);
        Assert.Equal(PageState.Unknown, kit.Pages.Gateway.State);
        Assert.False(kit.Pages.Gateway.Keys[0].IsRevealed);
        Assert.Null(kit.Pages.Gateway.Status);
    }
}
