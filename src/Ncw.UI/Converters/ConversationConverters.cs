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
        _ => NcwTexts.Get(NcwTextKind.SlashUnavailable),
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Text(value as SlashNotice);

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
        _ => protocol ?? "",
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Text(value as string);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>设备给的供应商错误 key(<c>providers.*</c>)/ 操作结局 → 文字。不认识的 key 显示通用失败。</summary>
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
