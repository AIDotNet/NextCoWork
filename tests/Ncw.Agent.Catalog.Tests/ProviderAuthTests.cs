using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Auth;
using Ncw.Agent.Llm.Wire;

namespace Ncw.Agent.Catalog.Tests;

/// <summary>移植自 2.x 的 <c>provider-auth.ts</c>(登录编排:单飞、同一上游账号合并、先写密文再写行、退出登录语义)与 <c>provider-accounts.ts</c>(账号操作、镜像旧槽、needsReauth 同步)。</summary>
public sealed class ProviderAuthTests : IDisposable
{
    private readonly ProviderFixture _fx = new();
    private readonly FakeLogin _login = new();

    public ProviderAuthTests()
    {
        _fx.AddProvider("codex", WireProtocol.OpenAiResponses);
        _fx.Service.OAuthLogins = issuer => issuer == "chatgpt" ? _login : null;
    }

    public void Dispose() => _fx.Dispose();

    private ProviderService Service => _fx.Service;

    private sealed class FakeLogin : IOAuthLogin
    {
        public string Issuer => "chatgpt";

        public OAuthGrantKind Grant { get; set; } = OAuthGrantKind.AuthorizationCode;

        public Func<IOAuthLoginSink, CancellationToken, Task<ProviderCredential>>? Script { get; set; }

        public OAuthCredential Next { get; set; } = Cred("acc-1", "me@example.com");

        public int Runs { get; private set; }

        public async Task<ProviderCredential> LoginAsync(IOAuthLoginSink sink, CancellationToken ct)
        {
            Runs++;
            if (Script is { } script)
            {
                return await script(sink, ct);
            }

            sink.OpenAuthorizeUrl("https://auth.example.com/authorize?state=s");
            return Next;
        }
    }

    private static OAuthCredential Cred(string account, string? email = null, string access = "at") =>
        new("chatgpt", access, "rt", null, account, email);

    private string AccountRef(ProviderAccountRow row) => ProviderRefs.ForAccount("codex", row.Id);

    // ── 登录 ──

    [Fact]
    public async Task Adding_an_account_writes_the_secret_then_the_row_and_the_first_account_becomes_current_and_the_legacy_slot()
    {
        var views = await Service.AddOrReauthAccountAsync("codex");

        var row = Assert.Single(views).Row;
        Assert.Equal(("chatgpt", 0, true, true, false), (row.Issuer, row.Order, row.Enabled, row.Current, row.NeedsReauth));
        var serialized = CredentialFormat.Serialize(Cred("acc-1", "me@example.com"));
        Assert.Equal(serialized, _fx.Vault.Values[AccountRef(row)]);
        Assert.Equal(serialized, _fx.Vault.Values["provider:codex"]); // 旧槽镜像当前账号
        Assert.Equal(new CredentialAuthInfo("chatgpt", "acc-1", "me@example.com", null, null, false, false), views[0].Auth);
    }

    [Fact]
    public async Task Logging_in_again_as_the_same_upstream_account_merges_instead_of_adding_a_row()
    {
        await Service.AddOrReauthAccountAsync("codex");
        _login.Next = Cred("acc-1", "me@example.com", access: "newer");
        var views = await Service.AddOrReauthAccountAsync("codex");
        Assert.Single(views);
        Assert.Contains("newer", _fx.Vault.Values[AccountRef(views[0].Row)]);

        // accountId 每次都变的家:邮箱兜底。
        _login.Next = Cred("acc-changed", "me@example.com");
        Assert.Single(await Service.AddOrReauthAccountAsync("codex"));

        _login.Next = Cred("acc-2", "other@example.com");
        Assert.Equal(2, (await Service.AddOrReauthAccountAsync("codex")).Count);
    }

    [Fact]
    public async Task Reauthenticating_a_specific_row_keeps_its_order_label_and_clears_needs_reauth_and_the_limit()
    {
        await Service.AddOrReauthAccountAsync("codex");
        _login.Next = Cred("acc-2", "b@example.com");
        await Service.AddOrReauthAccountAsync("codex");
        var second = _fx.Accounts.List("codex")[1];
        await Service.SetAccountLabelAsync("codex", second.Id, "Work");
        _fx.Accounts.SetNeedsReauth(second.Id, true);
        _fx.Accounts.SetLimit(second.Id, new AccountLimit(long.MaxValue / 2, 1, AccountLimitSource.Http429, "429"));

        _login.Next = Cred("brand-new-upstream-id", "c@example.com");
        await Service.AddOrReauthAccountAsync("codex", second.Id);

        var after = _fx.Accounts.Get(second.Id)!;
        Assert.Equal(("Work", 1, false, null), (after.Label, after.Order, after.NeedsReauth, after.Limit));
        Assert.Equal(2, _fx.Accounts.List("codex").Count);
    }

