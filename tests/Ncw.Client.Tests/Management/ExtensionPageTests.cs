using Ncw.Client.Actions;
using Ncw.Client.Services;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Tests.Management;

public sealed class SkillsPageTests
{
    private static async Task<ManagementKit> Open(string role = "owner", Action<FakeSkills>? seed = null)
    {
        var kit = new ManagementKit(role);
        kit.Skills.Skills.AddRange([FakeSkills.Skill("a", "alpha"), FakeSkills.Skill("b", "beta", "project", active: false), FakeSkills.Skill("c", "gamma", "plugin", "plugin")]);
        seed?.Invoke(kit.Skills);
        await kit.Pages.Skills.ActivateAsync();
        return kit;
    }

    [Fact]
    public async Task It_lists_the_skills_from_the_store_with_filtering_and_an_empty_state()
    {
        var kit = await Open();
        var page = kit.Pages.Skills;
        Assert.Equal(PageState.Ready, page.State);
        Assert.Equal(["alpha", "beta", "gamma"], page.Skills.Select(s => s.Name));
        Assert.False(page.Skills.Single(s => s.Name == "gamma").CanUninstall); // 插件带来的没有卸载入口

        page.Filter = "BET";
        Assert.Equal(["beta"], page.Skills.Select(s => s.Name));
        page.Filter = "zzz";
        Assert.True(page.ShowsNoMatch);

        var empty = await Open(seed: s => s.Skills.Clear());
        Assert.True(empty.Pages.Skills.ShowsEmpty);
    }

    [Fact]
    public async Task A_load_failure_is_an_error_and_a_disconnected_device_is_unknown_without_pretending_there_are_no_skills()
    {
        var kit = await Open(seed: s => s.ListFailure = R.Fail<SkillsListResult>(ActionOutcome.Failed, "skills.operationFailed"));
        Assert.Equal(PageState.Error, kit.Pages.Skills.State);

        var ok = await Open();
        ok.Rig.SetConnection(DeviceConnection.Offline);
        Assert.Equal(PageState.Unknown, ok.Pages.Skills.State);
        Assert.Empty(ok.Pages.Skills.Skills);
        Assert.False(ok.Pages.Skills.ShowsEmpty);
    }

    [Fact]
    public async Task Roles_gate_global_toggle_activation_and_install()
    {
        var owner = await Open();
        await owner.Pages.Skills.Skills[0].ToggleGlobalCommand.ExecuteAsync(null);
        Assert.False(owner.Pages.Skills.Skills[0].GlobalEnabled);

        var member = await Open("member");
        Assert.False(member.Pages.Skills.GlobalGate.Allowed);
        Assert.True(member.Pages.Skills.CanActivate);
        await member.Pages.Skills.Skills[0].ToggleGlobalCommand.ExecuteAsync(null);
        Assert.True(member.Pages.Skills.Skills[0].GlobalEnabled); // 没动
        await member.Pages.Skills.Skills[1].ToggleActiveCommand.ExecuteAsync(null);
        Assert.True(member.Pages.Skills.Skills.Single(s => s.Id == "b").ActiveInWorkspace);

        var viewer = await Open("viewer");
        Assert.False(viewer.Pages.Skills.CanActivate);
        Assert.True(viewer.Pages.Skills.IsReadOnly);
        await viewer.Pages.Skills.InstallZipCommand.ExecuteAsync(null);
        Assert.Empty(viewer.Dialogs.Picks); // 连文件选择框都不开
    }

    [Fact]
    public async Task A_zip_over_3_MB_is_refused_before_anything_is_read_or_sent_and_a_small_one_installs()
    {
        var kit = await Open();
        kit.Dialogs.NextFile = ScriptedDialogs.File("big.zip", SkillsLimits.MaxZipBytes + 1);
        await kit.Pages.Skills.InstallZipCommand.ExecuteAsync(null);
        Assert.Equal("skills.packageTooLarge", kit.Pages.Skills.ActionFailure?.Key);
        Assert.Empty(kit.Skills.ZipInstalls);

        kit.Dialogs.NextFile = ScriptedDialogs.File("my-skill.zip", 100);
        await kit.Pages.Skills.InstallZipCommand.ExecuteAsync(null);
        Assert.Equal("my-skill.zip", Assert.Single(kit.Skills.ZipInstalls).Name);
        Assert.Equal("my-skill.zip", kit.Pages.Skills.JustInstalled);
        Assert.Contains(kit.Pages.Skills.Skills, s => s.Name == "my-skill.zip");
    }

