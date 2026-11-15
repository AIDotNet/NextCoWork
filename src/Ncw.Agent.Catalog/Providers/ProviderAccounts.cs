using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Auth;

namespace Ncw.Agent.Catalog;

// 账号管理与 OAuth 编排(2.x ipc/provider-accounts.ts + ipc/provider-auth.ts)。

/// <summary>登录过程走到哪一步了(只在一次登录期间推;终态由 <c>StartOAuthAsync</c> 等调用本身的返回 / 异常给)。</summary>
public enum OAuthPhase
{
    /// <summary>授权页已交给控制端打开 / 配对码已展示,正在等用户。</summary>
    Waiting,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>
/// 登录进度事件。授权页由<b>控制端</b>打开(授权 URL 带着 state,经协议下发);设备码流程的配对码一并带上。
/// <see cref="NeedsPastedCode"/> 由授权方式现算:控制端据它决定 <c>Waiting</c> 阶段画输入框还是 spinner(猜错的表现是用户对着一个永远转下去的圈)。
/// </summary>
public sealed record OAuthProgress(
    string ProviderId,
    OAuthPhase Phase,
    string? AuthorizeUrl = null,
    string? UserCode = null,
    string? VerificationUrl = null,
    int? ExpiresInSeconds = null,
    bool NeedsPastedCode = false,
    string? ErrorKey = null,
    string? Detail = null);

/// <summary>
/// 登录流程里「手动粘贴回调码」的那一环:<see cref="IOAuthLogin"/> 的实现遇到 <c>manual-paste</c> 重定向时,检查传进来的 <see cref="IOAuthLoginSink"/>
/// 是不是这个接口,是就 <c>await AwaitPastedCodeAsync</c>。Llm 层的 <see cref="IOAuthLoginSink"/> 只有「打开授权页 / 显示配对码」两个方法,
/// 回调码回到 Agent 的通道(协议 <c>providers.oauth.submitCode</c>)在这里补上,不改 Llm 层。
/// </summary>
public interface IOAuthPasteSink : IOAuthLoginSink
{
    /// <summary>等控制端把回调码交回来。被取消(用户取消登录、被新的登录顶掉)时抛 <see cref="OperationCanceledException"/>。</summary>
    Task<string> AwaitPastedCodeAsync(CancellationToken ct);
}

public sealed partial class ProviderService
{
    /// <summary>登录进度(授权 URL、配对码、终态)。控制端据它打开授权页 / 显示配对码。</summary>
    public event Action<OAuthProgress>? OAuthProgressChanged;

    /// <summary>每家供应商用哪个 <see cref="IOAuthLogin"/> 登录(按 issuer 找)。登录实现随各家 OAuth 模块移植,宿主启动时注入。</summary>
    public Func<string, IOAuthLogin?> OAuthLogins { get; set; } = _ => null;

    private readonly object _loginGate = new();
    private ActiveLogin? _activeLogin;

    private sealed class ActiveLogin(string providerId, CancellationTokenSource cancel) : IOAuthPasteSink
    {
        private readonly TaskCompletionSource<string> _code = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ProviderId { get; } = providerId;

        public CancellationTokenSource Cancel { get; } = cancel;

        public Action<OAuthProgress>? Report { get; set; }

        public bool NeedsPastedCode { get; set; }

        public bool WaitsForCode => !_code.Task.IsCompleted;

        public void OpenAuthorizeUrl(string url) =>
            Report?.Invoke(new OAuthProgress(ProviderId, OAuthPhase.Waiting, AuthorizeUrl: url, NeedsPastedCode: NeedsPastedCode));

        public void ShowDeviceCode(string userCode, string verificationUrl, int expiresInSeconds) =>
            Report?.Invoke(new OAuthProgress(ProviderId, OAuthPhase.Waiting, UserCode: userCode, VerificationUrl: verificationUrl, ExpiresInSeconds: expiresInSeconds));

