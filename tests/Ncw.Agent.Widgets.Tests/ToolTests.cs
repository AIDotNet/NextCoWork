using System.Text.Json;
using Ncw.Agent.Extensions.Interactions;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;
using Ncw.Agent.Widgets;

namespace Ncw.Agent.Widgets.Tests;

/// <summary>2.x <c>visualize.test.ts</c> 的逐条移植(show / read_me / actions / interactive / edit / update),加上 3.0 新增的消毒、静态快照、环境标识和先到先得。</summary>
public sealed class ToolTests
{
    private const string Code = """<svg viewBox="0 0 10 10"><circle cx="5" cy="5" r="4" /></svg>""";

    private readonly Rig _rig = new();
    private readonly RunId _run = new("run-1");

    private string Show(string extra = "", string code = Code, string title = "compound_interest") =>
        $$"""{"title":"{{title}}","widget_code":{{Json.Esc(code)}},"loading_messages":["draw"]{{extra}}}""";

    private Task<ToolResult> ShowAsync(string extra = "", string code = Code, string title = "compound_interest", string callId = "call-1") =>
        _rig.Run("visualize_show_widget", Show(extra, code, title), _rig.Ctx(_run, callId));

    private static JsonElement Card(ToolResult r) => r.Details!.Value;

    // ───────────────────────── visualize_show_widget ─────────────────────────

    [Fact]
    public async Task A_card_is_produced_and_the_code_goes_only_to_the_card()
    {
        _rig.Register(_run);
        var r = await ShowAsync();
        Assert.False(r.IsError, Rig.Text(r));
        var card = Card(r);
        Assert.Equal("widget", card.GetProperty("kind").GetString());
        Assert.Equal("compound_interest", card.GetProperty("title").GetString());
        Assert.Equal("svg", card.GetProperty("format").GetString());
        Assert.Equal(Code, card.GetProperty("code").GetString());
        Assert.Equal(Code, card.GetProperty("snapshot").GetString());
        Assert.Equal(10, card.GetProperty("size").GetProperty("width").GetInt32());
        // 反向断言:给模型的内容不含代码(它刚写完,回灌既浪费窗口又会让它想再改一遍)
        Assert.DoesNotContain("<svg", Rig.Text(r));
        Assert.DoesNotContain("circle", Rig.Text(r));
    }

    [Fact]
    public async Task The_card_carries_the_execution_environment_identity()
    {
        _rig.Register(_run);
        var env = Card(await ShowAsync()).GetProperty("environment");
        Assert.Equal("dev-1", env.GetProperty("deviceId").GetString());
        Assert.Equal("local", env.GetProperty("type").GetString());
        Assert.False(string.IsNullOrEmpty(env.GetProperty("environment").GetString()));
    }

    [Fact]
    public async Task The_model_is_told_not_to_repeat_what_was_rendered()
    {
        _rig.Register(_run);
        Assert.Matches("(?i)do not duplicate", Rig.Text(await ShowAsync()));
    }

    [Theory]
    [InlineData("Q4 Revenue Chart")]
    [InlineData("UPPER")]
    [InlineData("has-dash")]
    [InlineData("")]
    public async Task The_title_must_be_a_self_explaining_snake_case_identifier(string title)
    {
        _rig.Register(_run);
        var r = await ShowAsync(title: title);
        Assert.True(r.IsError);
    }

    [Fact]
    public async Task The_title_error_names_snake_case()
    {
        _rig.Register(_run);
        Assert.Contains("snake_case", Rig.Text(await ShowAsync(title: "Q4 Revenue Chart")));
    }

    [Fact]
    public async Task Loading_messages_must_have_one_to_four_entries()
    {
        _rig.Register(_run);
        Assert.True((await _rig.Run("visualize_show_widget", $$"""{"title":"t","widget_code":{{Json.Esc(Code)}},"loading_messages":[]}""", _rig.Ctx(_run))).IsError);
        Assert.True((await _rig.Run("visualize_show_widget", $$"""{"title":"t","widget_code":{{Json.Esc(Code)}},"loading_messages":["a","b","c","d","e"]}""", _rig.Ctx(_run))).IsError);
    }

