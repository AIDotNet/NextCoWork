using PolyType;

namespace Ncw.Protocol.Methods;

// 云端 Agent 的客户端可见部分(docs/design/02 A6、09 §4):状态 / 配额 / 用量的只读查询,和被休眠打断的 Run 的确认。
// ★ 只有 `--mode cloud` 的 Agent 才注册这些方法并声明能力 `cloud`:本机内置的 Agent 没有配额、没有休眠,控制端据此隐藏用量显示和「正在唤醒云端…」。
// ★ 配额与用量的真源在 Agent(Agent 自己数 Run、读用量表),编排服务下发的只是上限;控制端只读。编排控制通道(Agent ↔ 编排服务)的类型在 Ncw.Protocol.Orchestration,客户端永远看不到。

/// <summary>云端设备的生命周期状态。休眠中的设备不在线,所以这里没有 sleeping:控制端看到的「休眠」是「设备离线 + 账户 API 说它在休眠」。</summary>
public static class CloudStates
{
    /// <summary>醒着,正常服务。</summary>
    public const string Awake = "awake";

    /// <summary>正在收尾准备休眠 / 升级:新的 Run 被拒绝(<c>unavailable</c>,可重试),连接马上会断开。</summary>
    public const string Preparing = "preparing";
}

/// <summary>配额上限(null = 不限)。字段含义见 <c>Ncw.Protocol.Orchestration.QuotaLimits</c>。</summary>
public sealed record CloudQuotaDto(
    long? DailyRuns,
    long? MonthlyRuns,
    long? DailyTokens,
    long? MonthlyTokens,
    int? MaxConcurrentRuns,
    long? MonthlyComputeSeconds,
    long? StorageBytes,
    bool Frozen);

/// <summary>当前窗口里的用量。<see cref="DayStart"/> / <see cref="MonthStart"/> 是窗口起点(UTC,Unix 毫秒),界面据此显示「重置时间」。</summary>
public sealed record CloudUsageDto(
    long RunsToday,
    long RunsThisMonth,
    long TokensToday,
    long TokensThisMonth,
    int ActiveRuns,
    long ComputeSecondsThisMonth,
    long? StorageBytes,
    long DayStart,
    long MonthStart);

/// <summary>被休眠 / 升级打断的 Run(<c>sleep.request</c> 的 <c>now</c> 模式)。会话和已落库的消息都在;控制端可以据此提示「上次的任务被中断了」并让用户继续。</summary>
public sealed record CloudInterruptedRunDto(string RunId, string SessionId, long At);

/// <summary>
/// <c>cloud.getStatus</c> 的结果。
/// </summary>
/// <param name="State"><see cref="CloudStates"/> 之一。</param>
/// <param name="StartedAt">这次进程启动的时间(Unix 毫秒)。</param>
/// <param name="ResumedFromSleep">这次启动是不是从休眠里醒来。</param>
/// <param name="WokeAt">醒来的时间(只在 <see cref="ResumedFromSleep"/> 为真时有值)。</param>
/// <param name="Quota">编排服务下发的配额(没下发过 = 全部不限)。</param>
/// <param name="Usage">当前用量。</param>
/// <param name="ExceededKey">已经超限时的稳定 key(<c>quota.exceeded</c>);没超限为 null。</param>
/// <param name="ExceededWindow">超限的是哪一项(<c>dailyRuns</c> / <c>monthlyTokens</c> / <c>frozen</c> ……)。</param>
/// <param name="InterruptedRuns">被休眠打断、还没被控制端确认的 Run。</param>
/// <param name="AccountId">这台云端 Agent 所属的账户(编排服务签发的身份绑定里的);还没注册完成为 null。</param>
public sealed record CloudStatus(
    string State,
    long StartedAt,
    bool ResumedFromSleep,
    long? WokeAt,
    CloudQuotaDto Quota,
    CloudUsageDto Usage,
    string? ExceededKey,
    string? ExceededWindow,
    CloudInterruptedRunDto[] InterruptedRuns,
    string? AccountId);

public sealed record CloudClearInterruptedParams(string[] RunIds);

/// <summary>稳定的错误 key(<c>error.details</c> 里 <c>ProviderErrorDto.Key</c>,控制端翻译)。</summary>
public static class CloudErrorKeys
{
    /// <summary>配额用尽:新的 Run(含排队消息、定时任务)被拒绝。错误码是 <c>rate_limited</c>,不可重试(窗口重置前再试没有意义);参数 <c>window</c> / <c>limit</c> / <c>used</c> / <c>resetsAt</c>。</summary>
    public const string QuotaExceeded = "quota.exceeded";

