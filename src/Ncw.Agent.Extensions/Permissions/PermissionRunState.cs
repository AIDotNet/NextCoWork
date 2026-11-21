using System.Collections.Concurrent;
using Ncw.Agent.Environments;
using Ncw.Agent.Extensions.Interactions;
using Ncw.Agent.Kernel;
using Ncw.Agent.Llm;
using Ncw.Agent.Tools;

namespace Ncw.Agent.Extensions.Permissions;

/// <summary>
/// 权限扩展对「一个 Run」需要知道的一切。由会话层在 Run 开始时造、注册进 <see cref="PermissionRuns"/>、<c>run_end</c> 时由扩展自己摘掉。
/// 权限扩展挂在 <c>before_tool</c> 上,而那个扩展点只给它 <c>RunId</c> 和调用本身——档位、执行环境、项目根、能否问人这些 Run 级事实经由这里取。
/// <para>
/// <b>档位是实时值</b>(<see cref="Mode"/>:用户在 Run 还没结束时调宽,下一个工具调用就生效);<see cref="StartMode"/> 是 Run 开始时的快照(给工具上下文用)。
/// <b>子代理的档位取 min(父 Run 的实时值, Agent 定义的值)</b>(<see cref="ForSubagent"/>),所以父 Run 中途调低,子代理立刻跟着降。
/// </para>
/// </summary>
public sealed class PermissionRunState
{
    private readonly PermissionRunState? _parent;
    private readonly PermissionMode? _cap;
    private int _mode;
    private int _webSearch;
    private EnvPath? _planFence;
    private EnvPath? _realRoot;

    public PermissionRunState(RunId runId, string sessionId, IExecutionEnvironment environment, RunInteractions interactions, PermissionMode mode)
    {
        RunId = runId;
        SessionId = sessionId;
        Environment = environment ?? throw new ArgumentNullException(nameof(environment));
        Interactions = interactions ?? throw new ArgumentNullException(nameof(interactions));
        _mode = (int)mode;
        StartMode = mode;
    }

    private PermissionRunState(PermissionRunState parent, RunId runId, RunInteractions interactions, PermissionMode defined, EnvPath? root)
    {
        _parent = parent;
        _cap = defined;
        RunId = runId;
        SessionId = parent.SessionId;
        Environment = parent.Environment;
        Interactions = interactions;
        Root = root ?? parent.Root;
        WorkspaceId = parent.WorkspaceId;
        UserTask = parent.UserTask;
        ReviewerModel = parent.ReviewerModel;
        StartMode = PermissionModes.Min(parent.Mode, defined);
        _mode = (int)defined;
    }

    public RunId RunId { get; }

    public string SessionId { get; }

    public string WorkspaceId { get; init; } = "";

    /// <summary>项目的执行环境。审批里带它的标识(设备 + 环境)。</summary>
    public IExecutionEnvironment Environment { get; }

    /// <summary>项目根(环境里的绝对路径)。<c>null</c> = 没有绑定工作区:项目作用域不可用,路径只能是绝对路径。</summary>
    public EnvPath? Root { get; init; }

    public RunInteractions Interactions { get; }

    /// <summary>0 = 顶层 Run;&gt; 0 = 子代理(不能问人)。</summary>
    public int Depth => Interactions.Depth;

    public bool IsSubagent => Depth > 0;

    /// <summary>用户这一轮的任务描述(给 AI 审核器看,截到 8000 字符)。</summary>
    public string UserTask { get; init; } = "";

    /// <summary>审核器模型,<b>按 Run 冻结</b>:别名和供应商必须在同一刻冻结,否则中途换了供应商会拼出「旧别名 + 新供应商」。</summary>
    public ModelRef? ReviewerModel { get; init; }

    /// <summary>计划工作流开着(2.x 的 <c>planWorkflowEnabled</c>):<c>EnterPlanMode</c> / <c>ExitPlanMode</c> 视为只读(它们改的是计划文件和模式,不是项目)。</summary>
    public bool PlanWorkflow { get; init; }

    /// <summary>Run 开始时的档位快照。</summary>
    public PermissionMode StartMode { get; }

    /// <summary>实时档位。子代理 = min(父的实时值, 定义的值)。</summary>
    public PermissionMode Mode
    {
        get
        {
            var own = (PermissionMode)Volatile.Read(ref _mode);
            return _parent is null ? own : PermissionModes.Min(_parent.Mode, own);
        }
        set => Volatile.Write(ref _mode, (int)(_cap is { } cap ? PermissionModes.Min(value, cap) : value)); // 子代理永远不能被调到比定义的值更宽
    }

