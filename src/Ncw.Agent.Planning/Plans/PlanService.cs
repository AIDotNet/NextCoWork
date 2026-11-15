using System.Collections.Concurrent;
using Ncw.Agent.Environments;
using Ncw.Agent.Extensions.Permissions;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Extensions;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;
using Ncw.Agent.Tools;

namespace Ncw.Agent.Extensions.Planning.Plans;

public sealed class PlanOptions
{
    /// <summary>
    /// 这个 Run 开着「计划工作流」吗(2.x:<c>mode.id === 'plan' || mode.tools 含 EnterPlanMode</c>)。模式 id 不在会话层的 <c>RunRequest</c> 里,由装配方(Host)按它自己知道的模式回答。
    /// 缺省 false:计划工具对所有模式隐藏。开着时:总是去掉 <c>Task</c>;有活动计划时隐藏 <c>EnterPlanMode</c>;没有时隐藏 <c>Write / Edit / ExitPlanMode</c>。
    /// </summary>
    public Func<ExtensionContext, bool> PlanWorkflow { get; init; } = static _ => false;

    /// <summary>Run → 权限状态(取交互句柄、同步 <c>PlanFence</c>)。没有 = 计划审批不可用,围栏只设 <c>ToolRuntime.WriteFence</c>。</summary>
    public PermissionRuns? PermissionRuns { get; init; }

    /// <summary>计划 id(缺省 ULID,和 2.x 混排)。</summary>
    public IIdGenerator Ids { get; init; } = new UlidIdGenerator();
}

/// <summary>
/// 计划模式的运行时状态与规则(05 §6)。工具、扩展、Run 扩展共用这一个对象:活动计划(按会话)、每个 Run 的草稿引用(计划变了就同步状态段)、围栏的同步。
/// <para>
/// <b>围栏</b>:<see cref="ToolRuntime.WriteFence"/> 与 <see cref="PermissionRunState.PlanFence"/> 永远是同一个值(<see cref="ApplyFence"/> 一起设)。
/// 进入 / 退出计划时由工具设;新 Run 开始时由 <see cref="PlanRunExtension"/> 在每次丰富工具上下文时按会话的活动计划同步(装配方不必记得去设)。
/// </para>
/// </summary>
public sealed class PlanService(PlanOptions? options = null)
{
    /// <summary>状态块里一段最多这么多字符(<c>ContextAssembler.StateSectionMax</c>);批准的计划整份塞进去,超了就按行切成多段。</summary>
    internal const int SectionChars = 3800;

    private sealed class RunPlan(string sessionId, bool workflow, RunDraft draft)
    {
        public string SessionId { get; } = sessionId;

        public bool Workflow { get; } = workflow;

        public RunDraft Draft { get; } = draft;

        public string? ActiveSection { get; set; }
    }

    private readonly ConcurrentDictionary<RunId, RunPlan> _runs = new();
    private readonly ConcurrentDictionary<RunId, PlanFileRef> _executions = new();

    public PlanOptions Options { get; } = options ?? new PlanOptions();

    public PlanRegistry Plans { get; } = new();

    public ActivePlan? ActiveFor(string sessionId) => Plans.Get(sessionId);

    /// <summary>这个 Run 开着计划工作流吗(<c>before_run</c> 时定下;没登记 = false)。目标层据此在计划模式里禁用 <c>ProposeGoal</c>。</summary>
    public bool InWorkflow(RunId runId) => _runs.TryGetValue(runId, out var run) && run.Workflow;

    /// <summary>会话当前计划文件的绝对路径(Run 开始时装配方设给 <c>ToolRuntime.WriteFence</c>;没有计划 = null)。</summary>
    public EnvPath? FenceFor(string sessionId) => Plans.Get(sessionId)?.Absolute;

    /// <summary>登记「这个 Run 是执行一份已批准的计划」(批准后客户端以 <c>code</c> 模式发起新 Run,带着计划引用):<c>before_run</c> 载入并放进状态块。</summary>
    public void ExpectExecution(RunId runId, PlanFileRef plan) => _executions[runId] = plan;

    /// <summary>撤回 <see cref="ExpectExecution"/>(这个 RunId 最终没有启动:会话忙、被拒):免得登记留到进程结束。</summary>
    public void CancelExpectation(RunId runId) => _executions.TryRemove(runId, out _);

    // ───────────────────────── 围栏 ─────────────────────────

    /// <summary>围栏开 / 关:工具层和权限层同一个值,一起设。</summary>
    public void ApplyFence(ToolContext ctx, EnvPath? fence)
    {
        if (ctx is ExecutionToolContext { Runtime: var runtime })
        {
            runtime.WriteFence = fence;
        }

        if (Options.PermissionRuns?.Get(ctx.RunId) is { } state)
        {
            state.PlanFence = fence;
        }
    }

