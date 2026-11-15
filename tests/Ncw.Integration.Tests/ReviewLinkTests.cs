using Ncw.Client.Actions;
using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Integration.Tests.Support;
using Ncw.Protocol.Methods;

using static Ncw.Integration.Tests.Support.OrchestrationRig;

namespace Ncw.Integration.Tests;

/// <summary>
/// 改动审查在控制端的端到端:真实 Agent + 真实 <c>DeviceLink</c> / Store / <see cref="ReviewActions"/> + 假上游 + 真实 Write 工具。
/// Run 收口后连接层自动取变更集进 Store;撤销 / 重做经 Actions、Store 里的撤销态跟着设备走(旧响应不改回去);设备断开是未知而不是结束。
/// </summary>
public sealed class ReviewLinkTests : IAsyncLifetime
{
    private static readonly SendOptionsDto Full = new("sonnet", PermissionMode: "full");

    private readonly OrchestrationRig _rig = new();

    public ValueTask InitializeAsync() => _rig.InitializeAsync();

    public ValueTask DisposeAsync() => _rig.DisposeAsync();

    private static ReviewMirror Mirror(Controller c)
    {
        lock (c.Ui.Gate)
        {
            return c.Stores.Review.State;
        }
    }

    private static string DeviceOf(Controller c) => c.Workspace.Devices.Keys.Single();

    private async Task<(ReviewActions Review, string DeviceId, string RunId, string ProjectPath)> RunAndWaitAsync(Controller c)
    {
        var project = await c.Link.AddProjectAsync(_rig.Workspace);
        var path = (await c.Link.CallAsync(DeviceMethods.ProjectsList, Unit.Value)).Projects.Single().Path;
        var session = (await c.Conversations.CreateSessionAsync(project, "sonnet"))!.Value;
        File.WriteAllText(Path.Combine(path, "existing.txt"), "v0\n");
        _rig.Llm.On(session.SessionId,
            S.Call("r1", "Read", new System.Text.Json.Nodes.JsonObject { ["file_path"] = Path.Combine(path, "existing.txt") }.ToJsonString()),
            S.Call("w1", "Write", new System.Text.Json.Nodes.JsonObject { ["file_path"] = Path.Combine(path, "existing.txt"), ["content"] = "v1\n" }.ToJsonString()),
            S.Call("w2", "Write", new System.Text.Json.Nodes.JsonObject { ["file_path"] = Path.Combine(path, "new.txt"), ["content"] = "hello\n" }.ToJsonString()),
            S.Say("done"));

        var sent = await c.Conversations.SendAsync(session, "write files", Full);
        Assert.Equal(SendOutcome.Started, sent.Outcome);
        var run = sent.Run!.Value;
        await WaitUntil(() => c.State.Runs[run] is { Phase: RunPhase.Done, Settled: true }, "the run to finish");
        return (new ReviewActions(c.Devices, c.Stores), DeviceOf(c), run.RunId, path);
    }

    [Fact]
    public async Task A_settled_run_puts_its_change_set_into_the_store_and_undo_and_redo_follow_the_devices_state()
    {
        var c = await _rig.StartControllerAsync("ctrl-a");
        var (review, deviceId, runId, path) = await RunAndWaitAsync(c);

        // 没有人调任何 Action:run_settled 之后连接层自己取
        await WaitUntil(() => Mirror(c).ChangeSetOf(deviceId, runId) is not null, "the change set to appear in the store");
        var set = Mirror(c).ChangeSetOf(deviceId, runId)!;
        Assert.Equal((2, ReviewStates.Applied), (set.FileCount, set.State));
        Assert.True(Mirror(c).Of(deviceId, runId)!.Known);

        var diff = await review.FileDiffAsync(deviceId, runId, "existing.txt");
        Assert.Equal(("v0\n", "v1\n"), (diff.Value!.Before, diff.Value.After));
        Assert.Empty((await review.PrecheckUndoAsync(deviceId, runId)).Value!);

        var undone = await review.UndoAsync(deviceId, runId);
        Assert.True(undone.IsOk);
        Assert.Equal(ReviewStates.Reverted, undone.Value!.State);
        await WaitUntil(() => Mirror(c).ChangeSetOf(deviceId, runId)!.State == ReviewStates.Reverted, "the store to show the reverted state");
        Assert.Equal("v0\n", File.ReadAllText(Path.Combine(path, "existing.txt")));
        Assert.False(File.Exists(Path.Combine(path, "new.txt")));

        var redone = await review.RedoAsync(deviceId, runId);
        Assert.Equal(ReviewStates.Applied, redone.Value!.State);
        await WaitUntil(() => Mirror(c).ChangeSetOf(deviceId, runId)!.State == ReviewStates.Applied, "the store to show the applied state again");
        Assert.Equal("hello\n", File.ReadAllText(Path.Combine(path, "new.txt")));

        // 会话里改过文件的各轮
        var listed = (await review.ListAsync(deviceId, set.SessionId)).Value!;
        Assert.Equal(runId, Assert.Single(listed).RunId);
    }

    [Fact]
    public async Task A_hand_edit_makes_precheck_report_a_conflict_and_a_missing_run_is_not_an_error()
    {
        var c = await _rig.StartControllerAsync("ctrl-a");
        var (review, deviceId, runId, path) = await RunAndWaitAsync(c);
        File.WriteAllText(Path.Combine(path, "existing.txt"), "mine\n");

        Assert.Equal(["existing.txt"], (await review.PrecheckUndoAsync(deviceId, runId)).Value!);
        var plain = await review.UndoAsync(deviceId, runId);
        Assert.Contains(plain.Value!.Files, f => f.Path == "existing.txt" && f.Status == ReviewFileStatuses.Conflict);
        Assert.Equal("mine\n", File.ReadAllText(Path.Combine(path, "existing.txt")));

        var none = await review.LoadChangeSetAsync(deviceId, "no-such-run");
        Assert.True(none.IsOk);
        Assert.Null(none.Value);
        Assert.True(Mirror(c).Of(deviceId, "no-such-run")!.Known); // 问过了:这一轮没改过文件
    }

    [Fact]
    public async Task When_the_device_disconnects_the_mirrored_change_set_becomes_unknown_and_is_confirmed_again_after_reconnecting()
    {
        var c = await _rig.StartControllerAsync("ctrl-a");
        var (review, deviceId, runId, _) = await RunAndWaitAsync(c);
        await WaitUntil(() => Mirror(c).ChangeSetOf(deviceId, runId) is not null, "the change set to appear in the store");

        c.Current!.Cut();

        await WaitUntil(() => Mirror(c).Of(deviceId, runId) is { Known: false }, "the change set to become unknown");
        Assert.NotNull(Mirror(c).ChangeSetOf(deviceId, runId)); // 内容保留,但不当现状

        await WaitUntil(() => c.Workspace.Devices.Values.Any(d => d.Connection == DeviceConnection.Connected), "the link to reconnect");
        Assert.True((await review.LoadChangeSetAsync(deviceId, runId)).IsOk);
        Assert.True(Mirror(c).Of(deviceId, runId)!.Known);
    }
}