        public Task<string> AwaitPastedCodeAsync(CancellationToken ct) => _code.Task.WaitAsync(ct);

        public bool Submit(string code) => _code.TrySetResult(code);
    }

    private (ProviderRecord Provider, string Issuer, IOAuthLogin Login) ResolveLogin(string providerId)
    {
        var provider = Require(providerId);
        // 「这家走不走 OAuth」查的是预设,不是凭据。
        var issuer = _presets.OAuthIssuerOf(providerId)
            ?? throw new ProviderException(ProviderErrorKeys.NoOAuth, $"Provider '{provider.Name}' does not support account login.", new Dictionary<string, string> { ["provider"] = providerId });
        var login = OAuthLogins(issuer)
            ?? throw new ProviderException(ProviderErrorKeys.NoOAuth, $"No login flow is registered for issuer '{issuer}'.", new Dictionary<string, string> { ["provider"] = providerId, ["issuer"] = issuer });
        return (provider, issuer, login);
    }

    /// <summary>
    /// 走一遍 OAuth。<b>单飞</b>:同一时刻只允许一条登录在跑,再次发起时先掐掉上一条(不报「已有登录正在进行」——最常见的触发是手抖点了两下按钮)。
    /// 失败变成 <see cref="ProviderException"/>(<c>providers.loginCancelled</c> / <c>providers.loginFailed</c>),同时推一条终态进度。
    /// </summary>
    private async Task<OAuthCredential> RunLoginAsync(string providerId, IOAuthLogin login, CancellationToken ct)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var flow = new ActiveLogin(providerId, cancel)
        {
            NeedsPastedCode = login.Grant == OAuthGrantKind.AuthorizationCode,
            Report = Report,
        };
        lock (_loginGate)
        {
            _activeLogin?.Cancel.Cancel();
            _activeLogin = flow;
        }

        try
        {
            var credential = await login.LoginAsync(flow, cancel.Token).ConfigureAwait(false);
            return credential as OAuthCredential
                ?? throw new ProviderException(ProviderErrorKeys.LoginFailed, "The login did not produce an account credential.");
        }
        catch (OperationCanceledException)
        {
            Report(new OAuthProgress(providerId, OAuthPhase.Cancelled, ErrorKey: ProviderErrorKeys.LoginCancelled));
            throw new ProviderException(ProviderErrorKeys.LoginCancelled, "The login was cancelled.");
        }
        catch (CredentialAuthException e)
        {
            Report(new OAuthProgress(providerId, OAuthPhase.Failed, ErrorKey: ProviderErrorKeys.LoginFailed, Detail: e.Message));
            throw new ProviderException(ProviderErrorKeys.LoginFailed, e.Message);
        }
        catch (ProviderException e)
        {
            Report(new OAuthProgress(providerId, OAuthPhase.Failed, ErrorKey: e.Key, Detail: e.Message));
            throw;
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or IOException)
        {
            Report(new OAuthProgress(providerId, OAuthPhase.Failed, ErrorKey: ProviderErrorKeys.LoginFailed, Detail: ModelList.RedactSecrets(e.Message)));
            throw new ProviderException(ProviderErrorKeys.LoginFailed, ModelList.RedactSecrets(e.Message));
        }
        finally
        {
            lock (_loginGate)
            {
                if (ReferenceEquals(_activeLogin, flow))
                {
                    _activeLogin = null;
                }
            }
        }

        void Report(OAuthProgress progress) => OAuthProgressChanged?.Invoke(progress);
    }

    /// <summary>取消这家正在进行的登录。</summary>
    public void CancelOAuth(string providerId)
    {
        lock (_loginGate)
        {
            if (_activeLogin is { } flow && flow.ProviderId == providerId)
            {
                flow.Cancel.Cancel();
            }
        }
    }

