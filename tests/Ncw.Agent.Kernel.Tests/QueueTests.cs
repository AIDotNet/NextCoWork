using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Tests.Support;
using Ncw.Agent.Llm;

namespace Ncw.Agent.Kernel.Tests;

/// <summary>两种输入队列(02 §5):steer 取走即消费;follow-up 只在 Run 以 done 结束后交出。</summary>
public sealed class QueueTests
{
    private static QueuedInput Item(string id, string text = "x") => new(new MessageId(id), [new TextPart(text)]);

    private static RunInputQueues Queues() => new(new FakeClock());

    [Fact]
    public void Only_promoted_items_are_steering_and_taking_them_consumes_them()
    {
        var q = Queues();
        q.Enqueue(Item("a"));
        q.Enqueue(Item("b"));
        q.TogglePromote(new MessageId("b"));

        var taken = q.TakeSteering();

        Assert.Equal(["b"], taken.Select(m => m.Id.Value));
        Assert.Equal(Role.User, taken[0].Role);
        Assert.Empty(q.TakeSteering()); // 取走即消费,不会重复注入
        Assert.Equal(["a"], q.Snapshot().Entries.Select(e => e.Input.Id.Value)); // pending 一条都不捎带
    }

    [Fact]
    public void Promoted_items_come_out_in_the_order_they_were_promoted_not_the_order_they_were_queued()
    {
        var q = Queues();
        q.Enqueue(Item("a"));
        q.Enqueue(Item("b"));
        q.Enqueue(Item("c"));
        q.TogglePromote(new MessageId("c"));
        q.TogglePromote(new MessageId("a"));

        Assert.Equal(["c", "a"], q.TakeSteering().Select(m => m.Id.Value));
    }

    [Fact]
    public void Un_promoting_and_promoting_again_goes_to_the_back_of_the_promoted_ones()
    {
        var q = Queues();
        q.Enqueue(Item("a"));
        q.Enqueue(Item("b"));
        q.TogglePromote(new MessageId("a"));
        q.TogglePromote(new MessageId("b"));
        q.TogglePromote(new MessageId("a")); // 取消
        q.TogglePromote(new MessageId("a")); // 再引入

        Assert.Equal(["b", "a"], q.TakeSteering().Select(m => m.Id.Value));
    }

    [Fact]
    public void Editing_changes_the_content_but_neither_the_state_nor_the_promotion_order()
    {
        var q = Queues();
        q.Enqueue(Item("a", "old"));
        q.Enqueue(Item("b"));
        q.TogglePromote(new MessageId("a"));
        q.TogglePromote(new MessageId("b"));

        Assert.True(q.Edit(new MessageId("a"), [new TextPart("new")]));

        var taken = q.TakeSteering();
        Assert.Equal(["a", "b"], taken.Select(m => m.Id.Value));
        Assert.Equal("new", ((TextPart)taken[0].Parts[0]).Text);
    }

    [Fact]
    public void Internal_steering_survives_user_side_edits_and_is_marked_internal()
    {
        var q = Queues();
        Assert.True(q.EnqueueInternalSteering(Item("bg-report")));
        q.Enqueue(Item("a"));
        q.Remove(new MessageId("a"));

        var taken = q.TakeSteering();

        var message = Assert.Single(taken);
        Assert.True(message.Internal);
        Assert.Equal("bg-report", message.Id.Value);
    }

    [Fact]
    public void A_closed_queue_rejects_internal_steering_because_the_run_is_over()
    {
        var q = Queues();
        q.Close();

        Assert.False(q.EnqueueInternalSteering(Item("late")));
    }

    [Fact]
    public void Steering_skips_items_without_content()
    {
        var q = Queues();
        q.Enqueue(new QueuedInput(new MessageId("e"), []));
        q.TogglePromote(new MessageId("e"));

        Assert.Empty(q.TakeSteering());
    }

    [Fact]
    public void Follow_ups_are_handed_out_only_after_a_run_that_ended_done_promoted_first_then_pending()
    {
        var q = Queues();
        q.Enqueue(Item("a"));
        q.Enqueue(Item("b"));
        q.Enqueue(Item("c"));
        q.TogglePromote(new MessageId("c"));

        var followUps = q.TakeFollowUps(RunStatus.Done);

        Assert.Equal(["c", "a", "b"], followUps.Select(i => i.Id.Value));
        Assert.Empty(q.Snapshot().Entries);
    }

    [Theory]
    [InlineData(RunStatus.Aborted)]
    [InlineData(RunStatus.Error)]
    public void An_aborted_or_failed_run_does_not_auto_continue_and_the_items_stay_queued(RunStatus status)
    {
        var q = Queues();
        q.Enqueue(Item("a"));

        Assert.Empty(q.TakeFollowUps(status));
        Assert.Single(q.Snapshot().Entries);
    }

