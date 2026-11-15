namespace Ncw.Client.Accounts;

/// <summary>
/// 账户后端(nextco.work)的 HTTP 接口(docs/design/07 §2、05 §6 的 B10 / B11)。默认实现 <see cref="HttpAccountApi"/> 用可注入的 <see cref="HttpClient"/>;
/// 后端暂时不存在,所以所有测试都用假实现或本地假 HTTP 服务。所有方法失败都抛 <see cref="AccountApiException"/>(种类见 <see cref="AccountApiErrorKind"/>)。
/// </summary>
public interface IAccountApi
{
    /// <summary>授权页地址(浏览器打开它)。纯拼接,不发请求。</summary>
    Uri BuildAuthorizeUri(string state, string codeChallenge);

    /// <summary>授权码 + <c>code_verifier</c> 换令牌。</summary>
    Task<TokenSet> ExchangeCodeAsync(string code, string codeVerifier, CancellationToken ct);

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

/// <summary>后端地址与 OAuth 客户端参数。<b>授权端点 / 设备授权 / 委托 / 控制端列表是 B11 的待确认约定</b>(05 §6),其余沿用 2.x 的实际端点。</summary>
public sealed record AccountEndpoints
{
    public static readonly AccountEndpoints Default = new();

    public Uri Origin { get; init; } = new("https://nextco.work/");

    /// <summary>桌面和手机各有自己的 client_id(B11);桌面是 <c>ncw-desktop</c>。</summary>
    public string ClientId { get; init; } = "ncw-desktop";

    public string RedirectUri { get; init; } = "nextcowork://auth/callback";

    /// <summary>
    /// 请求的 scope。后端对它是<b>全等匹配</b>(不是子集匹配),少一个就落到旧 scope、插件市场报 401——沿用 2.x 的完整串,改它之后必须重新登录。
    /// </summary>
    public string Scope { get; init; } =
        "profile:read wallet:read usage:read models:read inference:write config:read config:write config:secret:read config:secret:write skills:read skills:install plugins:read plugins:install";

    public string AuthorizePath { get; init; } = "oauth/authorize";

    public string TokenPath { get; init; } = "api/client/oauth/token";

    public string RevokePath { get; init; } = "api/client/oauth/revoke";

    public string DeviceAuthorizationPath { get; init; } = "api/client/oauth/device_authorization";

    public string DelegatePath { get; init; } = "api/client/oauth/delegate";

    public string AccountPath { get; init; } = "api/client/account";

    public string ContextOptionsPath { get; init; } = "api/client/context/options";

    public string ContextSelectPath { get; init; } = "api/client/context/select";

    public string UsagePath { get; init; } = "api/client/usage";

    public string ControllersPath { get; init; } = "api/client/sessions";

    /// <summary>每个账户请求的超时(2.x:10 秒,没有它设置页会无限转圈)。</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
}
