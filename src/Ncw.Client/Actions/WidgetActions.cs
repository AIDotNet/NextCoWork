using System.Text.Json;
using Ncw.Client.Devices;
using Ncw.Client.Stores;
using Ncw.Client.Widgets;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Actions;

/// <summary>要用户确认的一次动作:Agent 绑定参数之后<b>真实的</b>工具与入参 + 执行环境标识(确认框显示它们,不显示小组件自己写的文案)。</summary>
public sealed record WidgetActionPrompt(string ActionId, string Label, string ToolName, string InputJson, EnvRefDto? Environment);

public enum WidgetActionKind
{
    /// <summary>动作跑了(<see cref="WidgetActionOutcome.Ok"/> 说明成败;输出不进模型上下文)。</summary>
    Result,

    /// <summary>被拒绝(<see cref="WidgetActionOutcome.Reason"/> 是设备给的稳定 key,<c>widgets.action.*</c>)。</summary>
    Denied,

    /// <summary>用户在确认框里取消了。</summary>
    Declined,

    /// <summary>没送达设备(没连上 / 没有权限 / 设备出错)。</summary>
    Unavailable,
}

public sealed record WidgetActionOutcome(WidgetActionKind Kind, bool Ok = false, string? Output = null, string? Reason = null);

/// <summary>
/// 内联可视化小组件的用户意图:状态读写、交互模式的提交、动作(规则 4:一个意图一个方法;状态经 <see cref="WidgetsMirror"/> 进 Store,控制端只保留一份镜像)。
/// 小组件是模型写的,状态是不可信输入(设备限制大小,界面再按 <see cref="WidgetLimits"/> 卡一道);手机端降级为静态快照(只读,不走这里)。
/// </summary>
public sealed class WidgetActions(DeviceRegistry devices, Store<WidgetsMirror>? mirror = null)
{
    /// <summary>读一张卡的状态并合进镜像(发请求之后有本地写入就丢弃读回的结果,见 <see cref="WidgetsReducer.StateLoaded"/>)。</summary>
    public async Task<ActionResult<WidgetStateResult>> GetStateAsync(string deviceId, string sessionId, string callId, CancellationToken ct = default)
    {
        var card = new WidgetRef(deviceId, sessionId, callId);
        var rev = mirror?.State.Of(card).StateRev ?? 0;
        var result = await ServiceCall.RunAsync(devices, deviceId, link => link.CallAsync(WidgetsMethods.GetState, new WidgetRefParams(sessionId, callId), ct)).ConfigureAwait(false);
        if (result.IsOk)
        {
            mirror?.Update(m => WidgetsReducer.StateLoaded(m, card, result.Value!.State, rev));
        }

        return result;
    }

    /// <summary><paramref name="stateJson"/> 为 null = 清掉这张卡的状态。乐观更新镜像,写失败标陈旧。</summary>
    public async Task<ActionResult<Unit>> SetStateAsync(string deviceId, string sessionId, string callId, string? stateJson, CancellationToken ct = default)
    {
        var card = new WidgetRef(deviceId, sessionId, callId);
        mirror?.Update(m => WidgetsReducer.StateWritten(m, card, stateJson));
        var result = await ServiceCall.RunAsync(devices, deviceId, link => link.CallAsync(WidgetsMethods.SetState, new WidgetSetStateParams(sessionId, callId, stateJson), ct, idem: ServiceCall.Idem("widgets", "setState"))).ConfigureAwait(false);
        if (!result.IsOk)
        {
            mirror?.Update(m => WidgetsReducer.StateWriteFailed(m, card));
        }

        return result;
    }

