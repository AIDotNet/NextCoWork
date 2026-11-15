using System.Text.Json;
using Ncw.Agent.Extensions.Permissions;
using Ncw.Agent.Kernel.Extensions;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Skills;

namespace Ncw.Agent.Plugins.Tests;

/// <summary>技能 / 钩子贡献型插件(acme.guard):贡献 Skill(<c>ISkillPluginSource</c>)、工具拦截器(只能收紧)、上下文提供者(强制包裹、限长)。</summary>
public sealed class ContributionTests : IAsyncDisposable
{
    private readonly PluginFx _fx = new(b => b.Limits = new PluginRuntimeLimits { HookTimeout = TimeSpan.FromMilliseconds(500), ToolTimeout = TimeSpan.FromSeconds(3), ActivateTimeout = TimeSpan.FromSeconds(3) });

    public ValueTask DisposeAsync() => _fx.DisposeAsync();

    private Task<InstalledPlugin> GuardAsync() => _fx.InstallEnableAsync(PluginFx.FixtureZip("acme.guard"));

    private async Task<SkillScanResult> ScanSkillsAsync() =>
        await SkillLoader.ScanAsync(new SkillScanInput(null, null, null, null, _fx.Manager.SkillSource.Current()));

    private static PreToolContext Call(string toolInternalId, string inputJson) =>
        new(null!, new ToolInvocation("c1", toolInternalId, new NoopTool(toolInternalId), JsonDocument.Parse(inputJson).RootElement.Clone()), JsonDocument.Parse(inputJson).RootElement.Clone());

    // ───────────────────────── Skills ─────────────────────────

    [Fact]
    public async Task An_enabled_plugin_contributes_its_declared_skill_with_the_lowest_priority_and_the_plugin_as_source()
    {
        await GuardAsync();

        var scan = await ScanSkillsAsync();

        var skill = Assert.Single(scan.Skills);
        Assert.Equal("guard-style", skill.Name);
        Assert.Equal((SkillScope.Plugin, SkillSourceKind.Plugin, "acme.guard"), (skill.Scope, skill.Source.Kind, skill.Source.PluginId));
        Assert.Contains("imperative mood", skill.Body, StringComparison.Ordinal);
        Assert.Empty(scan.Diagnostics);
    }

    [Fact]
    public async Task A_disabled_or_pending_or_uninstalled_plugin_contributes_nothing_and_a_new_install_is_not_enabled_yet()
    {
        var installed = await _fx.Manager.InstallZipAsync(PluginFx.FixtureZip("acme.guard"));
        Assert.Empty((await ScanSkillsAsync()).Skills); // 还没启用

        await _fx.Manager.GrantAsync(installed.Id, ["agent.intercept", "agent.context"]);
        await _fx.Manager.SetEnabledAsync(installed.Id, true);
        Assert.Single((await ScanSkillsAsync()).Skills);

        await _fx.Manager.SetEnabledAsync(installed.Id, false);
        Assert.Empty((await ScanSkillsAsync()).Skills); // 每次扫描都重新问:下一个 Run 模型就不该再看见

        await _fx.Manager.SetEnabledAsync(installed.Id, true);
        await _fx.Manager.UninstallAsync(installed.Id);
        Assert.Empty((await ScanSkillsAsync()).Skills);
    }

    [Fact]
    public async Task Only_skills_the_manifest_declares_are_contributed()
    {
        await _fx.InstallEnableAsync(PluginFx.FixtureZip("acme.guard", null, ("skills/undeclared/SKILL.md", "---\ndescription: sneaky\n---\nbody\n")));

        var scan = await ScanSkillsAsync();

        Assert.Equal(["guard-style"], scan.Skills.Select(s => s.Name));
    }

    [Fact]
    public async Task A_users_own_skill_overrides_the_plugins_with_the_same_name()
    {
        await GuardAsync();
        var userRoot = Path.Combine(_fx.Root, "global", "skills");
        Directory.CreateDirectory(Path.Combine(userRoot, "guard-style"));
        await File.WriteAllTextAsync(Path.Combine(userRoot, "guard-style", "SKILL.md"), "---\ndescription: mine\n---\nmy rules\n");

        var scan = await SkillLoader.ScanAsync(new SkillScanInput(_fx.Env, _fx.P(userRoot), null, null, _fx.Manager.SkillSource.Current()));

        var skill = Assert.Single(scan.Skills);
        Assert.Equal((SkillScope.Global, "my rules"), (skill.Scope, skill.Body.Trim()));
    }

    // ───────────────────────── 拦截器 ─────────────────────────

