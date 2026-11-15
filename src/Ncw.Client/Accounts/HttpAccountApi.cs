using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ncw.Client.Accounts;

/// <summary>
/// <see cref="IAccountApi"/> 的 HTTP 实现(端点与字段沿用 2.x 的 <c>ipc/client-auth.ts</c>,新增端点见 <see cref="AccountEndpoints"/>)。
/// <see cref="HttpClient"/> 由调用方注入(默认一份不跟随重定向、有超时的;测试注入假的 <see cref="HttpMessageHandler"/>)。
/// 响应用 <see cref="JsonDocument"/> 手工读取(不依赖反射;容忍 <c>{ data: … }</c> 外壳)。<b>令牌只出现在请求头 / 请求体里,不进异常消息、不进日志。</b>
/// </summary>
public sealed class HttpAccountApi(HttpClient http, AccountEndpoints? endpoints = null, TimeProvider? clock = null) : IAccountApi
{
    private readonly AccountEndpoints _endpoints = endpoints ?? AccountEndpoints.Default;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>默认的 HttpClient:不跟随重定向(令牌请求不该被带去别处)、自动解压、有总超时。</summary>
    public static HttpClient CreateDefaultClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    public Uri BuildAuthorizeUri(string state, string codeChallenge)
    {
        var query = new StringBuilder();
        void Add(string name, string value)
        {
            if (query.Length > 0)
            {
                query.Append('&');
            }

            query.Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(value));
        }

