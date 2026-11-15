using PolyType;

namespace Ncw.Protocol.Methods;

// 输入框补全(docs/design/10 的 `composer` 模块 → `composer.*`,设计见 docs/composer-autocomplete.md):建议下一步(L2,输入框空着时那句灰字)、历史指令(L1,零请求)、打字续写(L3)、取消。
// ★ 一切失败都是「没有建议」(空串):补全是便利不是功能,不会在输入框上弹出任何错误。设备这一侧再判一次控制端判过的条件(开关、会话类型、目标、密钥)。
// 请求 id 由控制端生成(8–128 字符),用于合流 / 取消;同一会话同一个 `TriggerKey` 的 suggestNext 合流成一次上游请求(多个控制端只花一次钱),新的 `TriggerKey` 让旧的作废。
// 这些方法发起上游模型请求(花用户的钱),所以 member 起;历史指令 viewer 起。

/// <param name="TriggerKey">控制端看到的那次 Run 结束时间(同一会话同一个值 = 同一轮)。</param>
/// <param name="Model">输入框此刻的生效模型;设置里点名了补全模型时用设置的,不换家。</param>
public sealed record ComposerSuggestParams(string RequestId, string WorkspaceId, string SessionId, long TriggerKey, string Model, string? ModelProviderId = null);

/// <summary><see cref="Text"/> 为空 = 没有建议(沉默、被判不合规、超时、被取消都是这个)。</summary>
public sealed record ComposerReplyDto(string RequestId, string Text);

public sealed record ComposerHistoryParams(string WorkspaceId);

public sealed record ComposerHistoryResult(string[] Prompts);

/// <summary>打字续写时控制端一并递来的上下文(控制端本来就有的东西,设备只截断不信任)。</summary>
public sealed record ComposerContextDto(string Project, string? Branch, string[] Files, string[] Commands);

public sealed record ComposerCompleteParams(string RequestId, string WorkspaceId, string? SessionId, string Draft, ComposerContextDto Context);

public sealed record ComposerCancelParams(string RequestId);

/// <summary>输入框补全方法表(能力 <see cref="Capabilities.Composer"/>,承载 本机 + 远程)。</summary>
public static class ComposerMethods
{
    public static readonly MethodDef<ComposerSuggestParams, ComposerReplyDto> SuggestNext =
        MethodTable.Query<ComposerSuggestParams, ComposerReplyDto>("composer.suggestNext", Role.Member, Capabilities.Composer);

    public static readonly MethodDef<ComposerHistoryParams, ComposerHistoryResult> History =
        MethodTable.Query<ComposerHistoryParams, ComposerHistoryResult>("composer.history", Role.Viewer, Capabilities.Composer);

    public static readonly MethodDef<ComposerCompleteParams, ComposerReplyDto> Complete =
        MethodTable.Query<ComposerCompleteParams, ComposerReplyDto>("composer.complete", Role.Member, Capabilities.Composer);

    /// <summary>取消一个在途的建议 / 续写(幂等,不存在不报错)。</summary>
    public static readonly MethodDef<ComposerCancelParams, Unit> Cancel =
        MethodTable.Command<ComposerCancelParams, Unit>("composer.cancel", Role.Member, Capabilities.Composer);

    public static readonly IReadOnlyList<MethodDef> All = [SuggestNext, History, Complete, Cancel];
}

[GenerateShapeFor<ComposerSuggestParams>]
[GenerateShapeFor<ComposerReplyDto>]
[GenerateShapeFor<ComposerHistoryParams>]
[GenerateShapeFor<ComposerHistoryResult>]
[GenerateShapeFor<ComposerContextDto>]
[GenerateShapeFor<ComposerCompleteParams>]
[GenerateShapeFor<ComposerCancelParams>]
public sealed partial class PayloadShapes;
