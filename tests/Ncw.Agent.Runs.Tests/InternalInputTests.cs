using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Extensions;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Runs;
using Ncw.Agent.Llm;
using Ncw.Agent.Runs.Tests.Support;

namespace Ncw.Agent.Runs.Tests;

/// <summary>
/// 内部输入(后台子代理报告、目标检查):编排层给它们留的投递通道(05 §3、2.x <c>session-runtime.ts</c> 的 <c>deliverInternal</c> 一族)。
/// 移植自 2.x <c>session-runtime.test.ts</c> 的「后台子代理汇报」(投递与确认部分)和「目标空闲检查」两组;汇报状态的持久化(pending / injecting / reported)属于子代理层,不在这里。
/// </summary>
public sealed class InternalInputTests
{
    private static Rig EchoRig(out Func<EchoDriver> driver, Func<RunHandle, bool>? takesSteering = null, Func<RunHandle, Task>? during = null, Action<RigOptions>? more = null)
    {
        EchoDriver? echo = null;
        var rig = new Rig(o =>
        {
            o.Driver = r => echo = new EchoDriver(new RepoAccess(r.Repo), takesSteering, during);
            more?.Invoke(o);
        });
        rig.NewSessionRow();
        driver = () => echo!;
        return rig;
    }

    private static IReadOnlyList<Part> Text(string text) => [new TextPart(text)];

    private static async Task<RunHandle> Prime(Rig rig)
    {
        // 让这个会话「发过一次消息」:内部输入起新 Run 时沿用它的档位。
        var handle = rig.Start("s1", "first", runId: "r0");
        await Rig.Settled(handle);
        return handle;
    }

    private static Ext BlockOnRunEnd(TaskCompletionSource entered, Task release) => new("block", 0, r =>
        r.On(LoopExtensionPoints.RunEnd, async (_, _, _) =>
        {
            entered.TrySetResult();
            await release;
        }));

    [Fact]
    public async Task An_idle_session_gets_an_internal_run_with_the_last_options_and_done_fires_on_the_message_end()
    {
        using var rig = EchoRig(out var driver);
        await Prime(rig);
        var done = new List<bool>();
        var frozen = Opts.Default with { Thinking = ThinkingLevel.High };
        var result = rig.Registry.DeliverInternal("s1", new InternalInput(Text("report"), null, Done: done.Add));
        Assert.Equal(InternalDelivery.Launched, result);
        await Wait.UntilAsync(() => driver().Inputs.Count >= 2);
        await Wait.UntilAsync(() => done.Count == 1);
        Assert.Equal([true], done);
        var template = rig.Factory.Created.Single(t => t.Origin == RunOrigin.Internal);
        Assert.Equal(Opts.Default, template.Options); // 沿用这个会话上一次的档位
        Assert.True(template.Input[0].Internal);
        Assert.Equal("report", ((TextPart)template.Input[0].Parts[0]).Text);
        Assert.Contains(rig.Repo.History("s1"), r => r.Internal && r.Id == template.Input[0].Id.Value);
        _ = frozen;
        await rig.IdleAsync("s1");
    }

    [Fact]
    public async Task With_no_options_to_start_a_run_with_the_input_fails_loudly_instead_of_vanishing()
    {
        using var rig = EchoRig(out var driver);
        var done = new List<bool>();
        Assert.Equal(InternalDelivery.Failed, rig.Registry.DeliverInternal("s1", new InternalInput(Text("report"), null, Done: done.Add)));
        Assert.Equal([false], done);
        Assert.True(rig.Log.Any("no send options"));
        Assert.Empty(driver()?.Inputs ?? []);
        Assert.False(rig.Registry.IsSessionBusy("s1"));
    }

    [Fact]
    public async Task Explicit_options_on_the_input_win_over_the_remembered_ones()
    {
        using var rig = EchoRig(out var driver);
        var explicitOptions = Opts.Default with { Thinking = ThinkingLevel.XHigh, Mode = "plan" };
        rig.Registry.DeliverInternal("s1", new InternalInput(Text("check goal"), explicitOptions));
        await Wait.UntilAsync(() => driver().Inputs.Count >= 1);
        Assert.Equal(explicitOptions, rig.Factory.Created.Single().Options);
        await rig.IdleAsync("s1");
    }

