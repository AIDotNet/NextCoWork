namespace Ncw.Desktop.Sidecar;

public enum SidecarPhase
{
    /// <summary>还没启动。</summary>
    Idle,

    /// <summary>正在拉起并等握手。</summary>
    Starting,

    /// <summary>握手完成,Agent 可用。</summary>
    Running,

    /// <summary>Agent 意外退出,退避后重启。</summary>
    Restarting,

    /// <summary>不会自动继续了(见 <see cref="SidecarIssue"/>),等用户处理或调用 <see cref="LocalAgentSidecar.Retry"/>。</summary>
    Failed,

    /// <summary>桌面端正在关闭 Agent。</summary>
    Stopping,

    /// <summary>已停止(或按「关窗后继续运行」留下它继续跑)。</summary>
    Stopped,
}

/// <summary>需要告诉用户的问题(界面文案由 UI 层按这个枚举取,这里只给结构化数据:AGENTS.md §2)。</summary>
public enum SidecarIssue
{
    None,

    /// <summary>找不到 ncw-agent:安装不完整。</summary>
    AgentNotFound,

    /// <summary>进程拉不起来(权限、被安全软件拦截…)。</summary>
    LaunchFailed,

    /// <summary>这个数据目录已经被另一个 Agent 占用,而且它不认本机令牌(别的用户、别的程序)。</summary>
    AlreadyRunningElsewhere,

    /// <summary>包里的 Agent 比桌面端旧,换了也一样:安装包不完整,请重新安装。</summary>
    AgentOutdated,

    /// <summary>Agent 比桌面端新(或 Agent 拒绝了过旧的客户端:<c>client_outdated</c>):请更新 NextCoWork。</summary>
    ClientOutdated,

    /// <summary>反复崩溃,不再自动重启。</summary>
    CrashLoop,

    /// <summary>数据目录里有加密的数据,却找不到它的凭据主密钥(<c>credential.key</c>)。Agent 为了不让已有的凭据变得无法解密而拒绝启动(退出码 65),不会自动创建新密钥。重启没用。</summary>
    MasterKeyMissing,

    /// <summary><c>credential.key</c> 在,但读不了、已损坏,或者不是这份数据的密钥。Agent 拒绝启动(退出码 65)。重启没用。</summary>
    MasterKeyUnreadable,
}

/// <param name="RestartCount">本次桌面端运行期间 Agent 被意外重启的次数。</param>
/// <param name="RetryIn">下一次重启的等待时间(<see cref="SidecarPhase.Restarting"/> 时)。</param>
/// <param name="Detail">给诊断页的原始信息(退出码、异常消息);不是界面文案。</param>
public sealed record SidecarStatus(
    SidecarPhase Phase,
    SidecarIssue Issue = SidecarIssue.None,
    string? AgentVersion = null,
    int RestartCount = 0,
    TimeSpan? RetryIn = null,
    string? Detail = null)
{
    public static readonly SidecarStatus Initial = new(SidecarPhase.Idle);
}
