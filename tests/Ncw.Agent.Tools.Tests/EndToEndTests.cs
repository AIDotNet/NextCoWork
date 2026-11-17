using System.Text.Json;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Routing;

namespace Ncw.Agent.Tools.Tests;

/// <summary>
/// 端到端:<c>AgentLoop</c> + 假上游 + <b>真实工具</b>(Read → Edit → Bash),跑在 <c>LocalEnvironment</c> 上。
/// 验证的是接缝:工具快照(schema 发给模型)→ 预检(<c>PrepareArguments</c>)→ <c>EnrichToolContext</c> 换上执行环境 → 工具执行 →
/// 结果(Content 给模型、Details 给界面、带执行环境标识)→ 落库 → 下一个回合的请求里带着工具结果。
/// </summary>
public sealed class EndToEndTests : IDisposable
{
    private readonly Fixture _fx = new(shells: true);

    public void Dispose() => _fx.Dispose();

    private sealed class FakeLlm : ILlmClient
    {
        private readonly Queue<Func<LlmRequest, IReadOnlyList<LlmEvent>>> _scripts = new();

        public List<LlmRequest> Requests { get; } = [];

        public FakeLlm Then(Func<LlmRequest, IReadOnlyList<LlmEvent>> script)
        {
            _scripts.Enqueue(script);
            return this;
        }

        public async IAsyncEnumerable<LlmEvent> StreamAsync(ModelRef model, LlmRequest request, RequestContext context, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            var events = _scripts.TryDequeue(out var script)
                ? script(request)
                : [new StreamError(AgentError.Create(AgentErrorCode.Provider, "no script left", false))];
            foreach (var ev in events)
            {
                await Task.Yield();
                yield return ev;
            }
        }
    }

    private sealed class Ids : IIdGenerator
    {
        private int _n;

        public string NewId() => $"id{Interlocked.Increment(ref _n):D4}";
    }

