using System.Text.Json;
using System.Text.Json.Nodes;
using Ncw.Agent.Host.Conversation;
using Ncw.Agent.Host.Tests.Support;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Llm;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Tests;

/// <summary>
/// 内联可视化(visualize_*,协议接线):真实 Agent + <see cref="DeviceClient"/> + 脚本化的假上游 + 真实工具 / 权限 / 交互闸门 / SQLite。
/// 模型调用 <c>visualize_show_widget</c> → 卡片 Details 带消毒后的 <c>code</c> 和手机端用的 <c>snapshot</c> → <c>widgets.getState / setState</c> 往返;
/// 交互模式的提交在两个控制端之间先到先得;<c>widgets.runAction</c> 经权限链代参执行(未声明 / 参数非法 → deny;非只读动作第一次要确认;确认后执行并记住)。
/// </summary>
public sealed class WidgetsTests : IAsyncLifetime
{
    private readonly ScriptedLlm _llm = new();
    private readonly ConversationRig _rig;

    public WidgetsTests()
    {
        _rig = new ConversationRig(_ => new AgentServices
        {
            Models = new FakeModels(_llm),
            Http = new NoNetwork(),
            CatalogDelayer = new NeverDelay(),
            Delayer = new NoDelay(),
            Loop = new AgentLoopOptions { ResumeDelays = [] },
        });
    }

    public ValueTask InitializeAsync() => _rig.InitializeAsync();

    public ValueTask DisposeAsync() => _rig.DisposeAsync();

    private async Task<(DeviceClient Client, ProjectDto Project, SessionDto Session)> SetupAsync(string clientId = "ctrl-a")
    {
        var client = await _rig.ConnectAsync(clientId);
        var project = await _rig.AddProjectAsync(client);
        var session = await client.CallAsync(ConversationMethods.SessionsCreate, new SessionsCreateParams(project.WorkspaceId, Model: "sonnet"));
        return (client, project, session);
    }

    private static Task<RunsStartResult> StartAsync(DeviceClient client, string sessionId, string text, string runId) =>
        client.CallAsync(
            ConversationMethods.RunsStart,
            new RunsStartParams(runId, sessionId, Guid.NewGuid().ToString("N"), [new InputPartDto(InputPartKinds.Text, text)], new SendOptionsDto("sonnet", PermissionMode: "auto")),
            idem: $"start:{runId}");

    private static string ShowInput(string title, string code, JsonArray? actions = null, bool interactive = false)
    {
        var o = new JsonObject
        {
            ["title"] = title,
            ["widget_code"] = code,
            ["loading_messages"] = new JsonArray("drawing"),
        };
        if (interactive)
        {
            o["interactive"] = true;
        }

        if (actions is not null)
        {
            o["actions"] = actions;
        }

        return o.ToJsonString();
    }

    private static JsonElement DetailsOf(RunWatch watch, string callId)
    {
        var end = watch.Of<ToolEndEventDto>().Single(t => t.CallId == callId);
        Assert.NotNull(end.DetailsJson);
        return JsonDocument.Parse(end.DetailsJson!).RootElement.Clone();
    }

