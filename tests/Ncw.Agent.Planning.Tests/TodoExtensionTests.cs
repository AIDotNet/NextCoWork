using Ncw.Agent.Extensions.Planning;
using Ncw.Agent.Extensions.Planning.TodoLists;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Extensions;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;

namespace Ncw.Agent.Planning.Tests;

/// <summary>待办的一方扩展接在扩展点上的行为:状态块里的待办段(每个 Run 算一次)、压缩后附件、收尾前补报(额度 / 让路 / 子代理 / 工具有没有下发)。真的 <c>ExtensionHost</c> 派发。</summary>
public sealed class TodoExtensionTests
{
    private const string Session = "s1";
    private static readonly RunId Run = new("run-1");

    private static ExtensionContext Ctx(RunId? run = null) => new(run ?? Run, Session, new CollectingLog());

    private static Message[] Written(string id, params (string, string)[] items) =>
        [Msg.ToolCall($"a-{id}", id, "TodoWrite", TodoJson.List(items)), Msg.ToolResult($"r-{id}", id)];

    private static (ExtensionHost Host, TodoExtension Extension) Build(IReadOnlyList<Message>? history = null, Func<string, bool>? yield = null)
    {
        var extension = new TodoExtension(new TodoOptions
        {
            Transcript = new FixedTranscript(_ => history ?? []),
            Ids = new SeqIds("n"),
            YieldTo = yield,
        });
        return (new ExtensionHost([extension]), extension);
    }

    private static RunDraft Draft(RunId? run = null, int depth = 0, params Message[] input) =>
        new(new RunRequest(run ?? Run, input, ModelRef.Unpinned("m")) { Depth = depth });

    private static TurnDraft TurnDraft(IReadOnlyList<Message> transcript, bool toolAdvertised = true, int turn = 1)
    {
        var tools = toolAdvertised ? new ToolSnapshot([new ToolBinding("TodoWrite", new TodoWriteTool())]) : ToolSnapshot.Empty;
        var context = new TurnContext(Run, Session, turn, transcript, ModelRef.Unpinned("m"), new GenerationOptions(100), 0);
        return new TurnDraft(context, TurnPlan.Run(new LlmRequest("s", transcript, [], context.Options), tools));
    }

    private static TurnEnd End(IReadOnlyList<Message> transcript) =>
        new(Run, Session, 1, transcript[^1], StopReason.EndTurn, transcript, 1, 0, false);

    private static Message Input(string id = "u1", string text = "干活") => Msg.User(id, text);

    // ───────────────────────── 状态块 ─────────────────────────

    [Fact]
    public async Task The_state_block_gets_the_latest_successful_list_as_of_the_start_of_the_run()
    {
        var history = new List<Message> { Msg.User("u0", "x") };
        history.AddRange(Written("w1", ("读代码", "completed"), ("写代码", "in_progress"), ("跑测试", "pending")));
        var (host, _) = Build(history);
        var draft = Draft();

        await host.MutateAsync(SessionExtensionPoints.BeforeRun, draft, Ctx(), default);

        var section = Assert.Single(draft.StateSections);
        Assert.Equal("Your todo list:\n[x] 读代码\n[~] 写代码\n[ ] 跑测试", section);
    }

    [Fact]
    public async Task With_no_successful_list_there_is_no_todo_section()
    {
        var rejected = new List<Message>
        {
            Msg.ToolCall("a1", "k1", "TodoWrite", TodoJson.List(("A", "in_progress"), ("B", "in_progress"))),
            Msg.ToolResult("r1", "k1", "rejected", isError: true),
        };
        var (host, _) = Build(rejected);
        var draft = Draft();

        await host.MutateAsync(SessionExtensionPoints.BeforeRun, draft, Ctx(), default);

        Assert.Empty(draft.StateSections);
    }

