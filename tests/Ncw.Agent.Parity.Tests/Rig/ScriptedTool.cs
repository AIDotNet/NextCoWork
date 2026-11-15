using System.Text.Json;
using System.Text.Json.Nodes;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Json;

namespace Ncw.Agent.Parity.Tests.Rig;

/// <summary>测试内的假工具:行为按录制里 <c>toolBehaviors[callId]</c> 给(缺省:返回「&lt;名字&gt; ran」)。执行开始时先触发 <c>{tool, phase: start}</c> 用户操作。</summary>
public sealed class ScriptedTool(JsonObject spec, JsonObject behaviors, Action<JsonObject> fire, Func<string, Task>? gate = null) : IAgentTool
{
    public ToolSpec Info { get; } = new(
        spec["name"]!.GetValue<string>(),
        $"{spec["name"]!.GetValue<string>()} tool",
        Jsn.Parse("""{"type":"object"}"""),
        new ToolTraits(
            spec["readOnly"]?.GetValue<bool>() ?? true,
            spec["destructive"]?.GetValue<bool>() ?? false,
            false,
            spec["concurrencySafe"]?.GetValue<bool>() ?? true));

    public async ValueTask<ToolResult> ExecuteAsync(ToolInvocation call, ToolContext ctx, CancellationToken ct)
    {
        fire(new JsonObject { ["tool"] = call.CallId, ["phase"] = "start" });
        var behavior = behaviors[call.CallId]?.AsObject() ?? new JsonObject { ["kind"] = "ok", ["content"] = $"{Info.InternalId} ran" };
        while (behavior["kind"]!.GetValue<string>() == "then")
        {
            behavior = behavior["then"]!.AsObject();
        }

        switch (behavior["kind"]!.GetValue<string>())
        {
            case "ok":
                var text = behavior["content"]!.GetValue<string>();
                return behavior["isError"]?.GetValue<bool>() == true ? ToolResult.Error(text) : ToolResult.Text(text);
            case "throw":
                throw new InvalidOperationException(behavior["message"]!.GetValue<string>());
            case "big":
                return ToolResult.Text(new string('x', behavior["chars"]!.GetValue<int>()));
            case "waitGate":
                // 等一道闸门被放行才返回(整机回放:让下一回合晚于某个用户操作的处理)
                await gate!(behavior["gate"]!.GetValue<string>()).WaitAsync(ct).ConfigureAwait(false);
                return ToolResult.Text($"{Info.InternalId} ran");
            case "hang":
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                return ToolResult.Text(string.Empty);
            default:
                throw new InvalidOperationException($"Unknown tool behavior {behavior}.");
        }
    }
}
