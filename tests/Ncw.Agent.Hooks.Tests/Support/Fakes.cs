using System.Runtime.CompilerServices;
using System.Text.Json;
using Ncw.Agent.Environments;
using Ncw.Agent.Extensions.Planning;
using Ncw.Agent.Extensions.Planning.Goals;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Json;
using Ncw.Agent.Llm.Routing;

namespace Ncw.Agent.Hooks.Tests.Support;

public sealed class SeqIds(string prefix = "id") : IIdGenerator
{
    private int _n;

    public string NewId() => $"{prefix}{Interlocked.Increment(ref _n):D4}";
}

public sealed class NoDelay : IDelayer
{
    public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.CompletedTask;
}

public sealed class CollectingLog : ILoopDiagnostics
{
    public List<string> Warnings { get; } = [];

    public void Warn(string message, Exception? error = null)
    {
        lock (Warnings)
        {
            Warnings.Add(error is null ? message : $"{message} {error.Message}");
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

/// <summary>手动推进的时钟:定时器只在 <see cref="Advance"/> 时触发(空闲检查的 30 / 60 / 120 分钟、判定的墙钟都用它测,不睡真的时间)。</summary>
public sealed class ManualClock : TimeProvider
{
    private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        public long Due = long.MaxValue;
        public long Period = -1;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate)
            {
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
    private readonly DateTimeOffset _origin = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);
    private long _now;

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
        for (;;)
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
                next.Due = next.Period > 0 ? next.Due + next.Period : long.MaxValue;
            }

            next.Fire();
        }
    }
}

public delegate IAsyncEnumerable<LlmEvent> Script(LlmRequest request, CancellationToken ct);

/// <summary>
/// 假上游。主模型的请求按脚本队列一次一个回复;<b>判定器</b>的请求(system 恰好是 <see cref="GoalPrompts.EvaluatorSystem"/>)走另一条队列——
/// 这和 2.x 接线测试里认判定请求的判据一样。两条队列耗尽后:主模型说一句话收尾,判定器回「未达成」。
/// </summary>
public sealed class FakeLlm : ILlmClient
{
    private readonly object _gate = new();
    private readonly Queue<Script> _main = new();
    private readonly Queue<Script> _judge = new();

    public List<LlmRequest> MainRequests { get; } = [];

    public List<LlmRequest> JudgeRequests { get; } = [];

    public List<ModelRef> JudgeModels { get; } = [];

    public FakeLlm Main(params Script[] scripts)
    {
        lock (_gate)
        {
            foreach (var s in scripts)
            {
                _main.Enqueue(s);
            }
        }

        return this;
    }

    public FakeLlm Judge(params Script[] scripts)
    {
        lock (_gate)
        {
            foreach (var s in scripts)
            {
                _judge.Enqueue(s);
            }
        }

        return this;
    }

    public IAsyncEnumerable<LlmEvent> StreamAsync(ModelRef model, LlmRequest request, RequestContext context, CancellationToken ct = default)
    {
        Script script;
        lock (_gate)
        {
            if (request.System == GoalPrompts.EvaluatorSystem)
            {
                JudgeRequests.Add(request);
                JudgeModels.Add(model);
                script = _judge.Count > 0 ? _judge.Dequeue() : Up.Verdict(false, "no scripted verdict");
            }
            else
            {
                MainRequests.Add(request);
                script = _main.Count > 0 ? _main.Dequeue() : Up.Text("ok");
            }
        }

        return script(request, ct);
    }
}

public static class Up
{
    public static readonly TokenUsage Usage = new(10, 5);

    public static Script Events(params LlmEvent[] events) => (_, _) => Gen(events);

    private static async IAsyncEnumerable<LlmEvent> Gen(IEnumerable<LlmEvent> events)
    {
        foreach (var ev in events)
        {
            yield return ev;
            await Task.Yield();
        }
    }

    public static Script Text(string text) => Events(
        new MessageStart("fake", "p1", "anthropic", "fake-up"), new TextDelta(0, text), new MessageEnd(StopReason.EndTurn, Usage));

    /// <summary>一轮工具调用(可以带前导文字),停因是 tool_use。</summary>
    public static Script Calls(params (string Id, string Name, string Json)[] calls)
    {
        var events = new List<LlmEvent> { new MessageStart("fake", "p1", "anthropic", "fake-up") };
        var index = 0;
        foreach (var (id, name, json) in calls)
        {
            events.Add(new ToolCallStart(index, id, name));
            events.Add(new ToolCallDelta(index, id, json));
            events.Add(new ToolCallEnd(index, id));
            index++;
        }

        events.Add(new MessageEnd(StopReason.ToolUse, Usage));
        return Events([.. events]);
    }

