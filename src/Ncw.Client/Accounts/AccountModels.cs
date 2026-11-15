namespace Ncw.Client.Accounts;

/// <summary>账户的基本信息(2.x 的 <c>ClientAuthUser</c> 去掉钱包:3.0 不涉及支付)。</summary>
public sealed record AccountUser(string Id, string? Username, string? Email, string? DisplayName, string? AvatarUrl)
{
    /// <summary>界面上显示的名字:显示名 → 用户名 → 邮箱 → id。</summary>
    public string Label => FirstNonEmpty(DisplayName, Username, Email) ?? Id;

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}

/// <summary>账户下的一个团队(Team)。</summary>
public sealed record TeamOption(string Id, string Name, string Type, string Role, int MemberCount, bool IsSelected);

/// <summary>团队上下文:账户可选的全部团队和服务端记着的当前团队(没记 = null)。</summary>
public sealed record TeamContext(IReadOnlyList<TeamOption> Teams, string? SelectedTeamId);

/// <summary>一组令牌。<b>只在 <see cref="AccountService"/> 内部和钥匙串里</b>:不进 Store、不进 DTO、不进日志。</summary>
public sealed record TokenSet(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, AccountUser? User);

/// <summary>委托给本机 Agent 的令牌(07 §2 第 7 步)。</summary>
public sealed record DelegatedGrant(string Token, DateTimeOffset ExpiresAt);

/// <summary>设备授权(RFC 8628)的第一步结果:给用户看的短码和验证地址,以及轮询用的设备码。</summary>
public sealed record DeviceAuthorization(string DeviceCode, string UserCode, string VerificationUri, string? VerificationUriComplete, TimeSpan ExpiresIn, TimeSpan Interval);

public enum DevicePollKind
{
    /// <summary>用户还没完成授权(<c>authorization_pending</c>)。</summary>
    Pending,

    /// <summary>轮询太快(<c>slow_down</c>):间隔加 5 秒。</summary>
    SlowDown,

    Granted,

    /// <summary>用户拒绝(<c>access_denied</c>)。</summary>
    Denied,

    /// <summary>设备码过期(<c>expired_token</c>)。</summary>
    Expired,
}

public sealed record DevicePollResult(DevicePollKind Kind, TokenSet? Tokens = null);

/// <summary>账户在某个控制端上的登录(「已登录的控制端」列表的一行)。</summary>
public sealed record ControllerSession(string Id, string Name, string Platform, DateTimeOffset? LastActiveAt, bool IsCurrent);

/// <summary>账户的一条用量记录(2.x 的 <c>ClientUsageEntry</c>)。</summary>
public sealed record AccountUsageEntry(string Id, DateTimeOffset At, string? Model, string? Provider, long InputTokens, long OutputTokens, decimal? Cost, string? Currency);

/// <summary>后端调用失败的种类。界面按种类选文字,不看后端的原话。</summary>
public enum AccountApiErrorKind
{
    /// <summary>连不上、超时、DNS 失败:暂时的,可重试;已有的登录状态照常保留。</summary>
    Network,

    /// <summary>令牌被拒(401):访问令牌过期或被撤销。</summary>
    Unauthorized,

    /// <summary>刷新令牌 / 授权码无效(<c>invalid_grant</c>):登录已经失效,必须重新登录。</summary>
    InvalidGrant,

    /// <summary>没有权限(403)。</summary>
    Forbidden,

    /// <summary>服务端出错(5xx)。</summary>
    Server,

    /// <summary>后端不提供这个端点(404 / 405 / 501):后端还没就绪或版本较老。</summary>
    Unsupported,

    /// <summary>响应读不懂(形状不对)。</summary>
    BadResponse,

    /// <summary>其他被拒的请求(400 等)。</summary>
    Rejected,
}

/// <summary>后端调用失败。<see cref="Message"/> 是英文诊断,<b>不含令牌</b>。</summary>
public sealed class AccountApiException(AccountApiErrorKind kind, string message, int? status = null, string? oauthError = null, Exception? inner = null)
    : Exception(message, inner)
{
    public AccountApiErrorKind Kind { get; } = kind;

    public int? Status { get; } = status;

    /// <summary>OAuth 错误码(<c>invalid_grant</c> / <c>access_denied</c> …),没有为 null。</summary>
    public string? OAuthError { get; } = oauthError;
}
