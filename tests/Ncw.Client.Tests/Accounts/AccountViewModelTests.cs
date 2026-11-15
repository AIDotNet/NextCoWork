using Ncw.Client.Accounts;
using Ncw.Client.Devices;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;

namespace Ncw.Client.Tests.Accounts;

/// <summary>账户页 / 首次启动 / 链接路由的 ViewModel 状态机(状态都在账户会话的 Store 里,这里只看派生和动作)。</summary>
public class AccountViewModelTests
{
    private static (AccountRig Rig, ShellViewModel Shell) Make(bool withLocalDevice = false, DataStatus? data = null)
    {
        var rig = new AccountRig();
        var devices = new DeviceRegistry();
        if (withLocalDevice)
        {
            devices.Local = new DeviceLink(rig.Stores, InlineDispatcher.Instance, "local", DeviceKind.Local, "本机", _ => throw new InvalidOperationException(), new ClientHello("tests", "3.0.0"));
            rig.Stores.Workspace.Update(s => s with
            {
                Devices = s.Devices.SetItem("local", new DeviceInfo("local", "本机", DeviceKind.Local, DeviceConnection.Connected, null, data ?? new DataStatus(DataStates.Ready, null, null, null), null, "owner")),
            });
        }

        var shell = new ShellViewModel(rig.Stores, devices: devices, account: rig.Service, links: new LinkRouter(rig.Service));
        return (rig, shell);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (!condition())
        {
            Assert.True(Environment.TickCount64 < deadline, "timed out");
            await Task.Delay(5);
        }
    }

    // ───────────────────────── 首次启动 ─────────────────────────

    [Fact]
    public void Without_an_account_service_the_onboarding_never_opens_and_the_account_actions_are_inert()
    {
        var shell = new ShellViewModel(new ClientStores());
        Assert.False(shell.Onboarding.IsOpen);
        Assert.False(shell.Account.HasService);
        Assert.False(shell.Account.CanSignIn);
        shell.Account.SignInCommand.Execute(null); // 空操作,不抛
        Assert.Equal(AccountMode.Loading, shell.Stores.Account.State.Mode);
        Assert.Null(shell.Links);
    }

    [Fact]
    public async Task A_first_launch_opens_the_welcome_step_and_it_stays_closed_once_a_decision_was_made()
    {
        var (rig, shell) = Make();
        Assert.False(shell.Onboarding.IsOpen); // 还没读完账户记录
        await rig.Service.InitializeAsync();

        Assert.True(shell.Onboarding.IsOpen);
        Assert.True(shell.Onboarding.IsWelcome);
        Assert.Equal(1, shell.Onboarding.StepNumber);

        await shell.Onboarding.SkipCommand.ExecuteAsync(null);
        Assert.Equal(OnboardingStep.Data, shell.Onboarding.Step); // 做出决定:自动前进
        Assert.True(shell.Onboarding.IsOpen);                       // 流程还没走完

        // 下次启动:账户已经做过决定,不再出现
        var (service, stores) = rig.Restart();
        var next = new ShellViewModel(stores, account: service);
        await service.InitializeAsync();
        Assert.False(next.Onboarding.IsOpen);
    }

    [Fact]
    public async Task Choosing_your_own_provider_signs_nothing_in_and_the_provider_step_remembers_it()
    {
        var (rig, shell) = Make();
        await rig.Service.InitializeAsync();
        await shell.Onboarding.UseOwnProviderCommand.ExecuteAsync(null);
        Assert.Equal(AccountMode.Offline, rig.State.Mode);
        Assert.True(shell.Onboarding.WantsOwnProvider);
        Assert.Empty(rig.Api.Calls);
        Assert.Null(rig.Vault.Stored);
    }

    [Fact]
    public async Task Signing_in_from_the_welcome_step_shows_progress_then_advances_and_the_provider_step_knows_about_the_account()
    {
        var (rig, shell) = Make();
        await rig.Service.InitializeAsync();

        await shell.Account.SignInCommand.ExecuteAsync(null);
        Assert.True(shell.Account.IsLoggingIn);
        Assert.True(shell.Account.IsBrowserLogin);
        Assert.False(shell.Account.CanSignIn); // 进行中不能再发起
        Assert.True(shell.Onboarding.IsWelcome);

        await rig.Service.HandleLinkAsync(new AuthCallbackLink("C", rig.Launcher.Query("state"), null, null));
        Assert.False(shell.Account.IsLoggingIn);
        Assert.True(shell.Account.IsSignedIn);
        Assert.Equal(OnboardingStep.Data, shell.Onboarding.Step);

        shell.Onboarding.NextCommand.Execute(null);
        Assert.True(shell.Onboarding.IsProvider);
        Assert.True(shell.Onboarding.HasAccountModels);
        Assert.False(shell.Onboarding.NeedsOwnProvider);
    }

