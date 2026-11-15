using System.Net;
using System.Text;
using Ncw.Client.Accounts;

namespace Ncw.Client.Tests.Accounts;

/// <summary>默认实现对着本地假 HTTP 处理器(不开端口、不联网):请求形状、响应外壳、错误分类、令牌不进异常。</summary>
public class HttpAccountApiTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri, string? Auth, string Body)> Requests { get; } = [];

        public Func<HttpRequestMessage, HttpResponseMessage>? Respond { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body));
            return Respond?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.OK) { Content = Json("{}") };
        }
    }

    private static StringContent Json(string text) => new(text, Encoding.UTF8, "application/json");

    private static (HttpAccountApi Api, Handler Handler, ManualTimeProvider Clock) Make(Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
    {
        var clock = new ManualTimeProvider(AccountRig.Start);
        var handler = new Handler { Respond = respond };
        return (new HttpAccountApi(new HttpClient(handler), new AccountEndpoints { Origin = new Uri("https://api.example.test/") }, clock), handler, clock);
    }

    [Fact]
    public void The_authorize_url_carries_the_pkce_parameters_the_client_id_and_the_custom_scheme_redirect()
    {
        var (api, _, _) = Make();
        var uri = api.BuildAuthorizeUri("STATE-1", "CHALLENGE-1");
        Assert.Equal("https://api.example.test/oauth/authorize", uri.GetLeftPart(UriPartial.Path));
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        Assert.Equal("ncw-desktop", query["client_id"]);
        Assert.Equal("nextcowork://auth/callback", query["redirect_uri"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("STATE-1", query["state"]);
        Assert.Equal("CHALLENGE-1", query["code_challenge"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Contains("plugins:install", query["scope"]);
    }

    [Fact]
    public async Task Exchanging_a_code_posts_the_verifier_and_reads_the_token_response_with_or_without_a_data_envelope()
    {
        foreach (var envelope in new[] { false, true })
        {
            var json = """{"access_token":"A","refresh_token":"R","expires_in":900,"user":{"id":"u1","email":"a@b.c","displayName":"Ann"}}""";
            var (api, handler, clock) = Make(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(envelope ? $$"""{"data":{{json}}}""" : json) });
            var tokens = await api.ExchangeCodeAsync("CODE", "VERIFIER", CancellationToken.None);
            Assert.Equal(("A", "R"), (tokens.AccessToken, tokens.RefreshToken));
            Assert.Equal(clock.GetUtcNow().AddSeconds(900), tokens.ExpiresAt);
            Assert.Equal("Ann", tokens.User!.Label);
            var request = Assert.Single(handler.Requests);
            Assert.Equal("https://api.example.test/api/client/oauth/token", request.Uri.ToString());
            Assert.Contains("\"grant_type\":\"authorization_code\"", request.Body);
            Assert.Contains("\"code_verifier\":\"VERIFIER\"", request.Body);
            Assert.Null(request.Auth); // 换码不带令牌
        }
    }

    [Fact]
    public async Task A_refresh_the_server_rejects_is_an_invalid_grant_whether_it_answers_401_or_400()
    {
        foreach (var (status, body) in new[] { (HttpStatusCode.Unauthorized, ""), (HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""") })
        {
            var (api, _, _) = Make(_ => new HttpResponseMessage(status) { Content = Json(body) });
            var e = await Assert.ThrowsAsync<AccountApiException>(() => api.RefreshAsync("RT-SECRET", CancellationToken.None));
            Assert.Equal(AccountApiErrorKind.InvalidGrant, e.Kind);
            Assert.DoesNotContain("RT-SECRET", e.ToString()); // 令牌不进异常
        }
    }

    [Fact]
    public async Task Transport_failures_and_timeouts_are_network_errors_and_server_errors_are_server_errors()
    {
        var down = new HttpAccountApi(new HttpClient(new Handler { Respond = _ => throw new HttpRequestException("boom") }), new AccountEndpoints { Origin = new Uri("https://api.example.test/") });
        Assert.Equal(AccountApiErrorKind.Network, (await Assert.ThrowsAsync<AccountApiException>(() => down.GetUserAsync("AT", CancellationToken.None))).Kind);

        var (server, _, _) = Make(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        Assert.Equal(AccountApiErrorKind.Server, (await Assert.ThrowsAsync<AccountApiException>(() => server.GetUserAsync("AT", CancellationToken.None))).Kind);

        var (garbage, _, _) = Make(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = Json("<html>") });
        Assert.Equal(AccountApiErrorKind.BadResponse, (await Assert.ThrowsAsync<AccountApiException>(() => garbage.GetUserAsync("AT", CancellationToken.None))).Kind);

        var (incomplete, _, _) = Make(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = Json("""{"access_token":"A"}""") });
        Assert.Equal(AccountApiErrorKind.BadResponse, (await Assert.ThrowsAsync<AccountApiException>(() => incomplete.ExchangeCodeAsync("C", "V", CancellationToken.None))).Kind);
    }

    [Fact]
    public async Task Teams_and_the_user_are_read_with_the_bearer_token()
    {
        var (api, handler, _) = Make(r => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json(r.RequestUri!.AbsolutePath.EndsWith("options", StringComparison.Ordinal)
                ? """{"data":{"items":[{"id":"t1","name":"Acme","type":"team","role":"owner","memberCount":4,"isSelected":true},{"id":"t2","name":"Beta"}],"selectedTeamId":"t1"}}"""
                : """{"data":{"user":{"id":"u1","username":"alice"}}}"""),
        });
        var context = await api.GetTeamsAsync("AT", CancellationToken.None);
        Assert.Equal(["t1", "t2"], context.Teams.Select(t => t.Id));
        Assert.Equal(("t1", 4, true), (context.SelectedTeamId, context.Teams[0].MemberCount, context.Teams[0].IsSelected));
        Assert.Equal("alice", (await api.GetUserAsync("AT", CancellationToken.None)).Label);
        Assert.All(handler.Requests, r => Assert.Equal("Bearer AT", r.Auth));
    }

    [Fact]
    public async Task Selecting_a_team_posts_the_team_id()
    {
        var (api, handler, _) = Make();
        await api.SelectTeamAsync("AT", "t9", CancellationToken.None);
        Assert.Equal("""{"team":"t9"}""", handler.Requests[0].Body);
        Assert.EndsWith("/api/client/context/select", handler.Requests[0].Uri.AbsolutePath);
    }

    [Fact]
    public async Task The_device_authorization_flow_maps_the_RFC_8628_answers()
    {
        var (api, handler, _) = Make(r => r.RequestUri!.AbsolutePath.EndsWith("device_authorization", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = Json("""{"device_code":"DC","user_code":"ABCD-EFGH","verification_uri":"https://x.test/device","expires_in":600,"interval":7}""") }
            : new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = Json("""{"error":"authorization_pending"}""") });
        var auth = await api.StartDeviceAuthorizationAsync(CancellationToken.None);
        Assert.Equal(("DC", "ABCD-EFGH", 7, 600), (auth.DeviceCode, auth.UserCode, (int)auth.Interval.TotalSeconds, (int)auth.ExpiresIn.TotalSeconds));
        Assert.Equal(DevicePollKind.Pending, (await api.PollDeviceAuthorizationAsync("DC", CancellationToken.None)).Kind);
        Assert.Contains("urn:ietf:params:oauth:grant-type:device_code", handler.Requests[1].Body);

        foreach (var (error, kind) in new[] { ("slow_down", DevicePollKind.SlowDown), ("access_denied", DevicePollKind.Denied), ("expired_token", DevicePollKind.Expired) })
        {
            var (poll, _, _) = Make(_ => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = Json($$"""{"error":"{{error}}"}""") });
            Assert.Equal(kind, (await poll.PollDeviceAuthorizationAsync("DC", CancellationToken.None)).Kind);
        }

        var (granted, _, _) = Make(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = Json("""{"access_token":"A","refresh_token":"R","expires_in":60}""") });
        var result = await granted.PollDeviceAuthorizationAsync("DC", CancellationToken.None);
        Assert.Equal((DevicePollKind.Granted, "A"), (result.Kind, result.Tokens!.AccessToken));

        var (other, _, _) = Make(_ => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = Json("""{"error":"invalid_client"}""") });
        await Assert.ThrowsAsync<AccountApiException>(() => other.PollDeviceAuthorizationAsync("DC", CancellationToken.None));
    }

    [Fact]
    public async Task A_backend_without_the_delegation_endpoint_yields_null_so_the_caller_can_fall_back()
    {
        var (api, _, _) = Make(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Null(await api.ExchangeDelegatedAsync("AT", CancellationToken.None));

        var (ok, handler, clock) = Make(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = Json("""{"access_token":"DT","expires_in":1800}""") });
        var grant = await ok.ExchangeDelegatedAsync("AT", CancellationToken.None);
        Assert.Equal(("DT", clock.GetUtcNow().AddSeconds(1800)), (grant!.Token, grant.ExpiresAt));
        Assert.Contains("token-exchange", handler.Requests[0].Body);
        Assert.Equal("Bearer AT", handler.Requests[0].Auth);
    }

    [Fact]
    public async Task Revoke_usage_and_controller_calls_use_the_documented_endpoints()
    {
        var (api, handler, _) = Make(r => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json(r.RequestUri!.AbsolutePath.EndsWith("usage", StringComparison.Ordinal)
                ? """{"data":{"items":[{"id":"x","at":"2026-11-13T10:00:00Z","model":"m","inputTokens":10,"outputTokens":5,"cost":0.5,"currency":"USD"}]}}"""
                : """{"items":[{"id":"c1","name":"Mac","platform":"macOS","isCurrent":true}]}"""),
        });
        await api.RevokeAsync("RT", CancellationToken.None);
        var usage = await api.GetUsageAsync("AT", AccountRig.Start.AddDays(-1), null, CancellationToken.None);
        Assert.Equal(("x", 10L, 0.5m), (usage[0].Id, usage[0].InputTokens, usage[0].Cost));
        Assert.True((await api.ListControllersAsync("AT", CancellationToken.None))[0].IsCurrent);
        await api.RevokeControllerAsync("AT", "c 1", CancellationToken.None);

        Assert.EndsWith("/api/client/oauth/revoke", handler.Requests[0].Uri.AbsolutePath);
        Assert.Contains("from=", handler.Requests[1].Uri.Query);
        Assert.EndsWith("/api/client/sessions/c%201", handler.Requests[3].Uri.AbsolutePath);
        Assert.Equal(HttpMethod.Delete, handler.Requests[3].Method);
    }
}