    [Fact]
    public async Task Cancelling_the_file_picker_does_nothing()
    {
        var kit = await Open();
        kit.Dialogs.NextFile = null;
        await kit.Pages.Skills.InstallZipCommand.ExecuteAsync(null);
        Assert.Empty(kit.Skills.ZipInstalls);
        Assert.Null(kit.Pages.Skills.ActionFailure);
    }

    [Fact]
    public async Task Uninstall_needs_a_confirmation_and_cancel_sends_nothing()
    {
        var kit = await Open();
        kit.Pages.Skills.Skills[0].AskUninstallCommand.Execute(null);
        Assert.Equal("alpha", kit.Pages.Skills.PendingUninstall?.Subject);
        kit.Pages.Skills.CancelUninstallCommand.Execute(null);
        Assert.Empty(kit.Skills.Uninstalled);

        kit.Pages.Skills.Skills[0].AskUninstallCommand.Execute(null);
        await kit.Pages.Skills.ConfirmUninstallCommand.ExecuteAsync(null);
        Assert.Equal(["a"], kit.Skills.Uninstalled);
        Assert.DoesNotContain(kit.Pages.Skills.Skills, s => s.Id == "a");
    }

    [Fact]
    public async Task The_market_searches_shows_details_and_installs_and_says_when_sign_in_is_needed()
    {
        var kit = await Open(seed: s => s.Market = [new SkillMarketItemDto("pdf", "pdf", "PDF tools", "Work with PDFs", "dev", null, "me", "1.2.0", null, 1000, 5, 1)]);
        var page = kit.Pages.Skills;
        await page.ShowMarketCommand.ExecuteAsync(null);
        Assert.True(page.IsMarketTab);
        Assert.Single(page.Market);
        Assert.Equal(PageState.Ready, page.State);

        page.MarketQuery = "zzz";
        await page.SearchMarketCommand.ExecuteAsync(null);
        Assert.Empty(page.Market);
        Assert.True(page.MarketEmpty);

        page.MarketQuery = "pdf";
        await page.SearchMarketCommand.ExecuteAsync(null);
        await page.ShowDetailCommand.ExecuteAsync(page.Market[0]);
        Assert.Equal("1.2.0", page.Detail!.Versions.Single().Version);

        kit.Skills.AuthRequired = true;
        await page.InstallMarketCommand.ExecuteAsync("pdf");
        Assert.True(page.AuthRequired);
        Assert.Null(page.ActionFailure); // 不当成普通错误

        kit.Skills.AuthRequired = false;
        await page.InstallMarketCommand.ExecuteAsync("pdf");
        Assert.False(page.AuthRequired);
        Assert.Equal(("pdf", SkillScopes.Global), kit.Skills.MarketInstalls.Single());
    }

    [Fact]
    public async Task A_market_failure_has_its_own_state_and_a_late_old_search_never_overwrites_a_newer_one()
    {
        var kit = await Open(seed: s => s.MarketFailure = R.Fail<SkillsMarketListResult>(ActionOutcome.Failed, "skills.networkFailed"));
        await kit.Pages.Skills.ShowMarketCommand.ExecuteAsync(null);
        Assert.True(kit.Pages.Skills.MarketFailed);
        Assert.Equal("skills.networkFailed", kit.Pages.Skills.MarketFailure?.Key);

        var slow = new TaskCompletionSource();
        var kit2 = await Open(seed: s =>
        {
            s.Market = [new SkillMarketItemDto("old", "old", "Old", "", "dev", null, null, null, null, null, null, null), new SkillMarketItemDto("new", "new", "New", "", "dev", null, null, null, null, null, null, null)];
            s.MarketGate = q => q == "old" ? slow.Task : Task.CompletedTask;
        });
        kit2.Pages.Skills.MarketQuery = "old";
        var first = kit2.Pages.Skills.SearchMarketCommand.ExecuteAsync(null);
        kit2.Pages.Skills.MarketQuery = "new";
        await kit2.Pages.Skills.SearchMarketCommand.ExecuteAsync(null);
        slow.SetResult();
        await first;
        Assert.Equal(["new"], kit2.Pages.Skills.Market.Select(m => m.Slug));
    }
}

