using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Extensions;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Runs;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Llm;
using Ncw.Agent.Runs.Tests.Support;
using Ncw.Agent.Storage.Sessions;

namespace Ncw.Agent.Runs.Tests;

/// <summary>
/// 排队消息(05 §3):入队 / 插话 / 编辑 / 删除 / 撤回、续跑时机、与 Storage 的 <c>SessionInputStore</c> 对接、重启恢复、写盘失败的回滚、消费回执。
/// 移植自 2.x <c>session-runtime.test.ts</c> 的「排队续跑 / 插话 / 草稿与队列共用一个键(队列半边)/ 队列写盘失败 / 续跑写盘失败 / 已提交输入的消费回执 / 广播失败」各组。
/// 草稿相关的组(防抖、flush、草稿写盘失败)不移植:3.0 里草稿是客户端本地状态,kv 里这个字段只原样保留。
/// </summary>
public sealed class QueueTests
{
    private static Rig EchoRig(out Func<EchoDriver> driver, Func<RunHandle, bool>? takesSteering = null, Func<RunHandle, Task>? during = null, Func<RunHandle, RunStatus>? status = null, Action<RigOptions>? more = null)
    {
        EchoDriver? echo = null;
        var rig = new Rig(o =>
        {
            o.Driver = r => echo = new EchoDriver(new RepoAccess(r.Repo), takesSteering, during, status);
            more?.Invoke(o);
        });
        rig.NewSessionRow();
        driver = () => echo!;
        return rig;
    }

    private static Task<SendResult> Send(Rig rig, string run, string message, string text, SendOptions? options = null, string session = "s1") =>
        rig.Registry.SendAsync(new UserSend(new RunId(run), session, Rig.Workspace, new MessageId(message), [new TextPart(text)], options ?? Opts.Default));

    private static IReadOnlyList<Part> Text(string text) => [new TextPart(text)];

    private static List<QueueItem> Items(Rig rig, string session = "s1") => [.. rig.Registry.QueueOf(session).Items];

    private static string TextOf(QueueItem item) => string.Concat(item.Parts.OfType<TextPart>().Select(p => p.Text));

    private static async Task<RunHandle> StartHeld(Rig rig, TaskCompletionSource gate, string run = "r1")
    {
        var result = await Send(rig, run, "u-" + run, "first");
        var handle = Assert.IsType<SendResult.StartedRun>(result).Handle;
        await Wait.UntilAsync(() => handle.HasStarted);
        return handle;
    }

    private static Ext BlockOnRunEnd(TaskCompletionSource entered, Task release) => new("block", 0, r =>
        r.On(LoopExtensionPoints.RunEnd, async (_, _, _) =>
        {
            entered.TrySetResult();
            await release;
        }));

    // ── 排队续跑 ──

    [Fact]
    public async Task Sending_while_a_run_is_active_queues_the_message_saves_it_at_once_and_starts_no_second_run()
    {
        var gate = new TaskCompletionSource();
        using var rig = EchoRig(out _, during: _ => gate.Task);
        var run = await StartHeld(rig, gate);

        var result = await Send(rig, "r2", "m2", "second");
        var queued = Assert.IsType<SendResult.Queued>(result);
        Assert.True(queued.Result.Accepted);
        Assert.Equal(["m2"], queued.Result.Update.Items.Select(i => i.Id.Value));
        Assert.Equal(QueueState.Pending, queued.Result.Update.Items[0].State);
        Assert.Equal([run.RunId], rig.Registry.ActiveRunIds());

        // 立刻落盘(kv 里每会话一行,2.x 的 session.input.<id>)。
        var saved = rig.Inputs.Read("s1")!;
        var item = Assert.Single(saved.Queued!);
        Assert.Equal(("m2", "second", "pending"), (item.Id, item.Text, item.Status));
        Assert.Equal("claude-sonnet-4", item.Options!["model"]!.GetValue<string>());
        Assert.Equal(1, rig.QueueUpdates.Count(u => u.Items.Count == 1));
        gate.SetResult();
        await Rig.Settled(run);
    }

    [Fact]
    public async Task The_follow_up_starts_only_after_run_settled_not_at_run_end()
    {
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var gate = new TaskCompletionSource();
        using var rig = EchoRig(out var driver, during: h => h.RunId.Value == "r1" ? gate.Task : Task.CompletedTask,
            more: o => o.SessionExtensions = [BlockOnRunEnd(entered, release.Task)]);
        // 这里的 EchoDriver 不经过会话层,run_end 扩展点不会触发:用上游真会话测这一条见 Settle_window 测试;此处只验证顺序用的假驱动。
        var run = await StartHeld(rig, gate);
        await Send(rig, "r2", "m2", "second");
        gate.SetResult();
        await Wait.UntilAsync(() => driver().Inputs.Count >= 2);
        await rig.IdleAsync("s1");

        Assert.Equal(["r1:first", $"{rig.Factory.Created.Last(t => t.Origin == RunOrigin.Internal).RunId}:second"], driver().Inputs);
        Assert.Empty(Items(rig));
        Assert.Equal(["first", "second"], rig.Stored("s1").Select(m => m.Text));
        var followUp = rig.Factory.Created.Single(t => t.Origin == RunOrigin.Internal);
        Assert.Equal("m2", followUp.Input[0].Id.Value); // 单条的批沿用条目 id
        _ = run;
    }

