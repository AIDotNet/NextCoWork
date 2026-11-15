using Ncw.Agent.Kernel.Tests.Support;
using Ncw.Agent.Kernel.Tools;

namespace Ncw.Agent.Kernel.Tests;

public sealed class ToolProtocolTests
{
    [Fact]
    public async Task The_file_mutation_queue_serializes_work_on_the_same_key_in_fifo_order()
    {
        var queue = new FileMutationQueue();
        var log = new List<string>();
        var release = new TaskCompletionSource();

        var first = queue.RunAsync("/a", async _ =>
        {
            log.Add("1:begin");
            await release.Task;
            log.Add("1:end");
        }, default).AsTask();
        var second = queue.RunAsync("/a", _ =>
        {
            log.Add("2");
            return ValueTask.CompletedTask;
        }, default).AsTask();
        await Task.Delay(50);
        Assert.Equal(["1:begin"], log); // 第二个在排队
        release.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(["1:begin", "1:end", "2"], log);
    }

    [Fact]
    public async Task Different_keys_do_not_block_each_other_and_idle_keys_are_reclaimed()
    {
        var queue = new FileMutationQueue();
        var hold = new TaskCompletionSource();
        var a = queue.RunAsync("/a", _ => new ValueTask(hold.Task), default).AsTask();

        var b = await queue.RunAsync("/b", _ => ValueTask.FromResult(42), default);

        Assert.Equal(42, b);
        Assert.Equal(1, queue.ActiveKeys);
        hold.SetResult();
        await a;
        Assert.Equal(0, queue.ActiveKeys);
    }

    [Fact]
    public async Task A_waiter_that_is_cancelled_leaves_the_queue_usable()
    {
        var queue = new FileMutationQueue();
        var hold = new TaskCompletionSource();
        var holder = queue.RunAsync("/a", _ => new ValueTask(hold.Task), default).AsTask();
        using var cts = new CancellationTokenSource(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await queue.RunAsync("/a", _ => ValueTask.CompletedTask, cts.Token));
        hold.SetResult();
        await holder;

        Assert.Equal(7, await queue.RunAsync("/a", _ => ValueTask.FromResult(7), default));
        Assert.Equal(0, queue.ActiveKeys);
    }

    [Fact]
    public async Task The_queue_honours_a_custom_comparer_for_case_insensitive_file_systems()
    {
        var queue = new FileMutationQueue(StringComparer.OrdinalIgnoreCase);
        var hold = new TaskCompletionSource();
        var running = 0;
        var max = 0;
        async ValueTask Work(CancellationToken _)
        {
            max = Math.Max(max, Interlocked.Increment(ref running));
            await Task.Delay(20);
            Interlocked.Decrement(ref running);
        }

        await Task.WhenAll(queue.RunAsync("/Dir/File.txt", Work, default).AsTask(), queue.RunAsync("/dir/file.TXT", Work, default).AsTask());
        hold.SetResult();

        Assert.Equal(1, max);
    }

    [Fact]
    public void The_snapshot_lists_the_tools_for_the_model_and_looks_them_up_by_external_name()
    {
        var a = new FakeTool("a");
        var snapshot = new ToolSnapshot([new ToolBinding("ext_a", a)]);

        Assert.Equal(["ext_a"], snapshot.ModelTools.Select(t => t.ExternalName));
        Assert.Equal("Fake tool a", snapshot.ModelTools[0].Description);
        Assert.Same(a, snapshot.Find("ext_a"));
        Assert.Null(snapshot.Find("a"));
    }

    [Fact]
    public void The_snapshot_rejects_invalid_and_duplicate_external_names()
    {
        Assert.Throws<ArgumentException>(() => new ToolSnapshot([new ToolBinding("bad name!", new FakeTool("a"))]));
        Assert.Throws<ArgumentException>(() => new ToolSnapshot([new ToolBinding("x", new FakeTool("a")), new ToolBinding("x", new FakeTool("b"))]));
    }

    [Fact]
    public void Tool_traits_have_no_defaults_for_read_only_destructive_or_network_but_default_to_concurrency_safe()
    {
        var traits = new ToolTraits(true, false, true);

        Assert.True(traits.ConcurrencySafe);
        // 编译期保证:new ToolTraits() 或只给部分参数不能通过(ReadOnly / Destructive / NeedsNetwork 是没有默认值的位置参数)。
    }
}