    /// <summary>
    /// 交互模式的提交(<paramref name="valueJson"/> 为 null = 用户关掉了、没有提交)。先到先得;没有在等 → <see cref="ActionOutcome.NotFound"/>(镜像标「已关闭」)。
    /// 进度进镜像:提交中 → 已接受 / 已关闭(终态);没送达(设备不可用)退回「没提交」可以重试。
    /// </summary>
    public async Task<ActionResult<WidgetSubmitResult>> SubmitAsync(string deviceId, string sessionId, string callId, string? valueJson, CancellationToken ct = default)
    {
        var card = new WidgetRef(deviceId, sessionId, callId);
        mirror?.Update(m => WidgetsReducer.SubmitStarted(m, card));
        var result = await ServiceCall.RunAsync(devices, deviceId, link => link.CallAsync(WidgetsMethods.Submit, new WidgetSubmitParams(callId, valueJson), ct, idem: ServiceCall.Idem("widgets", "submit"))).ConfigureAwait(false);
        var settled = result.Outcome switch
        {
            ActionOutcome.Ok when result.Value!.Accepted => WidgetSubmit.Accepted,
            ActionOutcome.Ok or ActionOutcome.NotFound or ActionOutcome.Conflict => WidgetSubmit.Closed,
            _ => WidgetSubmit.None,
        };
        mirror?.Update(m => WidgetsReducer.SubmitSettled(m, card, settled));
        return result;
    }

    public Task<ActionResult<WidgetActionResultDto>> RunActionAsync(string deviceId, string sessionId, string callId, string actionId, string paramsJson, bool confirmed, CancellationToken ct = default) =>
        ServiceCall.RunAsync(devices, deviceId, link => link.CallAsync(WidgetsMethods.RunAction, new WidgetActionParams(sessionId, callId, actionId, paramsJson, confirmed), ct, idem: ServiceCall.Idem("widgets", "runAction")));

    /// <summary>
    /// 跑一个小组件声明过的动作(含「第一次一定问」的确认流程):先不带确认发一次;设备回 <c>confirm</c> 就把真实的工具与入参交给 <paramref name="confirm"/>(界面画确认框),
    /// 用户确认后带 <c>Confirmed = true</c> 重发。小组件只能选 id 和受约束的参数,「要跑什么」是 Agent 从转录里取的。
    /// </summary>
    public async Task<WidgetActionOutcome> RunDeclaredActionAsync(
        string deviceId, string sessionId, string callId, string actionId, string label, string paramsJson, Func<WidgetActionPrompt, Task<bool>> confirm, CancellationToken ct = default)
    {
        var first = await RunActionAsync(deviceId, sessionId, callId, actionId, paramsJson, false, ct).ConfigureAwait(false);
        if (!first.IsOk)
        {
            return new WidgetActionOutcome(WidgetActionKind.Unavailable, Reason: first.Outcome.ToString());
        }

        var dto = first.Value!;
        if (dto.Kind == "confirm")
        {
            if (!await confirm(ReadPrompt(actionId, label, dto.Output)).ConfigureAwait(false))
            {
                return new WidgetActionOutcome(WidgetActionKind.Declined);
            }

            var second = await RunActionAsync(deviceId, sessionId, callId, actionId, paramsJson, true, ct).ConfigureAwait(false);
            if (!second.IsOk)
            {
                return new WidgetActionOutcome(WidgetActionKind.Unavailable, Reason: second.Outcome.ToString());
            }

            dto = second.Value!;
        }

        return dto.Kind switch
        {
            "result" => new WidgetActionOutcome(WidgetActionKind.Result, dto.Reason is null, dto.Output, dto.Reason),
            "deny" => new WidgetActionOutcome(WidgetActionKind.Denied, Output: dto.Output, Reason: dto.Reason),
            // 确认了又被要求确认(入参在两次之间变了):当作拒绝,让小组件重来
            _ => new WidgetActionOutcome(WidgetActionKind.Denied, Reason: "widgets.action.confirmAgain"),
        };
    }

    /// <summary>设备回的 <c>confirm</c>:<c>{toolName,input,environment}</c>。容错:缺字段就给空串,确认框照样显示,用户看得出不对劲。</summary>
    internal static WidgetActionPrompt ReadPrompt(string actionId, string label, string? json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json ?? "{}");
            var root = doc.RootElement;
            var tool = root.TryGetProperty("toolName", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "";
            var input = root.TryGetProperty("input", out var i) ? i.GetRawText() : "{}";
            EnvRefDto? env = null;
            if (root.TryGetProperty("environment", out var e) && e.ValueKind == JsonValueKind.Object)
            {
                string S(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
                env = new EnvRefDto(S("deviceId"), S("environment"), S("name"), S("type"));
            }

            return new WidgetActionPrompt(actionId, label, tool, input, env);
        }
        catch (JsonException)
        {
            return new WidgetActionPrompt(actionId, label, "", "{}", null);
        }
    }
}
