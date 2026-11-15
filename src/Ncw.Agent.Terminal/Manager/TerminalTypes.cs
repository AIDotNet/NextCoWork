using Ncw.Agent.Environments;

namespace Ncw.Agent.Terminal;

/// <summary>终端会话的状态。<b>单调</b>:<see cref="Exited"/> 之后永远不会回到 <see cref="Running"/>(同一个 id 重新创建是新的一段会话,序号和缓冲接着走)。</summary>
public enum TerminalStatus
{
    Running,
    Exited,
}

/// <summary>
/// 终端在哪个环境里运行:设备 + 环境(AGENTS.md §5:涉及执行的审批和卡片必须带上它)。
/// 同一台远程主机从「本机」和从「云端」打开是两个不同的 <see cref="Id"/>。
/// </summary>
public sealed record TerminalEnvironmentRef(EnvironmentId Id, string Name, EnvironmentKind Kind)
{
    public static TerminalEnvironmentRef From(IExecutionEnvironment environment) => new(environment.Id, environment.DisplayName, environment.Kind);
}

/// <summary>一个终端会话的元数据(不可变快照;<c>terminals.list</c> 的一行)。</summary>
public sealed record TerminalInfo(
    string Id,
    string ScopeId,
    TerminalEnvironmentRef Environment,
    string Title,
    string Cwd,
    string Shell,
    int Columns,
    int Rows,
    TerminalStatus Status,
    DateTimeOffset CreatedAt,
    int? ExitCode = null,
    int? Signal = null,
    ProcessExitReason? ExitReason = null,
    DateTimeOffset? ExitedAt = null)
{
    public bool Alive => Status == TerminalStatus.Running;
}

/// <summary>
/// 创建终端的请求。<see cref="ScopeId"/> 是限额和列表的作用域(项目 id,2.x 的 <c>workspaceId</c>);
/// <see cref="Environment"/> 和 <see cref="Root"/> 由调用方(Host)按项目解析好——本层不依赖项目登记。
/// </summary>
/// <param name="Id">给了就复用:同一个 id 的活终端直接返回(控制端重新挂载后重连);已退出的则在同一个 id 上重新开,序号和缓冲接着走。</param>
/// <param name="Cwd">相对或绝对的目录,必须落在 <paramref name="Root"/> 内(按环境的路径守卫解析);缺省 = 根目录。</param>
/// <param name="Approval">远程(SSH)环境的创建凭据:先 <see cref="TerminalManager.PrepareAsync"/> 拿意图、<see cref="TerminalManager.Approve"/> 批准得到。本机环境不需要。</param>
public sealed record TerminalCreateRequest(
    string ScopeId,
    IExecutionEnvironment Environment,
    EnvPath Root,
    int Columns,
    int Rows,
    string? Id = null,
    string? Cwd = null,
    string? Approval = null);

/// <summary>
/// 一次待批准的终端创建(2.x 的 <c>TerminalIntent</c>)。只带结构化数据,控制端翻译成「将在 dev-server(SSH,经由 本机)上打开终端」。
/// 绑定到发起它的控制端、这个环境实例和这一代连接;<see cref="ExpiresAt"/> 之后作废。
/// </summary>
public sealed record TerminalIntent(string Id, string TerminalId, string ScopeId, TerminalEnvironmentRef Environment, string Cwd, string Shell, DateTimeOffset ExpiresAt);

/// <summary><see cref="TerminalManager.PrepareAsync"/> 的结果:本机环境(或已有的活终端)直接就绪;远程环境要先批准。</summary>
public abstract record TerminalPreparation
{
    private TerminalPreparation()
    {
    }

    public sealed record Ready(TerminalInfo Terminal) : TerminalPreparation;

    public sealed record ApprovalRequired(TerminalIntent Intent) : TerminalPreparation;
}

/// <summary>终端输出流上的事件。<see cref="Seq"/> 在每个终端内严格连续(+1),退出事件也占一个序号:有缺口就是丢了,控制端据此重新 attach。</summary>
public abstract record TerminalEvent(long Seq);

/// <summary>一批输出(原始字节,不做任何解码:多字节字符可能被切在两批之间,由显示端的流式解码器处理)。</summary>
public sealed record TerminalOutput(long Seq, ReadOnlyMemory<byte> Data) : TerminalEvent(Seq);

/// <summary>终态。之后不会再有事件。</summary>
public sealed record TerminalExit(long Seq, int? ExitCode, int? Signal, ProcessExitReason Reason) : TerminalEvent(Seq);

/// <summary>环形缓冲里现存的输出(<c>getBuffer</c>)。<see cref="Seq"/> 是它包含的最后一批的序号(含退出事件)。</summary>
/// <param name="Truncated">缓冲装不下,最早的输出已被丢弃。</param>
public sealed record TerminalBuffer(string Id, ReadOnlyMemory<byte> Data, bool Truncated, long Seq);

