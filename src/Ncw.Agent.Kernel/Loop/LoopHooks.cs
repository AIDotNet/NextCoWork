using System.Text.Json;
using Ncw.Agent.Llm;
using Ncw.Agent.Kernel.Tools;

namespace Ncw.Agent.Kernel.Loop;

/// <summary>
/// 一次 Run 的输入(02 §1)。<see cref="Context"/> 是已经过 messagesForModel 的上下文(从最后一个压缩边界开始),
/// <see cref="NewMessages"/> 是本次 Run 的输入(可以为空,表示「继续」),它们的 id 由调用方提供(用户消息 id 由客户端给)。
/// </summary>
public sealed record LoopInput(
    RunId RunId,
    string SessionId,
    string WorkspaceId,
    IReadOnlyList<Message> Context,
    IReadOnlyList<Message> NewMessages,
    ModelRef Model,
    GenerationOptions Options,
    EnvironmentTag? Environment = null,
    int Depth = 0);

/// <summary>循环层的可调参数。默认值是 2.x 的值。</summary>
public sealed class AgentLoopOptions
{
    /// <summary>流中断续传的退避表(2.x <c>RESUME_DELAYS_MS</c>)。长度 = 最多续传几次。空 = 不续传。</summary>
    public IReadOnlyList<TimeSpan> ResumeDelays { get; init; } =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(45),
    ];

    /// <summary>工具输出的字符上限(2.x <c>MAX_TOOL_OUTPUT_CHARS</c> = 64 KiB 个字符),超过且工具没有自己截断时由 Loop 截断并标记。</summary>
    public int MaxToolOutputChars { get; init; } = 64 * 1024;
}

/// <summary>诊断日志接口(循环层不依赖任何日志库)。</summary>
public interface ILoopDiagnostics
{
    void Warn(string message, Exception? error = null);
}

public sealed class NullLoopDiagnostics : ILoopDiagnostics
{
    public static NullLoopDiagnostics Instance { get; } = new();

    public void Warn(string message, Exception? error = null)
    {
    }
}

/// <summary>
/// 一个回合开始前交给 <c>PrepareTurn</c> 的信息。<see cref="Transcript"/> 是此刻的快照(上下文 + 本 Run 已提交的所有消息)。
/// <see cref="LastUsage"/> 是本 Run 里<b>上一次完成的上游请求</b>回报的真实用量,<see cref="LastUsageAnchor"/> 是发出那次请求时转录的最后一条消息的 id
/// (会话层据此算「真值 + 其后新增部分的估算」,2.x 的 <c>usageAnchor</c>);本 Run 还没有完成过请求时两者都是 null。
/// </summary>
public sealed record TurnContext(
    RunId RunId,
    string SessionId,
    int TurnIndex,
    IReadOnlyList<Message> Transcript,
    ModelRef Model,
    GenerationOptions Options,
    int ToolCallsThisRun,
    TokenUsage? LastUsage = null,
    MessageId? LastUsageAnchor = null);

/// <summary>
/// 回合计划:要发的请求 + 本回合的工具快照,或者「到此为止」。
/// 请求里的工具列表<b>总是以 <see cref="Tools"/> 为准</b>(Loop 会覆盖):同一个快照既告诉模型有哪些工具,也用来按名字找回工具。
/// </summary>
public sealed record TurnPlan
{
    private TurnPlan()
    {
    }

    public LlmRequest? Request { get; private init; }

    public ToolSnapshot Tools { get; private init; } = ToolSnapshot.Empty;

    public bool IsStop => Request is null;

    public RunStatus StopStatus { get; private init; } = RunStatus.Done;

    public AgentError? StopError { get; private init; }

    /// <summary>请求发出之前要写进事件流的事件(<c>context_usage</c>、<c>context_status</c> 等,由会话层产出)。</summary>
    public IReadOnlyList<AgentEvent> Events { get; private init; } = [];

    public static TurnPlan Run(LlmRequest request, ToolSnapshot? tools = null, IReadOnlyList<AgentEvent>? events = null) =>
        new() { Request = request, Tools = tools ?? ToolSnapshot.Empty, Events = events ?? [] };

    public static TurnPlan Stop(RunStatus status = RunStatus.Done, AgentError? error = null, IReadOnlyList<AgentEvent>? events = null) =>
        new() { StopStatus = status, StopError = error, Events = events ?? [] };
}

public abstract record PreflightDecision
{
    private PreflightDecision()
    {
    }

    public static PreflightDecision Allow { get; } = new AllowDecision();

    public static PreflightDecision Deny(string? reason = null) => new DenyDecision(reason);

    public static PreflightDecision Edit(JsonElement input) => new EditDecision(input);

    public sealed record AllowDecision : PreflightDecision;

    /// <summary>拒绝:理由让模型<b>看见</b>(系统提示词里写了「被拒绝时不要试图绕开」,前提是它知道自己被拒了)。不影响同批其他调用。</summary>
    public sealed record DenyDecision(string? Reason) : PreflightDecision;

    /// <summary>改写参数(用户在审批卡片上改过)。改写后的参数会重新过一遍 <see cref="IAgentTool.PrepareArguments"/>。</summary>
    public sealed record EditDecision(JsonElement Input) : PreflightDecision;
}

/// <summary>
/// 模型不再调用工具、本来要停的时候交给 <c>OnTurnEnd</c> 的信息。
/// <see cref="StoppedTurnStreak"/> = 连续「什么都没做就想停」被要求继续的回合数,一旦执行了工具就清零;
/// <see cref="StopHookActive"/> = 本 Run 里 <c>OnTurnEnd</c> 已经被调用过(防止 Stop Hook 无限循环,2.x ★)。
/// </summary>
public sealed record TurnEnd(
    RunId RunId,
    string SessionId,
    int TurnIndex,
    Message Assistant,
    StopReason? Stop,
    IReadOnlyList<Message> Transcript,
    int ToolCallsThisRun,
    int StoppedTurnStreak,
    bool StopHookActive);

