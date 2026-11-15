using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Routing;

namespace Ncw.Agent.Kernel.Tests.Support;

public sealed record RunResult(IReadOnlyList<AgentEvent> Events, IReadOnlyList<Message> Committed, FakeLlm Llm)
{
    public RunEndEvent End => Events.OfType<RunEndEvent>().Single();

    public RunStatus Status => End.Status;

    public IEnumerable<T> Of<T>() where T : AgentEvent => Events.OfType<T>();

    public IReadOnlyList<Message> Assistants => Committed.Where(m => m.Role == Role.Assistant).ToList();

    public IEnumerable<ToolResultPart> Results => Committed.SelectMany(m => m.Parts).OfType<ToolResultPart>();

    public ToolResultPart ResultOf(string callId) => Results.Single(r => r.CallId == callId);

    public string Type(AgentEvent e) => e.GetType().Name.Replace("Event", "");

    public IReadOnlyList<string> Kinds => Events.Select(Type).ToList();
}

/// <summary>把假上游 + 假工具 + 钩子接成一个可以跑的循环。每个测试都会自动检查事件流的完整性规则。</summary>
public sealed class Rig
{
    public FakeLlm Llm { get; } = new();

    public SequentialIds Ids { get; } = new();

    public FakeClock Clock { get; } = new();

    public IDelayer Delayer { get; set; } = new RecordingDelayer();

    public List<IAgentTool> Tools { get; } = [];

    public List<Message> Context { get; } = [];

    public List<Message> Committed { get; } = [];

    public AgentLoopOptions Options { get; set; } = new();

    public Func<ToolInvocation, CancellationToken, ValueTask<PreflightDecision>>? BeforeTool { get; set; }

    public Func<ToolInvocation, ToolResult, CancellationToken, ValueTask<ToolResult>>? AfterTool { get; set; }

    public Func<TurnEnd, CancellationToken, ValueTask<TurnEndDecision>>? OnTurnEnd { get; set; }

    public Func<IReadOnlyList<Message>>? TakeSteering { get; set; }

    public Func<Message, CancellationToken, ValueTask>? Commit { get; set; }

    public Func<ToolContext, ToolContext>? Enrich { get; set; }

    public Func<TurnContext, CancellationToken, ValueTask<TurnPlan>>? Prepare { get; set; }

    public Func<TurnContext, CancellationToken, ValueTask<bool>>? RecoverContextLength { get; set; }

    public EnvironmentTag? Environment { get; set; }

    /// <summary>允许 tool_end 没有对应 tool_start(恢复上个 Run 留下的孤儿时,只补 end)。</summary>
    public bool AllowUnstartedToolEnd { get; set; }

    public CollectingDiagnostics Diagnostics { get; } = new();

    public Rig Tool(FakeTool tool)
    {
        Tools.Add(tool);
        return this;
    }

    public static Message User(string id, string text) =>
        new(new MessageId(id), Role.User, [new TextPart(text)], DateTimeOffset.UnixEpoch);

    public LoopInput Input(params Message[] newMessages) => new(
        new RunId("run-1"), "session-1", "workspace-1", Context.ToList(), newMessages, ModelRef.Unpinned("alias"),
        new GenerationOptions(4096), Environment);

    public AgentLoop Loop() => new(Llm, Ids, Clock, Delayer, Options, Diagnostics);

    public ToolSnapshot Snapshot() => new(Tools.Select(t => new ToolBinding(t.Info.InternalId, t)));

    public LoopHooks Hooks() => new()
    {
        PrepareTurn = Prepare ?? ((turn, _) => ValueTask.FromResult(TurnPlan.Run(
            new LlmRequest("system", turn.Transcript, [], turn.Options), Snapshot()))),
        BeforeTool = BeforeTool ?? (static (_, _) => ValueTask.FromResult(PreflightDecision.Allow)),
        AfterTool = AfterTool ?? (static (_, r, _) => ValueTask.FromResult(r)),
        OnTurnEnd = OnTurnEnd ?? (static (_, _) => ValueTask.FromResult(TurnEndDecision.Stop)),
        TakeSteering = TakeSteering ?? (static () => []),
        CommitMessage = async (m, ct) =>
        {
            if (Commit is not null)
            {
                await Commit(m, ct);
            }

            lock (Committed)
            {
                Committed.Add(m);
            }
        },
        EnrichToolContext = Enrich,
        RecoverContextLength = RecoverContextLength,
    };