    [Fact]
    public async Task In_the_window_between_run_end_and_run_settled_the_queue_does_not_start_anything()
    {
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        using var rig = new Rig(o => o.SessionExtensions = [BlockOnRunEnd(entered, release.Task)]);
        rig.NewSessionRow();
        var run = rig.Start("s1", "first", runId: "r1");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(RunState.Done, run.State);

        // run_end 已经发了,会话还被占着:此刻排队 = 进队列,不能起新 Run。
        var result = await Send(rig, "r2", "m2", "second");
        var queued = Assert.IsType<SendResult.Refused>(result); // 没有运行中的顶层 Run、会话却忙:用户发送被拒(不是排队)
        Assert.Equal(RunRefusal.SessionBusy, queued.Reason);
        var direct = await rig.Registry.EnqueueAsync("s1", Rig.Workspace, new MessageId("m2"), Text("second"), Opts.Default);
        Assert.True(direct.Accepted);
        Assert.Empty(rig.Registry.ActiveRunIds()); // 没有任何新 Run
        Assert.DoesNotContain(rig.Factory.Created, t => t.Origin == RunOrigin.Internal);

        release.SetResult();
        await Rig.Settled(run);
        await Wait.UntilAsync(() => rig.Factory.Created.Any(t => t.Origin == RunOrigin.Internal), what: "follow-up");
        await Wait.UntilAsync(() => rig.Stored("s1").Any(m => m.Text == "second"));
        Assert.Equal("m2", rig.Stored("s1").Single(m => m.Text == "second").Id);
    }

    [Fact]
    public async Task Without_any_subscription_the_queue_still_carries_on_this_is_why_it_lives_in_the_agent()
    {
        var gate = new TaskCompletionSource();
        using var rig = EchoRig(out var driver, during: h => h.RunId.Value == "r1" ? gate.Task : Task.CompletedTask);
        var run = await StartHeld(rig, gate);
        await Send(rig, "r2", "m2", "second");
        await Send(rig, "r3", "m3", "third");
        Assert.Equal(0, run.SubscriberCount);
        gate.SetResult();
        await Wait.UntilAsync(() => driver().Inputs.Count >= 2);
        await Wait.UntilAsync(() => !rig.Registry.IsSessionBusy("s1") && Items(rig).Count == 1 || driver().Inputs.Count >= 3, what: "second run");
        // 非插话的排队项一次只续一条(2.x 的 FIFO 兼容承诺),接着第二轮结束后再续下一条。
        await Wait.UntilAsync(() => driver().Inputs.Count >= 3);
        await rig.IdleAsync("s1");
        Assert.Equal(["first", "second", "third"], rig.Stored("s1").Select(m => m.Text));
    }

    [Fact]
    public async Task The_follow_up_reuses_the_options_frozen_at_queue_time_not_the_latest_ones()
    {
        var gate = new TaskCompletionSource();
        using var rig = EchoRig(out var driver, during: h => h.RunId.Value == "r1" ? gate.Task : Task.CompletedTask);
        var run = await StartHeld(rig, gate);
        var frozen = Opts.Default with { Thinking = ThinkingLevel.High, PermissionMode = "acceptEdits", MaxContext = true };
        await Send(rig, "r2", "m2", "second", frozen);
        gate.SetResult();
        await Wait.UntilAsync(() => driver().Inputs.Count >= 2);
        var template = rig.Factory.Created.Single(t => t.Origin == RunOrigin.Internal);
        Assert.Equal(frozen, template.Options);
        await rig.IdleAsync("s1");
        _ = run;
    }

    [Fact]
    public async Task Attachments_come_back_in_the_follow_up_input_not_just_the_text()
    {
        var gate = new TaskCompletionSource();
        using var rig = EchoRig(out var driver, during: h => h.RunId.Value == "r1" ? gate.Task : Task.CompletedTask);
        var run = await StartHeld(rig, gate);
        var parts = new Part[] { new TextPart("look"), new ImagePart("image/jpeg", "ncw://attachments/a.jpg"), new FileRefPart("notes/n.md", "n.md") };
        await rig.Registry.EnqueueAsync("s1", Rig.Workspace, new MessageId("m2"), parts, Opts.Default);
        gate.SetResult();
        await Wait.UntilAsync(() => driver().Inputs.Count >= 2);
        var input = rig.Factory.Created.Single(t => t.Origin == RunOrigin.Internal).Input[0];
        Assert.Contains(input.Parts, p => p is ImagePart { DataRef: "ncw://attachments/a.jpg", Mime: "image/jpeg" });
        Assert.Contains(input.Parts, p => p is FileRefPart { Path: "notes/n.md" });
        Assert.Equal("look", ((TextPart)input.Parts[0]).Text);
        await rig.IdleAsync("s1");
        _ = run;
    }