    [Fact]
    public async Task A_failed_sign_in_stays_on_the_welcome_step_with_the_reason_and_can_be_cancelled_or_dismissed()
    {
        var (rig, shell) = Make();
        await rig.Service.InitializeAsync();
        rig.Launcher.Fail = true;
        await shell.Account.SignInCommand.ExecuteAsync(null);

        Assert.True(shell.Onboarding.IsWelcome);
        Assert.True(shell.Account.HasLoginError);
        Assert.Equal(AccountKeys.BrowserFailed, shell.Account.LoginError);
        Assert.Equal(AccountBannerKind.LoginFailed, shell.Account.Banner);

        shell.Account.DismissLoginErrorCommand.Execute(null);
        Assert.False(shell.Account.HasLoginError);

        rig.Launcher.Fail = false;
        await shell.Account.SignInCommand.ExecuteAsync(null);
        shell.Account.CancelLoginCommand.Execute(null);
        Assert.False(shell.Account.IsLoggingIn);
        Assert.True(shell.Onboarding.IsWelcome);
    }

    [Fact]
    public async Task The_device_code_is_shown_on_the_account_view_model_while_waiting()
    {
        var (rig, shell) = Make();
        await rig.Service.InitializeAsync();
        await shell.Account.SignInWithCodeCommand.ExecuteAsync(null);
        Assert.True(shell.Account.IsDeviceCodeLogin);
        Assert.Equal("ABCD-EFGH", shell.Account.UserCode);
        Assert.Equal("https://auth.example.test/device", shell.Account.VerificationUri);
        shell.Account.CancelLoginCommand.Execute(null);
        Assert.Null(shell.Account.UserCode);
    }

    [Fact]
    public async Task The_data_step_waits_for_preparation_offers_retry_when_blocked_and_reports_a_takeover()
    {
        var (rig, shell) = Make(withLocalDevice: true, data: new DataStatus(DataStates.TakingOver, DataSteps.Migrating, null, null));
        await rig.Service.InitializeAsync();
        await shell.Onboarding.SkipCommand.ExecuteAsync(null);
        var onboarding = shell.Onboarding;

        Assert.True(onboarding.IsDataStep);
        Assert.True(onboarding.DataBusy);
        Assert.False(onboarding.CanContinueFromData);
        onboarding.NextCommand.Execute(null);
        Assert.True(onboarding.IsDataStep); // 接管没完成:不前进

        SetData(rig.Stores, new DataStatus(DataStates.Blocked, null, DataReasons.LegacyAppRunning, null));
        Assert.True(onboarding.DataBlocked);
        Assert.Equal(DataReasons.LegacyAppRunning, onboarding.DataReason);
        Assert.False(onboarding.CanContinueFromData);

        SetData(rig.Stores, new DataStatus(DataStates.Ready, null, null, new TakeoverSummary(4, 120, 2)));
        Assert.True(onboarding.DataReady);
        Assert.True(onboarding.HasTakeover);
        Assert.Equal((4L, 120L), (onboarding.TakenOver!.Projects, onboarding.TakenOver.Sessions));
        Assert.True(onboarding.CanContinueFromData);
        onboarding.NextCommand.Execute(null);
        Assert.True(onboarding.IsProvider);
    }

    [Fact]
    public async Task A_fresh_install_has_nothing_to_take_over_and_a_missing_local_device_means_still_connecting()
    {
        var (rig, shell) = Make();
        await rig.Service.InitializeAsync();
        await shell.Onboarding.SkipCommand.ExecuteAsync(null);
        Assert.True(shell.Onboarding.DataConnecting); // 本机设备还没连上:不是「没有数据」

        var (rig2, shell2) = Make(withLocalDevice: true);
        await rig2.Service.InitializeAsync();
        await shell2.Onboarding.SkipCommand.ExecuteAsync(null);
        Assert.True(shell2.Onboarding.DataReady);
        Assert.False(shell2.Onboarding.HasTakeover);
    }