    /// <summary>云端 Agent 由账户签发身份、信任记录由编排服务下发,不支持配对码 / 二维码配对。</summary>
    public const string PairingUnsupported = "cloud.pairing.unsupported";

    /// <summary>云端 Agent 的远程连接设置由编排服务管理。</summary>
    public const string ManagedByOrchestrator = "cloud.managed";

    /// <summary>云端 Agent 正在收尾休眠 / 升级,稍后重试(可重试)。</summary>
    public const string Preparing = "cloud.preparing";

    /// <summary>出网策略拒绝了这个目标。</summary>
    public const string EgressDenied = "cloud.egress.denied";
}

/// <summary>云端方法表(能力 <see cref="Capabilities.Cloud"/>,承载 本机 + 远程)。</summary>
public static class CloudMethods
{
    public static readonly MethodDef<Unit, CloudStatus> GetStatus =
        MethodTable.Query<Unit, CloudStatus>("cloud.getStatus", Role.Viewer, Capabilities.Cloud);

    public static readonly MethodDef<CloudClearInterruptedParams, Unit> ClearInterrupted =
        MethodTable.Command<CloudClearInterruptedParams, Unit>("cloud.clearInterrupted", Role.Member, Capabilities.Cloud);

    public static readonly IReadOnlyList<MethodDef> All = [GetStatus, ClearInterrupted];
}

public static partial class Capabilities
{
    /// <summary>云端 Agent:<c>cloud.*</c>(状态 / 配额 / 用量)。只有 <c>--mode cloud</c> 的设备声明;没有它的设备没有配额,界面隐藏用量显示。</summary>
    public const string Cloud = "cloud";

    /// <summary>Git 凭据:<c>git.credentials.*</c>(owner;密钥只进不出,经 NCK1 加密存储)。云端项目克隆私有仓库用。</summary>
    public const string GitCredentials = "git.credentials";
}

// ---- Git 凭据(02 A6「工作区来源」「Git 接力」)------------------------------------------------------------------------

/// <summary>
/// 一个 Git 主机的 HTTPS 凭据(GitHub / GitLab OAuth 令牌或个人访问令牌)。<b>令牌只出现在 <c>git.credentials.set</c> 的请求里</b>(控制端 → 设备单向),
/// 存进设备的 NCK1 凭据库;设备从不把它发回任何控制端,也不写进日志、命令行参数或 <c>.git/config</c>(用 git 的环境变量配置注入)。
/// </summary>
/// <param name="Host">主机名(<c>github.com</c>;非默认端口形如 <c>git.example.com:8443</c>)。</param>
/// <param name="Token">令牌。</param>
/// <param name="Username">HTTP 基本认证的用户名;空 = <c>x-access-token</c>(GitHub 的 OAuth / 应用令牌约定;GitLab 用 <c>oauth2</c>,由控制端填)。</param>
public sealed record GitCredentialSetParams(string Host, string Token, string? Username = null);

public sealed record GitCredentialRemoveParams(string Host);

/// <summary>已保存凭据的清单:只有主机和用户名,没有令牌。</summary>
public sealed record GitCredentialDto(string Host, string Username);

public sealed record GitCredentialsResult(GitCredentialDto[] Credentials);

public static class GitCredentialsMethods
{
    public const int MaxTokenLength = 4096;

    public static readonly MethodDef<GitCredentialSetParams, GitCredentialsResult> Set =
        MethodTable.Command<GitCredentialSetParams, GitCredentialsResult>("git.credentials.set", Role.Owner, Capabilities.GitCredentials);

    public static readonly MethodDef<GitCredentialRemoveParams, GitCredentialsResult> Remove =
        MethodTable.Command<GitCredentialRemoveParams, GitCredentialsResult>("git.credentials.remove", Role.Owner, Capabilities.GitCredentials);

    public static readonly MethodDef<Unit, GitCredentialsResult> List =
        MethodTable.Query<Unit, GitCredentialsResult>("git.credentials.list", Role.Owner, Capabilities.GitCredentials);

    public static readonly IReadOnlyList<MethodDef> All = [Set, Remove, List];
}

[GenerateShapeFor<CloudStatus>]
[GenerateShapeFor<CloudQuotaDto>]
[GenerateShapeFor<CloudUsageDto>]
[GenerateShapeFor<CloudInterruptedRunDto>]
[GenerateShapeFor<CloudClearInterruptedParams>]
[GenerateShapeFor<GitCredentialSetParams>]
[GenerateShapeFor<GitCredentialRemoveParams>]
[GenerateShapeFor<GitCredentialDto>]
[GenerateShapeFor<GitCredentialsResult>]
public sealed partial class PayloadShapes;
