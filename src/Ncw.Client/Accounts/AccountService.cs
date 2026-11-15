using System.Collections.Immutable;
using Ncw.Client.Devices;
using Ncw.Client.Services;
using Ncw.Client.Stores;

namespace Ncw.Client.Accounts;

/// <summary>账户操作的结果:成功,或带稳定 key 的失败(<c>account.*</c>,界面翻译)。</summary>
public readonly record struct AccountResult(bool Ok, string? ErrorKey = null)
{
    public static readonly AccountResult Success = new(true);

    public static AccountResult Fail(string key) => new(false, key);
}

public readonly record struct AccountResult<T>(bool Ok, T? Value = default, string? ErrorKey = null);

/// <summary>跳转链接的处理结果。</summary>
public enum LinkOutcome
{
    /// <summary>不是这里处理的链接,或当前没有对应的进行中的操作(陌生的回调不改任何状态)。</summary>
    Ignored,

    /// <summary>被拒绝(state 对不上、已过期):不改变进行中的登录。</summary>
    Rejected,

    /// <summary>被接受:登录继续(换令牌等)。</summary>
    Accepted,
}

/// <summary>账户失败的稳定 key(界面翻译;Client 不产出界面文字)。</summary>
public static class AccountKeys
{
    public const string BrowserFailed = "account.login.browserFailed";
    public const string Expired = "account.login.expired";
    public const string Denied = "account.login.denied";
    public const string Failed = "account.login.failed";
    public const string ExchangeFailed = "account.login.exchangeFailed";
    public const string Network = "account.login.network";
    public const string NoTeam = "account.login.noTeam";
    public const string DeviceCodeUnavailable = "account.login.deviceCodeUnavailable";
    public const string NotSignedIn = "account.notSignedIn";
    public const string SessionExpired = "account.sessionExpired";
    public const string Unreachable = "account.unreachable";
    public const string TeamInvalid = "account.team.invalid";
    public const string Rejected = "account.rejected";
}

