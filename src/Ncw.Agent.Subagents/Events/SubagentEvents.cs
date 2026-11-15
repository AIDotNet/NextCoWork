using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Llm;
using Ncw.Core.Domain;

namespace Ncw.Agent.Kernel.Subagents;

/// <summary>子代理此刻在干什么(2.x 的 <c>SubagentPhase</c>)。<c>Starting</c> / <c>Background</c> 只出现在从库里回执重建的卡片上。</summary>
public enum SubagentPhase
{
    Starting,
    Background,
    Tool,
    Thinking,
    Finishing,
}

/// <summary>子代理正在退避重试 / 刚切了供应商(镜像自子 Run 的 <see cref="ProviderRetryEvent"/> / <see cref="ProviderSwitchEvent"/>)。</summary>
public sealed record SubagentNotice(string Kind, string Reason, int? Attempt = null, string? To = null)
{
    public const string Retry = "retry";

    public const string Switch = "switch";
}

public sealed record SubagentContextUsage(int Used, int Window, bool ShouldCompact);

/// <summary>
/// 父 Run 上的 <c>subagent_start</c>(05 §5):<b>子会话 id 必须由 Agent 带过来</b>(<see cref="ChildSessionId"/>),客户端不许拼——
/// 子 Run id 是递归拼出来的(<c>&lt;父 RunId&gt;:sub:N</c>),想从字符串里反推父亲会切出爷爷;权威在库里的 <c>parent_session_id</c> 那一列。
/// 排队中的子代理<b>还没有</b>这个事件(名额到手才发,排队期间走 <c>tool_progress</c>)。
/// </summary>
public sealed record SubagentStartEvent(
    string CallId,
    RunId ChildRunId,
    string ChildSessionId,
    string AgentType,
    string Description,
    string? Color,
    string Model,
    bool Background,
    DateTimeOffset At) : AgentEvent, IToolCallScopedEvent;

/// <summary>
/// 子 Run 的工具开始 / 结束、上下文用量、消息结束,在<b>父 Run 仍在运行</b>时镜像成 <c>subagent_update</c>,带 <see cref="ChildSeq"/> 去重(控制端订阅了整棵 Run 树,会同时收到原始子事件)。
/// 字段语义(<see cref="Patch{T}"/>):键存在但值为空 = 清除;键不存在 = 保持。
/// </summary>
public sealed record SubagentUpdateEvent(string CallId, RunId ChildRunId) : AgentEvent, IToolCallScopedEvent
{
    public SubagentPhase? Phase { get; init; }

    public Patch<string> CurrentTool { get; init; }

    public Patch<string> CurrentTarget { get; init; }

    public int? ToolCalls { get; init; }

    public int? ToolErrors { get; init; }

    public TokenUsage? Usage { get; init; }

    public SubagentContextUsage? ContextUsage { get; init; }

    public Patch<SubagentNotice> Notice { get; init; }

    public long? ChildSeq { get; init; }

    public DateTimeOffset? At { get; init; }
}

/// <summary>父 Run 上的 <c>subagent_end</c>。<see cref="Summary"/> 是前 240 字的预览(全文在子会话的最后一条助手消息里)。</summary>
public sealed record SubagentEndEvent(string CallId, RunId ChildRunId, RunStatus Status) : AgentEvent, IToolCallScopedEvent
{
    public string? Summary { get; init; }

    public AgentError? Error { get; init; }

    public long? ChildSeq { get; init; }

    public DateTimeOffset? At { get; init; }
}