    /// <summary>联网开关(实时值)。子代理跟随父 Run。</summary>
    public bool WebSearch
    {
        get => _parent?.WebSearch ?? Volatile.Read(ref _webSearch) != 0;
        set => Volatile.Write(ref _webSearch, value ? 1 : 0);
    }

    /// <summary>
    /// 计划模式的写入围栏:非空时 Write / Edit <b>只许改这一个文件</b>,而且只有改的正是它时才被当作「只读」放行。
    /// 它必须和 <c>ToolRuntime.WriteFence</c> 是同一个值——用 <see cref="FromRuntime"/> 构造就是从同一处取,不会各写一份。
    /// </summary>
    public EnvPath? PlanFence
    {
        get => _planFence;
        set => _planFence = value;
    }

    /// <summary>
    /// 从会话层造给工具的 <see cref="ToolRuntime"/> 取执行环境、项目根和写入围栏——权限层和工具层对「在哪、围在哪」永远是同一个答案。
    /// </summary>
    public static PermissionRunState FromRuntime(ToolRuntime runtime, RunId runId, string sessionId, RunInteractions interactions, PermissionMode mode) =>
        new(runId, sessionId, runtime.Environment, interactions, mode) { Root = runtime.Root, PlanFence = runtime.WriteFence };

    /// <summary>派生子代理的权限状态:档位 = min(父实时值, <paramref name="definedMode"/>),联网开关跟随父,没有围栏,<paramref name="interactions"/> 必须是 <c>depth &gt; 0</c> 的句柄。</summary>
    public PermissionRunState ForSubagent(RunId childRun, RunInteractions interactions, PermissionMode definedMode, EnvPath? root = null)
    {
        if (interactions.CanInteract)
        {
            throw new ArgumentException("A subagent's interaction scope must have depth > 0.", nameof(interactions));
        }

        return new PermissionRunState(this, childRun, interactions, definedMode, root);
    }

    public ExecutionEnvironmentRef EnvironmentRef => ExecutionEnvironmentRef.From(Environment);

    public RuleStoreContext RuleContext => new(SessionId, Environment, Root);

    /// <summary>项目根的真实路径(macOS 的 <c>/var</c> → <c>/private/var</c>)。路径守卫返回的绝对路径都是 realpath 之后的形式,「相对根显示」必须拿同样 realpath 过的根比。</summary>
    internal async ValueTask<EnvPath?> RealRootAsync(CancellationToken ct)
    {
        if (Root is not { } root)
        {
            return null;
        }

        if (_realRoot is { } cached)
        {
            return cached;
        }

        try
        {
            var real = await Environment.Fs.RealPathAsync(root, ct).ConfigureAwait(false);
            _realRoot = real;
            return real;
        }
        catch (EnvironmentException e) when (e.Code is EnvironmentErrorCode.NotFound or EnvironmentErrorCode.NotDirectory)
        {
            return root;
        }
    }
}

/// <summary>Run → 权限状态 的登记表。会话层 <see cref="Register"/>,扩展在 <c>run_end</c> 时 <see cref="Remove"/>。线程安全。</summary>
public sealed class PermissionRuns
{
    private readonly ConcurrentDictionary<RunId, PermissionRunState> _byRun = new();
    private readonly ConcurrentDictionary<string, PermissionRunState> _bySession = new(StringComparer.Ordinal);

    public void Register(PermissionRunState state)
    {
        if (!_byRun.TryAdd(state.RunId, state))
        {
            throw new InvalidOperationException($"Run {state.RunId} already has a permission state.");
        }

        _bySession[state.SessionId] = state;
    }

    public PermissionRunState? Get(RunId runId) => _byRun.GetValueOrDefault(runId);

    /// <summary>会话最近登记的那个 Run(系统提示词的权限事实用,它的输入是 SessionInfo,没有 RunId)。</summary>
    public PermissionRunState? ForSession(string sessionId) => _bySession.GetValueOrDefault(sessionId);

    public bool Remove(RunId runId)
    {
        if (!_byRun.TryRemove(runId, out var state))
        {
            return false;
        }

        _bySession.TryRemove(new KeyValuePair<string, PermissionRunState>(state.SessionId, state));
        return true;
    }

    public int Count => _byRun.Count;
}