/// <summary>
/// <see cref="TerminalManager.Attach"/> 的结果。取快照和挂订阅在同一把锁里完成:之后的事件一条不漏,也不会重复。
/// <list type="bullet">
/// <item><see cref="Resumed"/> = 假:给的游标不在日志里(或没给),<see cref="Snapshot"/> 是整个缓冲,控制端用它重置显示。</item>
/// <item><see cref="Resumed"/> = 真:游标还在日志里,<see cref="Snapshot"/> 为空,<see cref="Replay"/> 是游标之后错过的事件。</item>
/// </list>
/// </summary>
public sealed class TerminalAttachment : IDisposable
{
    private Action? _dispose;

    internal TerminalAttachment(TerminalInfo info, long seq, bool resumed, ReadOnlyMemory<byte> snapshot, bool truncated, IReadOnlyList<TerminalEvent> replay, Action dispose)
    {
        Info = info;
        Seq = seq;
        Resumed = resumed;
        Snapshot = snapshot;
        Truncated = truncated;
        Replay = replay;
        _dispose = dispose;
    }

    public TerminalInfo Info { get; }

    /// <summary>挂订阅那一刻日志的最后序号;之后推送的第一个事件是 <c>Seq + 1</c>。</summary>
    public long Seq { get; }

    public bool Resumed { get; }

    public ReadOnlyMemory<byte> Snapshot { get; }

    public bool Truncated { get; }

    public IReadOnlyList<TerminalEvent> Replay { get; }

    public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
}

public enum TerminalErrorCode
{
    /// <summary>没有这个终端(或它已被回收)。</summary>
    NotFound,

    /// <summary>状态不允许:终端已退出(再写、再调整大小、再终止),或这个 id 属于别的作用域 / 环境。</summary>
    Conflict,

    /// <summary>终端数达到上限(每作用域 8、总共 32,可配置)。</summary>
    ResourceLimit,

    /// <summary>参数不合法(空输入、窗口大小越界)。</summary>
    InvalidArgument,

    /// <summary>一次输入超过 <see cref="TerminalManagerOptions.InputMaxBytes"/>。</summary>
    TooLarge,

    /// <summary>远程环境的终端需要先逐个批准;或者这个终端属于另一个控制端。</summary>
    ApprovalRequired,

    /// <summary>批准已过期、不是这个控制端的,或这个意图已不存在。</summary>
    ApprovalExpired,

    /// <summary>创建被取消(控制端离开、页签关闭、设备停止)。</summary>
    Cancelled,
}

/// <summary>终端层的错误。环境本身的错误(目录不存在、连接断开、不支持终端)原样抛 <see cref="EnvironmentException"/>。本层不产出界面文字。</summary>
public sealed class TerminalException(TerminalErrorCode code, string? detail = null) : Exception(detail is null ? $"terminal:{code}" : $"terminal:{code} {detail}")
{
    public TerminalErrorCode Code { get; } = code;

    public string? Detail { get; } = detail;
}

/// <summary>插件终端(<c>tabs.openTerminal</c>)的一次性启动规格:创建时注入环境变量,并把启动行写进 shell(CLI 退出后终端仍是一个活 shell)。</summary>
public sealed record TerminalLaunchSpec(string ScopeId, IReadOnlyDictionary<string, string> Environment, IReadOnlyList<string> Argv);

public sealed class TerminalManagerOptions
{
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>终端 id 的生成。默认 <c>term-&lt;guid&gt;</c>。</summary>
    public Func<string>? NewId { get; init; }

    /// <summary>输出合批的间隔(2.x:16ms)。</summary>
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromMilliseconds(16);

    /// <summary>单批输出的上限,攒到它就立刻发,不等定时器(2.x:64 KiB)。</summary>
    public int MaxChunkBytes { get; init; } = 64 * 1024;

    /// <summary>环形缓冲的字节上限(2.x:256 KiB)。</summary>
    public int BufferBytes { get; init; } = 256 * 1024;

    /// <summary>一次输入的字节上限(2.x:64 KiB)。</summary>
    public int InputMaxBytes { get; init; } = 64 * 1024;

    /// <summary>已退出的终端保留多久供回看,之后回收(2.x:10 分钟)。</summary>
    public TimeSpan ExitedRetention { get; init; } = TimeSpan.FromMinutes(10);

    public int MaxPerScope { get; init; } = 8;

    public int MaxTotal { get; init; } = 32;

    /// <summary>待批准意图的有效期(2.x:60 秒)。</summary>
    public TimeSpan IntentTtl { get; init; } = TimeSpan.FromSeconds(60);

    public int MaxIntents { get; init; } = 64;

    /// <summary>一次性启动规格从备好到被认领的窗口(2.x:60 秒)。</summary>
    public TimeSpan LaunchSpecTtl { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>进程退出后,等输出流读到 EOF 的最长时间,之后不管了(后台进程攥着终端、SSH 通道提前关闭时不让「退出」挂住)。</summary>
    public TimeSpan ExitDrainWait { get; init; } = TimeSpan.FromSeconds(1);
}
