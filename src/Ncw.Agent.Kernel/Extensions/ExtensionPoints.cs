using Ncw.Agent.Llm;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Tools;

namespace Ncw.Agent.Kernel.Extensions;

// 扩展点的类型化标识(06 §1–2)。组合语义由<b>点的种类</b>决定,不由处理器自己决定:
//   ExtensionPoint       通知        所有处理器都跑,互不影响
//   MutatePoint          就地修改    处理器依次修改同一个可变对象
//   TransformPoint       链式变换    上一个的输出是下一个的输入
//   GatePoint            闸门        可以短路;放行需要所有人不反对
//   LastWinsPoint        最后者胜    每个处理器看到原值,按 Order 最后一个给出替换值的生效

/// <summary>通知(<c>run_end</c>、<c>after_compact</c> 等)。</summary>
public sealed record ExtensionPoint<TEvent>(string Name);

/// <summary>就地修改(<c>before_run</c>、<c>prepare_turn</c>)。</summary>
public sealed record MutatePoint<T>(string Name)
    where T : class;

/// <summary>链式变换(<c>input</c>、<c>after_tool</c>)。</summary>
public sealed record TransformPoint<T>(string Name);

/// <summary>
/// 闸门(<c>before_tool</c>、<c>turn_end</c>)。<paramref name="Default"/> 是没有任何处理器表态时的结果;
/// <paramref name="ShortCircuits"/> 为真的决定立即生效、后面的处理器不再运行(「第一个拒绝即短路」);
/// <paramref name="Merge"/> 把不短路的决定并进累计结果(缺省:忽略,保持 <paramref name="Default"/>)。
/// </summary>
public sealed record GatePoint<TEvent, TDecision>(
    string Name,
    TDecision Default,
    Func<TDecision, bool> ShortCircuits,
    Func<TDecision, TDecision, TDecision>? Merge = null);

/// <summary>最后者胜(<c>before_provider_request</c>)。处理器返回 null = 弃权。</summary>
public sealed record LastWinsPoint<T>(string Name)
    where T : class;

/// <summary>扩展处理器拿到的上下文(当前 Run 信息和日志)。</summary>
public sealed record ExtensionContext(RunId RunId, string SessionId, ILoopDiagnostics Log);

/// <summary>系统提示词分节回调拿到的会话信息(<c>system_sections</c>,06 §2)。只有事实,没有可变状态。</summary>
public sealed record SessionInfo(string SessionId, string WorkspaceId, int Depth, EnvironmentTag? Environment);

public interface IAgentExtension
{
    string Id { get; }

    /// <summary>同一个扩展点内的执行顺序,小的在前;权限扩展固定在最前。相同时按注册顺序。</summary>
    int Order { get; }

    void Register(IExtensionRegistry registry);
}

/// <summary>
/// 一方扩展贡献的斜杠命令(<c>RegisterCommand</c>,06 §1):和目录里的 <c>commands/&lt;name&gt;.md</c> 同一种东西——<b>一段提示词模板</b>,不是一个能执行的动作;
/// 展开(<c>$ARGUMENTS</c> / <c>$1–$9</c>)在客户端发送前完成。名字规则同命令文件名;与目录里的同名时目录里的(用户写的)胜出,内置的先于扩展的占位。
/// </summary>
public sealed record CommandDefinition(string Name, string Description, string Prompt, string? ArgumentHint = null);

public interface IExtensionRegistry
{
    void On<TEvent>(ExtensionPoint<TEvent> point, Func<TEvent, ExtensionContext, CancellationToken, ValueTask> handler);

    void Mutate<T>(MutatePoint<T> point, Func<T, ExtensionContext, CancellationToken, ValueTask> handler)
        where T : class;

    void Transform<T>(TransformPoint<T> point, Func<T, ExtensionContext, CancellationToken, ValueTask<T>> handler);

    /// <summary>
    /// <paramref name="onFailure"/>:处理器抛异常时代它表态。缺省(null)= 弃权(失败放行,与 Hook 的失败放行一致);
    /// 权限扩展传「拒绝」——它的异常按拒绝处理(06 §2)。
    /// </summary>
    void Gate<TEvent, TDecision>(
        GatePoint<TEvent, TDecision> point,
        Func<TEvent, ExtensionContext, CancellationToken, ValueTask<TDecision>> handler,
        Func<Exception, TDecision>? onFailure = null);

