using Ncw.Agent.Environments;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Json;
using Ncw.Agent.Llm.Routing;
using Ncw.Agent.Llm.Thinking;
using Ncw.Agent.Session.Tests.Support;
using Ncw.Core.Domain;

namespace Ncw.Agent.Session.Tests;

/// <summary>
/// 压缩(04 §4):边界、提示词、摘要格式、迭代摘要、累积的读写清单、PTL 重试、压缩后附件、模型绑定。
/// 移植自 2.x 的 <c>compact.test.ts / prompt.test.ts / attachments.test.ts / binding.test.ts / compaction-model.test.ts / shared/compaction.test.ts</c>;
/// 摘要模板换成 pi 的,差异见 parity.md。上游是假的,压缩走真 Llm 接口。
/// </summary>
public sealed class CompactionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"ncwcompact-{Guid.NewGuid():N}");
    private readonly LocalEnvironment _env = new("dev");
    private readonly FakeLlm _llm = new();

    public CompactionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static CompactBinding Binding(int window = 200_000, int maxOutput = 8_192, bool fellBack = false) =>
        new(ModelRef.Unpinned("claude-sonnet-4"), ThinkingLevel.Off, maxOutput, window, fellBack);

    private Compactor Compactor() => new(_llm, new SeqIds(), new FakeClock());

    private CompactRequest Request(IReadOnlyList<Message> messages, Action<Func<CompactRequest, CompactRequest>>? _ = null, string trigger = "auto") => new()
    {
        Messages = messages,
        Trigger = trigger,
        PreTokens = 123_456,
        AutoContinue = trigger == "auto",
        Binding = Binding(),
        Context = new RequestContext("ws", "run-1:compact", "s1"),
        ReadFile = (path, ct) => Ncw.Agent.Kernel.Session.Compactor.ReadAttachableFileAsync(_env, LocalEnvironment.PathOf(_dir), path, ct),
    };

    private static Message Ask(string id, string text) => Harness.User(id, text);

    private static Message Say(string id, string text) => Harness.Assistant(id, new TextPart(text));

    /// <summary>把原生路径放进 JSON 字符串字面量:Windows 路径里的反斜杠必须转义(<c>C:\Users</c> 的 <c>\U</c> 不是合法的 JSON 转义)。</summary>
    internal static string JsonPath(string path) => System.Text.Json.JsonEncodedText.Encode(path).ToString();

    private static Message Call(string id, string callId, string name, string json) => Harness.Assistant(id, new ToolCallPart(callId, name, Jsn.Parse(json)));

    private static Message Result(string id, string callId, string content = "ok", bool error = false) =>
        new(new MessageId(id), Role.User, [new ToolResultPart(callId, new ToolOutput(content), error)], DateTimeOffset.UnixEpoch);

    private static Message BoundaryMessage(string id, string summary, bool internalMessage = true) => new(
        new MessageId(id), Role.User, [new CompactBoundaryPart("auto", 1, 1, summary), new TextPart("continued: " + summary)], DateTimeOffset.UnixEpoch, Internal: internalMessage);

    private static IReadOnlyList<Message> Convo() => [Ask("u1", "refactor the login module"), Say("a1", "sure"), Ask("u2", "and add tests")];

    private async Task<CompactResult.Ok> Ok(IReadOnlyList<Message> messages, string trigger = "auto")
    {
        var result = await Compactor().CompactAsync(Request(messages, trigger: trigger), CancellationToken.None);
        return Assert.IsType<CompactResult.Ok>(result);
    }

    // ───────────── 边界(shared/compaction.ts) ─────────────

    [Fact]
    public void Boundary_of_recognises_the_boundary_part_and_nothing_else()
    {
        Assert.NotNull(CompactBoundaries.BoundaryOf(BoundaryMessage("b", "s")));
        Assert.Null(CompactBoundaries.BoundaryOf(Ask("u", "x")));
    }

    [Fact]
    public void For_model_returns_everything_as_a_copy_when_nothing_was_ever_compacted()
    {
        var transcript = Convo();

        var context = CompactBoundaries.ForModel(transcript);

        Assert.Equal(transcript.Select(m => m.Id), context.Select(m => m.Id));
        Assert.NotSame(transcript, context);
        Assert.Empty(CompactBoundaries.ForModel([]));
    }

    [Fact]
    public void For_model_starts_at_the_boundary_itself_and_with_several_only_the_last_counts()
    {
        var transcript = new[] { Ask("u0", "a"), BoundaryMessage("b1", "s1"), Ask("u1", "b"), BoundaryMessage("b2", "s2"), Ask("u2", "c") };

        Assert.Equal(["b2", "u2"], CompactBoundaries.ForModel(transcript).Select(m => m.Id.Value));
        Assert.Equal(3, CompactBoundaries.LastIndex(transcript));
        Assert.Equal(["b2"], CompactBoundaries.ForModel(transcript.Take(4).ToArray()).Select(m => m.Id.Value)); // 边界在末尾时只剩它一条
        Assert.Equal(-1, CompactBoundaries.LastIndex([Ask("u", "x")]));
    }

    [Fact]
    public void Deleting_the_boundary_message_automatically_reverts_to_not_compacted()
    {
        var transcript = new List<Message> { Ask("u0", "a"), BoundaryMessage("b1", "s"), Ask("u1", "b") };
        Assert.Equal(2, CompactBoundaries.ForModel(transcript).Count);

        transcript.RemoveAt(1);

        Assert.Equal(2, CompactBoundaries.ForModel(transcript).Count);
        Assert.Equal(["u0", "u1"], CompactBoundaries.ForModel(transcript).Select(m => m.Id.Value));
    }

    // ───────────── 提示词与摘要格式 ─────────────

    [Fact]
    public void The_summarise_prompt_has_every_pi_section_and_the_update_prompt_has_the_same_format_plus_the_preserve_rules()
    {
        foreach (var section in new[] { "## Goal", "## Constraints & Preferences", "## Progress", "### Done", "### In Progress", "### Blocked", "## Key Decisions", "## Next Steps", "## Critical Context" })
        {
            Assert.Contains(section, CompactionPrompts.SummarizePrompt);
            Assert.Contains(section, CompactionPrompts.UpdatePrompt);
        }

        Assert.Contains("PRESERVE all existing information", CompactionPrompts.UpdatePrompt);
        Assert.Contains("<previous-summary>", CompactionPrompts.UpdatePrompt);
        Assert.Contains("Do NOT call any tools", CompactionPrompts.SummarySystem);
    }

    [Fact]
    public void The_request_text_wraps_the_conversation_then_the_previous_summary_then_the_instruction()
    {
        var first = CompactionPrompts.RequestText("[User]: hi", null, null);
        var iterative = CompactionPrompts.RequestText("[User]: hi", "old summary", "focus on tests");

        Assert.StartsWith("<conversation>\n[User]: hi\n</conversation>", first);
        Assert.DoesNotContain("<previous-summary>", first);
        Assert.EndsWith(CompactionPrompts.SummarizePrompt, first);
        Assert.Contains("<previous-summary>\nold summary\n</previous-summary>", iterative);
        Assert.Contains(CompactionPrompts.UpdatePrompt, iterative);
        Assert.EndsWith("Additional focus: focus on tests", iterative);
    }

    [Fact]
    public void The_conversation_is_flattened_to_text_so_the_model_cannot_continue_it()
    {
        var messages = new[]
        {
            Ask("u1", "read the file"),
            Harness.Assistant("a1", new ThinkingPart("secret thoughts"), new TextPart("ok"), new ToolCallPart("c1", "Read", Jsn.Parse("""{"file_path":"/a.cs","limit":10}"""))),
            new Message(new MessageId("r1"), Role.User, [new ToolResultPart("c1", new ToolOutput("file body"), false)], DateTimeOffset.UnixEpoch),
            new Message(new MessageId("u2"), Role.User, [new ImagePart("image/png", "data:x")], DateTimeOffset.UnixEpoch),
        };

        var text = CompactionPrompts.Serialize(messages);

        Assert.Equal(
            "[User]: read the file\n\n[Assistant]: ok\n\n[Assistant tool calls]: Read(file_path=\"/a.cs\", limit=10)\n\n[Tool result]: file body\n\n[User]: [image]",
            text);
        Assert.DoesNotContain("secret thoughts", text);
    }

    [Fact]
    public void Tool_results_are_cut_to_2000_characters_with_a_note_saying_how_much_was_dropped()
    {
        var text = CompactionPrompts.Serialize([Result("r", "c", new string('z', 2500))]);

        Assert.Contains(new string('z', 2000), text);
        Assert.DoesNotContain(new string('z', 2001), text);
        Assert.Contains("[... 500 more characters truncated]", text);
    }

    [Fact]
    public void Format_summary_strips_an_analysis_scratchpad_and_takes_the_summary_block()
    {
        Assert.Equal("the real summary", CompactionPrompts.FormatSummary("<analysis>draft draft</analysis>\n<summary>\nthe real summary\n</summary>"));
        Assert.Equal("plain text without tags", CompactionPrompts.FormatSummary("plain text without tags")); // 没有 summary 标签时取全文
        Assert.Equal("", CompactionPrompts.FormatSummary("<analysis>cut off mid-thought...")); // analysis 没闭合:不把草稿当摘要
        Assert.Equal("a\n\nb", CompactionPrompts.FormatSummary("  a\n\n\n\n\nb  "));
    }

    [Fact]
    public void An_auto_compaction_tells_the_model_to_carry_on_a_manual_one_does_not()
    {
        Assert.Contains("without asking the user any further questions", CompactionPrompts.Continuation("S", autoContinue: true));
        Assert.DoesNotContain("without asking", CompactionPrompts.Continuation("S", autoContinue: false));
        Assert.Contains("S", CompactionPrompts.Continuation("S", false));
    }

    // ───────────── Compactor ─────────────

    [Fact]
    public async Task The_result_is_an_internal_user_message_whose_first_part_is_the_boundary_followed_by_the_continuation_text()
    {
        _llm.Summary = "## Goal\nrefactor login";

        var ok = await Ok(Convo());

        Assert.Equal(Role.User, ok.Message.Role); // 不能是助手消息,否则模型会把摘要当成自己说过的话
        Assert.True(ok.Message.Internal);
        Assert.Same(ok.Boundary, ok.Message.Parts[0]);
        Assert.Contains("## Goal\nrefactor login", ((TextPart)ok.Message.Parts[1]).Text);
        Assert.Equal(("auto", 123_456), (ok.Boundary.Trigger, ok.Boundary.PreTokens));
        Assert.Equal(TokenEstimator.Messages([ok.Message with { Parts = ok.Message.Parts.Skip(1).ToList() }]), ok.Boundary.PostTokens);
    }

    [Fact]
    public async Task The_summary_request_is_one_flattened_user_message_with_no_tools_and_the_summariser_system_prompt()
    {
        await Ok(Convo());

        var (request, model, context) = Assert.Single(_llm.Summaries);
        Assert.Equal(CompactionPrompts.SummarySystem, request.System);
        Assert.Empty(request.Tools);
        var message = Assert.Single(request.Messages);
        Assert.Equal(Role.User, message.Role);
        var text = ((TextPart)Assert.Single(message.Parts)).Text;
        Assert.Contains("[User]: refactor the login module", text);
        Assert.Contains("[Assistant]: sure", text);
        Assert.Equal(new GenerationOptions(8_192, ThinkingLevel.Off), request.Options);
        Assert.Equal("run-1:compact", context.RunId); // 独立的 RunId:用量单独记账
        Assert.Equal("claude-sonnet-4", model.Alias);
    }

    [Fact]
    public async Task Nothing_to_compact_right_after_a_compaction_is_a_conflict_with_a_translatable_key_and_no_upstream_call()
    {
        var result = await Compactor().CompactAsync(Request([Ask("old", "x"), BoundaryMessage("b", "s")]), CancellationToken.None);

        var failed = Assert.IsType<CompactResult.Failed>(result);
        Assert.Equal((AgentErrorCode.Conflict, CompactionErrorKeys.NothingToCompact, false), (failed.Error.Code, failed.Error.MessageKey, failed.Error.Retryable));
        Assert.Empty(_llm.Summaries);
    }

    [Fact]
    public async Task An_empty_summary_fails_instead_of_producing_an_empty_boundary()
    {
        _llm.SummaryScript(Up.Text("<analysis>x</analysis>"));

        var failed = Assert.IsType<CompactResult.Failed>(await Compactor().CompactAsync(Request(Convo()), CancellationToken.None));

        Assert.Equal(CompactionErrorKeys.EmptySummary, failed.Error.MessageKey);
    }

    [Fact]
    public async Task A_context_length_failure_drops_the_oldest_turns_and_retries_at_most_three_times()
    {
        var tooLong = AgentError.Create(AgentErrorCode.ContextLength, "prompt is too long");
        _llm.SummaryScript(Up.Fail(tooLong), Up.Fail(tooLong), Up.Fail(tooLong), Up.Fail(tooLong), Up.Fail(tooLong));
        var messages = Enumerable.Range(0, 20).SelectMany(i => new[] { Ask($"u{i}", $"question {i}"), Say($"a{i}", $"answer {i}") }).ToList();

        var failed = Assert.IsType<CompactResult.Failed>(await Compactor().CompactAsync(Request(messages), CancellationToken.None));

        Assert.Equal(AgentErrorCode.ContextLength, failed.Error.Code);
        Assert.Equal(Ncw.Agent.Kernel.Session.Compactor.MaxPtlRetries + 1, _llm.Summaries.Count);
        var sizes = _llm.Summaries.Select(s => ((TextPart)s.Request.Messages[0].Parts[0]).Text.Length).ToList();
        Assert.True(sizes.Zip(sizes.Skip(1), (a, b) => b < a).All(x => x)); // 每次重试都比上一次短
    }

    [Fact]
    public async Task A_context_length_failure_can_recover_on_a_retry()
    {
        _llm.SummaryScript(Up.Fail(AgentError.Create(AgentErrorCode.ContextLength, "too long")), Up.Text("## Goal\nrecovered"));
        var messages = Enumerable.Range(0, 10).SelectMany(i => new[] { Ask($"u{i}", $"q{i}"), Say($"a{i}", $"a{i}") }).ToList();

        var ok = Assert.IsType<CompactResult.Ok>(await Compactor().CompactAsync(Request(messages), CancellationToken.None));

        Assert.Contains("recovered", ok.Boundary.Summary);
        Assert.Equal(2, _llm.Summaries.Count);
    }

    [Fact]
    public async Task Other_failures_are_not_retried()
    {
        _llm.SummaryScript(Up.Fail(AgentError.Create(AgentErrorCode.Provider, "boom", true)));

        var failed = Assert.IsType<CompactResult.Failed>(await Compactor().CompactAsync(Request(Convo()), CancellationToken.None));

        Assert.Equal(AgentErrorCode.Provider, failed.Error.Code);
        Assert.Single(_llm.Summaries);
    }

    [Fact]
    public async Task An_aborted_summary_stream_surfaces_as_cancellation_not_as_a_failure_to_count()
    {
        _llm.SummaryScript(Up.Fail(AgentError.Create(AgentErrorCode.Aborted, "aborted")));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Compactor().CompactAsync(Request(Convo()), CancellationToken.None));
    }

    [Fact]
    public async Task Manual_compaction_records_the_instructions_and_forwards_them_as_additional_focus()
    {
        var req = Request(Convo(), trigger: "manual") with { Instructions = "  keep the API names  " };

        var ok = Assert.IsType<CompactResult.Ok>(await Compactor().CompactAsync(req, CancellationToken.None));

        Assert.Equal(("manual", "keep the API names"), (ok.Boundary.Trigger, ok.Boundary.Instructions));
        Assert.Contains("Additional focus: keep the API names", ((TextPart)_llm.Summaries[0].Request.Messages[0].Parts[0]).Text);
        Assert.DoesNotContain("without asking", ((TextPart)ok.Message.Parts[1]).Text);
    }

    [Fact]
    public async Task The_boundary_records_which_model_wrote_the_summary_and_whether_it_fell_back()
    {
        var req = Request(Convo()) with { Binding = Binding(fellBack: true) };

        var ok = Assert.IsType<CompactResult.Ok>(await Compactor().CompactAsync(req, CancellationToken.None));

        Assert.Equal(new SummaryModelInfo("claude-sonnet-4", null, "off", true), ok.Boundary.SummaryModel);
        var plain = await Ok(Convo());
        Assert.False(plain.Boundary.SummaryModel!.FellBack);
    }

    [Fact]
    public async Task A_custom_summary_from_an_extension_skips_the_model_entirely()
    {
        var req = Request(Convo()) with { CustomSummary = "hand written summary" };

        var ok = Assert.IsType<CompactResult.Ok>(await Compactor().CompactAsync(req, CancellationToken.None));

        Assert.Empty(_llm.Summaries);
        Assert.Contains("hand written summary", ok.Boundary.Summary);
    }

    [Fact]
    public async Task Extra_attachments_from_extensions_become_their_own_text_parts_after_the_summary()
    {
        var req = Request(Convo()) with { ExtraAttachments = ["<system-reminder>todo list</system-reminder>", ""] };

        var ok = Assert.IsType<CompactResult.Ok>(await Compactor().CompactAsync(req, CancellationToken.None));

        Assert.Equal("<system-reminder>todo list</system-reminder>", ((TextPart)ok.Message.Parts[^1]).Text);
        Assert.Equal(3, ok.Message.Parts.Count);
    }

    // ───────────── 迭代摘要与累积的文件清单 ─────────────

    [Fact]
    public async Task A_second_compaction_feeds_the_previous_summary_in_and_uses_the_update_prompt_with_only_the_new_messages()
    {
        _llm.SummaryScript(Up.Text("## Goal\nfirst"), Up.Text("## Goal\nsecond"));
        var first = await Ok(Convo());
        var transcript = new List<Message>(Convo()) { first.Message, Ask("u3", "now do the migration"), Say("a3", "done") };

        var second = await Ok(transcript);

        var text = ((TextPart)_llm.Summaries[1].Request.Messages[0].Parts[0]).Text;
        Assert.Contains("<previous-summary>\n## Goal\nfirst\n</previous-summary>", text);
        Assert.Contains(CompactionPrompts.UpdatePrompt, text);
        Assert.Contains("now do the migration", text);
        Assert.DoesNotContain("refactor the login module", text); // 旧的对话已经在上一份摘要里,不再重发
        Assert.Contains("second", second.Boundary.Summary);
    }

    [Fact]
    public async Task Read_and_modified_file_lists_accumulate_across_compactions_and_a_modified_file_leaves_the_read_list()
    {
        _llm.SummaryScript(Up.Text("## Goal\nfirst"), Up.Text("## Goal\nsecond"));
        var firstRound = new List<Message>
        {
            Ask("u1", "look around"),
            Call("a1", "c1", "Read", """{"file_path":"/src/a.cs"}"""), Result("r1", "c1"),
            Call("a2", "c2", "Read", """{"file_path":"/src/b.cs"}"""), Result("r2", "c2"),
            Call("a3", "c3", "Write", """{"file_path":"/src/new.cs"}"""), Result("r3", "c3"),
            Call("a4", "c4", "Read", """{"file_path":"/src/missing.cs"}"""), Result("r4", "c4", "not found", error: true),
        };
        var first = await Ok(firstRound);

        Assert.Contains("<read-files>\n/src/a.cs\n/src/b.cs\n</read-files>", first.Boundary.Summary);
        Assert.Contains("<modified-files>\n/src/new.cs\n</modified-files>", first.Boundary.Summary);
        Assert.DoesNotContain("missing.cs", first.Boundary.Summary); // 失败的调用不算「碰过」

        var secondRound = new List<Message>(firstRound) { first.Message, Ask("u2", "now edit a"), Call("a5", "c5", "Edit", """{"file_path":"/src/a.cs"}"""), Result("r5", "c5"), Call("a6", "c6", "Read", """{"file_path":"/src/c.cs"}"""), Result("r6", "c6") };
        var second = await Ok(secondRound);

        Assert.Contains("<read-files>\n/src/b.cs\n/src/c.cs\n</read-files>", second.Boundary.Summary);
        Assert.Contains("<modified-files>\n/src/a.cs\n/src/new.cs\n</modified-files>", second.Boundary.Summary);
        // 清单由代码写在摘要末尾,不让模型改:喂给模型做迭代的上一份摘要里没有它们
        var fed = ((TextPart)_llm.Summaries[1].Request.Messages[0].Parts[0]).Text;
        Assert.DoesNotContain("<read-files>", fed.Split("<previous-summary>")[1].Split("</previous-summary>")[0]);
    }

    [Fact]
    public void The_tracker_only_trusts_the_configured_tool_names_and_either_path_key()
    {
        var messages = new[]
        {
            Call("a1", "c1", "Read", """{"path":"/p.cs"}"""), Result("r1", "c1"),
            Call("a2", "c2", "Bash", """{"file_path":"/ignored"}"""), Result("r2", "c2"),
        };

        var ops = FileOperationTracker.Collect(messages, FileToolNames.Default);

        Assert.Equal(["/p.cs"], ops.Read);
        Assert.Empty(ops.Modified);
        Assert.Equal(["/p.cs"], FileOperationTracker.RecentPaths(messages, FileToolNames.Default));
    }

    [Fact]
    public void Recent_paths_are_newest_first_without_duplicates()
    {
        var messages = new[]
        {
            Call("a1", "c1", "Read", """{"file_path":"/a"}"""), Result("r1", "c1"),
            Call("a2", "c2", "Edit", """{"file_path":"/b"}"""), Result("r2", "c2"),
            Call("a3", "c3", "Read", """{"file_path":"/a"}"""), Result("r3", "c3"),
        };

        Assert.Equal(["/a", "/b"], FileOperationTracker.RecentPaths(messages, FileToolNames.Default));
    }

    [Fact]
    public void The_summary_file_blocks_split_and_format_roundtrip()
    {
        var text = "body text" + FileOperationTracker.Format(new FileOperations(["/r1", "/r2"], ["/m1"]));

        var (body, ops) = FileOperationTracker.Split(text);

        Assert.Equal("body text", body);
        Assert.Equal(["/r1", "/r2"], ops.Read);
        Assert.Equal(["/m1"], ops.Modified);
        Assert.Equal("", FileOperationTracker.Format(FileOperations.Empty));
    }

    // ───────────── 压缩前准备 ─────────────

    [Fact]
    public void Prepare_for_summary_drops_thinking_swaps_images_for_a_placeholder_and_removes_messages_that_end_up_empty()
    {
        var prepared = Ncw.Agent.Kernel.Session.Compactor.PrepareForSummary(
        [
            Harness.Assistant("a1", new ThinkingPart("secret", new ProtocolOpaque("anthropic", Jsn.Parse("""{"signature":"s"}""")))),
            Harness.Assistant("a2", new ThinkingPart("x"), new TextPart("kept")),
            new Message(new MessageId("u1"), Role.User, [new ImagePart("image/png", "data:image/png;base64,AAA"), new ImagePart("image/png", "ncw://attachments/sessions/s1/a.png")], DateTimeOffset.UnixEpoch),
            new Message(new MessageId("u2"), Role.User, [new ToolResultPart("c", new ToolOutput("shot", [new ToolOutputImage("image/png", "data:x")]), false)], DateTimeOffset.UnixEpoch),
        ]);

        Assert.Equal(["a2", "u1", "u2"], prepared.Select(m => m.Id.Value));
        Assert.Equal(["kept"], prepared[0].Parts.OfType<TextPart>().Select(p => p.Text));
        Assert.Equal(["[image]", "[image] ncw://attachments/sessions/s1/a.png"], prepared[1].Parts.OfType<TextPart>().Select(p => p.Text)); // 托管图的地址带上:摘要丢了它,模型就再也点不了名
        Assert.Null(((ToolResultPart)prepared[2].Parts[0]).Output.Images);
    }

    [Fact]
    public void Truncating_the_head_cuts_at_the_start_of_a_turn_so_no_orphan_tool_result_is_left_behind()
    {
        var messages = new List<Message> { Ask("u0", "q0"), Call("a0", "c0", "Read", "{}"), Result("r0", "c0"), Say("a1", "x"), Ask("u1", "q1"), Say("a2", "y"), Call("a3", "c3", "Read", "{}"), Result("r3", "c3"), Ask("u2", "q2"), Say("a4", "z") };

        var cut = Ncw.Agent.Kernel.Session.Compactor.TruncateHead(messages)!;

        Assert.Equal(Role.User, cut[0].Role);
        Assert.DoesNotContain(cut[0].Parts, p => p is ToolResultPart);
        Assert.True(cut.Count < messages.Count);
        Assert.Empty(Ncw.Agent.Kernel.Loop.Orphans.Find(cut));
    }

    [Fact]
    public void Truncating_the_head_keeps_a_leading_boundary_because_dropping_it_drops_all_older_memory()
    {
        var messages = new List<Message> { BoundaryMessage("b", "old memory"), Ask("u1", "a"), Say("a1", "b"), Ask("u2", "c"), Say("a2", "d"), Ask("u3", "e"), Say("a3", "f") };

        var cut = Ncw.Agent.Kernel.Session.Compactor.TruncateHead(messages)!;

        Assert.Equal("b", cut[0].Id.Value);
        Assert.True(cut.Count < messages.Count);
    }

    [Fact]
    public void Truncating_returns_null_when_nothing_more_can_go_rather_than_an_empty_array()
    {
        Assert.Null(Ncw.Agent.Kernel.Session.Compactor.TruncateHead([Ask("u1", "only")]));
        Assert.Null(Ncw.Agent.Kernel.Session.Compactor.TruncateHead([BoundaryMessage("b", "s"), Ask("u1", "only")]));
        Assert.Null(Ncw.Agent.Kernel.Session.Compactor.TruncateHead([Ask("u1", "a"), Call("a1", "c", "Read", "{}"), Result("r1", "c")])); // 没有可切的轮开头
    }

    [Fact]
    public async Task When_the_output_budget_eats_the_window_the_input_budget_is_floored_so_the_request_is_not_cut_down_to_one_message()
    {
        var messages = Enumerable.Range(0, 30).SelectMany(i => new[] { Ask($"u{i}", $"question number {i} " + new string('x', 200)), Say($"a{i}", "answer") }).ToList();
        var req = Request(messages) with { Binding = Binding(window: 8_000, maxOutput: 32_000) };

        await Compactor().CompactAsync(req, CancellationToken.None);

        var sent = ((TextPart)_llm.Summaries[0].Request.Messages[0].Parts[0]).Text;
        Assert.True(sent.Split("[User]:").Length > 5, "the floor (half the window) must keep several turns"); // 窗口 8K、输出 32K:减法是负数,封底到一半
    }

    // ───────────── 压缩后附件 ─────────────

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static IReadOnlyList<Message> Touched(params string[] paths) =>
        [Ask("u1", "go"), .. paths.SelectMany((p, i) => new[] { Call($"a{i}", $"c{i}", "Read", $$"""{"file_path":"{{JsonPath(p)}}"}"""), Result($"r{i}", $"c{i}") })];

    [Fact]
    public async Task Restored_file_content_is_read_from_disk_now_not_copied_from_the_transcript()
    {
        var path = Write("a.cs", "class Fresh {}");
        var messages = new List<Message> { Ask("u1", "go"), Call("a1", "c1", "Read", $$"""{"file_path":"{{JsonPath(path)}}"}"""), Result("r1", "c1", "class Stale {}") };

        var ok = await Ok(messages);

        var restored = ok.Message.Parts.OfType<TextPart>().Select(p => p.Text).Single(t => t.Contains("re-read from disk", StringComparison.Ordinal));
        Assert.Contains("class Fresh {}", restored);
        Assert.DoesNotContain("Stale", restored);
        Assert.Equal([path], ok.Boundary.RestoredFiles);
        Assert.StartsWith("<system-reminder>", restored);
    }

    [Fact]
    public async Task A_file_that_cannot_be_read_is_just_one_file_fewer_never_a_failed_compaction()
    {
        var good = Write("good.cs", "ok");
        var binary = Path.Combine(_dir, "bin.dat");
        File.WriteAllBytes(binary, [1, 2, 0, 3]);
        var directory = Path.Combine(_dir, "dir");
        Directory.CreateDirectory(directory);

        var ok = await Ok(Touched(Path.Combine(_dir, "deleted.cs"), binary, directory, good));

        Assert.Equal([good], ok.Boundary.RestoredFiles);
    }

    [Fact]
    public async Task At_most_five_files_are_restored_newest_first()
    {
        var paths = Enumerable.Range(0, 8).Select(i => Write($"f{i}.cs", $"content {i}")).ToArray();

        var ok = await Ok(Touched(paths));

        Assert.Equal(Ncw.Agent.Kernel.Session.Compactor.MaxRestoredFiles, ok.Boundary.RestoredFiles!.Count);
        Assert.Equal(paths.Reverse().Take(5), ok.Boundary.RestoredFiles);
    }

    [Fact]
    public async Task A_huge_file_is_not_read_at_all_and_a_big_one_is_cut_to_the_per_file_token_budget()
    {
        var huge = Path.Combine(_dir, "huge.log");
        using (var stream = File.Create(huge))
        {
            stream.SetLength(2 * 1024 * 1024);
        }

        var big = Write("big.cs", new string('x', 4 * 20_000)); // ≈20K token

        var ok = await Ok(Touched(huge, big));

        Assert.Equal([big], ok.Boundary.RestoredFiles);
        var text = ok.Message.Parts.OfType<TextPart>().Select(p => p.Text).Single(t => t.Contains("re-read from disk", StringComparison.Ordinal));
        Assert.Contains("truncated for context", text);
        Assert.True(TokenEstimate.OfText(text) <= Ncw.Agent.Kernel.Session.Compactor.MaxTokensPerFile + 100);
    }

    [Fact]
    public async Task Without_a_reader_nothing_is_restored_and_the_boundary_has_no_restored_list()
    {
        var path = Write("a.cs", "x");
        var req = Request(Touched(path)) with { ReadFile = null };

        var ok = Assert.IsType<CompactResult.Ok>(await Compactor().CompactAsync(req, CancellationToken.None));

        Assert.Null(ok.Boundary.RestoredFiles);
    }

    [Fact]
    public void Truncate_to_tokens_returns_short_text_untouched_and_cuts_both_latin_and_CJK_text_within_budget()
    {
        Assert.Equal("short", Ncw.Agent.Kernel.Session.Compactor.TruncateToTokens("short", 100));
        var latin = Ncw.Agent.Kernel.Session.Compactor.TruncateToTokens(new string('a', 4_000), 100);
        var cjk = Ncw.Agent.Kernel.Session.Compactor.TruncateToTokens(new string('字', 4_000), 100);

        Assert.Contains("truncated for context", latin);
        Assert.True(TokenEstimate.OfText(latin.Split("\n\n[...")[0]) <= 100);
        Assert.True(TokenEstimate.OfText(cjk.Split("\n\n[...")[0]) <= 100);
        Assert.True(cjk.Length < latin.Length); // 同样的预算,中文能装的字符数少得多
    }

    // ───────────── 压缩模型绑定 ─────────────

    private static readonly ModelBinding Session = new("claude-sonnet-4", "p1", "up", 200_000, 8_192);
    private static readonly ModelBinding Cheap = new("cheap-model", "p2", "cheap", 128_000, 8_192);

    private static readonly ModelBinding Gpt6 = new("gpt-6-astra", "p3", "gpt6", 200_000, 8_192,
        Thinking: new ThinkingConfig(ThinkingMode.Effort), ReasoningEfforts: [ReasoningEffort.Low, ReasoningEffort.Medium, ReasoningEffort.High, ReasoningEffort.XHigh, ReasoningEffort.Max]);

    private static Func<ModelRef, ModelBinding?> Resolver(params ModelBinding[] bindings) =>
        m => bindings.FirstOrDefault(b => b.Alias == m.Alias && (m.ProviderId is null || m.ProviderId == b.ProviderId));

    private static readonly ModelRef SessionModel = ModelRef.Unpinned("claude-sonnet-4");

    [Fact]
    public void With_nothing_configured_the_summary_follows_the_session_model_and_level()
    {
        var binding = CompactBinding.Resolve(Resolver(Session), SessionModel, ThinkingLevel.High)!;

        Assert.Equal(SessionModel, binding.Model);
        Assert.Equal(ThinkingLevel.High, binding.Thinking);
        Assert.False(binding.FellBack);
        Assert.Equal(ContextWindows.DefaultMaxOutputTokens, binding.MaxOutputTokens);
    }

    [Fact]
    public void A_configured_compaction_model_gets_the_summary_and_sizes_the_request_by_its_own_window()
    {
        var binding = CompactBinding.Resolve(Resolver(Session, Cheap), SessionModel, ThinkingLevel.Auto, new CompactionSettings("cheap-model", "p2"), maxOutputSetting: 200_000)!;

        Assert.Equal(new ModelRef("p2", "cheap-model"), binding.Model);
        Assert.Equal(128_000, binding.ProtocolWindow);
        Assert.Equal(128_000, binding.MaxOutputTokens); // 输出额度被压缩模型自己的窗口夹住
    }

    [Fact]
    public void A_model_that_cannot_turn_reasoning_off_gets_its_lowest_level_never_off()
    {
        var binding = CompactBinding.Resolve(Resolver(Session, Gpt6), SessionModel, ThinkingLevel.Auto,
            new CompactionSettings("gpt-6-astra", "p3", CompactThinkingSetting.Of(ThinkingLevel.Off)))!;

        Assert.Equal(ThinkingLevel.Low, binding.Thinking);
        Assert.Equal(ThinkingLevel.Medium, CompactBinding.AuxiliaryThinking(ThinkingLevel.Medium, Gpt6));
        Assert.Equal(ThinkingLevel.Auto, CompactBinding.AuxiliaryThinking(ThinkingLevel.Auto, Gpt6));
        Assert.Equal(ThinkingLevel.Off, CompactBinding.AuxiliaryThinking(ThinkingLevel.Off, Session)); // 没有声明:保留明确的 off
        Assert.Equal(ThinkingLevel.Off, CompactBinding.AuxiliaryThinking(ThinkingLevel.High, Session with { Thinking = new ThinkingConfig(ThinkingMode.Unsupported) }));
    }

    [Fact]
    public void A_vanished_compaction_model_falls_back_to_the_session_pair_instead_of_failing_the_compaction()
    {
        var warnings = new List<string>();
        var binding = CompactBinding.Resolve(Resolver(Session), new ModelRef("p1", "claude-sonnet-4"), ThinkingLevel.Auto,
            new CompactionSettings("deleted-alias", "gone"), warn: warnings.Add)!;

        Assert.Equal(new ModelRef("p1", "claude-sonnet-4"), binding.Model); // 别名和供应商一起换回去,不拼出「A 家的别名 + B 家的锁」
        Assert.True(binding.FellBack);
        Assert.Single(warnings);
        Assert.True(binding.Info.FellBack);
    }

    [Fact]
    public void It_returns_null_only_when_the_session_model_itself_cannot_be_resolved()
    {
        Assert.Null(CompactBinding.Resolve(Resolver(), SessionModel, ThinkingLevel.Auto));
        Assert.Null(CompactBinding.Resolve(Resolver(), SessionModel, ThinkingLevel.Auto, new CompactionSettings("x", "y")));
    }

    [Fact]
    public void The_workspace_beats_the_global_setting_including_an_explicit_follow_the_session()
    {
        var global = new CompactionSettings("cheap-model", "p2");

        var override1 = CompactBinding.SelectModel(new WorkspaceCompaction(Model: "claude-sonnet-4", ModelProviderId: "p1"), global, SessionModel);
        var follow = CompactBinding.SelectModel(new WorkspaceCompaction(FollowSessionModel: true), global, SessionModel);
        var unset = CompactBinding.SelectModel(new WorkspaceCompaction(Model: "  "), global, SessionModel); // 空串 = 没表态,不是「跟随会话」
        var nothing = CompactBinding.SelectModel(null, new CompactionSettings(), SessionModel);

        Assert.Equal((new ModelRef("p1", "claude-sonnet-4"), false), override1);
        Assert.Equal((SessionModel, true), follow);
        Assert.Equal((new ModelRef("p2", "cheap-model"), false), unset);
        Assert.Equal((SessionModel, true), nothing);
    }

    [Fact]
    public void The_thinking_level_is_chosen_workspace_then_global_then_the_run_level_for_inherit()
    {
        ThinkingLevel Level(CompactThinkingSetting? workspace, CompactThinkingSetting global) =>
            CompactBinding.Resolve(Resolver(Session), SessionModel, ThinkingLevel.Medium, new CompactionSettings(Thinking: global), new WorkspaceCompaction(Thinking: workspace))!.Thinking;

        Assert.Equal(ThinkingLevel.Medium, Level(null, CompactThinkingSetting.Inherit));
        Assert.Equal(ThinkingLevel.Low, Level(null, CompactThinkingSetting.Of(ThinkingLevel.Low)));
        Assert.Equal(ThinkingLevel.High, Level(CompactThinkingSetting.Of(ThinkingLevel.High), CompactThinkingSetting.Of(ThinkingLevel.Low)));
        Assert.Equal(ThinkingLevel.Medium, Level(CompactThinkingSetting.Inherit, CompactThinkingSetting.Of(ThinkingLevel.Low))); // 工作区显式选了 inherit
    }
}