    [Fact]
    public async Task Stop_does_not_continue_the_queue_and_neither_does_an_error_but_a_manual_resume_does()
    {
        foreach (var status in new[] { RunStatus.Aborted, RunStatus.Error })
        {
            var gate = new TaskCompletionSource();
            var aborted = status == RunStatus.Aborted;
            using var rig = EchoRig(out var driver,
                during: h => h.RunId.Value == "r1" ? (aborted ? Task.Delay(Timeout.Infinite, h.Token) : gate.Task) : Task.CompletedTask,
                status: h => h.RunId.Value == "r1" ? status : RunStatus.Done);
            var run = await StartHeld(rig, gate);
            await Send(rig, "r2", "m2", "second");
            if (aborted)
            {
                rig.Registry.Abort(run.RunId);
            }
            else
            {
                gate.SetResult();
            }

            await Rig.Settled(run);
            Assert.Equal(aborted ? RunState.Aborted : RunState.Error, run.State);
            await Task.Delay(150);
            Assert.Single(driver().Inputs); // 没有续跑
            Assert.Equal(["m2"], Items(rig).Select(i => i.Id.Value)); // 原样留着
            Assert.NotNull(rig.Inputs.Read("s1")); // 也还在盘上

            rig.Registry.ResumeQueue("s1");
            await Wait.UntilAsync(() => driver().Inputs.Count >= 2);
            await rig.IdleAsync("s1");
            Assert.Empty(Items(rig));
        }
    }

    [Fact]
    public async Task A_cold_start_restores_the_queue_from_disk_and_runs_nothing_until_the_user_acts()
    {
        using var rig = EchoRig(out var driver);
        await rig.Registry.EnqueueAsync("s1", Rig.Workspace, new MessageId("m1"), Text("one"), Opts.Default with { Thinking = ThinkingLevel.Low });
        // 上面是空闲会话:入队 = 接着发。换一个忙的会话状态:用操作占住它,再入队,然后「重启」。
        await Wait.UntilAsync(() => driver().Inputs.Count >= 1);
        await rig.IdleAsync("s1");
        using (rig.Registry.AcquireOperation("s1"))
        {
            await rig.Registry.EnqueueAsync("s1", Rig.Workspace, new MessageId("m2"), Text("two"), Opts.Default with { Thinking = ThinkingLevel.High });
            await rig.Registry.EnqueueAsync("s1", Rig.Workspace, new MessageId("m3"), Text("three"), Opts.Default);
            rig.Registry.TogglePromote("s1", new MessageId("m3"));
        }

        rig.Restart();
        await Task.Delay(100);
        Assert.Empty(driver().Inputs); // 冷启动不自动跑(重启后装上的是新驱动,什么都没收到)
        var restored = Items(rig);
        Assert.Equal(["m2", "m3"], restored.Select(i => i.Id.Value));
        Assert.Equal(QueueState.Pending, restored[0].State);
        Assert.Equal(QueueState.Promoted, restored[1].State); // 引入状态也恢复
        Assert.Equal(ThinkingLevel.High, restored[0].Options.Thinking); // 档位快照也恢复
        Assert.Equal(1, rig.Factory.Created.Count(t => t.Origin == RunOrigin.Internal));

        rig.Registry.ResumeQueue("s1"); // 用户继续:promoted 先发
        await Wait.UntilAsync(() => driver().Inputs.Count >= 1);
        Assert.EndsWith(":three", driver().Inputs[0]);
        await rig.IdleAsync("s1");
    }

    [Fact]
    public async Task A_follow_up_that_cannot_start_goes_back_to_the_head_of_the_queue_and_stays_saved()
    {
        using var rig = EchoRig(out var driver);
        using (rig.Registry.AcquireOperation("s1"))
        {
            await rig.Registry.EnqueueAsync("s1", Rig.Workspace, new MessageId("m1"), Text("one"), Opts.Default);
            await rig.Registry.EnqueueAsync("s1", Rig.Workspace, new MessageId("m2"), Text("two"), Opts.Default);
            rig.Registry.TogglePromote("s1", new MessageId("m2"));
        }

        rig.Factory.Tweak = (_, _) => throw new InvalidOperationException("cannot assemble");
        rig.Registry.ResumeQueue("s1");
        Assert.Equal(["m1", "m2"], Items(rig).Select(i => i.Id.Value).Order()); // 一条没丢
        Assert.Equal(QueueState.Promoted, Items(rig).Single(i => i.Id.Value == "m2").State);
        Assert.Equal(2, rig.Inputs.Read("s1")!.Queued!.Count);
        Assert.Empty(driver().Inputs);

        rig.Factory.Tweak = null;
        rig.Registry.ResumeQueue("s1");
        await Wait.UntilAsync(() => driver().Inputs.Count >= 1);
        await rig.IdleAsync("s1");
    }

    // ── 插话 ──

