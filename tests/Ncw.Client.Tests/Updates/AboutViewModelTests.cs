using System.Collections.Immutable;
using Ncw.Client.Actions;
using Ncw.Client.Stores;
using Ncw.Client.Updates;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;

namespace Ncw.Client.Tests.Updates;

/// <summary>「关于」页的 ViewModel:只读 Store、只调 Actions;约束强度由 Core 的纯函数按时钟派生。</summary>
public class AboutViewModelTests
{
    private static async Task<(UpdateRig Rig, AboutViewModel About)> Make(string current = "3.0.0", bool installed = true)
    {
        var rig = new UpdateRig(current: current, installed: installed);
        await rig.Service.InitializeAsync();
        return (rig, new AboutViewModel(rig.Stores, new UpdateActions(rig.Service), rig.Clock));
    }

    [Fact]
    public async Task Shows_the_running_version_and_channel_and_the_check_button()
    {
        var (_, about) = await Make();
        Assert.Equal("3.0.0", about.CurrentVersion);
        Assert.Equal(UpdateChannel.Stable, about.Channel);
        Assert.True(about.IsIdle);
        Assert.True(about.CanCheck);
        Assert.False(about.CanDownload);
        Assert.False(about.CanRestart);
        Assert.False(about.HasLastChecked);
        Assert.True(about.CheckCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_build_that_cannot_update_itself_is_disabled_and_nothing_can_be_clicked()
    {
        var (_, about) = await Make(installed: false);
        Assert.True(about.IsDisabled);
        Assert.False(about.CanCheck);
        Assert.False(about.CheckCommand.CanExecute(null));
        Assert.False(about.CanChangeChannel);
    }

    [Fact]
    public async Task The_whole_happy_path_check_download_restart_is_driven_by_the_store_and_the_commands()
    {
        var (rig, about) = await Make();
        rig.Engine.Offering(UpdateKit.Manifest("3.0.1", notes: "## Fixes\n- one"));

        await about.CheckCommand.ExecuteAsync(null);
        Assert.Equal("3.0.1", about.NewVersion);
        Assert.True(about.CanDownload);
        Assert.True(about.HasNotes);
        Assert.Equal(UpdateRig.Start, about.LastCheckedAt);
        Assert.Equal(UpdateEnforcement.None, about.Enforcement);

        await about.DownloadCommand.ExecuteAsync(null);
        Assert.True(about.IsReady);
        Assert.Equal(100, about.Progress);
        Assert.True(about.CanRestart);
        Assert.False(about.CanDownload);

        await about.RestartCommand.ExecuteAsync(null);
        Assert.True(about.IsInstalling);
        Assert.False(about.CanRestart);
        Assert.Equal(["prepare", "exit"], rig.Journal);
    }

    [Fact]
    public async Task Property_changes_are_raised_when_the_store_changes()
    {
        var (rig, about) = await Make();
        var changed = new List<string?>();
        about.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        rig.Engine.Offering(UpdateKit.Manifest("3.0.1"));

        await rig.Service.CheckAsync();

        Assert.Contains(nameof(AboutViewModel.NewVersion), changed);
        Assert.Contains(nameof(AboutViewModel.CanDownload), changed);
        Assert.Contains(nameof(AboutViewModel.Phase), changed);
    }

    [Fact]
    public async Task A_mandatory_update_inside_the_grace_period_is_required_with_a_deadline_and_blocks_after_it()
    {
        var (rig, about) = await Make();
        var grace = UpdateRig.Start.AddHours(2);
        rig.Engine.Offering(UpdateKit.Manifest("3.0.1", mandatory: true, minimum: "3.0.1", grace: grace));

        await about.CheckCommand.ExecuteAsync(null);
        Assert.True(about.IsRequired);
        Assert.False(about.IsBlocking);
        Assert.True(about.InGracePeriod);
        Assert.Equal(grace, about.GraceUntil);

        rig.Clock.Advance(TimeSpan.FromHours(2.1)); // 宽限期过了:服务给状态一个刻度,VM 重新按时钟派生
        Assert.True(about.IsBlocking);
        Assert.False(about.IsRequired);
        Assert.False(about.InGracePeriod);
        Assert.Null(about.GraceUntil);
    }

    [Fact]
    public async Task A_mandatory_update_without_a_deadline_blocks_immediately_when_the_version_is_below_the_minimum()
    {
        var (rig, about) = await Make("3.0.0");
        rig.Engine.Offering(UpdateKit.Manifest("3.0.5", mandatory: true, minimum: "3.0.5"));
        await about.CheckCommand.ExecuteAsync(null);
        Assert.True(about.IsBlocking);
        Assert.Equal(UpdateEnforcement.Blocking, about.Enforcement);
    }

    [Fact]
    public async Task An_optional_update_never_requires_or_blocks()
    {
        var (rig, about) = await Make();
        rig.Engine.Offering(UpdateKit.Manifest("9.0.0", mandatory: false, minimum: "9.0.0"));
        await about.CheckCommand.ExecuteAsync(null);
        Assert.False(about.IsRequired);
        Assert.False(about.IsBlocking);
    }

    [Theory]
    [InlineData(UpdateFailureKind.SignatureInvalid, true)]
    [InlineData(UpdateFailureKind.SignatureMissing, true)]
    [InlineData(UpdateFailureKind.UnknownSigningKey, true)]
    [InlineData(UpdateFailureKind.ManifestUnsigned, true)]
    [InlineData(UpdateFailureKind.ManifestRejected, true)]
    [InlineData(UpdateFailureKind.NoTrustedKeys, true)]
    [InlineData(UpdateFailureKind.ChecksumMismatch, true)]
    [InlineData(UpdateFailureKind.Network, false)]
    [InlineData(UpdateFailureKind.FeedInvalid, false)]
    [InlineData(UpdateFailureKind.DownloadFailed, false)]
    [InlineData(UpdateFailureKind.ApplyFailed, false)]
    public async Task Signature_and_manifest_failures_are_trust_failures_and_the_rest_are_plain_failures(UpdateFailureKind kind, bool trust)
    {
        var (rig, about) = await Make();
        rig.Engine.CheckFailure = new UpdateException(kind, "x");
        await about.CheckCommand.ExecuteAsync(null);

        Assert.True(about.HasFailure);
        Assert.Equal(kind, about.Failure);
        Assert.Equal(trust, about.IsTrustFailure);
        Assert.Equal(!trust, about.ShowsPlainFailure);
        Assert.True(about.CanCheck); // 失败后可以重试
    }

    [Fact]
    public async Task The_restart_warning_counts_only_running_tasks_on_the_connected_local_device()
    {
        var (rig, about) = await Make();
        rig.Engine.Offering(UpdateKit.Manifest("3.0.1"));
        await about.CheckCommand.ExecuteAsync(null);
        await about.DownloadCommand.ExecuteAsync(null);
        Assert.False(about.ShowsRunWarning);

        ActiveRunIndex Index(string device, int runs) => new(device, [.. Enumerable.Range(0, runs).Select(i => new ActiveRun(device, "r" + i, "s" + i, "w", 0))]);
        rig.Stores.Workspace.Update(w => w with
        {
            Devices = w.Devices
                .SetItem("local", new DeviceInfo("local", "Local", DeviceKind.Local, DeviceConnection.Connected, Index("local", 2)))
                .SetItem("remote", new DeviceInfo("remote", "Remote", DeviceKind.Remote, DeviceConnection.Connected, Index("remote", 5))),
        });

        Assert.Equal(2, about.ActiveRuns);
        Assert.True(about.ShowsRunWarning);

        // 设备断开:状态未知,不数(也不当作「没有任务」去掉提示之外的东西:这里只关心不把别的设备算进来)
        rig.Stores.Workspace.Update(w => w with { Devices = w.Devices.SetItem("local", w.Devices["local"] with { Connection = DeviceConnection.Offline }) });
        Assert.Equal(0, about.ActiveRuns);
    }

    [Fact]
    public async Task Picking_a_channel_goes_through_the_action_and_the_picker_reflects_the_store()
    {
        var (rig, about) = await Make();
        rig.Engine.Offering(UpdateKit.Manifest("3.0.0"), offer: false);

        about.SelectedChannel = UpdateChannel.Beta;
        await WaitUntil(() => about.Channel == UpdateChannel.Beta);

        Assert.Equal(UpdateChannel.Beta, about.SelectedChannel);
        Assert.Equal([UpdateChannel.Beta], rig.Engine.Checks);
        Assert.True(about.IsBeta);
    }

    [Fact]
    public async Task Picking_a_channel_is_refused_while_a_download_is_ready_and_the_picker_snaps_back()
    {
        var (rig, about) = await Make();
        rig.Engine.Offering(UpdateKit.Manifest("3.0.1"));
        await about.CheckCommand.ExecuteAsync(null);
        await about.DownloadCommand.ExecuteAsync(null);
        Assert.False(about.CanChangeChannel);

        about.SelectedChannel = UpdateChannel.Beta;

        Assert.Equal(UpdateChannel.Stable, about.SelectedChannel);
        Assert.Equal(UpdateChannel.Stable, rig.State.Channel);
    }

    [Fact]
    public void Without_an_update_service_the_page_still_works_and_nothing_is_clickable()
    {
        var stores = new ClientStores();
        var about = new AboutViewModel(stores, new UpdateActions(null));
        Assert.False(about.IsAvailable);
        Assert.False(about.CanCheck);
        Assert.False(about.CheckCommand.CanExecute(null));
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
