using Ncw.Client.Actions;
using Ncw.Client.Services;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Tests.Management;

/// <summary>
/// 设置 · 设备 · 插件:页面状态机与角色。设备是脚本化的 <see cref="FakePlugins"/>(变更返回完整状态并合进镜像),页面只读 Store。
/// viewer 按协议读不了(<see cref="PageState.Forbidden"/>,不是只读);member 可答复提示、运行命令、浏览市场;写入 owner。
/// </summary>
public sealed class PluginsPageTests
{
    private static readonly PluginConfigurationDto Schema = new("%config.title%",
    [
        new PluginConfigPropertyDto("greeting", "string", "%config.greeting%", "%config.greeting.hint%", [], "Hello", null, null),
        new PluginConfigPropertyDto("loud", "boolean", "%config.loud%", null, [], null, null, false),
        new PluginConfigPropertyDto("retries", "number", "%config.retries%", null, [], null, 3, null),
        new PluginConfigPropertyDto("mode", "enum", "%config.mode%", null, ["fast", "safe"], "safe", null, null),
    ]);

    private static readonly PluginMessagesDto[] Messages =
    [
        new("en-US", new Dictionary<string, string>
        {
            ["plugin.acme.hello.config.title"] = "Hello settings", ["plugin.acme.hello.config.greeting"] = "Greeting", ["plugin.acme.hello.config.greeting.hint"] = "Said on start.",
            ["plugin.acme.hello.config.loud"] = "Loud", ["plugin.acme.hello.config.retries"] = "Retries", ["plugin.acme.hello.config.mode"] = "Mode",
            ["plugin.acme.hello.cmd.hello"] = "Say hello", ["plugin.acme.hello.confirm.title"] = "Wipe the cache?", ["plugin.acme.hello.confirm.detail"] = "It cannot be undone.",
            ["plugin.acme.hello.ask.name"] = "Your name", ["plugin.acme.hello.pick.which"] = "Which one?", ["plugin.acme.hello.pick.a"] = "Option A",
        }),
        new("zh-CN", new Dictionary<string, string> { ["plugin.acme.hello.config.title"] = "你好设置", ["plugin.acme.hello.config.greeting"] = "问候语", ["plugin.acme.hello.cmd.hello"] = "打个招呼" }),
    ];

    private static PluginInfoDto Hello() => FakePlugins.Info(
        "acme.hello", "Hello", required: ["storage"], optional: ["workspace.read", "process"], granted: ["storage"], allowedCommands: ["echo"], hostPermissions: ["https://api.example.com/*"],
        config: Schema, commands: [new PluginCommandDto("acme.hello.hello", "%cmd.hello%", "sparkles")], messages: Messages, registeredTools: ["think"]);

    private static async Task<ManagementKit> Open(string role = "owner", Action<FakePlugins>? seed = null)
    {
        var kit = new ManagementKit(role);
        kit.Plugins.Plugins.Add(Hello());
        kit.Plugins.Plugins.Add(FakePlugins.Info("acme.zed", "Zed", PluginStatuses.Disabled, enabled: false, required: ["workspace.write"], granted: ["workspace.write"], version: "2.1.0", slug: "zed"));
        seed?.Invoke(kit.Plugins);
        await kit.Pages.Plugins.ActivateAsync();
        return kit;
    }

    private static PluginRowViewModel Row(ManagementKit kit, string id) => kit.Pages.Plugins.Plugins.Single(p => p.Id == id);

    // ───────────────────────── 状态机 ─────────────────────────

    [Fact]
    public async Task It_lists_the_installed_plugins_from_the_store_with_their_status()
    {
        var kit = await Open();
        var page = kit.Pages.Plugins;
        Assert.Equal(PageState.Ready, page.State);
        Assert.Equal(["acme.hello", "acme.zed"], page.Plugins.Select(p => p.Id));
        Assert.Equal(("ok", "mute"), (Row(kit, "acme.hello").Tone, Row(kit, "acme.zed").Tone));
        Assert.False(page.ShowsEmpty);
        Assert.Equal("0.3.3", page.ApiVersion);
        Assert.Equal(["storage", "workspace.read", "process"], Row(kit, "acme.hello").Permissions.Select(p => p.Name));
    }

    [Fact]
    public async Task No_plugins_is_an_empty_state_and_a_load_failure_is_an_error_with_its_key()
    {
        var empty = await Open(seed: p => p.Plugins.Clear());
        Assert.True(empty.Pages.Plugins.ShowsEmpty);

        var failed = await Open(seed: p => p.NextListFailure = R.Fail<PluginsTopicState>(ActionOutcome.Failed, "plugins.operationFailed"));
        Assert.Equal(PageState.Error, failed.Pages.Plugins.State);
        Assert.Equal("plugins.operationFailed", failed.Pages.Plugins.LoadFailure?.Key);
    }

