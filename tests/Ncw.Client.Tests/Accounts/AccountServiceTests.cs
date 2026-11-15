using System.Collections.Immutable;
using Ncw.Client.Accounts;
using Ncw.Client.Stores;

namespace Ncw.Client.Tests.Accounts;

/// <summary>登录状态机:PKCE 流程、state 校验、回跳参数、团队、设备授权码、令牌刷新与失效、离线、退出、委托令牌。后端是假的,时钟是手动的。</summary>
public class AccountServiceTests
{
    // ───────────────────────── 启动 ─────────────────────────

    [Fact]
    public async Task First_launch_with_no_account_record_is_undecided()
    {
        var rig = new AccountRig();
        Assert.Equal(AccountMode.Loading, rig.State.Mode);
        await rig.Service.InitializeAsync();
        Assert.Equal(AccountMode.Undecided, rig.State.Mode);
        Assert.Equal(AccountBannerKind.None, AccountBanner.Of(rig.State));
    }

    [Fact]
    public async Task Choosing_offline_is_remembered_across_restarts()
    {
        var rig = new AccountRig();
        await rig.Service.InitializeAsync();
        await rig.Service.UseOfflineAsync();
        Assert.Equal(AccountMode.Offline, rig.State.Mode);
        Assert.Equal("offline", rig.Meta.Stored!.Mode);

        var (_, stores) = rig.Restart();
        await new AccountService(stores, rig.Api, rig.Vault, rig.Meta, () => rig.Launcher, clock: rig.Clock).InitializeAsync();
        Assert.Equal(AccountMode.Offline, stores.Account.State.Mode);
        Assert.Empty(rig.Api.Calls); // 离线:不碰后端
    }

    // ───────────────────────── 浏览器登录 ─────────────────────────

    [Fact]
    public async Task The_browser_login_opens_the_authorize_page_and_exchanges_the_code_with_the_matching_verifier()
    {
        var rig = new AccountRig();
        rig.ConnectLocalDevice();
        await rig.SignInAsync("CODE-1");

        Assert.Equal("S256", rig.Launcher.Query("code_challenge_method"));
        var (code, verifier) = Assert.Single(rig.Api.Exchanges);
        Assert.Equal("CODE-1", code);
        Assert.Equal(rig.Launcher.Query("code_challenge"), Pkce.ChallengeOf(verifier)); // verifier 与交给浏览器的 challenge 对得上
        Assert.True(Pkce.IsValidVerifier(verifier));

        var state = rig.State;
        Assert.Equal(AccountMode.Authenticated, state.Mode);
        Assert.Equal("Alice", state.Session!.User!.Label);
        Assert.Equal("t1", state.Session.SelectedTeamId); // 只有一个团队:直接选定
        Assert.Null(state.Login);
        Assert.Equal(SessionHealth.Ok, state.Health);
        Assert.Equal("RT-1", rig.Vault.Stored);
        Assert.Equal("authenticated", rig.Meta.Stored!.Mode);
        Assert.Equal(["t1"], rig.Api.SelectedTeams);
    }

    [Fact]
    public async Task A_callback_with_the_wrong_state_is_rejected_and_the_pending_login_keeps_waiting()
    {
        var rig = new AccountRig();
        await rig.Service.InitializeAsync();
        await rig.Service.BeginBrowserLoginAsync();

        var outcome = await rig.Service.HandleLinkAsync(new AuthCallbackLink("EVIL-CODE", "forged-state", null, null));
        Assert.Equal(LinkOutcome.Rejected, outcome);
        Assert.Empty(rig.Api.Exchanges); // 伪造的回调不触发换码
        Assert.Equal(LoginPhase.Waiting, rig.State.Login!.Phase);
        Assert.Null(rig.State.LoginError);

        // 真正的回调仍然能完成
        Assert.Equal(LinkOutcome.Accepted, await rig.Service.HandleLinkAsync(new AuthCallbackLink("C", rig.Launcher.Query("state"), null, null)));
        Assert.True(rig.State.IsSignedIn);
    }