public enum TurnEndAction
{
    /// <summary>正常结束(但若此刻队列里又有引导消息,仍然会继续)。</summary>
    Stop,

    /// <summary>立即结束,不再检查引导消息(它们留在队列里)。</summary>
    Finish,

    /// <summary>再跑一个回合,先提交 <see cref="TurnEndDecision.InternalMessage"/>。没有非空的内部消息就退化成 <see cref="Stop"/>(否则会原样重发同一个请求)。</summary>
    Continue,
}

public sealed record TurnEndDecision(TurnEndAction Action = TurnEndAction.Stop, Message? InternalMessage = null, LoopNotice? Notice = null)
{
    public static TurnEndDecision Stop { get; } = new();

    public static TurnEndDecision Finish { get; } = new(TurnEndAction.Finish);

    public static TurnEndDecision Continue(Message internalMessage, LoopNotice? notice = null) => new(TurnEndAction.Continue, internalMessage, notice);
}

public sealed record LoopNotice(string Key, string? Detail = null);

/// <summary>
/// 循环层的钩子(扩展点的循环层子集,02 §1)。<b>只有 <see cref="PrepareTurn"/> 必填</b>,其余缺省为空操作
/// (设计原稿全部必填;缺省值让「假上游 + 假工具」的单测和小场景不必写一堆样板)。
/// 钩子的异常语义见 <c>AgentLoop</c>:<c>PrepareTurn</c>、<c>TakeSteering</c>、<c>CommitMessage</c> 的异常使 Run 以错误结束;
/// <c>BeforeTool</c> 的异常按拒绝处理(失败即关闭);<c>AfterTool</c> 的异常被吞掉;<c>OnTurnEnd</c> 的异常被吞掉(否则循环会无限重试,2.x ★)。
/// 取消异常(令牌已触发)一律原样传播。
/// </summary>
public sealed class LoopHooks
{
    /// <summary>每个回合开始前:压缩、刷新工具快照、装配请求。返回本回合的请求(或要求结束)。</summary>
    public required Func<TurnContext, CancellationToken, ValueTask<TurnPlan>> PrepareTurn { get; init; }

    /// <summary>可选:发出前最后一次改写请求(<c>before_provider_request</c>,最后者胜,仅一方扩展)。</summary>
    public Func<LlmRequest, CancellationToken, ValueTask<LlmRequest>>? BeforeProviderRequest { get; init; }

    /// <summary>工具预检:参数校验之后、执行之前。可以放行、拒绝(带原因)、改写参数。权限扩展挂在这里;可能等待用户回答。</summary>
    public Func<ToolInvocation, CancellationToken, ValueTask<PreflightDecision>> BeforeTool { get; init; } =
        static (_, _) => ValueTask.FromResult(PreflightDecision.Allow);

    /// <summary>
    /// 工具执行之后:<b>只能追加内容、翻转 <c>IsError</c></b>(2.x PostToolUse 规则:界面、记录、模型看到的必须是同一份)。
    /// Loop 会校验返回值:不是「原内容 + 追加」的内容改写被忽略;<c>IsError</c> 只会 false → true;<c>Details</c> 和 <c>Terminate</c> 不能改。
    /// </summary>
    public Func<ToolInvocation, ToolResult, CancellationToken, ValueTask<ToolResult>> AfterTool { get; init; } =
        static (_, result, _) => ValueTask.FromResult(result);

    /// <summary>模型不再调用工具、本来要停的时候:可以要求继续(带一条内部消息)。</summary>
    public Func<TurnEnd, CancellationToken, ValueTask<TurnEndDecision>> OnTurnEnd { get; init; } =
        static (_, _) => ValueTask.FromResult(TurnEndDecision.Stop);

    /// <summary>回合边界取引导消息。<b>取走即消费</b>;消息 id 等于队列项 id;空消息(没有任何 Part)被丢弃。</summary>
    public Func<IReadOnlyList<Message>> TakeSteering { get; init; } = static () => [];

    /// <summary>每条消息完成时(助手消息、工具结果、引导消息、内部消息、用户输入):会话层在这里落库。<b>落库不可中断</b>:Loop 总是传 <see cref="CancellationToken.None"/>。</summary>
    public Func<Message, CancellationToken, ValueTask> CommitMessage { get; init; } = static (_, _) => ValueTask.CompletedTask;

    /// <summary>
    /// 可选:上游以「提示词太长」(<see cref="AgentErrorCode.ContextLength"/>)拒绝、而且本回合<b>一个内容字节都没有</b>时调用(2.x 的被动压缩)。
    /// 返回 true = 已经处理了(会话层压缩了上下文),这个回合丢弃、不提交错误,重新 <c>PrepareTurn</c> 再试;返回 false 或抛异常 = 照常以错误结束。
    /// <b>每个回合最多问一次</b>(一个回合正常结束后复位):压完还被拒说明压缩救不了,不能「压缩 → 仍超 → 再压缩」循环到天荒地老。
    /// </summary>
    public Func<TurnContext, CancellationToken, ValueTask<bool>>? RecoverContextLength { get; init; }

    /// <summary>可选:把循环层造的基础 <see cref="ToolContext"/> 换成会话层的派生类型(带执行环境、权限档位快照等能力)。</summary>
    public Func<ToolContext, ToolContext>? EnrichToolContext { get; init; }
}
