using System.IO.Compression;
using System.Text;
using Ncw.Agent.Host.Conversation;
using Ncw.Agent.Host.Tests.Support;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Llm;
using Ncw.Agent.Plugins;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Security;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Tests;

/// <summary>
/// 插件的端到端:真实 Agent(进程内)+ 真实 <see cref="DeviceClient"/> + 假上游 + 真实 Jaunt 运行时 + 真实文件。
/// 覆盖:安装插件(<c>plugins.installPackage</c>)→ 授权 → 启用 → 命令 / 工具出现(<c>plugins.list</c> / <c>tools.list</c>)→ 调用(命令;模型调工具并走审批)→ 权限撤销后失效;
/// 插件贡献的 Skill / 拦截器 / 上下文进对话链路;死循环的插件被终止而 Agent 照常服务;viewer / member / owner 的角色限制;市场没有令牌 = <c>plugins.authRequired</c>。
/// </summary>
public sealed class PluginsTests : IAsyncLifetime
{
    private readonly HostDiagnostics _log = new();
    private readonly ConversationRig _rig;
    private readonly FakeMarket _market = new();
    private DeviceClient _owner = null!;
    private int _port;

    public PluginsTests()
    {
        _rig = new ConversationRig(llm => new AgentServices
        {
            Models = new FakeModels(llm),
            Http = new NoNetwork(),
            CatalogDelayer = new NeverDelay(),
            McpDelayer = new NeverDelay(),
            Delayer = new NoDelay(),
            Loop = new AgentLoopOptions { ResumeDelays = [] },
            Diagnostics = _log,
            PluginMarket = _market,
            PluginLimits = new PluginRuntimeLimits
            {
                ActivateTimeout = TimeSpan.FromSeconds(5),
                CommandTimeout = TimeSpan.FromSeconds(5),
                ToolTimeout = TimeSpan.FromSeconds(5),
                HookTimeout = TimeSpan.FromSeconds(2),
                MaxInstructionsPerCall = 3_000_000,
                InteractionTimeout = TimeSpan.FromSeconds(20),
            },
        });
    }

    public async ValueTask InitializeAsync()
    {
        await _rig.InitializeAsync();
        _owner = await _rig.ConnectAsync("owner");
        _port = (await _owner.CallAsync(DeviceMethods.RemoteSetEnabled, new RemoteSetEnabledParams(true))).Port!.Value;
    }

    public ValueTask DisposeAsync() => _rig.DisposeAsync();

    private sealed class FakeMarket : IPluginMarketClient
    {
        public Task<PluginMarketPage> ListAsync(string? query, string? category, int page, int pageSize, string clientVersion, CancellationToken ct) =>
            Task.FromResult(new PluginMarketPage([new PluginMarketItem("acme.pdf", "pdf", "acme", "PDF", "d", "docs", null, 1, "1.0.0", "^0.3.0", ["net"], ["pdf-tools"], "Acme")], false, page));

        public Task<IReadOnlyList<string>> CategoriesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(["docs"]);

        public Task<PluginMarketDetail> DetailAsync(string slug, CancellationToken ct) =>
            Task.FromResult(new PluginMarketDetail(new PluginMarketItem("acme.pdf", slug, "acme", "PDF", "d", "docs", null, 1, "1.0.0", "^0.3.0", [], [], "Acme"), [new PluginMarketVersion("1.0.0", null, null, null)]));

        public Task<PluginInstallGrant> GrantAsync(string slug, string version, CancellationToken ct) =>
            throw new PluginException(PluginErrorKeys.AuthRequired, "Installing from the market needs an account sign-in.");

        public Task<byte[]> DownloadAsync(string slug, string version, int maxBytes, Action<PluginDownloadProgress>? progress, CancellationToken ct) => throw new InvalidOperationException("not reached");
    }

    // ───────────────────────── 夹具 ─────────────────────────