    [Fact]
    public async Task The_legacy_login_path_also_creates_an_account_row_so_the_list_is_not_empty()
    {
        var info = await Service.StartOAuthAsync("codex");
        Assert.True(info.HasKey);
        Assert.Equal("acc-1", info.Auth!.AccountId);
        Assert.Single(_fx.Accounts.List("codex"));
    }

    [Fact]
    public async Task A_provider_that_does_not_support_login_or_has_no_flow_registered_is_refused()
    {
        _fx.AddProvider("openai");
        Assert.Equal(ProviderErrorKeys.NoOAuth, (await Assert.ThrowsAsync<ProviderException>(() => Service.AddOrReauthAccountAsync("openai"))).Key);

        Service.OAuthLogins = _ => null;
        Assert.Equal(ProviderErrorKeys.NoOAuth, (await Assert.ThrowsAsync<ProviderException>(() => Service.AddOrReauthAccountAsync("codex"))).Key);
        Assert.Empty(_fx.Accounts.List());
    }

    [Fact]
    public async Task A_credential_that_is_not_an_account_login_leaves_nothing_behind()
    {
        _login.Script = (_, _) => Task.FromResult<ProviderCredential>(new ApiKeyCredential("sk"));
        var error = await Assert.ThrowsAsync<ProviderException>(() => Service.AddOrReauthAccountAsync("codex"));
        Assert.Equal(ProviderErrorKeys.LoginFailed, error.Key);
        Assert.Empty(_fx.Accounts.List());
        Assert.Empty(_fx.Vault.Values);
    }

    // ── 进度:授权页由控制端打开 ──

    [Fact]
    public async Task The_authorize_url_and_the_device_code_are_handed_to_the_controller_as_progress_events()
    {
        var events = new List<OAuthProgress>();
        Service.OAuthProgressChanged += events.Add;

        await Service.AddOrReauthAccountAsync("codex");
        Assert.Equal(new OAuthProgress("codex", OAuthPhase.Waiting, AuthorizeUrl: "https://auth.example.com/authorize?state=s", NeedsPastedCode: true), events.Single());

        events.Clear();
        _login.Grant = OAuthGrantKind.DeviceCode;
        _login.Script = (sink, _) =>
        {
            sink.ShowDeviceCode("ABCD-1234", "https://auth.example.com/device", 900);
            return Task.FromResult<ProviderCredential>(Cred("acc-9"));
        };
        await Service.AddOrReauthAccountAsync("codex");
        Assert.Equal(new OAuthProgress("codex", OAuthPhase.Waiting, UserCode: "ABCD-1234", VerificationUrl: "https://auth.example.com/device", ExpiresInSeconds: 900), events.Single());
    }

    [Fact]
    public async Task A_pasted_callback_code_reaches_the_login_flow_through_the_paste_sink()
    {
        string? received = null;
        _login.Script = async (sink, ct) =>
        {
            sink.OpenAuthorizeUrl("https://auth.example.com/a");
            received = await ((IOAuthPasteSink)sink).AwaitPastedCodeAsync(ct);
            return Cred("acc-1");
        };
        var login = Service.AddOrReauthAccountAsync("codex");
        await WaitUntilAsync(() => _login.Runs == 1);
        await Task.Delay(50);

        Service.SubmitOAuthCode("codex", "the-code");
        await login;
        Assert.Equal("the-code", received);

        // 没有等码的登录:报错而不是静默吞掉。
        Assert.Equal(ProviderErrorKeys.NoLoginInProgress, Assert.Throws<ProviderException>(() => Service.SubmitOAuthCode("codex", "late")).Key);
    }

    [Fact]
    public async Task Cancelling_ends_the_login_with_a_cancelled_error_and_a_terminal_event()
    {
        var events = new List<OAuthProgress>();
        Service.OAuthProgressChanged += events.Add;
        _login.Script = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Cred("never");
        };

        var login = Service.AddOrReauthAccountAsync("codex");
        await WaitUntilAsync(() => _login.Runs == 1);
        Service.CancelOAuth("codex");