    [Fact]
    public async Task The_provider_step_opens_the_models_settings_and_the_done_step_opens_the_first_project()
    {
        var (rig, shell) = Make(withLocalDevice: true);
        await rig.Service.InitializeAsync();
        await shell.Onboarding.SkipCommand.ExecuteAsync(null);
        shell.Onboarding.NextCommand.Execute(null);
        Assert.False(shell.Onboarding.HasAccountModels);
        Assert.True(shell.Onboarding.NeedsOwnProvider);

        shell.Onboarding.OpenModelSettingsCommand.Execute(null);
        Assert.True(shell.Settings.IsOpen);
        Assert.Equal(SettingsSection.Models, shell.Settings.Section);
        Assert.True(shell.Onboarding.IsProvider); // 流程停在这一步

        var opened = 0;
        var onboarding = new OnboardingViewModel(rig.Stores, shell.Account, shell.DataGate, shell.Settings, () => { opened++; return Task.CompletedTask; });
        onboarding.NextCommand.Execute(null); // Welcome 已经决定了:构造时账户已是 Offline,Step 仍是 Welcome 之后由 Refresh 推进
        await onboarding.OpenFolderCommand.ExecuteAsync(null);
        Assert.Equal(1, opened);
        Assert.False(onboarding.IsOpen);
    }

    [Fact]
    public async Task Back_never_returns_to_the_welcome_step_and_finishing_closes_the_flow()
    {
        var (rig, shell) = Make(withLocalDevice: true);
        await rig.Service.InitializeAsync();
        await shell.Onboarding.SkipCommand.ExecuteAsync(null);
        shell.Onboarding.BackCommand.Execute(null);
        Assert.True(shell.Onboarding.IsDataStep);

        shell.Onboarding.NextCommand.Execute(null);
        shell.Onboarding.NextCommand.Execute(null);
        Assert.True(shell.Onboarding.IsDone);
        shell.Onboarding.BackCommand.Execute(null);
        Assert.True(shell.Onboarding.IsProvider);
        shell.Onboarding.NextCommand.Execute(null);
        shell.Onboarding.FinishCommand.Execute(null);
        Assert.False(shell.Onboarding.IsOpen);
    }

    private static void SetData(ClientStores stores, DataStatus status) =>
        stores.Workspace.Update(s => s with { Devices = s.Devices.SetItem("local", s.Devices["local"] with { Data = status }) });

    // ───────────────────────── 账户页 ─────────────────────────

    [Fact]
    public async Task The_signed_in_page_projects_the_user_the_teams_and_the_current_team()
    {
        var (rig, shell) = Make();
        rig.Api.Teams = new TeamContext([FakeAccountApi.Team("t1", "Acme"), FakeAccountApi.Team("t2", "Beta")], "t1");
        await rig.SignInAsync();
        var account = shell.Account;

        Assert.Equal(("Alice", "alice@example.com", "A"), (account.UserLabel, account.Email, account.Initial));
        Assert.Equal(["Acme", "Beta"], account.Teams.Select(t => t.Name));
        Assert.Equal("Acme", account.SelectedTeamName);
        Assert.True(account.HasMultipleTeams);
        Assert.False(account.NeedsTeam);
        Assert.True(account.IsActive);

        await account.SelectTeamCommand.ExecuteAsync("t2");
        Assert.Equal("Beta", account.SelectedTeamName);
        Assert.Equal([false, true], account.Teams.Select(t => t.IsSelected));

        await account.SelectTeamCommand.ExecuteAsync("t2"); // 已经是当前团队:不重复请求
        Assert.Equal(["t1", "t2"], rig.Api.SelectedTeams.Distinct().Order());
        Assert.Null(account.ActionError);
    }

    [Fact]
    public async Task Signing_out_needs_a_confirmation_and_then_returns_to_the_signed_out_page()
    {
        var (rig, shell) = Make();
        await rig.SignInAsync();
        var account = shell.Account;

        account.AskSignOutCommand.Execute(null);
        Assert.True(account.ConfirmingSignOut);
        account.CancelSignOutCommand.Execute(null);
        Assert.False(account.ConfirmingSignOut);
        Assert.True(account.IsSignedIn);

        account.AskSignOutCommand.Execute(null);
        await account.ConfirmSignOutCommand.ExecuteAsync(null);
        Assert.False(account.ConfirmingSignOut);
        Assert.True(account.IsSignedOut);
        Assert.False(account.IsSignedIn);
        Assert.Equal(AccountMode.Offline, account.Mode);
        Assert.Empty(account.Teams);
        Assert.Null(account.UserLabel);
    }

