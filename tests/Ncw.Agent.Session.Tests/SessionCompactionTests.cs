using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Json;
using Ncw.Agent.Llm.Routing;
using Ncw.Agent.Llm.Thinking;
using Ncw.Agent.Session.Tests.Support;
using Ncw.Core.Domain;

namespace Ncw.Agent.Session.Tests;

/// <summary>
/// 会话里的压缩:触发(读上游真值)、边界落库、从边界切起、熔断、被动压缩、压缩模型、扩展点。
/// 移植自 2.x 的 <c>agent-session.test.ts</c> 的「自动压缩」「压缩过的会话从边界切起」两组。
/// 数值夹具按 8192 的输出预留标定(窗口 200K,阈值 ≈178.8K;<c>BIG</c> 估算约 100K,不靠真值就不会压)。
/// </summary>
public sealed class SessionCompactionTests
{
    private const string Note = "## Goal\n用户在重构登录模块";
    private static readonly string Big = new('x', 400_000);

    private static readonly SessionSettings Settings = new() { MaxOutputTokens = 8192 };

    private static Message Seed(string id, Role role, params Part[] parts) =>
        new(new MessageId(id), role, parts, DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000));

    private static string TextOf(Message m) => string.Concat(m.Parts.OfType<TextPart>().Select(p => p.Text));

    private static string Sent(FakeLlm llm, int turn) => string.Join("\n", llm.Requests[turn].Messages.Select(m => MessageCodec.PartsToJson(m.Parts)));

    /// <summary>历史里有一个大块:本地估算约 100K,判据说还宽裕——要靠上游真值才会触发。</summary>
    private static void SeedBlob(Harness h) => h.SeedHistory(
        Seed("h0", Role.User, new TextPart("最初的任务")),
        Seed("h1", Role.Assistant, new ToolCallPart("c0", "echo", Jsn.EmptyObject)),
        Seed("h2", Role.User, new ToolResultPart("c0", new ToolOutput(Big), false)),
        Seed("h3", Role.Assistant, new TextPart("回应 0")),
        Seed("h4", Role.User, new TextPart("追问 1")),
        Seed("h5", Role.Assistant, new TextPart("回应 2")));

    private static Harness WithEcho(IReadOnlyList<Ncw.Agent.Kernel.Extensions.IAgentExtension>? extensions = null, Func<ModelRef, ModelBinding?>? resolve = null) =>
        new(extensions, [new FakeTool("echo")], resolve);

    private static async Task<Ran> Run(Harness h, SessionSettings? settings = null) => await h.RunAsync("go", b => b.Settings = settings ?? Settings);

    [Fact]
    public async Task When_the_upstream_reports_a_real_size_far_above_the_estimate_the_next_turn_compacts()
    {
        using var h = WithEcho();
        SeedBlob(h);
        h.Llm.Turn(Up.CallReporting(200_000, "c1", "echo"), Up.Text("好"));

        var ran = await Run(h);

        Assert.Contains(Big, Sent(h.Llm, 0)); // 第一轮没压:大块原样发出去了(估算 ≈100K,判据说还宽裕)
        Assert.Single(h.Llm.Summaries);
        Assert.Equal([ContextStatusPhase.Compacting, ContextStatusPhase.Compacted], ran.Phases);
        Assert.Equal(RunStatus.Done, ran.Status);
    }

    [Fact]
    public async Task The_boundary_message_is_announced_in_the_event_stream_between_compacting_and_compacted()
    {
        // 回放对照发现的缺陷:边界消息经会话层直接落库,事件流里没有它的 message_start / message_end,
        // 控制端的镜像看不到分隔线(2.x 是 message_commit)。顺序同 2.x:compacting → 边界消息 → compacted。
        using var h = WithEcho();
        SeedBlob(h);
        h.Llm.Summary = Note;
        h.Llm.Turn(Up.CallReporting(200_000, "c1", "echo"), Up.Text("好"));

        var ran = await Run(h);

        var stored = h.Stored().Single(m => m.Parts.OfType<CompactBoundaryPart>().Any());
        var announced = ran.Events.OfType<MessageEndEvent>().Single(e => e.Message.Parts.OfType<CompactBoundaryPart>().Any());
        Assert.Equal(stored.Id, announced.MessageId);
        Assert.True(announced.Message.Internal);
        var kinds = ran.Events
            .Where(e => e is ContextStatusEvent || e is MessageStartEvent { Role: Role.User } start && start.MessageId == stored.Id || e is MessageEndEvent end && end.MessageId == stored.Id)
            .Select(e => e.GetType().Name)
            .ToList();
        Assert.Equal([nameof(ContextStatusEvent), nameof(MessageStartEvent), nameof(MessageEndEvent), nameof(ContextStatusEvent)], kinds);
    }

    [Fact]
    public async Task After_compaction_the_early_history_is_gone_from_the_request_and_no_orphan_call_is_left()
    {
        using var h = WithEcho();
        SeedBlob(h);
        h.Llm.Summary = Note;
        h.Llm.Turn(Up.CallReporting(200_000, "c1", "echo"), Up.Text("好"));

        await Run(h);

        var last = h.Llm.Requests.Count - 1;
        var after = Sent(h.Llm, last);
        Assert.Contains("用户在重构登录模块", after);
        Assert.DoesNotContain(Big, after);
        Assert.DoesNotContain("最初的任务", after);
        Assert.Empty(Orphans.Find(h.Llm.Requests[last].Messages));
    }

    [Fact]
    public async Task The_boundary_is_an_internal_user_message_in_the_database_and_the_full_transcript_stays()
    {
        using var h = WithEcho();
        SeedBlob(h);
        h.Llm.Summary = Note;
        h.Llm.Turn(Up.CallReporting(200_000, "c1", "echo"), Up.Text("好"));

        await Run(h);

        var stored = h.Stored();
        var boundary = Assert.Single(stored, m => CompactBoundaries.BoundaryOf(m) is not null);
        Assert.True(boundary.Internal);
        Assert.Equal(Role.User, boundary.Role);
        Assert.Equal(("auto", true), (CompactBoundaries.BoundaryOf(boundary)!.Trigger, CompactBoundaries.BoundaryOf(boundary)!.Summary.Contains("用户在重构登录模块", StringComparison.Ordinal)));
        Assert.Contains(stored, m => m.Parts.OfType<ToolResultPart>().Any(p => p.Output.Content == Big)); // 压缩改变的是「发给模型的范围」,不是聊天记录
        Assert.Contains("h0", stored.Select(m => m.Id.Value));
        // 重开之后上下文仍然从边界切起
        var context = AgentSession.BuildContext(stored);
        Assert.True(CompactBoundaries.BoundaryOf(context[0]) is not null);
    }

    [Fact]
    public async Task The_summary_request_uses_the_session_model_by_default_carries_no_tools_and_has_its_own_run_id()
    {
        using var h = WithEcho();
        SeedBlob(h);
        h.Llm.Turn(Up.CallReporting(200_000, "c1", "echo"), Up.Text("好"));

        await Run(h);

        var (request, model, context) = Assert.Single(h.Llm.Summaries);
        Assert.Equal("claude-sonnet-4", model.Alias);
        Assert.Empty(request.Tools);
        Assert.EndsWith(":compact", context.RunId);
        Assert.Equal("s1", context.SessionId);
        Assert.All(h.Llm.TurnModels, m => Assert.Equal("claude-sonnet-4", m.Alias)); // 正文仍发会话模型
    }

    [Fact]
    public async Task A_configured_compaction_model_gets_only_the_summary_while_the_body_stays_on_the_session_model()
    {
        var cheap = new ModelBinding("cheap-model", "p2", "cheap", 200_000, 8192);
        using var h = WithEcho(resolve: m => m.Alias == "cheap-model" ? cheap : m.Alias == Harness.Alias.Alias ? Harness.Alias : null);
        SeedBlob(h);
        h.Llm.Turn(Up.CallReporting(200_000, "c1", "echo"), Up.Text("好"));

        await Run(h, Settings with { Compaction = new CompactionSettings("cheap-model", "p2") });

        Assert.Equal(new ModelRef("p2", "cheap-model"), h.Llm.Summaries[0].Model);
        Assert.All(h.Llm.TurnModels, m => Assert.Equal("claude-sonnet-4", m.Alias));
    }

    [Fact]
    public async Task A_model_that_cannot_turn_reasoning_off_is_asked_with_its_lowest_level()
    {
        var gpt6 = new ModelBinding("gpt-6-astra", "p3", "g", 200_000, 8192, Thinking: new ThinkingConfig(ThinkingMode.Effort),
            ReasoningEfforts: [ReasoningEffort.Low, ReasoningEffort.High]);
        using var h = WithEcho(resolve: m => m.Alias == "gpt-6-astra" ? gpt6 : Harness.Alias);
        SeedBlob(h);
        h.Llm.Turn(Up.CallReporting(200_000, "c1", "echo"), Up.Text("好"));

        var ran = await Run(h, Settings with { Compaction = new CompactionSettings("gpt-6-astra", "p3", CompactThinkingSetting.Of(ThinkingLevel.Off)) });

        Assert.Equal(ThinkingLevel.Low, h.Llm.Summaries[0].Request.Options.Thinking); // 不是 off:压缩每次必败、三次熔断、上下文再也压不下去
        Assert.Equal([ContextStatusPhase.Compacting, ContextStatusPhase.Compacted], ran.Phases);
    }

    [Fact]
    public async Task A_vanished_compaction_model_falls_back_to_the_session_model_and_the_boundary_says_so()
    {
        using var h = WithEcho();
        SeedBlob(h);
        h.Llm.Turn(Up.CallReporting(200_000, "c1", "echo"), Up.Text("好"));

        var ran = await Run(h, Settings with { Compaction = new CompactionSettings("deleted-alias", "gone") });

        Assert.Equal("claude-sonnet-4", h.Llm.Summaries[0].Model.Alias);
        Assert.Equal([ContextStatusPhase.Compacting, ContextStatusPhase.Compacted], ran.Phases);
        var boundary = CompactBoundaries.BoundaryOf(h.Stored().Single(m => CompactBoundaries.BoundaryOf(m) is not null))!;
        Assert.True(boundary.SummaryModel!.FellBack);
        Assert.Equal("claude-sonnet-4", boundary.SummaryModel.Model);
    }

    [Fact]
    public async Task A_workspace_override_beats_the_global_setting_including_an_explicit_follow_the_session()
    {
        var cheap = new ModelBinding("cheap-model", "p2", "cheap", 200_000, 8192);
        using var h = WithEcho(resolve: m => m.Alias == "cheap-model" ? cheap : Harness.Alias);
        SeedBlob(h);
        h.Llm.Turn(Up.CallReporting(200_000, "c1", "echo"), Up.Text("好"));

        await Run(h, Settings with { Compaction = new CompactionSettings("cheap-model", "p2"), WorkspaceCompaction = new WorkspaceCompaction(FollowSessionModel: true) });

        Assert.Equal("claude-sonnet-4", h.Llm.Summaries[0].Model.Alias);
    }

    [Fact]
    public async Task A_real_size_below_the_estimate_does_not_relax_the_verdict()
    {
        using var h = WithEcho();
        SeedBlob(h);
        h.Llm.Turn(Up.CallReporting(10, "c1", "echo"), Up.Text("好"));

        var ran = await Run(h);

        Assert.Empty(h.Llm.Summaries);
        Assert.Empty(ran.Phases);
    }

    [Fact]
    public async Task On_the_first_turn_there_is_no_real_size_and_the_verdict_is_the_pure_estimate()
    {
        using var h = WithEcho();
        SeedBlob(h);

        var ran = await Run(h);

        Assert.False(ran.Of<ContextUsageEvent>().First().ShouldCompact);
        Assert.Contains(Big, Sent(h.Llm, 0));
        Assert.Empty(h.Llm.Summaries);
    }

    [Fact]
    public async Task With_auto_compact_off_nothing_is_ever_compacted_even_when_the_verdict_is_true()
    {
        using var h = WithEcho();
        SeedBlob(h);
        h.Llm.Turn(Up.CallReporting(185_000, "c1", "echo"), Up.Text("好"));

        var ran = await Run(h, Settings with { AutoCompact = false });

        Assert.Empty(h.Llm.Summaries);
        Assert.Empty(ran.Phases);
        Assert.True(ran.Of<ContextUsageEvent>().Last().ShouldCompact); // 压力条照常说该压了
    }

    [Fact]
    public async Task A_summary_that_keeps_failing_trips_the_breaker_after_three_tries_and_the_verdict_stays_honest()
    {
        using var h = WithEcho();
        SeedBlob(h);
        var boom = AgentError.Create(AgentErrorCode.Provider, "summary failed", true);
        h.Llm.SummaryScript(Up.Fail(boom), Up.Fail(boom), Up.Fail(boom), Up.Fail(boom), Up.Fail(boom));
        // 185K:在阈值(≈178.8K)之上、协议窗口硬校验(≈191.8K)之下——「该压、压不动、但还发得出去」
        h.Llm.Turn(Up.CallReporting(185_000, "c1", "echo"), Up.CallReporting(185_000, "c2", "echo"), Up.CallReporting(185_000, "c3", "echo"),
            Up.CallReporting(185_000, "c4", "echo"), Up.Text("好"));

        var ran = await Run(h);

        Assert.Equal(Compactor.MaxConsecutiveFailures, h.Llm.Summaries.Count);
        Assert.Equal(ContextStatusPhase.Disabled, ran.Phases[^1]);
        Assert.Equal([ContextStatusPhase.Compacting, ContextStatusPhase.Failed, ContextStatusPhase.Compacting, ContextStatusPhase.Failed, ContextStatusPhase.Compacting, ContextStatusPhase.Disabled], ran.Phases);
        Assert.True(ran.Of<ContextUsageEvent>().Last().ShouldCompact); // 熔断之后判据本身不变,只是不再空转
        Assert.Equal(RunStatus.Done, ran.Status);
    }

    [Fact]
    public async Task A_context_length_rejection_with_no_content_compacts_once_and_resends_the_turn()
    {
        using var h = WithEcho();
        h.SeedHistory(Seed("h0", Role.User, new TextPart("earlier work")), Seed("h1", Role.Assistant, new TextPart("done earlier")));
        h.Llm.Summary = Note;
        h.Llm.Turn(Up.Fail(AgentError.Create(AgentErrorCode.ContextLength, "prompt is too long")), Up.Text("recovered"));

        var ran = await Run(h);

        Assert.Equal(RunStatus.Done, ran.Status);
        Assert.Single(h.Llm.Summaries);
        Assert.Equal(2, h.Llm.Requests.Count);
        Assert.Contains("用户在重构登录模块", Sent(h.Llm, 1));
        Assert.DoesNotContain("earlier work", Sent(h.Llm, 1));
        Assert.Equal([ContextStatusPhase.Compacting, ContextStatusPhase.Compacted], ran.Phases);
        Assert.DoesNotContain(h.Stored(), m => m.Parts.Any(p => p is ErrorPart)); // 被拒的那一次不留错误消息
    }

    [Fact]
    public async Task If_compaction_cannot_save_the_turn_the_original_error_is_reported_and_the_turn_is_not_compacted_twice()
    {
        using var h = WithEcho();
        h.SeedHistory(Seed("h0", Role.User, new TextPart("x")), Seed("h1", Role.Assistant, new TextPart("y")));
        var tooLong = AgentError.Create(AgentErrorCode.ContextLength, "prompt is too long");
        h.Llm.Turn(Up.Fail(tooLong), Up.Fail(tooLong));

        var ran = await Run(h);

        Assert.Equal(RunStatus.Error, ran.Status);
        Assert.Equal(AgentErrorCode.ContextLength, ran.Outcome.Error!.Code);
        Assert.Single(h.Llm.Summaries); // 压完还被拒说明压缩救不了,不再压
    }

    [Fact]
    public async Task Project_instructions_stay_on_the_first_message_after_compaction_with_exactly_one_copy()
    {
        using var h = WithEcho();
        File.WriteAllText(Path.Combine(h.Root, "AGENTS.md"), "这个仓库用 pnpm。");
        SeedBlob(h);
        h.Llm.Turn(Up.CallReporting(200_000, "c1", "echo"), Up.Text("好"));

        await Run(h);

        var last = h.Llm.Requests[^1];
        Assert.Equal(1, last.Messages.Sum(m => m.Parts.OfType<TextPart>().Count(p => p.Text.Contains("<project-instructions>", StringComparison.Ordinal))));
        Assert.Contains("<project-instructions>", TextOf(last.Messages[0]));
        Assert.True(CompactBoundaries.BoundaryOf(last.Messages[0]) is not null);
    }

    [Fact]
    public async Task Files_touched_before_compaction_are_re_read_from_disk_and_attached_after_the_summary()
    {
        using var h = WithEcho();
        var file = Path.Combine(h.Root, "a.cs");
        File.WriteAllText(file, "class Fresh {}");
        h.SeedHistory(
            Seed("h0", Role.User, new TextPart("edit a")),
            Seed("h1", Role.Assistant, new ToolCallPart("c0", "Read", Jsn.Parse($$"""{"file_path":"{{file}}"}"""))),
            Seed("h2", Role.User, new ToolResultPart("c0", new ToolOutput("class Stale {}" + Big), false)),
            Seed("h3", Role.Assistant, new TextPart("ok")));
        h.Llm.Turn(Up.CallReporting(200_000, "c1", "echo"), Up.Text("好"));

        await Run(h);

        var boundaryMessage = h.Stored().Single(m => CompactBoundaries.BoundaryOf(m) is not null);
        Assert.Contains(boundaryMessage.Parts.OfType<TextPart>(), p => p.Text.Contains("class Fresh {}", StringComparison.Ordinal));
        Assert.Equal([file], CompactBoundaries.BoundaryOf(boundaryMessage)!.RestoredFiles);
    }

    // ───────────── 扩展点 ─────────────

    [Fact]
    public async Task Before_compact_can_cancel_which_counts_as_a_failure_and_sends_no_summary_request()
    {
        var ext = new Ext("veto", 0, r => r.Gate(SessionExtensionPoints.BeforeCompact, (_, _, _) => ValueTask.FromResult(CompactGateDecision.Cancel("not now"))));
        using var h = WithEcho([ext]);
        SeedBlob(h);
        h.Llm.Turn(Up.CallReporting(185_000, "c1", "echo"), Up.Text("好"));

        var ran = await Run(h);

        Assert.Empty(h.Llm.Summaries);
        Assert.Equal([ContextStatusPhase.Compacting, ContextStatusPhase.Failed], ran.Phases);
        Assert.Equal(RunStatus.Done, ran.Status);
    }

    [Fact]
    public async Task Before_compact_can_supply_a_summary_compact_attachments_ride_along_and_after_compact_is_told()
    {
        AfterCompactEvent? told = null;
        var ext = new Ext("skills", 0, r =>
        {
            r.Gate(SessionExtensionPoints.BeforeCompact, (_, _, _) => ValueTask.FromResult(CompactGateDecision.Custom("## Goal\nfrom the extension")));
            r.Mutate(SessionExtensionPoints.CompactAttachments, (draft, _, _) =>
            {
                draft.Texts.Add("<system-reminder>\nYour todo list before the conversation was compacted:\n[ ] ship\n</system-reminder>");
                return ValueTask.CompletedTask;
            });
            r.On(SessionExtensionPoints.AfterCompact, (e, _, _) =>
            {
                told = e;
                return ValueTask.CompletedTask;
            });
        });
        using var h = WithEcho([ext]);
        SeedBlob(h);
        h.Llm.Turn(Up.CallReporting(200_000, "c1", "echo"), Up.Text("好"));

        await Run(h);

        Assert.Empty(h.Llm.Summaries); // 现成的摘要:不调模型
        var boundary = h.Stored().Single(m => CompactBoundaries.BoundaryOf(m) is not null);
        Assert.Contains("from the extension", CompactBoundaries.BoundaryOf(boundary)!.Summary);
        Assert.Contains(boundary.Parts.OfType<TextPart>(), p => p.Text.Contains("[ ] ship", StringComparison.Ordinal));
        Assert.Equal(boundary.Id, told!.Boundary.Id);
        Assert.Contains("from the extension", Sent(h.Llm, h.Llm.Requests.Count - 1));
    }

    // ───────────── 手动压缩 ─────────────

    [Fact]
    public async Task Manual_compaction_commits_a_manual_boundary_without_the_carry_on_wording_and_a_second_one_has_nothing_to_do()
    {
        using var h = new Harness();
        h.SeedHistory(Seed("u0", Role.User, new TextPart("q")), Seed("a0", Role.Assistant, new TextPart("a")));
        h.Llm.Summary = Note;

        var outcome = await h.Session.CompactNowAsync(h.Model, ThinkingLevel.Auto, instructions: "keep API names");

        Assert.Null(outcome.Error);
        var boundary = CompactBoundaries.BoundaryOf(outcome.Boundary!)!;
        Assert.Equal(("manual", "keep API names"), (boundary.Trigger, boundary.Instructions));
        Assert.Equal(outcome.Boundary!.Id, h.Stored()[^1].Id);
        Assert.DoesNotContain("without asking", TextOf(outcome.Boundary));
        Assert.Contains("Additional focus: keep API names", ((TextPart)h.Llm.Summaries[0].Request.Messages[0].Parts[0]).Text);
        Assert.False(h.Session.IsBusy);

        var again = await h.Session.CompactNowAsync(h.Model, ThinkingLevel.Auto);
        Assert.Equal(CompactionErrorKeys.NothingToCompact, again.Error!.MessageKey);
        Assert.Single(h.Llm.Summaries);
    }

    [Fact]
    public async Task A_manual_compaction_failure_is_reported_never_counted_towards_a_breaker()
    {
        using var h = new Harness();
        h.SeedHistory(Seed("u0", Role.User, new TextPart("q")), Seed("a0", Role.Assistant, new TextPart("a")));
        h.Llm.SummaryScript(Up.Fail(AgentError.Create(AgentErrorCode.Provider, "boom", true)));

        var outcome = await h.Session.CompactNowAsync(h.Model, ThinkingLevel.Auto);

        Assert.Null(outcome.Boundary);
        Assert.NotNull(outcome.Error);
        Assert.Equal([ContextStatusPhase.Compacting, ContextStatusPhase.Failed], outcome.Events.OfType<ContextStatusEvent>().Select(e => e.Phase));
        Assert.Single(h.Stored(), m => m.Role == Role.User); // 转录一条没少
    }

    [Fact]
    public async Task An_abort_during_compaction_is_an_abort_and_leaves_no_boundary_behind()
    {
        using var h = WithEcho();
        SeedBlob(h);
        h.Llm.SummaryScript(Up.Hang("sum"));
        h.Llm.Turn(Up.CallReporting(200_000, "c1", "echo"));
        using var cts = new CancellationTokenSource();
        var sink = new ListRunSink();
        var run = h.Session.RunAsync(h.Request("go", null, b => b.Settings = Settings), sink, cts.Token);
        while (h.Llm.Summaries.Count == 0)
        {
            await Task.Delay(5);
        }

        await cts.CancelAsync();
        var outcome = await run;

        Assert.Equal(RunStatus.Aborted, outcome.Status);
        Assert.DoesNotContain(h.Stored(), m => CompactBoundaries.BoundaryOf(m) is not null);
        Assert.Empty(Orphans.Find(h.Stored()));
        Assert.False(h.Session.IsBusy);
    }
}
