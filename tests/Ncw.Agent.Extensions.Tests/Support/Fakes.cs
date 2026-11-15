using System.Runtime.CompilerServices;
using System.Text.Json;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Json;
using Ncw.Agent.Llm.Routing;

namespace Ncw.Agent.Extensions.Tests.Support;

public sealed class SequentialIds : IIdGenerator
{
    private int _n;

    public string NewId() => $"id{Interlocked.Increment(ref _n):D4}";
}

public sealed class NoDelay : IDelayer
{
    public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.CompletedTask;
}

public sealed class Diagnostics : ILoopDiagnostics
{
    public List<(string Message, Exception? Error)> Warnings { get; } = [];

    public void Warn(string message, Exception? error = null)
    {
        lock (Warnings)
        {
            Warnings.Add((message, error));
        }
    }
}

/// <summary>假上游:按脚本一次一个请求回复,记录每个请求。</summary>
public sealed class FakeLlm : ILlmClient
{
    private readonly Queue<Func<LlmRequest, IReadOnlyList<LlmEvent>>> _scripts = new();

    public List<LlmRequest> Requests { get; } = [];

    public FakeLlm Then(Func<LlmRequest, IReadOnlyList<LlmEvent>> script)
    {
        _scripts.Enqueue(script);
        return this;
    }

    public FakeLlm Then(IReadOnlyList<LlmEvent> events) => Then(_ => events);

    public async IAsyncEnumerable<LlmEvent> StreamAsync(ModelRef model, LlmRequest request, RequestContext context, [EnumeratorCancellation] CancellationToken ct = default)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }

        Func<LlmRequest, IReadOnlyList<LlmEvent>>? script;
        lock (_scripts)
        {
            _scripts.TryDequeue(out script);
        }

        var events = script is null
            ? [new StreamError(AgentError.Create(AgentErrorCode.Provider, "no script left", false))]
            : script(request);
        foreach (var ev in events)
        {
            await Task.Yield();
            yield return ev;
        }
    }
}

public static class Up
{
    public static IReadOnlyList<LlmEvent> Calls(params (string Id, string Name, string Json)[] calls)
    {
        List<LlmEvent> events = [new MessageStart("fake-model", "p", "anthropic", "fake-up")];
        var index = 0;
        foreach (var (id, name, json) in calls)
        {
            events.Add(new ToolCallStart(index, id, name));
            events.Add(new ToolCallDelta(index, id, json));
            events.Add(new ToolCallEnd(index, id));
            index++;
        }

        events.Add(new MessageEnd(StopReason.ToolUse, new TokenUsage(10, 5)));
        return events;
    }

    public static IReadOnlyList<LlmEvent> Say(string text) =>
        [new MessageStart("fake-model", "p", "anthropic", "fake-up"), new TextDelta(0, text), new MessageEnd(StopReason.EndTurn, new TokenUsage(10, 5), 5, CostInfo.NotMetered)];

    public static string J(params (string Name, string Value)[] props)
    {
        var o = new System.Text.Json.Nodes.JsonObject();
        foreach (var (name, value) in props)
        {
            o[name] = value;
        }

        return o.ToJsonString();
    }
}

/// <summary>假工具:名字就是内部 id 和外部名。</summary>
public sealed class FakeTool(string name, bool readOnly = false, bool destructive = true, bool needsNetwork = false, Func<ToolInvocation, ToolContext, CancellationToken, Task<ToolResult>>? run = null) : IAgentTool
{
    private int _calls;

    public ToolSpec Info { get; } = new(name, $"Fake tool {name}", Jsn.Parse("""{"type":"object"}"""), new ToolTraits(readOnly, destructive, needsNetwork));

    public List<ToolInvocation> Invocations { get; } = [];

    public int Calls => Volatile.Read(ref _calls);

    public async ValueTask<ToolResult> ExecuteAsync(ToolInvocation call, ToolContext ctx, CancellationToken ct)
    {
        lock (Invocations)
        {
            Invocations.Add(call);
        }

        Interlocked.Increment(ref _calls);
        return run is null ? ToolResult.Text($"{name}:ok") : await run(call, ctx, ct);
    }
}

public static class Wait
{
    public static async Task<T> UntilNotNullAsync<T>(Func<T?> get, int timeoutMs = 10_000, string? what = null)
        where T : class
    {
        T? value = null;
        await UntilAsync(() => (value = get()) is not null, timeoutMs, what);
        return value!;
    }

    public static async Task UntilAsync(Func<bool> condition, int timeoutMs = 10_000, string? what = null)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what ?? "a condition"}.");
            }

            await Task.Delay(5);
        }
    }
}

public static class JsonExt
{
    public static JsonElement Json(this string text) => Jsn.Parse(text);
}