    /// <summary>把控制端回传的回调码交给正在等的那条登录。当前没有等码的登录时抛 <c>providers.noLoginInProgress</c>。</summary>
    public void SubmitOAuthCode(string providerId, string code)
    {
        ActiveLogin? flow;
        lock (_loginGate)
        {
            flow = _activeLogin;
        }

        if (flow is null || flow.ProviderId != providerId || !flow.Submit(code))
        {
            throw new ProviderException(ProviderErrorKeys.NoLoginInProgress, "No login is waiting for an authorization code.");
        }
    }

    /// <summary>
    /// 「把这家登录成」(老路径,语义不变)。多账号之后也要建账号行:只写旧槽等于登录出一个账号列表里看不见的登录态
    /// ——设置页显示「还没有账号」,而请求确实发得出去。已有同一上游账号是覆盖,重复登录不会多出一行。
    /// </summary>
    public async Task<CredentialInfo> StartOAuthAsync(string providerId, CancellationToken ct = default)
    {
        var (provider, _, login) = ResolveLogin(providerId);
        var credential = await RunLoginAsync(providerId, login, ct).ConfigureAwait(false);
        // 落库在广播之前;密钥文件不可写时 SetAsync 抛异常(不做明文降级)。
        await _vault.SetAsync(provider.CredentialRef, CredentialFormat.Serialize(credential), CancellationToken.None).ConfigureAwait(false);
        await UpsertAccountFromLoginAsync(providerId, credential, null).ConfigureAwait(false);
        return await GetCredentialInfoAsync(providerId, ct).ConfigureAwait(false);
    }