    private static byte[] FixtureZip(string id)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Fixtures", id);
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var entry = zip.CreateEntry($"{id}/{Path.GetRelativePath(root, file).Replace('\\', '/')}");
                using var output = entry.Open();
                output.Write(File.ReadAllBytes(file));
            }
        }

        return memory.ToArray();
    }

    private static byte[] InlineZip(string name, string main, string manifestExtra)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, string content)
            {
                using var w = new StreamWriter(zip.CreateEntry($"acme.{name}/{path}").Open(), new UTF8Encoding(false));
                w.Write(content);
            }

            Add("package.json", $$"""{ "name": "{{name}}", "publisher": "acme", "version": "1.0.0", "engines": { "nextcowork": "^0.3.0" }, "main": "./main.js", {{manifestExtra}} }""");
            Add("main.js", main);
        }

        return memory.ToArray();
    }

    private async Task<PluginsInstallResult> InstallAsync(DeviceClient client, byte[] zip, params string[] grant)
    {
        var installed = await client.CallAsync(PluginsMethods.InstallPackage, new PluginsInstallPackageParams(zip, "plugin.zip"), idem: Guid.NewGuid().ToString("N"));
        var id = installed.Installed.Id;
        await client.CallAsync(PluginsMethods.GrantPermissions, new PluginsPermissionsParams(id, [.. installed.Installed.Required, .. grant]), idem: Guid.NewGuid().ToString("N"));
        var state = await client.CallAsync(PluginsMethods.SetEnabled, new PluginsSetEnabledParams(id, true), idem: Guid.NewGuid().ToString("N"));
        return new PluginsInstallResult(state.Plugins.Single(p => p.Id == id), state);
    }

    private async Task<DeviceClient> ConnectRemoteAsync(string name, string role)
    {
        var identity = PeerIdentity.Create(name);
        var paired = await RemoteDevices.PairAsync("127.0.0.1", _port, identity, _rig.Host.Remote.PairingCode!, name);
        await _owner.CallAsync(DeviceMethods.TrustSetRole, new TrustSetRoleParams(identity.FingerprintHex, role));
        var stream = await RemoteDevices.ConnectAsync("127.0.0.1", _port, identity, paired.DeviceFingerprint);
        var client = await DeviceClient.ConnectAsync(stream, new ClientHello(name, "3.0.0"));
        Assert.Equal(role, client.Welcome.Role);
        return client;
    }

    private static async Task<string> CodeOf<T>(Func<Task<T>> call)
    {
        var e = await Assert.ThrowsAsync<ProtocolException>(call);
        return e.Code;
    }

    private static string KeyOf(ProtocolException e) => FrameCodec.DecodePayload(e.Details!, PayloadShapes.GetShape<ProviderErrorDto>()).Key;

    private static string TextOf(IEnumerable<Message> messages) =>
        string.Join('\n', messages.SelectMany(m => m.Parts).OfType<ToolResultPart>().Select(r => r.Output.Content));

    private static string AllText(IEnumerable<Message> messages) =>
        string.Join('\n', messages.SelectMany(m => m.Parts).OfType<TextPart>().Select(p => p.Text));

    // ───────────────────────── 安装 → 命令 / 工具 → 调用 ─────────────────────────

    [Fact]
    public async Task An_installed_and_enabled_plugin_shows_in_the_list_its_command_runs_and_revoking_a_required_permission_stops_it()
    {
        var topic = await Watch<PluginsTopicState>.OpenAsync(_owner, PluginsMethods.Topic);
        await Watch<Unit>.UntilAsync(() => topic.HasSnapshot, "plugins snapshot");
        Assert.Empty(topic.Snapshot.Plugins);
        Assert.Equal(PluginApiVersion.Current, topic.Snapshot.ApiVersion);

        var hello = await InstallAsync(_owner, FixtureZip("acme.hello"));

        Assert.Equal((PluginStatuses.Active, true), (hello.Installed.Status, hello.Installed.Enabled));
        Assert.Contains("acme.hello.hello", hello.Installed.RegisteredCommands);
        Assert.Equal(["storage"], hello.Installed.Granted);
        Assert.Equal("Say hello", hello.Installed.Messages.Single(m => m.Locale == "en-US").Entries["plugin.acme.hello.cmd.hello"]);
        var listed = await _owner.CallAsync(PluginsMethods.List, Unit.Value);
        Assert.Equal(hello.Installed.Id, listed.Plugins.Single().Id);

        var project = await _rig.AddProjectAsync(_owner);
        await _owner.CallAsync(PluginsMethods.RunCommand, new PluginsRunCommandParams("acme.hello", "acme.hello.hello", null, project.WorkspaceId), idem: Guid.NewGuid().ToString("N"));
        var activity = await _owner.CallAsync(PluginsMethods.Activity, new PluginsActivityParams("acme.hello"));
        Assert.Contains(activity.Entries, a => a.Method == "storage.set" && a.Verdict == "ok");
        Assert.DoesNotContain(activity.Entries, a => a.Summary.Contains(project.WorkspaceId, StringComparison.Ordinal)); // 不含参数原文

        // 撤销必选能力:插件停下来,命令失效
        var after = await _owner.CallAsync(PluginsMethods.RevokePermissions, new PluginsPermissionsParams("acme.hello", ["storage"]), idem: Guid.NewGuid().ToString("N"));
        Assert.Equal((PluginStatuses.Disabled, false), (after.Plugins.Single().Status, after.Plugins.Single().Enabled));
        var stopped = await Assert.ThrowsAsync<ProtocolException>(() => _owner.CallAsync(PluginsMethods.RunCommand, new PluginsRunCommandParams("acme.hello", "acme.hello.hello", null, null), idem: Guid.NewGuid().ToString("N")));
        Assert.Equal(PluginErrorKeys.NotRunnable, KeyOf(stopped));

        // 主题:Rev 单调,最后一帧就是最新状态
        await topic.WaitAsync(() => topic.Decoded<PluginsTopicState>(PluginsEvents.Changed).Any(s => s.Plugins.Any(p => p.Status == PluginStatuses.Disabled)), "disabled state");
        var revs = topic.Decoded<PluginsTopicState>(PluginsEvents.Changed).Select(s => s.Rev).ToList();
        Assert.Equal(revs.Order(), revs);
        Assert.Equal(revs.Count, revs.Distinct().Count());
    }

    [Fact]
    public async Task A_plugin_tool_appears_in_tools_list_the_model_calls_it_and_the_result_and_card_come_back()
    {
        await InstallAsync(_owner, FixtureZip("acme.think"));
        var tool = (await _owner.CallAsync(ConversationMethods.ToolsList, new ToolsListParams())).Tools.Single(t => t.Name == "plugin__acme_think__think");
        Assert.True(tool.ReadOnly);
        Assert.StartsWith("[Plugin:Think]", tool.Description, StringComparison.Ordinal);

        var project = await _rig.AddProjectAsync(_owner);
        var session = await _rig.CreateSessionAsync(_owner, project);
        _rig.Llm.Then(Up.Calls(("c1", "plugin__acme_think__think", """{"task_goal":"ship","thought":"check the edge cases"}"""))).Then(Up.Say("done"));
        var start = await _rig.StartAsync(_owner, session.Id, "think about it");
        var run = await RunWatch.OpenAsync(_owner, start.RunId);
        await run.SettledAsync();

        Assert.Contains("plugin__acme_think__think", _rig.Llm.Requests[0].Tools.Select(t => t.ExternalName));
        var result = TextOf(_rig.Llm.Requests[1].Messages);
        Assert.StartsWith("Thought recorded.", result, StringComparison.Ordinal);
        Assert.DoesNotContain("edge cases", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_non_read_only_plugin_tool_goes_through_the_approval_card_names_the_tool_and_only_runs_after_allow()
    {
        var project = await _rig.AddProjectAsync(_owner);
        await InstallAsync(_owner, InlineZip("writer", """
            import * as ncw from 'nextcowork'
            export function activate(c) {
              c.subscriptions.push(ncw.tools.registerTool('save', { description: 'saves a note', inputSchema: { type: 'object', properties: { path: { type: 'string' }, data: { type: 'string' } } }, async invoke({ input }) { await ncw.workspace.fs.writeFile(input.path, input.data); return 'saved ' + input.path } }))
            }
            """, """ "permissions": ["workspace.write"], "contributes": { "tools": [{ "name": "save", "title": "%t%" }] } """));
        var tools = (await _owner.CallAsync(ConversationMethods.ToolsList, new ToolsListParams())).Tools;
        var save = tools.Single(t => t.Name == "plugin__acme_writer__save");
        Assert.False(save.ReadOnly); // 清单里有写类能力:不信自报
        Assert.True(save.Destructive);

        var session = await _rig.CreateSessionAsync(_owner, project);
        _rig.Llm.Then(Up.Calls(("c1", "plugin__acme_writer__save", """{"path":"notes/a.txt","data":"hello"}"""))).Then(Up.Say("ok"));
        var interactions = await Watch<InteractionsDto>.OpenAsync(_owner, ConversationMethods.InteractionsTopic);
        var start = await _rig.StartAsync(_owner, session.Id, "save a note");
        var run = await RunWatch.OpenAsync(_owner, start.RunId);
        await interactions.WaitAsync(() => interactions.Decoded<InteractionsDto>(InteractionEvents.Changed).Any(s => s.Pending.Length == 1), "pending approval");
        var pending = interactions.Decoded<InteractionsDto>(InteractionEvents.Changed).Last(s => s.Pending.Length == 1).Pending[0];
        var permission = Assert.IsType<ToolPermissionInteractionDto>(pending);
        Assert.Equal("plugin__acme_writer__save", permission.ToolName);
        Assert.False(File.Exists(Path.Combine(_rig.WorkspaceDir, "notes", "a.txt"))); // 批准之前什么都没发生

        await _owner.CallAsync(ConversationMethods.InteractionsRespond, new InteractionsRespondParams(pending.Id, pending.Version, new ToolPermissionResponseDto(PermissionChoices.AllowOnce)));
        await run.SettledAsync();

        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(_rig.WorkspaceDir, "notes", "a.txt")));
        Assert.Equal("saved notes/a.txt", TextOf(_rig.Llm.Requests[1].Messages).Trim());
    }

    [Fact]
    public async Task Revoking_an_optional_permission_makes_the_tool_stop_working_immediately_without_restarting_the_plugin()
    {
        var project = await _rig.AddProjectAsync(_owner);
        await File.WriteAllTextAsync(Path.Combine(_rig.WorkspaceDir, "peek.txt"), "visible");
        await InstallAsync(_owner, InlineZip("peeker", """
            import * as ncw from 'nextcowork'
            export function activate(c) {
              c.subscriptions.push(ncw.tools.registerTool('peek', { description: 'reads peek.txt', inputSchema: { type: 'object' }, readOnly: true, async invoke() { return (await ncw.workspace.fs.readFile('peek.txt')).data } }))
            }
            """, """ "optionalPermissions": ["workspace.read"], "contributes": { "tools": [{ "name": "peek", "title": "%t%" }] } """), "workspace.read");
        var session = await _rig.CreateSessionAsync(_owner, project);

        _rig.Llm.Then(Up.Calls(("c1", "plugin__acme_peeker__peek", "{}"))).Then(Up.Say("one"));
        await (await RunWatch.OpenAsync(_owner, (await _rig.StartAsync(_owner, session.Id, "peek")).RunId)).SettledAsync();
        Assert.Equal("visible", TextOf(_rig.Llm.Requests[1].Messages).Trim());

        await _owner.CallAsync(PluginsMethods.RevokePermissions, new PluginsPermissionsParams("acme.peeker", ["workspace.read"]), idem: Guid.NewGuid().ToString("N"));
        Assert.Equal(PluginStatuses.Active, (await _owner.CallAsync(PluginsMethods.List, Unit.Value)).Plugins.Single().Status); // 可选能力被撤:插件继续在线

        _rig.Llm.Then(Up.Calls(("c2", "plugin__acme_peeker__peek", "{}"))).Then(Up.Say("two"));
        await (await RunWatch.OpenAsync(_owner, (await _rig.StartAsync(_owner, session.Id, "peek again")).RunId)).SettledAsync();
        var second = _rig.Llm.Requests[3].Messages.SelectMany(m => m.Parts).OfType<ToolResultPart>().Last();
        Assert.True(second.IsError);
        Assert.Contains("permission", second.Output.Content, StringComparison.OrdinalIgnoreCase);
        var activity = await _owner.CallAsync(PluginsMethods.Activity, new PluginsActivityParams("acme.peeker"));
        Assert.Contains(activity.Entries, a => a.Method == "workspace.readFile" && a.Verdict == "denied");
    }

    [Fact]
    public async Task Disabling_and_uninstalling_remove_the_tool_from_the_list_and_the_package_from_disk()
    {
        await InstallAsync(_owner, FixtureZip("acme.think"));
        Assert.Contains((await _owner.CallAsync(ConversationMethods.ToolsList, new ToolsListParams())).Tools, t => t.Name == "plugin__acme_think__think");

        await _owner.CallAsync(PluginsMethods.SetEnabled, new PluginsSetEnabledParams("acme.think", false), idem: Guid.NewGuid().ToString("N"));
        Assert.DoesNotContain((await _owner.CallAsync(ConversationMethods.ToolsList, new ToolsListParams())).Tools, t => t.Name == "plugin__acme_think__think");

        await _owner.CallAsync(PluginsMethods.SetEnabled, new PluginsSetEnabledParams("acme.think", true), idem: Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(_rig.DataRoot, "plugins", "acme.think");
        Assert.True(Directory.Exists(dir));
        var state = await _owner.CallAsync(PluginsMethods.Uninstall, new PluginsUninstallParams("acme.think"), idem: Guid.NewGuid().ToString("N"));

        Assert.Empty(state.Plugins);
        Assert.False(Directory.Exists(dir));
        Assert.DoesNotContain((await _owner.CallAsync(ConversationMethods.ToolsList, new ToolsListParams())).Tools, t => t.Name == "plugin__acme_think__think");
        var gone = await Assert.ThrowsAsync<ProtocolException>(() => _owner.CallAsync(PluginsMethods.Uninstall, new PluginsUninstallParams("acme.think"), idem: Guid.NewGuid().ToString("N")));
        Assert.Equal((ErrorCodes.NotFound, PluginErrorKeys.NotFound), (gone.Code, KeyOf(gone)));
    }

    // ───────────────────────── 贡献:Skill / 拦截器 / 上下文 ─────────────────────────

    [Fact]
    public async Task A_plugins_skill_interceptor_and_context_all_reach_the_conversation()
    {
        var project = await _rig.AddProjectAsync(_owner);
        await InstallAsync(_owner, FixtureZip("acme.guard"), "agent.intercept", "agent.context");

        // Skill:出现在清单里(来源是插件,界面据此藏掉卸载按钮),进系统提示词的目录
        var skills = await _owner.CallAsync(SkillsMethods.List, new SkillsScopeParams(project.WorkspaceId));
        var skill = skills.Skills.Single(s => s.Name == "guard-style");
        Assert.Equal((SkillSourceKinds.Plugin, "acme.guard"), (skill.SourceKind, skill.PluginId));

        var session = await _rig.CreateSessionAsync(_owner, project);
        _rig.Llm.Then(Up.Calls(("c1", "Bash", """{"command":"rm -rf /tmp/never-run","description":"danger"}"""))).Then(Up.Say("understood"));
        await (await RunWatch.OpenAsync(_owner, (await _rig.StartAsync(_owner, session.Id, "clean up", permission: "full")).RunId)).SettledAsync();

        var first = _rig.Llm.Requests[0];
        Assert.Contains("# Available Skills", first.System);
        Assert.Contains("guard-style", first.System);
        Assert.Contains("<plugin-context source=\"acme.guard\">", AllText(first.Messages)); // 上下文提供者的一段:强制包裹,模型看得出它来自插件
        Assert.Contains("Guard is active for session", AllText(first.Messages));
        // 拦截器:即使 full 档位,插件也能拦下(只能收紧);工具没被执行,模型拿到的是拒绝原因
        var verdict = _rig.Llm.Requests[1].Messages.SelectMany(m => m.Parts).OfType<ToolResultPart>().Last();
        Assert.True(verdict.IsError);
        Assert.Contains("blocked this tool call", verdict.Output.Content, StringComparison.Ordinal);
        Assert.Contains("acme.guard", verdict.Output.Content, StringComparison.Ordinal);

        // 卸载:Skill 随之消失
        await _owner.CallAsync(PluginsMethods.Uninstall, new PluginsUninstallParams("acme.guard"), idem: Guid.NewGuid().ToString("N"));
        Assert.DoesNotContain((await _owner.CallAsync(SkillsMethods.List, new SkillsScopeParams(project.WorkspaceId))).Skills, s => s.Name == "guard-style");
    }

    // ───────────────────────── 隔离 / 命令执行审批 ─────────────────────────

    [Fact]
    public async Task A_busy_loop_plugin_is_stopped_and_faulted_while_the_agent_keeps_serving()
    {
        await InstallAsync(_owner, InlineZip("spin", """
            import * as ncw from 'nextcowork'
            export function activate(c) { c.subscriptions.push(ncw.commands.registerCommand('acme.spin.go', async () => { while (true) {} })) }
            """, """ "contributes": { "commands": [{ "command": "acme.spin.go", "title": "%a%" }] } """));
        await InstallAsync(_owner, FixtureZip("acme.think"));

        var e = await Assert.ThrowsAsync<ProtocolException>(() => _owner.CallAsync(PluginsMethods.RunCommand, new PluginsRunCommandParams("acme.spin", "acme.spin.go", null, null), idem: Guid.NewGuid().ToString("N")));

        Assert.Equal(PluginErrorKeys.Faulted, KeyOf(e));
        var state = await _owner.CallAsync(PluginsMethods.List, Unit.Value);
        var spin = state.Plugins.Single(p => p.Id == "acme.spin");
        Assert.Equal(PluginStatuses.Faulted, spin.Status);
        Assert.Contains(spin.Diagnostics, d => d.Key == PluginDiagnosticKeys.Faulted && d.Params!["reason"] == "instructions");
        Assert.Equal(PluginStatuses.Active, state.Plugins.Single(p => p.Id == "acme.think").Status);
        Assert.Contains((await _owner.CallAsync(ConversationMethods.ToolsList, new ToolsListParams())).Tools, t => t.Name == "plugin__acme_think__think");
        Assert.NotNull((await _owner.CallAsync(DeviceMethods.Info, Unit.Value)).DeviceId);
    }

    [Fact]
    public async Task A_plugin_that_runs_a_command_asks_through_the_plugins_topic_with_the_execution_environment_and_a_member_answers()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The command needs a POSIX shell environment.");
        var project = await _rig.AddProjectAsync(_owner);
        await InstallAsync(_owner, FixtureZip("acme.hello"), "process");
        var topic = await Watch<PluginsTopicState>.OpenAsync(_owner, PluginsMethods.Topic);
        var member = await ConnectRemoteAsync("member-1", "member");

        var command = _owner.CallAsync(PluginsMethods.RunCommand, new PluginsRunCommandParams("acme.hello", "acme.hello.exec", """{"command":"echo","args":["from the plugin"]}""", project.WorkspaceId), idem: Guid.NewGuid().ToString("N"));
        await topic.WaitAsync(() => topic.Decoded<PluginsTopicState>(PluginsEvents.Changed).Any(s => s.Interactions.Length == 1), "the exec prompt");
        var prompt = topic.Decoded<PluginsTopicState>(PluginsEvents.Changed).Last(s => s.Interactions.Length == 1).Interactions[0];
        Assert.Equal((PluginInteractionKindNames.Exec, "echo 'from the plugin'", true), (prompt.Kind, prompt.Command, prompt.Danger));
        Assert.Equal(_rig.Host.DeviceId, prompt.Environment!.DeviceId); // 涉及执行的审批带设备 + 环境
        Assert.Equal("local", prompt.Environment.Environment);

        await member.CallAsync(PluginsMethods.InteractionReply, new PluginsInteractionReplyParams(prompt.RequestId, true, null, null), idem: Guid.NewGuid().ToString("N"));
        await command;

        var stale = await Assert.ThrowsAsync<ProtocolException>(() => member.CallAsync(PluginsMethods.InteractionReply, new PluginsInteractionReplyParams(prompt.RequestId, true, null, null), idem: Guid.NewGuid().ToString("N")));
        Assert.Equal(PluginErrorKeys.InteractionNotFound, KeyOf(stale)); // 已经被回答过了
    }

    // ───────────────────────── 配置 / 市场 / 角色 ─────────────────────────

    [Fact]
    public async Task Configuration_roundtrips_and_market_browsing_works_but_installing_without_an_account_says_auth_required()
    {
        await InstallAsync(_owner, FixtureZip("acme.hello"));

        var defaults = await _owner.CallAsync(PluginsMethods.GetConfiguration, new PluginsConfigurationParams("acme.hello"));
        Assert.Equal("Hello", defaults.Values["greeting"].Text);
        var set = await _owner.CallAsync(PluginsMethods.SetConfiguration, new PluginsSetConfigurationParams("acme.hello", "loud", new PluginConfigValueDto(null, null, true)), idem: Guid.NewGuid().ToString("N"));
        Assert.True(set.Values["loud"].Flag);
        var bad = await Assert.ThrowsAsync<ProtocolException>(() => _owner.CallAsync(PluginsMethods.SetConfiguration, new PluginsSetConfigurationParams("acme.hello", "loud", new PluginConfigValueDto("yes", null, null)), idem: Guid.NewGuid().ToString("N")));
        Assert.Equal((ErrorCodes.InvalidRequest, PluginErrorKeys.ConfigurationInvalid), (bad.Code, KeyOf(bad)));

        var page = await _owner.CallAsync(PluginsMethods.MarketList, new PluginsMarketListParams(null, null, 1));
        Assert.Equal(["net"], page.Items.Single().Permissions);
        Assert.Equal(["docs"], (await _owner.CallAsync(PluginsMethods.MarketCategories, Unit.Value)).Categories);
        Assert.Equal("pdf", (await _owner.CallAsync(PluginsMethods.MarketDetail, new PluginsMarketDetailParams("pdf"))).Item.Slug);
        var auth = await Assert.ThrowsAsync<ProtocolException>(() => _owner.CallAsync(PluginsMethods.InstallMarket, new PluginsInstallMarketParams("pdf", null), idem: Guid.NewGuid().ToString("N")));
        Assert.Equal((ErrorCodes.Unauthorized, PluginErrorKeys.AuthRequired), (auth.Code, KeyOf(auth)));
    }

    [Fact]
    public async Task Roles_viewers_see_nothing_members_browse_and_respond_owners_install_and_decide()
    {
        var project = await _rig.AddProjectAsync(_owner);
        await InstallAsync(_owner, FixtureZip("acme.hello"));
        var viewer = await ConnectRemoteAsync("viewer-1", "viewer");
        var member = await ConnectRemoteAsync("member-1", "member");
        var zip = FixtureZip("acme.think");

        // viewer:什么都不给(清单里有命令白名单 / 域名 / 诊断,活动日志里有插件做过什么)
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => viewer.CallAsync(PluginsMethods.List, Unit.Value)));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => viewer.CallAsync(PluginsMethods.Activity, new PluginsActivityParams(null))));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => viewer.CallAsync(PluginsMethods.MarketList, new PluginsMarketListParams(null, null, 1))));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => Watch<PluginsTopicState>.OpenAsync(viewer, PluginsMethods.Topic)));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => viewer.CallAsync(PluginsMethods.RunCommand, new PluginsRunCommandParams("acme.hello", "acme.hello.hello", null, null), idem: "v1")));

        // member:读、浏览、运行命令、回应;不能装 / 启停 / 授权 / 卸载 / 改配置
        Assert.Single((await member.CallAsync(PluginsMethods.List, Unit.Value)).Plugins);
        Assert.Single((await member.CallAsync(PluginsMethods.MarketList, new PluginsMarketListParams(null, null, 1))).Items);
        await member.CallAsync(PluginsMethods.RunCommand, new PluginsRunCommandParams("acme.hello", "acme.hello.hello", null, project.WorkspaceId), idem: Guid.NewGuid().ToString("N"));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => member.CallAsync(PluginsMethods.InstallPackage, new PluginsInstallPackageParams(zip, null), idem: "m1")));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => member.CallAsync(PluginsMethods.InstallMarket, new PluginsInstallMarketParams("pdf", null), idem: "m2")));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => member.CallAsync(PluginsMethods.SetEnabled, new PluginsSetEnabledParams("acme.hello", false), idem: "m3")));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => member.CallAsync(PluginsMethods.GrantPermissions, new PluginsPermissionsParams("acme.hello", ["net"]), idem: "m4")));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => member.CallAsync(PluginsMethods.Uninstall, new PluginsUninstallParams("acme.hello"), idem: "m5")));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => member.CallAsync(PluginsMethods.SetConfiguration, new PluginsSetConfigurationParams("acme.hello", "loud", null), idem: "m6")));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => member.CallAsync(PluginsMethods.UpdateAll, Unit.Value, idem: "m7")));

        // owner:都行
        var installed = await _owner.CallAsync(PluginsMethods.InstallPackage, new PluginsInstallPackageParams(zip, null), idem: Guid.NewGuid().ToString("N"));
        Assert.Equal("acme.think", installed.Installed.Id);
        Assert.Equal(2, installed.State.Plugins.Length);
    }

    [Fact]
    public async Task Hostile_and_oversized_packages_are_refused_with_stable_keys_and_nothing_is_installed()
    {
        var empty = await Assert.ThrowsAsync<ProtocolException>(() => _owner.CallAsync(PluginsMethods.InstallPackage, new PluginsInstallPackageParams([], null), idem: Guid.NewGuid().ToString("N")));
        Assert.Equal((ErrorCodes.TooLarge, PluginErrorKeys.PackageTooLarge), (empty.Code, KeyOf(empty)));

        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var w = new StreamWriter(zip.CreateEntry("acme.evil/../../escape.txt").Open());
            w.Write("x");
        }

        var evil = await Assert.ThrowsAsync<ProtocolException>(() => _owner.CallAsync(PluginsMethods.InstallPackage, new PluginsInstallPackageParams(memory.ToArray(), null), idem: Guid.NewGuid().ToString("N")));
        Assert.Equal((ErrorCodes.InvalidRequest, PluginErrorKeys.ZipInvalid), (evil.Code, KeyOf(evil)));
        var notZip = await Assert.ThrowsAsync<ProtocolException>(() => _owner.CallAsync(PluginsMethods.InstallPackage, new PluginsInstallPackageParams(Encoding.UTF8.GetBytes("nope"), null), idem: Guid.NewGuid().ToString("N")));
        Assert.Equal(PluginErrorKeys.ZipInvalid, KeyOf(notZip));
        Assert.Empty((await _owner.CallAsync(PluginsMethods.List, Unit.Value)).Plugins);
        Assert.False(File.Exists(Path.Combine(_rig.DataRoot, "escape.txt")));
    }
}