    [Fact]
    public async Task Promote_toggles_and_the_re_promoted_item_goes_to_the_back_of_the_promoted_ones()
    {
        var gate = new TaskCompletionSource();
        using var rig = EchoRig(out _, takesSteering: _ => false, during: _ => gate.Task);
        var run = await StartHeld(rig, gate);
        foreach (var id in new[] { "a", "b", "c" })
        {
            await Send(rig, "x" + id, id, id);
        }

        rig.Registry.TogglePromote("s1", new MessageId("a"));
        rig.Registry.TogglePromote("s1", new MessageId("b"));
        Assert.Equal(["a", "b"], Items(rig).Where(i => i.State == QueueState.Promoted).OrderBy(i => i.PromotedOrder).Select(i => i.Id.Value));
        rig.Registry.TogglePromote("s1", new MessageId("a")); // 取消
        Assert.Equal(QueueState.Pending, Items(rig).Single(i => i.Id.Value == "a").State);
        Assert.Null(Items(rig).Single(i => i.Id.Value == "a").PromotedAt);
        rig.Registry.TogglePromote("s1", new MessageId("a")); // 再引入:排到已引入者的队尾
        Assert.Equal(["b", "a"], Items(rig).Where(i => i.State == QueueState.Promoted).OrderBy(i => i.PromotedOrder).Select(i => i.Id.Value));
        Assert.False(rig.Registry.TogglePromote("s1", new MessageId("ghost")).Accepted);
        gate.SetResult();
        await Rig.Settled(run);
    }

    [Fact]
    public async Task Promoted_ids_get_distinct_strictly_increasing_timestamps_even_in_the_same_millisecond()
    {
        var gate = new TaskCompletionSource();
        using var rig = EchoRig(out _, takesSteering: _ => false, during: _ => gate.Task);
        var run = await StartHeld(rig, gate);
        foreach (var id in new[] { "a", "b", "c" })
        {
            await Send(rig, "x" + id, id, id);
        }

        foreach (var id in new[] { "a", "b", "c" })
        {
            rig.Registry.TogglePromote("s1", new MessageId(id));
        }

        var stamps = Items(rig).OrderBy(i => i.PromotedOrder).Select(i => i.PromotedAt!.Value).ToList();
        Assert.Equal(stamps.Order(), stamps);
        Assert.Equal(3, stamps.Distinct().Count());
        gate.SetResult();
        await Rig.Settled(run);
    }

    [Fact]
    public async Task Promoting_in_an_idle_session_sends_it_now_and_only_the_promoted_ones()
    {
        using var rig = EchoRig(out var driver);
        using (rig.Registry.AcquireOperation("s1"))
        {
            await rig.Registry.EnqueueAsync("s1", Rig.Workspace, new MessageId("a"), Text("A"), Opts.Default);
            await rig.Registry.EnqueueAsync("s1", Rig.Workspace, new MessageId("b"), Text("B"), Opts.Default);
        }

        rig.Registry.TogglePromote("s1", new MessageId("b"));
        await Wait.UntilAsync(() => driver().Inputs.Count >= 1);
        Assert.EndsWith(":B", driver().Inputs[0]);
        await rig.IdleAsync("s1");
        // 没有 promoted 了:b 的 Run 以 Done 结束之后,a 作为 FIFO 的下一条续上。
        await Wait.UntilAsync(() => driver().Inputs.Count >= 2);
        Assert.EndsWith(":A", driver().Inputs[1]);
        await rig.IdleAsync("s1");
    }

    [Fact]
    public async Task Several_promoted_items_merge_into_one_input_in_promotion_order_not_queue_order()
    {
        using var rig = EchoRig(out var driver);
        using (rig.Registry.AcquireOperation("s1"))
        {
            foreach (var id in new[] { "a", "b", "c" })
            {
                await rig.Registry.EnqueueAsync("s1", Rig.Workspace, new MessageId(id), Text(id.ToUpperInvariant()), Opts.Default);
            }

            rig.Registry.TogglePromote("s1", new MessageId("c"));
            rig.Registry.TogglePromote("s1", new MessageId("a"));
        }

        rig.Registry.ResumeQueue("s1");
        await Wait.UntilAsync(() => driver().Inputs.Count >= 1);
        Assert.EndsWith(":C\n\nA", driver().Inputs[0]);
        var template = rig.Factory.Created.First(t => t.Origin == RunOrigin.Internal);
        Assert.NotEqual("a", template.Input[0].Id.Value); // 多条合并成一条新消息
        await Wait.UntilAsync(() => driver().Inputs.Count >= 2);
        await rig.IdleAsync("s1");
        Assert.EndsWith(":B", driver().Inputs[1]);
    }

