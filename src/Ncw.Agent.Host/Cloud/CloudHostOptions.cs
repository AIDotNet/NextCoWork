using Ncw.Agent.Cloud;
using Ncw.Agent.Storage.Credentials;
using Ncw.Protocol.Orchestration;

namespace Ncw.Agent.Host.Cloud;

/// <summary>云端 Agent 退出的原因(<see cref="AgentHost.CloudExit"/>;进程退出码都是 0,原因由编排服务经生命周期通知 / 快照得知)。</summary>
/// <param name="Reason"><c>sleep</c>、<c>upgrade</c>,或 <c>identity</c>(运行中发现编排服务给的身份绑定和设备不符,进程以退出码 65 结束)。</param>
/// <param name="WakeAt">希望被唤醒的时间(下一个定时任务触发时间 − 提前量);null = 只在控制端连接时唤醒。</param>
public sealed record CloudExit(string Reason, long? WakeAt);

/// <summary>
/// <c>--mode cloud</c> 的装配参数(02 A6)。启动配置、编排通道、KMS 包裹后的主密钥来源由进程入口(<c>Program.cs</c>)按启动配置建好交进来;
/// 测试注入假编排服务、假 KMS(<see cref="KmsMasterKeySource"/> 背后的 <see cref="IKeyWrap"/>)和手动时钟。
/// </summary>
public sealed class CloudHostOptions
{
    public required CloudConfig Config { get; init; }

    /// <summary>Agent → 编排服务的通道。</summary>
    public required IOrchestrationChannel Channel { get; init; }

    /// <summary>账户身份令牌的持有者(通道的 Authorization 头从它取;<c>account.token</c> 指令更新它)。</summary>
    public required AccountTokenHolder Token { get; init; }

    /// <summary>KMS 包裹的主密钥来源(已经 <see cref="KmsMasterKeySource.OpenAsync"/>)。Host 登记它并在释放时清零。</summary>
    public required KmsMasterKeySource KeySource { get; init; }

    /// <summary>出网策略(缺省:不约束)。Git 克隆已经接线,其它出口见 <see cref="IEgressPolicy"/>。</summary>
    public IEgressPolicy? Egress { get; init; }

    /// <summary>云端运行时自己的时钟(空闲检测、用量上报、配额窗口、指令有效期)。缺省用 Host 的时钟;测试给手动时钟让休眠检测确定性。</summary>
    public TimeProvider? Clock { get; init; }

    /// <summary>编排通道失败后的退避(测试里缩短)。</summary>
    public TimeSpan RetryInitial { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan RetryMax { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>长轮询等指令的时间。</summary>
    public TimeSpan PollWait { get; init; } = TimeSpan.FromSeconds(25);

    /// <summary>
    /// 启动时最多等多久第一次打招呼成功(处理完编排服务积压的指令,例如休眠期间到达的吊销)再开始向中继注册。
    /// 超时则带着已有的信任记录先上线(编排服务不可用时设备不该整个不可用),之后指令到达再吊销。
    /// </summary>
    public TimeSpan FirstHelloDeadline { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>休眠 / 升级前通知编排服务的重试次数和每次的超时。通知失败不阻止退出(退出码 0 + 休眠快照本身就是真相)。</summary>
    public int NoticeAttempts { get; init; } = 3;

    public TimeSpan NoticeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>是否启动内网健康检查端点(配置里有 <c>health</c> 才会启动)。</summary>
    public bool EnableHealth { get; init; } = true;
}

/// <summary>Run 发起前的闸门(配额 / 收尾中)。Host 的 <c>CloudRuntime</c> 实现;本机模式没有它(<c>null</c>)。</summary>
internal interface IRunGate
{
    /// <summary>检查并记账。不允许时抛 <see cref="Ncw.Protocol.Wire.ProtocolException"/>(<c>quota.exceeded</c> / <c>cloud.preparing</c>);允许时返回凭条(Run 没真正发起时 <see cref="RunTicket.Refund"/>)。</summary>
    RunTicket? AdmitRun();

    /// <summary>只检查(排队 / 插话):不记账。</summary>
    void CheckInput();
}
