using Ncw.Client.Actions;
using Ncw.Client.Devices;
using Ncw.Client.Services;
using Ncw.Client.ViewModels;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Tests.Management;

/// <summary>设置 · 数据 · 回退到 2.x(docs/design/04 §5)的控制端:入口什么时候出现、确认、失败时说明原因,以及接管页的「已回退」。</summary>
public sealed class RollbackToLegacyTests
{
    private static async Task<ManagementKit> Open(Action<ManagementKit>? arrange = null, bool remote = false, string role = "owner")
    {
        var kit = new ManagementKit(role);
        arrange?.Invoke(kit);
        if (remote)
        {
            kit.Rig.MakeRemote();
        }

        await kit.Pages.Storage.ActivateAsync();
        return kit;
    }

    [Fact]
    public async Task The_entry_appears_only_for_data_moved_from_2x_on_this_device_for_its_owner()
    {
        var page = (await Open()).Pages.Storage;
        Assert.True(page.ShowsRollback);
        Assert.False(page.CanRollBack); // 没勾选「我已了解」

        Assert.False((await Open(k => k.Rollback.Info = new DataRollbackInfo(false, null, false))).Pages.Storage.ShowsRollback); // 全新的 3.0 数据

        var remote = await Open(remote: true);
        Assert.False(remote.Pages.Storage.ShowsRollback); // 远程设备:不问,也不显示
        Assert.DoesNotContain("info", remote.Rollback.Calls);

        Assert.False((await Open(role: "member")).Pages.Storage.ShowsRollback);
    }

    [Fact]
    public async Task Going_back_needs_the_acknowledgement_and_then_hands_over_to_the_action()
    {
        var kit = await Open();
        var page = kit.Pages.Storage;

        await page.RollBackToLegacyCommand.ExecuteAsync(null);
        Assert.DoesNotContain("rollback", kit.Rollback.Calls); // 没确认就不做

        page.RollbackAcknowledged = true;
        Assert.True(page.CanRollBack);
        await page.RollBackToLegacyCommand.ExecuteAsync(null);

        Assert.Contains("rollback", kit.Rollback.Calls);
        Assert.True(page.Rollback!.Pending);
        Assert.Null(page.RollbackFailure);
    }

    [Fact]
    public async Task A_refused_request_is_explained_and_a_pending_one_can_be_withdrawn()
    {
        var kit = await Open(k => k.Rollback.RequestFailure = R.Fail<DataRollbackInfo>(ActionOutcome.Invalid, "data.rollback.unavailable"));
        var page = kit.Pages.Storage;
        page.RollbackAcknowledged = true;

        await page.RollBackToLegacyCommand.ExecuteAsync(null);
        Assert.Equal("data.rollback.unavailable", page.RollbackFailure?.Key);
        Assert.False(page.RollingBack);

        kit.Rollback.RequestFailure = null;
        await page.RollBackToLegacyCommand.ExecuteAsync(null);
        Assert.True(page.Rollback!.Pending);
        await page.CancelRollbackCommand.ExecuteAsync(null);
        Assert.False(page.Rollback!.Pending);
        Assert.False(page.RollbackAcknowledged);
    }

    [Fact]
    public async Task The_action_requests_first_then_stops_the_local_agent_then_exits_and_does_neither_when_the_request_fails()
    {
        var steps = new List<string>();
        var host = new RecordingHost(steps);
        var notConnected = new DataActions(new DeviceRegistry(), () => host, () => () => steps.Add("exit"));

        var result = await notConnected.RollBackToLegacyAsync();

        Assert.False(result.IsOk); // 本机设备没连上:请求发不出去
        Assert.Empty(steps); // 什么都没停、没退出——用户还在 3.0 里,数据也没动
    }

    private sealed class RecordingHost(List<string> steps) : IStartupHost
    {
        public void Retry()
        {
        }

        public string? Diagnostics() => null;

        public Task StopLocalAgentAsync()
        {
            steps.Add("stop");
            return Task.CompletedTask;
        }
    }
}