public sealed class CommandsPageTests
{
    [Fact]
    public async Task It_lists_disabled_commands_too_toggles_for_the_owner_and_is_read_only_for_a_viewer()
    {
        var kit = new ManagementKit();
        kit.Commands.All.AddRange([new CommandItemDto("review", "Review changes", "global", "x", "[path]", true), new CommandItemDto("deploy", "Deploy", "project", "y", null, false)]);
        await kit.Pages.Commands.ActivateAsync();

        Assert.Equal(2, kit.Pages.Commands.Commands.Count);
        await kit.Pages.Commands.Commands.Single(c => c.Name == "deploy").ToggleCommand.ExecuteAsync(null);
        Assert.True(kit.Pages.Commands.Commands.Single(c => c.Name == "deploy").Enabled);

        kit.Rig.SetRole("viewer");
        Assert.Equal(PageState.Ready, kit.Pages.Commands.State); // viewer 可读
        Assert.True(kit.Pages.Commands.IsReadOnly);
        await kit.Pages.Commands.Commands[0].ToggleCommand.ExecuteAsync(null);
        Assert.True(kit.Pages.Commands.Commands.Single(c => c.Name == "review").Enabled);
    }

    [Fact]
    public async Task Empty_and_error_states()
    {
        var empty = new ManagementKit();
        await empty.Pages.Commands.ActivateAsync();
        Assert.True(empty.Pages.Commands.ShowsEmpty);

        var failing = new ManagementKit();
        failing.Commands.Failure = R.Fail<CommandsListAllResult>(ActionOutcome.Forbidden);
        await failing.Pages.Commands.ActivateAsync();
        Assert.Equal(PageState.Error, failing.Pages.Commands.State);
    }
}

public sealed class HooksPageTests
{
    private static async Task<ManagementKit> Open(string role = "owner")
    {
        var kit = new ManagementKit(role);
        kit.Hooks.Hooks.AddRange([FakeHooks.Hook("h1"), FakeHooks.Hook("h2", "Stop", "project", false, null, "notify", ["API_KEY"])]);
        await kit.Pages.Hooks.ActivateAsync();
        return kit;
    }

    [Fact]
    public async Task A_viewer_cannot_read_hooks_a_member_reads_but_cannot_write_and_an_owner_can_edit()
    {
        var viewer = await Open("viewer");
        Assert.Equal(PageState.Forbidden, viewer.Pages.Hooks.State);

        var member = await Open("member");
        Assert.Equal(2, member.Pages.Hooks.Hooks.Count);
        Assert.True(member.Pages.Hooks.IsReadOnly);
        member.Pages.Hooks.NewCommand.Execute(null);
        Assert.False(member.Pages.Hooks.Editor.IsOpen);
        member.Pages.Hooks.Hooks[0].EditCommand.Execute(null);
        Assert.False(member.Pages.Hooks.Editor.IsOpen);

        var owner = await Open();
        owner.Pages.Hooks.Hooks[0].EditCommand.Execute(null);
        Assert.True(owner.Pages.Hooks.Editor.IsOpen);
        Assert.False(owner.Pages.Hooks.Editor.IsNew);
    }

    [Fact]
    public async Task The_editor_validates_event_command_timeout_and_env_names()
    {
        var kit = await Open();
        kit.Pages.Hooks.NewCommand.Execute(null);
        var e = kit.Pages.Hooks.Editor;
        Assert.Contains(HookProblem.EmptyCommand, e.Problems);
        Assert.False(e.CanSave);

        e.Command = "echo ok";
        e.TimeoutSeconds = 0;
        Assert.Contains(HookProblem.Timeout, e.Problems);
        e.TimeoutSeconds = 601;
        Assert.Contains(HookProblem.Timeout, e.Problems);
        e.TimeoutSeconds = 20;

        kit.Pages.Hooks.AddEnvCommand.Execute(null);
        e.Env[0].Name = "1BAD";
        Assert.Contains(HookProblem.EnvName, e.Problems);
        e.Env[0].Name = "API_KEY";
        Assert.Empty(e.Problems);
        Assert.True(e.CanSave);

        e.Type = HookNames.TypeCommand;
        e.Type = HookNames.TypePrompt;
        Assert.Contains(HookProblem.EmptyPrompt, e.Problems);
    }

