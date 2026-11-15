using System.Text.Json;

namespace Ncw.Agent.Plugins.Tests;

/// <summary>命令型插件(acme.hello,移植自 2.x 的脚手架模板):安装 → 授权 → 启用 → 激活 → 命令出现 → 调用;配置;存储;状态与持久化。</summary>
public sealed class CommandPluginTests : IAsyncDisposable
{
    private readonly PluginFx _fx = new();

    public ValueTask DisposeAsync() => _fx.DisposeAsync();

    private Task<InstalledPlugin> HelloAsync(params string[] grant) => _fx.InstallEnableAsync(PluginFx.FixtureZip("acme.hello"), grant);

    [Fact]
    public async Task A_fresh_install_is_disabled_with_nothing_granted_and_nothing_running()
    {
        var installed = await _fx.Manager.InstallZipAsync(PluginFx.FixtureZip("acme.hello"));

        Assert.Equal(PluginStatus.Disabled, installed.Status);
        Assert.False(installed.Enabled);
        Assert.Empty(installed.Permissions.Granted);
        Assert.Equal(["storage"], installed.PendingPermissions); // 必选能力还没批
        Assert.Empty(installed.RegisteredCommands);
    }

    [Fact]
    public async Task Enabling_without_approving_the_required_permissions_leaves_it_pending_approval()
    {
        var installed = await _fx.Manager.InstallZipAsync(PluginFx.FixtureZip("acme.hello"));

        var result = await _fx.Manager.SetEnabledAsync(installed.Id, true);

        Assert.Equal(PluginStatus.PendingApproval, result.Status);
        Assert.False(result.Enabled);
    }

    [Fact]
    public async Task Granting_then_enabling_activates_the_plugin_and_its_declared_commands_appear()
    {
        var plugin = await HelloAsync();

        Assert.Equal(PluginStatus.Active, plugin.Status);
        Assert.True(plugin.Enabled);
        Assert.Equal(
            ["acme.hello.ask", "acme.hello.config", "acme.hello.exec", "acme.hello.fetch", "acme.hello.hello", "acme.hello.perm", "acme.hello.read", "acme.hello.write"],
            plugin.RegisteredCommands);
        Assert.Equal(["storage"], plugin.Permissions.Granted);
    }

    [Fact]
    public async Task Running_a_command_executes_the_plugins_js_with_the_host_call_context_and_its_storage_persists()
    {
        await HelloAsync();

        await _fx.Manager.RunCommandAsync("acme.hello", "acme.hello.hello", null, "w1");
        await _fx.Manager.RunCommandAsync("acme.hello", "acme.hello.hello", null, "w1");

        Assert.Equal("2", _fx.Stored("acme.hello", "greetings")); // JS 里的计数器活在长驻的引擎里
        Assert.Equal("w1", _fx.Stored("acme.hello", "workspaceId")); // 宿主调用点上下文固定带 workspaceId
        Assert.Equal("Hello", _fx.Stored("acme.hello", "greeting")); // 配置的默认值
        Assert.Equal("permission_denied", _fx.Stored("acme.hello", "notified")); // window.notify 没批:JS 看到稳定的 Error.code
    }

    [Fact]
    public async Task Calling_the_host_without_the_granted_permission_is_denied_and_leaves_a_diagnostic_and_an_activity_entry()
    {
        await HelloAsync(); // 只批了 storage
        await File.WriteAllTextAsync(Path.Combine(_fx.ProjectDir, "notes.txt"), "secret");

        await _fx.Manager.RunCommandAsync("acme.hello", "acme.hello.read", """{"path":"notes.txt"}""", "w1");

        Assert.Contains("permission_denied", _fx.Stored("acme.hello", "last.read.error"));
        var plugin = _fx.Plugin("acme.hello");
        var diagnostic = Assert.Single(plugin.Diagnostics, d => d.Key == PluginDiagnosticKeys.PermissionDenied);
        Assert.Equal("workspace.read", diagnostic.Params!["permission"]);
        Assert.Equal("workspace.readFile", diagnostic.Params["method"]);
        var activity = Assert.Single(_fx.Manager.Activity("acme.hello"), a => a.Method == "workspace.readFile");
        Assert.Equal("denied", activity.Verdict);
        Assert.DoesNotContain("notes.txt", activity.Summary, StringComparison.Ordinal); // 活动日志不记参数原文
    }