    /// <summary>新增一个账号 / 重新登录某个账号(给了 accountId 就是「重新登录这一条」,沿用它的行与顺序)。</summary>
    public async Task<IReadOnlyList<ProviderAccountView>> AddOrReauthAccountAsync(string providerId, string? accountId = null, CancellationToken ct = default)
    {
        var (_, _, login) = ResolveLogin(providerId);
        var credential = await RunLoginAsync(providerId, login, ct).ConfigureAwait(false);
        await UpsertAccountFromLoginAsync(providerId, credential, accountId).ConfigureAwait(false);
        return await ListAccountsAsync(providerId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 把刚登录成功的凭据落到某一行账号上。<b>同一个上游账号重复登录要合并,不能新增一行</b>:判据是凭据里的上游 <c>AccountId</c>(不是我们的 ULID),
    /// 邮箱是兜底(有的家每次登录换一个 accountId)。不合并的表现是界面上出现两行同一个账号,两条额度条读数一模一样。
    /// 先写密文再写行。重新登录清掉 needsReauth;限流闸门同样不带过来(新登录的 token 属于同一个上游账号,配额状态由上游说了算)。
    /// </summary>
    private async Task UpsertAccountFromLoginAsync(string providerId, OAuthCredential credential, string? accountId)
    {
        var rows = _accounts.List(providerId);
        var target = (accountId is null ? null : rows.FirstOrDefault(r => r.Id == accountId))
            ?? await FindRowByUpstreamAccountAsync(providerId, rows, credential).ConfigureAwait(false);
        var now = NowMs;
        var id = target?.Id ?? new UlidIdGenerator(_clock).NewId();
        await _vault.SetAsync(ProviderRefs.ForAccount(providerId, id), CredentialFormat.Serialize(credential), CancellationToken.None).ConfigureAwait(false);
        _accounts.Put(new ProviderAccountRow(
            id, providerId, credential.Issuer, target?.Label,
            target?.Order ?? _accounts.NextOrder(providerId),
            target?.Enabled ?? true,
            // 第一个账号自动成为当前账号——否则旧槽镜像空着,回退旧版本就是未登录。
            target?.Current ?? rows.Count == 0,
            false, null, target?.Quota, target?.CreatedAt ?? now, now));
        await SyncAccountCredentialStateAsync(providerId, id).ConfigureAwait(false);
        AccountsChanged?.Invoke(providerId);
    }

    private async Task<ProviderAccountRow?> FindRowByUpstreamAccountAsync(string providerId, IReadOnlyList<ProviderAccountRow> rows, OAuthCredential credential)
    {
        foreach (var row in rows)
        {
            if (CredentialFormat.Parse(await _vault.GetAsync(ProviderRefs.ForAccount(providerId, row.Id), CancellationToken.None).ConfigureAwait(false)) is not OAuthCredential existing)
            {
                continue;
            }

            if (existing.AccountId == credential.AccountId || (existing.Email is not null && existing.Email == credential.Email))
            {
                return row;
            }
        }

        return null;
    }

    /// <summary>
    /// 退出登录——退出的是「当前账号」,不是这家的全部账号(界面那颗按钮长在当前账号那一行上;配了三个号时它等价于「删掉当前这一个」,剩下的顶上来)。
    /// 走 <see cref="ICredentialVault.Remove"/> 而不是写空串:删密文不需要主密钥。
    /// </summary>
    public async Task<CredentialInfo> SignOutAsync(string providerId, CancellationToken ct = default)
    {
        var provider = ResolveLoginSpec(providerId);
        CancelOAuth(providerId);
        if (_accounts.CurrentOf(providerId) is { } current)
        {
            _accounts.Remove(current.Id);
            // 剩下的账号里选一个顶上,否则这家会处在「有账号但没有当前账号」的状态,镜像槽会据此被清空。
            if (_accounts.CurrentOf(providerId) is { } next)
            {
                _accounts.SetCurrent(providerId, next.Id);
            }

            await SyncLegacyMirrorAsync(providerId, ct).ConfigureAwait(false);
            AccountsChanged?.Invoke(providerId);
            return await GetCredentialInfoAsync(providerId, ct).ConfigureAwait(false);
        }

        // 一个账号行都没有 = 这家还停在多账号之前的形态(或者本来就没登录过)。
        await _vault.RemoveAsync(provider.CredentialRef, ct).ConfigureAwait(false);
        return await GetCredentialInfoAsync(providerId, ct).ConfigureAwait(false);
    }

    private ProviderRecord ResolveLoginSpec(string providerId)
    {
        var provider = Require(providerId);
        return _presets.OAuthIssuerOf(providerId) is null
            ? throw new ProviderException(ProviderErrorKeys.NoOAuth, $"Provider '{provider.Name}' does not support account login.", new Dictionary<string, string> { ["provider"] = providerId })
            : provider;
    }

    // ── 账号列表与操作 ──

    private ProviderAccountRow RequireAccount(string providerId, string accountId)
    {
        ResolveLoginSpec(providerId);
        var row = _accounts.Get(accountId);
        return row is not null && row.ProviderId == providerId
            ? row
            : throw new ProviderException(ProviderErrorKeys.UnknownAccount, "This account does not exist; it may have been removed on another device.");
    }

    /// <summary>
    /// 这家的账号列表(账号行 + 凭据摘要)。顺带把反范式的 needsReauth 同步回账号行:真值住在密文里,而账号池在热路径上只读行。
    /// 摘要拿不到(读密文失败)时<b>不动</b>那一列——把它写成 false 等于用一次读取失败抹掉一条真实的「登录已失效」。
    /// </summary>
    public async Task<IReadOnlyList<ProviderAccountView>> ListAccountsAsync(string providerId, CancellationToken ct = default)
    {
        ResolveLoginSpec(providerId);
        var views = new List<ProviderAccountView>();
        foreach (var row in _accounts.List(providerId))
        {
            CredentialAuthInfo? auth = null;
            try
            {
                auth = CredentialFormat.Parse(await _vault.GetAsync(ProviderRefs.ForAccount(providerId, row.Id), ct).ConfigureAwait(false)) is OAuthCredential oauth
                    ? CredentialInfos.AuthOf(oauth, NowMs)
                    : null;
            }
            catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or IOException)
            {
                auth = null;
            }

            var current = row;
            if (auth is not null && auth.NeedsReauth != row.NeedsReauth)
            {
                _accounts.SetNeedsReauth(row.Id, auth.NeedsReauth);
                current = row with { NeedsReauth = auth.NeedsReauth };
            }

            views.Add(new ProviderAccountView(current, auth));
        }

        return views;
    }

    public async Task<IReadOnlyList<ProviderAccountView>> RemoveAccountAsync(string providerId, string accountId, CancellationToken ct = default)
    {
        var row = RequireAccount(providerId, accountId);
        _accounts.Remove(row.Id);
        // 删掉的是当前账号时,镜像槽要跟着换人(不换的话旧槽里留着一条已经没有账号行指向它的凭据)。
        if (_accounts.CurrentOf(providerId) is { Current: false } next)
        {
            _accounts.SetCurrent(providerId, next.Id);
        }

        await SyncLegacyMirrorAsync(providerId, ct).ConfigureAwait(false);
        return await AnnounceAccountsAsync(providerId, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ProviderAccountView>> SetAccountEnabledAsync(string providerId, string accountId, bool enabled, CancellationToken ct = default)
    {
        _accounts.SetEnabled(RequireAccount(providerId, accountId).Id, enabled);
        return await AnnounceAccountsAsync(providerId, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ProviderAccountView>> SetAccountLabelAsync(string providerId, string accountId, string label, CancellationToken ct = default)
    {
        _accounts.SetLabel(RequireAccount(providerId, accountId).Id, label);
        return await AnnounceAccountsAsync(providerId, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ProviderAccountView>> SetCurrentAccountAsync(string providerId, string accountId, CancellationToken ct = default)
    {
        _accounts.SetCurrent(providerId, RequireAccount(providerId, accountId).Id);
        await SyncLegacyMirrorAsync(providerId, ct).ConfigureAwait(false);
        return await AnnounceAccountsAsync(providerId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 拖拽排序。入参里没出现的账号<b>排在后面</b>,而不是被删掉或者顺序归零:两个控制端同时开着设置页时,另一个刚加的账号不在这一份 accountIds 里,
    /// 按「没出现就归零」处理会把它悄悄插到队首。
    /// </summary>
    public async Task<IReadOnlyList<ProviderAccountView>> ReorderAccountsAsync(string providerId, IReadOnlyList<string> accountIds, CancellationToken ct = default)
    {
        ResolveLoginSpec(providerId);
        var known = _accounts.List(providerId).Select(r => r.Id).ToList();
        var ordered = accountIds.Where(known.Contains).Distinct().ToList();
        _accounts.SetOrder(providerId, [.. ordered, .. known.Where(id => !ordered.Contains(id))]);
        return await AnnounceAccountsAsync(providerId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 「立即解除限流」:用户点它的场景有两个(刚升级了套餐 / 我们判早了),只有他知道,所以不做「确认一下是不是真的恢复了」的校验
    /// (那需要发一次请求,而那次请求本身可能又撞一个 429 并把闸门重新立起来)。
    /// </summary>
    public async Task<IReadOnlyList<ProviderAccountView>> ClearAccountLimitAsync(string providerId, string accountId, CancellationToken ct = default)
    {
        _accounts.SetLimit(RequireAccount(providerId, accountId).Id, null);
        return await AnnounceAccountsAsync(providerId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 用户「手动停用」落闸(<see cref="AccountLimitSource.Manual"/>):不会被下一次成功请求清掉,也不被 429 续期,只有用户能解除。
    /// </summary>
    public async Task<IReadOnlyList<ProviderAccountView>> PauseAccountUntilAsync(string providerId, string accountId, DateTimeOffset until, CancellationToken ct = default)
    {
        var row = RequireAccount(providerId, accountId);
        _accounts.SetLimit(row.Id, new AccountLimit(until.ToUnixTimeMilliseconds(), NowMs, AccountLimitSource.Manual, ""));
        return await AnnounceAccountsAsync(providerId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 额度读取的接口留给各家的额度模块(2.x 的 Codex 搭便车头、GLM Coding Plan 主动拉取):它们算出
    /// <see cref="QuotaSnapshot"/> 后经这里落库,账号池会据此在跑满时提前落闸。本层不内置任何一家的解析。
    /// </summary>
    public async Task<IReadOnlyList<ProviderAccountView>> ReportAccountQuotaAsync(string providerId, string accountId, QuotaSnapshot quota, CancellationToken ct = default)
    {
        var row = RequireAccount(providerId, accountId);
        AccountPool.ReportQuota(row.ToAccount(), quota);
        return await AnnounceAccountsAsync(providerId, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ProviderAccountView>> AnnounceAccountsAsync(string providerId, CancellationToken ct)
    {
        var views = await ListAccountsAsync(providerId, ct).ConfigureAwait(false);
        AccountsChanged?.Invoke(providerId);
        return views;
    }

    /// <summary>
    /// 把<b>当前账号</b>的凭据镜像回旧槽 <c>provider:&lt;id&gt;</c>——「回退旧版本仍然能用」那条承诺的全部实现。
    /// 旧版本(以及不接账号池的辅助请求)只认旧槽;不同步的表现分两种:降级之后显示未登录,或槽里留着上一个账号的 token。
    /// <b>全仓库只有这个函数写旧槽</b>(登录 / 退出 / 删号 / 切换当前账号四条路径都调它)。一个账号都没有时清空旧槽。
    /// </summary>
    internal async Task SyncLegacyMirrorAsync(string providerId, CancellationToken ct)
    {
        if (Provider(providerId) is not { } provider)
        {
            return;
        }

        if (_accounts.CurrentOf(providerId) is not { } current)
        {
            await _vault.RemoveAsync(provider.CredentialRef, ct).ConfigureAwait(false);
            return;
        }

        var raw = await _vault.GetAsync(ProviderRefs.ForAccount(providerId, current.Id), ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(raw))
        {
            return;
        }

        // 已经一模一样就不写:这条路径在每次 token 刷新后都会被调到。
        if (await _vault.GetAsync(provider.CredentialRef, ct).ConfigureAwait(false) == raw)
        {
            return;
        }

        await _vault.SetAsync(provider.CredentialRef, raw, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 凭据变了(登录成功、刷新成功、刷新被拒)之后的同步点——<b>反范式列 <c>needs_reauth</c> 的唯一写入点</b>。
    /// 漏掉的表现是一条已经失效的登录永远不会被账号池跳过:每次请求都挑中它、每次都是同一个 401。
    /// </summary>
    public async Task SyncAccountCredentialStateAsync(string providerId, string accountId, CancellationToken ct = default)
    {
        var raw = await _vault.GetAsync(ProviderRefs.ForAccount(providerId, accountId), ct).ConfigureAwait(false);
        if (CredentialFormat.Parse(raw) is not OAuthCredential oauth)
        {
            return;
        }

        _accounts.SetNeedsReauth(accountId, oauth.NeedsReauth);
        await SyncLegacyMirrorAsync(providerId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 凭据刷新器的写回通知(接到 <c>CredentialResolver</c> 的 <c>onChanged</c>):刷新被上游拒绝时要把 needsReauth 写回账号行,
    /// 并把账号列表推给控制端。参数是被改写的凭据 ref(账号 ref 或旧槽)。永不抛。
    /// </summary>
    public void OnCredentialChanged(string credentialRef)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (ProviderRefs.ParseAccountRef(credentialRef) is { } parsed)
                {
                    await SyncAccountCredentialStateAsync(parsed.ProviderId, parsed.AccountId).ConfigureAwait(false);
                    AccountsChanged?.Invoke(parsed.ProviderId);
                }
                else if (ProviderRefs.FileOwnedProviderId(credentialRef) is { } providerId)
                {
                    AccountsChanged?.Invoke(providerId);
                }
            }
            catch (Exception)
            {
                // 广播失败不该影响那条正在跑的请求。
            }
        });
    }
}
