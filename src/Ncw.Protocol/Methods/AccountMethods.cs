using PolyType;

namespace Ncw.Protocol.Methods;

public static partial class Capabilities
{
    /// <summary>账户委托:<c>account.*</c>(把控制端的账户令牌委托给本机 Agent;owner,<b>仅本机通道</b>)。</summary>
    public const string Account = "account";
}

// 账户的委托令牌(docs/design/07 §2「账户状态归属」、09 §8.4 的 account 一行)。
// 账户会话(刷新令牌)只属于控制端,存在控制端的系统钥匙串里;本机 Agent 拿到的是一份<b>委托令牌</b>(短期访问令牌),只用于模型通道、配置同步和技能 / 插件市场。
// 令牌方向只有控制端 → Agent(和 providers.credential.set 的密钥一样):它只出现在 attach 的请求参数里,Agent 只放内存、不落盘,
// 任何结果 / 状态 / 事件 / 日志里都没有它,只有「有没有、属于谁、什么时候过期」。远程控制端不能调(远程 owner 不能替本机换账户)。

/// <summary>委托令牌的交付。<paramref name="Token"/> 是请求参数里唯一一处携带令牌的地方。</summary>
/// <param name="ExpiresAt">过期时刻(Unix 毫秒)。过期之后 Agent 视为没有令牌(市场回到 <c>authRequired</c>),控制端在刷新令牌之后再次交付。</param>
/// <param name="AccountId">令牌所属账户的 id(只用来给人看和对账,不是密钥)。</param>
/// <param name="TeamId">令牌所属团队;没选团队为 null。</param>
public sealed record AccountAttachParams(string Token, long ExpiresAt, string AccountId, string? TeamId);

/// <summary>Agent 上委托令牌的状态。<b>不含令牌本身</b>。</summary>
public sealed record AccountStatusDto(bool Attached, string? AccountId, string? TeamId, long? ExpiresAt);

/// <summary>账户委托方法表(能力 <see cref="Capabilities.Account"/>)。全部 owner + 仅本机通道。</summary>
public static class AccountMethods
{
    /// <summary>交付(或替换)委托令牌。成功返回新的状态(不含令牌)。已经过期的令牌 = <c>invalid_request</c>(<c>account.tokenExpired</c>)。</summary>
    public static readonly MethodDef<AccountAttachParams, AccountStatusDto> AttachDelegatedToken =
        MethodTable.Command<AccountAttachParams, AccountStatusDto>("account.attachDelegatedToken", Role.Owner, Capabilities.Account, Transports.Local);

    /// <summary>撤销委托令牌(退出登录、切换账户)。幂等:本来就没有也成功。</summary>
    public static readonly MethodDef<Unit, AccountStatusDto> DetachDelegatedToken =
        MethodTable.Command<Unit, AccountStatusDto>("account.detachDelegatedToken", Role.Owner, Capabilities.Account, Transports.Local);

    /// <summary>当前有没有委托令牌(控制端重连、Agent 重启之后对账用)。</summary>
    public static readonly MethodDef<Unit, AccountStatusDto> GetStatus =
        MethodTable.Query<Unit, AccountStatusDto>("account.getStatus", Role.Owner, Capabilities.Account, Transports.Local);

    public static readonly IReadOnlyList<MethodDef> All = [AttachDelegatedToken, DetachDelegatedToken, GetStatus];
}

public static class AccountLimits
{
    /// <summary>委托令牌的长度上限(JWT / 不透明令牌都远小于它);防止把任意大的东西塞进 Agent 的内存。</summary>
    public const int MaxTokenLength = 8 * 1024;
}

public static class AccountErrorKeys
{
    public const string TokenExpired = "account.tokenExpired";
    public const string TokenInvalid = "account.tokenInvalid";
}

[GenerateShapeFor<AccountAttachParams>]
[GenerateShapeFor<AccountStatusDto>]
public sealed partial class PayloadShapes;