    [Fact]
    public async Task Once_granted_the_same_call_works_and_revoking_takes_effect_immediately_without_restarting_the_engine()
    {
        await HelloAsync("workspace.read");
        await File.WriteAllTextAsync(Path.Combine(_fx.ProjectDir, "notes.txt"), "hello from the project");

        await _fx.Manager.RunCommandAsync("acme.hello", "acme.hello.read", """{"path":"notes.txt"}""", "w1");
        Assert.Equal("hello from the project", _fx.Stored("acme.hello", "last.read"));

        await _fx.Manager.RevokeAsync("acme.hello", ["workspace.read"]);
        Assert.Equal(PluginStatus.Active, _fx.Plugin("acme.hello").Status); // 可选能力被撤:插件继续跑

        await _fx.Manager.RunCommandAsync("acme.hello", "acme.hello.read", """{"path":"notes.txt"}""", "w1");
        Assert.Contains("permission_denied", _fx.Stored("acme.hello", "last.read.error"));
    }

    [Fact]
    public async Task Revoking_a_required_permission_stops_the_plugin()
    {
        await HelloAsync();

        var result = await _fx.Manager.RevokeAsync("acme.hello", ["storage"]);

        Assert.Equal(PluginStatus.Disabled, result.Status);
        Assert.False(result.Enabled);
        Assert.Empty(result.RegisteredCommands);
        await Assert.ThrowsAsync<PluginException>(() => _fx.Manager.RunCommandAsync("acme.hello", "acme.hello.hello", null, null));
    }

    [Fact]
    public async Task Grants_beyond_the_manifest_ceiling_are_silently_dropped()
    {
        var installed = await _fx.Manager.InstallZipAsync(PluginFx.FixtureZip("acme.hello"));

        var granted = await _fx.Manager.GrantAsync(installed.Id, ["storage", "scm.write", "agent.intercept", "bogus"]);

        Assert.Equal(["storage"], granted.Permissions.Granted); // 清单里的能力上界 = 运行期可获得的能力上界
    }

    [Fact]
    public async Task A_runtime_permission_request_outside_the_manifest_is_refused_without_a_prompt()
    {
        await HelloAsync();

        await _fx.Manager.RunCommandAsync("acme.hello", "acme.hello.perm", """{"permissions":["scm.write"]}""", "w1");

        Assert.Equal("false", _fx.Stored("acme.hello", "last.perm"));
        Assert.Empty(_fx.Manager.Snapshot().Interactions); // 越界:不弹窗
    }

    [Fact]
    public async Task A_runtime_permission_request_inside_the_manifest_asks_the_user_and_grants_on_yes()
    {
        await HelloAsync();
        var command = _fx.Manager.RunCommandAsync("acme.hello", "acme.hello.perm", """{"permissions":["workspace.read"]}""", "w1");

        await Wait.UntilAsync(() => _fx.Manager.Snapshot().Interactions.Count == 1, "the permission prompt");
        var prompt = _fx.Manager.Snapshot().Interactions.Single();
        Assert.Equal((PluginInteractionKinds.Permissions, "acme.hello"), (prompt.Kind, prompt.PluginId));
        Assert.Equal(["workspace.read"], prompt.Permissions);
        Assert.Equal("plugin.acme.hello.cmd.perm", prompt.TitleKey);
        _fx.Manager.ReplyInteraction(prompt.RequestId, new PluginInteractionReply(Confirmed: true));
        await command;

        Assert.Equal("true", _fx.Stored("acme.hello", "last.perm"));
        Assert.Contains("workspace.read", _fx.Plugin("acme.hello").Permissions.Granted);
        Assert.Empty(_fx.Manager.Snapshot().Interactions);
    }

