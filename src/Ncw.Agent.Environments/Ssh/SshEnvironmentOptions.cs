namespace Ncw.Agent.Environments.Ssh;

/// <summary>SSH 环境依赖的设备侧服务。全部可替换,所以环境本身不依赖协议层、存储层和界面。</summary>
public sealed class SshEnvironmentServices
{
    /// <summary>已知主机的指纹存储(按设备)。</summary>
    public required IHostKeyStore HostKeys { get; init; }

    /// <summary>首次出现的指纹怎么确认、指纹变化怎么通知。默认全部拒绝(没有交互通道时 fail closed)。</summary>
    public IHostKeyCallback HostKeyCallback { get; init; } = RejectingHostKeyCallback.Instance;

    /// <summary>密码、口令、键盘交互式问答。<c>null</c> = 没有交互通道:只能用无需口令的私钥。</summary>
    public ISshCredentialProvider? Credentials { get; init; }

    /// <summary>读 <c>~/.ssh/config</c> 和私钥所在的文件系统。默认设备本地磁盘。</summary>
    public ISshConfigFiles ConfigFiles { get; init; } = new PhysicalSshConfigFiles();
}

public sealed class SshEnvironmentOptions
{
    public ReconnectPolicy Reconnect { get; init; } = ReconnectPolicy.Default;

    /// <summary>
    /// 环境正在连接或重连时,新发起的操作最多等多久。超时抛 <see cref="EnvironmentErrorCode.Disconnected"/>。
    /// 已经在运行的操作不受它影响:连接中断时它们立刻以「连接中断」结束。
    /// </summary>
    public TimeSpan ReadyWait { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>终止远程进程树时,从 TERM 到 KILL 的宽限期。</summary>
    public TimeSpan KillGrace { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>本机用户名,用来展开 ssh_config 里的 <c>%u</c> 和作为没写 User 时的默认用户名。</summary>
    public string? LocalUser { get; init; }

    /// <summary>
    /// ssh-agent(<see cref="SshAgentClient"/>):非空时,密钥认证先试 agent 里的身份(私钥永远不进本进程),再试私钥文件。<c>null</c> = 不用 agent。
    /// 宿主在本机设备上用 <see cref="SshAgentClient.Discover"/> 找系统的 agent;云端设备没有用户的 agent,不设。
    /// </summary>
    public ISshAgent? Agent { get; init; }

    /// <summary>重连等待的时间源(测试里替换成立即返回)。</summary>
    public Func<TimeSpan, CancellationToken, Task>? Delay { get; init; }
}