        Add("client_id", _endpoints.ClientId);
        Add("redirect_uri", _endpoints.RedirectUri);
        Add("response_type", "code");
        Add("state", state);
        Add("code_challenge", codeChallenge);
        Add("code_challenge_method", "S256");
        Add("scope", _endpoints.Scope);
        return new UriBuilder(new Uri(_endpoints.Origin, _endpoints.AuthorizePath)) { Query = query.ToString() }.Uri;
    }

    public async Task<TokenSet> ExchangeCodeAsync(string code, string codeVerifier, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Post, _endpoints.TokenPath, null, new JsonObject
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = _endpoints.ClientId,
            ["code"] = code,
            ["redirect_uri"] = _endpoints.RedirectUri,
            ["code_verifier"] = codeVerifier,
        }, ct).ConfigureAwait(false);
        return ReadTokens(doc);
    }

    public async Task<TokenSet> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Post, _endpoints.TokenPath, null, new JsonObject
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = _endpoints.ClientId,
            ["refresh_token"] = refreshToken,
        }, ct, refreshing: true).ConfigureAwait(false);
        return ReadTokens(doc);
    }

    public async Task RevokeAsync(string refreshToken, CancellationToken ct)
    {
        using var _ = await SendAsync(HttpMethod.Post, _endpoints.RevokePath, null, new JsonObject
        {
            ["client_id"] = _endpoints.ClientId,
            ["refresh_token"] = refreshToken,
        }, ct, allowEmpty: true).ConfigureAwait(false);
    }

    public async Task<AccountUser> GetUserAsync(string accessToken, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, _endpoints.AccountPath, accessToken, null, ct).ConfigureAwait(false);
        var payload = Unwrap(doc.RootElement);
        var user = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object ? u : payload;
        return ReadUser(user) ?? throw new AccountApiException(AccountApiErrorKind.BadResponse, "The account response has no user.");
    }

    public async Task<TeamContext> GetTeamsAsync(string accessToken, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, _endpoints.ContextOptionsPath, accessToken, null, ct).ConfigureAwait(false);
        var payload = Unwrap(doc.RootElement);
        var teams = new List<TeamOption>();
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (Str(item, "id") is { Length: > 0 } id)
                {
                    teams.Add(new TeamOption(id, Str(item, "name") ?? id, Str(item, "type") ?? "", Str(item, "role") ?? "", (int)(Num(item, "memberCount") ?? 0), Bool(item, "isSelected") ?? false));
                }
            }
        }

        return new TeamContext(teams, payload.ValueKind == JsonValueKind.Object ? Str(payload, "selectedTeamId") : null);
    }

    public async Task SelectTeamAsync(string accessToken, string teamId, CancellationToken ct)
    {
        using var _ = await SendAsync(HttpMethod.Post, _endpoints.ContextSelectPath, accessToken, new JsonObject { ["team"] = teamId }, ct, allowEmpty: true).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AccountUsageEntry>> GetUsageAsync(string accessToken, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        var path = _endpoints.UsagePath;
        var parameters = new List<string>();
        if (from is { } f)
        {
            parameters.Add("from=" + Uri.EscapeDataString(f.ToString("O", CultureInfo.InvariantCulture)));
        }

        if (to is { } t)
        {
            parameters.Add("to=" + Uri.EscapeDataString(t.ToString("O", CultureInfo.InvariantCulture)));
        }

        if (parameters.Count > 0)
        {
            path += "?" + string.Join('&', parameters);
        }

        using var doc = await SendAsync(HttpMethod.Get, path, accessToken, null, ct).ConfigureAwait(false);
        var payload = Unwrap(doc.RootElement);
        var entries = new List<AccountUsageEntry>();
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (Str(item, "id") is { Length: > 0 } id && Str(item, "at") is { } at && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when))
                {
                    entries.Add(new AccountUsageEntry(id, when, Str(item, "model"), Str(item, "provider"), (long)(Num(item, "inputTokens") ?? 0), (long)(Num(item, "outputTokens") ?? 0),
                        Num(item, "cost") is { } cost ? (decimal)cost : null, Str(item, "currency")));
                }
            }
        }

        return entries;
    }

    public async Task<DeviceAuthorization> StartDeviceAuthorizationAsync(CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Post, _endpoints.DeviceAuthorizationPath, null, new JsonObject
        {
            ["client_id"] = _endpoints.ClientId,
            ["scope"] = _endpoints.Scope,
        }, ct).ConfigureAwait(false);
        var root = Unwrap(doc.RootElement);
        var deviceCode = Str(root, "device_code");
        var userCode = Str(root, "user_code");
        var uri = Str(root, "verification_uri");
        if (string.IsNullOrEmpty(deviceCode) || string.IsNullOrEmpty(userCode) || string.IsNullOrEmpty(uri))
        {
            throw new AccountApiException(AccountApiErrorKind.BadResponse, "The device authorization response is incomplete.");
        }

        var expires = TimeSpan.FromSeconds(Math.Clamp(Num(root, "expires_in") ?? 600, 30, 1800));
        var interval = TimeSpan.FromSeconds(Math.Clamp(Num(root, "interval") ?? 5, 1, 60));
        return new DeviceAuthorization(deviceCode, userCode, uri, Str(root, "verification_uri_complete"), expires, interval);
    }

    public async Task<DevicePollResult> PollDeviceAuthorizationAsync(string deviceCode, CancellationToken ct)
    {
        try
        {
            using var doc = await SendAsync(HttpMethod.Post, _endpoints.TokenPath, null, new JsonObject
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["client_id"] = _endpoints.ClientId,
                ["device_code"] = deviceCode,
            }, ct).ConfigureAwait(false);
            return new DevicePollResult(DevicePollKind.Granted, ReadTokens(doc));
        }
        catch (AccountApiException e) when (e.OAuthError is { } error)
        {
            DevicePollKind? kind = error switch
            {
                "authorization_pending" => DevicePollKind.Pending,
                "slow_down" => DevicePollKind.SlowDown,
                "access_denied" => DevicePollKind.Denied,
                "expired_token" => DevicePollKind.Expired,
                _ => null,
            };
            if (kind is null)
            {
                throw;
            }

            return new DevicePollResult(kind.Value);
        }
    }

    public async Task<DelegatedGrant?> ExchangeDelegatedAsync(string accessToken, CancellationToken ct)
    {
        try
        {
            using var doc = await SendAsync(HttpMethod.Post, _endpoints.DelegatePath, accessToken, new JsonObject
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
                ["client_id"] = _endpoints.ClientId,
                ["audience"] = "ncw-agent",
                ["scope"] = "models:read inference:write config:read config:write skills:read skills:install plugins:read plugins:install",
            }, ct).ConfigureAwait(false);
            var root = Unwrap(doc.RootElement);
            var token = Str(root, "access_token") ?? Str(root, "token");
            var seconds = Num(root, "expires_in");
            return string.IsNullOrEmpty(token) || seconds is null or <= 0
                ? throw new AccountApiException(AccountApiErrorKind.BadResponse, "The delegation response is incomplete.")
                : new DelegatedGrant(token, _clock.GetUtcNow().AddSeconds(seconds.Value));
        }
        catch (AccountApiException e) when (e.Kind == AccountApiErrorKind.Unsupported)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<ControllerSession>> ListControllersAsync(string accessToken, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, _endpoints.ControllersPath, accessToken, null, ct).ConfigureAwait(false);
        var payload = Unwrap(doc.RootElement);
        var sessions = new List<ControllerSession>();
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (Str(item, "id") is { Length: > 0 } id)
                {
                    DateTimeOffset? last = Str(item, "lastActiveAt") is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
                    sessions.Add(new ControllerSession(id, Str(item, "name") ?? id, Str(item, "platform") ?? "", last, Bool(item, "isCurrent") ?? false));
                }
            }
        }

        return sessions;
    }

    public async Task RevokeControllerAsync(string accessToken, string controllerId, CancellationToken ct)
    {
        using var _ = await SendAsync(HttpMethod.Delete, _endpoints.ControllersPath + "/" + Uri.EscapeDataString(controllerId), accessToken, null, ct, allowEmpty: true).ConfigureAwait(false);
    }

    // ───────────────────────── 传输 ─────────────────────────

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, string? bearer, JsonObject? body, CancellationToken ct, bool allowEmpty = false, bool refreshing = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_endpoints.Timeout);
        using var request = new HttpRequestMessage(method, new Uri(_endpoints.Origin, path));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AccountApiException(AccountApiErrorKind.Network, "The account service did not answer in time.");
        }
        catch (HttpRequestException e)
        {
            throw new AccountApiException(AccountApiErrorKind.Network, "The account service could not be reached.", inner: e);
        }

        using (response)
        {
            string text;
            try
            {
                var bytes = await ReadBoundedAsync(response, timeout.Token).ConfigureAwait(false);
                text = Encoding.UTF8.GetString(bytes);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new AccountApiException(AccountApiErrorKind.Network, "The account service did not answer in time.");
            }
            catch (HttpRequestException e)
            {
                throw new AccountApiException(AccountApiErrorKind.Network, "The account service connection was interrupted.", inner: e);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw Failure(response.StatusCode, text, refreshing);
            }

            if (text.Length == 0)
            {
                return allowEmpty ? JsonDocument.Parse("{}") : throw new AccountApiException(AccountApiErrorKind.BadResponse, "The account service returned an empty response.");
            }

            try
            {
                return JsonDocument.Parse(text);
            }
            catch (JsonException e)
            {
                if (allowEmpty)
                {
                    return JsonDocument.Parse("{}");
                }

                throw new AccountApiException(AccountApiErrorKind.BadResponse, "The account service returned a response that is not JSON.", (int)response.StatusCode, inner: e);
            }
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        const int Max = 1024 * 1024;
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            for (;;)
            {
                var read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
                if (read == 0)
                {
                    return buffer.ToArray();
                }

                if (buffer.Length + read > Max)
                {
                    throw new AccountApiException(AccountApiErrorKind.BadResponse, "The account service response is too large.");
                }

                buffer.Write(chunk, 0, read);
            }
        }
    }

    private static AccountApiException Failure(HttpStatusCode status, string body, bool refreshing)
    {
        var code = (int)status;
        string? oauthError = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                oauthError = Str(doc.RootElement, "error");
                if (oauthError is null && doc.RootElement.TryGetProperty("error", out var nested) && nested.ValueKind == JsonValueKind.Object)
                {
                    oauthError = Str(nested, "code");
                }
            }
        }
        catch (JsonException)
        {
            // 不是 JSON:只看状态码
        }

        var kind = status switch
        {
            HttpStatusCode.Unauthorized when refreshing => AccountApiErrorKind.InvalidGrant,
            HttpStatusCode.Unauthorized => AccountApiErrorKind.Unauthorized,
            HttpStatusCode.Forbidden => AccountApiErrorKind.Forbidden,
            HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented => AccountApiErrorKind.Unsupported,
            >= HttpStatusCode.InternalServerError => AccountApiErrorKind.Server,
            _ when oauthError == "invalid_grant" => AccountApiErrorKind.InvalidGrant,
            _ => AccountApiErrorKind.Rejected,
        };
        if (kind == AccountApiErrorKind.Rejected && oauthError == "invalid_grant")
        {
            kind = AccountApiErrorKind.InvalidGrant;
        }

        return new AccountApiException(kind, $"The account service answered {code}.", code, oauthError);
    }

    // ───────────────────────── 解析 ─────────────────────────

    private TokenSet ReadTokens(JsonDocument doc)
    {
        var root = Unwrap(doc.RootElement);
        var access = Str(root, "access_token");
        var refresh = Str(root, "refresh_token");
        var seconds = Num(root, "expires_in");
        if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(refresh) || seconds is null or <= 0)
        {
            throw new AccountApiException(AccountApiErrorKind.BadResponse, "The token response is incomplete.");
        }

        var user = root.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object ? ReadUser(u) : null;
        return new TokenSet(access, refresh, _clock.GetUtcNow().AddSeconds(seconds.Value), user);
    }

    private static AccountUser? ReadUser(JsonElement element) =>
        Str(element, "id") is { Length: > 0 } id
            ? new AccountUser(id, Str(element, "username"), Str(element, "email"), Str(element, "displayName"), Str(element, "avatarUrl"))
            : null;

    /// <summary>服务端有的响应带 <c>{ data: … }</c> 外壳,有的没有(2.x 两种都兼容)。</summary>
    private static JsonElement Unwrap(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object ? data : root;

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;

    private static bool? Bool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
}
