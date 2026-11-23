using System.Globalization;
using Avalonia.Data.Converters;
using Ncw.Client.Actions;
using Ncw.Client.ViewModels;
using Ncw.Protocol.Methods;
using Ncw.UI.Localization;

namespace Ncw.UI.Converters;

/// <summary>权限档位(<c>ask</c> / <c>auto</c> / <c>full</c>)→ 文字。</summary>
public sealed class PermissionModeTextConverter : IValueConverter
{
    public static readonly PermissionModeTextConverter Instance = new();

    public static string Text(string? mode) => NcwTexts.Get(mode switch
    {
        "auto" => NcwTextKind.PermissionAuto,
        "full" => NcwTextKind.PermissionFull,
        _ => NcwTextKind.PermissionAsk,
    });

    public static string Hint(string? mode) => NcwTexts.Get(mode switch
    {
        "auto" => NcwTextKind.PermissionAutoHint,
        "full" => NcwTextKind.PermissionFullHint,
        _ => NcwTextKind.PermissionAskHint,
    });

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Text(value as string);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>模型声明的推理强度(线上名,<c>none</c> = 关)→ 短名,和输入框里思考档位的叫法一致。</summary>
public sealed class EffortTextConverter : IValueConverter
{
    public static readonly EffortTextConverter Instance = new();

    public static string Text(string? effort) => ThinkingLevelTextConverter.ShortText(effort == "none" ? "off" : effort);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Text(value as string);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>思考档位 → 文字。</summary>
public sealed class ThinkingLevelTextConverter : IValueConverter
{
    public static readonly ThinkingLevelTextConverter Instance = new();

    public static string Text(string? level) => NcwTexts.Get(level switch
    {
        "off" => NcwTextKind.ThinkingOff,
        "minimal" => NcwTextKind.ThinkingMinimal,
        "low" => NcwTextKind.ThinkingLow,
        "medium" => NcwTextKind.ThinkingMedium,
        "high" => NcwTextKind.ThinkingHigh,
        "xhigh" => NcwTextKind.ThinkingXHigh,
        "max" => NcwTextKind.ThinkingMax,
        _ => NcwTextKind.ThinkingAuto,
    });

    /// <summary>不带「思考:」前缀的短名(输入框里思考按钮的图标后面、菜单行)。</summary>
    public static string ShortText(string? level) => NcwTexts.Get(level switch
    {
        "off" => NcwTextKind.ThinkingShortOff,
        "minimal" => NcwTextKind.ThinkingShortMinimal,
        "low" => NcwTextKind.ThinkingShortLow,
        "medium" => NcwTextKind.ThinkingShortMedium,
        "high" => NcwTextKind.ThinkingShortHigh,
        "xhigh" => NcwTextKind.ThinkingShortXHigh,
        "max" => NcwTextKind.ThinkingShortMax,
        _ => NcwTextKind.ThinkingShortAuto,
    });

