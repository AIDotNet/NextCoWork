using System.Collections.Immutable;
using Ncw.Client.Accounts;

namespace Ncw.Client.Stores;

/// <summary>账户的决定(07 §1、§2):还没读到 / 还没决定(首次启动)/ 离线使用 / 已登录。</summary>
public enum AccountMode
{
    /// <summary>账户服务还没读完本地的账户记录(启动瞬间)。也是没有账户服务的场合(测试、模拟数据)的状态:界面当作「不打扰」,不弹首次启动。</summary>
    Loading,

    /// <summary>从没做过决定:首次启动流程要显示。</summary>
    Undecided,

    /// <summary>不登录,用自己的模型供应商(也是退出登录之后的状态)。</summary>
    Offline,

    Authenticated,
}

/// <summary>已登录会话的健康度:这是账户的<b>状态</b>,不是一次请求的结果。</summary>
public enum SessionHealth
{
    Ok,

    /// <summary>正在刷新访问令牌。</summary>
    Refreshing,

    /// <summary>刷新时连不上服务:登录状态照常保留(本地功能不受影响),过一会儿自动重试。</summary>
    Unreachable,

    /// <summary>刷新令牌已失效(过期、被撤销、钥匙串里没有):要重新登录。委托令牌已撤销。</summary>
    Expired,
}

public enum LoginMethod
{
    /// <summary>浏览器授权码 + PKCE,<c>nextcowork://auth/callback</c> 跳回。</summary>
    Browser,

    /// <summary>设备授权码(RFC 8628):用户在浏览器里输入短码。</summary>
    DeviceCode,
}

public enum LoginPhase
{
    /// <summary>等用户在浏览器里完成登录。</summary>
    Waiting,

    /// <summary>收到授权码,正在换令牌、读取团队。</summary>
    Exchanging,
}

/// <summary>一次进行中的登录(只有展示需要的信息;<b>verifier / state / 设备码不在这里</b>)。</summary>
public sealed record LoginProgress(LoginMethod Method, LoginPhase Phase, DateTimeOffset StartedAt, DateTimeOffset ExpiresAt, string? UserCode = null, string? VerificationUri = null);

/// <summary>已登录账户的展示信息。<b>不含任何令牌</b>(令牌只在 <see cref="AccountService"/> 和钥匙串里)。</summary>
/// <param name="ContextRequired">账户有多个团队而还没选:选完才能用模型通道 / 同步 / 市场。</param>
public sealed record AccountSession(AccountUser? User, ImmutableArray<TeamOption> Teams, string? SelectedTeamId, bool ContextRequired)
{
    public TeamOption? SelectedTeam => SelectedTeamId is null ? null : Teams.FirstOrDefault(t => t.Id == SelectedTeamId);
}

public enum DelegationStatus
{
    /// <summary>设备在线,委托令牌正在交付。</summary>
    Pending,

    Attached,

    /// <summary>交付失败(设备拒绝或掉线);下次令牌刷新 / 设备重连时再试。</summary>
    Failed,
}

/// <summary>本机设备上委托令牌的状态(key 带 deviceId)。设备断开时这一项被移除(规则 7:状态未知,不当作「还在」)。</summary>
public sealed record DeviceDelegation(DelegationStatus Status, DateTimeOffset? ExpiresAt = null);

/// <summary>
/// 本控制端的账户会话(07 §2「账户状态归属」):每个控制端各自登录,各自持有。<b>真源是这一个 Store</b>(令牌除外:令牌在钥匙串里,只有 <see cref="AccountService"/> 读写)。
/// 状态单调:<see cref="Generation"/> 在登录完成 / 退出登录时加一,所有异步结果带着发起时的代数回来,代数不对的丢弃——退出登录之后迟到的刷新结果复活不了会话。
/// </summary>
/// <param name="LoginError">最近一次登录失败的稳定 key(<c>account.login.*</c>);没有为 null。</param>
public sealed record AccountState(
    AccountMode Mode,
    AccountSession? Session,
    LoginProgress? Login,
    string? LoginError,
    SessionHealth Health,
    bool SecureStorageUnavailable,
    ImmutableDictionary<string, DeviceDelegation> Delegations,
    long Generation)
{
    public static readonly AccountState Initial = new(AccountMode.Loading, null, null, null, SessionHealth.Ok, false, ImmutableDictionary<string, DeviceDelegation>.Empty, 0);

    public bool IsSignedIn => Mode == AccountMode.Authenticated && Session is not null;

    public bool IsLoggingIn => Login is not null;
}

/// <summary>账户状态条的种类(界面顶部 / 设置页上方的一行提示)。</summary>
public enum AccountBannerKind
{
    None,

    /// <summary>登录进行中(等浏览器 / 换令牌)。</summary>
    SigningIn,

    /// <summary>登录失败。</summary>
    LoginFailed,

    /// <summary>登录已过期:要重新登录。</summary>
    Expired,

    /// <summary>已登录但连不上账户服务(暂时的)。</summary>
    Unreachable,

    /// <summary>已登录,账户有多个团队,还没选。</summary>
    TeamRequired,

    /// <summary>此平台暂时无法安全保存登录状态(不降级明文):关闭应用后要重新登录。</summary>
    SecureStorageUnavailable,
}

/// <summary>
/// 「账户状态条显示什么」的唯一派生函数(规则 3:首屏状态条和设置页的账户页共用同一份判断)。
/// 优先级:登录进行中 &gt; 登录失败 &gt; 已过期 &gt; 要选团队 &gt; 连不上 &gt; 无法安全保存。
/// </summary>
public static class AccountBanner
{
    public static AccountBannerKind Of(AccountState state)
    {
        if (state.Login is not null)
        {
            return AccountBannerKind.SigningIn;
        }

        if (state.LoginError is not null)
        {
            return AccountBannerKind.LoginFailed;
        }

        if (state.Mode != AccountMode.Authenticated)
        {
            return AccountBannerKind.None;
        }

        if (state.Health == SessionHealth.Expired)
        {
            return AccountBannerKind.Expired;
        }

        if (state.Session is { ContextRequired: true })
        {
            return AccountBannerKind.TeamRequired;
        }

        if (state.Health == SessionHealth.Unreachable)
        {
            return AccountBannerKind.Unreachable;
        }

        return state.SecureStorageUnavailable ? AccountBannerKind.SecureStorageUnavailable : AccountBannerKind.None;
    }
}
