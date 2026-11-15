using System.Text.Json;
using System.Text.Json.Nodes;
using Ncw.Agent.Kernel.Extensions;
using Ncw.Agent.Kernel.Loop;

namespace Ncw.Agent.Parity.Tests.Rig;

/// <summary>
/// 按录制里 <c>approvals[callId]</c> 回答 <c>before_tool</c> 闸门(缺省放行);这是循环层的审批接缝,不是权限扩展本身
/// (2.x 侧同样是一个按 callId 给定决定的 <c>approve</c> 回调)。被询问时先触发 <c>{approval: callId}</c> 用户操作。
/// </summary>
public sealed class ScriptedApproval(JsonObject approvals, Action<JsonObject> fire) : IAgentExtension
{
    public string Id => "parity.approval";

    public int Order => 0;

    public void Register(IExtensionRegistry registry) =>
        registry.Gate(LoopExtensionPoints.BeforeTool, (invocation, _, _) =>
        {
            fire(new JsonObject { ["approval"] = invocation.CallId });
            var decision = approvals[invocation.CallId]?.AsObject();
            PreflightDecision result = decision?["kind"]?.GetValue<string>() switch
            {
                "deny" => PreflightDecision.Deny(decision["reason"]?.GetValue<string>()),
                "allow_edited" => PreflightDecision.Edit(JsonDocument.Parse(decision["input"]!.ToJsonString()).RootElement.Clone()),
                _ => PreflightDecision.Allow,
            };
            return ValueTask.FromResult(result);
        });
}