    [Fact]
    public async Task Empty_widget_code_is_refused()
    {
        _rig.Register(_run);
        Assert.True((await ShowAsync(code: "")).IsError);
    }

    [Fact]
    public async Task Widget_code_over_128K_is_refused_with_a_message_about_the_length()
    {
        _rig.Register(_run);
        var r = await ShowAsync(code: new string('x', (128 * 1024) + 1));
        Assert.True(r.IsError);
        Assert.Contains("widget_code", Rig.Text(r));
    }

    [Fact]
    public void The_traits_declare_network_because_the_output_can_load_cdn_scripts()
    {
        var show = _rig.Tool("visualize_show_widget").Info;
        Assert.True(show.Traits.NeedsNetwork);
        Assert.True(show.Traits.ReadOnly);
        Assert.False(show.Traits.Destructive);
        Assert.False(show.Traits.ConcurrencySafe);
        Assert.False(_rig.Tool("visualize_read_me").Info.Traits.NeedsNetwork);
        Assert.All(_rig.Tools, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Info.PromptSnippet));
            Assert.NotNull(t.Info.PromptGuidelines);
        });
    }

    [Fact]
    public async Task Tsx_is_refused_with_a_pointer_to_html_and_svg()
    {
        _rig.Register(_run);
        const string tsx = "import { Stack } from 'nextcowork/widget'\nexport default function W() { return null }";
        var byFormat = await ShowAsync(",\"format\":\"tsx\"", Code);
        var byInference = await ShowAsync(code: tsx);
        Assert.True(byFormat.IsError);
        Assert.True(byInference.IsError);
        Assert.Contains("not supported", Rig.Text(byInference));
        Assert.Contains("html", Rig.Text(byInference));
    }

    [Fact]
    public async Task The_format_is_inferred_from_the_start_of_the_code()
    {
        _rig.Register(_run);
        Assert.Equal("svg", Card(await ShowAsync(code: "  <SVG viewBox='0 0 1 1'></SVG>")).GetProperty("format").GetString());
        Assert.Equal("html", Card(await ShowAsync(code: "<div>hi</div>")).GetProperty("format").GetString());
        Assert.Equal("html", Card(await ShowAsync(",\"format\":\"html\"", Code)).GetProperty("format").GetString());
    }

    // ───────────────────────── 消毒进了工具 ─────────────────────────

    [Fact]
    public async Task Unsafe_content_is_removed_the_card_says_so_and_the_model_is_told_how_to_fix_it()
    {
        _rig.Register(_run);
        var r = await ShowAsync(code: """<div onclick="go()"><img src="https://evil.example/p.gif"><script src="https://evil.example/x.js"></script><iframe src="x"></iframe>ok</div>""");
        Assert.False(r.IsError);
        var card = Card(r);
        var code = card.GetProperty("code").GetString()!;
        Assert.Equal("<div><img>ok</div>", code);
        var rules = card.GetProperty("findings").EnumerateArray().Select(f => f.GetProperty("rule").GetString()).ToList();
        Assert.Contains("attr.event", rules);
        Assert.Contains("script.external", rules);
        Assert.Contains("element.forbidden", rules);
        var text = Rig.Text(r);
        Assert.Contains("removed", text);
        Assert.Contains("addEventListener", text);
    }

    [Fact]
    public async Task A_payload_with_nothing_renderable_left_is_an_error()
    {
        _rig.Register(_run);
        var r = await ShowAsync(code: """<iframe src="https://evil.example"></iframe>""");
        Assert.True(r.IsError);
        Assert.Contains("Nothing renderable", Rig.Text(r));
    }

    [Fact]
    public async Task The_snapshot_for_phones_has_no_scripts_even_when_the_desktop_code_does()
    {
        _rig.Register(_run);
        var card = Card(await ShowAsync(code: "<div id=\"o\"></div><script>o.textContent='x'</script>"));
        Assert.Contains("<script>", card.GetProperty("code").GetString());
        Assert.DoesNotContain("<script", card.GetProperty("snapshot").GetString());
        Assert.Contains("<div id=\"o\"></div>", card.GetProperty("snapshot").GetString());
    }

    // ───────────────────────── visualize_read_me ─────────────────────────

    [Fact]
    public async Task Read_me_returns_the_guidelines_body()
    {
        var r = await _rig.Run("visualize_read_me", """{"modules":["interactive"]}""", _rig.Ctx(_run));
        Assert.False(r.IsError);
        Assert.Contains("# Imagine", Rig.Text(r));
    }

    [Fact]
    public async Task An_unknown_module_is_stopped_by_the_schema_and_tsx_components_do_not_exist()
    {
        Assert.True((await _rig.Run("visualize_read_me", """{"modules":["nope"]}""", _rig.Ctx(_run))).IsError);
        Assert.True((await _rig.Run("visualize_read_me", """{"modules":["components"]}""", _rig.Ctx(_run))).IsError);
    }

    [Fact]
    public async Task A_combination_that_would_cross_the_output_cap_fails_explicitly_and_suggests_a_single_module()
    {
        var r = await _rig.Run("visualize_read_me", """{"modules":["diagram","chart"]}""", _rig.Ctx(_run));
        Assert.True(r.IsError);
        Assert.Contains("single module", Rig.Text(r));
        Assert.Contains("diagram", Rig.Text(r));
    }

    // ───────────────────────── actions ─────────────────────────

    private const string RunTests = """{"id":"run_tests","label":"Run tests","tool":"Bash","input":{"command":"dotnet test --filter {{suite}}"},"params":{"suite":{"type":"enum","values":["a","b"]}}}""";

    [Fact]
    public async Task Valid_actions_travel_with_the_card()
    {
        _rig.Register(_run);
        var r = await ShowAsync($",\"actions\":[{RunTests}]");
        Assert.False(r.IsError, Rig.Text(r));
        var action = Card(r).GetProperty("actions")[0];
        Assert.Equal("run_tests", action.GetProperty("id").GetString());
        Assert.Equal("Bash", action.GetProperty("tool").GetString());
        Assert.Equal("enum", action.GetProperty("params").GetProperty("suite").GetProperty("type").GetString());
    }

    private static string Actions(params string[] actions) => ",\"actions\":[" + string.Join(',', actions) + "]";

    [Fact]
    public async Task Placeholders_and_params_must_match_one_to_one()
    {
        _rig.Register(_run);
        var missing = await ShowAsync(Actions("""{"id":"r","label":"R","tool":"Bash","input":{"command":"x {{suite}}"}}"""));
        Assert.True(missing.IsError);
        Assert.Contains("{{suite}}", Rig.Text(missing));
        var unused = await ShowAsync(Actions("""{"id":"r","label":"R","tool":"Bash","input":{"command":"x"},"params":{"suite":{"type":"enum","values":["a"]}}}"""));
        Assert.True(unused.IsError);
        Assert.Contains("never used", Rig.Text(unused));
    }

    [Theory]
    [InlineData("AskUserQuestion")]
    [InlineData("ExitPlanMode")]
    [InlineData("ProposeGoal")]
    [InlineData("Task")]
    [InlineData("visualize_show_widget")]
    public async Task Tools_that_suspend_or_spawn_agents_cannot_be_actions(string tool)
    {
        _rig.Register(_run);
        var r = await ShowAsync(Actions("""{"id":"x","label":"X","tool":"TOOL","input":{}}""".Replace("TOOL", tool, StringComparison.Ordinal)));
        Assert.True(r.IsError);
        Assert.Contains("cannot be used", Rig.Text(r));
    }

    [Fact]
    public async Task Action_ids_must_be_unique_and_snake_case()
    {
        _rig.Register(_run);
        var twice = await ShowAsync($",\"actions\":[{RunTests},{RunTests}]");
        Assert.Contains("Duplicate", Rig.Text(twice));
        var bad = await ShowAsync(",\"actions\":[" + RunTests.Replace("run_tests", "Run-Tests") + "]");
        Assert.True(bad.IsError);
    }

    [Fact]
    public async Task Actions_are_not_available_to_subagents()
    {
        _rig.Register(new RunId("child"), depth: 1);
        var r = await _rig.Run("visualize_show_widget", Show($",\"actions\":[{RunTests}]"), _rig.Ctx(new RunId("child"), depth: 1));
        Assert.True(r.IsError);
        Assert.Contains("subagents", Rig.Text(r));
    }

    // ───────────────────────── interactive ─────────────────────────

    [Fact]
    public async Task Interactive_pushes_the_live_card_first_then_waits_and_returns_the_submitted_value()
    {
        _rig.Register(_run);
        var task = ShowAsync(",\"interactive\":true", title: "pick_db");
        var pending = await Until(() => _rig.Gate.Snapshot().Pending.OfType<WidgetSubmit>().FirstOrDefault());
        Assert.Equal(("call-1", "pick_db"), (pending.CallId, pending.Title));
        // 实时卡片:工具还没返回,卡片已经推出去了
        var live = Assert.Single(_rig.Progress);
        Assert.Equal("widget", live.Details!.Value.GetProperty("kind").GetString());
        Assert.True(live.Details.Value.GetProperty("interactive").GetBoolean());

        Assert.IsType<RespondResult.Accepted>(_rig.Gate.Respond(pending.Id, pending.Version, new WidgetSubmitResponse("""{"db":"postgres"}"""), "ctrl-a"));
        var r = await task;
        Assert.False(r.IsError);
        Assert.Contains("""{"db":"postgres"}""", Rig.Text(r));
        Assert.Equal("""{"db":"postgres"}""", Card(r).GetProperty("submitted").GetString());
    }

    [Fact]
    public async Task Two_controllers_racing_to_submit_only_one_wins()
    {
        _rig.Register(_run);
        var task = ShowAsync(",\"interactive\":true", title: "pick_db");
        var pending = await Until(() => _rig.Gate.Snapshot().Pending.OfType<WidgetSubmit>().FirstOrDefault());
        var first = _rig.Gate.Respond(pending.Id, pending.Version, new WidgetSubmitResponse("\"first\""), "ctrl-a");
        var second = _rig.Gate.Respond(pending.Id, pending.Version, new WidgetSubmitResponse("\"second\""), "ctrl-b");
        Assert.IsType<RespondResult.Accepted>(first);
        Assert.IsNotType<RespondResult.Accepted>(second);
        Assert.Contains("\"first\"", Rig.Text(await task));
    }

    [Fact]
    public async Task Closing_the_widget_without_submitting_is_an_explicit_failure()
    {
        _rig.Register(_run);
        var task = ShowAsync(",\"interactive\":true", title: "pick_db");
        var pending = await Until(() => _rig.Gate.Snapshot().Pending.OfType<WidgetSubmit>().FirstOrDefault());
        _rig.Gate.Respond(pending.Id, pending.Version, new WidgetSubmitResponse(null), "ctrl");
        var r = await task;
        Assert.True(r.IsError);
        Assert.Matches("without submitting", Rig.Text(r));
    }

    [Fact]
    public async Task An_oversized_submission_is_rejected_by_the_gate_not_given_to_the_model()
    {
        _rig.Register(_run);
        var task = ShowAsync(",\"interactive\":true", title: "pick_db");
        var pending = await Until(() => _rig.Gate.Snapshot().Pending.OfType<WidgetSubmit>().FirstOrDefault());
        var huge = _rig.Gate.Respond(pending.Id, pending.Version, new WidgetSubmitResponse(new string('x', WidgetSubmit.MaxValueLength + 1)), "ctrl");
        Assert.IsType<RespondResult.Invalid>(huge);
        _rig.Gate.Respond(pending.Id, pending.Version, new WidgetSubmitResponse("1"), "ctrl");
        Assert.False((await task).IsError);
    }

    [Fact]
    public async Task Interactive_mode_is_not_available_to_subagents_or_headless_runs()
    {
        _rig.Register(new RunId("child"), depth: 1);
        var sub = await _rig.Run("visualize_show_widget", Show(",\"interactive\":true"), _rig.Ctx(new RunId("child"), depth: 1));
        Assert.True(sub.IsError);
        Assert.Empty(_rig.Gate.Snapshot().Pending);

        // 没有登记交互作用域(没有人在场)
        var headless = await _rig.Run("visualize_show_widget", Show(",\"interactive\":true"), _rig.Ctx(new RunId("nobody")));
        Assert.True(headless.IsError);
        Assert.Contains("headless", Rig.Text(headless));
    }

    [Fact]
    public async Task Aborting_the_run_cancels_the_pending_submission()
    {
        _rig.Register(_run);
        using var cts = new CancellationTokenSource();
        var tool = _rig.Tool("visualize_show_widget");
        var input = Ncw.Agent.Llm.Json.Jsn.Parse(Show(",\"interactive\":true"));
        var task = tool.ExecuteAsync(new ToolInvocation("call-1", "visualize_show_widget", tool, input), _rig.Ctx(_run), cts.Token).AsTask();
        await Until(() => _rig.Gate.Snapshot().Pending.OfType<WidgetSubmit>().FirstOrDefault());
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Empty(_rig.Gate.Snapshot().Pending);
    }

    // ───────────────────────── visualize_edit_widget ─────────────────────────

    private async Task CommitShow(string code, string title = "chart", string extra = "")
    {
        _rig.Register(_run);
        var r = await ShowAsync(extra, code, title, callId: $"c{_rig.History.Count}");
        Assert.False(r.IsError, Rig.Text(r));
        _rig.Commit($"c{_rig.History.Count}", r);
    }

    [Fact]
    public async Task An_edit_finds_the_previous_version_by_title_and_produces_a_new_card()
    {
        await CommitShow("""<div class="a">Q1</div><div class="a">Q2</div>""");
        var r = await _rig.Run("visualize_edit_widget", """{"title":"chart","edits":[{"old_string":"Q1","new_string":"Jan"}]}""", _rig.Ctx(_run, "call-2"));
        Assert.False(r.IsError, Rig.Text(r));
        var card = Card(r);
        Assert.Equal("""<div class="a">Jan</div><div class="a">Q2</div>""", card.GetProperty("code").GetString());
        Assert.Equal("chart", card.GetProperty("title").GetString());
        Assert.Single(_rig.History); // 上一版留在原处
    }

    [Fact]
    public async Task An_ambiguous_old_string_fails_instead_of_guessing_unless_replace_all_is_set()
    {
        await CommitShow("""<div class="a">Q1</div><div class="a">Q2</div>""");
        var ambiguous = await _rig.Run("visualize_edit_widget", """{"title":"chart","edits":[{"old_string":"class=\"a\"","new_string":"class=\"b\""}]}""", _rig.Ctx(_run, "call-2"));
        Assert.True(ambiguous.IsError);
        Assert.Contains("2 places", Rig.Text(ambiguous));
        var all = await _rig.Run("visualize_edit_widget", """{"title":"chart","edits":[{"old_string":"class=\"a\"","new_string":"class=\"b\"","replace_all":true}]}""", _rig.Ctx(_run, "call-2"));
        Assert.Equal("""<div class="b">Q1</div><div class="b">Q2</div>""", Card(all).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Edit_reports_a_missing_widget_and_a_missing_old_string()
    {
        await CommitShow("<div>x</div>");
        Assert.True((await _rig.Run("visualize_edit_widget", """{"title":"nope","edits":[{"old_string":"a","new_string":"b"}]}""", _rig.Ctx(_run, "call-2"))).IsError);
        var missing = await _rig.Run("visualize_edit_widget", """{"title":"chart","edits":[{"old_string":"zzz","new_string":"b"}]}""", _rig.Ctx(_run, "call-2"));
        Assert.Contains("not found", Rig.Text(missing));
    }

    [Fact]
    public async Task An_edit_that_introduces_unsafe_content_is_sanitized_like_a_fresh_show()
    {
        await CommitShow("<div>x</div>");
        var r = await _rig.Run("visualize_edit_widget", """{"title":"chart","edits":[{"old_string":"<div>","new_string":"<div onclick=\"steal()\">"}]}""", _rig.Ctx(_run, "call-2"));
        Assert.Equal("<div>x</div>", Card(r).GetProperty("code").GetString());
        Assert.Contains("attr.event", Rig.Text(r));
    }

    [Fact]
    public async Task An_edit_inherits_actions_but_not_interactivity()
    {
        // 交互模式的卡:从转录里读回(模拟用户已提交)
        _rig.Register(_run);
        var showTask = ShowAsync($",\"interactive\":true,\"actions\":[{RunTests}]", "<div>x</div>", "form");
        var pending = await Until(() => _rig.Gate.Snapshot().Pending.OfType<WidgetSubmit>().FirstOrDefault());
        _rig.Gate.Respond(pending.Id, pending.Version, new WidgetSubmitResponse("1"), "ctrl");
        _rig.Commit("call-1", await showTask);

        var r = await _rig.Run("visualize_edit_widget", """{"title":"form","new_title":"form_v2","edits":[{"old_string":"x","new_string":"y"}]}""", _rig.Ctx(_run, "call-2"));
        var card = Card(r);
        Assert.Equal("form_v2", card.GetProperty("title").GetString());
        Assert.Equal("run_tests", card.GetProperty("actions")[0].GetProperty("id").GetString());
        Assert.False(card.TryGetProperty("interactive", out _));
    }

    // ───────────────────────── visualize_update_widget ─────────────────────────

    [Fact]
    public async Task Update_data_goes_only_to_the_card()
    {
        await CommitShow("<div>x</div>");
        var r = await _rig.Run("visualize_update_widget", """{"title":"chart","data":{"progress":0.5}}""", _rig.Ctx(_run, "call-2"));
        Assert.False(r.IsError, Rig.Text(r));
        var card = Card(r);
        Assert.Equal("widgetData", card.GetProperty("kind").GetString());
        Assert.Equal(0.5, card.GetProperty("data").GetProperty("progress").GetDouble());
        Assert.DoesNotContain("0.5", Rig.Text(r));
        Assert.Equal("dev-1", card.GetProperty("environment").GetProperty("deviceId").GetString());
    }

    [Fact]
    public async Task Update_refuses_an_unknown_widget_and_oversized_data()
    {
        await CommitShow("<div>x</div>");
        Assert.True((await _rig.Run("visualize_update_widget", """{"title":"nope","data":1}""", _rig.Ctx(_run, "call-2"))).IsError);
        var big = await _rig.Run("visualize_update_widget", $$"""{"title":"chart","data":"{{new string('x', 65 * 1024)}}"}""", _rig.Ctx(_run, "call-2"));
        Assert.True(big.IsError);
        Assert.Contains("limit", Rig.Text(big));
    }

    // ───────────────────────── 注册表与名字 ─────────────────────────

    [Fact]
    public void The_four_tools_have_stable_ids_valid_external_names_and_schemas()
    {
        Assert.Equal(VisualizeTools.ToolIds, _rig.Tools.Select(t => t.Info.InternalId).ToList());
        Assert.All(_rig.Tools, t => Assert.True(Ncw.Agent.Llm.ToolInfo.IsValidName(t.Info.InternalId)));
        Assert.All(_rig.Tools, t => Assert.Equal(JsonValueKind.Object, t.Info.InputSchema.ValueKind));
    }

    private static async Task<T> Until<T>(Func<T?> get)
        where T : class
    {
        var deadline = Environment.TickCount64 + 10_000;
        while (Environment.TickCount64 < deadline)
        {
            if (get() is { } value)
            {
                return value;
            }

            await Task.Delay(5);
        }

        throw new TimeoutException();
    }
}
