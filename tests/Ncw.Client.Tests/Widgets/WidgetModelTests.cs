using System.Collections.Immutable;
using Ncw.Client.Mock;
using Ncw.Client.Stores;
using Ncw.Client.Transcript;
using Ncw.Client.Widgets;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Tests.Widgets;

/// <summary>widget 卡片的读取、静态快照文字、状态镜像的单调规则、转录里的卡片派生(阶段、提交窗口、断线 unknown、数据认领)。</summary>
public class WidgetModelTests
{
    private const string Device = "local";
    private static readonly ProjectRef Project = new(Device, "nextcowork");
    private static readonly SessionRef Session = new(Device, "s1");
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);

    private static string Q(string text) => "\"" + System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping.Encode(text) + "\"";

    private static string CardJson(string title = "sales", string code = "<div>hi</div>", string snapshot = "<div>hi</div>", bool interactive = false, string? submitted = null, string? extra = null) =>
        "{\"kind\":\"widget\",\"environment\":{\"deviceId\":\"local\",\"environment\":\"local\",\"name\":\"Local\",\"type\":\"local\"}," +
        $"\"title\":\"{title}\",\"format\":\"html\",\"code\":{Q(code)},\"snapshot\":{Q(snapshot)}" +
        (interactive ? ",\"interactive\":true" : "") + (submitted is null ? "" : $",\"submitted\":{Q(submitted)}") + (extra ?? "") + "}";

    // ───────────────────────── 卡片读取 ─────────────────────────

    [Fact]
    public void A_card_is_read_once_per_details_string_and_equal_cards_are_the_same_instance()
    {
        var json = CardJson(extra: ",\"size\":{\"width\":600,\"height\":300},\"findings\":[{\"rule\":\"attr.event\",\"detail\":\"onclick\",\"count\":2}]," +
            "\"actions\":[{\"id\":\"say\",\"label\":\"Say\",\"tool\":\"Bash\",\"input\":{\"command\":\"echo {{w}}\"},\"params\":{\"w\":{\"type\":\"enum\",\"values\":[\"a\",\"b\"]},\"n\":{\"type\":\"number\",\"min\":1,\"max\":5,\"integer\":true}}}]");
        var card = WidgetCardReader.Read(json)!;
        Assert.Same(card, WidgetCardReader.Read(json));
        Assert.Equal(("sales", "html", "<div>hi</div>"), (card.Title, card.Format, card.Code));
        Assert.Equal(0.5, card.Size.Aspect);
        Assert.Equal(("local", "local", "Local"), (card.Environment!.DeviceId, card.Environment.Environment, card.Environment.Name));
        var finding = Assert.Single(card.Findings);
        Assert.Equal(("attr.event", 2), (finding.Rule, finding.Count));
        var action = Assert.Single(card.Actions);
        Assert.Equal(("say", "Say"), (action.Id, action.Label));
        Assert.Equal(["a", "b"], action.Params.Single(p => p.Key == "w").Value.Values);
        var n = action.Params.Single(p => p.Key == "n").Value;
        Assert.Equal((1d, 5d, true), (n.Min, n.Max, n.Integer));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"kind\":\"other\"}")]
    [InlineData("{\"kind\":\"widgetData\",\"title\":\"t\",\"data\":1}")]
    public void Anything_that_is_not_a_widget_card_reads_as_null_without_throwing(string? json) =>
        Assert.Null(WidgetCardReader.Read(json));

    [Fact]
    public void A_sparse_card_still_reads_and_an_unknown_format_falls_back_to_html()
    {
        var card = WidgetCardReader.Read("{\"kind\":\"widget\",\"format\":\"tsx\"}")!;
        Assert.Equal(("", "html", "", ""), (card.Title, card.Format, card.Code, card.Snapshot));
        Assert.Empty(card.Actions);
        Assert.False(card.Interactive);
        Assert.Null(card.Environment);
        Assert.Equal("svg", WidgetCardReader.Read("{\"kind\":\"widget\",\"format\":\"svg\"}")!.Format);
    }

    [Fact]
    public void Data_cards_are_claimed_by_title()
    {
        var item = WidgetCardReader.ReadData("{\"kind\":\"widgetData\",\"title\":\"sales\",\"data\":{\"q\":[1,2]}}");
        Assert.Equal(("sales", "{\"q\":[1,2]}"), item);
        Assert.Null(WidgetCardReader.ReadData(CardJson()));
        Assert.Null(WidgetCardReader.ReadData("{\"kind\":\"widgetData\",\"data\":1}"));
    }

    // ───────────────────────── 静态快照文字(手机 / WebView 不可用的降级) ─────────────────────────

    [Fact]
    public void Snapshot_text_keeps_the_readable_words_and_drops_style_script_and_markup()
    {
        var text = WidgetSnapshotText.Extract(
            "<style>.a{color:red}</style><h1>Q1 &amp; Q2</h1><p>Revenue <b>up</b> 12%</p><ul><li>North</li><li>South &lt;2&gt;</li></ul>" +
            "<script>alert('x')</script><svg><defs><style>.t{fill:red}</style></defs><text x=\"1\">Label A</text><text>Label B</text></svg><!-- hidden -->");
        Assert.Equal("Q1 & Q2\nRevenue up 12%\nNorth\nSouth <2>\nLabel A\nLabel B", text);
    }

    [Fact]
    public void Snapshot_text_never_returns_markup_and_survives_garbage()
    {
        Assert.Equal("", WidgetSnapshotText.Extract(null));
        Assert.Equal("", WidgetSnapshotText.Extract(""));
        Assert.Equal("a", WidgetSnapshotText.Extract("a<div"));            // 没收尾的标签:丢掉
        Assert.Equal("x", WidgetSnapshotText.Extract("<div title=\"a>b\">x</div>")); // 引号里的 >
        Assert.Equal("&bogus; & x", WidgetSnapshotText.Extract("&bogus; &amp; x"));
        Assert.Equal("A", WidgetSnapshotText.Extract("&#65;"));
        Assert.Equal("", WidgetSnapshotText.Extract("<script>never closed"));
        var weird = WidgetSnapshotText.Extract("<img src=x onerror=alert(1)><a href=\"javascript:alert(1)\">go</a>");
        Assert.Equal("go", weird);
        Assert.DoesNotContain("<", WidgetSnapshotText.Extract("<<<>>><a<b>c"));
    }

    [Fact]
    public void Snapshot_text_is_capped()
    {
        var text = WidgetSnapshotText.Extract(string.Concat(Enumerable.Repeat("<p>lorem ipsum dolor</p>", 2000)));
        Assert.InRange(text.Length, 1, WidgetSnapshotText.MaxChars + 1);
        Assert.EndsWith("…", text);
    }

    // ───────────────────────── 镜像的单调规则 ─────────────────────────

    private static readonly WidgetRef Card = new(Device, "s1", "c1");

    [Fact]
    public void A_state_read_that_raced_with_a_local_write_is_discarded()
    {
        var m = WidgetsMirror.Empty;
        var rev = m.Of(Card).StateRev;                       // 发起读
        m = WidgetsReducer.StateWritten(m, Card, "{\"v\":2}"); // 期间用户拖了滑块
        m = WidgetsReducer.StateLoaded(m, Card, "{\"v\":1}", rev); // 旧的读回结果到了
        Assert.Equal("{\"v\":2}", m.Of(Card).StateJson);

        var fresh = WidgetsReducer.StateLoaded(m, Card, "{\"v\":3}", m.Of(Card).StateRev);
        Assert.Equal("{\"v\":3}", fresh.Of(Card).StateJson);
    }

    [Fact]
    public void A_failed_write_marks_the_card_stale_and_bumps_the_revision_so_a_stale_read_cannot_overwrite_it()
    {
        var m = WidgetsReducer.StateWritten(WidgetsMirror.Empty, Card, "{\"v\":2}");
        var rev = m.Of(Card).StateRev;
        m = WidgetsReducer.StateWriteFailed(m, Card);
        Assert.True(m.Of(Card).Stale);
        Assert.Equal("{\"v\":2}", WidgetsReducer.StateLoaded(m, Card, "{\"v\":1}", rev).Of(Card).StateJson);
        Assert.False(WidgetsReducer.StateLoaded(m, Card, "{\"v\":9}", m.Of(Card).StateRev).Of(Card).Stale);
    }

    [Fact]
    public void Submit_progress_only_goes_forward_and_terminal_states_are_final()
    {
        var m = WidgetsReducer.SubmitStarted(WidgetsMirror.Empty, Card);
        Assert.Equal(WidgetSubmit.Submitting, m.Of(Card).Submit);
        m = WidgetsReducer.SubmitSettled(m, Card, WidgetSubmit.Accepted);
        Assert.Equal(WidgetSubmit.Accepted, m.Of(Card).Submit);

        // 重放 / 迟到的结果、再来一次提交都改不动终态
        Assert.Same(m, WidgetsReducer.SubmitStarted(m, Card));
        Assert.Same(m, WidgetsReducer.SubmitSettled(m, Card, WidgetSubmit.Closed));
        Assert.Same(m, WidgetsReducer.SubmitSettled(m, Card, WidgetSubmit.None));

        // 没送达(设备不可用)退回可重试;已关闭是终态
        var retry = WidgetsReducer.SubmitSettled(WidgetsReducer.SubmitStarted(WidgetsMirror.Empty, Card), Card, WidgetSubmit.None);
        Assert.Equal(WidgetSubmit.None, retry.Of(Card).Submit);
        var closed = WidgetsReducer.SubmitSettled(WidgetsReducer.SubmitStarted(WidgetsMirror.Empty, Card), Card, WidgetSubmit.Closed);
        Assert.Same(closed, WidgetsReducer.SubmitSettled(closed, Card, WidgetSubmit.Accepted));
    }

    [Fact]
    public void A_dropped_device_marks_only_its_cards_stale_without_touching_terminal_submissions_or_other_devices()
    {
        var other = new WidgetRef("remote", "s9", "c9");
        var m = WidgetsReducer.SubmitSettled(WidgetsReducer.SubmitStarted(WidgetsReducer.StateWritten(WidgetsMirror.Empty, Card, "{}"), Card), Card, WidgetSubmit.Accepted);
        m = WidgetsReducer.StateWritten(m, other, "{}");
        var inFlight = new WidgetRef(Device, "s1", "c2");
        m = WidgetsReducer.SubmitStarted(m, inFlight);

        m = WidgetsReducer.MarkUnknown(m, Device);
        Assert.True(m.Of(Card).Stale);
        Assert.Equal(WidgetSubmit.Accepted, m.Of(Card).Submit);       // 终态不退
        Assert.Equal(WidgetSubmit.None, m.Of(inFlight).Submit);        // 在途的提交没有结果:退回可重试,不假装成功
        Assert.False(m.Of(other).Stale);
        Assert.Same(m, WidgetsReducer.MarkUnknown(m, Device));
    }

    [Fact]
    public void The_mirror_is_bounded_and_drops_the_least_recently_touched_card()
    {
        var m = WidgetsMirror.Empty;
        for (var i = 0; i < WidgetsMirror.MaxCards + 10; i++)
        {
            m = WidgetsReducer.StateWritten(m, new WidgetRef(Device, "s", $"c{i}"), "{}");
        }

        Assert.Equal(WidgetsMirror.MaxCards, m.Cards.Count);
        Assert.False(m.Cards.ContainsKey(new WidgetRef(Device, "s", "c0")));
        Assert.True(m.Cards.ContainsKey(new WidgetRef(Device, "s", $"c{WidgetsMirror.MaxCards + 9}")));
    }

    // ───────────────────────── 转录里的卡片 ─────────────────────────

    private static string Call(string callId, string name, string input) =>
        $"{{\"type\":\"tool_call\",\"callId\":\"{callId}\",\"name\":\"{name}\",\"input\":{input}}}";

    private static string ResultWith(string callId, string content, string? detailsJson, bool isError = false) =>
        $"{{\"type\":\"tool_result\",\"callId\":\"{callId}\",\"output\":{{\"content\":{Q(content)}{(detailsJson is null ? "" : ",\"details\":" + detailsJson)}}},\"isError\":{(isError ? "true" : "false")}}}";

    private static MessageDto Msg(string id, string role, bool @internal, params string[] parts) =>
        new(id, role, Now.ToUnixTimeMilliseconds(), @internal, "[" + string.Join(",", parts) + "]");

    private static ClientStores Stores(IEnumerable<MessageDto> messages, string? activeRun = null)
    {
        var stores = MockWorkspace.Create(Now);
        var meta = new SessionDto("s1", "nextcowork", null, "user", "t", null, "opus", null, "code", "auto", false, false, 1, 2);
        stores.Conversations.Update(c => ConversationReducer.ApplySession(c, Device,
            new SessionTopicState(new SessionPageDto(meta, [.. messages], false, [], [], []), new QueueDto("s1", 1, []), activeRun)));
        return stores;
    }

    private static ImmutableArray<TranscriptRow> Build(ClientStores stores, bool deviceUnknown = false) =>
        TranscriptBuilder.Build(stores.Conversations.State, Session, new EnvRefDto(Device, "local", "local", "local"), Now, deviceUnknown);

    private const string ShowInput = "{\"title\":\"sales\",\"widget_code\":\"<div>raw unsanitized</div>\",\"loading_messages\":[\"Drawing the chart\",\"Adding labels\"]}";

    [Fact]
    public void A_stored_widget_call_becomes_a_ready_card_with_the_sanitized_code_never_the_raw_tool_input()
    {
        var stores = Stores([
            Msg("a1", "assistant", false, Call("w1", "visualize_show_widget", ShowInput)),
            Msg("r1", "user", true, ResultWith("w1", "Rendered.", CardJson())),
        ]);

        var row = Assert.IsType<WidgetRow>(Assert.Single(Build(stores)));
        Assert.Equal(WidgetPhase.Ready, row.Phase);
        Assert.Equal("<div>hi</div>", row.Card!.Code);               // 消毒后的代码,不是工具入参里的 widget_code
        Assert.DoesNotContain("raw unsanitized", row.Card.Code);
        Assert.Equal("sales", row.Title);
        Assert.Equal("local", row.Env!.DeviceId);
        Assert.Same(row.Card, Assert.IsType<WidgetRow>(Assert.Single(Build(stores))).Card); // 再派生一遍:还是同一个对象
    }

    [Fact]
    public void A_running_call_without_a_card_is_loading_and_shows_the_models_loading_messages()
    {
        var stores = Stores([Msg("a1", "assistant", false, Call("w1", "visualize_show_widget", ShowInput))], activeRun: "run-1");
        var row = Assert.IsType<WidgetRow>(Assert.Single(Build(stores)));
        Assert.Equal(WidgetPhase.Loading, row.Phase);
        Assert.Equal("Drawing the chart\nAdding labels", row.LoadingText);
        Assert.Null(row.Card);
    }

    [Fact]
    public void A_device_that_drops_while_a_widget_is_being_drawn_makes_it_unknown_not_failed_not_spinning()
    {
        var stores = Stores([Msg("a1", "assistant", false, Call("w1", "visualize_show_widget", ShowInput))], activeRun: "run-1");
        Assert.Equal(WidgetPhase.Unknown, Assert.Single(Build(stores, deviceUnknown: true).OfType<WidgetRow>()).Phase);
    }

    [Fact]
    public void A_failed_call_is_failed_and_a_call_nobody_answered_after_the_run_is_gone_is_expired()
    {
        var failed = Stores([
            Msg("a1", "assistant", false, Call("w1", "visualize_show_widget", ShowInput)),
            Msg("r1", "user", true, ResultWith("w1", "The widget was rejected: …", null, isError: true)),
        ]);
        Assert.Equal(WidgetPhase.Failed, Assert.IsType<WidgetRow>(Assert.Single(Build(failed))).Phase);

        var orphan = Stores([Msg("a1", "assistant", false, Call("w1", "visualize_show_widget", ShowInput))]); // 没有回执,也没有在跑的 Run
        Assert.Equal(WidgetPhase.Expired, Assert.IsType<WidgetRow>(Assert.Single(Build(orphan))).Phase);
    }

    [Fact]
    public void An_interactive_card_pushed_live_is_ready_while_the_device_waits_for_the_submit_then_ends_up_submitted()
    {
        var stores = Stores([Msg("a1", "assistant", false, Call("w1", "visualize_show_widget", "{\"title\":\"pick_db\",\"widget_code\":\"x\",\"interactive\":true}"))], activeRun: "run-1");
        var run = new RunRef(Device, "run-1");
        var live = CardJson("pick_db", interactive: true);
        stores.Conversations.Update(c => ConversationReducer.ApplyRunEvent(c, Device, "run-1", 1, new RunStartEventDto("run-1", "s1", "opus")));
        stores.Conversations.Update(c => ConversationReducer.ApplyRunEvent(c, Device, "run-1", 2, new ToolStartEventDto("w1", "visualize_show_widget", "{}", new EnvRefDto(Device, "local", "", ""))));
        stores.Conversations.Update(c => ConversationReducer.ApplyRunEvent(c, Device, "run-1", 3, new ToolProgressEventDto("w1", DetailsJson: live)));
        stores.Conversations.Update(c => c with
        {
            Interactions = c.Interactions.SetItem(Device, new DeviceInteractions(1, [new WidgetSubmitInteractionDto { Id = "i1", RunId = "run-1", CreatedAt = 1, CallId = "w1", Title = "pick_db" }])),
        });

        var row = Assert.IsType<WidgetRow>(Assert.Single(Build(stores).OfType<WidgetRow>()));
        Assert.Equal(WidgetPhase.Ready, row.Phase);
        Assert.True(WidgetView.CanSubmit(row.Facts));
        Assert.True(row.Card!.Interactive);

        // 重放的旧进度不能把终态改回去:tool_end 带着提交值之后,进度事件再来也不盖掉它
        var submitted = CardJson("pick_db", interactive: true, submitted: "\"postgres\"");
        stores.Conversations.Update(c => ConversationReducer.ApplyRunEvent(c, Device, "run-1", 4, new ToolEndEventDto("w1", "ok", null, false, null, submitted, false, 5)));
        stores.Conversations.Update(c => ConversationReducer.ApplyRunEvent(c, Device, "run-1", 5, new ToolProgressEventDto("w1", DetailsJson: live)));
        stores.Conversations.Update(c => c with { Interactions = c.Interactions.SetItem(Device, DeviceInteractions.Empty) });
        var done = Assert.IsType<WidgetRow>(Assert.Single(Build(stores).OfType<WidgetRow>()));
        Assert.Equal("\"postgres\"", done.Card!.Submitted);
        Assert.Equal(WidgetPhase.Ready, done.Phase);
        Assert.False(WidgetView.CanSubmit(done.Facts));
        Assert.Equal(ToolCallStatus.Done, stores.Conversations.State.Runs[run].Tools["w1"].Status);
    }

    [Fact]
    public void An_interactive_card_whose_submit_window_closed_without_a_value_is_expired()
    {
        var stores = Stores([
            Msg("a1", "assistant", false, Call("w1", "visualize_show_widget", "{\"title\":\"pick_db\",\"widget_code\":\"x\",\"interactive\":true}")),
            Msg("r1", "user", true, ResultWith("w1", "closed", CardJson("pick_db", interactive: true))),
        ]);
        var row = Assert.IsType<WidgetRow>(Assert.Single(Build(stores)));
        Assert.Equal(WidgetPhase.Expired, row.Phase);
        Assert.False(WidgetView.CanSubmit(row.Facts));
    }

    [Fact]
    public void Update_data_is_claimed_by_title_with_the_latest_one_winning_and_other_tools_stay_plain_tool_rows()
    {
        var stores = Stores([
            Msg("a1", "assistant", false,
                Call("w1", "visualize_show_widget", ShowInput),
                Call("u1", "visualize_update_widget", "{\"title\":\"sales\",\"data\":{\"q\":1}}"),
                Call("u2", "visualize_update_widget", "{\"title\":\"sales\",\"data\":{\"q\":2}}"),
                Call("x1", "Bash", "{\"command\":\"ls\"}"),
                Call("m1", "visualize_read_me", "{\"modules\":[\"chart\"]}")),
            Msg("r1", "user", true,
                ResultWith("w1", "Rendered.", CardJson()),
                ResultWith("u1", "ok", "{\"kind\":\"widgetData\",\"title\":\"sales\",\"data\":{\"q\":1}}"),
                ResultWith("u2", "ok", "{\"kind\":\"widgetData\",\"title\":\"sales\",\"data\":{\"q\":2}}"),
                ResultWith("x1", "out", null),
                ResultWith("m1", "guidelines", null)),
        ]);
        var rows = Build(stores);
        Assert.Equal("{\"q\":2}", rows.OfType<WidgetRow>().Single().DataJson);
        Assert.Equal(4, rows.OfType<ToolRow>().Count()); // update ×2 + Bash + read_me 仍是普通工具卡
    }

    [Fact]
    public void Details_of_other_tools_never_enter_the_transcript_mirror()
    {
        var stores = Stores([Msg("a1", "assistant", false, Call("x1", "Edit", "{}"))], activeRun: "run-1");
        stores.Conversations.Update(c => ConversationReducer.ApplyRunEvent(c, Device, "run-1", 1, new RunStartEventDto("run-1", "s1", "opus")));
        stores.Conversations.Update(c => ConversationReducer.ApplyRunEvent(c, Device, "run-1", 2, new ToolStartEventDto("x1", "Edit", "{}", null)));
        stores.Conversations.Update(c => ConversationReducer.ApplyRunEvent(c, Device, "run-1", 3, new ToolEndEventDto("x1", "ok", null, false, null, "{\"kind\":\"diff\",\"huge\":\"" + new string('x', 5000) + "\"}", false, 1)));
        Assert.Null(stores.Conversations.State.Runs[new RunRef(Device, "run-1")].Tools["x1"].DetailsJson);
    }

    [Fact]
    public void An_unchanged_card_keeps_the_row_equal_so_the_view_does_not_reload_the_web_view()
    {
        var parts = new[]
        {
            Msg("a1", "assistant", false, Call("w1", "visualize_show_widget", ShowInput)),
            Msg("r1", "user", true, ResultWith("w1", "Rendered.", CardJson(code: new string('x', 100_000)))),
        };
        var first = Assert.IsType<WidgetRow>(Assert.Single(Build(Stores(parts))));
        var second = Assert.IsType<WidgetRow>(Assert.Single(Build(Stores(parts))));
        Assert.Equal(first, second);
    }
}