    [Fact]
    public void The_queue_has_a_soft_cap_and_rejects_duplicate_ids()
    {
        var q = Queues();
        for (var i = 0; i < RunInputQueues.MaxItems; i++)
        {
            Assert.True(q.Enqueue(Item("i" + i)));
        }

        Assert.False(q.Enqueue(Item("overflow")));
        var q2 = Queues();
        q2.Enqueue(Item("dup"));
        Assert.False(q2.Enqueue(Item("dup")));
    }

    [Fact]
    public void The_revision_increases_on_every_change_so_clients_can_drop_stale_broadcasts()
    {
        var q = Queues();
        var r0 = q.Snapshot().Rev;
        q.Enqueue(Item("a"));
        var r1 = q.Snapshot().Rev;
        q.TogglePromote(new MessageId("a"));
        var r2 = q.Snapshot().Rev;
        q.TakeSteering();
        var r3 = q.Snapshot().Rev;

        Assert.True(r0 < r1 && r1 < r2 && r2 < r3);
    }

    [Fact]
    public async Task The_queue_plugs_into_the_loop_and_a_promoted_item_is_delivered_in_the_running_run()
    {
        var queues = Queues();
        queues.Enqueue(Item("q1", "also check the logs"));
        var rig = new Rig { TakeSteering = queues.TakeSteering };
        rig.Tool(new FakeTool("echo", (_, _, _) =>
        {
            queues.TogglePromote(new MessageId("q1")); // 工具跑着的时候用户点了「插话」
            return Task.FromResult(Ncw.Agent.Kernel.Tools.ToolResult.Text("ok"));
        }));
        rig.Llm.Enqueue(Up.Call("c1", "echo"), Up.Text("done"));

        var run = await rig.RunAsync(Rig.User("u1", "go"));

        Assert.Contains(run.Of<MessageEndEvent>(), e => e.MessageId.Value == "q1");
        Assert.Empty(queues.Snapshot().Entries);
    }

    // ── 编排层(Ncw.Agent.Runs)要用的两个追加:Restore / TakeInternal ──

    [Fact]
    public void Restore_replaces_the_user_side_with_exactly_the_given_state_and_order_and_leaves_internal_messages_alone()
    {
        var q = Queues();
        q.Enqueue(Item("a"));
        q.EnqueueInternalSteering(Item("report"));
        q.Restore([
            new QueueEntry(Item("x"), QueueState.Promoted, 5),
            new QueueEntry(Item("y"), QueueState.Pending, 0),
            new QueueEntry(Item("z"), QueueState.Promoted, 2),
        ]);

        var snapshot = q.Snapshot();
        Assert.Equal(["x", "y", "z"], snapshot.Entries.Select(e => e.Input.Id.Value));
        Assert.Equal(["report"], snapshot.InternalSteering.Select(i => i.Id.Value));
        Assert.Equal(["z", "x", "report"], q.TakeSteering().Select(m => m.Id.Value)); // 已引入的按 PromotedOrder,然后是内部消息
    }

    [Fact]
    public void After_a_restore_the_next_promotion_is_counted_after_the_largest_existing_order_and_the_revision_moves()
    {
        var q = Queues();
        var before = q.Snapshot().Rev;
        q.Restore([new QueueEntry(Item("x"), QueueState.Promoted, 9), new QueueEntry(Item("y"), QueueState.Pending, 0)]);
        Assert.True(q.Snapshot().Rev > before);
        q.TogglePromote(new MessageId("y"));
        Assert.Equal(["x", "y"], q.TakeSteering().Select(m => m.Id.Value));
    }

    [Fact]
    public void Restoring_a_snapshot_taken_earlier_is_an_exact_rollback()
    {
        var q = Queues();
        q.Enqueue(Item("a"));
        q.Enqueue(Item("b"));
        q.TogglePromote(new MessageId("b"));
        q.TogglePromote(new MessageId("a"));
        var before = q.Snapshot().Entries;
        q.TogglePromote(new MessageId("b"));
        q.Remove(new MessageId("a"));
        q.Restore(before);
        Assert.Equal(["b", "a"], q.TakeSteering().Select(m => m.Id.Value));
    }

    [Fact]
    public void TakeInternal_removes_only_the_internal_messages_so_the_next_runs_TakeSteering_cannot_pick_them_up_again()
    {
        var q = Queues();
        q.Enqueue(Item("a"));
        q.TogglePromote(new MessageId("a"));
        q.EnqueueInternalSteering(Item("r1"));
        q.EnqueueInternalSteering(Item("r2"));

        var taken = q.TakeInternal();
        Assert.Equal(["r1", "r2"], taken.Select(i => i.Id.Value));
        Assert.All(taken, i => Assert.True(i.Internal));
        Assert.Empty(q.TakeInternal());
        Assert.Equal(["a"], q.TakeSteering().Select(m => m.Id.Value)); // 用户侧条目不受影响
    }
}