    [Fact]
    public async Task A_promoted_item_is_steered_into_the_running_run_and_counts_as_delivered_only_on_the_message_end_with_its_id()
    {
        var gate = new TaskCompletionSource();
        var seenInRun = new List<string>();
        using var rig = new Rig();
        rig.NewSessionRow();
        rig.Tools.Add(new FakeTool("slow", async (_, _, ct) =>
        {
            await gate.Task.WaitAsync(ct);
            return Ncw.Agent.Kernel.Tools.ToolResult.Text("tool done");
        }));
        rig.Llm.Turn(Up.Call("c1", "slow"), Up.Text("after steering"));
        var run = rig.Start("s1", "go", runId: "r1");
        await Wait.UntilAsync(() => rig.Llm.Requests.Count == 1);
        await Wait.UntilAsync(() => rig.Tools.OfType<FakeTool>().Single().Calls == 1);

        var queued = Assert.IsType<SendResult.Queued>(await Send(rig, "r2", "m2", "steer me"));
        Assert.True(queued.Result.Accepted);
        rig.Registry.TogglePromote("s1", new MessageId("m2"));
        Assert.Equal(QueueState.Promoted, Items(rig).Single().State);
        gate.SetResult();
        await Rig.Settled(run);

        // 顺序:用户 → 助手(调用)→ 工具结果 → 插话(id = 队列项 id)→ 助手。插话插在工具结果之后,没有把调用和结果拆开。
        var stored = rig.Stored("s1");
        Assert.Equal(["go", "", "", "steer me", "after steering"], stored.Select(m => m.Text));
        Assert.Equal(["user", "assistant", "user", "user", "assistant"], stored.Select(m => m.Role));
        Assert.Equal("m2", stored[3].Id);
        Assert.Contains("tool done", rig.Repo.History("s1")[2].PartsJson, StringComparison.Ordinal);
        Assert.Equal(2, rig.Llm.Requests.Count);
        Assert.Contains(rig.Llm.Requests[1].Messages, m => m.Parts.OfType<TextPart>().Any(p => p.Text == "steer me"));
        // 送达之后:队列空了,盘上也空了,没有续跑(它已经被这个 Run 消费了)。
        Assert.Empty(Items(rig));
        Assert.Empty(rig.Inputs.Read("s1")?.Queued ?? []);
        Assert.DoesNotContain(rig.Factory.Created, t => t.Origin == RunOrigin.Internal);
        Assert.Equal(1, rig.Llm.Requests.Count(r => r.Messages.Any(m => m.Parts.OfType<TextPart>().Any(p => p.Text == "steer me"))));
        _ = seenInRun;
    }

    [Fact]
    public async Task A_steering_message_that_was_taken_but_never_confirmed_goes_back_to_the_queue_at_settle()
    {
        var calls = 0;
        // r1 取走了插话却没有提交(驱动半路出了岔子):没有 message_end,就不算送达。
        using var rig = new Rig(o => o.Driver = _ => new ScriptedDriver(async (handle, request, sink, ct) =>
        {
            await sink.EmitAsync(new RunStartEvent(handle.RunId, handle.SessionId, request.Model), CancellationToken.None);
            if (handle.RunId.Value == "r1" && Interlocked.Increment(ref calls) == 1)
            {
                Assert.Single(request.TakeSteering!());
            }

            await sink.EmitAsync(new RunEndEvent(RunStatus.Error, AgentError.Create(AgentErrorCode.Provider, "x")), CancellationToken.None);
            return ScriptedDriver.Done(handle, RunStatus.Error);
        }));
        rig.NewSessionRow();
        using (rig.Registry.AcquireOperation("s1"))
        {
            await rig.Registry.EnqueueAsync("s1", Rig.Workspace, new MessageId("m2"), Text("steer"), Opts.Default);
            rig.Registry.TogglePromote("s1", new MessageId("m2"));
        }

        var run = rig.Start("s1", "go", runId: "r1");
        await Rig.Settled(run);
        Assert.Equal(["m2"], Items(rig).Select(i => i.Id.Value));
        Assert.Equal(QueueState.Promoted, Items(rig)[0].State);
        Assert.Equal("m2", rig.Inputs.Read("s1")!.Queued!.Single().Id);
    }

    [Fact]
    public async Task A_steering_message_that_was_committed_but_whose_event_never_arrived_is_not_sent_again_the_receipt_wins()
    {
        // 驱动提交了(回执已写)却没来得及发 message_end(被看门狗收口、事件被丢):清队列没做成,同一条插话绝不能被续跑再发一次。
        using var rig = new Rig(o => o.Driver = r => new ScriptedDriver(async (handle, request, sink, ct) =>
        {
            var access = new RepoAccess(r.Repo);
            await sink.EmitAsync(new RunStartEvent(handle.RunId, handle.SessionId, request.Model), CancellationToken.None);
            foreach (var message in request.TakeSteering!())
            {
                access.Commit(handle.SessionId, handle.RunId, message); // 提交 + 回执,但不发 message_end
            }

            await sink.EmitAsync(new RunEndEvent(RunStatus.Done), CancellationToken.None);
            return ScriptedDriver.Done(handle);
        }));
        rig.NewSessionRow();
        using (rig.Registry.AcquireOperation("s1"))
        {
            await rig.Registry.EnqueueAsync("s1", Rig.Workspace, new MessageId("m2"), Text("steer"), Opts.Default);
            rig.Registry.TogglePromote("s1", new MessageId("m2"));
        }

        var run = rig.Start("s1", "go", runId: "r1");
        await Rig.Settled(run);
        await Task.Delay(100);
        Assert.Empty(Items(rig));
        Assert.DoesNotContain(rig.Factory.Created, t => t.Origin == RunOrigin.Internal);
        Assert.Empty(rig.Inputs.Read("s1")?.Queued ?? []);
    }