    [Fact]
    public async Task A_replayed_callback_cannot_exchange_the_same_code_twice()
    {
        var rig = new AccountRig();
        await rig.SignInAsync();
        var replay = await rig.Service.HandleLinkAsync(new AuthCallbackLink("CODE-1", rig.Launcher.Query("state"), null, null));
        Assert.Equal(LinkOutcome.Ignored, replay);
        Assert.Single(rig.Api.Exchanges);
    }

    [Fact]
    public async Task A_callback_when_no_login_is_pending_changes_nothing()
    {
        var rig = new AccountRig();
        await rig.Service.InitializeAsync();
        var before = rig.State;
        Assert.Equal(LinkOutcome.Ignored, await rig.Service.HandleLinkAsync(new AuthCallbackLink("C", "S", null, null)));
        Assert.Equal(before, rig.State);
        Assert.Empty(rig.Api.Calls);
    }

    [Fact]
    public async Task Starting_a_new_login_supersedes_the_old_one()
    {
        var rig = new AccountRig();
        await rig.Service.InitializeAsync();
        await rig.Service.BeginBrowserLoginAsync();
        var oldState = rig.Launcher.Query("state");
        await rig.Service.BeginBrowserLoginAsync();
        var newState = rig.Launcher.Query("state");
        Assert.NotEqual(oldState, newState);

        Assert.Equal(LinkOutcome.Rejected, await rig.Service.HandleLinkAsync(new AuthCallbackLink("C", oldState, null, null)));
        Assert.Equal(LinkOutcome.Accepted, await rig.Service.HandleLinkAsync(new AuthCallbackLink("C", newState, null, null)));
        Assert.True(rig.State.IsSignedIn);
    }