    [Fact]
    public async Task A_running_session_takes_it_at_the_next_turn_boundary_and_done_waits_for_the_same_id_message_end()
    {
        var gate = new TaskCompletionSource();
        using var rig = EchoRig(out var driver, during: h => h.RunId.Value == "r1" ? gate.Task : Task.CompletedTask);
        var run = rig.Start("s1", "first", runId: "r1");
        await Wait.UntilAsync(() => run.HasStarted);
        var done = new List<bool>();
        Assert.Equal(InternalDelivery.Interjected, rig.Registry.DeliverInternal("s1", new InternalInput(Text("report"), Opts.Default, Done: done.Add)));
        Assert.Empty(done); // 入队不算送达
        Assert.Empty(rig.Registry.QueueOf("s1").Items); // 也不是用户队列里的东西(用户侧的整份替换抹不掉它)
        gate.SetResult();
        await Rig.Settled(run);
        Assert.Equal([true], done);
        Assert.Contains(driver().Inputs, i => i.EndsWith(":report", StringComparison.Ordinal));
        Assert.Contains(rig.Repo.History("s1"), r => r.Internal && r.PartsJson.Contains("report", StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Factory.Created, t => t.Origin == RunOrigin.Internal); // 已经被这个 Run 消费,不会再开新 Run
    }

    [Fact]
    public async Task An_internal_input_survives_the_user_side_replacing_or_clearing_the_queue()
    {
        var gate = new TaskCompletionSource();
        using var rig = EchoRig(out var driver, during: h => h.RunId.Value == "r1" ? gate.Task : Task.CompletedTask);
        var run = rig.Start("s1", "first", runId: "r1");
        await Wait.UntilAsync(() => run.HasStarted);
        var done = new List<bool>();
        rig.Registry.DeliverInternal("s1", new InternalInput(Text("report"), Opts.Default, Done: done.Add));
        await rig.Registry.EnqueueAsync("s1", Rig.Workspace, new MessageId("m1"), Text("user"), Opts.Default);
        rig.Registry.DropQueued("s1", new MessageId("m1"));
        gate.SetResult();
        await Rig.Settled(run);
        Assert.Equal([true], done);
        Assert.Contains(driver().Inputs, i => i.EndsWith(":report", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_session_that_is_settling_defers_it_until_after_settle_and_it_goes_before_the_queued_user_messages()
    {
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        using var rig = new Rig(o => o.SessionExtensions = [BlockOnRunEnd(entered, release.Task)]);
        rig.NewSessionRow();
        var first = rig.Start("s1", "go", runId: "r1");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var done = new List<bool>();
        await rig.Registry.EnqueueAsync("s1", Rig.Workspace, new MessageId("m1"), Text("user follow-up"), Opts.Default);
        Assert.Equal(InternalDelivery.Deferred, rig.Registry.DeliverInternal("s1", new InternalInput(Text("report"), Opts.Default, Done: done.Add)));
        Assert.Empty(done);

        release.SetResult();
        await Rig.Settled(first);
        await Wait.UntilAsync(() => rig.Stored("s1").Count(m => m.Role == "user") == 3, what: "both follow-ups");
        await rig.IdleAsync("s1");
        Assert.Equal(["go", "report", "user follow-up"], rig.Stored("s1").Where(m => m.Role == "user").Select(m => m.Text));
        Assert.Equal([true], done);
    }

    [Fact]
    public async Task A_stopped_run_returns_the_unconsumed_input_and_does_not_open_a_new_conversation()
    {
        var gate = new TaskCompletionSource();
        using var rig = EchoRig(out var driver, during: h => h.RunId.Value == "r1" ? Task.Delay(Timeout.Infinite, h.Token) : gate.Task);
        var run = rig.Start("s1", "first", runId: "r1");
        await Wait.UntilAsync(() => run.HasStarted);
        var done = new List<bool>();
        rig.Registry.DeliverInternal("s1", new InternalInput(Text("report"), Opts.Default, Done: done.Add));
        rig.Registry.Abort(run.RunId);
        await Rig.Settled(run);
        Assert.Equal([false], done);
        await Task.Delay(100);
        Assert.Single(driver().Inputs);
        Assert.DoesNotContain(rig.Factory.Created, t => t.Origin == RunOrigin.Internal);
        Assert.Empty(rig.Registry.QueueOf("s1").Items); // 退回给投递方,而不是留在队列里被下一个 Run 顺手取走
    }

    [Fact]
    public async Task A_run_that_ends_normally_without_consuming_it_re_delivers_it_ahead_of_the_queued_user_messages()
    {
        var gate = new TaskCompletionSource();
        using var rig = EchoRig(out var driver, takesSteering: h => h.RunId.Value != "r1", during: h => h.RunId.Value == "r1" ? gate.Task : Task.CompletedTask);
        var run = rig.Start("s1", "first", runId: "r1");
        await Wait.UntilAsync(() => run.HasStarted);
        var done = new List<bool>();
        rig.Registry.DeliverInternal("s1", new InternalInput(Text("report"), Opts.Default, Done: done.Add));
        await rig.Registry.EnqueueAsync("s1", Rig.Workspace, new MessageId("m1"), Text("user"), Opts.Default);
        gate.SetResult(); // r1 不取引导消息就结束了
        await Rig.Settled(run);
        await Wait.UntilAsync(() => driver().Inputs.Count >= 3, what: "report then user");
        await rig.IdleAsync("s1");
        Assert.EndsWith(":report", driver().Inputs[1]);
        Assert.EndsWith(":user", driver().Inputs[2]);
        Assert.Equal([true], done);
    }

    [Fact]
    public async Task A_confirmed_input_is_never_re_delivered()
    {
        var gate = new TaskCompletionSource();
        using var rig = EchoRig(out var driver, during: h => h.RunId.Value == "r1" ? gate.Task : Task.CompletedTask);
        var run = rig.Start("s1", "first", runId: "r1");
        await Wait.UntilAsync(() => run.HasStarted);
        var done = new List<bool>();
        rig.Registry.DeliverInternal("s1", new InternalInput(Text("report"), Opts.Default, Done: done.Add));
        gate.SetResult();
        await Rig.Settled(run);
        await Task.Delay(100);
        Assert.Equal(1, driver().Inputs.Count(i => i.EndsWith(":report", StringComparison.Ordinal)));
        Assert.Equal([true], done);
    }

    [Fact]
    public async Task A_launch_that_is_refused_leaves_no_residue_so_a_later_settle_cannot_re_deliver_it()
    {
        using var rig = EchoRig(out var driver);
        await Prime(rig);
        rig.Factory.Tweak = (_, _) => throw new InvalidOperationException("refused");
        var done = new List<bool>();
        Assert.Equal(InternalDelivery.Failed, rig.Registry.DeliverInternal("s1", new InternalInput(Text("report"), null, Done: done.Add)));
        Assert.Equal([false], done);
        rig.Factory.Tweak = null;
        await rig.RunAsync("s1", "another", runId: "r5");
        await Task.Delay(100);
        Assert.Equal([false], done); // 只回调了一次
        Assert.Equal(2, driver().Inputs.Count);
    }

    [Fact]
    public async Task An_input_without_a_done_callback_is_fire_and_forget()
    {
        using var rig = EchoRig(out var driver);
        await Prime(rig);
        Assert.Equal(InternalDelivery.Launched, rig.Registry.DeliverInternal("s1", new InternalInput(Text("goal check"))));
        await Wait.UntilAsync(() => driver().Inputs.Count >= 2);
        await rig.IdleAsync("s1");
    }

    // ── 历史操作占着会话时的有界重试 ──

    [Fact]
    public async Task While_an_operation_holds_the_session_a_bounded_retry_delivers_it_once_the_session_frees_up()
    {
        using var rig = EchoRig(out var driver, more: o => o.ManualClock = true);
        await Prime(rig);
        var done = new List<bool>();
        var lease = rig.Registry.AcquireOperation("s1");
        Assert.Equal(InternalDelivery.Deferred, rig.Registry.DeliverInternal("s1", new InternalInput(Text("report"), null, Done: done.Add)));
        rig.Manual!.Advance(TimeSpan.FromSeconds(1));
        rig.Manual.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(done);
        Assert.Single(driver().Inputs);

        lease.Dispose();
        rig.Manual.Advance(TimeSpan.FromSeconds(1));
        await Wait.UntilAsync(() => done.Count == 1);
        Assert.Equal([true], done);
        await rig.IdleAsync("s1");
    }

    [Fact]
    public async Task The_retry_gives_up_after_the_limit_and_hands_the_input_back()
    {
        using var rig = EchoRig(out _, more: o =>
        {
            o.ManualClock = true;
            o.Registry = r => r with { DeferredMaxRetries = 3 };
        });
        await Prime(rig);
        var done = new List<bool>();
        using var lease = rig.Registry.AcquireOperation("s1");
        rig.Registry.DeliverInternal("s1", new InternalInput(Text("report"), null, Done: done.Add));
        for (var i = 0; i < 2; i++)
        {
            rig.Manual!.Advance(TimeSpan.FromSeconds(1));
            Assert.Empty(done);
        }

        rig.Manual!.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal([false], done);
        Assert.True(rig.Log.Any("Gave up delivering"));
        rig.Manual.Advance(TimeSpan.FromSeconds(30)); // 没有悬挂的定时器再去叫醒它
        Assert.Equal([false], done);
    }

    [Fact]
    public async Task If_a_run_becomes_active_before_the_retry_fires_the_waiting_input_goes_into_that_runs_queue()
    {
        var gate = new TaskCompletionSource();
        using var rig = EchoRig(out var driver, during: h => h.RunId.Value == "r2" ? gate.Task : Task.CompletedTask, more: o => o.ManualClock = true);
        await Prime(rig);
        var done = new List<bool>();
        var lease = rig.Registry.AcquireOperation("s1");
        rig.Registry.DeliverInternal("s1", new InternalInput(Text("report"), null, Done: done.Add));
        lease.Dispose();
        var run = rig.Start("s1", "next", runId: "r2");
        await Wait.UntilAsync(() => run.HasStarted);
        rig.Manual!.Advance(TimeSpan.FromSeconds(1)); // 定时器到点:会话忙、有 Run 在跑 → 塞进它的引导队列
        gate.SetResult();
        await Rig.Settled(run);
        Assert.Equal([true], done);
        Assert.Contains(driver().Inputs, i => i.StartsWith("r2:report", StringComparison.Ordinal));
    }
}