    private sealed class NoDelay : IDelayer
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.CompletedTask;
    }

    private static IReadOnlyList<LlmEvent> Calls(params (string Id, string Name, string Json)[] calls)
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

    private static IReadOnlyList<LlmEvent> Say(string text) =>
        [new MessageStart("fake-model", "p", "anthropic", "fake-up"), new TextDelta(0, text), new MessageEnd(StopReason.EndTurn, new TokenUsage(10, 5), 5, CostInfo.NotMetered)];

    private static string J(params (string Name, string Value)[] props)
    {
        var o = new System.Text.Json.Nodes.JsonObject();
        foreach (var (name, value) in props)
        {
            o[name] = value;
        }

        return o.ToJsonString();
    }

    private sealed record Outcome(List<AgentEvent> Events, List<Message> Committed, FakeLlm Llm)
    {
        public RunStatus Status => Events.OfType<RunEndEvent>().Single().Status;

        public ToolEndEvent End(string callId) => Events.OfType<ToolEndEvent>().Single(e => e.CallId == callId);
    }

    private async Task<Outcome> RunAsync(FakeLlm llm, CancellationToken ct = default)
    {
        var snapshot = new ToolSnapshot(BuiltinTools.Create().Select(t => new ToolBinding(t.Info.InternalId, t)));
        var committed = new List<Message>();
        var hooks = new LoopHooks
        {
            PrepareTurn = (turn, _) => ValueTask.FromResult(TurnPlan.Run(new LlmRequest("system", turn.Transcript, [], turn.Options), snapshot)),
            CommitMessage = (m, _) =>
            {
                lock (committed)
                {
                    committed.Add(m);
                }

                return ValueTask.CompletedTask;
            },
            // 会话层的工作:把项目的执行环境(和读取跟踪、变更记录、Shell 注册表)交给每个工具调用
            EnrichToolContext = ExecutionToolContext.Enricher(_fx.Runtime),
        };
        var loop = new AgentLoop(llm, new Ids(), TimeProvider.System, new NoDelay());
        var input = new LoopInput(
            new RunId("run-1"), "session-1", "workspace-1", [],
            [new Message(new MessageId("user-1"), Role.User, [new TextPart("bump the version, then show me the file")], DateTimeOffset.UnixEpoch)],
            ModelRef.Unpinned("alias"), new GenerationOptions(4096), new EnvironmentTag("dev-1", "local"));
        var events = new List<AgentEvent>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await foreach (var ev in loop.RunAsync(input, hooks, ct).WithCancellation(timeout.Token))
        {
            events.Add(ev);
        }

        return new Outcome(events, committed, llm);
    }

    [Fact]
    public async Task Read_then_Edit_then_Bash_through_the_real_loop()
    {
        var file = _fx.Abs("app.txt");
        _fx.Put("app.txt", "name=demo\nversion=1\n");
        var llm = new FakeLlm()
            .Then(_ => Calls(("c1", "Read", J(("file_path", file)))))
            .Then(_ => Calls(("c2", "Edit", J(("file_path", file), ("old_string", "version=1"), ("new_string", "version=2")))))
            .Then(_ => Calls(("c3", "Bash", J(("command", "cat app.txt"), ("description", "show the file")))))
            .Then(_ => Say("done"));

        var run = await RunAsync(llm);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal("name=demo\nversion=2\n", _fx.Text("app.txt"));
        Assert.All(run.Events.OfType<ToolEndEvent>(), e => Assert.False(e.IsError, e.Output.Content));

        // 工具快照里的 schema 发给了模型(LoopHooks 总以快照为准)
        var offered = llm.Requests[0].Tools.Select(t => t.ExternalName).ToList();
        Assert.Contains("Edit", offered);
        Assert.Equal("object", llm.Requests[0].Tools.Single(t => t.ExternalName == "Read").InputSchema.GetProperty("type").GetString());

        // Read 的结果(给模型的 Content)进了下一个回合的请求
        var secondRequest = string.Join('\n', llm.Requests[1].Messages.SelectMany(m => m.Parts).OfType<ToolResultPart>().Select(p => p.Output.Content));
        Assert.Contains("     2\tversion=1", secondRequest);

        // Bash 看到的是 Edit 之后的文件
        var shown = run.End("c3").Output;
        Assert.True(shown.Content.Contains("version=2", StringComparison.Ordinal), $"Bash output: {shown.Content}; details: {shown.Details?.GetRawText()}");

        // Details 只给界面,且每张卡片都带执行环境标识(设备 + 环境);发给模型的请求里没有 Details 的内容
        foreach (var id in new[] { "c1", "c2", "c3" })
        {
            var details = run.End(id).Output.Details!.Value;
            Assert.Equal("dev-1", details.GetProperty("environment").GetProperty("deviceId").GetString());
            Assert.Equal("local", details.GetProperty("environment").GetProperty("environment").GetString());
        }

        Assert.Equal("edit", run.End("c2").Output.Details!.Value.GetProperty("kind").GetString());
        Assert.DoesNotContain("@@", string.Join('\n', llm.Requests[2].Messages.SelectMany(m => m.Parts).OfType<ToolResultPart>().Select(p => p.Output.Content)));

        // 变更记录:本 Run 改了一个文件,before 是读到的原文
        var change = Assert.Single(_fx.Runtime.Changes.Take(new RunId("run-1")));
        Assert.Equal("name=demo\nversion=1\n", change.Before);
    }

    [Fact]
    public async Task An_edit_without_a_prior_Read_fails_in_the_loop_and_the_model_sees_why()
    {
        var file = _fx.Abs("app.txt");
        _fx.Put("app.txt", "version=1\n");
        var llm = new FakeLlm()
            .Then(_ => Calls(("c1", "Edit", J(("file_path", file), ("old_string", "version=1"), ("new_string", "version=2")))))
            .Then(_ => Say("ok, I will read it first"));

        var run = await RunAsync(llm);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal("version=1\n", _fx.Text("app.txt"));
        Assert.True(run.End("c1").IsError);
        var fed = llm.Requests[1].Messages.SelectMany(m => m.Parts).OfType<ToolResultPart>().Single();
        Assert.True(fed.IsError);
        Assert.Contains("Read it first", fed.Output.Content);
    }

    [Fact]
    public async Task Bad_arguments_never_reach_the_tool_and_come_back_as_a_validation_error()
    {
        var llm = new FakeLlm()
            .Then(_ => Calls(("c1", "Read", """{"offset":"abc"}""")))
            .Then(_ => Say("fixing"));

        var run = await RunAsync(llm);

        Assert.Equal(RunStatus.Done, run.Status);
        var end = run.End("c1");
        Assert.True(end.IsError);
        Assert.Contains("file_path", end.Output.Content);
        Assert.Contains("offset", end.Output.Content);
    }

    /// <summary>并行批:同一批里的多个只读调用并行,同一文件的修改经 <c>FileMutationQueue</c> 串行 —— 谁也不丢更新。</summary>
    [Fact]
    public async Task Parallel_edits_to_one_file_in_one_batch_all_land()
    {
        var file = _fx.Abs("app.txt");
        _fx.Put("app.txt", "a=1\nb=1\nc=1\n");
        var llm = new FakeLlm()
            .Then(_ => Calls(("r1", "Read", J(("file_path", file)))))
            .Then(_ => Calls(
                ("e1", "Edit", J(("file_path", file), ("old_string", "a=1"), ("new_string", "a=2"))),
                ("e2", "Edit", J(("file_path", file), ("old_string", "b=1"), ("new_string", "b=2"))),
                ("e3", "Edit", J(("file_path", file), ("old_string", "c=1"), ("new_string", "c=2")))))
            .Then(_ => Say("done"));

        var run = await RunAsync(llm);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.All(run.Events.OfType<ToolEndEvent>(), e => Assert.False(e.IsError, e.Output.Content));
        Assert.Equal("a=2\nb=2\nc=2\n", _fx.Text("app.txt"));
    }

    /// <summary>中止:整轮取消时 Bash 的进程被杀、取消异常原样传播,Loop 收尾为 Aborted 且调用与结果成对。</summary>
    [Fact]
    public async Task Aborting_the_run_kills_a_running_Bash_and_the_loop_ends_aborted_with_paired_results()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the scripted command is POSIX shell (echo $$ > pid.txt; exec sleep 60)");
        var llm = new FakeLlm()
            .Then(_ => Calls(("c1", "Bash", J(("command", "echo $$ > pid.txt; exec sleep 60"), ("description", "sleep")))));
        using var cts = new CancellationTokenSource();
        var pending = RunAsync(llm, cts.Token);
        await Wait.UntilAsync(() => File.Exists(_fx.Abs("pid.txt")) && new FileInfo(_fx.Abs("pid.txt")).Length > 0);
        await cts.CancelAsync();
        var run = await pending;

        Assert.Equal(RunStatus.Aborted, run.Status);
        Assert.Single(run.Events.OfType<ToolEndEvent>());
        var pid = int.Parse(_fx.Text("pid.txt").Trim());
        await Wait.UntilAsync(() => !Wait.ProcessAlive(pid), what: "the aborted command to die");
    }

    /// <summary>Terminate 以外的收尾也不丢:工具结果落库的顺序 = 调用顺序,Details 随 tool_end 到界面,但不进落库给模型的文本。</summary>
    [Fact]
    public async Task Tool_results_are_committed_in_call_order_in_one_message()
    {
        _fx.Put("a.txt", "A\n");
        _fx.Put("b.txt", "B\n");
        var llm = new FakeLlm()
            .Then(_ => Calls(("r1", "Read", J(("file_path", _fx.Abs("a.txt")))), ("r2", "Read", J(("file_path", _fx.Abs("b.txt"))))))
            .Then(_ => Say("done"));

        var run = await RunAsync(llm);

        var results = run.Committed.Where(m => m.Parts.OfType<ToolResultPart>().Any()).Single().Parts.OfType<ToolResultPart>().ToList();
        Assert.Equal(["r1", "r2"], results.Select(r => r.CallId).ToArray());
        Assert.Contains("A", results[0].Output.Content);
        Assert.Contains("B", results[1].Output.Content);
    }
}