    [Fact]
    public async Task An_expired_session_keeps_the_name_hides_the_account_only_content_and_forgets_the_controller_list()
    {
        var (rig, shell) = Make();
        rig.Api.Controllers.Add(new ControllerSession("c1", "MacBook", "macOS", rig.Clock.GetUtcNow(), true));
        await rig.SignInAsync();
        await shell.Account.LoadControllersCommand.ExecuteAsync(null);
        Assert.True(shell.Account.ControllersReady);
        Assert.Single(shell.Account.Controllers);

        rig.Api.OnRefresh = _ => throw new AccountApiException(AccountApiErrorKind.InvalidGrant, "bad", 400, "invalid_grant");
        rig.Clock.Advance(TimeSpan.FromMinutes(14));
        await rig.Service.RefreshAsync();

        var account = shell.Account;
        Assert.True(account.IsExpired);
        Assert.False(account.IsActive);
        Assert.Equal("Alice", account.UserLabel);
        Assert.Equal(AccountBannerKind.Expired, account.Banner);
        Assert.True(account.ControllersSignedOut);   // 规则 7:过期之后不显示旧的控制端列表
        Assert.Empty(account.Controllers);
    }

    [Fact]
    public async Task The_controller_list_loads_marks_this_app_and_a_revoke_needs_a_confirmation()
    {
        var (rig, shell) = Make();
        rig.Api.Controllers.AddRange([
            new ControllerSession("c2", "iPhone", "iOS", rig.Clock.GetUtcNow().AddDays(-3), false),
            new ControllerSession("c1", "MacBook", "macOS", rig.Clock.GetUtcNow(), true),
        ]);
        await rig.SignInAsync();
        var account = shell.Account;
        await account.LoadControllersCommand.ExecuteAsync(null);

        Assert.Equal(["MacBook", "iPhone"], account.Controllers.Select(c => c.Name)); // 这个应用排第一
        Assert.True(account.Controllers[0].IsCurrent);
        Assert.True(account.Controllers[1].IsMobile);
        Assert.False(account.Controllers[0].CanRevoke);

        account.AskRevokeCommand.Execute(account.Controllers[0]); // 当前这个用「退出登录」,不能在这里撤销
        Assert.False(account.HasConfirmingRevoke);

        account.AskRevokeCommand.Execute(account.Controllers[1]);
        Assert.True(account.HasConfirmingRevoke);
        account.CancelRevokeCommand.Execute(null);
        Assert.Equal(2, rig.Api.Controllers.Count);

        account.AskRevokeCommand.Execute(account.Controllers[1]);
        await account.ConfirmRevokeCommand.ExecuteAsync(null);
        Assert.Equal(["c1"], rig.Api.Controllers.Select(c => c.Id));
        Assert.Equal(["MacBook"], account.Controllers.Select(c => c.Name));
        Assert.True(account.ControllersEmptyOthers);
    }

    [Fact]
    public async Task A_controller_list_that_cannot_be_read_says_why_and_can_be_retried()
    {
        var (rig, shell) = Make();
        await rig.SignInAsync();
        rig.Api.OnRefresh = _ => throw new AccountApiException(AccountApiErrorKind.Network, "down");
        rig.Clock.Advance(TimeSpan.FromMinutes(16)); // 访问令牌已经过期又刷新不了
        await shell.Account.LoadControllersCommand.ExecuteAsync(null);
        Assert.True(shell.Account.ControllersFailed);
        Assert.Equal(AccountKeys.Unreachable, shell.Account.ControllersError);
        Assert.Equal(AccountBannerKind.Unreachable, shell.Account.Banner);

        rig.Api.OnRefresh = null;
        await shell.Account.LoadControllersCommand.ExecuteAsync(null);
        Assert.True(shell.Account.ControllersReady);
    }

    [Fact]
    public async Task A_controller_list_that_arrives_after_sign_out_is_dropped()
    {
        var (rig, shell) = Make();
        await rig.SignInAsync();
        var gate = new TaskCompletionSource();
        rig.Api.ControllersGate = gate.Task;
        rig.Api.Controllers.Add(new ControllerSession("c1", "MacBook", "macOS", null, true));

        var load = shell.Account.LoadControllersCommand.ExecuteAsync(null); // 慢请求还没返回
        await rig.Service.SignOutAsync();                                    // 期间退出登录
        gate.SetResult();
        await load;

        Assert.True(shell.Account.ControllersSignedOut);
        Assert.Empty(shell.Account.Controllers);
    }

