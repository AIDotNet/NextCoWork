using System.Globalization;
using Avalonia.Data.Converters;
using Ncw.Client.Actions;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.UI.Localization;

namespace Ncw.UI.Converters;

// 手机壳(收件箱、设备页、扫码配对、推送、附件)用的值转换器:Client 只给结构化数据,文字在这里按当前语言生成。

/// <summary>收件箱一条 → 标题(「需要审批:Bash」「计划待批准」……)。绑定整条的 <see cref="InboxEntryViewModel"/>。</summary>
public sealed class InboxHeadlineConverter : IValueConverter
{
    public static readonly InboxHeadlineConverter Instance = new();

    public static string Text(InboxEntryViewModel entry) => entry.Kind switch
    {
        InboxKind.ToolApproval => NcwTexts.Format(NcwTextKind.InboxToolApproval, entry.ToolName ?? ""),
        InboxKind.Question => NcwTexts.Get(NcwTextKind.InboxQuestion),
        InboxKind.PlanApproval => NcwTexts.Get(NcwTextKind.InboxPlan),
        InboxKind.GoalProposal => NcwTexts.Get(NcwTextKind.InboxGoal),
        InboxKind.WorkflowApproval => NcwTexts.Get(NcwTextKind.InboxWorkflow),
        InboxKind.DeviceApproval => NcwTexts.Get(NcwTextKind.InboxDeviceApproval),
        InboxKind.RunFailed => NcwTexts.Get(NcwTextKind.InboxFailed),
        _ => NcwTexts.Get(NcwTextKind.InboxCompleted),
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is InboxEntryViewModel entry ? Text(entry) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>项目 / 会话的一行副标题(「NextCoWork · 设计 Agent Runtime」)。(项目名, 会话名) 两个字符串,缺的省掉。</summary>
public sealed class JoinDotConverter : IMultiValueConverter
{
    public static readonly JoinDotConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        string.Join(" · ", values.OfType<string>().Where(static s => s.Length > 0));
}

/// <summary>设备种类 → 文字(本机 / 云端 / 电脑)。</summary>
public sealed class DeviceKindTextConverter : IValueConverter
{
    public static readonly DeviceKindTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        DeviceKind.Local => NcwTexts.Get(NcwTextKind.DeviceKindLocal),
        DeviceKind.Cloud => NcwTexts.Get(NcwTextKind.DeviceKindCloud),
        DeviceKind.Remote => NcwTexts.Get(NcwTextKind.DeviceKindRemote),
        _ => null,
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>整数 + 文字格式(<c>ConverterParameter</c> 是 <see cref="NcwTextKind"/> 的成员名,例如 <c>InboxRunning</c>)。</summary>
public sealed class CountTextConverter : IValueConverter
{
    public static readonly CountTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        parameter is string name && Enum.TryParse<NcwTextKind>(name, out var kind) && value is int count ? NcwTexts.Format(kind, count) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>二维码配对失败 → 提示。(<see cref="QrPairingFailure"/>?, <see cref="PairOutcome"/>?) 两个值:配对失败时按设备返回的结局写具体原因。</summary>
public sealed class QrFailureTextConverter : IMultiValueConverter
{
    public static readonly QrFailureTextConverter Instance = new();

    public static string? Text(QrPairingFailure? failure, PairOutcome? outcome) => failure switch
    {
        QrPairingFailure.CameraDenied => NcwTexts.Get(NcwTextKind.QrFailCameraDenied),
        QrPairingFailure.CameraRestricted => NcwTexts.Get(NcwTextKind.QrFailCameraRestricted),
        QrPairingFailure.ScannerUnavailable => NcwTexts.Get(NcwTextKind.QrFailUnavailable),
        QrPairingFailure.InvalidCode => NcwTexts.Get(NcwTextKind.QrFailInvalid),
        QrPairingFailure.Expired => NcwTexts.Get(NcwTextKind.QrFailExpired),
        QrPairingFailure.PairingFailed => outcome switch
        {
            PairOutcome.QrRejected => NcwTexts.Get(NcwTextKind.QrFailRejected),
            { } other => PairOutcomeConverter.Instance.Convert(other, typeof(string), null, CultureInfo.CurrentCulture) as string ?? NcwTexts.Get(NcwTextKind.PairUnreachable),
            _ => NcwTexts.Get(NcwTextKind.PairUnreachable),
        },
        _ => null,
    };

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        Text(values.Count > 0 ? values[0] as QrPairingFailure? : null, values.Count > 1 ? values[1] as PairOutcome? : null);
}

/// <summary>推送状态 → 文字。(<see cref="PushStatus"/>, 已登记设备数)。</summary>
public sealed class PushStatusTextConverter : IMultiValueConverter
{
    public static readonly PushStatusTextConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) => values.Count > 0 && values[0] is PushStatus status
        ? status switch
        {
            PushStatus.Unsupported => NcwTexts.Get(NcwTextKind.PushUnsupported),
            PushStatus.NotConfigured => NcwTexts.Get(NcwTextKind.PushNotConfigured),
            PushStatus.NotDetermined => NcwTexts.Get(NcwTextKind.PushNotDetermined),
            PushStatus.Denied => NcwTexts.Get(NcwTextKind.PushDenied),
            PushStatus.NoToken => NcwTexts.Get(NcwTextKind.PushNoToken),
            PushStatus.Registering => NcwTexts.Get(NcwTextKind.PushRegistering),
            PushStatus.Registered => NcwTexts.Format(NcwTextKind.PushRegistered, values.Count > 1 && values[1] is int n ? n : 0),
            _ => NcwTexts.Get(NcwTextKind.PushFailed),
        }
        : null;
}

/// <summary>附件上传进度 → 「正在上传 42%」。绑定 <see cref="AttachmentItemViewModel.Progress"/>(0–1)。</summary>
public sealed class UploadPercentTextConverter : IValueConverter
{
    public static readonly UploadPercentTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double progress ? NcwTexts.Format(NcwTextKind.AttachmentUploading, (int)Math.Round(progress * 100)) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>附件失败原因 → 文字。</summary>
public sealed class AttachmentFailureTextConverter : IValueConverter
{
    public static readonly AttachmentFailureTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        AttachmentFailure.TooLarge => NcwTexts.Format(NcwTextKind.AttachmentTooLarge, Attachments.MaxBytes / 1024 / 1024),
        AttachmentFailure.Empty => NcwTexts.Get(NcwTextKind.AttachmentEmpty),
        AttachmentFailure.DeviceUnavailable => NcwTexts.Get(NcwTextKind.AttachmentDeviceUnavailable),
        AttachmentFailure.Forbidden => NcwTexts.Get(NcwTextKind.AttachmentForbidden),
        AttachmentFailure.Rejected => NcwTexts.Get(NcwTextKind.AttachmentRejected),
        AttachmentFailure.ReadFailed => NcwTexts.Get(NcwTextKind.AttachmentReadFailed),
        _ => null,
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>字节数 → 「1.2 MB」。</summary>
public sealed class AttachmentSizeConverter : IValueConverter
{
    public static readonly AttachmentSizeConverter Instance = new();

    public static string Text(long bytes) => bytes switch
    {
        >= 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0 / 1024.0:0.#} MB"),
        >= 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is long bytes ? Text(bytes) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// 时刻 → 本地化的短时间(二维码有效期):格式跟<b>界面语言</b>(<see cref="TKCode.Arc.Localization.ArcLanguage.Culture"/>)走,不跟操作系统的区域走——
/// 中文界面 → 「12:30」,英文界面 → 「12:30 PM」。以前用的是绑定传进来的当前区域(操作系统的 en-US),中文界面里也显示成英文 12 小时制。
/// </summary>
public sealed class ClockTimeConverter : IValueConverter
{
    public static readonly ClockTimeConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DateTimeOffset at ? NcwTexts.Format(NcwTextKind.QrExpiresAt, at.ToLocalTime().ToString("t", TKCode.Arc.Localization.ArcLanguage.Culture)) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>进度 0–1 → 填充条宽度比例用的 <see cref="Avalonia.GridLength"/>(星号):填充列和余下列。</summary>
public sealed class StarLengthConverter : IValueConverter
{
    public static readonly StarLengthConverter Fill = new(false);
    public static readonly StarLengthConverter Rest = new(true);

    private readonly bool _rest;

    private StarLengthConverter(bool rest) => _rest = rest;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var progress = value is double d ? Math.Clamp(d, 0, 1) : 0;
        return new Avalonia.Controls.GridLength(_rest ? 1 - progress : progress, Avalonia.Controls.GridUnitType.Star);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>(进度 0–1, 按钮宽度) → 填充条宽度(像素)。</summary>
public sealed class ProgressWidthConverter : IMultiValueConverter
{
    public static readonly ProgressWidthConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var progress = values.Count > 0 && values[0] is double p ? Math.Clamp(p, 0, 1) : 0;
        var width = values.Count > 1 && values[1] is double w && !double.IsNaN(w) ? w : 0;
        return progress * width;
    }
}

/// <summary>「长按允许」按钮的文字。(<c>IsHolding</c>, <c>HoldCompleted</c>) → 长按允许 / 继续按住… / 正在允许…。</summary>
public sealed class HoldLabelConverter : IMultiValueConverter
{
    public static readonly HoldLabelConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Count > 1 && values[1] is true ? NcwTexts.Get(NcwTextKind.ApprovalSubmitting)
        : values.Count > 0 && values[0] is true ? NcwTexts.Get(NcwTextKind.ApprovalHolding)
        : NcwTexts.Get(NcwTextKind.ApprovalHoldToAllow);
}
