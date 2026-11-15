using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Ncw.Agent.Extensions.Planning.Goals;
using Ncw.Agent.Host.Conversation;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Subagents;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Routing;
using Ncw.Agent.Llm.Thinking;

namespace Ncw.Agent.Host.Tests.Support;

/// <summary>一次上游调用的脚本:拿到请求和取消令牌,产出流事件(可以挂起、等门、按请求内容现造)。</summary>
public delegate IAsyncEnumerable<LlmEvent> Script(LlmRequest request, CancellationToken ct);

/// <summary>
/// 按<b>会话</b>分派脚本的假上游(父会话、子会话、目标判定器并发调用上游时,按「谁先到」消费一条队列不可靠):
/// <c>RequestContext.SessionId</c> 决定走哪条队列;判定器的请求(system 恰好是 <see cref="GoalPrompts.EvaluatorSystem"/>)走单独一条。
/// 脚本用完后:主模型说一句话收尾,判定器回「未达成」。
/// </summary>
public sealed class ScriptedLlm : ILlmClient
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<Script>> _bySession = new(StringComparer.Ordinal);
    private readonly Queue<Script> _judge = new();
    private readonly List<(string SessionId, LlmRequest Request)> _requests = [];
    private readonly List<(string SessionId, ModelRef Model)> _models = [];

    public ScriptedLlm On(string sessionId, params Script[] scripts)
    {
        lock (_gate)
        {
            if (!_bySession.TryGetValue(sessionId, out var queue))
            {
                _bySession[sessionId] = queue = new Queue<Script>();
            }

            foreach (var s in scripts)
            {
                queue.Enqueue(s);
            }
        }

        return this;
    }

    public ScriptedLlm Judge(params Script[] scripts)
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

    public IReadOnlyList<LlmRequest> RequestsOf(string sessionId)
    {
        lock (_gate)
        {
            return [.. _requests.Where(r => r.SessionId == sessionId && r.Request.System != GoalPrompts.EvaluatorSystem).Select(r => r.Request)];
        }
    }

    public IReadOnlyList<LlmRequest> JudgeRequests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests.Where(r => r.Request.System == GoalPrompts.EvaluatorSystem).Select(r => r.Request)];
            }
        }
    }

    /// <summary>判定器请求用的模型(别名 + 供应商)。</summary>
    public IReadOnlyList<ModelRef> JudgeModels
    {
        get
        {
            lock (_gate)
            {
                return [.. _models.Where(m => _requests[_models.IndexOf(m)].Request.System == GoalPrompts.EvaluatorSystem).Select(m => m.Model)];
            }
        }
    }

    public IReadOnlyList<(string SessionId, LlmRequest Request)> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    public IAsyncEnumerable<LlmEvent> StreamAsync(ModelRef model, LlmRequest request, RequestContext context, CancellationToken ct = default)
    {
        Script script;
        lock (_gate)
        {
            var session = context.SessionId ?? "";
            _requests.Add((session, request));
            _models.Add((session, model));
            if (request.System == GoalPrompts.EvaluatorSystem)
            {
                script = _judge.Count > 0 ? _judge.Dequeue() : S.Verdict(false, "no scripted verdict");
            }
            else
            {
                script = _bySession.TryGetValue(session, out var queue) && queue.Count > 0 ? queue.Dequeue() : S.Say("ok");
            }
        }

        return script(request, ct);
    }
}

/// <summary>脚本构造器。</summary>
public static class S
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

    public static Script Say(string text) => Events(
        new MessageStart("fake-model", "p", "anthropic", "fake-up"), new TextDelta(0, text), new MessageEnd(StopReason.EndTurn, Usage, 5, CostInfo.NotMetered));

    public static Script Calls(params (string Id, string Name, string Json)[] calls)
    {
        var events = new List<LlmEvent> { new MessageStart("fake-model", "p", "anthropic", "fake-up") };
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

    /// <summary>脚本内容要等到这一轮才知道(例如计划文件的路径是 EnterPlanMode 跑完才有的):到点再造,可以读请求。</summary>
    public static Script Lazy(Func<LlmRequest, Script> make) => (req, ct) => make(req)(req, ct);

    /// <summary>门放行之前不回话(测「子代理还在跑」)。取消令牌触发时直接结束(中止)。</summary>
    public static Script Gated(Task gate, Script then) => (req, ct) => GatedGen(gate, then, req, ct);

    private static async IAsyncEnumerable<LlmEvent> GatedGen(Task gate, Script then, LlmRequest req, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new MessageStart("fake-model", "p", "anthropic", "fake-up");
        await gate.WaitAsync(ct).ConfigureAwait(false);
        await foreach (var ev in then(req, ct).WithCancellation(ct).ConfigureAwait(false))
        {
            yield return ev;
        }
    }

    /// <summary>一直不回话,直到被中止。</summary>
    public static Script Hang() => (_, ct) => HangGen(ct);

    private static async IAsyncEnumerable<LlmEvent> HangGen([EnumeratorCancellation] CancellationToken ct)
    {
        yield return new MessageStart("fake-model", "p", "anthropic", "fake-up");
        await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
    }

    /// <summary>目标判定器的一轮:完整的 end_turn 回复,正文是判定 JSON。</summary>
    public static Script Verdict(bool ok, string reason, bool impossible = false) =>
        Say(impossible
            ? new JsonObject { ["ok"] = ok, ["impossible"] = true, ["reason"] = reason }.ToJsonString()
            : new JsonObject { ["ok"] = ok, ["reason"] = reason }.ToJsonString());

    public static string TaskArgs(string prompt = "look into it", string type = "general-purpose", string description = "Look into it", bool background = false) =>
        new JsonObject
        {
            ["description"] = description,
            ["prompt"] = prompt,
            ["subagent_type"] = type,
            ["run_in_background"] = background,
        }.ToJsonString();

    public static string ChildSession(string parentSession, string parentRunId, int n = 1) =>
        SubagentIds.ChildSession(parentSession, SubagentIds.ChildRun(new RunId(parentRunId), n));

    public static string ChildRun(string parentRunId, int n = 1) => SubagentIds.ChildRun(new RunId(parentRunId), n).Value;

    /// <summary>请求里送给模型的全部文字(消息文字块 + 工具结果),断言「模型看得见什么」用。</summary>
    public static string WireText(LlmRequest request) =>
        string.Join('\n', request.Messages.SelectMany(m => m.Parts).Select(p => p switch
        {
            TextPart t => t.Text,
            ToolResultPart r => r.Output.Content,
            _ => "",
        }));
}

/// <summary>两个模型:<c>sonnet</c>(没有思考声明)和 <c>haiku</c>(不支持思考)——验证子代理的模型 / 思考档位适配。</summary>
public sealed class TwoModels(ILlmClient client) : IModelResolver
{
    public ILlmClient Client { get; } = client;

    public ModelBinding? Resolve(ModelRef model) => model.Alias switch
    {
        "sonnet" => new ModelBinding("sonnet", "p1", "up", 200_000, 8192),
        "haiku" => new ModelBinding("haiku", "p1", "up-small", 100_000, 4096, Thinking: new ThinkingConfig(ThinkingMode.Unsupported)),
        _ => null,
    };
}
