using Ncw.Agent.Environments;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;

namespace Ncw.Agent.Kernel.Session;

/// <summary>
/// <c>run_settled</c>(R8):落库、释放会话锁<b>之后</b>才发。<see cref="RunEndEvent"/> 只表示循环结束,此时可能还没落库完;下一个 Run 只等这个事件。
/// 由会话层发(它是循环层之上的事件),编排层把它当作 Run 的真正结束。
/// </summary>
public sealed record RunSettledEvent(RunId RunId, RunStatus Status) : AgentEvent;

/// <summary>事件的接收方(编排层的事件日志 / 推送)。抛异常不会影响 Run(记诊断后继续)。</summary>
public interface IRunSink
{
    ValueTask EmitAsync(AgentEvent ev, CancellationToken ct);
}

/// <summary>把事件收进一个列表(测试和同步调用方用)。</summary>
public sealed class ListRunSink : IRunSink
{
    private readonly List<AgentEvent> _events = [];

    public IReadOnlyList<AgentEvent> Events
    {
        get
        {
            lock (_events)
            {
                return [.. _events];
            }
        }
    }

    public ValueTask EmitAsync(AgentEvent ev, CancellationToken ct)
    {
        lock (_events)
        {
            _events.Add(ev);
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>Run 开始时的设置快照(运行期不变)。</summary>
public sealed record SessionSettings
{
    /// <summary>总闸:关掉就一次都不自动压缩——哪怕判据为真。</summary>
    public bool AutoCompact { get; init; } = true;

    /// <summary>设置 › 通用 › Agent 的「最大输出 Token」;null = 出厂值。</summary>
    public int? MaxOutputTokens { get; init; }

    public CompactionSettings? Compaction { get; init; }

    public WorkspaceCompaction? WorkspaceCompaction { get; init; }

    public Personalization? Personalization { get; init; }

    /// <summary>压缩要认的「碰过文件」的工具的外部名(由装配方按工具注册表查出来)。</summary>
    public FileToolNames FileTools { get; init; } = FileToolNames.Default;

    /// <summary>全局指令文件所在的设备本地目录;null = 不读全局层。</summary>
    public GlobalInstructions? GlobalInstructions { get; init; }

    /// <summary>2.x 从其他工具导入的指令副本,附在最后,本地文件优先。</summary>
    public string? ImportedInstructions { get; init; }

    /// <summary>读项目指令(AGENTS.md / CLAUDE.md)。关掉时(例如纯净的子代理)不读。</summary>
    public bool ProjectInstructions { get; init; } = true;
}

/// <summary>一次 Run 的请求(04 §、05)。<see cref="Input"/> 的消息 id 由调用方提供(用户消息 id 由客户端给,送达以同 id 的 <c>message_end</c> 为准)。</summary>
public sealed record RunRequest(
    RunId RunId,
    IReadOnlyList<Message> Input,
    ModelRef Model,
    ThinkingLevel Thinking = ThinkingLevel.Auto)
{
    /// <summary>用户打开「最大上下文」:有效窗口不再夹在计费分界内。</summary>
    public bool MaxContext { get; init; }

    /// <summary>子代理深度(0 = 主 Run)。</summary>
    public int Depth { get; init; }

    public SessionSettings Settings { get; init; } = new();

    /// <summary>子代理的角色提示词(<b>追加</b>在基础提示词之后,不替换)。</summary>
    public string? AgentPrompt { get; init; }

    public string? ModePrompt { get; init; }

    /// <summary>编排层的引导消息队列:回合边界取走即消费(02 §5)。</summary>
    public Func<IReadOnlyList<Message>>? TakeSteering { get; init; }

    /// <summary>编排层把基础工具上下文换成带 <c>Interact</c> / <c>SpawnSubagent</c> 等能力的派生类型。会话层先换上执行环境,再交给它。</summary>
    public Func<ToolContext, ToolContext>? EnrichToolContext { get; init; }

    /// <summary>
    /// 这个 Run 是否已被编排层放弃(看门狗强收、删除会话前强制收口:05 §1、§12)。放弃之后会话层的每一处写库
    /// (消息提交、压缩落库、孤儿修复经提交)和随之而来的扩展通知(<c>after_compact</c>、<c>run_end</c>、<c>run_settled</c>)一律跳过,
    /// 并记一条以 <see cref="SessionDiagnosticKeys.WriteSkippedAbandoned"/> 开头的诊断。null = 永不放弃(没有编排层的调用方)。
    /// 只会从 false 变成 true(单调):检查与写入之间的极短窗口里翻转,写入仍在会话锁之内,下一个 Run 照样得到 <c>session.busy</c> 而不是和它交错。
    /// </summary>
    public Func<bool>? IsAbandoned { get; init; }
}

/// <summary>Run 的结局(落库之后)。</summary>
public sealed record RunOutcome(
    RunId RunId,
    RunStatus Status,
    AgentError? Error,
    TokenUsage Usage,
    CostInfo Cost,
    int ToolCalls,
    /// <summary>false = 这个 Run 根本没有开始(例如会话里已经有另一个 Run 在跑):没有事件,也没有 <c>run_settled</c>。</summary>
    bool Started = true);

public static class SessionErrorKeys
{
    /// <summary>会话里已经有一个 Run 在跑(会话互斥)。</summary>
    public const string Busy = "session.busy";

    public const string NotFound = "session.notFound";

    /// <summary>输入加上预留输出超过上下文窗口:不发请求(协议窗口的硬校验)。</summary>
    public const string ContextTooLong = "context.tooLong";

    public const string Internal = "session.internal";

    /// <summary>输入被扩展拦下(例如 UserPromptSubmit Hook 阻断),原因在错误的英文诊断里。</summary>
    public const string InputBlocked = "session.inputBlocked";
}

/// <summary>会话层的诊断 key(只进诊断日志,不是界面文字;日志行形如 <c>key site=… run=… session=…</c>)。</summary>
public static class SessionDiagnosticKeys
{
    /// <summary>Run 已被放弃,会话层跳过了一次写库 / 通知(<c>site=</c> 指明是哪一处)。</summary>
    public const string WriteSkippedAbandoned = "session.write.skippedAbandoned";
}

/// <summary>会话层派生的工具上下文:在基础字段之上带着执行环境句柄(工具只经它访问文件和进程,AGENTS.md §5)和项目根。</summary>
public sealed record SessionToolContext : ToolContext
{
    public SessionToolContext(ToolContext baseContext, IExecutionEnvironment execution, EnvPath workspaceRoot)
        : base(baseContext)
    {
        Execution = execution;
        WorkspaceRoot = workspaceRoot;
    }

    public IExecutionEnvironment Execution { get; }

    public EnvPath WorkspaceRoot { get; }
}

/// <summary>每回合取一次工具快照的来源(Tools 层 / 注册表实现它)。外部名由来源分配,必须匹配 <c>^[a-zA-Z0-9_-]{1,64}$</c>。</summary>
public interface IToolSource
{
    IReadOnlyList<ToolBinding> Snapshot(ToolContext context);
}

/// <summary>固定的一组工具,外部名 = 内部 id。</summary>
public sealed class StaticToolSource(IEnumerable<IAgentTool> tools) : IToolSource
{
    private readonly IAgentTool[] _tools = [.. tools];

    public IReadOnlyList<ToolBinding> Snapshot(ToolContext context) =>
        [.. _tools.Where(t => t.IsEnabled(context)).Select(t => new ToolBinding(t.Info.InternalId, t))];
}

/// <summary>不上报进度的 <see cref="IProgress{T}"/>(组装工具快照时的占位上下文用)。</summary>
internal sealed class NullProgress : IProgress<ToolProgress>
{
    public static NullProgress Instance { get; } = new();

    public void Report(ToolProgress value)
    {
    }
}