    [Fact]
    public async Task A_model_drawn_widget_carries_a_sanitized_card_and_a_static_snapshot_and_its_state_round_trips()
    {
        var (client, _, session) = await SetupAsync();
        const string html = "<div id=\"o\" onclick=\"steal()\">hi</div><img src=\"https://evil.example/p.gif\"><script>document.getElementById('o').textContent='x'</script>";
        _llm.On(session.Id, S.Call("w1", "visualize_show_widget", ShowInput("sales_by_region", html)), S.Say("shown"));

        await StartAsync(client, session.Id, "draw it", "r1");
        var watch = await RunWatch.OpenAsync(client, "r1");
        await watch.SettledAsync();

        var tool = watch.Of<ToolEndEventDto>().Single(t => t.CallId == "w1");
        Assert.False(tool.IsError, tool.Content);
        // 给模型的内容不含代码,并说明拿掉了什么
        Assert.DoesNotContain("<div", tool.Content);
        Assert.Contains("removed", tool.Content);

        var card = DetailsOf(watch, "w1");
        Assert.Equal("widget", card.GetProperty("kind").GetString());
        Assert.Equal("html", card.GetProperty("format").GetString());
        Assert.Equal("local", card.GetProperty("environment").GetProperty("type").GetString());
        var code = card.GetProperty("code").GetString()!;
        Assert.DoesNotContain("onclick", code);
        Assert.DoesNotContain("evil.example", code);
        Assert.Contains("<script>", code);
        var snapshot = card.GetProperty("snapshot").GetString()!;
        Assert.DoesNotContain("<script", snapshot);
        Assert.Contains("<div id=\"o\">hi</div>", snapshot);
        Assert.Contains("attr.event", card.GetProperty("findings").EnumerateArray().Select(f => f.GetProperty("rule").GetString()));

        // 状态往返(widgets.getState / setState)
        Assert.Null((await client.CallAsync(WidgetsMethods.GetState, new WidgetRefParams(session.Id, "w1"))).State);
        await client.CallAsync(WidgetsMethods.SetState, new WidgetSetStateParams(session.Id, "w1", "{\"slider\":7}"), idem: "set-1");
        Assert.Equal("{\"slider\":7}", (await client.CallAsync(WidgetsMethods.GetState, new WidgetRefParams(session.Id, "w1"))).State);

        // tools.list 列出四个可视化工具,来源是内置(可被禁用);show 申报联网(它产出的东西会出网)
        var tools = (await client.CallAsync(ConversationMethods.ToolsList, new ToolsListParams())).Tools;
        foreach (var name in new[] { "visualize_read_me", "visualize_show_widget", "visualize_edit_widget", "visualize_update_widget" })
        {
            Assert.Contains(tools, t => t.Name == name);
        }

        Assert.True(tools.Single(t => t.Name == "visualize_show_widget").NeedsNetwork);
        Assert.True(tools.Single(t => t.Name == "visualize_show_widget").ReadOnly);
    }