    [Fact]
    public async Task Declining_or_ignoring_a_prompt_is_a_cancel_not_an_error()
    {
        await HelloAsync();
        var command = _fx.Manager.RunCommandAsync("acme.hello", "acme.hello.ask", null, "w1");
        await Wait.UntilAsync(() => _fx.Manager.Snapshot().Interactions.Count == 1, "the confirm prompt");

        _fx.Manager.ReplyInteraction(_fx.Manager.Snapshot().Interactions.Single().RequestId, new PluginInteractionReply());
        await command;

        Assert.Equal("false", _fx.Stored("acme.hello", "last.ask"));
        Assert.Throws<PluginException>(() => _fx.Manager.ReplyInteraction("nope", new PluginInteractionReply())); // 已经没有这个请求了
    }

    [Fact]
    public async Task Only_declared_and_registered_commands_can_run()
    {
        await HelloAsync();

        var undeclared = await Assert.ThrowsAsync<PluginException>(() => _fx.Manager.RunCommandAsync("acme.hello", "acme.hello.nope", null, null));
        Assert.Equal(PluginErrorKeys.CommandUndeclared, undeclared.Key);
        var missing = await Assert.ThrowsAsync<PluginException>(() => _fx.Manager.RunCommandAsync("acme.nobody", "x", null, null));
        Assert.Equal(PluginErrorKeys.NotFound, missing.Key);
        var badWorkspace = await Assert.ThrowsAsync<PluginException>(() => _fx.Manager.RunCommandAsync("acme.hello", "acme.hello.hello", null, "no-such-project"));
        Assert.Equal(PluginErrorKeys.InvalidArgument, badWorkspace.Key);
    }

    [Fact]
    public async Task A_command_the_plugin_throws_from_is_a_command_failure_with_the_reason_and_the_plugin_keeps_working()
    {
        var zip = PluginFx.MinimalZip("boom", """
            import * as ncw from 'nextcowork'
            export function activate(c) {
              c.subscriptions.push(ncw.commands.registerCommand('acme.boom.go', async () => { throw new Error('kaboom') }))
              c.subscriptions.push(ncw.commands.registerCommand('acme.boom.ok', async () => 'fine'))
            }
            """, """ "contributes": { "commands": [{ "command": "acme.boom.go", "title": "%a%" }, { "command": "acme.boom.ok", "title": "%b%" }] } """);
        await _fx.InstallEnableAsync(zip);

        var e = await Assert.ThrowsAsync<PluginException>(() => _fx.Manager.RunCommandAsync("acme.boom", "acme.boom.go", null, null));
        await _fx.Manager.RunCommandAsync("acme.boom", "acme.boom.ok", null, null);

        Assert.Equal(PluginErrorKeys.CommandFailed, e.Key);
        Assert.Contains("kaboom", e.Message, StringComparison.Ordinal);
        Assert.Equal(PluginStatus.Active, _fx.Plugin("acme.boom").Status); // 插件自己 throw 不是故障
    }

    [Fact]
    public async Task Registering_an_undeclared_command_is_ignored_with_a_diagnostic()
    {
        var zip = PluginFx.MinimalZip("sneaky", """
            import * as ncw from 'nextcowork'
            export function activate(c) { c.subscriptions.push(ncw.commands.registerCommand('someone.elses.command', async () => 1)) }
            """);

        await _fx.InstallEnableAsync(zip);

        var plugin = _fx.Plugin("acme.sneaky");
        Assert.Empty(plugin.RegisteredCommands);
        Assert.Contains(plugin.Diagnostics, d => d.Key == PluginDiagnosticKeys.CommandUndeclared);
    }

