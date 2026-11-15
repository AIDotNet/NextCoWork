using System.Text.Json;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Json;
using Ncw.Agent.Llm.Routing;

namespace Ncw.Agent.Kernel.Tests.Support;

/// <summary>可手动拨动的时钟(计时器和时间戳仍用系统的)。</summary>
public sealed class FakeClock(long startMs = 1_800_000_000_000) : TimeProvider
{
    private long _ms = startMs;

    public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Increment(ref _ms));
}

public sealed class SequentialIds : IIdGenerator
{
    private int _n;

    public string NewId() => $"id{Interlocked.Increment(ref _n):D4}";
}

/// <summary>记录每次退避并立即返回(不真的睡)。</summary>
public sealed class RecordingDelayer : IDelayer
{
    private readonly List<TimeSpan> _delays = [];

    public IReadOnlyList<TimeSpan> Delays
    {
        get
        {
            lock (_delays)
            {
                return _delays.ToList();
            }
        }
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_delays)
        {
            _delays.Add(delay);
        }

        return Task.CompletedTask;
    }
}

/// <summary>退避睡眠一直等到被取消。</summary>
public sealed class BlockingDelayer : IDelayer
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        Entered.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }
}

public delegate IAsyncEnumerable<LlmEvent> UpstreamScript(LlmRequest request, CancellationToken ct);

/// <summary>假上游:按脚本一次一个请求回复,记录每个请求(验证「发出去的历史」用)。</summary>
public sealed class FakeLlm : ILlmClient
{
    private readonly Queue<UpstreamScript> _scripts = new();

    public List<LlmRequest> Requests { get; } = [];

    public List<RequestContext> Contexts { get; } = [];

    public FakeLlm Enqueue(params UpstreamScript[] scripts)
    {
        foreach (var script in scripts)
        {
            _scripts.Enqueue(script);
        }

        return this;
    }

    public IAsyncEnumerable<LlmEvent> StreamAsync(ModelRef model, LlmRequest request, RequestContext context, CancellationToken ct = default)
    {
        Requests.Add(request);
        Contexts.Add(context);
        return _scripts.TryDequeue(out var script)
            ? script(request, ct)
            : Up.Events(new StreamError(AgentError.Create(AgentErrorCode.Provider, "No script left in the fake upstream.", false)))(request, ct);
    }
}

/// <summary>上游脚本的构造器。事件序列和 Llm 层的解码器输出一致(MessageStart … MessageEnd)。</summary>
public static class Up
{
    public static readonly TokenUsage Usage = new(10, 5);

    private static async IAsyncEnumerable<LlmEvent> Gen(IEnumerable<LlmEvent> events)
    {
        foreach (var ev in events)
        {
            await Task.Yield();
            yield return ev;
        }
    }

    public static UpstreamScript Events(params LlmEvent[] events) => (_, _) => Gen(events);

    public static UpstreamScript Custom(Func<LlmRequest, CancellationToken, IAsyncEnumerable<LlmEvent>> script) => new(script);

    public static UpstreamScript Text(string text, StopReason stop = StopReason.EndTurn) =>
        Events(new MessageStart("fake-model", "p", "anthropic", "fake-up"), new TextDelta(0, text), new MessageEnd(stop, Usage, 5, CostInfo.NotMetered));

    /// <summary>一条助手消息:可选文字 + 若干工具调用(各自一次性给出完整参数)。</summary>
    public static UpstreamScript Calls(string? text, params (string Id, string Name, string Json)[] calls) =>
        Events(CallEvents(text, StopReason.ToolUse, calls).ToArray());

    public static UpstreamScript Calls(params (string Id, string Name, string Json)[] calls) => Calls(null, calls);

    public static UpstreamScript Call(string id, string name, string json = "{}") => Calls(null, (id, name, json));

    /// <summary>参数被截断、stopReason=MaxTokens 的调用:参数 JSON 不完整。</summary>
    public static UpstreamScript Truncated(string id, string name, string brokenJson) =>
        Events(
            new MessageStart("fake-model", "p", "anthropic", "fake-up"),
            new ToolCallStart(0, id, name),
            new ToolCallDelta(0, id, brokenJson),
            new ToolCallEnd(0, id),
            new MessageEnd(StopReason.MaxTokens, Usage));

    public static IEnumerable<LlmEvent> CallEvents(string? text, StopReason stop, params (string Id, string Name, string Json)[] calls)
    {
        yield return new MessageStart("fake-model", "p", "anthropic", "fake-up");
        var index = 0;
        if (text is not null)
        {
            yield return new TextDelta(index++, text);
        }

        foreach (var (id, name, json) in calls)
        {
            yield return new ToolCallStart(index, id, name);
            yield return new ToolCallDelta(index, id, json);
            yield return new ToolCallEnd(index, id);
            index++;
        }

        yield return new MessageEnd(stop, Usage);
    }

