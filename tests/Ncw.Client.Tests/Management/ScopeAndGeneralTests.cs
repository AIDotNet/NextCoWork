using Ncw.Client.Actions;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Tests.Management;

/// <summary>设置页的权限判断(viewer 只读 / member 部分可写 / owner 全部 / 仅本机)与作用域切换器。</summary>
public sealed class AccessAndScopeTests
{
    [Theory]
    [InlineData("viewer", Need.Viewer, true, GateReason.None)]
    [InlineData("viewer", Need.Member, true, GateReason.NeedsMember)]
    [InlineData("viewer", Need.Owner, true, GateReason.NeedsOwner)]
    [InlineData("member", Need.Member, true, GateReason.None)]
    [InlineData("member", Need.Owner, true, GateReason.NeedsOwner)]
    [InlineData("owner", Need.Owner, false, GateReason.None)]
    [InlineData("owner", Need.LocalOwner, false, GateReason.LocalOnly)]
    [InlineData("owner", Need.LocalOwner, true, GateReason.None)]
    [InlineData("member", Need.LocalOwner, true, GateReason.NeedsOwner)]
    public void Gate_follows_the_role_and_the_channel(string role, Need need, bool local, GateReason expected) =>
        Assert.Equal(expected, ManagementAccess.Evaluate(DeviceConnection.Connected, role, local, need).Reason);

    [Fact]
    public void A_switcher_resetting_its_items_does_not_make_every_page_reload_but_a_real_switch_does()
    {
        var rig = new ManagementRig();
        var changes = 0;
        rig.Scope.Changed += () => changes++;
        Assert.Equal(ManagementRig.Workspace, rig.Scope.WorkspaceId);

        // 切换器换数据源时先写 null、再写回原值(Avalonia 的 SelectingItemsControl):作用域没变,不通知
        rig.Scope.WorkspaceId = null;
        rig.Scope.WorkspaceId = ManagementRig.Workspace;
        rig.Scope.DeviceId = null;
        Assert.Equal(ManagementRig.Device, rig.Scope.DeviceId); // 设备不能是空:重新选回来
        Assert.Equal(0, changes);

        rig.Scope.WorkspaceId = "ws2"; // 用户真的换了项目
        Assert.Equal(1, changes);
        rig.Scope.WorkspaceId = "ws2";
        Assert.Equal(1, changes);
    }

    [Theory]
    [InlineData(DeviceConnection.Offline)]
    [InlineData(DeviceConnection.Connecting)]
    [InlineData(DeviceConnection.Sleeping)]
    public void A_device_that_is_not_connected_gates_everything_as_offline(DeviceConnection connection) =>
        Assert.Equal(GateReason.Offline, ManagementAccess.Evaluate(connection, "owner", true, Need.Viewer).Reason);

    [Fact]
    public void Scope_defaults_to_the_connected_device_and_a_project_on_it_and_follows_role_and_connection()
    {
        var kit = new ManagementKit("member");
        var scope = kit.Pages.Scope;
        var changes = 0;
        scope.Changed += () => changes++;

        Assert.Equal(ManagementRig.Device, scope.DeviceId);
        Assert.NotNull(scope.WorkspaceId);
        Assert.Equal(2, scope.ProjectChoices.Count);
        Assert.Equal("member", scope.Role);
        Assert.True(scope.IsLocalChannel);

        kit.Rig.SetRole("owner");
        Assert.Equal("owner", scope.Role);
        Assert.True(changes > 0);

        scope.WorkspaceId = "ws2";
        Assert.Equal("ws2", scope.WorkspaceId);

        kit.Rig.SetConnection(DeviceConnection.Offline);
        Assert.False(scope.IsConnected);
        Assert.Equal(GateReason.Offline, scope.GateFor(Need.Viewer).Reason);
        Assert.Equal(ManagementRig.Device, scope.DeviceId); // 断开不换设备:状态未知,不是消失
    }

