using PolyType;

namespace Ncw.Protocol.Methods;

// 内联可视化小组件(docs/design/10 的 `widget` 模块 → `widgets.*`):状态、交互模式的提交、预先声明的动作。手机端降级为静态快照(只读状态)。
// 小组件是模型写的、跑在不透明源的沙箱里,它的状态是不可信输入:大小有上限(单张卡 64 KiB、每会话 200 张),不是合法 JSON 的拒绝;状态不进模型上下文。读 viewer 起,写 / 提交 / 动作 member 起。
// 动作(`widgets.runAction`):可视化工具(`visualize_show_widget`)把动作声明写进转录卡片,Agent 从转录里找到声明、绑定参数(枚举 / 有界数字)、经和 Run 里同一条权限链判定后代参执行(widget 只能选 id 和受约束的参数)。
// 结果都是 `WidgetActionResultDto`,不是错误:`deny`(Reason = 稳定 key `widgets.action.*`,Output = 英文说明)/ `confirm`(Output = `{toolName,input,environment}` 的 JSON,控制端画确认框,确认后带 `Confirmed = true` 重发)/ `result`(Reason = null 成功,`widgets.action.failed` 失败;Output = 工具输出,不进模型上下文)。

public sealed record WidgetRefParams(string SessionId, string CallId);

/// <summary><see cref="State"/> 是 JSON 文本;没有 = null。</summary>
public sealed record WidgetStateResult(string? State);

/// <summary><see cref="State"/> 为 null = 清掉这张卡的状态。</summary>
public sealed record WidgetSetStateParams(string SessionId, string CallId, string? State);

/// <summary>交互模式的提交:<see cref="CallId"/> 找到挂起的 <c>widget_submit</c> 交互并回答它(先到先得,和 <c>interactions.respond</c> 同一个闸门)。<see cref="Value"/> 为 null = 用户关掉了、没有提交。</summary>
public sealed record WidgetSubmitParams(string CallId, string? Value);

public sealed record WidgetSubmitResult(bool Accepted);

public sealed record WidgetActionParams(string SessionId, string CallId, string ActionId, string ParamsJson, bool Confirmed);

/// <param name="Kind"><c>deny</c>(<see cref="Reason"/> 是稳定 key:<c>widgets.action.undeclared / invalidParams / toolUnavailable / denied / sessionGone / projectGone</c>)/ <c>confirm</c>(<see cref="Output"/> 是要确认的工具与入参的 JSON)/ <c>result</c>(<see cref="Reason"/> 为 null = 成功,<c>widgets.action.failed</c> = 工具失败)。</param>
public sealed record WidgetActionResultDto(string Kind, string? Reason = null, string? Output = null);

/// <summary>小组件方法表(能力 <see cref="Capabilities.Widgets"/>,承载 本机 + 远程)。</summary>
public static class WidgetsMethods
{
    public static readonly MethodDef<WidgetRefParams, WidgetStateResult> GetState =
        MethodTable.Query<WidgetRefParams, WidgetStateResult>("widgets.getState", Role.Viewer, Capabilities.Widgets);

    public static readonly MethodDef<WidgetSetStateParams, Unit> SetState =
        MethodTable.Command<WidgetSetStateParams, Unit>("widgets.setState", Role.Member, Capabilities.Widgets);

    public static readonly MethodDef<WidgetSubmitParams, WidgetSubmitResult> Submit =
        MethodTable.Command<WidgetSubmitParams, WidgetSubmitResult>("widgets.submit", Role.Member, Capabilities.Widgets);

    public static readonly MethodDef<WidgetActionParams, WidgetActionResultDto> RunAction =
        MethodTable.Command<WidgetActionParams, WidgetActionResultDto>("widgets.runAction", Role.Member, Capabilities.Widgets);

    public static readonly IReadOnlyList<MethodDef> All = [GetState, SetState, Submit, RunAction];
}

[GenerateShapeFor<WidgetRefParams>]
[GenerateShapeFor<WidgetStateResult>]
[GenerateShapeFor<WidgetSetStateParams>]
[GenerateShapeFor<WidgetSubmitParams>]
[GenerateShapeFor<WidgetSubmitResult>]
[GenerateShapeFor<WidgetActionParams>]
[GenerateShapeFor<WidgetActionResultDto>]
public sealed partial class PayloadShapes;