    [Fact]
    public async Task The_interceptor_can_veto_or_ask_but_an_allow_is_an_abstention_it_cannot_open_the_door()
    {
        await GuardAsync();
        var interceptor = _fx.Manager.Interceptor;

        var deny = await interceptor.InterceptAsync(Call("Bash", """{"command":"rm -rf /tmp/x"}"""), default);
        var ask = await interceptor.InterceptAsync(Call("Write", """{"file_path":"/a"}"""), default);
        var allow = await interceptor.InterceptAsync(Call("Read", """{"file_path":"/a"}"""), default);
        var abstain = await interceptor.InterceptAsync(Call("Glob", """{"pattern":"*"}"""), default);
        var harmless = await interceptor.InterceptAsync(Call("Bash", """{"command":"ls"}"""), default);

        Assert.Contains("blocked this tool call", deny.Deny, StringComparison.Ordinal);
        Assert.Contains("acme.guard", deny.Deny, StringComparison.Ordinal);
        Assert.Contains("plugin.acme.guard.denied", deny.Deny, StringComparison.Ordinal);
        Assert.True(ask.Ask);
        Assert.Equal(InterceptorVerdict.None, allow); // 类型上就没有 allow:返回 allow = 弃权,宿主自己的权限链照走
        Assert.Equal(InterceptorVerdict.None, abstain);
        Assert.Equal(InterceptorVerdict.None, harmless);
    }

    [Fact]
    public async Task The_interceptor_stops_working_the_moment_the_permission_is_revoked_or_the_plugin_is_disabled()
    {
        await GuardAsync();
        Assert.NotNull((await _fx.Manager.Interceptor.InterceptAsync(Call("Bash", """{"command":"rm -rf x"}"""), default)).Deny);

        await _fx.Manager.RevokeAsync("acme.guard", ["agent.intercept"]); // 必选能力被撤:插件停了

        Assert.Equal(InterceptorVerdict.None, await _fx.Manager.Interceptor.InterceptAsync(Call("Bash", """{"command":"rm -rf x"}"""), default));
        Assert.Equal(PluginStatus.Disabled, _fx.Plugin("acme.guard").Status);
    }

    [Fact]
    public async Task Registering_a_hook_without_its_permission_is_refused_with_a_diagnostic_and_no_hook_runs()
    {
        var zip = PluginFx.MinimalZip("sneak", """
            import * as ncw from 'nextcowork'
            export function activate(c) {
              c.subscriptions.push(ncw.agent.registerToolInterceptor(async () => ({ decision: 'deny' })))
            }
            """, """ "optionalPermissions": ["agent.intercept"] """);
        await _fx.InstallEnableAsync(zip); // 清单声明了可选的 agent.intercept,但用户没批

        var verdict = await _fx.Manager.Interceptor.InterceptAsync(Call("Bash", """{"command":"ls"}"""), default);

        Assert.Equal(InterceptorVerdict.None, verdict);
        Assert.Contains(_fx.Plugin("acme.sneak").Diagnostics, d => d.Key == PluginDiagnosticKeys.PermissionDenied && d.Params!["permission"] == "agent.intercept");
    }

    [Fact]
    public async Task A_slow_or_broken_interceptor_abstains_records_a_diagnostic_and_never_blocks_the_call()
    {
        var zip = PluginFx.MinimalZip("slowpoke", """
            import * as ncw from 'nextcowork'
            export function activate(c) {
              c.subscriptions.push(ncw.agent.registerToolInterceptor(async ({ input }) => {
                if (input.mode === 'throw') throw new Error('policy engine down')
                if (input.mode === 'spin') { while (true) {} }
                await new Promise((resolve) => setTimeout(resolve, 5000))
                return { decision: 'deny' }
              }))
            }
            """, """ "permissions": ["agent.intercept"] """);
        await _fx.InstallEnableAsync(zip);

        var slow = await _fx.Manager.Interceptor.InterceptAsync(Call("Bash", """{"mode":"slow"}"""), default); // 3 秒预算(这里 0.5 秒):超时 = 弃权
        var thrown = await _fx.Manager.Interceptor.InterceptAsync(Call("Bash", """{"mode":"throw"}"""), default);

        Assert.Equal(InterceptorVerdict.None, slow);
        Assert.Equal(InterceptorVerdict.None, thrown);
        var diagnostics = _fx.Plugin("acme.slowpoke").Diagnostics;
        Assert.Contains(diagnostics, d => d.Key == PluginDiagnosticKeys.InterceptorTimeout);
        Assert.Contains(diagnostics, d => d.Key == PluginDiagnosticKeys.InterceptorFailed && d.Message.Contains("policy engine down", StringComparison.Ordinal));
        Assert.Equal(PluginStatus.Active, _fx.Plugin("acme.slowpoke").Status);

        // 死循环的拦截器:弃权,并且插件因超预算被标成 faulted(不拖垮宿主)
        var spin = await _fx.Manager.Interceptor.InterceptAsync(Call("Bash", """{"mode":"spin"}"""), default);
        Assert.Equal(InterceptorVerdict.None, spin);
        Assert.Equal(PluginStatus.Faulted, _fx.Plugin("acme.slowpoke").Status);
    }

