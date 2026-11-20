using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Workflows;

/// <summary>对话内 Agent 工作流方法路由注册(docs/design/12-workflows.md)。</summary>
internal static class WorkflowHandlers
{
    public static void Register(MethodRouter router, Func<WorkflowService> workflows)
    {
        router.Handle(WorkflowMethods.Cancel, (p, _, _) =>
        {
            workflows().Cancel(p.RunId);
            return ValueTask.FromResult(Unit.Value);
        });
        router.Handle(WorkflowMethods.Resume, (p, _, _) => ValueTask.FromResult(workflows().Resume(p.RunId)));
        router.Handle(WorkflowMethods.RetryNode, (p, _, _) => ValueTask.FromResult(workflows().RetryNode(p.RunId, p.NodeId)));
        router.Handle(WorkflowMethods.ListRuns, (p, _, _) => ValueTask.FromResult(workflows().ListRuns(p)));
        router.Handle(WorkflowMethods.GetRun, (p, _, _) => ValueTask.FromResult(workflows().GetRun(p.RunId)));
    }
}
