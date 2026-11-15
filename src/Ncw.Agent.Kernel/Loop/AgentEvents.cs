using System.Text.Json;
using Ncw.Agent.Llm;
using Ncw.Agent.Kernel.Tools;

namespace Ncw.Agent.Kernel.Loop;

/// <summary>
/// 循环层事件(02 §3)。三层:run / turn / message,外加工具的 start / progress / end。
/// 不是 sealed 层级:会话层和编排层往同一条流里写自己的事件(<c>context_usage</c>、<c>interaction_request</c>、<c>subagent_*</c> 等)。
/// 序号 <c>seq</c> 由编排层分配,循环层不管。
/// </summary>
public abstract record AgentEvent;

public enum RunStatus
{
    Done,
    Error,
    Aborted,
}

public sealed record RunStartEvent(RunId RunId, string SessionId, ModelRef Model) : AgentEvent;

public sealed record TurnStartEvent(int TurnIndex) : AgentEvent;

/// <summary>
/// 每条消息都有 start / end(用户、引导、内部、工具结果消息也是)。
/// 助手消息在<b>第一个内容事件到来时</b>才发 start(只有 <c>MessageStart</c> 没有内容的回合不产生任何消息事件)。
/// </summary>
public sealed record MessageStartEvent(MessageId MessageId, Role Role, string? Model = null, string? ProviderId = null) : AgentEvent;

public enum MessageDeltaKind
{
    Text,
    Thinking,
    ToolArgs,
}

/// <summary>
/// 只推增量(R7),按 <see cref="ContentIndex"/> 归位。<see cref="ToolArgs"/> 的第一条带 <see cref="ToolName"/>(增量为空串),
/// 让界面在参数还在流式输出时就能画出工具卡片。
/// </summary>
public sealed record MessageDeltaEvent(
    MessageId MessageId,
    int ContentIndex,
    MessageDeltaKind Kind,
    string Delta,
    string? CallId = null,
    string? ToolName = null) : AgentEvent;

/// <summary>
/// 完整消息 + 用量 + 费用 + 停止原因。<b>唯一的落库点</b>:循环层先调 <c>CommitMessage</c>,成功后才发它;客户端用完整消息替换累积的增量。
/// </summary>
public sealed record MessageEndEvent(Message Message, TokenUsage? Usage = null, CostInfo Cost = default, StopReason? Stop = null) : AgentEvent
{
    public MessageId MessageId => Message.Id;
}

/// <summary>
/// 已发过 start 的助手消息最后没有提交(流中断后丢弃重来、或者什么内容都没有):客户端丢掉对应的增量。
/// 保证「每个 <see cref="MessageStartEvent"/> 都以 <see cref="MessageEndEvent"/> 或本事件收尾」。
/// </summary>
public sealed record MessageDiscardEvent(MessageId MessageId) : AgentEvent;

/// <summary>来自模型层。流中断续传(循环层)复用同一个事件:要说的话一样(「在等,因为上游出了问题」)。</summary>
public sealed record ProviderRetryEvent(int Attempt, int DelayMs, string Reason) : AgentEvent;

public sealed record ProviderSwitchEvent(string From, string To, string Reason) : AgentEvent;

public sealed record ToolStartEvent(string CallId, string ToolName, JsonElement Input, EnvironmentTag? Environment) : AgentEvent;

/// <summary>
/// 「关于某次工具调用」的事件(进度、子代理的起止 / 更新)。它们可能由工具线程<b>直接</b>投进 Run 的事件流(<c>RunRegistry.Post</c>),
/// 而循环自己的事件(包括这次调用的 <see cref="ToolStartEvent"/>)要经过循环的事件通道,所以前者可能先到。
/// <c>RunHandle</c> 保证这类事件永远排在对应的 <see cref="ToolStartEvent"/> 之后(先到的暂存,<c>tool_start</c> 一到就紧跟着落下);
/// 控制端因此可以假设「收到某个 CallId 的进度时,这张工具卡片已经存在」。
/// </summary>
public interface IToolCallScopedEvent
{
    string CallId { get; }
}

/// <summary>易失,不写进记录;<see cref="ToolEndEvent"/> 之后日志里清掉(排队的排位除外,见 <c>RunEventLog</c>)。</summary>
public sealed record ToolProgressEvent(string CallId, ToolProgress Progress) : AgentEvent, IToolCallScopedEvent;

/// <summary>失败也必须发,否则界面卡片一直转。每个 <see cref="ToolStartEvent"/> 恰好对应一个。</summary>
public sealed record ToolEndEvent(string CallId, ToolOutput Output, bool IsError, long DurationMs) : AgentEvent;

/// <summary>结构化通知。本层不产出界面文字:<see cref="Key"/> 由控制端翻译,<see cref="Detail"/> 是英文诊断。</summary>
public sealed record NotificationEvent(string Key, string? Detail = null) : AgentEvent;

public sealed record TurnEndEvent(int TurnIndex) : AgentEvent;

/// <summary>终态。此时<b>可能还没落库完</b>(落库由会话层在它之后收尾,<c>run_settled</c> 才是真的结束)。每个 Run 恰好一个,且是循环层的最后一个事件。</summary>
public sealed record RunEndEvent(RunStatus Status, AgentError? Error = null) : AgentEvent;