    /// <summary>跑到结束,收集全部事件,并检查事件流规则。</summary>
    public async Task<RunResult> RunAsync(params Message[] newMessages) => await RunAsync(CancellationToken.None, newMessages);

    public async Task<RunResult> RunAsync(CancellationToken ct, params Message[] newMessages)
    {
        var events = new List<AgentEvent>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await foreach (var ev in Loop().RunAsync(Input(newMessages), Hooks(), ct).WithCancellation(timeout.Token))
        {
            if (ev is MessageEndEvent end)
            {
                // 先落库、后发 message_end:看到 end 时它必须已经在提交列表里。
                lock (Committed)
                {
                    Assert.Contains(Committed, m => m.Id == end.Message.Id);
                }
            }

            events.Add(ev);
        }

        EventRules.AssertWellFormed(events, AllowUnstartedToolEnd);
        return new RunResult(events, Committed.ToList(), Llm);
    }
}

public sealed class CollectingDiagnostics : ILoopDiagnostics
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

/// <summary>事件流的完整性规则(02 §3):任何一次 Run,无论怎么结束,都必须满足。</summary>
public static class EventRules
{
    public static void AssertWellFormed(IReadOnlyList<AgentEvent> events, bool allowUnstartedToolEnd = false)
    {
        Assert.NotEmpty(events);
        Assert.IsType<RunStartEvent>(events[0]);
        Assert.IsType<RunEndEvent>(events[^1]);
        Assert.Single(events.OfType<RunStartEvent>());
        Assert.Single(events.OfType<RunEndEvent>());

        // turn:严格嵌套、不重叠、下标递增。
        var open = -1;
        var last = -1;
        foreach (var ev in events)
        {
            switch (ev)
            {
                case TurnStartEvent s:
                    Assert.Equal(-1, open);
                    Assert.Equal(last + 1, s.TurnIndex);
                    open = s.TurnIndex;
                    break;
                case TurnEndEvent e:
                    Assert.Equal(open, e.TurnIndex);
                    last = e.TurnIndex;
                    open = -1;
                    break;
                case RunEndEvent:
                    Assert.Equal(-1, open);
                    break;
            }
        }

        // message:每个 start 恰好一个 end 或 discard,且 end / delta 之前一定有 start。
        var started = new List<MessageId>();
        var finished = new List<MessageId>();
        foreach (var ev in events)
        {
            switch (ev)
            {
                case MessageStartEvent s:
                    Assert.DoesNotContain(s.MessageId, started);
                    started.Add(s.MessageId);
                    break;
                case MessageDeltaEvent d:
                    Assert.Contains(d.MessageId, started);
                    Assert.DoesNotContain(d.MessageId, finished);
                    break;
                case MessageEndEvent e:
                    Assert.Contains(e.MessageId, started);
                    Assert.DoesNotContain(e.MessageId, finished);
                    Assert.True(e.Message.Parts.Count > 0, "an empty message must never be committed");
                    finished.Add(e.MessageId);
                    break;
                case MessageDiscardEvent d:
                    Assert.Contains(d.MessageId, started);
                    Assert.DoesNotContain(d.MessageId, finished);
                    finished.Add(d.MessageId);
                    break;
            }
        }

        Assert.Equal(started.OrderBy(i => i.Value), finished.OrderBy(i => i.Value));

        // tool:每个 start 恰好一个 end,end 在 start 之后。
        var toolsStarted = new HashSet<string>();
        var toolsEnded = new HashSet<string>();
        foreach (var ev in events)
        {
            switch (ev)
            {
                case ToolStartEvent s:
                    Assert.True(toolsStarted.Add(s.CallId), $"duplicate tool_start for {s.CallId}");
                    break;
                case ToolProgressEvent p:
                    Assert.Contains(p.CallId, toolsStarted);
                    Assert.DoesNotContain(p.CallId, toolsEnded);
                    break;
                case ToolEndEvent e:
                    if (!allowUnstartedToolEnd)
                    {
                        Assert.Contains(e.CallId, toolsStarted);
                    }

                    Assert.True(toolsEnded.Add(e.CallId), $"duplicate tool_end for {e.CallId}");
                    break;
            }
        }

        Assert.All(toolsStarted, id => Assert.Contains(id, toolsEnded));
    }
}