    internal void SyncFence(RunId runId, string sessionId, ToolContext? ctx)
    {
        var fence = FenceFor(sessionId);
        if (ctx is ExecutionToolContext { Runtime: var runtime } && runtime.WriteFence != fence)
        {
            runtime.WriteFence = fence;
        }

        if (Options.PermissionRuns?.Get(runId) is { } state && state.PlanFence != fence)
        {
            state.PlanFence = fence;
        }
    }

    // ───────────────────────── 工具允许列表(每回合现算) ─────────────────────────

    internal const string EnterTool = "EnterPlanMode";
    internal const string ExitTool = "ExitPlanMode";

    /// <summary>
    /// 这个工具本回合要不要下发。<b>每回合重新算</b>(过滤器闭包每次取快照都被调):计划在 Run 中间开 / 关,下一回合的工具表就跟着变。
    /// 没开计划工作流时只摘掉 <c>EnterPlanMode / ExitPlanMode</c>。
    /// </summary>
    internal bool Allows(string internalId, string sessionId, bool workflow)
    {
        if (!workflow)
        {
            return internalId is not (EnterTool or ExitTool);
        }

        if (internalId == "Task")
        {
            return false;
        }

        return Plans.Get(sessionId) is not null
            ? internalId != EnterTool
            : internalId is not ("Write" or "Edit" or ExitTool);
    }

    // ───────────────────────── before_run ─────────────────────────

    internal async ValueTask OnBeforeRunAsync(RunDraft draft, ExtensionContext ctx, CancellationToken ct)
    {
        var workflow = Options.PlanWorkflow(ctx);
        var run = new RunPlan(ctx.SessionId, workflow, draft);
        _runs[ctx.RunId] = run;

        var prior = draft.ToolFilter;
        var sessionId = ctx.SessionId;
        draft.ToolFilter = spec => (prior?.Invoke(spec) ?? true) && Allows(spec.InternalId, sessionId, workflow);

        Refresh(ctx.RunId);

        if (_executions.TryRemove(ctx.RunId, out var reference))
        {
            var sections = await LoadApprovedSectionsAsync(ctx.RunId, reference, ctx, ct).ConfigureAwait(false);
            lock (draft.StateSections)
            {
                draft.StateSections.AddRange(sections);
            }
        }
    }

    private async ValueTask<IReadOnlyList<string>> LoadApprovedSectionsAsync(RunId runId, PlanFileRef reference, ExtensionContext ctx, CancellationToken ct)
    {
        var state = Options.PermissionRuns?.Get(runId);
        try
        {
            if (state?.Root is not { } root)
            {
                throw new PlanExecutionException("The approved plan cannot be loaded without a workspace.");
            }

            var plan = await PlanExecution.LoadAsync(state.Environment, root, reference, ct).ConfigureAwait(false);
            return PlanSections.Approved(plan);
        }
        catch (PlanExecutionException e)
        {
            // before_run 的异常会被吞掉,模型就会在没有计划的情况下「执行计划」:失败要进状态块,让它如实告诉用户。
            ctx.Log.Warn("The approved plan could not be loaded.", e);
            return [PlanSections.ApprovedUnavailable(reference.Path, e.Message)];
        }
        catch (EnvironmentException e)
        {
            ctx.Log.Warn("The approved plan could not be read.", e);
            return [PlanSections.ApprovedUnavailable(reference.Path, $"The execution environment could not read it ({e.Code}).")];
        }
    }

    /// <summary>
    /// 计划状态变了(进入 / 退出):同步这个 Run 状态块里的「当前计划文件」那一段。★ 状态块是每个 Run 算一次的快照,但计划文件路径是模型写文件的唯一依据,
    /// 而它原本只存在于 <c>EnterPlanMode</c> 的工具结果里——压缩会把它抹掉。所以路径每回合都在状态块里,计划一变就在下一回合的装配里生效。
    /// </summary>
    internal void Refresh(RunId runId)
    {
        if (!_runs.TryGetValue(runId, out var run) || !run.Workflow)
        {
            return;
        }

        var section = Plans.Get(run.SessionId) is { } plan ? PlanSections.Active(plan.Path) : null;
        lock (run.Draft.StateSections)
        {
            if (run.ActiveSection == section)
            {
                return;
            }

            if (run.ActiveSection is { } old)
            {
                run.Draft.StateSections.Remove(old);
            }

            if (section is not null)
            {
                run.Draft.StateSections.Add(section);
            }

            run.ActiveSection = section;
        }
    }

    internal void CloseRun(RunId runId)
    {
        _runs.TryRemove(runId, out _);
        _executions.TryRemove(runId, out _);
    }

    internal int OpenRuns => _runs.Count;

    // ───────────────────────── 压缩后附件 ─────────────────────────

    /// <summary>压缩把历史里的工具输出整段丢了:计划文件路径重新附在边界消息里(和每回合状态块里那段是同一句话)。</summary>
    internal void OnCompactAttachments(CompactAttachmentDraft draft)
    {
        if (Plans.Get(draft.SessionId) is { } plan)
        {
            draft.Texts.Add($"<system-reminder>\n{PlanSections.Active(plan.Path)}\n</system-reminder>");
        }
    }
}