    [Fact]
    public async Task Configuration_merges_manifest_defaults_with_the_users_values_and_checks_types()
    {
        await HelloAsync();

        var defaults = _fx.Manager.GetConfiguration("acme.hello");
        Assert.Equal(("Hello", false, 3d, "safe"), (defaults["greeting"].Text, defaults["loud"].Flag, defaults["retries"].Number, defaults["mode"].Text));

        _fx.Manager.SetConfiguration("acme.hello", "greeting", PluginConfigValue.Of("Howdy"));
        _fx.Manager.SetConfiguration("acme.hello", "loud", PluginConfigValue.Of(true));
        _fx.Manager.SetConfiguration("acme.hello", "mode", PluginConfigValue.Of("fast"));
        await _fx.Manager.RunCommandAsync("acme.hello", "acme.hello.hello", null, "w1");
        Assert.Equal("HOWDY", _fx.Stored("acme.hello", "greeting")); // 插件经 configuration.get 读到的是用户的值

        Assert.Throws<PluginException>(() => _fx.Manager.SetConfiguration("acme.hello", "loud", PluginConfigValue.Of("yes"))); // 类型不对
        Assert.Throws<PluginException>(() => _fx.Manager.SetConfiguration("acme.hello", "mode", PluginConfigValue.Of("reckless"))); // 不在 enum 里
        Assert.Throws<PluginException>(() => _fx.Manager.SetConfiguration("acme.hello", "unknown", PluginConfigValue.Of("x")));

        var reset = _fx.Manager.SetConfiguration("acme.hello", "greeting", null);
        Assert.Equal("Hello", reset["greeting"].Text); // null = 恢复默认
    }

    [Fact]
    public async Task The_users_decisions_survive_a_restart_and_the_plugin_comes_back_active()
    {
        await HelloAsync("workspace.read");
        var kv = _fx.Kv;
        await _fx.Manager.DisposeAsync();

        await using var again = new PluginManager(new PluginManagerOptions
        {
            Environment = _fx.Env, Root = _fx.P(_fx.PluginRoot), Kv = kv, Tools = _fx.Registry, Secrets = _fx.Secrets, Http = _fx.Http, Workspaces = _fx.Workspaces, WorkspaceOfSession = _ => "w1",
        });
        await again.StartAsync();

        var plugin = again.Snapshot().Plugins.Single();
        Assert.Equal((PluginStatus.Active, true), (plugin.Status, plugin.Enabled));
        Assert.Equal(["workspace.read", "storage"], plugin.Permissions.Granted); // 按能力枚举里的书写顺序
        await again.RunCommandAsync("acme.hello", "acme.hello.hello", null, "w1");
        Assert.Equal("1", _fx.Stored("acme.hello", "greetings"));
    }

    [Fact]
    public async Task A_state_value_in_kv_that_cannot_be_read_does_not_stop_the_plugin_system_from_starting()
    {
        _fx.Kv.Set(PluginManager.StateKey, "{ this is not json");
        await _fx.Manager.StartAsync();

        Assert.Empty(_fx.Manager.Snapshot().Plugins);
    }

    [Fact]
    public async Task Snapshots_carry_a_monotonic_revision_and_every_change_is_published_in_order()
    {
        var seen = new List<long>();
        _fx.Manager.Changed += s => seen.Add(s.Rev);

        await HelloAsync();
        await _fx.Manager.RunCommandAsync("acme.hello", "acme.hello.hello", null, "w1");

        Assert.NotEmpty(seen);
        Assert.Equal(seen.Order(), seen);
        Assert.Equal(seen.Distinct().Count(), seen.Count);
        Assert.True(_fx.Manager.Snapshot().Rev >= seen.Max());
    }