    [Fact]
    public async Task After_a_restart_an_old_queue_row_never_overrides_a_message_that_was_already_committed()
    {
        using var rig = EchoRig(out var driver);
        // 旧队列行还在盘上(清队列的写失败过),但这条消息已经提交并有回执。
        using (rig.Registry.AcquireOperation("s1"))
        {
            await rig.Registry.EnqueueAsync("s1", Rig.Workspace, new MessageId("m9"), Text("already sent"), Opts.Default);
        }

        new RepoAccess(rig.Repo).Commit("s1", new RunId("old"), Msg.User("m9", "already sent"));
        rig.Restart();
        Assert.Empty(Items(rig)); // 回执在,条目就不再被发出去
        rig.Registry.ResumeQueue("s1");
        await Task.Delay(100);
        Assert.Empty(driver().Inputs);
    }

    // ── 编辑 / 删除 / 撤回 / 改档位 ──

    [Fact]
    public async Task Edit_changes_the_text_but_not_the_options_snapshot_or_the_promotion_order()
    {
        var gate = new TaskCompletionSource();
        using var rig = EchoRig(out _, takesSteering: _ => false, during: _ => gate.Task);
        var run = await StartHeld(rig, gate);
        var high = Opts.Default with { Thinking = ThinkingLevel.High };
        await Send(rig, "xa", "a", "A", high);
        await Send(rig, "xb", "b", "B");
        rig.Registry.TogglePromote("s1", new MessageId("b"));
        rig.Registry.TogglePromote("s1", new MessageId("a"));
        var before = Items(rig).Single(i => i.Id.Value == "a");

        Assert.True(rig.Registry.EditQueued("s1", new MessageId("a"), Text("A edited")).Accepted);
        var after = Items(rig).Single(i => i.Id.Value == "a");
        Assert.Equal("A edited", TextOf(after));
        Assert.Equal(before.Options, after.Options);
        Assert.Equal(before.PromotedOrder, after.PromotedOrder);
        Assert.Equal("A edited", rig.Inputs.Read("s1")!.Queued!.Single(q => q.Id == "a").Text);
        Assert.False(rig.Registry.EditQueued("s1", new MessageId("ghost"), Text("x")).Accepted);
        gate.SetResult();
        await Rig.Settled(run);
    }

    [Fact]
    public async Task Take_hands_the_content_back_and_drop_only_touches_the_target()
    {
        var gate = new TaskCompletionSource();
        using var rig = EchoRig(out _, takesSteering: _ => false, during: _ => gate.Task);
        var run = await StartHeld(rig, gate);
        await Send(rig, "xa", "a", "A");
        await Send(rig, "xb", "b", "B");
        await Send(rig, "xc", "c", "C");

        var taken = rig.Registry.TakeQueued("s1", new MessageId("a"));
        Assert.Equal("A", ((TextPart)taken.Taken![0]).Text);
        Assert.Equal(["b", "c"], taken.Update.Items.Select(i => i.Id.Value));
        var dropped = rig.Registry.DropQueued("s1", new MessageId("b"));
        Assert.Equal(["c"], dropped.Update.Items.Select(i => i.Id.Value));
        Assert.False(rig.Registry.DropQueued("s1", new MessageId("b")).Accepted);
        Assert.Equal(["c"], rig.Inputs.Read("s1")!.Queued!.Select(q => q.Id));
        gate.SetResult();
        await Rig.Settled(run);
    }

    [Fact]
    public async Task Retagging_the_permission_changes_the_queued_items_and_nothing_else()
    {
        var gate = new TaskCompletionSource();
        using var rig = EchoRig(out _, takesSteering: _ => false, during: _ => gate.Task);
        var run = await StartHeld(rig, gate);
        await Send(rig, "xa", "a", "A", Opts.Default with { PermissionMode = "default", Thinking = ThinkingLevel.Low });
        await Send(rig, "xb", "b", "B", Opts.Default with { PermissionMode = "plan", Mode = "plan" });
        var result = rig.Registry.RetagQueuedPermission("s1", "fullAccess");
        Assert.All(result.Update.Items, i => Assert.Equal("fullAccess", i.Options.PermissionMode));
        Assert.Equal(ThinkingLevel.Low, result.Update.Items.Single(i => i.Id.Value == "a").Options.Thinking);
        Assert.Equal("plan", result.Update.Items.Single(i => i.Id.Value == "b").Options.Mode);
        Assert.Equal("fullAccess", rig.Inputs.Read("s1")!.Queued![0].Options!["permissionMode"]!.GetValue<string>());
        var mode = rig.Registry.RetagQueuedMode("s1", "code");
        Assert.All(mode.Update.Items, i => Assert.Equal("code", i.Options.Mode));
        gate.SetResult();
        await Rig.Settled(run);
    }

    [Fact]
    public async Task The_queue_has_a_soft_cap_and_a_refused_item_is_not_saved_or_broadcast()
    {
        var gate = new TaskCompletionSource();
        using var rig = EchoRig(out _, takesSteering: _ => false, during: _ => gate.Task);
        var run = await StartHeld(rig, gate);
        for (var i = 0; i < RunInputQueues.MaxItems; i++)
        {
            Assert.True((await Send(rig, $"x{i}", $"m{i}", $"t{i}") is SendResult.Queued { Result.Accepted: true }));
        }

        var broadcasts = rig.QueueUpdates.Count;
        var saves = rig.Store.Saves;
        var refused = Assert.IsType<SendResult.Queued>(await Send(rig, "xx", "overflow", "t"));
        Assert.False(refused.Result.Accepted);
        Assert.Equal(RunInputQueues.MaxItems, refused.Result.Update.Items.Count);
        Assert.Equal(broadcasts, rig.QueueUpdates.Count);
        Assert.Equal(saves, rig.Store.Saves);
        // 同 id 也不能重复入队。
        Assert.False((await rig.Registry.EnqueueAsync("s1", Rig.Workspace, new MessageId("m0"), Text("dup"), Opts.Default)).Accepted);
        gate.SetResult();
        await Rig.Settled(run);
    }