        Assert.Equal(ProviderErrorKeys.LoginCancelled, (await Assert.ThrowsAsync<ProviderException>(() => login)).Key);
        Assert.Equal(OAuthPhase.Cancelled, events[^1].Phase);
        Assert.Empty(_fx.Accounts.List());
    }

    [Fact]
    public async Task Starting_a_new_login_cancels_the_previous_one_instead_of_complaining()
    {
        var first = true;
        _login.Script = async (_, ct) =>
        {
            if (first)
            {
                first = false;
                await Task.Delay(Timeout.Infinite, ct);
            }

            return Cred("acc-1");
        };

        var one = Service.AddOrReauthAccountAsync("codex");
        await WaitUntilAsync(() => _login.Runs == 1);
        var two = Service.AddOrReauthAccountAsync("codex");

        Assert.Equal(ProviderErrorKeys.LoginCancelled, (await Assert.ThrowsAsync<ProviderException>(() => one)).Key);
        Assert.Single(await two);
    }

    [Fact]
    public async Task A_failed_login_is_reported_with_its_diagnostic_and_a_failed_event()
    {
        var events = new List<OAuthProgress>();
        Service.OAuthProgressChanged += events.Add;
        _login.Script = (_, _) => throw new CredentialAuthException(AgentError.Create(AgentErrorCode.Auth, "token endpoint said invalid_grant", false));

        var error = await Assert.ThrowsAsync<ProviderException>(() => Service.AddOrReauthAccountAsync("codex"));
        Assert.Equal(ProviderErrorKeys.LoginFailed, error.Key);
        Assert.Contains("invalid_grant", error.Message);
        Assert.Equal((OAuthPhase.Failed, ProviderErrorKeys.LoginFailed), (events[^1].Phase, events[^1].ErrorKey));
    }

    // ── 账号操作 ──

    private async Task<(ProviderAccountRow A, ProviderAccountRow B, ProviderAccountRow C)> ThreeAccountsAsync()
    {
        foreach (var n in new[] { "1", "2", "3" })
        {
            _login.Next = Cred($"acc-{n}", $"{n}@example.com");
            await Service.AddOrReauthAccountAsync("codex");
        }

        var rows = _fx.Accounts.List("codex");
        return (rows[0], rows[1], rows[2]);
    }

    [Fact]
    public async Task Signing_out_removes_only_the_current_account_and_the_next_one_takes_over_the_mirror()
    {
        var (a, b, _) = await ThreeAccountsAsync();
        var info = await Service.SignOutAsync("codex");

        Assert.Null(_fx.Accounts.Get(a.Id));
        Assert.DoesNotContain(AccountRef(a), _fx.Vault.Values.Keys); // 密文一起删
        Assert.Equal(2, _fx.Accounts.List("codex").Count);
        Assert.Equal(b.Id, _fx.Accounts.CurrentOf("codex")!.Id);
        Assert.Equal("acc-2", info.Auth!.AccountId); // 镜像槽换人了
    }

    [Fact]
    public async Task Signing_out_the_last_account_clears_the_legacy_slot()
    {
        await Service.AddOrReauthAccountAsync("codex");
        var info = await Service.SignOutAsync("codex");
        Assert.False(info.HasKey);
        Assert.Empty(_fx.Vault.Values);
    }

    [Fact]
    public async Task Signing_out_without_any_account_row_removes_the_old_single_slot()
    {
        _fx.Vault.Seed("provider:codex", CredentialFormat.Serialize(Cred("legacy")));
        Assert.False((await Service.SignOutAsync("codex")).HasKey);
    }

    [Fact]
    public async Task Removing_the_current_account_hands_the_mirror_to_the_next_one()
    {
        var (a, _, c) = await ThreeAccountsAsync();
        await Service.RemoveAccountAsync("codex", a.Id);
        var current = _fx.Accounts.CurrentOf("codex")!;
        Assert.True(current.Current);
        Assert.Contains($"\"accountId\":\"{(current.Id == c.Id ? "acc-3" : "acc-2")}\"", _fx.Vault.Values["provider:codex"]);
        Assert.Equal(ProviderErrorKeys.UnknownAccount, (await Assert.ThrowsAsync<ProviderException>(() => Service.RemoveAccountAsync("codex", a.Id))).Key);
    }

    [Fact]
    public async Task Switching_the_current_account_rewrites_the_mirror_and_disabling_does_not_touch_it()
    {
        var (_, b, _) = await ThreeAccountsAsync();
        await Service.SetCurrentAccountAsync("codex", b.Id);
        Assert.Contains("\"accountId\":\"acc-2\"", _fx.Vault.Values["provider:codex"]);

        await Service.SetAccountEnabledAsync("codex", b.Id, false);
        Assert.False(_fx.Accounts.Get(b.Id)!.Enabled);
        Assert.Contains("\"accountId\":\"acc-2\"", _fx.Vault.Values["provider:codex"]);
    }

    [Fact]
    public async Task Reordering_puts_the_unmentioned_accounts_after_the_mentioned_ones_and_ignores_strangers()
    {
        var (a, b, c) = await ThreeAccountsAsync();
        var views = await Service.ReorderAccountsAsync("codex", [c.Id, "ghost", c.Id]);
        Assert.Equal([c.Id, a.Id, b.Id], views.Select(v => v.Row.Id).ToArray());
    }

    [Fact]
    public async Task Clearing_a_limit_and_pausing_manually_use_the_limit_columns()
    {
        var (a, _, _) = await ThreeAccountsAsync();
        var until = _fx.Clock.GetUtcNow().AddHours(1);
        await Service.PauseAccountUntilAsync("codex", a.Id, until);
        Assert.Equal(AccountLimitSource.Manual, _fx.Accounts.Get(a.Id)!.Limit!.Source);
        Assert.NotEqual(a.Id, Service.AccountPool.Select("codex")!.Id); // 池子绕开它

        await Service.ClearAccountLimitAsync("codex", a.Id);
        Assert.Null(_fx.Accounts.Get(a.Id)!.Limit);
        Assert.Equal(a.Id, Service.AccountPool.Select("codex")!.Id);
    }

    [Fact]
    public async Task A_reported_quota_that_is_full_drops_the_gate_until_the_reset_and_is_persisted()
    {
        var (a, _, _) = await ThreeAccountsAsync();
        var resetsAt = _fx.Clock.GetUtcNow().AddHours(2).ToUnixTimeMilliseconds();
        await Service.ReportAccountQuotaAsync("codex", a.Id, new QuotaSnapshot(new QuotaWindow(100, 300, resetsAt), null, 1));

        var row = _fx.Accounts.Get(a.Id)!;
        Assert.Equal((AccountLimitSource.QuotaExhausted, resetsAt), (row.Limit!.Source, row.Limit.Until));
        Assert.NotNull(row.Quota);
    }

    [Fact]
    public async Task The_account_list_syncs_the_denormalised_needs_reauth_bit_from_the_ciphertext_but_not_from_a_failed_read()
    {
        var (a, _, _) = await ThreeAccountsAsync();
        _fx.Vault.Seed(AccountRef(a), CredentialFormat.Serialize(Cred("acc-1", "1@example.com") with { NeedsReauth = true }));
        var views = await Service.ListAccountsAsync("codex");
        Assert.True(views[0].Row.NeedsReauth);
        Assert.True(_fx.Accounts.Get(a.Id)!.NeedsReauth); // 账号池热路径读的是这一列
        Assert.NotEqual(a.Id, Service.AccountPool.Select("codex")!.Id);

        // 读不到密文时不动那一列:不能用一次读取失败抹掉一条真实的「登录已失效」。
        _fx.Vault.Remove(AccountRef(a));
        var again = await Service.ListAccountsAsync("codex");
        Assert.Null(again[0].Auth);
        Assert.True(_fx.Accounts.Get(a.Id)!.NeedsReauth);
    }

    [Fact]
    public async Task The_credential_change_hook_writes_back_needs_reauth_and_announces_the_provider()
    {
        var (a, _, _) = await ThreeAccountsAsync();
        var changed = new List<string>();
        Service.AccountsChanged += changed.Add;
        _fx.Vault.Seed(AccountRef(a), CredentialFormat.Serialize(Cred("acc-1", "1@example.com") with { NeedsReauth = true }));

        Service.OnCredentialChanged(AccountRef(a));
        await WaitUntilAsync(() => _fx.Accounts.Get(a.Id)!.NeedsReauth);
        await WaitUntilAsync(() => changed.Contains("codex"));
    }

    [Fact]
    public async Task Account_operations_on_a_provider_without_login_support_or_an_unknown_account_are_rejected()
    {
        _fx.AddProvider("openai");
        Assert.Equal(ProviderErrorKeys.NoOAuth, (await Assert.ThrowsAsync<ProviderException>(() => Service.ListAccountsAsync("openai"))).Key);
        Assert.Equal(ProviderErrorKeys.UnknownAccount, (await Assert.ThrowsAsync<ProviderException>(() => Service.SetAccountEnabledAsync("codex", "ghost", true))).Key);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }
}
