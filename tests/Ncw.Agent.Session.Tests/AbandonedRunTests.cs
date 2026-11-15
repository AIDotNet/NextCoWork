using Ncw.Agent.Kernel.Extensions;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Json;
using Ncw.Agent.Session.Tests.Support;

namespace Ncw.Agent.Session.Tests;

/// <summary>
/// 被编排层放弃(看门狗强收、删除会话前强制收口)的 Run,其驱动醒来后不许再写库(05 §12、2.x #187)。
/// 这里直接翻转 <see cref="RunRequest.IsAbandoned"/>,在每一个落库点之前、之中放弃,断言数据库里没有放弃之后的写入、
/// 扩展收不到 <c>run_end</c> / <c>run_settled</c>、诊断里有 <see cref="SessionDiagnosticKeys.WriteSkippedAbandoned"/>。
/// 编排层一侧(手动时钟推动真看门狗)的端到端用例在 <c>Ncw.Agent.Runs.Tests</c>。
/// </summary>
public sealed class AbandonedRunTests
{
    private static readonly string Big = new('x', 400_000);

    private static readonly SessionSettings Settings = new() { MaxOutputTokens = 8192 };

    private static Message Seed(string id, Role role, params Part[] parts) =>
        new(new MessageId(id), role, parts, DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000));

    private static async IAsyncEnumerable<LlmEvent> Stream(IEnumerable<LlmEvent> events, Action onFirst)
    {
        onFirst();
        foreach (var ev in events)
        {
            yield return ev;
            await Task.Yield();
        }
    }

    private static int Skips(Harness h)
    {
        lock (h.Log.Warnings)
        {
            return h.Log.Warnings.Count(w => w.StartsWith(SessionDiagnosticKeys.WriteSkippedAbandoned, StringComparison.Ordinal));
        }
    }

    private static (Ext Extension, List<string> Calls) Recorder()
    {
        var calls = new List<string>();
        var ext = new Ext("probe", 0, r =>
        {
            r.On(LoopExtensionPoints.RunEnd, (_, _, _) =>
            {
                calls.Add("run_end");
                return ValueTask.CompletedTask;
            });
            r.On(SessionExtensionPoints.RunSettled, (_, _, _) =>
            {
                calls.Add("run_settled");
                return ValueTask.CompletedTask;
            });
            r.On(SessionExtensionPoints.AfterCompact, (_, _, _) =>
            {
                calls.Add("after_compact");
                return ValueTask.CompletedTask;
            });
        });
        return (ext, calls);
    }

    [Fact]
    public async Task A_live_run_commits_every_message_in_order_notifies_the_extensions_and_logs_no_skip()
    {
        var (probe, calls) = Recorder();
        using var h = new Harness([probe], [new FakeTool("echo")]);
        h.Llm.Turn(Up.Call("c1", "echo"), Up.Text("done"));

        var ran = await h.RunAsync("go", b => b.IsAbandoned = () => false);

        Assert.Equal(RunStatus.Done, ran.Status);
        var stored = h.Stored();
        Assert.Equal(4, stored.Count);
        Assert.Equal([Role.User, Role.Assistant, Role.User, Role.Assistant], stored.Select(m => m.Role));
        Assert.Single(stored[2].Parts.OfType<ToolResultPart>());
        Assert.Equal(["run_end", "run_settled"], calls);
        Assert.Equal(0, Skips(h));
    }

    [Fact]
    public async Task Abandoned_while_a_tool_runs_the_tool_result_and_everything_after_it_stay_out_of_the_database()
    {
        var abandoned = false;
        var (probe, calls) = Recorder();
        using var h = new Harness([probe], [new FakeTool("echo", (_, _, _) =>
        {
            abandoned = true; // 看门狗在驱动卡在工具里的时候收了口
            return Task.FromResult(ToolResult.Text("late result"));
        })]);
        h.Llm.Turn(Up.Call("c1", "echo")); // 第二回合如果真的发了请求,会落到默认应答上,Requests 计数会暴露

        var ran = await h.RunAsync("go", b => b.IsAbandoned = () => abandoned);

        // 放弃之前落库的(用户输入、带工具调用的助手消息)在;之后的(工具结果、孤儿修复、下一回合)一条都没有。
        var stored = h.Stored();
        Assert.Equal([Role.User, Role.Assistant], stored.Select(m => m.Role));
        Assert.DoesNotContain(stored, m => m.Parts.OfType<ToolResultPart>().Any());
        Assert.Single(h.Llm.Requests); // 回合边界上收尾,不再给上游发请求
        Assert.Equal(RunStatus.Aborted, ran.Status);
        Assert.Empty(calls); // run_end / run_settled 扩展都不再收到
        Assert.True(Skips(h) >= 2);
        // 一次放弃不留任何半截状态:会话仍然可用,下一个 Run 照常落库。
        h.Llm.Turn(Up.Text("fine"));
        abandoned = false;
        await h.RunAsync("again");
        Assert.Contains(h.Stored(), m => m.Parts.OfType<ToolResultPart>().Any(r => r.CallId == "c1")); // 孤儿调用在新 Run 开始时被修复
        Assert.Equal("fine", string.Concat(h.Stored()[^1].Parts.OfType<TextPart>().Select(p => p.Text)));
    }

    [Fact]
    public async Task A_run_abandoned_before_it_starts_writes_nothing_not_even_its_input_or_the_orphan_repair()
    {
        using var h = new Harness(tools: [new FakeTool("echo")]);
        h.SeedHistory(
            Seed("h0", Role.User, new TextPart("earlier")),
            Seed("h1", Role.Assistant, new ToolCallPart("c0", "echo", Jsn.EmptyObject))); // 上一个 Run 崩溃留下的孤儿调用
        var before = h.Stored().Select(m => m.Id.Value).ToList();

        var ran = await h.RunAsync("go", b => b.IsAbandoned = () => true);

        Assert.Equal(before, h.Stored().Select(m => m.Id.Value));
        Assert.Empty(h.Llm.Requests);
        Assert.Equal(RunStatus.Aborted, ran.Status);
        Assert.True(Skips(h) >= 1);
    }

    [Fact]
    public async Task Abandoned_during_the_summary_request_no_boundary_is_saved_and_after_compact_is_not_sent()
    {
        var abandoned = false;
        var (probe, calls) = Recorder();
        using var h = new Harness([probe], [new FakeTool("echo")]);
        h.SeedHistory(
            Seed("h0", Role.User, new TextPart("first task")),
            Seed("h1", Role.Assistant, new ToolCallPart("c0", "echo", Jsn.EmptyObject)),
            Seed("h2", Role.User, new ToolResultPart("c0", new ToolOutput(Big), false)),
            Seed("h3", Role.Assistant, new TextPart("reply")));
        h.Llm.Turn(Up.CallReporting(200_000, "c1", "echo"), Up.Text("never sent"));
        // 摘要请求在飞的时候,这个 Run 被放弃
        h.Llm.SummaryScript(Up.Custom((_, _) => Stream(Up.TextEvents("## Goal\nrefactor login"), () => abandoned = true)));

        var ran = await h.RunAsync("go", b =>
        {
            b.Settings = Settings;
            b.IsAbandoned = () => abandoned;
        });

        Assert.Single(h.Llm.Summaries); // 摘要请求已经发出去了(无法收回),但结果不落库
        Assert.DoesNotContain(h.Stored(), m => CompactBoundaries.BoundaryOf(m) is not null);
        Assert.Single(h.Llm.Requests); // 压缩之后的那一回合没有发请求
        Assert.Equal(RunStatus.Aborted, ran.Status);
        Assert.DoesNotContain(ran.Phases, p => p == ContextStatusPhase.Compacted);
        Assert.DoesNotContain("after_compact", calls);
        Assert.True(Skips(h) >= 1);
    }

    [Fact]
    public async Task Abandoned_before_compaction_starts_no_summary_request_is_even_sent()
    {
        var abandoned = false;
        using var h = new Harness(tools: [new FakeTool("echo", (_, _, _) =>
        {
            abandoned = true;
            return Task.FromResult(ToolResult.Text("ok"));
        })]);
        h.SeedHistory(
            Seed("h0", Role.User, new TextPart("first task")),
            Seed("h1", Role.Assistant, new TextPart("reply")));
        h.Llm.Turn(Up.CallReporting(200_000, "c1", "echo"), Up.Text("never sent"));

        var ran = await h.RunAsync("go", b =>
        {
            b.Settings = Settings;
            b.IsAbandoned = () => abandoned;
        });

        Assert.Empty(h.Llm.Summaries);
        Assert.DoesNotContain(h.Stored(), m => CompactBoundaries.BoundaryOf(m) is not null);
        Assert.Equal(RunStatus.Aborted, ran.Status);
    }

    [Fact]
    public async Task A_throwing_abandoned_check_counts_as_live_and_never_fails_the_run()
    {
        using var h = new Harness();
        h.Llm.Turn(Up.Text("hi"));

        var ran = await h.RunAsync("go", b => b.IsAbandoned = () => throw new InvalidOperationException("boom"));

        Assert.Equal(RunStatus.Done, ran.Status);
        Assert.Equal(2, h.Stored().Count);
    }
}
