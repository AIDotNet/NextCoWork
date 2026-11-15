using System.Collections.Immutable;
using Ncw.Client.Accounts;
using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Core.Layout;

namespace Ncw.Client.Tests.Accounts;

/// <summary>账户配置同步:只有本地快照和关闭的实现(后端 B10 到位前不同步任何东西)。</summary>
public class ConfigSyncTests
{
    [Fact]
    public void A_layout_survives_a_capture_and_apply_round_trip()
    {
        var project = new ProjectRef("dev-1", "ws1");
        var layout = new LayoutState(RailLayout.Empty, ImmutableDictionary<ProjectRef, ProjectAppearance>.Empty.Add(project, new ProjectAppearance("N", "#3366ff")));

        var snapshot = ConfigSnapshots.Capture(layout, rev: 7);
        Assert.Equal(7, snapshot.Rev);
        Assert.Equal(layout, ConfigSnapshots.Apply(snapshot));
        Assert.Equal(LayoutState.Empty, ConfigSnapshots.Apply(ConfigSnapshot.Empty));
    }

    [Fact]
    public async Task The_disabled_sync_reports_why_and_never_pretends_to_have_synced()
    {
        IConfigSync sync = DisabledConfigSync.Instance;
        Assert.False(sync.IsAvailable);
        Assert.Equal(DisabledConfigSync.BackendMissing, sync.UnavailableReason);

        var push = await sync.PushAsync(ConfigSnapshot.Empty);
        Assert.False(push.Ok);
        Assert.Equal(DisabledConfigSync.BackendMissing, push.ErrorKey);
        var pull = await sync.PullAsync();
        Assert.False(pull.Ok);
        Assert.Null(pull.Value);
    }
}