    [Fact]
    public async Task A_disconnected_device_is_unknown_not_empty_and_prompts_diagnostics_and_rows_are_dropped()
    {
        var kit = await Open(seed: p =>
        {
            p.Interactions.Add(FakePlugins.Exec("r1", "acme.hello", "echo hi"));
            p.Plugins[1] = p.Plugins[1] with { Status = PluginStatuses.Faulted, Diagnostics = [FakePlugins.Diag("plugins.diag.faulted", "out of budget", "error")] };
        });
        Assert.True(kit.Pages.Plugins.HasPrompts);
        Assert.True(kit.Pages.Plugins.HasDiagnostics);

        kit.Rig.SetConnection(DeviceConnection.Offline);
        var page = kit.Pages.Plugins;
        Assert.Equal(PageState.Unknown, page.State);
        Assert.Empty(page.Plugins);
        Assert.Empty(page.Prompts);
        Assert.Empty(page.Diagnostics);
        Assert.Empty(page.Faulted);
        Assert.False(page.ShowsEmpty); // 不是「没有插件」
    }

    [Fact]
    public async Task A_viewer_cannot_read_plugins_at_all_so_the_page_is_forbidden_and_nothing_is_requested()
    {
        var kit = await Open("viewer");
        var page = kit.Pages.Plugins;
        Assert.Equal(PageState.Forbidden, page.State);
        Assert.False(page.ReadGate.Allowed);
        Assert.Empty(kit.Plugins.Calls); // viewer 按协议读不了:连列表都不请求
        Assert.Empty(page.Plugins);
    }

    [Fact]
    public async Task A_member_can_look_answer_prompts_run_commands_and_browse_but_not_change_anything()
    {
        var kit = await Open("member", p => p.Interactions.Add(FakePlugins.Confirm("c1", "acme.hello", "confirm.title")));
        var page = kit.Pages.Plugins;
        Assert.True(page.IsReadOnly);
        Assert.False(page.OwnerGate.Allowed);
        Assert.True(page.MemberGate.Allowed);
        Assert.True(Row(kit, "acme.hello").Commands[0].CanRun);
        Assert.False(Row(kit, "acme.hello").CanWrite);

        await Row(kit, "acme.zed").ToggleEnabledCommand.ExecuteAsync(null);
        await Row(kit, "acme.hello").Permissions[1].ToggleCommand.ExecuteAsync(null);
        page.Plugins[0].AskUninstallCommand.Execute(null);
        await page.InstallZipCommand.ExecuteAsync(null);
        Assert.False(page.HasPendingUninstall);
        Assert.Empty(kit.Dialogs.Picks);
        Assert.DoesNotContain(kit.Plugins.Calls, c => c.StartsWith("setEnabled", StringComparison.Ordinal) || c.StartsWith("grant", StringComparison.Ordinal) || c.StartsWith("uninstall", StringComparison.Ordinal));

        await Row(kit, "acme.hello").Commands[0].RunCommand.ExecuteAsync(null);
        await page.Prompts[0].AllowCommand.ExecuteAsync(null);
        Assert.Contains(kit.Plugins.Calls, c => c == "runCommand:acme.hello:acme.hello.hello:ws1");
        Assert.Contains(kit.Plugins.Calls, c => c == "reply:c1:True::");
        Assert.False(page.HasPrompts);
    }

    // ───────────────────────── 启停 / 卸载 / 安装 ─────────────────────────

    [Fact]
    public async Task The_owner_switches_a_plugin_on_and_off_and_one_that_cannot_load_cannot_be_enabled()
    {
        var kit = await Open(seed: p => p.Plugins.Add(FakePlugins.Info("acme.broken", "Broken", PluginStatuses.Error, enabled: false)));
        await Row(kit, "acme.zed").ToggleEnabledCommand.ExecuteAsync(null);
        Assert.Equal(PluginStatuses.Active, Row(kit, "acme.zed").Status);
        await Row(kit, "acme.zed").ToggleEnabledCommand.ExecuteAsync(null);
        Assert.Equal(PluginStatuses.Disabled, Row(kit, "acme.zed").Status);

        Assert.False(Row(kit, "acme.broken").CanToggleEnabled);
        Assert.True(Row(kit, "acme.broken").IsError);
    }