    /// <summary>刻度条点亮几格(0–6):关 = 0、最大 = 6;「自动」画在中间(3 格,用强调色区分)。</summary>
    public static int MeterLevel(string? level) => level switch
    {
        "off" => 0,
        "minimal" => 1,
        "low" => 2,
        "medium" => 3,
        "high" => 4,
        "xhigh" => 5,
        "max" => 6,
        _ => 3,
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Text(value as string);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>百分比(0–100)→ 圆环的扫过角度(0–360°,输入框里的上下文占用环)。</summary>
public sealed class PercentSweepConverter : IValueConverter
{
    public static readonly PercentSweepConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int percent ? Math.Clamp(percent, 0, 100) * 3.6 : 0.0;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>输入框不能发送的原因 / 发送失败的结局 → 提示文字。</summary>
public sealed class ComposerHintConverter : IValueConverter
{
    public static readonly ComposerHintConverter Block = new(block: true);
    public static readonly ComposerHintConverter Failure = new(block: false);

    private readonly bool _block;

    private ComposerHintConverter(bool block) => _block = block;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (_block)
        {
            return value switch
            {
                ComposerState { Block: ComposerBlock.DeviceUnavailable } => NcwTexts.Get(NcwTextKind.ComposerDeviceUnavailable),
                ComposerState { Block: ComposerBlock.ViewerRole } => NcwTexts.Get(NcwTextKind.ComposerViewerRole),
                ComposerState { Block: ComposerBlock.NoModel } => NcwTexts.Get(NcwTextKind.ComposerNoModel),
                _ => null,
            };
        }

        return value switch
        {
            SendOutcome.Busy => NcwTexts.Get(NcwTextKind.SendBusy),
            SendOutcome.Unavailable => NcwTexts.Get(NcwTextKind.SendUnavailable),
            SendOutcome.Forbidden => NcwTexts.Get(NcwTextKind.SendForbidden),
            SendOutcome.Rejected => NcwTexts.Get(NcwTextKind.SendRejected),
            _ => null,
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// 斜杠菜单一行的说明 / 参数提示:内置的本地动作按动作翻译(应用自己的文案);设备上的命令用它自己写的说明(用户的内容,不翻译)。
/// </summary>
public sealed class SlashTextConverter : IValueConverter
{
    public static readonly SlashTextConverter Description = new(hint: false);
    public static readonly SlashTextConverter Hint = new(hint: true);

    private readonly bool _hint;

    private SlashTextConverter(bool hint) => _hint = hint;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not SlashCommandItem item ? null : item.Action switch
    {
        null => _hint ? item.ArgumentHint : item.Description,
        SlashAction.Goal => NcwTexts.Get(_hint ? NcwTextKind.SlashGoalHint : NcwTextKind.SlashGoalDescription),
        SlashAction.Compact => NcwTexts.Get(_hint ? NcwTextKind.SlashCompactHint : NcwTextKind.SlashCompactDescription),
        SlashAction.Model => NcwTexts.Get(_hint ? NcwTextKind.SlashModelHint : NcwTextKind.SlashModelDescription),
        SlashAction.Plan => NcwTexts.Get(_hint ? NcwTextKind.SlashPlanHint : NcwTextKind.SlashPlanDescription),
        _ => _hint ? null : NcwTexts.Get(NcwTextKind.SlashClearDescription),
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>本地动作的结果提示(<see cref="SlashNotice"/>)→ 文字。</summary>
public sealed class SlashNoticeConverter : IValueConverter
{
    public static readonly SlashNoticeConverter Instance = new();

    public static string? Text(SlashNotice? notice) => notice is null ? null : notice.Kind switch
    {
        SlashNoticeKind.GoalUsage => NcwTexts.Get(NcwTextKind.SlashGoalUsage),
        SlashNoticeKind.GoalEmpty => NcwTexts.Get(NcwTextKind.SlashGoalEmpty),
        SlashNoticeKind.GoalTooLong => NcwTexts.Format(NcwTextKind.SlashGoalTooLong, notice.Arg ?? ""),
        SlashNoticeKind.CompactBusy => NcwTexts.Get(NcwTextKind.SlashCompactBusy),
        SlashNoticeKind.CompactNothing => NcwTexts.Get(NcwTextKind.SlashCompactNothing),
        SlashNoticeKind.CompactFailed => NcwTexts.Get(NcwTextKind.SlashCompactFailed),
        SlashNoticeKind.ModelNotFound => NcwTexts.Format(NcwTextKind.SlashModelNotFound, notice.Arg ?? ""),
        SlashNoticeKind.ModelAmbiguous => NcwTexts.Format(NcwTextKind.SlashModelAmbiguous, notice.Arg ?? ""),
        SlashNoticeKind.ModelUnavailable => NcwTexts.Format(NcwTextKind.SlashModelUnavailable, notice.Arg ?? ""),
        SlashNoticeKind.ModelSwitched => NcwTexts.Format(NcwTextKind.SlashModelSwitched, notice.Arg ?? ""),
        SlashNoticeKind.PlanEnabled => NcwTexts.Get(NcwTextKind.SlashPlanEnabled),
        SlashNoticeKind.PlanDisabled => NcwTexts.Get(NcwTextKind.SlashPlanDisabled),
        _ => NcwTexts.Get(NcwTextKind.SlashUnavailable),
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Text(value as SlashNotice);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>上下文分段标识(<c>system</c> / <c>tools</c> / <c>instructions</c> / <c>messages</c>)→ 当前语言名称。</summary>
public sealed class ContextSegmentTitleConverter : IValueConverter
{
    public static readonly ContextSegmentTitleConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => NcwTexts.Get((value as string) switch
    {
        "tools" => NcwTextKind.ContextCardTools,
        "instructions" => NcwTextKind.ContextCardInstructions,
        "messages" => NcwTextKind.ContextCardMessages,
        _ => NcwTextKind.ContextCardSystem,
    });

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>「记住」的作用域(<c>session</c> / <c>project</c> / <c>global</c>)→ 文字。</summary>
public sealed class ScopeTextConverter : IValueConverter
{
    public static readonly ScopeTextConverter Instance = new();

    public static string Text(string? scope) => NcwTexts.Get(scope switch
    {
        "project" => NcwTextKind.ScopeProject,
        "global" => NcwTextKind.ScopeGlobal,
        _ => NcwTextKind.ScopeSession,
    });

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Text(value as string);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>交互回答没成功的原因 → 提示文字。</summary>
public sealed class InteractionProblemConverter : IValueConverter
{
    public static readonly InteractionProblemConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        InteractionOutcome.Stale => NcwTexts.Get(NcwTextKind.InteractionStale),
        InteractionOutcome.Invalid => NcwTexts.Get(NcwTextKind.InteractionInvalid),
        InteractionOutcome.Forbidden => NcwTexts.Get(NcwTextKind.InteractionForbidden),
        InteractionOutcome.NotFound => NcwTexts.Get(NcwTextKind.InteractionGone),
        InteractionOutcome.Unavailable => NcwTexts.Get(NcwTextKind.InteractionUnavailable),
        _ => null,
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>「已在另一台设备上处理」:谁、什么时候。</summary>
public sealed class HandledNoticeTextConverter : IValueConverter
{
    public static readonly HandledNoticeTextConverter Title = new(detail: false);
    public static readonly HandledNoticeTextConverter Detail = new(detail: true);

    private readonly bool _detail;

    private HandledNoticeTextConverter(bool detail) => _detail = detail;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not HandledNotice notice)
        {
            return null;
        }

        if (!_detail)
        {
            return NcwTexts.Get(notice.Aborted ? NcwTextKind.HandledAborted : NcwTextKind.HandledElsewhere);
        }

        var when = RelativeTimeConverter.Instance.Convert(notice.At, typeof(string), null, culture) as string ?? "";
        return notice.ResolvedBy is { Length: > 0 } by
            ? NcwTexts.Format(NcwTextKind.HandledBy, by.Length > 8 ? by[..8] : by, when)
            : when;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>压缩触发方式 → 文字。</summary>
public sealed class CompactTriggerTextConverter : IValueConverter
{
    public static readonly CompactTriggerTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        NcwTexts.Get(value is true ? NcwTextKind.CompactAuto : NcwTextKind.CompactManual);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>重试等待提示(次数 + 秒数)。</summary>
public sealed class RetryDelayTextConverter : IValueConverter
{
    public static readonly RetryDelayTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is StatusRowViewModel row && row.DelaySeconds > 0
            ? NcwTexts.Format(NcwTextKind.StatusRetryingWithDelay, row.Attempt, row.DelaySeconds)
            : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>错误行的文字:有设备给的 messageKey 就翻译(翻译不了就退回英文诊断),没有就直接显示诊断。</summary>
public sealed class ErrorTextConverter : IValueConverter
{
    public static readonly ErrorTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Ncw.Client.Transcript.ErrorRow row ? Text(row.Code, row.MessageKey) : null;

    /// <summary>
    /// 错误码 / 消息 key → 一句人话(docs/design/03 §11.4)。码是 Agent 写进消息和 Run 终态的那一套(<c>MessageCodec.CodeName</c>:auth、rate_limit、context_length、network……);
    /// 早期的别名(rate_limited、context_overflow、upstream_unavailable)也认。认不出来的给通用说明——设备的英文诊断只在「详情」里。
    /// </summary>
    public static string Text(string code, string? messageKey) => NcwTexts.Get(messageKey switch
    {
        "run.abandoned" => NcwTextKind.ErrorRunAbandoned,
        "run.internal" => NcwTextKind.ErrorRunInternal,
        "run.notStarted" => NcwTextKind.ErrorRunNotStarted,
        _ => code switch
        {
            "rate_limit" or "rate_limited" => NcwTextKind.ErrorRateLimited,
            "context_length" or "context_overflow" => NcwTextKind.ErrorContextOverflow,
            "auth" => NcwTextKind.ErrorAuth,
            "network" => NcwTextKind.ErrorNetwork,
            "provider" or "upstream_unavailable" => NcwTextKind.ErrorProvider,
            "cache_unsupported" => NcwTextKind.ErrorCacheUnsupported,
            "no_healthy_provider" => NcwTextKind.ErrorNoHealthyProvider,
            "conflict" => NcwTextKind.ErrorConflict,
            "tool_failed" => NcwTextKind.ErrorToolFailed,
            "aborted" => NcwTextKind.ErrorAborted,
            _ => NcwTextKind.ErrorGeneric,
        },
    });

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>供应商协议 id → 名称。</summary>
public sealed class ProtocolTextConverter : IValueConverter
{
    public static readonly ProtocolTextConverter Instance = new();

    public static string Text(string? protocol) => protocol switch
    {
        "anthropic" => "Anthropic Messages",
        "openai-chat" => "OpenAI Chat",
        "openai-responses" => "OpenAI Responses",
        "openai-images" => NcwTexts.Get(NcwTextKind.ProtocolOpenAiImages),
        "volcengine-images" => NcwTexts.Get(NcwTextKind.ProtocolVolcengineImages),
        "openai-videos" => NcwTexts.Get(NcwTextKind.ProtocolOpenAiVideos),
        "volcengine-videos" => NcwTexts.Get(NcwTextKind.ProtocolVolcengineVideos),
        "openai-audio" => NcwTexts.Get(NcwTextKind.ProtocolOpenAiAudio),
        _ => protocol ?? "",
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Text(value as string);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>调试弹窗协议分段上的短名字(并排放得下)。</summary>
public sealed class ProtocolShortTextConverter : IValueConverter
{
    public static readonly ProtocolShortTextConverter Instance = new();

    public static string Text(string? protocol) => protocol switch
    {
        "anthropic" => "Anthropic",
        "openai-chat" => "OpenAI Chat",
        "openai-responses" => "Responses",
        "openai-images" => "OpenAI",
        "volcengine-images" => NcwTexts.Get(NcwTextKind.ProtocolVolcengineImagesShort),
        "openai-videos" => "OpenAI",
        "volcengine-videos" => NcwTexts.Get(NcwTextKind.ProtocolVolcengineVideosShort),
        "openai-audio" => "OpenAI Audio",
        _ => protocol ?? "",
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Text(value as string);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>供应商类型(<c>text</c> / <c>image</c> / <c>video</c> / <c>audio</c>)→ 文字。<c>ConverterParameter=Short</c> 返回短名。</summary>
public sealed class ProviderKindTextConverter : IValueConverter
{
    public static readonly ProviderKindTextConverter Instance = new();

    public static string Text(string? kind, bool shortForm = false) => NcwTexts.Get((kind, shortForm) switch
    {
        ("image", true) => NcwTextKind.ProviderKindShortImage,
        ("video", true) => NcwTextKind.ProviderKindShortVideo,
        ("audio", true) => NcwTextKind.ProviderKindShortAudio,
        (_, true) => NcwTextKind.ProviderKindShortText,
        ("image", false) => NcwTextKind.ProviderKindImage,
        ("video", false) => NcwTextKind.ProviderKindVideo,
        ("audio", false) => NcwTextKind.ProviderKindAudio,
        _ => NcwTextKind.ProviderKindText,
    });

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Text(value as string, string.Equals(parameter as string, "Short", StringComparison.OrdinalIgnoreCase));

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// 调试弹窗事件流里第二列:事件的种类 / 名字。这些是线上的技术标识(SSE 事件名、停止原因、失败类别),原样显示,不翻译。
/// </summary>
public sealed class DebugEventNameConverter : IValueConverter
{
    public static readonly DebugEventNameConverter Instance = new();

    public static string Text(Ncw.Protocol.Methods.ProviderDebugEventDto? ev) => ev switch
    {
        null => "",
        { Kind: Ncw.Protocol.Methods.DebugEventKinds.Sse } => ev.Name is { Length: > 0 } name ? name : "message",
        { Kind: Ncw.Protocol.Methods.DebugEventKinds.Request } => "→ request",
        { Kind: Ncw.Protocol.Methods.DebugEventKinds.Response } => $"← {ev.Name}",
        { Name: { Length: > 0 } n } => $"{ev.Kind} · {n}",
        _ => ev.Kind,
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Text(value as Ncw.Protocol.Methods.ProviderDebugEventDto);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>调试弹窗协议分段上的圆点颜色(<see cref="Ncw.UI.Controls.NcwStatusDot.Tone"/>):通过绿、失败红、正在跑黄、还没试灰。</summary>
public sealed class DebugStateToneConverter : IValueConverter
{
    public static readonly DebugStateToneConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        Ncw.Client.ViewModels.DebugProtocolState.Ok => "ok",
        Ncw.Client.ViewModels.DebugProtocolState.Failed => "bad",
        Ncw.Client.ViewModels.DebugProtocolState.Running => "warn",
        _ => "mute",
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class ProviderErrorTextConverter : IValueConverter
{
    public static readonly ProviderErrorTextConverter Instance = new();

    public static string Text(string? key) => NcwTexts.Get(key switch
    {
        "providers.aliasExists" => NcwTextKind.ProvErrAliasExists,
        "providers.emptyKey" => NcwTextKind.ProvErrEmptyKey,
        "providers.fetchEmpty" => NcwTextKind.ProvErrFetchEmpty,
        "providers.fetchFailed" or "providers.fetchTimeout" => NcwTextKind.ProvErrFetchFailed,
        "providers.fetchRejected" or "Unauthorized" or "Forbidden" => NcwTextKind.ProvErrRejected,
        "providers.fetchNotFound" => NcwTextKind.ProvErrFetchNotFound,
        "providers.invalidBaseUrl" => NcwTextKind.ProvErrInvalidBaseUrl,
        "providers.invalidId" => NcwTextKind.ProvErrInvalidId,
        "providers.invalidName" => NcwTextKind.ProvErrInvalidName,
        "providers.invalidKind" or "providers.file.invalidKind" => NcwTextKind.ProvErrInvalidKind,
        "providers.invalidProtocol" or "providers.file.invalidProtocol" => NcwTextKind.ProvErrInvalidProtocol,
        "providers.managedProvider" or "providers.file.managedId" => NcwTextKind.ProvErrManaged,
        "providers.noCredential" => NcwTextKind.ProvErrNoCredential,
        "providers.loginCancelled" => NcwTextKind.ProvErrLoginCancelled,
        "providers.loginFailed" => NcwTextKind.ProvErrLoginFailed,
        "providers.unknownProvider" or "providers.unknownAlias" or "NotFound" => NcwTextKind.ProvErrNotFound,
        "Unavailable" => NcwTextKind.ProvErrUnavailable,
        "Conflict" => NcwTextKind.ProvErrConflict,
        "providers.discovery.gone" => NcwTextKind.ProvErrDiscoveryGone,
        "providers.discovery.alreadyImported" => NcwTextKind.ProvErrDiscoveryAlreadyImported,
        "providers.editor.keyRequired" => NcwTextKind.ProvErrKeyRequired,
        "providers.editor.idTaken" => NcwTextKind.ProvErrIdTaken,
        "providers.debug.modelRequired" => NcwTextKind.ProvErrDebugModelRequired,
        "models.editor.aliasRequired" => NcwTextKind.ModelEditorErrAliasRequired,
        "models.editor.contextTooSmall" => NcwTextKind.ModelEditorErrContextTooSmall,
        "models.editor.outputTooLarge" => NcwTextKind.ModelEditorErrOutputTooLarge,
        "models.editor.budgetTooSmall" => NcwTextKind.ModelEditorErrBudgetTooSmall,
        "models.editor.noEffort" => NcwTextKind.ModelEditorErrNoEffort,
        _ => NcwTextKind.ProvErrGeneric,
    });

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ProviderErrorView view => Text(view.Key ?? view.Outcome.ToString()),
        string key => Text(key),
        _ => null,
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>目录来源(<c>seed</c> / <c>cache</c> / <c>remote</c>)→ 文字。</summary>
public sealed class CatalogSourceTextConverter : IValueConverter
{
    public static readonly CatalogSourceTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => NcwTexts.Get(value switch
    {
        "remote" => NcwTextKind.CatalogSourceRemote,
        "cache" => NcwTextKind.CatalogSourceCache,
        _ => NcwTextKind.CatalogSourceSeed,
    });

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>手动刷新目录的结局 → 文字。</summary>
public sealed class RefreshOutcomeTextConverter : IValueConverter
{
    public static readonly RefreshOutcomeTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        CatalogRefreshOutcomes.Updated => NcwTexts.Get(NcwTextKind.CatalogUpdated),
        CatalogRefreshOutcomes.NotModified => NcwTexts.Get(NcwTextKind.CatalogNotModified),
        CatalogRefreshOutcomes.Rejected => NcwTexts.Get(NcwTextKind.CatalogRejected),
        CatalogRefreshOutcomes.Failed => NcwTexts.Get(NcwTextKind.CatalogFailed),
        _ => null,
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>连通性测试的结果 → 文字(测试中 / 连通 · 延迟 / 失败原因)。</summary>
public sealed class TestResultTextConverter : IValueConverter
{
    public static readonly TestResultTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        TestResult { State: TestState.Testing } => NcwTexts.Get(NcwTextKind.ProviderTesting),
        TestResult { State: TestState.Ok, LatencyMs: { } ms } => NcwTexts.Format(NcwTextKind.ProviderTestOkLatency, ms),
        TestResult { State: TestState.Ok } => NcwTexts.Get(NcwTextKind.ProviderTestOk),
        TestResult { State: TestState.Failed } r => NcwTexts.Format(NcwTextKind.ProviderTestFailed, ProviderErrorTextConverter.Text(r.ErrorKey)),
        _ => null,
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>目录上次更新时间:相对时间;没有为「从未」。</summary>
public sealed class CatalogUpdatedConverter : IValueConverter
{
    public static readonly CatalogUpdatedConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DateTimeOffset at
            ? NcwTexts.Format(NcwTextKind.CatalogUpdatedAt, RelativeTimeConverter.Instance.Convert(at, typeof(string), null, culture))
            : NcwTexts.Get(NcwTextKind.CatalogNeverUpdated);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>会话行状态点的提示:运行中 / 连接中。</summary>
public sealed class ActivityTextConverter : IValueConverter
{
    public static readonly ActivityTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        Ncw.Core.Domain.SessionActivity.Running => NcwTexts.Get(NcwTextKind.SessionRunning),
        Ncw.Core.Domain.SessionActivity.Unknown => NcwTexts.Get(NcwTextKind.SessionUnknown),
        _ => null,
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>字符数缩写:12000 → 12K。</summary>
public sealed class TokensTextConverter : IValueConverter
{
    public static readonly TokensTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int n ? ComposerLogic.WindowText(n) : value is long l ? ComposerLogic.WindowText((int)Math.Min(l, int.MaxValue)) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// 连续工具分组表头(<see cref="Ncw.Client.Transcript.ToolRunGroupRow"/>,03 §6)的一行文字:有当前动作时是「正在编辑 xxx.cs」,
/// 否则是各类工具的个数(「读取 2 个文件，运行 1 条命令」)。绑定 <c>Row</c>(行一变整份重算)。
/// </summary>
public sealed class ToolRunTextConverter : IValueConverter
{
    public static readonly ToolRunTextConverter Instance = new();

    public static string Text(Ncw.Client.Transcript.ToolRunGroupRow row)
    {
        if (row.Active is { } active)
        {
            var kind = active.Kind switch
            {
                Ncw.Client.Transcript.ToolKind.Read => NcwTextKind.ToolRunActiveRead,
                Ncw.Client.Transcript.ToolKind.Edit => NcwTextKind.ToolRunActiveEdit,
                Ncw.Client.Transcript.ToolKind.Write => NcwTextKind.ToolRunActiveWrite,
                Ncw.Client.Transcript.ToolKind.Command => NcwTextKind.ToolRunActiveCommand,
                Ncw.Client.Transcript.ToolKind.Search => NcwTextKind.ToolRunActiveSearch,
                _ => NcwTextKind.ToolRunActiveOther,
            };
            var target = ToolRunGroupRowViewModel.TargetNameOf(active.Kind, active.Target);
            return NcwTexts.Format(kind, target.Length > 0 ? target : active.Name);
        }

        var parts = new List<string>(5);
        void Add(int count, NcwTextKind kind)
        {
            if (count > 0)
            {
                parts.Add(NcwTexts.Format(kind, count));
            }
        }

        Add(row.Reads, NcwTextKind.ToolRunReads);
        Add(row.Searches, NcwTextKind.ToolRunSearches);
        Add(row.Edits, NcwTextKind.ToolRunEdits);
        Add(row.Commands, NcwTextKind.ToolRunCommands);
        Add(row.Others, NcwTextKind.ToolRunOthers);
        return string.Join(NcwTexts.Get(NcwTextKind.ToolRunSeparator), parts);
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Ncw.Client.Transcript.ToolRunGroupRow row ? Text(row) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
