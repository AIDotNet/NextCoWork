using Ncw.Agent.Kernel.Extensions;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Kernel.Tools;

namespace Ncw.Agent.Kernel.Subagents;

/// <summary>
/// 子代理的工具快照过滤(2.x <c>no-interaction-filter.test.ts</c> 钉住的那条):子代理的工具表里<b>去掉每一个会等用户的工具</b>——主动提问那条路在快照层就没了。
/// 「子代理不能提问」是三处对称的闸:① 这里(快照过滤);② 不提供 <c>Interact</c>(交互闸门对 <c>depth &gt; 0</c> 的 Run 当场抛 <c>InteractionRefusedException</c>);
/// ③ 权限扩展对子代理的审批直接拒绝(给出可添加的规则)。2.x 曾因此死锁一小时(六百多次工具调用之后毫无进展)。
/// </summary>
public static class SubagentToolFilter
{
    /// <summary>会等用户的工具。<b>恰好这两个</b>(有测试钉着):多摘一个等于悄悄削弱子代理,少摘一个等于留一条死锁的路。</summary>
    public static IReadOnlySet<string> InteractiveToolIds { get; } = new HashSet<string>(StringComparer.Ordinal) { "AskUserQuestion", "ExitPlanMode" };

    /// <summary>子代理能不能用这个工具:定义里写了 tools 就只认那份(归一化后的内部 id);没写 = 继承全部;去掉交互类工具与 disallowedTools。</summary>
    public static bool Allows(AgentDefinition definition, ToolSpec tool) =>
        !InteractiveToolIds.Contains(tool.InternalId)
        && (definition.Tools is null || definition.Tools.Contains(tool.InternalId, StringComparer.Ordinal))
        && (definition.DisallowedTools is null || !definition.DisallowedTools.Contains(tool.InternalId, StringComparer.Ordinal));
}

/// <summary>
/// 子代理这个一方扩展(06 §3 的 <c>Subagents</c>:<c>before_run</c>、工具 <c>Task</c>;可关闭)。每个工作区一个实例(<c>Task</c> 工具的 description 是那个工作区的子代理清单):
/// <list type="bullet">
/// <item>工具 <see cref="TaskTool"/>(<c>tool_registry</c>)。</item>
/// <item><c>before_run</c>,顶层 Run:重扫这个工作区的子代理目录(用户新加的文件不该「要重启才生效」,而没有任何地方会提示他;失败只记诊断,沿用上一份清单)。</item>
/// <item><c>before_run</c>,子 Run:收窄工具快照(定义里的 tools ∩ 去掉交互类工具,<see cref="SubagentToolFilter"/>;只过滤、不新增,所以永远不可能提权)、角色提示词追加。</item>
/// </list>
/// 子 Run 的权限收窄和交互作用域不在这里:那要在 Run 登记时做(<see cref="SubagentService.OnCreated"/>),先于第一次工具调用。
/// </summary>
public sealed class SubagentExtension : IAgentExtension
{
    private readonly SubagentService _service;
    private readonly string _workspaceId;
    private readonly TaskTool _task;

    public SubagentExtension(SubagentService service, string workspaceId)
    {
        _service = service;
        _workspaceId = workspaceId;
        _task = new TaskTool(service, () => service.Agents.Registry(workspaceId).List());
    }

    public string Id => "subagents";

    public int Order => 100;

    public TaskTool Tool => _task;

    public void Register(IExtensionRegistry registry)
    {
        registry.RegisterTool(_task);
        registry.Mutate(SessionExtensionPoints.BeforeRun, async (draft, ctx, ct) =>
        {
            if (draft.Request.Depth == 0)
            {
                try
                {
                    await _service.Agents.RefreshAsync(_workspaceId, ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    ctx.Log.Warn("[subagent] could not refresh the agent definitions; using the previous list.", e);
                }

                return;
            }

            if (!_service.TryGetChild(ctx.RunId, out var child))
            {
                return;
            }

            var previous = draft.ToolFilter;
            draft.ToolFilter = previous is null
                ? tool => SubagentToolFilter.Allows(child.Definition, tool)
                : tool => previous(tool) && SubagentToolFilter.Allows(child.Definition, tool);
            draft.AgentPrompt = child.Definition.Prompt;
        });
    }
}
