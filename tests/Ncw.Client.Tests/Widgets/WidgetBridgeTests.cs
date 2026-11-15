using System.Text.Json;
using Ncw.Client.Actions;
using Ncw.Client.Devices;
using Ncw.Client.Services;
using Ncw.Client.Widgets;
using Ncw.Core.Domain;

namespace Ncw.Client.Tests.Widgets;

/// <summary>
/// 宿主桥的安全测试(AGENTS.md 任务 4):页面里跑的是模型写的代码,它发来的每一条消息都是不可信输入。
/// 恶意载荷:垃圾 / 超大 / 伪造类型的消息、不在白名单里的方法、越界的参数、洪水、伪造的 URL、劫持导航与弹窗。
/// </summary>
public class WidgetBridgeTests
{
    private static string Call(int id, string method, string argsJson = "{}") =>
        $"{{\"type\":\"{WidgetMessages.Call}\",\"id\":{id},\"method\":\"{method}\",\"args\":{argsJson}}}";

    // ───────────────────────── 解析与白名单 ─────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("\"a string\"")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("{\"type\":123}")]
    [InlineData("{\"type\":\"ncw:widget:content\",\"html\":\"<script>steal()</script>\"}")] // 宿主 → 页面的类型,页面不能反过来用
    [InlineData("{\"type\":\"ncw:widget:env\",\"env\":{}}")]
    [InlineData("{\"type\":\"ncw:widget:reply\",\"id\":1,\"ok\":true}")]
    [InlineData("{\"type\":\"ncw:widget:evil\"}")]
    [InlineData("{\"type\":\"NCW:WIDGET:READY\"}")] // 大小写不同也不认
    [InlineData("{\"method\":\"submit\"}")]
    public void Garbage_and_forged_message_types_are_rejected(string? body) =>
        Assert.IsType<WidgetRejected>(WidgetInboundParser.Parse(body));

    [Fact]
    public void Known_messages_parse()
    {
        Assert.IsType<WidgetReady>(WidgetInboundParser.Parse($"{{\"type\":\"{WidgetMessages.Ready}\"}}"));
        Assert.Equal(120.5, Assert.IsType<WidgetHeight>(WidgetInboundParser.Parse($"{{\"type\":\"{WidgetMessages.Height}\",\"height\":120.5}}")).Value);
        var set = Assert.IsType<SetStateCall>(WidgetInboundParser.Parse(Call(7, "setState", "{\"state\":{\"a\":[1,2]}}")));
        Assert.Equal((7, "{\"a\":[1,2]}"), (set.Id, set.StateJson));
        Assert.Null(Assert.IsType<SetStateCall>(WidgetInboundParser.Parse(Call(8, "setState", "{\"state\":null}"))).StateJson);
        Assert.Equal("{\"v\":1}", Assert.IsType<SubmitCall>(WidgetInboundParser.Parse(Call(9, "submit", "{\"value\":\"{\\\"v\\\":1}\"}"))).Value);
        var run = Assert.IsType<RunActionCall>(WidgetInboundParser.Parse(Call(10, "runAction", "{\"id\":\"say\",\"params\":{\"word\":\"hello\",\"n\":3,\"on\":true}}")));
        Assert.Equal("say", run.ActionId);
        using var p = JsonDocument.Parse(run.ParamsJson);
        Assert.Equal(("hello", 3, true), (p.RootElement.GetProperty("word").GetString(), p.RootElement.GetProperty("n").GetInt32(), p.RootElement.GetProperty("on").GetBoolean()));
        Assert.Equal("https://example.com/a", Assert.IsType<OpenUrlCall>(WidgetInboundParser.Parse(Call(11, "openUrl", "{\"url\":\"https://example.com/a\"}"))).Url.AbsoluteUri);
        Assert.Equal("{}", Assert.IsType<RunActionCall>(WidgetInboundParser.Parse(Call(12, "runAction", "{\"id\":\"x\"}"))).ParamsJson);
    }

    [Fact]
    public void An_oversized_message_is_rejected_before_it_is_parsed()
    {
        var huge = "{\"type\":\"" + WidgetMessages.Ready + "\",\"pad\":\"" + new string('x', WidgetLimits.MaxMessageChars) + "\"}";
        Assert.Equal("too-large", Assert.IsType<WidgetRejected>(WidgetInboundParser.Parse(huge)).Reason);
    }

