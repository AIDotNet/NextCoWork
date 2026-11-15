using Ncw.Agent.Kernel.Subagents;

namespace Ncw.Agent.Subagents.Tests;

/// <summary>
/// 子代理并发队列。移植自 2.x 的 <c>subagent-queue.test.ts</c>。这里的赌注是<b>「满了」不等于「失败」</b>:撞上上限的派发要排队,只有在真的没有出路时(禁用、死锁、队列过长)才退回拒绝。
/// 所以每条用例都盯着同一件事:<b>这次派发最后到底跑没跑</b>。占用量与上限都是注进来的,整份测试零 Run 注册表、零真实时钟(<see cref="ManualClock"/>)。
/// </summary>
public sealed class SubagentQueueTests : IDisposable
{
    /// <summary>假注册表:childRunId → parentRunId,只装「还在跑的」。</summary>
    private sealed class World : ISlotOccupancy
    {
        private readonly Dictionary<string, string> _running = [];
        private int _seq;

        public SlotCapacity Capacity { get; set; } = new(1, 4);

        public IReadOnlyList<string> ChildRunIds(string parentRunId)
        {
            lock (_running)
            {
                return [.. _running.Where(p => p.Value == parentRunId).Select(p => p.Key)];
            }
        }

        public IReadOnlyList<string> SubagentRunIds()
        {
            lock (_running)
            {
                return [.. _running.Keys];
            }
        }

        /// <summary>模拟「一个子 Run 起来了」。用作 <c>SlotRequest.Start</c>,所以必须是同步的。</summary>
        public string Launch(string parentRunId, string? id = null)
        {
            lock (_running)
            {
                var child = id ?? $"{parentRunId}:sub:{++_seq}";
                _running[child] = parentRunId;
                return child;
            }
        }

        public void Stop(string childRunId)
        {
            lock (_running)
            {
                _running.Remove(childRunId);
            }
        }
    }

    private sealed class FakeParent(string runId) : IParentSignal
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly List<Action> _endCallbacks = [];

        public string RunId => runId;

        public bool IsActive => !Ended && !_cts.IsCancellationRequested;

        public bool Ended { get; private set; }

        public CancellationToken Token => _cts.Token;

        public IDisposable OnEnded(Action callback)
        {
            lock (_endCallbacks)
            {
                _endCallbacks.Add(callback);
            }

            return new Registration(this, callback);
        }

        public void Abort() => _cts.Cancel();

        public void End()
        {
            Ended = true;
            Action[] callbacks;
            lock (_endCallbacks)
            {
                callbacks = [.. _endCallbacks];
            }

            foreach (var callback in callbacks)
            {
                callback();
            }
        }

