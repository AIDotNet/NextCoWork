namespace Ncw.Client.Accounts;

/// <summary>
/// 账户后端(nextco.work)的 HTTP 接口(docs/design/07 §2)。授权码流程、令牌 / 账户 / 团队 / 用量端点与 2.x 共用线上的真实后端(<c>ClientOAuth</c>);
/// 设备授权、委托令牌、控制端列表由后端仓库(<c>Codex/CoWork</c>)提供,尚未部署的老环境上是 404 / 405 = <see cref="AccountApiErrorKind.Unsupported"/>(客户端会优雅降级)。默认实现 <see cref="HttpAccountApi"/> 用可注入的 <see cref="HttpClient"/>;
/// 测试用假实现或本地假 HTTP 服务。所有方法失败都抛 <see cref="AccountApiException"/>(种类见 <see cref="AccountApiErrorKind"/>)。
/// </summary>
public interface IAccountApi
{
    /// <summary>授权页地址(浏览器打开它)。纯拼接,不发请求。<paramref name="redirectUri"/> 是本机回环监听的地址(<c>http://127.0.0.1:{端口}/callback</c>)。</summary>
    Uri BuildAuthorizeUri(string state, string codeChallenge, string redirectUri);

    /// <summary>授权码 + <c>code_verifier</c> 换令牌。<paramref name="redirectUri"/> 必须与授权请求里的逐字相同。</summary>
    Task<TokenSet> ExchangeCodeAsync(string code, string codeVerifier, string redirectUri, CancellationToken ct);

    /// <summary>刷新令牌换新的访问令牌(刷新令牌会轮换,新的要存回钥匙串)。刷新令牌无效 = <see cref="AccountApiErrorKind.InvalidGrant"/>。</summary>
    Task<TokenSet> RefreshAsync(string refreshToken, CancellationToken ct);

    /// <summary>撤销刷新令牌(退出登录;尽力而为,调用方吞掉失败)。</summary>
    Task RevokeAsync(string refreshToken, CancellationToken ct);

    Task<AccountUser> GetUserAsync(string accessToken, CancellationToken ct);

    Task<TeamContext> GetTeamsAsync(string accessToken, CancellationToken ct);

    Task SelectTeamAsync(string accessToken, string teamId, CancellationToken ct);

    Task<IReadOnlyList<AccountUsageEntry>> GetUsageAsync(string accessToken, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct);

    /// <summary>设备授权的第一步(RFC 8628 §3.1)。</summary>
    Task<DeviceAuthorization> StartDeviceAuthorizationAsync(CancellationToken ct);

    /// <summary>设备授权的轮询(RFC 8628 §3.4)。</summary>
    Task<DevicePollResult> PollDeviceAuthorizationAsync(string deviceCode, CancellationToken ct);

    /// <summary>
    /// 为本机 Agent 换一枚委托令牌(RFC 8693 令牌交换;07 §2 第 7 步)。后端不支持(<see cref="AccountApiErrorKind.Unsupported"/>)时返回 null,
    /// 调用方退回把访问令牌本身作为委托令牌(同一台电脑、同一个用户,短期)。
    /// </summary>
    Task<DelegatedGrant?> ExchangeDelegatedAsync(string accessToken, CancellationToken ct);

    /// <summary>这个账户在哪些控制端上登录着(设置页「已登录的控制端」)。</summary>
    Task<IReadOnlyList<ControllerSession>> ListControllersAsync(string accessToken, CancellationToken ct);

    /// <summary>撤销另一个控制端的登录。</summary>
    Task RevokeControllerAsync(string accessToken, string controllerId, CancellationToken ct);
}

/// <summary>后端地址与 OAuth 客户端参数。授权页、<c>client_id</c>、scope 和令牌端点与线上后端(<c>ClientOAuth.NextCoWorkDesktop</c>)一致;设备授权 / 委托 / 控制端列表端点在后端仓库已实现,部署前线上是 404(05 §6)。</summary>
public sealed record AccountEndpoints
{
    public static readonly AccountEndpoints Default = new();

    public Uri Origin { get; init; } = new("https://nextco.work/");

    /// <summary>后端注册表里的桌面客户端(<c>ClientOAuth.ClientId</c>,已发布的 2.x 也用它)。后端只给它放行回环回调(<c>http://127.0.0.1:{端口}/callback</c>),没有自定义 scheme。</summary>
    public string ClientId { get; init; } = "nextcowork-desktop";

    /// <summary>
    /// 请求的 scope。后端对它是<b>全等匹配</b>(不是子集匹配),少一个就落到旧 scope、插件市场报 401——沿用 2.x 的完整串,改它之后必须重新登录。
    /// </summary>
    public string Scope { get; init; } =
        "profile:read wallet:read usage:read models:read inference:write config:read config:write config:secret:read config:secret:write skills:read skills:install plugins:read plugins:install";

    /// <summary>授权同意页(后端的网页路由,不在 <c>/api</c> 下)。</summary>
    public string AuthorizePath { get; init; } = "client/authorize";

    public string TokenPath { get; init; } = "api/client/oauth/token";

    public string RevokePath { get; init; } = "api/client/oauth/revoke";

    /// <summary>RFC 8628 设备授权端点(后端的 <c>POST /api/client/oauth/device</c>,<c>nextcowork-desktop</c> 与 <c>tack-server</c> 都可用)。</summary>
    public string DeviceAuthorizationPath { get; init; } = "api/client/oauth/device";

    public string DelegatePath { get; init; } = "api/client/oauth/delegate";

    public string AccountPath { get; init; } = "api/client/account";

    public string ContextOptionsPath { get; init; } = "api/client/context/options";

    public string ContextSelectPath { get; init; } = "api/client/context/select";

    public string UsagePath { get; init; } = "api/client/usage";

    public string ControllersPath { get; init; } = "api/client/sessions";

    /// <summary>每个账户请求的超时(2.x:10 秒,没有它设置页会无限转圈)。</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
}