    [Fact]
    public async Task A_faulted_plugin_is_recovered_by_disabling_and_enabling_it_again()
    {
        var kit = await Open(seed: p => p.Plugins[0] = p.Plugins[0] with { Status = PluginStatuses.Faulted });
        Assert.Equal(["acme.hello"], kit.Pages.Plugins.Faulted.Select(r => r.Id));
        kit.Plugins.Calls.Clear();

        await Row(kit, "acme.hello").RecoverCommand.ExecuteAsync(null);
        Assert.Equal(["setEnabled:acme.hello:False", "setEnabled:acme.hello:True"], kit.Plugins.Calls);
        Assert.Equal(PluginStatuses.Active, Row(kit, "acme.hello").Status);
        Assert.False(kit.Pages.Plugins.HasFaulted);
    }

    [Fact]
    public async Task Uninstall_needs_a_second_confirmation_and_cancel_does_nothing()
    {
        var kit = await Open();
        var page = kit.Pages.Plugins;
        Row(kit, "acme.zed").AskUninstallCommand.Execute(null);
        Assert.True(page.HasPendingUninstall);
        Assert.Equal("Zed", page.PendingUninstall!.Subject);
        page.CancelUninstallCommand.Execute(null);
        Assert.DoesNotContain(kit.Plugins.Calls, c => c.StartsWith("uninstall", StringComparison.Ordinal));

        Row(kit, "acme.zed").AskUninstallCommand.Execute(null);
        await page.ConfirmUninstallCommand.ExecuteAsync(null);
        Assert.Equal(["acme.hello"], page.Plugins.Select(p => p.Id));
        Assert.False(page.HasPendingUninstall);
    }

    [Fact]
    public async Task A_zip_over_3_MB_is_refused_before_anything_is_read_or_sent_and_a_small_one_installs_disabled_and_opens_for_review()
    {
        var kit = await Open();
        var page = kit.Pages.Plugins;
        kit.Dialogs.NextFile = ScriptedDialogs.File("big.zip", PluginsLimits.MaxZipBytes + 1);
        await page.InstallZipCommand.ExecuteAsync(null);
        Assert.Equal([FilePickKind.PluginZip], kit.Dialogs.Picks);
        Assert.Equal("plugins.packageTooLarge", page.ActionFailure?.Key);
        Assert.DoesNotContain(kit.Plugins.Calls, c => c.StartsWith("installPackage", StringComparison.Ordinal)); // 不发请求

        kit.Dialogs.NextFile = ScriptedDialogs.File("fresh.zip", 1024);
        await page.InstallZipCommand.ExecuteAsync(null);
        Assert.Null(page.ActionFailure);
        Assert.Equal("Fresh", page.JustInstalled);
        var fresh = Row(kit, "acme.fresh");
        Assert.Equal(PluginStatuses.Disabled, fresh.Status); // 新装的是禁用的、没有授权
        Assert.True(fresh.IsExpanded);
        Assert.All(fresh.Permissions, p => Assert.False(p.IsGranted));
    }

    [Fact]
    public async Task Cancelling_the_file_picker_sends_nothing()
    {
        var kit = await Open();
        kit.Dialogs.NextFile = null;
        await kit.Pages.Plugins.InstallZipCommand.ExecuteAsync(null);
        Assert.DoesNotContain(kit.Plugins.Calls, c => c.StartsWith("installPackage", StringComparison.Ordinal));
    }

    // ───────────────────────── 权限 ─────────────────────────

    [Fact]
    public async Task Permissions_are_listed_required_first_with_sensitive_ones_flagged()
    {
        var kit = await Open();
        var hello = Row(kit, "acme.hello");
        var byName = hello.Permissions.ToDictionary(p => p.Name);
        Assert.True(byName["storage"].IsRequired && byName["storage"].IsGranted && !byName["storage"].IsDangerous);
        Assert.True(byName["process"].IsOptional && byName["process"].IsPending && byName["process"].IsDangerous);
        Assert.True(byName["workspace.read"].IsOptional && !byName["workspace.read"].IsDangerous);
        Assert.Equal(["echo"], hello.AllowedCommands);
        Assert.Equal(["https://api.example.com/*"], hello.HostPermissions);
        Assert.True(Row(kit, "acme.zed").Permissions[0].IsDangerous); // workspace.write
    }

    [Theory]
    [InlineData("process", true)]
    [InlineData("workspace.write", true)]
    [InlineData("scm.write", true)]
    [InlineData("secrets", true)]
    [InlineData("agent.intercept", true)]
    [InlineData("storage", false)]
    [InlineData("workspace.read", false)]
    [InlineData("net", false)]
    [InlineData("something.new", false)]
    public void Sensitivity_is_decided_by_the_capability_name(string name, bool dangerous) => Assert.Equal(dangerous, PluginPermissionInfo.IsDangerous(name));

