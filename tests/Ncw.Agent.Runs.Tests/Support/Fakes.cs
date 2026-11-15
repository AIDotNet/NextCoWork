using System.Text.Json;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Runs;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Json;
using Ncw.Agent.Llm.Routing;
using Ncw.Agent.Storage.Sessions;

namespace Ncw.Agent.Runs.Tests.Support;

public sealed class FakeClock : TimeProvider
{
    private long _ms = 1_800_000_000_000;

    public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Add(ref _ms, 10));
}

public sealed class SeqIds(string prefix = "id") : IIdGenerator
{
    private int _n;

    public string NewId() => $"{prefix}{Interlocked.Increment(ref _n):D4}";
}

public sealed class NoDelay : IDelayer
{
    public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>手动推进的时钟:定时器只在 <see cref="Advance"/> 时触发(看门狗、合并窗口、回收 TTL、续投重试都用它测,不睡真的时间)。</summary>
public sealed class ManualClock : TimeProvider
{
    private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        public long Due = long.MaxValue;
        public long Period = -1;
        public bool Disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate)
            {
                if (Disposed)
                {
                    return false;
                }

                Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock._now + (long)dueTime.TotalMilliseconds;
                Period = period == Timeout.InfiniteTimeSpan ? -1 : (long)period.TotalMilliseconds;
                return true;
            }
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (clock._gate)
            {
                Disposed = true;
                clock._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private long _now;
    private readonly DateTimeOffset _origin = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _origin.AddMilliseconds(_now);
        }
    }

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override long TimestampFrequency => 1000;

    public int PendingTimers
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count(t => t.Due != long.MaxValue);
            }
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        var target = _now + (long)by.TotalMilliseconds;
        while (true)
        {
            ManualTimer? next;
            lock (_gate)
            {
                next = _timers.Where(t => t.Due <= target).OrderBy(t => t.Due).FirstOrDefault();
                if (next is null)
                {
                    _now = target;
                    return;
                }

                _now = Math.Max(_now, next.Due);
                if (next.Period > 0)
                {
                    next.Due += next.Period;
                }
                else
                {
                    next.Due = long.MaxValue;
                }
            }

            next.Fire();
        }
    }
}

public delegate IAsyncEnumerable<LlmEvent> Script(LlmRequest request, ModelRef model, RequestContext context, CancellationToken ct);

public sealed class FakeLlm : ILlmClient
{
    private readonly Queue<Script> _turns = new();
    private readonly object _gate = new();

    public List<LlmRequest> Requests { get; } = [];

    /// <summary>按会话分派脚本(多个会话并发调用上游时,按「谁先到」消费脚本队列不可靠)。返回 null = 走队列。</summary>
    public Func<RequestContext, Script?>? Route { get; set; }

    public FakeLlm Turn(params Script[] scripts)
    {
        lock (_gate)
        {
            foreach (var s in scripts)
            {
                _turns.Enqueue(s);
            }
        }

        return this;
    }

    public IAsyncEnumerable<LlmEvent> StreamAsync(ModelRef model, LlmRequest request, RequestContext context, CancellationToken ct = default)
    {
        Script script;
        lock (_gate)
        {
            Requests.Add(request);
            script = Route?.Invoke(context) ?? (_turns.Count > 0 ? _turns.Dequeue() : Up.Text("ok"));
        }

        return script(request, model, context, ct);
    }
}

public static class Up
{
    public static readonly TokenUsage Usage = new(10, 5);

    public static Script Events(params LlmEvent[] events) => (_, _, _, _) => Gen(events);

    private static async IAsyncEnumerable<LlmEvent> Gen(IEnumerable<LlmEvent> events)
    {
        foreach (var ev in events)
        {
            yield return ev;
            await Task.Yield();
        }
    }

    public static IEnumerable<LlmEvent> TextEvents(string text, StopReason stop = StopReason.EndTurn)
    {
        yield return new MessageStart("fake", "p1", "anthropic", "fake-up");
        yield return new TextDelta(0, text);
        yield return new MessageEnd(stop, Usage);
    }

    public static Script Text(string text) => Events(TextEvents(text).ToArray());

    public static Script Call(string id, string name, string json = "{}") =>
        Events([
            new MessageStart("fake", "p1", "anthropic", "fake-up"),
            new ToolCallStart(0, id, name),
            new ToolCallDelta(0, id, json),
            new ToolCallEnd(0, id),
            new MessageEnd(StopReason.ToolUse, Usage),
        ]);

    /// <summary>等到门被放行才回话(测并发 / 中止 / 续跑时机)。</summary>
    public static Script Gate(Task gate, string text = "ok") => (_, _, _, ct) => GateGen(gate, text, ct);

    private static async IAsyncEnumerable<LlmEvent> GateGen(Task gate, string text, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        yield return new MessageStart("fake", "p1", "anthropic", "fake-up");
        await gate.WaitAsync(ct).ConfigureAwait(false);
        yield return new TextDelta(0, text);
        yield return new MessageEnd(StopReason.EndTurn, Usage);
    }

