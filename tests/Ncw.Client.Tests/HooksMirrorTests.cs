using Ncw.Client.Actions;
using Ncw.Client.Devices;
using Ncw.Client.Stores;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Tests;

/// <summary>钩子清单镜像的折叠规则(规则 1 / 6 / 7):key 带 deviceId、乱序的旧响应不能盖掉新的、设备断开标未知、没取过和「取过、没有钩子」是两回事。</summary>
public sealed class HooksMirrorTests
{
    private const string A = "dev-a";
    private const string B = "dev-b";

    private static HookDto Hook(string id, bool enabled = true) =>
        new(id, HookNames.ScopeProject, "PreToolUse", HookNames.TypeCommand, "Bash", enabled, 10_000, null, "echo " + id, null, null, null, [], "/p/settings.local.json");

    private static HooksListResult List(params HookDto[] hooks) => new(hooks);

    [Fact]
    public void A_list_is_kept_per_device_and_per_project_and_an_unfetched_one_is_not_an_empty_one()
    {
        var m = HooksReducer.ApplyList(HooksMirror.Empty, A, "ws1", List(Hook("a")), 1);
        m = HooksReducer.ApplyList(m, A, null, List(), 2);
        m = HooksReducer.ApplyList(m, B, "ws1", List(Hook("b")), 3);

        Assert.Equal(["a"], m.Of(A, "ws1")!.Hooks.Select(h => h.Id));
        Assert.True(m.Of(A, null)!.Known);
        Assert.Empty(m.Of(A, null)!.Hooks);        // 取过、没有钩子
        Assert.Null(m.Of(A, "ws2"));                 // 没取过
        Assert.Equal(["b"], m.Of(B, "ws1")!.Hooks.Select(h => h.Id));
        Assert.Equivalent(new string?[] { "ws1", null }, m.WorkspacesOf(A));
    }

    [Fact]
    public void A_response_that_arrives_late_cannot_overwrite_a_newer_one_but_the_same_or_newer_replaces_it()
    {
        var m = HooksReducer.ApplyList(HooksMirror.Empty, A, "ws", List(Hook("new")), 5);

        var stale = HooksReducer.ApplyList(m, A, "ws", List(Hook("old")), 4);
        var newer = HooksReducer.ApplyList(m, A, "ws", List(Hook("newer"), Hook("x")), 6);

        Assert.Same(m, stale);
        Assert.Equal(["newer", "x"], newer.Of(A, "ws")!.Hooks.Select(h => h.Id));
    }

    [Fact]
    public void A_disconnect_marks_only_that_devices_lists_unknown_and_keeps_their_content()
    {
        var m = HooksReducer.ApplyList(HooksMirror.Empty, A, "ws", List(Hook("a")), 1);
        m = HooksReducer.ApplyList(m, B, "ws", List(Hook("b")), 2);

        var after = HooksReducer.MarkDeviceUnknown(m, A);

        Assert.False(after.Of(A, "ws")!.Known);
        Assert.Equal(["a"], after.Of(A, "ws")!.Hooks.Select(h => h.Id)); // 内容留着,但界面不把它当现状
        Assert.True(after.Of(B, "ws")!.Known);
        Assert.Same(after, HooksReducer.MarkDeviceUnknown(after, A));
        Assert.Same(after, HooksReducer.MarkDeviceUnknown(after, "nobody"));
    }

    [Fact]
    public void After_a_reconnect_the_next_fetch_makes_the_list_known_again_even_with_an_older_sequence_than_before()
    {
        var m = HooksReducer.ApplyList(HooksMirror.Empty, A, "ws", List(Hook("a")), 9);
        m = HooksReducer.MarkDeviceUnknown(m, A);

        // 未知的清单不挡新数据:重连后的第一份响应一定收
        m = HooksReducer.ApplyList(m, A, "ws", List(Hook("fresh")), 1);

        Assert.True(m.Of(A, "ws")!.Known);
        Assert.Equal(["fresh"], m.Of(A, "ws")!.Hooks.Select(h => h.Id));
    }

    [Fact]
    public void Diagnostics_are_fetched_separately_and_follow_the_same_ordering_rule()
    {
        var m = HooksReducer.ApplyList(HooksMirror.Empty, A, "ws", List(Hook("a")), 1);
        Assert.False(m.Of(A, "ws")!.HasDiagnostics);

        m = HooksReducer.ApplyDiagnostics(m, A, "ws", new HooksDiagnosticsResult([new HookDiagnosticDto("/p", "hooks.diagnostic.invalid", "x")]), 5);
        var stale = HooksReducer.ApplyDiagnostics(m, A, "ws", new HooksDiagnosticsResult([]), 4);

        Assert.Same(m, stale);
        Assert.Single(m.Of(A, "ws")!.Diagnostics);
        Assert.Equal(["a"], m.Of(A, "ws")!.Hooks.Select(h => h.Id)); // 诊断不动清单
    }

    [Fact]
    public async Task Without_a_connected_device_every_action_reports_unavailable_and_touches_nothing()
    {
        var store = new Store<HooksMirror>(HooksMirror.Empty);
        var actions = new HooksActions(new DeviceRegistry(), store);
        var draft = new HookUpsertDto(null, "Stop", HookNames.TypeCommand, null, true, 60_000, null, "echo", null, null, null, null);

        Assert.Equal(ActionOutcome.Unavailable, (await actions.ListAsync("dev", "ws")).Outcome);
        Assert.Equal(ActionOutcome.Unavailable, (await actions.DiagnosticsAsync("dev")).Outcome);
        Assert.Equal(ActionOutcome.Unavailable, (await actions.SaveAsync("dev", HookNames.ScopeGlobal, null, draft)).Outcome);
        Assert.Equal(ActionOutcome.Unavailable, (await actions.DeleteAsync("dev", HookNames.ScopeGlobal, null, "h")).Outcome);
        Assert.Equal(ActionOutcome.Unavailable, (await actions.SetEnabledAsync("dev", HookNames.ScopeGlobal, null, "h", false)).Outcome);
        Assert.Equal(ActionOutcome.Unavailable, (await actions.TestAsync("dev", HookNames.ScopeGlobal, null, draft)).Outcome);
        await actions.RefreshHeldAsync("dev");
        Assert.Same(HooksMirror.Empty, store.State);
    }
}
