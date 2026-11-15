using System.Net;
using System.Text;
using Ncw.Agent.Host.Conversation;
using Ncw.Agent.Host.Tests.Support;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Llm;
using Ncw.Agent.WebTools.Net;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Tests;

/// <summary>
/// 提问与联网工具(端到端):真实 Agent + <see cref="DeviceClient"/> + 假上游 + 假的联网出口(<see cref="AgentServices.WebHttp"/>)+ 真实的工具 / 权限 / 交互闸门 / SQLite。
/// 覆盖:AskUserQuestion 两个控制端抢答只有一个成功;WebFetch 在联网开关开着时无须审批、返回页面给模型、卡片带执行环境;开关关着时工具不下发、强行调用被拒;
/// 内网地址在到达网络之前被拒;web_search 经 websearch.* 配置后能搜、Key 不出设备;tools.list 列出新工具。
/// </summary>
public sealed class WebToolsTests : IAsyncLifetime
{
    private sealed class FakeWeb(Func<HttpRequestMessage, HttpResponseMessage> respond) : IWebHttp
    {
        public List<Uri> Requests { get; } = [];

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Requests)
            {
                Requests.Add(request.RequestUri!);
            }

            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Html(string body, string type = "text/html")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) };
        response.Content.Headers.ContentType = new(type);
        return response;
    }

    private readonly FakeWeb _web = new(r => r.RequestUri!.Host switch
    {
        "docs.example.com" => Html("<html><body><h1>Release notes</h1><p>Version 3.0 ships on Friday.</p><script>track()</script></body></html>"),
        "api.tavily.com" => Html("""{"results":[{"title":"NextCoWork 3.0","url":"https://nextco.work/3","content":"The C# rewrite","published_date":"2026-11-01"}]}""", "application/json"),
        _ => throw new InvalidOperationException("unexpected host " + r.RequestUri),
    });

    private readonly ConversationRig _rig;

    public WebToolsTests()
    {
        _rig = new ConversationRig(llm => new AgentServices
        {
            Models = new FakeModels(llm),
            Http = new NoNetwork(),
            CatalogDelayer = new NeverDelay(),
            Delayer = new NoDelay(),
            Loop = new AgentLoopOptions { ResumeDelays = [] },
            WebHttp = _web,
        });
    }

    public ValueTask InitializeAsync() => _rig.InitializeAsync();

    public ValueTask DisposeAsync() => _rig.DisposeAsync();

    private async Task<(DeviceClient Client, ProjectDto Project, SessionDto Session)> SetupAsync(string clientId = "ctrl-a", bool? webSearch = null)
    {
        var client = await _rig.ConnectAsync(clientId);
        var project = await _rig.AddProjectAsync(client);
        if (webSearch is { } on)
        {
            await client.CallAsync(SettingsMethods.ProjectUpdate, new ProjectSettingsPatchDto(project.WorkspaceId, WebSearch: on));
        }

        var session = await _rig.CreateSessionAsync(client, project);
        return (client, project, session);
    }

    private static string Json(params (string Key, string Value)[] fields) =>
        new System.Text.Json.Nodes.JsonObject(fields.Select(static f => new KeyValuePair<string, System.Text.Json.Nodes.JsonNode?>(f.Key, f.Value))).ToJsonString();

    private string LastToolResults() =>
        string.Join('\n', _rig.Llm.Requests[^1].Messages.SelectMany(static m => m.Parts).OfType<ToolResultPart>().Select(static r => r.Output.Content));

    // ───────────────────────── AskUserQuestion ─────────────────────────

    [Fact]
    public async Task Two_controllers_racing_to_answer_an_AskUserQuestion_only_one_wins_and_the_model_gets_that_answer()
    {
        var (a, _, session) = await SetupAsync("ctrl-a");
        var b = await _rig.ConnectAsync("ctrl-b");
        var args = """{"questions":[{"header":"Database","question":"Which database?","options":[{"label":"SQLite","description":"embedded"},{"label":"Postgres"}]}]}""";
        _rig.Llm.Then(Up.Calls(("q1", "AskUserQuestion", args))).Then(Up.Say("ok"));
        var interactions = await Watch<InteractionsDto>.OpenAsync(b, ConversationMethods.InteractionsTopic);

        var start = await _rig.StartAsync(a, session.Id, "set up storage");
        var watch = await RunWatch.OpenAsync(a, start.RunId);

        await interactions.WaitAsync(() => interactions.Decoded<InteractionsDto>(InteractionEvents.Changed).Any(s => s.Pending.Length == 1), "pending question");
        var pending = Assert.IsType<AskUserInteractionDto>(Assert.Single(interactions.Decoded<InteractionsDto>(InteractionEvents.Changed).Last(s => s.Pending.Length == 1).Pending));
        Assert.Equal("Database", pending.Questions[0].Header);
        Assert.Equal(["SQLite", "Postgres"], pending.Questions[0].Options.Select(static o => o.Label));
        Assert.Equal(start.RunId, pending.RunId);

        // 两个控制端同时回答不同的答案:只有一个成功,另一个 conflict
        async Task<(RespondOutcome, string)> Answer(DeviceClient client, string who, string value)
        {
            try
            {
                var result = await client.CallAsync(ConversationMethods.InteractionsRespond,
                    new InteractionsRespondParams(pending.Id, pending.Version, new AskUserResponseDto([new AskUserAnswerDto([value])])));
                return (new RespondOutcome(result.Resolution.ResolvedBy, null), value);
            }
            catch (ProtocolException e)
            {
                return (new RespondOutcome(null, e.Code), value);
            }
        }

        var results = await Task.WhenAll(Answer(a, "ctrl-a", "SQLite"), Answer(b, "ctrl-b", "Postgres"));
        var winner = Assert.Single(results, static r => r.Item1.ResolvedBy is not null);
        var loser = Assert.Single(results, static r => r.Item1.ErrorCode is not null);
        Assert.Equal(ErrorCodes.Conflict, loser.Item1.ErrorCode);

        await watch.SettledAsync();
        Assert.Equal("done", watch.Of<RunEndEventDto>().Single().Status);
        // 模型拿到的是赢家的答案,不是后到的
        var seen = LastToolResults();
        Assert.Contains($"\"answer\":[\"{winner.Item2}\"]", seen);
        Assert.DoesNotContain($"\"answer\":[\"{loser.Item2}\"]", seen);
        Assert.Contains("Which database?", seen);
        Assert.False(Assert.Single(watch.Of<ToolEndEventDto>()).IsError);
    }

    private sealed record RespondOutcome(string? ResolvedBy, string? ErrorCode);

    [Fact]
    public async Task Dismissing_an_AskUserQuestion_reaches_the_model_as_no_answer()
    {
        var (client, _, session) = await SetupAsync();
        _rig.Llm.Then(Up.Calls(("q1", "AskUserQuestion", """{"questions":[{"header":"x","question":"anything?"}]}"""))).Then(Up.Say("fine"));
        var interactions = await Watch<InteractionsDto>.OpenAsync(client, ConversationMethods.InteractionsTopic);
        var start = await _rig.StartAsync(client, session.Id, "ask me");
        var watch = await RunWatch.OpenAsync(client, start.RunId);

        await interactions.WaitAsync(() => interactions.Decoded<InteractionsDto>(InteractionEvents.Changed).Any(s => s.Pending.Length == 1), "pending question");
        var pending = interactions.Decoded<InteractionsDto>(InteractionEvents.Changed).Last(s => s.Pending.Length == 1).Pending[0];
        await client.CallAsync(ConversationMethods.InteractionsRespond, new InteractionsRespondParams(pending.Id, pending.Version, new AskUserResponseDto(null)));

        await watch.SettledAsync();
        Assert.True(Assert.Single(watch.Of<ToolEndEventDto>()).IsError);
        Assert.Contains("Do not assume an answer", LastToolResults());
    }

    // ───────────────────────── WebFetch ─────────────────────────

    [Fact]
    public async Task WebFetch_runs_without_an_approval_when_the_web_switch_is_on_hands_the_page_to_the_model_and_the_card_carries_the_environment()
    {
        var (client, _, session) = await SetupAsync(webSearch: true);
        _rig.Llm.Then(Up.Calls(("f1", "WebFetch", Json(("url", "https://docs.example.com/release"), ("prompt", "when does 3.0 ship"))))).Then(Up.Say("Friday"));
        var start = await _rig.StartAsync(client, session.Id, "when does 3.0 ship?", permission: "ask");
        var watch = await RunWatch.OpenAsync(client, start.RunId);

        await watch.SettledAsync();
        Assert.Equal("done", watch.Of<RunEndEventDto>().Single().Status);
        Assert.Empty(watch.Of<InteractionRequestEventDto>()); // 只读 + 开关开着:不弹审批

        var toolStart = Assert.Single(watch.Of<ToolStartEventDto>());
        Assert.Equal("WebFetch", toolStart.ToolName);
        Assert.Equal(_rig.Host.DeviceId, toolStart.Environment!.DeviceId); // 工具卡片带执行环境标识
        var toolEnd = Assert.Single(watch.Of<ToolEndEventDto>());
        Assert.False(toolEnd.IsError);
        Assert.Contains("You are looking for: when does 3.0 ship", toolEnd.Content);
        Assert.Contains("Version 3.0 ships on Friday.", toolEnd.Content);
        Assert.DoesNotContain("track()", toolEnd.Content);
        Assert.Contains("\"kind\":\"webFetch\"", toolEnd.DetailsJson);
        Assert.Contains("\"deviceId\":\"" + _rig.Host.DeviceId + "\"", toolEnd.DetailsJson);
        Assert.Equal("https://docs.example.com/release", Assert.Single(_web.Requests).ToString());
        Assert.Contains("Version 3.0 ships on Friday.", LastToolResults());
    }

    [Fact]
    public async Task With_the_web_switch_off_the_tools_are_not_offered_and_a_forced_call_is_denied_before_any_request()
    {
        var (client, _, session) = await SetupAsync(webSearch: false);
        _rig.Llm.Then(Up.Calls(("f1", "WebFetch", Json(("url", "https://docs.example.com/release"), ("prompt", "x"))))).Then(Up.Say("understood"));
        var start = await _rig.StartAsync(client, session.Id, "fetch it", permission: "full"); // full 档也放宽不了联网开关
        var watch = await RunWatch.OpenAsync(client, start.RunId);

        await watch.SettledAsync();
        // 工具快照里没有联网工具(模型根本看不到它们)
        var offered = _rig.Llm.Requests[0].Tools.Select(static t => t.ExternalName).ToList();
        Assert.DoesNotContain("WebFetch", offered);
        Assert.DoesNotContain("web_search", offered);
        Assert.Contains("Read", offered);
        Assert.Contains("AskUserQuestion", offered);
        // 模型硬要调:快照里没有这个名字 → 未知工具错误;就算在快照里,权限闸也会拒(NetworkOff,见 PermissionGateTests)——无论哪一道,都没有发出任何请求
        Assert.True(Assert.Single(watch.Of<ToolEndEventDto>()).IsError);
        Assert.Empty(_web.Requests);
    }

    [Fact]
    public async Task With_the_web_switch_on_both_network_tools_are_offered_to_the_model()
    {
        var (client, _, session) = await SetupAsync(webSearch: true);
        _rig.Llm.Then(Up.Say("hi"));
        var start = await _rig.StartAsync(client, session.Id, "hello");
        await (await RunWatch.OpenAsync(client, start.RunId)).SettledAsync();
        var offered = _rig.Llm.Requests[0].Tools.Select(static t => t.ExternalName).ToList();
        Assert.Contains("WebFetch", offered);
        Assert.Contains("web_search", offered);
        Assert.Contains("AskUserQuestion", offered);
        Assert.Contains("WebFetch", _rig.Llm.Requests[0].System); // 系统提示词的工具段(PromptSnippet)
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://127.0.0.1:8080/admin")]
    [InlineData("http://localhost/")]
    [InlineData("http://2130706433/")]
    [InlineData("http://[::ffff:7f00:1]/")]
    public async Task An_internal_address_is_refused_end_to_end_and_never_reaches_the_network(string url)
    {
        var (client, _, session) = await SetupAsync(webSearch: true);
        _rig.Llm.Then(Up.Calls(("f1", "WebFetch", Json(("url", url), ("prompt", "x"))))).Then(Up.Say("ok"));
        var start = await _rig.StartAsync(client, session.Id, "fetch the metadata", permission: "full");
        var watch = await RunWatch.OpenAsync(client, start.RunId);

        await watch.SettledAsync();
        var end = Assert.Single(watch.Of<ToolEndEventDto>());
        Assert.True(end.IsError);
        Assert.Contains("Refusing to reach", end.Content);
        Assert.Empty(_web.Requests);
    }

    // ───────────────────────── web_search + websearch.* ─────────────────────────

    [Fact]
    public async Task Websearch_methods_configure_a_provider_and_web_search_uses_it_without_the_key_ever_coming_back()
    {
        var (client, _, session) = await SetupAsync(webSearch: true);
        var topic = await Watch<WebSearchTopicState>.OpenAsync(client, WebSearchMethods.Topic);
        await topic.WaitAsync(() => topic.HasSnapshot, "websearch snapshot");
        Assert.Equal(8, topic.Snapshot.Providers.Length);
        Assert.All(topic.Snapshot.Providers, static p => Assert.False(p.Enabled || p.HasKey));

        const string secret = "tvly-SECRET-0123456789";
        // 没有 Key 时不能测试;Key 写进去之后列表只带 HasKey / Last4
        var before = await client.CallAsync(WebSearchMethods.Test, new WebSearchRefParams("tavily"));
        Assert.Equal("websearch.test.noKey", before.Key);
        var info = await client.CallAsync(WebSearchMethods.SetCredential, new WebSearchCredentialSetParams("tavily", secret));
        Assert.True(info.HasKey);
        Assert.Equal("6789", info.Last4);
        var enabled = await client.CallAsync(WebSearchMethods.SetEnabled, new WebSearchSetEnabledParams("tavily", true));
        var tavily = enabled.Providers.Single(static p => p.Id == "tavily");
        Assert.True(tavily.Enabled);
        Assert.True(tavily.HasKey);
        Assert.Equal("6789", tavily.Last4);
        await topic.WaitAsync(() => topic.Decoded<WebSearchTopicState>(WebSearchEvents.Changed).Any(static s => s.Providers.Single(p => p.Id == "tavily").Enabled), "websearch.changed");
        Assert.True(topic.Decoded<WebSearchTopicState>(WebSearchEvents.Changed).Select(static s => s.Version).Distinct().Count() >= 2);

        var test = await client.CallAsync(WebSearchMethods.Test, new WebSearchRefParams("tavily"));
        Assert.True(test.Ok);
        Assert.Null(test.Detail); // 只回通不通,没有搜索结果

        // 用不了的家不能启用
        var bing = await Assert.ThrowsAsync<ProtocolException>(() => client.CallAsync(WebSearchMethods.SetEnabled, new WebSearchSetEnabledParams("bing", true)));
        Assert.Equal(ErrorCodes.InvalidRequest, bing.Code);

        // 模型搜索:结果回到模型,Key 不在任何地方
        _rig.Llm.Then(Up.Calls(("s1", "web_search", Json(("query", "nextcowork 3.0"))))).Then(Up.Say("found"));
        var start = await _rig.StartAsync(client, session.Id, "search for it");
        var watch = await RunWatch.OpenAsync(client, start.RunId);
        await watch.SettledAsync();
        var end = Assert.Single(watch.Of<ToolEndEventDto>());
        Assert.False(end.IsError);
        Assert.Contains("NextCoWork 3.0", end.Content);
        Assert.Contains("https://nextco.work/3", end.Content);
        Assert.Contains("(via Tavily)", end.Content);
        Assert.DoesNotContain(secret, end.Content);
        Assert.DoesNotContain(secret, end.DetailsJson);
        Assert.DoesNotContain(secret, LastToolResults());
        Assert.All(topic.Events, e => Assert.DoesNotContain(secret, Encoding.UTF8.GetString(e.Data.ToArray())));

        // 清 Key 顺手把这家关掉
        var cleared = await client.CallAsync(WebSearchMethods.ClearCredential, new WebSearchRefParams("tavily"));
        var after = cleared.Providers.Single(static p => p.Id == "tavily");
        Assert.False(after.HasKey);
        Assert.False(after.Enabled);
    }

    [Fact]
    public async Task Reordering_providers_is_a_full_renumber_and_unknown_ids_are_ignored()
    {
        var (client, _, _) = await SetupAsync();
        var state = await client.CallAsync(WebSearchMethods.Reorder, new WebSearchReorderParams(["metaso", "ghost", "exa"]));
        Assert.Equal(["metaso", "exa", "tavily", "brave", "serper", "serpapi", "doubao", "bing"], state.Providers.Select(static p => p.Id));
        Assert.Equal(Enumerable.Range(0, 8), state.Providers.Select(static p => p.Priority));
        Assert.Equal(state.Providers.Select(static p => p.Id), (await client.CallAsync(WebSearchMethods.List, Unit.Value)).Providers.Select(static p => p.Id));
    }

    [Fact]
    public async Task The_method_table_declares_member_reads_and_owner_writes_and_tests()
    {
        var (owner, _, _) = await SetupAsync();
        // 方法表声明 owner 才能写 / 测试、member 起读:这里验证声明本身(角色检查由路由统一做,在 ConversationRoleTests 里对每个角色走过)
        Assert.Equal(Ncw.Protocol.Methods.Role.Member, WebSearchMethods.List.MinRole);
        Assert.All(new MethodDef[] { WebSearchMethods.SetEnabled, WebSearchMethods.Reorder, WebSearchMethods.SetCredential, WebSearchMethods.ClearCredential, WebSearchMethods.Test }, static m => Assert.Equal(Ncw.Protocol.Methods.Role.Owner, m.MinRole));
        Assert.NotNull(await owner.CallAsync(WebSearchMethods.List, Unit.Value));
    }

    // ───────────────────────── tools.list ─────────────────────────

    [Fact]
    public async Task Tools_list_includes_the_new_tools_with_their_traits_and_the_network_flag()
    {
        var client = await _rig.ConnectAsync();
        var tools = (await client.CallAsync(ConversationMethods.ToolsList, new ToolsListParams())).Tools;

        var fetch = Assert.Single(tools, static t => t.Name == "WebFetch");
        Assert.True(fetch.NeedsNetwork);
        Assert.True(fetch.ReadOnly);
        var search = Assert.Single(tools, static t => t.Name == "web_search");
        Assert.True(search.NeedsNetwork);
        var ask = Assert.Single(tools, static t => t.Name == "AskUserQuestion");
        Assert.True(ask.ReadOnly);
        Assert.False(ask.NeedsNetwork);
        Assert.All(new[] { fetch, search, ask }, static t => Assert.StartsWith("{", t.InputSchemaJson));
    }
}