    [Fact]
    public async Task A_login_that_is_not_completed_within_ten_minutes_expires()
    {
        var rig = new AccountRig();
        await rig.Service.InitializeAsync();
        await rig.Service.BeginBrowserLoginAsync();
        var state = rig.Launcher.Query("state");
        rig.Clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));

        Assert.Null(rig.State.Login);
        Assert.Equal(AccountKeys.Expired, rig.State.LoginError);
        Assert.Equal(AccountBannerKind.LoginFailed, AccountBanner.Of(rig.State));
        // 过期之后迟到的回调不能再登录
        Assert.Equal(LinkOutcome.Ignored, await rig.Service.HandleLinkAsync(new AuthCallbackLink("C", state, null, null)));
        Assert.Empty(rig.Api.Exchanges);
    }

    [Fact]
    public async Task The_user_denying_access_is_a_failure_with_its_own_key_and_can_be_dismissed()
    {
        var rig = new AccountRig();
        await rig.Service.InitializeAsync();
        await rig.Service.BeginBrowserLoginAsync();
        await rig.Service.HandleLinkAsync(new AuthCallbackLink(null, rig.Launcher.Query("state"), "access_denied", null));
        Assert.Equal(AccountKeys.Denied, rig.State.LoginError);
        Assert.Null(rig.State.Login);

        rig.Service.DismissLoginError();
        Assert.Null(rig.State.LoginError);
    }

    [Fact]
    public async Task Cancelling_a_pending_login_returns_to_idle_and_voids_the_state()
    {
        var rig = new AccountRig();
        await rig.Service.InitializeAsync();
        await rig.Service.BeginBrowserLoginAsync();
        var state = rig.Launcher.Query("state");
        rig.Service.CancelLogin();
        Assert.Null(rig.State.Login);
        Assert.Null(rig.State.LoginError);
        Assert.Equal(LinkOutcome.Ignored, await rig.Service.HandleLinkAsync(new AuthCallbackLink("C", state, null, null)));
    }

    [Fact]
    public async Task A_browser_that_cannot_open_is_reported_so_the_user_can_switch_to_the_device_code()
    {
        var rig = new AccountRig();
        await rig.Service.InitializeAsync();
        rig.Launcher.Fail = true;
        Assert.False(await rig.Service.BeginBrowserLoginAsync());
        Assert.Equal(AccountKeys.BrowserFailed, rig.State.LoginError);
        Assert.Null(rig.State.Login);
    }

    [Fact]
    public async Task A_network_failure_while_exchanging_the_code_fails_the_login_without_signing_in()
    {
        var rig = new AccountRig();
        await rig.Service.InitializeAsync();
        await rig.Service.BeginBrowserLoginAsync();
        rig.Api.ExchangeFailure = new AccountApiException(AccountApiErrorKind.Network, "down");
        await rig.Service.HandleLinkAsync(new AuthCallbackLink("C", rig.Launcher.Query("state"), null, null));
        Assert.Equal(AccountKeys.Network, rig.State.LoginError);
        Assert.False(rig.State.IsSignedIn);
        Assert.Null(rig.Vault.Stored);
        Assert.Null(rig.Meta.Stored);
    }

    [Fact]
    public async Task An_account_without_any_team_cannot_sign_in_and_its_tokens_are_revoked()
    {
        var rig = new AccountRig();
        rig.Api.Teams = new TeamContext([], null);
        await rig.Service.InitializeAsync();
        await rig.Service.BeginBrowserLoginAsync();
        await rig.Service.HandleLinkAsync(new AuthCallbackLink("C", rig.Launcher.Query("state"), null, null));
        Assert.Equal(AccountKeys.NoTeam, rig.State.LoginError);
        Assert.False(rig.State.IsSignedIn);
        Assert.Equal(["RT-1"], rig.Api.Revoked);
    }

    [Fact]
    public async Task A_sign_in_that_fails_after_the_tokens_were_issued_revokes_them_and_stores_nothing()
    {
        var rig = new AccountRig();
        await rig.Service.InitializeAsync();
        await rig.Service.BeginBrowserLoginAsync();
        rig.Api.TeamsFailure = new AccountApiException(AccountApiErrorKind.Network, "dropped");
        await rig.Service.HandleLinkAsync(new AuthCallbackLink("C", rig.Launcher.Query("state"), null, null));

        Assert.Equal(AccountKeys.Network, rig.State.LoginError);
        Assert.False(rig.State.IsSignedIn);
        Assert.Equal(["RT-1"], rig.Api.Revoked); // 服务端那份刷新令牌不留着
        Assert.Null(rig.Vault.Stored);
        Assert.Null(rig.Meta.Stored);
    }

    // ───────────────────────── 团队 ─────────────────────────

    [Fact]
    public async Task With_several_teams_the_session_waits_for_a_choice_and_no_delegation_is_made_until_then()
    {
        var rig = new AccountRig();
        rig.Api.Teams = new TeamContext([FakeAccountApi.Team("t1", "Acme"), FakeAccountApi.Team("t2", "Beta")], null);
        rig.ConnectLocalDevice();
        await rig.SignInAsync();

        Assert.True(rig.State.Session!.ContextRequired);
        Assert.Null(rig.State.Session.SelectedTeamId);
        Assert.Equal(AccountBannerKind.TeamRequired, AccountBanner.Of(rig.State));
        Assert.Empty(rig.Delegation.Attached);

        Assert.True((await rig.Service.SelectTeamAsync("t2")).Ok);
        Assert.False(rig.State.Session!.ContextRequired);
        Assert.Equal("t2", rig.State.Session.SelectedTeamId);
        var attached = Assert.Single(rig.Delegation.Attached);
        Assert.Equal(("local", "u1", "t2"), (attached.Device, attached.AccountId, attached.TeamId));
    }

    [Fact]
    public async Task Selecting_an_unknown_team_is_refused_without_touching_the_backend()
    {
        var rig = new AccountRig();
        await rig.SignInAsync();
        var before = rig.Api.SelectedTeams.Count;
        var result = await rig.Service.SelectTeamAsync("nope");
        Assert.False(result.Ok);
        Assert.Equal(AccountKeys.TeamInvalid, result.ErrorKey);
        Assert.Equal(before, rig.Api.SelectedTeams.Count);
    }

    // ───────────────────────── 设备授权码 ─────────────────────────

    [Fact]
    public async Task The_device_code_login_shows_the_code_polls_with_slow_down_and_signs_in_when_granted()
    {
        var rig = new AccountRig();
        await rig.Service.InitializeAsync();
        Assert.True(await rig.Service.BeginDeviceCodeLoginAsync());

        var login = rig.State.Login!;
        Assert.Equal((LoginMethod.DeviceCode, "ABCD-EFGH", "https://auth.example.test/device"), (login.Method, login.UserCode, login.VerificationUri));
        Assert.Contains("user_code=ABCD-EFGH", Assert.Single(rig.Launcher.Opened));
        Assert.DoesNotContain("DEVICE-CODE-SECRET", login.ToString()); // 设备码不进状态

        rig.Api.Polls.Enqueue(new DevicePollResult(DevicePollKind.Pending));
        rig.Api.Polls.Enqueue(new DevicePollResult(DevicePollKind.SlowDown));
        rig.Api.Polls.Enqueue(new DevicePollResult(DevicePollKind.Granted, rig.Api.NewTokens()));
        await AdvanceUntilAsync(rig, () => rig.State.IsSignedIn);

        Assert.Equal(3, rig.Api.Calls.Count(c => c == "poll"));
        Assert.Equal("Alice", rig.State.Session!.User!.Label);
        Assert.Equal("RT-1", rig.Vault.Stored);
        await rig.Service.LoginTask;
    }

    [Fact]
    public async Task Slow_down_lengthens_the_polling_interval_by_five_seconds()
    {
        var rig = new AccountRig();
        await rig.Service.InitializeAsync();
        await rig.Service.BeginDeviceCodeLoginAsync();
        rig.Api.Polls.Enqueue(new DevicePollResult(DevicePollKind.SlowDown));
        await AdvanceUntilAsync(rig, () => rig.Api.Calls.Count(c => c == "poll") == 1, step: TimeSpan.FromSeconds(5));

        // 第一次轮询之后间隔是 10 秒:再过 6 秒不会有第二次
        rig.Clock.Advance(TimeSpan.FromSeconds(6));
        await Task.Delay(50);
        Assert.Equal(1, rig.Api.Calls.Count(c => c == "poll"));
        await AdvanceUntilAsync(rig, () => rig.Api.Calls.Count(c => c == "poll") == 2, step: TimeSpan.FromSeconds(1));
        rig.Service.CancelLogin();
    }

    [Theory]
    [InlineData(DevicePollKind.Denied, "account.login.denied")]
    [InlineData(DevicePollKind.Expired, "account.login.expired")]
    public async Task The_device_code_login_fails_when_denied_or_expired(DevicePollKind kind, string key)
    {
        var rig = new AccountRig();
        await rig.Service.InitializeAsync();
        await rig.Service.BeginDeviceCodeLoginAsync();
        rig.Api.Polls.Enqueue(new DevicePollResult(kind));
        await AdvanceUntilAsync(rig, () => rig.State.LoginError is not null);
        Assert.Equal(key, rig.State.LoginError);
        Assert.False(rig.State.IsSignedIn);
    }

    [Fact]
    public async Task The_device_code_expires_on_its_own_deadline_even_if_the_server_never_says_so()
    {
        var rig = new AccountRig();
        await rig.Service.InitializeAsync();
        await rig.Service.BeginDeviceCodeLoginAsync();
        await AdvanceUntilAsync(rig, () => rig.State.LoginError is not null, step: TimeSpan.FromSeconds(30), max: 40);
        Assert.Equal(AccountKeys.Expired, rig.State.LoginError);
    }

    [Fact]
    public async Task A_backend_without_device_authorization_says_so()
    {
        var rig = new AccountRig();
        await rig.Service.InitializeAsync();
        rig.Api.DeviceStartFailure = new AccountApiException(AccountApiErrorKind.Unsupported, "404", 404);
        Assert.False(await rig.Service.BeginDeviceCodeLoginAsync());
        Assert.Equal(AccountKeys.DeviceCodeUnavailable, rig.State.LoginError);
    }

    [Fact]
    public async Task Cancelling_the_device_code_login_stops_polling()
    {
        var rig = new AccountRig();
        await rig.Service.InitializeAsync();
        await rig.Service.BeginDeviceCodeLoginAsync();
        rig.Service.CancelLogin();
        await rig.Service.LoginTask;
        rig.Clock.Advance(TimeSpan.FromMinutes(1));
        await Task.Delay(50);
        Assert.Equal(0, rig.Api.Calls.Count(c => c == "poll"));
        Assert.Null(rig.State.Login);
    }

    private static async Task AdvanceUntilAsync(AccountRig rig, Func<bool> done, TimeSpan? step = null, int max = 60)
    {
        for (var i = 0; i < max && !done(); i++)
        {
            rig.Clock.Advance(step ?? TimeSpan.FromSeconds(5));
            for (var wait = 0; wait < 20 && !done(); wait++)
            {
                await Task.Delay(5);
            }
        }

        Assert.True(done(), "the condition was not reached");
    }

    // ───────────────────────── 令牌刷新与失效 ─────────────────────────

    [Fact]
    public async Task A_session_is_restored_on_restart_and_refreshed_with_the_keychain_token()
    {
        var rig = new AccountRig();
        await rig.SignInAsync();

        var (service, stores) = rig.Restart();
        await service.InitializeAsync();
        Assert.Equal(["RT-1"], rig.Api.Refreshes); // 钥匙串里的刷新令牌换了新的访问令牌
        Assert.Equal("RT-2", rig.Vault.Stored);    // 刷新令牌轮换:新的存回钥匙串
        Assert.True(stores.Account.State.IsSignedIn);
        Assert.Equal(SessionHealth.Ok, stores.Account.State.Health);
        Assert.Equal("Alice", stores.Account.State.Session!.User!.Label); // 资料来自本地记录,刷新前就能显示
    }

    [Fact]
    public async Task An_access_token_close_to_expiry_is_refreshed_by_the_background_tick_and_delegated_again()
    {
        var rig = new AccountRig();
        rig.ConnectLocalDevice();
        await rig.SignInAsync(); // 访问令牌 15 分钟
        Assert.Single(rig.Delegation.Attached);

        rig.Clock.Advance(TimeSpan.FromMinutes(10)); // 还有 5 分钟:不刷新
        await rig.Service.RefreshAsync();
        Assert.Empty(rig.Api.Refreshes);

        rig.Clock.Advance(TimeSpan.FromMinutes(4)); // 还剩 1 分钟 < 2 分钟边界
        await rig.Service.RefreshAsync();
        Assert.Equal(["RT-1"], rig.Api.Refreshes);
        Assert.Equal(2, rig.Delegation.Attached.Count); // 新的访问令牌交付给了本机 Agent
        Assert.NotEqual(rig.Delegation.Attached[0].Token, rig.Delegation.Attached[1].Token);
    }

    [Fact]
    public async Task Concurrent_refreshes_make_a_single_request()
    {
        var rig = new AccountRig();
        await rig.SignInAsync();
        var gate = new TaskCompletionSource<TokenSet>();
        rig.Api.OnRefresh = _ => gate.Task;
        rig.Clock.Advance(TimeSpan.FromMinutes(14));

        var a = rig.Service.RefreshAsync();
        var b = rig.Service.RefreshAsync();
        gate.SetResult(rig.Api.NewTokens());
        await Task.WhenAll(a, b);
        Assert.Single(rig.Api.Refreshes);
    }

    [Fact]
    public async Task A_rejected_refresh_token_expires_the_session_clears_the_keychain_and_revokes_the_agents_token()
    {
        var rig = new AccountRig();
        rig.ConnectLocalDevice();
        await rig.SignInAsync();
        rig.Api.OnRefresh = _ => throw new AccountApiException(AccountApiErrorKind.InvalidGrant, "bad", 400, "invalid_grant");
        rig.Clock.Advance(TimeSpan.FromMinutes(14));
        await rig.Service.RefreshAsync();

        var state = rig.State;
        Assert.Equal(SessionHealth.Expired, state.Health);
        Assert.Equal(AccountMode.Authenticated, state.Mode);          // 资料还在(显示「登录已过期」)
        Assert.Equal("Alice", state.Session!.User!.Label);
        Assert.Equal(AccountBannerKind.Expired, AccountBanner.Of(state));
        Assert.Null(rig.Vault.Stored);                                 // 钥匙串里的刷新令牌清掉
        Assert.Equal(["local"], rig.Delegation.Detached);              // 委托令牌撤销
        Assert.Empty(state.Delegations);
        Assert.False(rig.Service.HasSession);

        // 之后不会再拿失效的令牌去刷新
        rig.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Single(rig.Api.Refreshes);
    }

    [Fact]
    public async Task An_expired_session_can_sign_in_again()
    {
        var rig = new AccountRig();
        await rig.SignInAsync();
        rig.Api.OnRefresh = _ => throw new AccountApiException(AccountApiErrorKind.InvalidGrant, "bad", 400, "invalid_grant");
        rig.Clock.Advance(TimeSpan.FromMinutes(14));
        await rig.Service.RefreshAsync();
        Assert.Equal(SessionHealth.Expired, rig.State.Health);

        await rig.Service.BeginBrowserLoginAsync();
        await rig.Service.HandleLinkAsync(new AuthCallbackLink("C2", rig.Launcher.Query("state"), null, null));
        Assert.Equal(SessionHealth.Ok, rig.State.Health);
        Assert.True(rig.State.IsSignedIn);
        Assert.NotNull(rig.Vault.Stored);
    }

    [Fact]
    public async Task A_network_failure_while_refreshing_keeps_the_session_and_recovers_on_the_next_tick()
    {
        var rig = new AccountRig();
        await rig.SignInAsync();
        var fail = true;
        rig.Api.OnRefresh = _ => fail
            ? throw new AccountApiException(AccountApiErrorKind.Network, "offline")
            : Task.FromResult(rig.Api.NewTokens());
        rig.Clock.Advance(TimeSpan.FromMinutes(14));
        await rig.Service.RefreshAsync();

        Assert.Equal(SessionHealth.Unreachable, rig.State.Health);
        Assert.True(rig.State.IsSignedIn);                // 登录状态保留:本地功能照常
        Assert.Equal("RT-1", rig.Vault.Stored);           // 钥匙串不动
        Assert.Equal(AccountBannerKind.Unreachable, AccountBanner.Of(rig.State));

        fail = false;
        await rig.Service.RefreshAsync();
        Assert.Equal(SessionHealth.Ok, rig.State.Health);
    }

    [Fact]
    public async Task A_missing_keychain_token_at_startup_means_expired_not_signed_in_silently()
    {
        var rig = new AccountRig();
        await rig.SignInAsync();
        rig.Vault.Stored = null; // 用户删了钥匙串条目 / 换了机器

        var (service, stores) = rig.Restart();
        await service.InitializeAsync();
        Assert.Equal(SessionHealth.Expired, stores.Account.State.Health);
        Assert.Empty(rig.Api.Refreshes);
    }

    [Fact]
    public async Task A_refresh_result_arriving_after_sign_out_cannot_resurrect_the_session()
    {
        var rig = new AccountRig();
        rig.ConnectLocalDevice();
        await rig.SignInAsync();
        var gate = new TaskCompletionSource<TokenSet>();
        rig.Api.OnRefresh = _ => gate.Task;
        rig.Clock.Advance(TimeSpan.FromMinutes(14));
        var refresh = rig.Service.RefreshAsync();

        await rig.Service.SignOutAsync();
        gate.SetResult(rig.Api.NewTokens("AT-LATE", "RT-LATE"));
        await refresh;

        Assert.Equal(AccountMode.Offline, rig.State.Mode);
        Assert.Null(rig.State.Session);
        Assert.Null(rig.Vault.Stored); // 迟到的新刷新令牌没有被写回
        Assert.False(rig.Service.HasSession);
        Assert.Single(rig.Delegation.Attached); // 只有登录那一次的交付,迟到的刷新没有再交付
    }

    // ───────────────────────── 退出登录 ─────────────────────────

    [Fact]
    public async Task Signing_out_clears_everything_revokes_server_side_and_withdraws_the_agents_token()
    {
        var rig = new AccountRig();
        rig.ConnectLocalDevice();
        await rig.SignInAsync();
        await rig.Service.SignOutAsync();

        Assert.Equal(AccountMode.Offline, rig.State.Mode);
        Assert.Null(rig.State.Session);
        Assert.Equal(AccountBannerKind.None, AccountBanner.Of(rig.State));
        Assert.Null(rig.Vault.Stored);
        Assert.Equal("offline", rig.Meta.Stored!.Mode);
        Assert.Null(rig.Meta.Stored.User);
        Assert.Equal(["RT-1"], rig.Api.Revoked);
        Assert.Equal(["local"], rig.Delegation.Detached);
        Assert.Empty(rig.State.Delegations);
    }

    [Fact]
    public async Task Sign_out_still_completes_locally_when_the_server_is_unreachable()
    {
        var rig = new AccountRig();
        await rig.SignInAsync();
        await rig.Service.SignOutAsync();
        Assert.Equal(AccountMode.Offline, rig.State.Mode);
        Assert.Null(rig.Vault.Stored);
    }

    // ───────────────────────── 安全存储 ─────────────────────────

    [Fact]
    public async Task Without_a_secure_store_the_login_works_for_this_run_only_and_nothing_is_written_in_plain_text()
    {
        var rig = new AccountRig { };
        rig.Vault.Unsupported = true; // Linux:libsecret 未接入
        await rig.SignInAsync();

        Assert.True(rig.State.IsSignedIn);
        Assert.True(rig.State.SecureStorageUnavailable);
        Assert.Equal(AccountBannerKind.SecureStorageUnavailable, AccountBanner.Of(rig.State));
        Assert.Null(rig.Vault.Stored);
        Assert.DoesNotContain("RT-", rig.Meta.Stored!.ToString()); // 非机密记录里没有令牌

        // 下次启动:拿不到刷新令牌 = 需要重新登录
        var (service, stores) = rig.Restart();
        await service.InitializeAsync();
        Assert.Equal(SessionHealth.Expired, stores.Account.State.Health);
        Assert.True(stores.Account.State.SecureStorageUnavailable);
    }

    // ───────────────────────── 委托令牌 ─────────────────────────

    [Fact]
    public async Task The_agent_gets_a_delegated_token_when_the_local_device_connects_after_sign_in_and_not_before_connection()
    {
        var rig = new AccountRig();
        await rig.SignInAsync(); // 设备还没连上:没有地方交付
        Assert.Empty(rig.Delegation.Attached);

        rig.ConnectLocalDevice();
        await WaitAsync(() => rig.State.Delegations.ContainsKey("local") && rig.State.Delegations["local"].Status == DelegationStatus.Attached);
        var attached = Assert.Single(rig.Delegation.Attached);
        Assert.Equal(("local", "u1"), (attached.Device, attached.AccountId));
        Assert.Equal(DelegationStatus.Attached, rig.State.Delegations["local"].Status);
    }

    [Fact]
    public async Task A_device_that_disconnects_loses_its_delegation_status_and_gets_it_again_on_reconnect()
    {
        var rig = new AccountRig();
        rig.ConnectLocalDevice();
        await rig.SignInAsync();
        Assert.Equal(DelegationStatus.Attached, rig.State.Delegations["local"].Status);

        rig.DisconnectLocalDevice();
        Assert.Empty(rig.State.Delegations); // 规则 7:状态未知,不当作「还在」

        rig.ConnectLocalDevice();
        await WaitAsync(() => rig.Delegation.Attached.Count == 2);
        Assert.Equal(DelegationStatus.Attached, rig.State.Delegations["local"].Status);
    }

    [Fact]
    public async Task A_dedicated_delegated_token_is_used_when_the_backend_issues_one_otherwise_the_access_token()
    {
        var rig = new AccountRig();
        rig.ConnectLocalDevice();
        await rig.SignInAsync();
        Assert.Equal("AT-1", rig.Delegation.Attached[0].Token); // 后端没有委托端点:退回访问令牌

        var other = new AccountRig();
        other.Api.DelegationSupported = true;
        other.ConnectLocalDevice();
        await other.SignInAsync();
        Assert.Equal("DT-AT-1", other.Delegation.Attached[0].Token);
    }

    [Fact]
    public async Task A_device_that_refuses_the_token_is_marked_failed_and_login_still_succeeds()
    {
        var rig = new AccountRig();
        rig.Delegation.FailAttach = true;
        rig.ConnectLocalDevice();
        await rig.SignInAsync();
        Assert.True(rig.State.IsSignedIn);
        Assert.Equal(DelegationStatus.Failed, rig.State.Delegations["local"].Status);
    }

    [Fact]
    public async Task Tokens_never_appear_in_the_published_state()
    {
        var rig = new AccountRig();
        rig.ConnectLocalDevice();
        await rig.SignInAsync();
        var text = rig.State.ToString() + rig.State.Session + string.Join(',', rig.State.Delegations.Select(d => d.Value.ToString()));
        Assert.DoesNotContain("AT-", text);
        Assert.DoesNotContain("RT-", text);
        Assert.DoesNotContain("AT-", rig.Meta.Stored!.ToString());
        Assert.DoesNotContain("RT-", rig.Meta.Stored.ToString());
    }

    // ───────────────────────── 其他 ─────────────────────────

    [Fact]
    public async Task Listing_controllers_and_revoking_one_go_through_the_backend_with_a_fresh_token()
    {
        var rig = new AccountRig();
        rig.Api.Controllers.AddRange([new ControllerSession("c1", "MacBook", "macOS", rig.Clock.GetUtcNow(), true), new ControllerSession("c2", "iPhone", "iOS", null, false)]);
        await rig.SignInAsync();
        var list = await rig.Service.ListControllersAsync();
        Assert.Equal(["c1", "c2"], list.Value!.Select(c => c.Id));

        Assert.True((await rig.Service.RevokeControllerAsync("c2")).Ok);
        Assert.Equal(["c1"], (await rig.Service.ListControllersAsync()).Value!.Select(c => c.Id));
    }

    [Fact]
    public async Task Calls_that_need_the_account_say_so_when_signed_out()
    {
        var rig = new AccountRig();
        await rig.Service.InitializeAsync();
        await rig.Service.UseOfflineAsync();
        var result = await rig.Service.ListControllersAsync();
        Assert.False(result.Ok);
        Assert.Equal(AccountKeys.NotSignedIn, result.ErrorKey);
    }

    [Fact]
    public void The_banner_priority_puts_an_active_login_before_everything_else()
    {
        var progress = new LoginProgress(LoginMethod.Browser, LoginPhase.Waiting, AccountRig.Start, AccountRig.Start.AddMinutes(10));
        var expired = AccountState.Initial with { Mode = AccountMode.Authenticated, Session = new AccountSession(null, [], null, false), Health = SessionHealth.Expired };
        Assert.Equal(AccountBannerKind.Expired, AccountBanner.Of(expired));
        Assert.Equal(AccountBannerKind.SigningIn, AccountBanner.Of(expired with { Login = progress, LoginError = "x" }));
        Assert.Equal(AccountBannerKind.LoginFailed, AccountBanner.Of(expired with { LoginError = "account.login.failed" }));
        Assert.Equal(AccountBannerKind.None, AccountBanner.Of(AccountState.Initial with { Mode = AccountMode.Offline }));
        _ = ImmutableArray<int>.Empty;
    }

    private static async Task WaitAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            Assert.True(Environment.TickCount64 < deadline, "timed out");
            await Task.Delay(5);
        }
    }
}