    // ───────────────────────── 上下文 ─────────────────────────

    [Fact]
    public async Task The_context_provider_adds_a_wrapped_section_to_the_head_of_each_run()
    {
        await GuardAsync();
        var extension = _fx.Manager.CreateExtension();
        var host = new ExtensionHost([extension]);
        var draft = new RunDraft(new RunRequest(new Ncw.Agent.Kernel.RunId("run-1"), [], Ncw.Agent.Llm.ModelRef.Unpinned("m")));

        await host.MutateAsync(SessionExtensionPoints.BeforeRun, draft, new ExtensionContext(new Ncw.Agent.Kernel.RunId("run-1"), "session-9", Ncw.Agent.Kernel.Loop.NullLoopDiagnostics.Instance), default);

        var section = Assert.Single(draft.HeadSections);
        Assert.StartsWith("<plugin-context source=\"acme.guard\">\n", section, StringComparison.Ordinal);
        Assert.Contains("Guard is active for session session-9", section, StringComparison.Ordinal);
        Assert.EndsWith("\n</plugin-context>", section, StringComparison.Ordinal);
    }

    [Fact]
    public void Context_is_wrapped_stripped_of_control_characters_and_clipped()
    {
        var wrapped = HostGuards.WrapContext("acme.x", "  hello\u0007 \u001bworld  " + new string('z', 3000), 100);

        Assert.StartsWith("<plugin-context source=\"acme.x\">\nhello world", wrapped, StringComparison.Ordinal);
        Assert.Contains("…\n</plugin-context>", wrapped, StringComparison.Ordinal);
        Assert.True(wrapped.Length < 200);
        Assert.Equal("", HostGuards.WrapContext("acme.x", "   \u0001  ", 100)); // 空的不注入
    }

    // ───────────────────────── 来源 / 版本 ─────────────────────────

    [Fact]
    public async Task Upgrading_a_plugin_that_asks_for_new_required_permissions_waits_for_approval_and_does_not_run()
    {
        await _fx.InstallEnableAsync(PluginFx.FixtureZip("acme.think"));
        Assert.Equal(PluginStatus.Active, _fx.Plugin("acme.think").Status);

        var upgraded = await _fx.Manager.InstallZipAsync(PluginFx.FixtureZipWithManifest("acme.think", m => m.Replace("\"permissions\": []", "\"permissions\": [\"net\"], \"hostPermissions\": [\"https://x.example.com/*\"]", StringComparison.Ordinal).Replace("\"version\": \"0.1.0\"", "\"version\": \"0.2.0\"", StringComparison.Ordinal)));

        Assert.Equal(PluginStatus.PendingApproval, upgraded.Status);
        Assert.Equal(["net"], upgraded.PendingPermissions); // 授权界面逐条列出新增了哪几条
        Assert.Null(_fx.Registry.Find("plugin__acme_think__think")); // 不批不激活
        Assert.Contains(upgraded.Diagnostics, d => d.Key == PluginDiagnosticKeys.PendingApproval);
        var stillPending = await _fx.Manager.SetEnabledAsync("acme.think", true);
        Assert.Equal(PluginStatus.PendingApproval, stillPending.Status); // 绕过界面直接启用也挡得住

        var approved = await _fx.Manager.GrantAsync("acme.think", ["net"]);
        Assert.Equal(PluginStatus.Active, approved.Status); // 批了之后解除待批准,启用着的随即激活
        Assert.NotNull(_fx.Registry.Find("plugin__acme_think__think"));
    }

    [Fact]
    public async Task An_upgrade_that_asks_for_nothing_new_keeps_the_users_enable_and_grants()
    {
        await _fx.InstallEnableAsync(PluginFx.FixtureZip("acme.hello"), "workspace.read");

        var upgraded = await _fx.Manager.InstallZipAsync(PluginFx.FixtureZipWithManifest("acme.hello", m => m.Replace("\"version\": \"0.1.0\"", "\"version\": \"0.1.1\"", StringComparison.Ordinal)));

        Assert.Equal(("0.1.1", PluginStatus.Active, true), (upgraded.Manifest.Version, upgraded.Status, upgraded.Enabled));
        Assert.Equal(["workspace.read", "storage"], upgraded.Permissions.Granted);
    }

    private sealed class NoopTool(string id) : IAgentTool
    {
        public ToolSpec Info { get; } = new(id, "d", JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone(), new ToolTraits(false, true, false));

        public ValueTask<ToolResult> ExecuteAsync(ToolInvocation call, ToolContext ctx, CancellationToken ct) => ValueTask.FromResult(ToolResult.Text("x"));
    }
}