    /// <summary>吐一点内容之后停住,直到被取消;像真的路由一样以 StreamError(Aborted) 收尾。</summary>
    public static UpstreamScript Hang(string partial = "par") => (_, ct) => HangGen(partial, ct);

    private static async IAsyncEnumerable<LlmEvent> HangGen(string partial, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        yield return new MessageStart("fake-model", "p", "anthropic", "fake-up");
        yield return new TextDelta(0, partial);
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
        }

        yield return new StreamError(AgentError.Create(AgentErrorCode.Aborted, "Aborted", false));
    }

    /// <summary>吐一点内容之后永远卡住,<b>完全不理</b>取消令牌(一个坏掉的上游也不能拖住中止)。</summary>
    public static UpstreamScript HangDeaf(string partial = "par") => (_, _) => HangDeafGen(partial);

    private static async IAsyncEnumerable<LlmEvent> HangDeafGen(string partial)
    {
        yield return new MessageStart("fake-model", "p", "anthropic", "fake-up");
        yield return new TextDelta(0, partial);
        await new TaskCompletionSource().Task;
        yield break;
    }

    /// <summary>工具调用只流了一半(没有 ToolCallEnd)就被取消。</summary>
    public static UpstreamScript HangInsideToolCall() => (_, ct) => HangInsideToolCallGen(ct);

    private static async IAsyncEnumerable<LlmEvent> HangInsideToolCallGen([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        yield return new MessageStart("fake-model", "p", "anthropic", "fake-up");
        yield return new TextDelta(0, "let me");
        yield return new ToolCallStart(1, "half", "echo");
        yield return new ToolCallDelta(1, "half", "{\"a\":");
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
        }

        yield return new StreamError(AgentError.Create(AgentErrorCode.Aborted, "Aborted", false));
    }

    public static UpstreamScript Fail(AgentError error, string? partial = null) =>
        Events(
            partial is null
                ? [new StreamError(error)]
                : [new MessageStart("fake-model", "p", "anthropic", "fake-up"), new TextDelta(0, partial), new StreamError(error)]);

    public static readonly AgentError NetworkError = AgentError.Create(AgentErrorCode.Network, "connection reset");

    /// <summary>断流:有内容,没有 MessageEnd,也没有错误事件。</summary>
    public static UpstreamScript Cut(string partial = "par") =>
        Events(new MessageStart("fake-model", "p", "anthropic", "fake-up"), new TextDelta(0, partial));

    /// <summary>枚举器直接抛异常(模型层承诺不抛,这里测循环的兜底)。</summary>
    public static UpstreamScript Throws(Exception e) => (_, _) => ThrowsGen(e);

    private static async IAsyncEnumerable<LlmEvent> ThrowsGen(Exception e)
    {
        yield return new MessageStart("fake-model", "p", "anthropic", "fake-up");
        yield return new TextDelta(0, "partial");
        await Task.Yield();
        throw e;
    }
}

/// <summary>假工具。名字就是外部名。</summary>
public sealed class FakeTool(string name, Func<ToolInvocation, ToolContext, CancellationToken, Task<ToolResult>>? run = null, ToolTraits? traits = null) : IAgentTool
{
    private int _running;
    private int _maxRunning;
    private int _calls;

    public ToolSpec Info { get; } = new(
        name,
        $"Fake tool {name}",
        Jsn.Parse("""{"type":"object"}"""),
        traits ?? new ToolTraits(ReadOnly: true, Destructive: false, NeedsNetwork: false));

    public Func<JsonElement, ToolArgumentCheck>? Prepare { get; set; }

    public List<ToolInvocation> Invocations { get; } = [];

    public List<ToolContext> Contexts { get; } = [];

    public int Calls => Volatile.Read(ref _calls);

    public int MaxConcurrent => Volatile.Read(ref _maxRunning);

    public ToolArgumentCheck PrepareArguments(JsonElement input) => Prepare?.Invoke(input) ?? ToolArgumentCheck.Valid(input);

    public async ValueTask<ToolResult> ExecuteAsync(ToolInvocation call, ToolContext ctx, CancellationToken ct)
    {
        lock (Invocations)
        {
            Invocations.Add(call);
            Contexts.Add(ctx);
        }

        Interlocked.Increment(ref _calls);
        var now = Interlocked.Increment(ref _running);
        int seen;
        while ((seen = Volatile.Read(ref _maxRunning)) < now && Interlocked.CompareExchange(ref _maxRunning, now, seen) != seen)
        {
        }

        try
        {
            return run is null ? ToolResult.Text($"{name}:ok") : await run(call, ctx, ct);
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }
}
