using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Protocol.Tests;

/// <summary>
/// 幂等结果表(09 §8.1:设备保存 24 小时内的「幂等键 → 结果」)的保留期与上限,以及连接结束回调。
/// 高频 command(终端按键输入是一条 <c>terminals.write</c>)不能让这张表无限增长。
/// </summary>
public sealed class IdempotencyTests
{
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks = 1_000;

        public override long TimestampFrequency => 1_000;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, (long)(by.TotalMilliseconds));
    }

    private static readonly CallContext Owner = new("c1", Role.Owner, Transports.Local);

    private static async Task<ResponseFrame> CallAsync(MethodRouter router, string idem, CallContext? context = null) =>
        await router.InvokeAsync(
            new RequestFrame(1, DeviceMethods.ProjectsRemove.Name, FrameCodec.EncodePayload(new ProjectsRemoveParams("ws"), DeviceMethods.ProjectsRemove.ParamsShape), idem),
            context ?? Owner, CancellationToken.None);

    private static (MethodRouter Router, ManualClock Clock, int[] Calls) Rig()
    {
        var clock = new ManualClock();
        var router = new MethodRouter(clock);
        var calls = new int[1];
        router.Handle(DeviceMethods.ProjectsRemove, (_, _, _) =>
        {
            Interlocked.Increment(ref calls[0]);
            return ValueTask.FromResult(Unit.Value);
        });
        return (router, clock, calls);
    }

    private sealed class Scope(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }

    /// <summary>
    /// 闸门返回的句柄要罩住整个方法实现(成功或失败都在实现结束之后才释放):宿主用它在请求期间占着执行环境,
    /// 提前释放的话,一次比空闲阈值更久的操作会被空闲回收器中途断开。
    /// </summary>
    [Fact]
    public async Task The_scope_returned_by_the_gate_is_held_for_the_whole_call_and_released_afterwards_even_when_the_handler_fails()
    {
        var router = new MethodRouter(new ManualClock());
        var held = 0;
        var heldInsideHandler = -1;
        var fail = false;
        router.BeforeInvoke = (_, _, _, _) =>
        {
            held++;
            return ValueTask.FromResult<IDisposable?>(new Scope(() => held--));
        };
        router.Handle(DeviceMethods.ProjectsRemove, async (_, _, _) =>
        {
            await Task.Yield();
            heldInsideHandler = held;
            return fail ? throw new ProtocolException(ErrorCodes.Conflict, "nope") : Unit.Value;
        });

        Assert.True((await CallAsync(router, "a")).Ok);
        Assert.Equal((1, 0), (heldInsideHandler, held));

        fail = true;
        Assert.False((await CallAsync(router, "b")).Ok);
        Assert.Equal((1, 0), (heldInsideHandler, held));

        // 闸门可以不给句柄
        router.BeforeInvoke = (_, _, _, _) => ValueTask.FromResult<IDisposable?>(null);
        fail = false;
        Assert.True((await CallAsync(router, "c")).Ok);
    }

    [Fact]
    public async Task The_same_key_runs_once_within_the_retention_window()
    {
        var (router, clock, calls) = Rig();

        Assert.True((await CallAsync(router, "k1")).Ok);
        clock.Advance(TimeSpan.FromHours(23));
        Assert.True((await CallAsync(router, "k1")).Ok);

        Assert.Equal(1, calls[0]);
    }

    [Fact]
    public async Task Entries_older_than_the_retention_window_are_evicted_and_the_key_runs_again()
    {
        var (router, clock, calls) = Rig();
        await CallAsync(router, "old");
        clock.Advance(MethodRouter.IdempotencyTtl + TimeSpan.FromMinutes(5));

        await CallAsync(router, "new"); // 触发一次扫描:过期的被淘汰
        Assert.Equal(1, router.IdempotentCount);

        await CallAsync(router, "old");
        Assert.Equal(3, calls[0]);
    }

    [Fact]
    public async Task A_flood_of_distinct_keys_does_not_grow_the_table_beyond_its_cap()
    {
        var (router, clock, _) = Rig();
        for (var i = 0; i < MethodRouter.MaxIdempotentEntries + 15_000; i++)
        {
            await CallAsync(router, "k" + i);
            if (i % 5_000 == 0)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
            }
        }

        Assert.True(router.IdempotentCount <= MethodRouter.MaxIdempotentEntries + 10_000, $"table grew to {router.IdempotentCount}");
    }

    [Fact]
    public void Close_callbacks_run_once_even_when_one_throws_and_late_registrations_run_immediately()
    {
        var context = new CallContext("c1", Role.Member, Transports.Local);
        var order = new List<string>();
        context.OnClosed(() => order.Add("a"));
        context.OnClosed(() => throw new InvalidOperationException("boom"));
        context.OnClosed(() => order.Add("b"));

        context.RaiseClosed();
        context.RaiseClosed();
        context.OnClosed(() => order.Add("late"));

        Assert.Equal(["a", "b", "late"], order);
    }
}