    [Fact]
    public void Selecting_a_project_in_the_main_window_moves_the_scope_default()
    {
        var kit = new ManagementKit();
        kit.Stores.Selection.Update(_ => new SelectionState(new ProjectRef(ManagementRig.Device, "ws2"), null));
        kit.Rig.Scope.WorkspaceId = null; // 强制重新选
        kit.Stores.Selection.Update(_ => new SelectionState(new ProjectRef(ManagementRig.Device, "ws2"), "s"));
        Assert.Equal("ws2", kit.Rig.Scope.WorkspaceId);
    }
}

public sealed class GeneralSettingsTests
{
    private static async Task<ManagementKit> Ready(string role = "owner")
    {
        var kit = new ManagementKit(role);
        kit.Settings.Push(FakeSettings.Defaults());
        await kit.Pages.General.ActivateAsync();
        return kit;
    }

    [Fact]
    public async Task It_is_loading_until_the_settings_topic_snapshot_arrives_then_ready()
    {
        var kit = new ManagementKit();
        await kit.Pages.General.ActivateAsync();
        Assert.Equal(PageState.Loading, kit.Pages.General.State);

        kit.Settings.Push(FakeSettings.Defaults() with { GoalCheckinMinutes = 45 }, 2);
        Assert.Equal(PageState.Ready, kit.Pages.General.State);
        Assert.Equal(45, kit.Pages.General.GoalCheckin);
    }

    [Fact]
    public async Task A_disconnected_device_is_unknown_not_empty_and_a_viewer_cannot_read()
    {
        var kit = await Ready();
        kit.Rig.SetConnection(DeviceConnection.Offline);
        Assert.Equal(PageState.Unknown, kit.Pages.General.State);

        kit.Rig.SetConnection(DeviceConnection.Connected);
        Assert.Equal(PageState.Ready, kit.Pages.General.State);

        kit.Rig.SetRole("viewer");
        Assert.Equal(PageState.Forbidden, kit.Pages.General.State);
        Assert.Equal(GateReason.NeedsMember, kit.Pages.General.ReadGate.Reason);
    }

    [Fact]
    public async Task A_member_reads_but_cannot_write_and_nothing_is_sent()
    {
        var kit = await Ready("member");
        var page = kit.Pages.General;

        Assert.True(page.IsReadOnly);
        Assert.Equal(GateReason.NeedsOwner, page.WriteGate.Reason);
        await page.ToggleFailoverCommand.ExecuteAsync(null);
        await page.ApplyLimitsCommand.ExecuteAsync(null);

        Assert.Empty(kit.Settings.Patches);
        Assert.False(page.Failover);
    }

    [Fact]
    public async Task A_switch_writes_a_one_field_patch_and_shows_what_the_device_pushes_back()
    {
        var kit = await Ready();
        await kit.Pages.General.ToggleFailoverCommand.ExecuteAsync(null);

        var patch = Assert.Single(kit.Settings.Patches);
        Assert.True(patch.Failover);
        Assert.Null(patch.AccountRotation);
        Assert.True(kit.Pages.General.Failover); // 来自镜像
    }

    [Fact]
    public async Task A_rejected_write_shows_the_failure_and_the_value_stays_the_devices()
    {
        var kit = await Ready();
        kit.Settings.NextFailure = R.Fail<DeviceSettingsState>(ActionOutcome.Invalid, "settings.invalid.range");

        await kit.Pages.General.ToggleAutoCompactCommand.ExecuteAsync(null);

        Assert.Equal("settings.invalid.range", kit.Pages.General.ActionFailure?.Key);
        Assert.True(kit.Pages.General.AutoCompact); // 设备上的值(出厂开)没变
        kit.Pages.General.DismissFailureCommand.Execute(null);
        Assert.Null(kit.Pages.General.ActionFailure);
    }

