using System.Net;
using Ncw.Agent.Llm.Auth;
using Ncw.Agent.Llm.Json;
using Ncw.Agent.Llm.Tests.Support;

namespace Ncw.Agent.Llm.Tests;

/// <summary>凭据格式、OAuth 刷新(01-llm §7)、账户池(§5)。</summary>
public sealed class AuthTests
{
    private const string Ref = "provider:chatgpt";

    private static OAuthCredential Oauth(long? expiresAt, string refresh = "R1", string access = "A1") =>
        new("chatgpt", access, refresh, expiresAt, "acct-1", "me@example.com");

    private static OAuthProviderSpec Spec(Func<System.Text.Json.JsonElement, long, OAuthIdentity?>? identity = null, string? scope = null) => new(
        "chatgpt", "https://auth.example.com/oauth/token", "client-1",
        (json, now) => identity?.Invoke(json, now) ?? new OAuthIdentity(
            json.Str("access_token")!, json.Str("refresh_token"), now + (json.Count("expires_in") ?? 3600) * 1000, "acct-1"),
        scope, new Dictionary<string, string> { ["x-oauth-extra"] = "yes" });

    private sealed class Fixture
    {
        public FakeClock Clock { get; } = new();

        public FakeUpstream Http { get; } = new();

        public MemoryCredentialStore Store { get; } = new();

        public List<string> Changed { get; } = [];

        public CredentialResolver Build(OAuthProviderSpec? spec = null) => new(
            Store, new OAuthRegistry([spec ?? Spec()]), new HttpClientUpstream(new HttpClient(Http)), Clock, Changed.Add);

        public Fixture WithCredential(OAuthCredential credential)
        {
            Store.Put(Ref, CredentialFormat.Serialize(credential));
            return this;
        }
    }

    private static HttpResponseMessage TokenReply(string access = "A2", string? refresh = "R2") =>
        Reply.Json(HttpStatusCode.OK, $$$"""{"access_token":"{{{access}}}",{{{(refresh is null ? "" : $"\"refresh_token\":\"{refresh}\",")}}}"expires_in":3600}""");

    // ───────────────────────── 凭据格式 ─────────────────────────