    [Fact]
    public async Task Edit_in_a_later_turn_finds_the_card_by_title_in_the_stored_transcript()
    {
        var (client, _, session) = await SetupAsync();
        _llm.On(session.Id,
            S.Call("w1", "visualize_show_widget", ShowInput("chart", "<div>Q1</div>")),
            S.Call("w2", "visualize_edit_widget", new JsonObject { ["title"] = "chart", ["edits"] = new JsonArray(new JsonObject { ["old_string"] = "Q1", ["new_string"] = "Jan" }) }.ToJsonString()),
            S.Say("done"));

        await StartAsync(client, session.Id, "draw then edit", "r1");
        var watch = await RunWatch.OpenAsync(client, "r1");
        await watch.SettledAsync();

        Assert.False(watch.Of<ToolEndEventDto>().Single(t => t.CallId == "w2").IsError);
        Assert.Equal("<div>Jan</div>", DetailsOf(watch, "w2").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Two_controllers_racing_to_submit_an_interactive_widget_only_one_wins_and_the_run_gets_that_value()
    {
        var (a, _, session) = await SetupAsync("ctrl-a");
        var b = await _rig.ConnectAsync("ctrl-b");
        var interactions = await Watch<InteractionsDto>.OpenAsync(a, ConversationMethods.InteractionsTopic);
        _llm.On(session.Id, S.Call("w1", "visualize_show_widget", ShowInput("pick_db", "<div>pick</div>", interactive: true)), S.Say("got it"));

        await StartAsync(a, session.Id, "ask via widget", "r1");
        var watch = await RunWatch.OpenAsync(a, "r1");
        await Watch<Unit>.UntilAsync(() => interactions.Decoded<InteractionsDto>(InteractionEvents.Changed).Any(s => s.Pending.OfType<WidgetSubmitInteractionDto>().Any()), "the widget submit interaction");
        var pending = interactions.Decoded<InteractionsDto>(InteractionEvents.Changed).Last(s => s.Pending.Length == 1).Pending.OfType<WidgetSubmitInteractionDto>().Single();
        Assert.Equal(("w1", "pick_db"), (pending.CallId, pending.Title));

        async Task<(bool Ok, string? Code)> Submit(DeviceClient who, string value, string key)
        {
            try
            {
                var r = await who.CallAsync(WidgetsMethods.Submit, new WidgetSubmitParams("w1", JsonValue.Create(value)!.ToJsonString()), idem: key);
                return (r.Accepted, null);
            }
            catch (ProtocolException e)
            {
                return (false, e.Code);
            }
        }

        var results = await Task.WhenAll(Submit(a, "postgres", "sub-a"), Submit(b, "sqlite", "sub-b"));
        Assert.Equal(1, results.Count(r => r.Ok));
        await watch.SettledAsync();

        var content = watch.Of<ToolEndEventDto>().Single(t => t.CallId == "w1").Content;
        var winner = results[0].Ok ? "postgres" : "sqlite";
        Assert.Contains($"\"{winner}\"", content);
        Assert.DoesNotContain(results[0].Ok ? "sqlite" : "postgres", content);
        Assert.Equal(winner, JsonDocument.Parse(DetailsOf(watch, "w1").GetProperty("submitted").GetString()!).RootElement.GetString());
    }

    [Fact]
    public async Task Actions_run_through_the_permission_chain_declared_only_parameter_checked_and_confirmed_before_the_first_non_read_only_run()
    {
        var (client, project, session) = await SetupAsync();
        var note = Path.Combine(project.Path, "note.txt");
        File.WriteAllText(note, "hello from the file");
        var actions = new JsonArray(
            new JsonObject { ["id"] = "read_note", ["label"] = "Read note", ["tool"] = "Read", ["input"] = new JsonObject { ["file_path"] = note } },
            new JsonObject
            {
                ["id"] = "say",
                ["label"] = "Say a word",
                ["tool"] = "Bash",
                ["input"] = new JsonObject { ["command"] = "echo {{word}}" },
                ["params"] = new JsonObject { ["word"] = new JsonObject { ["type"] = "enum", ["values"] = new JsonArray("hello", "world") } },
            });
        _llm.On(session.Id, S.Call("w1", "visualize_show_widget", ShowInput("dashboard", "<div>buttons</div>", actions)), S.Say("ok"));
        await StartAsync(client, session.Id, "draw", "r1");
        await (await RunWatch.OpenAsync(client, "r1")).SettledAsync();

        Task<WidgetActionResultDto> Act(string id, string parameters, bool confirmed, string key) =>
            client.CallAsync(WidgetsMethods.RunAction, new WidgetActionParams(session.Id, "w1", id, parameters, confirmed), idem: key);

        // 只读动作:直接执行,输出回给 widget
        var read = await Act("read_note", "{}", false, "a1");
        Assert.Equal("result", read.Kind);
        Assert.Null(read.Reason);
        Assert.Contains("hello from the file", read.Output);

        // 没声明的动作 / 参数不合法:拒绝(稳定 key),不执行
        var undeclared = await Act("rm_everything", "{}", false, "a2");
        Assert.Equal(("deny", "widgets.action.undeclared"), (undeclared.Kind, undeclared.Reason));
        var evil = await Act("say", "{\"word\":\"hello; rm -rf ~\"}", true, "a3");
        Assert.Equal(("deny", "widgets.action.invalidParams"), (evil.Kind, evil.Reason));
        Assert.Equal("widgets.action.invalidParams", (await Act("say", "{\"word\":\"hello\",\"extra\":1}", true, "a4")).Reason);

        // 非只读:第一次一定先问,问的是真实的工具与入参(绑定之后),带执行环境标识
        var ask = await Act("say", "{\"word\":\"hello\"}", false, "a5");
        Assert.Equal("confirm", ask.Kind);
        using (var confirm = JsonDocument.Parse(ask.Output!))
        {
            Assert.Equal("Bash", confirm.RootElement.GetProperty("toolName").GetString());
            Assert.Equal("echo hello", confirm.RootElement.GetProperty("input").GetProperty("command").GetString());
            Assert.Equal("local", confirm.RootElement.GetProperty("environment").GetProperty("type").GetString());
        }

        // 确认后执行;确认记住(同一张卡、同一个动作、同样的入参不再问),入参一变又要问
        var run = await Act("say", "{\"word\":\"hello\"}", true, "a6");
        Assert.Equal("result", run.Kind);
        Assert.Null(run.Reason);
        Assert.Contains("hello", run.Output);
        Assert.Equal("result", (await Act("say", "{\"word\":\"hello\"}", false, "a7")).Kind);
        Assert.Equal("confirm", (await Act("say", "{\"word\":\"world\"}", false, "a8")).Kind);

        // 不存在的会话 / 卡片
        Assert.Equal("widgets.action.sessionGone", (await client.CallAsync(WidgetsMethods.RunAction, new WidgetActionParams("nope", "w1", "say", "{}", false), idem: "a9")).Reason);
        Assert.Equal("widgets.action.undeclared", (await client.CallAsync(WidgetsMethods.RunAction, new WidgetActionParams(session.Id, "ghost", "say", "{}", false), idem: "a10")).Reason);
    }
}