    public static Script Call(string id, string name, string json) => Calls((id, name, json));

    /// <summary>脚本内容要等到这一轮才知道(例如计划文件的路径是 EnterPlanMode 跑完才有的):到点再造。</summary>
    public static Script Lazy(Func<Script> make) => (req, ct) => make()(req, ct);

    /// <summary>判定器的一轮:完整的 end_turn 回复,正文是判定 JSON。</summary>
    public static Script Verdict(bool ok, string reason, bool impossible = false) =>
        Text(impossible
            ? new System.Text.Json.Nodes.JsonObject { ["ok"] = ok, ["impossible"] = true, ["reason"] = reason }.ToJsonString()
            : new System.Text.Json.Nodes.JsonObject { ["ok"] = ok, ["reason"] = reason }.ToJsonString());

    public static Script Raw(string text) => Text(text);

    public static Script Hang() => (_, ct) => HangGen(ct);

    private static async IAsyncEnumerable<LlmEvent> HangGen([EnumeratorCancellation] CancellationToken ct)
    {
        yield return new MessageStart("fake", "p1", "anthropic", "fake-up");
        await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
    }

    /// <summary>完全不看令牌、永远不结束的流(只有 Abortable 能从它那里脱身)。</summary>
    public static Script Stuck() => (_, _) => StuckGen();

    private static async IAsyncEnumerable<LlmEvent> StuckGen()
    {
        await new TaskCompletionSource().Task.ConfigureAwait(false);
        yield return new MessageStart("fake", "p1", "anthropic", "fake-up");
    }

    public static Script Fail(AgentErrorCode code) => Events(new MessageStart("fake", "p1", "anthropic", "fake-up"), new StreamError(AgentError.Create(code, "scripted failure", false)));

    /// <summary>门放行之前不回话(测「判定途中发生了什么」)。</summary>
    public static Script Gated(Task gate, Script then) => (req, ct) => GatedGen(gate, then, req, ct);

    private static async IAsyncEnumerable<LlmEvent> GatedGen(Task gate, Script then, LlmRequest req, [EnumeratorCancellation] CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        await foreach (var ev in then(req, ct).WithCancellation(ct).ConfigureAwait(false))
        {
            yield return ev;
        }
    }

    /// <summary>流的第一个事件之前先跑一段副作用(「判到一半用户清掉了目标」)。</summary>
    public static Script Before(Action action, Script then) => (req, ct) =>
    {
        action();
        return then(req, ct);
    };
}

public static class Wait
{
    public static async Task<T> UntilNotNull<T>(Func<T?> get, int timeoutMs = 30_000, string? what = null)
        where T : class
    {
        T? value = null;
        await UntilAsync(() => (value = get()) is not null, timeoutMs, what);
        return value!;
    }

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

public static class JsonExt
{
    public static JsonElement Json(this string text) => Jsn.Parse(text);
}

public static class Msg
{
    public static Message User(string id, string text, bool internalMessage = false) =>
        new(new MessageId(id), Role.User, [new TextPart(text)], DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000), internalMessage);

    public static Message Assistant(string id, params Part[] parts) =>
        new(new MessageId(id), Role.Assistant, parts, DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000));

    public static Message Say(string id, string text) => Assistant(id, new TextPart(text));

    public static Message ToolCall(string id, string callId, string name, string json) =>
        Assistant(id, new ToolCallPart(callId, name, json.Json()));

    public static Message ToolResult(string id, string callId, string text = "ok", bool isError = false) =>
        new(new MessageId(id), Role.User, [new ToolResultPart(callId, new ToolOutput(text), isError)], DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000));
}

/// <summary>待办入参的 JSON。</summary>
public static class TodoJson
{
    public static string List(params (string Content, string Status)[] items) =>
        new System.Text.Json.Nodes.JsonObject
        {
            ["todos"] = Jsn.ArrayOf(items.Select(i => (System.Text.Json.Nodes.JsonNode?)new System.Text.Json.Nodes.JsonObject
            {
                ["content"] = i.Content,
                ["status"] = i.Status,
                ["activeForm"] = $"Doing {i.Content}",
            })),
        }.ToJsonString();
}

/// <summary>工具入参的 JSON(不走反射序列化:测试项目也开着 AOT 分析器)。</summary>
public static class J
{
    public static string Obj(params (string Name, string Value)[] props)
    {
        var o = new System.Text.Json.Nodes.JsonObject();
        foreach (var (name, value) in props)
        {
            o[name] = value;
        }

        return o.ToJsonString();
    }
}