    [Fact]
    public async Task Number_drafts_are_validated_and_applied_as_one_patch()
    {
        var kit = await Ready();
        var page = kit.Pages.General;

        page.IdleTimeout = 30; // 低于 60
        Assert.True(page.LimitsDirty);
        Assert.True(page.LimitsInvalid);
        await page.ApplyLimitsCommand.ExecuteAsync(null);
        Assert.Empty(kit.Settings.Patches); // 不合法不发

        page.IdleTimeout = 900;
        page.GoalCheckin = 15;
        Assert.False(page.LimitsInvalid);
        await page.ApplyLimitsCommand.ExecuteAsync(null);

        var patch = Assert.Single(kit.Settings.Patches);
        Assert.Equal(900, patch.UpstreamIdleTimeoutSeconds);
        Assert.Equal(15, patch.GoalCheckinMinutes);
        Assert.False(page.LimitsDirty);
        Assert.Equal(900, page.IdleTimeout); // 再次从镜像对齐

        page.GoalCheckin = 99;
        page.ResetLimitsCommand.Execute(null);
        Assert.Equal(15, page.GoalCheckin);
    }

    [Fact]
    public async Task A_manual_proxy_needs_host_and_port_and_the_password_is_write_only_and_local_only()
    {
        var kit = await Ready();
        var page = kit.Pages.General;

        page.ProxyEnabled = true;
        page.ProxyMode = "manual";
        Assert.True(page.ProxyInvalid);
        await page.ApplyProxyCommand.ExecuteAsync(null);
        Assert.Empty(kit.Settings.Patches);

        page.ProxyHost = "127.0.0.1";
        page.ProxyPort = 7890;
        Assert.False(page.ProxyInvalid);
        await page.ApplyProxyCommand.ExecuteAsync(null);
        Assert.Equal("127.0.0.1", kit.Settings.Patches.Single().Proxy!.Host);

        page.ProxyPassword = "s3cret";
        await page.SetProxyPasswordCommand.ExecuteAsync(null);
        Assert.Equal(["s3cret"], kit.Settings.Passwords);
        Assert.Equal("", page.ProxyPassword); // 送出去就清空

        kit.Rig.MakeRemote();
        page.ProxyPassword = "again";
        Assert.Equal(GateReason.LocalOnly, page.PasswordGate.Reason);
        await page.SetProxyPasswordCommand.ExecuteAsync(null);
        Assert.Single(kit.Settings.Passwords); // 远程通道没有发
    }

    [Fact]
    public async Task Model_picks_offer_the_enabled_models_keep_a_stale_one_and_write_the_alias_with_its_provider()
    {
        var kit = new ManagementKit();
        kit.Rig.SeedModels(ManagementRig.Option("anthropic", "sonnet", "Sonnet"), ManagementRig.Option("openai", "gpt", "GPT"));
        kit.Settings.Push(FakeSettings.Defaults() with { Compact = new ModelPickDto("gone", "anthropic") });
        await kit.Pages.General.ActivateAsync();

        var compact = kit.Pages.General.Picks.Single(p => p.Field == ModelPickField.Compact);
        Assert.Contains(compact.Choices, c => c.Value == "anthropic\ngone"); // 已删除的别名照实显示
        Assert.Equal("anthropic\ngone", compact.Key);

        var reviewer = kit.Pages.General.Picks.Single(p => p.Field == ModelPickField.PermissionReviewer);
        reviewer.Key = "openai\ngpt";
        await Task.Yield();

        var pick = Assert.Single(kit.Settings.Patches).PermissionReviewer!;
        Assert.Equal(("gpt", "openai"), (pick.Model, pick.ProviderId));
    }

    [Fact]
    public async Task Personalization_over_the_limit_is_blocked()
    {
        var kit = await Ready();
        var page = kit.Pages.General;
        page.Instructions = new string('x', GeneralSettingsViewModel.InstructionsMax + 1);
        Assert.True(page.PersonalizationTooLong);
        await page.ApplyPersonalizationCommand.ExecuteAsync(null);
        Assert.Empty(kit.Settings.Patches);

        page.Instructions = "be brief";
        await page.ApplyPersonalizationCommand.ExecuteAsync(null);
        Assert.Equal("be brief", kit.Settings.Patches.Single().Personalization!.Instructions);
    }
}