    [Fact]
    public async Task Incompatible_engines_make_the_plugin_an_error_and_deprecated_ones_only_warn()
    {
        var incompatible = await _fx.Manager.InstallZipAsync(PluginFx.FixtureZipWithManifest("acme.hello", m => m.Replace("^0.3.0", "^0.9.0", StringComparison.Ordinal)));
        Assert.Equal(PluginStatus.Error, incompatible.Status);
        Assert.Contains(incompatible.Diagnostics, d => d.Key == PluginDiagnosticKeys.EngineIncompatible && d.Level == "error");
        await Assert.ThrowsAsync<PluginException>(() => _fx.Manager.SetEnabledAsync("acme.hello", true));

        var deprecated = await _fx.Manager.InstallZipAsync(PluginFx.FixtureZipWithManifest("acme.hello", m => m.Replace("^0.3.0", "^0.2.0", StringComparison.Ordinal)));
        Assert.NotEqual(PluginStatus.Error, deprecated.Status);
        Assert.Contains(deprecated.Diagnostics, d => d.Key == PluginDiagnosticKeys.EngineDeprecated && d.Level == "warn");
    }

    [Fact]
    public async Task Unsupported_ui_contributions_get_a_diagnostic_per_contribution_point()
    {
        var zip = PluginFx.MinimalZip("ui", "export function activate() {}", """ "contributes": { "views": [{ "id": "v", "title": "%t%", "path": "v.html" }], "customEditors": [] } """);

        var installed = await _fx.Manager.InstallZipAsync(zip);

        var unsupported = installed.Diagnostics.Where(d => d.Key == PluginDiagnosticKeys.Unsupported).Select(d => d.Params!["contribution"]).Order().ToList();
        Assert.Equal(["customEditors", "views"], unsupported);
    }

    [Fact]
    public async Task Uninstalling_takes_the_plugin_offline_first_then_removes_everything_it_left_behind()
    {
        await HelloAsync("secrets");
        await _fx.Manager.RunCommandAsync("acme.hello", "acme.hello.hello", null, "w1");
        _fx.Secrets.Values["plugin:acme.hello:token"] = "t";
        _fx.Kv.Set("plugins.kv.acme.hello", """{"a":"b"}""");

        await _fx.Manager.UninstallAsync("acme.hello");

        Assert.Empty(_fx.Manager.Snapshot().Plugins);
        Assert.False(Directory.Exists(Path.Combine(_fx.PluginRoot, "acme.hello")));
        Assert.Empty(_fx.Manager.Activity("acme.hello"));
        Assert.Equal("{}", _fx.Kv.Get("plugins.kv.acme.hello"));
        using var state = JsonDocument.Parse(_fx.Kv.Get(PluginManager.StateKey)!);
        Assert.False(state.RootElement.TryGetProperty("acme.hello", out _));
        var gone = await Assert.ThrowsAsync<PluginException>(() => _fx.Manager.UninstallAsync("acme.hello"));
        Assert.Equal(PluginErrorKeys.NotFound, gone.Key);
    }

    [Fact]
    public async Task Disabling_deactivates_the_plugin_and_disposes_what_it_registered()
    {
        await HelloAsync();
        Assert.NotEmpty(_fx.Plugin("acme.hello").RegisteredCommands);

        var disabled = await _fx.Manager.SetEnabledAsync("acme.hello", false);

        Assert.Equal((PluginStatus.Disabled, false), (disabled.Status, disabled.Enabled));
        Assert.Empty(disabled.RegisteredCommands);
        var e = await Assert.ThrowsAsync<PluginException>(() => _fx.Manager.RunCommandAsync("acme.hello", "acme.hello.hello", null, null));
        Assert.Equal(PluginErrorKeys.NotRunnable, e.Key);

        var again = await _fx.Manager.SetEnabledAsync("acme.hello", true);
        Assert.Equal(PluginStatus.Active, again.Status);
        Assert.NotEmpty(again.RegisteredCommands);
    }
}