    [Fact]
    public async Task Editing_an_env_name_or_a_secret_value_revalidates_the_form_immediately()
    {
        var kit = await Open();
        kit.Pages.Hooks.NewCommand.Execute(null);
        var hook = kit.Pages.Hooks.Editor;
        hook.Command = "echo";
        kit.Pages.Hooks.AddEnvCommand.Execute(null);
        var raised = new List<string?>();
        hook.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        hook.Env[0].Name = "9bad";
        Assert.Contains(nameof(HookEditorViewModel.Problems), raised);
        Assert.False(hook.CanSave);
        hook.Env[0].Name = "GOOD";
        Assert.True(hook.CanSave);

        kit.Pages.Mcp.NewCommand.Execute(null);
        var mcp = kit.Pages.Mcp.Editor;
        (mcp.Id, mcp.Name, mcp.Command) = ("x", "X", "node");
        kit.Pages.Mcp.AddSecretCommand.Execute(null);
        mcp.Secrets[0].Name = "TOKEN";
        Assert.Empty(mcp.Problems);
        mcp.Secrets[0].Name = "";
        Assert.Contains(McpProblem.SecretName, mcp.Problems);
        mcp.Secrets[0].Name = "TOKEN";
        kit.Pages.Mcp.AddSecretCommand.Execute(null);
        mcp.Secrets[1].Name = "B";
        mcp.Secrets[0].Value = "v";
        Assert.Contains(McpProblem.SecretsIncomplete, mcp.Problems);
    }

    [Fact]
    public async Task Saving_sends_env_values_once_keeps_unset_values_and_clears_the_inputs()
    {
        var kit = await Open();
        kit.Pages.Hooks.Hooks.Single(h => h.Dto.Id == "h2").EditCommand.Execute(null);
        var e = kit.Pages.Hooks.Editor;
        Assert.Equal("API_KEY", Assert.Single(e.Env).Name);
        Assert.True(e.Env[0].HadValue);

        // 留空 = 沿用已存的值(Value 为 null);新加的变量带值
        kit.Pages.Hooks.AddEnvCommand.Execute(null);
        e.Env[1].Name = "NEW_VAR";
        e.Env[1].Value = "top-secret";
        await kit.Pages.Hooks.SaveCommand.ExecuteAsync(null);

        var (scope, workspace, draft) = Assert.Single(kit.Hooks.Saved);
        Assert.Equal(HookNames.ScopeProject, scope);
        Assert.Equal(ManagementRig.Workspace, workspace);
        Assert.Equal("h2", draft.Id);
        Assert.Null(draft.Env![0].Value);
        Assert.Equal("top-secret", draft.Env[1].Value);
        Assert.All(e.Env, row => Assert.Equal("", row.Value));
        Assert.False(e.IsOpen);
    }

    [Fact]
    public async Task A_soft_warning_keeps_the_editor_open_and_a_save_failure_shows_its_key()
    {
        var kit = await Open();
        kit.Hooks.Warning = "hooks.warn.weakMatcher";
        kit.Pages.Hooks.NewCommand.Execute(null);
        kit.Pages.Hooks.Editor.Command = "echo hi";
        await kit.Pages.Hooks.SaveCommand.ExecuteAsync(null);
        Assert.Equal("hooks.warn.weakMatcher", kit.Pages.Hooks.Editor.WarningKey);
        Assert.True(kit.Pages.Hooks.Editor.IsOpen);

        kit.Hooks.SaveFailure = R.Fail<HooksSaveResult>(ActionOutcome.Invalid, "hooks.error.badMatcher");
        await kit.Pages.Hooks.SaveCommand.ExecuteAsync(null);
        Assert.Equal("hooks.error.badMatcher", kit.Pages.Hooks.Editor.SaveFailure?.Key);
    }

    [Fact]
    public async Task A_project_hook_without_a_project_is_refused_and_test_run_shows_the_report()
    {
        var kit = await Open();
        kit.Pages.Hooks.NewCommand.Execute(null);
        kit.Pages.Hooks.Editor.Command = "echo hi";
        kit.Pages.Hooks.Editor.Scope = HookNames.ScopeProject;
        kit.Stores.Workspace.Update(s => s with { Projects = s.Projects.Clear() });
        await kit.Pages.Hooks.SaveCommand.ExecuteAsync(null);
        Assert.Equal("hooks.error.noWorkspace", kit.Pages.Hooks.Editor.SaveFailure?.Key);
        Assert.Empty(kit.Hooks.Saved);

        await kit.Pages.Hooks.TestCommand.ExecuteAsync(null);
        Assert.Equal(42, kit.Pages.Hooks.TestReport!.DurationMs);
        Assert.Single(kit.Hooks.Tested);
    }