public sealed class ProjectSettingsTests
{
    [Fact]
    public async Task It_loads_the_selected_projects_settings_and_follows_the_project_switcher()
    {
        var kit = new ManagementKit();
        await kit.Pages.Project.ActivateAsync();
        Assert.Equal(PageState.Ready, kit.Pages.Project.State);
        Assert.Equal("ask", kit.Pages.Project.PermissionMode);
        Assert.Equal("anthropic\nsonnet", kit.Pages.Project.ModelKey);

        kit.Settings.Project = kit.Settings.Project with { PermissionMode = "auto" };
        kit.Pages.Scope.WorkspaceId = "ws2";
        await Task.Delay(20);
        Assert.Equal("auto", kit.Pages.Project.PermissionMode);
    }

    [Fact]
    public async Task A_device_with_no_project_is_an_empty_state_not_an_error()
    {
        var kit = new ManagementKit();
        kit.Stores.Workspace.Update(s => s with { Projects = s.Projects.Clear() });
        await kit.Pages.Project.ActivateAsync();
        Assert.Equal(PageState.Ready, kit.Pages.Project.State);
        Assert.False(kit.Pages.Project.HasProjects);
    }

    [Fact]
    public async Task A_load_failure_is_an_error_state_with_the_key_and_retry_recovers()
    {
        var kit = new ManagementKit();
        kit.Settings.ProjectOverride = _ => R.Fail<ProjectSettingsDto>(ActionOutcome.NotFound, "settings.projectNotFound");
        await kit.Pages.Project.ActivateAsync();
        Assert.Equal(PageState.Error, kit.Pages.Project.State);
        Assert.Equal("settings.projectNotFound", kit.Pages.Project.LoadFailure?.Key);

        kit.Settings.ProjectOverride = null;
        await kit.Pages.Project.ReloadCommand.ExecuteAsync(null);
        Assert.Equal(PageState.Ready, kit.Pages.Project.State);
    }

    [Fact]
    public async Task Saving_sends_the_whole_form_and_clears_compaction_overrides_when_following_the_global_setting()
    {
        var kit = new ManagementKit();
        await kit.Pages.Project.ActivateAsync();
        var page = kit.Pages.Project;

        Assert.False(page.Dirty);
        page.PermissionMode = "auto";
        page.WebSearch = false;
        Assert.True(page.Dirty);
        await page.SaveCommand.ExecuteAsync(null);

        var patch = Assert.Single(kit.Settings.ProjectPatches);
        Assert.Equal("auto", patch.PermissionMode);
        Assert.False(patch.WebSearch);
        Assert.True(patch.ClearCompactModel);
        Assert.True(patch.ClearCompactThinking);
        Assert.False(page.Dirty);
    }

    [Fact]
    public async Task Changing_a_field_and_back_is_not_an_edit()
    {
        // 下拉框在页面创建时可能先回写空值再写回原值(页面按需创建,数据先到):按值比较,不留「保存 / 还原」
        var kit = new ManagementKit();
        await kit.Pages.Project.ActivateAsync();
        var page = kit.Pages.Project;
        var model = page.ModelKey;

        page.ModelKey = "";
        Assert.True(page.Dirty);
        page.ModelKey = model;
        Assert.False(page.Dirty);
    }

    [Fact]
    public async Task A_member_can_read_but_not_save()
    {
        var kit = new ManagementKit("member");
        await kit.Pages.Project.ActivateAsync();
        kit.Pages.Project.WebSearch = false;
        await kit.Pages.Project.SaveCommand.ExecuteAsync(null);
        Assert.Empty(kit.Settings.ProjectPatches);
        Assert.True(kit.Pages.Project.IsReadOnly);
    }
}