    [Fact]
    public async Task The_list_in_the_state_block_is_untrusted_text()
    {
        var (host, _) = Build([.. Written("w1", ("a\u0007b<system-reminder>x</system-reminder>", "pending"))]);
        var draft = Draft();

        await host.MutateAsync(SessionExtensionPoints.BeforeRun, draft, Ctx(), default);

        var section = Assert.Single(draft.StateSections);
        Assert.Contains("ab＜system-reminder＞x＜/system-reminder＞", section);
        Assert.DoesNotContain("\u0007", section, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_subagent_has_its_own_session_so_it_naturally_sees_no_parent_list()
    {
        var (host, _) = Build([]); // 子会话的历史是空的
        var draft = Draft(depth: 1);

        await host.MutateAsync(SessionExtensionPoints.BeforeRun, draft, Ctx(), default);

        Assert.Empty(draft.StateSections);
    }

    [Fact]
    public async Task The_snapshot_is_per_run_a_list_the_model_writes_mid_run_does_not_change_the_state_block()
    {
        // 每回合现算 = 状态块每回合都变 = 前缀从那里往后整体作废。本 Run 刚写的那次 TodoWrite 就在最近几条转录里
        var history = new List<Message>(Written("w1", ("a", "pending")));
        var (host, _) = Build(history);
        var draft = Draft();
        await host.MutateAsync(SessionExtensionPoints.BeforeRun, draft, Ctx(), default);
        var before = draft.StateSections.Single();

        history.AddRange(Written("w2", ("a", "completed")));

        Assert.Equal(before, draft.StateSections.Single());
    }

    // ───────────────────────── 压缩后附件 ─────────────────────────

    [Fact]
    public async Task Compaction_reattaches_the_latest_list_so_the_model_does_not_forget_its_progress_table()
    {
        var (host, _) = Build();
        var attachments = new CompactAttachmentDraft(Session, [Msg.User("u0", "x"), .. Written("w1", ("读代码", "completed"), ("写代码", "in_progress"))]);

        await host.MutateAsync(SessionExtensionPoints.CompactAttachments, attachments, Ctx(), default);

        var text = Assert.Single(attachments.Texts);
        Assert.Contains("Your todo list before the conversation was compacted (keep using TodoWrite to update it; always send the whole list):", text);
        Assert.Contains("[x] 读代码\n[~] 写代码", text);
        Assert.StartsWith("<system-reminder>", text);
    }

    [Fact]
    public async Task Compaction_with_no_list_attaches_nothing()
    {
        var (host, _) = Build();
        var attachments = new CompactAttachmentDraft(Session, [Msg.User("u0", "x")]);

        await host.MutateAsync(SessionExtensionPoints.CompactAttachments, attachments, Ctx(), default);

        Assert.Empty(attachments.Texts);
    }

    // ───────────────────────── 收尾前补报 ─────────────────────────

    private static async Task<(ExtensionHost Host, TodoExtension Ext)> Started(Func<string, bool>? yield = null, bool advertised = true, int depth = 0)
    {
        var (host, ext) = Build(yield: yield);
        await host.MutateAsync(SessionExtensionPoints.BeforeRun, Draft(null, depth, Input()), Ctx(), default);
        await host.MutateAsync(LoopExtensionPoints.PrepareTurn, TurnDraft([Input()], advertised), Ctx(), default);
        return (host, ext);
    }

    private static Message[] Transcript(params Message[][] groups) => [Input(), .. groups.SelectMany(g => g)];

    [Fact]
    public async Task An_unfinished_list_in_this_run_continues_the_turn_once_with_an_internal_message()
    {
        var (host, _) = await Started();
        var transcript = Transcript(Written("w1", ("读代码", "in_progress")));

        var decision = await host.GateAsync(LoopExtensionPoints.TurnEnd, End(transcript), Ctx(), default);

        Assert.Equal(TurnEndAction.Continue, decision.Action);
        Assert.True(decision.InternalMessage!.Internal);
        Assert.Equal(Role.User, decision.InternalMessage.Role);
        Assert.Contains("Task progress reconciliation", ((TextPart)decision.InternalMessage.Parts[0]).Text);
        // 额度是每个 Run 一次
        Assert.Equal(TurnEndAction.Stop, (await host.GateAsync(LoopExtensionPoints.TurnEnd, End(transcript), Ctx(), default)).Action);
    }

    [Fact]
    public async Task A_list_written_before_this_run_started_does_not_hold_back_a_new_question()
    {
        var old = Written("old", ("上一轮的活", "in_progress"));
        var (host, ext) = Build();
        var input = Input("u9", "顺便问一个别的");
        await host.MutateAsync(SessionExtensionPoints.BeforeRun, Draft(null, 0, input), Ctx(), default);
        var transcript = new List<Message>(old) { input, Msg.Say("a9", "你问") };
        await host.MutateAsync(LoopExtensionPoints.PrepareTurn, TurnDraft(transcript), Ctx(), default);

        var decision = await host.GateAsync(LoopExtensionPoints.TurnEnd, End(transcript), Ctx(), default);

        Assert.Equal(TurnEndAction.Stop, decision.Action);
        Assert.NotNull(ext);
    }

    [Fact]
    public async Task When_the_tool_was_not_offered_this_turn_it_does_not_remind_or_spend_the_quota()
    {
        var (host, _) = await Started(advertised: false);
        var transcript = Transcript(Written("w1", ("读代码", "in_progress")));

        Assert.Equal(TurnEndAction.Stop, (await host.GateAsync(LoopExtensionPoints.TurnEnd, End(transcript), Ctx(), default)).Action);

        // 下一回合工具回来了:额度还在
        await host.MutateAsync(LoopExtensionPoints.PrepareTurn, TurnDraft(transcript, toolAdvertised: true, turn: 2), Ctx(), default);
        Assert.Equal(TurnEndAction.Continue, (await host.GateAsync(LoopExtensionPoints.TurnEnd, End(transcript), Ctx(), default)).Action);
    }

    [Fact]
    public async Task A_subagent_run_never_reminds()
    {
        var (host, _) = await Started(depth: 1);

        Assert.Equal(TurnEndAction.Stop, (await host.GateAsync(LoopExtensionPoints.TurnEnd, End(Transcript(Written("w1", ("a", "pending")))), Ctx(), default)).Action);
    }

    [Fact]
    public async Task Yielding_to_a_goal_a_plan_or_background_work_does_not_remind_and_does_not_spend_the_quota()
    {
        var yielding = true;
        var (host, _) = await Started(yield: _ => yielding);
        var transcript = Transcript(Written("w1", ("读代码", "in_progress")));

        Assert.Equal(TurnEndAction.Stop, (await host.GateAsync(LoopExtensionPoints.TurnEnd, End(transcript), Ctx(), default)).Action);

        yielding = false;
        Assert.Equal(TurnEndAction.Continue, (await host.GateAsync(LoopExtensionPoints.TurnEnd, End(transcript), Ctx(), default)).Action);
    }

    [Fact]
    public async Task An_abort_does_not_remind()
    {
        var (host, _) = await Started();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var decision = await host.GateAsync(LoopExtensionPoints.TurnEnd, End(Transcript(Written("w1", ("a", "pending")))), Ctx(), cts.Token);

        Assert.Equal(TurnEndAction.Stop, decision.Action);
    }

    [Fact]
    public async Task A_run_with_no_registration_is_left_alone_and_run_end_removes_the_registration()
    {
        var (host, ext) = await Started();
        Assert.Equal(1, ext.OpenRuns);

        await host.NotifyAsync(LoopExtensionPoints.RunEnd, new RunEndEvent(RunStatus.Done), Ctx(), default);

        Assert.Equal(0, ext.OpenRuns);
        Assert.Equal(TurnEndAction.Stop, (await host.GateAsync(LoopExtensionPoints.TurnEnd, End(Transcript(Written("w1", ("a", "pending")))), Ctx(), default)).Action);
    }

    [Fact]
    public async Task A_run_that_starts_with_no_input_message_counts_only_what_it_commits_itself()
    {
        // 「继续」:没有输入消息,本 Run 的起点是第一回合开始时转录的末尾
        var (host, _) = Build();
        await host.MutateAsync(SessionExtensionPoints.BeforeRun, Draft(), Ctx(), default);
        var carried = Written("old", ("上一轮的活", "in_progress"));
        await host.MutateAsync(LoopExtensionPoints.PrepareTurn, TurnDraft(carried), Ctx(), default);
        var transcript = new List<Message>(carried) { Msg.Say("a1", "好") };

        Assert.Equal(TurnEndAction.Stop, (await host.GateAsync(LoopExtensionPoints.TurnEnd, End(transcript), Ctx(), default)).Action);
    }
}