    [Fact]
    public async Task File_references_in_a_queued_message_are_normalised_like_the_first_input()
    {
        var gate = new TaskCompletionSource();
        using var rig = new Rig();
        rig.NewSessionRow();
        rig.Llm.Turn(Up.Gate(gate.Task));
        var run = rig.Start("s1", "go", runId: "r1");
        await Wait.UntilAsync(() => rig.Llm.Requests.Count == 1);
        var absolute = Path.Combine(rig.WorkRoot, "src", "a.txt");
        var result = await rig.Registry.EnqueueAsync("s1", Rig.Workspace, new MessageId("m2"), [new TextPart("see"), new FileRefPart(absolute, "a.txt")], Opts.Default);
        Assert.Equal("src/a.txt", result.Update.Items[0].Parts.OfType<FileRefPart>().Single().Path); // 工作区内 → 相对路径
        gate.SetResult();
        await Rig.Settled(run);
    }

    // ── 写盘失败(先落盘再确认) ──

    [Fact]
    public async Task A_failed_enqueue_throws_is_not_accepted_not_broadcast_and_leaves_the_old_queue_in_memory()
    {
        var gate = new TaskCompletionSource();
        using var rig = EchoRig(out _, takesSteering: _ => false, during: _ => gate.Task);
        var run = await StartHeld(rig, gate);
        await Send(rig, "xa", "a", "A");
        var broadcasts = rig.QueueUpdates.Count;

        rig.Store.FailSave = true;
        await Assert.ThrowsAsync<QueuePersistenceException>(async () => await Send(rig, "xb", "b", "B"));
        Assert.Equal(["a"], Items(rig).Select(i => i.Id.Value));
        Assert.Equal(broadcasts, rig.QueueUpdates.Count);

        rig.Store.FailSave = false;
        Assert.True((await Send(rig, "xb2", "b", "B")) is SendResult.Queued { Result.Accepted: true });
        Assert.Equal(["a", "b"], Items(rig).Select(i => i.Id.Value)); // 重试成功只多出一条
        gate.SetResult();
        await Rig.Settled(run);
    }

    [Fact]
    public async Task A_failed_edit_drop_promote_or_retag_rolls_everything_back()
    {
        var gate = new TaskCompletionSource();
        using var rig = EchoRig(out _, takesSteering: _ => false, during: _ => gate.Task);
        var run = await StartHeld(rig, gate);
        await Send(rig, "xa", "a", "A", Opts.Default with { PermissionMode = "default" });
        await Send(rig, "xb", "b", "B");
        rig.Registry.TogglePromote("s1", new MessageId("b"));
        var before = Items(rig);

        rig.Store.FailSave = true;
        Assert.Throws<QueuePersistenceException>(() => rig.Registry.EditQueued("s1", new MessageId("a"), Text("changed")));
        Assert.Throws<QueuePersistenceException>(() => rig.Registry.DropQueued("s1", new MessageId("a")));
        Assert.Throws<QueuePersistenceException>(() => rig.Registry.TogglePromote("s1", new MessageId("a")));
        Assert.Throws<QueuePersistenceException>(() => rig.Registry.TogglePromote("s1", new MessageId("b")));
        Assert.Throws<QueuePersistenceException>(() => rig.Registry.RetagQueuedPermission("s1", "fullAccess"));
        Assert.Throws<QueuePersistenceException>(() => rig.Registry.TakeQueued("s1", new MessageId("a")));
        Assert.Equal(before, Items(rig)); // 文本、档位快照、引入状态与顺序全部回到失败前
        rig.Store.FailSave = false;
        gate.SetResult();
        await Rig.Settled(run);
    }

    [Fact]
    public async Task A_follow_up_whose_hand_over_cannot_be_saved_starts_nothing_and_loses_nothing()
    {
        using var rig = EchoRig(out var driver);
        using (rig.Registry.AcquireOperation("s1"))
        {
            await rig.Registry.EnqueueAsync("s1", Rig.Workspace, new MessageId("m1"), Text("one"), Opts.Default);
        }

        rig.Store.FailSave = true;
        Assert.Throws<QueuePersistenceException>(() => rig.Registry.ResumeQueue("s1"));
        Assert.Equal(["m1"], Items(rig).Select(i => i.Id.Value));
        Assert.Empty(driver().Inputs);
        Assert.DoesNotContain(rig.Factory.Created, t => t.Origin == RunOrigin.Internal);

        rig.Store.FailSave = false;
        rig.Registry.ResumeQueue("s1");
        await Wait.UntilAsync(() => driver().Inputs.Count >= 1);
        await rig.IdleAsync("s1");
    }

