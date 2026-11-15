using System.Text.Json;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Json;
using Ncw.Agent.Llm.Routing;

namespace Ncw.Agent.Skills.Tests.Support;

public sealed class FakeClock : TimeProvider
{
    private long _ms = 1_800_000_000_000;

    public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Add(ref _ms, 10));
}

public sealed class SeqIds : IIdGenerator
{
    private int _n;

    public string NewId() => $"id{Interlocked.Increment(ref _n):D4}";
}

public sealed class NoDelay : IDelayer
{
    public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>一次上游调用的脚本:收到请求,吐出事件。</summary>
public delegate IAsyncEnumerable<LlmEvent> Script(LlmRequest request, ModelRef model, RequestContext context, CancellationToken ct);

/// <summary>
/// 假上游:摘要请求(system 是 <see cref="CompactionPrompts.SummarySystem"/>)和正文请求分开记录、分开应答;正文请求按顺序消费脚本,用完后一律回一句 "ok"。
/// </summary>
public sealed class FakeLlm : ILlmClient
{
    private readonly Queue<Script> _turns = new();
    private readonly Queue<Script> _summaries = new();
    private readonly object _gate = new();

    public List<LlmRequest> Requests { get; } = [];

    public List<(LlmRequest Request, ModelRef Model, RequestContext Context)> Summaries { get; } = [];

    public List<ModelRef> TurnModels { get; } = [];

    public string Summary { get; set; } = "## Goal\nrefactor login";

    public FakeLlm Turn(params Script[] scripts)
    {
        foreach (var s in scripts)
        {
            _turns.Enqueue(s);
        }

        return this;
    }

    public FakeLlm SummaryScript(params Script[] scripts)
    {
        foreach (var s in scripts)
        {
            _summaries.Enqueue(s);
        }

        return this;
    }

    public IAsyncEnumerable<LlmEvent> StreamAsync(ModelRef model, LlmRequest request, RequestContext context, CancellationToken ct = default)
    {
        Script script;
        lock (_gate)
        {
            if (request.System == CompactionPrompts.SummarySystem)
            {
                Summaries.Add((request, model, context));
                script = _summaries.Count > 0 ? _summaries.Dequeue() : Up.Text(Summary);
            }
            else
            {
                Requests.Add(request);
                TurnModels.Add(model);
                script = _turns.Count > 0 ? _turns.Dequeue() : Up.Text("ok");
            }
        }

        return script(request, model, context, ct);
    }
}

public static class Up
{
    public static readonly TokenUsage Usage = new(10, 5);

    public static Script Events(params LlmEvent[] events) => (_, _, _, _) => Gen(events);

    public static Script Custom(Func<LlmRequest, CancellationToken, IAsyncEnumerable<LlmEvent>> run) => (r, _, _, ct) => run(r, ct);

    private static async IAsyncEnumerable<LlmEvent> Gen(IEnumerable<LlmEvent> events)
    {
        foreach (var ev in events)
        {
            yield return ev;
            await Task.Yield();
        }
    }

    public static IEnumerable<LlmEvent> TextEvents(string text, StopReason stop = StopReason.EndTurn, TokenUsage? usage = null)
    {
        yield return new MessageStart("fake", "p1", "anthropic", "fake-up");
        yield return new TextDelta(0, text);
        yield return new MessageEnd(stop, usage ?? Usage);
    }

    public static Script Text(string text, StopReason stop = StopReason.EndTurn, TokenUsage? usage = null) =>
        Events(TextEvents(text, stop, usage).ToArray());

    /// <summary>上游回报指定的真实输入用量(压缩判据读它)。</summary>
    public static Script TextReporting(long inputTokens, string text = "ok") => Text(text, usage: new TokenUsage(inputTokens, 5));

    public static Script CallReporting(long inputTokens, string id, string name, string json = "{}") =>
        Events([
            new MessageStart("fake", "p1", "anthropic", "fake-up"),
            new ToolCallStart(0, id, name),
            new ToolCallDelta(0, id, json),
            new ToolCallEnd(0, id),
            new MessageEnd(StopReason.ToolUse, new TokenUsage(inputTokens, 5)),
        ]);

    public static Script Call(string id, string name, string json = "{}") => CallReporting(10, id, name, json);

    public static readonly AgentError NetworkError = AgentError.Create(AgentErrorCode.Network, "connection reset");

    public static Script Fail(AgentError error, string? partial = null) => (_, _, _, _) => FailGen(error, partial);

    private static async IAsyncEnumerable<LlmEvent> FailGen(AgentError error, string? partial)
    {
        yield return new MessageStart("fake", "p1", "anthropic", "fake-up");
        if (partial is not null)
        {
            yield return new TextDelta(0, partial);
        }

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

public sealed class FakeTool(string name, Func<ToolInvocation, ToolContext, CancellationToken, Task<ToolResult>>? run = null, string? snippet = null, string[]? guidelines = null) : IAgentTool
{
    public int Calls;

    public ToolContext? LastContext;

    public ToolSpec Info { get; } = new(name, $"{name} tool", Jsn.Parse("""{"type":"object"}"""), new ToolTraits(true, false, false), snippet, guidelines);

    public async ValueTask<ToolResult> ExecuteAsync(ToolInvocation call, ToolContext ctx, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        LastContext = ctx;
        return run is null ? ToolResult.Text($"{name} ran") : await run(call, ctx, ct);
    }
}
