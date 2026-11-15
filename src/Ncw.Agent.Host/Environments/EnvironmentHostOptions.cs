using Ncw.Agent.Environments.Ssh;

namespace Ncw.Agent.Host.Environments;

/// <summary>
/// SSH 环境在 Host 里的参数(<see cref="Ncw.Agent.Host.Conversation.AgentServices.SshEnvironments"/>)。都有生产默认值;测试用它缩短重连退避、空闲回收时间、
/// 指向临时 <c>~/.ssh</c>、关掉对系统 ssh-agent 的自动发现。
/// </summary>
public sealed class EnvironmentHostOptions
{
    /// <summary>SSH 连接的参数(重连策略、等待重连的上限、终止进程的宽限期、用户名)。<see cref="SshEnvironmentOptions.Agent"/> 为空时见 <see cref="DiscoverSshAgent"/>。</summary>
    public SshEnvironmentOptions? Ssh { get; init; }

    /// <summary>读 <c>~/.ssh/config</c> 和私钥所在的文件系统。默认:设备本地磁盘(测试指向临时目录,绝不读用户真实的 <c>~/.ssh</c>)。</summary>
    public ISshConfigFiles? ConfigFiles { get; init; }

    /// <summary>
    /// <see cref="Ssh"/> 没有给 agent 时,是否按系统环境找 ssh-agent(Unix 的 <c>SSH_AUTH_SOCK</c>、Windows 的 OpenSSH 命名管道)。默认开:
    /// 用户的 agent 里有密钥,SSH 环境就不必再问口令。云端设备没有用户的 agent(找不到就是没有)。
    /// </summary>
    public bool DiscoverSshAgent { get; init; } = true;

    /// <summary>
    /// 空闲回收:一个 SSH 环境没有任何 Run / 终端 / 后台 Shell 在用、也没有被访问过,超过这么久就断开连接回收(2.x 的 <c>EnvironmentManager</c> 的 60 秒)。
    /// 配置和凭据保留,下次使用惰性重连。<see cref="TimeSpan.Zero"/> = 不回收。
    /// </summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(60);
}
