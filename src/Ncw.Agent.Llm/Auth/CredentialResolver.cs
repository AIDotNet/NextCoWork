using System.Text.Json;
using Ncw.Agent.Llm.Json;

namespace Ncw.Agent.Llm.Auth;

/// <summary>刷新失败。带着一个已经分好类的 <see cref="Llm.AgentError"/>,路由直接把它当结果用,不要在上层重新猜一遍。</summary>
public sealed class CredentialAuthException(AgentError error) : Exception(error.Message)
{
    public AgentError Error { get; } = error;
}

/// <summary>
/// 凭据解析与刷新(01-llm §7):路由和「凭据是不是还有效」之间的那一层。
/// </summary>
public sealed class CredentialResolver(
    ICredentialStore store,
    IOAuthRegistry registry,
    IUpstreamHttp http,
    TimeProvider clock,
    Action<string>? onChanged = null)
{
    /// <summary>
    /// 提前多久开始刷。不能是 0:请求发出去要时间,一把「还有 3 秒过期」的 token 到达上游时已经死了,
    /// 而那个 401 会被当成「凭据无效」而不是「该刷新了」。
    /// </summary>
    public static readonly TimeSpan Skew = TimeSpan.FromSeconds(60);

    /// <summary>刷新自己的超时。调用方的取消令牌只用来取消「等待」,不取消一次已经在飞的刷新(见 <see cref="RefreshOnce"/>)。</summary>
    public static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();

    /// <summary>
    /// 按 ref 去重(ref 才是凭据的身份):OAuth 的 refresh token 是轮换的,用一次就作废。
    /// 一个带 3 个子代理的 Run 会在同一毫秒发出 3 个请求:不去重就是 3 次并发刷新,后两次拿着已作废的 token 得到 invalid_grant,
    /// 失败再去写库的话用户会被我们自己的并发踢下线。
    /// </summary>
    private readonly Dictionary<string, Task<OAuthCredential>> _inflight = [];

    private long NowMs => clock.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>读凭据;是 OAuth 且快过期了就先刷一次。没配过返回 null(不是错误)。</summary>
    public async Task<ProviderCredential?> ResolveAsync(string credentialRef, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var credential = CredentialFormat.Parse(await store.GetAsync(credentialRef, ct).ConfigureAwait(false));

        // 签名凭据没有可刷新的东西:原样返回,由签名器每次请求重算。
        if (credential is not OAuthCredential oauth)
        {
            return credential;
        }

        // expiresAt 为 null = 不知道什么时候过期,一律答「先别刷」:把未知当成已过期会让每次请求前都先刷一遍
        // (对 refresh token 轮换的家,还是自己给自己制造并发的路)。未知时的兜底是 401 之后的强制刷新。
        if (oauth.ExpiresAt is not { } expiresAt || expiresAt - NowMs >= (long)Skew.TotalMilliseconds)
        {
            return oauth;
        }

        return await WaitAsync(RefreshOnce(credentialRef, oauth), ct).ConfigureAwait(false);
    }

    /// <summary>不看 expiresAt,强制刷一次。给 401 之后的重试用:覆盖本机时钟偏和服务端主动吊销。</summary>
    public async Task<OAuthCredential> RefreshNowAsync(string credentialRef, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (CredentialFormat.Parse(await store.GetAsync(credentialRef, ct).ConfigureAwait(false)) is not OAuthCredential oauth)
        {
            throw new CredentialAuthException(AgentError.Create(AgentErrorCode.Auth, "This credential is not an account login and cannot be refreshed.", false));
        }

        return await WaitAsync(RefreshOnce(credentialRef, oauth), ct).ConfigureAwait(false);
    }

    /// <summary>等一个已经在飞的刷新,但等待本身可被取消;被取消时刷新继续跑完(新令牌不会丢)。</summary>
    private static async Task<OAuthCredential> WaitAsync(Task<OAuthCredential> task, CancellationToken ct) => await task.WaitAsync(ct).ConfigureAwait(false);

    private Task<OAuthCredential> RefreshOnce(string credentialRef, OAuthCredential credential)
    {
        lock (_gate)
        {
            // 第二个调用者等同一个 Task,不发第二次请求。
            if (_inflight.TryGetValue(credentialRef, out var running))
            {
                return running;
            }

            var task = Task.Run(async () =>
            {
                try
                {
                    return await DoRefreshAsync(credentialRef, credential).ConfigureAwait(false);
                }
                finally
                {
                    lock (_gate)
                    {
                        _inflight.Remove(credentialRef);
                    }
                }
            });
            _inflight[credentialRef] = task;
            return task;
        }
    }

    private async Task<OAuthCredential> DoRefreshAsync(string credentialRef, OAuthCredential credential)
    {
        var spec = registry.Get(credential.Issuer)
            ?? throw new CredentialAuthException(AgentError.Create(AgentErrorCode.Auth, $"Unknown OAuth issuer: {credential.Issuer}", false));

        // 刷新用自己的 30 秒超时,不用调用方的取消令牌:刷新请求一发出去旧 refresh token 就被上游消耗掉了,
        // 中途放弃等于扔掉响应里那把新的、而旧的已经作废,用户被登出,原因是我们自己取消了一次请求。
        using var cts = new CancellationTokenSource(RefreshTimeout, clock);
        var identity = spec.Refresh is { } hook
            ? await CustomRefreshAsync(hook, credential, cts.Token).ConfigureAwait(false)
            : await StandardRefreshAsync(credentialRef, credential, spec, cts.Token).ConfigureAwait(false);

        if (identity is null)
        {
            throw await MarkReauthAsync(credentialRef, credential, "The refreshed credential is incomplete; please sign in again.");
        }

        var next = credential with
        {
            AccessToken = identity.AccessToken,
            // 轮换后的 refresh token 必须存下来;上游有的家刷新时不回新的,那就沿用旧的。
            RefreshToken = string.IsNullOrEmpty(identity.RefreshToken) ? credential.RefreshToken : identity.RefreshToken,
            ExpiresAt = identity.ExpiresAt,
            AccountId = identity.AccountId,
            Email = identity.Email ?? credential.Email,
            PlanType = identity.PlanType ?? credential.PlanType,
            RefreshedAt = NowMs,
            NeedsReauth = false,
        };

        // 写回必须在返回之前完成:轮换后的 refresh token 只存在于这一次响应里,进程崩在写回之前,
        // 库里那把旧的已经被上游作废了,用户下次启动直接掉线。写回也不用调用方的取消令牌。
        await store.SetAsync(credentialRef, CredentialFormat.Serialize(next), CancellationToken.None).ConfigureAwait(false);
        onChanged?.Invoke(credentialRef);
        return next;
    }

    /// <summary>标准 <c>grant_type=refresh_token</c>。没有声明 refresh 钩子的家走这里。</summary>
    private async Task<OAuthIdentity?> StandardRefreshAsync(string credentialRef, OAuthCredential credential, OAuthProviderSpec spec, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = credential.RefreshToken,
            ["client_id"] = spec.ClientId,
        };

        // scope 没声明的家一个字都不能写(写成字面量 "undefined" 发出去的 400 不会提到 scope)。
        if (spec.Scope is { } scope)
        {
            form["scope"] = scope;
        }

        if (spec.ExtraTokenParams is { } extra)
        {
            foreach (var (key, value) in extra)
            {
                form[key] = value;
            }
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, spec.TokenUrl) { Content = new FormUrlEncodedContent(form) };
        request.Headers.TryAddWithoutValidation("accept", "application/json");

        // 这家的私货头在刷新这一跳上一样要带:漏掉的表现不是登不上,而是能登录、第二天刷新 403。
        if (spec.Headers is { } headers)
        {
            foreach (var (key, value) in headers)
            {
                request.Headers.TryAddWithoutValidation(key, value);
            }
        }

        string body;
        int status;
        try
        {
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            status = (int)response.StatusCode;
            body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException)
        {
            // 网络失败一个字节都不动库里那条记录,而且可重试:离线时删掉 refresh token 等于因为一次断网强迫用户重新登录。
            throw new CredentialAuthException(AgentError.Create(AgentErrorCode.Network, $"Refreshing the sign-in credential failed: {e.Message}", true));
        }

        if (status >= 500)
        {
            // 授权服务器抽风时把用户登出,是拿别人的故障惩罚自己的用户。
            throw new CredentialAuthException(AgentError.Create(AgentErrorCode.Network, $"The authorization server is temporarily unavailable (HTTP {status}).", true));
        }

        if (status is < 200 or >= 300 || !Jsn.TryParse(body, out var json))
        {
            throw await MarkReauthAsync(credentialRef, credential, $"The sign-in has expired (HTTP {status}); please sign in again.");
        }

        return spec.Identity(json, NowMs);
    }

    /// <summary>任何非 <see cref="CredentialAuthException"/> 的异常一律按网络故障处理,不动库:钩子里通常要自己发请求,抛出来的多半是 fetch 的错。</summary>
    private async Task<OAuthIdentity?> CustomRefreshAsync(OAuthRefreshHook hook, OAuthCredential credential, CancellationToken ct)
    {
        try
        {
            return await hook(credential, new OAuthRefreshContext(http, NowMs, ct)).ConfigureAwait(false);
        }
        catch (CredentialAuthException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new CredentialAuthException(AgentError.Create(AgentErrorCode.Network, $"Refreshing the sign-in credential failed: {e.Message}", true));
        }
    }

    /// <summary>上游明确拒了。保留 refreshToken 不删(万一是上游抖动,用户手动重新登录会覆盖它),只打一个 NeedsReauth 标让界面说人话。</summary>
    private async Task<CredentialAuthException> MarkReauthAsync(string credentialRef, OAuthCredential credential, string message)
    {
        try
        {
            await store.SetAsync(credentialRef, CredentialFormat.Serialize(credential with { NeedsReauth = true }), CancellationToken.None).ConfigureAwait(false);
            onChanged?.Invoke(credentialRef);
        }
        catch (Exception)
        {
            // 主密钥文件不可写时记不进去,那也不该盖掉下面这个更要紧的错误。
        }

        return new CredentialAuthException(AgentError.Create(AgentErrorCode.Auth, message, false));
    }
}