    [Theory]
    [InlineData("fetch")]
    [InlineData("eval")]
    [InlineData("readFile")]
    [InlineData("exec")]
    [InlineData("download")]
    [InlineData("openFile")]
    [InlineData("sendPrompt")]
    [InlineData("complete")]
    [InlineData("SUBMIT")]
    [InlineData("__proto__")]
    [InlineData("")]
    public void Methods_outside_the_whitelist_are_rejected_and_the_call_id_is_kept_so_the_widget_is_not_left_hanging(string method)
    {
        var rejected = Assert.IsType<WidgetRejected>(WidgetInboundParser.Parse(Call(5, method)));
        Assert.Equal(("unknown-method", 5), (rejected.Reason, rejected.Id));
    }

    [Theory]
    [InlineData("{\"type\":\"ncw:widget:call\",\"method\":\"submit\",\"args\":{\"value\":\"x\"}}")] // 没有 id
    [InlineData("{\"type\":\"ncw:widget:call\",\"id\":0,\"method\":\"submit\",\"args\":{\"value\":\"x\"}}")]
    [InlineData("{\"type\":\"ncw:widget:call\",\"id\":-3,\"method\":\"submit\",\"args\":{\"value\":\"x\"}}")]
    [InlineData("{\"type\":\"ncw:widget:call\",\"id\":1.5,\"method\":\"submit\",\"args\":{\"value\":\"x\"}}")]
    [InlineData("{\"type\":\"ncw:widget:call\",\"id\":\"1\",\"method\":\"submit\",\"args\":{\"value\":\"x\"}}")]
    [InlineData("{\"type\":\"ncw:widget:call\",\"id\":99999999999,\"method\":\"submit\",\"args\":{\"value\":\"x\"}}")]
    public void A_call_needs_a_positive_integer_id(string body) =>
        Assert.Equal("bad-id", Assert.IsType<WidgetRejected>(WidgetInboundParser.Parse(body)).Reason);

    [Theory]
    [InlineData("submit", "{}")]
    [InlineData("submit", "{\"value\":42}")]
    [InlineData("submit", "{\"value\":null}")]
    [InlineData("runAction", "{}")]
    [InlineData("runAction", "{\"id\":\"\"}")]
    [InlineData("runAction", "{\"id\":5}")]
    [InlineData("runAction", "{\"id\":\"a\",\"params\":[1]}")]
    [InlineData("runAction", "{\"id\":\"a\",\"params\":{\"o\":{\"nested\":1}}}")] // 参数不能是对象 / 数组 / null
    [InlineData("runAction", "{\"id\":\"a\",\"params\":{\"o\":[1]}}")]
    [InlineData("runAction", "{\"id\":\"a\",\"params\":{\"o\":null}}")]
    [InlineData("openUrl", "{}")]
    [InlineData("openUrl", "{\"url\":5}")]
    public void Arguments_are_narrowed_field_by_field(string method, string args) =>
        Assert.Equal("bad-args", Assert.IsType<WidgetRejected>(WidgetInboundParser.Parse(Call(3, method, args))).Reason);

    [Fact]
    public void Calls_with_non_object_args_are_rejected()
    {
        Assert.Equal("bad-args", Assert.IsType<WidgetRejected>(WidgetInboundParser.Parse($"{{\"type\":\"{WidgetMessages.Call}\",\"id\":1,\"method\":\"submit\",\"args\":\"oops\"}}")).Reason);
        Assert.Equal("bad-args", Assert.IsType<WidgetRejected>(WidgetInboundParser.Parse($"{{\"type\":\"{WidgetMessages.Call}\",\"id\":1,\"method\":\"submit\",\"args\":[1]}}")).Reason);
    }

    [Fact]
    public void Per_channel_limits_reject_instead_of_truncating()
    {
        Assert.IsType<SubmitCall>(WidgetInboundParser.Parse(Call(1, "submit", $"{{\"value\":\"{new string('a', WidgetLimits.Submit)}\"}}")));
        Assert.Equal("bad-args", Assert.IsType<WidgetRejected>(WidgetInboundParser.Parse(Call(1, "submit", $"{{\"value\":\"{new string('a', WidgetLimits.Submit + 1)}\"}}"))).Reason);

        var big = "\"" + new string('s', WidgetLimits.State) + "\"";
        Assert.Equal("state-too-large", Assert.IsType<WidgetRejected>(WidgetInboundParser.Parse(Call(2, "setState", $"{{\"state\":{big}}}"))).Reason);

        Assert.Equal("bad-args", Assert.IsType<WidgetRejected>(WidgetInboundParser.Parse(Call(3, "runAction", $"{{\"id\":\"{new string('i', WidgetLimits.ActionId + 1)}\"}}"))).Reason);
        Assert.Equal("bad-args", Assert.IsType<WidgetRejected>(WidgetInboundParser.Parse(Call(4, "runAction", $"{{\"id\":\"a\",\"params\":{{\"p\":\"{new string('v', WidgetLimits.ParamString + 1)}\"}}}}"))).Reason);
        var many = string.Join(",", Enumerable.Range(0, WidgetLimits.ActionParamCount + 1).Select(i => $"\"p{i}\":1"));
        Assert.Equal("bad-args", Assert.IsType<WidgetRejected>(WidgetInboundParser.Parse(Call(5, "runAction", $"{{\"id\":\"a\",\"params\":{{{many}}}}}"))).Reason);
    }