    [Fact]
    public async Task Delete_needs_a_confirmation_and_toggle_calls_the_device()
    {
        var kit = await Open();
        kit.Pages.Hooks.Hooks[0].AskDeleteCommand.Execute(null);
        Assert.True(kit.Pages.Hooks.HasPendingDelete);
        kit.Pages.Hooks.CancelDeleteCommand.Execute(null);
        Assert.Empty(kit.Hooks.Deleted);

        kit.Pages.Hooks.Hooks[0].AskDeleteCommand.Execute(null);
        await kit.Pages.Hooks.ConfirmDeleteCommand.ExecuteAsync(null);
        Assert.Equal(["h1"], kit.Hooks.Deleted);

        await kit.Pages.Hooks.Hooks[0].ToggleCommand.ExecuteAsync(null);
        Assert.True(kit.Pages.Hooks.Hooks[0].Enabled);
    }
}

public sealed class McpPageTests
{
    private static async Task<ManagementKit> Open(string role = "owner")
    {
        var kit = new ManagementKit(role);
        kit.Mcp.Servers.AddRange([FakeMcp.Server("fs"), FakeMcp.Server("web", McpStates.Failed, McpTransports.StreamableHttp, "mcp.connect.timeout"), FakeMcp.Server("off", McpStates.Disabled, enabled: false)]);
        kit.Mcp.Push();
        await kit.Pages.Mcp.ActivateAsync();
        return kit;
    }

    [Fact]
    public async Task It_shows_connection_state_and_loads_only_when_the_topic_has_arrived()
    {
        var early = new ManagementKit();
        await early.Pages.Mcp.ActivateAsync();
        Assert.Equal(PageState.Loading, early.Pages.Mcp.State);

        var kit = await Open();
        var rows = kit.Pages.Mcp.Servers;
        Assert.Equal(McpConnection.Connected, rows.Single(r => r.Id == "fs").Connection);
        Assert.Equal(3, rows.Single(r => r.Id == "fs").ToolCount);
        Assert.Equal(McpConnection.Failed, rows.Single(r => r.Id == "web").Connection);
        Assert.Equal("mcp.connect.timeout", rows.Single(r => r.Id == "web").ErrorKey);
        Assert.Equal(McpConnection.Disabled, rows.Single(r => r.Id == "off").Connection);
    }

    [Fact]
    public async Task A_disconnected_device_marks_every_server_unknown_and_hides_the_tool_counts()
    {
        var kit = await Open();
        kit.Rig.SetConnection(DeviceConnection.Offline);
        Assert.Equal(PageState.Unknown, kit.Pages.Mcp.State);
        Assert.All(kit.Pages.Mcp.Servers, r => Assert.Equal(McpConnection.Unknown, r.Connection));
        Assert.All(kit.Pages.Mcp.Servers, r => Assert.Equal(0, r.ToolCount));
    }

    [Fact]
    public async Task Viewer_is_forbidden_member_is_read_only_and_owner_can_toggle_and_reconnect()
    {
        Assert.Equal(PageState.Forbidden, (await Open("viewer")).Pages.Mcp.State);

        var member = await Open("member");
        Assert.True(member.Pages.Mcp.IsReadOnly);
        await member.Pages.Mcp.Servers[0].ReconnectCommand.ExecuteAsync(null);
        Assert.Empty(member.Mcp.Reconnected);

        var owner = await Open();
        await owner.Pages.Mcp.Servers.Single(r => r.Id == "fs").ReconnectCommand.ExecuteAsync(null);
        await owner.Pages.Mcp.Servers.Single(r => r.Id == "off").ToggleCommand.ExecuteAsync(null);
        Assert.Equal(["fs"], owner.Mcp.Reconnected);
        Assert.Equal(("off", true), owner.Mcp.Toggled.Single());
        Assert.Equal(McpConnection.Connected, owner.Pages.Mcp.Servers.Single(r => r.Id == "off").Connection);
    }