    [Fact]
    public void A_bare_string_is_an_api_key_and_serialises_back_to_the_bare_string()
    {
        var credential = CredentialFormat.Parse("sk-abc");

        Assert.Equal(new ApiKeyCredential("sk-abc"), credential);
        // 降级兼容的守门线:API Key 回吐的是裸字符串,不是 JSON。
        Assert.Equal("sk-abc", CredentialFormat.Serialize(credential!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Nothing_configured_is_null_not_an_error(string? raw)
    {
        Assert.Null(CredentialFormat.Parse(raw));
    }

    [Fact]
    public void A_key_that_merely_starts_with_a_brace_is_still_an_api_key()
    {
        // 解析失败一律退回「当作裸 API key」,绝不抛:否则用户看到「还没有配置密钥」,而他明明配过。
        Assert.Equal(new ApiKeyCredential("{not json"), CredentialFormat.Parse("{not json"));
        Assert.Equal(new ApiKeyCredential("{\"foo\":1}"), CredentialFormat.Parse("{\"foo\":1}"));
    }

    [Fact]
    public void An_oauth_credential_round_trips_with_every_field_including_unknown_expiry()
    {
        var full = Oauth(1_800_000_000_000) with { PlanType = "plus", NeedsReauth = true, RefreshedAt = 5 };
        var unknown = Oauth(null);

        Assert.Equal(full, CredentialFormat.Parse(CredentialFormat.Serialize(full)));
        Assert.Equal(unknown, CredentialFormat.Parse(CredentialFormat.Serialize(unknown))); // null = 不知道,不是永不过期
    }

    [Fact]
    public void A_damaged_oauth_record_is_null_not_a_bearer_made_of_json()
    {
        Assert.Null(CredentialFormat.Parse("""{"kind":"oauth","issuer":"chatgpt","accessToken":"a"}"""));
        Assert.Null(CredentialFormat.Parse("""{"kind":"oauth","issuer":"nope","accessToken":"a","refreshToken":"r","accountId":"x"}"""));
        Assert.Null(CredentialFormat.Parse("""{"kind":"oauth","issuer":"chatgpt","accessToken":"a","refreshToken":"r","accountId":"x","expiresAt":"soon"}"""));
    }

    [Fact]
    public void Signature_credentials_round_trip_and_cannot_be_used_as_a_bearer()
    {
        var credential = new SignatureCredential(SignatureScheme.AwsSigV4, "AKIA", "SECRET", "us-east-1", "TOKEN");

        Assert.Equal(credential, CredentialFormat.Parse(CredentialFormat.Serialize(credential)));
        Assert.Null(CredentialFormat.Parse("""{"kind":"signature","scheme":"aws-sigv4","accessKeyId":"a"}"""));
        Assert.Throws<InvalidOperationException>(() => CredentialFormat.BearerOf(credential));
    }

    // ───────────────────────── 刷新 ─────────────────────────

    [Fact]
    public async Task A_credential_that_is_not_about_to_expire_is_returned_without_any_http()
    {
        var f = new Fixture();
        f.WithCredential(Oauth(f.Clock.Ms + 3_600_000));

        var resolved = await f.Build().ResolveAsync(Ref, CancellationToken.None);

        Assert.Equal("A1", Assert.IsType<OAuthCredential>(resolved).AccessToken);
        Assert.Empty(f.Http.Requests);
    }

    [Fact]
    public async Task A_credential_within_60_seconds_of_expiry_is_refreshed_and_written_back_before_it_is_returned()
    {
        var f = new Fixture();
        f.WithCredential(Oauth(f.Clock.Ms + 59_000));
        f.Http.Enqueue(() => TokenReply());

        var resolved = (OAuthCredential)(await f.Build().ResolveAsync(Ref, CancellationToken.None))!;

        Assert.Equal("A2", resolved.AccessToken);
        Assert.Equal("R2", resolved.RefreshToken); // 轮换后的 refresh token 存下来
        var stored = (OAuthCredential)CredentialFormat.Parse(f.Store.Peek(Ref))!;
        Assert.Equal("A2", stored.AccessToken); // 写回先于返回
        Assert.Equal(f.Clock.Ms + 3_600_000, stored.ExpiresAt);
        Assert.Equal([Ref], f.Changed);

        var request = Assert.Single(f.Http.Requests);
        Assert.Contains("grant_type=refresh_token", request.Body);
        Assert.Contains("refresh_token=R1", request.Body);
        Assert.Contains("client_id=client-1", request.Body);
        Assert.DoesNotContain("scope=", request.Body); // 没声明 scope 的家一个字都不能写
        Assert.Equal("yes", request.Header("x-oauth-extra")); // 私货头在刷新这一跳上一样要带
    }

    [Fact]
    public async Task The_old_refresh_token_is_kept_when_the_server_does_not_return_a_new_one()
    {
        var f = new Fixture();
        f.WithCredential(Oauth(f.Clock.Ms + 1000));
        f.Http.Enqueue(() => TokenReply(refresh: null));

        var resolved = (OAuthCredential)(await f.Build().ResolveAsync(Ref, CancellationToken.None))!;

        Assert.Equal("R1", resolved.RefreshToken);
    }

    [Fact]
    public async Task Unknown_expiry_is_never_refreshed_proactively()
    {
        var f = new Fixture();
        f.WithCredential(Oauth(null));

        await f.Build().ResolveAsync(Ref, CancellationToken.None);

        Assert.Empty(f.Http.Requests);
    }

    [Fact]
    public async Task Concurrent_refreshes_of_the_same_credential_are_coalesced_into_one_request()
    {
        // refresh token 是轮换的:并发刷新时后几次拿着已作废的 token 得到 invalid_grant,会把用户踢下线。
        var f = new Fixture();
        f.WithCredential(Oauth(f.Clock.Ms + 1000));
        var release = new TaskCompletionSource();
        f.Http.Enqueue(async _ =>
        {
            await release.Task;
            return TokenReply();
        });
        var resolver = f.Build();

        var tasks = Enumerable.Range(0, 5).Select(_ => resolver.ResolveAsync(Ref, CancellationToken.None)).ToList();
        await Task.Delay(50);
        release.SetResult();
        var results = await Task.WhenAll(tasks);

        Assert.Single(f.Http.Requests);
        Assert.All(results, r => Assert.Equal("A2", ((OAuthCredential)r!).AccessToken));
    }

    [Fact]
    public async Task Refresh_does_not_use_the_callers_cancellation_token_so_a_cancel_cannot_lose_the_new_token()
    {
        // 刷新请求一发出去旧 refresh token 就被上游消耗掉了:中途放弃等于扔掉那把新的。
        var f = new Fixture();
        f.WithCredential(Oauth(f.Clock.Ms + 1000));
        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        f.Http.Enqueue(async _ =>
        {
            started.SetResult();
            await release.Task;
            return TokenReply();
        });
        using var cts = new CancellationTokenSource();
        var waiting = f.Build().ResolveAsync(Ref, cts.Token);
        await started.Task;

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        release.SetResult();
        // 刷新脱离了调用方在后台完成:等的是「新 token 写进了存储」这件事本身(不是睡一段时间去赌它写完了)
        await f.Store.FirstWrite.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("A2", ((OAuthCredential)CredentialFormat.Parse(f.Store.Peek(Ref))!).AccessToken);
    }

    [Fact]
    public async Task A_network_failure_during_refresh_is_retryable_and_leaves_the_store_untouched()
    {
        var f = new Fixture();
        f.WithCredential(Oauth(f.Clock.Ms + 1000));
        f.Http.Enqueue(_ => throw new HttpRequestException("offline"));

        var error = await Assert.ThrowsAsync<CredentialAuthException>(() => f.Build().ResolveAsync(Ref, CancellationToken.None));

        // 离线时删掉 refresh token 等于因为一次断网强迫用户重新登录一遍。
        Assert.Equal(AgentErrorCode.Network, error.Error.Code);
        Assert.True(error.Error.Retryable);
        Assert.Empty(f.Store.Writes);
    }

    [Fact]
    public async Task A_5xx_from_the_auth_server_is_a_network_problem_not_a_logout()
    {
        var f = new Fixture();
        f.WithCredential(Oauth(f.Clock.Ms + 1000));
        f.Http.Enqueue(() => Reply.Status(HttpStatusCode.BadGateway, "bad gateway"));

        var error = await Assert.ThrowsAsync<CredentialAuthException>(() => f.Build().ResolveAsync(Ref, CancellationToken.None));

        Assert.Equal(AgentErrorCode.Network, error.Error.Code);
        Assert.Empty(f.Store.Writes);
    }

    [Fact]
    public async Task An_explicit_rejection_marks_needs_reauth_keeps_the_refresh_token_and_is_not_retryable()
    {
        var f = new Fixture();
        f.WithCredential(Oauth(f.Clock.Ms + 1000));
        f.Http.Enqueue(() => Reply.Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}"""));

        var error = await Assert.ThrowsAsync<CredentialAuthException>(() => f.Build().ResolveAsync(Ref, CancellationToken.None));

        Assert.Equal(AgentErrorCode.Auth, error.Error.Code);
        Assert.False(error.Error.Retryable);
        var stored = (OAuthCredential)CredentialFormat.Parse(f.Store.Peek(Ref))!;
        Assert.True(stored.NeedsReauth);
        Assert.Equal("R1", stored.RefreshToken); // 万一是上游抖动,用户手动重新登录会覆盖它
    }

    [Fact]
    public async Task Refresh_now_ignores_the_expiry_and_scope_is_sent_when_declared()
    {
        var f = new Fixture();
        f.WithCredential(Oauth(f.Clock.Ms + 3_600_000));
        f.Http.Enqueue(() => TokenReply());

        var fresh = await f.Build(Spec(scope: "openid offline")).RefreshNowAsync(Ref, CancellationToken.None);

        Assert.Equal("A2", fresh.AccessToken);
        Assert.Contains("scope=openid+offline", Assert.Single(f.Http.Requests).Body);
    }

    [Fact]
    public async Task Refresh_now_on_an_api_key_is_an_auth_error()
    {
        var f = new Fixture();
        f.Store.Put(Ref, "sk-key");

        await Assert.ThrowsAsync<CredentialAuthException>(() => f.Build().RefreshNowAsync(Ref, CancellationToken.None));
    }

    [Fact]
    public async Task A_signature_credential_has_nothing_to_refresh()
    {
        var f = new Fixture();
        var signature = new SignatureCredential(SignatureScheme.TencentTc3, "id", "key");
        f.Store.Put(Ref, CredentialFormat.Serialize(signature));

        Assert.Equal(signature, await f.Build().ResolveAsync(Ref, CancellationToken.None));
        Assert.Empty(f.Http.Requests);
    }

    [Fact]
    public async Task A_custom_refresh_hook_failure_that_is_not_an_explicit_rejection_is_a_network_error()
    {
        var f = new Fixture();
        f.WithCredential(Oauth(f.Clock.Ms + 1000));
        var spec = Spec() with { Refresh = (_, _) => throw new InvalidOperationException("socket closed") };

        var error = await Assert.ThrowsAsync<CredentialAuthException>(() => f.Build(spec).ResolveAsync(Ref, CancellationToken.None));

        Assert.Equal(AgentErrorCode.Network, error.Error.Code);
        Assert.Empty(f.Store.Writes);
    }

    // ───────────────────────── PKCE ─────────────────────────

    [Fact]
    public void Pkce_challenge_matches_the_rfc_7636_appendix_b_vector()
    {
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", Pkce.ChallengeOf("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    }

    [Fact]
    public void Pkce_verifier_has_a_valid_length_and_state_comparison_is_exact()
    {
        var pair = Pkce.Create();

        Assert.InRange(pair.Verifier.Length, 43, 128);
        Assert.Equal(Pkce.ChallengeOf(pair.Verifier), pair.Challenge);
        Assert.Equal("S256", pair.Method);
        var state = Pkce.RandomState();
        Assert.True(Pkce.StateMatches(state, state));
        Assert.False(Pkce.StateMatches(state, state + "x"));
        Assert.False(Pkce.StateMatches(state, null));
    }

    // ───────────────────────── 账户池 ─────────────────────────

    private static ProviderAccount Account(string id, int order, bool enabled = true, bool needsReauth = false, AccountLimit? limit = null) =>
        new(id, "p", "chatgpt", order, enabled, NeedsReauth: needsReauth, Limit: limit);

    private static FakePort Port(params ProviderAccount[] accounts)
    {
        var port = new FakePort();
        port.Accounts.AddRange(accounts);
        return port;
    }

    private static AgentError Failure(AgentErrorCode code, int status, string message, int? retryAfterMs = null) =>
        AgentError.Create(code, message, status: status, retryAfterMs: retryAfterMs);

    [Fact]
    public void The_first_usable_account_in_order_is_selected_skipping_disabled_reauth_and_limited_ones()
    {
        var port = Port(
            Account("c", 3),
            Account("a", 1, enabled: false),
            Account("b", 2, needsReauth: true),
            Account("d", 0, limit: new AccountLimit(2_000_000, 0, AccountLimitSource.Http429, "x")),
            Account("e", 4, limit: new AccountLimit(500_000, 0, AccountLimitSource.Http429, "expired")));

        Assert.Equal("c", new AccountPool(port).Select("p")!.Id); // d 被限流,a 停用,b 待重登;c(顺序 3)排在已到期的 e(顺序 4)之前
    }

    [Fact]
    public void Selection_order_is_by_order_then_id_and_limits_expire_on_their_own()
    {
        var port = Port(
            Account("z", 1),
            Account("a", 1, limit: new AccountLimit(1_000_100, 0, AccountLimitSource.Http429, "x")),
            Account("m", 2));
        var pool = new AccountPool(port);

        Assert.Equal("z", pool.Select("p")!.Id); // a 被限流
        port.Now = 1_000_200; // 到期自动为假,不需要任何人去「解」
        Assert.Equal("a", pool.Select("p")!.Id); // order 并列按 id 稳定排
    }

    [Fact]
    public void No_accounts_is_distinguishable_from_all_accounts_unusable()
    {
        var pool = new AccountPool(Port(Account("a", 1, enabled: false)));

        Assert.Null(pool.Select("p"));
        Assert.True(pool.HasAccounts("p"));
        Assert.False(pool.HasAccounts("other"));
    }

    [Theory]
    [InlineData(AgentErrorCode.RateLimit, 429, "slow down", true)]
    [InlineData(AgentErrorCode.Provider, 403, "You exceeded your current quota", true)]
    [InlineData(AgentErrorCode.Provider, 402, "insufficient_quota", true)]
    [InlineData(AgentErrorCode.Provider, 403, "usage limit reached for this plan", true)]
    [InlineData(AgentErrorCode.Auth, 401, "Your quota is exhausted", false)] // 401 不算
    [InlineData(AgentErrorCode.Provider, 403, "forbidden", false)] // 403 没有配额字样
    [InlineData(AgentErrorCode.Provider, 500, "quota", false)]
    public void Quota_exhaustion_is_429_or_402_403_with_quota_wording_but_never_401(AgentErrorCode code, int status, string message, bool expected)
    {
        Assert.Equal(expected, AccountPool.IsQuotaFailure(Failure(code, status, message)));
    }

    [Fact]
    public void A_quota_failure_sets_the_limit_from_retry_after_and_returns_the_next_usable_account()
    {
        var port = Port(Account("a", 1), Account("b", 2));
        var pool = new AccountPool(port);

        var next = pool.ReportFailure(port.Accounts[0], Failure(AgentErrorCode.RateLimit, 429, "slow", 120_000));

        Assert.Equal("b", next!.Id);
        Assert.Equal(1_000_000 + 120_000, port.Accounts[0].Limit!.Until);
        Assert.Equal(["p"], port.Changes);
    }

    [Fact]
    public void Without_retry_after_the_default_limit_is_minutes_not_the_routers_seconds_backoff()
    {
        var port = Port(Account("a", 1));

        new AccountPool(port).ReportFailure(port.Accounts[0], Failure(AgentErrorCode.RateLimit, 429, "slow"));

        Assert.Equal(1_000_000 + 5 * 60_000, port.Accounts[0].Limit!.Until);
    }

    [Fact]
    public void With_rotation_disabled_the_limit_is_recorded_but_no_next_account_is_offered()
    {
        // 轮换关掉时落闸照落(用户关掉的是自动换号,不是知情权),但绝不返回下一个账号——这一行还挡着路由里的死循环。
        var port = Port(Account("a", 1), Account("b", 2));
        port.Rotation = false;

        var next = new AccountPool(port).ReportFailure(port.Accounts[0], Failure(AgentErrorCode.RateLimit, 429, "slow"));

        Assert.Null(next);
        Assert.NotNull(port.Accounts[0].Limit);
    }

    [Fact]
    public void A_later_limit_is_never_shortened_by_a_concurrent_earlier_one_and_manual_limits_are_untouchable()
    {
        var port = Port(Account("a", 1, limit: new AccountLimit(9_000_000, 0, AccountLimitSource.Http429, "1h")), Account("m", 2, limit: new AccountLimit(5_000_000, 0, AccountLimitSource.Manual, "me")));
        var pool = new AccountPool(port);

        pool.ReportFailure(port.Accounts[0], Failure(AgentErrorCode.RateLimit, 429, "short", 60_000));
        pool.ReportFailure(port.Accounts[1], Failure(AgentErrorCode.RateLimit, 429, "longer", 3_600_000));

        Assert.Equal(9_000_000, port.Accounts[0].Limit!.Until);
        Assert.Equal(AccountLimitSource.Manual, port.Accounts[1].Limit!.Source);
        Assert.Equal(5_000_000, port.Accounts[1].Limit!.Until);
    }

    [Fact]
    public void Success_clears_a_rate_limit_but_not_a_manual_one()
    {
        var port = Port(Account("a", 1, limit: new AccountLimit(9_000_000, 0, AccountLimitSource.Http429, "x")), Account("m", 2, limit: new AccountLimit(9_000_000, 0, AccountLimitSource.Manual, "me")));
        var pool = new AccountPool(port);

        pool.ReportSuccess(port.Accounts[0]);
        pool.ReportSuccess(port.Accounts[1]);

        Assert.Null(port.Accounts[0].Limit);
        Assert.NotNull(port.Accounts[1].Limit);
    }

    [Fact]
    public void A_full_quota_window_limits_the_account_until_the_reset_taking_the_later_window()
    {
        var port = Port(Account("a", 1));

        new AccountPool(port).ReportQuota(port.Accounts[0], new QuotaSnapshot(
            new QuotaWindow(100, 300, 2_000_000), new QuotaWindow(100, 10_080, 3_000_000), 1_000_000));

        Assert.Equal(3_000_000, port.Accounts[0].Limit!.Until);
        Assert.Equal(AccountLimitSource.QuotaExhausted, port.Accounts[0].Limit!.Source);
        Assert.NotNull(port.Accounts[0].Quota);
    }

    [Fact]
    public void A_non_full_quota_snapshot_only_updates_the_snapshot()
    {
        var port = Port(Account("a", 1));

        new AccountPool(port).ReportQuota(port.Accounts[0], new QuotaSnapshot(new QuotaWindow(40, 300, 2_000_000), null, 1_000_000));

        Assert.Null(port.Accounts[0].Limit);
    }

    [Fact]
    public void Earliest_recovery_ignores_disabled_and_reauth_accounts_which_will_not_heal_by_waiting()
    {
        var port = Port(
            Account("a", 1, limit: new AccountLimit(3_000_000, 0, AccountLimitSource.Http429, "x")),
            Account("b", 2, limit: new AccountLimit(2_000_000, 0, AccountLimitSource.Http429, "x")),
            Account("c", 3, enabled: false, limit: new AccountLimit(1_500_000, 0, AccountLimitSource.Http429, "x")),
            Account("d", 4, needsReauth: true, limit: new AccountLimit(1_200_000, 0, AccountLimitSource.Http429, "x")));

        Assert.Equal(2_000_000, new AccountPool(port).EarliestRecoveryAt("p"));
        Assert.Null(new AccountPool(Port(Account("a", 1, enabled: false))).EarliestRecoveryAt("p"));
    }
}