/// <summary>账户服务的可调参数(测试里调小)。</summary>
public sealed record AccountOptions
{
    /// <summary>一次登录的有效期(07 §2 第 1 步:state 一次性,10 分钟过期)。</summary>
    public TimeSpan LoginTtl { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>访问令牌离过期不到这么久就刷新(2.x:2 分钟)。</summary>
    public TimeSpan RefreshMargin { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>后台检查刷新的间隔(失败时也按它重试)。</summary>
    public TimeSpan RefreshTick { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>设备授权轮询连续网络失败多少次就放弃。</summary>
    public int MaxPollNetworkFailures { get; init; } = 3;
}

/// <summary>
/// 控制端的账户服务(07 §2):登录(浏览器 + PKCE + 本机回环回调 / 设备授权码备用)、选团队、令牌刷新与失效、离线使用、退出登录、把委托令牌交给本机 Agent。
/// <list type="bullet">
/// <item><b>真源</b>:账户的展示状态在 <see cref="ClientStores.Account"/>(唯一一份);令牌只在本类内部(访问令牌)和系统钥匙串(刷新令牌),<b>永远不进 Store、不进日志、不进异常消息</b>。</item>
/// <item><b>单调</b>:每次登录完成 / 退出 / 失效,代数加一;所有异步结果带着发起时的代数回来,对不上就丢弃——退出登录之后迟到的刷新结果复活不了会话(规则 6)。</item>
/// <item><b>state / PKCE</b>:每次登录一份一次性的 state 和 verifier,回跳时常数时间比较;陌生的、过期的、重复的回调都不能改变状态。</item>
/// <item><b>不降级明文</b>:钥匙串不可用(Linux 的 libsecret 未接入)时本次登录只在内存里,并给出 <see cref="AccountState.SecureStorageUnavailable"/> 提示,绝不写明文文件。</item>
/// </list>
/// 同一个用户意图只有一个方法(规则 4):界面、首次启动、设置页都调这里。
/// </summary>
public sealed class AccountService : IAsyncDisposable
{
    private sealed class Attempt
    {
        public required long Id { get; init; }

        public required LoginMethod Method { get; init; }

        public required DateTimeOffset StartedAt { get; init; }

        public required DateTimeOffset ExpiresAt { get; init; }

        public string? Verifier { get; init; }

        public string? State { get; init; }

        public string? DeviceCode { get; init; }

        public CancellationTokenSource Cts { get; } = new();

        public ITimer? Expiry { get; set; }

        /// <summary>浏览器登录的本机回环监听(<see cref="ILoginCallbackListener"/>);登录结束就关。</summary>
        public ILoginCallbackSession? Listener { get; set; }

        /// <summary>授权请求里用的 <c>redirect_uri</c>;换码请求必须逐字相同。</summary>
        public string? RedirectUri { get; set; }

        /// <summary>释放这次登录占用的计时器和本机监听(幂等)。</summary>
        public void Release()
        {
            Expiry?.Dispose();
            Expiry = null;
            Listener?.Dispose();
        }

        /// <summary>回调已被接受过一次:同一个授权码的重放(同一个 state)不能再触发第二次换码。</summary>
        public bool Consumed { get; set; }
    }

    private readonly ClientStores _stores;
    private readonly IAccountApi _api;
    private readonly IRefreshTokenVault _vault;
    private readonly IAccountMetaStore _meta;
    private readonly Func<IUrlLauncher> _launcher;
    private readonly IDelegationTarget _delegation;
    private readonly IUiDispatcher _ui;
    private readonly ILoginCallbackListener _listener;
    private readonly TimeProvider _clock;
    private readonly AccountOptions _options;
    private readonly object _gate = new();

    private TokenSet? _tokens;               // 访问令牌 + 刷新令牌(内存;刷新令牌的持久副本在钥匙串)
    private DelegatedGrant? _delegated;      // 最近一次为本机 Agent 换到的委托令牌
    private string? _delegatedFrom;          // 它是用哪个访问令牌换的(访问令牌变了就重换)
    private Attempt? _attempt;
    private long _attemptCounter;
    private long _generation;
    private Task? _refreshFlight;
    private Task _loginTask = Task.CompletedTask;
    private ITimer? _refreshTimer;
    private int _disposed;

    public AccountService(
        ClientStores stores, IAccountApi api, IRefreshTokenVault vault, IAccountMetaStore meta, Func<IUrlLauncher> launcher,
        IDelegationTarget? delegation = null, IUiDispatcher? ui = null, TimeProvider? clock = null, AccountOptions? options = null,
        ILoginCallbackListener? listener = null)
    {
        _stores = stores;
        _api = api;
        _vault = vault;
        _meta = meta;
        _launcher = launcher;
        _delegation = delegation ?? NoDelegationTarget.Instance;
        _ui = ui ?? InlineDispatcher.Instance;
        _clock = clock ?? TimeProvider.System;
        _options = options ?? new AccountOptions();
        _listener = listener ?? new LoopbackLoginCallbackListener();
        stores.Workspace.Changed += OnWorkspaceChanged;
    }

    /// <summary>当前是否登录着(同步读,服务内部的真相;界面读 Store)。</summary>
    public bool HasSession
    {
        get
        {
            lock (_gate)
            {
                return _tokens is not null;
            }
        }
    }

    /// <summary>进行中的登录(设备授权码的轮询)结束时完成的任务;测试用来等终态。</summary>
    public Task LoginTask
    {
        get
        {
            lock (_gate)
            {
                return _loginTask;
            }
        }
    }

    // ───────────────────────── 启动 ─────────────────────────

    /// <summary>读本地的账户记录,决定首次启动还是恢复会话。已登录的会话在后台刷新访问令牌(<see cref="RefreshAsync"/> 可以显式等)。</summary>
    public async Task InitializeAsync()
    {
        var meta = _meta.Load();
        if (meta is null)
        {
            Publish(s => s with { Mode = AccountMode.Undecided });
            return;
        }

        if (meta.Mode != "authenticated")
        {
            Publish(s => s with { Mode = AccountMode.Offline });
            return;
        }

        var session = new AccountSession(meta.User, [.. meta.Teams], meta.SelectedTeamId, meta.ContextRequired);
        string? refresh = null;
        var insecure = false;
        try
        {
            refresh = _vault.Load();
        }
        catch (PlatformNotSupportedException)
        {
            insecure = true;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // 钥匙串读不出来(换了机器、被用户删了):当作没有
        }

        if (string.IsNullOrEmpty(refresh))
        {
            long generation;
            lock (_gate)
            {
                generation = ++_generation;
            }

            Publish(s => s with { Mode = AccountMode.Authenticated, Session = session, Health = SessionHealth.Expired, SecureStorageUnavailable = insecure, Generation = generation });
            return;
        }

        long restored;
        lock (_gate)
        {
            // 访问令牌留空、过期时间设成过去:第一次用就会刷新
            _tokens = new TokenSet("", refresh, DateTimeOffset.MinValue, meta.User);
            restored = ++_generation;
        }

        Publish(s => s with { Mode = AccountMode.Authenticated, Session = session, Health = SessionHealth.Refreshing, Generation = restored });
        StartRefreshTimer();
        await RefreshAsync().ConfigureAwait(false);
    }

    // ───────────────────────── 登录:浏览器 ─────────────────────────

    /// <summary>
    /// 发起浏览器登录(PKCE):生成一次性的 verifier / challenge / state,在本机回环地址上起一个临时监听(后端只认 <c>http://127.0.0.1:{端口}/callback</c>),
    /// 再打开系统浏览器;授权完成后浏览器跳回这个监听,state 对上的回调进入 <see cref="HandleLinkAsync"/>。
    /// 再次调用 = 顶掉上一次。返回 false = 监听起不来或浏览器没能打开(<see cref="AccountState.LoginError"/> 已设,界面建议改用登录码)。
    /// </summary>
    public async Task<bool> BeginBrowserLoginAsync()
    {
        var pkce = Pkce.Create();
        var state = Pkce.NewState();
        var attempt = NewAttempt(LoginMethod.Browser, pkce.Verifier, state, null);
        try
        {
            var session = _listener.Start(state, HandleCallbackAsync);
            bool current;
            lock (_gate)
            {
                current = ReferenceEquals(_attempt, attempt);
                if (current)
                {
                    attempt.Listener = session;
                    attempt.RedirectUri = session.RedirectUri;
                }
            }

            if (!current)
            {
                session.Dispose(); // 启动期间已被新的登录顶掉
                return false;
            }

            var uri = _api.BuildAuthorizeUri(state, pkce.Challenge, session.RedirectUri);
            await _launcher().OpenAsync(uri.AbsoluteUri).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            FailAttempt(attempt.Id, AccountKeys.BrowserFailed);
            return false;
        }

        return true;
    }

    private Task HandleCallbackAsync(AuthCallbackLink link) => HandleLinkAsync(link);

    /// <summary>收到系统转来的 <c>nextcowork://</c> 链接。只处理登录回调;其他路由由 <see cref="LinkRouter"/> 分发。</summary>
    public async Task<LinkOutcome> HandleLinkAsync(NcwLink link)
    {
        if (link is not AuthCallbackLink callback)
        {
            return LinkOutcome.Ignored;
        }

        Attempt attempt;
        lock (_gate)
        {
            if (_attempt is not { Method: LoginMethod.Browser, RedirectUri: not null } current)
            {
                return LinkOutcome.Ignored; // 没有进行中的浏览器登录(或本机监听还没就绪):陌生的回调什么都不改
            }

            attempt = current;
        }

        if (_clock.GetUtcNow() >= attempt.ExpiresAt)
        {
            FailAttempt(attempt.Id, AccountKeys.Expired);
            return LinkOutcome.Rejected;
        }

        if (!Pkce.StateEquals(attempt.State, callback.State))
        {
            return LinkOutcome.Rejected; // 别人构造的 / 旧的回调:不影响真正在等的登录
        }

        // 校验过 state:这一次回调是这个登录的;之后它不能再被重放
        lock (_gate)
        {
            if (!ReferenceEquals(_attempt, attempt) || attempt.Consumed)
            {
                return LinkOutcome.Ignored;
            }

            attempt.Consumed = true;
        }

        if (callback.Error is { } error)
        {
            FailAttempt(attempt.Id, error == "access_denied" ? AccountKeys.Denied : AccountKeys.Failed);
            return LinkOutcome.Accepted;
        }

        var code = callback.Code!;
        var verifier = attempt.Verifier!;
        var redirectUri = attempt.RedirectUri!;
        lock (_gate)
        {
            attempt.Release(); // 回调已收到:计时器和本机监听都不再需要
        }

        Publish(s => s with { Login = new LoginProgress(LoginMethod.Browser, LoginPhase.Exchanging, attempt.StartedAt, attempt.ExpiresAt) });
        try
        {
            var tokens = await _api.ExchangeCodeAsync(code, verifier, redirectUri, attempt.Cts.Token).ConfigureAwait(false);
            await CompleteSignInAsync(tokens, attempt).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 被取消 / 顶掉:状态已经由取消的一方设好
        }
        catch (AccountApiException e)
        {
            FailAttempt(attempt.Id, e.Kind == AccountApiErrorKind.Network ? AccountKeys.Network : AccountKeys.ExchangeFailed);
        }

        return LinkOutcome.Accepted;
    }

    // ───────────────────────── 登录:设备授权码(备用) ─────────────────────────

    /// <summary>
    /// 备用方式(RFC 8628):取一个短码给用户看(<see cref="LoginProgress.UserCode"/> / <see cref="LoginProgress.VerificationUri"/>),同时尽力打开验证页,
    /// 然后在后台按间隔轮询,直到成功 / 拒绝 / 过期 / 取消(<see cref="LoginTask"/>)。适用于 Linux AppImage 没注册 scheme、企业浏览器拦截跳转。
    /// </summary>
    public async Task<bool> BeginDeviceCodeLoginAsync()
    {
        var attempt = NewAttempt(LoginMethod.DeviceCode, null, null, null);
        DeviceAuthorization auth;
        try
        {
            auth = await _api.StartDeviceAuthorizationAsync(attempt.Cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (AccountApiException e)
        {
            FailAttempt(attempt.Id, e switch
            {
                { Kind: AccountApiErrorKind.Network } => AccountKeys.Network,
                { Kind: AccountApiErrorKind.Unsupported } => AccountKeys.DeviceCodeUnavailable,
                // 还没部署设备码的老后端对 nextcowork-desktop 回 invalid_client / unauthorized_client:同样是「暂时不可用」
                { OAuthError: "invalid_client" or "unauthorized_client" } => AccountKeys.DeviceCodeUnavailable,
                _ => AccountKeys.Failed,
            });
            return false;
        }

        var expires = _clock.GetUtcNow() + auth.ExpiresIn;
        lock (_gate)
        {
            if (_attempt?.Id != attempt.Id)
            {
                return false; // 已被新的登录顶掉
            }

            // 设备码有自己的有效期(轮询循环按它判断过期),不再用浏览器登录的 10 分钟计时器
            attempt.Release();
        }

        var shown = new LoginProgress(LoginMethod.DeviceCode, LoginPhase.Waiting, attempt.StartedAt, expires, auth.UserCode, auth.VerificationUri);
        Publish(s => s.Login is { } l && l.StartedAt == attempt.StartedAt ? s with { Login = shown } : s);
        try
        {
            await _launcher().OpenAsync(auth.VerificationUriComplete ?? auth.VerificationUri).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // 打不开浏览器没关系:短码和地址在界面上,用户可以手动输入
        }

        lock (_gate)
        {
            if (_attempt?.Id == attempt.Id)
            {
                _loginTask = PollAsync(attempt, auth, expires);
            }
        }

        return true;
    }

    private async Task PollAsync(Attempt attempt, DeviceAuthorization auth, DateTimeOffset expires)
    {
        var interval = auth.Interval;
        var failures = 0;
        try
        {
            for (;;)
            {
                await Task.Delay(interval, _clock, attempt.Cts.Token).ConfigureAwait(false);
                if (_clock.GetUtcNow() >= expires)
                {
                    FailAttempt(attempt.Id, AccountKeys.Expired);
                    return;
                }

                DevicePollResult result;
                try
                {
                    result = await _api.PollDeviceAuthorizationAsync(auth.DeviceCode, attempt.Cts.Token).ConfigureAwait(false);
                }
                catch (AccountApiException e) when (e.Kind is AccountApiErrorKind.Network or AccountApiErrorKind.Server)
                {
                    if (++failures >= _options.MaxPollNetworkFailures)
                    {
                        FailAttempt(attempt.Id, AccountKeys.Network);
                        return;
                    }

                    continue;
                }
                catch (AccountApiException)
                {
                    FailAttempt(attempt.Id, AccountKeys.Failed);
                    return;
                }

                failures = 0;
                switch (result.Kind)
                {
                    case DevicePollKind.Pending:
                        break;
                    case DevicePollKind.SlowDown:
                        interval += TimeSpan.FromSeconds(5);
                        break;
                    case DevicePollKind.Denied:
                        FailAttempt(attempt.Id, AccountKeys.Denied);
                        return;
                    case DevicePollKind.Expired:
                        FailAttempt(attempt.Id, AccountKeys.Expired);
                        return;
                    default:
                        Publish(s => s.Login is not null ? s with { Login = s.Login with { Phase = LoginPhase.Exchanging } } : s);
                        try
                        {
                            await CompleteSignInAsync(result.Tokens!, attempt).ConfigureAwait(false);
                        }
                        catch (AccountApiException e)
                        {
                            FailAttempt(attempt.Id, e.Kind == AccountApiErrorKind.Network ? AccountKeys.Network : AccountKeys.ExchangeFailed);
                        }

                        return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 取消 / 被新的登录顶掉
        }
    }

    // ───────────────────────── 登录:共同的收尾 ─────────────────────────

    /// <summary>读团队、(必要时)选团队、保存令牌、发布状态、交付委托令牌。attempt 已经不是当前的就整个丢弃(不写任何东西)。</summary>
    private async Task CompleteSignInAsync(TokenSet tokens, Attempt attempt)
    {
        try
        {
            await CompleteSignInCoreAsync(tokens, attempt).ConfigureAwait(false);
        }
        catch (Exception e) when (e is AccountApiException or OperationCanceledException)
        {
            // 令牌已经换到手但没能完成登录(读团队 / 选团队失败、被取消):不留下服务端那份刷新令牌
            await BestEffortRevokeAsync(tokens.RefreshToken).ConfigureAwait(false);
            throw;
        }
    }

    private async Task CompleteSignInCoreAsync(TokenSet tokens, Attempt attempt)
    {
        var teams = await _api.GetTeamsAsync(tokens.AccessToken, attempt.Cts.Token).ConfigureAwait(false);
        if (!IsCurrent(attempt))
        {
            return;
        }

        if (teams.Teams.Count == 0)
        {
            await BestEffortRevokeAsync(tokens.RefreshToken).ConfigureAwait(false);
            FailAttempt(attempt.Id, AccountKeys.NoTeam);
            return;
        }

        var selected = teams.SelectedTeamId ?? (teams.Teams.Count == 1 ? teams.Teams[0].Id : null);
        var user = tokens.User;
        if (selected is not null)
        {
            await _api.SelectTeamAsync(tokens.AccessToken, selected, attempt.Cts.Token).ConfigureAwait(false);
            user = await _api.GetUserAsync(tokens.AccessToken, attempt.Cts.Token).ConfigureAwait(false);
        }

        long generation;
        lock (_gate)
        {
            if (!ReferenceEquals(_attempt, attempt))
            {
                return; // 取消 / 顶掉:不写任何东西
            }

            attempt.Release();
            _attempt = null;
            _tokens = tokens;
            _delegated = null;
            _delegatedFrom = null;
            generation = ++_generation;
        }

        var insecure = !TrySaveRefresh(tokens.RefreshToken);
        var session = new AccountSession(user, [.. teams.Teams.Select(t => t with { IsSelected = t.Id == selected })], selected, selected is null);
        SaveMeta(session);
        Publish(s => s with
        {
            Mode = AccountMode.Authenticated,
            Session = session,
            Login = null,
            LoginError = null,
            Health = SessionHealth.Ok,
            SecureStorageUnavailable = insecure,
            Delegations = ImmutableDictionary<string, DeviceDelegation>.Empty,
            Generation = generation,
        });
        StartRefreshTimer();
        await PushDelegationsAsync(force: true).ConfigureAwait(false);
    }

    private Attempt NewAttempt(LoginMethod method, string? verifier, string? state, string? deviceCode)
    {
        var now = _clock.GetUtcNow();
        Attempt? previous;
        Attempt attempt;
        lock (_gate)
        {
            previous = _attempt;
            attempt = new Attempt
            {
                Id = ++_attemptCounter,
                Method = method,
                StartedAt = now,
                ExpiresAt = now + _options.LoginTtl,
                Verifier = verifier,
                State = state,
                DeviceCode = deviceCode,
            };
            _attempt = attempt;
        }

        if (previous is not null)
        {
            previous.Release();
            previous.Cts.Cancel(); // 顶掉上一次
        }

        attempt.Expiry = _clock.CreateTimer(_ => FailAttempt(attempt.Id, AccountKeys.Expired), null, _options.LoginTtl, Timeout.InfiniteTimeSpan);
        Publish(s => s with { Login = new LoginProgress(method, LoginPhase.Waiting, attempt.StartedAt, attempt.ExpiresAt), LoginError = null });
        return attempt;
    }

    private bool IsCurrent(Attempt attempt)
    {
        lock (_gate)
        {
            return ReferenceEquals(_attempt, attempt);
        }
    }

    /// <summary>登录失败:只对「当前」的那次生效(被取消 / 顶掉的迟到失败不改状态)。</summary>
    private void FailAttempt(long attemptId, string key)
    {
        Attempt? attempt;
        lock (_gate)
        {
            if (_attempt is not { } current || current.Id != attemptId)
            {
                return;
            }

            attempt = current;
            _attempt = null;
        }

        attempt.Release();
        attempt.Cts.Cancel();
        Publish(s => s with { Login = null, LoginError = key });
    }

    /// <summary>取消进行中的登录(用户点「取消」)。已经完成的登录不受影响。</summary>
    public void CancelLogin()
    {
        Attempt? attempt;
        lock (_gate)
        {
            attempt = _attempt;
            _attempt = null;
        }

        if (attempt is null)
        {
            Publish(s => s with { LoginError = null });
            return;
        }

        attempt.Release();
        attempt.Cts.Cancel();
        Publish(s => s with { Login = null, LoginError = null });
    }

    /// <summary>关掉登录失败的提示。</summary>
    public void DismissLoginError() => Publish(s => s.LoginError is null ? s : s with { LoginError = null });

    // ───────────────────────── 团队 / 离线 / 退出 ─────────────────────────

    /// <summary>选团队(登录时账户有多个团队,或用户之后切换)。成功后重新交付委托令牌(带新的团队)。</summary>
    public async Task<AccountResult> SelectTeamAsync(string teamId)
    {
        if (string.IsNullOrWhiteSpace(teamId))
        {
            return AccountResult.Fail(AccountKeys.TeamInvalid);
        }

        var generation = Generation();
        var session = _stores.Account.State.Session;
        if (session is null || session.Teams.All(t => t.Id != teamId))
        {
            return AccountResult.Fail(AccountKeys.TeamInvalid);
        }

        var result = await WithAccessAsync(async token =>
        {
            await _api.SelectTeamAsync(token, teamId, CancellationToken.None).ConfigureAwait(false);
            var teams = await _api.GetTeamsAsync(token, CancellationToken.None).ConfigureAwait(false);
            var user = await _api.GetUserAsync(token, CancellationToken.None).ConfigureAwait(false);
            return (teams, user);
        }).ConfigureAwait(false);
        if (!result.Ok)
        {
            return AccountResult.Fail(result.ErrorKey ?? AccountKeys.Rejected);
        }

        if (Generation() != generation)
        {
            return AccountResult.Fail(AccountKeys.NotSignedIn); // 期间退出了登录
        }

        var (context, refreshedUser) = result.Value;
        var next = new AccountSession(refreshedUser, [.. context.Teams.Select(t => t with { IsSelected = t.Id == teamId })], teamId, false);
        SaveMeta(next);
        Publish(s => s with { Session = next });
        await PushDelegationsAsync(force: true).ConfigureAwait(false);
        return AccountResult.Success;
    }

    /// <summary>「使用自己的模型供应商」/ 跳过:不登录,以后可以随时在设置里登录。已登录时等同退出登录。</summary>
    public async Task UseOfflineAsync()
    {
        if (_stores.Account.State.Mode == AccountMode.Authenticated)
        {
            await SignOutAsync().ConfigureAwait(false);
            return;
        }

        CancelLogin();
        try
        {
            _meta.Save(new AccountMeta(1, "offline", null, [], null, false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 存不下:这一次照样离线,下次启动还会问
        }

        Publish(s => s with { Mode = AccountMode.Offline, LoginError = null });
    }

    /// <summary>
    /// 退出登录:界面立刻回到离线,本地令牌和钥匙串里的刷新令牌清掉,本机 Agent 的委托令牌撤销,服务端的刷新令牌尽力撤销(失败不拦着)。
    /// 之后迟到的刷新 / 换码结果因为代数对不上而被丢弃。
    /// </summary>
    public async Task SignOutAsync()
    {
        string? refresh;
        Attempt? attempt;
        lock (_gate)
        {
            refresh = _tokens?.RefreshToken;
            attempt = _attempt;
            _attempt = null;
            _tokens = null;
            _delegated = null;
            _delegatedFrom = null;
            _generation++;
        }

        attempt?.Release();
        attempt?.Cts.Cancel();
        StopRefreshTimer();
        var devices = _stores.Account.State.Delegations.Keys.Concat(_delegation.ConnectedTargets()).Distinct().ToArray();
        Publish(s => s with
        {
            Mode = AccountMode.Offline,
            Session = null,
            Login = null,
            LoginError = null,
            Health = SessionHealth.Ok,
            SecureStorageUnavailable = false,
            Delegations = ImmutableDictionary<string, DeviceDelegation>.Empty,
            Generation = _generation,
        });
        try
        {
            _vault.Delete();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // 平台不支持 / 钥匙串删不掉:内存里已经清了
        }

        try
        {
            _meta.Save(new AccountMeta(1, "offline", null, [], null, false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _meta.Clear();
        }

        foreach (var device in devices)
        {
            await BestEffortDetachAsync(device).ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(refresh))
        {
            await BestEffortRevokeAsync(refresh).ConfigureAwait(false);
        }
    }

    // ───────────────────────── 令牌刷新 ─────────────────────────

    /// <summary>
    /// 刷新访问令牌(不到期不刷新;单飞)。成功 = 会话健康并重新交付委托令牌;刷新令牌被拒 = <see cref="SessionHealth.Expired"/>(清钥匙串、撤销委托、要重新登录);
    /// 连不上 = <see cref="SessionHealth.Unreachable"/>(会话保留,下个节拍重试)。
    /// </summary>
    public Task RefreshAsync(bool force = false)
    {
        TaskCompletionSource flight;
        lock (_gate)
        {
            if (_tokens is null)
            {
                return Task.CompletedTask;
            }

            if (_refreshFlight is { } running)
            {
                return running;
            }

            flight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _refreshFlight = flight.Task;
        }

        _ = RunRefreshAsync(force, flight);
        return flight.Task;
    }

    private async Task RunRefreshAsync(bool force, TaskCompletionSource flight)
    {
        try
        {
            await RefreshCoreAsync(force).ConfigureAwait(false);
            flight.TrySetResult();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            flight.TrySetException(e);
        }
        finally
        {
            lock (_gate)
            {
                _refreshFlight = null;
            }
        }
    }

    private async Task RefreshCoreAsync(bool force)
    {
        string refresh;
        long generation;
        lock (_gate)
        {
            if (_tokens is not { } held || (!force && held.AccessToken.Length > 0 && held.ExpiresAt - _clock.GetUtcNow() > _options.RefreshMargin))
            {
                return;
            }

            refresh = held.RefreshToken;
            generation = _generation;
        }

        Publish(s => s.Health == SessionHealth.Ok ? s with { Health = SessionHealth.Refreshing } : s);
        TokenSet fresh;
        try
        {
            fresh = await _api.RefreshAsync(refresh, CancellationToken.None).ConfigureAwait(false);
        }
        catch (AccountApiException e) when (e.Kind is AccountApiErrorKind.InvalidGrant or AccountApiErrorKind.Unauthorized)
        {
            await ExpireSessionAsync(generation).ConfigureAwait(false);
            return;
        }
        catch (AccountApiException)
        {
            lock (_gate)
            {
                if (_generation != generation)
                {
                    return;
                }
            }

            Publish(s => s.Generation == generation ? s with { Health = SessionHealth.Unreachable } : s);
            return;
        }

        lock (_gate)
        {
            if (_generation != generation || _tokens is null)
            {
                return; // 期间退出 / 重新登录 / 失效了:迟到的结果不复活旧会话
            }

            _tokens = fresh with { User = _tokens.User };
            _delegated = null;
            _delegatedFrom = null;
        }

        var insecure = !TrySaveRefresh(fresh.RefreshToken); // 刷新令牌会轮换:新的存回钥匙串
        Publish(s => s.Generation == generation ? s with { Health = SessionHealth.Ok, SecureStorageUnavailable = insecure } : s);
        await PushDelegationsAsync(force: true).ConfigureAwait(false);
    }

    private async Task ExpireSessionAsync(long generation)
    {
        lock (_gate)
        {
            if (_generation != generation)
            {
                return;
            }

            _tokens = null;
            _delegated = null;
            _delegatedFrom = null;
            _generation++;
        }

        StopRefreshTimer();
        try
        {
            _vault.Delete();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // 内存里已经清了
        }

        var devices = _stores.Account.State.Delegations.Keys.Concat(_delegation.ConnectedTargets()).Distinct().ToArray();
        Publish(s => s with { Health = SessionHealth.Expired, Delegations = ImmutableDictionary<string, DeviceDelegation>.Empty, Generation = _generation });
        foreach (var device in devices)
        {
            await BestEffortDetachAsync(device).ConfigureAwait(false);
        }
    }

    /// <summary>拿一个有效的访问令牌去调用 <paramref name="call"/>(先按需刷新)。没登录 / 已过期 / 连不上都给稳定 key。</summary>
    public async Task<AccountResult<T>> WithAccessAsync<T>(Func<string, Task<T>> call)
    {
        await RefreshAsync().ConfigureAwait(false);
        string? access;
        lock (_gate)
        {
            access = _tokens is { AccessToken.Length: > 0 } held && held.ExpiresAt > _clock.GetUtcNow() ? held.AccessToken : null;
        }

        if (access is null)
        {
            return new AccountResult<T>(false, default, _stores.Account.State.Health switch
            {
                SessionHealth.Expired => AccountKeys.SessionExpired,
                SessionHealth.Unreachable => AccountKeys.Unreachable,
                _ => AccountKeys.NotSignedIn,
            });
        }

        try
        {
            return new AccountResult<T>(true, await call(access).ConfigureAwait(false));
        }
        catch (AccountApiException e)
        {
            return new AccountResult<T>(false, default, e.Kind switch
            {
                AccountApiErrorKind.Network or AccountApiErrorKind.Server => AccountKeys.Unreachable,
                AccountApiErrorKind.Unauthorized => AccountKeys.SessionExpired,
                _ => AccountKeys.Rejected,
            });
        }
    }

    /// <summary>已登录的控制端(设置页「已登录的控制端」)。</summary>
    public Task<AccountResult<IReadOnlyList<ControllerSession>>> ListControllersAsync() =>
        WithAccessAsync(token => _api.ListControllersAsync(token, CancellationToken.None));

    /// <summary>撤销另一个控制端的登录。</summary>
    public async Task<AccountResult> RevokeControllerAsync(string controllerId)
    {
        var result = await WithAccessAsync(async token =>
        {
            await _api.RevokeControllerAsync(token, controllerId, CancellationToken.None).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
        return result.Ok ? AccountResult.Success : AccountResult.Fail(result.ErrorKey!);
    }

    private void StartRefreshTimer()
    {
        lock (_gate)
        {
            _refreshTimer?.Dispose();
            _refreshTimer = _clock.CreateTimer(_ => _ = RefreshAsync(), null, _options.RefreshTick, _options.RefreshTick);
        }
    }

    private void StopRefreshTimer()
    {
        lock (_gate)
        {
            _refreshTimer?.Dispose();
            _refreshTimer = null;
        }
    }

    // ───────────────────────── 委托令牌 ─────────────────────────

    /// <summary>
    /// 把委托令牌交给本机 Agent。<paramref name="force"/> = 对所有在线的本机设备重新交付(令牌刷新 / 换团队之后);否则只补还没交付的(设备刚连上)。
    /// 还没选团队、会话已失效时什么都不交付。交付失败只记状态(<see cref="DelegationStatus.Failed"/>),下次刷新 / 重连再试。
    /// </summary>
    public async Task PushDelegationsAsync(bool force)
    {
        var state = _stores.Account.State;
        var targets = _delegation.ConnectedTargets();
        var todo = force ? targets : [.. targets.Where(id => !state.Delegations.ContainsKey(id))];
        if (todo.Count == 0)
        {
            return;
        }

        long generation;
        string? access;
        TokenSet? held;
        lock (_gate)
        {
            held = _tokens;
            generation = _generation;
            access = held is { AccessToken.Length: > 0 } && held.ExpiresAt > _clock.GetUtcNow() ? held.AccessToken : null;
        }

        var session = state.Session;
        if (access is null || held is null || session is null || session.ContextRequired || state.Mode != AccountMode.Authenticated || session.User is null)
        {
            return;
        }

        foreach (var id in todo)
        {
            Publish(s => s.Generation == generation ? s with { Delegations = s.Delegations.SetItem(id, new DeviceDelegation(DelegationStatus.Pending)) } : s);
        }

        DelegatedGrant grant;
        try
        {
            grant = await ResolveGrantAsync(access, held.ExpiresAt, generation).ConfigureAwait(false);
        }
        catch (AccountApiException)
        {
            foreach (var id in todo)
            {
                Publish(s => s.Generation == generation ? s with { Delegations = s.Delegations.SetItem(id, new DeviceDelegation(DelegationStatus.Failed)) } : s);
            }

            return;
        }

        foreach (var id in todo)
        {
            try
            {
                await _delegation.AttachAsync(id, grant.Token, grant.ExpiresAt, session.User.Id, session.SelectedTeamId, CancellationToken.None).ConfigureAwait(false);
                Publish(s => s.Generation == generation ? s with { Delegations = s.Delegations.SetItem(id, new DeviceDelegation(DelegationStatus.Attached, grant.ExpiresAt)) } : s);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Publish(s => s.Generation == generation ? s with { Delegations = s.Delegations.SetItem(id, new DeviceDelegation(DelegationStatus.Failed)) } : s);
            }
        }
    }

    private async Task<DelegatedGrant> ResolveGrantAsync(string access, DateTimeOffset accessExpires, long generation)
    {
        lock (_gate)
        {
            if (_delegated is { } cached && _delegatedFrom == access && cached.ExpiresAt - _clock.GetUtcNow() > _options.RefreshMargin)
            {
                return cached;
            }
        }

        // 后端换得到专用的委托令牌就用它;后端还不支持(B11 待确认)就退回访问令牌本身:同一台电脑、同一个用户、短期
        var exchanged = await _api.ExchangeDelegatedAsync(access, CancellationToken.None).ConfigureAwait(false);
        var grant = exchanged ?? new DelegatedGrant(access, accessExpires);
        lock (_gate)
        {
            if (_generation == generation)
            {
                _delegated = grant;
                _delegatedFrom = access;
            }
        }

        return grant;
    }

    /// <summary>本机设备刚连上 / 断开:连上的补交付;断开的状态记为未知(移除,规则 7)。</summary>
    private void OnWorkspaceChanged()
    {
        var state = _stores.Account.State;
        if (state.Mode != AccountMode.Authenticated)
        {
            return;
        }

        var connected = _delegation.ConnectedTargets();
        var stale = state.Delegations.Keys.Where(id => !connected.Contains(id)).ToArray();
        if (stale.Length > 0)
        {
            Publish(s => s with { Delegations = s.Delegations.RemoveRange(stale) });
        }

        if (connected.Any(id => !state.Delegations.ContainsKey(id)))
        {
            _ = PushDelegationsAsync(force: false);
        }
    }

    private async Task BestEffortDetachAsync(string deviceId)
    {
        try
        {
            await _delegation.DetachAsync(deviceId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // 设备已断开:Agent 的令牌只在内存里且会过期,重启即清
        }
    }

    private async Task BestEffortRevokeAsync(string refresh)
    {
        try
        {
            await _api.RevokeAsync(refresh, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // 尽力而为:服务端的令牌自己会过期
        }
    }

    // ───────────────────────── 存取与发布 ─────────────────────────

    /// <summary>把刷新令牌存进钥匙串。平台不支持(或写失败)= false:本次会话只在内存里,界面提示,绝不降级为明文。</summary>
    private bool TrySaveRefresh(string refresh)
    {
        try
        {
            _vault.Save(refresh);
            return true;
        }
        catch (Exception e) when (e is PlatformNotSupportedException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    private void SaveMeta(AccountSession session)
    {
        try
        {
            _meta.Save(new AccountMeta(1, "authenticated", session.User, [.. session.Teams], session.SelectedTeamId, session.ContextRequired));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 资料存不下不影响登录;下次启动会重新读取
        }
    }

    private long Generation()
    {
        lock (_gate)
        {
            return _generation;
        }
    }

    private void Publish(Func<AccountState, AccountState> change) => _ui.Post(() => _stores.Account.Update(change));

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stores.Workspace.Changed -= OnWorkspaceChanged;
        Attempt? attempt;
        lock (_gate)
        {
            attempt = _attempt;
            _attempt = null;
        }

        attempt?.Release();
        attempt?.Cts.Cancel();
        StopRefreshTimer();
        Task poll;
        lock (_gate)
        {
            poll = _loginTask;
        }

        try
        {
            await poll.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 已取消
        }
    }
}