    [Fact]
    public async Task The_editor_validates_id_name_command_url_and_secret_names()
    {
        var kit = await Open();
        kit.Pages.Mcp.NewCommand.Execute(null);
        var e = kit.Pages.Mcp.Editor;
        Assert.Contains(McpProblem.Id, e.Problems);
        Assert.Contains(McpProblem.Command, e.Problems);

        e.Id = "my-server";
        e.Name = "My server";
        e.Command = "node";
        Assert.Empty(e.Problems);

        e.Transport = McpTransports.StreamableHttp;
        Assert.Contains(McpProblem.Url, e.Problems);
        e.Url = "ftp://x";
        Assert.Contains(McpProblem.Url, e.Problems);
        e.Url = "https://user:pw@host/mcp";
        Assert.Contains(McpProblem.UrlCredentials, e.Problems);
        e.Url = "https://host/mcp";
        Assert.Empty(e.Problems);

        kit.Pages.Mcp.AddSecretCommand.Execute(null);
        e.Secrets[0].Name = "bad name";
        Assert.Contains(McpProblem.SecretName, e.Problems);
        e.Secrets[0].Name = "Authorization";
        Assert.Empty(e.Problems);
    }

    [Fact]
    public async Task Secrets_are_written_as_one_complete_set_once_and_the_inputs_are_cleared()
    {
        var kit = await Open();
        kit.Mcp.SecretInfo = [new McpSecretEntryDto("TOKEN", true), new McpSecretEntryDto("EXTRA", false)];
        await kit.Pages.Mcp.Servers.Single(r => r.Id == "fs").EditCommand.ExecuteAsync(null);
        var e = kit.Pages.Mcp.Editor;
        Assert.Equal(["TOKEN"], e.Secrets.Select(s => s.Name)); // 键名来自配置
        Assert.True(e.Secrets[0].IsSet);

        // 只填了一部分:写密钥是整张 map,漏掉的会丢,所以不让保存
        kit.Pages.Mcp.AddSecretCommand.Execute(null);
        e.Secrets[1].Name = "OTHER";
        e.Secrets[0].Value = "t0ken";
        Assert.Contains(McpProblem.SecretsIncomplete, e.Problems);
        await kit.Pages.Mcp.SaveCommand.ExecuteAsync(null);
        Assert.Empty(kit.Mcp.Upserts);

        e.Secrets[1].Value = "other";
        await kit.Pages.Mcp.SaveCommand.ExecuteAsync(null);
        Assert.Single(kit.Mcp.Upserts);
        var write = Assert.Single(kit.Mcp.SecretWrites);
        Assert.Equal(new Dictionary<string, string> { ["TOKEN"] = "t0ken", ["OTHER"] = "other" }, write.Values);
        Assert.All(e.Secrets, s => Assert.Equal("", s.Value));
        Assert.False(e.IsOpen);
    }

    [Fact]
    public async Task Leaving_every_secret_empty_keeps_the_saved_ones_and_writes_no_secrets()
    {
        var kit = await Open();
        await kit.Pages.Mcp.Servers.Single(r => r.Id == "fs").EditCommand.ExecuteAsync(null);
        await kit.Pages.Mcp.SaveCommand.ExecuteAsync(null);
        Assert.Single(kit.Mcp.Upserts);
        Assert.Empty(kit.Mcp.SecretWrites);
    }

    [Fact]
    public async Task Test_connection_reports_the_result_and_remove_needs_a_confirmation_and_browsing_needs_a_connection()
    {
        var kit = await Open();
        await kit.Pages.Mcp.Servers.Single(r => r.Id == "fs").TestCommand.ExecuteAsync(null);
        Assert.True(kit.Pages.Mcp.TestResult!.Ok);

        kit.Pages.Mcp.Servers.Single(r => r.Id == "off").AskRemoveCommand.Execute(null);
        Assert.True(kit.Pages.Mcp.HasPendingRemove);
        kit.Pages.Mcp.CancelRemoveCommand.Execute(null);
        Assert.Empty(kit.Mcp.Removed);
        kit.Pages.Mcp.Servers.Single(r => r.Id == "off").AskRemoveCommand.Execute(null);
        await kit.Pages.Mcp.ConfirmRemoveCommand.ExecuteAsync(null);
        Assert.Equal(["off"], kit.Mcp.Removed);

        await kit.Pages.Mcp.Servers.Single(r => r.Id == "web").BrowseCommand.ExecuteAsync(null);
        Assert.Equal("mcp.notConnected", kit.Pages.Mcp.BrowseFailure?.Key);
        await kit.Pages.Mcp.Servers.Single(r => r.Id == "fs").BrowseCommand.ExecuteAsync(null);
        Assert.Single(kit.Pages.Mcp.Resources);
        Assert.Single(kit.Pages.Mcp.Prompts);
        Assert.Null(kit.Pages.Mcp.BrowseFailure);
    }
}