    void LastWins<T>(LastWinsPoint<T> point, Func<T, ExtensionContext, CancellationToken, ValueTask<T?>> handler)
        where T : class;

    /// <summary>注册一个工具(<c>tool_registry</c>,收集型)。外部名由会话层分配;同名的后注册者被拒绝并记诊断。</summary>
    void RegisterTool(Tools.IAgentTool tool);

    /// <summary>
    /// 给系统提示词贡献一节(<c>system_sections</c>,收集型):<paramref name="section"/> 返回 null / 空串 = 这一节不出现。
    /// 节按 (<see cref="IAgentExtension.Order"/>, 注册顺序) 排在内置节之后、工具段之前。运行级常量才应该放在这里(放易变内容会破坏缓存前缀)。
    /// </summary>
    void ContributeSystemSection(string name, Func<SessionInfo, string?> section);

    /// <summary>
    /// 注册一条斜杠命令(<c>commands.*</c> 的清单里会出现,来源标为扩展)。默认实现什么也不做,所以不关心命令的注册表实现(测试里的假注册表)不用改;
    /// <see cref="ExtensionHost"/> 收集到 <see cref="ExtensionHost.RegisteredCommands"/>。
    /// </summary>
    void RegisterCommand(CommandDefinition command)
    {
    }
}

/// <summary>循环层用到的扩展点(其余扩展点属于会话层 / 编排层,随它们定义)。</summary>
public static class LoopExtensionPoints
{
    /// <summary><c>prepare_turn</c>:就地修改本回合的草稿(压缩、工具快照过滤、状态块)。</summary>
    public static MutatePoint<TurnDraft> PrepareTurn { get; } = new("prepare_turn");

    /// <summary><c>before_provider_request</c>:最后者胜,仅一方扩展。</summary>
    public static LastWinsPoint<LlmRequest> BeforeProviderRequest { get; } = new("before_provider_request");

    /// <summary>
    /// <c>before_tool</c>:第一个拒绝即短路;放行需要所有人不反对;改写参数的决定取最后一个。
    /// </summary>
    public static GatePoint<ToolInvocation, PreflightDecision> BeforeTool { get; } = new(
        "before_tool",
        PreflightDecision.Allow,
        static d => d is PreflightDecision.DenyDecision,
        static (acc, next) => next is PreflightDecision.EditDecision ? next : acc);

    /// <summary><c>after_tool</c>:链式变换,只允许追加和翻转 IsError(Loop 会校验)。</summary>
    public static TransformPoint<AfterToolPayload> AfterTool { get; } = new("after_tool");

    /// <summary><c>turn_end</c>:任一要求继续(或立即结束)则生效,按 Order 取第一个。</summary>
    public static GatePoint<TurnEnd, TurnEndDecision> TurnEnd { get; } = new(
        "turn_end",
        TurnEndDecision.Stop,
        static d => d.Action != TurnEndAction.Stop);

    /// <summary><c>run_end</c>:通知(后台报告、<c>SubagentStop</c> Hook、目标暂停)。由编排层在落库之后触发。</summary>
    public static ExtensionPoint<RunEndEvent> RunEnd { get; } = new("run_end");
}

public sealed record AfterToolPayload(ToolInvocation Invocation, ToolResult Result);

/// <summary>
/// <c>prepare_turn</c> 的可变草稿:扩展处理器就地修改。<see cref="Stop"/> 之后 <see cref="Request"/> 为 null。
/// </summary>
public sealed class TurnDraft(TurnContext context, TurnPlan baseline)
{
    public TurnContext Context { get; } = context;

    public LlmRequest? Request { get; set; } = baseline.Request;

    public ToolSnapshot Tools { get; set; } = baseline.Tools;

    public List<AgentEvent> Events { get; } = [.. baseline.Events];

    public RunStatus StopStatus { get; private set; } = baseline.StopStatus;

    public AgentError? StopError { get; private set; } = baseline.StopError;

    public void Stop(RunStatus status = RunStatus.Done, AgentError? error = null)
    {
        Request = null;
        StopStatus = status;
        StopError = error;
    }

    public TurnPlan ToPlan() => Request is null ? TurnPlan.Stop(StopStatus, StopError, Events) : TurnPlan.Run(Request, Tools, Events);
}