    [Fact]
    public async Task An_optional_permission_is_granted_and_revoked_one_at_a_time_without_confirmation()
    {
        var kit = await Open();
        var process = () => Row(kit, "acme.hello").Permissions.Single(p => p.Name == "process");
        await process().ToggleCommand.ExecuteAsync(null);
        Assert.True(process().IsGranted);
        Assert.Contains("grant:acme.hello:process", kit.Plugins.Calls);

        await process().ToggleCommand.ExecuteAsync(null);
        Assert.False(process().IsGranted);
        Assert.False(kit.Pages.Plugins.HasPendingRevoke);
        Assert.Equal(PluginStatuses.Active, Row(kit, "acme.hello").Status); // 撤销可选的不影响运行
    }

    [Fact]
    public async Task Revoking_a_required_permission_asks_first_and_stops_the_plugin_when_confirmed()
    {
        var kit = await Open();
        var page = kit.Pages.Plugins;
        await Row(kit, "acme.hello").Permissions.Single(p => p.Name == "storage").ToggleCommand.ExecuteAsync(null);
        Assert.True(page.HasPendingRevoke);
        Assert.Equal(("acme.hello", "storage", true), (page.PendingRevoke!.PluginId, page.PendingRevoke.Permission, page.PendingRevoke.IsRequired));
        Assert.DoesNotContain(kit.Plugins.Calls, c => c.StartsWith("revoke", StringComparison.Ordinal)); // 还没发

        page.CancelRevokeCommand.Execute(null);
        Assert.Equal(PluginStatuses.Active, Row(kit, "acme.hello").Status);

        await Row(kit, "acme.hello").Permissions.Single(p => p.Name == "storage").ToggleCommand.ExecuteAsync(null);
        await page.ConfirmRevokeCommand.ExecuteAsync(null);
        Assert.Equal(PluginStatuses.Disabled, Row(kit, "acme.hello").Status); // 撤掉必选能力 = 插件停下来
        Assert.False(page.HasPendingRevoke);
    }

    [Fact]
    public async Task A_plugin_waiting_for_approval_lists_what_is_missing_and_approve_all_activates_it()
    {
        var kit = await Open(seed: p => p.Plugins.Add(FakePlugins.Info("acme.wait", "Wait", PluginStatuses.PendingApproval, required: ["storage", "net"], granted: ["storage"])));
        var wait = Row(kit, "acme.wait");
        Assert.True(wait.IsPendingApproval);
        Assert.Equal(["net"], wait.PendingPermissions);
        await wait.GrantPendingCommand.ExecuteAsync(null);
        Assert.Equal(PluginStatuses.Active, Row(kit, "acme.wait").Status);
        Assert.Contains("grant:acme.wait:net", kit.Plugins.Calls);
    }

    // ───────────────────────── 配置 ─────────────────────────

    [Fact]
    public async Task The_configuration_form_follows_the_manifest_schema_with_titles_from_the_plugins_own_messages()
    {
        var kit = await Open();
        kit.Pages.Plugins.Locale = "zh-CN";
        var hello = Row(kit, "acme.hello");
        hello.IsExpanded = true;
        await Task.Yield();
        Assert.Equal("你好设置", hello.ConfigTitle);
        var fields = hello.ConfigFields.ToDictionary(f => f.Key);
        Assert.Equal("问候语", fields["greeting"].Title);
        Assert.Equal("Loud", fields["loud"].Title); // 中文词条里没有:回退 en-US
        Assert.Equal("config.retries".Length > 0 ? "Retries" : "", fields["retries"].Title);
        Assert.Equal(("Hello", false, 3d, "safe"), (fields["greeting"].Text, fields["loud"].Flag, fields["retries"].Number, fields["mode"].Text));
        Assert.True(fields["mode"].IsEnum && fields["loud"].IsBoolean && fields["retries"].IsNumber && fields["greeting"].IsText);
        Assert.Equal(["fast", "safe"], fields["mode"].Options.Select(o => o.Value));
        Assert.Contains("getConfig:acme.hello", kit.Plugins.Calls);
    }

    [Fact]
    public async Task Text_and_number_edits_are_drafts_until_applied_while_switches_and_choices_apply_at_once()
    {
        var kit = await Open();
        var hello = Row(kit, "acme.hello");
        hello.IsExpanded = true;
        await Task.Yield();
        var f = hello.ConfigFields.ToDictionary(x => x.Key);

        f["greeting"].Text = "Howdy";
        Assert.True(f["greeting"].IsDirty && f["greeting"].CanApply);
        Assert.DoesNotContain(kit.Plugins.Calls, c => c.StartsWith("setConfig", StringComparison.Ordinal)); // 草稿:没发
        await f["greeting"].ApplyCommand.ExecuteAsync(null);
        Assert.Equal("setConfig:acme.hello:greeting:Howdy::", kit.Plugins.Calls.Last());
        Assert.False(f["greeting"].IsDirty);
        Assert.True(f["greeting"].CanReset);

        f["retries"].Number = null;
        Assert.False(f["retries"].CanApply); // 数字必须有值

        f["loud"].Flag = true; // 开关立即写
        await Task.Yield();
        Assert.Contains(kit.Plugins.Calls, c => c == "setConfig:acme.hello:loud:::True");
        f["mode"].Text = "fast";
        await Task.Yield();
        Assert.Contains(kit.Plugins.Calls, c => c == "setConfig:acme.hello:mode:fast::");
    }