    [Fact]
    public async Task The_local_agent_link_state_is_derived_from_the_delegation_statuses()
    {
        var (rig, shell) = Make();
        rig.ConnectLocalDevice();
        await rig.SignInAsync();
        Assert.True(shell.Account.AgentLinkAttached);
        Assert.True(shell.Account.HasAgentLink);

        rig.Stores.Account.Update(s => s with { Delegations = s.Delegations.SetItem("local", new DeviceDelegation(DelegationStatus.Failed)) });
        Assert.True(shell.Account.AgentLinkFailed);
        rig.Stores.Account.Update(s => s with { Delegations = s.Delegations.SetItem("local", new DeviceDelegation(DelegationStatus.Pending)) });
        Assert.True(shell.Account.AgentLinkPending);
        rig.Stores.Account.Update(s => s with { Delegations = s.Delegations.Clear() });
        Assert.False(shell.Account.HasAgentLink);
    }

    [Fact]
    public async Task The_secure_storage_warning_and_team_required_banner_are_reflected()
    {
        var (rig, shell) = Make();
        rig.Vault.Unsupported = true;
        await rig.SignInAsync();
        Assert.True(shell.Account.SecureStorageUnavailable);
        Assert.Equal(AccountBannerKind.SecureStorageUnavailable, shell.Account.Banner);
        Assert.True(shell.Account.ShowsBanner);
    }

    [Fact]
    public async Task Opening_the_account_tab_loads_the_controllers_and_the_status_bar_command_opens_the_tab()
    {
        var (rig, shell) = Make();
        rig.Api.Controllers.Add(new ControllerSession("c1", "MacBook", "macOS", null, true));
        await rig.SignInAsync();

        shell.OpenAccountSettingsCommand.Execute(null);
        Assert.True(shell.Settings.IsOpen);
        Assert.Equal(SettingsSection.Account, shell.Settings.Section);
        await WaitUntil(() => shell.Account.ControllersReady);
        Assert.True(shell.Settings.IsAccountSection);
    }

    [Fact]
    public void The_plugin_pages_sign_in_prompt_opens_the_account_tab()
    {
        var (_, shell) = Make();
        Assert.False(shell.Settings.IsOpen);
        shell.Settings.Pages.Plugins.SignInCommand.Execute(null); // 市场安装返回 plugins.authRequired 时提示卡上的「去登录」
        Assert.True(shell.Settings.IsOpen);
        Assert.Equal(SettingsSection.Account, shell.Settings.Section);
    }

    // ───────────────────────── 链接路由 ─────────────────────────

    [Fact]
    public async Task The_link_router_hands_auth_callbacks_to_the_login_and_only_records_pair_invites_for_confirmation()
    {
        var (rig, shell) = Make();
        await rig.Service.InitializeAsync();
        await rig.Service.BeginBrowserLoginAsync();
        var router = shell.Links!;
        var invites = new List<string>();
        router.PairRequested += invites.Add;

        // 回调(带正确的 state)完成登录
        Assert.Equal(LinkOutcome.Accepted, await router.HandleAsync($"nextcowork://auth/callback?code=C1&state={Uri.EscapeDataString(rig.Launcher.Query("state"))}"));
        Assert.True(rig.State.IsSignedIn);

        // 配对链接:只记下邀请,不配对
        Assert.Equal(LinkOutcome.Accepted, await router.HandleAsync("nextcowork://pair?invite=INVITE-123"));
        Assert.Equal("INVITE-123", router.PendingPairInvite);
        Assert.Equal(["INVITE-123"], invites);
        router.ClearPendingPair();
        Assert.Null(router.PendingPairInvite);
    }

    [Theory]
    [InlineData("nextcowork://open?project=p1")]      // 后续版本:只解析不执行
    [InlineData("nextcowork://settings/reset")]
    [InlineData("https://evil.test/auth/callback?code=c&state=s")]
    [InlineData("nextcowork://auth/callback?code=c&code=d&state=s")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Everything_else_is_ignored_without_touching_the_account(string? raw)
    {
        var (rig, shell) = Make();
        await rig.Service.InitializeAsync();
        var before = rig.State;
        Assert.Equal(LinkOutcome.Ignored, await shell.Links!.HandleAsync(raw));
        Assert.Equal(before, rig.State);
        Assert.Empty(rig.Api.Calls);
        Assert.Null(shell.Links.PendingPairInvite);
    }
}