        private sealed class Registration(FakeParent parent, Action callback) : IDisposable
        {
            public void Dispose()
            {
                lock (parent._endCallbacks)
                {
                    parent._endCallbacks.Remove(callback);
                }
            }
        }
    }

    private readonly World _world = new();
    private readonly ManualClock _clock = new();
    private readonly SubagentQueue _queue;

    public SubagentQueueTests() => _queue = new SubagentQueue(() => _world.Capacity, _world, _clock);

    public void Dispose() => _queue.Dispose();

    /// <summary>派一次。返回任务与「排到第几位」的回调记录。</summary>
    private (Task<SlotResult<string>> Slot, List<int> Positions) Dispatch(FakeParent parent, bool blocking = true, string? id = null)
    {
        var positions = new List<int>();
        var slot = _queue.Acquire(new SlotRequest<string>(parent, blocking, () => _world.Launch(parent.RunId, id), ahead => positions.Add(ahead)));
        return (slot, positions);
    }

    private static async Task<string> Granted(Task<SlotResult<string>> slot) => Assert.IsType<SlotResult<string>.Granted>(await slot).Value;

    private static async Task<SlotRefusal> Refused(Task<SlotResult<string>> slot) => Assert.IsType<SlotResult<string>.Refused>(await slot).Reason;

    [Fact]
    public async Task When_full_it_queues_and_starts_in_arrival_order_as_slots_free_up()
    {
        var parent = new FakeParent("p");
        var first = Dispatch(parent, id: "c1");
        Assert.Empty(first.Positions); // 直接拿到名额的不回调,启动器靠这条区分「已启动 / 已排队」
        Assert.Equal("c1", await Granted(first.Slot));

        var second = Dispatch(parent, id: "c2");
        var third = Dispatch(parent, id: "c3");
        Assert.Equal(2, _queue.Size);
        Assert.Equal([0], second.Positions);
        Assert.Equal([1], third.Positions);

        // ★ 关键断言:第二、三次派发没有被拒,它们只是还没轮到。
        _world.Stop("c1");
        _queue.Notify();
        Assert.Equal("c2", await Granted(second.Slot));
        Assert.Equal(1, _queue.Size);
        Assert.Equal([1, 0], third.Positions); // 排位前移了就再报一次

        _world.Stop("c2");
        _queue.Notify();
        Assert.Equal("c3", await Granted(third.Slot));
        Assert.Equal(0, _queue.Size);
    }

    [Fact]
    public async Task A_head_that_is_stuck_does_not_block_a_request_in_another_conversation_that_fits()
    {
        var p = new FakeParent("p");
        var q = new FakeParent("q");
        await Granted(Dispatch(p, id: "p1").Slot);
        await Granted(Dispatch(q, id: "q1").Slot);

        var pWaiter = Dispatch(p); // 队头,p 的名额满着
        var qWaiter = Dispatch(q, id: "q2");
        Assert.Equal(2, _queue.Size);

        _world.Stop("q1");
        _queue.Notify();

        Assert.Equal("q2", await Granted(qWaiter.Slot));
        Assert.Equal(1, _queue.Size); // 队头还在等,而后面那个已经跑了
        GC.KeepAlive(pWaiter);
    }

    [Fact]
    public async Task A_global_limit_of_zero_refuses_at_once_and_never_queues()
    {
        _world.Capacity = new SlotCapacity(1, 0);

        var (slot, _) = Dispatch(new FakeParent("p"));

        Assert.Equal(SlotRefusal.Disabled, await Refused(slot));
        Assert.Equal(0, _queue.Size);
        Assert.Equal(0, _clock.PendingTimers); // 一个定时器都没建:它不是「等一等」,是「不准」
    }

    [Fact]
    public async Task When_every_holder_is_itself_waiting_for_a_slot_it_is_a_deadlock_and_refused_on_the_spot()
    {
        _world.Capacity = new SlotCapacity(2, 2);
        var p = new FakeParent("p");
        await Granted(Dispatch(p, id: "a").Slot);
        await Granted(Dispatch(p, id: "b").Slot);

        // a 去派孙代理——全局满了,它排进队列,于是 a 自己也卡住了
        var grandchildOfA = Dispatch(new FakeParent("a"));
        Assert.Equal(1, _queue.Size);

        // 现在 b 也去派孙代理:占着两个名额的 a、b 都在等,谁也腾不出位子。
        // ★ 判定时自己也算卡住的,否则 b 会以为 a 能救它,白等一个永远不会来的名额。
        Assert.Equal(SlotRefusal.Deadlock, await Refused(Dispatch(new FakeParent("b")).Slot));
        Assert.Equal(1, _queue.Size);
        GC.KeepAlive(grandchildOfA);
    }

    [Fact]
    public async Task A_deadlock_that_forms_later_is_caught_on_the_next_rescan_for_the_one_already_waiting()
    {
        // 两段对话:p 名下跑着 a、b(会话名额 2 用满),q 名下跑着 c(全局名额 3 用满)。
        // W 入队的那一刻 a、b 都在好好跑着——这不是死锁,是拥塞,所以它该等。
        _world.Capacity = new SlotCapacity(2, 3);
        var p = new FakeParent("p");
        var q = new FakeParent("q");
        await Granted(Dispatch(p, id: "a").Slot);
        await Granted(Dispatch(p, id: "b").Slot);
        await Granted(Dispatch(q, id: "c").Slot);

        var waiting = Dispatch(p);
        Assert.Equal(1, _queue.Size);

        // 随后 a、b 各自也去派孙代理,双双卡在全局闸门上——p 的会话名额于是没人能腾。
        Dispatch(new FakeParent("a"));
        Dispatch(new FakeParent("b"));

        _clock.Advance(SubagentQueue.TickInterval); // 兜底重扫,不必有人来 Notify
        Assert.Equal(SlotRefusal.Deadlock, await Refused(waiting.Slot));
    }

    [Fact]
    public async Task A_background_waiter_does_not_count_as_stuck_its_parent_is_still_doing_work()
    {
        _world.Capacity = new SlotCapacity(1, 1);
        var p = new FakeParent("p");
        await Granted(Dispatch(p, id: "a").Slot);

        // a 派了一个后台子代理,排着队;但 a 本身没停,随时会跑完腾出名额
        var child = new FakeParent("a");
        Dispatch(child, blocking: false);

        // 所以 p 的下一次派发应当排队,而不是被误判成死锁
        var next = Dispatch(p);
        Assert.Equal(2, _queue.Size);
        Assert.Single(next.Positions);
    }

    [Fact]
    public async Task Queueing_has_no_wall_clock_limit_while_the_one_in_front_runs_for_half_an_hour_the_one_behind_waits_half_an_hour()
    {
        var p = new FakeParent("p");
        await Granted(Dispatch(p, id: "a").Slot);
        Dispatch(p);

        _clock.Advance(TimeSpan.FromMinutes(60));

        Assert.Equal(1, _queue.Size);
    }

    [Fact]
    public async Task Too_many_queued_are_refused_the_model_cannot_pile_up_a_hundred_at_once()
    {
        var p = new FakeParent("p");
        await Granted(Dispatch(p, id: "a").Slot);
        for (var i = 0; i < SubagentQueue.MaxQueuedPerParent; i++)
        {
            Dispatch(p);
        }

        Assert.Equal(SubagentQueue.MaxQueuedPerParent, _queue.Size);
        Assert.Equal(SlotRefusal.Overflow, await Refused(Dispatch(p).Slot));
    }

    [Fact]
    public async Task The_global_queue_cap_applies_across_parents()
    {
        _world.Capacity = new SlotCapacity(1, 1000);
        var parents = Enumerable.Range(0, 5).Select(i => new FakeParent($"p{i}")).ToList();
        foreach (var parent in parents)
        {
            await Granted(Dispatch(parent, id: $"run-{parent.RunId}").Slot);
        }

        // 每个父 Run 排满 16 个会先撞每父上限;4 个父 Run × 16 = 64 刚好撞全局上限
        for (var i = 0; i < 4; i++)
        {
            for (var j = 0; j < SubagentQueue.MaxQueuedPerParent; j++)
            {
                Dispatch(parents[i]);
            }
        }

        Assert.Equal(SubagentQueue.MaxQueuedTotal, _queue.Size);
        Assert.Equal(SlotRefusal.Overflow, await Refused(Dispatch(parents[4]).Slot));
    }

    // ★ 中止和正常结束分两条用例,因为它们在 RunHandle 上是两条完全不同的路:中止只触发取消令牌、不改状态;结束发 run_end、不碰令牌。
    // 只挂一个监听的话,漏掉的那一条上的等待者会永久留在队列里。
    [Fact]
    public async Task Invariant_a_parent_that_is_aborted_wakes_its_waiters()
    {
        var p = new FakeParent("p");
        await Granted(Dispatch(p, id: "a").Slot);
        var (slot, _) = Dispatch(p);

        p.Abort();

        Assert.Equal(SlotRefusal.Cancelled, await Refused(slot));
        Assert.Equal(0, _queue.Size);
    }

    [Fact]
    public async Task Invariant_a_parent_that_ends_normally_wakes_its_waiters()
    {
        var p = new FakeParent("p");
        await Granted(Dispatch(p, id: "a").Slot);
        var (slot, _) = Dispatch(p);

        p.End();

        Assert.Equal(SlotRefusal.Cancelled, await Refused(slot));
        Assert.Equal(0, _queue.Size);
    }

    [Fact]
    public async Task A_dispatch_from_a_parent_that_is_already_gone_is_refused_without_entering_the_queue()
    {
        var p = new FakeParent("p");
        p.End();

        Assert.Equal(SlotRefusal.Cancelled, await Refused(Dispatch(p).Slot));
        Assert.Equal(0, _queue.Size);
    }

    [Fact]
    public async Task Raising_the_limit_at_runtime_takes_effect_on_the_next_rescan()
    {
        var p = new FakeParent("p");
        await Granted(Dispatch(p, id: "a").Slot);
        var (slot, _) = Dispatch(p, id: "b");

        _world.Capacity = new SlotCapacity(2, 4); // 用户在设置页改了并发,没有人会来 Notify
        _clock.Advance(SubagentQueue.TickInterval);

        Assert.Equal("b", await Granted(slot));
    }

    [Fact]
    public async Task Reset_settles_every_waiter_and_stops_the_timer()
    {
        var p = new FakeParent("p");
        await Granted(Dispatch(p, id: "a").Slot);
        var (slot, _) = Dispatch(p);
        Assert.True(_clock.PendingTimers > 0);

        _queue.Reset();

        Assert.Equal(SlotRefusal.Cancelled, await Refused(slot));
        Assert.Equal(0, _queue.Size);
        Assert.Equal(0, _clock.PendingTimers); // 留着的话,下一个用例会被上一个的定时器唤醒
    }

    [Fact]
    public async Task Invariant_the_start_callback_runs_inline_so_a_freed_slot_goes_to_exactly_one_of_many_waiters()
    {
        // 这是整个队列避开 TOCTOU 的唯一手段:同一个空位被唤醒的 N 个等待者同时认领的话,上限就被悄悄打穿了。
        _world.Capacity = new SlotCapacity(1, 1);
        var parents = Enumerable.Range(0, 8).Select(i => new FakeParent($"p{i}")).ToList();
        await Granted(Dispatch(parents[0], id: "first").Slot);
        var waiters = parents.Skip(1).Select(p => Dispatch(p)).ToList();

        _world.Stop("first");
        _queue.Notify();

        Assert.Single(_world.SubagentRunIds());
        Assert.Equal(6, _queue.Size);
        Assert.Equal(1, waiters.Count(w => w.Slot.IsCompleted));
    }

    [Fact]
    public async Task Hammering_acquire_and_notify_from_many_threads_never_exceeds_the_limit()
    {
        _world.Capacity = new SlotCapacity(3, 3);
        var parent = new FakeParent("p");
        var peak = 0;
        var tasks = Enumerable.Range(0, 200).Select(_ => Task.Run(async () =>
        {
            var result = await _queue.Acquire(new SlotRequest<string>(parent, false, () =>
            {
                var id = _world.Launch("p");
                lock (_world)
                {
                    peak = Math.Max(peak, _world.SubagentRunIds().Count);
                }

                return id;
            }));
            if (result is SlotResult<string>.Granted granted)
            {
                await Task.Yield();
                _world.Stop(granted.Value);
                _queue.Notify();
            }
        })).ToList();

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(peak <= 3, $"peak was {peak}");
    }

    [Fact]
    public void Capacity_normalisation_keeps_zero_as_a_legal_global_limit_and_repairs_nonsense()
    {
        Assert.Equal(new SlotCapacity(4, 0), SlotCapacity.Normalize(4, 0));
        Assert.Equal(new SlotCapacity(4, 4), SlotCapacity.Normalize(0, -1));
        Assert.Equal(new SlotCapacity(2, 8), SlotCapacity.Normalize(2, 8));
    }

    [Fact]
    public async Task A_start_that_throws_fails_that_dispatch_only_and_leaves_the_queue_working()
    {
        var p = new FakeParent("p");
        await Granted(Dispatch(p, id: "a").Slot);
        var bad = _queue.Acquire(new SlotRequest<string>(p, true, () => throw new InvalidOperationException("launch refused")));
        var good = Dispatch(p, id: "c");

        _world.Stop("a");
        _queue.Notify();

        await Assert.ThrowsAsync<InvalidOperationException>(() => bad);
        // 失败的那个没有占着名额:后面的照样能拿到
        _world.Stop("c"); // (保证即使 c 先拿到也不会卡住)
        _queue.Notify();
        Assert.Equal("c", await Granted(good.Slot).WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