    [Fact]
    public async Task A_value_the_device_rejects_is_shown_on_the_field_and_the_draft_is_kept_and_reset_restores_the_default()
    {
        var kit = await Open();
        var hello = Row(kit, "acme.hello");
        hello.IsExpanded = true;
        await Task.Yield();
        var mode = hello.ConfigFields.Single(f => f.Key == "mode");

        mode.Text = "bogus"; // 不在可选范围
        await Task.Yield();
        Assert.Equal("plugins.configurationInvalid", mode.Error);
        Assert.True(mode.HasError);
        Assert.Null(kit.Pages.Plugins.ActionFailure); // 错误在字段上,不再重复一条页面级失败

        var greeting = hello.ConfigFields.Single(f => f.Key == "greeting");
        greeting.Text = "Hi";
        await greeting.ApplyCommand.ExecuteAsync(null);
        await greeting.ResetCommand.ExecuteAsync(null);
        Assert.Equal("setConfig:acme.hello:greeting:::", kit.Plugins.Calls.Last()); // Value = null
        Assert.Equal("Hello", greeting.Text);
        Assert.False(greeting.CanReset);
    }

    [Fact]
    public async Task A_device_side_change_updates_untouched_fields_but_never_overwrites_a_draft()
    {
        var kit = await Open();
        var hello = Row(kit, "acme.hello");
        hello.IsExpanded = true;
        await Task.Yield();
        var greeting = hello.ConfigFields.Single(f => f.Key == "greeting");
        var loud = hello.ConfigFields.Single(f => f.Key == "loud");
        greeting.Text = "my draft";

        kit.Plugins.Config["acme.hello"] = new() { ["greeting"] = new("from elsewhere", null, null), ["loud"] = new(null, null, true) };
        await kit.Pages.Plugins.LoadConfigAsync(hello);
        Assert.Equal("my draft", greeting.Text);   // 有未应用的草稿:跟随不覆盖
        Assert.True(loud.Flag);                    // 没动过的:跟随设备
        Assert.Equal("from elsewhere", greeting.DeviceValue!.Text);
    }

    [Theory]
    [InlineData("zh-CN", "config.greeting", "问候语")]
    [InlineData("zh-TW", "%config.greeting%", "问候语")] // 同语种(zh)优先于 en-US
    [InlineData("fr-FR", "%config.loud%", "Loud")]      // 都没有:回退 en-US
    [InlineData("en-US", "%unknown.key%", "unknown.key")] // 解析不了:显示去掉 % 的 key
    [InlineData("en-US", "plain", "plain")]
    public void Plugin_messages_resolve_by_language_then_en_US_then_the_bare_key(string locale, string key, string expected)
    {
        Assert.Equal(expected, PluginMessages.Resolve(Hello(), key, locale));
    }

    // ───────────────────────── 命令 ─────────────────────────

    [Fact]
    public async Task Registered_commands_show_their_resolved_titles_and_running_one_reports_success_or_the_plugins_failure()
    {
        var kit = await Open("member");
        var cmd = Row(kit, "acme.hello").Commands.Single();
        Assert.Equal("Say hello", cmd.Title);
        await cmd.RunCommand.ExecuteAsync(null);
        Assert.Equal("Say hello", kit.Pages.Plugins.JustRan);
        Assert.Contains("runCommand:acme.hello:acme.hello.hello:ws1", kit.Plugins.Calls);

        kit.Plugins.CommandFailureKey = "plugins.commandFailed";
        await cmd.RunCommand.ExecuteAsync(null);
        Assert.Equal("plugins.commandFailed", kit.Pages.Plugins.ActionFailure?.Key);
    }

    [Fact]
    public async Task Commands_that_are_declared_but_not_registered_are_not_offered()
    {
        var kit = await Open(seed: p => p.Plugins[0] = p.Plugins[0] with { RegisteredCommands = [] });
        Assert.Empty(Row(kit, "acme.hello").Commands);
    }

    // ───────────────────────── 提示 ─────────────────────────

