using Ncw.Agent.Extensions.Permissions;
using Ncw.Agent.Kernel.Extensions;
using Ncw.Agent.Kernel.Session;

namespace Ncw.Agent.Host.Conversation;

/// <summary>
/// 「联网搜索」开关的工具快照那一半(03 §3 / PermissionGate.NetworkSwitchTools 的注释:「两处消费者必须读同一张表:工具快照(不下发)和权限扩展(调用时拒)」)。
/// 开关关着时 <c>WebFetch</c> / <c>web_search</c> 不进本回合的工具快照——模型根本看不到它们,不会去试;调用时的拒绝(权限扩展)是第二道,留给「Run 中途开关被关」的情形不一致也不会放行。
/// 只过滤、不新增,所以永远不可能提权;子代理的 Run 跟随父 Run 的开关(<c>PermissionRunState.WebSearch</c> 沿父链读)。
/// </summary>
internal sealed class NetworkSwitchExtension(PermissionRuns runs) : IAgentExtension
{
    public string Id => "network-switch";

    public int Order => 50;

    public void Register(IExtensionRegistry registry) =>
        registry.Mutate(SessionExtensionPoints.BeforeRun, (draft, ctx, _) =>
        {
            // 没有登记的 Run 当作开关关着(失败即关闭)
            var open = runs.Get(ctx.RunId)?.WebSearch ?? false;
            if (!open)
            {
                var previous = draft.ToolFilter;
                draft.ToolFilter = previous is null
                    ? spec => !PermissionGate.NetworkSwitchTools.Contains(spec.InternalId)
                    : spec => previous(spec) && !PermissionGate.NetworkSwitchTools.Contains(spec.InternalId);
            }

            return ValueTask.CompletedTask;
        });
}
