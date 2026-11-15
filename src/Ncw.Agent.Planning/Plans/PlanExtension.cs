using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Extensions;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Runs;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;

namespace Ncw.Agent.Extensions.Planning.Plans;

/// <summary>
/// 计划模式的一方扩展(06 §3):<c>before_run</c>(工具允许列表、状态块里的计划文件路径、批准的计划)、<c>compact_attachments</c>(压缩后重附计划文件路径)、
/// <c>run_end</c>(摘掉 Run 级登记)、工具 <c>EnterPlanMode</c> / <c>ExitPlanMode</c>。可关闭(设置里的计划工作流开关 = 不注册这个扩展)。
/// </summary>
public sealed class PlanModeExtension(PlanService plans) : IAgentExtension
{
    public string Id => "plan-mode";

    /// <summary>在权限(最前)和 Hooks 之后:<c>turn_end</c> 上它没有处理器,Order 只决定 <c>before_run</c> 的修改顺序(它的 ToolFilter 要套在别人设的过滤器外面)。</summary>
    public int Order => 400;

    public void Register(IExtensionRegistry registry)
    {
        registry.RegisterTool(new EnterPlanModeTool(plans));
        registry.RegisterTool(new ExitPlanModeTool(plans));
        registry.Mutate(SessionExtensionPoints.BeforeRun, (draft, ctx, ct) => plans.OnBeforeRunAsync(draft, ctx, ct));
        registry.Mutate(SessionExtensionPoints.CompactAttachments, (draft, _, _) =>
        {
            plans.OnCompactAttachments(draft);
            return ValueTask.CompletedTask;
        });
        registry.On(LoopExtensionPoints.RunEnd, (_, ctx, _) =>
        {
            plans.CloseRun(ctx.RunId);
            return ValueTask.CompletedTask;
        });
    }
}

/// <summary>
/// 计划的 Run 扩展(编排层的接缝):每次丰富工具上下文时,按会话的活动计划把<b>写入围栏</b>同步给这个 Run 的 <c>ToolRuntime</c> 和 <c>PermissionRunState</c>——
/// 一个计划跨多个 Run,新 Run 开始时围栏必须已经在(装配方不必记得去设)。必须排在造 <c>ToolRuntime</c> 的那个 Run 扩展(Host)之后。
/// </summary>
public sealed class PlanRunExtension(PlanService plans) : IRunExtension
{
    public void OnCreated(RunHandle run) => plans.SyncFence(run.RunId, run.SessionId, null);

    public ToolContext EnrichToolContext(RunHandle run, ToolContext context)
    {
        plans.SyncFence(run.RunId, run.SessionId, context);
        return context;
    }

    public void OnEnded(RunHandle run, RunStatus status, AgentError? error) => plans.CloseRun(run.RunId);
}