    [Fact]
    public async Task A_command_approval_carries_the_command_line_and_the_device_and_environment_it_would_run_in()
    {
        var kit = await Open(seed: p => p.Interactions.Add(FakePlugins.Exec("x1", "acme.hello", "rm -rf build", "ssh-prod", "prod-1", "ssh", "dev-9")));
        var prompt = kit.Pages.Plugins.Prompts.Single();
        Assert.True(prompt.IsExec && prompt.Danger && prompt.IsAllowDeny);
        Assert.Equal("rm -rf build", prompt.Command);
        Assert.Equal(("prod-1", "ssh", "ssh-prod", "dev-9"), (prompt.EnvironmentName, prompt.EnvironmentType, prompt.EnvironmentId, prompt.DeviceId));
        Assert.True(prompt.HasEnvironment);
        Assert.Equal("Hello", prompt.PluginName);

        await prompt.AllowCommand.ExecuteAsync(null);
        Assert.Contains("reply:x1:True::", kit.Plugins.Calls);
        Assert.False(kit.Pages.Plugins.HasPrompts); // 提示从镜像里消失靠设备的新状态
    }

    [Fact]
    public async Task Prompt_texts_are_the_plugins_own_words_and_each_kind_answers_with_its_own_field()
    {
        var kit = await Open(seed: p =>
        {
            p.Interactions.Add(FakePlugins.Confirm("c", "acme.hello", "confirm.title", "confirm.detail", danger: true));
            p.Interactions.Add(FakePlugins.Input("i", "acme.hello", "ask.name", "ask.name", initial: "Ann"));
            p.Interactions.Add(FakePlugins.Pick("p", "acme.hello", "pick.which", ("a", "pick.a"), ("b", "pick.b")));
            p.Interactions.Add(FakePlugins.PermissionRequest("q", "acme.hello", "confirm.title", "process", "net"));
        });
        var prompts = kit.Pages.Plugins.Prompts.ToDictionary(p => p.RequestId);
        Assert.Equal(("Wipe the cache?", "It cannot be undone.", true), (prompts["c"].Title, prompts["c"].Detail, prompts["c"].Danger));
        Assert.Equal(("Your name", "Ann"), (prompts["i"].Placeholder, prompts["i"].Text));
        Assert.Equal(["Option A", "pick.b"], prompts["p"].Items.Select(x => x.Label)); // 解析不了:去掉 % 的 key
        Assert.True(prompts["q"].IsPermissions && prompts["q"].Danger); // process 是敏感能力

        prompts["i"].Text = "Bob";
        await prompts["i"].SubmitCommand.ExecuteAsync(null);
        await prompts["p"].PickCommand.ExecuteAsync("a");
        await prompts["c"].DenyCommand.ExecuteAsync(null);
        await prompts["q"].CancelCommand.ExecuteAsync(null); // 取消 = 全部缺省
        Assert.Equal(["reply:i::Bob:", "reply:p:::a", "reply:c:False::", "reply:q:::"], kit.Plugins.Calls.Where(c => c.StartsWith("reply", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_request_another_controller_already_answered_is_a_race_not_a_failure()
    {
        var kit = await Open(seed: p =>
        {
            p.Interactions.Add(FakePlugins.Exec("x1", "acme.hello", "echo hi"));
            p.ReplyGone = true;
        });
        var page = kit.Pages.Plugins;
        await page.Prompts[0].AllowCommand.ExecuteAsync(null);
        Assert.True(page.PromptHandledElsewhere);
        Assert.Null(page.ActionFailure); // 不是故障
        Assert.False(page.HasPrompts);   // 对账之后卡片消失

        page.DismissHandledCommand.Execute(null);
        Assert.False(page.PromptHandledElsewhere);
    }

    // ───────────────────────── 市场 ─────────────────────────

    private static Task<ManagementKit> OpenMarket(string role = "owner", Action<FakePlugins>? seed = null) => Open(role, p =>
    {
        p.MarketItems.AddRange([
            FakePlugins.MarketItem("pdf", "PDF Tools", ["net"], skills: ["pdf-tools"]),
            FakePlugins.MarketItem("zed", "Zed", ["workspace.write"], "2.2.0", pluginId: "acme.zed"),
        ]);
        seed?.Invoke(p);
    });

    [Fact]
    public async Task Browsing_the_market_searches_filters_pages_and_marks_what_is_installed()
    {
        var kit = await OpenMarket(seed: p => p.MarketHasNext = true);
        var page = kit.Pages.Plugins;
        page.ShowMarketCommand.Execute(null);
        await Task.Yield();
        Assert.Equal(PluginQueryState.Ready, page.MarketStatus);
        Assert.Equal(["pdf", "zed"], page.Market.Select(m => m.Slug));
        Assert.Equal(["", "docs", "tools"], page.Categories.Select(c => c.Value));
        Assert.True(page.Market.Single(m => m.Slug == "zed").IsInstalled);
        Assert.Equal("2.1.0", page.Market.Single(m => m.Slug == "zed").InstalledVersion);
        Assert.True(page.Market[0].HasDangerousPermission == false && page.Market[1].HasDangerousPermission);

        page.MarketQuery = "pdf";
        await page.SearchMarketCommand.ExecuteAsync(null);
        Assert.Equal(["pdf"], page.Market.Select(m => m.Slug));
        Assert.Contains("marketList:pdf::1", kit.Plugins.Calls);

        await page.NextPageCommand.ExecuteAsync(null);
        Assert.Equal(2, page.MarketPage);
        Assert.True(page.HasPreviousPage);
        await page.PreviousPageCommand.ExecuteAsync(null);
        Assert.Equal(1, page.MarketPage);
    }

    [Fact]
    public async Task A_failing_market_is_an_error_with_a_retry_and_a_member_can_browse()
    {
        var kit = await OpenMarket("member", p => p.NextMarketFailure = R.Fail<PluginsMarketListResult>(ActionOutcome.Failed, "plugins.marketFailed"));
        var page = kit.Pages.Plugins;
        await page.SearchMarketCommand.ExecuteAsync(null);
        Assert.True(page.MarketFailed);
        Assert.Equal("plugins.marketFailed", page.MarketFailure?.Key);
        await page.SearchMarketCommand.ExecuteAsync(null);
        Assert.True(page.MarketReady);
        Assert.False(page.OwnerGate.Allowed); // 浏览 member 起,安装 owner
    }

    [Fact]
    public async Task Detail_shows_versions_and_permission_escalation()
    {
        var kit = await OpenMarket();
        var page = kit.Pages.Plugins;
        await page.SearchMarketCommand.ExecuteAsync(null);
        await page.ShowDetailCommand.ExecuteAsync(page.Market[0]);
        Assert.Equal(["1.1.0", "1.0.0"], page.Detail!.Versions.Select(v => v.Version));
        Assert.True(page.Detail.Versions[0].PermissionEscalated);
        page.CloseDetailCommand.Execute(null);
        Assert.Null(page.Detail);
    }

    [Fact]
    public async Task Without_an_account_token_install_shows_the_sign_in_card_and_the_sign_in_request_reaches_the_shell()
    {
        var kit = await OpenMarket(seed: p => p.AuthRequired = true);
        var page = kit.Pages.Plugins;
        var requested = 0;
        page.SignInRequested += () => requested++;
        await page.SearchMarketCommand.ExecuteAsync(null);
        await page.InstallMarketCommand.ExecuteAsync("pdf");
        Assert.True(page.AuthRequired);
        Assert.Null(page.ActionFailure);   // 不当成普通错误
        Assert.Null(page.InstallingSlug);
        Assert.True(page.MarketReady);     // 浏览照常

        page.SignInCommand.Execute(null);
        Assert.Equal(1, requested);

        kit.Plugins.AuthRequired = false; // 登录之后令牌到位:再试就成功,提示卡消失
        await page.InstallMarketCommand.ExecuteAsync("pdf");
        Assert.False(page.AuthRequired);
        Assert.Equal("PDF Tools", page.JustInstalled);
        Assert.True(page.Market.Single(m => m.Slug == "pdf").IsInstalled);
    }

    [Fact]
    public async Task A_member_cannot_install_from_the_market()
    {
        var kit = await OpenMarket("member");
        await kit.Pages.Plugins.SearchMarketCommand.ExecuteAsync(null);
        await kit.Pages.Plugins.InstallMarketCommand.ExecuteAsync("pdf");
        Assert.DoesNotContain(kit.Plugins.Calls, c => c.StartsWith("installMarket", StringComparison.Ordinal));
    }

    // ───────────────────────── 更新 ─────────────────────────

    [Fact]
    public async Task Update_checks_list_new_required_permissions_and_exclude_locally_installed_plugins_from_update_all()
    {
        var kit = await OpenMarket(seed: p => p.UpdateList =
        [
            new PluginUpdateDto("acme.zed", "zed", "Zed", "2.1.0", "2.2.0", ["process"], true),
            new PluginUpdateDto("acme.hello", "hello", "Hello", "1.0.0", "1.1.0", [], false),
        ]);
        var page = kit.Pages.Plugins;
        await page.CheckUpdatesCommand.ExecuteAsync(null);
        Assert.Equal(2, page.Updates.Count);
        Assert.Equal(1, page.UpdatableCount);
        Assert.True(page.CanUpdateAll);
        Assert.True(page.Updates[0].HasEscalation && page.Updates[0].HasDangerousEscalation);
        Assert.True(page.Updates[1].NotBatchable);

        await page.UpdateAllCommand.ExecuteAsync(null);
        Assert.Equal(["acme.zed"], page.UpdateSummary!.Updated);
        Assert.Equal("2.2.0", Row(kit, "acme.zed").Version);
        Assert.Equal(["acme.hello"], page.Updates.Select(u => u.Dto.PluginId)); // 本地装的没动,仍在列表里
        Assert.False(page.CanUpdateAll);
    }

    [Fact]
    public async Task Only_an_owner_can_update_all()
    {
        var kit = await OpenMarket("member", p => p.UpdateList = [new PluginUpdateDto("acme.zed", "zed", "Zed", "2.1.0", "2.2.0", [], true)]);
        await kit.Pages.Plugins.CheckUpdatesCommand.ExecuteAsync(null); // member 可以检查
        Assert.Single(kit.Pages.Plugins.Updates);
        Assert.False(kit.Pages.Plugins.CanUpdateAll);
        await kit.Pages.Plugins.UpdateAllCommand.ExecuteAsync(null);
        Assert.DoesNotContain("updateAll", kit.Plugins.Calls);
    }

    // ───────────────────────── 活动与诊断 ─────────────────────────

    [Fact]
    public async Task Activity_lists_recent_host_calls_filters_by_plugin_and_reports_a_failure_with_retry()
    {
        var kit = await Open(seed: p =>
        {
            p.ActivityEntries.Add(new PluginActivityDto(1_700_000_005_000, "acme.hello", "workspace.readFile", "read a.txt", "ok", 3));
            p.ActivityEntries.Add(new PluginActivityDto(1_700_000_004_000, "acme.zed", "process.exec", "echo hi", "denied", 1));
            p.NextActivityFailure = R.Fail<PluginsActivityResult>(ActionOutcome.Failed, "plugins.operationFailed");
        });
        var page = kit.Pages.Plugins;
        page.ShowActivityCommand.Execute(null);
        await Task.Yield();
        Assert.True(page.ActivityFailed);

        await page.LoadActivityCommand.ExecuteAsync(null);
        Assert.Equal(PluginQueryState.Ready, page.ActivityStatus);
        Assert.Equal(["Hello", "Zed"], page.Activity.Select(a => a.PluginName));
        Assert.Equal(["ok", "warn"], page.Activity.Select(a => a.Tone));
        Assert.Equal(["", "acme.hello", "acme.zed"], page.ActivityChoices.Select(c => c.Value));

        page.ActivityFilter = "acme.zed";
        await Task.Delay(20);
        Assert.Equal(["process.exec"], page.Activity.Select(a => a.Method));
        Assert.Contains("activity:acme.zed", kit.Plugins.Calls);
    }

    [Fact]
    public async Task Diagnostics_from_every_plugin_are_collected_with_their_level_and_translated_key_suffix()
    {
        var kit = await Open(seed: p => p.Plugins[0] = p.Plugins[0] with
        {
            Diagnostics = [FakePlugins.Diag("plugins.diag.unsupported", "views not supported"), FakePlugins.Diag("plugins.diag.activateFailed", "boom", "error")],
        });
        var rows = kit.Pages.Plugins.Diagnostics;
        Assert.Equal(["unsupported", "activateFailed"], rows.Select(d => d.KeySuffix));
        Assert.Equal(["warn", "bad"], rows.Select(d => d.Tone));
        Assert.Equal(2, Row(kit, "acme.hello").DiagnosticCount);
    }

    // ───────────────────────── 镜像与角色变化 ─────────────────────────

    [Fact]
    public async Task A_change_pushed_by_another_controller_updates_the_page_without_any_call_and_keeps_expansion()
    {
        var kit = await Open();
        Row(kit, "acme.hello").IsExpanded = true;
        kit.Plugins.Plugins[0] = kit.Plugins.Plugins[0] with { Status = PluginStatuses.Faulted };
        kit.Plugins.Push();
        Assert.True(Row(kit, "acme.hello").IsFaulted);
        Assert.True(Row(kit, "acme.hello").IsExpanded); // 行对象跨快照复用
        Assert.Equal(["acme.hello"], kit.Pages.Plugins.Faulted.Select(r => r.Id));
    }

    [Fact]
    public async Task Losing_the_owner_role_disables_writes_immediately()
    {
        var kit = await Open();
        Assert.True(Row(kit, "acme.hello").CanToggleEnabled);
        kit.Rig.SetRole("member");
        Assert.False(Row(kit, "acme.hello").CanToggleEnabled);
        Assert.False(Row(kit, "acme.hello").Permissions[0].CanWrite);
        Assert.True(kit.Pages.Plugins.IsReadOnly);
        kit.Rig.SetRole("viewer");
        Assert.Equal(PageState.Forbidden, kit.Pages.Plugins.State);
    }
}