    [Fact]
    public void Deeply_nested_json_is_rejected_not_parsed()
    {
        var deep = new string('[', 200) + new string(']', 200);
        Assert.Equal("not-json", Assert.IsType<WidgetRejected>(WidgetInboundParser.Parse(Call(1, "setState", $"{{\"state\":{deep}}}"))).Reason);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1e8)]
    public void Absurd_heights_are_rejected(double height) =>
        Assert.Equal("bad-height", Assert.IsType<WidgetRejected>(WidgetInboundParser.Parse($"{{\"type\":\"{WidgetMessages.Height}\",\"height\":{height.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}")).Reason);

    [Theory]
    [InlineData("{\"type\":\"ncw:widget:height\",\"height\":\"100\"}")]
    [InlineData("{\"type\":\"ncw:widget:height\"}")]
    [InlineData("{\"type\":\"ncw:widget:height\",\"height\":null}")]
    public void A_height_must_be_a_number(string body) =>
        Assert.Equal("bad-height", Assert.IsType<WidgetRejected>(WidgetInboundParser.Parse(body)).Reason);

    [Fact]
    public void Heights_are_clamped_by_the_host_so_a_widget_cannot_blow_up_the_transcript()
    {
        Assert.Equal(WidgetLimits.MaxHeight, WidgetLimits.ClampHeight(100_000));
        Assert.Equal(WidgetLimits.MinHeight, WidgetLimits.ClampHeight(0));
        Assert.Equal(WidgetLimits.MinHeight, WidgetLimits.ClampHeight(double.NaN));
        Assert.Equal(300, WidgetLimits.ClampHeight(299.6));
    }

    [Theory]
    [InlineData("https://example.com/path?q=1", true)]
    [InlineData("http://example.com/", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("JaVaScRiPt:alert(1)", false)]
    [InlineData("data:text/html,<script>alert(1)</script>", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("vbscript:msgbox(1)", false)]
    [InlineData("blob:https://example.com/uuid", false)]
    [InlineData("ftp://example.com/x", false)]
    [InlineData("mailto:a@b.c", false)]
    [InlineData("https://user:pass@example.com/", false)] // 带用户信息:典型的钓鱼写法
    [InlineData("https://", false)]
    [InlineData("//example.com/", false)]
    [InlineData("/relative/path", false)]
    [InlineData("https://example.com/\u0000evil", false)]
    [InlineData("https://example.com/\nHost: evil", false)]
    public void Only_plain_http_and_https_links_can_ever_reach_the_system_browser(string url, bool allowed) =>
        Assert.Equal(allowed, WidgetInboundParser.TryExternalUrl(url, out _));

    [Fact]
    public void An_overlong_url_is_not_accepted() =>
        Assert.False(WidgetInboundParser.TryExternalUrl("https://example.com/" + new string('a', WidgetLimits.Url), out _));

    // ───────────────────────── 桥:限速、锁死、回复 ─────────────────────────

    private sealed class ManualClock : TimeProvider
    {
        private long _ms;

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => _ms;

        public void Advance(TimeSpan by) => _ms += (long)by.TotalMilliseconds;
    }

    private sealed class Confirmations : IWidgetConfirmations
    {
        public bool Answer { get; set; } = true;
        public List<string> Urls { get; } = [];
        public List<WidgetActionPrompt> Actions { get; } = [];
        public TaskCompletionSource? Gate { get; set; }

        public async Task<bool> ConfirmOpenUrlAsync(string url)
        {
            Urls.Add(url);
            if (Gate is { } gate)
            {
                await gate.Task;
            }

            return Answer;
        }

        public Task<bool> ConfirmActionAsync(WidgetActionPrompt prompt)
        {
            Actions.Add(prompt);
            return Task.FromResult(Answer);
        }
    }

    private sealed class Launcher : IUrlLauncher
    {
        public List<string> Opened { get; } = [];

        public Task OpenAsync(string url)
        {
            Opened.Add(url);
            return Task.CompletedTask;
        }
    }

    private sealed class Rig
    {
        public ManualClock Clock { get; } = new();
        public Confirmations Confirm { get; } = new();
        public Launcher Launch { get; } = new();
        public List<string> Posts { get; } = [];
        public WidgetFacts Facts { get; set; } = new(WidgetCall.Done, true, false, false, false, false);
        public WidgetBridge Bridge { get; }
        public List<double> Heights { get; } = [];
        public int Readies { get; private set; }

        public Rig()
        {
            // 没有连任何设备:凡是要走设备的调用都得到「没连上」
            Bridge = new WidgetBridge(new WidgetRef("local", "s1", "c1"), new WidgetActions(new DeviceRegistry()), Confirm, () => Launch,
                () => new WidgetBridgeContext(Facts, []), Posts.Add, Clock);
            Bridge.HeightChanged += Heights.Add;
            Bridge.Ready += () => Readies++;
        }

        public JsonElement Reply(int index = -1)
        {
            using var doc = JsonDocument.Parse(Posts[index < 0 ? Posts.Count + index : index]);
            return doc.RootElement.Clone();
        }
    }

    [Fact]
    public async Task Ready_and_height_messages_reach_the_host_with_the_height_clamped()
    {
        var rig = new Rig();
        await rig.Bridge.HandleAsync($"{{\"type\":\"{WidgetMessages.Ready}\"}}");
        await rig.Bridge.HandleAsync($"{{\"type\":\"{WidgetMessages.Height}\",\"height\":250.2}}");
        await rig.Bridge.HandleAsync($"{{\"type\":\"{WidgetMessages.Height}\",\"height\":9999999}}");
        Assert.Equal(1, rig.Readies);
        Assert.Equal([250, WidgetLimits.MaxHeight], rig.Heights);
        Assert.Empty(rig.Posts);
    }

    [Fact]
    public async Task A_rejected_call_gets_a_failing_reply_with_a_stable_code_not_silence()
    {
        var rig = new Rig();
        await rig.Bridge.HandleAsync(Call(4, "fetch", "{\"url\":\"https://evil.example\"}"));
        var reply = rig.Reply();
        Assert.Equal(WidgetMessages.Reply, reply.GetProperty("type").GetString());
        Assert.Equal((4, false, "unknown-method"), (reply.GetProperty("id").GetInt32(), reply.GetProperty("ok").GetBoolean(), reply.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task Garbage_gets_no_reply_at_all_and_counts_as_a_strike()
    {
        var rig = new Rig();
        await rig.Bridge.HandleAsync("garbage");
        await rig.Bridge.HandleAsync("{\"type\":\"ncw:widget:nope\"}");
        Assert.Empty(rig.Posts);
        Assert.Equal(2, rig.Bridge.Strikes);
    }

    [Fact]
    public async Task A_flood_is_rate_limited_and_a_persistent_attacker_locks_the_bridge_for_good()
    {
        var rig = new Rig();
        var locked = 0;
        rig.Bridge.Locked += () => locked++;

        // 同一时刻灌进去上千条:令牌桶只放行一小撮,其余是违规
        for (var i = 0; i < 2000 && !rig.Bridge.IsLocked; i++)
        {
            await rig.Bridge.HandleAsync($"{{\"type\":\"{WidgetMessages.Height}\",\"height\":10}}");
        }

        Assert.True(rig.Bridge.IsLocked);
        Assert.Equal(1, locked);
        Assert.InRange(rig.Heights.Count, 100, 130); // 突发容量 120,之外的全被丢

        // 锁死之后:连正常消息都丢弃,时间走多久也不恢复(这个页面要么坏了要么在攻击)
        rig.Clock.Advance(TimeSpan.FromMinutes(5));
        var before = rig.Heights.Count;
        await rig.Bridge.HandleAsync($"{{\"type\":\"{WidgetMessages.Height}\",\"height\":10}}");
        await rig.Bridge.HandleAsync(Call(1, "setState", "{\"state\":1}"));
        Assert.Equal(before, rig.Heights.Count);
        Assert.Empty(rig.Posts);
        Assert.Equal(1, locked);
    }

    [Fact]
    public async Task Rate_limits_recover_with_time_when_the_widget_behaves()
    {
        var rig = new Rig();
        for (var i = 0; i < 120; i++)
        {
            await rig.Bridge.HandleAsync($"{{\"type\":\"{WidgetMessages.Height}\",\"height\":10}}");
        }

        var burst = rig.Heights.Count;
        await rig.Bridge.HandleAsync($"{{\"type\":\"{WidgetMessages.Height}\",\"height\":10}}"); // 桶空了:丢一条
        Assert.Equal(burst, rig.Heights.Count);
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        await rig.Bridge.HandleAsync($"{{\"type\":\"{WidgetMessages.Height}\",\"height\":10}}");
        Assert.Equal(burst + 1, rig.Heights.Count);
        Assert.False(rig.Bridge.IsLocked);
    }

    [Fact]
    public async Task Call_flood_gets_rate_limited_replies_on_the_call_channel()
    {
        var rig = new Rig();
        for (var i = 1; i <= 40; i++)
        {
            await rig.Bridge.HandleAsync(Call(i, "setState", "{\"state\":1}"));
        }

        Assert.Contains(rig.Posts, p => p.Contains("rate_limited", StringComparison.Ordinal));
        Assert.True(rig.Bridge.Strikes > 0);
    }

    [Fact]
    public async Task Calls_do_not_reach_the_device_while_it_is_offline_or_the_card_is_not_open_for_them()
    {
        var rig = new Rig { Facts = new WidgetFacts(WidgetCall.Done, true, false, false, false, DeviceUnknown: true) };
        await rig.Bridge.HandleAsync(Call(1, "setState", "{\"state\":1}"));
        await rig.Bridge.HandleAsync(Call(2, "runAction", "{\"id\":\"a\"}"));
        Assert.Equal("offline", rig.Reply(0).GetProperty("error").GetString());
        Assert.Equal("offline", rig.Reply(1).GetProperty("error").GetString());

        // 不是交互卡片:不能提交
        rig.Facts = new WidgetFacts(WidgetCall.Done, true, Interactive: false, false, false, false);
        await rig.Bridge.HandleAsync(Call(3, "submit", "{\"value\":\"x\"}"));
        Assert.Equal("not_interactive", rig.Reply().GetProperty("error").GetString());

        // 已经提交过
        rig.Facts = new WidgetFacts(WidgetCall.Done, true, Interactive: true, Submitted: true, false, false);
        await rig.Bridge.HandleAsync(Call(4, "submit", "{\"value\":\"x\"}"));
        Assert.Equal("already_submitted", rig.Reply().GetProperty("error").GetString());

        // 提交窗口关了(过期)
        rig.Facts = new WidgetFacts(WidgetCall.Done, true, Interactive: true, false, AwaitingSubmit: false, false);
        await rig.Bridge.HandleAsync(Call(5, "submit", "{\"value\":\"x\"}"));
        Assert.Equal("closed", rig.Reply().GetProperty("error").GetString());

        // 失败的调用没有卡片可言
        rig.Facts = new WidgetFacts(WidgetCall.Failed, false, false, false, false, false);
        await rig.Bridge.HandleAsync(Call(6, "setState", "{\"state\":1}"));
        Assert.Equal("unavailable", rig.Reply().GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_submit_that_passes_the_local_checks_still_fails_cleanly_when_no_device_is_connected()
    {
        var rig = new Rig { Facts = new WidgetFacts(WidgetCall.Running, true, true, false, true, false) };
        await rig.Bridge.HandleAsync(Call(1, "submit", "{\"value\":\"\\\"pg\\\"\"}"));
        Assert.Equal((false, "offline"), (rig.Reply().GetProperty("ok").GetBoolean(), rig.Reply().GetProperty("error").GetString()));
    }

    // ───────────────────────── 外链:确认 + 只有 http(s) + 同一时刻一个确认框 ─────────────────────────

    [Fact]
    public async Task Opening_a_link_asks_first_with_the_real_address_and_only_then_launches_the_browser()
    {
        var rig = new Rig();
        await rig.Bridge.HandleAsync(Call(1, "openUrl", "{\"url\":\"https://docs.example.com/page\"}"));
        Assert.Equal(["https://docs.example.com/page"], rig.Confirm.Urls);
        Assert.Equal(["https://docs.example.com/page"], rig.Launch.Opened);
        Assert.True(rig.Reply().GetProperty("ok").GetBoolean());

        rig.Confirm.Answer = false;
        await rig.Bridge.HandleAsync(Call(2, "openUrl", "{\"url\":\"https://docs.example.com/other\"}"));
        Assert.Single(rig.Launch.Opened);
        Assert.Equal("declined", rig.Reply().GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("javascript:alert(document.cookie)")]
    [InlineData("file:///Users/token/.ssh/id_ed25519")]
    [InlineData("data:text/html,<h1>x</h1>")]
    [InlineData("https://user:pw@evil.example/")]
    public async Task Dangerous_urls_never_even_show_a_dialog_let_alone_launch(string url)
    {
        var rig = new Rig();
        await rig.Bridge.HandleAsync(Call(1, "openUrl", $"{{\"url\":\"{url}\"}}"));
        Assert.Empty(rig.Confirm.Urls);
        Assert.Empty(rig.Launch.Opened);
        Assert.False(rig.Reply().GetProperty("ok").GetBoolean());

        Assert.False(await rig.Bridge.OpenExternalAsync(new Uri(url, UriKind.RelativeOrAbsolute)));
        Assert.Empty(rig.Launch.Opened);
    }

    [Fact]
    public async Task A_second_confirmation_while_one_is_open_is_refused_not_queued()
    {
        var rig = new Rig();
        rig.Confirm.Gate = new TaskCompletionSource();
        var first = rig.Bridge.OpenExternalAsync(new Uri("https://a.example/"));
        var second = await rig.Bridge.OpenExternalAsync(new Uri("https://b.example/"));
        Assert.False(second);
        Assert.Equal(["https://a.example/"], rig.Confirm.Urls);

        rig.Confirm.Gate.SetResult();
        Assert.True(await first);
        Assert.Equal(["https://a.example/"], rig.Launch.Opened);
    }

    // ───────────────────────── 导航与弹窗 ─────────────────────────

    [Fact]
    public void Only_the_shell_load_and_in_page_anchors_may_navigate()
    {
        var policy = new WidgetNavigationPolicy();
        policy.ExpectShellLoad();
        Assert.Equal(WidgetNavigation.Allow, policy.Decide(new Uri("http://localhost:12345/")));
        Assert.Equal(WidgetNavigation.Allow, policy.Decide(new Uri("about:blank"))); // 就绪之前:还是外壳自己的加载

        policy.MarkReady();
        // 从这一刻起 widget 的代码跑起来了:任何导航都不许
        Assert.Equal(WidgetNavigation.Allow, policy.Decide(new Uri("http://localhost:12345/#section")));
        Assert.Equal(WidgetNavigation.Cancel, policy.Decide(new Uri("http://localhost:12345/other")));
        Assert.Equal(WidgetNavigation.OpenExternal, policy.Decide(new Uri("https://example.com/")));
        Assert.Equal(WidgetNavigation.Cancel, policy.Decide(new Uri("javascript:alert(1)")));
        Assert.Equal(WidgetNavigation.Cancel, policy.Decide(new Uri("file:///etc/passwd")));
        Assert.Equal(WidgetNavigation.Cancel, policy.Decide(new Uri("data:text/html,x")));
        Assert.Equal(WidgetNavigation.Cancel, policy.Decide(new Uri("mailto:a@b.c")));
        Assert.Equal(WidgetNavigation.Cancel, policy.Decide(null));
    }

    [Fact]
    public void A_widget_cannot_use_a_reload_to_slip_a_navigation_through()
    {
        var policy = new WidgetNavigationPolicy();
        policy.ExpectShellLoad();
        policy.Decide(new Uri("http://localhost:12345/"));
        policy.MarkReady();
        Assert.Equal(WidgetNavigation.Cancel, policy.Decide(new Uri("http://localhost:12345/steal")));
        // 重新加载是宿主主动发起的(ExpectShellLoad),widget 自己没有这个入口
        policy.ExpectShellLoad();
        Assert.Equal(WidgetNavigation.Allow, policy.Decide(new Uri("http://localhost:12345/")));
    }

    [Fact]
    public void New_windows_never_open_and_only_web_links_are_offered_to_the_browser()
    {
        var policy = new WidgetNavigationPolicy();
        Assert.Equal(WidgetNavigation.OpenExternal, policy.DecideNewWindow(new Uri("https://example.com/")));
        Assert.Equal(WidgetNavigation.Cancel, policy.DecideNewWindow(new Uri("javascript:window.open(1)")));
        Assert.Equal(WidgetNavigation.Cancel, policy.DecideNewWindow(new Uri("file:///x")));
        Assert.Equal(WidgetNavigation.Cancel, policy.DecideNewWindow(null));
    }
}
