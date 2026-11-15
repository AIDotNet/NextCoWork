using Ncw.Agent.Browser;
using Ncw.Agent.Browser.Launch;
using Ncw.Agent.Host.Conversation;
using Ncw.Agent.Host.Tests.Support;
using Ncw.Agent.Kernel.Loop;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;

namespace Ncw.Agent.Host.Tests;

/// <summary>
/// 浏览器工具的 Host 装配(端到端):真实 Agent + <see cref="DeviceClient"/> + 假上游 + 假的浏览器启动器(模拟「设备上没装浏览器」)。
/// CDP 本身(协议、页面语义、真实 Chrome 冒烟)在 <c>Ncw.Agent.Browser.Tests</c>;这里只验证工具被装配、列出、受权限约束、失败带执行环境标识和结构化 key。
/// </summary>
public sealed class BrowserToolsTests : IAsyncLifetime
{
    private sealed class MissingBrowserLauncher : IBrowserLauncher
    {
        public int Launches { get; private set; }

        public Task<LaunchedBrowser> LaunchAsync(BrowserLaunchRequest request, CancellationToken ct)
        {
            Launches++;
            throw new BrowserException(BrowserErrorKeys.NotInstalled, "No Chrome, Edge or Chromium installation was found on this device, and browser tools never download one. Do not retry.");
        }
    }

    private readonly MissingBrowserLauncher _launcher = new();
    private readonly ConversationRig _rig;

    public BrowserToolsTests()
    {
        _rig = new ConversationRig(llm => new AgentServices
        {
            Models = new FakeModels(llm),
            Http = new NoNetwork(),
            CatalogDelayer = new NeverDelay(),
            Delayer = new NoDelay(),
            Loop = new AgentLoopOptions { ResumeDelays = [] },
            BrowserLauncher = _launcher,
        });
    }

    public ValueTask InitializeAsync() => _rig.InitializeAsync();

    public ValueTask DisposeAsync() => _rig.DisposeAsync();

    [Fact]
    public async Task Tools_list_includes_every_browser_tool_with_2x_traits()
    {
        var client = await _rig.ConnectAsync();
        var tools = (await client.CallAsync(ConversationMethods.ToolsList, new ToolsListParams())).Tools;

        foreach (var name in BrowserTools.Names)
        {
            Assert.Single(tools, t => t.Name == name);
        }

        var open = Assert.Single(tools, static t => t.Name == "browser_open");
        Assert.True(open.ReadOnly);
        Assert.True(open.NeedsNetwork);
        var click = Assert.Single(tools, static t => t.Name == "browser_click");
        Assert.False(click.ReadOnly);
        Assert.False(click.Destructive);
        Assert.Contains("\"ref\"", click.InputSchemaJson);
    }

    [Fact]
    public async Task Browser_tools_are_offered_even_with_the_web_switch_off_because_that_switch_only_governs_search_and_fetch()
    {
        var client = await _rig.ConnectAsync();
        var project = await _rig.AddProjectAsync(client);
        await client.CallAsync(SettingsMethods.ProjectUpdate, new ProjectSettingsPatchDto(project.WorkspaceId, WebSearch: false));
        var session = await _rig.CreateSessionAsync(client, project);
        _rig.Llm.Then(Up.Say("hi"));

        var start = await _rig.StartAsync(client, session.Id, "hello");
        await (await RunWatch.OpenAsync(client, start.RunId)).SettledAsync();

        var offered = _rig.Llm.Requests[0].Tools.Select(static t => t.ExternalName).ToList();
        Assert.Contains("browser_open", offered);
        Assert.Contains("browser_snapshot", offered);
        Assert.DoesNotContain("WebFetch", offered);
        Assert.Contains("browser_open", _rig.Llm.Requests[0].System); // 系统提示词的工具段(PromptSnippet)
    }