    public static Script Fail(AgentError error) => (_, _, _, _) => FailGen(error);

    private static async IAsyncEnumerable<LlmEvent> FailGen(AgentError error)
    {
        yield return new MessageStart("fake", "p1", "anthropic", "fake-up");
        yield return new StreamError(error);
        await Task.CompletedTask;
    }

    /// <summary>流到一半卡住直到被取消(测中止)。</summary>
    public static Script Hang(string partial = "par") => (_, _, _, ct) => HangGen(partial, ct);

    private static async IAsyncEnumerable<LlmEvent> HangGen(string partial, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        yield return new MessageStart("fake", "p1", "anthropic", "fake-up");
        yield return new TextDelta(0, partial);
        await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
    }
}

public sealed class FakeTool(string name, Func<ToolInvocation, ToolContext, CancellationToken, Task<ToolResult>>? run = null) : IAgentTool
{
    public int Calls;

    public ToolSpec Info { get; } = new(name, $"{name} tool", Jsn.Parse("""{"type":"object"}"""), new ToolTraits(true, false, false), null, null);

    public async ValueTask<ToolResult> ExecuteAsync(ToolInvocation call, ToolContext ctx, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        return run is null ? ToolResult.Text($"{name} ran") : await run(call, ctx, ct);
    }
}

public sealed class CollectingLog : ILoopDiagnostics
{
    public List<string> Warnings { get; } = [];

    public void Warn(string message, Exception? error = null)
    {
        lock (Warnings)
        {
            Warnings.Add(message);
        }
    }

    public bool Any(string fragment)
    {
        lock (Warnings)
        {
            return Warnings.Any(w => w.Contains(fragment, StringComparison.Ordinal));
        }
    }
}

/// <summary>装配方:模板 → 会话层的请求。</summary>
public sealed class TestFactory : IRunRequestFactory
{
    public List<RunTemplate> Created { get; } = [];

    public Func<RunTemplate, RunRequest, RunRequest>? Tweak { get; set; }

    public RunRequest Create(RunTemplate template)
    {
        lock (Created)
        {
            Created.Add(template);
        }

        var request = new RunRequest(template.RunId, template.Input, template.Options.Model, template.Options.Thinking) { MaxContext = template.Options.MaxContext };
        return Tweak?.Invoke(template, request) ?? request;
    }
}

/// <summary>收事件的接收方。<see cref="Accept"/> 为 false 时模拟控制端发送缓冲满了。</summary>
public sealed class CollectingListener : IRunListener
{
    private readonly object _gate = new();
    private readonly List<RunEventBatch> _batches = [];

    public bool Accept { get; set; } = true;

    public int Resyncs { get; private set; }

    public Exception? ThrowOnDeliver { get; set; }

    public int Deliveries { get; private set; }

    public bool TryDeliver(IReadOnlyList<RunEventBatch> batches)
    {
        lock (_gate)
        {
            Deliveries++;
            if (ThrowOnDeliver is { } error)
            {
                throw error;
            }

            if (!Accept)
            {
                return false;
            }

            _batches.AddRange(batches);
            return true;
        }
    }

    public void Resync(ResyncReason reason)
    {
        lock (_gate)
        {
            Resyncs++;
        }
    }

    public IReadOnlyList<RunEventBatch> Batches
    {
        get
        {
            lock (_gate)
            {
                return [.. _batches];
            }
        }
    }

    public IReadOnlyList<AgentEvent> Events(RunId? run = null)
    {
        lock (_gate)
        {
            return [.. _batches.Where(b => run is null || b.RunId == run).SelectMany(b => b.Events.Select(e => e.Event))];
        }
    }

    public IReadOnlyList<long> Seqs(RunId? run = null)
    {
        lock (_gate)
        {
            return [.. _batches.Where(b => run is null || b.RunId == run).SelectMany(b => b.Events.Select(e => e.Seq))];
        }
    }
}

public static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, int timeoutMs = 30_000, string? what = null)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException($"timed out waiting for {what ?? "condition"}");
            }

            await Task.Delay(5);
        }
    }
}

public static class Msg
{
    public static Message User(string id, string text) =>
        new(new MessageId(id), Role.User, [new TextPart(text)], DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000));
}

public static class Opts
{
    public static readonly SendOptions Default = new(ModelRef.Unpinned("claude-sonnet-4"), ThinkingLevel.Auto, false, "code", "default");
}

/// <summary>每个事件到达时回调一次(在订阅的发送线程上,探测「当时」注册表是什么状态)。</summary>
public sealed class ProbeListener(Action<AgentEvent> onEvent) : IRunListener
{
    public bool TryDeliver(IReadOnlyList<RunEventBatch> batches)
    {
        foreach (var ev in batches.SelectMany(b => b.Events))
        {
            onEvent(ev.Event);
        }

        return true;
    }

    public void Resync(ResyncReason reason)
    {
    }
}
