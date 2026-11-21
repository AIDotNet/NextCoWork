using Ncw.Agent.Catalog;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Llm;

namespace Ncw.Agent.Kernel.Subagents;

/// <summary>子代理的固定上限。</summary>
public static class SubagentLimits
{
    /// <summary>嵌套深度上限:子代理再派子代理最多两层(<c>depth &lt; 2</c> 才能派)。</summary>
    public const int MaxDepth = 2;

    /// <summary>前台 / 后台报告在卡片上的那截预览长度。与 <c>subagent_end.summary</c> 同一个口径。</summary>
    public const int SummaryChars = 240;
}

/// <summary>子代理在独立 git worktree 结束后若有未清理的改动或提交,交回给父代理的路径与分支。</summary>
public sealed record WorktreeInfo(string Path, string Branch);

/// <summary>派一个子代理要说清的事。<see cref="ParentRunId"/> 是派发它的那个 Run(<c>ToolContext.RunId</c>),<see cref="CallId"/> 是 <c>Task</c> 调用的 id(事件配对用)。</summary>
public sealed record SubagentRequest(RunId ParentRunId, string CallId, string AgentType, string Prompt, string Description, bool Background = false)
{
    /// <summary>显式指定是否后台运行;null = 按 <see cref="Background"/> 或定义的 <see cref="AgentDefinition.DefaultBackground"/> 决定。</summary>
    public bool? RunInBackground { get; init; }

    /// <summary>恢复已有子会话的会话 id(<c>resume</c>),保留其 SQLite 转录继续对话。</summary>
    public string? Resume { get; init; }

    /// <summary>单次调用的最大回合数(与定义上的 <see cref="AgentDefinition.MaxTurns"/> 取较小值)。</summary>
    public int? MaxTurns { get; init; }

    /// <summary>单次调用覆盖的模型别名。</summary>
    public string? Model { get; init; }

    /// <summary>单次调用覆盖的隔离策略(仅可从 <see cref="AgentIsolation.None"/> 收窄为 <see cref="AgentIsolation.Worktree"/>)。</summary>
    public AgentIsolation? Isolation { get; init; }
}

/// <summary>派发的结局(2.x 的 <c>SubagentOutcome</c>)。三种结局给出<b>三种不同的东西</b>,因为模型下一步该做的事完全不同(换个名字 / 换个 prompt / 什么都别做)。</summary>
public abstract record SubagentOutcome
{
    private SubagentOutcome()
    {
    }

    /// <summary>还没派出去就被拒(没有这个名字、深度到顶、并发禁用……)。<see cref="Reason"/> 是一段完整的人话,<b>原样</b>转给模型,不加任何前缀。</summary>
    public sealed record Refused(string Reason) : SubagentOutcome;

    /// <summary>后台:立即返回。<see cref="Queued"/> 区分「已排队」和「已开始」;<see cref="Detached"/> = 前台派出、被用户转去了后台。</summary>
    public sealed record Background(RunId ChildRunId, bool Queued = false, bool Detached = false, string? Color = null, WorktreeInfo? Worktree = null) : SubagentOutcome;

    /// <summary>前台:子 Run 走完了(<see cref="Status"/> 是终态)。<see cref="Text"/> 是子会话最后一条助手消息的文字。</summary>
    public sealed record Finished(RunId ChildRunId, RunStatus Status, string Text, string? Color = null, AgentError? Error = null, TokenUsage? Usage = null, WorktreeInfo? Worktree = null) : SubagentOutcome;
}

/// <summary>
/// <c>Task</c> 工具调用的那条窄缝(2.x 的 <c>ctx.spawnSubagent</c>)。工具本身不碰存储、不碰 Run 注册表,只调它;建 Run、继承订阅、发 <c>subagent_*</c>、取子代理的产出全在 <see cref="SubagentService"/> 里,
/// 因为只有它同时握着父 Run 的句柄和子 Run 的 id。
/// <para>
/// 为什么不是 <c>ToolContext</c> 的派生类型:<c>SessionToolContext</c>、<c>ExecutionToolContext</c> 都是「换一个子类型」的写法,两个兄弟子类型套在一起会丢掉彼此的能力
/// (<c>base(inner)</c> 只拷贝 <c>ToolContext</c> 自己的字段)。所以子代理能力经注入的接口提供,不经上下文套娃。
/// </para>
/// </summary>
public interface ISubagentLauncher
{
    /// <summary>
    /// 派发。前台:等到子 Run 结束才返回(父 Run 的取消令牌触发 = 抛 <see cref="OperationCanceledException"/>);后台:立即返回。被拒返回 <see cref="SubagentOutcome.Refused"/>,不抛。
    /// </summary>
    Task<SubagentOutcome> SpawnAsync(SubagentRequest request, CancellationToken ct);
}

/// <summary>设置里与子代理有关的几项(2.x 的 <c>settings.subagent</c>)。每次派发、每个队列 tick 都现读一次。</summary>
public sealed record SubagentSettings
{
    /// <summary>每个父 Run 的并发上限(默认 4)。</summary>
    public int PerSessionLimit { get; init; } = SlotCapacity.DefaultLimit;

    /// <summary>全局并发上限(默认 4;<b>0 = 完全禁用子代理</b>)。</summary>
    public int GlobalLimit { get; init; } = SlotCapacity.DefaultLimit;

    /// <summary>「默认子代理」模型。null / 空别名 = 跟随父 Run。</summary>
    public ModelSelection? Model { get; init; }

    public SubagentThinking Thinking { get; init; } = SubagentThinking.Inherit;

    public SlotCapacity Capacity => SlotCapacity.Normalize(PerSessionLimit, GlobalLimit);
}

public interface ISubagentSettingsSource
{
    SubagentSettings Current();
}

public sealed class FixedSubagentSettings(SubagentSettings? settings = null) : ISubagentSettingsSource
{
    public SubagentSettings Value { get; set; } = settings ?? new SubagentSettings();

    public SubagentSettings Current() => Value;
}

/// <summary>
/// 模型相关的两个校验(Host 用 <c>IModelResolver</c> 适配;子代理层够不着路由器)。
/// <para>
/// <see cref="Exists"/>:设置里那个「默认子代理」当前还指得到一条绑定吗。这道校验不能省:别名是个字符串,而供应商随时会被删、别名随时会被改名——悬空时的表现不是报错,
/// 是子代理在路由器里拿到空候选集然后整条失败,而父代理只会转述一句「子代理失败了」,用户无从知道是这一栏的锅。查不到就让它落到下一档(父 Run)——这一栏是偏好,不是硬约束。
/// </para>
/// <see cref="NormalizeThinking"/>:档位是模型的属性,子 Run 的模型未必和父 Run 是同一个。不归一化的话,模型不认这个档位时,适配器要到<b>发请求那一刻</b>才抛,
/// 于是每一次派发全数失败。归一化是<b>静默降级</b>(档位是偏好,不是硬约束)。
/// </summary>
public interface ISubagentModels
{
    bool Exists(ModelSelection selection);

    ThinkingLevel NormalizeThinking(ThinkingLevel wanted, ModelSelection selection);
}

/// <summary>不校验、不归一化(没有路由器的场合:测试、纯内核)。</summary>
public sealed class PassThroughSubagentModels : ISubagentModels
{
    public static PassThroughSubagentModels Instance { get; } = new();

    public bool Exists(ModelSelection selection) => true;

    public ThinkingLevel NormalizeThinking(ThinkingLevel wanted, ModelSelection selection) => wanted;
}