    [Fact]
    public async Task When_a_failed_start_cannot_be_put_back_on_disk_the_error_is_thrown_and_the_queue_survives_in_memory()
    {
        using var rig = EchoRig(out _);
        using (rig.Registry.AcquireOperation("s1"))
        {
            await rig.Registry.EnqueueAsync("s1", Rig.Workspace, new MessageId("m1"), Text("one"), Opts.Default);
        }

        rig.Factory.Tweak = (_, _) =>
        {
            rig.Store.FailSave = true; // 移交那一次写成功了,放回去这一次失败
            throw new InvalidOperationException("cannot start");
        };
        Assert.Throws<QueuePersistenceException>(() => rig.Registry.ResumeQueue("s1"));
        Assert.Equal(["m1"], Items(rig).Select(i => i.Id.Value));
        Assert.True(rig.Registry.QueueOf("s1").Items.Count == 1);
    }

    [Fact]
    public async Task A_broadcast_that_throws_neither_fails_the_saved_item_nor_lets_it_be_queued_twice_nor_blocks_other_subscribers()
    {
        var gate = new TaskCompletionSource();
        using var rig = EchoRig(out _, takesSteering: _ => false, during: _ => gate.Task);
        var run = await StartHeld(rig, gate);
        var later = 0;
        rig.Registry.QueueChanged += _ => throw new InvalidOperationException("boom");
        rig.Registry.QueueChanged += _ => later++;
        var result = Assert.IsType<SendResult.Queued>(await Send(rig, "xa", "a", "A"));
        Assert.True(result.Result.Accepted);
        Assert.Equal(["a"], Items(rig).Select(i => i.Id.Value));
        Assert.Equal(1, later);
        Assert.False((await Send(rig, "xa2", "a", "A")) is SendResult.Queued { Result.Accepted: true });
        Assert.True(rig.Log.Any("queue subscriber threw"));
        gate.SetResult();
        await Rig.Settled(run);
    }

    [Fact]
    public async Task The_revision_is_global_so_it_keeps_growing_after_an_empty_entry_is_dropped_and_re_created()
    {
        var gate = new TaskCompletionSource();
        using var rig = EchoRig(out _, takesSteering: _ => false, during: _ => gate.Task);
        var run = await StartHeld(rig, gate);
        await Send(rig, "xa", "a", "A");
        rig.Registry.DropQueued("s1", new MessageId("a")); // 空了 → 条目被丢掉
        await Send(rig, "xb", "b", "B"); // 重新建出来
        var revs = rig.QueueUpdates.Select(u => u.Rev).ToList();
        Assert.Equal(revs.Order(), revs);
        Assert.Equal(revs.Distinct().Count(), revs.Count);
        Assert.Equal(3, revs.Count);
        gate.SetResult();
        await Rig.Settled(run);
    }

    [Fact]
    public async Task An_unreadable_saved_queue_is_treated_as_empty_and_does_not_break_the_session()
    {
        using var rig = EchoRig(out _);
        rig.Store.FailLoad = true;
        Assert.Empty(Items(rig));
        Assert.True(rig.Log.Any("could not be read"));
    }

    [Fact]
    public async Task A_saved_queue_with_a_wrong_structure_is_dropped_whole_not_repaired()
    {
        using var rig = EchoRig(out _);
        // 一条缺了 text 的条目:读回来校验失败 → 整份丢弃(宁可少一份草稿,也不做部分修复)。
        rig.Inputs.Write("s1", new SessionInputState { Queued = [new Ncw.Agent.Storage.Sessions.QueuedInput { Id = "x", Status = "pending", Attachments = [], Options = new System.Text.Json.Nodes.JsonObject(), EnqueuedAt = 1 }] });
        Assert.Null(rig.Inputs.Read("s1"));
        Assert.Empty(Items(rig));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task The_draft_and_unknown_fields_in_the_shared_kv_row_survive_queue_writes()
    {
        var gate = new TaskCompletionSource();
        using var rig = EchoRig(out _, takesSteering: _ => false, during: _ => gate.Task);
        rig.Inputs.Write("s1", new SessionInputState { Draft = "half typed", Queued = [] });
        var run = await StartHeld(rig, gate);
        await Send(rig, "xa", "a", "A");
        var row = rig.Inputs.Read("s1")!;
        Assert.Equal("half typed", row.Draft);
        Assert.Single(row.Queued!);
        gate.SetResult();
        await Rig.Settled(run);
    }

    [Fact]
    public async Task The_queue_is_forgotten_with_the_session_and_internal_inputs_waiting_for_it_are_returned()
    {
        using var rig = EchoRig(out _);
        using (rig.Registry.AcquireOperation("s1"))
        {
            await rig.Registry.EnqueueAsync("s1", Rig.Workspace, new MessageId("m1"), Text("one"), Opts.Default);
            var delivered = new List<bool>();
            rig.Registry.DeliverInternal("s1", new InternalInput(Text("report"), Opts.Default, Done: delivered.Add));
            await rig.Registry.ForgetSessionsAsync(["s1"]);
            Assert.Equal([false], delivered);
        }

        rig.Inputs.Remove("s1"); // 存档由会话删除一并清掉
        Assert.Empty(Items(rig));
    }
}