    [Fact]
    public async Task No_installed_browser_is_a_tool_error_with_a_structured_key_and_the_environment_identity_and_the_run_carries_on()
    {
        var client = await _rig.ConnectAsync();
        var project = await _rig.AddProjectAsync(client);
        var session = await _rig.CreateSessionAsync(client, project);
        _rig.Llm.Then(Up.Calls(("b1", "browser_open", """{"url":"http://127.0.0.1:5173/"}"""))).Then(Up.Say("I could not open a browser"));

        var start = await _rig.StartAsync(client, session.Id, "open my dev server", permission: "ask");
        var watch = await RunWatch.OpenAsync(client, start.RunId);
        await watch.SettledAsync();

        Assert.Equal("done", watch.Of<RunEndEventDto>().Single().Status);
        Assert.Empty(watch.Of<InteractionRequestEventDto>()); // browser_open 只读(2.x):不弹审批
        var start1 = Assert.Single(watch.Of<ToolStartEventDto>());
        Assert.Equal(_rig.Host.DeviceId, start1.Environment!.DeviceId);
        var end = Assert.Single(watch.Of<ToolEndEventDto>());
        Assert.True(end.IsError);
        Assert.Contains("never download", end.Content);
        Assert.Contains("\"errorKey\":\"browser.error.notInstalled\"", end.DetailsJson);
        Assert.Contains("\"deviceId\":\"" + _rig.Host.DeviceId + "\"", end.DetailsJson);
        Assert.Equal(1, _launcher.Launches);
    }

    [Fact]
    public async Task A_url_with_credentials_or_a_script_scheme_never_launches_a_browser()
    {
        var client = await _rig.ConnectAsync();
        var project = await _rig.AddProjectAsync(client);
        var session = await _rig.CreateSessionAsync(client, project);
        _rig.Llm
            .Then(Up.Calls(("b1", "browser_open", """{"url":"https://user:pw@example.com/"}"""), ("b2", "browser_open", """{"url":"javascript:alert(1)"}""")))
            .Then(Up.Say("refused"));

        var start = await _rig.StartAsync(client, session.Id, "open these", permission: "full");
        var watch = await RunWatch.OpenAsync(client, start.RunId);
        await watch.SettledAsync();

        var ends = watch.Of<ToolEndEventDto>().ToList();
        Assert.Equal(2, ends.Count);
        Assert.All(ends, e =>
        {
            Assert.True(e.IsError);
            Assert.Contains("browser.error.urlRefused", e.DetailsJson);
        });
        Assert.Equal(0, _launcher.Launches);
    }

    [Fact]
    public async Task A_state_changing_browser_tool_asks_for_approval_in_ask_mode_and_the_card_carries_the_environment()
    {
        var client = await _rig.ConnectAsync();
        var project = await _rig.AddProjectAsync(client);
        var session = await _rig.CreateSessionAsync(client, project);
        _rig.Llm.Then(Up.Calls(("b1", "browser_click", """{"tabId":"tab-none","ref":"e2"}"""))).Then(Up.Say("ok"));
        var interactions = await Watch<InteractionsDto>.OpenAsync(client, ConversationMethods.InteractionsTopic);

        var start = await _rig.StartAsync(client, session.Id, "click it", permission: "ask");
        var watch = await RunWatch.OpenAsync(client, start.RunId);
        await interactions.WaitAsync(() => interactions.Decoded<InteractionsDto>(InteractionEvents.Changed).Any(s => s.Pending.Length == 1), "pending approval");
        var pending = Assert.IsType<ToolPermissionInteractionDto>(Assert.Single(interactions.Decoded<InteractionsDto>(InteractionEvents.Changed).Last(s => s.Pending.Length == 1).Pending));

        Assert.Equal("browser_click", pending.InternalId);
        Assert.False(pending.ReadOnly);
        Assert.True(pending.NeedsNetwork); // 浏览器工具声明联网,但联网开关只管 WebFetch / web_search(2.x),所以开关没开也走到了审批
        Assert.Equal(_rig.Host.DeviceId, pending.Env.DeviceId);

        await client.CallAsync(ConversationMethods.InteractionsRespond, new InteractionsRespondParams(pending.Id, pending.Version, new ToolPermissionResponseDto(PermissionChoices.AllowOnce)));
        await watch.SettledAsync();

        // 批准后才真正执行:没有这个标签 → 结构化的 tabNotFound,不是崩溃
        var end = Assert.Single(watch.Of<ToolEndEventDto>());
        Assert.True(end.IsError);
        Assert.Contains("browser.error.tabNotFound", end.DetailsJson);
    }
}
