using Ncw.Agent.Kernel.Extensions;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;

namespace Ncw.Agent.Kernel.Session;

/// <summary><c>input</c>(链式变换)的载荷:本次 Run 的输入消息。扩展可以改写(命令展开校验、<c>UserPromptSubmit</c> Hook)。</summary>
public sealed record InputPayload(IReadOnlyList<Message> Messages)
{
    /// <summary>子代理深度(0 = 顶层 Run;会话层填)。「用户提交了提示词」这类只对顶层 Run 有意义的处理器据此跳过子 Run。</summary>
    public int Depth { get; init; }

    /// <summary>
    /// 非 null = 扩展拦下了这次输入(<c>UserPromptSubmit</c> Hook 的阻断):Run 以错误结束,输入消息<b>不</b>落库(2.x:被拦下的提示词不进会话)。
    /// 值是给人看的原因(英文 / 脚本原文),进 <c>AgentError.Message</c>;<see cref="BlockedKey"/> 是控制端翻译用的稳定 key。
    /// </summary>
    public string? BlockedReason { get; init; }

    public string? BlockedKey { get; init; }

    /// <summary>扩展给的上下文(Hook 的 <c>additionalContext</c>):进发出去的请求的头块,<b>不进记录</b>(会话层把它们并进 <see cref="RunDraft.HeadSections"/>)。</summary>
    public IReadOnlyList<string> HeadSections { get; init; } = [];
}

/// <summary><c>before_run</c>(就地修改)的草稿:计划模式(工具允许列表、模式提示词)、子代理(权限收窄)、状态段(待办、计划文件)在这里改本次 Run 的参数。</summary>
public sealed class RunDraft(RunRequest request)
{
    public RunRequest Request { get; } = request;

    public string? AgentPrompt { get; set; } = request.AgentPrompt;

    public string? ModePrompt { get; set; } = request.ModePrompt;

    /// <summary>头块的附加段(Hook 和插件给的上下文):静态的,保住缓存前缀。</summary>
    public List<string> HeadSections { get; } = [];

    /// <summary>尾块的状态段(待办、计划文件):每个 Run 算一次。</summary>
    public List<string> StateSections { get; } = [];

    /// <summary>收窄本回合的工具快照。只过滤、不新增,所以永远不可能提权。</summary>
    public Func<ToolSpec, bool>? ToolFilter { get; set; }
}

public sealed record SessionStartEvent(string SessionId, string WorkspaceId);

/// <summary><c>before_compact</c> 的载荷。</summary>
public sealed record CompactGateInput(string SessionId, string Trigger, string? Instructions);

/// <summary><c>before_compact</c> 闸门的决定:放行(缺省)、取消(带英文诊断)、或给一份现成的摘要(不调模型)。</summary>
public abstract record CompactGateDecision
{
    private CompactGateDecision()
    {
    }

    public static CompactGateDecision Proceed { get; } = new ProceedDecision();

    public static CompactGateDecision Cancel(string reason) => new CancelDecision(reason);

    public static CompactGateDecision Custom(string summary) => new CustomDecision(summary);

    public sealed record ProceedDecision : CompactGateDecision;

    public sealed record CancelDecision(string Reason) : CompactGateDecision;

    public sealed record CustomDecision(string Summary) : CompactGateDecision;
}

/// <summary><c>compact_attachments</c> 的草稿(在压缩调用之前收集):扩展给压缩后附件(最新待办、本会话调用过的 Skill 正文),每段一个独立文字块。</summary>
public sealed class CompactAttachmentDraft(string sessionId, IReadOnlyList<Message> history)
{
    public string SessionId { get; } = sessionId;

    /// <summary>被压缩的那一段(最后一个边界之后的历史)。</summary>
    public IReadOnlyList<Message> History { get; } = history;

    public List<string> Texts { get; } = [];
}

/// <summary><c>after_compact</c>(通知)的载荷。</summary>
public sealed record AfterCompactEvent(string SessionId, Message Boundary, CompactBoundaryPart Part);

/// <summary>会话层用到的扩展点(06 §2);循环层的点在 <see cref="LoopExtensionPoints"/>。</summary>
public static class SessionExtensionPoints
{
    public static ExtensionPoint<SessionStartEvent> SessionStart { get; } = new("session_start");

    /// <summary><c>input</c>:链式变换。</summary>
    public static TransformPoint<InputPayload> Input { get; } = new("input");

    /// <summary><c>before_run</c>:就地修改运行参数。</summary>
    public static MutatePoint<RunDraft> BeforeRun { get; } = new("before_run");

    /// <summary><c>before_compact</c>:闸门,可以取消或提供自定义摘要。第一个不放行的决定生效。</summary>
    public static GatePoint<CompactGateInput, CompactGateDecision> BeforeCompact { get; } = new(
        "before_compact",
        CompactGateDecision.Proceed,
        static d => d is not CompactGateDecision.ProceedDecision);

    /// <summary><c>compact_attachments</c>:就地修改(06 里 after_compact 是通知,这里拆成两个点:要贡献附件就得赶在边界消息生成之前)。</summary>
    public static MutatePoint<CompactAttachmentDraft> CompactAttachments { get; } = new("compact_attachments");

    /// <summary><c>after_compact</c>:通知(边界已落库)。</summary>
    public static ExtensionPoint<AfterCompactEvent> AfterCompact { get; } = new("after_compact");

    /// <summary><c>run_settled</c>:通知。落库和释放会话锁之后才触发。</summary>
    public static ExtensionPoint<RunSettledEvent> RunSettled { get; } = new("run_settled");
}
